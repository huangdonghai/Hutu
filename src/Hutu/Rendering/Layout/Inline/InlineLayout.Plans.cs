using AngleSharp.Css.Dom;
using Hutu.Rendering.Boxes;
using System.Buffers;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;

using System.Globalization;

namespace Hutu.Rendering.Layout;

internal sealed partial class InlineLayout
{
    private TextPlan TextPlanFor(
        String text,
        ComputedStyle style,
        WritingModeInfo flow,
        LineBreaks.WordBreaking word,
        Boolean continuesWord,
        Boolean needWidths)
    {
        var key = new ChunkKey(text, word, continuesWord);
        var flowSignature = FlowSignature(flow);

        if (_textPlans.TryGetValue(key, out var known))
        {
            if (!needWidths || known.Matches(style.Values, flowSignature))
            {
                return known;
            }

            known.Measure(_measurer, text, style, flow, flowSignature);
            return known;
        }

        var ranges = new List<TextChunker.ChunkRange>();

        foreach (var range in TextChunker.Ranges(text, word, continuesWord))
        {
            ranges.Add(range);
        }

        var plan = new TextPlan(ranges.ToArray());

        if (needWidths)
        {
            plan.Measure(_measurer, text, style, flow, flowSignature);
        }

        _textPlans[key] = plan;

        return plan;
    }

    private readonly struct ChunkKey : IEquatable<ChunkKey>
    {
        public ChunkKey(String text, LineBreaks.WordBreaking word, Boolean continuesWord)
        {
            Text = text;
            Word = word;
            ContinuesWord = continuesWord;
        }

        public String Text { get; }

        public LineBreaks.WordBreaking Word { get; }

        public Boolean ContinuesWord { get; }

        public Boolean Equals(ChunkKey other) =>
            String.Equals(Text, other.Text, StringComparison.Ordinal) &&
            Word == other.Word &&
            ContinuesWord == other.ContinuesWord;

        public override Boolean Equals(Object? obj) => obj is ChunkKey other && Equals(other);

        public override Int32 GetHashCode() =>
            HashCode.Combine(StringComparer.Ordinal.GetHashCode(Text), Word, ContinuesWord);
    }

    private sealed class TextPlan
    {
        private ComputedStyle.StyleValues? _style;
        private Int32 _flow;

        public TextPlan(TextChunker.ChunkRange[] chunks)
        {
            Chunks = chunks;
            Widths = new Double[chunks.Length];
        }

        public TextChunker.ChunkRange[] Chunks { get; }

        public Double[] Widths { get; }

        public Boolean Matches(ComputedStyle.StyleValues style, Int32 flow) =>
            ReferenceEquals(_style, style) && _flow == flow;

        public void Measure(
            ITextMeasurer measurer,
            String text,
            ComputedStyle style,
            WritingModeInfo flow,
            Int32 flowSignature)
        {
            for (var index = 0; index < Chunks.Length; index++)
            {
                var chunk = Chunks[index];

                // White space is measured by where the line has reached, not by the font alone: a tab
                // runs on to a stop and a run beginning a collapsed line takes no room. The two callers
                // both take that answer from the line, so the plan carries no width for it.
                Widths[index] = TextChunker.IsCollapsible(text, chunk.Start, chunk.Length)
                    ? 0
                    : MeasureChunk(measurer, style, text, chunk.Start, chunk.Length, flow);
            }

            _style = style.Values;
            _flow = flowSignature;
        }
    }

    private readonly record struct MaskMark(Int32 Offset, Char Code);

    private readonly struct MaskSpan
    {
        public MaskSpan(Int32 start, Int32 count)
        {
            Start = start;
            Count = count;
        }

        public Int32 Start { get; }

        public Int32 Count { get; }
    }

    /// <summary>
    /// Gets the lines the container's inline content is broken into at this extent, making them the
    /// first time the pass asks and handing back the same lines every time it asks again.
    /// </summary>
    private List<Line> Plan(
        Box container,
        Double maxInline,
        WritingModeInfo flow,
        InlineContext context,
        TextIndentation indent,
        InlineStart? start = null)
    {
        // A plan that begins partway through the content belongs to one fragment, not to the box: it is
        // made fresh each time rather than cached under the key the whole-content plan answers to.
        if (start is { } from)
        {
            return PlanFrom(container, maxInline, flow, context, indent, from);
        }

        var key = new LinePlanKey(container, maxInline, indent, FlowSignature(flow), context.Containing.Floats);

        if (_plans.TryGetValue(key, out var known))
        {
            return known;
        }

        var items = RentItems();
        Collect(container, items, flow, null, maxInline);

        // Content with nothing to break still gets a plan: the phases ask again for the same answer and
        // an empty list is the same work saved as any other.
        var lines = items.Count == 0
            ? new List<Line>()
            : BuildLines(items, maxInline, flow, context, container.Style, indent);

        if (items.Count > 0 && container.Style.WrapStyle == "balance")
        {
            lines = Balanced(items, maxInline, flow, context, indent, container.Style, lines);
        }

        // A block that clips its inline overflow may mark the line it cuts short, and a column the page
        // clamps keeps only the lines it asked for and marks the last (CSS Overflow 3 section 3).
        if (items.Count > 0 && (container.LineClampOverride ?? container.Style.LineClamp) is { } clamp)
        {
            ClampTo(lines, clamp, maxInline, flow, container.Style.EllipsisText ?? "\u2026", container);
        }
        else if (items.Count > 0 && container.Style.EllipsisOverflow &&
            container.Style.EllipsisText is { Length: > 0 } ellipsis)
        {
            Ellipsize(lines, maxInline, flow, ellipsis, container);
        }

        // The lines the block reached are then scaled to fill their line boxes, where the page asked for
        // fitting: the breaks are already settled, and what changes is how big the text on each line is
        // drawn (CSS Text 5 §1.1, Appendix A - applied after wrapping, before justification).
        FitLines(container, lines, context);

        lines = AddInterlineNotes(container, lines, maxInline, flow, context);

        ReturnItems(items);
        _plans[key] = lines;
        return lines;
    }

    /// <summary>
    /// Cuts every line of the plan that runs past the container's inline extent and sets the mark where
    /// it ends. The line's runs are kept up to the last character that still fits beside the mark, and
    /// nothing after it is drawn.
    /// </summary>
    private void Ellipsize(
        List<Line> lines, Double maxInline, WritingModeInfo flow, String mark, Box container)
    {
        foreach (var line in lines)
        {
            EllipsizeLine(line, maxInline, flow, mark, container, force: false);
        }
    }

    /// <summary>
    /// Scales the block's lines so the text on each of them fills its line box, which is what
    /// <c>text-fit</c> asks for (CSS Text 5 §1.1).
    /// </summary>
    /// <remarks>
    /// A line's factor is the room the fitting can hand to its text divided by the room that text takes:
    /// the scalable parts are the text and the spacing that travels with the font size, while a replaced
    /// box, an atomic inline and a spacing written as a length stay at the size they were written at and
    /// come off the room first (§1.1.1). The direction says which lines the property reaches at all - a
    /// block that grows leaves a line that already overflows alone - and the scaling says whether one
    /// factor serves every line or each has its own, with <c>per-line</c> holding the block's last line
    /// and any line a forced break ended unscaled.
    /// </remarks>
    private void FitLines(Box container, List<Line> lines, InlineContext context)
    {
        var fit = container.Style.TextFitting;

        if (fit.IsNone || lines.Count == 0)
        {
            return;
        }

        // A line whose room depends on how far down the block it stands is a line the rule leaves alone,
        // and floats are exactly that: the fitting is switched off for the whole block (§1.1).
        if (context.Containing.Floats is not null)
        {
            return;
        }

        Double[]? rentedFactors = null;
        Span<Double> factors = lines.Count <= StackScratchLimit
            ? stackalloc Double[lines.Count]
            : (rentedFactors = RentDoubles(lines.Count));
        var shared = 1.0;

        for (var index = 0; index < lines.Count; index++)
        {
            var line = lines[index];

            // Nothing to scale, or no room to scale into, is a line drawn at the size it stands at. What
            // the line holds that the fitting cannot reach is everything the text does not account for:
            // the boxes of replaced and atomic content, an inline element's own padding, border and margin
            // along the line, and the room the line's trailing spaces still take - all of which stand at
            // the size they were written at while the text is stretched or shrunk around them
            // (CSS Text 5 §1.1.1).
            var scalable = ScalableExtent(line);
            var room = line.Extent - line.Indent - Math.Max(0, line.Used - scalable);

            if (scalable <= 0 || room <= 0)
            {
                factors[index] = 1;
                continue;
            }

            var factor = room / scalable;

            factors[index] = fit.Clamp(fit.Mode switch
            {
                TextFitMode.Grow => Math.Max(1, factor),
                TextFitMode.Shrink => Math.Min(1, factor),
                _ => 1,
            });

            if (index == 0 || factors[index] < shared)
            {
                shared = factors[index];
            }
        }

        for (var index = 0; index < lines.Count; index++)
        {
            var line = lines[index];

            var factor = fit.Scaling switch
            {
                // The last line of the block, and any line a forced break ended, keep the size they were
                // written at: `per-line` is the mode that says so (CSS Text 5 §1.1).
                TextFitScaling.PerLine when index == lines.Count - 1 || line.ForceEnded => 1,
                TextFitScaling.PerLine or TextFitScaling.PerLineAll => factors[index],
                _ => shared,
            };

            ScaleLine(container, line, factor);
        }

        if (rentedFactors is not null)
        {
            ReturnDoubles(rentedFactors);
        }

    }

    /// <summary>
    /// Gets the room a line's scalable parts take: the text it holds, with the boxes the fitting cannot
    /// reach left out (CSS Text 5 §1.1.1).
    /// </summary>
    private static Double ScalableExtent(Line line)
    {
        var total = 0.0;

        foreach (var fragment in line.Fragments)
        {
            if (!fragment.IsReplaced && fragment.Atomic is null && !fragment.OutOfFlow)
            {
                total += fragment.Width;
            }
        }

        return total;
    }


    /// <summary>
    /// Puts a line at the factor the fitting worked out for it: the runs it holds are widened by it, the
    /// line's height goes with them where it came from the font rather than from the page, and the scale
    /// is carried for the placing pass, which draws the runs at the size it says.
    /// </summary>
    private static void ScaleLine(Box container, Line line, Double factor)
    {
        if (Math.Abs(factor - 1) < 0.0001)
        {
            return;
        }

        var scalable = 0.0;

        foreach (var fragment in line.Fragments)
        {
            if (!fragment.IsReplaced && fragment.Atomic is null && !fragment.OutOfFlow)
            {
                fragment.Width *= factor;
                scalable += fragment.Width / factor;
            }
        }

        // Only the text moved: what the line held at its written size keeps its room, and the rest of the
        // line's extent is that part plus the text at the new size.
        line.Used += scalable * (factor - 1);
        line.NaturalUsed = line.NaturalUsed is { } natural ? natural + scalable * (factor - 1) : line.Used;
        line.Scale = factor;

        // A line's height goes with its text where it is a multiple of the font it is drawn at - a bare
        // number, a percentage, or `normal`. A length the page wrote is its own number and stands, however
        // big the glyphs get around it (CSS Text 5 §1.1, and the `line-height: 20px` reference of
        // grow-per-line-all-line-height measures both readings).
        if (container.Style.LineHeightTracksFont)
        {
            line.Ascent *= factor;
            line.Height *= factor;
        }
    }

    /// <summary>
    /// Gets the font a run on a fitted line is drawn and measured with: the fragment's own face at the
    /// size the fitting scaled the line to. The computed font size is left where the page put it - the
    /// scaling reaches the used size alone, which is why the line-height and the spacing the style carries
    /// are read from the style rather than from this font (CSS Text 5 §1.1).
    /// </summary>
    private static FontStyle FittedFont(Line line, ComputedStyle style)
    {
        var font = style.GetFont();

        if (Math.Abs(line.Scale - 1) < 0.0001)
        {
            return font;
        }

        return font.WithSize(font.Size * line.Scale);
    }

    /// <summary>
    /// Keeps only the first <paramref name="count"/> lines and marks the last of them, which is what a
    /// vertical <c>-webkit-box</c> with <c>-webkit-line-clamp</c> draws: the content after the clamp is
    /// not laid out at all (CSS Overflow 3).
    /// </summary>
    private void ClampTo(
        List<Line> lines, Int32 count, Double maxInline, WritingModeInfo flow, String mark, Box container)
    {
        if (count <= 0 || lines.Count <= count)
        {
            return;
        }

        lines.RemoveRange(count, lines.Count - count);
        EllipsizeLine(lines[^1], maxInline, flow, mark, container, force: true);
    }

    /// <summary>
    /// Cuts one line at a whole character and sets the mark at its end. <paramref name="force"/> marks
    /// the last line of a clamp, which ends in the mark even where the text alone would have fitted.
    /// </summary>
    private void EllipsizeLine(
        Line line, Double maxInline, WritingModeInfo flow, String mark, Box container, Boolean force)
    {
        if (line.Fragments.Count == 0)
        {
            // A clamp that ends on a blank line still marks it: the mark is content the clamp adds,
            // not something the line's own text had room for (webkit-line-clamp-032).
            if (!force)
            {
                return;
            }

            var blankStyle = container.Style;
            var blankWidth = MeasureChunk(_measurer, blankStyle, mark, 0, mark.Length, flow);

            line.Fragments.Add(NewFragment(mark, 0, mark.Length, blankWidth, blankStyle, container, flow, null));
            return;
        }

        var used = line.Indent;

        foreach (var fragment in line.Fragments)
        {
            used += fragment.Width;
        }

        // A line shortened by a float ends at the float, not at the block's edge.
        var edge = line.Extent > 0 ? Math.Min(maxInline, line.Extent) : maxInline;
        var available = edge - line.Indent;

        if (available <= 0 || (!force && used <= edge + 0.01))
        {
            return;
        }

        // The mark stands in the block container's own font, not in the run it cuts: a wide span
        // still ends in the block's small ellipsis (measured on text-overflow-002).
        var style = container.Style;
        var markWidth = MeasureChunk(_measurer, style, mark, 0, mark.Length, flow);
        var room = Math.Max(0, available - markWidth);
        var kept = 0;
        var remaining = room;
        var cut = -1;

        for (var index = 0; index < line.Fragments.Count; index++)
        {
            var fragment = line.Fragments[index];

            if (fragment.OutOfFlow)
            {
                kept = index + 1;
                continue;
            }

            if (remaining >= fragment.Width - 0.001)
            {
                remaining -= fragment.Width;
                kept = index + 1;
                continue;
            }

            if (fragment.Length == 0 || fragment.IsReplaced || fragment.Ruby is not null ||
                fragment.Atomic is not null)
            {
                cut = index;
                break;
            }

            // The longest run of whole characters that still leaves room for the mark.
            var length = 0;
            var width = 0.0;

            while (length < fragment.Length)
            {
                var units = TextClusters.UnitsAt(fragment.Text, length, fragment.Length);
                var piece = MeasureChunk(_measurer, fragment.Style, fragment.Text, length, units, fragment.Flow);

                if (width + piece > remaining + 0.001)
                {
                    break;
                }

                width += piece;
                length += units;
            }

            fragment.Length = length;
            fragment.Width = width;
            cut = index + 1;
            break;
        }

        if (cut < 0)
        {
            cut = kept;
        }

        if (cut < line.Fragments.Count)
        {
            line.Fragments.RemoveRange(cut, line.Fragments.Count - cut);
        }

        if (line.Fragments.Count == 0)
        {
            return;
        }

        line.Fragments.Add(NewFragment(
            mark, 0, mark.Length, markWidth, style, container, flow, null));
    }

    /// <summary>
    /// Shares a short paragraph's lines more evenly: the narrowest line box that still needs the same
    /// number of lines is the width whose line breaks balance the text, and the lines are then drawn
    /// in the block's own (wider) box - only where they end changes (CSS Text 4 §5, text-wrap: balance).
    /// </summary>
    private List<Line> Balanced(
        List<InlineItem> items,
        Double maxInline,
        WritingModeInfo flow,
        InlineContext context,
        TextIndentation indent,
        ComputedStyle strut,
        List<Line> greedy)
    {
        // The corpus's balancing is about a short paragraph in its own block: a column, a float's band
        // and a paragraph too long to be worth the search keep the ordinary plan.
        // A height-capped block is the shape a line clamp takes (line-clamp itself is not read yet);
        // balancing it before the clamp is modelled only exposes content the page meant to clip.
        if (flow.IsVertical || context.Containing.Floats is not null || strut.ClipsOverflow ||
            strut.MaxHeight.HasValue ||
            greedy.Count <= 1 || greedy.Count > 6 || maxInline <= 0)
        {
            return greedy;
        }

        var target = greedy.Count;
        var low = 0.0;
        var high = maxInline;
        var best = greedy;

        for (var step = 0; step < 24; step++)
        {
            var middle = (low + high) / 2;

            if (middle <= 0)
            {
                break;
            }

            var candidate = BuildLines(items, middle, flow, context, strut, indent, emergency: false);

            if (candidate.Count <= target)
            {
                best = candidate;
                high = middle;
            }
            else
            {
                low = middle;
            }
        }

        // The break decisions were made at the balanced width; the line box is the block's own, which
        // is what text-align and justification read.
        foreach (var line in best)
        {
            line.Extent = maxInline;
            line.Offset = Aligned(line, maxInline);
        }

        return best;
    }

    /// <summary>
    /// Builds the plan one fragment resumes with: everything from the place the last fragment broke at,
    /// laid out as a fresh first line. A column past a float needs this because its lines are longer
    /// than the first column's and break in other places.
    /// </summary>
    private List<Line> PlanFrom(
        Box container,
        Double maxInline,
        WritingModeInfo flow,
        InlineContext context,
        TextIndentation indent,
        InlineStart start)
    {
        var items = RentItems();
        Collect(container, items, flow, null, maxInline);

        var kept = Resume(items, start);

        if (kept.Count == 0)
        {
            if (!ReferenceEquals(kept, items))
            {
                ReturnItems(kept);
            }

            ReturnItems(items);
            return new List<Line>();
        }

        var lines = BuildLines(kept, maxInline, flow, context, container.Style, indent);
        var result = AddInterlineNotes(container, lines, maxInline, flow, context);

        if (!ReferenceEquals(kept, items))
        {
            ReturnItems(kept);
        }

        ReturnItems(items);
        return result;
    }

    /// <summary>
    /// Gets the items a plan resuming at <paramref name="start"/> still has to lay out: the text run the
    /// break fell in is cut from the offset and everything before it is dropped. A place no item answers
    /// is not taken to mean the content is over - the whole list comes back instead.
    /// </summary>
    private List<InlineItem> Resume(List<InlineItem> items, InlineStart start)
    {
        var after = -1;

        for (var index = 0; index < items.Count; index++)
        {
            var item = items[index];

            if (!ReferenceEquals(item.Box, start.Box))
            {
                continue;
            }

            if (item.Text is { } text)
            {
                if (start.Offset < text.Length)
                {
                    var kept = RentItems();
                    kept.Add(InlineItem.CreateText(text.Substring(start.Offset), item.Style, item.Box, item.Inline));

                    for (var rest = index + 1; rest < items.Count; rest++)
                    {
                        kept.Add(items[rest]);
                    }

                    return kept;
                }

                // The break stands at the end of this run: what follows it begins the rest.
                after = index + 1;
                continue;
            }

            // A break or an atomic box at the place itself begins the rest.
            after = index;
            break;
        }

        if (after < 0)
        {
            return items;
        }

        var slice = RentItems();

        for (var index = after; index < items.Count; index++)
        {
            slice.Add(items[index]);
        }

        return slice;
    }

    /// <summary>Gets where the line at <paramref name="index"/> of the box's plan ends, for resuming.</summary>
    internal InlineStart? EndOfLine(
        Box container,
        Double maxInline,
        WritingModeInfo flow,
        InlineContext context,
        Int32 index)
    {
        var lines = Plan(container, maxInline, flow, context, Indentation(container, flow, maxInline));

        if (index < 0 || index >= lines.Count)
        {
            return null;
        }

        var fragments = lines[index].Fragments;

        for (var at = fragments.Count - 1; at >= 0; at--)
        {
            var fragment = fragments[at];

            if (fragment.Length > 0 && !String.IsNullOrEmpty(fragment.Source))
            {
                return new InlineStart(fragment.Box, fragment.Start + fragment.Length);
            }
        }

        return null;
    }

    /// <summary>
    /// Packs the writing mode, orientation, direction and upright combining into the one number the
    /// plan key wants. <see cref="WritingModeInfo"/> is a value type that answers equality through the
    /// reflection-based default rather than an override, so hashing and comparing it whole is what
    /// this avoids.
    /// </summary>
    private static Int32 FlowSignature(WritingModeInfo flow) =>
        (((Int32)flow.Mode) << 12) | (((Int32)flow.Orientation) << 8) | (((Int32)flow.Direction) << 1) |
        (flow.CombinesUpright ? 1 : 0);

}

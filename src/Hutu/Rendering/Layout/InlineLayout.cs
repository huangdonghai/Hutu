using AngleSharp.Css.Dom;
using Hutu.Rendering.Boxes;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;

using System.Globalization;

namespace Hutu.Rendering.Layout;

/// <summary>The extent inline content occupies along each of the two axes it is laid out in.</summary>
/// <remarks>
/// Which physical axis either one lands on is the writing mode's business, not the content's: a block
/// asks for the pair and then decides what to do with it.
/// </remarks>
internal readonly struct InlineContentSize
{
    public InlineContentSize(Double inline, Double block)
    {
        Inline = inline;
        Block = block;
    }

    /// <summary>Gets the extent along the axis the text runs, which fills the content box's inline size.</summary>
    public Double Inline { get; }

    /// <summary>Gets the extent along the axis the lines advance, which is what the content needs to stack.</summary>
    public Double Block { get; }
}

/// <summary>
/// Lays out an inline formatting context: it flattens the inline content of a block into text runs,
/// breaks them into line boxes and positions every run.
/// </summary>
/// <remarks>
/// The tree is flattened into a single stream of runs plus explicit breaks, but the inline elements
/// the runs came from are not forgotten: every run carries the chain of boxes it sits inside, and
/// every such element is given a <see cref="LayoutKind.Inline"/> node on every line it reaches. Those
/// nodes nest, so an outer element's background lands under an inner one's, and an element broken over
/// several lines leaves one fragment per line with the cut edges left bare, which is what
/// <c>box-decoration-break: slice</c> asks for. An element is given a node whether or not it draws
/// anything, because whether it draws anything is a question for the painter: a change that only adds
/// a background is a change the layout is deliberately reused through, so the node has to be there
/// for the background to have somewhere to land. An <c>inline-block</c> is the one inline-level box
/// that does not enter the stream at all: it is placed whole, at the size its own content asks for,
/// through the engine that lays that content out.
/// <para>
/// Line height is the tallest run on the line, expanded by half-leading when the element sets an
/// explicit leading, and the baseline is shared by the whole line. An inline element's own band is
/// the exception: it is the font's content area, and it neither grows the line nor is grown by it -
/// inline padding and borders overflow the line rather than widen it. An inline-block is the other
/// exception the other way round: it is as tall as its own content and does grow the line, which is
/// what puts its text on the line's baseline with the text around it.
/// </para>
/// <para>
/// Everything is written in terms of the two logical axes rather than x and y, so the same code lays
/// out a row of lines and a column of them: <em>inline</em> is the extent a line runs along and where
/// a run sits within it, <em>block</em> is the extent a line occupies and where the next one starts.
/// Horizontal writing puts inline on x and block on y, vertical writing swaps the two, and
/// <see cref="WritingModeInfo"/> is the only thing that knows which is which.
/// </para>
/// </remarks>
internal sealed class InlineLayout
{
    private readonly ITextMeasurer _measurer;

    /// <summary>
    /// The lines each container was broken into, kept for the rest of the layout pass.
    /// </summary>
    /// <remarks>
    /// A context is broken into lines by the measuring phase and its lines are placed by the arranging
    /// one, and both of them used to run the whole breaking themselves: the same text chunked, oriented,
    /// measured and lined up twice, for the second time producing nothing but the placement of what the
    /// first had already settled. A line the placing phase reads is a line it does not have to make,
    /// which is what this dictionary is for - a plan is settled once <see cref="BuildLines"/> returns
    /// (<see cref="LineBuilder.Build"/> and <see cref="MarkSlices"/> are the only writers) and every
    /// reader after that only places.<para>
    /// The key carries the resolved indentation as well as the inline extent, because a percentage
    /// <c>text-indent</c> is a share of the containing block, and the two phases reach this container
    /// through contexts that are not always the same. Two that disagree about the indent are two
    /// different breakings and get their own plan.<br/>
    /// It lives exactly as long as the pass does, cleared with the engine's own caches, so a repeat
    /// layout never inherits lines from a tree it has not measured.<br/>
    /// What is held is not free: the lines stay live until the pass ends instead of becoming garbage
    /// when the measuring phase does. It is the cheaper half of the trade - the placing phase used to
    /// build its own copy of every one of them and throw it away straight after.
    /// </remarks>
    private readonly Dictionary<LinePlanKey, List<Line>> _plans = new();

    /// <summary>
    /// The baselines worked out by laying a box out for the question, held by the box and the size it
    /// was asked at. A box inside a box is asked once per level, and each answer lays its own
    /// children out for theirs, so without this a deeply nested chain of inline-blocks costs the
    /// whole subtree again at every step down it.
    /// </summary>
    private readonly Dictionary<BaselineKey, Double> _baselines = new();

    private readonly record struct BaselineKey(Box Box, Double Width, Double Height, Double Basis, Double ContainingWidth, Double ContainingHeight);

    /// <summary>Drops the plans, which belong to one layout pass as the engine's own caches do.</summary>
    internal void ResetPlanCache()
    {
        _plans.Clear();
        _baselines.Clear();
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

        var items = new List<InlineItem>();
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

        var factors = new Double[lines.Count];
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

        return new FontStyle(
            font.Family,
            font.Size * line.Scale,
            font.Weight,
            font.IsItalic,
            font.Adjust,
            font.Features);
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

            line.Fragments.Add(new TextFragment(mark, 0, mark.Length, blankWidth, blankStyle, container, flow, null));
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

        line.Fragments.Add(new TextFragment(
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
        var items = new List<InlineItem>();
        Collect(container, items, flow, null, maxInline);

        var kept = Resume(items, start);

        if (kept.Count == 0)
        {
            return new List<Line>();
        }

        var lines = BuildLines(kept, maxInline, flow, context, container.Style, indent);

        return AddInterlineNotes(container, lines, maxInline, flow, context);
    }

    /// <summary>
    /// Gets the items a plan resuming at <paramref name="start"/> still has to lay out: the text run the
    /// break fell in is cut from the offset and everything before it is dropped. A place no item answers
    /// is not taken to mean the content is over - the whole list comes back instead.
    /// </summary>
    private static List<InlineItem> Resume(List<InlineItem> items, InlineStart start)
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
                    var kept = new List<InlineItem>(items.Count - index)
                    {
                        InlineItem.CreateText(text.Substring(start.Offset), item.Style, item.Box, item.Inline),
                    };

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

        return after < 0 ? items : items.GetRange(after, items.Count - after);
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

    public InlineLayout(ITextMeasurer measurer) => _measurer = measurer;

    /// <summary>
    /// Measures the extent the inline children need along the block axis at the given inline extent,
    /// without producing any geometry. This is what the measuring phase of a block asks for before it
    /// knows where the lines will sit.
    /// </summary>
    public Double MeasureHeight(Box container, Double contentWidth, InlineContext context) =>
        Measure(container, contentWidth, container.Flow, context).Block;

    /// <summary>
    /// Measures the extents the inline children need when they are given <paramref name="maxInline"/>
    /// to run along. The block extent is what the lines of a row stack into, or the width of the band
    /// of columns a column stacks into.
    /// </summary>
    public InlineContentSize Measure(Box container, Double maxInline, WritingModeInfo flow, InlineContext context)
    {
        return MeasureCore(container, maxInline, flow, context);
    }

    private InlineContentSize MeasureCore(Box container, Double maxInline, WritingModeInfo flow, InlineContext context)
    {
        var lines = Plan(container, maxInline, flow, context, Indentation(container, flow, maxInline));

        var block = 0.0;
        var inline = 0.0;

        foreach (var line in lines)
        {
            if (line.Height > 0)
            {
                block += line.Height;
            }

            // Room a line had to drop through to get clear of a float is height the block owes it too.
            block += line.GapBefore;

            // The room the line takes is what its runs take plus the distance it was set in by, which
            // only the first line of a box has any of.
            inline = Math.Max(inline, line.Indent + line.Used);
        }

        // A cleared break at the content's end reaches past the last line to the floats it clears.
        if (lines.Count > 0)
        {
            block = Math.Max(block, lines[^1].Tail);
        }

        // A multi-column container breaks its lines at the width of one column and is as tall as the
        // columns were shared at, not as the one long stack would have been.
        var multi = container.Style.Multi;

        if (!flow.IsVertical && multi.IsMultiColumn)
        {
            var (columns, share) = multi.Used(maxInline);

            if (columns > 1)
            {
                var columnLines = Plan(container, share, flow, context, Indentation(container, flow, share));
                var breaks = container.Style.Breaks;
                var heights = Heights(columnLines);
                var keeps = Keeps(columnLines);
                var starts = Leadings(container, columnLines, atStart: true);
                var ends = Leadings(container, columnLines, atStart: false);
                var (columnTrimStart, columnTrimEnd) = Trim(container, columnLines);

                // `column-fill: auto` has a column height to fill to only when the box states one. An
                // auto-height box has none, and its first column takes the whole stack - Chrome lays
                // widows-orphans-017's seven lines all in the first of its four columns. A percentage
                // height against a containing block that has no height is auto, so it has none either.
                var height = container.Style.Height;
                var fillsToHeight = !height.IsAuto &&
                    (height.Kind != StyleLengthKind.Percent || context.Containing.ContainingBlock.Height > 0);

                // Columns that wrap make a grid of column boxes, so the content comes to the rows of
                // boxes it needed rather than one stack shared over the columns (CSS Multicol 2 §3).
                if (multi.Wraps && multi.Height is { } statedHeight && statedHeight > 0)
                {
                    var boxes = Math.Max(1, (Int32)Math.Ceiling(Total(heights) / statedHeight));
                    var rows = (Int32)Math.Ceiling((Double)boxes / columns);

                    return new InlineContentSize(
                        inline,
                        Math.Max(0, rows * (statedHeight + multi.RowGap) - multi.RowGap - columnTrimStart - columnTrimEnd));
                }

                if (multi.Fills && !fillsToHeight)
                {
                    return new InlineContentSize(inline, Math.Max(0, Total(heights) - columnTrimStart - columnTrimEnd));
                }

                var columnHeight = multi.Fills
                    ? context.Containing.ContainingBlock.Height
                    : Fragmentainer.BalanceLines(
                        heights,
                        columns,
                        breaks.Orphans,
                        breaks.Widows,
                        starts,
                        ends,
                        keeps);

                return new InlineContentSize(inline, Math.Max(0, columnHeight - columnTrimStart - columnTrimEnd));
            }
        }

        // The leading a block trims away is not height it takes, in the measure as in the arrangement.
        var (trimStart, trimEnd) = Trim(container, lines);

        return new InlineContentSize(inline, Math.Max(0, block - trimStart - trimEnd));
    }

    /// <summary>
    /// Measures the intrinsic inline extents of the container's content: the largest thing that cannot
    /// be broken - <c>min-content</c> - and the extent on a single line - <c>max-content</c>.
    /// </summary>
    /// <param name="basis">
    /// The inline extent a percentage among a replaced box's constraints resolves against; there is no
    /// definite line to use, so the containing block's inline extent stands in.
    /// </param>
    public IntrinsicSize MeasureIntrinsic(Box container, Double basis, WritingModeInfo flow, InlineContext context)
    {
        return MeasureIntrinsicCore(container, basis, flow, context);
    }

    /// <summary>Gets the room the elements open on the line ask for at one of their edges where they
    /// clone their decoration: the inset and the margin a fragment of a cloned box carries with it, which
    /// the walk otherwise charges once for the whole element and never again on the lines the box is
    /// broken across. A box that only slices its decoration is charged its opening edge once and its
    /// closing edge at the end of the run, which is where a sliced box asks for them.</summary>
    private static Double ClonedEdge(IReadOnlyList<OpenInline> open, Boolean start)
    {
        var edge = 0.0;

        for (var i = 0; i < open.Count; i++)
        {
            var ancestor = open[i].Ancestor;

            if (!ancestor.Box.Style.ClonesDecoration)
            {
                continue;
            }

            edge += start
                ? ancestor.InlineStartInset + ancestor.InlineStartMargin
                : ancestor.InlineEndInset + ancestor.InlineEndMargin;
        }

        return edge;
    }

    private IntrinsicSize MeasureIntrinsicCore(Box container, Double basis, WritingModeInfo flow, InlineContext context)
    {
        var items = new List<InlineItem>();
        Collect(container, items, flow, null, basis);

        // The first line carries the box's indentation ahead of anything set on it, so both extents the
        // content asks for are that much wider: one line is the indent and the line, and the narrowest
        // break is the indent and the longest thing that cannot be broken. A forced break ends the line
        // that carried it, and the next segment asks for the distance only where the modifiers put it on
        // a line after a break (CSS Text 3 §5.2).
        var indent = Indentation(container, flow, basis);
        var headIndent = indent.Carries(first: true, afterBreak: false) ? indent.Distance : 0;
        var breakIndent = indent.Carries(first: false, afterBreak: true) ? indent.Distance : 0;

        var minContent = 0.0;
        var maxContent = headIndent;
        var unbreakable = headIndent;

        // The widest line of those a forced break makes. A forced break is honoured even by the
        // max-content size (CSS Sizing 3 §5.1), so the lines it makes lie beside each other rather than
        // one after another: what the content asks for is the widest of them, not their sum.
        var widest = 0.0;

        // Whether the stream has reached anything a line would draw. White space ahead of it asks for
        // no room, because the line it would begin is the one line that does not draw it.
        var sawContent = false;

        // Which line segment the walk is in, counted by the forced breaks it has passed: the opening
        // glyph of a first formatted line and the closing glyph of a last one give up their room, and
        // the block's own text reaches those lines only in the first and final segment (CSS Text 3
        // §8.2.1).
        var segment = 0;

        // The last character the stream drew, kept so a line's closing glyph can give up its room
        // (CSS Text 3 §8.2). White space after it is not drawn at a line's end, so it does not move.
        DrawnGlyph? tail = null;

        // The width of a collapsible run held back until something is drawn after it. CSS 2.1 §16.6.1
        // removes a space at the end of a line just as it removes one at the beginning, so a trailing
        // space is only room the content asks for once the line turns out to carry something past it.
        var trailing = 0.0;

        // Nothing here is broken into lines, so an element is opened once and stays open: the padding
        // it brings with it is charged to the run that enters it, wherever in the stream that is.
        var open = new List<OpenInline>();

        // The last character the stream drew, carried across the chunks of one element and across the
        // edges between them: a boundary the property widens asks for its room on the one line the
        // content needs, wherever the two characters happened to be written.
        var previous = -1;

        // Where each item stands along the one line the content needs. A reading hanging past its pair
        // asks the room of a neighbour whose tab stops are already decided by that place, and the walk
        // from the pair outward cannot answer it for itself.
        var begins = new Double[items.Count];

        for (var position = 0; position < items.Count; position++)
        {
            var item = items[position];

            if (item.IsBreak)
            {
                // The glyph a stop or comma at the line's end hangs from asks for no room on the
                // widest line either: it stands past the edge the line is measured to (CSS Text 3
                // §8.2). `last` reaches only the block's final line, which this is not.
                var ending = tail is { } stop ? EndHang(stop, last: false) : 0;

                // A clone keeps its decoration on every fragment, and with it the room that
                // decoration asks for: the line the break ends is charged the closing edge of each
                // cloning element still open across the break, and the line the break begins is
                // charged their opening edge again (CSS Break 3 4.6, measured:
                // gecko/css-break/box-decoration-break-1, whose column asks for the whole margin box
                // of a clone rather than for its left half).
                // The break ends this line, so every element the break stands outside of closes here:
                // its closing edge is room on the line that ends, not on the one after it. Without this
                // the walk charges that edge when the next item's chain is walked - a line too late - and
                // the line that really held the element reads short of it (measured: a cell holding
                // `<span>b</span><br>c` came to 56.59 rather than the 119.97 the span's margin box asks
                // for, and gecko/css-break/box-decoration-break-1's reference page is that arithmetic
                // with three of them).
                var left = Advance(open, item.Inline, carried: null, origin: 0, apply: true, step: null);

                maxContent += left;
                unbreakable += left;

                var edge = ClonedEdge(open, start: false);

                maxContent += edge;
                unbreakable += edge;

                minContent = Math.Max(minContent, unbreakable - ending);
                widest = Math.Max(widest, maxContent);

                // The line the break ends takes its trailing space with it. The one it begins carries
                // the box's indentation only where the modifiers say a line after a break does, and the
                // opening edge of every element it still stands inside where those elements clone their
                // decoration (CSS Text 3 §5.2 with CSS Break 3 §4.6).
                var reopened = breakIndent + ClonedEdge(open, start: true);

                maxContent = reopened;
                trailing = 0;
                sawContent = false;
                unbreakable = reopened;
                previous = -1;
                segment++;
                tail = null;
                continue;
            }

            // Padding and border are part of what an element asks for, so they count towards both the
            // extent the content needs on one line and the extent the narrowest break can leave it.
            var insets = Advance(open, item.Inline, carried: null, origin: 0, apply: true, step: null);
            maxContent += insets;
            unbreakable += insets;
            begins[position] = maxContent + trailing;

            if (item.IsOutOfFlow)
            {
                // A box that left the flow asks for nothing: the extent the content needs is the extent
                // of what is actually drawn on the line.
                continue;
            }

            if (item.IsReplaced)
            {
                var (width, height) = ReplacedSizing.Resolve(item.Box, basis, 0);
                var extent = flow.InlineExtent(width, height);
                minContent = Math.Max(minContent, extent);
                maxContent += trailing + extent;
                trailing = 0;
                unbreakable += extent;
                sawContent = true;
                previous = -1;
                continue;
            }

            // An inline-block is atomic too: it takes the extent its own content asks for and offers no
            // break inside itself, however its content would have been broken had it stood on its own. The floats the
            // line stands among are left behind here as well - an extent asked of an atomic box is asked of its
            // own root, not of the band around the line (CSS 2.1 §9.4.1, §9.5).
            if (item.IsAtomic)
            {
                var intrinsic = context.Engine.MeasureIntrinsic(
                    item.Box,
                    context.Containing.WithFloats(null));
                var (start, end) = flow.InlineMargins(item.Box.Style.Margin.Resolve(basis));
                var extent = intrinsic.MaxContent + start + end;

                maxContent += trailing + extent;
                trailing = 0;
                unbreakable += extent;
                sawContent = true;
                previous = -1;
                minContent = Math.Max(minContent, intrinsic.MinContent + start + end);
                continue;
            }

            // A ruby pair cannot be broken between its two parts, so it asks for the room the wider of
            // them needs and offers no break opportunity inside itself.
            if (item.IsRuby)
            {
                var baseWidth = PairWidth(item.Style, container.Style, item.Text!, maxContent + trailing, flow);
                var annotationWidth = Measure(item.AnnotationStyle, item.AnnotationText!, flow);
                var (beforeRoom, afterRoom) = OverhangRoom(
                    items, position, maxContent + trailing, baseWidth, annotationWidth, begins, container.Style);
                var extent = RubyExtent(item, baseWidth, annotationWidth, beforeRoom, afterRoom);

                maxContent += trailing + extent;
                trailing = 0;
                unbreakable += extent;
                sawContent = true;
                previous = -1;
                continue;
            }

            // The chunks are ranges of the item's own text: only a width is wanted here, and a string
            // per chunk would be characters copied for a number.
            var source = item.Text!;

            // The character the boundary stands after, kept across the chunks of this one item: where the
            // text of another element begins, the neighbourhood of the two is what the line will decide,
            // and the walk holds no answer for it. It is the last unit of the character before, which is
            // what the rules ask for - a mark written after it is read as that character's own.
            var before = -1;

            foreach (var chunk in TextChunker.Ranges(source, item.Style.WordBreakingRules))
            {
                // A tab asks the same question here as on a line: how far the content has run decides
                // where the next stop is.
                var width = TextChunker.IsCollapsible(source, chunk.Start, chunk.Length)
                    ? WhiteSpaceWidth(item.Style, container.Style, source, chunk.Start, chunk.Length, maxContent, flow)
                    : Measure(item.Style, source, chunk.Start, chunk.Length, flow);
                var space = TextChunker.IsCollapsible(source, chunk.Start, chunk.Length);
                var collapsible = space && item.Style.CollapsesSpaces;

                // A collapsible space the line would begin with is not drawn, so it asks for no room in
                // either extent and leaves the break it stands at uncounted as well.
                if (collapsible && !sawContent)
                {
                    continue;
                }

                // Where the tailorable properties put a break between the characters of a word, the
                // narrowest the content can be is no longer the widest word but the widest piece between
                // those breaks - a preserved space excepted, since the rule that forbids a break before one
                // is set aside only by the values that speak of white space, and punctuation excepted, since
                // `break-all` speaks of letters (CSS Text 3 §5.4-§5.6, and Chrome measures "X XX X" at two
                // characters under `break-all` and at one under `overflow-wrap: anywhere`, with "AB., CD"
                // at three). `overflow-wrap: break-word` is left out: its breaks are the last resort a line
                // takes, and a line that could have been narrower is not what the property is asked for.
                if (!collapsible &&
                    (item.Style.BreaksAnywhere ||
                        item.Style.BreaksBetweenLetters ||
                        item.Style.CountsLongWordBreaks) &&
                    item.Style.WrapsLines)
                {
                    var end = chunk.Start + chunk.Length;

                    for (var at = chunk.Start; at < end;)
                    {
                        var units = TextClusters.UnitsAt(source, at, end);
                        var standing = TextChunker.IsCollapsible(source, at, units);

                        // Which side of the boundary the segment ends at: a value that sets the rules aside
                        // answers for every boundary it meets, wherever the character came from; the mode
                        // answers after every space it keeps, its own or another separator; and
                        // `word-break` leaves the rules standing - both the rules themselves and the
                        // neighbourhood the line will settle when the piece was written on another element.
                        var ends = item.Style.BreaksAnywhere || item.Style.CountsLongWordBreaks
                            ? true
                            : standing
                                ? item.Style.WrapsBeforeSpaces
                                : before >= 0 &&
                                    LineBreaks.MayBreak(source, before, at, item.Style.WordBreakingRules);

                        if (ends)
                        {
                            minContent = Math.Max(minContent, unbreakable);
                            unbreakable = 0;
                        }

                        unbreakable += space
                            ? WhiteSpaceWidth(item.Style, container.Style, source, at, units, maxContent, flow)
                            : Measure(item.Style, source, at, units, flow);
                        before = at + units - 1;
                        at += units;
                    }

                    maxContent += trailing + width;
                    trailing = 0;
                    sawContent = true;
                    tail = new DrawnGlyph(
                        item.Style,
                        source,
                        TextClusters.ClusterStart(source, chunk.Start + chunk.Length),
                        chunk.Start + chunk.Length - TextClusters.ClusterStart(source, chunk.Start + chunk.Length),
                        flow);
                    continue;
                }

                if (space)
                {
                    // White space is a break opportunity: the run before it can be split, and the space
                    // itself belongs to neither side of that break. A style that suppresses soft breaks -
                    // `nowrap` or `pre` - offers none, and then the space reads as part of the one piece
                    // the text is, so it joins that piece instead of ending it (CSS Text 3 §5.1). That is
                    // what makes a row of `nowrap` labels measure as wide as it reads: the minimum was
                    // being taken as the widest single character, and a flex container was free to shrink
                    // every label down to one of them.
                    if (item.Style.WrapsInsideSpaces)
                    {
                        // The opportunity sits after every character of such a run and before none of them,
                        // so the run's first character is carried by the piece above it while every other
                        // one may stand on a line by itself (CSS Text 3 §4.1.3, and Chrome's min-content of
                        // "A   B" is the width of "A " for exactly that reason).
                        minContent = Math.Max(
                            minContent,
                            unbreakable + WhiteSpaceWidth(item.Style, container.Style, source, chunk.Start, 1, maxContent, flow));

                        unbreakable = 0;

                        for (var index = chunk.Start + 1; index < chunk.Start + chunk.Length; index++)
                        {
                            minContent = Math.Max(
                                minContent,
                                WhiteSpaceWidth(item.Style, container.Style, source, index, 1, 0, flow));
                        }
                    }
                    else if (item.Style.WrapsLines)
                    {
                        minContent = Math.Max(minContent, unbreakable);
                        unbreakable = 0;
                    }
                    else
                    {
                        unbreakable += trailing;
                    }

                    if (collapsible)
                    {
                        // A collapsible space the line would end with is not drawn either (§16.6.1), so
                        // its width waits here until something is drawn after it: only a space the line
                        // carries between two runs is room the content asks for.
                        trailing += width;
                        continue;
                    }

                    if (!item.Style.WrapsLines)
                    {
                        unbreakable += width;
                    }

                    // A preformatted space is drawn wherever it stands, so it is room the content asks
                    // for on the one line - but it is still not part of the narrowest break.
                    maxContent += trailing + width;
                    trailing = 0;
                    sawContent = true;
                    previous = TextClusters.BaseBefore(source, chunk.Start + chunk.Length);
                    continue;
                }

                // Something the line draws follows whatever space was held back, so the space between
                // the two runs is room the content asks for after all - and room inside the piece, where
                // the style offers no break for it to be left at.
                if (!space && previous >= 0)
                {
                    maxContent += AutoSpaceBetween(
                        _measurer, item.Style, previous, TextChunker.CodePointAt(source, chunk.Start), flow);
                }

                // The opening glyph of a first formatted line hangs past the line, so the widest line
                // the content asks for does not count it (CSS Text 3 §8.2.1, §8.2).
                if (!sawContent && previous < 0)
                {
                    var opening = FirstHangRoom(
                        item.Style, source, chunk.Start, chunk.Length, item.Inline is not null || segment == 0, flow);

                    maxContent -= opening;
                    unbreakable -= opening;
                }

                maxContent += trailing + width;

                previous = TextClusters.BaseBefore(source, chunk.Start + chunk.Length);

                if (!item.Style.WrapsLines)
                {
                    unbreakable += trailing;
                }

                trailing = 0;
                sawContent = true;
                tail = new DrawnGlyph(
                    item.Style,
                    source,
                    TextClusters.ClusterStart(source, chunk.Start + chunk.Length),
                    chunk.Start + chunk.Length - TextClusters.ClusterStart(source, chunk.Start + chunk.Length),
                    flow);

                if (source[chunk.Start + chunk.Length - 1] == (Char)0x00AD && item.Style.WrapsLines &&
                    !item.Style.WordBreak.Equals("auto-phrase", StringComparison.OrdinalIgnoreCase))
                {
                    // A line may end at the soft hyphen, so the piece before it is one the content can
                    // be as narrow as - the mark included, since the break draws it (CSS Text 3 §6.1).
                    // Without this a table cell holding "supercalifragilistic-shy-expialidocious" was
                    // as wide as the whole word and never wrapped.
                    minContent = Math.Max(minContent, unbreakable + width + HyphenWidth(_measurer, item.Style, flow));
                    unbreakable = 0;
                }
                else if (TextChunker.IsCjk(source, chunk.Start) && item.Style.WrapsLines)
                {
                    // A line may break on either side of a CJK character, so the character stands
                    // alone rather than joining the run around it, and it is the run beside it - the
                    // Latin word, say - that decides how narrow the content can go.
                    minContent = Math.Max(minContent, Math.Max(unbreakable, width));
                    unbreakable = 0;
                }
                else
                {
                    unbreakable += width;
                }
            }
        }

        // An element the stream ends inside still draws its closing edge, so it is charged for it here.
        foreach (var item in open)
        {
            maxContent += item.Ancestor.InlineEndInset + item.Ancestor.InlineEndMargin;
            unbreakable += item.Ancestor.InlineEndInset + item.Ancestor.InlineEndMargin;
        }

        // The closing glyph of the block's final line hangs past it, so neither extent counts it
        // (CSS Text 3 §8.2.1, §8.2).
        var closingRoom = tail is { } last ? EndHang(last, last: true) : 0;

        minContent = Math.Max(minContent, unbreakable - closingRoom);
        widest = Math.Max(widest, maxContent - closingRoom);

        return new IntrinsicSize(minContent, Math.Max(widest, minContent));
    }

    /// <summary>One character the intrinsic walk drew, with where its room can be asked for again.</summary>
    private readonly record struct DrawnGlyph(
        ComputedStyle Style,
        String Source,
        Int32 Start,
        Int32 Length,
        WritingModeInfo Flow);

    /// <summary>
    /// Gets the room the opening glyph of a first formatted line asks for under the value: nothing at
    /// all, because the glyph hangs outside the line the content is measured at.
    /// </summary>
    private Double FirstHangRoom(
        ComputedStyle style,
        String source,
        Int32 start,
        Int32 length,
        Boolean firstOfElement,
        WritingModeInfo flow)
    {
        if (!firstOfElement || !HangingPunctuation.HangsAtStart(style.HangingPunctuation))
        {
            return 0;
        }

        var units = TextClusters.UnitsAt(source, start, start + length);

        return HangingPunctuation.IsOpening(TextChunker.CodePointAt(source, start))
            ? Measure(style, source, start, units, flow)
            : 0;
    }

    /// <summary>
    /// Gets the room the closing glyph of a line asks for under the value: nothing at all, because the
    /// glyph hangs outside the line. <c>allow-end</c> hangs only a line that would not otherwise fit,
    /// and the widest line always fits.
    /// </summary>
    private Double EndHang(DrawnGlyph glyph, Boolean last) =>
        HangingPunctuation.HangsAtEnd(
            glyph.Style.HangingPunctuation,
            TextChunker.CodePointAt(glyph.Source, glyph.Start),
            overflows: false,
            last)
            ? Measure(glyph.Style, glyph.Source, glyph.Start, glyph.Length, glyph.Flow)
            : 0;

    /// <summary>
    /// Lays out the inline children of the container inside the given content box, appending line
    /// boxes to it, and returns the extent they occupied along the block axis.
    /// </summary>
    public Double Layout(Box container, LayoutBox parent, Rect contentBox, WritingModeInfo flow, InlineContext context)
    {
        return LayoutCore(container, parent, contentBox, flow, context);
    }

    /// <summary>
    /// Gets how tall each of the container's lines stands at the given width. A block whose parent is
    /// multi-column is broken between its own lines (CSS Multicol 3 §3.1, CSS Break 3 §4.4), and the
    /// parent needs those heights to know where a column ends.
    /// </summary>
    public IReadOnlyList<Double> LineHeights(
        Box container, Double maxInline, WritingModeInfo flow, InlineContext context) =>
        Heights(Plan(container, maxInline, flow, context, Indentation(container, flow, maxInline)));

    /// <summary>
    /// Gets how tall each line of the content from <paramref name="start"/> on stands: what a column
    /// that resumes a broken paragraph needs, where the lines are longer than the first column's
    /// because the float that shortened them is not beside them.
    /// </summary>
    internal IReadOnlyList<Double> LineHeights(
        Box container, Double maxInline, WritingModeInfo flow, InlineContext context, InlineStart start) =>
        Heights(Plan(container, maxInline, flow, context, Indentation(container, flow, maxInline), start));

    /// <summary>
    /// Lays the lines numbered <paramref name="from"/> up to <paramref name="to"/> into the given content
    /// box, which is one fragment of a block broken across columns. The plan is the whole content's, so
    /// the line a fragment ends on is not the line the block ends on and is not aligned as one; the
    /// fragment that resumes a break starts where its first line stands rather than under a trimmed
    /// leading, which belongs to the fragment that begins the block.
    /// </summary>
    public Double Layout(
        Box container,
        LayoutBox parent,
        Rect contentBox,
        WritingModeInfo flow,
        InlineContext context,
        Int32 from,
        Int32 to,
        InlineStart? start = null)
    {
        var maxInline = flow.InlineExtent(contentBox.Width, contentBox.Height);
        var lines = Plan(container, maxInline, flow, context, Indentation(container, flow, maxInline), start);

        AlignLastLines(lines, maxInline);
        DistributeSpace(lines);

        var (trimStart, _) = Trim(container, lines);

        // A block broken across columns is placed one fragment at a time, so the inline elements it
        // holds are whole only within the fragment being placed. What that costs is written up under
        // Placement. A fragment that resumes partway through starts where its plan does: the trimmed
        // leading belongs to the fragment that began the block.
        var placement = new Placement(context.Engine);
        var used = PlaceLines(
            container, parent, lines, from, to, contentBox, flow, context, start is null && from == 0 ? -trimStart : 0, placement);
        placement.Finish(flow);

        return used;
    }

    private Double LayoutCore(Box container, LayoutBox parent, Rect contentBox, WritingModeInfo flow, InlineContext context)
    {
        var maxInline = flow.InlineExtent(contentBox.Width, contentBox.Height);
        var multi = container.Style.Multi;

        // A multi-column container breaks its lines at the width of one column, so the plan it needs is
        // a different one from the block's own extent: it is made here, at the column's width, and the
        // columns share it.
        if (!flow.IsVertical && multi.IsMultiColumn)
        {
            var (columns, share) = multi.Used(maxInline);

            if (columns > 1)
            {
                return LayoutColumns(container, parent, contentBox, flow, context, columns, share, multi);
            }
        }

        // The lines the measuring phase broke this content into, if it broke them at the same extent -
        // which is what the block layout makes sure of, since it reconstructs the child's containing
        // block from the same arithmetic it measured against.
        var lines = Plan(container, maxInline, flow, context, Indentation(container, flow, maxInline));

        // Which lines the block's alignment reaches is settled before the room is spread: the last line
        // and the ones a forced break ended are the block's `text-align-last` to align, and a line it
        // leaves justified is spread like any other.
        AlignLastLines(lines, maxInline);

        // Justification is the arrangement's business and not the breaking's: the same lines the
        // measuring broke are spread across their spaces here, where the room they were given is
        // settled and will not move again.
        DistributeSpace(lines);

        // CSS Inline 3 §5.1: a block may trim the leading away at its start and end, so the first and last
        // lines stand with their text edge on the block's edge. The line box is not resized - the lines
        // simply start above the block's start and the block's height ends at the last text edge.
        var (trimStart, trimEnd) = Trim(container, lines);
        var placement = new Placement(context.Engine);
        var cursor = PlaceLines(container, parent, lines, 0, lines.Count, contentBox, flow, context, -trimStart, placement);

        // Whatever the lines held back is placed once the whole of the content stands where it does, and
        // the displaced elements are moved before the container's height is handed back - which is a
        // question of where the drawing lands, not of how tall the block came out.
        placement.Finish(flow);

        return Math.Max(0, cursor - trimEnd);
    }

    /// <summary>
    /// Lays the lines out in the container's columns: each column takes the lines the line slicer gives
    /// it (CSS Break 3 §3.3 keeps the orphans and widows), and the lines of one column stand at its own
    /// inline offset. The columns end at the height they were shared at, which the measure settled.
    /// </summary>
    private Double LayoutColumns(
        Box container,
        LayoutBox parent,
        Rect contentBox,
        WritingModeInfo flow,
        InlineContext context,
        Int32 columns,
        Double share,
        MulticolStyle multi)
    {
        var lines = Plan(container, share, flow, context, Indentation(container, flow, share));
        AlignLastLines(lines, share);
        DistributeSpace(lines);

        var heights = Heights(lines);
        var keeps = Keeps(lines);
        var breaks = container.Style.Breaks;
        var starts = Leadings(container, lines, atStart: true);
        var ends = Leadings(container, lines, atStart: false);
        // A container that wraps its columns makes a grid of column boxes, one `column-height` tall,
        // and the lines run down a box, on into the next column and then into the next row.
        var columnHeight = multi.Wraps && multi.Height is { } stated ? stated : 0.0;
        var wraps = columnHeight > 0;
        var rows = wraps && contentBox.Height > 0
            ? Math.Max(1, (Int32)Math.Floor((contentBox.Height + multi.RowGap) / (columnHeight + multi.RowGap)))
            : 1;

        var extent = wraps
            ? columnHeight
            : multi.Fills
                ? contentBox.Height
                : Fragmentainer.BalanceLines(
                    heights,
                    columns,
                    breaks.Orphans,
                    breaks.Widows,
                    starts,
                    ends,
                    keeps);

        // A fill is not held to the column count: content that does not fit the stated columns spills
        // into more of them, which is how Chrome lays a run too long for its `column-count` out.
        var counts = Fragmentainer.FillLines(
            heights,
            extent,
            wraps ? columns * rows : multi.Fills ? Int32.MaxValue : columns,
            breaks.Orphans,
            breaks.Widows,
            starts,
            ends,
            spills: multi.Fills && !wraps,
            keeps: keeps);

        // Each column trims the leading off its own first and last lines (CSS Inline 3 §6.2), so every
        // column begins with its first line standing where that line's text edge asks.
        var (trimStart, trimEnd) = Trim(container, lines);
        var from = 0;

        // The columns share one placement, because an inline element reaching down past the end of one
        // is one box standing in several of them - and the boxes it holds are anchored to the whole of it.
        var placement = new Placement(context.Engine);

        for (var cell = 0; cell < counts.Count; cell++)
        {
            var count = counts[cell];
            var area = new Rect(
                contentBox.X + (cell % columns) * (share + multi.Gap),
                contentBox.Y + (cell / columns) * (columnHeight + multi.RowGap),
                share,
                extent);

            PlaceLines(container, parent, lines, from, from + count, area, flow, context, -starts[from], placement);
            from += count;
        }

        placement.Finish(flow);
        MulticolRules.Add(
            parent,
            container.Style,
            contentBox,
            new List<(Double, Double)> { (contentBox.Y, contentBox.Y + extent) });

        return Math.Max(0, extent - trimStart - trimEnd);
    }

    /// <summary>
    /// Builds the line flow of one margin-note group at the inline extent the page margin offers.
    /// Margin notes use the ordinary line composer but are placed as one block after the document
    /// layout has settled.
    /// </summary>
    internal MarginNoteFlow BuildMarginNoteFlow(
        Box note,
        Double inlineExtent,
        WritingModeInfo flow,
        InlineContext context)
    {
        var source = NoteSource.Read(note);
        var items = NoteItems(
            source,
            0,
            source.Text.Length,
            inlineExtent,
            flow,
            upright: false);
        var lines = items.Count == 0
            ? new List<Line>()
            : BuildLines(
                items,
                inlineExtent,
                flow,
                context,
                note.Style,
                Indentation(note, flow, inlineExtent),
                emergency: false,
                settleEdges: true);

        AlignLastLines(lines, inlineExtent);
        DistributeSpace(lines);

        var block = 0.0;

        foreach (var line in lines)
        {
            block += Math.Max(0, line.Height) + line.GapBefore;
        }

        return new MarginNoteFlow(note, lines, flow, block, this);
    }

    /// <summary>Places a margin-note flow into its already-positioned content rectangle.</summary>
    internal void PlaceMarginNoteFlow(
        MarginNoteFlow flow,
        LayoutBox parent,
        Rect contentBox,
        InlineContext context)
    {
        if (flow.BlockSize <= 0)
        {
            return;
        }

        flow.Place(parent, contentBox, context);
    }

    /// <summary>Gets the heights the lines take when stacked, the gaps a float made included.</summary>
    /// <summary>Gets how far along its line each of the container's lines reaches at the given width:
    /// the place a float written after that content stands at, and a right one ends at (CSS 2.1 §9.5.1).
    /// </summary>
    public IReadOnlyList<Double> LineWidths(
        Box container, Double maxInline, WritingModeInfo flow, InlineContext context) =>
        Widths(Plan(container, maxInline, flow, context, Indentation(container, flow, maxInline)));

    private static List<Double> Widths(List<Line> lines)
    {
        var widths = new List<Double>(lines.Count);

        foreach (var line in lines)
        {
            // `Used` rather than `Extent`: the extent is the room the line was broken at, which is the
            // whole block unless a float shortened it, while a float set after the line has to begin
            // where the line's own content reaches (CSS 2.1 §9.5.1).
            widths.Add(Math.Max(0, line.Used));
        }

        return widths;
    }

    private static List<Double> Heights(List<Line> lines)
    {
        var heights = new List<Double>(lines.Count);

        foreach (var line in lines)
        {
            heights.Add(Math.Max(0, line.Height) + line.GapBefore + line.Tail);
        }

        return heights;
    }

    /// <summary>Gets the boundaries between lines that a fragment break must not cut.</summary>
    private static List<Boolean> Keeps(List<Line> lines)
    {
        var keeps = new List<Boolean>(lines.Count);

        foreach (var line in lines)
        {
            keeps.Add(line.KeepWithNext);
        }

        return keeps;
    }

    private static Double Total(List<Double> heights)
    {
        var total = 0.0;

        foreach (var height in heights)
        {
            total += height;
        }

        return total;
    }

    /// <summary>
    /// Places lines <paramref name="from"/> to <paramref name="to"/> into the content box, stacking them
    /// from <paramref name="start"/>, and returns where the stack ended.
    /// </summary>
    private Double PlaceLines(
        Box container,
        LayoutBox parent,
        List<Line> lines,
        Int32 from,
        Int32 to,
        Rect contentBox,
        WritingModeInfo flow,
        InlineContext context,
        Double start,
        Placement placement)
    {
        var cursor = start;
        var step = new InlineStep();

        for (var lineIndex = from; lineIndex < to; lineIndex++)
        {
            var line = lines[lineIndex];

            // A line is placed for what is on it, not for how tall it came out: a run set with no line
            // height at all still has glyphs, and they are drawn overflowing the line they sit on.
            if (line.Fragments.Count == 0)
            {
                cursor += Math.Max(0, line.Height) + line.GapBefore;
                continue;
            }

            // The room a line had to fall through to get clear of a float belongs ahead of it, not after:
            // the line that dropped stands at the height it reached, and the ones below it stack from there.
            cursor += line.GapBefore;

            // A line whose text runs both ways is drawn in the order the bidi algorithm gives it: the
            // runs are split where their direction changes, and the pieces stand in the line by their
            // levels (UAX #9 L2). A line of one direction is left exactly as the markup wrote it.
            // A column is a line like any other - it only runs the other way - and the level a run is
            // set at says which end of its room its first character stands at there too.
            BidiOrder(line, flow, container.Style);

            // The line box spans the whole inline extent of the content box and only as much of the
            // block one as it needs, so in a row it is the content width and the line's own height,
            // and in a column the content height and the band the text runs down.
            var lineBox = new LayoutBox(container, LayoutKind.Line)
            {
                BorderBox = flow.Map(contentBox, line.Left, line.Extent, cursor, line.Height),
                Baseline = flow.BlockOffsetFromLeft(line.Height, line.Ascent),
                Flow = flow,
            };

            foreach (var marker in line.Fragments)
            {
                if (marker.MarginNote is { } margin)
                {
                    context.Engine.RegisterMarginNote(margin.Box, lineBox);
                }
            }

            var open = new List<OpenInline>();
            var origin = line.Offset + line.Left;
            var last = line.Fragments[^1];

            foreach (var fragment in line.Fragments)
            {
                // A glyph hanging off the line's start edge stands one advance before the line: the
                // boxes it belongs to reach out over it, and the line's own cursor is where the
                // content after it begins (CSS Text 3 §8.2.1).
                var box = fragment.HangSide == HangStart ? origin - fragment.Width : origin;

                if (fragment.InterlineGap || fragment.InterlineNote is not null ||
                    fragment.MarginNote is not null)
                {
                    origin = box + fragment.Width;
                    continue;
                }

                // The inline elements the run sits inside are opened and closed around it, each taking
                // a box that begins and ends where the run reaches its edges. The step charges the same
                // padding and border the line was measured with, applied here one edge at a time so
                // that every box lands where the extent says it should.
                step.Clear();
                Advance(open, fragment.Inline, line.ContinuesFromPrevious, box, apply: true, step);

                // An element the run leaves is never cut: a cut is what carrying on to the next line
                // is, and an element that ends in the middle of one ends for good. Its box runs to the
                // far side of its own end edge, which is the edge the run it holds stops short of - and
                // its end margin then takes its room on the line, after that box.
                foreach (var left in step.Closed)
                {
                    box += left.Ancestor.InlineEndInset;
                    CloseBox(left, box, contentBox, flow, line, cursor, context, placement);
                    box += left.Ancestor.InlineEndMargin;
                }

                foreach (var entered in step.Opened)
                {
                    // The edge the element begins at is the one Advance worked out: a box the line
                    // resumes inside was cut at its start edge before the break, and pays nothing for
                    // that edge - or for the margin that stands outside it - again here.
                    box = entered.Start;
                    OpenBox(entered, open, lineBox, flow, line);
                    box += entered.Inset;
                }

                // The spaces a `pre-wrap` line hangs are drawn where they stand but take none of the
                // room the line was given, so the box of the run that trails into them is narrowed by
                // that much: either way the glyphs land where a line without the spaces would have put
                // them, which is what a right-to-line-end hanging looks like from both directions.
                var room = ReferenceEquals(fragment, last) ? fragment.Width - line.Hanging : fragment.Width;

                Place(fragment, open, lineBox, contentBox, flow, line, box, cursor, context, room, placement, _measurer);

                // A glyph hung from the end edge keeps its advance on the line's boxes - the background
                // of an inline element covers it - while the room the line was measured at already
                // leaves it out.
                origin = box + fragment.Width;
            }

            // The elements the line ends inside carry on to the next line, so each keeps its end edge
            // only if it does not: where it was cut, that edge belongs to the line the element ends on.
            for (var index = open.Count - 1; index >= 0; index--)
            {
                var left = open[index];
                var cut = !left.Ancestor.Box.Style.ClonesDecoration &&
                    line.ContinuesToNext?.Contains(left.Ancestor) == true;

                origin += cut ? 0 : left.Ancestor.InlineEndInset;
                CloseBox(left, origin, contentBox, flow, line, cursor, context, placement);

                if (!cut)
                {
                    origin += left.Ancestor.InlineEndMargin;
                }
            }

            parent.Children.Add(lineBox);
            cursor += line.Height;
        }

        // A break that clears at the content's end carries the block down to the floats it clears,
        // past the line the break stands on (CSS 2.1 §9.5.2).
        if (to >= lines.Count && start >= -0.0001 && lines.Count > 0)
        {
            cursor = Math.Max(cursor, lines[^1].Tail);
        }

        return cursor;
    }

    /// <summary>
    /// Splits a line's runs where their direction changes, so each piece of text is shaped in the
    /// direction it is read in: a Latin word inside a right-to-left line keeps its letters in the order
    /// a reader of it expects, and a neutral at the end of such a line is set at the line's own level
    /// (UAX #9 W, N, I). The pieces keep the logical order of the runs - the line places its fragments
    /// from the edge its own direction begins at - and each records the direction the algorithm gave
    /// it, which the painter turns the glyphs of a run round by where the shaper would not.
    /// </summary>
    /// <remarks>
    /// The levels are resolved over the whole line, not over one fragment at a time: what the strong
    /// text before a space says about it is a question about the line, and the whitespace at the line's
    /// end is the only whitespace the rules reset (L1). An element that asks for an embedding sets its
    /// content at a level of its own, and an override also replaces every character's class with its
    /// direction (X1-X8); the line is one string by then, so the marks that say so are written around
    /// each run's text, resolved with it, and dropped. A line that holds no change of direction is left
    /// as the markup wrote it and pays one pass over its classes.
    /// </remarks>
    private void BidiOrder(Line line, WritingModeInfo flow, ComputedStyle container)
    {
        var baseLevel = flow.IsRightToLeft ? 1 : 0;

        var changed = false;
        var ordered = new List<TextFragment>(line.Fragments.Count);
        var group = new List<TextFragment>();
        var joined = new System.Text.StringBuilder();

        void Flush()
        {
            if (group.Count == 0)
            {
                return;
            }

            var text = joined.ToString();

            // `unicode-bidi: plaintext` takes the paragraph's direction from the first strong character
            // written on the line, so a line is resolved at the level its own text asks for (P2, P3).
            var level = container.PlaintextParagraph || group[0].Style.PlaintextParagraph
                ? Bidi.ParagraphLevel(text, baseLevel)
                : baseLevel;

            // An element that asks for an embedding sets its content at a level of its own, and an
            // override also replaces every character's class with its own direction (UAX #9 X1-X8).
            // The runs were flattened into one string, so the marks that say so are written around
            // each run's own text here, read back with it, and dropped. For the container only the
            // override reaches the line: its direction is the line's base level already, so an embed
            // or an isolate embeds the line in what it already is, while an override rewrites the
            // direction of every character written on it (CSS Writing Modes 3 §5.2).
            var wrapped = new System.Text.StringBuilder(text.Length + (group.Count * 8) + 4);
            var starts = new Int32[group.Count];
            var lineMarks = Overrides(container) ? Controls(container) : default;

            if (lineMarks.Open is { } opening)
            {
                wrapped.Append(opening);
            }

            for (var index = 0; index < group.Count; index++)
            {
                var fragment = group[index];
                var marks = new List<(String Open, String Close)>(3);

                foreach (var style in Embeddings(fragment))
                {
                    marks.Add(Controls(style));
                }

                foreach (var (mark, _) in marks)
                {
                    wrapped.Append(mark);
                }

                starts[index] = wrapped.Length;
                wrapped.Append(fragment.Text);

                for (var at = marks.Count - 1; at >= 0; at--)
                {
                    wrapped.Append(marks[at].Close);
                }
            }

            if (lineMarks.Close is { } closing)
            {
                wrapped.Append(closing);
            }

            var levels = Bidi.Levels(wrapped.ToString(), level);

            for (var index = 0; index < group.Count; index++)
            {
                var fragment = group[index];
                var own = new Int32[fragment.Length];
                Array.Copy(levels, starts[index], own, 0, fragment.Length);

                var uniform = true;

                for (var at = 1; at < own.Length; at++)
                {
                    if (own[at] != own[0])
                    {
                        uniform = false;
                        break;
                    }
                }

                if (uniform)
                {
                    fragment.RightToLeft = own[0] % 2 == 1;
                    fragment.Level = own[0];
                    ordered.Add(fragment);
                    continue;
                }

                changed = true;
                var runs = Bidi.Runs(fragment.Text, own);
                var pieces = new List<TextFragment>(runs.Count);
                var earned = 0.0;

                foreach (var run in runs)
                {
                    var width = MeasureChunk(_measurer, fragment.Style, fragment.Text, run.Start, run.Length, fragment.Flow);
                    earned += width;

                    pieces.Add(new TextFragment(
                        fragment.Text, run.Start, run.Length, width,
                        fragment.Style, fragment.Box, fragment.Flow, fragment.Inline)
                    {
                        RightToLeft = run.Level % 2 == 1,
                        Level = run.Level,
                        Trims = MaskOf(fragment.Trims, run.Start, run.Length, false),
                        AutoSpaces = MaskOf(fragment.AutoSpaces, run.Start, run.Length, true),
                        AutoSpaceRoom = fragment.AutoSpaceRoom,
                    });
                }

                // The room a merge folded into the run stands between two pieces of it, and the pieces
                // are boxes of their own now: the room is the gap after the piece it follows, and the
                // mask no longer draws it (the piece was given the slice with its trailing mark cleared).
                for (var at = 1; at < runs.Count; at++)
                {
                    if (SpacedBefore(fragment, runs[at].Start))
                    {
                        pieces[at - 1].Width += fragment.AutoSpaceRoom;
                        earned += fragment.AutoSpaceRoom;
                    }
                }

                if (pieces.Count > 0)
                {
                    // A piece holds the places of the run it was cut from, and the room the line gave
                    // each of them: the painter spreads the share wherever the piece's own text offers
                    // a place, so a place the split lost at a boundary is handed to the piece that
                    // ends the run, where the line's own edge stands.
                    var expansion = 0.0;

                    if (fragment.Expansions > 0 && fragment.Justify != 0)
                    {
                        var given = 0;

                        foreach (var piece in pieces)
                        {
                            var room = fragment.Expansions - given;

                            if (room <= 0)
                            {
                                break;
                            }

                            var places = Math.Min(
                                room,
                                TextClusters.ExpansionPoints(
                                    piece.Source, piece.Start, piece.Length, piece.Style.TextJustify).Length);

                            if (places <= 0)
                            {
                                continue;
                            }

                            piece.Expand(fragment.Justify, places);
                            given += places;
                            expansion += fragment.Justify * places;
                        }

                        if (given < fragment.Expansions)
                        {
                            var places = fragment.Expansions - given;

                            pieces[^1].Expand(fragment.Justify, pieces[^1].Expansions + places);
                            expansion += fragment.Justify * places;
                        }
                    }

                    if (Math.Abs(fragment.Width - earned - expansion) > 0.0001)
                    {
                        pieces[^1].Width += fragment.Width - earned - expansion;
                    }

                    // A hung glyph is one character and keeps the edge of the run it was cut from:
                    // the piece that begins at the run's start, or the one that ends at its end.
                    if (fragment.HangSide == HangStart)
                    {
                        pieces[0].HangSide = HangStart;
                    }
                    else if (fragment.HangSide == HangEnd)
                    {
                        pieces[^1].HangSide = HangEnd;
                    }

                    // The space a pre-wrap line hangs is at the end of its own text, so it is the last
                    // piece that carries it: the line takes the room off that piece and the painter
                    // draws it without the characters it hangs.
                    if (fragment.Hanging > 0)
                    {
                        pieces[^1].Hanging = Math.Min(fragment.Hanging, pieces[^1].Length);
                    }
                }

                foreach (var piece in pieces)
                {
                    ordered.Add(piece);
                }
            }

            group.Clear();
            joined.Clear();
        }

        foreach (var fragment in line.Fragments)
        {
            if (fragment.Length == 0 || fragment.IsReplaced || fragment.Atomic is not null ||
                fragment.Ruby is not null || fragment.OutOfFlow)
            {
                if (fragment.InterlineGap || fragment.InterlineNote is not null ||
                    fragment.MarginNote is not null)
                {
                    fragment.Level = baseLevel;
                }

                Flush();
                ordered.Add(fragment);
                continue;
            }

            // `unicode-bidi: plaintext` makes an element's own text a paragraph: its direction is read
            // off the first strong character written there, not off the text around it (P2, P3), so a
            // run that asks for it is resolved apart from the runs beside it.
            if (group.Count > 0 && group[^1].Style.PlaintextParagraph != fragment.Style.PlaintextParagraph)
            {
                Flush();
            }

            group.Add(fragment);
            joined.Append(fragment.Text);
        }

        Flush();

        if (changed)
        {
            line.Fragments.Clear();
            line.Fragments.AddRange(ordered);
        }

        // A column mirrors its inline axis along the column, where the same reversal already stands:
        // what is left to order is a line's runs.
        if (!flow.IsVertical)
        {
            Reorder(line, baseLevel);
        }
    }

    /// <summary>
    /// Puts the line's runs in the order they are drawn in (UAX #9 L2). The flow lays a right-to-left
    /// line from its right edge, which is the paragraph-level reversal L2 makes - so the runs are
    /// handed over that way round, and the reversal cancels the mirror.
    /// </summary>
    /// <remarks>
    /// Only a line whose every run knows its level is put through this: a box that is not text has a
    /// level the line has not asked for, and moving the runs around it would guess.
    /// </remarks>
    private static void Reorder(Line line, Int32 baseLevel)
    {
        if (line.Fragments.Count < 2)
        {
            return;
        }

        var levels = new Int32[line.Fragments.Count];

        for (var index = 0; index < levels.Length; index++)
        {
            if (line.Fragments[index].Level < 0)
            {
                return;
            }

            levels[index] = line.Fragments[index].Level;
        }

        var visual = Bidi.VisualOrder(levels);

        if (baseLevel == 1)
        {
            Array.Reverse(visual);
        }

        var reordered = new List<TextFragment>(visual.Length);

        foreach (var at in visual)
        {
            reordered.Add(line.Fragments[at]);
        }

        line.Fragments.Clear();
        line.Fragments.AddRange(reordered);
    }

    /// <summary>
    /// Gets the elements around one run that ask for a bidi embedding, from the outermost in. An
    /// element with no <c>unicode-bidi</c> of its own leaves its content in the embedding it
    /// inherited. The block container is not here: its own embedding wraps the whole line turn.
    /// </summary>
    private static List<ComputedStyle> Embeddings(TextFragment fragment)
    {
        var chain = new List<ComputedStyle>(3);
        var ancestors = new List<InlineAncestor>(3);

        for (var ancestor = fragment.Inline; ancestor is not null; ancestor = ancestor.Parent)
        {
            ancestors.Add(ancestor);
        }

        ancestors.Reverse();

        foreach (var ancestor in ancestors)
        {
            if (IsEmbedding(ancestor.Box.Style))
            {
                chain.Add(ancestor.Box.Style);
            }
        }

        return chain;
    }

    private static Boolean IsEmbedding(ComputedStyle style) =>
        style.UnicodeBidiKeyword?.Trim().ToLowerInvariant() is
            "embed" or "bidi-override" or "isolate" or "isolate-override";

    /// <summary>
    /// Gets whether an element rewrites the direction of the characters written on it rather than
    /// only setting the level they are set at (CSS Writing Modes 3 §5.2).
    /// </summary>
    private static Boolean Overrides(ComputedStyle style) =>
        style.UnicodeBidiKeyword?.Trim().ToLowerInvariant() is "bidi-override" or "isolate-override";

    /// <summary>
    /// Gets the pair of control characters that put an element's text at the level, and in the
    /// direction, its <c>unicode-bidi</c> and <c>direction</c> ask for (UAX #9 X1-X8).
    /// </summary>
    private static (String Open, String Close) Controls(ComputedStyle style)
    {
        var rightToLeft = style.Direction == TextDirection.RightToLeft;

        return style.UnicodeBidiKeyword?.Trim().ToLowerInvariant() switch
        {
            "isolate-override" => (
                Upright(rightToLeft ? 0x2067 : 0x2066) + Upright(rightToLeft ? 0x202E : 0x202D),
                Upright(0x202C) + Upright(0x2069)),
            "isolate" => (
                Upright(rightToLeft ? 0x2067 : 0x2066),
                Upright(0x2069)),
            "bidi-override" => (
                Upright(rightToLeft ? 0x202E : 0x202D),
                Upright(0x202C)),
            _ => (
                Upright(rightToLeft ? 0x202B : 0x202A),
                Upright(0x202C)),
        };
    }

    private static String Upright(Int32 codePoint) => Char.ConvertFromUtf32(codePoint);

    /// <summary>
    /// Gets the leading every line would have cut off one of its edges, zero where the block trims no
    /// edge or the line carries nothing to measure a text edge on. A column asks for these per line:
    /// the trim reaches the first and the last line of every column, not only the block's own ends.
    /// </summary>
    private List<Double> Leadings(Box container, List<Line> lines, Boolean atStart)
    {
        var trims = new List<Double>(lines.Count);
        var trim = container.Style.TextBoxTrim;
        var applies = atStart
            ? trim is TextBoxTrim.Start or TextBoxTrim.Both
            : trim is TextBoxTrim.End or TextBoxTrim.Both;
        var edge = atStart ? container.Style.TextBoxEdgeStart : container.Style.TextBoxEdgeEnd;

        foreach (var line in lines)
        {
            trims.Add(applies && line.Fragments.Count > 0 ? Leading(line, edge, atStart) : 0);
        }

        return trims;
    }

    /// <summary>
    /// Gets the leading the block cuts away at its start and its end: what stands between the line's box
    /// edge and the line its <c>text-box-edge</c> names, for the ends the trim reaches.
    /// </summary>
    private (Double Start, Double End) Trim(Box container, List<Line> lines)
    {
        var trim = container.Style.TextBoxTrim;

        if (trim == TextBoxTrim.None)
        {
            return (0, 0);
        }

        var (first, last) = ContentEnds(lines);
        var start = first is not null && trim is TextBoxTrim.Start or TextBoxTrim.Both
            ? Leading(first, container.Style.TextBoxEdgeStart, atStart: true)
            : 0;
        var end = last is not null && trim is TextBoxTrim.End or TextBoxTrim.Both
            ? Leading(last, container.Style.TextBoxEdgeEnd, atStart: false)
            : 0;

        return (start, end);
    }

    /// <summary>Gets the first and the last line of the block that carry anything on them.</summary>
    private static (Line? First, Line? Last) ContentEnds(List<Line> lines)
    {
        Line? first = null;
        Line? last = null;

        foreach (var line in lines)
        {
            if (line.Fragments.Count == 0)
            {
                continue;
            }

            first ??= line;
            last = line;
        }

        return (first, last);
    }

    /// <summary>
    /// Gets the distance from the line box's edge to the line its <c>text-box-edge</c> names: the text
    /// box, a capital's top, the x-height, or the alphabetic baseline itself.
    /// </summary>
    private Double Leading(Line line, TextBoxEdge edge, Boolean atStart)
    {
        var fragment = atStart ? line.Fragments[0] : line.Fragments[^1];
        var metrics = _measurer.GetRunMetrics(fragment.Text, fragment.Style.GetFont());

        // What is trimmed is everything between the line box and the edge asked for: the room the line
        // takes beyond the text - the block's strut, or a run set taller than this one - and the part of
        // the text box the edge leaves. The room is read off the line's own sides rather than halved,
        // because a strut and a borrowed face need not stand the same distance above and below.
        var outside = atStart
            ? line.Ascent - metrics.Ascent
            : line.Height - line.Ascent - metrics.Descent;
        var inside = atStart
            ? metrics.Ascent - Edge(metrics, edge, atStart: true)
            : metrics.Descent - Edge(metrics, edge, atStart: false);

        return Math.Max(0, outside) + Math.Max(0, inside);
    }

    private static Double Edge(FontMetrics metrics, TextBoxEdge edge, Boolean atStart) => edge switch
    {
        TextBoxEdge.Cap => metrics.CapHeight,
        TextBoxEdge.Ex => metrics.XHeight,
        TextBoxEdge.Alphabetic => 0,
        TextBoxEdge.Ideographic or TextBoxEdge.IdeographicInk => atStart ? metrics.Ascent : metrics.Descent,
        _ => atStart ? metrics.Ascent : metrics.Descent,
    };

    /// <summary>
    /// Opens the box an inline element draws through on this line.
    /// </summary>
    /// <remarks>
    /// The band is the element's font content area, which is what makes it taller than the glyphs
    /// without being as tall as the line: inline padding and border overflow the line rather than
    /// widening it, so nothing here can move the lines below. An element the line is cut through draws
    /// neither the padding, the border nor the curve of the edge it was cut at.
    /// </remarks>
    private static void OpenBox(OpenInline item, List<OpenInline> open, LayoutBox lineBox, WritingModeInfo flow, Line line)
    {
        var ancestor = item.Ancestor;
        var clones = ancestor.Box.Style.ClonesDecoration;
        var cutStart = !clones && line.ContinuesFromPrevious?.Contains(ancestor) == true;
        var cutEnd = !clones && line.ContinuesToNext?.Contains(ancestor) == true;

        item.Node = new LayoutBox(ancestor.Box, LayoutKind.Inline)
        {
            Flow = flow,
            Margin = ancestor.NoteContent ? ancestor.Margin : default,
            Padding = ancestor.PaddingFor(cutStart, cutEnd),
            Border = ancestor.BorderFor(cutStart, cutEnd),
            SlicesInlineStart = cutStart,
            SlicesInlineEnd = cutEnd,

            // The painting walks the box tree rather than re-reading styles, and an inline box is built
            // here rather than arranged, so this is where it says whether it forms a context of its own -
            // which decides whether the levels written inside it are answered by the context above.
            EstablishesStackingContext = ancestor.Box.Style.EstablishesStackingContext,
            Opacity = ancestor.Box.Style.Opacity,
        };

        // A relatively placed inline keeps the place the line gave it and is drawn where its offset puts
        // it, which is what the pass that closes the box does. It carries the same answer as a block does,
        // because the layer it belongs to comes from the position and the level rather than from the kind
        // of box: an inline that states a level under `relative` is painted with the levels around it, not
        // with the lines the text stands on (CSS 2.1 §Appendix E). Only `relative` is written here: a box
        // that leaves the flow altogether is gathered by the positioning pass rather than by the line.
        var stated = ancestor.Box.Style.Position;

        if (stated.Position == PositionMode.Relative)
        {
            item.Node.Position = PositionMode.Relative;
            item.Node.ZIndex = stated.HasZIndex ? stated.ZIndex : 0;
        }

        // Everything opened before this one on the line encloses it, so the box just inside it - or the
        // line itself when this is the outermost - is where its box belongs.
        var index = open.IndexOf(item);
        var owner = index > 0 ? open[index - 1].Node! : lineBox;
        owner.Children.Add(item.Node);
    }

    /// <summary>
    /// Closes the box an inline element drew through on this line, spanning it from where the element
    /// began to where it ends.
    /// </summary>
    private static void CloseBox(
        OpenInline item,
        Double end,
        Rect contentBox,
        WritingModeInfo flow,
        Line line,
        Double cursor,
        InlineContext context,
        Placement placement)
    {
        var ancestor = item.Ancestor;

        // The band sits astride the line's shared baseline, offset by the element's own vertical
        // alignment, so a run that is raised or lowered takes the band around it with it while the
        // line's baseline stays where the rest of the line put it.
        var shift = line.BandShifts is { } bands && bands.TryGetValue(ancestor, out var aligned)
            ? aligned
            : 0;

        // The band an element draws is as tall as the font it is set in, so on a line the fitting scaled
        // it goes with the text - while the padding, border and margin around it are lengths the page
        // wrote, and those stand at the size they were written at (CSS Text 5 §1.1).
        var ascent = ancestor.Ascent * line.Scale;
        var descent = ancestor.Descent * line.Scale;
        var blockOffset = cursor + line.Ascent + shift - ascent - ancestor.BlockStartInset;
        var blockSize = ascent + descent + ancestor.BlockStartInset + ancestor.BlockEndInset;

        item.Node!.BorderBox = flow.Map(contentBox, item.Start, end - item.Start, blockOffset, blockSize);

        // Where this fragment stands is what the boxes flown out of the element will be anchored to, so
        // the fragment is written up before anything moves it (CSS Positioned Layout 3 2.1).
        placement.Fragment(ancestor, item.Node);

        // A relative element keeps the place it had in the flow - the lines and the boxes around it are
        // measured as though it never moved - and is drawn where its offset puts it. Its fragments are
        // made one per line here rather than by the pass that arranges every other kind of box, so this
        // is where each of them is moved, and what is drawn through the fragment goes with it: the text,
        // the line under it, whatever background the element asks for (CSS 2.1 §9.4.3).
        //
        // The move itself waits for the boxes held back under the element, because those are arranged
        // against the place the element had and then carried along by it - which is what an offset on an
        // ancestor does to everything inside it.
        var position = ancestor.Box.Style.Position;

        if (position.Position == PositionMode.Relative)
        {
            placement.Shift(item.Node, PositionedLayout.RelativeOffset(position, context.Containing.ContainingBlock, item.Node.Flow));
        }
    }

    /// <summary>Builds the node a run is drawn through and puts it in the innermost box open around it.</summary>
    private void Place(
        TextFragment fragment,
        List<OpenInline> open,
        LayoutBox lineBox,
        Rect contentBox,
        WritingModeInfo flow,
        Line line,
        Double origin,
        Double cursor,
        InlineContext context,
        Double room,
        Placement placement,
        ITextMeasurer measurer)
    {
        if (fragment.OutOfFlow)
        {
            // A box that left the flow is placed against its containing block rather than drawn as part of
            // the line. Where it stood on the line is its static position: the place it would have had had
            // it stayed in flow, which is what an offset left unset falls back to. The line only says
            // where that was - the box's own geometry, and whatever offsets it declares, are the
            // positioning pass's business.
            var stood = flow.Map(contentBox, origin, 0, cursor, 0);
            var against = context.Absolute ?? context.Containing;

            // Where the box hangs from is the line's own business as well: an inline-level box grows
            // from the insertion point along the way the line reads, while a block-level one keeps the
            // content's start edge whatever the line's alignment did - measured in Chrome, where
            // `text-align: center` on a 100px container puts a 20px `display: inline` absolute box at
            // 50 but a `display: block` one at 0, and a right-to-left line hangs the inline-level box
            // leftwards from its insertion point. A column's insertion point runs down the page and
            // this anchor is written along x, so only a row is answered here.
            StaticLine? hang = null;

            if (!flow.IsVertical)
            {
                var at = fragment.Box.Style.Display.StartsWith("inline", StringComparison.OrdinalIgnoreCase)
                    ? stood.X
                    : flow.InlineSign > 0 ? contentBox.X : contentBox.Right;

                hang = new StaticLine(at, at, 0, flow.InlineSign);
            }

            // An inline element the run sits inside answers for the box when it is the nearest one
            // positioned - which the line cannot settle on its own, since the rectangle is drawn over
            // every fragment of that element (CSS Positioned Layout 3 2.1).
            var anchor = AnchoredBy(fragment.Inline);

            if (anchor is null)
            {
                Attach(open, lineBox, context.Engine.ArrangePositioned(
                    fragment.Box,
                    new Offset(stood.X, stood.Y),
                    against,
                    line: hang,
                    staticFromHigh: flow.IsVertical && flow.BlockSign < 0,
                    staticFromInline: flow.IsVertical && flow.IsRightToLeft));
            }
            else
            {
                var (owner, index) = Standing(open, lineBox);
                placement.Hold(fragment.Box, anchor, owner, index, against, new Offset(stood.X, stood.Y), hang);
            }

            return;
        }

        if (fragment.Note is { } note)
        {
            PlaceNote(
                fragment,
                note,
                open,
                lineBox,
                contentBox,
                flow,
                line,
                origin,
                cursor,
                context,
                placement);

            return;
        }

        if (fragment.Atomic is { } atomic)
        {
            // The box is arranged as the block container it is - it brings its own geometry, radii and
            // children with it - and the line only decides where it goes: the margins along the line
            // are taken out of the room the line reserved, and the box's own baseline is put on the
            // line's, which is what makes its text sit with the text around it.
            var frame = flow.Map(
                contentBox,
                origin + atomic.InlineStartMargin,
                atomic.InlineSize - atomic.InlineStartMargin - atomic.InlineEndMargin,
                cursor + line.Ascent + fragment.BaselineShift - atomic.Ascent,
                atomic.BlockSize);

            // Its own content is under the same inline element, so whatever leaves the flow inside the
            // atomic box anchors to that element too - which waits for the element to be whole.
            var anchor = AnchoredBy(fragment.Inline);

            if (anchor is null)
            {
                Attach(open, lineBox, context.Engine.Arrange(fragment.Box, frame, context.Atomic));
            }
            else
            {
                var (owner, index) = Standing(open, lineBox);
                placement.Hold(fragment.Box, anchor, owner, index, context.Atomic, frame);
            }

            return;
        }

        if (fragment.IsReplaced)
        {
            // The bottom margin edge of a replaced inline rests on the baseline, so its box is lifted
            // by its own height rather than filling the line box. The band of the element around it is
            // still the font's content area, so it does not grow to cover an image taller than its text.
            Attach(open, lineBox, new LayoutBox(fragment.Box, LayoutKind.Image)
            {
                Font = FittedFont(line, fragment.Style),
                FitScale = line.Scale,
                Baseline = flow.BlockOffsetFromLeft(line.Height, line.Ascent + fragment.BaselineShift),
                Flow = fragment.Flow,
                ImageUrl = fragment.Box.SourceUrl,
                ImageFit = fragment.Style.ObjectFit,
                BorderBox = flow.Map(
                    contentBox,
                    origin,
                    fragment.Width,
                    cursor + line.Ascent - fragment.ContentHeight,
                    fragment.ContentHeight),
            });

            return;
        }

        if (fragment.Ruby is { } annotation)
        {
            // An annotation does not make the line taller: the line box is the base's, exactly as if
            // the pair were a plain inline and the reading were not there (CSS Ruby 1 §3.6). The
            // reading is laid over the base's content edge, which the shared baseline fixes, and it
            // only grows the line when it reaches past the leading the line already has (Build).
            var metrics = measurer.GetMetrics(FittedFont(line, fragment.Style));
            // The turned base is centred across the line by the drawing, so the reading that stands off
            // its edge is stood off the same middle - see `Marks` for the same exchange.
            var turned = fragment.Flow.IsVertical && !fragment.Flow.IsSideways &&
                fragment.Flow.Orientation == TextOrientation.Sideways;
            var baseline = (turned
                ? (line.Height + metrics.Ascent - metrics.Descent) / 2
                : line.Ascent) + fragment.BaselineShift;

            // The base keeps the band its own line-height asks for rather than the whole line box: a
            // line grown by the reading - or by a taller neighbour - leaves the base in the band around
            // the shared baseline, which is where its own leading puts it and where the reading, standing
            // just past its content edge, no longer covers it.
            var band = Math.Min(line.Height, LineBoxHeight(measurer, fragment.Style, FittedFont(line, fragment.Style)));
            var bandTop = baseline - metrics.Ascent - ((band - metrics.Ascent - metrics.Descent) / 2);

            // A reading that hung over the text beside its pair pushed the pair along to make room for
            // itself; otherwise the base keeps the middle of the room the two halves share.
            var standing = fragment.Hang ?? RubyAligned(
                fragment.Box.Style.RubyAlign,
                fragment.Width,
                fragment.BaseWidth);

            Attach(open, lineBox, new LayoutBox(fragment.Box, LayoutKind.Text)
            {
                Text = fragment.Text,
                Font = FittedFont(line, fragment.Style),
                FitScale = line.Scale,
                Baseline = flow.BlockOffsetFromLeft(band, baseline - bandTop),
                Flow = fragment.Flow,
                BorderBox = flow.Map(
                    contentBox,
                    origin + standing,
                    fragment.BaseWidth,
                    cursor + bandTop,
                    band),
            });

            Attach(open, lineBox, AnnotationBox(
                annotation,
                contentBox,
                flow,
                origin + standing,
                fragment.BaseWidth,
                cursor + (annotation.Under
                    ? baseline + metrics.Descent
                    : baseline - metrics.Ascent - annotation.Block),
                fragment.Box.Style.RubyAlign));

            return;
        }

        var text = fragment.Hanging > 0
            ? fragment.Source.Substring(fragment.Start, fragment.Length - fragment.Hanging)
            : fragment.Text;

        // The shaper reads a run's direction off the first character that names a script, so a run the
        // bidi algorithm set at a level the characters do not agree with - a Latin page turned around
        // by an override, or an override that keeps a right-to-left word left to right - is drawn with
        // its glyphs mirrored across the run's own room.
        var backwards = fragment.RightToLeft is { } rightToLeft
            ? rightToLeft != measurer.ReadsRightToLeft(text)
            : (Boolean?)null;

        var run = new LayoutBox(fragment.Box, LayoutKind.Text)
        {
            Text = text,
            Trims = fragment.Trims is { } trims && text.Length <= trims.Length ? trims[..text.Length] : null,
            AutoSpaces = fragment.AutoSpaces is { } spaces && text.Length <= spaces.Length
                ? spaces[..text.Length]
                : null,
            AutoSpaceRoom = fragment.AutoSpaceRoom,
            Font = FittedFont(line, fragment.Style),
            FitScale = line.Scale,
            Baseline = flow.BlockOffsetFromLeft(line.Height, line.Ascent + fragment.BaselineShift),
            Flow = fragment.Flow,
            HangSide = fragment.HangSide,
            Justification = fragment.Justify,
            Justifies = fragment.Expansions,
            Reversed = backwards,
            BorderBox = flow.Map(contentBox, origin, room, cursor, line.Height),
        };

        run.Marks = Marks(
            fragment, run.Text!, flow, contentBox, origin, room, cursor, line, measurer);

        Attach(open, lineBox, run);
    }

    /// <summary>Places one two-line inline note fragment and its two ordinary text lines.</summary>
    private void PlaceNote(
        TextFragment fragment,
        NoteRun note,
        List<OpenInline> open,
        LayoutBox lineBox,
        Rect contentBox,
        WritingModeInfo flow,
        Line line,
        Double origin,
        Double cursor,
        InlineContext context,
        Placement placement)
    {
        var (marginStart, _) = flow.InlineMargins(note.Margin);
        var (paddingStart, paddingEnd) = flow.InlineMargins(note.Padding);
        var (borderStart, borderEnd) = flow.InlineMargins(note.Border);
        var (blockMarginStart, blockMarginEnd) = flow.BlockMargins(note.Margin);
        var (blockPaddingStart, blockPaddingEnd) = flow.BlockMargins(note.Padding);
        var (blockBorderStart, blockBorderEnd) = flow.BlockMargins(note.Border);
        var contentInline = Math.Max(note.FirstWidth, note.SecondWidth);
        var borderInline = contentInline + paddingStart + paddingEnd + borderStart + borderEnd;
        var firstBlock = note.First?.Height ?? note.EmptyLineHeight;
        var secondBlock = note.Second?.Height ?? note.EmptyLineHeight;
        var borderBlock = firstBlock + secondBlock + blockPaddingStart + blockPaddingEnd +
            blockBorderStart + blockBorderEnd;
        var marginBoxBlock = borderBlock + blockMarginStart + blockMarginEnd;
        var marginBoxTop = cursor + ((line.Height - marginBoxBlock) / 2);

        var outer = new LayoutBox(fragment.Box, LayoutKind.Inline)
        {
            Margin = note.Margin,
            Padding = note.Padding,
            Border = note.Border,
            Flow = flow,
            BorderBox = flow.Map(
                contentBox,
                origin + marginStart,
                borderInline,
                marginBoxTop + blockMarginStart,
                borderBlock),
        };

        Attach(open, lineBox, outer);

        void PlaceRow(Line? row, Double rowOffset)
        {
            if (row is null)
            {
                return;
            }

            PlaceLines(
                fragment.Box,
                outer,
                [row],
                0,
                1,
                outer.ContentBox,
                note.Flow,
                context,
                rowOffset,
                placement);
        }

        PlaceRow(note.First, 0);
        PlaceRow(note.Second, firstBlock);
    }

    /// <summary>
    /// The marks a run of text is drawn with: one over each character the page asked to have marked,
    /// centred on that character within the room the run was given - so a line that was spread carries its
    /// marks with the spread - and set against the edge of the line its position named. Nothing here
    /// changes a size: the line keeps the height it was laid out with, which is what makes a mark paint
    /// rather than content (CSS Text Decoration 4 §5.2).
    /// </summary>
    private static List<EmphasisMark>? Marks(
        TextFragment fragment,
        String text,
        WritingModeInfo flow,
        Rect contentBox,
        Double origin,
        Double room,
        Double cursor,
        Line line,
        ITextMeasurer measurer)
    {
        var emphasis = fragment.Style.Emphasis;

        // A base carrying its own reading is not marked as well - the reading stands where the mark
        // would - and a run with no room along the line has no character to mark.
        if (!emphasis.IsSet || fragment.Ruby is not null || room <= 0 || text.Length == 0)
        {
            return null;
        }

        var font = fragment.Style.GetFont();

        // A mark is drawn at half the size of the text it marks, which is the size a reading of that base
        // comes to.
        var drawn = new FontStyle(font.Family, Math.Max(1, font.Size / 2), font.Weight, font.IsItalic);
        var metrics = measurer.GetMetrics(drawn);

        // A mark is set solid in a strip its own size: the glyph is lifted to the middle of that strip,
        // which is how a reading of the same size stands (CSS Ruby 1 §3.2 and the `rt` rule the
        // rendering requirements give it), and it is why a mark neither hangs below the line's headroom
        // nor reaches into the line above.
        var block = drawn.Size;
        var ascent = metrics.Ascent + ((block - (metrics.Ascent + metrics.Descent)) / 2);

        if (block <= 0)
        {
            return null;
        }

        var width = measurer.MeasureAdvance(emphasis.Mark!, drawn, flow);
        var spacing = fragment.Style.LetterSpacing;
        var words = fragment.Style.WordSpacing;
        var face = measurer.GetMetrics(font);
        // A turned run stands with its em box centred across the line it fills, and the drawing places
        // its baseline from that middle rather than from the ascent the line was measured with - which
        // a taller neighbour, or a face resolved for the glyphs rather than the one the page named,
        // moves. The marks stand off the text's own edge, so they are stood off the same middle, or a
        // mark drifts away from the letters it marks (CSS Text Decoration 4 §5.2 with Writing Modes 3
        // §5.2, which is what makes the two mirrored vertical modes read the same side of the text).
        var turned = fragment.Flow.IsVertical && !fragment.Flow.IsSideways &&
            fragment.Flow.Orientation == TextOrientation.Sideways;
        var baseline = (turned
            ? (line.Height + face.Ascent - face.Descent) / 2
            : line.Ascent) + fragment.BaselineShift;

        // A mark stands just outside the text it marks - above the content edge for a mark on the
        // ascent side, below it for one on the descent side - and takes no room in the line: it may
        // reach into the line above or below, which is the author's business to leave room for
        // (CSS Text Decoration 3 §3.3, measured against Chrome).
        var start = cursor + (emphasis.KeepsItsStripOnTheAscentSide(fragment.Flow)
            ? baseline - face.Ascent - block
            : baseline + face.Descent);
        var colour = emphasis.HasColor ? emphasis.Color : ComputedStyle.ToArgb(fragment.Style.Color);
        var elements = new List<Int32>();
        var advances = new List<Double>();
        var total = 0.0;

        for (var run = StringInfo.GetTextElementEnumerator(text); run.MoveNext(); )
        {
            var element = (String)run.Current!;
            var advance = measurer.MeasureAdvance(element, font, flow);

            // The room between one character and the next is the spacing the page wrote after it, which
            // carries the marks along with the letters - and the space a word ends on is a word space too.
            advance += spacing + (Char.IsWhiteSpace(element[0]) ? words : 0);

            // A character left unmarked still takes its room on the line, so what it leaves behind is its
            // place in the walk: the marks after it stand over the characters they mark, not where a line
            // without the character would have put them.
            advances.Add(advance);
            total += advance;

            if (!emphasis.Skips(element))
            {
                elements.Add(advances.Count - 1);
            }
        }

        if (elements.Count == 0)
        {
            return null;
        }

        // The run was measured as a whole and the line may have been spread since, so the marks follow the
        // spread rather than the plain advances. A backwards run starts at the far end of its room.
        var share = total > 0 ? room / total : 0;
        var backwards = fragment.RightToLeft ?? flow.IsRightToLeft;
        var marks = new List<EmphasisMark>(elements.Count);
        var walk = 0.0;
        var wanted = 0;

        // The walk goes over every character of the run, not only the marked ones, and charges each what
        // it takes: a stop, a bracket or a space asks for no mark of its own, but it stands in the way of
        // the letters after it, and a mark set at the count of the marked characters alone would sit that
        // many characters too far back (CSS Text Decoration 4 §5.4).
        for (var position = 0; position < advances.Count; position++)
        {
            if (wanted < elements.Count && elements[wanted] == position)
            {
                var centre = walk + (advances[position] / 2);

                marks.Add(new EmphasisMark(
                    flow.Map(
                        contentBox,
                        backwards
                            ? origin + room - (centre * share) - (width / 2)
                            : origin + (centre * share) - (width / 2),
                        width,
                        start,
                        block),
                    emphasis.Mark!,
                    flow.BlockOffsetFromLeft(block, ascent),
                    drawn,
                    colour));

                wanted++;
            }

            walk += advances[position];
        }

        return marks;
    }

    /// <summary>Adds a node inside the innermost inline element still open, or inside the line itself.</summary>
    private static void Attach(List<OpenInline> open, LayoutBox lineBox, LayoutBox node) =>
        (open.Count > 0 ? open[^1].Node! : lineBox).Children.Add(node);

    /// <summary>
    /// Gets the node a run's box belongs inside, and how many boxes already stood in it when the run came
    /// to be placed - which is where a box held back to the end of the container goes again, so that the
    /// content of the line reads in the order the line was built in.
    /// </summary>
    private static (LayoutBox Owner, Int32 Index) Standing(List<OpenInline> open, LayoutBox lineBox)
    {
        var owner = open.Count > 0 ? open[^1].Node! : lineBox;

        return (owner, owner.Children.Count);
    }

    /// <summary>
    /// Gets the innermost inline element a run sits inside that lends its descendants a frame, or null
    /// when none of them does: the nearest ancestor whose <c>position</c> is anything but static
    /// (CSS 2.1 10.3.7), or the nearest carrying a filter (CSS Filter Effects 1 2.4) - and an inline
    /// element is one of those whatever its display.
    /// </summary>
    private static InlineAncestor? AnchoredBy(InlineAncestor? chain)
    {
        for (var ancestor = chain; ancestor is not null; ancestor = ancestor.Parent)
        {
            if (PositionedLayout.LendsAFrame(ancestor.Box, inlineLevel: true))
            {
                return ancestor;
            }
        }

        return null;
    }

    /// <summary>
    /// Builds the box that draws a ruby's annotation beside its base, centred within the room the pair
    /// takes along the inline axis and placed against the block-start edge of the line.
    /// </summary>
    private static LayoutBox AnnotationBox(
        RubyAnnotation annotation,
        Rect contentBox,
        WritingModeInfo flow,
        Double origin,
        Double room,
        Double cursor,
        RubyAlign align)
    {
        return new LayoutBox(annotation.Box, LayoutKind.Text)
        {
            Text = annotation.Text,
            Font = annotation.Style.GetFont(),
            Flow = annotation.Flow,
            IsRubyAnnotation = true,
            Baseline = flow.BlockOffsetFromLeft(annotation.Block, annotation.Ascent),
            BorderBox = flow.Map(
                contentBox,
                origin + RubyAligned(align, room, annotation.Width),
                annotation.Width,
                cursor,
                annotation.Block),
        };
    }

    /// <summary>
    /// Flattens the container's inline content into the stream of runs and breaks the lines are built
    /// from, remembering for each run the chain of inline elements it sits inside.
    /// </summary>
    /// <param name="chain">The elements the content is already inside, innermost first.</param>
    /// <param name="basis">
    /// The inline extent a percentage among an element's padding and border resolves against. There is
    /// no definite line yet, so the extent the content is being fitted into stands in for one.
    /// </param>
    private void Collect(Box container, List<InlineItem> items, WritingModeInfo flow, InlineAncestor? chain, Double basis)
    {
        foreach (var child in container.Children)
        {
            // A note's placement owns the element's participation in flow before position and float
            // are considered: those properties are ignored on the defining element itself.
            if (child.Kind == BoxKind.Inline &&
                child.Style.NotePlacement == NotePlacement.Inline &&
                !InsideRubyText(chain))
            {
                var note = NoteSource.Read(child, hoistInterline: true, hoistMargin: true);

                if (note.Text.Length > 0 && note.HasInk)
                {
                    items.Add(InlineItem.CreateNote(note, child, chain));
                }
                else
                {
                    foreach (var nested in note.NestedInterline)
                    {
                        items.Add(InlineItem.CreateInterlineNote(
                            nested.Note.Content,
                            nested.Note.Box,
                            chain));
                    }

                    foreach (var nested in note.NestedMargin)
                    {
                        items.Add(InlineItem.CreateMarginNote(nested.Note.Box, chain));
                    }
                }

                continue;
            }

            if (child.Kind == BoxKind.Inline &&
                child.Style.NotePlacement == NotePlacement.Interline &&
                !InsideRubyText(chain))
            {
                var note = NoteSource.Read(child, hoistMargin: true);

                if (note.Text.Length > 0 && note.HasInk)
                {
                    items.Add(InlineItem.CreateInterlineNote(note, child, chain));
                }

                continue;
            }

            if (child.Kind == BoxKind.Inline &&
                child.Style.NotePlacement == NotePlacement.Margin &&
                !InsideRubyText(chain))
            {
                var note = NoteSource.Read(child);

                if (note.Text.Length > 0 && note.HasInk)
                {
                    items.Add(InlineItem.CreateMarginNote(child, chain));
                }

                continue;
            }

            // A box that left the flow is not part of the line: it is placed against a containing block of
            // its own. It is kept as an item of no extent so the place it would have had is still known -
            // that place is the static position an offset left unset falls back to - while the line it
            // sits on is neither wider nor taller for it.
            if (PositionedLayout.IsOutOfFlow(child))
            {
                items.Add(InlineItem.CreateOutOfFlow(child, chain));
                continue;
            }

            switch (child.Kind)
            {
                case BoxKind.Text:
                    if (!String.IsNullOrEmpty(child.Text))
                    {
                        AddText(child.Text, child.Style, child, items, chain);
                    }

                    break;

                case BoxKind.LineBreak:
                    items.Add(InlineItem.CreateBreak(child.Style, child, chain));
                    break;

                // A replaced inline box is atomic: unlike other inline boxes it is not flattened into
                // its text runs, it occupies its own used size on the line.
                case BoxKind.Inline when child.IsReplaced:
                    items.Add(InlineItem.CreateReplaced(child, chain));
                    break;

                // An inline-block is atomic: it takes part in the line as one piece of the size its own
                // content asks for, and is laid out as the block container it is rather than being
                // broken into the runs that would otherwise carry the line.
                case BoxKind.InlineBlock:
                    items.Add(InlineItem.CreateAtomic(child, chain));
                    break;

                // An inline-flex, inline-grid or inline-table box is atomic on the same terms: its
                // outer role is inline, so the line carries it whole, while its inner role is the
                // container its kind names and its own context lays the content out that way. Only the
                // inline spellings reach here - a block-level container is not inline content, and its
                // box never stands among the runs a line is built from.
                case BoxKind.Flex:
                case BoxKind.Grid:
                case BoxKind.Masonry:
                case BoxKind.Table:
                    if (child.IsInlineOuter)
                    {
                        items.Add(InlineItem.CreateAtomic(child, chain));
                    }

                    break;

                // A ruby pairs its base text with the annotation that goes over it, so the two are
                // gathered into one item instead of being run together as ordinary text. One that
                // holds nothing still stands on the line for the edges its box carries along it.
                case BoxKind.Ruby:
                {
                    var before = items.Count;
                    AddRuby(child, items, chain);

                    if (items.Count == before)
                    {
                        var ancestor = new InlineAncestor(child, chain, basis, flow, _measurer);

                        if (CarriesAnInlineEdge(ancestor))
                        {
                            items.Add(InlineItem.CreateEmpty(child, ancestor));
                        }
                    }

                    break;
                }

                // An ordinary inline element keeps its own box. It is not flattened away: the runs inside
                // it go on carrying it, so the line can give it back the box that draws it.
                case BoxKind.Inline:
                {
                    var ancestor = new InlineAncestor(child, chain, basis, flow, _measurer);
                    var before = items.Count;

                    Collect(child, items, flow, ancestor, basis);

                    // A box with nothing inside it still stands on the line for the margins, borders
                    // and padding it carries along that axis, and draws its box there. One that
                    // carries none of them draws nothing at all, and would be a line of its own.
                    if (items.Count == before && CarriesAnInlineEdge(ancestor))
                    {
                        items.Add(InlineItem.CreateEmpty(child, ancestor));
                    }

                    break;
                }

                // A base or a reading is a run like any other; only the pair around them is set apart.
                case BoxKind.RubyBase:
                case BoxKind.RubyText:
                    Collect(child, items, flow, chain, basis);
                    break;
            }
        }
    }

    /// <summary>
    /// Gets whether an inline element carries a margin, border or padding along the axis a line runs,
    /// which is what the line keeps room for even when the element holds nothing.
    /// </summary>
    private static Boolean CarriesAnInlineEdge(InlineAncestor? ancestor) =>
        ancestor is not null &&
        (ancestor.InlineStartInset > 0 ||
            ancestor.InlineEndInset > 0 ||
            ancestor.InlineStartMargin != 0 ||
            ancestor.InlineEndMargin != 0);

    /// <summary>
    /// Gets whether the element a run is written in begins at that run: the line has not opened it
    /// yet. An element that does not wrap its text is one unit the line moves whole, so a run that
    /// opens one may be handed back to the opportunity before it even though nothing inside it may
    /// be broken (CSS Text 3 §5.1, §5.2).
    /// </summary>
    private static Boolean BeginsHere(LineBuilder builder, InlineAncestor? inline) =>
        inline is not null && !builder.IsOpen(inline);

    /// <summary>
    /// Gets whether an item sits inside an element, at any depth - the test that says the steps
    /// taken since a break opportunity belong to the box that now fails to fit.
    /// </summary>
    private static Boolean SitsWithin(InlineAncestor? chain, InlineAncestor? unit)
    {
        if (unit is null)
        {
            return false;
        }

        for (var at = chain; at is not null; at = at.Parent)
        {
            if (ReferenceEquals(at, unit))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Turns a <c>ruby</c> element into one item carrying its base text and the annotation beside it.
    /// </summary>
    /// <remarks>
    /// A base is whatever is not an annotation: an explicit <c>rb</c>, the element's own text, or any
    /// other inline content. Several bases under one annotation are read as a single base, which is
    /// what all but the longest ruby passage comes to.
    /// </remarks>
    private static void AddRuby(Box ruby, List<InlineItem> items, InlineAncestor? chain)
    {
        // A ruby is a sequence of pairs: each reading is set over the base it follows, and it is that
        // base alone the reading is centred on - which is what puts a mark above its own character rather
        // than spread out along the whole run (CSS Ruby 1 §3.1).
        var bases = new List<String>();
        var readings = new List<(String Text, ComputedStyle Style, Box Box)>();

        var baseText = new System.Text.StringBuilder();
        var readingText = new System.Text.StringBuilder();
        var readingStyle = ruby.Style;
        Box? readingBox = null;

        void CloseReading()
        {
            if (readingText.Length > 0)
            {
                readings.Add((readingText.ToString(), readingStyle, readingBox ?? ruby));
            }

            readingText = new System.Text.StringBuilder();
            readingBox = null;
            readingStyle = ruby.Style;
        }

        foreach (var child in ruby.Children)
        {
            if (child.Kind == BoxKind.RubyText)
            {
                // The base of the pair ends where its reading begins.
                if (baseText.Length > 0)
                {
                    bases.Add(baseText.ToString());
                    baseText = new System.Text.StringBuilder();
                }

                Flatten(child, readingText);

                // The first element of a reading is the one it is set in; a second written beside it
                // joins the text of the same pair, as it always has.
                readingBox ??= child;
                readingStyle = child.Style;
                continue;
            }

            // A base begins where the reading of the pair before it ends.
            CloseReading();
            Flatten(child, baseText);
        }

        CloseReading();

        if (baseText.Length > 0)
        {
            bases.Add(baseText.ToString());
        }

        if (bases.Count == 0)
        {
            // A ruby with nothing to annotate has nothing to contribute.
            return;
        }

        if (readings.Count == 0)
        {
            // Without an annotation there is nothing to set the base apart from ordinary text.
            items.Add(InlineItem.CreateText(String.Concat(bases), ruby.Style, ruby, chain));
            return;
        }

        // Each base carrying the reading that follows it, and at most one base at the end carrying none -
        // a reading with no base before it, or anything else that will not pair off, is set as one
        // passage over the whole ruby, which is what all but the shortest ruby passage comes to.
        var off = bases.Count - readings.Count;

        if (off == 0 || (off == 1 && bases[^1].Length > 0))
        {
            for (var index = 0; index < readings.Count; index++)
            {
                var (text, style, box) = readings[index];

                items.Add(InlineItem.CreateRuby(
                    bases[index],
                    ruby.Style,
                    ruby,
                    text,
                    style,
                    box,
                    chain));
            }

            if (off == 1)
            {
                items.Add(InlineItem.CreateText(bases[^1], ruby.Style, ruby, chain));
            }

            return;
        }

        items.Add(InlineItem.CreateRuby(
            String.Concat(bases),
            ruby.Style,
            ruby,
            String.Concat(readings.ConvertAll(reading => reading.Text)),
            readings[0].Style,
            readings[0].Box,
            chain));
    }

    /// <summary>Appends every character of text held anywhere under a box, in document order.</summary>
    private static void Flatten(Box box, System.Text.StringBuilder text)
    {
        if (box.Kind == BoxKind.Text)
        {
            text.Append(box.Text);
            return;
        }

        foreach (var child in box.Children)
        {
            Flatten(child, text);
        }
    }

    /// <summary>Gets whether a run sits inside the reading half of a ruby pair, where notes do not apply.</summary>
    private static Boolean InsideRubyText(InlineAncestor? chain)
    {
        for (var ancestor = chain; ancestor is not null; ancestor = ancestor.Parent)
        {
            if (ancestor.Box.Element?.LocalName is "rt" or "rtc")
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Takes the physical edges one fragment of a sliced inline box carries: inline-start only on the
    /// first fragment, inline-end only on the last, and both block edges everywhere between.
    /// </summary>
    private static Thickness SliceEdges(WritingModeInfo flow, Thickness edges, Boolean first, Boolean last)
    {
        var top = edges.Top;
        var right = edges.Right;
        var bottom = edges.Bottom;
        var left = edges.Left;

        if (!flow.IsVertical)
        {
            if (flow.InlineSign > 0)
            {
                if (!first)
                {
                    left = 0;
                }

                if (!last)
                {
                    right = 0;
                }
            }
            else
            {
                if (!first)
                {
                    right = 0;
                }

                if (!last)
                {
                    left = 0;
                }
            }
        }
        else if (flow.InlineSign > 0)
        {
            if (!first)
            {
                top = 0;
            }

            if (!last)
            {
                bottom = 0;
            }
        }
        else
        {
            if (!first)
            {
                bottom = 0;
            }

            if (!last)
            {
                top = 0;
            }
        }

        return new Thickness(top, right, bottom, left);
    }

    /// <summary>Turns one range of a note into ordinary inline items for the shared line composer.</summary>
    private List<InlineItem> NoteItems(
        NoteSource source,
        Int32 start,
        Int32 end,
        Double basis,
        WritingModeInfo flow,
        Boolean upright,
        InlineAncestor? root = null)
    {
        var items = new List<InlineItem>();
        var ancestors = new Dictionary<Box, InlineAncestor>();
        var markers = source.NestedInterline
            .Where(marker =>
                marker.Position >= start &&
                (marker.Position < end || (marker.Position == end && end == source.Text.Length)))
            .ToList();
        var markerIndex = 0;
        var marginMarkers = source.NestedMargin
            .Where(marker =>
                marker.Position >= start &&
                (marker.Position < end || (marker.Position == end && end == source.Text.Length)))
            .ToList();
        var marginIndex = 0;

        InlineAncestor? ChainFor(IReadOnlyList<Box> boxes)
        {
            InlineAncestor? chain = root;

            foreach (var ancestor in boxes)
            {
                if (!ancestors.TryGetValue(ancestor, out chain))
                {
                    chain = new InlineAncestor(ancestor, chain, basis, flow, _measurer, noteContent: true);
                    ancestors.Add(ancestor, chain);
                }
            }

            return chain;
        }

        void AddMarker(NestedInterlineNote marker)
        {
            items.Add(InlineItem.CreateInterlineNote(
                marker.Note.Content,
                marker.Note.Box,
                ChainFor(marker.Ancestors)));
        }

        void AddMarginMarker(NestedMarginNote marker)
        {
            items.Add(InlineItem.CreateMarginNote(
                marker.Note.Box,
                ChainFor(marker.Ancestors)));
        }

        void AddMarkersThrough(Int32 position)
        {
            while (true)
            {
                var interlinePosition = markerIndex < markers.Count
                    ? markers[markerIndex].Position
                    : Int32.MaxValue;
                var marginPosition = marginIndex < marginMarkers.Count
                    ? marginMarkers[marginIndex].Position
                    : Int32.MaxValue;
                var next = Math.Min(interlinePosition, marginPosition);

                if (next > position)
                {
                    return;
                }

                if (interlinePosition <= marginPosition)
                {
                    AddMarker(markers[markerIndex++]);
                }
                else
                {
                    AddMarginMarker(marginMarkers[marginIndex++]);
                }
            }
        }

        void AddText(NoteSpan span, Int32 from, Int32 to)
        {
            if (to <= from)
            {
                return;
            }

            var text = source.Text[from..to];
            var partFlow = upright
                ? new WritingModeInfo(
                    flow.Mode,
                    TextOrientation.Upright,
                    flow.CombinesUpright,
                    flow.Direction)
                : flow;

            items.Add(InlineItem.CreateText(
                text,
                span.Style,
                span.Box,
                ChainFor(span.Ancestors),
                partFlow));
        }

        foreach (var span in source.Spans)
        {
            var from = Math.Max(start, span.Start);
            var to = Math.Min(end, span.Start + span.Length);

            if (to <= from)
            {
                continue;
            }

            AddMarkersThrough(from);

            while (true)
            {
                var interlinePosition = markerIndex < markers.Count
                    ? markers[markerIndex].Position
                    : Int32.MaxValue;
                var marginPosition = marginIndex < marginMarkers.Count
                    ? marginMarkers[marginIndex].Position
                    : Int32.MaxValue;
                var at = Math.Min(interlinePosition, marginPosition);

                if (at >= to)
                {
                    break;
                }

                AddText(span, from, at);

                if (interlinePosition <= marginPosition)
                {
                    AddMarker(markers[markerIndex++]);
                }
                else
                {
                    AddMarginMarker(marginMarkers[marginIndex++]);
                }

                from = at;
            }

            AddText(span, from, to);
            AddMarkersThrough(to);
        }

        AddMarkersThrough(end);
        return items;
    }

    private static Boolean HasUprightCharacters(String text, Int32 start, Int32 end)
    {
        foreach (var rune in text[start..end].EnumerateRunes())
        {
            var value = rune.Value;

            if (value is >= 0x2E80 and <= 0x9FFF or
                >= 0xF900 and <= 0xFAFF or
                >= 0xFE30 and <= 0xFE4F or
                >= 0xFF00 and <= 0xFFEF or
                >= 0x20000 and <= 0x3FFFF)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Lays one note subline through the ordinary line composer, forced to a single line.</summary>
    private Line? NoteLine(
        NoteSource source,
        Int32 start,
        Int32 end,
        ComputedStyle noteStyle,
        WritingModeInfo flow,
        InlineContext context,
        Double maxInline,
        Boolean? orientation = null)
    {
        if (end <= start)
        {
            return null;
        }

        // A note is set in one orientation: the one its whole text asks for, not the one a piece of
        // it would ask for on its own. The caller that splits a note hands the decided value in.
        var upright = orientation ?? (flow.IsVertical && HasUprightCharacters(source.Text, start, end));
        var lines = BuildLines(
            NoteItems(source, start, end, maxInline, flow, upright),
            maxInline,
            flow,
            context,
            noteStyle,
            emergency: false,
            singleLine: true,
            settleEdges: false);

        if (lines.Count == 0)
        {
            return null;
        }

        if (lines.Count != 1)
        {
            var excerpt = source.Text.Substring(start, Math.Min(80, end - start));
            throw new InvalidOperationException(
                $"An inline-note subline must build exactly one line; got {lines.Count} for \"{excerpt}\".");
        }

        var line = lines[0];

        if (line.Used <= 0 && !line.Fragments.Exists(HasInk))
        {
            return null;
        }

        line.Left = 0;
        line.GapBefore = 0;
        line.Extent = line.Used;
        line.Offset = 0;

        return line;
    }

    /// <summary>
    /// Splits a text node on its explicit newlines. They survive white-space processing only for
    /// preformatted content, where each one forces a line break rather than being drawn as a glyph.
    /// </summary>
    private static void AddText(String text, ComputedStyle style, Box box, List<InlineItem> items, InlineAncestor? chain)
    {
        var start = 0;

        while (true)
        {
            var index = text.IndexOf('\n', start);

            if (index < 0)
            {
                if (start < text.Length)
                {
                    items.Add(InlineItem.CreateText(text[start..], style, box, chain));
                }

                return;
            }

            if (index > start)
            {
                items.Add(InlineItem.CreateText(text[start..index], style, box, chain));
            }

            items.Add(InlineItem.CreateBreak(style, box, chain));
            start = index + 1;
        }
    }

    private List<Line> BuildLines(
        List<InlineItem> items,
        Double maxInline,
        WritingModeInfo flow,
        InlineContext context,
        ComputedStyle strut,
        TextIndentation indent = default,
        Boolean emergency = true,
        Boolean singleLine = false,
        Boolean settleEdges = true,
        Double blockStart = 0,
        IReadOnlyList<Double>? slotExtents = null,
        IReadOnlyList<Double>? minimumInlineStarts = null,
        Int32 initialSlot = 0,
        IReadOnlyList<Line>? slotBodyLines = null)
    {
        var lines = new List<Line>();

        // Where the content's last drawing character stands: the white space a page trails into is
        // not drawn at the end of the line, so it does not keep the glyph in front of it from being
        // the last one the line ends at.
        var lastDrawn = LastDrawnContent(items);

        // The elements a line resumes inside, which is what the next builder needs to know so that it
        // draws no start edge for an element whose start edge is already on the line above.
        var carried = (HashSet<InlineAncestor>?)null;

        // How much room each line has, which is the block's extent for every block that has nothing
        // floated beside it and the float's far edge for the lines that have. A column is left out, as
        // it has been for floats themselves: the band a line would be shortened by runs sideways there.
        var room = new LineRoom(
            flow.IsVertical || singleLine ? null : context.Containing.Floats,
            singleLine ? Double.PositiveInfinity : maxInline,
            flow.InlineSign < 0,
            blockStart);

        var slot = initialSlot;

        Double Inset(Double indent)
        {
            var leading = indent > 0 ? Math.Max(0, indent - room.Inset) : indent;
            var minimumInlineStart = minimumInlineStarts is { } starts && slot < starts.Count
                ? starts[slot]
                : 0;

            if (minimumInlineStart > room.Inset)
            {
                leading = Math.Max(leading, minimumInlineStart - room.Inset);
            }

            return leading;
        }

        // The distance a line is set in by, measured from the edge its room begins at: a line the
        // modifiers put the distance on takes it, and every other line begins where the box does.
        Double Settled(Boolean first, Boolean afterBreak) =>
            Inset(indent.Carries(first, afterBreak) ? indent.Distance : 0);

        // An indent is measured from the content edge and a float shortens the line from that same edge,
        // so a float standing where the indent reaches leaves the line to start where the float ends, not
        // that far past it. A negative indent hangs out of the box and is never pulled back in.
        var leading = Settled(first: true, afterBreak: false);
        var builder = new LineBuilder(
            strut!,
            leading,
            flow.IsVertical,
            _measurer,
            startsParagraph: true,
            firstLine: settleEdges);

        // The place the line could be cut back to at a soft hyphen, and the steps it has taken since:
        // they begin the next line when the run after the hyphen turns out not to fit.
        LineBuilder.Checkpoint? breakPoint = null;
        List<TailStep>? following = null;

        // The one buffer the steps after an opportunity are kept in. Text that may break after every
        // character made a fresh opportunity at every one of them, and a fresh list each time.
        var tail = new List<TailStep>();

        // The room a stop or comma at the end of the line was left off for, which it only keeps while
        // it is the last thing on the line: the next thing put there takes that room back, and the
        // fit it is asked about is made against the line as it really is (CSS Text 3 §8.2.1).
        var pendingGrace = 0.0;

        void Charge()
        {
            if (pendingGrace > 0)
            {
                builder.Widen(pendingGrace);
                pendingGrace = 0;
            }
        }

        void Close(LineBuilder filled, Boolean forced = false, Boolean hyphen = false)
        {
            // The room a cleared break moved is taken by the line that follows it, so the line
            // before no longer reaches past itself.
            if (lines.Count > 0)
            {
                lines[^1].Tail = 0;
            }

            var line = filled.Build(_measurer, room.Room, carried, forced, hyphen);

            line.Left = room.Inset;
            line.Extent = room.Room;
            line.GapBefore = room.Gap;
            line.ForceEnded = forced;
            line.SlotIndex = slotExtents is null ? -1 : slot;

            lines.Add(line);
            carried = line.ContinuesToNext;
            room.Next(line.Height);

            if (slotExtents is { Count: > 0 } && slot < slotExtents.Count - 1)
            {
                var extent = slotExtents[slot];

                if (slotBodyLines is { } bodyLines && slot < bodyLines.Count)
                {
                    var noteStyle = line.Governing ?? strut;
                    extent -= InterlineBodyAdjustment(bodyLines[slot], noteStyle);
                }

                room.Skip(Math.Max(0, extent));
                slot++;
            }
        }

        void NextLine(Boolean forced = false, Boolean hyphenate = false)
        {
            Close(builder, forced, hyphenate);
            builder = new LineBuilder(
                strut,
                carried,
                flow.IsVertical,
                _measurer,
                startsParagraph: forced,
                indent: Settled(first: false, afterBreak: forced));
            breakPoint = null;
            following = null;
            pendingGrace = 0;
        }

        void AdvanceToSlot(Int32 target)
        {
            if (slotExtents is not { Count: > 0 } || target <= slot)
            {
                return;
            }

            if (builder.HasContent)
            {
                NextLine(forced: true);
            }
            else
            {
                builder = new LineBuilder(
                    strut,
                    carried,
                    flow.IsVertical,
                    _measurer,
                    startsParagraph: false,
                    indent: Settled(first: false, afterBreak: false));
                breakPoint = null;
                following = null;
                pendingGrace = 0;
            }

            while (slot < target && slot < slotExtents.Count - 1)
            {
                room.Skip(slotExtents[slot]);
                slot++;
                builder = new LineBuilder(
                    strut,
                    carried,
                    flow.IsVertical,
                    _measurer,
                    startsParagraph: false,
                    indent: Settled(first: false, afterBreak: false));
            }
        }

        void Replay(List<TailStep> steps)
        {
            foreach (var step in steps)
            {
                if (step.OutOfFlow)
                {
                    builder.AddOutOfFlow(step.Box, step.Style, step.Inline);
                }
                else if (step.Empty)
                {
                    builder.AddEmpty(step.Box, step.Style, step.Flow, step.Inline!);
                }
                else
                {
                    builder.Add(
                        step.Source, step.Start, step.Length, step.Advance,
                        step.Style, step.Box, step.Flow, step.Inline);
                }
            }
        }

        // A box that does not fit ends the line at the last opportunity before the element it belongs
        // to, when the steps taken since that opportunity are the element's own: an element that does
        // not wrap its content is one unit the line moves whole, and the box is part of it.
        Boolean HandsBack(InlineAncestor? unit) =>
            breakPoint is not null && following is { } taken &&
            taken.Exists(step => SitsWithin(step.Inline, unit));

        Boolean HandBack(InlineAncestor? unit)
        {
            if (breakPoint is not { } point || following is not { } steps ||
                !steps.Exists(step => SitsWithin(step.Inline, unit)))
            {
                return false;
            }

            builder.Restore(point);
            NextLine();
            breakPoint = null;
            following = null;
            Replay(steps);

            return true;
        }

        // Where each item begins along the line the builder is filling. A reading hanging past its pair
        // asks the room of a neighbour whose tabs are already placed, and only the line can say where.
        var begins = new Double[items.Count];

        for (var index = 0; index < items.Count; index++)
        {
            var item = items[index];

            begins[index] = builder.Width;

            if (item.IsBreak)
            {
                NextLine(forced: true);

                // A break that asks to clear stands the lines after it below the floats it names;
                // this is the `<br clear>` a page still writes for the job (HTML rendering §15.3.3).
                var dropped = room.Clear(item.Style.Clear);

                // A break that clears at the end of the content carries the block down to the
                // floats it clears, even though no line follows it to stand there. A line that
                // does follow takes the room instead, and the tail is dropped where it is built.
                if (dropped > 0 && lines.Count > 0)
                {
                    lines[^1].Tail = Math.Max(lines[^1].Tail, room.Top);
                }


                continue;
            }

            // Whatever comes beside the glyph the line was left short for takes that room back: the
            // glyph hangs only while it ends the line, and from here it does not.
            Charge();

            if (item.InterlineAnchor is { } anchor)
            {
                AdvanceToSlot(anchor.Slot);

                var target = Math.Max(builder.Width, anchor.Position - room.Inset);

                if (room.Room > 0)
                {
                    target = Math.Min(target, Math.Max(builder.Width, room.Room));
                }

                builder.AddInterlineGap(item.Box, item.Style, flow, target - builder.Width);
                continue;
            }

            if (item.InterlineNote is { } interlineNote)
            {
                builder.AddInterlineNote(item.Box, item.Style, flow, item.Inline, interlineNote);
                continue;
            }

            if (item.MarginNote is { } marginNote)
            {
                builder.AddMarginNote(item.Box, item.Style, flow, item.Inline, marginNote);
                continue;
            }

            // Only runs of text are kept for the line after the box the loop is about to place: a box
            // is placed where it stands and hands the line back only to the opportunity before the
            // element it belongs to, so the cut a later run looks for never stands past it.
            if (item.IsOutOfFlow)
            {
                // No room is taken and the cursor does not move, so where the cursor stands when the box
                // is met is the place it would have had. The fragment carries that much and nothing else.
                builder.AddOutOfFlow(item.Box, item.Style, item.Inline);

                if (breakPoint is not null && following is not null)
                {
                    following.Add(new TailStep(
                        String.Empty, 0, 0, 0, item.Style, item.Box, flow, item.Inline, OutOfFlow: true));
                }

                continue;
            }

            // An inline element that holds nothing still stands on the line for the margins, borders
            // and padding it carries along that axis. It takes that room where the line has come to,
            // and a line with no room left for it ends at the last opportunity before it - the box
            // stands on the line after this one, not across its own edge (CSS 2.1 §8.1, §9.4.2).
            if (item.IsEmpty)
            {
                var ancestor = item.Inline!;
                var after = builder.ExtentFor(ancestor) + ancestor.InlineEndInset + ancestor.InlineEndMargin;

                if (room.Room > 0 && after > room.Room)
                {
                    if (breakPoint is { } point && builder.HasContent)
                    {
                        var replay = following;

                        builder.Restore(point);
                        NextLine();
                        breakPoint = null;

                        if (replay is not null)
                        {
                            Replay(replay);
                        }
                    }
                    else if (builder.HasContent)
                    {
                        NextLine();
                    }
                    else if (room.Shortened)
                    {
                        room.Drop(after);
                    }
                }

                builder.AddEmpty(item.Box, item.Style, item.Box.Flow, ancestor);

                if (breakPoint is not null && following is not null)
                {
                    following.Add(new TailStep(
                        String.Empty, 0, 0, 0, item.Style, item.Box, flow, ancestor, Empty: true));
                }

                continue;
            }

            if (item.NoteSource is { Text.Length: > 0 } noteSource)
            {
                var noteText = noteSource.Text;
                var note = item.Box;
                var noteStyle = item.Style;
                var noteFlow = note.Flow;
                var noteLineHeight = LineBoxHeight(noteStyle, noteStyle.GetFont());
                var margin = noteStyle.Margin.Resolve(maxInline);
                var padding = noteStyle.Padding.Resolve(maxInline);
                var border = BlockLayout.BorderOf(note);
                var (marginStart, marginEnd) = noteFlow.InlineMargins(margin);
                var (paddingStart, paddingEnd) = noteFlow.InlineMargins(padding);
                var (borderStart, borderEnd) = noteFlow.InlineMargins(border);
                var startEdges = marginStart + paddingStart + borderStart;
                var endEdges = marginEnd + paddingEnd + borderEnd;
                var clonesDecoration = noteStyle.ClonesDecoration;
                var offset = 0;
                var firstFragment = true;
                var measured = new Dictionary<(Int32 Start, Int32 End, Double Basis, Boolean Upright), Line?>();
                var hoistedMarginNotes = new HashSet<Box>();
                var hoistedInterlineNotes = new HashSet<Box>();

                // The orientation the note's current line is being set in, which every piece of it
                // takes: a note is upright as a whole, not row by row.
                var segmentUprightNow = false;

                Line? MeasuredLine(Int32 start, Int32 end, Double basis)
                {
                    var key = (start, end, basis, segmentUprightNow);

                    if (!measured.TryGetValue(key, out var line))
                    {
                        line = NoteLine(
                            noteSource,
                            start,
                            end,
                            noteStyle,
                            noteFlow,
                            context,
                            basis,
                            segmentUprightNow);
                        measured.Add(key, line);
                    }

                    return line;
                }

                while (offset < noteText.Length)
                {
                    if (noteText[offset] == '\n')
                    {
                        if (builder.HasContent)
                        {
                            NextLine();
                        }

                        offset++;
                        continue;
                    }

                    var segmentEnd = noteText.IndexOf('\n', offset);

                    if (segmentEnd < 0)
                    {
                        segmentEnd = noteText.Length;
                    }

                    var boundaries = new List<Int32> { offset };

                    for (var at = offset; at < segmentEnd;)
                    {
                        at += TextClusters.UnitsAt(noteText, at, segmentEnd);
                        boundaries.Add(at);
                    }

                    var count = boundaries.Count - 1;
                    // Ask for the same extent Build() will account from these fragments. The running
                    // estimate includes indent and temporary punctuation grace, which would otherwise
                    // make this note choose too few characters for the room it really has.
                    var available = room.Room - builder.Indent - builder.CurrentUsed();
                    var segmentIsEnd = segmentEnd == noteText.Length;

                    Boolean AllowsBreakAt(Int32 at)
                    {
                        if (at <= offset || at >= segmentEnd)
                        {
                            return true;
                        }

                        var before = CodePointBefore(noteText, at);
                        var after = TextChunker.CodePointAt(noteText, at);
                        var beforeKind = CharUnicodeInfo.GetUnicodeCategory(before);
                        var afterKind = CharUnicodeInfo.GetUnicodeCategory(after);

                        static Boolean IsBreakSensitive(UnicodeCategory category) =>
                            category is UnicodeCategory.SpaceSeparator or
                                UnicodeCategory.LineSeparator or
                                UnicodeCategory.ParagraphSeparator or
                                UnicodeCategory.ConnectorPunctuation or
                                UnicodeCategory.DashPunctuation or
                                UnicodeCategory.OpenPunctuation or
                                UnicodeCategory.ClosePunctuation or
                                UnicodeCategory.InitialQuotePunctuation or
                                UnicodeCategory.FinalQuotePunctuation or
                                UnicodeCategory.OtherPunctuation;

                        if (IsBreakSensitive(afterKind) ||
                            beforeKind is UnicodeCategory.OpenPunctuation or
                                UnicodeCategory.InitialQuotePunctuation)
                        {
                            return false;
                        }

                        if (!IsBreakSensitive(beforeKind))
                        {
                            return true;
                        }

                        var style = noteSource.SpanAt(at).Style;
                        return LineBreaks.MayBreak(before, after, style.WordBreakingRules);
                    }

                    var advances = new Double[count + 1];
                    var segmentUpright = noteFlow.IsVertical &&
                        HasUprightCharacters(noteText, offset, segmentEnd);
                    segmentUprightNow = segmentUpright;
                    var estimateFlow = segmentUpright
                        ? new WritingModeInfo(
                            noteFlow.Mode,
                            TextOrientation.Upright,
                            noteFlow.CombinesUpright,
                            noteFlow.Direction)
                        : noteFlow;

                    // Cumulative cluster advances answer both the seed and the widths a pair of
                    // candidates is judged by, so a candidate costs no line at all: only the pair the
                    // line finally takes is built.
                    for (var unit = 0; unit < count; unit++)
                    {
                        var start = boundaries[unit];
                        var end = boundaries[unit + 1];
                        var span = noteSource.SpanAt(start);
                        advances[unit + 1] = advances[unit] +
                            MeasureChunk(_measurer, span.Style, noteText, start, end - start, estimateFlow);
                    }

                    // The room a half takes, read off the cumulative advances the segment already
                    // measured: the whole segment's orientation is the note's, so a half is the same
                    // clusters in the same flow, and only the boundary rules at a cut are asked of the
                    // line (the ending grace below).
                    Double HalfWidth(Int32 from, Int32 to) =>
                        from >= to ? 0 : advances[to] - advances[from];

                    // Punctuation prohibitions can cover every boundary near the width-balanced
                    // estimate. Walk outward for legal splits instead of falling back to one line.
                    List<Int32> NearbySplits(Int32 take, Int32 center, Int32 wanted)
                    {
                        var splits = new List<Int32>(wanted);
                        var left = Math.Min(take - 1, center);
                        var right = Math.Min(take, center + 1);

                        while (splits.Count < wanted && (left >= 1 || right <= take))
                        {
                            if (left >= 1)
                            {
                                if (AllowsBreakAt(boundaries[left]))
                                {
                                    splits.Add(left);
                                }

                                left--;
                            }

                            if (splits.Count < wanted && right <= take)
                            {
                                if (AllowsBreakAt(boundaries[right]))
                                {
                                    splits.Add(right);
                                }

                                right++;
                            }
                        }

                        if (splits.Count == 0)
                        {
                            // The end boundary is always legal, even where a long unbreakable unit
                            // leaves no interior split available.
                            splits.Add(take);
                        }

                        return splits;
                    }

                    (Line? First, Line? Second) SplitAt(Int32 take, Int32 at, Double content)
                    {
                        var end = boundaries[take];
                        var split = boundaries[at];
                        var first = MeasuredLine(offset, split, content);
                        var second = MeasuredLine(split, end, content);

                        return (first, second);
                    }

                    // The two rows a note is set in: the first is never shorter than the second. Among
                    // the splits that keep it so the narrowest first row wins - the pair that is most
                    // balanced - and a split that cannot keep it only stands where none can.
                    Boolean Improves(
                        Boolean keeps, Double first, Double needed, Boolean bestKeeps,
                        Double bestFirst, Double bestNeeded, Int32 at, Int32 bestAt)
                    {
                        if (keeps != bestKeeps)
                        {
                            return keeps;
                        }

                        if (keeps)
                        {
                            if (first < bestFirst - 0.001)
                            {
                                return true;
                            }

                            if (Math.Abs(first - bestFirst) > 0.001)
                            {
                                return false;
                            }
                        }

                        if (needed < bestNeeded - 0.001)
                        {
                            return true;
                        }

                        return Math.Abs(needed - bestNeeded) <= 0.001 && at > bestAt;
                    }

                    (Int32 Split, Double Needed) Balanced(Int32 take, Boolean last)
                    {
                        var estimate = Estimate(take, last);
                        var startEdge = firstFragment || clonesDecoration ? startEdges : 0;
                        var endEdge = last || clonesDecoration ? endEdges : 0;
                        var bestAt = estimate.Split;

                        // A note that ends here ends at the line's end, where a trailing stop or comma
                        // may hang: the pair that fits is the pair the built line would report.
                        var grace = 0.0;

                        if (last && take == count)
                        {
                            var start = boundaries[take - 1];
                            var taken = boundaries[take] - start;
                            var style = noteSource.SpanAt(start).Style;
                            var endGrace = EndGrace(_measurer, style, noteText, start, taken, estimateFlow);
                            var hangGrace = HangGrace(
                                _measurer, style, noteText, start, taken, segmentEnd,
                                lastOfContent: true, estimateFlow);
                            grace = Math.Max(endGrace, hangGrace);
                        }
                        var bestKeeps = false;
                        var bestFirst = Double.PositiveInfinity;
                        var bestNeeded = Double.PositiveInfinity;

                        foreach (var at in NearbySplits(take, estimate.Split, wanted: 3))
                        {
                            var split = boundaries[at];
                            var first = HalfWidth(0, at);
                            var second = Math.Max(0, HalfWidth(at, take) - grace);
                            var needed = Math.Max(first, second) + startEdge + endEdge;
                            var keeps = first + 0.001 >= second;

                            if (Improves(
                                keeps, first, needed, bestKeeps, bestFirst, bestNeeded, at, bestAt))
                            {
                                bestKeeps = keeps;
                                bestFirst = first;
                                bestNeeded = needed;
                                bestAt = at;
                            }
                        }

                        if (Double.IsPositiveInfinity(bestNeeded))
                        {
                            bestAt = take;
                            bestNeeded = HalfWidth(0, take) + startEdge + endEdge;
                        }

                        return (bestAt, bestNeeded);
                    }

                    (Int32 Split, Double Needed) Estimate(Int32 take, Boolean last)
                    {
                        var startEdge = firstFragment || clonesDecoration ? startEdges : 0;
                        var endEdge = last || clonesDecoration ? endEdges : 0;
                        var target = advances[take] / 2;
                        var upper = Math.Max(1, take);
                        var found = Array.BinarySearch(advances, 0, take + 1, target);

                        if (found < 0)
                        {
                            found = ~found;
                        }

                        var bestMaximum = Double.PositiveInfinity;
                        var bestSplit = upper;

                        foreach (var at in NearbySplits(take, found, wanted: 6))
                        {
                            var firstWidth = advances[at];
                            var secondWidth = advances[take] - advances[at];
                            var maximum = Math.Max(firstWidth, secondWidth);

                            if (maximum < bestMaximum - 0.001 ||
                                (Math.Abs(maximum - bestMaximum) <= 0.001 && at > bestSplit))
                            {
                                bestMaximum = maximum;
                                bestSplit = at;
                            }
                        }

                        return (bestSplit, bestMaximum + startEdge + endEdge);
                    }

                    var best = 0;
                    var bestLast = false;
                    var chosen = default((Int32 Split, Double Needed));

                    if (segmentIsEnd && count > 0)
                    {
                        var whole = Estimate(count, last: true);

                        if (whole.Needed <= available)
                        {
                            best = count;
                            bestLast = true;
                        }
                    }

                    if (best == 0)
                    {
                        var low = 1;
                        var high = segmentIsEnd ? count - 1 : count;

                        while (low <= high)
                        {
                            var middle = low + ((high - low) / 2);

                            if (Estimate(middle, last: false).Needed <= available)
                            {
                                best = middle;
                                bestLast = false;
                                low = middle + 1;
                            }
                            else
                            {
                                high = middle - 1;
                            }
                        }
                    }

                    if (best > 0)
                    {
                        chosen = Balanced(best, bestLast);

                        if (chosen.Needed > available && bestLast)
                        {
                            best = segmentIsEnd ? count - 1 : count;
                            bestLast = false;
                            chosen = best > 0
                                ? Balanced(best, last: false)
                                : default;
                        }

                        while (!bestLast && best > 0 && chosen.Needed > available)
                        {
                            best--;
                            chosen = Balanced(best, last: false);
                        }

                        while (!bestLast && best > 0 && !AllowsBreakAt(boundaries[best]))
                        {
                            best--;
                            chosen = Balanced(best, last: false);
                        }
                    }

                    if (best == 0)
                    {
                        if (builder.HasContent)
                        {
                            NextLine();
                            continue;
                        }

                        // A note that cannot fit even one character in an empty line overflows rather
                        // than stalling the line breaker.
                        best = 1;
                        bestLast = segmentIsEnd;
                        chosen = Balanced(best, bestLast);
                    }

                    // The cumulative-advance search is only a fast estimate. Its measured split
                    // can often fit a few more graphemes, so probe forward until the next prefix
                    // genuinely overflows.
                    var furthest = segmentIsEnd ? count - 1 : count;

                    while (!bestLast && best < furthest)
                    {
                        var next = best + 1;

                        while (next <= furthest && !AllowsBreakAt(boundaries[next]))
                        {
                            next++;
                        }

                        if (next > furthest)
                        {
                            break;
                        }

                        var wider = Balanced(next, last: false);

                        if (wider.Needed > available)
                        {
                            break;
                        }

                        best = next;
                        chosen = wider;
                    }

                    if (segmentIsEnd && !bestLast && best == count - 1)
                    {
                        var whole = Balanced(count, last: true);

                        if (whole.Needed <= available)
                        {
                            best = count;
                            bestLast = true;
                            chosen = whole;
                        }
                    }

                    var prefixEnd = boundaries[best];
                    var content = Math.Max(1, available -
                        (firstFragment || clonesDecoration ? startEdges : 0) -
                        (bestLast || clonesDecoration ? endEdges : 0));
                    var drawn = SplitAt(best, chosen.Split, content);
                    var firstLine = drawn.First;
                    var secondLine = drawn.Second;
                    var firstWidth = firstLine?.Used ?? 0;
                    var secondWidth = secondLine?.Used ?? 0;

                    var drawsNothing = firstLine is null && secondLine is null;
                    var fragmentMargin = clonesDecoration
                        ? margin
                        : SliceEdges(noteFlow, margin, firstFragment, bestLast);
                    var fragmentPadding = clonesDecoration
                        ? padding
                        : SliceEdges(noteFlow, padding, firstFragment, bestLast);
                    var fragmentBorder = clonesDecoration
                        ? border
                        : SliceEdges(noteFlow, border, firstFragment, bestLast);
                    var (blockMarginStart, blockMarginEnd) = noteFlow.BlockMargins(margin);
                    var (blockPaddingStart, blockPaddingEnd) = noteFlow.BlockMargins(padding);
                    var (blockBorderStart, blockBorderEnd) = noteFlow.BlockMargins(border);
                    var blockSize = (firstLine?.Height ?? noteLineHeight) +
                        (secondLine?.Height ?? noteLineHeight) +
                        blockMarginStart + blockMarginEnd +
                        blockPaddingStart + blockPaddingEnd +
                        blockBorderStart + blockBorderEnd;
                    var run = new NoteRun(
                        firstLine,
                        secondLine,
                        noteFlow,
                        noteLineHeight,
                        blockSize,
                        Math.Max(0, (LineBoxHeight(strut, strut.GetFont()) - strut.FontSize) / 2),
                        prefixEnd < segmentEnd,
                        fragmentMargin,
                        fragmentPadding,
                        fragmentBorder);
                    var inlineSize = Math.Max(firstWidth, secondWidth) +
                        (firstFragment || clonesDecoration ? startEdges : 0) +
                        (bestLast || clonesDecoration ? endEdges : 0);
                    var noteStart = builder.Width;
                    var noteContentStart = noteStart +
                        (firstFragment || clonesDecoration ? startEdges : 0);

                    if (!drawsNothing)
                    {
                        builder.AddNote(note, noteStyle, noteFlow, item.Inline, run, inlineSize);
                        firstFragment = false;
                    }

                    void HoistNested(Line? row)
                    {
                        if (row is null)
                        {
                            return;
                        }

                        foreach (var marker in row.Fragments)
                        {
                            if (marker.InterlineNote is not { } nested)
                            {
                                if (marker.MarginNote is { } margin)
                                {
                                    if (hoistedMarginNotes.Add(margin.Box))
                                    {
                                        builder.AddMarginNote(
                                            marker.Box,
                                            marker.Style,
                                            flow,
                                            marker.Inline,
                                            margin);
                                    }
                                }

                                continue;
                            }

                            var at = InterlineAnchorPosition(row, marker, 0);
                            if (hoistedInterlineNotes.Add(nested.Box))
                            {
                                builder.AddMappedInterlineNote(
                                    marker.Box,
                                    marker.Style,
                                    noteFlow,
                                    nested,
                                    noteContentStart + at);
                            }
                        }
                    }

                    HoistNested(firstLine);
                    HoistNested(secondLine);

                    offset = prefixEnd;

                    if (offset < noteText.Length)
                    {
                        if (noteText[offset] == '\n')
                        {
                            offset++;
                        }

                        if (offset < noteText.Length && !drawsNothing)
                        {
                            NextLine();
                        }
                    }
                }

                breakPoint = null;
                following = null;
                pendingGrace = 0;
                continue;
            }

            if (item.IsReplaced)
            {
                // The container's inline extent is the containing block of everything on the line, so
                // that is what a percentage among the item's constraints resolves against. A line box
                // has no definite extent across itself, so a percentage on the other axis is `auto`.
                var (width, height) = ReplacedSizing.Resolve(item.Box, flow.IsVertical ? 0 : maxInline, flow.IsVertical ? maxInline : 0);
                var inlineSize = flow.InlineExtent(width, height);
                var blockSize = flow.BlockExtent(width, height);

                var band = room.Height;
                room.Grow(blockSize);

                if (room.Room > 0 && builder.ExtentFor(item.Inline) + inlineSize > room.Room)
                {
                    if (builder.HasContent)
                    {
                        room.Restore(band);

                        if (!HandBack(item.Inline))
                        {
                            NextLine();
                        }

                        room.Grow(blockSize);
                    }
                    else if (room.Shortened)
                    {
                        // CSS 2.1 §9.5: a line box a float has shortened, too small for the content
                        // standing on it, is shifted downward until some content fits or no floats
                        // are left - the wide picture beside the floated one goes below it rather
                        // than overflowing the room the float left.
                        room.Drop(builder.ExtentFor(item.Inline) + inlineSize);
                    }
                }

                builder.AddReplaced(item.Box, inlineSize, blockSize, item.Inline);
                breakPoint = null;
                following = null;
                continue;
            }

            // An inline-block is placed as one piece on the same terms as a replaced box, but its size
            // is the layout's own to work out, which is why it is measured here rather than resolved
            // from the box's intrinsic one.
            if (item.IsAtomic)
            {
                // CSS 2.1 §10.3.9: an inline-block whose width is `auto` shrinks to fit the space its
                // line offers - the whole line, not what is left of it. Handing it the room the cursor
                // has left instead lets the box be squeezed into the last few pixels of a line it never
                // fitted on: a news item that should have moved down to the next line wraps into a
                // column a couple of characters wide and grows as tall as the block it sits in.
                var line = room.Room <= 0 ? Double.PositiveInfinity : room.Room;

                var atomic = MeasureInlineBlock(item.Box, maxInline, line, flow, context);

                // The height the box gives the line is asked of the shape before the box is put on the
                // line, because the room the line has is the room across the whole band it stands in.
                var band = room.Height;
                room.Grow(atomic.BlockSize + atomic.BlockStartMargin + atomic.BlockEndMargin);

                // An inline-block that does not fit ends the line before it where the line may end:
                // where the value its own text is written with lets the line break, or where the box
                // is part of a unit that began after that opportunity and can be handed back whole.
                // A box the line may not break beside - one written under `white-space: nowrap` and
                // belonging to no unit that can move - overflows instead (CSS Text 3 §5.1, §5.2).
                if (room.Room > 0 && builder.ExtentFor(item.Inline) + atomic.InlineSize > room.Room)
                {
                    var mayBreak = item.Style.WrapsLines || BeginsHere(builder, item.Inline);
                    var handsBack = !mayBreak && builder.HasContent && HandsBack(item.Inline);

                    if (builder.HasContent && (mayBreak || handsBack))
                    {
                        room.Restore(band);

                        if (!HandBack(item.Inline))
                        {
                            NextLine();
                        }

                        room.Grow(atomic.BlockSize + atomic.BlockStartMargin + atomic.BlockEndMargin);
                    }
                    else if (room.Shortened)
                    {
                        // CSS 2.1 §9.5: a line box a float has shortened, too small for the content
                        // standing on it, is shifted downward until some content fits or no floats
                        // are left. The box then stands on a wider line, so it is measured again
                        // against the room that line has rather than against the one it left.
                        room.Drop(builder.ExtentFor(item.Inline) + atomic.InlineSize);

                        line = room.Room <= 0 ? Double.PositiveInfinity : room.Room;
                        atomic = MeasureInlineBlock(item.Box, maxInline, line, flow, context);
                    }
                }

                builder.AddAtomic(item.Box, atomic, item.Inline);
                breakPoint = null;
                following = null;
                continue;
            }

            // A ruby is placed as one piece: breaking between a base and its annotation would leave a
            // reading stranded without the characters it belongs to.
            if (item.IsRuby)
            {
                // The pair's own flow is what its two halves are oriented by, so an inline element
                // that asks for a different orientation keeps it inside the line it sits in. The base
                // is then drawn in the orientation its own characters resolve to - a CJK base stands
                // upright under `mixed`, exactly as the same character would outside a ruby.
                var rubyFlow = item.Box.Flow;
                var baseFlow = ResolveOrientation(item.Text!, rubyFlow);
                var annotation = MeasureAnnotation(item);
                var baseWidth = PairWidth(item.Style, strut, item.Text!, builder.Width, rubyFlow);
                var (before, after) = OverhangRoom(
                    items, index, builder.Width, baseWidth, annotation.Width, begins, strut);
                var extent = RubyExtent(item, baseWidth, annotation.Width, before, after);

                var band = room.Height;
                room.Grow(LineBoxHeight(item.Style, item.Style.GetFont()));

                if (room.Room > 0 && builder.ExtentFor(item.Inline) + extent > room.Room &&
                    item.Style.WrapsLines)
                {
                    if (builder.HasContent)
                    {
                        room.Restore(band);

                        if (!HandBack(item.Inline))
                        {
                            NextLine();
                        }

                        room.Grow(LineBoxHeight(item.Style, item.Style.GetFont()));
                    }
                    else if (room.Shortened)
                    {
                        // CSS 2.1 §9.5: a line box a float has shortened, too small for the content
                        // standing on it, is shifted downward until some content fits or no floats
                        // are left.
                        room.Drop(builder.ExtentFor(item.Inline) + extent);
                    }

                    // The pair now stands where a different line left it, and the room beside it is
                    // another question: it is asked again of the place the pair holds here.
                    begins[index] = builder.Width;
                    (before, after) = OverhangRoom(
                        items, index, builder.Width, baseWidth, annotation.Width, begins, strut);
                    extent = RubyExtent(item, baseWidth, annotation.Width, before, after);
                }

                builder.AddRuby(
                    item.Text!,
                    extent,
                    item.Style,
                    item.Box,
                    baseFlow,
                    annotation,
                    baseWidth,
                    RubyHang(item, extent, baseWidth, annotation.Width, after),
                    item.Inline);
                breakPoint = null;
                following = null;
                continue;
            }

            // An inline element may ask for an orientation of its own, so each run is measured and
            // drawn in the flow of the box it came from rather than in the container's. The font is
            // taken once for the item, because every piece of it is set in that one font.
            var itemFlow = item.Flow ?? item.Box.Flow;
            var font = item.Style.GetFont();

            // A line does not break inside ruby: the base and its reading stand together, so a soft
            // hyphen written there offers no place to end and draws no mark (CSS Ruby 1 §3).
            var inRuby = InsideRuby(item.Inline);

            // The words a line carries make it as tall as the line height their own style asks for, and
            // that band is what a shape beside the line is read across.
            room.Grow(LineBoxHeight(item.Style, font));

            // A text node that follows letters is inside the word they began: its own first hyphen does
            // not open one, and the chunker is told so (UAX14 LB20a).
            var continuesWord = builder.LastCodePoint is { } prior &&
                LineBreaks.ContinuesWord(prior, item.Style.WordBreakingRules);

            foreach (var chunk in TextChunker.Ranges(item.Text!, item.Style.WordBreakingRules, continuesWord))
            {
                foreach (var run in new OrientedPieces(item.Text!, chunk.Start, chunk.Length, itemFlow))
                {
                    var piece = Measure(item.Style, font, run, itemFlow);

                    // A tab's room is the line running on to its next stop, which is a question about
                    // where the line stands rather than about the font, so it is asked here where that
                    // is known and not of the measurer.
                    var width = piece.IsWhiteSpace
                        ? WhiteSpaceWidth(item.Style, strut, run.Source, run.Start, run.Length, builder.Width, itemFlow)
                        : piece.Width;

                    var runTrim = builder.PreviewTrim(run.Source, run.Start, run.Length, item.Style, itemFlow);
                    var runAuto = builder.PreviewAutoSpace(run.Source, run.Start, run.Length, item.Style, itemFlow);

                    // White space a line begins with is not drawn, but only where the mode collapses it:
                    // `pre`, `pre-wrap` and `break-spaces` keep it, so a line that begins with one
                    // begins with it (and `break-spaces` makes it take room, which is what wraps the
                    // word after it). The node a run of text starts with may begin with the one space an
                    // element's edge collapsed into, and only the line knows whether that edge is a
                    // line's start - so the piece is dropped here rather than when the nodes were
                    // collapsed.
                    if (piece.IsWhiteSpace && !builder.HasContent && item.Style.CollapsesSpaces)
                    {
                        continue;
                    }

                    // White space never begins a line of its own: the break opportunity the other modes
                    // give it is after the run, not before it, so it stands at the end of the line it
                    // would have overflowed - hung, where the mode hangs its spaces, or taking room,
                    // where it does not - and takes the following word down with it. That is also why a
                    // `<br>` behind a space ends the line the space overflowed rather than an empty one.
                    var spaces = piece.IsWhiteSpace;

                    // A run the mode breaks inside is one that holds a character a line may end after:
                    // §4.1.3 gives `break-spaces` that opportunity after every preserved white space
                    // character *and* after every other space separator, so a piece the chunker left as
                    // "two letters and two ideographic spaces" is stepped as well.
                    var inside = item.Style.WrapsInsideSpaces;
                    var stepsSpaces = inside && TextChunker.HoldsBreakSpace(run.Source, run.Start, run.Length);

                    // The properties that tailor where a line may end put their opportunities in two different
                    // places (CSS Text 3 §5.4-§5.6): before the first character of a run of preserved white
                    // space, to keep a line from overflowing at all, and between the characters of a word.
                    // Only `word-break: break-all` leaves the first of these alone, because the rule it sets
                    // aside is the one about words.
                    // The opportunity before a preserved space is set aside by `word-break: break-all`,
                    // which is the one value that leaves the word rules standing and fills the line
                    // instead. `line-break: anywhere` sets those rules aside too, so there the line
                    // may still end before a space it would not have room for (CSS Text 3 §5.4-§5.6).
                    var beforeFirst = stepsSpaces && item.Style.WrapsBeforeSpaces &&
                        (!item.Style.BreaksBetweenLetters || item.Style.BreaksAnywhere);

                    // A word is stepped only where the break inside it is worth something: where the value
                    // gives it as an opportunity of its own, or where the word will not fit on a line at all
                    // and so no amount of moving it down would help.
                    var letters = !spaces &&
                        (item.Style.BreaksAnywhere || item.Style.BreaksBetweenLetters);
                    var stepsWord = !spaces &&
                        (letters || (emergency && item.Style.WrapsLongWords && width > room.Room));

                    var steps = (stepsSpaces || stepsWord) && room.Room > 0 &&
                        builder.ExtentFor(item.Inline) + width + runAuto.BoundaryDelta - runAuto.RetroDelta - runTrim.BoundaryDelta - runTrim.RetroDelta > room.Room &&
                        item.Style.WrapsLines;

                    var end = run.Start + run.Length;
                    var at = run.Start;
                    var previous = -1;

                    // A chunk the page may end a line after carries the mark it would draw: the hyphenate
                    // character is part of how much room the chunk needs, since a line that takes it and
                    // then breaks after it is one character longer than the chunk alone (CSS Text 3
                    // §6.2). Without this, "im-ple-" was set in a line five and a half characters wide.
                    var mark = run.Source[end - 1] == '­' && item.Style.WrapsLines
                        ? HyphenWidth(_measurer, item.Style, itemFlow)
                        : 0;

                    // A word broken only because nothing else would serve is begun on a line of its own
                    // first: the room the line had left is not filled with the end of it, which is what
                    // makes the break inside a word the last resort the property names. Where the licence
                    // stands between every two characters there is nothing to move down, and the line is
                    // filled to its end edge and cut there - but the licence to break between letters is
                    // worth nothing inside a run the rules hold together, and such a run is one unit the
                    // line has no business filling the remainder of with its head.
                    var movesWhole = !letters || !CutsInside(run.Source, run.Start, run.Length, item.Style);
                    var moved = false;

                    // `break-spaces` is the one mode whose spaces at the end of a line take room, so a word
                    // asked for the whole of itself is asked for the space that follows it too: where that
                    // space would not fit, the line ends at the opportunity before the word rather than
                    // between the word and its space - the break CSS Text 3 §5.3 does not give the mode,
                    // and the one `overflow-wrap: break-word` allows only where the line has no earlier
                    // opportunity to end at, which is not the case when a word is standing on it.
                    var trailed = item.Style.WrapsInsideSpaces && !spaces && end < run.Source.Length &&
                        TextChunker.IsBreakSpace(run.Source[end])
                            ? WhiteSpaceWidth(item.Style, strut, run.Source, end, 1, builder.Width, itemFlow)
                            : 0;

                    if (steps && stepsWord && movesWhole && builder.HasContent)
                    {
                        NextLine();
                        moved = true;
                    }

                    while (at < end)
                    {
                        // A glyph the line was left short for is only short while it ends the line.
                        Charge();

                        var taken = steps ? TextClusters.UnitsAt(run.Source, at, end) : run.Length;

                        // The room a character of the run asks for is asked of the line it stands on: a tab
                        // runs the line on to its next stop, and the stop is a question about where the line
                        // has come to rather than about the font.
                        var advance = !steps
                            ? width
                            : spaces
                                ? WhiteSpaceWidth(item.Style, strut, run.Source, at, taken, builder.Width, itemFlow)
                                : Measure(item.Style, run.Source, at, taken, itemFlow);

                        var trim = builder.PreviewTrim(run.Source, at, taken, item.Style, itemFlow);
                        var auto = builder.PreviewAutoSpace(run.Source, at, taken, item.Style, itemFlow);

                        // Where the break may be taken. The mode's own licence stands after a space it keeps,
                        // whichever piece that space was written in; the tailorable ones stand inside a word
                        // they were asked about, and for a word the script breaks between letters only between
                        // two of them - the punctuation keeps the rules it had, as does the character that
                        // opened the piece, whose boundary the chunking already answered.
                        var breaks = at == run.Start
                            ? spaces
                                ? beforeFirst
                                : !moved && builder.CanBreakBefore(run.Source, run.Start, item.Style)
                            : (inside && TextChunker.IsBreakSpace(run.Source[previous])) ||
                                (stepsWord &&
                                    (!item.Style.BreaksBetweenLetters ||
                                        item.Style.BreaksAnywhere ||
                                        item.Style.CountsLongWordBreaks ||
                                        LineBreaks.MayBreak(run.Source, previous, at, item.Style.WordBreakingRules)));

                        // A run that opens with content asks for the room the whole of it needs before it is
                        // stepped: the opportunity inside it is what the line falls back on once the run
                        // stands on a line of its own and still does not fit, not a licence to fill the last
                        // of the line above with the end of it. A run that opens with the space itself is the
                        // mode's own case - every character of it takes room where it stands. Where the value
                        // gives the break as an opportunity of its own, the line is filled to its end edge.
                        var asks = (at == run.Start && !spaces && !letters &&
                            !TextChunker.IsBreakSpace(run.Source[run.Start])
                            ? AskWidth(item, strut, run.Source, run.Start, run.Length, width, trailed, mark,
                                builder.Width, stepsSpaces, itemFlow)
                            : advance) - trim.BoundaryDelta + auto.BoundaryDelta - auto.RetroDelta;

                        // A closing punctuation that ends the line may give up its trailing half. The
                        // breaker keeps it on the line where that half is what fails to fit; the line
                        // close takes the room off for real.
                        var endGrace = EndGrace(
                            _measurer, item.Style, run.Source, at, taken, itemFlow);

                        // A line whose last character is a stop or comma may let that character hang
                        // rather than break the line before it, which is the room `allow-end` gives
                        // (CSS Text 3 §8.2.1).
                        var lastOfContent = index == lastDrawn.Item && at + taken == lastDrawn.End;

                        // A border or padding of the element the run sits in stands between the glyph
                        // and the line's end edge only where that element ends on this line: an
                        // element the line cuts draws its end edge on a later line. White space the
                        // line trails into is not drawn, so it does not put the glyph before it back
                        // inside the element.
                        var blocked = OnlySpaceRemains(item.Text!, at + taken) &&
                            EndEdgeOnLine(items, index, item.Inline);

                        var hangGrace = blocked
                            ? 0
                            : HangGrace(
                                _measurer, item.Style, run.Source, at, taken, end, lastOfContent, itemFlow);

                        // The margin the element the run sits in takes after its last words stands at
                        // the end of the line the run ends: the line asks for it as it asks for the
                        // words, and a negative one leaves the line that much shorter (CSS 2.1 §9.4.2).
                        var closing = 0.0;

                        if (at + taken == end && end == item.Text!.Length && item.Inline is { } owner &&
                            (index + 1 >= items.Count || !SitsWithin(items[index + 1].Inline, owner)))
                        {
                            closing = owner.InlineEndMargin;
                        }

                        // A soft hyphen is the one opportunity a line takes even when it has no room at
                        // all: the word has nowhere else to go, and the mark is drawn where the page put
                        // the hyphen (CSS Text 3 §6.1). Chrome measured on a zero-width box does the same.
                        var hyphen = builder.EndsInSoftHyphen && item.Style.WrapsLines && !inRuby;

                        // `auto-phrase` gives hyphenation up rather than overflow: the rest of the word is
                        // measured against the room the line has, and a word that fits runs on with its
                        // soft hyphens drawn as nothing. Only a word that cannot fit keeps them (CSS Text
                        // 4 §5.6, measured by word-break-auto-phrase-006 and -008).
                        if (hyphen && item.Style.WordBreak.Equals("auto-phrase", StringComparison.OrdinalIgnoreCase))
                        {
                            var wordEnd = at;

                            while (wordEnd < run.Source.Length && !TextChunker.IsCollapsible(run.Source[wordEnd]))
                            {
                                wordEnd += TextClusters.UnitsAt(run.Source, wordEnd, run.Source.Length);
                            }

                            var rest = Measure(item.Style, run.Source, at, wordEnd - at, itemFlow);

                            if (builder.ExtentFor(item.Inline) + rest <= room.Room)
                            {
                                hyphen = false;
                                breaks = false;
                                breakPoint = null;
                                following = null;
                            }
                        }

                        // The line may end at the boundary this step begins at: the state it stands in
                        // before the step is kept, and the steps taken after it are kept beside it, so a
                        // line forced to end earlier can back up to the last place it was allowed to.
                        if (breaks && builder.HasContent && !inRuby)
                        {
                            breakPoint = builder.Capture();
                            tail.Clear();
                            following = tail;
                        }

                        // A line that ran past its last opportunity may have to give back what came
                        // after it: this step cannot fit where it stands and nothing here lets the line
                        // end, so the line ends at the opportunity it last passed and the steps since it
                        // begin the next line. Chrome measured: "X XX " in a four-character box ends
                        // before the space rather than overflowing the line, and "high-shy-wa" then "y"
                        // breaks at the hyphen rather than letting the y overflow the word it completes
                        // (CSS Text 3 §5.1 - a line ends at an opportunity, not where the room runs out).
                        // A collapsible space is not given back: it hangs at the end of the line instead.
                        // A preserved space is worth an earlier line only when something else follows it:
                        // a space at the very end of the text overflows with the word it trails, because
                        // moving it down would open a line nobody asked for.
                        var followed = at + taken < run.Source.Length &&
                            !TextChunker.IsCollapsible(run.Source, at + taken, 1);

                        if (breakPoint is { } point && !breaks && builder.HasContent && !inRuby &&
                            advance > 0 && !String.IsNullOrEmpty(item.Text) &&
                            (item.Style.WrapsLines || BeginsHere(builder, item.Inline)) &&
                            (!spaces || (item.Style.WrapsInsideSpaces && followed)) &&
                            builder.ExtentFor(item.Inline) + advance + closing + auto.BoundaryDelta - auto.RetroDelta - trim.BoundaryDelta - trim.RetroDelta - Math.Max(endGrace, hangGrace) > room.Room)
                        {
                            var replay = following;

                            builder.Restore(point);
                            NextLine(hyphenate: builder.EndsInSoftHyphen);
                            breakPoint = null;

                            if (replay is not null)
                            {
                                Replay(replay);
                            }
                        }

                        if (breaks && (room.Room > 0 || hyphen) &&
                            builder.ExtentFor(item.Inline) + asks + closing - trim.RetroDelta - Math.Max(endGrace, hangGrace) > room.Room &&
                            item.Style.WrapsLines)
                        {
                            // The mark a break draws is part of the line it ends: a shortened line that
                            // cannot hold it moves down first, the way any line too small for its content
                            // does (CSS 2.1 §9.5). Without this the line broke at the soft hyphen and
                            // overhung the float by the width of the mark.
                            if (hyphen && room.Shortened)
                            {
                                var trailing = builder.ExtentFor(item.Inline) +
                                    (builder.EndsInSoftHyphen ? HyphenWidth(_measurer, item.Style, itemFlow) : 0);

                                if (trailing > room.Room)
                                {
                                    room.Drop(trailing);
                                }
                            }

                            if (builder.ExtentFor(item.Inline) + asks + closing - trim.RetroDelta - Math.Max(endGrace, hangGrace) > room.Room)
                            {
                                if (builder.HasContent)
                                {
                                    NextLine(hyphenate: hyphen);
                                    moved = false;
                                }
                                else if (room.Shortened)
                                {
                                    // CSS 2.1 §9.5: a line box a float has shortened, too small for the
                                    // content standing on it, is shifted downward until some content fits
                                    // or no floats are left - the long word beside the floated picture goes
                                    // below it rather than overflowing the room the float left, which is
                                    // the specification's own example of the rule.
                                    room.Drop(builder.ExtentFor(item.Inline) + asks - trim.RetroDelta - Math.Max(endGrace, hangGrace));
                                }
                            }
                        }

                        builder.Add(run.Source, at, taken, advance, item.Style, item.Box, piece.Flow, item.Inline);

                        // The stop or comma the line was measured without is only without it while it
                        // ends the line; the next thing put there is asked for its room (CSS Text 3
                        // §8.2.1).
                        if (hangGrace > 0)
                        {
                            pendingGrace = hangGrace;
                        }

                        // The steps taken after the last opportunity are kept beside it, so the line
                        // after the break can begin with them.
                        if (breakPoint is not null && following is not null)
                        {
                            following.Add(new TailStep(
                                run.Source, at, taken, advance, item.Style, item.Box, piece.Flow, item.Inline));
                        }

                        previous = at;
                        at += taken;
                    }
                }
            }
        }

        // Closed for any fragment rather than for any content: a line whose only fragment is a box that
        // left the flow draws nothing, but that box still has to be placed.
        if (builder.HasFragments)
        {
            Close(builder);
        }

        SettleNotesAtLineEnds(lines);

        // Which inline elements the lines hand on is settled before the edges are: an element that
        // ends at a line's end is not one the line cut, and the glyph standing there is the last of
        // its element's last formatted line.
        MarkSlices(lines, maxInline);

        if (settleEdges)
        {
            SettleLines(lines);
        }

        return lines;
    }

    /// <summary>
    /// Turns the zero-width interline-note anchors left on body lines into one note stream whose lines
    /// share the body's interlinear slots. A note line that fills up continues in the slot of the next
    /// body line rather than adding another line in front of the same one.
    /// </summary>
    private List<Line> AddInterlineNotes(
        Box container,
        List<Line> bodyLines,
        Double maxInline,
        WritingModeInfo flow,
        InlineContext context)
    {
        var anchors = new List<(Int32 Slot, InterlineNoteSource Note, Double Position)>();
        var slotTops = new Double[bodyLines.Count];
        var slotExtents = new Double[bodyLines.Count];
        var minimumStarts = new Double[bodyLines.Count];
        var top = 0.0;

        for (var bodyIndex = 0; bodyIndex < bodyLines.Count; bodyIndex++)
        {
            var bodyLine = bodyLines[bodyIndex];
            top += bodyLine.GapBefore;
            slotTops[bodyIndex] = top;
            slotExtents[bodyIndex] = bodyLine.GapBefore + bodyLine.Height;
            minimumStarts[bodyIndex] = bodyIndex == 0 ? bodyLine.Indent : 0;

            var markers = bodyLine.Fragments
                .Where(fragment => fragment.InterlineNote is not null)
                .ToList();

            if (markers.Count > 0)
            {
                var bodyAlignment = BodyLineAlignment(container, bodyLine, bodyIndex, bodyLines.Count);

                if (bodyAlignment.Equals("justify", StringComparison.OrdinalIgnoreCase))
                {
                    bodyLine.Alignment = bodyAlignment;
                    DistributeSpace([bodyLine]);
                }

                var bodyOffset = BodyLineInlineOffset(container, bodyLine, bodyIndex, bodyLines.Count);

                foreach (var marker in markers)
                {
                    anchors.Add((
                        bodyIndex,
                        marker.InterlineNote!,
                        InterlineAnchorPosition(bodyLine, marker, bodyOffset)));
                }

                bodyLine.Fragments.RemoveAll(fragment => fragment.InterlineNote is not null);
            }

            top += bodyLine.Height;
        }

        if (anchors.Count == 0)
        {
            return bodyLines;
        }

        var noteLines = BuildInterlineFlow(
            container,
            anchors,
            maxInline,
            flow,
            context,
            slotTops,
            slotExtents,
            minimumStarts,
            anchors[0].Slot,
            bodyLines);
        var bySlot = new Dictionary<Int32, List<Line>>();

        foreach (var noteLine in noteLines)
        {
            var slot = Math.Clamp(noteLine.SlotIndex, 0, bodyLines.Count - 1);
            noteLine.SlotIndex = slot;

            if (!bySlot.TryGetValue(slot, out var slotLines))
            {
                bySlot.Add(slot, slotLines = new List<Line>());
            }

            slotLines.Add(noteLine);
        }

        var lines = new List<Line>(bodyLines.Count);

        for (var bodyIndex = 0; bodyIndex < bodyLines.Count; bodyIndex++)
        {
            var bodyLine = bodyLines[bodyIndex];
            if (bySlot.TryGetValue(bodyIndex, out var notes) && notes.Count > 0)
            {
                foreach (var noteLine in notes)
                {
                    foreach (var marker in noteLine.Fragments
                        .Where(fragment => fragment.MarginNote is not null)
                        .ToList())
                    {
                        bodyLine.Fragments.Add(marker);
                        noteLine.Fragments.Remove(marker);
                    }

                    lines.Add(noteLine);
                }

                notes[^1].KeepWithNext = true;
                bodyLine.GapBefore -= InterlineBodyAdjustment(container, bodyLine, notes[^1]);
            }

            lines.Add(bodyLine);
        }

        return lines;
    }

    /// <summary>Gets the inline coordinate at which an interline-note anchor stands on its body line.</summary>
    private Double InterlineAnchorPosition(Line line, TextFragment marker, Double alignment)
    {
        if (marker.InterlinePosition is { } positioned)
        {
            return line.Left + alignment + positioned;
        }

        var open = new List<OpenInline>();
        var at = line.Indent;

        foreach (var fragment in line.Fragments)
        {
            at += Advance(open, fragment.Inline, line.ContinuesFromPrevious, at, apply: true, step: null);

            if (ReferenceEquals(fragment, marker))
            {
                return line.Left + alignment + at;
            }

            at += fragment.Width;
        }

        return line.Left + alignment + at;
    }

    /// <summary>Gets the inline offset the body line's own alignment gives it.</summary>
    private static Double BodyLineInlineOffset(
        Box container,
        Line line,
        Int32 index,
        Int32 count)
    {
        var alignment = BodyLineAlignment(container, line, index, count);

        return AlignmentOffset(
            alignment,
            Math.Max(0, line.Extent - line.Indent),
            line.Used,
            line.Fragments.FirstOrDefault()?.Flow ?? container.Flow);
    }

    private static String BodyLineAlignment(Box container, Line line, Int32 index, Int32 count)
    {
        var style = line.Governing ?? container.Style;
        var last = index == count - 1 || line.ForceEnded;

        return last
            ? (style.TextAlignLast.Equals("auto", StringComparison.OrdinalIgnoreCase)
                ? (style.TextAlign.Equals("justify", StringComparison.OrdinalIgnoreCase)
                    ? "start"
                    : style.TextAlign)
                : style.TextAlignLast)
            : style.TextAlign;
    }

    /// <summary>
    /// Gets the leading the note and body lines share at their boundary, which is removed from the
    /// ordinary gap between their two line boxes so the note stands nearer to the body it annotates.
    /// </summary>
    private Double InterlineBodyAdjustment(Box container, Line bodyLine, Line noteLine)
    {
        var noteStyle = noteLine.Governing ??
            noteLine.Fragments.LastOrDefault(fragment => !fragment.InterlineGap)?.Style ??
            container.Style;

        return InterlineBodyAdjustment(bodyLine, noteStyle);
    }

    /// <summary>
    /// Gets how far the body line moves toward its interline note. Without an inline note this is the
    /// smaller of the two half-leadings; with one, the effective body boundary is the first subline's
    /// block-start edge and that boundary is overlapped by the two half-leadings' smaller value.
    /// </summary>
    private Double InterlineBodyAdjustment(Line bodyLine, ComputedStyle noteStyle)
    {
        var noteGap = HalfLeading(noteStyle);
        var inline = InlineNoteFirstAdjustment(bodyLine, noteGap);

        if (inline is { } adjustment)
        {
            return adjustment;
        }

        var bodyStyle = bodyLine.Governing ??
            bodyLine.Fragments.FirstOrDefault()?.Style ??
            noteStyle;
        return Math.Min(HalfLeading(bodyStyle), noteGap);
    }

    /// <summary>
    /// Gets the line movement needed to put an interline note against the nearest inline-note first
    /// subline, including the centring and block decorations between the body edge and that subline.
    /// </summary>
    private Double? InlineNoteFirstAdjustment(Line bodyLine, Double noteGap)
    {
        Double? adjustment = null;

        foreach (var fragment in bodyLine.Fragments)
        {
            if (fragment.Note is not { First: { } first } note)
            {
                continue;
            }

            var style = first.Governing ??
                first.Fragments.FirstOrDefault(row => row.Length > 0)?.Style ??
                fragment.Style;
            var (blockMarginStart, _) = note.Flow.BlockMargins(note.Margin);
            var (blockPaddingStart, _) = note.Flow.BlockMargins(note.Padding);
            var (blockBorderStart, _) = note.Flow.BlockMargins(note.Border);
            var boundary =
                ((bodyLine.Height - note.BlockSize) / 2) +
                blockMarginStart +
                blockBorderStart +
                blockPaddingStart;
            var overlap = Math.Min(HalfLeading(style), noteGap);
            var current = boundary + overlap;

            adjustment = adjustment is { } existing ? Math.Min(existing, current) : current;
        }

        return adjustment;
    }

    /// <summary>Gets the space a line's leading adds above or below its glyphs.</summary>
    private Double HalfLeading(ComputedStyle style)
    {
        var font = style.GetFont();
        return Math.Max(0, (LineBoxHeight(style, font) - style.FontSize) / 2);
    }

    /// <summary>
    /// Builds the shared note flow in DOM order. Each line fills the interlinear slot of one body line;
    /// when it fills up, the remaining text continues in the next slot rather than adding a second note
    /// line in front of the same body line.
    /// </summary>
    private List<Line> BuildInterlineFlow(
        Box container,
        List<(Int32 Slot, InterlineNoteSource Note, Double Position)> anchors,
        Double maxInline,
        WritingModeInfo flow,
        InlineContext context,
        IReadOnlyList<Double> slotTops,
        IReadOnlyList<Double> slotExtents,
        IReadOnlyList<Double> minimumInlineStarts,
        Int32 firstSlot,
        IReadOnlyList<Line> slotBodyLines)
    {
        var items = new List<InlineItem>();
        ComputedStyle? strut = null;

        foreach (var (slot, note, position) in anchors)
        {
            var root = new InlineAncestor(note.Box, null, maxInline, flow, _measurer, noteContent: true);
            var noteItems = NoteItems(
                note.Content,
                0,
                note.Content.Text.Length,
                maxInline,
                flow,
                upright: false,
                root: root);

            items.Add(InlineItem.CreateInterlineAnchor(slot, position, note.Box));

            foreach (var item in noteItems)
            {
                items.Add(item);
            }

            strut ??= note.Box.Style;
        }

        if (items.Count == 0 || strut is null)
        {
            return new List<Line>();
        }

        var lineStrut = strut ?? container.Style;
        var noteLines = BuildLines(
            items,
            maxInline,
            flow,
            context,
            lineStrut,
            emergency: false,
            singleLine: false,
            settleEdges: true,
            blockStart: slotTops[firstSlot],
            slotExtents: slotExtents,
            minimumInlineStarts: minimumInlineStarts,
            initialSlot: firstSlot,
            slotBodyLines: slotBodyLines);
        var justify = container.Style.TextAlign.Equals("justify", StringComparison.OrdinalIgnoreCase);

        for (var index = 0; index < noteLines.Count; index++)
        {
            var line = noteLines[index];
            line.IsInterlineNote = true;
            line.Alignment = justify && index < noteLines.Count - 1 && !line.ForceEnded
                ? "justify"
                : "start";
            line.Offset = Aligned(line, maxInline);
        }

        return noteLines;
    }

    /// <summary>
    /// Settles the sublines of notes that are the last in-flow content of an outer line. A note in
    /// the middle of a line keeps its natural width: justification and hanging punctuation describe a
    /// line edge, and the note has not reached one there.
    /// </summary>
    private void SettleNotesAtLineEnds(List<Line> lines)
    {
        for (var lineIndex = 0; lineIndex < lines.Count; lineIndex++)
        {
            var line = lines[lineIndex];
            TextFragment? last = null;
            var hasVisible = false;
            var allNotes = true;

            for (var index = line.Fragments.Count - 1; index >= 0; index--)
            {
                var fragment = line.Fragments[index];

                if (fragment.OutOfFlow)
                {
                    continue;
                }

                if (!HasInk(fragment))
                {
                    continue;
                }

                hasVisible = true;
                allNotes &= fragment.Note is not null;
                last ??= fragment;
            }

            if (last?.Note is not { } note)
            {
                continue;
            }

            // A line whose visible content is entirely notes is a note layout line even when its
            // naturally ending fragment leaves some trailing room. That trailing room is outside
            // the note, just as the remaining part of a line box is outside a short final line.
            var fullNoteLine = note.ReachedLineEnd || (hasVisible && allNotes);
            note.EndsOuterLine = fullNoteLine;

            var rows = new List<Line>(2);

            if (note.First is { } first)
            {
                rows.Add(first);
            }

            if (note.Second is { } second)
            {
                rows.Add(second);
            }

            foreach (var row in rows)
            {
                SettleLine(
                    row,
                    firstLine: true,
                    lastLine: fullNoteLine || lineIndex == lines.Count - 1,
                    forceLineEnd: fullNoteLine);
            }

            var naturalContent = rows.Count == 0 ? 0 : rows.Max(row => row.Used);
            var alignLast = last.Style.TextAlignLast;
            var lastSpokenFor = lineIndex == lines.Count - 1 || line.ForceEnded;
            var justified = alignLast.Equals("justify", StringComparison.OrdinalIgnoreCase) ||
                (!lastSpokenFor &&
                    last.Style.TextAlign.Equals("justify", StringComparison.OrdinalIgnoreCase));

            foreach (var row in rows)
            {
                row.Left = 0;
                row.Indent = 0;
                row.Extent = naturalContent;
                row.Offset = 0;

                if (justified)
                {
                    row.Alignment = "justify";
                }
            }

            var grown = naturalContent + note.InlineEdges;
            line.Used += grown - last.Width;
            last.Width = grown;
            line.Offset = Aligned(line, line.Extent);
        }
    }

    /// <summary>The side of a line a fragment hangs from: the line's start edge, or its end edge.</summary>
    private const Int32 HangStart = 1;

    private const Int32 HangEnd = 2;

    /// <summary>
    /// Settles what stands past each line's edges once every line is built (CSS Text 3 §8.2.1,
    /// §8.2): a closing punctuation either gives up half its blank at the line's end
    /// (<c>text-spacing-trim</c>) or hangs there whole, and an opening glyph on an element's first
    /// formatted line is drawn before the line begins. Hanging is what a value of
    /// <c>hanging-punctuation</c> reaches; the glyph keeps its ink and gives up its advance, which is
    /// the negative margin on the edge of its parent inline box the specification describes.
    /// </summary>
    /// <remarks>
    /// At most one character hangs at each edge, a non-zero border or padding between the character
    /// and the edge keeps it from hanging, and a glyph that hangs leaves nothing for the trim to give
    /// up: it is outside the room the line was measured at, so the line does not overflow because of
    /// it and the trim would only narrow it.
    /// </remarks>
    private void SettleLines(List<Line> lines)
    {
        for (var index = 0; index < lines.Count; index++)
        {
            SettleLine(
                lines[index],
                firstLine: index == 0,
                lastLine: index == lines.Count - 1,
                forceLineEnd: false);
        }
    }

    /// <summary>Settles the edges of one line with the line boundaries its surrounding block gives it.</summary>
    private void SettleLine(Line line, Boolean firstLine, Boolean lastLine, Boolean forceLineEnd)
    {
        // The opening glyph hangs first: it stands outside the line, so the room the line has is
        // what the rest of the line measures into - and the trim at the end of the line asks
        // whether the line overflows on what is left.
        var opening = HangAtStart(line, firstLine);
        var used = line.Used - opening;
        var hung = HangAtEnd(line, lastLine, used, forceLineEnd);
        var trimmed = 0.0;

        if (hung > 0)
        {
            line.Used = used - hung;
        }
        else
        {
            trimmed = TrimLineEnd(line, line.Extent, used);
            line.Used = used - trimmed;
        }

        if (hung > 0 || opening > 0 || trimmed > 0)
        {
            line.Offset = Aligned(line, line.Extent);
        }
    }

    /// <summary>
    /// Takes the room a closing punctuation gives up at the end of the line. <c>trim-end</c> always
    /// takes it; <c>allow-end</c> only where the line would not otherwise fit. The room of the last
    /// visible character is what changes; its ink does not move, because the blank it gives up lies
    /// after it (CSS Text 4 §8.5.2).
    /// </summary>
    private Double TrimLineEnd(Line line, Double maxInline, Double used)
    {
        for (var index = line.Fragments.Count - 1; index >= 0; index--)
        {
            var fragment = line.Fragments[index];

            if (fragment.OutOfFlow || fragment.InterlineGap ||
                fragment.InterlineNote is not null || fragment.MarginNote is not null)
            {
                continue;
            }

            if (fragment.IsReplaced || fragment.Atomic is not null || fragment.Ruby is not null)
            {
                return 0;
            }

            var visible = fragment.Length - fragment.Hanging;

            if (visible <= 0)
            {
                // An empty box the line ends at stands in the way of the trim as it does of the
                // hang: its border is the edge the punctuation would have to cross.
                if (CarriesAnInlineEdge(fragment.Inline))
                {
                    return 0;
                }

                continue;
            }

            var codePoint = CodePointBefore(fragment.Source, fragment.Start + visible);
            var kind = KindOf(fragment.Style, codePoint);
            var value = fragment.Style.TextSpacingTrim;

            if (kind == PunctuationClass.None ||
                value.All ||
                TextSpacingTrim.HasExclusiveFeature(fragment.Style.ResolvedFont.Features) ||
                !IsFullwidthPunctuation(_measurer, fragment.Style, codePoint, fragment.Flow))
            {
                return 0;
            }

            var overflows = used > maxInline - line.Indent;

            if (!TextSpacingTrim.TrimsAtLineEnd(value, kind, overflows))
            {
                return 0;
            }

            var at = visible - 1;
            var chars = (fragment.Trims ?? new String('\0', fragment.Length)).ToCharArray();

            if (at >= chars.Length || chars[at] != '\0')
            {
                return 0;
            }

            chars[at] = TrimClosing;
            fragment.Trims = MaskOf(chars);

            var delta = TrimDelta(_measurer, fragment.Style, codePoint, fragment.Flow);
            fragment.Width -= delta;

            return delta;
        }

        return 0;
    }

    /// <summary>
    /// Gets whether a fragment draws something at a line's edge. A run whose characters all hang -
    /// the spaces a <c>pre-wrap</c> line trails into - draws nothing, and neither does a box that left
    /// the flow; a zero-length inline box with a border draws only its edge, which still stands in
    /// the way of anything hanging past it.
    /// </summary>
    private static Boolean HasInk(TextFragment fragment) =>
        !fragment.InterlineGap &&
        fragment.InterlineNote is null &&
        fragment.MarginNote is null &&
        (fragment.IsReplaced ||
            fragment.Atomic is not null ||
            fragment.Ruby is not null ||
            fragment.Note is not null ||
            fragment.Length > fragment.Hanging ||
            (fragment.Length == 0 && (fragment.Width > 0 || CarriesAnInlineEdge(fragment.Inline))));

    /// <summary>Hangs the line's opening glyph where the property reaches it, and answers with its advance.</summary>
    private Double HangAtStart(Line line, Boolean firstLine)
    {
        for (var index = 0; index < line.Fragments.Count; index++)
        {
            var fragment = line.Fragments[index];

            if (fragment.OutOfFlow || !HasInk(fragment))
            {
                continue;
            }

            if (fragment.IsReplaced || fragment.Atomic is not null || fragment.Ruby is not null ||
                fragment.Length - fragment.Hanging <= 0 || fragment.HangSide != 0)
            {
                return 0;
            }

            // The glyph is at the start of an element's first formatted line: the block's own text
            // reaches it on the block's first line, and an inline element's text on the line it
            // begins on (CSS Text 3 §8.2.1).
            var first = fragment.Inline is null
                ? firstLine
                : line.ContinuesFromPrevious?.Contains(fragment.Inline) != true;

            if (!first || !HangingPunctuation.HangsAtStart(fragment.Style.HangingPunctuation))
            {
                return 0;
            }

            var units = TextClusters.UnitsAt(fragment.Source, fragment.Start, fragment.Start + fragment.Length - fragment.Hanging);

            if (!HangingPunctuation.IsOpening(TextChunker.CodePointAt(fragment.Source, fragment.Start)))
            {
                return 0;
            }

            if (EdgeBlocked(line.ContinuesFromPrevious, fragment.Inline, start: true))
            {
                return 0;
            }

            return Hang(line, fragment, fragment.Start, units, HangStart);
        }

        return 0;
    }

    /// <summary>Hangs the line's closing glyph where the property reaches it, and answers with its advance.</summary>
    private Double HangAtEnd(Line line, Boolean lastLine, Double used, Boolean forceLineEnd)
    {
        for (var index = line.Fragments.Count - 1; index >= 0; index--)
        {
            var fragment = line.Fragments[index];

            if (fragment.OutOfFlow || !HasInk(fragment))
            {
                continue;
            }

            if (fragment.IsReplaced || fragment.Atomic is not null || fragment.Ruby is not null ||
                fragment.Length - fragment.Hanging <= 0 || fragment.HangSide != 0)
            {
                return 0;
            }

            // The glyph is at the end of an element's last formatted line: the block's own text
            // reaches it on the block's last line, and an inline element's text on the line it ends
            // on - which a cut element has not reached yet (CSS Text 3 §8.2.1).
            var last = fragment.Inline is null
                ? lastLine
                : line.ContinuesToNext?.Contains(fragment.Inline) != true;

            var stop = fragment.Start + fragment.Length - fragment.Hanging;

            // White space the line trails into is not drawn, so the glyph it hangs is the last one
            // before it.
            while (stop > fragment.Start)
            {
                var before = TextClusters.ClusterStart(fragment.Source, stop);

                if (!TextChunker.IsCollapsible(fragment.Source, before, stop - before))
                {
                    break;
                }

                stop = before;
            }

            if (stop <= fragment.Start)
            {
                return 0;
            }

            var start = TextClusters.ClusterStart(fragment.Source, stop);
            var overflows = forceLineEnd || used > line.Extent - line.Indent;
            var hangs = HangingPunctuation.HangsAtEnd(
                fragment.Style.HangingPunctuation,
                TextChunker.CodePointAt(fragment.Source, start),
                overflows,
                last);

            if (!hangs || EdgeBlocked(line.ContinuesToNext, fragment.Inline, start: false))
            {
                return 0;
            }

            return Hang(line, fragment, start, stop - start, HangEnd);
        }

        return 0;
    }

    /// <summary>
    /// Gets whether nothing but white space a line collapses stands from a position to the end of the
    /// text: the glyph before that position is the last the line draws.
    /// </summary>
    private static Boolean OnlySpaceRemains(String text, Int32 at)
    {
        for (var index = at; index < text.Length;)
        {
            var units = TextClusters.UnitsAt(text, index, text.Length);

            if (!TextChunker.IsCollapsible(text, index, units))
            {
                return false;
            }

            index += units;
        }

        return true;
    }

    /// <summary>
    /// Gets the last character the content draws, as the item it stands in and the position its run
    /// ends at - or item <c>-1</c> where the content's last line draws no character at all: a forced
    /// break at the end leaves an empty last line, and a box draws no character.
    /// </summary>
    private static (Int32 Item, Int32 End) LastDrawnContent(List<InlineItem> items)
    {
        for (var index = items.Count - 1; index >= 0; index--)
        {
            var item = items[index];

            if (item.IsOutOfFlow)
            {
                continue;
            }

            if (item.IsBreak ||
                item.IsReplaced ||
                item.IsAtomic ||
                item.IsRuby ||
                item.IsEmpty)
            {
                return (-1, -1);
            }

            if (item.Text is not { Length: > 0 } text)
            {
                continue;
            }

            var at = text.Length;

            while (at > 0)
            {
                var start = TextClusters.ClusterStart(text, at);

                if (!TextChunker.IsCollapsible(text, start, at - start))
                {
                    return (index, at);
                }

                at = start;
            }
        }

        return (-1, -1);
    }

    /// <summary>
    /// Gets whether a non-zero border or padding of an element the run sits in will stand between the
    /// glyph at its end and the line's end edge: the element's end edge is drawn on this line only
    /// when nothing after the run belongs to it, so an element the line cuts does not stand in the way.
    /// </summary>
    private static Boolean EndEdgeOnLine(List<InlineItem> items, Int32 index, InlineAncestor? inline)
    {
        for (var ancestor = inline; ancestor is not null; ancestor = ancestor.Parent)
        {
            if (ancestor.InlineEndInset <= 0)
            {
                continue;
            }

            var ends = true;

            for (var at = index + 1; at < items.Count && ends; at++)
            {
                for (var chain = items[at].Inline; chain is not null; chain = chain.Parent)
                {
                    if (ReferenceEquals(chain, ancestor))
                    {
                        ends = false;
                        break;
                    }
                }
            }

            if (ends)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Gets whether a non-zero border or padding stands between a glyph and the line's edge on the
    /// side it would hang from. An edge the element was cut at belongs to another line, so it is not
    /// in the way (CSS Text 3 §8.2.1).
    /// </summary>
    private static Boolean EdgeBlocked(HashSet<InlineAncestor>? cut, InlineAncestor? inline, Boolean start)
    {
        for (var ancestor = inline; ancestor is not null; ancestor = ancestor.Parent)
        {
            if (cut?.Contains(ancestor) == true)
            {
                continue;
            }

            if ((start ? ancestor.InlineStartInset : ancestor.InlineEndInset) > 0)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Cuts one character off the edge of a run and marks it as hanging from <paramref name="side"/>,
    /// giving up its advance. The room is measured at the character itself, so a punctuation set in a
    /// smaller font hangs by its own width rather than the line's.
    /// </summary>
    private Double Hang(Line line, TextFragment fragment, Int32 start, Int32 length, Int32 side)
    {
        var width = MeasureChunk(_measurer, fragment.Style, fragment.Source, start, length, fragment.Flow);

        var hung = new TextFragment(
            fragment.Source,
            start,
            length,
            width,
            fragment.Style,
            fragment.Box,
            fragment.Flow,
            fragment.Inline)
        {
            RightToLeft = fragment.RightToLeft,
            Level = fragment.Level,
            BaselineShift = fragment.BaselineShift,
            HangSide = side,
            Trims = MaskOf(fragment.Trims, start - fragment.Start, length, false),
            AutoSpaces = MaskOf(fragment.AutoSpaces, start - fragment.Start, length, false),
        };

        var at = line.Fragments.IndexOf(fragment);
        var rest = fragment.Length - length;

        if (side == HangStart)
        {
            fragment.Trims = MaskOf(fragment.Trims, length, rest, false);
            fragment.AutoSpaces = MaskOf(fragment.AutoSpaces, length, rest, false);
            fragment.TrimStart(length);
            fragment.Width -= width;
            line.Fragments.Insert(at, hung);
        }
        else
        {
            var kept = start - fragment.Start;

            fragment.Trims = MaskOf(fragment.Trims, 0, kept, false);
            fragment.AutoSpaces = MaskOf(fragment.AutoSpaces, 0, kept, false);
            fragment.Length = kept;
            fragment.Width -= width;
            line.Fragments.Insert(at + 1, hung);
        }

        return width;
    }

    /// <summary>
    /// Gets whether the stepping could cut the run anywhere inside it, which is what the line asks before
    /// it fills its remaining room with the head of a run that will not fit.
    /// </summary>
    /// <remarks>
    /// The two break values that license a cut answer for the run themselves: <c>line-break: anywhere</c>
    /// sets the rules aside and <c>overflow-wrap: anywhere</c> reaches every character of a word, so every
    /// boundary inside it is open. <c>word-break: break-all</c> is the one that leaves them standing, and
    /// it gives nothing to a run the rules hold together - a period takes no break before itself and none
    /// between its own marks ([UAX14] LB13), so "X.." is one unit however the page spells its break value,
    /// which is how Chrome lays it out (CSS Text 3 §5.6).
    /// </remarks>
    /// <param name="source">The text the run was cut from.</param>
    /// <param name="start">The index the run begins at.</param>
    /// <param name="length">How many characters the run holds.</param>
    /// <param name="style">The style the run is set in, which is what gives the cut its licence.</param>
    private static Boolean CutsInside(String source, Int32 start, Int32 length, ComputedStyle style)
    {
        var end = start + length;
        var previous = start;
        var at = start + TextClusters.UnitsAt(source, start, end);

        while (at < end)
        {
            // The same answer the walk gives at a boundary inside the run, read for every one of them.
            if ((style.WrapsInsideSpaces && TextChunker.IsBreakSpace(source[previous])) ||
                !style.BreaksBetweenLetters || style.BreaksAnywhere || style.CountsLongWordBreaks ||
                LineBreaks.MayBreak(source, previous, at, style.WordBreakingRules))
            {
                return true;
            }

            previous = at;
            at += TextClusters.UnitsAt(source, at, end);
        }

        return false;
    }

    /// <summary>
    /// Works out which lines cut an inline element in half. An element a line hands on to the next one
    /// was cut there, so that line draws none of the element's end edge and the line that resumes it
    /// draws none of its start edge - those two edges belong where the element began and where it ends.
    /// </summary>
    /// <remarks>
    /// The extent a line takes had to be settled before this could be known, so an element that turns
    /// out not to carry on has the end edge it was given taken back off the line's extent here.
    /// </remarks>
    private static void MarkSlices(List<Line> lines, Double maxInline)
    {
        for (var index = 0; index < lines.Count; index++)
        {
            var line = lines[index];

            if (line.ContinuesToNext is not { Count: > 0 } pending)
            {
                continue;
            }

            // Nothing carries on past the last line, so an element the content ends inside ends there.
            var next = index + 1 < lines.Count ? Inlines(lines[index + 1]) : new HashSet<InlineAncestor>();
            var dropped = 0.0;

            foreach (var ancestor in pending)
            {
                if (!next.Contains(ancestor))
                {
                    dropped += ancestor.InlineEndInset;
                }
            }

            pending.IntersectWith(next);
            line.Used -= dropped;

            if (pending.Count == 0)
            {
                line.ContinuesToNext = null;
                continue;
            }

            // The line is aligned on the extent it ends up with, which may have shrunk by the edge an
            // element that stops here no longer draws twice.
            line.Offset = Aligned(line, maxInline);
            lines[index + 1].ContinuesFromPrevious = pending;
        }
    }

    /// <summary>Gets the inline elements a line carries a run of, at any depth.</summary>
    private static HashSet<InlineAncestor> Inlines(Line line)
    {
        var inlines = new HashSet<InlineAncestor>();

        foreach (var fragment in line.Fragments)
        {
            for (var ancestor = fragment.Inline; ancestor is not null; ancestor = ancestor.Parent)
            {
                inlines.Add(ancestor);
            }
        }

        return inlines;
    }

    /// <summary>
    /// Settles the padding and border that stepping from one chain of inline elements to another adds
    /// along the inline axis, and reports what it adds. The two chains are compared from the outside
    /// in, so the elements they share are left standing; the ones the step leaves behind give up their
    /// end edge and the ones it enters take up their start edge.
    /// </summary>
    /// <param name="open">
    /// The elements the step starts from, outermost first. The step leaves it holding the ones it ends
    /// with, which is what keeps the caller's picture of what is open in step with the runs it places.
    /// </param>
    /// <param name="chain">The innermost element the run sits inside, or null when it sits in none.</param>
    /// <param name="carried">
    /// The elements the line resumed inside. Their start edge was drawn on the line the element began
    /// on, so nothing is charged for it again here.
    /// </param>
    /// <param name="origin">Where the step is taken, which is the offset the elements it enters begin at.</param>
    /// <param name="apply">
    /// Whether to move the open elements to the new chain as well as to price the move. The wrap test
    /// asks for the price alone: a run that does not fit goes on to the next line, and it has entered
    /// nothing until it is placed.
    /// </param>
    /// <param name="step">Receives the elements the step left and the edges it charged for, or null when only the extent is wanted.</param>
    private static Double Advance(
        List<OpenInline> open,
        InlineAncestor? chain,
        HashSet<InlineAncestor>? carried,
        Double origin,
        Boolean apply,
        InlineStep? step)
    {
        // Almost every run in a document sits in no inline element at all, which costs nothing.
        if (chain is null && open.Count == 0)
        {
            return 0;
        }

        // Outermost first, so that this chain and the open one line up and can be compared from the
        // outside in. A step only happens where an element begins or ends, so this is rare.
        var target = new List<InlineAncestor>();

        for (var ancestor = chain; ancestor is not null; ancestor = ancestor.Parent)
        {
            target.Add(ancestor);
        }

        target.Reverse();

        var shared = 0;

        while (shared < open.Count && shared < target.Count &&
               ReferenceEquals(open[shared].Ancestor, target[shared]))
        {
            shared++;
        }

        var added = 0.0;
        var position = origin;

        // The elements the run leaves are closed from the inside out, so that the edge of the one
        // wrapped around the others is drawn outside theirs. Each ends its box at the padding and
        // border it was measured with, and then takes its end margin, which is room on the line rather
        // than part of the box - the edge the next run starts after.
        for (var index = open.Count - 1; index >= shared; index--)
        {
            var item = open[index];

            step?.Closed.Add(item);
            added += item.Ancestor.InlineEndInset;
            position += item.Ancestor.InlineEndInset;
            added += item.Ancestor.InlineEndMargin;
            position += item.Ancestor.InlineEndMargin;
        }

        if (apply)
        {
            open.RemoveRange(shared, open.Count - shared);
        }

        for (var index = shared; index < target.Count; index++)
        {
            var ancestor = target[index];

            // An element the line resumed inside was cut at its start edge, which was drawn before the
            // break; only an element a line begins inside pays nothing for that edge here. Its margin
            // was charged at the start of the fragment the element began in too - a margin belongs to
            // the ends of the whole element, not to each piece a break cut it into.
            var resumed = !ancestor.Box.Style.ClonesDecoration &&
                carried?.Contains(ancestor) == true;
            var inset = resumed ? 0 : ancestor.InlineStartInset;
            var margin = resumed ? 0 : ancestor.InlineStartMargin;
            var item = new OpenInline(ancestor, position + margin, inset);

            step?.Opened.Add(item);

            if (apply)
            {
                open.Add(item);
            }

            added += margin + inset;
            position += margin + inset;
        }

        return added;
    }

    /// <summary>
    /// An inline element that is open at some point of a line, with the box the line gave it.
    /// </summary>
    /// <remarks>
    /// A context is broken into lines in one pass and its boxes are placed in another, and only the
    /// chain of elements and the arithmetic of walking it are shared between the two - the measuring
    /// pass has no boxes to hand and the placing pass has nothing else. Walking the chain in one place
    /// is what stops the extent a line is measured to from drifting away from the extent its boxes are
    /// placed at.
    /// </remarks>
    private sealed class OpenInline
    {
        public OpenInline(InlineAncestor ancestor, Double start, Double inset)
        {
            Ancestor = ancestor;
            Start = start;
            Inset = inset;
        }

        /// <summary>Gets the element.</summary>
        public InlineAncestor Ancestor { get; }

        /// <summary>Gets the offset along the inline axis the element's box begins at.</summary>
        public Double Start { get; }

        /// <summary>
        /// Gets the extent the step that entered the element charged for its start edge, which is
        /// nothing when the element was already open before the line began.
        /// </summary>
        public Double Inset { get; }

        /// <summary>Gets or sets the node the element draws through. Only the placing pass makes one.</summary>
        public LayoutBox? Node { get; set; }
    }

    /// <summary>
    /// The boxes one container's walk could not place when it met them, with the inline elements they
    /// were waiting for.
    /// </summary>
    /// <remarks>
    /// <para>
    /// An inline element whose <c>position</c> is not static is the containing block for the boxes flown
    /// out of its content, and the rectangle they resolve their offsets against is drawn from the start
    /// edges of the element's first fragment to the end edges of its last (CSS Positioned Layout 3 2.1).
    /// Neither end of that is known while a single line is being placed: the element may carry on past the
    /// line it stands on, or past the column. So a box that anchors to an inline element is held here till
    /// the walk has put down the last fragment, and an atomic inline standing under one is held with it,
    /// because the content it arranges flies to the same rectangle.
    /// </para>
    /// <para>
    /// The displacement a relative element takes is held back for the same reason. It carries everything
    /// drawn through the element, and a box arranged once the element had moved would ride on none of it -
    /// so the anchors are read from the places the fragments had before any of them moved, and the move
    /// then takes the held boxes along with the text, exactly as it takes the fragments themselves.
    /// </para>
    /// </remarks>
    /// <summary>The ordinary line flow belonging to one margin-note group.</summary>
    internal sealed class MarginNoteFlow
    {
        internal MarginNoteFlow(
            Box note,
            Object lines,
            WritingModeInfo flow,
            Double blockSize,
            InlineLayout owner)
        {
            _owner = owner;
            Note = note;
            _lines = (List<Line>)lines;
            Flow = flow;
            BlockSize = blockSize;
        }

        private readonly InlineLayout _owner;
        private readonly List<Line> _lines;

        public Box Note { get; }

        public WritingModeInfo Flow { get; }

        public Double BlockSize { get; }

        internal void Place(LayoutBox parent, Rect contentBox, InlineContext context)
        {
            var placement = new Placement(context.Engine);
            _owner.PlaceLines(
                Note,
                parent,
                _lines,
                0,
                _lines.Count,
                contentBox,
                Flow,
                context,
                0,
                placement);
            placement.Finish(Flow);
        }
    }

    private sealed class Placement
    {
        private readonly LayoutEngine _engine;
        private readonly Dictionary<InlineAncestor, Edges> _anchors = new();
        private readonly List<Held> _held = new();
        private readonly List<(LayoutBox Box, Offset Shift)> _shifts = new();

        public Placement(LayoutEngine engine) => _engine = engine;

        /// <summary>
        /// Notes one fragment of an inline element: the first one the walk hands over is kept as the start
        /// of the rectangle and every later one takes the end of it.
        /// </summary>
        public void Fragment(InlineAncestor ancestor, LayoutBox fragment)
        {
            if (!PositionedLayout.LendsAFrame(ancestor.Box, inlineLevel: true))
            {
                return;
            }

            var box = fragment.PaddingBox;

            _anchors[ancestor] = _anchors.TryGetValue(ancestor, out var seen)
                ? new Edges(seen.First, box)
                : new Edges(box, box);
        }

        /// <summary>Holds back the place a relative element's offset gives its whole subtree.</summary>
        public void Shift(LayoutBox fragment, Offset offset)
        {
            if (offset.X != 0 || offset.Y != 0)
            {
                _shifts.Add((fragment, offset));
            }
        }

        /// <summary>
        /// Holds a box that left the flow, with the place on the line it stood in - which is the static
        /// position an offset it does not declare falls back to.
        /// </summary>
        /// <param name="against">
        /// The context the box would have been placed against had the inline element above it not answered
        /// for it, which is where the offsets the element does not settle are still read from.
        /// </param>
        /// <param name="index">
        /// How many boxes already stood in <paramref name="owner"/> when the line came to this one, which
        /// is where it goes again so that the line reads in the order it was built in.
        /// </param>
        public void Hold(Box box, InlineAncestor anchor, LayoutBox owner, Int32 index, LayoutContext against, Offset stood, StaticLine? line = null) =>
            _held.Add(new Held(box, anchor, owner, index, against, stood, null, line));

        /// <summary>Holds an atomic inline box, with the frame the line gave it.</summary>
        /// <inheritdoc cref="Hold(Box, InlineAncestor, LayoutBox, Int32, LayoutContext, Offset)" path="/param[@name='against']"/>
        /// <inheritdoc cref="Hold(Box, InlineAncestor, LayoutBox, Int32, LayoutContext, Offset)" path="/param[@name='index']"/>
        public void Hold(Box box, InlineAncestor anchor, LayoutBox owner, Int32 index, LayoutContext against, Rect frame) =>
            _held.Add(new Held(box, anchor, owner, index, against, default, frame, null));

        /// <summary>Places every box that was held, then moves the fragments their offsets displace.</summary>
        public void Finish(WritingModeInfo flow)
        {
            var placed = new Dictionary<LayoutBox, Int32>();

            foreach (var held in _held)
            {
                // A filter holds the fixed ones behind it as well, the way it does on a block; a position
                // that is merely not static leaves them to the viewport (CSS Filter Effects 1 2.4).
                var frame = ContainingBlockOf(held.Anchor, flow);
                var against = held.Anchor.Box.Style.FormsContainingBlock(inlineLevel: true)
                    ? held.Against.WithCapturedContainingBlock(frame)
                    : held.Against.WithAbsoluteContainingBlock(frame);
                var node = held.Frame is { } area
                    ? _engine.Arrange(held.Box, area, against)
                    : _engine.ArrangePositioned(
                        held.Box,
                        held.Stood,
                        against,
                        line: held.Line,
                        staticFromHigh: flow.IsVertical && flow.BlockSign < 0,
                        staticFromInline: flow.IsVertical && flow.IsRightToLeft);

                // The siblings placed while the box waited are not the ones it stood among, so the place it
                // was counted at moves along by the boxes already put back into that owner.
                var before = placed.TryGetValue(held.Owner, out var seen) ? seen : 0;
                held.Owner.Children.Insert(Math.Min(held.Index + before, held.Owner.Children.Count), node);
                placed[held.Owner] = before + 1;
            }

            foreach (var (box, shift) in _shifts)
            {
                PositionedLayout.Translate(box, shift);
            }
        }

        /// <summary>
        /// Gets the rectangle an inline element's out-of-flow descendants resolve their offsets against:
        /// the first fragment's start edges to the last fragment's end edges, in the direction the
        /// container's text runs. Fragments on different lines leave nothing between their inline edges -
        /// an element broken over two lines gives a box that starts where its first fragment starts, which
        /// is what the rule comes to and what Chrome draws.
        /// </summary>
        private Rect ContainingBlockOf(InlineAncestor anchor, WritingModeInfo flow)
        {
            var (first, last) = _anchors[anchor];

            var inline = Math.Max(0,
                flow.InlineSign * flow.InlineStart(last) + flow.InlineExtent(last.Width, last.Height) -
                flow.InlineSign * flow.InlineStart(first));
            var block = Math.Max(0,
                flow.BlockSign * flow.BlockStart(last) + flow.BlockExtent(last.Width, last.Height) -
                flow.BlockSign * flow.BlockStart(first));

            return flow.Map(first, 0, inline, 0, block);
        }

        /// <summary>The padding boxes of the first and the last fragment an inline element left.</summary>
        private readonly record struct Edges(Rect First, Rect Last);

        /// <summary>
        /// A box the walk held back. <see cref="Stood"/> is what a box that left the flow is placed from,
        /// and <see cref="Frame"/> the frame an atomic inline was given - one of the two is always there,
        /// and which it is is answered by the frame standing empty.
        /// </summary>
        private readonly record struct Held(
            Box Box,
            InlineAncestor Anchor,
            LayoutBox Owner,
            Int32 Index,
            LayoutContext Against,
            Offset Stood,
            Rect? Frame,
            StaticLine? Line);
    }

    /// <summary>What one step between two chains of inline elements did to the elements it left and entered.</summary>
    /// <remarks>
    /// An element the step left draws its end edge, which is always the whole of it - a cut is what
    /// carrying on to the next line is, and nothing left in the middle of a line carries on anywhere.
    /// An element the step entered draws its start edge only as far as <see cref="OpenInline.Inset"/>
    /// says, which is nothing for one the line was already inside.
    /// </remarks>
    private sealed class InlineStep
    {
        /// <summary>Gets the elements the step left, innermost first, in the order their end edges are laid out.</summary>
        public List<OpenInline> Closed { get; } = new();

        /// <summary>Gets the elements the step entered, outermost first, in the order their start edges are laid out.</summary>
        public List<OpenInline> Opened { get; } = new();

        /// <summary>Empties the step so that it can be filled again.</summary>
        public void Clear()
        {
            Closed.Clear();
            Opened.Clear();
        }
    }

    /// <summary>
    /// Measures an inline-block: how much room it takes along the line, how far it reaches along the
    /// block axis, and where its baseline falls inside that.
    /// </summary>
    /// <param name="basis">
    /// The inline extent of the containing block, which is what a percentage among the box's margins,
    /// padding and border resolves against.
    /// </param>
    /// <param name="room">
    /// The room the box's line offers its margin box - negative when there is less than none - or
    /// positive infinity when the line has no definite extent to measure it against. It is the line's
    /// own room rather than what the cursor has left of it, because CSS 2.1 §10.3.9 shrinks the box to
    /// what the line can hold and leaves the line breaking to move a box that does not fit where the
    /// cursor stands down to the next line.
    /// </param>
    /// <remarks>
    /// The engine is handed the extent the box should come out at as the basis it is measured against,
    /// not as a size imposed on it: an <c>auto</c> width fills the extent it is measured against, so a
    /// box measured against the extent it should come out at comes out at that extent. Its own margins
    /// are part of that arithmetic, because they are already inside the extent the line reserved.
    /// <para>
    /// A column does not behave that way: its <c>auto</c> size is what the content asks for rather than
    /// the extent it is measured against, so handing it the room as a basis would not bound anything.
    /// The room left for the box's own content is given to it instead, and the box takes whatever its
    /// content then comes out at.
    /// </para>
    /// </remarks>
    private AtomicInline MeasureInlineBlock(Box box, Double basis, Double room, WritingModeInfo flow, InlineContext context)
    {
        var intrinsic = context.Engine.MeasureIntrinsic(box, context.Atomic);

        // The margins are kept in both directions: along the line they are part of what the line
        // reserves, and across it they are part of what the line grows by.
        var margin = box.Style.Margin.Resolve(basis);
        var (start, end) = flow.InlineMargins(margin);
        var (blockStart, blockEnd) = flow.BlockMargins(margin);
        var margins = start + end;

        // CSS2.1 §10.3.9: shrink-to-fit is the size the content prefers, brought down to what is
        // available and never taken below the size the content cannot go under. All three extents are
        // margin boxes, since that is what has to fit on the line.
        var shrink = Math.Min(intrinsic.MaxContent + margins, Math.Max(intrinsic.MinContent + margins, room));

        // Either way the box is measured against the extent it should come out at, because an `auto`
        // size fills what it is measured against however the line runs - and the box's own margins and
        // insets come off it inside the box model, which is what leaves a column content the room the
        // box was given rather than the room plus everything around it.
        var constraints = box.Flow.IsVertical
            ? MeasureConstraints.Unconstrained.WithHeight(shrink)
            : MeasureConstraints.FitWidth(shrink);

        var size = context.Engine.Measure(box, constraints, context.Atomic);

        return new AtomicInline(
            flow.InlineExtent(size.Width, size.Height) + margins,
            flow.BlockExtent(size.Width, size.Height),
            InlineBlockBaseline(box, size, shrink, flow, context),
            start,
            end,
            blockStart,
            blockEnd);
    }

    /// <summary>
    /// Gets where an inline-block's baseline falls, as a distance from the block-start edge of its
    /// border box.
    /// </summary>
    /// <remarks>
    /// CSS2.1 §10.8.1 makes the baseline that of the last line box in the box's normal flow, and the
    /// box's bottom margin edge when it has none - which is what a box holding block-level content has,
    /// since it never forms a line of its own to stand on. The lines are built here, from the same
    /// content and the same extent the arrangement will use, rather than read back off the arranged
    /// box, because the line the box sits on has to know how tall it is before anything is arranged.
    /// <para>
    /// A box set in a column, or standing in one, falls back to the bottom edge: the baseline of a line
    /// in that frame is measured from a different edge than the one the line here places the box
    /// against.
    /// </para>
    /// </remarks>
    private Double InlineBlockBaseline(Box box, Size size, Double basis, WritingModeInfo flow, InlineContext context)
    {
        // Answering the question can mean laying the box out, so the answer is kept for the rest of the
        // pass: a chain of boxes inside boxes would otherwise pay for the whole subtree again at every
        // step down it.
        var key = new BaselineKey(
            box, size.Width, size.Height, basis,
            context.Atomic.ContainingBlock.Width, context.Atomic.ContainingBlock.Height);

        if (_baselines.TryGetValue(key, out var held))
        {
            return held;
        }

        var answer = MeasureInlineBlockBaseline(box, size, basis, flow, context);
        _baselines[key] = answer;
        return answer;
    }

    private Double MeasureInlineBlockBaseline(Box box, Size size, Double basis, WritingModeInfo flow, InlineContext context)
    {
        // The box's baseline can only be carried to the line it stands on while the two are set the same
        // way: a box in a column stands on a line of its own frame, and the line here would be measuring
        // a baseline from an edge the box does not have one against. A box that holds blocks has no line
        // of its own to stand on either, and falls back to its end edge.
        if (flow.IsVertical != box.Flow.IsVertical)
        {
            return flow.BlockExtent(size.Width, size.Height);
        }

        // A flex or grid container has no line box of its own, but its first item does, and that item's
        // baseline is the container's: the item may be aligned to the start, the middle or the end of
        // the container's cross axis, and the container moves its baseline with it. Chrome places a
        // marker beside a list item that opens with an inline-flex on that baseline, not on the
        // container's end edge (CSS Flexbox 1 §8.5, CSS Grid 1 §11.1). A container that contains its
        // contents speaks only for its own box: the contents do not reach the line it stands on.
        if (!box.Flow.IsVertical && box.Kind is BoxKind.Flex or BoxKind.Grid or BoxKind.Masonry)
        {
            var flexPadding = box.Style.Padding.Resolve(basis);
            var flexBorder = BlockLayout.BorderOf(box);
            var contentBlock = Math.Max(0, size.Height - flexPadding.Vertical - flexBorder.Vertical);

            if (box.Style.Contain is not { Stated: true } && contentBlock > 0)
            {
                var container = context.Engine.Arrange(
                    box, new Rect(0, 0, size.Width, size.Height), context.Atomic);
                var first = FirstLineOfContainer(container);

                if (first is { } line)
                {
                    return BlockStartBaseline(
                        line, container.RelativeDisplacement, box.Flow, flow.BlockExtent(size.Width, size.Height));
                }
            }

            return flow.BlockExtent(size.Width, size.Height);
        }

        if (HasBlockChildren(box) || box.Children.Count == 0)
        {
            // A block child carries its own lines, and the last of them is the one CSS 2.1 §10.8.1
            // answers with: it may stand inside the child, three levels down, so it cannot be read from
            // the box's own inline content. The box is laid out for the question and the answer thrown
            // away - in an engine of its own, so the out-of-flow children the trial places are not
            // placed a second time when the box is arranged for real. A box whose overflow is clipped
            // has its content taken out of the question: §10.8.1 gives it the bottom margin edge.
            if (!box.Style.ClipsOverflow)
            {
                var probe = new LayoutEngine(_measurer);
                var arranged = probe.Arrange(
                    box, new Rect(0, 0, size.Width, size.Height), context.Atomic);

                if (LastLineInFlow(arranged, arranged.RelativeDisplacement) is { } found)
                {
                    return BlockStartBaseline(
                        found.Line, found.Displacement, flow, flow.BlockExtent(size.Width, size.Height));
                }
            }

            return flow.BlockExtent(size.Width, size.Height);
        }

        var style = box.Style;
        var padding = style.Padding.Resolve(basis);
        var border = BlockLayout.BorderOf(box);

        // Its own baseline is measured from the edge its lines begin at - the top of a row, and the right
        // of a column that runs leftward - and along the extent its text has to run along.
        var vertical = box.Flow.IsVertical;
        var start = vertical ? padding.Right + border.Right : padding.Top + border.Top;
        var contentInline = vertical
            ? Math.Max(0, size.Height - padding.Vertical - border.Vertical)
            : Math.Max(0, size.Width - padding.Horizontal - border.Horizontal);

        var items = new List<InlineItem>();
        Collect(box, items, box.Flow, null, contentInline);

        var lines = BuildLines(items, contentInline, box.Flow, context, style, Indentation(box, box.Flow, contentInline));

        if (lines.Count == 0)
        {
            // A box with nothing to set has no line of its own, so its baseline is the one its font
            // would have given it.
            return start + _measurer.GetMetrics(style.GetFont()).Ascent;
        }

        var baseline = start;

        for (var index = 0; index < lines.Count - 1; index++)
        {
            baseline += lines[index].Height;
        }

        return baseline + lines[^1].Ascent;
    }

    /// <summary>Gets whether a box's children include any that stack rather than forming lines.</summary>
    private static Boolean HasBlockChildren(Box box)
    {
        foreach (var child in box.Children)
        {
            if (child.IsBlockLevel)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Gets the line a flex or grid container stands on for its baseline: the first line an item of the
    /// container makes, read from the block-start edge of the container inward, and within one line the
    /// item at the main-start edge. A wrapping container may put the line its markup wrote first at the
    /// far edge - <c>wrap-reverse</c> does - and the line a browser reads first is still the one nearest
    /// the block-start edge, so the marker and the line the container stands on take their baseline from
    /// that line (CSS Flexbox 1 §8.5).
    /// </summary>
    private static LayoutBox? FirstLineOfContainer(LayoutBox container)
    {
        var flex = container.Source.Style.Flex;
        var row = flex.IsRow;
        var flow = container.Flow;
        var rtl = container.Flow.IsRightToLeft;
        var reverse = row
            ? flex.Direction == FlexDirection.RowReverse
            : flex.Direction == FlexDirection.ColumnReverse;
        LayoutBox? best = null;
        var bestBlock = Double.PositiveInfinity;
        var bestMain = Double.PositiveInfinity;
        var bestX = Double.PositiveInfinity;

        void Walk(LayoutBox node)
        {
            if (node.Kind == LayoutKind.Line)
            {
                var box = node.BorderBox;

                // The box the line stands beside reads first from its own block-start edge inward: the
                // top of a row, the right or left of a column. Within one line the item at the main-start
                // edge speaks for it, and the leftmost line settles the case where two wrap-reversed
                // lines share both coordinates (CSS Flexbox 1 §8.5).
                var block = flow.IsVertical ? (flow.BlockSign < 0 ? -box.X : box.X) : box.Y;
                var main = row ? (rtl ? -box.X : box.X) : box.Y;

                if (reverse)
                {
                    main = -main;
                }

                if (block < bestBlock - 0.01 ||
                    (Math.Abs(block - bestBlock) <= 0.01 &&
                        (main < bestMain - 0.01 ||
                            (Math.Abs(main - bestMain) <= 0.01 && box.X < bestX))))
                {
                    best = node;
                    bestBlock = block;
                    bestMain = main;
                    bestX = box.X;
                }

                return;
            }

            foreach (var child in node.Children)
            {
                if (child.IsOutOfFlow || child.Source.Style.Float != FloatSide.None)
                {
                    continue;
                }

                Walk(child);
            }
        }

        Walk(container);

        return best;
    }

    /// <summary>
    /// Gets the last line box in the box's normal flow, searching its own last children before those
    /// nested inside them, together with how far relative positioning moved the line on the way down.
    /// A box that left the flow - or a float, which takes no place in the lines - carries no line of
    /// this box with it, so its subtree is passed by.
    /// </summary>
    private static (LayoutBox Line, Offset Displacement)? LastLineInFlow(LayoutBox box, Offset carried)
    {
        if (box.Kind == LayoutKind.Line)
        {
            // A line box standing only to say where an out-of-flow box would have gone is not a line
            // of the box's normal flow, and its baseline is not the box's: §10.8.1 asks for the last
            // line the content formed, and content that left the flow forms none.
            var inFlow = false;

            foreach (var child in box.Children)
            {
                if (!child.IsOutOfFlow && child.Source.Style.Float == FloatSide.None)
                {
                    inFlow = true;
                    break;
                }
            }

            return inFlow || box.Children.Count == 0 ? (box, carried) : null;
        }

        for (var index = box.Children.Count - 1; index >= 0; index--)
        {
            var child = box.Children[index];

            if (child.IsOutOfFlow || child.Source.Style.Float != FloatSide.None)
            {
                continue;
            }

            var shift = new Offset(
                carried.X + child.RelativeDisplacement.X,
                carried.Y + child.RelativeDisplacement.Y);

            if (LastLineInFlow(child, shift) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    /// <summary>
    /// Gets how far a line's baseline stands from the block-start edge of the box that holds it, which
    /// is the form an inline-block's baseline has to take: a row measures it down from the top, and a
    /// column from the right or the left, whichever edge its block axis begins at. The line's place is
    /// read before relative positioning moved it, which is what <paramref name="displacement"/> puts
    /// back (CSS 2.1 §9.4.3).
    /// </summary>
    private static Double BlockStartBaseline(
        LayoutBox line, Offset displacement, WritingModeInfo flow, Double blockSize)
    {
        var baseline = flow.IsVertical
            ? line.BorderBox.X - displacement.X + line.Baseline
            : line.BorderBox.Y - displacement.Y + line.Baseline;

        return flow.BlockSign < 0 ? blockSize - baseline : baseline;
    }
    /// <summary>Gets the padding and border a box adds along its own inline axis.</summary>
    /// <remarks>
    /// A box whose text runs down a column borders its content above and below rather than to either
    /// side, so the insets that count along its line are the vertical pair; a box in a row takes the
    /// horizontal one. A percentage among them resolves against the extent the box is measured against,
    /// which is the basis the box model would use for it in a row.
    /// </remarks>
    private static Double InlineInsets(Box box, Double basis)
    {
        var padding = box.Style.Padding.Resolve(basis);
        var border = BlockLayout.BorderOf(box);

        return box.Flow.IsVertical
            ? padding.Vertical + border.Vertical
            : padding.Horizontal + border.Horizontal;
    }

    /// <summary>Measures a whole string of text the way the writing mode advances it.</summary>
    private Double Measure(ComputedStyle style, String chunk, WritingModeInfo flow) =>
        MeasureChunk(_measurer, style, chunk, 0, chunk.Length, flow);

    /// <summary>Measures a range of text the way the writing mode advances it.</summary>
    private Double Measure(ComputedStyle style, String source, Int32 start, Int32 length, WritingModeInfo flow) =>
        MeasureChunk(_measurer, style, source, start, length, flow);

    /// <summary>
    /// Measures a range against the measurer, adding the letter spacing the style asks for on top of
    /// every character - the last one included, which is what CSS says a spaced run is worth.
    /// </summary>
    /// <remarks>
    /// A range rather than a string because the range is what the line keeps: a string made here for
    /// the measurer's sake is characters copied that the run would only copy again when it merged with
    /// the piece after it.
    /// </remarks>
    /// <summary>
    /// Gets whether the box's chain stands inside a <c>ruby</c> element, where a line may not break.
    /// </summary>
    private static Boolean InsideRuby(InlineAncestor? inline)
    {
        for (var ancestor = inline; ancestor is not null; ancestor = ancestor.Parent)
        {
            if (ancestor.Box.Element?.LocalName.Equals("ruby", StringComparison.OrdinalIgnoreCase) == true)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Gets the mark a break draws. A column draws the upright hyphen: the hyphen-minus a line sets its
    /// mark in is turned on its side where a column carries it, and the upright form is the one a reading
    /// of the same base draws beside the text (CSS Text 4 §6.2, measured on the vertical soft-hyphen
    /// pairs).
    /// </summary>
    private static String HyphenMark(ComputedStyle style, WritingModeInfo flow) =>
        flow.IsVertical && style.HyphenateCharacter == "-" ? UprightHyphen : style.HyphenateCharacter;

    /// <summary>The mark a browser draws at a break: the upright hyphen, U+2010.</summary>
    private static readonly String UprightHyphen = Char.ConvertFromUtf32(0x2010);

    /// <summary>
    /// Measures what a pair's own characters ask of the line. A tab inside the base runs the line on to
    /// its next stop rather than by an advance of its own, which is a question about where the pair
    /// stands rather than about the font (CSS Text 3 §4.2).
    /// </summary>
    private Double PairWidth(
        ComputedStyle style,
        ComputedStyle tabStyle,
        String text,
        Double position,
        WritingModeInfo flow) =>
        text.IndexOf('\t') < 0
            ? Measure(style, text, flow)
            : WhiteSpaceWidth(style, tabStyle, text, 0, text.Length, position, flow);

    /// <summary>
    /// Gets the room a run that opens with content asks the line for before the line steps inside it. The
    /// opportunity inside a word is what the line falls back on once the word stands on a line of its own
    /// and still does not fit, not a licence to fill the last of the line above with the end of it, so the
    /// whole of the word is asked for - with the space that trails it and the mark a break draws. Where the
    /// mode gives the line a place to end inside the run, after a preserved space it keeps (CSS Text 3
    /// §4.1.3), the ask stops at the first of those: the line may genuinely be brought to an end there, and
    /// a three-character box holding two kana and four ideographic spaces fills with one of those spaces
    /// rather than moving the second kana down to begin the line.
    /// </summary>
    private Double AskWidth(
        InlineItem item,
        ComputedStyle strut,
        String source,
        Int32 start,
        Int32 length,
        Double width,
        Double trailed,
        Double mark,
        Double position,
        Boolean stepsSpaces,
        WritingModeInfo flow)
    {
        if (stepsSpaces)
        {
            var end = start + length;
            var at = start;

            while (at < end && !TextChunker.IsBreakSpace(source[at]))
            {
                at += TextClusters.UnitsAt(source, at, end);
            }

            if (at > start && at < end)
            {
                // The content up to the first space it holds, and that space: the line ends after it.
                var leading = Measure(item.Style, source, start, at - start, flow);

                return leading + WhiteSpaceWidth(
                    item.Style,
                    strut,
                    source,
                    at,
                    TextClusters.UnitsAt(source, at, end),
                    position + leading,
                    flow);
            }
        }

        return width + trailed + mark;
    }

    /// <summary>
    /// Gets the room the mark a break draws takes: the hyphenate character, set in the run's own font.
    /// </summary>
    private static Double HyphenWidth(ITextMeasurer measurer, ComputedStyle style, WritingModeInfo flow) =>
        String.IsNullOrEmpty(style.HyphenateCharacter)
            ? 0
            : MeasureChunk(measurer, style, HyphenMark(style, flow), 0, HyphenMark(style, flow).Length, flow);

    private static Double MeasureChunk(
        ITextMeasurer measurer,
        ComputedStyle style,
        String source,
        Int32 start,
        Int32 length,
        WritingModeInfo flow) =>
        (flow.IsVertical
            ? MeasureColumn(source, start, length, style.GetFont(), style.FontSize, flow, measurer)
            : measurer.MeasureWidth(source.Substring(start, length), style.GetFont()))
        + (style.LetterSpacing * SpacingUnits(source, start, length, flow))
        + (style.WordSpacing * TextChunker.WordSeparators(source, start, length))
        + AutoSpaceWithin(measurer, style, source, start, length, flow)
        - TrimWithin(measurer, style, source, start, length, flow);

    /// <summary>The codes a trimmed character wears in a fragment's trim mask.</summary>
    private const Char TrimOpening = '\u0001';

    private const Char TrimClosing = '\u0002';
    private const Char TrimMiddle = '\u0003';

    /// <summary>
    /// The previous character a line has already set, with everything the collapse rules ask of it:
    /// its class, whether it is still full-width, the size it stands at, the value its own style
    /// computed, and whether it was already trimmed.
    /// </summary>
    private readonly record struct PreviousText(
        TrimNeighbor Neighbor,
        ComputedStyle Style,
        Int32 CodePoint,
        Boolean Trimmed,
        WritingModeInfo Flow,
        Int32 Before);

    /// <summary>
    /// What one range of text contributes to a line's trims: the mask to draw with, the room the
    /// first character gives up because of where the line put it, and the room the character before
    /// the range gives up because this range begins with a punctuation beside it.
    /// </summary>
    private readonly record struct TrimStep(
        String? Mask,
        Double BoundaryDelta,
        Double RetroDelta,
        Char RetroCode);

    /// <summary>
    /// Gets the room the punctuation inside a range gives up for its neighbors. Only characters with
    /// both neighbors inside the range are counted: the two ends belong to whatever stands beside
    /// them on the line, which the line builder settles when it knows what that is.
    /// </summary>
    private static Double TrimWithin(
        ITextMeasurer measurer,
        ComputedStyle style,
        String source,
        Int32 start,
        Int32 length,
        WritingModeInfo flow)
    {
        var value = style.TextSpacingTrim;

        if (length <= 0 ||
            TextSpacingTrim.HasExclusiveFeature(style.ResolvedFont.Features) ||
            (!value.All && !value.Adjacent))
        {
            return 0;
        }

        var total = 0.0;
        var end = start + length;

        for (var index = start; index < end;)
        {
            var codePoint = TextChunker.CodePointAt(source, index);
            var take = Char.IsSurrogatePair(source, index) ? 2 : 1;

            if (!TextSpacingTrim.IsCandidate(codePoint))
            {
                index += take;
                continue;
            }

            var kind = KindOf(style, codePoint);

            if (kind != PunctuationClass.None && IsFullwidthPunctuation(measurer, style, codePoint, flow))
            {
                var trimmed = value.All;

                if (!trimmed && value.Adjacent)
                {
                    if (kind == PunctuationClass.Opening && index > start)
                    {
                        trimmed = TextSpacingTrim.TrimsOpening(
                            NeighborOf(measurer, style, CodePointBefore(source, index), flow),
                            style.FontSize);
                    }
                    else if (kind == PunctuationClass.Closing && index + take < end)
                    {
                        trimmed = TextSpacingTrim.TrimsClosing(
                            NeighborOf(measurer, style, TextChunker.CodePointAt(source, index + take), flow),
                            style.FontSize);
                    }
                }

                if (trimmed)
                {
                    total += TrimDelta(measurer, style, codePoint, flow);
                }
            }

            index += take;
        }

        return total;
    }

    /// <summary>The codes an autospaced boundary wears in a fragment's autospace mask.</summary>
    private const Char AutoSpaceAfter = '\u0001';

    private const Char AutoSpaceReplace = '\u0002';

    /// <summary>What one range of text contributes to a line's autospacing.</summary>
    private readonly record struct AutoSpaceStep(
        Double BoundaryDelta,
        String? Mask,
        Double Room,
        Double RetroDelta,
        Boolean Inside);

    /// <summary>
    /// Gets the room the property adds between the characters inside a range. Only characters with
    /// both neighbors inside the range are counted: the two ends belong to whatever stands beside
    /// them on the line, which the line builder settles when it knows what that is.
    /// </summary>
    private static Double AutoSpaceWithin(
        ITextMeasurer measurer,
        ComputedStyle style,
        String source,
        Int32 start,
        Int32 length,
        WritingModeInfo flow)
    {
        var value = style.TextAutospace;

        if (value.Off || length <= 0)
        {
            return 0;
        }

        var space = AutoSpaceWidth(measurer, style, flow);

        if (space <= 0)
        {
            return 0;
        }

        var total = 0.0;
        var end = start + length;

        for (var index = start; index < end;)
        {
            // Stepped by cluster: a base with the marks and selectors that stand on it is one
            // character, and a boundary falls between characters rather than inside one.
            var next = index + TextClusters.UnitsAt(source, index, end);

            if (next < end)
            {
                total += PairRoom(measurer, style, value, source, index, next, end, flow, space, out _);
            }

            index = next;
        }

        return total;
    }

    /// <summary>
    /// Plans the autospacing of one range the line is about to add: the room the boundary with the
    /// character already standing on the line adds, and the mask the run draws with.
    /// </summary>
    /// <remarks>
    /// A boundary's room is added to the room the range was measured at, and no mask is needed for it:
    /// the fragments stand side by side on the line, so the room between them is a gap the boxes
    /// already leave. It is the boundaries inside one run that need the mask, because a run's glyphs
    /// are set one after another by its own shaping.
    /// </remarks>
    private static AutoSpaceStep PlanAutoSpace(
        ITextMeasurer measurer,
        String source,
        Int32 start,
        Int32 length,
        ComputedStyle style,
        WritingModeInfo flow,
        PreviousText? previous,
        Boolean wantMask)
    {
        var value = style.TextAutospace;

        if (value.Off || length <= 0)
        {
            return default;
        }

        var space = AutoSpaceWidth(measurer, style, flow);

        if (space <= 0)
        {
            return default;
        }

        var boundary = 0.0;
        var retro = 0.0;
        var inside = false;
        Char[]? mask = null;
        var end = start + length;

        // A range that begins with a mark or a selector continues the character before it, so the
        // boundary is not between the two runs but after the character this one completes: the pair
        // the property reads stands between the base before and the first character of this range
        // that begins one of its own.
        var pairAt = start;

        while (pairAt < end && TextClusters.Continues(source, pairAt))
        {
            pairAt += TextClusters.UnitsAt(source, pairAt, end);
        }

        if (previous is { } last && pairAt < end)
        {
            var room = RoomBetween(
                measurer,
                style,
                value,
                last.CodePoint,
                last.Flow,
                TextChunker.CodePointAt(source, pairAt),
                flow,
                space);

            if (value.Replace &&
                pairAt == start &&
                last.CodePoint == ' ' &&
                last.Before != 0 &&
                RoomBetween(
                    measurer,
                    style,
                    value,
                    last.Before,
                    last.Flow,
                    TextChunker.CodePointAt(source, pairAt),
                    flow,
                    space) is var replaced and > 0)
            {
                // The space the run before this one ends with is where the boundary stands: it takes
                // the room the boundary is worth and gives up its own.
                retro = measurer.MeasureWidth(" ", last.Style.GetFont()) - replaced;
            }
            else if (room > 0)
            {
                boundary = room;

                if (pairAt > start)
                {
                    // The room stands after the character this range completes, inside the run it is
                    // drawn with rather than in the gap before it.
                    inside = true;

                    if (wantMask)
                    {
                        // The character this range completes is the cluster its first units make, so
                        // the room is marked on that cluster - the one the range opens with.
                        (mask ??= new Char[length])[0] = AutoSpaceAfter;
                    }
                }
            }
        }

        if (wantMask)
        {

            // A space that opens the range is a boundary the caller may keep between two runs; a
            // replace value takes its room, so the space stands in the cell the boundary is worth
            // rather than its own.
            var first = TextChunker.UnitsAt(source, start);

            if (value.Replace && start < end && source[start] == ' ' && start + first < end &&
                previous is { } spaced &&
                RoomBetween(
                    measurer,
                    style,
                    value,
                    spaced.CodePoint,
                    spaced.Flow,
                    TextChunker.CodePointAt(source, start + first),
                    flow,
                    space) is var replaced and > 0)
            {
                (mask ??= new Char[length])[0] = AutoSpaceReplace;
                boundary -= measurer.MeasureWidth(" ", style.GetFont()) - replaced;
            }

            for (var index = start; index < end;)
            {
                var next = index + TextClusters.UnitsAt(source, index, end);

                if (next < end)
                {
                    PairRoom(measurer, style, value, source, index, next, end, flow, space, out var code);

                    if (code == AutoSpaceAfter)
                    {
                        (mask ??= new Char[length])[index - start] = code;
                    }
                    else if (code == AutoSpaceReplace)
                    {
                        (mask ??= new Char[length])[next - start] = code;
                    }
                }

                index = next;
            }
        }

        return new AutoSpaceStep(boundary, MaskOf(mask), space, retro, inside);
    }

    /// <summary>
    /// Gets the room one pair of neighboring characters adds, and the mask code that draws it:
    /// <see cref="AutoSpaceAfter"/> on the left character, or <see cref="AutoSpaceReplace"/> on the
    /// space a <c>replace</c> value takes the place of.
    /// </summary>
    private static Double PairRoom(
        ITextMeasurer measurer,
        ComputedStyle style,
        TextAutospaceValue value,
        String source,
        Int32 leftAt,
        Int32 rightAt,
        Int32 end,
        WritingModeInfo flow,
        Double space,
        out Char code)
    {
        code = '\0';

        var room = RoomBetween(
            measurer,
            style,
            value,
            TextChunker.CodePointAt(source, leftAt),
            flow,
            TextChunker.CodePointAt(source, rightAt),
            flow,
            space);

        if (room > 0)
        {
            code = AutoSpaceAfter;
            return room;
        }

        if (value.Replace && source[rightAt] == ' ' && rightAt + 1 < end)
        {
            var replaced = RoomBetween(
                measurer,
                style,
                value,
                TextChunker.CodePointAt(source, leftAt),
                flow,
                TextChunker.CodePointAt(source, rightAt + 1),
                flow,
                space);

            if (replaced > 0)
            {
                code = AutoSpaceReplace;
                return replaced - measurer.MeasureWidth(" ", style.GetFont());
            }
        }

        return 0;
    }

    /// <summary>
    /// Gets whether the property separates this pair of characters: the classes it names, or the
    /// no-break space the language's typography puts around a punctuation.
    /// </summary>
    /// <summary>
    /// Gets the room one boundary between two characters adds: the eighth of the CJK advance the
    /// property names, or the width of the no-break space the language's typography puts beside a
    /// punctuation.
    /// </summary>
    private static Double RoomBetween(
        ITextMeasurer measurer,
        ComputedStyle style,
        TextAutospaceValue value,
        Int32 left,
        WritingModeInfo leftFlow,
        Int32 right,
        WritingModeInfo rightFlow,
        Double space)
    {
        if (TextAutospace.ShouldSpace(
            value,
            TextAutospace.ClassOf(left, IsSetUpright(leftFlow, left)),
            TextAutospace.ClassOf(right, IsSetUpright(rightFlow, right))))
        {
            return space;
        }

        return TextAutospace.Punctuation(value, style.Language, right) == PunctuationSpace.Before ||
               TextAutospace.Punctuation(value, style.Language, left) == PunctuationSpace.After
            ? measurer.MeasureWidth("\u202F", style.GetFont())
            : 0;
    }

    /// <summary>
    /// Gets whether a character is set upright by the column it stands in, by
    /// <c>text-orientation: upright</c> or by <c>text-combine-upright</c> - neither of which is a
    /// neighbor the property spaces (CSS Text 4 §8.4).
    /// </summary>
    private static Boolean IsSetUpright(WritingModeInfo flow, Int32 codePoint) =>
        flow.IsVertical && (flow.CombinesUpright || flow.IsUpright(codePoint));

    /// <summary>Gets the room one boundary is widened by: an eighth of the font's CJK advance.</summary>
    private static Double AutoSpaceWidth(
        ITextMeasurer measurer, ComputedStyle style, WritingModeInfo flow)
    {
        var font = style.GetFont();
        var ideograph = flow.IsVertical
            ? measurer.MeasureUprightAdvance(0x6C34, font)
            : measurer.MeasureWidth("\u6C34", font);

        return ideograph > 0 ? ideograph * TextAutospace.SpaceFraction : 0;
    }

    /// <summary>
    /// Gets the room one autospaced boundary asks for, where the two characters stand side by side on
    /// the one line the content needs.
    /// </summary>
    private static Double AutoSpaceBetween(
        ITextMeasurer measurer, ComputedStyle style, Int32 left, Int32 right, WritingModeInfo flow)
    {
        var value = style.TextAutospace;

        if (value.Off)
        {
            return 0;
        }

        var space = AutoSpaceWidth(measurer, style, flow);

        return space <= 0 ? 0 : RoomBetween(measurer, style, value, left, flow, right, flow, space);
    }

    /// <summary>
    /// Plans the trims of one range the line is about to add: the mask it will draw with, the room
    /// its first character gives up at the line's start or beside the character already standing
    /// there, and the room the previous character gives up because this range begins beside it.
    /// </summary>
    private static TrimStep PlanTrim(
        ITextMeasurer measurer,
        String source,
        Int32 start,
        Int32 length,
        ComputedStyle style,
        WritingModeInfo flow,
        PreviousText? previous,
        Boolean lineEmpty,
        Boolean startsParagraph,
        Boolean wantMask)
    {
        if (length <= 0)
        {
            return default;
        }

        var value = style.TextSpacingTrim;
        Char[]? mask = null;
        var boundary = 0.0;
        var enabled = !TextSpacingTrim.HasExclusiveFeature(style.ResolvedFont.Features);

        // The first character stands where the range cannot see a neighbor: at the line's start, or
        // beside what the line already holds. A value that trims everywhere already takes its room
        // in the measurement, so only the positioned modes are read here.
        var firstCodePoint = TextChunker.CodePointAt(source, start);
        var firstKind = TextSpacingTrim.IsCandidate(firstCodePoint)
            ? KindOf(style, firstCodePoint)
            : PunctuationClass.None;

        if (enabled &&
            !value.All &&
            firstKind != PunctuationClass.None &&
            IsFullwidthPunctuation(measurer, style, firstCodePoint, flow))
        {
            var trimmed = lineEmpty
                ? TextSpacingTrim.TrimsAtLineStart(value, firstKind, startsParagraph)
                : value.Adjacent &&
                  firstKind == PunctuationClass.Opening &&
                  previous is { } left &&
                  TextSpacingTrim.TrimsOpening(left.Neighbor, style.FontSize);

            if (trimmed)
            {
                if (wantMask)
                {
                    (mask ??= new Char[length])[0] = CodeOf(firstKind);
                }

                boundary = TrimDelta(measurer, style, firstCodePoint, flow);
            }
        }

        var retro = 0.0;
        var retroCode = '\0';

        if (!lineEmpty &&
            previous is { Trimmed: false, Neighbor.Class: PunctuationClass.Closing } lastText)
        {
            var previousValue = lastText.Style.TextSpacingTrim;
            var previousEnabled =
                !TextSpacingTrim.HasExclusiveFeature(lastText.Style.ResolvedFont.Features) &&
                (previousValue.All || previousValue.Adjacent);

            if (previousEnabled &&
                IsFullwidthPunctuation(measurer, lastText.Style, lastText.CodePoint, lastText.Flow) &&
                TextSpacingTrim.TrimsClosing(
                    NeighborOf(measurer, style, firstCodePoint, flow),
                    lastText.Style.FontSize))
            {
                retro = TrimDelta(measurer, lastText.Style, lastText.CodePoint, lastText.Flow);
                retroCode = TrimClosing;
            }
        }

        if (enabled && wantMask)
        {
            var end = start + length;

            for (var index = start; index < end;)
            {
                var codePoint = TextChunker.CodePointAt(source, index);
                var take = Char.IsSurrogatePair(source, index) ? 2 : 1;

                if (!TextSpacingTrim.IsCandidate(codePoint))
                {
                    index += take;
                    continue;
                }

                var kind = KindOf(style, codePoint);

                if (kind != PunctuationClass.None && IsFullwidthPunctuation(measurer, style, codePoint, flow))
                {
                    var trimmed = value.All;

                    if (!trimmed && value.Adjacent)
                    {
                        if (kind == PunctuationClass.Opening && index > start)
                        {
                            trimmed = TextSpacingTrim.TrimsOpening(
                                NeighborOf(measurer, style, CodePointBefore(source, index), flow),
                                style.FontSize);
                        }
                        else if (kind == PunctuationClass.Closing && index + take < end)
                        {
                            trimmed = TextSpacingTrim.TrimsClosing(
                                NeighborOf(measurer, style, TextChunker.CodePointAt(source, index + take), flow),
                                style.FontSize);
                        }
                    }

                    if (trimmed)
                    {
                        (mask ??= new Char[length])[index - start] = CodeOf(kind);
                    }
                }

                index += take;
            }
        }

        return new TrimStep(MaskOf(mask), boundary, retro, retroCode);
    }

    private static String? MaskOf(Char[]? mask)
    {
        if (mask is null)
        {
            return null;
        }

        foreach (var code in mask)
        {
            if (code != '\0')
            {
                return new String(mask);
            }
        }

        return null;
    }

    /// <summary>
    /// Gets the part of a mask one range of the text covers, or null where the range marks nothing.
    /// </summary>
    /// <param name="clearBoundary">
    /// Whether the code on the range's last character is taken out. A boundary at the end of a piece
    /// stands between it and the piece after it, which is a gap between boxes rather than a mask.
    /// </param>
    private static String? MaskOf(String? mask, Int32 start, Int32 length, Boolean clearBoundary)
    {
        if (mask is null || start < 0 || length <= 0 || start + length > mask.Length)
        {
            return null;
        }

        var chars = mask.Substring(start, length).ToCharArray();

        if (clearBoundary)
        {
            var at = chars.Length - 1;

            while (at > 0 && Char.IsLowSurrogate(chars[at]))
            {
                at--;
            }

            if (chars[at] == AutoSpaceAfter)
            {
                chars[at] = '\0';
            }
        }

        return MaskOf(chars);
    }

    /// <summary>
    /// Gets whether the character before a place in a fragment is one a spaced boundary follows.
    /// </summary>
    private static Boolean SpacedBefore(TextFragment fragment, Int32 at)
    {
        if (fragment.AutoSpaces is not { } mask || at <= 0)
        {
            return false;
        }

        var index = at - 1;

        while (index > 0 && Char.IsLowSurrogate(fragment.Text[index]))
        {
            index--;
        }

        return index < mask.Length && mask[index] == AutoSpaceAfter;
    }

    private static Char CodeOf(PunctuationClass kind) => kind switch
    {
        PunctuationClass.Opening => TrimOpening,
        PunctuationClass.Closing => TrimClosing,
        PunctuationClass.Middle => TrimMiddle,
        _ => '\0',
    };

    private static PunctuationClass KindOf(ComputedStyle style, Int32 codePoint) =>
        TextSpacingTrim.Classify(codePoint, TextSpacingTrim.LanguageOf(style.Language));

    /// <summary>Gets what the collapse rules need to know about one character standing beside another.</summary>
    private static TrimNeighbor NeighborOf(
        ITextMeasurer measurer, ComputedStyle style, Int32 codePoint, WritingModeInfo? flow) =>
        new(
            KindOf(style, codePoint),
            codePoint == 0x3000,
            KindOf(style, codePoint) == PunctuationClass.Closing &&
                IsFullwidthPunctuation(measurer, style, codePoint, flow),
            style.FontSize);

    /// <summary>
    /// Gets whether the character still advances by the fullwidth one. A glyph the font already sets
    /// proportionally is simultaneously full- and half-width, and the property leaves it alone.
    /// </summary>
    private static Boolean IsFullwidthPunctuation(
        ITextMeasurer measurer, ComputedStyle style, Int32 codePoint, WritingModeInfo? flow = null)
    {
        if (TextSpacingTrim.HasExclusiveFeature(style.ResolvedFont.Features))
        {
            return false;
        }

        // Down an upright column every character owns a cell of the whole em, whatever width it has
        // along a line: the steps a column takes are floored at the em box there. A narrow Latin quote
        // stood upright in such a column is therefore as full-width down it as the ideograph beside
        // it, which is the pair the collapse rules read. Measuring its line width instead says the
        // glyph is already proportional and leaves the trim unmarked, which makes every column of
        // these pages one character too long (CSS Writing Modes 3 §4.1.2 with CSS Text 4 §4.4,
        // measured: the text-spacing-trim-*-vrl variants, where Chrome trims the U+2018 that follows
        // a U+FF09 down an upright column). A turned run is left to the rule above, since it really
        // does advance by the width the character has along a line.
        if (flow is { IsVertical: true, Orientation: TextOrientation.Upright })
        {
            return true;
        }

        var fullwidth = measurer.MeasureWidth("\u6C34", style.GetFont());

        if (fullwidth <= 0)
        {
            fullwidth = style.FontSize;
        }

        return TextSpacingTrim.IsFullwidth(
            measurer.MeasureWidth(Char.ConvertFromUtf32(codePoint), style.GetFont()),
            fullwidth);
    }

    /// <summary>Gets the room one trimmed character gives up along the line or column it is set on.</summary>
    private static Double TrimDelta(
        ITextMeasurer measurer, ComputedStyle style, Int32 codePoint, WritingModeInfo flow)
    {
        if (!flow.IsVertical)
        {
            return measurer.MeasureWidth(Char.ConvertFromUtf32(codePoint), style.GetFont()) / 2;
        }

        var font = style.GetFont();
        var advance = font.AsksForVerticalWidth
            ? measurer.MeasureUprightAdvance(codePoint, font)
            : measurer.MeasureAdvance(codePoint, font, flow);
        var step = font.AsksForVerticalWidth && advance > 0
            ? advance
            : Math.Max(advance, font.Size);

        return step / 2;
    }

    /// <summary>
    /// Gets the room a closing punctuation can give up if it ends the line. The breaker keeps such a
    /// character on the line where only its trailing half fails to fit; the line's close takes the
    /// room off for real. <c>trim-end</c> always has this room; <c>allow-end</c> has it because the
    /// line does not otherwise fit.
    /// </summary>
    private static Double EndGrace(
        ITextMeasurer measurer,
        ComputedStyle style,
        String source,
        Int32 at,
        Int32 taken,
        WritingModeInfo flow)
    {
        if (taken <= 0)
        {
            return 0;
        }

        var codePoint = CodePointBefore(source, at + taken);
        var kind = KindOf(style, codePoint);
        var value = style.TextSpacingTrim;

        if (kind != PunctuationClass.Closing ||
            value.All ||
            value.End == SpacingTrimEnd.Space ||
            TextSpacingTrim.HasExclusiveFeature(style.ResolvedFont.Features) ||
            !IsFullwidthPunctuation(measurer, style, codePoint))
        {
            return 0;
        }

        return TrimDelta(measurer, style, codePoint, flow);
    }

    private static Int32 CodePointBefore(String text, Int32 index) =>
        index >= 2 && Char.IsSurrogatePair(text, index - 2)
            ? Char.ConvertToUtf32(text[index - 2], text[index - 1])
            : text[index - 1];

    /// <summary>
    /// Gets the room a stop or comma at the end of a run gives up when the line cannot otherwise hold
    /// it: <c>allow-end</c> hangs the character instead of breaking the line before it, and the line's
    /// close takes its advance off for real (CSS Text 3 §8.2.1).
    /// </summary>
    private static Double HangGrace(
        ITextMeasurer measurer,
        ComputedStyle style,
        String source,
        Int32 at,
        Int32 taken,
        Int32 runEnd,
        Boolean lastOfContent,
        WritingModeInfo flow)
    {
        if (taken <= 0 || at + taken != runEnd)
        {
            return 0;
        }

        var end = at + taken;
        var start = TextClusters.ClusterStart(source, end);
        var codePoint = TextChunker.CodePointAt(source, start);

        // The glyph the breaker may keep past the line's edge: a stop or comma under `allow-end` or
        // `force-end`, and a closing bracket or quote under `last` where this is the content's final
        // line (CSS Text 3 §8.2.1). A line the value would not hang the glyph from takes no grace.
        return HangingPunctuation.HangsAtEnd(style.HangingPunctuation, codePoint, overflows: true, lastOfContent)
            ? MeasureChunk(measurer, style, source, start, end - start, flow)
            : 0;
    }

    /// <summary>
    /// Gets how many times the run's letter spacing is charged along a line, where the glyphs are set
    /// from the characters they were shaped out of, so the character is the unit and not the half of an
    /// encoding that stands for it. A column is left to its own count: it gives every character of the
    /// text a cell of its own, and the cells are stepped by the same units the drawing steps by.
    /// </summary>
    private static Int32 SpacingUnits(String source, Int32 start, Int32 length, WritingModeInfo flow) =>
        flow.IsVertical ? length : TextClusters.Count(source, start, length);

    /// <summary>
    /// Measures the annotation of a ruby item: how far it reaches along the inline axis, how much room
    /// it needs beside the base, and the orientation its glyphs are drawn in.
    /// </summary>
    /// <remarks>
    /// The annotation is set in a writing mode of its own only in that its characters may orient
    /// differently from the base's - a reading in Latin is turned where the kanji beside it stay
    /// upright - so it is measured the same way any other run is and keeps the orientation its first
    /// run resolved to.
    /// </remarks>
    /// <summary>
    /// Gets how tall a line of text set in the given style is - the band the words it carries stand in,
    /// which is the line height the style asks for and the font's own height where it asks for none.
    /// </summary>
    private Double LineBoxHeight(ComputedStyle style, FontStyle font) =>
        LineBoxHeight(_measurer, style, font);

    /// <summary>
    /// Gets how tall a line of text set in the given style is - the band the words it carries stand in,
    /// which is the line height the style asks for and the font's own height where it asks for none.
    /// </summary>
    private static Double LineBoxHeight(ITextMeasurer measurer, ComputedStyle style, FontStyle font)
    {
        if (style.LineHeight is { } stated)
        {
            return stated;
        }

        var metrics = measurer.GetMetrics(font);

        return metrics.Ascent + metrics.Descent;
    }

    private RubyAnnotation MeasureAnnotation(InlineItem item)
    {
        var style = item.AnnotationStyle;
        var text = item.AnnotationText!;
        var flow = item.AnnotationBox!.Flow;
        var metrics = _measurer.GetMetrics(style.GetFont());
        var ascent = metrics.Ascent;
        var descent = metrics.Descent;

        if (style.LineHeight is { } lineHeight)
        {
            // Half the leading, which is negative when the font is taller than the line it is set on -
            // a face whose own ascent and descent come to more than the declared line height is drawn
            // overflowing a line box that is still as tall as the page asked for.
            var halfLeading = (lineHeight - (ascent + descent)) / 2;
            ascent += halfLeading;
            descent += halfLeading;
        }

        return new RubyAnnotation(
            text,
            Measure(style, text, flow),
            ascent + descent,
            ascent,
            item.Box.Style.HasRubyUnder,
            style,
            item.AnnotationBox!,
            ResolveOrientation(text, flow));
    }

    /// <summary>
    /// Gets how far a reading may hang past its pair on each side: the room the text standing
    /// beside the pair gives up (CSS Ruby 1 §5.1).
    /// </summary>
    private (Double Before, Double After) OverhangRoom(
        List<InlineItem> items,
        Int32 index,
        Double position,
        Double baseWidth,
        Double annotationWidth,
        Double[] begins,
        ComputedStyle tabStyle)
    {
        var pair = items[index].Text;
        var facing = String.IsNullOrEmpty(pair) ? '\0' : pair![0];
        var before = Spaces(items, index - 1, fromEnd: true, position, facing, begins, tabStyle);

        // A reading that cannot hang far enough forces its pair wider, and the wider pair carries the
        // text after it along the line. What that side gives up is therefore asked of the place the
        // reading itself reaches, which the room already found on this side decides.
        var end = ReadingEnd(items[index], position, baseWidth, annotationWidth, before);
        var after = Spaces(items, index + 1, fromEnd: false, end,
            String.IsNullOrEmpty(pair) ? '\0' : pair[^1], begins, tabStyle);

        return (before, after);
    }

    /// <summary>
    /// Gets where a reading reaches along the line: the pair's own characters stand where its alignment
    /// puts them once the room beside it has been taken, and the reading is laid over them from there
    /// (CSS Ruby 1 §4.3, §5.1).
    /// </summary>
    private static Double ReadingEnd(
        InlineItem item,
        Double position,
        Double baseWidth,
        Double annotationWidth,
        Double before)
    {
        var excess = Math.Max(0, annotationWidth - baseWidth);
        var start = position + HangPast(item, excess, before, fromEnd: true);

        return item.Box.Style.RubyAlign switch
        {
            RubyAlign.Start or RubyAlign.SpaceBetween => start + annotationWidth,
            RubyAlign.End => start + baseWidth,
            _ => start + (baseWidth + annotationWidth) / 2,
        };
    }

    /// <summary>
    /// Measures the room one side of a pair gives up to a reading. The characters standing against the
    /// pair are gathered outward - white space of any width, the blank side of a fullwidth mark, a
    /// quarter of a middle dot - until one gives up nothing, and they are measured from the place the
    /// line holds them at, because a tab's room is a distance to a stop (CSS Ruby 1 §5.1). A neighbour
    /// that is not a run of text gives up nothing at all.
    /// </summary>
    private Double Spaces(
        List<InlineItem> items,
        Int32 at,
        Boolean fromEnd,
        Double position,
        Char facing,
        Double[] begins,
        ComputedStyle tabStyle)
    {
        var given = 0.0;
        var edge = position;

        for (var index = at; index >= 0 && index < items.Count; index += fromEnd ? -1 : 1)
        {
            var item = items[index];

            if (item.IsRuby || item.IsReplaced || item.IsAtomic || item.IsBreak || item.IsOutOfFlow ||
                String.IsNullOrEmpty(item.Text))
            {
                break;
            }

            var text = item.Text!;
            var flow = item.Box.Flow;

            // How many characters at the edge a reading may cover, counted outward until one keeps its
            // whole room for itself.
            var taken = 0;

            while (taken < text.Length &&
                HangShare(item, text, fromEnd ? text.Length - 1 - taken : taken, fromEnd, facing) > 0)
            {
                taken++;
            }

            if (taken == 0)
            {
                break;
            }

            var first = fromEnd ? text.Length - taken : 0;

            // A tab runs the line on to its next stop rather than by any advance of its own, so what the
            // neighbour gives up is measured from where the line stands at its edge: for the one behind
            // the pair that is where the line left it, and for the one ahead where the reading ends.
            var place = fromEnd
                ? begins[index] + WhiteSpaceWidth(item.Style, tabStyle, text, 0, first, begins[index], flow)
                : edge;

            // The characters are then paid for in the order the line sets them: white space gives up all
            // of its room, a mark only the part of the fullwidth one that faces the pair.
            var cursor = first;

            while (cursor < first + taken)
            {
                if (IsOverhangSpace(text[cursor]))
                {
                    var next = cursor;

                    while (next < first + taken && IsOverhangSpace(text[next]))
                    {
                        next++;
                    }

                    var run = WhiteSpaceWidth(item.Style, tabStyle, text, cursor, next - cursor, place, flow);
                    given += run;
                    place += run;
                    cursor = next;
                    continue;
                }

                var units = TextChunker.UnitsAt(text, cursor);

                given += HangShare(item, text, cursor, fromEnd, facing) *
                    FullwidthRoom(item.Style, flow);
                place += Measure(item.Style, text, cursor, units, flow);
                cursor += units;
            }

            edge = fromEnd ? begins[index] : place;

            if (taken < text.Length)
            {
                break;
            }
        }

        return given;
    }

    /// <summary>
    /// Gets whether a character is one a reading may hang over. CSS Ruby 1 §5.1 names preserved white
    /// space, no-break space and the other space separators - and, of punctuation, only part of the
    /// advance. The pages beside a pair use the ideographic space, a space separator like the rest.
    /// </summary>
    private static Boolean IsOverhangSpace(Char character) =>
        character == '\t' || Char.GetUnicodeCategory(character) == UnicodeCategory.SpaceSeparator;

    /// <summary>
    /// Gets what part of a full character's room a character standing beside a pair gives up to the
    /// reading: all of white space, half of a fullwidth bracket on the blank side that faces the pair,
    /// and a quarter of a middle dot on either side. A mark whose blank has already been taken by
    /// <c>text-spacing-trim</c> has none left to give, and any other character gives up nothing at all -
    /// which is where the reading stops (CSS Ruby 1 §5.1).
    /// </summary>
    private Double HangShare(InlineItem item, String text, Int32 at, Boolean fromEnd, Char pair)
    {
        var character = text[at];

        if (IsOverhangSpace(character))
        {
            return 1;
        }

        var style = item.Style;
        var codePoint = (Int32)character;
        var kind = KindOf(style, codePoint);

        // Only the blank side that looks at the pair is offered: an opening mark keeps its room on its
        // far side and a closing mark on this, and a middle dot is blank on both.
        var share = kind switch
        {
            PunctuationClass.Opening => fromEnd ? 0 : 0.5,
            PunctuationClass.Closing => fromEnd ? 0.5 : 0,
            PunctuationClass.Middle => 0.25,
            _ => 0,
        };

        if (share == 0 ||
            (kind != PunctuationClass.Middle &&
                !IsFullwidthPunctuation(_measurer, style, codePoint)))
        {
            // A narrow parenthesis is a letter the reading must not cover, however the pair is set.
            return 0;
        }

        // The character the pair stands against, and the one beyond, decide whether the blank is still
        // there: a mark already trimmed by `text-spacing-trim` gives up nothing (CSS Ruby 1 §5.1).
        var value = style.TextSpacingTrim;

        if (value.All)
        {
            return 0;
        }

        var inward = at == (fromEnd ? text.Length - 1 : 0)
            ? pair
            : fromEnd ? text[at + 1] : text[at - 1];

        if (value.Adjacent)
        {
            var trims = kind switch
            {
                PunctuationClass.Opening => TextSpacingTrim.TrimsOpening(
                    NeighborOf(_measurer, style, inward, null), style.FontSize),
                PunctuationClass.Closing => TextSpacingTrim.TrimsClosing(
                    NeighborOf(_measurer, style, inward, null), style.FontSize),
                _ => false,
            };

            if (trims)
            {
                return 0;
            }
        }

        return share;
    }

    /// <summary>
    /// Gets the room one full character takes along the line: the ideographic space, which is the width
    /// a fullwidth form is measured in.
    /// </summary>
    private Double FullwidthRoom(ComputedStyle style, WritingModeInfo flow) =>
        Measure(style, "\u3000", flow);

    /// <summary>
    /// Gets how much room a pair must take for itself on one side, past its own characters, when the
    /// reading does not fit in the room that side gives up (CSS Ruby 1 §5.1).
    /// </summary>
    private static Double HangPast(InlineItem item, Double excess, Double room, Boolean fromEnd)
    {
        if (excess <= 0 || item.Box.Style.RubyOverhang != RubyOverhangMode.Spaces)
        {
            return 0;
        }

        // Which side the reading hangs from is `ruby-align`'s business: from the start it asks room of
        // the far side only, from the end of the near side only, and from the centre of both.
        return item.Box.Style.RubyAlign switch
        {
            RubyAlign.Start or RubyAlign.SpaceBetween => fromEnd ? 0 : Math.Max(0, excess - room),
            RubyAlign.End => fromEnd ? Math.Max(0, excess - room) : 0,
            _ => Math.Max(0, (excess / 2) - room),
        };
    }

    /// <summary>
    /// Gets the room a ruby pair takes along the line: the wider of its base and its reading, less
    /// what the reading may hang over the text beside it (CSS Ruby 1 §5.1).
    /// </summary>
    private static Double RubyExtent(
        InlineItem item,
        Double baseWidth,
        Double annotationWidth,
        Double before,
        Double after)
    {
        var excess = annotationWidth - baseWidth;

        if (excess <= 0)
        {
            return baseWidth;
        }

        if (item.Box.Style.RubyOverhang == RubyOverhangMode.Spaces)
        {
            // A reading may cover the spaces beside it and no further: once those run out, the pair
            // grows by what is left on that side.
            return baseWidth +
                HangPast(item, excess, before, fromEnd: true) +
                HangPast(item, excess, after, fromEnd: false);
        }

        // What `auto` allows is the user agent's choice. Chrome lets a reading hang half an em past
        // its base in total - a quarter of it on each side - whatever stands beside it.
        return Math.Max(baseWidth, annotationWidth - (item.Style.GetFont().Size / 2));
    }

    /// <summary>
    /// Gets where a box narrower than its ruby column stands in it, which is what <c>ruby-align</c>
    /// says (CSS Ruby 1 §4.3).
    /// </summary>
    private static Double RubyAligned(RubyAlign align, Double extent, Double width) => align switch
    {
        RubyAlign.Start or RubyAlign.SpaceBetween => 0,
        RubyAlign.End => extent - width,
        _ => (extent - width) / 2,
    };

    /// <summary>
    /// Gets how far a pair's own characters stand inside the room it takes: what the reading took from
    /// the near side is what pushes them along, and the reading is laid over them from there. Nothing is
    /// pushed where the room the pair may take is the user agent's choice, which hangs the reading past
    /// the pair evenly instead (CSS Ruby 1 §4.3, §5.1).
    /// </summary>
    private static Double? RubyHang(
        InlineItem item,
        Double extent,
        Double baseWidth,
        Double annotationWidth,
        Double after) =>
        item.Box.Style.RubyOverhang == RubyOverhangMode.Spaces
            ? extent - baseWidth - HangPast(item, Math.Max(0, annotationWidth - baseWidth), after, fromEnd: false)
            : null;

    /// <summary>
    /// Measures a range of text the way a column advances it. Upright text advances by its em box
    /// rather than by its horizontal width, so a Latin letter that would leave room to spare in a line
    /// still takes a whole em in a column, and text combined upright is compressed into a single em.
    /// </summary>
    private static Double MeasureColumn(
        String source,
        Int32 origin,
        Int32 length,
        FontStyle font,
        Double fontSize,
        WritingModeInfo flow,
        ITextMeasurer measurer)
    {
        var total = 0.0;

        foreach (var run in new OrientedPieces(source, origin, length, flow))
        {
            total += run.Advance(font, measurer, fontSize, flow);
        }

        return total;
    }

    /// <summary>
    /// Walks the pieces a range of text is measured and drawn in: wherever the orientation changes, and
    /// wherever <c>text-combine-upright</c> sets a short run as one box. Text along a line is one piece,
    /// the whole range, measured exactly as it always was.
    /// </summary>
    /// <remarks>
    /// Walked rather than collected, because a column of upright text is one character to a piece - the
    /// smallest thing the engine splits at and the one asked for most often. On a page of CJK the walk
    /// runs tens of thousands of times per layout, and a list per piece is a heap object per character;
    /// the pieces are ranges of the same string besides, not copies of it.
    /// </remarks>
    private struct OrientedPieces
    {
        private readonly String _source;
        private readonly WritingModeInfo _flow;
        private readonly Int32 _origin;
        private readonly Int32 _length;
        private Int32 _index;
        private Boolean _taken;

        public OrientedPieces(String source, Int32 origin, Int32 length, WritingModeInfo flow)
        {
            _source = source;
            _flow = flow;
            _origin = origin;
            _length = length;
            _index = origin;
            _taken = false;
            Current = default;
        }

        public OrientedPieces GetEnumerator() => this;

        public OrientedRun Current { get; private set; }

        public Boolean MoveNext()
        {
            if (!_flow.IsVertical)
            {
                if (_taken || _length == 0)
                {
                    return false;
                }

                _taken = true;
                Current = OrientedRun.Line(_source, _origin, _length);
                return true;
            }

            var end = _origin + _length;

            if (_index >= end)
            {
                return false;
            }

            // Stepped by code point, not by character: the pair written above the BMP is one character of
            // the text and stands in one cell, and taken by character it was two - each half turned or
            // upright by what a lone surrogate says about nothing.
            if (_flow.CombinesUpright && TextChunker.IsCombinable(TextChunker.CodePointAt(_source, _index)))
            {
                var count = 0;

                while (_index + count < end &&
                       TextChunker.IsCombinable(TextChunker.CodePointAt(_source, _index + count)))
                {
                    count += TextChunker.UnitsAt(_source, _index + count);
                }

                // Only a short run reads as one box. A longer one is ordinary text that happens to be
                // turned, and combining it would squeeze a whole word into a single em.
                if (count <= 4)
                {
                    Current = OrientedRun.Combined(_source, _index, count);
                    _index += count;
                    return true;
                }
            }

            var start = _index;
            var upright = _flow.IsUpright(TextChunker.CodePointAt(_source, start));

            _index += TextChunker.UnitsAt(_source, start);

            while (_index < end && _flow.IsUpright(TextChunker.CodePointAt(_source, _index)) == upright)
            {
                _index += TextChunker.UnitsAt(_source, _index);
            }

            Current = OrientedRun.Of(_source, start, _index - start, upright);
            return true;
        }
    }

    /// <summary>
    /// Measures one piece of a range: how far it advances and which way its glyphs are set. Horizontal
    /// text is measured as the whole run it is, exactly as before, so the horizontal path is unchanged
    /// down to the numbers.
    /// </summary>
    private MeasuredRun Measure(ComputedStyle style, FontStyle font, OrientedRun run, WritingModeInfo flow)
    {
        // The same two spacings the intrinsic arithmetic charges: the letter spacing once per character of
        // the run, and the room between words once per separator it is drawn with.
        var spacing = (style.LetterSpacing * SpacingUnits(run.Source, run.Start, run.Length, flow)) +
            (style.WordSpacing * TextChunker.WordSeparators(run.Source, run.Start, run.Length));

        var width = flow.IsVertical
            ? run.Advance(font, _measurer, style.FontSize, flow) + spacing
            : _measurer.MeasureWidth(run.Source.Substring(run.Start, run.Length), font) + spacing;

        return new MeasuredRun(
            run.Source,
            run.Start,
            run.Length,
            width + AutoSpaceWithin(_measurer, style, run.Source, run.Start, run.Length, flow) -
                TrimWithin(_measurer, style, run.Source, run.Start, run.Length, flow),
            PinOrientation(flow, run),
            TextChunker.IsCollapsible(run.Source, run.Start, run.Length));
    }

    /// <summary>
    /// Gets the room a run of white space takes where it holds tabs. A tab is not a glyph with an
    /// advance of its own: it runs the line on to the next multiple of the tab size counted from where
    /// the line began (CSS Text 3 §4.2), so the answer depends on how far the line has already come and
    /// has to be asked of the line rather than of the font.
    /// </summary>
    private Double WhiteSpaceWidth(
        ComputedStyle style,
        ComputedStyle tabStyle,
        String source,
        Int32 start,
        Int32 length,
        Double position,
        WritingModeInfo flow)
    {
        if (source.IndexOf('\t', start, length) < 0)
        {
            return Measure(style, source, start, length, flow);
        }

        // The stop is the width of a space in the font of the nearest block container, not in the
        // inline element the tab happens to stand in: an integer tab size is that many spaces of the
        // block's font, while a length is already absolute (CSS Text 3 §4.2).
        var unit = style.TabLength ?? style.TabUnits * Measure(tabStyle, " ", flow);

        if (unit <= 0)
        {
            return 0;
        }

        var width = 0.0;
        var index = start;
        var end = start + length;

        while (index < end)
        {
            if (source[index] == '\t')
            {
                var at = position + width;
                var next = (Math.Floor(at / unit) + 1) * unit;

                // A tab that would move the line less than half a character is taken to the stop after
                // the nearest one instead, so a tab does not become a hairline space (CSS Text 3 §4.2).
                var half = Measure(tabStyle, "0", flow) / 2;

                if (next - at < half)
                {
                    next += unit;
                }

                width = next - position;
                index++;
                continue;
            }

            var from = index;

            while (index < end && source[index] != '\t')
            {
                index++;
            }

            width += Measure(style, source, from, index - from, flow);
        }

        return width;
    }

    /// <summary>
    /// Gets the flow a piece of text is drawn in: the orientation the piece was split on, so that what
    /// the measurer treats as upright is also what the painter draws upright.
    /// </summary>
    /// <remarks>
    /// A combined run is drawn as one upright box and keeps the mode's own orientation; the rest are
    /// pinned to the orientation they were split on. Text that is not vertical is handed back as it
    /// is, because orientation means nothing along a line.
    /// </remarks>
    private static WritingModeInfo PinOrientation(WritingModeInfo flow, OrientedRun run)
    {
        if (!flow.IsVertical)
        {
            return flow;
        }

        return run.IsCombined
            ? flow
            : new WritingModeInfo(
                flow.Mode,
                run.IsUpright ? TextOrientation.Upright : TextOrientation.Sideways,
                false,
                flow.Direction);
    }

    /// <summary>
    /// Gets the flow a piece of text that is set as a whole - a ruby base, say - is drawn in, which is
    /// the orientation of its first piece. That is the whole of it whenever the base is the single
    /// character a base usually is; a base that genuinely mixes orientations takes the first, the same
    /// approximation the reading beside it already makes.
    /// </summary>
    private static WritingModeInfo ResolveOrientation(String text, WritingModeInfo flow)
    {
        if (!flow.IsVertical)
        {
            return flow;
        }

        foreach (var run in new OrientedPieces(text, 0, text.Length, flow))
        {
            return PinOrientation(flow, run);
        }

        return flow;
    }

    /// <summary>
    /// Gets where a line's runs start along the inline axis: aligned in what the line's indentation
    /// leaves of the content box, and then moved along by that indentation.
    /// </summary>
    private static Double Aligned(Line line, Double maxInline) =>
        line.Fragments.Count == 0
            ? 0
            : AlignmentOffset(
                line.Alignment,
                maxInline - line.Indent,
                line.Used,
                line.Fragments[0].Flow) + line.Indent;

    /// <summary>
    /// Gives the lines the block's <c>text-align-last</c> speaks for - the one that ends the block and
    /// every one a forced break ended - the alignment that property states, and takes the offset they
    /// stand at with it (CSS Text 3 §6.3).
    /// </summary>
    /// <remarks>
    /// <c>auto</c> is not a fifth way of aligning a line but a delegation: the affected line takes
    /// <c>text-align</c> as it is, unless that asks for justification. A justified line the room was not
    /// spread across does not keep the offset its spread neighbours were given - it stands at its start
    /// edge, which is what <c>text-align-last: auto</c> comes to.
    /// </remarks>
    private static void AlignLastLines(List<Line> lines, Double maxInline)
    {
        // Walked from the end, because the line that ends the block is the one the property is named for
        // and the rest it speaks for are only those the page was told to break.
        for (var position = lines.Count - 1; position >= 0; position--)
        {
            var line = lines[position];

            if (line.IsInterlineNote ||
                line.Fragments.Count == 0 ||
                (!line.ForceEnded && position != lines.Count - 1))
            {
                continue;
            }

            var style = line.Governing ?? line.Fragments[0].Style;
            var alignLast = style.TextAlignLast;
            var aligned = alignLast.Equals("auto", StringComparison.OrdinalIgnoreCase)
                ? (style.TextAlign.Equals("justify", StringComparison.OrdinalIgnoreCase)
                    ? "start"
                    : style.TextAlign)
                : alignLast;

            if (aligned.Equals(line.Alignment, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            line.Alignment = aligned;
            line.Offset = Aligned(line, maxInline);
        }
    }

    /// <summary>
    /// Spreads the room a justified line did not use across the spaces inside it, which is all
    /// inter-word justification is (CSS Text 3 §5.7.1). Which lines are justified is the alignment the
    /// line was given by the pass above, so the line that ends the block and the ones a forced break
    /// ended are spread only when <c>text-align-last</c> says so. A line with no space left in it has
    /// nowhere honest to put the room, and is left standing as it is - the engine does not space out
    /// letters.
    /// </summary>
    /// <remarks>
    /// The room is owed out as steps of a running total rather than as one share repeated, so the
    /// fractions fall on different spaces and the last of them lands exactly on the edge the line was
    /// given. A plan is kept and asked again, so every line starts from nothing owed: the room it was
    /// given can be different the next time round.
    /// </remarks>
    private static void DistributeSpace(List<Line> lines)
    {
        for (var position = 0; position < lines.Count; position++)
        {
            var line = lines[position];

            // A plan is kept and asked again, and the room a line was given can be different the next
            // time round: every expansion, including the two rows of a note, comes back out first.
            ResetExpansion(line);

            if (line.Fragments.Count == 0 ||
                !line.Alignment.Equals("justify", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var bodyPlaces = 0;
            var notes = new List<NoteExpansion>();

            for (var index = 0; index < line.Fragments.Count; index++)
            {
                var fragment = line.Fragments[index];

                if (fragment.Note is not { } note)
                {
                    bodyPlaces += Places(line, index);
                    continue;
                }

                var firstWidth = note.First?.Used ?? 0;
                var secondWidth = note.Second?.Used ?? 0;
                var naturalContent = Math.Max(firstWidth, secondWidth);

                notes.Add(new NoteExpansion(
                    fragment,
                    note.First,
                    note.Second,
                    Math.Max(0, fragment.Width - naturalContent),
                    note.EndsOuterLine,
                    note.First is null ? 0 : LinePlaces(note.First),
                    note.Second is null ? 0 : LinePlaces(note.Second),
                    firstWidth,
                    secondWidth));
            }

            var free = (line.Extent - line.Indent) - line.Used;

            if (free <= 0)
            {
                foreach (var note in notes)
                {
                    if (note.EndsLine)
                    {
                        ApplyNoteExpansion(note, 0);
                    }
                }

                continue;
            }

            if (notes.Count == 0)
            {
                if (bodyPlaces > 0)
                {
                    ExpandByShare(line, free / bodyPlaces);
                }

                continue;
            }

            Double Growth(Double share)
            {
                var growth = bodyPlaces * share;

                foreach (var note in notes)
                {
                    var first = note.FirstWidth + (note.FirstPlaces * share);
                    var second = note.SecondWidth + (note.SecondPlaces * share);
                    growth += Math.Max(first, second) - Math.Max(note.FirstWidth, note.SecondWidth);
                }

                return growth;
            }

            if (bodyPlaces == 0 &&
                !notes.Exists(note => note.FirstPlaces > 0 || note.SecondPlaces > 0))
            {
                continue;
            }

            var high = 1.0;

            while (Growth(high) < free && high < 1_000_000_000)
            {
                high *= 2;
            }

            var low = 0.0;

            for (var step = 0; step < 60; step++)
            {
                var middle = (low + high) / 2;

                if (Growth(middle) < free)
                {
                    low = middle;
                }
                else
                {
                    high = middle;
                }
            }

            var share = (low + high) / 2;

            if (share <= 0)
            {
                continue;
            }

            ExpandByShare(line, share);

            foreach (var note in notes)
            {
                ApplyNoteExpansion(note, share);
            }
        }
    }

    /// <summary>Restores every fragment and note subline to the extent it had before justification.</summary>
    private static void ResetExpansion(Line line)
    {
        if (line.NaturalUsed is { } used)
        {
            line.Used = used;
        }
        else
        {
            line.NaturalUsed = line.Used;
        }

        if (line.NaturalExtent is { } extent)
        {
            line.Extent = extent;
        }
        else
        {
            line.NaturalExtent = line.Extent;
        }

        foreach (var fragment in line.Fragments)
        {
            fragment.Expand(0, 0);

            if (fragment.Note is { First: { } first })
            {
                ResetExpansion(first);
            }

            if (fragment.Note is { Second: { } second })
            {
                ResetExpansion(second);
            }
        }
    }

    /// <summary>
    /// Gives every expansion place on a line the same share and returns the width it added. The room is
    /// owed out on a running total so fractions fall on different places and the last one lands exactly.
    /// </summary>
    private static Double ExpandByShare(Line line, Double share)
    {
        var owed = 0.0;
        var given = 0.0;

        for (var index = 0; index < line.Fragments.Count; index++)
        {
            var fragment = line.Fragments[index];
            var count = Places(line, index);

            if (count == 0)
            {
                continue;
            }

            owed += share * count;
            fragment.Expand((owed - given) / count, count);
            given = owed;
        }

        return given;
    }

    /// <summary>
    /// Expands a note's two sublines with the line-wide share. A subline that reaches the outer line
    /// end also fills the natural width of the wider subline, which is the room inside the note box.
    /// </summary>
    private static void ApplyNoteExpansion(NoteExpansion note, Double share)
    {
        var target = Math.Max(
            note.FirstWidth + (note.FirstPlaces * share),
            note.SecondWidth + (note.SecondPlaces * share));
        var firstShare = note.EndsLine &&
            note.First is not null &&
            note.FirstPlaces > 0 &&
            note.FirstWidth < target
            ? (target - note.FirstWidth) / note.FirstPlaces
            : share;
        var secondShare = note.EndsLine &&
            note.Second is not null &&
            note.SecondPlaces > 0 &&
            note.SecondWidth < target
            ? (target - note.SecondWidth) / note.SecondPlaces
            : share;
        var firstGrowth = note.First is null ? 0 : ExpandByShare(note.First, firstShare);
        var secondGrowth = note.Second is null ? 0 : ExpandByShare(note.Second, secondShare);
        var firstUsed = note.FirstWidth + firstGrowth;
        var secondUsed = note.SecondWidth + secondGrowth;
        var content = Math.Max(firstUsed, secondUsed);

        if (note.First is { } first)
        {
            first.Used = firstUsed;
            first.Extent = content;
            first.Offset = 0;
        }

        if (note.Second is { } second)
        {
            second.Used = secondUsed;
            second.Extent = content;
            second.Offset = 0;
        }

        note.Fragment.SetJustifiedWidth(content + note.Edges);
    }

    /// <summary>Gets every expansion place held by the fragments of one line.</summary>
    private static Int32 LinePlaces(Line line)
    {
        var places = 0;

        for (var index = 0; index < line.Fragments.Count; index++)
        {
            places += Places(line, index);
        }

        return places;
    }

    /// <summary>
    /// Gets how many places the fragment at this point of the line holds a share of the room for: the
    /// runs of white space it stands between and, where the text expands between characters, the seams
    /// between the ideographs it is made of. A place is counted for the fragment on its left, whose
    /// advance carries the room, which is also how a fragment that ends in a space already worked; the
    /// seam into the fragment after it is therefore this one's, and is what lets a line of Chinese -
    /// where two ideographs may be split between the pieces the markup made - be paid in full.
    /// </summary>
    /// <remarks>
    /// The place a line trails into is left out: a line is not justified into the room past its own edge
    /// (CSS Text 3 §5.7.1), whichever lines the block ends up spreading.
    /// </remarks>
    private static Int32 Places(Line line, Int32 index)
    {
        var fragment = line.Fragments[index];

        // A glyph hung off the line's edge takes no room in the line and no place in it: the room a
        // justified line spreads is owed to the text the line still holds, and a share falling on the
        // seam into a hung glyph is a share falling into the margin (CSS Text 3 §8.2.1).
        if (fragment.HangSide != 0)
        {
            return 0;
        }

        var last = fragment.Start + fragment.Length - 1;
        var justify = fragment.Style.TextJustify;
        var places = 0;
        var atEdge = false;

        foreach (var point in TextClusters.ExpansionPoints(
            fragment.Source, fragment.Start, fragment.Length, justify))
        {
            if (point < last)
            {
                places++;
                continue;
            }

            // The room past the edge of the line is nowhere to put, and a place standing at the edge of
            // the fragment already carries the seam into what comes next.
            atEdge = true;

            if (!EndsLine(line, index))
            {
                places++;
            }
        }

        // A fragment that carries no text - the empty inline box a page puts between two words - has no
        // first and no last character to ask about, so the seam either side of it is not a question.
        if (atEdge || fragment.Length == 0 || EndsLine(line, index))
        {
            return places;
        }

        var following = line.Fragments[index + 1];

        if (following.Length == 0)
        {
            return places;
        }

        return places + (TextClusters.Seam(
            fragment.Source, last, following.Source, following.Start, justify)
            ? 1
            : 0);
    }

    /// <summary>
    /// Gets whether the fragment is the one the text of the line ends with: the last fragment, or the one
    /// before a glyph hung off the line's edge, whose trailing place is the line's trailing as well.
    /// </summary>
    private static Boolean EndsLine(Line line, Int32 index) =>
        index == line.Fragments.Count - 1 || line.Fragments[index + 1].HangSide != 0;

    /// <summary>
    /// Gets the distance the first line of a box is set in from the edge it begins at.
    /// </summary>
    /// <remarks>
    /// A percentage among the indentation is a share of the containing block's inline extent, which is
    /// the block the text sits in rather than the one it makes - the same extent every other length of
    /// the line resolves against.
    /// </remarks>
    /// <summary>
    /// Gets the distance the first line of the container is set in by, as a percentage of the
    /// container's own content box along the line (CSS Text 3 §8.1, measured: a border-box 120px
    /// div with 20px of right inset indents 50px for `text-indent: 50%`, not 50% of the containing
    /// block - text-indent-percentage-002).
    /// </summary>
    /// <summary>
    /// The distance a block's lines are set in by, with the two modifiers that say which of them carry it:
    /// <c>hanging</c> puts the distance on every line but the ones the rule names, and <c>each-line</c>
    /// adds the line after every forced break to the ones it names (CSS Text 3 §5.2).
    /// </summary>
    private readonly record struct TextIndentation(Double Distance, Boolean Hanging, Boolean EachLine)
    {
        /// <summary>
        /// Gets whether a line carries the distance. <paramref name="first"/> says the line begins the
        /// block's content and <paramref name="afterBreak"/> says a forced break ended the line before it,
        /// which are the two lines the property names; <c>hanging</c> puts it on every other line.
        /// </summary>
        internal Boolean Carries(Boolean first, Boolean afterBreak)
        {
            var named = first || (EachLine && afterBreak);

            return Hanging ? !named : named;
        }
    }

    private static TextIndentation Indentation(Box container, WritingModeInfo flow, Double contentInline)
    {
        var indent = container.Style.TextIndent;

        // A run of inline content that a block child broke away from the head of its box is wrapped in an
        // anonymous block of its own, and only the first of those wraps is the line the indentation was
        // written for: the block's first line stands where its content begins, not where each fragment of
        // it happens to resume (CSS Text 3 §5.2).
        if (!indent.HasValue || (container.IsAnonymous && !container.StartsParentContent))
        {
            return default;
        }

        return new TextIndentation(
            indent.Resolve(contentInline),
            container.Style.TextIndentHanging,
            container.Style.TextIndentEachLine);
    }

    /// <summary>
    /// Gets where the line's content starts along the inline axis, which is what <c>text-align</c>
    /// asks for and which end the line reads from when it asks for nothing.
    /// </summary>
    /// <remarks>
    /// <c>start</c> and <c>end</c> are logical and follow the direction the line reads in, while
    /// <c>left</c> and <c>right</c> are physical and do not; the initial value is <c>start</c>, so an
    /// unaligned right-to-left line sits against its right edge.
    /// </remarks>
    internal static Double AlignmentOffset(String textAlign, Double maxInline, Double used, WritingModeInfo flow)
    {
        if (maxInline <= used)
        {
            return 0;
        }

        var free = maxInline - used;

        if (textAlign.Equals("center", StringComparison.OrdinalIgnoreCase))
        {
            return free / 2;
        }

        // The offset is measured from the edge the line begins at, which `Map` then turns into a
        // physical place; only the two physical keywords have to look at which edge that is.
        if (textAlign.Equals("left", StringComparison.OrdinalIgnoreCase))
        {
            return flow.InlineSign > 0 ? 0 : free;
        }

        if (textAlign.Equals("right", StringComparison.OrdinalIgnoreCase))
        {
            return flow.InlineSign > 0 ? free : 0;
        }

        // `start`, `justify` and the initial value begin at the start; `end` moves over to the far end.
        return textAlign.Equals("end", StringComparison.OrdinalIgnoreCase) ? free : 0;
    }

    /// <summary>A piece of a text node that shares one orientation and is measured and drawn as one.</summary>
    private readonly struct OrientedRun
    {
        private OrientedRun(String source, Int32 start, Int32 length, Boolean upright, Boolean combined)
        {
            Source = source;
            Start = start;
            Length = length;
            IsUpright = upright;
            IsCombined = combined;
        }

        /// <summary>Gets the string the run is a piece of.</summary>
        public String Source { get; }

        /// <summary>Gets where in that string the run begins.</summary>
        public Int32 Start { get; }

        /// <summary>Gets how many characters the run takes.</summary>
        public Int32 Length { get; }

        /// <summary>
        /// Gets the run as a string, which is what the measurer speaks: made on asking, because most
        /// runs are only ever advanced over and never handed to anything that wants the characters.
        /// </summary>
        public String Text => Source.Substring(Start, Length);

        public Boolean IsUpright { get; }

        /// <summary>Gets whether the run is set as a single upright box rather than turned.</summary>
        public Boolean IsCombined { get; }

        public static OrientedRun Combined(String source, Int32 start, Int32 length) => new(source, start, length, true, true);

        /// <summary>Gets the whole of a range along a line, which is not split by orientation at all.</summary>
        public static OrientedRun Line(String source, Int32 start, Int32 length) => new(source, start, length, false, false);

        /// <summary>
        /// Gets a piece by the one distinction a column draws on it: upright characters take their em
        /// box and are drawn one to a cell, turned ones take the width they had along a line.
        /// </summary>
        public static OrientedRun Of(String source, Int32 start, Int32 length, Boolean upright) =>
            new(source, start, length, upright, false);

        /// <summary>
        /// Gets the distance the run takes along the inline axis of the column it sits in: an upright
        /// character takes its em box, a turned run the width it would have had in a line, and a
        /// combined run the single em it is compressed into.
        /// </summary>
        public Double Advance(FontStyle font, ITextMeasurer measurer, Double fontSize, WritingModeInfo flow)
        {
            if (IsCombined)
            {
                return fontSize;
            }

            if (!IsUpright)
            {
                return measurer.MeasureAdvance(Text, font, flow);
            }

            var total = 0.0;

            // Walked over the source rather than over a string of this run's: an upright run is measured
            // character by character, and the answer is the character's and the font's, so neither the
            // character nor a string holding it is made afresh to ask for it. It is walked by code point,
            // because one character of the text is one cell of the column however many of the string's
            // characters it takes to write it.
            var verticalWidth = font.AsksForVerticalWidth;

            for (var index = Start; index < Start + Length;)
            {
                var codePoint = TextChunker.CodePointAt(Source, index);
                var advance = verticalWidth
                    ? measurer.MeasureUprightAdvance(codePoint, font)
                    : measurer.MeasureAdvance(codePoint, font, flow);

                // The forms `vhal` and `halt` stand for are half-width by definition: a glyph the
                // feature leaves alone keeps its own width, and one it substitutes for a
                // proportional mark is set in half the em - a font whose vertical metrics say
                // nothing for that glyph answers with its horizontal advance, which is narrower
                // still and is not what the feature asks for (CSS Writing Modes 4 SS4.2, OpenType
                // 'vhal'/'halt'; measured: the text-spacing-trim-*-vrl pages, where Chrome stands
                // the U+2018 of a 'vhal' run in half of the 20-pixel em and the face's own answer
                // is 5.56).
                var floor = verticalWidth ? fontSize / 2 : fontSize;

                total += Math.Max(advance, floor);

                index += TextChunker.UnitsAt(Source, index);
            }

            return total;
        }
    }

    /// <summary>An oriented run together with the advance it was measured to, ready for the builder.</summary>
    private readonly struct MeasuredRun
    {
        public MeasuredRun(String source, Int32 start, Int32 length, Double width, WritingModeInfo flow, Boolean isWhiteSpace)
        {
            Source = source;
            Start = start;
            Length = length;
            Width = width;
            Flow = flow;
            IsWhiteSpace = isWhiteSpace;
        }

        /// <summary>Gets the string the run is a piece of, with the run's own place in it.</summary>
        public String Source { get; }

        public Int32 Start { get; }

        public Int32 Length { get; }

        public Double Width { get; }

        public WritingModeInfo Flow { get; }

        public Boolean IsWhiteSpace { get; }
    }

    private readonly struct InlineItem
    {
        private InlineItem(
            String? text,
            ComputedStyle style,
            Box box,
            InlineAncestor? inline,
            Boolean isBreak,
            Boolean isReplaced,
            String? annotationText = null,
            ComputedStyle annotationStyle = default,
            Box? annotationBox = null,
            Boolean atomic = false,
            Boolean outOfFlow = false,
            Boolean empty = false,
            NoteSource? noteSource = null,
            InterlineNoteSource? interlineNote = null,
            InterlineFlowAnchor? interlineAnchor = null,
            MarginNoteSource? marginNote = null,
            WritingModeInfo? flow = null)
        {
            Text = text;
            Style = style;
            Box = box;
            Inline = inline;
            IsBreak = isBreak;
            IsReplaced = isReplaced;
            AnnotationText = annotationText;
            AnnotationStyle = annotationStyle;
            AnnotationBox = annotationBox;
            IsAtomic = atomic;
            IsOutOfFlow = outOfFlow;
            IsEmpty = empty;
            NoteSource = noteSource;
            InterlineNote = interlineNote;
            InterlineAnchor = interlineAnchor;
            MarginNote = marginNote;
            Flow = flow;
        }

        public String? Text { get; }

        public ComputedStyle Style { get; }

        public Box Box { get; }

        /// <summary>
        /// Gets the innermost inline element the item sits inside, or null when it sits in none. The
        /// rest of the chain is reached through <see cref="InlineAncestor.Parent"/>.
        /// </summary>
        public InlineAncestor? Inline { get; }

        public Boolean IsBreak { get; }

        public Boolean IsReplaced { get; }

        /// <summary>
        /// Gets whether the item is an inline-block: an atomic box the line places whole, whose size is
        /// the layout's own to work out rather than something the box resolves by itself.
        /// </summary>
        public Boolean IsAtomic { get; }

        /// <summary>
        /// Gets whether the item is a box that left the flow. It stands on the line only to say where it
        /// would have been: it takes no room, and it is placed against a containing block of its own
        /// rather than drawn as part of the line.
        /// </summary>
        public Boolean IsOutOfFlow { get; }

        /// <summary>
        /// Gets whether the item is an inline element with nothing inside it that takes room: the box
        /// stands on the line for the margins, borders and padding it carries along it, and draws no
        /// text (CSS 2.1 §9.4.2, §8.1).
        /// </summary>
        public Boolean IsEmpty { get; }

        /// <summary>Gets the source of the inline note this item carries, or null for ordinary content.</summary>
        public NoteSource? NoteSource { get; }

        /// <summary>Gets the interline note whose zero-width position this item marks.</summary>
        public InterlineNoteSource? InterlineNote { get; }

        /// <summary>Gets the midpoint target that begins an interline note in its shared flow.</summary>
        public InterlineFlowAnchor? InterlineAnchor { get; }

        /// <summary>Gets the margin note whose zero-width declaration this item marks.</summary>
        public MarginNoteSource? MarginNote { get; }

        /// <summary>Gets the flow this run uses where it differs from its source box's flow.</summary>
        public WritingModeInfo? Flow { get; }

        /// <summary>Gets the annotation of a ruby item, or null when the item carries none.</summary>
        public String? AnnotationText { get; }

        /// <summary>Gets the style the annotation is set in, which the <c>rt</c> element shrank for it.</summary>
        public ComputedStyle AnnotationStyle { get; }

        /// <summary>Gets the box the annotation came from, so its own geometry can be traced back to the tree.</summary>
        public Box? AnnotationBox { get; }

        /// <summary>Gets whether the item is a ruby pair: base text with an annotation over it.</summary>
        public Boolean IsRuby => AnnotationText is not null;

        public static InlineItem CreateText(
            String text,
            ComputedStyle style,
            Box box,
            InlineAncestor? inline,
            WritingModeInfo? flow = null) =>
            new(text, style, box, inline, false, false, flow: flow);

        public static InlineItem CreateBreak(ComputedStyle style, Box box, InlineAncestor? inline) =>
            new(null, style, box, inline, true, false);

        public static InlineItem CreateReplaced(Box box, InlineAncestor? inline) =>
            new(null, box.Style, box, inline, false, true);

        public static InlineItem CreateAtomic(Box box, InlineAncestor? inline) =>
            new(null, box.Style, box, inline, false, false, atomic: true);

        public static InlineItem CreateOutOfFlow(Box box, InlineAncestor? inline) =>
            new(null, box.Style, box, inline, false, false, outOfFlow: true);

        public static InlineItem CreateEmpty(Box box, InlineAncestor inline) =>
            new(String.Empty, box.Style, box, inline, false, false, empty: true);

        public static InlineItem CreateRuby(
            String text,
            ComputedStyle style,
            Box box,
            String annotationText,
            ComputedStyle annotationStyle,
            Box annotationBox,
            InlineAncestor? inline) =>
            new(text, style, box, inline, false, false, annotationText, annotationStyle, annotationBox);

        public static InlineItem CreateNote(NoteSource source, Box box, InlineAncestor? inline) =>
            new(null, box.Style, box, inline, false, false, noteSource: source);

        public static InlineItem CreateInterlineNote(NoteSource source, Box box, InlineAncestor? inline) =>
            new(null, box.Style, box, inline, false, false,
                interlineNote: new InterlineNoteSource(source, box));

        public static InlineItem CreateInterlineAnchor(Int32 slot, Double position, Box box) =>
            new(null, box.Style, box, null, false, false,
                interlineAnchor: new InterlineFlowAnchor(slot, position));

        public static InlineItem CreateMarginNote(Box box, InlineAncestor? inline) =>
            new(null, box.Style, box, inline, false, false,
                marginNote: new MarginNoteSource(box));
    }

    private sealed record InterlineNoteSource(NoteSource Content, Box Box);

    private readonly record struct InterlineFlowAnchor(Int32 Slot, Double Position);

    /// <summary>The styled text an inline note holds, kept as spans so nested markup survives.</summary>
    private sealed class NoteSource
    {
        private NoteSource(
            String text,
            IReadOnlyList<NoteSpan> spans,
            IReadOnlyList<NestedInterlineNote> nestedInterline,
            IReadOnlyList<NestedMarginNote> nestedMargin)
        {
            Text = text;
            Spans = spans;
            NestedInterline = nestedInterline;
            NestedMargin = nestedMargin;
        }

        public String Text { get; }

        public IReadOnlyList<NoteSpan> Spans { get; }

        /// <summary>Gets the interline notes hoisted out of this inline note's text.</summary>
        public IReadOnlyList<NestedInterlineNote> NestedInterline { get; }

        /// <summary>Gets the margin notes hoisted out of this note's text.</summary>
        public IReadOnlyList<NestedMarginNote> NestedMargin { get; }

        /// <summary>Gets the styled span holding the character at the text position.</summary>
        public NoteSpan SpanAt(Int32 position)
        {
            var low = 0;
            var high = Spans.Count - 1;

            while (low <= high)
            {
                var middle = low + ((high - low) / 2);
                var span = Spans[middle];

                if (position < span.Start)
                {
                    high = middle - 1;
                }
                else if (position >= span.Start + span.Length)
                {
                    low = middle + 1;
                }
                else
                {
                    return span;
                }
            }

            throw new InvalidOperationException("A note's text position must belong to one of its spans.");
        }

        /// <summary>
        /// Gets whether the note still has something to draw after its text nodes' white-space rules
        /// collapse their spaces. A note made only of ordinary spaces is the same as no note at all.
        /// </summary>
        public Boolean HasInk
        {
            get
            {
                foreach (var span in Spans)
                {
                    for (var at = 0; at < span.Length; at++)
                    {
                        if (!span.Style.CollapsesSpaces ||
                            !TextChunker.IsCollapsible(Text, span.Start + at, 1))
                        {
                            return true;
                        }
                    }
                }

                return false;
            }
        }

        public static NoteSource Read(
            Box note,
            Boolean hoistInterline = false,
            Boolean hoistMargin = false)
        {
            var text = new System.Text.StringBuilder();
            var spans = new List<NoteSpan>();
            var nested = new List<NestedInterlineNote>();
            var nestedMargin = new List<NestedMarginNote>();
            Append(note, text, spans, nested, nestedMargin, new List<Box>(), hoistInterline, hoistMargin);

            return new NoteSource(text.ToString(), spans, nested, nestedMargin);
        }

        private static void Append(
            Box box,
            System.Text.StringBuilder text,
            List<NoteSpan> spans,
            List<NestedInterlineNote> nested,
            List<NestedMarginNote> nestedMargin,
            List<Box> ancestors,
            Boolean hoistInterline,
            Boolean hoistMargin)
        {
            if (box.Kind == BoxKind.Text)
            {
                if (box.Text is { Length: > 0 } run)
                {
                    // Segment breaks in the source are ordinary collapsible spaces unless this
                    // element's white-space mode preserves them. Keeping them as line feeds here
                    // would turn the pretty-printing of the document into hard note breaks.
                    if (box.Style.CollapsesSpaces &&
                        !box.Style.KeepsSegmentBreaks &&
                        (run.Contains('\r') || run.Contains('\n')))
                    {
                        run = run.Replace('\r', ' ').Replace('\n', ' ');
                    }

                    spans.Add(new NoteSpan(
                        text.Length,
                        run.Length,
                        box.Style,
                        box,
                        box.Flow,
                        ancestors.ToArray()));
                    text.Append(run);
                }

                return;
            }

            if (box.Kind == BoxKind.LineBreak)
            {
                text.Append('\n');
                return;
            }

            if (hoistInterline &&
                box.Kind == BoxKind.Inline &&
                box.Style.NotePlacement == NotePlacement.Interline)
            {
                var content = Read(box, hoistMargin: true);
                nested.Add(new NestedInterlineNote(
                    text.Length,
                    new InterlineNoteSource(content, box),
                    ancestors.ToArray()));
                return;
            }

            if (hoistMargin &&
                box.Kind == BoxKind.Inline &&
                box.Style.NotePlacement == NotePlacement.Margin)
            {
                nestedMargin.Add(new NestedMarginNote(
                    text.Length,
                    new MarginNoteSource(box),
                    ancestors.ToArray()));
                return;
            }

            foreach (var child in box.Children)
            {
                var isNested = child.Kind == BoxKind.Inline;

                if (isNested)
                {
                    ancestors.Add(child);
                }

                Append(
                    child,
                    text,
                    spans,
                    nested,
                    nestedMargin,
                    ancestors,
                    hoistInterline,
                    hoistMargin);

                if (isNested)
                {
                    ancestors.RemoveAt(ancestors.Count - 1);
                }
            }
        }
    }

    private readonly record struct NoteSpan(
        Int32 Start,
        Int32 Length,
        ComputedStyle Style,
        Box Box,
        WritingModeInfo Flow,
        IReadOnlyList<Box> Ancestors);

    private readonly record struct NestedInterlineNote(
        Int32 Position,
        InterlineNoteSource Note,
        IReadOnlyList<Box> Ancestors);

    private readonly record struct NestedMarginNote(
        Int32 Position,
        MarginNoteSource Note,
        IReadOnlyList<Box> Ancestors);

    private sealed record MarginNoteSource(Box Box);

    private readonly record struct NoteExpansion(
        TextFragment Fragment,
        Line? First,
        Line? Second,
        Double Edges,
        Boolean EndsLine,
        Int32 FirstPlaces,
        Int32 SecondPlaces,
        Double FirstWidth,
        Double SecondWidth);

    /// <summary>One outer fragment of a two-line inline note.</summary>
    private sealed class NoteRun
    {
        public NoteRun(
            Line? first,
            Line? second,
            WritingModeInfo flow,
            Double emptyLineHeight,
            Double blockSize,
            Double lineGap,
            Boolean reachedLineEnd,
            Thickness margin,
            Thickness padding,
            Thickness border)
        {
            First = first;
            Second = second;
            Flow = flow;
            EmptyLineHeight = emptyLineHeight;
            BlockSize = blockSize;
            LineGap = lineGap;
            ReachedLineEnd = reachedLineEnd;
            Margin = margin;
            Padding = padding;
            Border = border;
        }

        public Line? First { get; }

        public Line? Second { get; }

        public Double FirstWidth => First?.Used ?? 0;

        public Double SecondWidth => Second?.Used ?? 0;

        public WritingModeInfo Flow { get; }

        /// <summary>Gets the computed note line-height used when one subline is empty.</summary>
        public Double EmptyLineHeight { get; }

        public Double BlockSize { get; }

        /// <summary>Gets the body's half-leading kept at each block edge of an expanded note line.</summary>
        public Double LineGap { get; }

        /// <summary>Gets whether more content in this segment was pushed to the next outer line.</summary>
        public Boolean ReachedLineEnd { get; }

        /// <summary>Gets or sets whether this note is the last visible content on its outer line.</summary>
        public Boolean EndsOuterLine { get; set; }

        /// <summary>Gets this fragment's inline margin, padding and border.</summary>
        public Double InlineEdges
        {
            get
            {
                var (marginStart, marginEnd) = Flow.InlineMargins(Margin);
                var (paddingStart, paddingEnd) = Flow.InlineMargins(Padding);
                var (borderStart, borderEnd) = Flow.InlineMargins(Border);
                return marginStart + marginEnd + paddingStart + paddingEnd + borderStart + borderEnd;
            }
        }

        public Thickness Margin { get; }

        public Thickness Padding { get; }

        public Thickness Border { get; }
    }

    private sealed class TextFragment
    {
        public TextFragment(
            String source,
            Int32 start,
            Int32 length,
            Double width,
            ComputedStyle style,
            Box box,
            WritingModeInfo flow,
            InlineAncestor? inline,
            Boolean isReplaced = false,
            Double contentHeight = 0,
            RubyAnnotation? ruby = null,
            Double baseWidth = 0,
            AtomicInline? atomic = null,
            Boolean outOfFlow = false,
            NoteRun? note = null,
            InterlineNoteSource? interlineNote = null,
            Boolean interlineGap = false,
            Double? interlinePosition = null,
            MarginNoteSource? marginNote = null,
            Double? hang = null)
        {
            Source = source;
            Start = start;
            Length = length;
            Width = width;
            Style = style;
            Box = box;
            Flow = flow;
            Inline = inline;
            IsReplaced = isReplaced;
            ContentHeight = contentHeight;
            Ruby = ruby;
            BaseWidth = baseWidth;
            Atomic = atomic;
            OutOfFlow = outOfFlow;
            Note = note;
            InterlineNote = interlineNote;
            InterlineGap = interlineGap;
            InterlinePosition = interlinePosition;
            MarginNote = marginNote;
            Hang = hang;
        }

        /// <summary>Gets whether the fragment is a box that left the flow, placed but not drawn here.</summary>
        public Boolean OutOfFlow { get; }

        /// <summary>Gets the two-line inline note this fragment draws, or null for ordinary content.</summary>
        public NoteRun? Note { get; }

        /// <summary>Gets the interline note whose zero-width position this fragment marks.</summary>
        public InterlineNoteSource? InterlineNote { get; }

        /// <summary>Gets whether this zero-length fragment is fixed positioning room in an interline flow.</summary>
        public Boolean InterlineGap { get; }

        /// <summary>Gets the line-relative position a hoisted interline-note marker stands at.</summary>
        public Double? InterlinePosition { get; }

        /// <summary>Gets the margin note whose zero-width declaration this fragment marks.</summary>
        public MarginNoteSource? MarginNote { get; }

        /// <summary>
        /// Gets or sets whether the run reads right to left at the level the bidi algorithm gave it, or
        /// null for a run the algorithm never looked at. It is the direction the text is displayed in,
        /// which the characters alone do not say when an override asks for the other one.
        /// </summary>
        public Boolean? RightToLeft { get; set; }

        /// <summary>
        /// Gets or sets the bidi embedding level the run was resolved at, or -1 where it was never
        /// asked for - a box that is not text has no level of its own.
        /// </summary>
        public Int32 Level { get; set; } = -1;

        /// <summary>
        /// Gets the annotation that goes on the block-start side of this fragment's text, or null when
        /// the fragment is ordinary text.
        /// </summary>
        public RubyAnnotation? Ruby { get; }

        /// <summary>
        /// Gets the extent the base text itself takes along the inline axis. For a ruby fragment the
        /// fragment is as long as the wider of its two parts, and this is what centres the base inside
        /// the room the annotation needed.
        /// </summary>
        public Double BaseWidth { get; }

        /// <summary>
        /// Gets how far a ruby's base stands inside the room the pair takes, where the reading has hung
        /// over the text beside it; null where the pair's halves are simply centred on each other.
        /// </summary>
        public Double? Hang { get; }

        /// <summary>
        /// Gets the string the fragment's text is a piece of. A fragment whose pieces came from two
        /// different strings carries a string of its own, which is the one case a merge has to write
        /// out - and it is one per element rather than one per character.
        /// </summary>
        public String Source { get; private set; }

        /// <summary>Gets where in <see cref="Source"/> the fragment's text begins.</summary>
        public Int32 Start { get; private set; }

        /// <summary>Gets how many characters the fragment holds.</summary>
        public Int32 Length { get; set; }

        /// <summary>
        /// Gets or sets whether the fragment's run ends in a tab. A tab is drawn as one space but was
        /// charged the room up to its stop, so a run written into it would put its first glyph where the
        /// space ends rather than where the stop does.
        /// </summary>
        public Boolean FromTab { get; set; }

        /// <summary>
        /// Gets or sets how many of the fragment's trailing characters hang past the line's advance:
        /// the spaces a <c>pre-wrap</c> line trails into are neither room the line takes nor ink it
        /// draws, so the painter is handed the run without them.
        /// </summary>
        public Int32 Hanging { get; set; }

        /// <summary>
        /// Gets or sets the edge of the line the fragment hangs from - <see cref="HangStart"/> or
        /// <see cref="HangEnd"/> - or zero when it hangs from none. The glyph stands outside the room
        /// the line was measured at, which is what the line's alignment and justification read
        /// (CSS Text 3 §8.2.1).
        /// </summary>
        public Int32 HangSide { get; set; }

        /// <summary>
        /// Gets the fragment's text as a string, which is what the node the painter draws out of wants.
        /// Asked once per fragment, when the line is placed - not once per chunk of it, as it was while
        /// the chunks were being appended one character at a time.
        /// </summary>
        public String Text => Source.Substring(Start, Length);

        /// <summary>Takes the fragment's text to be the whole of the given string.</summary>
        public void MergeInto(String text)
        {
            Source = text;
            Start = 0;
            Length = text.Length;
        }

        /// <summary>
        /// Takes characters off the fragment's start, leaving it to draw the rest: a glyph hanging
        /// from the line's start edge is cut off the run it began rather than copied out of it.
        /// </summary>
        public void TrimStart(Int32 length)
        {
            Start += length;
            Length -= length;
        }

        /// <summary>Gets how many spaces the fragment ends with, which is what a line does not draw.</summary>
        public Int32 TrailingSpaces()
        {
            var count = 0;

            for (var index = Start + Length - 1; index >= Start && Source[index] == ' '; index--)
            {
                count++;
            }

            return count;
        }

        public Double Width { get; set; }

        /// <summary>
        /// Gets or sets the characters the line set half-width, one code per UTF-16 unit of the
        /// fragment's text: <c>'\0'</c> for a character the property left alone, <c>'\1'</c> for a
        /// trimmed opening punctuation, <c>'\2'</c> for a closing one and <c>'\3'</c> for a middle dot.
        /// Null when the run has nothing to trim (CSS Text 4 §8.5).
        /// </summary>
        public String? Trims { get; set; }

        /// <summary>
        /// Gets or sets which boundaries of the fragment the property widened, one code per UTF-16
        /// unit: <c>'\1'</c> when a space follows the unit, <c>'\2'</c> when the unit is a space the
        /// boundary takes the place of; null when the fragment spaces nothing (CSS Text 4 §8.4).
        /// </summary>
        public String? AutoSpaces { get; set; }

        /// <summary>
        /// Gets or sets the room one spaced boundary of the fragment adds, which is the eighth of the
        /// font's ideograph advance the layout measured.
        /// </summary>
        public Double AutoSpaceRoom { get; set; }

        /// <summary>
        /// Gets the width the fragment was measured at, before any justified room was folded into it, or
        /// nothing until the first arrangement asks for it: a fragment joins its neighbours while the line
        /// is being built, and only when the line is arranged is its own width settled.
        /// </summary>
        public Double? Measured { get; private set; }

        /// <summary>
        /// Gets the share of the line's unused room owed to every place between its words that this
        /// fragment holds, which the width above is grown by and the painter repeats after each of
        /// those places so that the glyphs land where the layout put them.
        /// </summary>
        public Double Justify { get; private set; }

        /// <summary>
        /// Gets how many places between its words the fragment holds a share for, which is how many times
        /// the painter repeats the share: the place a line trails into is already left out of the count.
        /// </summary>
        public Int32 Expansions { get; private set; }

        /// <summary>Folds this fragment's share of a justified line's room into the width it stands at.</summary>
        public void Expand(Double share, Int32 places)
        {
            Measured ??= Width;
            Justify = share;
            Expansions = places;
            Width = Measured.Value + (share * places);
        }

        /// <summary>Sets the width justification gave a fragment whose growth is not one repeated share.</summary>
        public void SetJustifiedWidth(Double width)
        {
            Measured ??= Width;
            Width = width;
        }

        public ComputedStyle Style { get; }

        public Box Box { get; }

        /// <summary>
        /// Gets the innermost inline element the run sits inside, or null when it sits in none. The
        /// rest of the chain is reached through <see cref="InlineAncestor.Parent"/>.
        /// </summary>
        public InlineAncestor? Inline { get; }

        /// <summary>Gets the orientation the fragment's glyphs are drawn in, which in a column may
        /// differ from one run to the next even inside a single element.</summary>
        public WritingModeInfo Flow { get; }

        /// <summary>Gets whether this fragment is an atomic replaced box rather than text.</summary>
        public Boolean IsReplaced { get; }

        /// <summary>
        /// Gets the measurements of an inline-block, or null when the fragment is not one. The width of
        /// such a fragment is what the box's own margins add to the size it was measured to, so the size
        /// the line reserved and the size the box is drawn at are not the same number.
        /// </summary>
        public AtomicInline? Atomic { get; }

        /// <summary>Gets the extent of the replaced content along the block axis, which sits on the baseline.</summary>
        public Double ContentHeight { get; }

        /// <summary>
        /// Gets the offset the line's shared baseline is moved by for this fragment, which is where
        /// <c>vertical-align</c> puts it. Set while the line is built, because the reference the
        /// alignment is taken against - the line as it stands before anything is aligned - is not known
        /// until the runs on it have been measured.
        /// </summary>
        public Double BaselineShift { get; set; }
    }

    /// <summary>
    /// Gets the offset <c>vertical-align</c> gives a box's baseline against the one the line shares.
    /// </summary>
    /// <remarks>
    /// The box reaches <c>above</c> the baseline it is set on and <c>below</c> it; the line it stands in
    /// reached <c>reach</c> above and <c>depth</c> below before anything was aligned, and that is the
    /// frame CSS 2.1 §10.8.1 gives the alignments against - the line box is grown *from* the aligned
    /// boxes rather than serving as their reference. The two extremes are why this is asked of the line
    /// rather than of the box: <c>top</c> and <c>bottom</c> cannot be known while the box is alone.
    /// <para>
    /// <c>middle</c> asks for half the parent's x-height, which the engine does not have from the face
    /// yet; it uses the half-em <c>ex</c> the style layer already stands in for that unit, so the two
    /// agree. A <c>&lt;length&gt;</c> or a <c>&lt;percentage&gt;</c> is not carried to the layout as a
    /// resolved number either, and is left on the baseline until the style layer sends one.
    /// </para>
    /// </remarks>
    /// <summary>Gets whether the style leaves its box on the baseline the line shares.</summary>
    private static Boolean IsBaseline(ComputedStyle style) =>
        style.VerticalAlign.Equals("baseline", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Gets whether the alignment is taken against the line box itself rather than against the box the
    /// element sits in, which is what makes it a place to stand instead of another step to climb.
    /// </summary>
    private static Boolean IsAtTheLineEdge(ComputedStyle style) =>
        style.VerticalAlign.Equals("top", StringComparison.OrdinalIgnoreCase) ||
        style.VerticalAlign.Equals("bottom", StringComparison.OrdinalIgnoreCase);

    /// <summary>Gets the chain of inline elements a run sits inside, innermost last.</summary>
    private static List<InlineAncestor> Chain(InlineAncestor? inline)
    {
        var chain = new List<InlineAncestor>();

        for (var ancestor = inline; ancestor is not null; ancestor = ancestor.Parent)
        {
            chain.Add(ancestor);
        }

        return chain;
    }

    private static Double Alignment(
        ITextMeasurer measurer,
        ComputedStyle style,
        ComputedStyle? parent,
        Double above,
        Double below,
        Double reach,
        Double depth)
    {
        var alignment = style.VerticalAlign;

        if (alignment.Equals("baseline", StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        if (alignment.Equals("super", StringComparison.OrdinalIgnoreCase))
        {
            return -0.4 * style.FontSize;
        }

        if (alignment.Equals("sub", StringComparison.OrdinalIgnoreCase))
        {
            return 0.2 * style.FontSize;
        }

        if (alignment.Equals("top", StringComparison.OrdinalIgnoreCase))
        {
            return above - reach;
        }

        if (alignment.Equals("bottom", StringComparison.OrdinalIgnoreCase))
        {
            return depth - below;
        }

        // The parent's text, not the line's: the reference is the box this one sits in.
        var reference = parent ?? style;

        if (alignment.Equals("text-top", StringComparison.OrdinalIgnoreCase))
        {
            return above - measurer.GetMetrics(reference.GetFont()).Ascent;
        }

        if (alignment.Equals("text-bottom", StringComparison.OrdinalIgnoreCase))
        {
            return measurer.GetMetrics(reference.GetFont()).Descent - below;
        }

        if (alignment.Equals("middle", StringComparison.OrdinalIgnoreCase))
        {
            // Half the parent's x-height is what the middle of the box is lifted by, so a face that states
            // one is followed rather than guessed at. Where it says nothing, the engine's own half-em `ex`
            // stands in for it, as it does wherever that unit is read.
            var parentMetrics = measurer.GetMetrics(reference.GetFont());
            var halfXHeight = parentMetrics.XHeight > 0 ? parentMetrics.XHeight / 2 : 0.25 * reference.FontSize;

            return ((above - below) / 2) - halfXHeight;
        }

        return 0;
    }

    /// <summary>
    /// The measurements of an inline-block, taken once on the line that places it.
    /// </summary>
    /// <remarks>
    /// The size along the line is the size the box is drawn at with its own margins added, since the
    /// line reserves the room for those too; the size across it is the box's alone, and the ascent says
    /// where inside that the box's baseline falls. The margins are what the line reserves and grows by
    /// on top of those sizes.
    /// </remarks>
    private readonly struct AtomicInline
    {
        public AtomicInline(
            Double inlineSize,
            Double blockSize,
            Double ascent,
            Double inlineStartMargin,
            Double inlineEndMargin,
            Double blockStartMargin,
            Double blockEndMargin)
        {
            InlineSize = inlineSize;
            BlockSize = blockSize;
            Ascent = ascent;
            InlineStartMargin = inlineStartMargin;
            InlineEndMargin = inlineEndMargin;
            BlockStartMargin = blockStartMargin;
            BlockEndMargin = blockEndMargin;
        }

        /// <summary>Gets the extent the box takes along the line, its own inline margins included.</summary>
        public Double InlineSize { get; }

        /// <summary>Gets the extent the box takes along the block axis, its own block margins aside.</summary>
        public Double BlockSize { get; }

        /// <summary>Gets the distance from the block-start edge of the border box to the box's baseline.</summary>
        public Double Ascent { get; }

        /// <summary>Gets the margin taken before the box along the line.</summary>
        public Double InlineStartMargin { get; }

        /// <summary>Gets the margin taken after the box along the line.</summary>
        public Double InlineEndMargin { get; }

        /// <summary>Gets the margin taken before the box across the line, above its ascent.</summary>
        public Double BlockStartMargin { get; }

        /// <summary>Gets the margin taken after the box across the line, below its descent.</summary>
        public Double BlockEndMargin { get; }
    }

    /// <summary>
    /// The annotation of a ruby fragment, measured and ready to be put beside its base text.
    /// </summary>
    /// <remarks>
    /// It costs the line the extent it takes along the block axis and nothing along the inline one
    /// beyond what the base already asked for, unless it is longer - a long reading over a short
    /// character widens the room the pair needs rather than being squeezed.
    /// </remarks>
    private sealed class RubyAnnotation
    {
        public RubyAnnotation(
            String text,
            Double width,
            Double block,
            Double ascent,
            Boolean under,
            ComputedStyle style,
            Box box,
            WritingModeInfo flow)
        {
            Text = text;
            Width = width;
            Block = block;
            Ascent = ascent;
            Under = under;
            Style = style;
            Box = box;
            Flow = flow;
        }

        /// <summary>
        /// Gets whether the annotation goes on the block-end side of its base, which is what
        /// <c>ruby-position: under</c> asks for: a reading placed after the base rather than before it.
        /// </summary>
        public Boolean Under { get; }

        public String Text { get; }

        /// <summary>Gets the extent the annotation takes along the inline axis.</summary>
        public Double Width { get; }

        /// <summary>Gets the extent the annotation takes along the block axis, which the line grows by.</summary>
        public Double Block { get; }

        /// <summary>Gets the annotation's own ascent, which positions its baseline inside that extent.</summary>
        public Double Ascent { get; }

        public ComputedStyle Style { get; }

        public Box Box { get; }

        public WritingModeInfo Flow { get; }
    }

    /// <summary>
    /// One breaking of one container's inline content: the box it was broken out of, the extent it was
    /// broken at, the indentation its first line started with, the writing mode it was read in, and the
    /// floats its lines had to stand aside for together with how far down and in the container sits
    /// among them.
    /// </summary>
    /// <remarks>
    /// The container is compared by identity rather than by its own equality: two boxes that happen to
    /// carry the same style are still two containers whose lines belong to each of them alone.
    /// <para>
    /// The three float parts are what a line's room is asked from, and they are the reason a block beside
    /// a float is broken twice in a pass - once while measuring, from its own origin, and once while
    /// arranging, at the height it actually lands at. A block with nothing floated near it pays nothing:
    /// all three are null and zero in both phases, and the plan it made while measuring is the one the
    /// placing reads.
    /// </para>
    /// </remarks>
    private readonly struct LinePlanKey : IEquatable<LinePlanKey>
    {
        public LinePlanKey(Box container, Double maxInline, TextIndentation indent, Int32 flow, FloatBand? band)
        {
            Container = container;
            MaxInline = maxInline;
            Indent = indent;
            Flow = flow;
            Tracks = band?.Tracks;
            BandY = band?.OriginY ?? 0;
            BandLeft = band?.Left ?? 0;
        }

        public Box Container { get; }

        public Double MaxInline { get; }

        public TextIndentation Indent { get; }

        public Int32 Flow { get; }

        public FloatTracks? Tracks { get; }

        public Double BandY { get; }

        public Double BandLeft { get; }

        public Boolean Equals(LinePlanKey other) =>
            ReferenceEquals(Container, other.Container) &&
            MaxInline == other.MaxInline &&
            Indent == other.Indent &&
            Flow == other.Flow &&
            ReferenceEquals(Tracks, other.Tracks) &&
            BandY == other.BandY &&
            BandLeft == other.BandLeft;

        public override Boolean Equals(Object? obj) => obj is LinePlanKey other && Equals(other);

        public override Int32 GetHashCode() =>
            HashCode.Combine(
                RuntimeHelpers.GetHashCode(Container),
                MaxInline,
                Indent,
                Flow,
                RuntimeHelpers.GetHashCode(Tracks),
                BandY,
                BandLeft);
    }

    /// <summary>
    /// How much room the line being built has, which is the block's own extent less whatever a float
    /// standing at that line's height takes of it.
    /// </summary>
    /// <remarks>
    /// The room is worked out once per line, when it opens, rather than per run added to it: a line
    /// whose width moved while it was being filled would let the same word break on one pass and
    /// stand on the next, and a block would settle at whatever the order of measuring happened to be.
    /// <para>
    /// A line that finds no room at all - a float across the whole width, or two meeting in the middle
    /// - is dropped below the floats in its band, which is what the specification does for a line box
    /// too narrow for anything and what the block's height then has to count. One dropped still
    /// narrower than a character gives up and overflows, the way a float wider than its container does.
    /// </para>
    /// </remarks>
    private sealed class LineRoom
    {
        /// <summary>How fine the descent of a shortened line is scanned, in steps over the float.</summary>
        private const Double UnitScan = 1000;

        /// <summary>The layout unit a browser places boxes in, which is the tolerance a fit is judged by.</summary>
        private const Double Unit = 1.0 / 64;

        private readonly FloatBand? _band;
        private readonly Double _max;
        private readonly Boolean _reversed;

        /// <summary>How far down the band the open line stands, which is what a shape is read across.</summary>
        private Double _height;

        public LineRoom(FloatBand? band, Double max, Boolean reversed, Double start = 0)
        {
            _band = band;
            _max = max;
            _reversed = reversed;
            Top = start;
            Open();
        }

        /// <summary>Gets how far down the block's lines have reached, which is where the next one stands.</summary>
        public Double Top { get; private set; }

        /// <summary>Gets how far the line that is open now reaches along the inline axis.</summary>
        public Double Room { get; private set; }

        /// <summary>Gets the height the open line has been grown to so far.</summary>
        public Double Height => _height;

        /// <summary>Gets where that line begins inside the block's content box.</summary>
        public Double Inset { get; private set; }

        /// <summary>Gets the room it had to drop through to get there, which the block's stack owes it.</summary>
        public Double Gap { get; private set; }

        /// <summary>
        /// Moves on to the next line, at the height the one just built took.
        /// </summary>
        public void Next(Double height)
        {
            Top += height;
            _height = 0;
            Open();
        }

        /// <summary>Advances over a body line's block extent without emitting another note line.</summary>
        public void Skip(Double height)
        {
            if (height <= 0)
            {
                return;
            }

            Top += height;
            _height = 0;
            Open();
        }

        /// <summary>
        /// Stands the lines that are to come below the floats of one side: a break that asks to clear
        /// puts whatever follows it under the floats it names (CSS 2.1 §9.5.2). Gets the distance the
        /// room was carried down, which is zero where there was nothing of that side to clear.
        /// </summary>
        public Double Clear(ClearSide side)
        {
            if (_band is not { } band || side == ClearSide.None)
            {
                return 0;
            }

            var cleared = band.Below(Top, side);

            if (cleared <= Top + 0.001)
            {
                return 0;
            }

            var dropped = cleared - Top;
            Top = cleared;
            Open();
            Gap += dropped;

            return dropped;
        }

        /// <summary>
        /// Stands the content that is about to be placed on the line, and asks the room again over the
        /// band it makes: a line box is shortened to avoid the whole of the float area it stands in, so
        /// the height a piece of content gives the line is part of the question the room answers, and not
        /// something the line discovers after it has been filled (CSS Shapes 1 §1.3).
        /// </summary>
        public void Grow(Double height)
        {
            if (height <= _height)
            {
                return;
            }

            _height = height;

            if (_band is { } band)
            {
                Room = band.Room(Top, _height, _max, _reversed, out var inset);
                Inset = inset;
            }
        }

        /// <summary>
        /// Takes back a growth the line did not keep: an item measured against the band it would have
        /// made, but which did not fit on the line, is turned away before the line is built, and the
        /// answer the shape gave for the taller band is no longer the line's (CSS Shapes 1 §1.3).
        /// </summary>
        public void Restore(Double height)
        {
            if (height >= _height)
            {
                return;
            }

            _height = height;

            if (_band is { } band)
            {
                Room = band.Room(Top, _height, _max, _reversed, out var inset);
                Inset = inset;
            }
        }

        private void Open()
        {
            Gap = 0;
            _height = 0;

            if (_band is not { } band)
            {
                Room = _max;
                Inset = 0;
                return;
            }

            Room = band.Room(Top, _height, _max, _reversed, out var inset);
            Inset = inset;

            if (Room > 0)
            {
                return;
            }

            var below = band.Below(Top);

            if (below > Top)
            {
                Gap = below - Top;
                Top = below;

                Room = band.Room(Top, _height, _max, _reversed, out inset);
                Inset = inset;
            }

            if (Room <= 0)
            {
                Room = _max;
                Inset = 0;
            }
        }

        /// <summary>
        /// Gets whether a float is standing in the line's way, so the room it has is less than the
        /// block's own extent - the case the specification's shift-down rule is about.
        /// </summary>
        public Boolean Shortened => Inset > 0 || Room < _max;

        /// <summary>
        /// Shifts the open line down until the content standing on it fits, which is what CSS 2.1 §9.5
        /// asks of a shortened line box too small for what is on it: it moves downward, and its width is
        /// recomputed, until some content fits or no floats are left. The descent is added to the gap,
        /// the room the block's stack owes the line for getting there.
        /// </summary>
        /// <remarks>
        /// A shaped float leaves more room the further down a line stands, so the line comes to rest at
        /// the first height the content fits at rather than below the whole float - which is the
        /// difference between a long word moving down a curved shape's edge and dropping past it
        /// (CSS Shapes 1 §1.3). The scan is a pixel at a time, the grain a line is placed on.
        /// </remarks>
        public void Drop(Double needed)
        {
            if (_band is not { } band)
            {
                return;
            }

            var below = band.Below(Top);

            if (below <= Top)
            {
                return;
            }

            var step = Math.Max(1.0, (below - Top) / UnitScan);

            for (var y = Top + step; y < below; y += step)
            {
                var room = band.Room(y, _height, _max, _reversed, out var inset);

                if (room + Unit >= needed)
                {
                    Gap = y - Top;
                    Top = y;
                    Room = room;
                    Inset = inset;
                    return;
                }
            }

            Gap = below - Top;
            Top = below;

            Room = band.Room(Top, _height, _max, _reversed, out var rest);
            Inset = rest;

            if (Room <= 0)
            {
                Room = _max;
                Inset = 0;
            }
        }
    }

    private sealed class Line
    {
        public List<TextFragment> Fragments { get; } = new();

        public Double Ascent { get; set; }

        public Double Height { get; set; }

        /// <summary>
        /// Gets or sets how far the block's content reaches past this line, where a break that clears
        /// stands at the content's end: the line before it owes the room the clearance took, and no
        /// line follows to take it (CSS 2.1 §9.5.2).
        /// </summary>
        public Double Tail { get; set; }

        /// <summary>
        /// Gets or sets where the line begins inside its block's content box: zero for every line of a
        /// block with nothing floated beside it, and the far edge of the float for one that has.
        /// </summary>
        public Double Left { get; set; }

        /// <summary>
        /// Gets or sets the extent the line was broken at, which is the block's content extent unless a
        /// float shortens it. Alignment works within this much, and the line box is drawn this wide.
        /// </summary>
        public Double Extent { get; set; }

        /// <summary>
        /// Gets or sets the room the line had to drop to get clear of a float that left it nothing to
        /// stand in, which the block's own stack has to add back where it counts lines out.
        /// </summary>
        public Double GapBefore { get; set; }

        /// <summary>Gets the extent the line's fragments take along the inline axis, before alignment.</summary>
        public Double Used { get; set; }

        /// <summary>Gets or sets the used extent before justification, which every later pass starts from.</summary>
        public Double? NaturalUsed { get; set; }

        /// <summary>Gets or sets the extent the line was broken at before justification, which a later pass restores.</summary>
        public Double? NaturalExtent { get; set; }

        /// <summary>
        /// Gets or sets how much of the last fragment's width hangs past the line's advance. The spaces
        /// a <c>pre-wrap</c> line trails into are drawn where they stand but take no room (CSS Text 3
        /// §4.1.3), so the box that ends the line is narrowed by this much and the glyphs land where a
        /// line without them would have put them.
        /// </summary>
        public Double Hanging { get; set; }

        /// <summary>
        /// Gets the distance the line is set in from the edge it begins at, which is the indentation
        /// the first line of a box carries. It is not part of <see cref="Used"/>: the runs are aligned
        /// in what the indentation leaves, and the indent is what they are then all moved by.
        /// </summary>
        public Double Indent { get; set; }

        /// <summary>
        /// Gets or sets how much the line's text was scaled to fill its line box by <c>text-fit</c>, or one
        /// where the line stands at the size the page wrote. The runs it holds are already measured at the
        /// scaled size; what is left is to draw them with a font of that size, which is the question the
        /// placing pass asks this for (CSS Text 5 §1.1).
        /// </summary>
        public Double Scale { get; set; } = 1;

        /// <summary>Gets where the line's content starts along the inline axis, after alignment.</summary>
        public Double Offset { get; set; }

        /// <summary>
        /// Gets or sets the keyword the line is aligned by: what <c>text-align</c> gave it, unless the
        /// block's <c>text-align-last</c> speaks for this line - which it does for the line that ends the
        /// block and for every line a forced break ended (CSS Text 3 §6.3).
        /// </summary>
        public String Alignment { get; set; } = CssInitialValues.TextAlign;

        /// <summary>
        /// Gets or sets the style whose <c>text-align</c> and <c>text-align-last</c> the line is set with.
        /// It is the style of the first thing on the line, which is the block's own as it stands in
        /// text, except where the line begins with a box it carries whole - and there it is the block's
        /// style, because what that box declares reaches inside it and not out.
        /// </summary>
        public ComputedStyle? Governing { get; set; }

        /// <summary>
        /// Gets the inline elements this line carries on from an earlier one, or null when nothing was
        /// cut. Their start edges belong to the line the element began on, so this line shows neither
        /// the padding nor the border nor the curve of that side.
        /// </summary>
        public HashSet<InlineAncestor>? ContinuesFromPrevious { get; set; }

        /// <summary>
        /// Gets the inline elements this line hands on to a later one, or null when none were cut. The
        /// mirror of <see cref="ContinuesFromPrevious"/> at the other end of the line.
        /// </summary>
        public HashSet<InlineAncestor>? ContinuesToNext { get; set; }

        /// <summary>
        /// Gets how each inline element that aligned its own box on this line moves the band it draws,
        /// or null while none did. Held by the line rather than by the element because what an alignment
        /// is taken against - <c>top</c> of the line, <c>bottom</c> of it - belongs to the line, and one
        /// element's chain of them stands on several lines.
        /// </summary>
        public Dictionary<InlineAncestor, Double>? BandShifts { get; set; }

        /// <summary>
        /// Gets whether the line ends where the page forced it to - a line feed in preformatted text, a
        /// <c>&lt;br&gt;</c> - rather than being broken where the room ran out. The block's
        /// <c>text-align-last</c> speaks for such a line as it does for the one that ends the block
        /// (CSS Text 3 §6.3).
        /// </summary>
        public Boolean ForceEnded { get; set; }

        /// <summary>Gets or sets whether this line belongs to a shared interline-note flow.</summary>
        public Boolean IsInterlineNote { get; set; }

        /// <summary>Gets or sets whether a fragment break should keep this line with the one after it.</summary>
        public Boolean KeepWithNext { get; set; }

        /// <summary>Gets or sets the body-line slot this note line fills, or -1 for an ordinary line.</summary>
        public Int32 SlotIndex { get; set; } = -1;
    }

    /// <summary>
    /// One inline element a run sits inside, with its box model already resolved.
    /// </summary>
    /// <remarks>
    /// The box tree has no parent pointer and the flattening above throws the element away, so this
    /// chain is what remembers where a run came from. One instance is built per inline element each
    /// time the content is collected, and every run under that element shares it, which is why the
    /// nodes of a line can be reconstructed by comparing chains by reference.
    /// <para>
    /// The padding and border are kept in both forms: physically, so a fragment can hand the painter
    /// the sides it actually draws, and as the four logical insets, so the layout can talk about the
    /// edge a line is cut at without asking which way the text runs.
    /// </para>
    /// </remarks>
    private sealed class InlineAncestor
    {
        private const Int32 Top = 0;
        private const Int32 Right = 1;
        private const Int32 Bottom = 2;
        private const Int32 Left = 3;

        private readonly Int32 _inlineStartSide;
        private readonly Int32 _inlineEndSide;

        public InlineAncestor(
            Box box,
            InlineAncestor? parent,
            Double basis,
            WritingModeInfo flow,
            ITextMeasurer measurer,
            Boolean noteContent = false)
        {
            Box = box;
            Parent = parent;
            NoteContent = noteContent;

            Padding = box.Style.Padding.Resolve(basis);
            Border = BlockLayout.BorderOf(box);
            Margin = box.Style.Margin.Resolve(basis);

            var metrics = measurer.GetMetrics(box.Style.GetFont());
            Ascent = metrics.Ascent;
            Descent = metrics.Descent;
            AnyAlignment = !IsBaseline(box.Style) || (parent?.AnyAlignment ?? false);

            // The element's decoration is placed in the frame its line was laid out in, so it is the
            // container's writing mode - and not the element's own - that decides which physical side
            // each logical edge falls on.
            _inlineStartSide = InlineSide(flow, start: true);
            _inlineEndSide = InlineSide(flow, start: false);

            InlineStartInset = Insets(_inlineStartSide);
            InlineEndInset = Insets(_inlineEndSide);

            // The margins the element takes along the axis its text runs, which is room the line gives
            // it rather than part of the box it draws: an inline element's horizontal margins take their
            // place on the line like any other horizontal margin (CSS 2.1 §8.1), while the vertical ones
            // never grow the line box. They are held apart from the insets above, which describe the
            // border box the element's edges are drawn at.
            InlineStartMargin = Read(Margin, _inlineStartSide);
            InlineEndMargin = Read(Margin, _inlineEndSide);
            BlockStartInset = Insets(BlockSide(flow, start: true));
            BlockEndInset = Insets(BlockSide(flow, start: false));
        }

        /// <summary>Gets the element this chain entry stands for.</summary>
        public Box Box { get; }

        /// <summary>Gets the element this one sits inside, or null when it is the outermost.</summary>
        public InlineAncestor? Parent { get; }

        /// <summary>Gets whether this inline wrapper was reconstructed from note content.</summary>
        public Boolean NoteContent { get; }

        /// <summary>Gets the padding the element resolved to, all four sides.</summary>
        public Thickness Padding { get; }

        /// <summary>Gets the border widths the element resolved to, all four sides.</summary>
        public Thickness Border { get; }

        /// <summary>Gets the margins the element resolved to, all four sides.</summary>
        public Thickness Margin { get; }

        /// <summary>Gets the room the element's margin takes at the start of the inline axis.</summary>
        public Double InlineStartMargin { get; }

        /// <summary>Gets the room the element's margin takes at the end of the inline axis.</summary>
        public Double InlineEndMargin { get; }

        /// <summary>Gets the extent the element's padding and border add at the start of the inline axis.</summary>
        public Double InlineStartInset { get; }

        /// <summary>Gets the extent the element's padding and border add at the end of the inline axis.</summary>
        public Double InlineEndInset { get; }

        /// <summary>Gets the extent the element's padding and border add at the start of the block axis.</summary>
        public Double BlockStartInset { get; }

        /// <summary>Gets the extent the element's padding and border add at the end of the block axis.</summary>
        public Double BlockEndInset { get; }

        /// <summary>Gets the distance from the top of the element's font to its baseline.</summary>
        public Double Ascent { get; }

        /// <summary>Gets the distance from the element's baseline to the bottom of its font.</summary>
        public Double Descent { get; }

        /// <summary>
        /// Gets whether this element, or any element it sits inside, asks for an alignment of its own
        /// box. Carried down the chain so a line with nothing aligned on it can skip the walk entirely.
        /// </summary>
        public Boolean AnyAlignment { get; }

        /// <summary>
        /// Gets the padding a fragment of this element draws. An edge the element was cut at carries
        /// none of it: everything that would have been drawn there was drawn on the line the element
        /// began or ended on.
        /// </summary>
        public Thickness PaddingFor(Boolean cutStart, Boolean cutEnd) =>
            Sliced(Padding, cutStart, cutEnd);

        /// <summary>Gets the border a fragment of this element draws, cut edges carrying none.</summary>
        public Thickness BorderFor(Boolean cutStart, Boolean cutEnd) =>
            Sliced(Border, cutStart, cutEnd);

        private Thickness Sliced(Thickness thickness, Boolean cutStart, Boolean cutEnd)
        {
            // An element cut at both ends draws neither side border: the two edges stand on the
            // lines the element began and ends on, and this line holds only its middle.
            var sliced = thickness;

            if (cutStart)
            {
                sliced = Clear(sliced, _inlineStartSide);
            }

            if (cutEnd)
            {
                sliced = Clear(sliced, _inlineEndSide);
            }

            return sliced;
        }

        /// <summary>
        /// Gets the physical side the inline axis starts on: the left edge of a left-to-right row, the
        /// right edge of a right-to-left one, the top edge of a column - and the bottom edge under
        /// <c>sideways-lr</c>, the one mode that runs up the page.
        /// </summary>
        private static Int32 InlineSide(WritingModeInfo flow, Boolean start) => flow.IsVertical
            ? start == (flow.InlineSign > 0) ? Top : Bottom
            : start == (flow.InlineSign > 0) ? Left : Right;

        /// <summary>
        /// Gets the physical side the block axis starts on: the top edge of a row, and of a column the
        /// left edge unless the mode is a right-to-left one, whose first column is the right-hand one.
        /// </summary>
        private static Int32 BlockSide(WritingModeInfo flow, Boolean start) => flow.IsVertical
            ? start == (flow.BlockSign > 0) ? Left : Right
            : start ? Top : Bottom;

        /// <summary>Gets the padding and border the element adds along one physical side.</summary>
        private Double Insets(Int32 side) => Read(Padding, side) + Read(Border, side);

        private static Double Read(Thickness thickness, Int32 side) => side switch
        {
            Top => thickness.Top,
            Bottom => thickness.Bottom,
            Left => thickness.Left,
            _ => thickness.Right,
        };

        private static Thickness Clear(Thickness t, Int32 side) => side switch
        {
            Top => new Thickness(0, t.Right, t.Bottom, t.Left),
            Bottom => new Thickness(t.Top, t.Right, 0, t.Left),
            Left => new Thickness(t.Top, t.Right, t.Bottom, 0),
            _ => new Thickness(t.Top, 0, t.Bottom, t.Left),
        };
    }

    /// <summary>
    /// One step a line took: a piece of a run, the room it was charged, and where it came from, kept so
    /// the step can be taken again on the line after a soft hyphen.
    /// </summary>
    private readonly record struct TailStep(
        String Source,
        Int32 Start,
        Int32 Length,
        Double Advance,
        ComputedStyle Style,
        Box Box,
        WritingModeInfo Flow,
        InlineAncestor? Inline,
        Boolean OutOfFlow = false,
        Boolean Empty = false);

    private sealed class LineBuilder
    {
        private readonly List<TextFragment> _fragments = new();
        private readonly List<OpenInline> _open = new();

        /// <summary>The chain a captured line stood with, kept from one capture to the next.</summary>
        private OpenInline[] _opened = Array.Empty<OpenInline>();
        private readonly HashSet<InlineAncestor>? _carried;
        private readonly ComputedStyle? _fallback;
        private readonly Boolean _vertical;
        private readonly Double _indent;

        public LineBuilder()
        {
        }

        /// <param name="indent">
        /// The distance the line is set in from the edge it begins at. It is the extent the line starts
        /// out having used, so the run that would not have fitted without it is carried to the next
        /// line - which is what an indented first line does to the text on it.
        /// </param>
        public LineBuilder(
            ComputedStyle strut,
            Double indent,
            Boolean vertical,
            ITextMeasurer measurer,
            Boolean startsParagraph,
            Boolean firstLine = false)
        {
            _fallback = strut;
            _indent = indent;
            _vertical = vertical;
            _measurer = measurer;
            _startsParagraph = startsParagraph;
            _firstLine = firstLine;
            Width = indent;
        }

        public LineBuilder(
            ComputedStyle fallback,
            HashSet<InlineAncestor>? carried,
            Boolean vertical,
            ITextMeasurer measurer,
            Boolean startsParagraph,
            Double indent = 0)
        {
            _fallback = fallback;
            _carried = carried;
            _vertical = vertical;
            _measurer = measurer;
            _startsParagraph = startsParagraph;
            _indent = indent;
            Width = indent;
        }

        private readonly ITextMeasurer _measurer;
        private readonly Boolean _startsParagraph;
        private readonly Boolean _firstLine;

        /// <summary>
        /// Gets the extent the line takes as it is being built, which is what the wrap test is made
        /// against while the line is still growing. It is only an estimate of what the line ends up
        /// taking - trimming the trailing spaces can still take a run back out - so
        /// <see cref="Build"/> works the extent it hands back out afresh instead of reusing this.
        /// </summary>
        public Double Width { get; private set; }

        public Double Indent => _indent;

        /// <summary>
        /// Gets the room the runs already on the line take, using the same fragment accounting as
        /// <see cref="Build"/> rather than the running wrapping estimate.
        /// </summary>
        public Double CurrentUsed()
        {
            var open = new List<OpenInline>();
            var used = 0.0;

            foreach (var fragment in _fragments)
            {
                used += Advance(open, fragment.Inline, _carried, origin: 0, apply: true, step: null);
                used += fragment.Width;
            }

            foreach (var item in open)
            {
                used += item.Ancestor.InlineEndInset;
            }

            return used;
        }

        /// <summary>
        /// Charges room the line had left off for a glyph that turned out to stand inside it: a stop
        /// or comma keeps its advance only while it ends the line.
        /// </summary>
        public void Widen(Double room) => Width += room;

        /// <summary>
        /// Gets whether the line already holds the element, which is what says a run is not the first
        /// one written in it: an element the line has not opened yet begins where the run stands.
        /// </summary>
        public Boolean IsOpen(InlineAncestor inline) =>
            _open.Exists(entry => ReferenceEquals(entry.Ancestor, inline));

        /// <summary>
        /// Gets whether the line carries anything drawn on it. A box that left the flow is not: it stands
        /// on the line only to say where it would have been, so a line holding nothing else is an empty
        /// line and takes no height.
        /// </summary>
        public Boolean HasContent =>
            _fragments.Exists(fragment => !fragment.OutOfFlow && !fragment.InterlineGap);

        /// <summary>
        /// Gets whether the line draws anything yet. An empty inline box is content but no ink, and a
        /// glyph hangs from the line's start edge just the same past one.
        /// </summary>
        private Boolean HasInk => _fragments.Exists(fragment => !fragment.OutOfFlow && HasInk(fragment));

        /// <summary>
        /// Gets whether a run is the opening glyph an element's first formatted line hangs: it stands
        /// at the line's start, its element begins here, and no border or padding of its own stands
        /// between it and the edge (CSS Text 3 §8.2.1).
        /// </summary>
        private Boolean HangsStart(ComputedStyle style, String source, Int32 start, InlineAncestor? inline)
        {
            if (!HangingPunctuation.HangsAtStart(style.HangingPunctuation) ||
                !HangingPunctuation.IsOpening(TextChunker.CodePointAt(source, start)))
            {
                return false;
            }

            // The block's own text reaches the edge on its first line; an inline element's text on
            // the line the element begins on, which is the line this run is written on.
            var first = inline is null ? _firstLine : _carried?.Contains(inline) != true;

            return first && !EdgeBlocked(_carried, inline, start: true);
        }

        /// <summary>
        /// Gets whether the line carries any fragment at all, drawn or not. A line with nothing but a box
        /// that left the flow still has to be closed, or that box is never placed.
        /// </summary>
        public Boolean HasFragments => _fragments.Count > 0;

        /// <summary>
        /// Gets whether the line ends in a soft hyphen that may end it: the mark is drawn there, and only
        /// where the line carries something before the hyphen - a line of soft hyphens alone is where the
        /// previous one was cut, and cutting it again would draw a mark with nothing before it.
        /// </summary>
        public Boolean EndsInSoftHyphen
        {
            get
            {
                // A box that left the flow holds none of the line's text, so the word runs straight on
                // across it: the hyphen the line ends on is still the hyphen the line before it wrote.
                TextFragment? last = null;
                var wordsBefore = 0;

                for (var index = _fragments.Count - 1; index >= 0; index--)
                {
                    if (_fragments[index].OutOfFlow ||
                        _fragments[index].InterlineGap ||
                        _fragments[index].InterlineNote is not null ||
                        _fragments[index].MarginNote is not null)
                    {
                        continue;
                    }

                    last = _fragments[index];
                    wordsBefore = index;
                    break;
                }

                if (last is not { Length: > 0, IsReplaced: false, Ruby: null, Atomic: null })
                {
                    return false;
                }

                var text = last.Text;
                var trailing = 0;

                while (trailing < text.Length && text[text.Length - 1 - trailing] == (Char)0x00AD)
                {
                    trailing++;
                }

                return trailing > 0 && (text.Length > trailing || wordsBefore > 0);
            }
        }

        /// <summary>
        /// Gets a place the line can be cut back to: the runs it holds, the room they took, and the
        /// elements their chain had opened.
        /// </summary>
        /// <remarks>
        /// The opened chain is copied into a buffer the builder keeps rather than into a fresh array:
        /// a page whose text may break after every character took a snapshot at every one of them, and
        /// only the newest snapshot is ever read back.
        /// </remarks>
        public Checkpoint Capture()
        {
            var opened = _open.Count;

            if (_opened.Length < opened)
            {
                Array.Resize(ref _opened, Math.Max(opened, Math.Max(4, _opened.Length * 2)));
            }

            _open.CopyTo(0, _opened, 0, opened);

            return new Checkpoint(
                _fragments.Count,
                _fragments.Count > 0 ? _fragments[^1].Length : 0,
                _fragments.Count > 0 ? _fragments[^1].Width : 0,
                _fragments.Count > 0 ? _fragments[^1].Trims : null,
                _fragments.Count > 0 ? _fragments[^1].AutoSpaces : null,
                Width,
                _opened,
                opened);
        }

        /// <summary>
        /// Cuts the line back to a place it stood at. The caller has kept the steps taken since and
        /// takes them again on the next line, so nothing is drawn here and the boxes those runs would
        /// open are never made - the placing pass runs only when a line closes.
        /// </summary>
        public void Restore(Checkpoint at)
        {
            if (_fragments.Count > at.Fragments)
            {
                _fragments.RemoveRange(at.Fragments, _fragments.Count - at.Fragments);
            }

            if (at.Fragments > 0 && _fragments.Count == at.Fragments)
            {
                var last = _fragments[^1];
                last.Length = at.Length;
                last.Width = at.RunWidth;
                last.Trims = at.RunTrims;
                last.AutoSpaces = at.RunAutoSpaces;
            }

            Width = at.Width;
            _open.Clear();

            for (var index = 0; index < at.Opened; index++)
            {
                _open.Add(at.Open[index]);
            }
        }

        /// <summary>The state a line may be cut back to, kept while a soft hyphen offers the place.</summary>
        public readonly record struct Checkpoint(
            Int32 Fragments,
            Int32 Length,
            Double RunWidth,
            String? RunTrims,
            String? RunAutoSpaces,
            Double Width,
            OpenInline[] Open,
            Int32 Opened);

        /// <summary>
        /// Gets the code point the line's text ends on, or null where it ends on a box rather than a run.
        /// </summary>
        public Int32? LastCodePoint
        {
            get
            {
                // A box the page took out of the flow stands in the line's list but is not part of its
                // text, so the word the line has begun carries straight on across it; a box that holds
                // text of its own - a replaced picture, a ruby pair, an atomic line - really does end
                // the word.
                for (var index = _fragments.Count - 1; index >= 0; index--)
                {
                    var fragment = _fragments[index];

                    if (fragment.OutOfFlow || fragment.InterlineGap ||
                        fragment.InterlineNote is not null || fragment.MarginNote is not null)
                    {
                        continue;
                    }

                    if (fragment.Length == 0 || fragment.IsReplaced || fragment.Ruby is not null ||
                        fragment.Atomic is not null)
                    {
                        return null;
                    }

                    return TextChunker.CodePointBefore(fragment.Source, fragment.Start + fragment.Length);
                }

                return null;
            }
        }

        /// <summary>
        /// Gets the extent the line would already take if the next run sat in the given chain. The
        /// wrap test asks this rather than reading <see cref="Width"/>, so that the padding and border
        /// a run brings with it are counted before the run itself is measured against what is left.
        /// </summary>
        public Double ExtentFor(InlineAncestor? inline) =>
            Width + Advance(_open, inline, _carried, origin: 0, apply: false, step: null);

        /// <summary>
        /// Adds an inline element that holds nothing: the line keeps the margins, borders and padding
        /// the box carries along it, and the box is drawn where they put it (CSS 2.1 §8.1).
        /// </summary>
        public void AddEmpty(Box box, ComputedStyle style, WritingModeInfo flow, InlineAncestor inline)
        {
            Enter(inline);

            _fragments.Add(new TextFragment(String.Empty, 0, 0, 0, style, box, flow, inline));
        }

        /// <summary>Adds an atomic replaced box, which occupies its own used size on the line.</summary>
        public void AddReplaced(Box box, Double width, Double height, InlineAncestor? inline)
        {
            Enter(inline);

            _fragments.Add(new TextFragment(
                String.Empty,
                0,
                0,
                width,
                box.Style,
                box,
                box.Flow,
                inline,
                isReplaced: true,
                contentHeight: height));

            Width += width;
        }

        /// <summary>
        /// Adds a box that left the flow. It takes no room: the cursor does not move and the line grows no
        /// taller for it, so what the fragment records is only where the cursor stood - the place the box
        /// would have had, which is the static position it is placed by.
        /// </summary>
        public void AddOutOfFlow(Box box, ComputedStyle style, InlineAncestor? inline)
        {
            Enter(inline);

            _fragments.Add(new TextFragment(
                String.Empty,
                0,
                0,
                0,
                style,
                box,
                box.Flow,
                inline,
                outOfFlow: true));
        }

        /// <summary>
        /// Adds an inline-block, which the line places whole. The width it takes along the line is the
        /// one it was measured to together with its own margins, which the box is then drawn inside of.
        /// </summary>
        public void AddAtomic(Box box, AtomicInline atomic, InlineAncestor? inline)
        {
            Enter(inline);

            _fragments.Add(new TextFragment(
                String.Empty,
                0,
                0,
                atomic.InlineSize,
                box.Style,
                box,
                box.Flow,
                inline,
                atomic: atomic));

            Width += atomic.InlineSize;
        }

        /// <summary>Adds one outer fragment of a two-line inline note.</summary>
        public void AddNote(Box box, ComputedStyle style, WritingModeInfo flow, InlineAncestor? inline, NoteRun note, Double width)
        {
            Enter(inline);

            _fragments.Add(new TextFragment(
                String.Empty,
                0,
                0,
                width,
                style,
                box,
                flow,
                inline,
                note: note));

            Width += width;
        }

        /// <summary>Adds a zero-width marker that records where an interline note was declared.</summary>
        public void AddInterlineNote(
            Box box,
            ComputedStyle style,
            WritingModeInfo flow,
            InlineAncestor? inline,
            InterlineNoteSource note)
        {
            Enter(inline);

            _fragments.Add(new TextFragment(
                String.Empty,
                0,
                0,
                0,
                style,
                box,
                flow,
                inline,
                interlineNote: note));
        }

        /// <summary>Adds a zero-width marker for a margin note declared in the line.</summary>
        public void AddMarginNote(
            Box box,
            ComputedStyle style,
            WritingModeInfo flow,
            InlineAncestor? inline,
            MarginNoteSource note)
        {
            _fragments.Add(new TextFragment(
                String.Empty,
                0,
                0,
                0,
                style,
                box,
                flow,
                null,
                marginNote: note));
        }

        /// <summary>Adds fixed empty room used to place an interline note's midpoint.</summary>
        public void AddInterlineGap(Box box, ComputedStyle style, WritingModeInfo flow, Double width)
        {
            if (width <= 0)
            {
                return;
            }

            _fragments.Add(new TextFragment(
                String.Empty,
                0,
                0,
                width,
                style,
                box,
                flow,
                null,
                interlineGap: true));

            Width += width;
        }

        /// <summary>Adds a hoisted interline-note marker at a position inside the line it came from.</summary>
        public void AddMappedInterlineNote(
            Box box,
            ComputedStyle style,
            WritingModeInfo flow,
            InterlineNoteSource note,
            Double position)
        {
            _fragments.Add(new TextFragment(
                String.Empty,
                0,
                0,
                0,
                style,
                box,
                flow,
                null,
                interlineNote: note,
                interlinePosition: position));
        }

        /// <summary>
        /// Gets the last character the line has already set, or null where the line holds nothing or
        /// a box that is not text stands between it and what comes next.
        /// </summary>
        private PreviousText? Previous
        {
            get
            {
                for (var index = _fragments.Count - 1; index >= 0; index--)
                {
                    var fragment = _fragments[index];

                    if (fragment.OutOfFlow)
                    {
                        continue;
                    }

                    if (fragment.Note is not null)
                    {
                        return null;
                    }

                    if (fragment.Length == 0)
                    {
                        continue;
                    }

                    if (fragment.IsReplaced || fragment.Atomic is not null || fragment.Ruby is not null)
                    {
                        return null;
                    }

                    var codePoint = TextClusters.BaseBefore(
                        fragment.Source, fragment.Start + fragment.Length);
                    var stop = fragment.Start + fragment.Length;
                    var take = stop >= 2 && Char.IsSurrogatePair(fragment.Source, stop - 2) ? 2 : 1;
                    var before = fragment.Length > take
                        ? CodePointBefore(fragment.Source, stop - take)
                        : 0;
                    var last = fragment.Length - 1;
                    var trimmed = fragment.Trims is { } trims &&
                        last < trims.Length &&
                        trims[last] != '\0';

                    return new PreviousText(
                        NeighborOf(_measurer, fragment.Style, codePoint, fragment.Flow),
                        fragment.Style,
                        codePoint,
                        trimmed,
                        fragment.Flow,
                        before);
                }

                return null;
            }
        }

        /// <summary>
        /// Gets what adding this range would trim, without adding it: the caller asks before it tests
        /// whether the range fits, and the answer has to be the one the add will take.
        /// </summary>
        public TrimStep PreviewTrim(
            String source,
            Int32 start,
            Int32 length,
            ComputedStyle style,
            WritingModeInfo flow) =>
            PlanTrim(
                _measurer,
                source,
                start,
                length,
                style,
                flow,
                Previous,
                !HasContent,
                _startsParagraph,
                wantMask: false);

        /// <summary>
        /// Gets what adding this range would space, without adding it: the caller asks before it tests
        /// whether the range fits, and the answer has to be the one the add will take.
        /// </summary>
        public AutoSpaceStep PreviewAutoSpace(
            String source,
            Int32 start,
            Int32 length,
            ComputedStyle style,
            WritingModeInfo flow) =>
            PlanAutoSpace(
                _measurer,
                source,
                start,
                length,
                style,
                flow,
                Previous,
                wantMask: false);

        /// <summary>
        /// Takes the room a trimmed character gives up off the last run the line holds, and marks it
        /// in the run's mask. The caller has already found that this character begins the collapse.
        /// </summary>
        private void TrimLast(Double delta, Char code)
        {
            for (var index = _fragments.Count - 1; index >= 0; index--)
            {
                var fragment = _fragments[index];

                if (fragment.OutOfFlow)
                {
                    continue;
                }

                if (fragment.IsReplaced || fragment.Atomic is not null || fragment.Ruby is not null)
                {
                    return;
                }

                if (fragment.Length == 0)
                {
                    continue;
                }

                var last = fragment.Length - 1;
                var chars = (fragment.Trims ?? new String('\0', fragment.Length)).ToCharArray();

                if (last >= chars.Length || chars[last] != '\0')
                {
                    return;
                }

                chars[last] = code;
                fragment.Trims = MaskOf(chars);
                fragment.Width -= delta;
                Width -= delta;
                return;
            }
        }

        /// <summary>
        /// Widens the last run the line holds by the room a boundary to the run arriving after it
        /// adds, which is where the gap between the two is drawn from.
        /// </summary>
        private void WidenLast(Double delta)
        {
            for (var index = _fragments.Count - 1; index >= 0; index--)
            {
                var fragment = _fragments[index];

                if (fragment.OutOfFlow || fragment.Length == 0)
                {
                    continue;
                }

                fragment.Width += delta;
                Width += delta;
                return;
            }
        }

        /// <summary>
        /// Takes the room a replaced space gives up off the last run the line holds, and marks the
        /// space, so the drawing gives it the boundary's room instead of its own.
        /// </summary>
        private void ReplaceLastSpace(Double delta)
        {
            for (var index = _fragments.Count - 1; index >= 0; index--)
            {
                var fragment = _fragments[index];

                if (fragment.OutOfFlow)
                {
                    continue;
                }

                if (fragment.IsReplaced || fragment.Atomic is not null || fragment.Ruby is not null)
                {
                    return;
                }

                if (fragment.Length == 0)
                {
                    continue;
                }

                var text = fragment.Text;
                var at = fragment.Length - 1;

                while (at > 0 && Char.IsLowSurrogate(text[at]))
                {
                    at--;
                }

                var chars = (fragment.AutoSpaces ?? new String('\0', fragment.Length)).ToCharArray();

                if (at >= chars.Length || chars[at] != '\0')
                {
                    return;
                }

                chars[at] = AutoSpaceReplace;
                fragment.AutoSpaces = MaskOf(chars);
                fragment.Width -= delta;
                Width -= delta;
                return;
            }
        }

        /// <summary>
        /// Marks the last character the line holds as one a spaced boundary follows, which is what the
        /// merged run needs to draw the room the boundary between its pieces added.
        /// </summary>
        private static void MarkLastBoundary(TextFragment fragment, Int32 before)
        {
            var text = fragment.Text;
            var at = Math.Max(0, before - 1);

            while (at > 0 && Char.IsLowSurrogate(text[at]))
            {
                at--;
            }

            var chars = (fragment.AutoSpaces ?? new String('\0', text.Length)).ToCharArray();

            if (at < chars.Length)
            {
                chars[at] = AutoSpaceAfter;
            }

            fragment.AutoSpaces = MaskOf(chars);
        }

        private static String? Concat(String? left, Int32 leftLength, String? right, Int32 rightLength)
        {
            if (left is null && right is null)
            {
                return null;
            }

            var chars = new Char[leftLength + rightLength];

            if (left is not null)
            {
                left.CopyTo(0, chars, 0, Math.Min(leftLength, left.Length));
            }

            if (right is not null)
            {
                right.CopyTo(0, chars, leftLength, Math.Min(rightLength, right.Length));
            }

            return MaskOf(chars);
        }

        public void Add(
            String source,
            Int32 start,
            Int32 length,
            Double width,
            ComputedStyle style,
            Box box,
            WritingModeInfo flow,
            InlineAncestor? inline)
        {
            var isSpace = TextChunker.IsCollapsible(source, start, length);

            // CSS Text 3 §4.1.1: a collapsible space at the head of a line is removed - the line begins
            // with what follows the break, not with the white space that stood around it. A mode that
            // keeps its spaces (pre, pre-wrap, break-spaces) keeps them wherever the line starts.
            if (isSpace && _fragments.Count == 0 && style.CollapsesSpaces)
            {
                return;
            }

            // White space that follows white space across an element's edge collapses into the space
            // already on the line: the pieces come from two nodes, so no node's own collapse saw them
            // together, and the line is where their neighbourhood finally shows up.
            if (isSpace && _fragments.Count > 0 && EndsInCollapsibleSpace(_fragments[^1]))
            {
                return;
            }

            var trim = PlanTrim(
                _measurer,
                source,
                start,
                length,
                style,
                flow,
                Previous,
                !HasContent,
                _startsParagraph,
                wantMask: true);

            if (trim.RetroDelta != 0)
            {
                TrimLast(trim.RetroDelta, trim.RetroCode);
            }

            width -= trim.BoundaryDelta;

            var auto = PlanAutoSpace(
                _measurer,
                source,
                start,
                length,
                style,
                flow,
                Previous,
                wantMask: true);

            if (auto.RetroDelta != 0)
            {
                ReplaceLastSpace(auto.RetroDelta);
            }

            // The room of a boundary stands between the run already on the line and the one arriving,
            // so it widens the run behind it: the cursor that places the new run is the sum of the
            // rooms before it. A merge into that run is the same edit, and there the mask has to
            // carry it too, because one run draws both pieces.
            if (auto.BoundaryDelta != 0)
            {
                if (auto.Inside)
                {
                    width += auto.BoundaryDelta;
                }
                else
                {
                    WidenLast(auto.BoundaryDelta);
                }
            }

            // A line that begins with an opening glyph the property hangs gives up that glyph's
            // advance: it stands outside the line, so the room the line is filled to no longer counts
            // it (CSS Text 3 §8.2.1). The line's close cuts the glyph out and takes the same room off
            // the extent it reports.
            if (!isSpace && !HasInk && HangsStart(style, source, start, inline))
            {
                Width -= MeasureChunk(
                    _measurer, style, source, start,
                    TextClusters.UnitsAt(source, start, start + length), flow);
            }

            Enter(inline);

            // A tab draws nothing: the room it took was the line running on to a stop, not a glyph, so
            // the run is drawn as the spaces it stands between. The width the layout settled on is kept
            // either way; only the characters handed to the painter change.
            var drawn = HasTab(source, start, length) ? ToSpaces(source, start, length) : null;

            if (_fragments.Count > 0)
            {
                var last = _fragments[^1];

                // A run that ended in a tab is one nothing may be written into: the tab was charged the
                // room up to its stop, and the character that follows it stands after that room rather
                // than after the one space the tab is drawn as. Starting a new fragment there is what
                // puts the following text on the stop the layout settled on.
                //
                // Chunks of one text node belong to one run. Keeping them together stops the paint
                // commands - and the decorations drawn for them - from being split per word. Pieces
                // that are drawn differently, a turned word among upright text, are not the same run
                // however they happened to be chunked, and neither are pieces from either side of an
                // inline element's edge.
                if (!last.FromTab &&
                    ReferenceEquals(last.Box, box) && last.Flow.Equals(flow) && ReferenceEquals(last.Inline, inline))
                {
                    // The run grows by the piece added and nothing is copied, because the piece is the
                    // next thing in the same string - which every chunk of one text node is. A run whose
                    // pieces come from two strings is the one case that still has to be written out: a
                    // box with more than one text child, and one merge per element rather than one per
                    // character.
                    var before = last.Length;

                    if (drawn is null && ReferenceEquals(last.Source, source) && last.Start + last.Length == start)
                    {
                        last.Length += length;
                    }
                    else
                    {
                        last.MergeInto(last.Text + (drawn ?? source.Substring(start, length)));
                    }

                    last.Trims = Concat(last.Trims, before, trim.Mask, length);

                    // The two pieces are drawn as one run, so the room the boundary added is not a gap
                    // between boxes any more: it rides in the merged run's mask, on the last character
                    // of the piece the run already held.
                    if (auto.BoundaryDelta != 0 && !auto.Inside)
                    {
                        MarkLastBoundary(last, before);
                    }

                    last.AutoSpaces = Concat(last.AutoSpaces, before, auto.Mask, length);
                    last.Width += width;
                    last.FromTab = drawn is not null;
                    Width += width;
                    return;
                }
            }

            var added = new TextFragment(source, start, length, width, style, box, flow, inline)
            {
                Trims = trim.Mask,
                AutoSpaces = auto.Mask,
                AutoSpaceRoom = auto.Room,
            };

            if (drawn is not null)
            {
                added.MergeInto(drawn);
                added.FromTab = true;
            }

            _fragments.Add(added);
            Width += width;
        }

        /// <summary>
        /// Gets whether a line may end where a new run begins. A run from another box is shaped and
        /// chunked on its own, so the chunker cannot see the character standing before it: the pair
        /// is asked here, across the box edge. Two letters from either side of an inline element are
        /// one word, and splitting them put "PASS" and "FAIL" on two lines where the page wrote one
        /// word across two elements (contain-paint-table-002).
        /// </summary>
        public Boolean CanBreakBefore(String source, Int32 start, ComputedStyle style)
        {
            // A box that left the flow holds none of the line's text, so the question is asked of the
            // last run the line really carries (the same reason the other look-backs pass such boxes by).
            TextFragment? last = null;

            for (var index = _fragments.Count - 1; index >= 0; index--)
            {
                if (_fragments[index].OutOfFlow)
                {
                    continue;
                }

                last = _fragments[index];
                break;
            }

            if (last is not { Length: > 0, IsReplaced: false, Ruby: null, Atomic: null })
            {
                return true;
            }

            // A run that opens with a space the mode *keeps* stands where no licence reaches: §4.1.3
            // puts the opportunity after every space the mode keeps, never before the first of them, and
            // the licences for breaking within words (§5.2-§5.5) speak of words - the space in front of
            // one is not one. Measured on "X　XX　X" in a four-character box, where the line ends at the
            // second X and the space hangs past the edge, and on `XXX<span>　</span>X`, where it does not
            // begin the next line either. Two spaces meeting across an element's edge are the exception:
            // there the break after the first of them is exactly the place the mode gives, and
            // `line-break: anywhere` sets the whole question aside.
            var opensWithKeptSpace = TextChunker.IsBreakSpace(source[start]) &&
                !TextChunker.IsCollapsible(source[start]);
            var holdsKeptSpace = TextChunker.IsBreakSpace(last.Source[last.Start + last.Length - 1]) &&
                !TextChunker.IsCollapsible(last.Source[last.Start + last.Length - 1]);

            if (opensWithKeptSpace && !holdsKeptSpace && !style.BreaksAnywhere)
            {
                return false;
            }

            // Where the page licensed a break anywhere - `line-break: anywhere`, `overflow-wrap` in
            // either value, `break-spaces`, `word-break: break-all` - the rules between the two
            // characters are set aside, and the edge of an inline element is no place to bring them
            // back.
            if (style.BreaksAnywhere || style.BreaksBetweenLetters || style.WrapsLongWords || style.WrapsInsideSpaces)
            {
                return true;
            }

            var end = last.Start + last.Length;

            return LineBreaks.MayBreak(
                TextChunker.CodePointBefore(last.Source, end),
                TextChunker.CodePointAt(source, start),
                style.WordBreakingRules);
        }

        private static Boolean HasTab(String source, Int32 start, Int32 length) =>            source.IndexOf('\t', start, length) >= 0;

        private static String ToSpaces(String source, Int32 start, Int32 length) =>
            source.Substring(start, length).Replace('\t', ' ');

        // White space only collapses into the run before it where the mode collapses it. `break-spaces`
        // keeps every character of a preserved run, so two of them meeting across an element's edge stay
        // two, which is what makes the run long enough to break inside.
        private static Boolean EndsInCollapsibleSpace(TextFragment fragment) =>
            !fragment.IsReplaced &&
            fragment.Atomic is null &&
            fragment.Ruby is null &&
            fragment.Length > 0 &&
            fragment.Style.CollapsesSpaces &&
            TextChunker.IsCollapsible(fragment.Source, fragment.Start + fragment.Length - 1, 1);

        /// <summary>
        /// Adds a ruby pair, which never joins the run before it: the base has an annotation of its own
        /// and the two are drawn as separate pieces whatever they are set in.
        /// </summary>
        /// <param name="width">The room the pair needs, which is the wider of the base and the annotation.</param>
        /// <param name="baseWidth">The extent the base itself takes, which centres it in that room.</param>
        public void AddRuby(
            String text,
            Double width,
            ComputedStyle style,
            Box box,
            WritingModeInfo flow,
            RubyAnnotation ruby,
            Double baseWidth,
            Double? hang,
            InlineAncestor? inline)
        {
            Enter(inline);

            _fragments.Add(new TextFragment(
                text,
                0,
                text.Length,
                width,
                style,
                box,
                flow,
                inline,
                ruby: ruby,
                baseWidth: baseWidth,
                hang: hang));

            Width += width;
        }

        /// <summary>
        /// Moves the line into the chain of inline elements the next run sits in, taking the padding
        /// and border the edges of that move are worth along with it.
        /// </summary>
        private void Enter(InlineAncestor? inline) =>
            Width += Advance(_open, inline, _carried, origin: 0, apply: true, step: null);

        /// <summary>
        /// Closes the line and hands it back, with the extent it takes along the inline axis and the
        /// report of which elements it ends inside.
        /// </summary>
        public Line Build(
            ITextMeasurer measurer,
            Double maxInline,
            HashSet<InlineAncestor>? carried,
            Boolean forced,
            Boolean hyphen)
        {
            TrimTrailingSpaces(measurer);

            var line = new Line { ContinuesFromPrevious = carried };
            line.Fragments.AddRange(_fragments);

            // The extent is worked out afresh from the runs the trimming left, because the padding and
            // border a chain change brings in are part of it and a run taken back out takes them with it.
            var open = new List<OpenInline>();
            var used = 0.0;

            // Gathered from nothing rather than from zero: a face set on a line shorter than itself has a
            // negative descent as well as a negative ascent, and starting from zero would hold every line
            // to at least the height of the tallest font on it.
            var ascent = Double.NegativeInfinity;
            var descent = Double.NegativeInfinity;
            Double? statedLine = null;
            var onlyText = true;
            var noteBlockSize = 0.0;
            var noteGap = 0.0;

            // How far each fragment reaches above and below the baseline it stands on, kept because a
            // box's alignment is worked out from it once the line knows how far it reached.
            var above = new Double[line.Fragments.Count];
            var below = new Double[line.Fragments.Count];
            var runAscent = Double.NegativeInfinity;
            var runDescent = Double.NegativeInfinity;
            var inFlow = false;

            for (var position = 0; position < line.Fragments.Count; position++)
            {
                var fragment = line.Fragments[position];

                used += Advance(open, fragment.Inline, carried, origin: 0, apply: true, step: null);
                used += fragment.Width;

                if (fragment.OutOfFlow)
                {
                    // A box that left the flow takes no room across the line and reaches nothing above or
                    // below its baseline: it is placed against a containing block of its own, and the line
                    // it stood on is neither wider nor taller for it.
                    continue;
                }

                if (fragment.InterlineGap)
                {
                    above[position] = 0;
                    below[position] = 0;
                    continue;
                }

                if (fragment.InterlineNote is not null || fragment.MarginNote is not null)
                {
                    inFlow = true;
                    above[position] = 0;
                    below[position] = 0;
                    continue;
                }

                inFlow = true;

                // A note occupies room along the line. Its block size joins the line's ascent and
                // descent symmetrically, so a note larger than the base line grows the line around it.
                if (fragment.Note is not null)
                {
                    onlyText = false;
                    noteBlockSize = Math.Max(noteBlockSize, fragment.Note.BlockSize);
                    noteGap = Math.Max(noteGap, fragment.Note.LineGap);
                    continue;
                }

                // A run lifted out of its row by vertical-align reaches past the line-height its
                // text asks for, and that reach is the line's own height rather than an overflow
                // of the face; the clamp below is only for the face's content area.
                if (!IsBaseline(fragment.Style) || fragment.BaselineShift != 0)
                {
                    onlyText = false;
                }

                // An inline element that holds nothing gives the line no height of its own: the line
                // still exists - the room the box takes along it is what keeps the margins around the
                // block from folding through it - and is zero-height otherwise (CSS 2.1 §9.4.2, §10.8).
                if (fragment.Length == 0 &&
                    !fragment.IsReplaced &&
                    fragment.Atomic is null &&
                    fragment.Ruby is null &&
                    CarriesAnInlineEdge(fragment.Inline))
                {
                    above[position] = 0;
                    below[position] = 0;
                    continue;
                }

                // A replaced inline rests its bottom margin edge on the baseline, so it contributes
                // its whole height as ascent and nothing as descent.
                if (fragment.IsReplaced)
                {
                    onlyText = false;
                    // Its bottom margin edge rests on the baseline, so it reaches nothing below it - a
                    // descent of zero, which is not the same as leaving it out where the line gathers
                    // descents from nothing because a run of text may hang above the baseline.
                    above[position] = fragment.ContentHeight;
                    below[position] = 0;
                    ascent = Math.Max(ascent, above[position]);
                    descent = Math.Max(descent, below[position]);
                    continue;
                }

                // An inline-block is as tall as its own content and stands on its own baseline, so the
                // line grows by whatever the box reaches above and below that either way - its margins
                // across the line included, since those are part of the room it takes up there.
                if (fragment.Atomic is { } atomic)
                {
                    onlyText = false;
                    above[position] = atomic.BlockStartMargin + atomic.Ascent;
                    below[position] = atomic.BlockEndMargin + (atomic.BlockSize - atomic.Ascent);
                    ascent = Math.Max(ascent, above[position]);
                    descent = Math.Max(descent, below[position]);
                    continue;
                }

                // The glyphs of one element may come from several fonts, so the metrics are the ones the
                // run is really set with rather than the ones the requested font would give: a line whose
                // characters fell to a fallback is as tall as that fallback (CSS 2.2 §10.8.1).
                var metrics = measurer.GetRunMetrics(fragment.Text, fragment.Style.GetFont());
                var fragmentAscent = metrics.Ascent;
                var fragmentDescent = metrics.Descent;
                var lineHeight = fragment.Style.LineHeight;

                if (lineHeight is { } stated && (statedLine is null || stated > statedLine))
                {
                    statedLine = stated;
                }

                if (lineHeight.HasValue)
                {
                    // Half the leading, negative included: a face taller than the line height the page
                    // asked for overflows a line box that is that height and no taller.
                    var halfLeading = (lineHeight.Value - (fragmentAscent + fragmentDescent)) / 2;
                    fragmentAscent += halfLeading;
                    fragmentDescent += halfLeading;
                }

                // An annotation stands over its base, on the block-start side, but it does not make the
                // line taller than the base's own line-height does (CSS Ruby 1 §3.6): the leading the
                // line already has is where a reading is meant to sit. Only a reading that reaches
                // past that leading - the distance from the base's content edge to the line box's edge -
                // adds the difference, so that stacked lines do not overlap.
                if (fragment.Ruby is { } annotation)
                {
                    onlyText = false;
                    if (annotation.Under)
                    {
                        fragmentDescent += Math.Max(0, annotation.Block - (fragmentDescent - metrics.Descent));
                    }
                    else
                    {
                        fragmentAscent += Math.Max(0, annotation.Block - (fragmentAscent - metrics.Ascent));
                    }
                }
                else if (fragment.Style.Emphasis is { IsSet: true })
                {
                    // Marks are drawn in the manner of a reading, but they take no room of their own:
                    // the line keeps the height its text asks for and the marks stand outside it
                    // (CSS Text Decoration 3 §3.3). A page that wants room for them leaves it with
                    // line-height or padding, as the tests beside a reading do.
                }

                above[position] = fragmentAscent;
                below[position] = fragmentDescent;
                runAscent = Math.Max(runAscent, fragmentAscent);
                runDescent = Math.Max(runDescent, fragmentDescent);
                ascent = Math.Max(ascent, fragmentAscent);
                descent = Math.Max(descent, fragmentDescent);
            }

            // An element the line ends inside may have been cut there, and only the line below can say
            // whether it was: until that is known the end edge it would draw here is counted, so that
            // the line is neither aligned nor broken as if the element had none. MarkSlices takes the
            // edge back off the ones that turn out not to carry on.
            if (open.Count > 0)
            {
                var pending = new HashSet<InlineAncestor>();

                foreach (var item in open)
                {
                    pending.Add(item.Ancestor);
                    used += item.Ancestor.InlineEndInset;
                }

                line.ContinuesToNext = pending;
            }

            // Every line box starts with the container's strut: its own font and line height, a
            // zero-width box standing on the baseline (CSS 2.1 §10.8). Usually the runs on the line ask
            // for as much or more, but a run set at no size - or one whose line height is zero - does
            // not, and the line keeps the room the block's text would have taken.
            // A line holding an inline-block, a picture or a ruby pair already says how far it reaches
            // on its own; the corpus's buttons and badges are measured without the strut's descent added
            // under the box that stands on the baseline, so the strut answers only for a line of text.
            // A column's width comes from the runs set down it, each standing the block's font and
            // line height as it inherits them; the strut is asked only where a break left a line with
            // no run to speak for it, which no run on that line can make wider or narrower.
            // A line holding nothing but the anchors of empty inline elements exists for the room they
            // take along it and for nothing else: it takes no height from the strut (CSS 2.1 §9.4.2).
            var anchorsOnly = HasContent && !_fragments.Exists(fragment =>
                !fragment.OutOfFlow &&
                (fragment.Length > 0 || fragment.IsReplaced || fragment.Atomic is not null ||
                    fragment.Ruby is not null || fragment.Note is not null ||
                    fragment.InterlineNote is not null));

            var textual = !anchorsOnly && (_vertical
                ? _fragments.Count == 0 ||
                    _fragments.TrueForAll(fragment => fragment.OutOfFlow || fragment.Note is not null)
                : _fragments.TrueForAll(fragment =>
                    !fragment.IsReplaced && fragment.Atomic is null && fragment.Ruby is null));

            var strut = textual && _fallback is { } fallback && (HasContent || forced)
                ? Stretched(measurer, fallback)
                : ((Double Ascent, Double Descent)?)null;

            if (strut is { } strutMetrics)
            {
                ascent = Double.IsNegativeInfinity(ascent)
                    ? strutMetrics.Ascent
                    : Math.Max(ascent, strutMetrics.Ascent);
                descent = Double.IsNegativeInfinity(descent)
                    ? strutMetrics.Descent
                    : Math.Max(descent, strutMetrics.Descent);

                // The block's own line-height is a floor for the line, however small the runs on it
                // ask to be (CSS 2.1 §10.8.1). It is the strut that carries that answer.
                var strutHeight = strutMetrics.Ascent + strutMetrics.Descent;

                // Only a line height the block *stated* also caps the line: `normal` says the line is as
                // tall as the faces on it, so a block written at no size must not shrink the line its
                // runs ask for - which is how a `pre` at `font-size: 0` came to lay two twelve-pixel
                // lines on top of one another (CSS 2.1 §10.8.1).
                if (_fallback?.LineHeight is not null)
                {
                    statedLine = statedLine is { } stated ? Math.Max(stated, strutHeight) : strutHeight;
                }
            }
            else if (Double.IsNegativeInfinity(ascent))
            {
                // A line holding nothing in flow - one whose only content left the flow, and which
                // exists only to say where that box's static position is - is a zero-height line box
                // (CSS 2.1 §9.4.2), unless a break ended it (the strut then keeps it standing).
                ascent = 0;
                descent = 0;
            }

            // The line box is grown from the boxes as they end up standing (CSS 2.1 §10.8.1): a run
            // raised by `super`, or aligned to the `top` of the line, lifts the height the line takes,
            // and a lowered one reaches below it. The reference each alignment is taken against is the
            // line as it was gathered above, before any box on it was moved.
            var alignedReach = Double.NegativeInfinity;
            var alignedDepth = Double.NegativeInfinity;
            Dictionary<InlineAncestor, Double>? bands = null;

            if (inFlow)
            {
                for (var position = 0; position < line.Fragments.Count; position++)
                {
                    var fragment = line.Fragments[position];

                    if (fragment.OutOfFlow)
                    {
                        continue;
                    }

                    if (fragment.InterlineGap || fragment.InterlineNote is not null ||
                        fragment.MarginNote is not null)
                    {
                        continue;
                    }

                    if (IsBaseline(fragment.Style) && fragment.Inline?.AnyAlignment != true)
                    {
                        // Nothing asks for this box to stand anywhere but on the baseline the line
                        // shares, so it stays where the gathering above put it.
                        alignedReach = Math.Max(alignedReach, above[position]);
                        alignedDepth = Math.Max(alignedDepth, below[position]);
                        continue;
                    }

                    // Each box on the chain stands on the baseline of the box around it, so an aligned
                    // element carries both the text inside it and the bands of the elements within it -
                    // and the innermost box is the one the run's own <c>vertical-align</c> was written
                    // on, which is why it is counted once here rather than again for the run.
                    // `top` and `bottom` are the exception: they are taken against the line box rather
                    // than against the parent, so they say where the chain stands instead of adding.
                    var parents = Chain(fragment.Inline);
                    var running = 0.0;

                    if (parents.Count == 0 && fragment.Box.Element is not null)
                    {
                        // A box with an element of its own is aligned by what that element says. Text
                        // sitting straight in the block has no such box: its style is the block's, and a
                        // block's own <c>vertical-align</c> places the block - a cell in its row - and
                        // never lifts the words the block holds (CSS 2.1 §10.8.1, measured against Chrome
                        // on a div and a table cell that both say `middle`).
                        running = Alignment(
                            measurer, fragment.Style, _fallback, above[position], below[position], ascent, descent);
                    }

                    for (var step = parents.Count - 1; step >= 0; step--)
                    {
                        var ancestor = parents[step];
                        var own = Alignment(
                            measurer,
                            ancestor.Box.Style,
                            ancestor.Parent?.Box.Style ?? _fallback,
                            ancestor.Ascent,
                            ancestor.Descent,
                            ascent,
                            descent);

                        running = own == 0
                            ? running
                            : IsAtTheLineEdge(ancestor.Box.Style)
                                ? own
                                : running + own;

                        if (running != 0)
                        {
                            bands ??= new Dictionary<InlineAncestor, Double>();
                            bands[ancestor] = running;
                        }
                    }

                    fragment.BaselineShift = running;
                    alignedReach = Math.Max(alignedReach, above[position] - running);
                    alignedDepth = Math.Max(alignedDepth, below[position] + running);
                }
            }

            // The line box is grown from the boxes as they end up standing, not from where they stood
            // before <c>vertical-align</c> moved them (CSS 2.1 §10.8.1). A box aligned to the top of the
            // line hangs its whole height below that edge, so the reach and depth gathered above are the
            // reference the alignment was measured against, not the line's final extent.
            if (!Double.IsNegativeInfinity(alignedReach))
            {
                // A run of text still reaches as far as its face does even when `vertical-align` lifts
                // it, which is the room the line keeps under a superscript; a box that stands on its
                // own baseline is measured only where the alignment leaves it.
                ascent = Math.Max(alignedReach, runAscent);
                descent = Math.Max(alignedDepth, runDescent);

                if (strut is { } band)
                {
                    ascent = Math.Max(ascent, band.Ascent);
                    descent = Math.Max(descent, band.Descent);
                }
            }

            // A note stands across the line rather than on its baseline. It therefore contributes its
            // total block size to the line's centre line, not half of it above and below the text
            // baseline: an asymmetric font baseline must not make a note that exactly fills the line
            // grow it. A larger note adds the same room at both edges, keeping its margin box centred
            // in the line that now contains it.
            var lineBlockSize = ascent + descent;

            if (noteBlockSize > lineBlockSize)
            {
                // The note first expands the line to its own margin-box block size. The body's
                // inter-line gap is then added outside that expanded line, once on each edge.
                var expanded = noteBlockSize;
                var withGap = expanded + (noteGap * 2);
                var extra = (withGap - lineBlockSize) / 2;
                ascent += extra;
                descent += extra;
            }

            // A line the page's own soft hyphen ended draws the mark where the character stood: the
            // hyphenate character takes its place and its room, and the run keeps reading as the word
            // the page spelled (CSS Text 3 §6.2). A line that ends anywhere else keeps the hyphen as the
            // invisible character it is, taking nothing and drawing nothing.
            if (hyphen)
            {
                // The mark stands in the run the word was written in, which is the last run that holds
                // text of this line: a box that left the flow may stand after it and is not part of the
                // word (the same reason the look-backs pass such boxes by).
                TextFragment? last = null;
                var at = -1;

                for (var index = _fragments.Count - 1; index >= 0; index--)
                {
                    if (_fragments[index].OutOfFlow)
                    {
                        continue;
                    }

                    last = _fragments[index];
                    at = index;
                    break;
                }

                if (last is { Length: > 0, IsReplaced: false, Ruby: null, Atomic: null } &&
                    last.Source[last.Start + last.Length - 1] == '­' &&
                    !String.IsNullOrEmpty(last.Style.HyphenateCharacter))
                {
                    // A run of soft hyphens stands where the page could have cut the word once: the whole
                    // run gives way to the single mark the break draws.
                    var text = last.Text;
                    var trailing = 0;

                    while (trailing < text.Length && text[text.Length - 1 - trailing] == (Char)0x00AD)
                    {
                        trailing++;
                    }

                    var mark = HyphenMark(last.Style, last.Flow);
                    var shy = text.Substring(text.Length - trailing, trailing);
                    var gained = MeasureChunk(measurer, last.Style, mark, 0, mark.Length, last.Flow) -
                        MeasureChunk(measurer, last.Style, shy, 0, shy.Length, last.Flow);

                    last.MergeInto(text[..^trailing] + mark);
                    last.Width += gained;
                    used += gained;

                    // The mark now stands in the run's text, so the line is as tall as the character it
                    // draws: a column's upright hyphen is taller than the letters beside it, and the
                    // line that does not count it stands a little narrower than the same word spelled
                    // with the hyphen written out (hyphens-vertical-001).
                    var drawnMark = measurer.GetRunMetrics(mark, last.Style.GetFont());
                    var markAscent = drawnMark.Ascent;
                    var markDescent = drawnMark.Descent;

                    if (last.Style.LineHeight is { } markLine)
                    {
                        var half = (markLine - (markAscent + markDescent)) / 2;
                        markAscent += half;
                        markDescent += half;
                    }

                    above[at] = Math.Max(above[at], markAscent);
                    below[at] = Math.Max(below[at], markDescent);
                    ascent = Math.Max(ascent, above[at]);
                    descent = Math.Max(descent, below[at]);
                }
            }

            line.BandShifts = bands;
            line.Ascent = ascent;
            line.Height = ascent + descent;

            // A line of text is as tall as the line-height it asks for, however much taller the
            // face's own content area is: the glyphs overflow the line rather than growing it
            // (CSS 2.1 §10.8.1). A replaced box, an atomic inline or a ruby reading is content of
            // its own and is left to grow the line as it does.
            if (onlyText && statedLine is { } limit && line.Height > limit)
            {
                line.Height = limit;
            }

            // The end of the line is settled once every line is built: the trim a closing punctuation
            // gives up and the glyph a hanging value takes outside the line are decisions about the
            // line's own edge, and which of the two applies depends on whether this is the last line.
            line.Hanging = HangingTrail(measurer);
            line.Used = used - line.Hanging;
            line.Indent = _indent;
            line.Governing = Governing(line.Fragments.Count > 0 ? line.Fragments[0] : null);
            line.Alignment = line.Governing?.TextAlign ?? CssInitialValues.TextAlign;
            line.Offset = Aligned(line, maxInline);

            return line;
        }

        /// <summary>
        /// Gets the strut of a line box: the container's font and line height, with the leading the
        /// line height puts above and below the face it is set in.
        /// </summary>
        private static (Double Ascent, Double Descent) Stretched(ITextMeasurer measurer, ComputedStyle style)
        {
            var metrics = measurer.GetMetrics(style.GetFont());
            var ascent = metrics.Ascent;
            var descent = metrics.Descent;

            if (style.LineHeight is { } lineHeight)
            {
                var halfLeading = (lineHeight - (ascent + descent)) / 2;
                ascent += halfLeading;
                descent += halfLeading;
            }

            return (ascent, descent);
        }

        /// <summary>
        /// Gets the style the line is aligned by. A box the line carries whole brings its own
        /// <c>text-align</c>, but that states how the text inside the box is set and not where the box
        /// stands, so the style the line was built with governs there; every other line takes the
        /// alignment from what is on it, which is how a <c>::first-line</c> rule reaches the line it
        /// styles (CSS Text 3 §6.2).
        /// </summary>
        private ComputedStyle? Governing(TextFragment? first) =>
            first is null
                ? _fallback
                : first.Atomic is not null || first.IsReplaced || first.Note is not null
                    ? _fallback ?? first.Style
                    : first.Style;

        /// <summary>
        /// Gets the width of the white space the line trails into where its mode hangs it, marking each
        /// run it took the room off so that the painter draws the word without it: a `pre-wrap` line
        /// keeps those spaces rather than drawing them away, and they take none of its advance
        /// (CSS Text 3 §4.1.3) - so the room it was given is shared over what does not hang.
        /// </summary>
        private Double HangingTrail(ITextMeasurer measurer)
        {
            var hanging = 0.0;

            for (var index = _fragments.Count - 1; index >= 0; index--)
            {
                var fragment = _fragments[index];

                if (fragment.IsReplaced ||
                    fragment.Atomic is not null ||
                    fragment.Ruby is not null ||
                    fragment.Length == 0 ||
                    !fragment.Style.WhiteSpace.Equals("pre-wrap", StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }

                var trailing = fragment.TrailingSpaces();

                if (trailing == 0)
                {
                    break;
                }

                fragment.Hanging = trailing;

                // A fragment that is nothing but the spaces hanging is worth what the layout measured
                // it at, which for a run carrying tabs is the distance to the last stop rather than a
                // count of spaces.
                hanging += trailing < fragment.Length
                    ? MeasureChunk(
                        measurer,
                        fragment.Style,
                        fragment.Source,
                        fragment.Start + fragment.Length - trailing,
                        trailing,
                        fragment.Flow)
                    : fragment.Width;

                if (trailing < fragment.Length)
                {
                    break;
                }
            }

            return hanging;
        }

        /// <summary>
        /// Takes the collapsible spaces a line trails into off, which a line never draws at its end.
        /// Only the runs themselves are touched: the extent is worked out from what is left when the
        /// line is built.
        /// </summary>
        private void TrimTrailingSpaces(ITextMeasurer measurer)
        {
            while (_fragments.Count > 0)
            {
                var last = _fragments[^1];

                // A pair is not a run of text that can be shortened: its base belongs to the annotation
                // drawn over it, so trailing space inside one is left where it is.
                if (last.Ruby is not null)
                {
                    break;
                }

                // White space a preformatted mode preserves is not taken off: it hangs at the end of the
                // line it was written on rather than being removed, and a line holding nothing else is
                // still a line box at the height of its strut (CSS Text 3 §4.1.3). Only the modes that
                // collapse their spaces lose the run at the line's end.
                if (!last.Style.CollapsesSpaces)
                {
                    break;
                }

                if (TextChunker.IsCollapsible(last.Source, last.Start, last.Length))
                {
                    _fragments.RemoveAt(_fragments.Count - 1);
                    continue;
                }

                var trailing = last.TrailingSpaces();

                if (trailing == 0)
                {
                    break;
                }

                if (trailing >= last.Length)
                {
                    _fragments.RemoveAt(_fragments.Count - 1);
                    continue;
                }

                // Shortening the run is moving its end, not cutting a string: the characters the line
                // drops were never copied out of the text node to begin with.
                last.Length -= trailing;
                last.Width = MeasureChunk(measurer, last.Style, last.Source, last.Start, last.Length, last.Flow);
                break;
            }
        }
    }
}

using AngleSharp.Css.Dom;
using Hutu.Rendering.Boxes;
using System;
using System.Collections.Generic;
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

    /// <summary>Drops the plans, which belong to one layout pass as the engine's own caches do.</summary>
    internal void ResetPlanCache() => _plans.Clear();

    /// <summary>
    /// Gets the lines the container's inline content is broken into at this extent, making them the
    /// first time the pass asks and handing back the same lines every time it asks again.
    /// </summary>
    private List<Line> Plan(
        Box container,
        Double maxInline,
        WritingModeInfo flow,
        InlineContext context,
        Double indent,
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

        _plans[key] = lines;
        return lines;
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
        Double indent,
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
        Double indent,
        InlineStart start)
    {
        var items = new List<InlineItem>();
        Collect(container, items, flow, null, maxInline);

        var kept = Resume(items, start);

        return kept.Count == 0
            ? new List<Line>()
            : BuildLines(kept, maxInline, flow, context, container.Style, indent);
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
                    : Fragmentainer.BalanceLines(heights, columns, breaks.Orphans, breaks.Widows, starts, ends);

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

    private IntrinsicSize MeasureIntrinsicCore(Box container, Double basis, WritingModeInfo flow, InlineContext context)
    {
        var items = new List<InlineItem>();
        Collect(container, items, flow, null, basis);

        // The first line carries the box's indentation ahead of anything set on it, so both extents the
        // content asks for are that much wider: one line is the indent and the line, and the narrowest
        // break is the indent and the longest thing that cannot be broken.
        var indent = Indentation(container, flow, basis);

        var minContent = 0.0;
        var maxContent = indent;
        var unbreakable = indent;

        // The widest line of those a forced break makes. A forced break is honoured even by the
        // max-content size (CSS Sizing 3 §5.1), so the lines it makes lie beside each other rather than
        // one after another: what the content asks for is the widest of them, not their sum.
        var widest = 0.0;

        // Whether the stream has reached anything a line would draw. White space ahead of it asks for
        // no room, because the line it would begin is the one line that does not draw it.
        var sawContent = false;

        // The width of a collapsible run held back until something is drawn after it. CSS 2.1 §16.6.1
        // removes a space at the end of a line just as it removes one at the beginning, so a trailing
        // space is only room the content asks for once the line turns out to carry something past it.
        var trailing = 0.0;

        // Nothing here is broken into lines, so an element is opened once and stays open: the padding
        // it brings with it is charged to the run that enters it, wherever in the stream that is.
        var open = new List<OpenInline>();

        foreach (var item in items)
        {
            if (item.IsBreak)
            {
                minContent = Math.Max(minContent, unbreakable);
                widest = Math.Max(widest, maxContent);

                // The line the break ends takes its trailing space with it, and the one it begins
                // starts where any line but the first does: no indentation, and no space of its own
                // ahead of the first thing drawn on it.
                maxContent = 0;
                trailing = 0;
                sawContent = false;
                unbreakable = 0;
                continue;
            }

            // Padding and border are part of what an element asks for, so they count towards both the
            // extent the content needs on one line and the extent the narrowest break can leave it.
            var insets = Advance(open, item.Inline, carried: null, origin: 0, apply: true, step: null);
            maxContent += insets;
            unbreakable += insets;

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
                continue;
            }

            // An inline-block is atomic too: it takes the extent its own content asks for and offers no
            // break inside itself, however its content would have been broken had it stood on its own.
            if (item.IsAtomic)
            {
                var intrinsic = context.Engine.MeasureIntrinsic(item.Box, context.Containing);
                var (start, end) = flow.InlineMargins(item.Box.Style.Margin.Resolve(basis));
                var extent = intrinsic.MaxContent + start + end;

                maxContent += trailing + extent;
                trailing = 0;
                unbreakable += extent;
                sawContent = true;
                minContent = Math.Max(minContent, intrinsic.MinContent + start + end);
                continue;
            }

            // A ruby pair cannot be broken between its two parts, so it asks for the room the wider of
            // them needs and offers no break opportunity inside itself.
            if (item.IsRuby)
            {
                var extent = Math.Max(
                    Measure(item.Style, item.Text!, flow),
                    Measure(item.AnnotationStyle, item.AnnotationText!, flow));

                maxContent += trailing + extent;
                trailing = 0;
                unbreakable += extent;
                sawContent = true;
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
                    continue;
                }

                // Something the line draws follows whatever space was held back, so the space between
                // the two runs is room the content asks for after all - and room inside the piece, where
                // the style offers no break for it to be left at.
                maxContent += trailing + width;

                if (!item.Style.WrapsLines)
                {
                    unbreakable += trailing;
                }

                trailing = 0;
                sawContent = true;

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
            maxContent += item.Ancestor.InlineEndInset;
            unbreakable += item.Ancestor.InlineEndInset;
        }

        minContent = Math.Max(minContent, unbreakable);
        widest = Math.Max(widest, maxContent);

        return new IntrinsicSize(minContent, Math.Max(widest, minContent));
    }

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
                : Fragmentainer.BalanceLines(heights, columns, breaks.Orphans, breaks.Widows, starts, ends);

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
            spills: multi.Fills && !wraps);

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

    /// <summary>Gets the heights the lines take when stacked, the gaps a float made included.</summary>
    private static List<Double> Heights(List<Line> lines)
    {
        var heights = new List<Double>(lines.Count);

        foreach (var line in lines)
        {
            heights.Add(Math.Max(0, line.Height) + line.GapBefore);
        }

        return heights;
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
            if (!flow.IsVertical)
            {
                BidiOrder(line, flow, container.Style.PlaintextParagraph);
            }

            // The line box spans the whole inline extent of the content box and only as much of the
            // block one as it needs, so in a row it is the content width and the line's own height,
            // and in a column the content height and the band the text runs down.
            var lineBox = new LayoutBox(container, LayoutKind.Line)
            {
                BorderBox = flow.Map(contentBox, line.Left, line.Extent, cursor, line.Height),
                Baseline = flow.BlockOffsetFromLeft(line.Height, line.Ascent),
                Flow = flow,
            };

            var open = new List<OpenInline>();
            var origin = line.Offset + line.Left;
            var last = line.Fragments[^1];

            foreach (var fragment in line.Fragments)
            {
                // The inline elements the run sits inside are opened and closed around it, each taking
                // a box that begins and ends where the run reaches its edges. The step charges the same
                // padding and border the line was measured with, applied here one edge at a time so
                // that every box lands where the extent says it should.
                step.Clear();
                Advance(open, fragment.Inline, line.ContinuesFromPrevious, origin, apply: true, step);

                // An element the run leaves is never cut: a cut is what carrying on to the next line
                // is, and an element that ends in the middle of one ends for good. Its box runs to the
                // far side of its own end edge, which is the edge the run it holds stops short of - and
                // its end margin then takes its room on the line, after that box.
                foreach (var left in step.Closed)
                {
                    origin += left.Ancestor.InlineEndInset;
                    CloseBox(left, origin, contentBox, flow, line, cursor, context, placement);
                    origin += left.Ancestor.InlineEndMargin;
                }

                foreach (var entered in step.Opened)
                {
                    origin += entered.Ancestor.InlineStartMargin;
                    OpenBox(entered, open, lineBox, flow, line);
                    origin += entered.Inset;
                }

                // The spaces a `pre-wrap` line hangs are drawn where they stand but take none of the
                // room the line was given, so the box of the run that trails into them is narrowed by
                // that much: either way the glyphs land where a line without the spaces would have put
                // them, which is what a right-to-line-end hanging looks like from both directions.
                var room = ReferenceEquals(fragment, last) ? fragment.Width - line.Hanging : fragment.Width;

                Place(fragment, open, lineBox, contentBox, flow, line, origin, cursor, context, room, placement, _measurer);
                origin += fragment.Width;
            }

            // The elements the line ends inside carry on to the next line, so each keeps its end edge
            // only if it does not: where it was cut, that edge belongs to the line the element ends on.
            for (var index = open.Count - 1; index >= 0; index--)
            {
                var left = open[index];
                var cut = line.ContinuesToNext?.Contains(left.Ancestor) == true;

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

        return cursor;
    }

    /// <summary>
    /// Splits a line's runs where their direction changes, so each piece of text is shaped in the
    /// direction it is read in: a Latin word inside a right-to-left line keeps its letters in the order
    /// a reader of it expects, and a neutral at the end of such a line is set at the line's own level
    /// (UAX #9 W, N, I). The pieces keep the logical order of the runs - the line places its fragments
    /// from the edge its own direction begins at, and the shaper turns the glyphs of a run of the other
    /// direction within its box - so only the boundaries are new.
    /// </summary>
    /// <remarks>
    /// The levels are resolved over the whole line, not over one fragment at a time: what the strong
    /// text before a space says about it is a question about the line, and the whitespace at the line's
    /// end is the only whitespace the rules reset (L1). A line that holds no change of direction is left
    /// as the markup wrote it and pays one pass over its classes.
    /// </remarks>
    private void BidiOrder(Line line, WritingModeInfo flow, Boolean plaintextParagraph)
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
            var level = plaintextParagraph || group[0].Style.PlaintextParagraph
                ? Bidi.ParagraphLevel(text, baseLevel)
                : baseLevel;
            var levels = Bidi.Levels(text, level);
            var offset = 0;

            foreach (var fragment in group)
            {
                var own = new Int32[fragment.Length];
                Array.Copy(levels, offset, own, 0, fragment.Length);
                offset += fragment.Length;

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
                        fragment.Style, fragment.Box, fragment.Flow, fragment.Inline));
                }

                if (pieces.Count > 0)
                {
                    if (Math.Abs(earned - fragment.Width) > 0.0001)
                    {
                        pieces[^1].Width += fragment.Width - earned;
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
                Flush();
                ordered.Add(fragment);
                continue;
            }

            group.Add(fragment);
            joined.Append(fragment.Text);
        }

        Flush();

        if (!changed)
        {
            return;
        }

        line.Fragments.Clear();
        line.Fragments.AddRange(ordered);
    }

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
        var cutStart = line.ContinuesFromPrevious?.Contains(ancestor) == true;
        var cutEnd = line.ContinuesToNext?.Contains(ancestor) == true;

        item.Node = new LayoutBox(ancestor.Box, LayoutKind.Inline)
        {
            Flow = flow,
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
        var blockOffset = cursor + line.Ascent + shift - ancestor.Ascent - ancestor.BlockStartInset;
        var blockSize = ancestor.Ascent + ancestor.Descent + ancestor.BlockStartInset + ancestor.BlockEndInset;

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
            placement.Shift(item.Node, PositionedLayout.RelativeOffset(position, context.Containing.ContainingBlock));
        }
    }

    /// <summary>Builds the node a run is drawn through and puts it in the innermost box open around it.</summary>
    private static void Place(
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

            // An inline element the run sits inside answers for the box when it is the nearest one
            // positioned - which the line cannot settle on its own, since the rectangle is drawn over
            // every fragment of that element (CSS Positioned Layout 3 2.1).
            var anchor = AnchoredBy(fragment.Inline);

            if (anchor is null)
            {
                Attach(open, lineBox, context.Engine.ArrangePositioned(
                    fragment.Box,
                    new Offset(stood.X, stood.Y),
                    against));
            }
            else
            {
                var (owner, index) = Standing(open, lineBox);
                placement.Hold(fragment.Box, anchor, owner, index, against, new Offset(stood.X, stood.Y));
            }

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
                cursor + line.Ascent - atomic.Ascent,
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
                Font = fragment.Style.GetFont(),
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
            // The annotation takes a strip at one end of the line's block extent and the base keeps
            // the rest, which is the end away from the reading. Along the inline axis the two share the
            // room the wider of them asked for, each centred in it, which is what stops a long reading
            // from hanging off one side.
            var baseBlock = line.Height - annotation.Block;

            // An upright character in a column is drawn centred in its box, so a base drawn that way
            // has to be boxed centred on the band as well - boxed against the far end it would sit half
            // a reading off the column line its neighbours stand on. A base that is turned, or set in a
            // line, is drawn from the line's shared baseline instead, and the end away from the reading
            // is where that baseline puts it.
            var baseOffset = fragment.Flow.IsVertical && fragment.Flow.Orientation == TextOrientation.Upright
                ? cursor + (annotation.Block / 2)
                : cursor + (annotation.Under ? 0 : annotation.Block);

            Attach(open, lineBox, new LayoutBox(fragment.Box, LayoutKind.Text)
            {
                Text = fragment.Text,
                Font = fragment.Style.GetFont(),
                Baseline = flow.BlockOffsetFromLeft(
                    baseBlock,
                    line.Ascent + fragment.BaselineShift - (annotation.Under ? 0 : annotation.Block)),
                Flow = fragment.Flow,
                BorderBox = flow.Map(
                    contentBox,
                    origin + ((fragment.Width - fragment.BaseWidth) / 2),
                    fragment.BaseWidth,
                    baseOffset,
                    baseBlock),
            });

            Attach(open, lineBox, AnnotationBox(
                annotation,
                contentBox,
                flow,
                origin,
                fragment.Width,
                cursor + (annotation.Under ? line.Height - annotation.Block : 0)));

            return;
        }

        var run = new LayoutBox(fragment.Box, LayoutKind.Text)
        {
            Text = fragment.Hanging > 0
                ? fragment.Source.Substring(fragment.Start, fragment.Length - fragment.Hanging)
                : fragment.Text,
            Font = fragment.Style.GetFont(),
            Baseline = flow.BlockOffsetFromLeft(line.Height, line.Ascent + fragment.BaselineShift),
            Flow = fragment.Flow,
            Justification = fragment.Justify,
            Justifies = fragment.Expansions,
            BorderBox = flow.Map(contentBox, origin, room, cursor, line.Height),
        };

        run.Marks = Marks(
            fragment, run.Text!, flow, contentBox, origin, room, cursor, line, measurer);

        Attach(open, lineBox, run);
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
        var start = cursor + (emphasis.StandsAtTheEnd(flow) ? line.Height - block : 0);
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
        var backwards = flow.IsRightToLeft;
        var marks = new List<EmphasisMark>(elements.Count);
        var walk = 0.0;

        for (var index = 0; index < elements.Count; index++)
        {
            var at = elements[index];
            var centre = walk + (advances[at] / 2);

            walk += advances[at];

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
        Double cursor)
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
                origin + ((room - annotation.Width) / 2),
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
                // gathered into one item instead of being run together as ordinary text.
                case BoxKind.Ruby:
                    AddRuby(child, items, chain);
                    break;

                // An ordinary inline element keeps its own box. It is not flattened away: the runs inside
                // it go on carrying it, so the line can give it back the box that draws it.
                case BoxKind.Inline:
                    Collect(
                        child,
                        items,
                        flow,
                        new InlineAncestor(child, chain, basis, flow, _measurer),
                        basis);
                    break;

                // A base or a reading is a run like any other; only the pair around them is set apart.
                case BoxKind.RubyBase:
                case BoxKind.RubyText:
                    Collect(child, items, flow, chain, basis);
                    break;
            }
        }
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
        Double indent = 0,
        Boolean emergency = true)
    {
        var lines = new List<Line>();

        // The elements a line resumes inside, which is what the next builder needs to know so that it
        // draws no start edge for an element whose start edge is already on the line above.
        var carried = (HashSet<InlineAncestor>?)null;

        // How much room each line has, which is the block's extent for every block that has nothing
        // floated beside it and the float's far edge for the lines that have. A column is left out, as
        // it has been for floats themselves: the band a line would be shortened by runs sideways there.
        var room = new LineRoom(
            flow.IsVertical ? null : context.Containing.Floats,
            maxInline,
            flow.InlineSign < 0);

        // Only the first line of the content is set in; the line after a forced break begins where any
        // other line does, which is what leaving the indent behind on the first builder says. An indent
        // is measured from the content edge and a float shortens the line from that same edge, so a
        // float standing where the indent reaches leaves the line to start where the float ends, not
        // that far past it. A negative indent hangs out of the box and is never pulled back in.
        var leading = indent > 0 ? Math.Max(0, indent - room.Inset) : indent;
        var builder = new LineBuilder(strut, leading, flow.IsVertical);

        // The place the line could be cut back to at a soft hyphen, and the steps it has taken since:
        // they begin the next line when the run after the hyphen turns out not to fit.
        LineBuilder.Checkpoint? breakPoint = null;
        List<TailStep>? following = null;

        void Close(LineBuilder filled, Boolean forced = false, Boolean hyphen = false)
        {
            var line = filled.Build(_measurer, room.Room, carried, forced, hyphen);

            line.Left = room.Inset;
            line.Extent = room.Room;
            line.GapBefore = room.Gap;
            line.ForceEnded = forced;

            lines.Add(line);
            carried = line.ContinuesToNext;
            room.Next(line.Height);
        }

        void NextLine(Boolean forced = false, Boolean hyphenate = false)
        {
            Close(builder, forced, hyphenate);
            builder = new LineBuilder(strut, carried, flow.IsVertical);
            breakPoint = null;
            following = null;
        }

        foreach (var item in items)
        {
            if (item.IsBreak)
            {
                NextLine(forced: true);
                continue;
            }

            // Only runs of text are kept for the line after a soft hyphen: a box the page puts there
            // is placed where it stands and cannot be handed back, so the hyphen offers no cutting
            // place past it.
            if (String.IsNullOrEmpty(item.Text) || item.IsRuby || item.IsReplaced || item.IsAtomic || item.IsOutOfFlow)
            {
                breakPoint = null;
                following = null;
            }

            if (item.IsOutOfFlow)
            {
                // No room is taken and the cursor does not move, so where the cursor stands when the box
                // is met is the place it would have had. The fragment carries that much and nothing else.
                builder.AddOutOfFlow(item.Box, item.Style, item.Inline);
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
                        NextLine();
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

                if (room.Room > 0 && builder.ExtentFor(item.Inline) + atomic.InlineSize > room.Room &&
                    item.Style.WrapsLines)
                {
                    if (builder.HasContent)
                    {
                        room.Restore(band);
                        NextLine();
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
                var baseWidth = Measure(item.Style, item.Text!, rubyFlow);
                var extent = Math.Max(baseWidth, annotation.Width);

                var band = room.Height;
                room.Grow(LineBoxHeight(item.Style, item.Style.GetFont()) + annotation.Block);

                if (room.Room > 0 && builder.ExtentFor(item.Inline) + extent > room.Room &&
                    item.Style.WrapsLines)
                {
                    if (builder.HasContent)
                    {
                        room.Restore(band);
                        NextLine();
                        room.Grow(LineBoxHeight(item.Style, item.Style.GetFont()) + annotation.Block);
                    }
                    else if (room.Shortened)
                    {
                        // CSS 2.1 §9.5: a line box a float has shortened, too small for the content
                        // standing on it, is shifted downward until some content fits or no floats
                        // are left.
                        room.Drop(builder.ExtentFor(item.Inline) + extent);
                    }
                }

                builder.AddRuby(item.Text!, extent, item.Style, item.Box, baseFlow, annotation, baseWidth, item.Inline);
                continue;
            }

            // An inline element may ask for an orientation of its own, so each run is measured and
            // drawn in the flow of the box it came from rather than in the container's. The font is
            // taken once for the item, because every piece of it is set in that one font.
            var itemFlow = item.Box.Flow;
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
                        builder.ExtentFor(item.Inline) + width > room.Room && item.Style.WrapsLines;

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
                        var taken = steps ? TextClusters.UnitsAt(run.Source, at, end) : run.Length;

                        // The room a character of the run asks for is asked of the line it stands on: a tab
                        // runs the line on to its next stop, and the stop is a question about where the line
                        // has come to rather than about the font.
                        var advance = !steps
                            ? width
                            : spaces
                                ? WhiteSpaceWidth(item.Style, strut, run.Source, at, taken, builder.Width, itemFlow)
                                : Measure(item.Style, run.Source, at, taken, itemFlow);

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
                        var asks = at == run.Start && !spaces && !letters &&
                            !TextChunker.IsBreakSpace(run.Source[run.Start])
                            ? width + trailed + mark
                            : advance;

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
                            following = new List<TailStep>();
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
                            advance > 0 && !String.IsNullOrEmpty(item.Text) && item.Style.WrapsLines &&
                            (!spaces || (item.Style.WrapsInsideSpaces && followed)) &&
                            builder.ExtentFor(item.Inline) + advance > room.Room)
                        {
                            var replay = following ?? new List<TailStep>();

                            builder.Restore(point);
                            NextLine(hyphenate: builder.EndsInSoftHyphen);
                            breakPoint = null;
                            following = null;

                            foreach (var step in replay)
                            {
                                builder.Add(
                                    step.Source, step.Start, step.Length, step.Advance,
                                    step.Style, step.Box, step.Flow, step.Inline);
                            }
                        }

                        if (breaks && (room.Room > 0 || hyphen) &&
                            builder.ExtentFor(item.Inline) + asks > room.Room && item.Style.WrapsLines)
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

                            if (builder.ExtentFor(item.Inline) + asks > room.Room)
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
                                    room.Drop(builder.ExtentFor(item.Inline) + asks);
                                }
                            }
                        }

                        builder.Add(run.Source, at, taken, advance, item.Style, item.Box, piece.Flow, item.Inline);

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

        MarkSlices(lines, maxInline);
        return lines;
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
            var resumed = carried?.Contains(ancestor) == true;
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
        public void Hold(Box box, InlineAncestor anchor, LayoutBox owner, Int32 index, LayoutContext against, Offset stood) =>
            _held.Add(new Held(box, anchor, owner, index, against, stood, null));

        /// <summary>Holds an atomic inline box, with the frame the line gave it.</summary>
        /// <inheritdoc cref="Hold(Box, InlineAncestor, LayoutBox, Int32, LayoutContext, Offset)" path="/param[@name='against']"/>
        /// <inheritdoc cref="Hold(Box, InlineAncestor, LayoutBox, Int32, LayoutContext, Offset)" path="/param[@name='index']"/>
        public void Hold(Box box, InlineAncestor anchor, LayoutBox owner, Int32 index, LayoutContext against, Rect frame) =>
            _held.Add(new Held(box, anchor, owner, index, against, default, frame));

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
                    : _engine.ArrangePositioned(held.Box, held.Stood, against);

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
            Rect? Frame);
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
        // The box's baseline can only be carried to the line it stands on while the two are set the same
        // way: a box in a column stands on a line of its own frame, and the line here would be measuring
        // a baseline from an edge the box does not have one against. A box that holds blocks has no line
        // of its own to stand on either, and falls back to its end edge.
        if (flow.IsVertical != box.Flow.IsVertical || HasBlockChildren(box) || box.Children.Count == 0)
        {
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
        + (style.WordSpacing * TextChunker.WordSeparators(source, start, length));

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
    private Double LineBoxHeight(ComputedStyle style, FontStyle font)
    {
        if (style.LineHeight is { } stated)
        {
            return stated;
        }

        var metrics = _measurer.GetMetrics(font);

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

        return new MeasuredRun(
            run.Source,
            run.Start,
            run.Length,
            flow.IsVertical
                ? run.Advance(font, _measurer, style.FontSize, flow) + spacing
                : _measurer.MeasureWidth(run.Source.Substring(run.Start, run.Length), font) + spacing,
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

            if (line.Fragments.Count == 0 ||
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
            // time round: what was folded in last time comes back out before anything is folded in now.
            foreach (var fragment in line.Fragments)
            {
                fragment.Expand(0, 0);
            }

            if (line.Fragments.Count == 0 ||
                !line.Alignment.Equals("justify", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var places = 0;

            for (var index = 0; index < line.Fragments.Count; index++)
            {
                places += Places(line, index);
            }

            var free = (line.Extent - line.Indent) - line.Used;

            if (places == 0 || free <= 0)
            {
                continue;
            }

            var share = free / places;
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

                // The room is owed out on a running total, so the fractions of a share that does not
                // divide evenly fall on the places one after another and the last word still lands
                // exactly on the edge the line was given.
                owed += share * count;

                fragment.Expand((owed - given) / count, count);
                given = owed;
            }
        }
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

    /// <summary>Gets whether the fragment is the one the line ends with, whose trailing place is the line trailing.</summary>
    private static Boolean EndsLine(Line line, Int32 index) => index == line.Fragments.Count - 1;

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
    private static Double Indentation(Box container, WritingModeInfo flow, Double contentInline)
    {
        var indent = container.Style.TextIndent;

        return indent.HasValue ? indent.Resolve(contentInline) : 0;
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
    private static Double AlignmentOffset(String textAlign, Double maxInline, Double used, WritingModeInfo flow)
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
            for (var index = Start; index < Start + Length;)
            {
                total += Math.Max(
                    measurer.MeasureAdvance(TextChunker.CodePointAt(Source, index), font, flow),
                    fontSize);

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
            Boolean outOfFlow = false)
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

        /// <summary>Gets the annotation of a ruby item, or null when the item carries none.</summary>
        public String? AnnotationText { get; }

        /// <summary>Gets the style the annotation is set in, which the <c>rt</c> element shrank for it.</summary>
        public ComputedStyle AnnotationStyle { get; }

        /// <summary>Gets the box the annotation came from, so its own geometry can be traced back to the tree.</summary>
        public Box? AnnotationBox { get; }

        /// <summary>Gets whether the item is a ruby pair: base text with an annotation over it.</summary>
        public Boolean IsRuby => AnnotationText is not null;

        public static InlineItem CreateText(String text, ComputedStyle style, Box box, InlineAncestor? inline) =>
            new(text, style, box, inline, false, false);

        public static InlineItem CreateBreak(ComputedStyle style, Box box, InlineAncestor? inline) =>
            new(null, style, box, inline, true, false);

        public static InlineItem CreateReplaced(Box box, InlineAncestor? inline) =>
            new(null, box.Style, box, inline, false, true);

        public static InlineItem CreateAtomic(Box box, InlineAncestor? inline) =>
            new(null, box.Style, box, inline, false, false, atomic: true);

        public static InlineItem CreateOutOfFlow(Box box, InlineAncestor? inline) =>
            new(null, box.Style, box, inline, false, false, outOfFlow: true);

        public static InlineItem CreateRuby(
            String text,
            ComputedStyle style,
            Box box,
            String annotationText,
            ComputedStyle annotationStyle,
            Box annotationBox,
            InlineAncestor? inline) =>
            new(text, style, box, inline, false, false, annotationText, annotationStyle, annotationBox);
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
            Boolean outOfFlow = false)
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
        }

        /// <summary>Gets whether the fragment is a box that left the flow, placed but not drawn here.</summary>
        public Boolean OutOfFlow { get; }

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
        public LinePlanKey(Box container, Double maxInline, Double indent, Int32 flow, FloatBand? band)
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

        public Double Indent { get; }

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

        public LineRoom(FloatBand? band, Double max, Boolean reversed)
        {
            _band = band;
            _max = max;
            _reversed = reversed;
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

        /// <summary>Moves on to the next line, at the height the one just built took.</summary>
        public void Next(Double height)
        {
            Top += height;
            _height = 0;
            Open();
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

        public InlineAncestor(Box box, InlineAncestor? parent, Double basis, WritingModeInfo flow, ITextMeasurer measurer)
        {
            Box = box;
            Parent = parent;

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

        private Thickness Sliced(Thickness thickness, Boolean cutStart, Boolean cutEnd) =>
            cutStart ? Clear(thickness, _inlineStartSide)
            : cutEnd ? Clear(thickness, _inlineEndSide)
            : thickness;

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
        InlineAncestor? Inline);

    private sealed class LineBuilder
    {
        private readonly List<TextFragment> _fragments = new();
        private readonly List<OpenInline> _open = new();
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
        public LineBuilder(ComputedStyle strut, Double indent, Boolean vertical)
        {
            _fallback = strut;
            _indent = indent;
            _vertical = vertical;
            Width = indent;
        }

        public LineBuilder(ComputedStyle fallback, HashSet<InlineAncestor>? carried, Boolean vertical)
        {
            _fallback = fallback;
            _carried = carried;
            _vertical = vertical;
        }

        /// <summary>
        /// Gets the extent the line takes as it is being built, which is what the wrap test is made
        /// against while the line is still growing. It is only an estimate of what the line ends up
        /// taking - trimming the trailing spaces can still take a run back out - so
        /// <see cref="Build"/> works the extent it hands back out afresh instead of reusing this.
        /// </summary>
        public Double Width { get; private set; }

        /// <summary>
        /// Gets whether the line carries anything drawn on it. A box that left the flow is not: it stands
        /// on the line only to say where it would have been, so a line holding nothing else is an empty
        /// line and takes no height.
        /// </summary>
        public Boolean HasContent => _fragments.Exists(fragment => !fragment.OutOfFlow);

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
                    if (_fragments[index].OutOfFlow)
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
        public Checkpoint Capture() => new(
            _fragments.Count,
            _fragments.Count > 0 ? _fragments[^1].Length : 0,
            _fragments.Count > 0 ? _fragments[^1].Width : 0,
            Width,
            _open.ToArray());

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
            }

            Width = at.Width;
            _open.Clear();
            _open.AddRange(at.Open);
        }

        /// <summary>The state a line may be cut back to, kept while a soft hyphen offers the place.</summary>
        public readonly record struct Checkpoint(
            Int32 Fragments,
            Int32 Length,
            Double RunWidth,
            Double Width,
            OpenInline[] Open);

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

                    if (fragment.OutOfFlow)
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
                    if (drawn is null && ReferenceEquals(last.Source, source) && last.Start + last.Length == start)
                    {
                        last.Length += length;
                    }
                    else
                    {
                        last.MergeInto(last.Text + (drawn ?? source.Substring(start, length)));
                    }

                    last.Width += width;
                    last.FromTab = drawn is not null;
                    Width += width;
                    return;
                }
            }

            var added = new TextFragment(source, start, length, width, style, box, flow, inline);

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
            // Where the page licensed a break anywhere - `line-break: anywhere`, `overflow-wrap` in
            // either value, `break-spaces` - the rules between the two characters are set aside, and
            // the edge of an inline element is no place to bring them back.
            if (style.BreaksAnywhere || style.BreaksBetweenLetters || style.WrapsLongWords || style.WrapsInsideSpaces)
            {
                return true;
            }

            if (_fragments.Count == 0)
            {
                return true;
            }

            var last = _fragments[^1];

            if (last.IsReplaced || last.Atomic is not null || last.Ruby is not null || last.Length <= 0)
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
                baseWidth: baseWidth));

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

            // How far each fragment reaches above and below the baseline it stands on, kept because a
            // box's alignment is worked out from it once the line knows how far it reached.
            var above = new Double[line.Fragments.Count];
            var below = new Double[line.Fragments.Count];
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

                inFlow = true;

                // A replaced inline rests its bottom margin edge on the baseline, so it contributes
                // its whole height as ascent and nothing as descent.
                if (fragment.IsReplaced)
                {
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

                if (lineHeight.HasValue)
                {
                    // Half the leading, negative included: a face taller than the line height the page
                    // asked for overflows a line box that is that height and no taller.
                    var halfLeading = (lineHeight.Value - (fragmentAscent + fragmentDescent)) / 2;
                    fragmentAscent += halfLeading;
                    fragmentDescent += halfLeading;
                }

                // An annotation stands over its base, on the block-start side, so it lifts the baseline
                // the whole line shares by its own extent instead of making the base taller. One asked
                // for `under` takes its room from the other side of the baseline and leaves it where it
                // would have been.
                if (fragment.Ruby is { } annotation)
                {
                    if (annotation.Under)
                    {
                        fragmentDescent += annotation.Block;
                    }
                    else
                    {
                        fragmentAscent += annotation.Block;
                    }
                }
                else if (fragment.Style.Emphasis is { IsSet: true } marks)
                {
                    // Marks are drawn in the manner of a reading, and they take the room one would: the
                    // line grows by the strip they stand in, which is their own size, on the side the page
                    // named (CSS Text Decoration 4 §5.2). A base carrying its own reading is marked
                    // through that reading instead, so it is asked for nothing here.
                    var strip = Math.Max(1, fragment.Style.GetFont().Size / 2);

                    if (marks.StandsAtTheEnd(fragment.Flow))
                    {
                        fragmentDescent += strip;
                    }
                    else
                    {
                        fragmentAscent += strip;
                    }
                }

                above[position] = fragmentAscent;
                below[position] = fragmentDescent;
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
            var textual = _vertical
                ? _fragments.Count == 0
                : _fragments.TrueForAll(fragment =>
                    !fragment.IsReplaced && fragment.Atomic is null && fragment.Ruby is null);

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
            var reach = ascent;
            var depth = descent;
            Dictionary<InlineAncestor, Double>? bands = null;

            if (inFlow)
            {
                for (var position = 0; position < line.Fragments.Count; position++)
                {
                    var fragment = line.Fragments[position];

                    if (fragment.OutOfFlow ||
                        (IsBaseline(fragment.Style) && fragment.Inline?.AnyAlignment != true))
                    {
                        // Nothing asks for this box to stand anywhere but on the baseline the line
                        // shares, and the extents gathered above counted it there already.
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

                    if (running == 0)
                    {
                        continue;
                    }

                    fragment.BaselineShift = running;
                    reach = Math.Max(reach, above[position] - running);
                    depth = Math.Max(depth, below[position] + running);
                }
            }

            // A line the page's own soft hyphen ended draws the mark where the character stood: the
            // hyphenate character takes its place and its room, and the run keeps reading as the word
            // the page spelled (CSS Text 3 §6.2). A line that ends anywhere else keeps the hyphen as the
            // invisible character it is, taking nothing and drawing nothing.
            if (hyphen)
            {
                var last = _fragments.Count > 0 ? _fragments[^1] : null;

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

                    var at = line.Fragments.Count - 1;
                    above[at] = Math.Max(above[at], markAscent);
                    below[at] = Math.Max(below[at], markDescent);
                    ascent = Math.Max(ascent, above[at]);
                    descent = Math.Max(descent, below[at]);
                    reach = Math.Max(reach, ascent);
                    depth = Math.Max(depth, descent);
                }
            }

            line.BandShifts = bands;
            line.Ascent = reach;
            line.Height = reach + depth;

            var hanging = HangingTrail(measurer);
            line.Hanging = hanging;
            line.Used = used - hanging;
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
                : first.Atomic is not null || first.IsReplaced
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

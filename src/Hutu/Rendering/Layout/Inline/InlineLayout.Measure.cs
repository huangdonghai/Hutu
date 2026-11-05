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
        var items = RentItems();
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
        var begins = RentDoubles(items.Count);

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

            var textPlan = TextPlanFor(
                source,
                item.Style,
                flow,
                item.Style.WordBreakingRules,
                continuesWord: false,
                needWidths: true);

            for (var chunkIndex = 0; chunkIndex < textPlan.Chunks.Length; chunkIndex++)
            {
                var chunk = textPlan.Chunks[chunkIndex];

                // A tab asks the same question here as on a line: how far the content has run decides
                // where the next stop is.
                var width = TextChunker.IsCollapsible(source, chunk.Start, chunk.Length)
                    ? WhiteSpaceWidth(item.Style, container.Style, source, chunk.Start, chunk.Length, maxContent, flow)
                    : textPlan.Widths[chunkIndex];
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

        ReturnItems(items);
        ReturnDoubles(begins);
        return new IntrinsicSize(minContent, Math.Max(widest, minContent));
    }

}

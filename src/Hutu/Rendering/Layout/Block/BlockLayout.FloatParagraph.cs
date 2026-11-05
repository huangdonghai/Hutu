using AngleSharp.Css.Dom;
using Hutu.Rendering.Boxes;
using System;
using System.Collections.Generic;

namespace Hutu.Rendering.Layout;

internal sealed partial class BlockLayout
{
    /// <summary>A block whose lines live in the one in-flow block it holds, with floats beside them.</summary>
    private readonly record struct FloatHost(Box Paragraph, Box Host);

    /// <summary>
    /// Finds the shape this path handles: a multi-column box whose only in-flow child is an undecorated
    /// block that holds a single in-flow block and at least one float. The lines belong to that inner
    /// block, and the float shortens the lines of the column that begins the paragraph only.
    /// </summary>
    private static FloatHost? FloatHostOf(Box box)
    {
        Box? paragraph = null;

        foreach (var child in box.Children)
        {
            if (!child.IsBlockLevel || PositionedLayout.IsOutOfFlow(child))
            {
                continue;
            }

            if (child.Style.Float != FloatSide.None || paragraph is not null)
            {
                return null;
            }

            paragraph = child;
        }

        if (paragraph is not { } block || !BreaksAtLines(block))
        {
            return null;
        }

        Box? host = null;
        var floats = 0;

        foreach (var child in block.Children)
        {
            if (!child.IsBlockLevel || PositionedLayout.IsOutOfFlow(child))
            {
                continue;
            }

            if (IsFloat(child))
            {
                floats++;
                continue;
            }

            if (host is not null || HasBlockChildren(child))
            {
                return null;
            }

            host = child;
        }

        return host is not null && floats > 0 ? new FloatHost(block, host) : null;
    }

    /// <summary>
    /// Gets the band the paragraph's own floats leave for the lines of its inner block: the floats are
    /// placed at the paragraph's content top, which is where the column begins them. <paramref name="reach"/>
    /// is how far below that top the deepest of them reaches, and <paramref name="linesBelowBand"/> says whether
    /// the row the band begins in is left no width at all, which is when every line of the paragraph has to
    /// stand below it rather than beside it (CSS 2.1 §9.5.1-§9.5.2).
    /// </summary>
    private FloatBand? FloatHostBand(
        FloatHost host,
        Double share,
        LayoutContext context,
        Double contentX,
        Double contentY,
        out Double reach,
        out Boolean linesBelowBand)
    {
        var tracks = TracksFor(host.Paragraph, share, contentX);
        reach = 0.0;
        linesBelowBand = false;

        if (tracks is null)
        {
            return null;
        }

        foreach (var child in host.Paragraph.Children)
        {
            if (child.IsBlockLevel && IsFloat(child) && !PositionedLayout.IsOutOfFlow(child))
            {
                PlaceFloat(child, tracks, contentY, share, context);
            }
        }

        reach = Math.Max(0.0, tracks.Bottom - contentY);

        var (freeStart, freeEnd) = tracks.FreeSpanAt(contentY, 0.001);
        linesBelowBand = freeEnd - freeStart <= 0.001;

        var margin = host.Host.Style.Margin.Resolve(share);

        return BandFor(
            host.Host,
            tracks,
            context.Floats,
            contentY,
            contentX + margin.Left,
            margin.Right,
            contentX,
            contentY,
            share);
    }

    /// <summary>One column's share of a broken float paragraph: the plan it is placed from and how
    /// many of that plan's lines it takes. A null <see cref="Start"/> is the paragraph's own first
    /// plan, the one the float's band shortens.</summary>
    private sealed record FloatColumn(InlineStart? Start, IReadOnlyList<Double> Heights, Int32 Count);

    /// <summary>The columns a float paragraph breaks into, and the height they share.</summary>
    private sealed record FloatParagraphPlan(IReadOnlyList<FloatColumn> Columns, Double Extent);

    /// <summary>
    /// Plans a float paragraph across the columns: the first plans at the column's width with the
    /// float's band, and each column after it resumes the text where the plan before it broke and
    /// plans it at the full column width (CSS Multicol 3 §7). A balance picks the least extent all the
    /// text fits in; a fill cuts each column at the height the page stated.
    /// </summary>
    private FloatParagraphPlan? PlanFloatParagraph(
        FloatHost host, Double share, Double? declaredHeight, Int32 columns, LayoutContext context)
    {
        if (columns < 2)
        {
            return null;
        }

        // The paragraph's own edges stand around its lines: the plan is made at what the border and
        // padding leave, and the fragment that owns an edge carries its room (CSS Break 3 §4.7).
        var border = BorderOf(host.Paragraph);
        var padding = host.Paragraph.Style.Padding.Resolve(share);
        var content = Math.Max(0, share - border.Horizontal - padding.Horizontal);
        var margin = host.Paragraph.Style.Margin.Resolve(share);
        var top = margin.Top + border.Top + padding.Top;
        var bottom = border.Bottom + padding.Bottom;

        var childContext = context.ForContainingBlock(new Size(content, declaredHeight ?? 0));
        var band = FloatHostBand(host, content, childContext, 0, 0, out var reach, out var linesBelowBand);
        var bandedContext = childContext.WithFloats(band);
        var banded = new InlineContext(_engine, bandedContext, bandedContext, bandedContext);
        var first = _inline.LineHeights(host.Host, content, host.Host.Flow, banded);

        if (first.Count == 0)
        {
            return null;
        }

        var plainContext = childContext.WithFloats(null);
        var plain = new InlineContext(_engine, plainContext, plainContext, plainContext);
        var orphans = Math.Max(1, host.Paragraph.Style.Breaks.Orphans);
        var widows = Math.Max(1, host.Paragraph.Style.Breaks.Widows);
        var clones = host.Paragraph.Style.ClonesDecoration;
        var whole = new FloatParagraphPlan(
            new[] { new FloatColumn(null, first, first.Count) },
            top + Height(first, first.Count) + bottom);

        if (declaredHeight is { } stated)
        {
            // A fill lets its last column overflow rather than move content past the columns it has.
            var filled = Fill(
                host, content, first, banded, plain, stated, columns, top, bottom, clones, orphans, widows,
                reach, linesBelowBand, mayOverflow: true);

            return filled is null ? whole : filled with { Extent = stated };
        }

        // The least extent the whole text fits in, found by halving the way a line balance does.
        var low = 0.0;
        var high = top + Height(first, first.Count) + bottom;
        FloatParagraphPlan? best = null;

        for (var step = 0; step < 40; step++)
        {
            var middle = (low + high) / 2;
            var plan = Fill(
                host, content, first, banded, plain, middle, columns, top, bottom, clones, orphans, widows,
                reach, linesBelowBand, mayOverflow: false);

            if (plan is null)
            {
                low = middle;
            }
            else
            {
                best = plan;
                high = middle;
            }
        }

        return best ?? whole;
    }

    /// <summary>
    /// Cuts a float paragraph's text into columns of at most <paramref name="extent"/>: each column
    /// plans what is left of the text at its own width, takes the lines that fit with the orphans and
    /// widows the paragraph states, and the next plan resumes from where this one broke. The paragraph's
    /// float begins the first column that has room for the band it needs, and a column that leaves none
    /// for a line hands it on rather than overflowing. Null says the text does not fit in the columns it
    /// was given.
    /// </summary>
    private FloatParagraphPlan? Fill(
        FloatHost host,
        Double width,
        IReadOnlyList<Double> first,
        InlineContext banded,
        InlineContext plain,
        Double extent,
        Int32 columns,
        Double top,
        Double bottom,
        Boolean clones,
        Int32 orphans,
        Int32 widows,
        Double reach,
        Boolean linesBelowBand,
        Boolean mayOverflow)
    {
        var result = new List<FloatColumn>();
        var heights = first;
        var inline = banded;
        InlineStart? start = null;
        var highest = 0.0;

        // A float the room left below the paragraph's own top edge cannot hold begins the column after
        // it, where a whole column is free for it, and the columns before it hold only the edge the
        // paragraph brought (CSS Multicol 3 §7, CSS Fragmentation 3 §4.3, measured:
        // line-after-unbreakable-float-after-padding, whose 75px float with break-inside:avoid stands at
        // the top of the second 101px column while the 100px padding-top fills the first).
        var bandColumn = mayOverflow && reach > 0.001 && reach <= extent + 0.001 && top + reach > extent + 0.001
            ? 1
            : 0;

        for (var column = 0; column < columns; column++)
        {
            // A cloned box owns both edges on every fragment; a sliced one owns the top on its first
            // and the bottom on its last (CSS Break 3 §4.7).
            var starts = clones || column == 0;
            var endsEdge = clones || column == columns - 1;
            var room = extent - (starts ? top : 0) - (endsEdge ? bottom : 0);

            if (column < bandColumn)
            {
                result.Add(new FloatColumn(new InlineStart(host.Host, 0), heights, 0));
                highest = Math.Max(highest, (starts ? top : 0) + (endsEdge ? bottom : 0));
                continue;
            }

            // The band belongs to the one column that holds its float, and a line with no room beside the
            // band stands below it: where the column has no room for that either, the float keeps the
            // column to itself and the text begins the next one at the full width
            // (CSS 2.1 §9.5.2, CSS Multicol 3 §7).
            if (column == bandColumn && linesBelowBand && Height(heights, 1) > room + 0.001)
            {
                result.Add(new FloatColumn(start, heights, 0));
                highest = Math.Max(highest, reach + (starts ? top : 0) + (endsEdge ? bottom : 0));
                heights = _inline.LineHeights(host.Host, width, host.Host.Flow, plain);
                inline = plain;
                start = new InlineStart(host.Host, 0);
                continue;
            }

            var count = FragmentSlicer.Fitting(heights, room, orphans, widows);

            if (count == 0 && column < columns - 1)
            {
                // Nothing of the text fits the room this column has left: it stands in a column after
                // this one, which begins at its own top. The band belongs to the column that holds the
                // float alone, so the text from there on is planned at the column's full width.
                result.Add(new FloatColumn(start, heights, 0));
                highest = Math.Max(
                    highest,
                    (column == bandColumn ? reach : 0.0) + (starts ? top : 0) + (endsEdge ? bottom : 0));

                if (column == bandColumn)
                {
                    heights = _inline.LineHeights(host.Host, width, host.Host.Flow, plain);
                    inline = plain;
                    start = new InlineStart(host.Host, 0);
                }

                continue;
            }

            if (count == 0)
            {
                if (!mayOverflow)
                {
                    return null;
                }

                // The last column has nowhere to hand the text on: it takes it and overflows.
                count = heights.Count;
            }
            else if (mayOverflow && column == columns - 1)
            {
                count = heights.Count;
            }

            // The fragment that ends the paragraph also owns its bottom edge: where the lines alone
            // leave no room for it, the break moves back so that edge still fits.
            if (count >= heights.Count && !endsEdge && bottom > 0.001)
            {
                var ends = FragmentSlicer.Fitting(heights, room - bottom, orphans, widows);

                if (ends <= 0)
                {
                    if (!mayOverflow)
                    {
                        return null;
                    }
                }
                else if (ends < count)
                {
                    count = ends;
                }
            }

            result.Add(new FloatColumn(start, heights, count));
            highest = Math.Max(
                highest,
                Height(heights, count) + (starts ? top : 0) + ((clones || count >= heights.Count) ? bottom : 0));

            if (count >= heights.Count)
            {
                return new FloatParagraphPlan(result, highest);
            }

            var at = _inline.EndOfLine(host.Host, width, host.Host.Flow, inline, count - 1);

            if (at is not { } resume)
            {
                return null;
            }

            start = resume;
            heights = _inline.LineHeights(host.Host, width, host.Host.Flow, plain, resume);
            inline = plain;

            if (heights.Count == 0)
            {
                return new FloatParagraphPlan(result, highest);
            }
        }

        return null;
    }

    /// <summary>
    /// Arranges a float paragraph the way it was measured: each column takes its share of the text
    /// from the plan it resumes, and the paragraph's own first fragment stands below its top margin
    /// with the float beside it (CSS Multicol 3 §7).
    /// </summary>
    private Double ArrangeFloatParagraph(
        Box box,
        FloatHost host,
        FloatParagraphPlan plan,
        LayoutBox parent,
        Rect content,
        Double share,
        Boolean heightStated,
        Double declaredHeight,
        LayoutContext context)
    {
        var multi = box.Style.Multi;
        var border = BorderOf(host.Paragraph);
        var padding = host.Paragraph.Style.Padding.Resolve(share);
        var margin = host.Paragraph.Style.Margin.Resolve(share);
        var last = plan.Columns.Count - 1;
        var wholeHeight = Height(plan.Columns[0].Heights, plan.Columns[0].Heights.Count) + border.Vertical + padding.Vertical;
        var whole = new Rect(content.X, content.Y + margin.Top, share, wholeHeight);

        // The plan's width is what the paragraph's own edges leave; the arrangement reads it back
        // from the fragment's insets, so the two passes break the lines at the same width.
        var childContext = context.ForContainingBlock(
            new Size(Math.Max(0, share - border.Horizontal - padding.Horizontal), heightStated ? declaredHeight : 0));

        for (var index = 0; index < plan.Columns.Count; index++)
        {
            var column = plan.Columns[index];
            var clones = host.Paragraph.Style.ClonesDecoration;
            var starts = clones || index == 0;
            var ends = clones || index == last;
            var height = Height(column.Heights, column.Count)
                + (starts ? border.Top + padding.Top : 0)
                + (ends ? border.Bottom + padding.Bottom : 0);

            // Each fragment draws the edges it owns, and the background is painted across the box the
            // paragraph would have been had it never been broken (CSS Break 3 §4.7).
            var slice = new FragmentSlice(
                new Thickness(starts ? border.Top : 0, border.Right, ends ? border.Bottom : 0, border.Left),
                new Thickness(starts ? padding.Top : 0, padding.Right, ends ? padding.Bottom : 0, padding.Left),
                starts,
                ends,
                whole);

            parent.Children.Add(_engine.Arrange(
                host.Paragraph,
                new Rect(
                    content.X + index * (share + multi.Gap),
                    content.Y + (starts ? margin.Top : 0),
                    share,
                    height),
                childContext,
                false,
                new LineRange(0, column.Count),
                slice,
                column.Start));
        }

        MulticolRules.Add(
            parent,
            box.Style,
            content,
            new List<(Double, Double)> { (content.Y, content.Y + plan.Extent) });

        _engine.PublishFloats(box, null);

        return plan.Extent;
    }

    /// <summary>Gets the height of the first <paramref name="count"/> lines, or of them all.</summary>
    private static Double Height(IReadOnlyList<Double> heights, Int32 count)
    {
        var total = 0.0;

        for (var index = 0; index < count && index < heights.Count; index++)
        {
            total += heights[index];
        }

        return total;
    }

}

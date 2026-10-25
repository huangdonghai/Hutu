using System;
using System.Collections.Generic;

namespace Hutu.Rendering.Layout;

/// <summary>
/// Turns the gaps of a multi-column container into the decorations they ask for (CSS Gaps 1 §3.4 and
/// CSS Multicol 3 §4): a column rule stands in the room between two columns of a row of column boxes,
/// and a row rule stands between two rows.
/// </summary>
/// <remarks>
/// The runs are the stretches of the container the columns actually ran through, in the container's own
/// block axis. A spanner cuts the container into more than one run, and the rules stop where a run does
/// rather than crossing the spanner - which is why the layout that knows the runs makes the bands, and
/// the painter only draws them.
/// </remarks>
internal static class MulticolRules
{
    /// <summary>Appends the decorations of the container's gaps to its layout.</summary>
    /// <param name="runs">The block-axis stretches the columns ran through, in the container's coordinates.</param>
    public static void Add(LayoutBox layout, ComputedStyle style, Rect content, List<(Double From, Double To)> runs)
    {
        var multi = style.Multi;

        if (!multi.IsMultiColumn)
        {
            return;
        }

        var along = layout.Flow.IsVertical;
        var room = along ? content.Height : content.Width;
        var crossRoom = along ? content.Width : content.Height;
        var (count, width) = multi.Used(room);

        if (count < 1 || runs.Count == 0)
        {
            return;
        }

        var step = width + multi.Gap;
        var columnHeight = multi.Wraps && multi.Height is { } stated ? stated : crossRoom;
        var rowStep = columnHeight + multi.RowGap;
        var start = along ? content.Y : content.X;
        var segments = new List<GapRuleSegment>();

        // A column rule runs along the block axis and a row rule along the inline, so each takes its ends
        // from whichever of the two the container's writing mode turns back (CSS Gaps 1 §3.3).
        var columnRules = style.Gap.Column.MeasuredFromTheEnd(layout.Flow.BlockSign < 0);
        var rowRules = style.Gap.Row.MeasuredFromTheEnd(layout.Flow.InlineSign < 0);

        foreach (var run in runs)
        {
            var rows = multi.Wraps && multi.Height is { } tall && tall > 0 && rowStep > 0
                ? Math.Max(1, (Int32)Math.Floor((run.To - run.From + multi.RowGap) / rowStep))
                : 1;

            var filled = new Boolean[Math.Max(1, count * rows)];

            foreach (var child in layout.Children)
            {
                if (child.BorderBox.Height <= 0 || child.BorderBox.Width <= 0)
                {
                    continue;
                }

                var edge = (along ? child.BorderBox.Y : child.BorderBox.X) - start;
                var down = (along ? child.BorderBox.X : child.BorderBox.Y) - run.From;
                var column = step > 0 ? (Int32)Math.Round(edge / step) : 0;
                var row = rowStep > 0 ? (Int32)Math.Round(down / rowStep) : 0;

                if (column >= 0 && column < count && row >= 0 && row < rows &&
                    Math.Abs(edge - column * step) <= 0.5 && Math.Abs(down - row * rowStep) <= 0.5)
                {
                    filled[row * count + column] = true;
                }
            }

            AddColumnGaps(
                segments,
                columnRules,
                rowRules,
                along,
                count,
                width,
                step,
                columnHeight,
                rows,
                rowStep,
                start,
                run,
                filled);
            AddRowGaps(
                segments,
                rowRules,
                columnRules,
                along,
                count,
                width,
                step,
                start,
                rows,
                rowStep,
                columnHeight,
                run,
                filled);
        }

        if (segments.Count > 0)
        {
            layout.GapDecorations = segments;
        }
    }

    /// <summary>
    /// Appends the decorations of the gaps between two neighbouring columns.
    /// </summary>
    /// <remarks>
    /// A column gap stands in one row's height, so the gap's stretches are the rows the columns wrapped
    /// into: the decoration of one is drawn along that row's column height, and the row gap either cuts it
    /// there or is run through, which is what the axis's break says (§3.1.2). Stretches of neighbouring
    /// rows of one gap merge into one decoration when the break lets them, so a gap whose every row is
    /// filled is drawn from the head of the run to its foot.
    /// </remarks>
    private static void AddColumnGaps(
        List<GapRuleSegment> target,
        GapRuleAxis axis,
        GapRuleAxis across,
        Boolean along,
        Int32 count,
        Double width,
        Double step,
        Double columnHeight,
        Int32 rows,
        Double rowStep,
        Double start,
        (Double From, Double To) run,
        Boolean[] filled)
    {
        if (count < 2 || !axis.IsStated)
        {
            return;
        }

        // A multi-column container draws its column rules between every pair of used columns that both
        // hold something, which is what its `normal` means (CSS Gaps 1 §3.4).
        var visibility = axis.Visibility == GapRuleVisibility.Normal ? GapRuleVisibility.Between : axis.Visibility;
        var gaps = count - 1;

        // Stretches of the same gap run into one decoration only where the break says nothing of crossings.
        var merges = axis.Break == GapRuleBreak.None;

        for (var index = 0; index < gaps; index++)
        {
            var centre = start + index * step + width + (step - width) / 2;
            var row = 0;

            while (row < rows)
            {
                if (!Filled(visibility, filled, count, row, index))
                {
                    row++;
                    continue;
                }

                var low = run.From + row * rowStep;
                var high = low + columnHeight;
                var from = row;

                while (merges && row + 1 < rows && Filled(visibility, filled, count, row + 1, index))
                {
                    row++;
                    high = run.From + row * rowStep + columnHeight;
                }

                var rule = axis.At(from * gaps + index, rows * gaps);

                if (rule.Appears)
                {
                    // An end at the bound of the whole gap takes its cap inset; an end at a row crossing
                    // takes the inset written for crossings, read against the row gap standing there.
                    low += from == 0
                        ? axis.CapStart.Resolve(0, 0)
                        : axis.JunctionStart.Resolve(
                            rowStep - columnHeight,
                            across.At(from - 1, rows - 1).Width);
                    high -= row == rows - 1
                        ? axis.CapEnd.Resolve(0, 0)
                        : axis.JunctionEnd.Resolve(
                            rowStep - columnHeight,
                            across.At(row, rows - 1).Width);

                    if (high > low)
                    {
                        target.Add(new GapRuleSegment(
                            along
                                ? new Rect(low, centre - rule.Width / 2, high - low, rule.Width)
                                : new Rect(centre - rule.Width / 2, low, rule.Width, high - low),
                            rule,
                            isRow: false));
                    }
                }

                row++;
            }
        }
    }

    /// <summary>
    /// Gets whether the stretch of a column gap in one row is drawn: of the two columns that gap's place
    /// in the row stands between, the visibility asks for both, either, or neither.
    /// </summary>
    private static Boolean Filled(
        GapRuleVisibility visibility,
        Boolean[] filled,
        Int32 count,
        Int32 row,
        Int32 index)
    {
        if (visibility == GapRuleVisibility.All)
        {
            return true;
        }

        var before = filled[row * count + index];
        var after = filled[row * count + index + 1];

        return visibility == GapRuleVisibility.Around ? before || after : before && after;
    }

    /// <summary>
    /// Appends the decorations of the gaps between two rows of column boxes.
    /// </summary>
    /// <remarks>
    /// The gap runs the whole width the columns were laid out across, but it is still cut into one stretch
    /// per column, because that is where the columns and the room between them make their bounds: which of
    /// those stretches is drawn is asked of the column it stands under, since the visibility the axis asks
    /// for is about the areas beside a gap and the areas beside one stretch are that one column's cell
    /// above and below (CSS Gaps 1 §3.1.2, §3.4). Stretches of neighbouring columns run into one decoration
    /// - the crossing between them is painted too - unless the axis says its decoration starts and ends at
    /// every crossing.
    /// </remarks>
    private static void AddRowGaps(
        List<GapRuleSegment> target,
        GapRuleAxis axis,
        GapRuleAxis across,
        Boolean along,
        Int32 count,
        Double width,
        Double step,
        Double start,
        Int32 rows,
        Double rowStep,
        Double columnHeight,
        (Double From, Double To) run,
        Boolean[] filled)
    {
        if (rows < 2 || !axis.IsStated)
        {
            return;
        }

        // Between two rows of column boxes the decoration is drawn whatever stands beside it, which is
        // what `normal` means for a multi-column container's row gaps (§3.4).
        var visibility = axis.Visibility == GapRuleVisibility.Normal ? GapRuleVisibility.All : axis.Visibility;

        // A crossing splits the decoration where it stands; only a gap that says nothing of crossings runs
        // through them.
        var atCrossing = axis.Break == GapRuleBreak.Intersection;
        var crossing = step - width;
        var gaps = count - 1;

        for (var row = 0; row + 1 < rows; row++)
        {
            var rule = axis.At(row, rows - 1);

            if (!rule.Appears)
            {
                continue;
            }

            var centre = run.From + (row + 1) * rowStep - (rowStep - columnHeight) / 2;
            var column = 0;

            while (column < count)
            {
                if (!Shown(visibility, filled, count, row, column))
                {
                    column++;
                    continue;
                }

                var low = start + column * step;
                var high = low + width;
                var from = column;

                while (!atCrossing && column + 1 < count && Shown(visibility, filled, count, row, column + 1))
                {
                    column++;
                    high = start + column * step + width;
                }

                // An end at the bound of the whole gap takes its cap inset; an end at a crossing takes the
                // inset written for crossings, read against the column gap standing there.
                var startAtBound = from == 0;
                var endAtBound = column == count - 1;

                low += startAtBound
                    ? axis.CapStart.Resolve(0, 0)
                    : axis.JunctionStart.Resolve(crossing, across.At(from - 1, gaps).Width);
                high -= endAtBound
                    ? axis.CapEnd.Resolve(0, 0)
                    : axis.JunctionEnd.Resolve(crossing, across.At(column, gaps).Width);

                if (high > low)
                {
                    target.Add(new GapRuleSegment(
                        along
                            ? new Rect(centre - rule.Width / 2, low, rule.Width, high - low)
                            : new Rect(low, centre - rule.Width / 2, high - low, rule.Width),
                        rule,
                        isRow: true));
                }

                column++;
            }
        }
    }

    /// <summary>
    /// Gets whether the stretch of a row gap under one column is drawn: of the two cells that column's
    /// place in the rows either side of the gap, the visibility asks for both, either, or neither.
    /// </summary>
    private static Boolean Shown(
        GapRuleVisibility visibility,
        Boolean[] filled,
        Int32 count,
        Int32 row,
        Int32 column)
    {
        if (visibility == GapRuleVisibility.All)
        {
            return true;
        }

        var above = filled[row * count + column];
        var below = filled[(row + 1) * count + column];

        return visibility == GapRuleVisibility.Around ? above || below : above && below;
    }
}

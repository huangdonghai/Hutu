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

            AddColumnGaps(segments, style.Gap.Column, along, count, width, step, columnHeight, rows, rowStep, start, run, filled);
            AddRowGaps(segments, style.Gap.Row, along, count, rows, rowStep, columnHeight, run, content, filled);
        }

        if (segments.Count > 0)
        {
            layout.GapDecorations = segments;
        }
    }

    private static void AddColumnGaps(
        List<GapRuleSegment> target,
        GapRuleAxis axis,
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

        // A multi-column container draws its column rules between every pair of used columns, which is
        // what its `normal` means (CSS Gaps 1 §3.4).
        var visibility = axis.Visibility == GapRuleVisibility.Normal ? GapRuleVisibility.Between : axis.Visibility;
        var gaps = count - 1;

        for (var row = 0; row < rows; row++)
        {
            for (var index = 0; index < gaps; index++)
            {
                var rule = axis.At(row * gaps + index, rows * gaps);
                var shown = visibility == GapRuleVisibility.All ||
                    (visibility == GapRuleVisibility.Around
                        ? filled[row * count + index] || filled[row * count + index + 1]
                        : filled[row * count + index] && filled[row * count + index + 1]);

                if (!rule.Appears || !shown)
                {
                    continue;
                }

                var centre = start + index * step + width + (step - width) / 2;
                var low = run.From + row * rowStep + axis.CapStart.Resolve(0, 0);
                var high = run.From + row * rowStep + columnHeight - axis.CapEnd.Resolve(0, 0);

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
        }
    }

    private static void AddRowGaps(
        List<GapRuleSegment> target,
        GapRuleAxis axis,
        Boolean along,
        Int32 count,
        Int32 rows,
        Double rowStep,
        Double columnHeight,
        (Double From, Double To) run,
        Rect content,
        Boolean[] filled)
    {
        if (rows < 2 || !axis.IsStated)
        {
            return;
        }

        // Between two rows of column boxes the decoration is drawn without asking what stands beside
        // it, which is what `normal` means for a multi-column container's row gaps (§3.4).
        var visibility = axis.Visibility == GapRuleVisibility.Normal ? GapRuleVisibility.All : axis.Visibility;

        for (var row = 0; row + 1 < rows; row++)
        {
            var rule = axis.At(row, rows - 1);
            var before = RowHasContent(filled, count, row);
            var after = RowHasContent(filled, count, row + 1);
            var shown = visibility == GapRuleVisibility.All ||
                (visibility == GapRuleVisibility.Around ? before || after : before && after);

            if (!rule.Appears || !shown)
            {
                continue;
            }

            var centre = run.From + (row + 1) * rowStep - (rowStep - columnHeight) / 2;
            var low = (along ? content.Y : content.X) + axis.CapStart.Resolve(0, 0);
            var high = (along ? content.Y + content.Height : content.X + content.Width) - axis.CapEnd.Resolve(0, 0);

            if (high > low)
            {
                target.Add(new GapRuleSegment(
                    along
                        ? new Rect(centre - rule.Width / 2, low, rule.Width, high - low)
                        : new Rect(low, centre - rule.Width / 2, high - low, rule.Width),
                    rule,
                    isRow: true));
            }
        }
    }

    private static Boolean RowHasContent(Boolean[] filled, Int32 count, Int32 row)
    {
        for (var column = 0; column < count; column++)
        {
            if (filled[row * count + column])
            {
                return true;
            }
        }

        return false;
    }
}

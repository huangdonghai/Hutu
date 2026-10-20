using System;
using System.Collections.Generic;

namespace Hutu.Rendering.Layout;

/// <summary>
/// One of a grid's gaps, standing where the tracks left it: the midline it is drawn on, the stretch it
/// runs along at right angles, and how wide the room between the tracks is.
/// </summary>
internal readonly struct GridGapRun
{
    internal GridGapRun(Boolean centreIsX, Double centre, Double from, Double to, Double across)
    {
        CentreIsX = centreIsX;
        Centre = centre;
        From = from;
        To = to;
        Across = across;
    }

    /// <summary>Gets whether <see cref="Centre"/> is the horizontal coordinate of the gap's midline.</summary>
    public Boolean CentreIsX { get; }

    /// <summary>Gets where the gap stands, measured across the tracks it separates.</summary>
    public Double Centre { get; }

    /// <summary>Gets where the gap starts along its own centreline.</summary>
    public Double From { get; }

    /// <summary>Gets where it ends.</summary>
    public Double To { get; }

    /// <summary>Gets how wide the room between the tracks is, across the gap.</summary>
    public Double Across { get; }
}

/// <summary>
/// Turns a grid's gaps into the decorations they ask for, splitting each one where the specification says
/// (CSS Gaps 1 §3.1): at the bounds of the gap, at every place a perpendicular gap crosses it, and, for
/// the values of <c>rule-break</c> that ask for it, where no items span the crossing.
/// </summary>
internal static class GridGapBands
{
    private const Double Epsilon = 0.01;

    /// <summary>
    /// Appends the decorations of one axis's gaps, given the gaps the perpendicular axis made where they
    /// cross this one, in the order the container placed them - the order the decoration list is read.
    /// </summary>
    public static void Add(
        List<GapRuleSegment> target,
        GapRuleAxis axis,
        Boolean isRow,
        IReadOnlyList<GridGapRun> gaps,
        IReadOnlyList<GridGapRun> crossings,
        GapRuleBreak breaks,
        IReadOnlyList<Rect> items)
    {
        for (var index = 0; index < gaps.Count; index++)
        {
            var gap = gaps[index];
            var rule = axis.At(index, gaps.Count);

            if (!rule.Appears)
            {
                continue;
            }

            foreach (var (from, to) in Segments(gap, crossings, breaks, items))
            {
                if (to <= from)
                {
                    continue;
                }

                target.Add(new GapRuleSegment(
                    GapRuleBands.Band(rule, gap.CentreIsX, gap.Centre, from, to),
                    rule,
                    isRow));
            }
        }
    }

    /// <summary>
    /// Splits one gap into the stretches its decoration is drawn along: the steps of §3.1.2, from the
    /// endpoints the gap's bounds and crossings make, merged while nothing stands in the way.
    /// </summary>
    private static List<(Double From, Double To)> Segments(
        GridGapRun gap,
        IReadOnlyList<GridGapRun> crossings,
        GapRuleBreak breaks,
        IReadOnlyList<Rect> items)
    {
        var segments = new List<(Double, Double)>();

        if (breaks == GapRuleBreak.None)
        {
            // Nothing splits the decoration: it runs from one end of the gap to the other, whatever it
            // crosses - including the items, which are painted over it.
            segments.Add((gap.From, gap.To));
            return segments;
        }

        // The endpoints alternate: the gap's own start, an end/start pair around every crossing, and its
        // own end.
        var endpoints = new List<Double> { gap.From };

        foreach (var crossing in crossings)
        {
            if (crossing.From > gap.From && crossing.To < gap.To)
            {
                endpoints.Add(crossing.From);
                endpoints.Add(crossing.To);
            }
        }

        endpoints.Add(gap.To);

        var cursor = 0;

        while (cursor + 1 < endpoints.Count)
        {
            var start = endpoints[cursor];
            var end = endpoints[cursor + 1];
            cursor += 2;

            if (Discontiguous(gap, start, end, items))
            {
                continue;
            }

            while (cursor + 1 < endpoints.Count)
            {
                var candidate = endpoints[cursor + 1];

                if (Discontiguous(gap, start, candidate, items) ||
                    (breaks == GapRuleBreak.Intersection && !Flanked(gap, start, candidate, crossings, items)))
                {
                    break;
                }

                end = candidate;
                cursor += 2;
            }

            segments.Add((start, end));
        }

        return segments;
    }

    /// <summary>
    /// Gets whether an item stands in the room a stretch of the gap runs through, which ends a decoration
    /// there: a line drawn from one endpoint to the other, as wide as the gap, would strike it.
    /// </summary>
    private static Boolean Discontiguous(GridGapRun gap, Double from, Double to, IReadOnlyList<Rect> items)
    {
        var band = gap.CentreIsX
            ? new Rect(gap.Centre - gap.Across / 2, from, gap.Across, to - from)
            : new Rect(from, gap.Centre - gap.Across / 2, to - from, gap.Across);

        foreach (var item in items)
        {
            if (band.X < item.Right && item.X < band.Right && band.Y < item.Bottom && item.Y < band.Bottom)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Gets whether the items on either side of the gap all span the crossings this stretch runs through,
    /// which is what lets an <c>intersection</c> decoration continue past them (CSS Gaps 1 §3.1.2).
    /// </summary>
    private static Boolean Flanked(
        GridGapRun gap,
        Double from,
        Double to,
        IReadOnlyList<GridGapRun> crossings,
        IReadOnlyList<Rect> items)
    {
        foreach (var crossing in crossings)
        {
            if (crossing.From <= from || crossing.To >= to)
            {
                continue;
            }

            var before = false;
            var after = false;

            foreach (var item in items)
            {
                // The item has to cover the crossing - the whole room the perpendicular gap takes - rather
                // than merely touch its edge.
                var spans = gap.CentreIsX
                    ? item.Y <= crossing.From + Epsilon && item.Bottom >= crossing.To - Epsilon
                    : item.X <= crossing.From + Epsilon && item.Right >= crossing.To - Epsilon;

                if (!spans)
                {
                    continue;
                }

                if (gap.CentreIsX)
                {
                    before |= item.Right <= gap.Centre - gap.Across / 2 + Epsilon;
                    after |= item.X >= gap.Centre + gap.Across / 2 - Epsilon;
                }
                else
                {
                    before |= item.Bottom <= gap.Centre - gap.Across / 2 + Epsilon;
                    after |= item.Y >= gap.Centre + gap.Across / 2 - Epsilon;
                }
            }

            if (!before || !after)
            {
                return false;
            }
        }

        return true;
    }
}

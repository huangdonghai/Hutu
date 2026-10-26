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
        GapRuleAxis across,
        Boolean isRow,
        IReadOnlyList<GridGapRun> gaps,
        IReadOnlyList<GridGapRun> crossings,
        IReadOnlyList<(Double From, Double To)> before,
        IReadOnlyList<(Double From, Double To)> after,
        GapRuleBreak breaks,
        IReadOnlyList<Rect> items)
    {
        // The visibility the axis asked for, with the grid's own reading of `normal`: every gap.
        var visibility = axis.Visibility == GapRuleVisibility.Normal ? GapRuleVisibility.All : axis.Visibility;

        for (var index = 0; index < gaps.Count; index++)
        {
            var gap = gaps[index];
            var rule = axis.At(index, gaps.Count);

            if (!rule.Appears)
            {
                continue;
            }

            foreach (var (from, to, fromAtBound, toAtBound) in Segments(
                         gap, crossings, breaks, items, visibility, before[index], after[index]))
            {
                var start = from + Inset(axis, fromAtBound, true, from, crossings, across);
                var end = to - Inset(axis, toAtBound, false, to, crossings, across);

                if (end > start)
                {
                    target.Add(new GapRuleSegment(
                        GapRuleBands.Band(rule, gap.CentreIsX, gap.Centre, start, end),
                        rule,
                        isRow));
                }
            }
        }
    }

    /// <summary>
    /// Gets whether the areas on the two sides of a stretch of the gap hold anything - which is what the
    /// axis's visibility asks about before a decoration is drawn (CSS Gaps 1 §3.4).
    /// </summary>
    private static Boolean Shown(
        GapRuleVisibility visibility,
        GridGapRun gap,
        (Double From, Double To) before,
        (Double From, Double To) after,
        Double from,
        Double to,
        IReadOnlyList<Rect> items)
    {
        if (visibility == GapRuleVisibility.All)
        {
            return true;
        }

        var first = Occupied(gap.CentreIsX, before, from, to, items);
        var second = Occupied(gap.CentreIsX, after, from, to, items);

        return visibility == GapRuleVisibility.Around ? first || second : first && second;
    }

    /// <summary>Gets whether an item stands in the area beside the gap, over the stretch in question.</summary>
    private static Boolean Occupied(
        Boolean centreIsX,
        (Double From, Double To) area,
        Double from,
        Double to,
        IReadOnlyList<Rect> items)
    {
        foreach (var item in items)
        {
            var across = centreIsX
                ? item.X < area.To && item.Right > area.From
                : item.Y < area.To && item.Bottom > area.From;
            var along = centreIsX
                ? item.Y < to && item.Bottom > from
                : item.X < to && item.Right > from;

            if (across && along)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Gets how far the end of a decoration drawn back from <paramref name="at"/> moves: the inset the page
    /// asked for this kind of end, against the crossing that stands there - nothing, where the end is a cap.
    /// </summary>
    private static Double Inset(
        GapRuleAxis axis,
        Boolean atBound,
        Boolean start,
        Double at,
        IReadOnlyList<GridGapRun> crossings,
        GapRuleAxis across)
    {
        var inset = atBound
            ? start ? axis.CapStart : axis.CapEnd
            : start ? axis.JunctionStart : axis.JunctionEnd;

        if (!inset.IsSet)
        {
            return 0;
        }

        if (atBound)
        {
            return inset.Resolve(0, 0);
        }

        for (var index = 0; index < crossings.Count; index++)
        {
            var crossing = crossings[index];
            var touching = start ? Math.Abs(crossing.To - at) < Epsilon : Math.Abs(crossing.From - at) < Epsilon;

            if (touching)
            {
                return inset.Resolve(crossing.To - crossing.From, across.At(index, crossings.Count).Width);
            }
        }

        return inset.Resolve(0, 0);
    }

    /// <summary>
    /// Splits one gap into the stretches its decoration is drawn along: the steps of §3.1.2, from the
    /// endpoints the gap's bounds and crossings make, merged while nothing stands in the way.
    /// </summary>
    private static List<(Double From, Double To, Boolean FromAtBound, Boolean ToAtBound)> Segments(
        GridGapRun gap,
        IReadOnlyList<GridGapRun> crossings,
        GapRuleBreak breaks,
        IReadOnlyList<Rect> items,
        GapRuleVisibility visibility,
        (Double From, Double To) before,
        (Double From, Double To) after)
    {
        var segments = new List<(Double, Double, Boolean, Boolean)>();

        if (breaks == GapRuleBreak.None)
        {
            // Nothing splits the decoration: it runs from one end of the gap to the other, whatever it
            // crosses - including the items, which are painted over it. Visibility still asks about the
            // whole of that one segment, since there is nothing smaller to ask about.
            if (Shown(visibility, gap, before, after, gap.From, gap.To, items))
            {
                segments.Add((gap.From, gap.To, true, true));
            }

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

        // Visibility is a question about a gap decoration segment, and a segment is what a run of these
        // junction-to-junction pieces becomes once it is merged. A piece that lies beside nothing is not
        // part of a segment at all, so it ends the run rather than being swallowed into it - which is what
        // keeps a decoration from reaching into the empty rows or columns past the last item
        // (grid-gap-decorations-068).
        var cursor = 0;

        while (cursor + 1 < endpoints.Count)
        {
            var start = endpoints[cursor];
            var end = endpoints[cursor + 1];
            cursor += 2;

            if (!Shown(visibility, gap, before, after, start, end, items) ||
                Discontiguous(gap, start, end, items))
            {
                continue;
            }

            while (cursor + 1 < endpoints.Count)
            {
                var candidateStart = endpoints[cursor];
                var candidate = endpoints[cursor + 1];

                if (!Shown(visibility, gap, before, after, candidateStart, candidate, items) ||
                    Discontiguous(gap, start, candidate, items) ||
                    (breaks == GapRuleBreak.Intersection && !Flanked(gap, start, candidate, crossings, items)))
                {
                    break;
                }

                end = candidate;
                cursor += 2;
            }

            segments.Add((start, end, Math.Abs(start - gap.From) < Epsilon, Math.Abs(end - gap.To) < Epsilon));
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

                // The items have to stand against the gap, one on either side of the crossing. A grid
                // item further along the same axis can span the crossing as well, but it is not the
                // area the decoration runs into: counting it let a rule cross a gap that the cells
                // beside it leave open (grid-gap-decorations-007).
                if (gap.CentreIsX)
                {
                    before |= Math.Abs(item.Right - (gap.Centre - gap.Across / 2)) < Epsilon;
                    after |= Math.Abs(item.X - (gap.Centre + gap.Across / 2)) < Epsilon;
                }
                else
                {
                    before |= Math.Abs(item.Bottom - (gap.Centre - gap.Across / 2)) < Epsilon;
                    after |= Math.Abs(item.Y - (gap.Centre + gap.Across / 2)) < Epsilon;
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

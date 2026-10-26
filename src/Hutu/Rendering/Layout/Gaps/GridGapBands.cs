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
/// <para>
/// The decorations of the two axes are worked out before either is inset, because an end standing at a
/// crossing is only a junction when a decoration of the other axis reaches that crossing: where none does,
/// the end dangles and is a cap, whatever the page asked for the ends at crossings to do (CSS Gaps 1 §3.3).
/// </para>
/// </summary>
internal static class GridGapBands
{
    private const Double Epsilon = 0.01;

    /// <summary>
    /// Gets the stretches each gap of an axis is decorated along, before any end is inset: one list per
    /// gap, in the order the container placed the gaps. The other axis asks about these to know which of
    /// its own ends at a crossing join with anything.
    /// </summary>
    public static List<List<(Double From, Double To, Boolean FromAtBound, Boolean ToAtBound)>> Stretches(
        GapRuleAxis axis,
        IReadOnlyList<GridGapRun> gaps,
        IReadOnlyList<GridGapRun> crossings,
        IReadOnlyList<(Double From, Double To)> before,
        IReadOnlyList<(Double From, Double To)> after,
        IReadOnlyList<Rect> items)
    {
        var stretches = new List<List<(Double, Double, Boolean, Boolean)>>(gaps.Count);
        var visibility = Visibility(axis);

        for (var index = 0; index < gaps.Count; index++)
        {
            var gap = gaps[index];
            var rule = axis.At(index, gaps.Count);

            stretches.Add(!rule.Appears
                ? new List<(Double, Double, Boolean, Boolean)>()
                : Segments(gap, crossings, axis.Break, items, visibility, before[index], after[index]));
        }

        return stretches;
    }

    /// <summary>
    /// Appends the decorations of one axis's gaps, given the gaps the perpendicular axis made where they
    /// cross this one, in the order the container placed them - the order the decoration list is read.
    /// </summary>
    /// <param name="stretches">This axis's stretches, as <see cref="Stretches"/> gives them out.</param>
    /// <param name="crossStretches">
    /// The stretches of the axis that crosses this one, per gap of it, in the order its gaps stand - which
    /// is the order this axis's crossings are listed in. An end of this axis standing at a crossing joins
    /// with the other axis only where one of those stretches is drawn up against this gap: an end of it at
    /// either bound of this gap, since that is where the crossing decoration stands.
    /// </param>
    public static void Add(
        List<GapRuleSegment> target,
        GapRuleAxis axis,
        GapRuleAxis across,
        Boolean isRow,
        IReadOnlyList<GridGapRun> gaps,
        IReadOnlyList<GridGapRun> crossings,
        IReadOnlyList<List<(Double From, Double To, Boolean FromAtBound, Boolean ToAtBound)>> stretches,
        IReadOnlyList<List<(Double From, Double To, Boolean FromAtBound, Boolean ToAtBound)>> crossStretches,
        IReadOnlyList<Rect> items)
    {
        // Only a gap that asks for items on both sides of a stretch has to ask whether the crossing
        // decoration is drawn there at all: where every gap is decorated, it always is (CSS Gaps 1 §3.4).
        var visibility = Visibility(axis);

        for (var index = 0; index < gaps.Count; index++)
        {
            var gap = gaps[index];
            var rule = axis.At(index, gaps.Count);

            if (!rule.Appears)
            {
                continue;
            }

            foreach (var (from, to, fromAtBound, toAtBound) in stretches[index])
            {
                var start = from + Inset(
                    axis, across, visibility, fromAtBound, true, from, gap, crossings, crossStretches);
                var end = to - Inset(
                    axis, across, visibility, toAtBound, false, to, gap, crossings, crossStretches);

                if (end > start)
                {
                    target.Add(new GapRuleSegment(
                        GapRuleBands.Band(rule, gap.CentreIsX, gap.Centre, start, end),
                        rule,
                        isRow,
                        gap.Across));
                }
            }
        }
    }

    /// <summary>
    /// Gets how far one end of a decoration moves. An end is a junction only where the decoration of the
    /// axis crossing this gap has an endpoint standing at this very place: with nothing drawn beside it the
    /// end dangles, and a dangling end is a cap even where the page asked for <c>overlap-join</c> at every
    /// junction (CSS Gaps 1 §3.3). A percentage is still read against the crossing gap it stands at, except
    /// at the bound of the container, where there is no crossing to measure.
    /// </summary>
    private static Double Inset(
        GapRuleAxis axis,
        GapRuleAxis across,
        GapRuleVisibility visibility,
        Boolean atBound,
        Boolean start,
        Double at,
        GridGapRun gap,
        IReadOnlyList<GridGapRun> crossings,
        IReadOnlyList<List<(Double From, Double To, Boolean FromAtBound, Boolean ToAtBound)>> crossStretches)
    {
        for (var index = 0; index < crossings.Count; index++)
        {
            var crossing = crossings[index];
            var touching = start ? Math.Abs(crossing.To - at) < Epsilon : Math.Abs(crossing.From - at) < Epsilon;

            if (touching)
            {
                // The crossing is a junction only where a decoration of the other axis is drawn up against
                // this gap; where none is, this end dangles and is a cap (CSS Gaps 1 §3.3).
                var joins = !atBound &&
                    (visibility != GapRuleVisibility.Between || Reaches(crossStretches, gap, index));
                var inset = start
                    ? joins ? axis.JunctionStart : axis.CapStart
                    : joins ? axis.JunctionEnd : axis.CapEnd;

                return inset.Resolve(
                    crossing.To - crossing.From,
                    across.At(index, crossings.Count).Width,
                    joins);
            }
        }

        var cap = start ? axis.CapStart : axis.CapEnd;

        return cap.Resolve(0, 0);
    }

    /// <summary>
    /// Gets whether the gap that crosses this one is decorated at all beside it - which is what makes the
    /// end of this gap's decoration a junction rather than a dangling cap. The crossing's stretches run
    /// along the axis this gap stands on, so a stretch that reaches either of this gap's two sides brings
    /// a decoration to the crossing.
    /// </summary>
    private static Boolean Reaches(
        IReadOnlyList<List<(Double From, Double To, Boolean FromAtBound, Boolean ToAtBound)>> crossStretches,
        GridGapRun gap,
        Int32 index)
    {
        if (crossStretches.Count <= index)
        {
            return true;
        }

        // This gap's own room, measured along the axis the crossing runs on: a stretch of the crossing gap
        // that ends at its near bound, or begins at its far bound, or runs clean through it, is drawn at
        // the crossing. One that stops short of the gap is not.
        var near = gap.Centre - gap.Across / 2;
        var far = gap.Centre + gap.Across / 2;

        foreach (var stretch in crossStretches[index])
        {
            if (stretch.From <= far + Epsilon && stretch.To >= near - Epsilon)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Gets the visibility the axis asked for, with the grid's own reading of `normal`: every gap.</summary>
    private static GapRuleVisibility Visibility(GapRuleAxis axis) =>
        axis.Visibility == GapRuleVisibility.Normal ? GapRuleVisibility.All : axis.Visibility;

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

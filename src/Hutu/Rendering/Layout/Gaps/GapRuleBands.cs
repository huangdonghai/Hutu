using System;
using System.Collections.Generic;

namespace Hutu.Rendering.Layout;

/// <summary>
/// A gap a container put between two of its boxes, told in the page's own axes: the coordinate the gap
/// stands at, and the stretch it runs along at right angles to it.
/// </summary>
internal readonly struct Gap
{
    internal Gap(Boolean centreIsX, Double centre, Double from, Double to)
    {
        CentreIsX = centreIsX;
        Centre = centre;
        From = from;
        To = to;
    }

    /// <summary>Gets whether <see cref="Centre"/> is the horizontal coordinate of the gap's midline.</summary>
    public Boolean CentreIsX { get; }

    /// <summary>Gets where the gap stands, measured across the boxes it separates.</summary>
    public Double Centre { get; }

    /// <summary>Gets where the gap runs from, along the side the boxes it separates are stacked on.</summary>
    public Double From { get; }

    /// <summary>Gets where it runs to.</summary>
    public Double To { get; }
}

/// <summary>
/// Turns the gaps of a container into the decorations they ask for (CSS Gaps 1 §3.1).
/// </summary>
internal static class GapRuleBands
{
    /// <summary>
    /// Appends the decoration of every gap in <paramref name="gaps"/>, which are the gaps of one axis and
    /// stand in the order the container placed them - the order the decoration list is read out in.
    /// </summary>
    /// <remarks>
    /// A rule takes the middle of its gap and is as wide as it says, so a rule wider than the gap it
    /// stands in overhangs it and runs under the boxes on either side - and a gap of no room at all,
    /// which is what two boxes touching each other leave, still gets its rule (CSS Gaps 1 §3).
    /// </remarks>
    public static void Add(
        List<GapRuleSegment> target,
        GapRuleAxis axis,
        Boolean isRow,
        IReadOnlyList<Gap> gaps)
    {
        var count = gaps.Count;

        if (count == 0)
        {
            return;
        }

        for (var index = 0; index < count; index++)
        {
            var gap = gaps[index];
            var rule = axis.At(index, count);

            if (!rule.Appears || gap.To <= gap.From)
            {
                continue;
            }

            var band = gap.CentreIsX
                ? new Rect(gap.Centre - rule.Width / 2, gap.From, rule.Width, gap.To - gap.From)
                : new Rect(gap.From, gap.Centre - rule.Width / 2, gap.To - gap.From, rule.Width);

            target.Add(new GapRuleSegment(band, rule, isRow));
        }
    }
}

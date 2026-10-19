using System;
using System.Collections.Generic;

namespace Hutu.Rendering.Layout;

/// <summary>
/// Shares a block's children over the fragments a container gives them: the columns of a multi-column
/// box, or the pages of a paged flow.
/// </summary>
/// <remarks>
/// Each fragment takes as many whole children as its extent allows, asking
/// <see cref="ChildSlicer.Fitting"/>, and a forced break ends it even where more would fit. The two
/// edges of a block carry the breaks its first and last children state (CSS Break 3 §3.1.1): a forced
/// value there stands for the block's own, and an avoid stands beside the block's own avoid.
/// </remarks>
internal static class Fragmentainer
{
    /// <summary>
    /// Fills the fragments in turn and reports how many children each takes. Filling stops once every
    /// child is placed, so the list may be shorter than the extents it was given.
    /// </summary>
    public static IReadOnlyList<Int32> Fill(IReadOnlyList<FragmentChild> children, IReadOnlyList<Double> extents)
    {
        var counts = new List<Int32>(extents.Count);
        var placed = 0;

        foreach (var extent in extents)
        {
            if (placed >= children.Count)
            {
                break;
            }

            var cut = ChildSlicer.Fitting(children, extent, placed);
            counts.Add(cut.Fits);
            placed += cut.Fits;
        }

        return counts;
    }

    /// <summary>
    /// Gets the least extent a balanced fill of <paramref name="fragments"/> fragments needs: the height
    /// at which the whole content still fits, which is what <c>column-fill: balance</c> answers and what
    /// Chrome iterates towards.
    /// </summary>
    public static Double Balance(IReadOnlyList<FragmentChild> children, Int32 fragments)
    {
        if (fragments <= 1 || children.Count == 0)
        {
            return Total(children);
        }

        var extents = new Double[fragments];
        var tallest = Tallest(children);
        var low = 0.0;
        var high = Total(children);

        for (var step = 0; step < 40; step++)
        {
            var middle = (low + high) / 2;

            Array.Fill(extents, middle);

            // A fragment that takes a child taller than itself overflows on purpose - the flow must make
            // progress - so an extent below the tallest child is no balance even where every child
            // "fits" one to a fragment.
            if (middle >= tallest && Placed(Fill(children, extents)) >= children.Count)
            {
                high = middle;
            }
            else
            {
                low = middle;
            }
        }

        return high;
    }

    /// <summary>
    /// Gets the break a block's start carries: its own value combined with the one its first in-flow
    /// child states (CSS Break 3 §3.1.1). A forced value wins over an avoid, and an avoid over auto.
    /// </summary>
    public static BreakValue Before(IReadOnlyList<FragmentChild> children, BreakValue own) =>
        children.Count == 0 ? own : Combine(own, children[0].Before);

    /// <summary>Gets the break the block's end carries, from its own value and its last child's.</summary>
    public static BreakValue After(IReadOnlyList<FragmentChild> children, BreakValue own) =>
        children.Count == 0 ? own : Combine(own, children[^1].After);

    /// <summary>
    /// Combines two values that land on the same break point: a forced break is taken however the other
    /// side feels about it, and two autos are an auto (CSS Break 3 §3.1).
    /// </summary>
    public static BreakValue Combine(BreakValue left, BreakValue right)
    {
        if (Forced(left))
        {
            return left;
        }

        if (Forced(right))
        {
            return right;
        }

        return left == BreakValue.Avoid || right == BreakValue.Avoid ? BreakValue.Avoid : BreakValue.Auto;
    }

    /// <summary>Gets whether a value forces the break point it stands on.</summary>
    public static Boolean Forced(BreakValue value) =>
        value is BreakValue.Page or BreakValue.Column or BreakValue.Left or BreakValue.Right;

    private static Int32 Placed(IReadOnlyList<Int32> counts)
    {
        var total = 0;

        for (var i = 0; i < counts.Count; i++)
        {
            total += counts[i];
        }

        return total;
    }

    private static Double Tallest(IReadOnlyList<FragmentChild> children)
    {
        var tallest = 0.0;

        for (var i = 0; i < children.Count; i++)
        {
            tallest = Math.Max(tallest, children[i].Height);
        }

        return tallest;
    }

    private static Double Total(IReadOnlyList<FragmentChild> children)
    {
        var total = 0.0;

        for (var i = 0; i < children.Count; i++)
        {
            total += children[i].Height;
        }

        return total;
    }
}

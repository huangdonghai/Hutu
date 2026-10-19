using System;
using System.Collections.Generic;

namespace Hutu.Rendering.Layout;

/// <summary>One in-flow child as the fragmentainer sees it: how tall it is and where it lets a break fall.</summary>
internal readonly struct FragmentChild
{
    public FragmentChild(Double height, BreakValue before, BreakValue after, BreakValue inside)
    {
        Height = height;
        Before = before;
        After = after;
        Inside = inside;
    }

    /// <summary>Gets the child's outer height: its border box and the margins that stand around it.</summary>
    public Double Height { get; }

    /// <summary>Gets what the child asks for before itself.</summary>
    public BreakValue Before { get; }

    /// <summary>Gets what it asks for after itself.</summary>
    public BreakValue After { get; }

    /// <summary>Gets what it refuses inside itself. A child is only ever placed whole here, so this is
    /// the caller's business when it asks the child to split itself.</summary>
    public BreakValue Inside { get; }
}

/// <summary>Where a fragment ends and why.</summary>
/// <param name="Fits">How many leading children of the block the fragment takes whole.</param>
/// <param name="Forced">Whether a forced break ends the fragment, so a fresh one begins even though the
/// next child might still fit.</param>
internal readonly record struct ChildCut(Int32 Fits, Boolean Forced);

/// <summary>
/// Works out how many of a block's children a fragment takes (CSS Break 3 §4.1-4.4): a child that does
/// not fit begins the next fragment, unless the break between it and its neighbour is forbidden by
/// <c>break-before: avoid</c> or <c>break-after: avoid</c>, in which case the cut moves back until an
/// allowed one is found. With no allowed cut at all the fragment overflows with one child, because a
/// break is not invented where the page forbade one. A forced value on either side of a boundary ends the
/// fragment there.
/// </summary>
internal static class ChildSlicer
{
    public static ChildCut Fitting(IReadOnlyList<FragmentChild> children, Double limit)
    {
        var used = 0.0;

        for (var i = 0; i < children.Count; i++)
        {
            if (i > 0 && (Forces(children[i - 1].After) || Forces(children[i].Before)))
            {
                return new ChildCut(i, true);
            }

            if (used + children[i].Height <= limit + 0.001)
            {
                used += children[i].Height;
                continue;
            }

            var cut = i;

            while (cut > 0 && !Allowed(children, cut))
            {
                cut--;
            }

            // No boundary before any child may take the break: the fragment overflows with the one child,
            // and the flow still advances.
            return new ChildCut(Math.Max(1, cut), false);
        }

        return new ChildCut(children.Count, false);
    }

    /// <summary>Gets whether a forced value on either side ends the fragment at this boundary.</summary>
    private static Boolean Forces(BreakValue value) =>
        value is BreakValue.Page or BreakValue.Column or BreakValue.Left or BreakValue.Right;

    /// <summary>Gets whether a break may fall between the children either side of the boundary.</summary>
    private static Boolean Allowed(IReadOnlyList<FragmentChild> children, Int32 cut) =>
        !Forces(children[cut - 1].After) &&
        !Forces(children[cut].Before) &&
        children[cut - 1].After != BreakValue.Avoid &&
        children[cut].Before != BreakValue.Avoid;
}

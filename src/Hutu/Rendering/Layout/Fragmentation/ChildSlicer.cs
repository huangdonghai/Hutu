using System;
using System.Collections.Generic;

namespace Hutu.Rendering.Layout;

/// <summary>One in-flow child as the fragmentainer sees it: how tall it is and where it lets a break fall.</summary>
internal readonly struct FragmentChild
{
    public FragmentChild(
        Double height,
        BreakValue before,
        BreakValue after,
        BreakValue inside,
        Int32 run = -1,
        Int32 orphans = 0,
        Int32 widows = 0)
    {
        Height = height;
        Before = before;
        After = after;
        Inside = inside;
        Run = run;
        Orphans = orphans;
        Widows = widows;
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

    /// <summary>
    /// Gets which child's line this piece is, or -1 for a child that stands whole. Two pieces of one run
    /// sit next to each other in the list, and the break between them is the block's own line break.
    /// </summary>
    public Int32 Run { get; }

    /// <summary>Gets the fewest lines of its run a fragment that is not the last may end with.</summary>
    public Int32 Orphans { get; }

    /// <summary>Gets the fewest lines of its run a fragment that is not the first may begin with.</summary>
    public Int32 Widows { get; }
}

/// <summary>Where a fragment ends and why.</summary>
/// <param name="Fits">How many leading children of the block the fragment takes whole.</param>
/// <param name="Forced">Whether a forced break ends the fragment, so a fresh one begins even though the
/// next child might still fit.</param>
internal readonly record struct ChildCut(Int32 Fits, Boolean Forced);

/// <summary>
/// The lines of a block's own inline content that one fragment of it takes. A block whose parent is
/// multi-column is broken between its lines rather than moved whole (CSS Multicol 3 §3.1), and the
/// fragment is told which of them stand in it.
/// </summary>
internal readonly record struct LineRange(Int32 From, Int32 To);

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
    public static ChildCut Fitting(IReadOnlyList<FragmentChild> children, Double limit, Int32 start = 0)
    {
        var used = 0.0;

        for (var i = start; i < children.Count; i++)
        {
            if (i > start && (Forces(children[i - 1].After) || Forces(children[i].Before)))
            {
                return new ChildCut(i - start, true);
            }

            if (used + children[i].Height <= limit + 0.001)
            {
                used += children[i].Height;
                continue;
            }

            var cut = KeepsRun(children, i, start);

            // A cut at the end of the list is no boundary between two children at all: the fragment takes
            // what is left and the flow moves on.
            while (cut > start && cut < children.Count && !Allowed(children, cut))
            {
                cut--;
            }

            // No boundary before any child may take the break: the fragment overflows with the one child,
            // and the flow still advances.
            return new ChildCut(Math.Max(1, cut - start), false);
        }

        return new ChildCut(children.Count - start, false);
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

    /// <summary>
    /// Gets where the boundary moves to when it fell inside a run of one block's lines: the tail the
    /// widows ask for is kept, but not at the cost of the orphans this fragment owes, which is the order
    /// Chrome keeps the two in (CSS Break 3 §3.3). A break between two children, or between the lines of
    /// two different blocks, is nobody's widows business and is returned as it was.
    /// </summary>
    private static Int32 KeepsRun(IReadOnlyList<FragmentChild> children, Int32 cut, Int32 start)
    {
        if (cut == 0)
        {
            return cut;
        }

        var run = children[cut].Run;

        if (run < 0 || children[cut - 1].Run != run)
        {
            return cut;
        }

        var total = 0;
        var taken = 0;

        for (var index = start; index < children.Count; index++)
        {
            if (children[index].Run != run)
            {
                continue;
            }

            total++;
            taken += index < cut ? 1 : 0;
        }

        var orphans = Math.Max(1, children[cut].Orphans);
        var widows = Math.Max(1, children[cut].Widows);

        // The break moves back until the tail the widows ask for is left behind it. How far back it may
        // go is the orphans' business: the fragment before the break owes them too, so a move that would
        // leave it with fewer lines than that stops at the orphans and lets the widows go short instead
        // (CSS Break 3 §3.3, and Chrome's matrix of nine lines at three and three, which comes out
        // 4/3/2 - the last column short - rather than 4/5).
        var kept = Math.Min(taken, Math.Max(total - widows, orphans));

        if (kept == taken)
        {
            return cut;
        }

        var boundary = start;
        var seen = 0;

        for (var index = start; index < children.Count && seen < kept; index++)
        {
            if (children[index].Run != run)
            {
                continue;
            }

            seen++;
            boundary = index + 1;
        }

        return Math.Max(boundary, start);
    }
}

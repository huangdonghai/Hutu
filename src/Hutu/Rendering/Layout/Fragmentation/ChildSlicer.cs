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
        Int32 widows = 0,
        Boolean splits = false)
    {
        Height = height;
        Before = before;
        After = after;
        Inside = inside;
        Run = run;
        Orphans = orphans;
        Widows = widows;
        Splits = splits;
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

    /// <summary>
    /// Gets whether the child lets a fragmentainer cut through it. A balance may then settle a fragment
    /// shorter than the child, since the child can stand in more than one column
    /// (CSS Flexbox 1 §6, measured: multi-line-column-flex-fragmentation-059).
    /// </summary>
    public Boolean Splits { get; }
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
/// What a fragment of a broken box draws of the box's own edges: the border and padding it owns, whether
/// the box begins or ends with it, and the border box the whole box would have had (CSS Break 3 §4.7).
/// A sliced box keeps its border, its padding and its corner curves only at the ends it really has, and
/// its background is painted across the whole box and cut with it; a cloned box is drawn complete on
/// every fragment, which is the same record with every edge owned and no whole box to line up against.
/// </summary>
/// <param name="Border">The border this fragment draws, nothing on an edge it was cut at.</param>
/// <param name="Padding">The padding it keeps room for, nothing on an edge it was cut at.</param>
/// <param name="Starts">Whether the box begins with this fragment, which owns its start edge.</param>
/// <param name="Ends">Whether the box ends with this fragment, which owns its end edge.</param>
/// <param name="Whole">The border box of the unbroken box, which its background is painted across.</param>
internal readonly record struct FragmentSlice(
    Thickness Border,
    Thickness Padding,
    Boolean Starts,
    Boolean Ends,
    Rect Whole,
    Double Offset = 0,
    Double Height = 0);

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

            // The run the break fell in may not begin in this fragment at all: it moves whole to the
            // next one, and this fragment ends with what stood before it (CSS Break 3 §4.3). Saying so
            // with no child at all leaves the caller free to hand the run on - and lets a balance search
            // see that an extent this short was no fit, rather than mistake the forced first line for one.
            if (cut < 0)
            {
                return new ChildCut(0, false);
            }

            // A cut at the end of the list is no boundary between two children at all: the fragment takes
            // what is left and the flow moves on.
            var natural = cut;

            while (cut > start && cut < children.Count && !Allowed(children, cut))
            {
                cut--;
            }

            // With no allowed boundary before this child, the page's avoid cannot be honoured: the
            // natural cut stands and the avoid is ignored, rather than leave the fragment almost empty
            // (CSS Break 3 §4.4; measured - Chrome fills the column and breaks at the forbidden point
            // when there is nowhere better to break).
            if (cut <= start && natural > start)
            {
                cut = natural;
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
    /// two different blocks, is nobody's widows business and is returned as it was. Minus one says the
    /// run cannot begin in this fragment at all.
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

        // The orphans are the fewest lines the fragment before a break may keep. Where the room leaves
        // fewer than that, the block does not begin in this fragment at all: the cut moves back to the
        // line the run starts on, so the whole run goes to the next fragment and whatever stood before
        // it in this one stays where it is (CSS Break 3 §4.3).
        // The orphans only say anything about a run the break falls inside: one that fits whole keeps
        // every line it has whatever the orphans ask for.
        if (kept < orphans && taken < total)
        {
            var first = start;

            while (first < cut && children[first].Run != run)
            {
                first++;
            }

            // The run begins where this fragment begins: nowhere earlier may take the break, so the
            // fragment takes none of it (-1 tells the caller so).
            if (first == start)
            {
                return -1;
            }

            // Moving the run whole puts the break at the boundary before it. Where the page forbade that
            // boundary and no earlier one may take the break either, the lines that fit stay here and the
            // orphans go short: Chrome lets an orphans rule yield to break-before/break-after: avoid
            // (CSS Break 3 §4.4, WPT break-between-avoid-004).
            for (var before = first; before > start; before--)
            {
                if (Allowed(children, before))
                {
                    return first;
                }
            }

            return cut;
        }

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

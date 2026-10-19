using System;
using System.Collections.Generic;

namespace Hutu.Rendering.Layout;

/// <summary>
/// Works out how much of a block's line stack a fragmentainer takes, at the point where the lines would
/// otherwise run past its extent (CSS Break 3 §3.3).
/// </summary>
/// <remarks>
/// The two line counts are the ones a page or a column keeps at the break: <c>orphans</c> lines before it
/// and <c>widows</c> after it. A block shorter than either count is never split - all of its lines are
/// kept together - and a fragment that would take fewer than its orphans leaves the block whole for the
/// next one. A fragment the block already started in must take at least one line, or no fragmentainer
/// could ever make progress and the flow would never finish.
/// </remarks>
internal static class FragmentSlicer
{
    /// <summary>
    /// Gets the number of leading lines that fit in a fragment of <paramref name="limit"/> pixels.
    /// </summary>
    /// <param name="startsHere">
    /// Whether the block's first line would be in this fragment. The orphans rule only speaks of a block
    /// that begins here: a continuation has already had its share of the block.
    /// </param>
    public static Int32 Fitting(
        IReadOnlyList<Double> heights,
        Double limit,
        Int32 orphans,
        Int32 widows,
        Boolean startsHere)
    {
        var count = heights.Count;

        if (count == 0)
        {
            return 0;
        }

        // A block with fewer lines than either count is kept together, whatever room is left: it either
        // fits whole or begins on the next fragmentainer.
        if (count < orphans || count < widows)
        {
            return startsHere ? (Total(heights) <= limit + 0.001 ? count : 0) : count;
        }

        var used = 0.0;
        var fit = 0;

        while (fit < count && used + heights[fit] <= limit + 0.001)
        {
            used += heights[fit];
            fit++;
        }

        if (fit == count)
        {
            return count;
        }

        // The fragment after the break keeps at least the widows, so the break moves earlier where the
        // room that is left would strand a shorter tail than that.
        if (count - fit < widows)
        {
            fit = Math.Min(fit, Math.Max(0, count - widows));
        }

        if (fit == 0)
        {
            // Nothing fits: a block that begins here moves whole to the next fragment, and one that
            // already began must still take a line or the flow could not advance.
            return startsHere ? 0 : 1;
        }

        if (startsHere && fit < orphans)
        {
            return 0;
        }

        return fit;
    }

    private static Double Total(IReadOnlyList<Double> heights)
    {
        var total = 0.0;

        for (var i = 0; i < heights.Count; i++)
        {
            total += heights[i];
        }

        return total;
    }
}

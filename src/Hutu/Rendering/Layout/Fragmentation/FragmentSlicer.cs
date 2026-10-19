using System;
using System.Collections.Generic;

namespace Hutu.Rendering.Layout;

/// <summary>
/// Works out how much of a block's line stack a fragmentainer takes, at the point where the lines would
/// otherwise run past its extent (CSS Break 3 §3.3).
/// </summary>
/// <remarks>
/// The fragment takes the lines that fit, and the two counts it keeps at the break are <c>orphans</c>
/// lines before it and <c>widows</c> after it. Where leaving the widows would take the break back past
/// the orphans the fragment itself owes, the orphans hold and the widows go short: Chrome lays the
/// corpus's nine-line, three-orphans and three-widows columns out as 4/3/2, not 4/2/3, and the same
/// order holds across its whole matrix of small counts. A fragment also always takes at least one line,
/// so that a line taller than the fragment overflows it rather than leaving the flow stuck.
/// </remarks>
internal static class FragmentSlicer
{
    /// <summary>
    /// Gets the number of leading lines that fit in a fragment of <paramref name="limit"/> pixels.
    /// </summary>
    /// <param name="startTrims">
    /// The leading each line has trimmed off its start edge, or null where the box trims none. The
    /// first line of a fragment stands that much further up than its box, so the room below it is
    /// that much more.
    /// </param>
    /// <param name="endTrims">
    /// The leading each line has trimmed off its end edge. The line a fragment ends on needs that
    /// much less room, since its box may run past the fragment's end into the trim (CSS Inline 3 §6.2
    /// applies the trim to the first and last line of every column).
    /// </param>
    public static Int32 Fitting(
        IReadOnlyList<Double> heights,
        Double limit,
        Int32 orphans,
        Int32 widows,
        Int32 start = 0,
        IReadOnlyList<Double>? startTrims = null,
        IReadOnlyList<Double>? endTrims = null)
    {
        var count = heights.Count - start;

        if (count <= 0)
        {
            return 0;
        }

        var room = limit + (startTrims is null ? 0 : startTrims[start]);
        var used = 0.0;
        var fit = 0;

        while (fit < count &&
            used + heights[start + fit] <= room + (endTrims is null ? 0 : endTrims[start + fit]) + 0.001)
        {
            used += heights[start + fit];
            fit++;
        }

        if (fit == count)
        {
            return count;
        }

        // The fragment after the break keeps at least the widows, so the break moves earlier where the
        // room that is left would strand a shorter tail than that. How far back it may go is the
        // orphans' business: the fragment before the break owes them too, so a move that would leave
        // it with fewer lines than that stops at the orphans and lets the widows go unmet instead.
        if (count - fit < widows)
        {
            fit = Math.Min(fit, Math.Max(count - widows, orphans));
        }

        // A fragment must make progress: a line taller than the fragment overflows it, and a fragment
        // that could take none of the block still takes its first line.
        return Math.Max(fit, 1);
    }
}

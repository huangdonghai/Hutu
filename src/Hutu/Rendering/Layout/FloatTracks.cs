using System;
using System.Collections.Generic;

namespace Hutu.Rendering.Layout;

/// <summary>
/// The floats a block container has room against, and the edges they leave for everything else.
/// </summary>
/// <remarks>
/// A float is pulled out of the stacking cursor and pinned to one edge of its container's content
/// box, at the height its place in flow would have had. Everything that follows has to know: a box
/// that clears moves below them, a box that has none left of it reads the free span between the two
/// columns of reserved space, and the container itself grows to the deepest margin box so the floats
/// do not hang out below it.
/// <para>
/// The tracks are kept in whatever frame the pass that fills them works in - the container's own
/// content-box origin for a measuring pass, document coordinates for an arranging one - because the
/// two passes walk the same children in the same order with the same numbers, and a translation is
/// all that differs between them. That is also why both passes ask for the width a float is measured
/// at before they place it: the same free span has to come out of the same sequence, or the size the
/// arranging pass reads from the cache would not be the one the height was answered from.
/// </para>
/// <para>
/// Only the floats a container holds directly are kept here, which is what CSS asks: a float belongs to
/// its nearest block container ancestor, and the line boxes of a block outside that container are not
/// adjacent to it and do not step aside for it. What CSS does add is a rule about boxes rather than
/// lines - the border edge of a box that establishes a formatting context may not touch a float's
/// margin edge - and that is settled where the child is placed, in <see cref="BlockLayout"/>, rather
/// than here.
/// </para>
/// </remarks>
internal sealed class FloatTracks
{
    private readonly List<Rect> _left = new();
    private readonly List<Rect> _right = new();

    private readonly Double _edgeLeft;
    private readonly Double _edgeRight;

    public FloatTracks(Double edgeLeft, Double edgeRight)
    {
        _edgeLeft = edgeLeft;
        _edgeRight = edgeRight;
    }

    /// <summary>Gets the deepest bottom any float reaches, margin box included.</summary>
    public Double Bottom
    {
        get
        {
            var deepest = 0.0;

            foreach (var track in _left)
            {
                deepest = Math.Max(deepest, track.Bottom);
            }

            foreach (var track in _right)
            {
                deepest = Math.Max(deepest, track.Bottom);
            }

            return deepest;
        }
    }

    /// <summary>
    /// Gets the free span at the height a box of the given extent would start, between the floats
    /// already standing beside it.
    /// </summary>
    public (Double Left, Double Right) FreeSpanAt(Double top, Double height)
    {
        var left = _edgeLeft;
        var right = _edgeRight;

        foreach (var track in _left)
        {
            if (Intersects(track, top, height))
            {
                left = Math.Max(left, track.Right);
            }
        }

        foreach (var track in _right)
        {
            if (Intersects(track, top, height))
            {
                right = Math.Min(right, track.X);
            }
        }

        return right < left ? (left, left) : (left, right);
    }

    /// <summary>
    /// Gets the height a box of <paramref name="inlineExtent"/> first stands clear of every float it
    /// is beside, stepping down from <paramref name="top"/> one blocking float at a time. A box that
    /// keeps its own formatting context may not overlap a float, so when the room left beside the
    /// floats is narrower than the box it has to drop until it fits - the same descent a float makes
    /// onto the next row, asked here for a box rather than a float. Returns <paramref name="top"/>
    /// unchanged once nothing below still blocks it, so a box wider than the container just overflows.
    /// </summary>
    public Double FitBelow(Double top, Double inlineExtent)
    {
        var y = top;

        for (var attempt = 0; attempt < _left.Count + _right.Count; attempt++)
        {
            var (left, right) = FreeSpanAt(y, 0.001);

            if (right - left >= inlineExtent)
            {
                return y;
            }

            var next = NextBottom(y, 0.001);

            if (next <= y)
            {
                break;
            }

            y = next;
        }

        return y;
    }

    /// <summary>
    /// Gets the height a box clearing the given side stands at: below every float it refuses to sit
    /// beside, and unchanged when there is none to go below.
    /// </summary>
    public Double ClearBelow(ClearSide side, Double top)
    {
        var below = top;

        if (side is ClearSide.Left or ClearSide.Both)
        {
            foreach (var track in _left)
            {
                below = Math.Max(below, track.Bottom);
            }
        }

        if (side is ClearSide.Right or ClearSide.Both)
        {
            foreach (var track in _right)
            {
                below = Math.Max(below, track.Bottom);
            }
        }

        return below;
    }

    /// <summary>
    /// Places a float at the given edge and reserves the room it takes. The returned rectangle is the
    /// float's border box; the reservation covers its margin box, which is the area later floats and
    /// clearing boxes have to stay out of.
    /// </summary>
    public Rect Place(FloatSide side, Double width, Double height, Thickness margin, Double top)
    {
        var y = top + margin.Top;

        // The float goes to the first height at which its own extent fits between the floats already
        // standing there. The specification steps down one line at a time; this steps to the bottom
        // of the float that blocked it, which reaches the same place whenever the band the float
        // spans crosses one row of neighbours - and the rows of a portal's columns never interleave.
        for (var attempt = 0; attempt < _left.Count + _right.Count + 1; attempt++)
        {
            var (left, right) = FreeSpanAt(y, height);
            var x = side == FloatSide.Left ? left + margin.Left : right - width - margin.Right;
            var fits = side == FloatSide.Left
                ? x + width <= right
                : x >= left;

            if (fits || attempt == _left.Count + _right.Count)
            {
                var reserved = new Rect(
                    x - margin.Left,
                    y - margin.Top,
                    margin.Left + width + margin.Right,
                    margin.Top + height + margin.Bottom);

                (side == FloatSide.Left ? _left : _right).Add(reserved);
                return new Rect(x, y, width, height);
            }

            var next = NextBottom(y, height);

            if (next <= y)
            {
                break;
            }

            y = next;
        }

        // Nothing below the blocked heights had room either, which only happens when the float is
        // wider than the container: it keeps the place it was asked for and overflows the way a
        // browser lets one overflow, rather than being pushed into a track of its own.
        var overflow = new Rect(
            (side == FloatSide.Left ? _edgeLeft : _edgeRight - width) - margin.Left,
            y - margin.Top,
            margin.Left + width + margin.Right,
            margin.Top + height + margin.Bottom);

        (side == FloatSide.Left ? _left : _right).Add(overflow);
        return new Rect(
            side == FloatSide.Left ? _edgeLeft : _edgeRight - width,
            y,
            width,
            height);
    }

    /// <summary>
    /// Absorbs the floats standing in another set of tracks, moved by the given distances, as
    /// though they had been placed here.
    /// </summary>
    /// <remarks>
    /// This is how a float held by a container that establishes no formatting context of its own
    /// reaches the container it belongs to. CSS 2.1 §9.4.1 lists what establishes a block
    /// formatting context, and a plain block is not among them: such a float takes part in the
    /// context of the nearest ancestor that does establish one, so §9.5 lets it shorten the line
    /// boxes of every later box in that context - including the container's own later siblings,
    /// whose lines are the ones the ordinary band never told. The distances are the holding
    /// container's content-box corner in this container's frame, which is what the walk that
    /// places a child already works out.
    /// </remarks>
    public void Absorb(FloatTracks other, Double dx, Double dy)
    {
        foreach (var track in other._left)
        {
            _left.Add(new Rect(track.X + dx, track.Y + dy, track.Width, track.Height));
        }

        foreach (var track in other._right)
        {
            _right.Add(new Rect(track.X + dx, track.Y + dy, track.Width, track.Height));
        }
    }

    /// <summary>Gets whether the track stands in the band the float would occupy.</summary>
    private static Boolean Intersects(Rect track, Double top, Double height) =>
        track.Bottom > top && track.Y < top + Math.Max(height, 0.001);

    /// <summary>
    /// Gets the lowest float bottom that would still block the band starting here. Stepping to it
    /// moves the placement down past at least one float, so the loop always makes progress.
    /// </summary>
    private Double NextBottom(Double top, Double height)
    {
        var next = Double.MaxValue;

        foreach (var track in _left)
        {
            if (Intersects(track, top, height) && track.Bottom > top && track.Bottom < next)
            {
                next = track.Bottom;
            }
        }

        foreach (var track in _right)
        {
            if (Intersects(track, top, height) && track.Bottom > top && track.Bottom < next)
            {
                next = track.Bottom;
            }
        }

        return next == Double.MaxValue ? top : next;
    }
}

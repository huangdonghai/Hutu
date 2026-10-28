using System;
using System.Collections.Generic;
using System.Linq;
using Hutu.Rendering.Boxes;

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
    /// <summary>
    /// One float's place in the run, and the area its own lines stand clear of.
    /// </summary>
    /// <remarks>
    /// The box is the float's margin box, and it is what the next float, a clearing box and a block that
    /// keeps its own formatting context are measured against (CSS 2.1 §9.4): a shape narrows only the room a
    /// line has and never the place a float is put at, which is the difference CSS Shapes 1 §2 draws.
    /// </remarks>
    /// <remarks>
    /// The owner is the float's own box, which is what lets a question be asked about <em>where the float
    /// came from</em>: a list item's marker stands beside the floats its container was given, and not beside
    /// a float the item holds itself (CSS 2.1 §12.5.1 read with §9.5), which is a question the geometry
    /// alone cannot answer once a container's walks hand their floats up the tree.
    /// </remarks>
    private readonly record struct FloatTrack(Rect Box, FloatArea? Area, Box? Owner);

    private readonly List<FloatTrack> _left = new();
    private readonly List<FloatTrack> _right = new();

    /// <summary>
    /// The floats of the context this container stands in, translated into this container's frame and kept
    /// on the side the context put them on.
    /// </summary>
    /// <remarks>
    /// A container that holds a float of its own still takes part in the formatting context above it: CSS
    /// 2.1 §9.5 makes a float step clear of the floats already standing there, and §9.5 narrows the lines
    /// of every later box in that context by them - so a list item with a floated thumbnail beside a
    /// floated picture reads both, not just its own. They are kept apart from the ones this walk placed
    /// because they belong to the context and not to the container: <see cref="Absorb"/> hands only what
    /// the container holds up the tree, and handing the context's copies along would re-pay the distance
    /// between the two frames at every level, and let a box's marker read a float that is really its own.
    /// </remarks>
    private readonly List<FloatTrack> _aroundLeft = new();

    private readonly List<FloatTrack> _aroundRight = new();

    private readonly Double _edgeLeft;
    private readonly Double _edgeRight;

    public FloatTracks(Double edgeLeft, Double edgeRight)
    {
        _edgeLeft = edgeLeft;
        _edgeRight = edgeRight;
    }

    /// <summary>Gets the floats standing on the start side: the container's own, then the context's.</summary>
    private IEnumerable<FloatTrack> Lefts => _left.Concat(_aroundLeft);

    /// <summary>Gets the floats standing on the end side: the container's own, then the context's.</summary>
    private IEnumerable<FloatTrack> Rights => _right.Concat(_aroundRight);

    /// <summary>All the floats the run has to stand aside for, this container's own and the context's.</summary>
    private IEnumerable<FloatTrack> Every => Lefts.Concat(Rights);

    /// <summary>Gets the deepest bottom any float reaches, margin box included.</summary>
    public Double Bottom
    {
        get
        {
            var deepest = 0.0;

            foreach (var track in Every)
            {
                deepest = Math.Max(deepest, track.Box.Bottom);
            }

            return deepest;
        }
    }

    /// <summary>
    /// Gets the free span at the height a box of the given extent would start, between the floats
    /// already standing beside it.
    /// </summary>
    /// <remarks>
    /// This is the margin-box answer, which is what a float standing beside another and a box that keeps
    /// its own formatting context are measured against; a line box asks <see cref="LineSpanAt"/> instead.
    /// </remarks>
    public (Double Left, Double Right) FreeSpanAt(Double top, Double height)
    {
        var left = _edgeLeft;
        var right = _edgeRight;

        foreach (var track in Lefts)
        {
            if (Intersects(track.Box, top, height))
            {
                left = Math.Max(left, track.Box.Right);
            }
        }

        foreach (var track in Rights)
        {
            if (Intersects(track.Box, top, height))
            {
                right = Math.Min(right, track.Box.X);
            }
        }

        return right < left ? (left, left) : (left, right);
    }

    /// <summary>
    /// Gets the span a line box standing between the two heights has left beside the floats, read against
    /// each float's area where it has one.
    /// </summary>
    /// <remarks>
    /// A line is shortened as necessary to avoid the float area, and the area is what a shape came to
    /// rather than the box it was drawn on: a band that meets nothing of the shape is left its whole
    /// width, and one that meets it is held at the shape's furthest reach across the band (CSS Shapes 1
    /// §1.3, §2).
    /// </remarks>
    public (Double Left, Double Right) LineSpanAt(Double top, Double height)
    {
        return LineSpanAt(top, height, null);
    }

    /// <summary>
    /// Gets the span a line box standing between the two heights has left beside the floats, leaving out
    /// the floats that stand inside the box asking the question.
    /// </summary>
    /// <remarks>
    /// A box's own floated child narrows the lines the box holds - but it is not a float the box stands
    /// beside, and the room a box's marker hangs in is measured against the floats of the context, which
    /// is what a browser answers (CSS 2.1 §12.5.1 read with §9.5). A float that came down the tree with
    /// <see cref="Hold"/> keeps the box that placed it as its owner, so the question can be asked of both
    /// sets at once.
    /// </remarks>
    public (Double Left, Double Right) LineSpanAt(Double top, Double height, Box? notWithin)
    {
        var left = _edgeLeft;
        var right = _edgeRight;

        foreach (var track in Lefts)
        {
            if (StandsWithin(track, notWithin))
            {
                continue;
            }

            var edge = EdgeOf(track, top, height, left: true);

            if (edge > left)
            {
                left = Math.Min(edge, right);
            }
        }

        foreach (var track in Rights)
        {
            if (StandsWithin(track, notWithin))
            {
                continue;
            }

            var edge = EdgeOf(track, top, height, left: false);

            if (edge < right)
            {
                right = Math.Max(edge, left);
            }
        }

        return right < left ? (left, left) : (left, right);
    }

    /// <summary>
    /// Gets whether the float was placed by the box itself or by one of the boxes inside it, which is
    /// what makes it the box's own float rather than a float of the context it stands in.
    /// </summary>
    private static Boolean StandsWithin(FloatTrack track, Box? box)
    {
        if (track.Owner?.Element is not { } inner || box?.Element is not { } outer)
        {
            return false;
        }

        for (var node = inner; node is not null; node = node.ParentElement)
        {
            if (ReferenceEquals(node, outer))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Gets how far one float reaches into the band a line stands in, or where it stands at the float's own
    /// margin box edge where the float states no shape.
    /// </summary>
    private static Double EdgeOf(FloatTrack track, Double top, Double height, Boolean left)
    {
        if (track.Area is not { } area)
        {
            return Intersects(track.Box, top, height)
                ? left ? track.Box.Right : track.Box.X
                : left ? Double.NegativeInfinity : Double.PositiveInfinity;
        }

        return left
            ? area.Furthest(top, top + Math.Max(height, 0.001))
            : area.Nearest(top, top + Math.Max(height, 0.001));
    }

    /// <summary>
    /// Gets the height a box of <paramref name="inlineExtent"/> first stands clear of every float it
    /// is beside, stepping down from <paramref name="top"/> one blocking float at a time. A box that
    /// keeps its own formatting context may not overlap a float, so when the room left beside the
    /// floats is narrower than the box it has to drop until it fits - the same descent a float makes
    /// onto the next row, asked here for a box rather than a float. Returns <paramref name="top"/>
    /// unchanged once nothing below still blocks it, so a box wider than the container just overflows.
    /// </summary>
    /// <summary>
    /// Takes the floats of the context this set of tracks stands in, moved by the distance between the
    /// two frames, as the room its own floats and lines have to stand aside for as well.
    /// </summary>
    /// <remarks>
    /// Unlike <see cref="Absorb"/> this keeps what it was given at arm's length: the context's floats are
    /// nobody this container holds, so they are not handed on up the tree, and a box asking which floats
    /// stand beside <em>it</em> can tell the two sets apart.
    /// </remarks>
    public void Hold(FloatTracks? around, Double dx, Double dy)
    {
        if (around is null || ReferenceEquals(around, this))
        {
            return;
        }

        foreach (var track in around._left)
        {
            _aroundLeft.Add(Moved(track, dx, dy));
        }

        foreach (var track in around._right)
        {
            _aroundRight.Add(Moved(track, dx, dy));
        }

        foreach (var track in around._aroundLeft)
        {
            _aroundLeft.Add(Moved(track, dx, dy));
        }

        foreach (var track in around._aroundRight)
        {
            _aroundRight.Add(Moved(track, dx, dy));
        }
    }

    public Double FitBelow(Double top, Double inlineExtent)
    {
        var y = top;

        for (var attempt = 0; attempt < Lefts.Count() + Rights.Count(); attempt++)
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
            foreach (var track in Lefts)
            {
                below = Math.Max(below, track.Box.Bottom);
            }
        }

        if (side is ClearSide.Right or ClearSide.Both)
        {
            foreach (var track in Rights)
            {
                below = Math.Max(below, track.Box.Bottom);
            }
        }

        return below;
    }

    /// <summary>
    /// Places a float at the given edge and reserves the room it takes. The rectangle returned is the
    /// float's border box; the reservation covers its margin box, which is the area later floats and
    /// clearing boxes have to stay out of.
    /// </summary>
    /// <param name="area">
    /// Asked once the float's place is settled with its border box, for the area the float's own lines
    /// stand clear of. A float states one only where it asks for a shape, and the shape is measured from
    /// the boxes the placement leaves it - which is why it is asked here rather than handed in.
    /// </param>
    /// <param name="owner">
    /// The float's own box, kept so a later question can tell a float this container holds from a float it
    /// merely stands beside.
    /// </param>
    public Rect Place(FloatSide side, Double width, Double height, Thickness margin, Double top, Func<Rect, FloatArea?>? area = null, Box? owner = null)
    {
        var y = top + margin.Top;

        // The float's place is its margin box, so the room it asks for is the whole of that box and
        // the height it is compared against is where its margin box begins. The specification steps
        // down one line at a time; this steps to the bottom of the float that blocked it, which reaches
        // the same place whenever the band the float spans crosses one row of neighbours - and the rows
        // of a portal's columns never interleave.
        var outer = height + margin.Vertical;
        var blocking = Lefts.Count() + Rights.Count();

        for (var attempt = 0; attempt < blocking + 1; attempt++)
        {
            var (left, right) = FreeSpanAt(y - margin.Top, outer);
            var x = side == FloatSide.Left ? left + margin.Left : right - width - margin.Right;

            // What pushes a float down is another float it would really stand on, and nothing else: the
            // specification pins its outer edge against the containing block (§9.5.1 rule 1) and only asks
            // it to clear the floats (§9.5.1 rules 3 and 4). A float wider than the block it was put in
            // therefore overflows the block the way a browser lets one overflow, instead of hunting for a
            // deeper row beside the block's own edge - which is what the "other float outside rule 3"
            // family holds to.
            var across = FarEdgeAt(y - margin.Top, outer, side != FloatSide.Left);
            var fits = side == FloatSide.Left
                ? x + width <= across
                : x >= across;

            if (fits || attempt == blocking)
            {
                return Reserved(side, x, y, width, height, margin, area, owner);
            }

            var next = NextBottom(y - margin.Top, outer);

            if (next <= y - margin.Top)
            {
                break;
            }

            y = next + margin.Top;
        }

        // Nothing below the blocked heights had room either, which only happens when the float is
        // wider than the container: it keeps the place it was asked for and overflows the way a
        // browser lets one overflow, rather than being pushed into a track of its own.
        return Reserved(
            side,
            side == FloatSide.Left ? _edgeLeft : _edgeRight - width,
            y,
            width,
            height,
            margin,
            area,
            owner);
    }

    /// <summary>Records the float's margin box in the track it belongs to and hands back its border box.</summary>
    private Rect Reserved(FloatSide side, Double x, Double y, Double width, Double height, Thickness margin, Func<Rect, FloatArea?>? area, Box? owner)
    {
        var border = new Rect(x, y, width, height);

        var reserved = new Rect(
            x - margin.Left,
            y - margin.Top,
            margin.Left + width + margin.Right,
            margin.Top + height + margin.Bottom);

        (side == FloatSide.Left ? _left : _right).Add(new FloatTrack(
            reserved,
            area is null ? null : area(border),
            owner));

        return border;
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
            _left.Add(Moved(track, dx, dy));
        }

        foreach (var track in other._right)
        {
            _right.Add(Moved(track, dx, dy));
        }
    }

    private static FloatTrack Moved(FloatTrack track, Double dx, Double dy) =>
        new(
            new Rect(track.Box.X + dx, track.Box.Y + dy, track.Box.Width, track.Box.Height),
            track.Area?.Shifted(dx, dy),
            track.Owner);

    /// <summary>Gets whether the float stands in the band the box would occupy.</summary>
    private static Boolean Intersects(Rect box, Double top, Double height) =>
        box.Bottom > top && box.Y < top + Math.Max(height, 0.001);

    /// <summary>
    /// Gets the edge of the floats standing on the far side of the band - the edge a float placed on this
    /// side may not cross - with nothing but the floats themselves in the answer.
    /// </summary>
    /// <remarks>
    /// The container's own edge is not a float: it is where the rule starts the box from, and a box that
    /// is wider than the room it was given simply reaches past it. Reading the clamped span here would
    /// send such a float down the page looking for a row that its own width can never fit into.
    /// </remarks>
    private Double FarEdgeAt(Double top, Double height, Boolean lookLeft)
    {
        var edge = lookLeft ? Double.NegativeInfinity : Double.PositiveInfinity;

        foreach (var track in lookLeft ? Lefts : Rights)
        {
            var reach = EdgeOf(track, top, height, lookLeft);

            edge = lookLeft ? Math.Max(edge, reach) : Math.Min(edge, reach);
        }

        return double.IsInfinity(edge)
            ? lookLeft ? _edgeLeft : _edgeRight
            : edge;
    }

    /// <summary>
    /// Gets the lowest float bottom that would still block the band starting here. Stepping to it
    /// moves the placement down past at least one float, so the loop always makes progress.
    /// </summary>
    private Double NextBottom(Double top, Double height)
    {
        var next = Double.MaxValue;

        foreach (var track in Every)
        {
            if (Intersects(track.Box, top, height) && track.Box.Bottom > top && track.Box.Bottom < next)
            {
                next = track.Box.Bottom;
            }
        }

        return next == Double.MaxValue ? top : next;
    }
}

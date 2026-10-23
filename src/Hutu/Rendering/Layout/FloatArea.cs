using System;
using System.Collections.Generic;
using Hutu.Rendering.Paint;

namespace Hutu.Rendering.Layout;

/// <summary>
/// The area a float's lines have to stand clear of, kept as the closed edge that bounds it.
/// </summary>
/// <remarks>
/// A line box is shortened "as necessary to avoid intersections with the float area" (CSS Shapes 1 §1.3),
/// and what that asks of a non-rectangular area is the shape's furthest reach across the whole height the
/// line takes rather than at the one row its top falls on. Measured against Chrome on a left float whose
/// shape is a triangle widening downward - <c>polygon(0 0, 0 100px, 100px 100px)</c> over a block of
/// 20-pixel lines - the line standing at 0..20 begins at x=20 and the one at 20..40 at x=40, which is the
/// width at the band's further edge and not at its own.
/// <para>
/// The edge is a polyline because every form the grammar offers comes to one: a rectangle with four radii,
/// an oval, a polygon and a path all bound the same closed outline, and the furthest point of a band is on
/// the outline. A curve is turned into chords fine enough that the pixel a line is put at cannot move.
/// </para>
/// <para>
/// Two things are folded outside the outline. The area is cut to the float's margin box, so a shape can
/// only ever reduce that box and never grow it (CSS Shapes 1 §2) - which is why a line whose band meets
/// nothing of the shape runs straight through the box above it. And <c>shape-margin</c> stands the outline
/// outward by a distance: the smallest contour through every point that far out from the shape is the shape
/// swept by a disc of that radius, so a band is answered by the outline's reach over the band widened by
/// the distance, plus the distance itself (§6.3).
/// </para>
/// </remarks>
internal sealed class FloatArea
{
    /// <summary>How far a chord may lie inside the curve it stands for, in pixels.</summary>
    private const Double Tolerance = 0.01;

    /// <summary>How near a point has to be to a height to stand on it.</summary>
    private const Double Touch = 1e-6;

    private readonly Double[] _edge;

    private FloatArea(Double[] edge, Rect bounds, Double outset, Rect marginBox)
    {
        _edge = edge;
        Bounds = bounds;
        Outset = outset;
        MarginBox = marginBox;
    }

    /// <summary>Gets how far the outline reaches along each axis, before the margin or the cut.</summary>
    public Rect Bounds { get; }

    /// <summary>Gets how much further out than the outline the area stands.</summary>
    public Double Outset { get; }

    /// <summary>Gets the box the area is cut to, which is the float's own margin box.</summary>
    public Rect MarginBox { get; }

    /// <summary>
    /// Gets whether the outline bounds no height and is stood no distance out, which is an empty float area:
    /// a shape that is only a horizontal line has no effect on wrapping, while the same shape with a margin
    /// on it is swept into a stadium that does (CSS Shapes 1 §2, §6.3).
    /// </summary>
    public Boolean IsEmpty => Bounds.Height <= 0 && Outset <= 0;

    /// <summary>
    /// Gets the edge a line standing between the two heights has to begin past, for a float pinned to the
    /// left; negative infinity where the area reaches nothing of the band.
    /// </summary>
    public Double Furthest(Double top, Double bottom)
    {
        if (IsEmpty)
        {
            return Double.NegativeInfinity;
        }

        var (from, to) = Overlapped(top, bottom);

        if (to <= from)
        {
            return Double.NegativeInfinity;
        }

        var reach = Reach(from, to).Furthest;

        return reach < MarginBox.X
            ? Double.NegativeInfinity
            : Math.Min(reach, MarginBox.Right);
    }

    /// <summary>
    /// Gets the edge a line standing between the two heights has to stop before, for a float pinned to the
    /// right; positive infinity where the area reaches nothing of the band.
    /// </summary>
    public Double Nearest(Double top, Double bottom)
    {
        if (IsEmpty)
        {
            return Double.PositiveInfinity;
        }

        var (from, to) = Overlapped(top, bottom);

        if (to <= from)
        {
            return Double.PositiveInfinity;
        }

        var reach = Reach(from, to).Nearest;

        return reach > MarginBox.Right
            ? Double.PositiveInfinity
            : Math.Max(reach, MarginBox.X);
    }

    /// <summary>Gets the same area where the float it belongs to has been moved by the given distance.</summary>
    public FloatArea Shifted(Double dx, Double dy)
    {
        if (dx == 0 && dy == 0)
        {
            return this;
        }

        var edge = new Double[_edge.Length];

        for (var i = 0; i < edge.Length; i += 2)
        {
            edge[i] = _edge[i] + dx;
            edge[i + 1] = _edge[i + 1] + dy;
        }

        return new FloatArea(
            edge,
            new Rect(Bounds.X + dx, Bounds.Y + dy, Bounds.Width, Bounds.Height),
            Outset,
            new Rect(MarginBox.X + dx, MarginBox.Y + dy, MarginBox.Width, MarginBox.Height));
    }

    /// <summary>
    /// Gets the heights the outline is asked over: the band the line takes, cut to the part of the margin box
    /// the swept shape can reach.
    /// </summary>
    /// <remarks>
    /// The height the area covers is the shape's own, grown by the distance its margin sweeps it and cut back
    /// to the float's margin box. A band that lies wholly outside it meets nothing of the area; a band whose
    /// further edge is exactly where a shape with no margin begins encloses nothing of it either, which is
    /// the difference between touching a float area along one row of pixels and standing inside it (CSS Shapes
    /// 1 §1.3).
    /// </remarks>
    private (Double From, Double To) Overlapped(Double top, Double bottom) =>
        (Math.Max(top, Math.Max(MarginBox.Y, Bounds.Y - Outset)),
         Math.Min(bottom, Math.Min(MarginBox.Bottom, Bounds.Bottom + Outset)));

    /// <summary>Builds the area a float stands from the shape it came to and the box that shape is cut to.</summary>
    public static FloatArea? Of(ClipCut cut, Double outset, Rect marginBox)
    {
        var edge = OutlineOf(cut);

        if (edge.Count == 0)
        {
            return null;
        }

        // The outline closes on itself: the last edge is the one back to the first point, whether the shape
        // wrote one or not (CSS Shapes 1 §3.1).
        Add(edge, edge[0], edge[1]);

        return new FloatArea(edge.ToArray(), Enclosing(edge), Math.Max(0, outset), marginBox);
    }

    /// <summary>Builds the area of a box's own edge, with the curvature that box was given.</summary>
    public static FloatArea Of(Rect box, CornerRadii radii, Double outset, Rect marginBox) =>
        Of(new ClipCut(box, radii, null, false), outset, marginBox);

    /// <summary>Gets the closed outline of a cut, with every curve turned into the chords it is drawn with.</summary>
    private static List<Double> OutlineOf(ClipCut cut)
    {
        var edge = new List<Double>();

        if (cut.Outline is { } outline)
        {
            var beginX = 0.0;
            var beginY = 0.0;
            var x = 0.0;
            var y = 0.0;

            foreach (var segment in outline)
            {
                switch (segment.Edge)
                {
                    case ClipEdge.Begin:
                        x = segment.X;
                        y = segment.Y;
                        beginX = x;
                        beginY = y;
                        break;

                    case ClipEdge.Line:
                        x = segment.X;
                        y = segment.Y;
                        break;

                    case ClipEdge.Curve:
                        Flat(edge, x, y, segment.Control1X, segment.Control1Y,
                            segment.Control2X, segment.Control2Y, segment.X, segment.Y, 0);
                        x = segment.X;
                        y = segment.Y;
                        break;

                    default:
                        // A closed subpath returns to where it began.
                        x = beginX;
                        y = beginY;
                        continue;
                }

                Add(edge, x, y);
            }

            return edge;
        }

        // A rectangle with four radii is the outline of the box it names, each corner turned the way the
        // two axes of its radius say.
        var radii = cut.Radii;
        var box = cut.Bounds;

        Add(edge, box.X + radii.TopLeft.Horizontal, box.Y);
        Add(edge, box.Right - radii.TopRight.Horizontal, box.Y);
        Corner(edge, box.Right - radii.TopRight.Horizontal, box.Y + radii.TopRight.Vertical,
            radii.TopRight.Horizontal, radii.TopRight.Vertical, Math.PI * 1.5, Math.PI * 2);
        Add(edge, box.Right, box.Y + radii.TopRight.Vertical);
        Add(edge, box.Right, box.Bottom - radii.BottomRight.Vertical);
        Corner(edge, box.Right - radii.BottomRight.Horizontal, box.Bottom - radii.BottomRight.Vertical,
            radii.BottomRight.Horizontal, radii.BottomRight.Vertical, 0, Math.PI * 0.5);
        Add(edge, box.Right - radii.BottomRight.Horizontal, box.Bottom);
        Add(edge, box.X + radii.BottomLeft.Horizontal, box.Bottom);
        Corner(edge, box.X + radii.BottomLeft.Horizontal, box.Bottom - radii.BottomLeft.Vertical,
            radii.BottomLeft.Horizontal, radii.BottomLeft.Vertical, Math.PI * 0.5, Math.PI);
        Add(edge, box.X, box.Bottom - radii.BottomLeft.Vertical);
        Add(edge, box.X, box.Y + radii.TopLeft.Vertical);
        Corner(edge, box.X + radii.TopLeft.Horizontal, box.Y + radii.TopLeft.Vertical,
            radii.TopLeft.Horizontal, radii.TopLeft.Vertical, Math.PI, Math.PI * 1.5);

        return edge;
    }

    /// <summary>Adds the quarter of an ellipse that turns one corner, ending on the edge it turns into.</summary>
    private static void Corner(List<Double> edge, Double centerX, Double centerY, Double rx, Double ry, Double from, Double to)
    {
        if (rx <= 0 || ry <= 0)
        {
            return;
        }

        // The chords are stepped so that none lies more than the tolerance inside the curve: the sag of a
        // chord over a turn of dtheta is about the radius times dtheta squared over eight, and the longer
        // of the two radii is the one that sets the bound.
        var sag = 8 * Tolerance / Math.Max(rx, ry);
        var steps = Math.Max(2, (Int32)Math.Ceiling(Math.PI * 0.5 / Math.Sqrt(sag)));

        for (var step = 1; step <= steps; step++)
        {
            var angle = from + (to - from) * step / steps;

            Add(edge, centerX + rx * Math.Cos(angle), centerY + ry * Math.Sin(angle));
        }
    }

    /// <summary>
    /// Adds a cubic as the chords its own flatness allows, ending on the point the curve reaches.
    /// </summary>
    /// <param name="depth">
    /// How many times the curve has been halved already, which is where a curve that will not flatten - a
    /// control point on top of its own end - is stopped from halving for ever.
    /// </param>
    private static void Flat(
        List<Double> edge,
        Double x0,
        Double y0,
        Double c1x,
        Double c1y,
        Double c2x,
        Double c2y,
        Double x3,
        Double y3,
        Int32 depth)
    {
        var off1 = Deviation(x0, y0, x3, y3, c1x, c1y);
        var off2 = Deviation(x0, y0, x3, y3, c2x, c2y);

        if (depth >= 24 || (off1 <= Tolerance && off2 <= Tolerance))
        {
            Add(edge, x3, y3);
            return;
        }

        // De Casteljau's split at the halfway point, so that the two halves meet on the curve.
        var l1x = (x0 + c1x) / 2;
        var l1y = (y0 + c1y) / 2;
        var m1x = (c1x + c2x) / 2;
        var m1y = (c1y + c2y) / 2;
        var r1x = (c2x + x3) / 2;
        var r1y = (c2y + y3) / 2;
        var l2x = (l1x + m1x) / 2;
        var l2y = (l1y + m1y) / 2;
        var r2x = (m1x + r1x) / 2;
        var r2y = (m1y + r1y) / 2;
        var mid = ((l2x + r2x) / 2, (l2y + r2y) / 2);

        Flat(edge, x0, y0, l1x, l1y, l2x, l2y, mid.Item1, mid.Item2, depth + 1);
        Flat(edge, mid.Item1, mid.Item2, r2x, r2y, r1x, r1y, x3, y3, depth + 1);
    }

    /// <summary>Gets how far a point stands from the chord drawn between two others.</summary>
    private static Double Deviation(Double x, Double y, Double toX, Double toY, Double atX, Double atY)
    {
        var dx = toX - x;
        var dy = toY - y;
        var length = Math.Sqrt(dx * dx + dy * dy);

        return length <= 0
            ? Math.Sqrt((atX - x) * (atX - x) + (atY - y) * (atY - y))
            : Math.Abs(dy * (atX - x) - dx * (atY - y)) / length;
    }

    private static void Add(List<Double> edge, Double x, Double y)
    {
        if (edge.Count >= 2 && Math.Abs(edge[^2] - x) <= 0.0001 && Math.Abs(edge[^1] - y) <= 0.0001)
        {
            return;
        }

        edge.Add(x);
        edge.Add(y);
    }

    private static Rect Enclosing(List<Double> edge)
    {
        var left = Double.PositiveInfinity;
        var right = Double.NegativeInfinity;
        var top = Double.PositiveInfinity;
        var bottom = Double.NegativeInfinity;

        for (var i = 0; i < edge.Count; i += 2)
        {
            left = Math.Min(left, edge[i]);
            right = Math.Max(right, edge[i]);
            top = Math.Min(top, edge[i + 1]);
            bottom = Math.Max(bottom, edge[i + 1]);
        }

        return new Rect(left, top, right - left, bottom - top);
    }

    /// <summary>
    /// Gets how far the outline reaches along the inline axis between two heights, at both of its sides.
    /// </summary>
    /// <remarks>
    /// The extent of a straight edge over a band is at one of its ends or where the band's own bounds cut
    /// it, so those are the only places asked of every edge - which is what lets a band that begins in the
    /// middle of an edge still see the whole of it, and a shape whose outline runs along the band's edge
    /// still count.
    /// </remarks>
    private (Double Furthest, Double Nearest) Reach(Double top, Double bottom)
    {
        var furthest = Double.NegativeInfinity;
        var nearest = Double.PositiveInfinity;
        var count = _edge.Length / 2;

        for (var step = 0; step < count; step++)
        {
            var x = _edge[step * 2];
            var y = _edge[step * 2 + 1];

            var next = (step + 1) % count;
            var toX = _edge[next * 2];
            var toY = _edge[next * 2 + 1];
            var previous = (step + count - 1) % count;
            var fromY = _edge[previous * 2 + 1];

            // A point standing exactly where the band begins counts only where an edge runs on down from
            // it: the outer corner of a step at the band's top row belongs to the step above, and the
            // line that opens on that row stands beside the step below it (CSS Shapes 1 §3.1).
            var corner = Math.Abs(y - top) <= Touch && toY <= top + Touch && fromY <= top + Touch;

            // A point of the outline a distance away from the band still reaches it, but only as far along
            // the inline axis as the circle of the shape's own margin drawn about it allows.
            var away = y < top ? top - y : y > bottom ? y - bottom : 0;

            if (!corner && away <= Outset)
            {
                var reach = Math.Sqrt(Outset * Outset - away * away);

                furthest = Math.Max(furthest, x + reach);
                nearest = Math.Min(nearest, x - reach);
            }

            // And where an edge crosses one of the two heights the band is bounded by, the outline is at
            // that height itself, so the whole of the margin lies along the inline axis there. A crossing
            // at the top counts only where the edge passes on through it: one that merely ends there is
            // the corner answered above.
            var crossing = Crossing(x, y, toX, toY, top);

            if (!Double.IsNegativeInfinity(crossing) &&
                Math.Abs(y - top) > Touch && Math.Abs(toY - top) > Touch)
            {
                furthest = Math.Max(furthest, crossing + Outset);
                nearest = Math.Min(nearest, crossing - Outset);
            }

            crossing = Crossing(x, y, toX, toY, bottom);

            if (!Double.IsNegativeInfinity(crossing))
            {
                furthest = Math.Max(furthest, crossing + Outset);
                nearest = Math.Min(nearest, crossing - Outset);
            }
        }

        return (furthest, nearest);
    }

    /// <summary>
    /// Gets where the edge from one point of the outline to the next crosses a height, or nothing where it
    /// does not cross it.
    /// </summary>
    private static Double Crossing(Double x, Double y, Double toX, Double toY, Double at)
    {
        if (y == toY)
        {
            // A horizontal edge lies along a height or away from it, and its two ends carry the answer.
            return Double.NegativeInfinity;
        }

        var share = (at - y) / (toY - y);

        return share < 0 || share > 1 ? Double.NegativeInfinity : x + (toX - x) * share;
    }
}

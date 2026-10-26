using System;
using System.Collections.Generic;
using Hutu.Rendering.Layout;
using Hutu.Rendering.Paint;

namespace Hutu.Rendering;

/// <summary>Which of the shapes <c>clip-path</c> was handed.</summary>
internal enum ClipForm
{
    /// <summary>The page named a box and nothing else: the clip is that box.</summary>
    Box,

    /// <summary><c>inset()</c>, <c>rect()</c> or <c>xywh()</c>: a rectangle, possibly with round corners.</summary>
    Rectangle,

    /// <summary><c>circle()</c> or <c>ellipse()</c>.</summary>
    Ellipse,

    /// <summary><c>polygon()</c>.</summary>
    Polygon,

    /// <summary>
    /// A box whose four corners are cut straight between the ends of their radii, which is what
    /// <c>corner-shape: bevel</c> asks for (CSS Borders 4 §4.1).
    /// </summary>
    Bevel,

    /// <summary><c>path()</c>, whose own coordinates start at the reference box's top left corner.</summary>
    Path,

    /// <summary><c>shape()</c>, whose commands are resolved against the reference box when it is cut.</summary>
    Shape,
}

/// <summary>The box a shape measures itself against, as the page named it.</summary>
internal enum ClipBox
{
    Border,
    Padding,
    Content,
    Margin,

    /// <summary>An SVG term: for a box the layout made, it answers the content box (CSS Masking 1 §5.1).</summary>
    Fill,

    /// <summary>As above, answering the border box.</summary>
    Stroke,

    /// <summary>As above, answering the border box.</summary>
    View,
}

/// <summary>How far a <c>circle()</c> or an <c>ellipse()</c> reaches where the page named no radius.</summary>
internal enum ClipReach
{
    ClosestSide,
    FarthestSide,
    ClosestCorner,
    FarthestCorner,
}

/// <summary>
/// The four boxes a <c>clip-path</c> can be told to measure itself against, as the layout settled them.
/// </summary>
internal readonly record struct ClipBoxes(Rect Border, Rect Padding, Rect Content, Rect Margin);

/// <summary>
/// The area a <c>clip-path</c> came to on the box it was given, with every number folded into the pixels the
/// paint list is written in. A shape that is a rectangle - and an oval is, with its four corners each turned
/// a quarter of the way round - needs no outline: the cut the drawing backend makes from a rectangle and
/// four radii is the same cut.
/// </summary>
internal readonly record struct ClipCut(
    Rect Bounds,
    CornerRadii Radii,
    IReadOnlyList<ClipSegment>? Outline,
    Boolean EvenOdd);

/// <summary>
/// What a <c>clip-path</c> states, with its numbers still in the units the page wrote them in.
/// </summary>
/// <remarks>
/// A percentage is kept as a percentage because it is a share of the reference box, and the reference box is
/// only known when the box is painted: what the page wrote and what the box turned out to be are kept apart
/// until <see cref="Cut"/> brings them together.
/// </remarks>
internal sealed partial class ClipPathShape
{
    /// <summary>Gets which of the shapes the value was.</summary>
    public required ClipForm Form { get; init; }

    /// <summary>Gets the box the shape measures itself against; the border box unless the page said otherwise.</summary>
    public ClipBox Box { get; init; } = ClipBox.Border;

    /// <summary>Gets whether the shape's inside is decided by the even-odd rule rather than by winding.</summary>
    public Boolean EvenOdd { get; init; }

    /// <summary>
    /// Gets the four distances a rectangle is inset by, in the order <c>inset()</c> names them - or the four
    /// edges themselves where <see cref="SidesAreEdges"/> says the page wrote <c>rect()</c>.
    /// </summary>
    public StyleLength InsetTop { get; set; }

    public StyleLength InsetRight { get; set; }

    public StyleLength InsetBottom { get; set; }

    public StyleLength InsetLeft { get; set; }

    /// <summary>Gets whether the four terms name the edges of the rectangle rather than its insets.</summary>
    public Boolean SidesAreEdges { get; set; }

    /// <summary>Gets whether the four terms name the rectangle's own place and size, as <c>xywh()</c> does.</summary>
    public Boolean PlacedBySize { get; set; }

    /// <summary>Gets where the rectangle stands and how far it runs, where <c>xywh()</c> wrote it that way.</summary>
    public StyleLength OffsetX { get; set; }

    public StyleLength OffsetY { get; set; }

    public StyleLength ExtentX { get; set; }

    public StyleLength ExtentY { get; set; }

    /// <summary>Gets the corners a rectangle was asked to round off.</summary>
    public StyleCornerRadii Corners { get; set; } = StyleCornerRadii.None;

    /// <summary>
    /// Gets the radius the page wrote, per axis, or null where it named a reach instead. A circle writes one
    /// and leaves the other axis to it.
    /// </summary>
    public StyleLength? RadiusHorizontal { get; set; }

    public StyleLength? RadiusVertical { get; set; }

    /// <summary>Gets the reach the page named instead of a radius, per axis.</summary>
    public ClipReach? ReachHorizontal { get; set; }

    public ClipReach? ReachVertical { get; set; }

    /// <summary>Gets whether one radius stands for both axes, which is what a circle is.</summary>
    public Boolean OneRadius { get; set; }

    /// <summary>Gets where the circle or ellipse is centred, a keyword having been folded to a share.</summary>
    public StyleLength CenterX { get; set; } = StyleLength.FromPercent(0.5);

    public StyleLength CenterY { get; set; } = StyleLength.FromPercent(0.5);

    /// <summary>Gets a polygon's points, flattened x, y, x, y..., each still in its own unit.</summary>
    public StyleLength[] Points { get; set; } = Array.Empty<StyleLength>();

    /// <summary>Gets the edge of a <c>path()</c>, in the pixels the string itself wrote.</summary>
    public IReadOnlyList<ClipSegment>? Segments { get; set; }

    /// <summary>Gets the commands of a <c>shape()</c>, with their lengths still unresolved.</summary>
    public IReadOnlyList<ShapeCommand>? ShapeCommands { get; set; }

    /// <summary>
    /// Works out what the shape covers on the box it was given, and how the ink is to be cut to it.
    /// </summary>
    public ClipCut Cut(ClipBoxes boxes)
    {
        var reference = Of(boxes);

        return Form switch
        {
            ClipForm.Box => new ClipCut(reference, CornerRadii.None, null, false),
            ClipForm.Rectangle => RectangleOf(reference),
            ClipForm.Ellipse => EllipseOf(reference),
            ClipForm.Polygon => PolygonOf(reference),
            ClipForm.Bevel => BevelOf(reference),
            ClipForm.Shape => ShapeOf(reference),
            _ => PathOf(reference),
        };
    }

    /// <summary>
    /// Gets the box the shape measures itself against, in the coordinates the caller works in. A float's
    /// area needs this on its own: where a clip cuts the whole of the shape, an area is cut to the box the
    /// float already took, and the two are asked of the same four boxes at two different times.
    /// </summary>
    public Rect ReferenceOf(ClipBoxes boxes) => Of(boxes);

    /// <summary>Gets the box the page named, in the coordinates the paint list is drawn in.</summary>
    private Rect Of(ClipBoxes boxes) =>
        Box switch
        {
            ClipBox.Content or ClipBox.Fill => boxes.Content,
            ClipBox.Padding => boxes.Padding,
            ClipBox.Margin => boxes.Margin,
            _ => boxes.Border,
        };

    private ClipCut RectangleOf(Rect reference)
    {
        Rect area;

        if (PlacedBySize)
        {
            var left = OffsetX.Resolve(reference.Width);
            var top = OffsetY.Resolve(reference.Height);

            area = new Rect(
                reference.X + left,
                reference.Y + top,
                Math.Max(0, ExtentX.Resolve(reference.Width)),
                Math.Max(0, ExtentY.Resolve(reference.Height)));
        }
        else if (SidesAreEdges)
        {
            // `rect()` names the edges themselves rather than the distance in from each of them, and
            // `auto` leaves an edge where the reference box already has it (CSS Shapes 1 §3.1).
            var left = Side(InsetLeft, 0, reference.Width);
            var top = Side(InsetTop, 0, reference.Height);
            var right = Side(InsetRight, reference.Width, reference.Width);
            var bottom = Side(InsetBottom, reference.Height, reference.Height);

            area = new Rect(
                reference.X + left,
                reference.Y + top,
                Math.Max(0, right - left),
                Math.Max(0, bottom - top));
        }
        else
        {
            var top = InsetTop.Resolve(reference.Height);
            var bottom = InsetBottom.Resolve(reference.Height);
            var left = InsetLeft.Resolve(reference.Width);
            var right = InsetRight.Resolve(reference.Width);

            // Insets that would pass each other are brought back in the proportion the two of them were
            // written in, so that the pair sums to the axis and each keeps the share of it the page asked
            // for (CSS Shapes 1 §3.1, reading CSS Backgrounds 3 §4.5).
            (top, bottom) = Share(top, bottom, reference.Height);
            (left, right) = Share(left, right, reference.Width);

            area = new Rect(
                reference.X + left,
                reference.Y + top,
                Math.Max(0, reference.Width - left - right),
                Math.Max(0, reference.Height - top - bottom));
        }

        return new ClipCut(area, CornersOf(area, reference), null, false);
    }

    /// <summary>
    /// Gets the corners as the box they round off wants them: a percentage is a share of the reference box,
    /// while the rule that keeps two corners from meeting past the side they share is measured against the
    /// rectangle the shape came to.
    /// </summary>
    private CornerRadii CornersOf(Rect area, Rect reference)
    {
        if (Corners.Equals(StyleCornerRadii.None))
        {
            return CornerRadii.None;
        }

        return CornerRadii.Fit(
            new CornerRadii(
                CornerRadii.Resolve(Corners.TopLeft, reference.Width, reference.Height),
                CornerRadii.Resolve(Corners.TopRight, reference.Width, reference.Height),
                CornerRadii.Resolve(Corners.BottomRight, reference.Width, reference.Height),
                CornerRadii.Resolve(Corners.BottomLeft, reference.Width, reference.Height)),
            area.Width,
            area.Height);
    }

    private static (Double First, Double Second) Share(Double first, Double second, Double axis)
    {
        var sum = first + second;

        return sum <= axis || sum <= 0 ? (first, second) : (first * axis / sum, second * axis / sum);
    }

    private static Double Side(StyleLength term, Double automatic, Double axis) =>
        term.IsAuto ? automatic : term.Resolve(axis);

    private ClipCut EllipseOf(Rect reference)
    {
        var centerX = reference.X + CenterX.Resolve(reference.Width);
        var centerY = reference.Y + CenterY.Resolve(reference.Height);

        // A circle is one radius for both axes, and a percentage of it is a share of the box measured
        // diagonally (CSS Shapes 1 §3.1) - the same number as the side of a square box, and a different one
        // from either axis on any other.
        var diagonal = Math.Sqrt((reference.Width * reference.Width + reference.Height * reference.Height) / 2);

        var horizontal = RadiusOf(centerX, centerY, reference, OneRadius ? diagonal : reference.Width, axis: true, both: OneRadius);
        var vertical = OneRadius ? horizontal : RadiusOf(centerX, centerY, reference, reference.Height, axis: false, both: false);

        var area = new Rect(centerX - horizontal, centerY - vertical, horizontal * 2, vertical * 2);

        var corner = new CornerRadius(horizontal, vertical);

        return new ClipCut(area, new CornerRadii(corner, corner, corner, corner), null, false);
    }

    /// <summary>
    /// Gets how far the shape reaches along one axis, from the radius the page wrote or from the edge or
    /// corner it named instead of a radius.
    /// </summary>
    /// <param name="both">
    /// Whether the axis is a circle's, which reaches the same distance in every direction and so answers a
    /// named edge or corner out of all four of them at once.
    /// </param>
    private Double RadiusOf(
        Double centerX,
        Double centerY,
        Rect reference,
        Double basis,
        Boolean axis,
        Boolean both)
    {
        var written = axis ? RadiusHorizontal : RadiusVertical;

        if (written is { } length)
        {
            return Math.Max(0, length.Resolve(basis));
        }

        var reach = (axis ? ReachHorizontal : ReachVertical) ??
                    (OneRadius ? ClipReach.FarthestCorner : ClipReach.FarthestSide);

        var start = axis ? centerX - reference.X : centerY - reference.Y;
        var end = axis ? reference.Right - centerX : reference.Bottom - centerY;

        if (!both)
        {
            return reach switch
            {
                ClipReach.ClosestSide or ClipReach.ClosestCorner => Math.Max(0, Math.Min(start, end)),
                ClipReach.FarthestSide or ClipReach.FarthestCorner => Math.Max(start, end),
            };
        }

        var up = centerY - reference.Y;
        var down = reference.Bottom - centerY;

        return reach switch
        {
            ClipReach.ClosestSide => Math.Max(0, Math.Min(Math.Min(start, end), Math.Min(up, down))),
            ClipReach.FarthestSide => Math.Max(Math.Max(start, end), Math.Max(up, down)),
            ClipReach.ClosestCorner => Corner(centerX, centerY, reference, farthest: false),
            _ => Corner(centerX, centerY, reference, farthest: true),
        };
    }

    /// <summary>Gets the distance from the centre to the nearest or the farthest corner of the box.</summary>
    private static Double Corner(Double centerX, Double centerY, Rect reference, Boolean farthest)
    {
        var best = farthest ? 0.0 : Double.PositiveInfinity;

        foreach (var dx in new[] { reference.X - centerX, reference.Right - centerX })
        {
            foreach (var dy in new[] { reference.Y - centerY, reference.Bottom - centerY })
            {
                var distance = Math.Sqrt(dx * dx + dy * dy);

                best = farthest ? Math.Max(best, distance) : Math.Min(best, distance);
            }
        }

        return best;
    }

    /// <summary>
    /// Gets the outline a beveled box comes to: each corner is the straight line between the two ends
    /// of its radius, and a corner with no radius stays square.
    /// </summary>
    private ClipCut BevelOf(Rect reference)
    {
        var radii = CornerRadii.Resolve(Corners, reference.Width, reference.Height);
        var edges = new List<ClipSegment>(8);

        void Point(Double x, Double y) =>
            edges.Add(ClipSegment.To(edges.Count == 0 ? ClipEdge.Begin : ClipEdge.Line, x, y));

        Point(reference.X + radii.TopLeft.Horizontal, reference.Y);
        Point(reference.Right - radii.TopRight.Horizontal, reference.Y);
        Point(reference.Right, reference.Y + radii.TopRight.Vertical);
        Point(reference.Right, reference.Bottom - radii.BottomRight.Vertical);
        Point(reference.Right - radii.BottomRight.Horizontal, reference.Bottom);
        Point(reference.X + radii.BottomLeft.Horizontal, reference.Bottom);
        Point(reference.X, reference.Bottom - radii.BottomLeft.Vertical);
        Point(reference.X, reference.Y + radii.TopLeft.Vertical);

        return new ClipCut(reference, CornerRadii.None, edges, false);
    }

    private ClipCut PolygonOf(Rect reference)
    {
        var edges = new List<ClipSegment>(Points.Length / 2 + 1);
        var left = Double.PositiveInfinity;
        var top = Double.PositiveInfinity;
        var right = Double.NegativeInfinity;
        var bottom = Double.NegativeInfinity;

        for (var index = 0; index + 1 < Points.Length; index += 2)
        {
            var x = reference.X + Points[index].Resolve(reference.Width);
            var y = reference.Y + Points[index + 1].Resolve(reference.Height);

            left = Math.Min(left, x);
            right = Math.Max(right, x);
            top = Math.Min(top, y);
            bottom = Math.Max(bottom, y);

            edges.Add(ClipSegment.To(index == 0 ? ClipEdge.Begin : ClipEdge.Line, x, y));
        }

        edges.Add(ClipSegment.Closed);

        return new ClipCut(
            new Rect(left, top, right - left, bottom - top),
            CornerRadii.None,
            edges,
            EvenOdd);
    }

    private ClipCut PathOf(Rect reference)
    {
        var outline = new List<ClipSegment>(Segments!.Count);
        var left = Double.PositiveInfinity;
        var top = Double.PositiveInfinity;
        var right = Double.NegativeInfinity;
        var bottom = Double.NegativeInfinity;

        // The string was written against a box whose top left corner is (0, 0), so the reference box's own
        // corner is what moves the edge onto the page.
        foreach (var piece in Segments)
        {
            outline.Add(piece.Shifted(reference.X, reference.Y));

            if (piece.Edge != ClipEdge.Close)
            {
                left = Math.Min(left, reference.X + piece.X);
                right = Math.Max(right, reference.X + piece.X);
                top = Math.Min(top, reference.Y + piece.Y);
                bottom = Math.Max(bottom, reference.Y + piece.Y);
            }
        }

        return new ClipCut(
            new Rect(left, top, right - left, bottom - top),
            CornerRadii.None,
            outline,
            EvenOdd);
    }

    /// <summary>Gets the box named by a value that is nothing but a box; null where it names a shape.</summary>
    internal static ClipBox? BoxNamed(String value) => BoxNamed(value, shapesOnly: false);

    /// <summary>
    /// Gets the box a value named, read either by the grammar of masking - which adds the three boxes an
    /// SVG element answers - or by that of a shape, whose <c>&lt;shape-box&gt;</c> is the four CSS boxes and
    /// no others (CSS Shapes 1 §5).
    /// </summary>
    internal static ClipBox? BoxNamed(String value, Boolean shapesOnly)
    {
        var box = value.ToLowerInvariant() switch
        {
            "border-box" => ClipBox.Border,
            "padding-box" => ClipBox.Padding,
            "content-box" => ClipBox.Content,
            "margin-box" => ClipBox.Margin,
            "fill-box" => ClipBox.Fill,
            "stroke-box" => ClipBox.Stroke,
            "view-box" => ClipBox.View,
            _ => (ClipBox?)null,
        };

        return box is { } found && shapesOnly && found is ClipBox.Fill or ClipBox.Stroke or ClipBox.View
            ? null
            : box;
    }
}

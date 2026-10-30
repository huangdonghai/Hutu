using Hutu.Rendering;
using Hutu.Rendering.Layout;
using Hutu.Rendering.Media;
using Hutu.Rendering.Paint;
using SkiaSharp;
using Svg.Skia;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Hutu.Skia;

/// <summary>
/// Executes a <see cref="PaintList"/> against an <see cref="SKCanvas"/>.
/// </summary>
/// <remarks>
/// The executor borrows its fonts from the measurer, so a run is drawn with exactly the font it was
/// measured with - measuring and painting disagreeing is the classic source of wrapped text that
/// overflows its line box.
/// </remarks>
public sealed class SkiaPaintExecutor
{
    private readonly SkiaTextMeasurer _measurer;
    private readonly SkiaImageLoader? _images;

    /// <summary>Parsed inline-SVG pictures, keyed by markup; a null is one that failed to parse.</summary>
    private readonly Dictionary<String, SKSvg?> _svg = new(StringComparer.Ordinal);

    /// <param name="images">
    /// The loader that decoded the document's images. Without it image commands are skipped, which
    /// is the right degradation for a host that does not render images.
    /// </param>
    public SkiaPaintExecutor(SkiaTextMeasurer measurer, SkiaImageLoader? images = null)
    {
        _measurer = measurer;
        _images = images;
    }

    /// <summary>
    /// Draws one inline SVG: parses its markup to a picture (cached, since the same icon repeats many
    /// times) and maps the picture's own coordinate space onto the box, so it stays crisp at any size.
    /// </summary>
    private void DrawSvg(SKCanvas canvas, SvgCommand command)
    {
        if (!_svg.TryGetValue(command.Markup, out var svg))
        {
            svg = ParseSvg(command.Markup);
            _svg[command.Markup] = svg;
        }

        if (svg?.Picture is not { } picture)
        {
            return;
        }

        var bounds = picture.CullRect;
        var target = ToRect(command.Bounds);

        if (bounds.Width <= 0 || bounds.Height <= 0 || target.Width <= 0 || target.Height <= 0)
        {
            return;
        }

        canvas.Save();
        SkiaRounded.Clip(canvas, target, command.Radii);
        canvas.Translate(target.Left, target.Top);
        canvas.Scale(target.Width / bounds.Width, target.Height / bounds.Height);
        canvas.Translate(-bounds.Left, -bounds.Top);
        canvas.DrawPicture(picture);
        canvas.Restore();
    }

    private static SKSvg? ParseSvg(String markup)
    {
        var svg = new SKSvg();

        try
        {
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(markup));

            if (svg.Load(stream) is not null && svg.Picture is not null)
            {
                return svg;
            }
        }
        catch (Exception)
        {
            // A graphic the renderer cannot parse is skipped, never fatal to the page.
        }

        svg.Dispose();
        return null;
    }

    /// <param name="scroll">
    /// How far the content is scrolled. It is what keeps a fixed box on screen and pins a sticky one
    /// to its edge; a host that does not scroll leaves it at zero.
    /// </param>
    public void Execute(PaintList list, SKCanvas canvas, Offset scroll = default)
    {
        // Read before anything below is drawn. The clip is a fact about this canvas as it stands, and
        // the translations the sticky and fixed lists are drawn under move it: the same rectangle read
        // afterwards would be that much further along, and the culling would be out by exactly the
        // shift it is meant to know nothing about.
        var area = VisibleArea(canvas);

        canvas.Clear(SKColors.White);

        Draw(canvas, list.Commands, area, default);

        // A sticky box is pinned to an edge of the scrollport, so it is drawn shifted by whatever its
        // constraint asks for at this scroll position.
        foreach (var layer in list.Sticky)
        {
            var shift = layer.Constraint.Shift(scroll);

            canvas.Save();
            canvas.Translate((Single)shift.X, (Single)shift.Y);
            Draw(canvas, layer.Commands, area, shift);
            canvas.Restore();
        }

        // A fixed box is already in viewport coordinates, so cancelling the scroll keeps it in place.
        if (list.FixedCommands.Count > 0)
        {
            canvas.Save();
            canvas.Translate((Single)scroll.X, (Single)scroll.Y);
            Draw(canvas, list.FixedCommands, area, scroll);
            canvas.Restore();
        }
    }

    /// <summary>
    /// Gets the part of the document the screen can show, which is what the canvas' own clip says, in
    /// the coordinates the commands are drawn in - the space the clip is kept in.
    /// </summary>
    /// <remarks>
    /// This is the answer a host does not have to work out and cannot get wrong. How the content was
    /// moved onto the screen is a fact of the moment that lives in the canvas' transform - a scroll
    /// container moves a control by moving the canvas rather than by arranging it again - and the clip
    /// is where that movement ended, so it is also where the reader is. A host that clips nothing gets
    /// the whole surface back, which culls nothing: the direction a wrong answer has to fall in.
    /// <para>
    /// It is public for the reason <see cref="IsVisible"/> is: it is the half of the rule worth asking
    /// about on its own, without a picture to read the answer off.
    /// </para>
    /// </remarks>
    public static Rect VisibleArea(SKCanvas canvas)
    {
        var clip = canvas.LocalClipBounds;

        return new Rect(clip.Left, clip.Top, clip.Width, clip.Height);
    }

    /// <summary>
    /// Draws the commands, skipping the ones that cannot reach <paramref name="area"/>.
    /// </summary>
    /// <param name="area">
    /// The part of the document the screen can show, in document coordinates - the same one for all
    /// three lists, because it is a fact about the canvas rather than about what is being drawn on it.
    /// </param>
    /// <param name="offset">
    /// How far these commands are translated as they are drawn, which is what puts a command's own
    /// rectangle into document coordinates: the three lists of a document are drawn under three
    /// different translations, and a rectangle means nothing until the one it belongs to is added.
    /// </param>
    private void Draw(SKCanvas canvas, List<PaintCommand> commands, Rect area, Offset offset)
    {
        foreach (var command in commands)
        {
            if (!IsVisible(command, offset, area))
            {
                continue;
            }

            switch (command)
            {
                case FillCommand fill:
                    Fill(canvas, fill);
                    break;

                case BoxShadowCommand shadow:
                    DrawBoxShadow(canvas, shadow);
                    break;

                case BorderCommand border:
                    DrawBorder(canvas, border);
                    break;

                case OutlineCommand outline:
                    DrawOutline(canvas, outline);
                    break;

                case ImageCommand image:
                    _images?.Draw(canvas, image.Url, ToRect(image.Bounds), image.Fit, image.Radii);
                    break;

                case SvgCommand svg:
                    DrawSvg(canvas, svg);
                    break;

                case BackgroundImageCommand background:
                    _images?.DrawBackground(canvas, background.Layer, ToRect(background.Placement), ToRect(background.Bounds), background.Radii);
                    break;

                case GradientCommand gradient:
                    DrawGradient(canvas, gradient);
                    break;

                case BorderImageCommand border:
                    DrawBorderImage(canvas, border);
                    break;

                case TextClippedBackgroundCommand clipped:
                    DrawTextClippedBackground(canvas, clipped);
                    break;

                case PushClipCommand clip:
                    canvas.Save();

                    if (clip.Outline is { } edge)
                    {
                        // An edge that is not a box's own is drawn as it was given, and anti-aliased: the
                        // cut a clip-path asks for falls where the shape says it falls, not on a pixel.
                        using (var path = OutlineOf(edge, clip.EvenOdd))
                        {
                            canvas.ClipPath(path, SKClipOperation.Intersect, antialias: true);
                        }
                    }
                    else
                    {
                        // A square clip cuts whole pixels, on the same terms as the fill that is cut by it:
                        // an edge left inside a pixel row anti-aliases the row instead, and a box drawn at a
                        // fractional edge then shows the ink it stands over through the seam the clip leaves.
                        SkiaRounded.Clip(
                            canvas,
                            clip.Radii.IsZero ? Snap(ToRect(clip.Bounds)) : ToRect(clip.Bounds),
                            clip.Radii);
                    }

                    break;

                case PopClipCommand:
                    canvas.Restore();
                    break;

                case PushTransformCommand transform:
                    // Paint about the origin: shift it to the page corner, apply the matrix, shift back.
                    canvas.Save();
                    canvas.Translate((Single)transform.Origin.X, (Single)transform.Origin.Y);
                    canvas.Concat(ToSkMatrix(transform.Matrix));
                    canvas.Translate(-(Single)transform.Origin.X, -(Single)transform.Origin.Y);
                    break;

                case PopTransformCommand:
                    canvas.Restore();
                    break;

                case PushLayerCommand layer:
                    // The subtree is drawn into the layer untouched and the layer is then laid down at
                    // that strength as one picture, which is what keeps two translucent children from
                    // darkening each other where they overlap. Skia copies the paint when the layer is
                    // opened, so the paint does not have to outlive the call.
                    using (var fade = new SKPaint
                    {
                        Color = SKColors.White.WithAlpha((Byte)Math.Round(layer.Alpha * 255, MidpointRounding.AwayFromZero)),
                        BlendMode = ToSkBlend(layer.Blend),
                    })
                    {
                        canvas.SaveLayer(fade);
                    }

                    break;

                case PopLayerCommand:
                    canvas.Restore();
                    break;

                case TextCommand text:
                    DrawText(canvas, text);
                    break;
            }
        }
    }

    /// <summary>
    /// Builds the path an outlined clip is cut to: the edge the shape was given, walked in the order it was
    /// written, with the inside decided by the rule the shape named.
    /// </summary>
    /// <remarks>
    /// Every point is put on a whole pixel, as a square clip is: a shape's numbers come from the box it was
    /// drawn against, and the ink of that box was laid down on the same grid, so a cut kept at the fraction
    /// the layout left would anti-alias a row the fill had already decided whole. A diagonal edge keeps its
    /// slope, since both of its ends are moved by less than half a pixel.
    /// </remarks>
    private static SKPath OutlineOf(IReadOnlyList<ClipSegment> outline, Boolean evenOdd)
    {
        var path = new SKPath
        {
            FillType = evenOdd ? SKPathFillType.EvenOdd : SKPathFillType.Winding,
        };

        foreach (var piece in outline)
        {
            switch (piece.Edge)
            {
                case ClipEdge.Begin:
                    path.MoveTo(Whole(piece.X), Whole(piece.Y));
                    break;

                case ClipEdge.Line:
                    path.LineTo(Whole(piece.X), Whole(piece.Y));
                    break;

                case ClipEdge.Curve:
                    path.CubicTo(
                        Whole(piece.Control1X),
                        Whole(piece.Control1Y),
                        Whole(piece.Control2X),
                        Whole(piece.Control2Y),
                        Whole(piece.X),
                        Whole(piece.Y));
                    break;

                case ClipEdge.Close:
                    path.Close();
                    break;
            }
        }

        return path;
    }

    private static Single Whole(Double value) =>
        (Single)Math.Round(value, MidpointRounding.AwayFromZero);

    /// <summary>
    /// Gets whether a command draws anything inside the given area of the document.
    /// </summary>
    /// <remarks>
    /// The area is in document coordinates and a command's rectangle is in the coordinates of the
    /// list it came from, so the translation it is drawn under is what brings the two together.
    /// <para>
    /// Two commands are never turned away. A clip is a save and a restore around everything that
    /// follows it, so dropping one would leave the canvas stack unbalanced for every command after it.
    /// An outline is drawn outside the border box it names - by its offset and then by its width - so
    /// its rectangle is grown to what it really covers, which is the box itself when the offset draws
    /// it inward.
    /// </para>
    /// <para>
    /// The test is half open: a command whose edge lies exactly on the edge of the area is out of it,
    /// because a line of no width at that edge is not something a reader can see.
    /// </para>
    /// <para>
    /// It is public because the three translations are the whole of what this class knows about
    /// visibility, and they are the part of the rule worth checking on their own.
    /// </para>
    /// </remarks>
    public static Boolean IsVisible(PaintCommand command, Offset offset, Rect visible)
    {
        if (command is PushClipCommand or PopClipCommand or PushTransformCommand or PopTransformCommand or
            PushLayerCommand or PopLayerCommand)
        {
            return true;
        }

        var area = new Rect(
            command.Bounds.X + offset.X,
            command.Bounds.Y + offset.Y,
            command.Bounds.Width,
            command.Bounds.Height);

        if (command is OutlineCommand outline)
        {
            var outset = Math.Max(0, outline.Offset + outline.Width);

            area = new Rect(
                area.X - outset,
                area.Y - outset,
                area.Width + outset * 2,
                area.Height + outset * 2);
        }

        return area.X < visible.Right &&
               visible.X < area.Right &&
               area.Y < visible.Bottom &&
               visible.Y < area.Bottom;
    }

    private static void Fill(SKCanvas canvas, FillCommand command)
    {
        using var paint = new SKPaint { Color = new SKColor(command.Color), IsAntialias = true };

        // A background covers whole pixels, the way a browser snaps the rectangle it fills. An edge
        // that falls inside a pixel row is otherwise anti-aliased there, while the same edge reached
        // as a clip boundary is not - a clip keeps the rows it partly covers - and so the two pictures
        // of one box part over a single row of it. The snap belongs in the pixels the page is drawn in,
        // not in the box's own coordinates: a box moved by a scale would otherwise have its rounded
        // edge scaled along with it and land off the pixel grid again.
        var area = command.Radii.IsZero ? SnapToDevice(canvas, ToRect(command.Bounds)) : ToRect(command.Bounds);

        SkiaRounded.Fill(canvas, area, command.Radii, paint);
    }

    /// <summary>
    /// Moves each edge of an area onto the device pixel it is nearest to, which is the same thing as
    /// <see cref="Snap"/> while the canvas draws in page coordinates. Under a transform the edge is
    /// taken to where it will be drawn, snapped there, and carried back, so a scale does not scale the
    /// rounding along with it. Only a transform that keeps the rectangle a rectangle has edges on the
    /// device grid at all - the axes mapped straight or swapped, which is what a quarter turn does - so
    /// a box turned by any other angle is snapped where it stands, as before.
    /// </summary>
    private static SKRect SnapToDevice(SKCanvas canvas, SKRect area)
    {
        var matrix = canvas.TotalMatrix;

        // A perspective divide bends the edges away from any grid, and a skew slides them apart.
        var straight = Math.Abs(matrix.SkewX) < Epsilon && Math.Abs(matrix.SkewY) < Epsilon;
        var swapped = Math.Abs(matrix.ScaleX) < Epsilon && Math.Abs(matrix.ScaleY) < Epsilon;

        if (matrix.Persp0 != 0 || matrix.Persp1 != 0 ||
            Math.Abs(matrix.Persp2 - 1) > Epsilon ||
            !(straight ^ swapped))
        {
            return Snap(area);
        }

        if (straight)
        {
            return Edges(
                SnapEdge(area.Left, matrix.ScaleX, matrix.TransX),
                SnapEdge(area.Top, matrix.ScaleY, matrix.TransY),
                SnapEdge(area.Right, matrix.ScaleX, matrix.TransX),
                SnapEdge(area.Bottom, matrix.ScaleY, matrix.TransY));
        }

        // A quarter turn: the box's y runs across the page and its x down.
        return Edges(
            SnapEdge(area.Left, matrix.SkewY, matrix.TransY),
            SnapEdge(area.Top, matrix.SkewX, matrix.TransX),
            SnapEdge(area.Right, matrix.SkewY, matrix.TransY),
            SnapEdge(area.Bottom, matrix.SkewX, matrix.TransX));
    }

    /// <summary>Moves one edge onto the device pixel it is nearest to, read back into the box's own
    /// coordinates through the scale and the move that carry it there.</summary>
    private static Single SnapEdge(Single at, Single scale, Single move) =>
        scale == 0 ? at : (Single)((RoundEdge(at * scale + move) - move) / scale);

    /// <summary>Builds a rectangle from edges that may have crossed over a negative scale.</summary>
    private static SKRect Edges(Single left, Single top, Single right, Single bottom) => new(
        Math.Min(left, right),
        Math.Min(top, bottom),
        Math.Max(left, right),
        Math.Max(top, bottom));

    private const Single Epsilon = 1e-6f;

    /// <summary>Moves each edge of an area onto the whole pixel it is nearest to.</summary>
    private static SKRect Snap(SKRect rect) => new(
        RoundEdge(rect.Left),
        RoundEdge(rect.Top),
        RoundEdge(rect.Right),
        RoundEdge(rect.Bottom));

    /// <summary>
    /// Moves a coordinate onto the pixel it falls between by taking the further one, which is the way
    /// the rest of this file snaps - a band laid half a pixel out of a whole one gains its far edge
    /// rather than losing it, and a fill and a border side standing at the same half pixel round alike
    /// (CSS will not say which of the two a half pixel belongs to; this is the answer the engine gives
    /// everywhere else).
    /// </summary>
    private static Single RoundEdge(Single at) =>
        (Single)Math.Round(at, MidpointRounding.AwayFromZero);

    /// <summary>
    /// Draws one shadow of a box, which is the same operation read from the two opposite ends: an outer one
    /// fills the shape it was cast from and cuts the box out of it, an inner one fills the box and cuts the
    /// shape out of that. Either cut has to happen inside a layer of its own, or the mode that takes the
    /// shadow off the box would take the page off with it.
    /// </summary>
    /// <remarks>
    /// The blur is a Gaussian whose standard deviation is half the radius the declaration named, which is
    /// the one answer the specification pins down for a shadow's soft edge - and it asks only that each
    /// pixel come within five percent of what that Gaussian would give, so the filter is Skia's own
    /// (CSS Backgrounds 3 §6.1.2). An inner shadow is blurred only where the shape is taken out; the edge
    /// the shadow is clipped to stays sharp, which is what fills a box whose shape the spread has shrunk to
    /// nothing with the whole of its colour.
    /// </remarks>
    private static void DrawBoxShadow(SKCanvas canvas, BoxShadowCommand command)
    {
        var area = ToRect(command.Area);
        var shape = ToRect(command.Shape);

        using var paint = new SKPaint
        {
            Color = new SKColor(command.Color),
            IsAntialias = true,
        };

        // The cut is a `DstOut` rather than a `Clear`: the shape being taken out is blurred, and Skia applies
        // a filter over the whole bounds it is given, which a `Clear` would then erase to nothing - the box
        // left with no shadow at all, and no fill either. `DstOut` takes off only as much as the shape
        // leaves behind, which is the softer answer the blur asks for.
        using var cut = new SKPaint { BlendMode = SKBlendMode.DstOut, IsAntialias = true };

        if (command.Inset)
        {
            canvas.Save();
            SkiaRounded.Clip(canvas, area, command.AreaRadii);

            var layer = canvas.SaveLayer(area, null);

            SkiaRounded.Fill(canvas, area, command.AreaRadii, paint);

            // The box is filled plain, and it is the shape that is softened - so that the ring the shadow
            // leaves keeps the edge of the box it was thrown from, and only its inner edge runs out soft.
            cut.ImageFilter = Blur(command.Blur);
            SkiaRounded.Fill(canvas, shape, command.ShapeRadii, cut);

            canvas.RestoreToCount(layer);
            canvas.Restore();
            return;
        }

        paint.ImageFilter = Blur(command.Blur);

        var bounds = canvas.SaveLayer(ToRect(command.Bounds), null);

        SkiaRounded.Fill(canvas, shape, command.ShapeRadii, paint);
        SkiaRounded.Fill(canvas, area, command.AreaRadii, cut);

        canvas.RestoreToCount(bounds);
    }

    /// <summary>
    /// Gets the filter that softens a shadow's edge, or null where the radius names none - a sharp shadow is
    /// drawn without one, which is both what the specification asks for and a good deal cheaper to draw.
    /// </summary>
    private static SKImageFilter? Blur(Double radius) =>
        radius > 0 ? SKImageFilter.CreateBlur((Single)(radius / 2), (Single)(radius / 2)) : null;

    /// <summary>
    /// Draws the border of a box.
    /// </summary>
    /// <remarks>
    /// A border follows the corner radii of the box, so it is the area between two rounded rectangles -
    /// the border box outside and the padding box inside - rather than four rectangles that overlap at
    /// the corners. A border that lays one flat ink across square corners is four rectangles; otherwise
    /// each side is confined to the quarter the corner's mitre gives it. A side that is dashed
    /// or dotted is stroked along the middle of the border instead of filled, which is what lets the
    /// pattern run around the curve; on a corner whose radius is smaller than half the border width
    /// that middle line degenerates and the pattern straightens out, which is the one visible
    /// approximation of the two.
    /// </remarks>
    private static void DrawBorder(SKCanvas canvas, BorderCommand command)
    {
        // The plain case - square corners and strokes that need no pattern - is by far the most common
        // one on a page, and four rectangles are a good deal cheaper than two rounded paths. The sides
        // may differ in width, and any number of them may be left unwritten, so long as the ink they
        // lay is one colour: across the corner that colour is all there is to see, and the rectangles
        // cover it whole. Cutting the sides apart along the mitre there leaves nothing to see but the
        // seam two anti-aliased edges make where they meet, and the background that CSS paints under
        // the border shows through it.
        if (command.Radii.IsZero && (command.Styles.IsSolid || IsOneFlatStroke(command)))
        {
            DrawSquareBorder(canvas, command);
            return;
        }

        // A bevelled side is not one flat colour but a light and a dark shade split across its width,
        // so it cannot ride the single-stroke ring below; it is drawn side by side instead, each side
        // clipped to the mitred quarter it owns. Other styles sharing the box still go down the ring.
        if (command.Styles.HasBevel)
        {
            DrawBevelBorder(canvas, command);
            return;
        }

        using var ring = BorderRing(command);
        using var middle = BorderMiddle(command);

        // Every side that lays ink lays the same flat stroke, so the ring is drawn in one go. The widths
        // need not agree - the ring already follows each side's own - and a side left at nothing, which is
        // what a broken inline box has at the edge it was cut at, lays no ink to disagree with. Splitting
        // such a box at the corners would cut the arcs there with the mitre the split is made of, and a
        // rounded box would show a notch at every corner it was cut at.
        if (IsOneFlatStroke(command))
        {
            DrawBorderStroke(canvas, command.Widths.Top, command.TopColor, BorderLineStyle.Solid, ring, middle);
            return;
        }

        foreach (var side in BorderSides.All)
        {
            canvas.Save();
            ClipToSide(canvas, command, side);
            DrawBorderStroke(canvas, command.Widths.Side(side), command.ColorOf(side), command.Styles.Side(side), ring, middle);
            canvas.Restore();
        }
    }

    /// <summary>
    /// Keeps one side of a border and cuts the others away, which is how two sides that disagree on
    /// colour are told apart where they meet.
    /// </summary>
    /// <remarks>
    /// A square box is cut along the mitre that runs corner to corner. A rounded one is cut along the
    /// forty-five degree line through the centre of the corner's arc: the arc itself belongs half to each
    /// of the two sides, and a mitre drawn between the corners' tangent points would leave the arc outside
    /// both sides' quarters - the background showing through the corner of every rounded border whose
    /// sides are not all one colour.
    /// </remarks>
    private static void ClipToSide(SKCanvas canvas, BorderCommand command, BorderSide side)
    {
        if (command.Radii.IsZero)
        {
            using var quad = SideQuad(command.Bounds, command.Widths, side);
            canvas.ClipPath(quad, SKClipOperation.Intersect, antialias: true);
            return;
        }

        var box = command.Bounds;
        var radii = command.Radii;

        (Double X, Double Y) topLeft = (box.X + radii.TopLeft.Horizontal, box.Y + radii.TopLeft.Vertical);
        (Double X, Double Y) topRight = (box.Right - radii.TopRight.Horizontal, box.Y + radii.TopRight.Vertical);
        (Double X, Double Y) bottomRight = (box.Right - radii.BottomRight.Horizontal, box.Bottom - radii.BottomRight.Vertical);
        (Double X, Double Y) bottomLeft = (box.X + radii.BottomLeft.Horizontal, box.Bottom - radii.BottomLeft.Vertical);

        var (first, firstDir, firstNormal, second, secondDir, secondNormal) = side switch
        {
            BorderSide.Top => (topLeft, (1.0, 1.0), (1.0, -1.0), topRight, (-1.0, 1.0), (-1.0, -1.0)),
            BorderSide.Bottom => (bottomLeft, (1.0, -1.0), (1.0, 1.0), bottomRight, (-1.0, -1.0), (-1.0, 1.0)),
            BorderSide.Left => (topLeft, (1.0, 1.0), (-1.0, 1.0), bottomLeft, (1.0, -1.0), (-1.0, -1.0)),
            _ => (topRight, (-1.0, 1.0), (1.0, 1.0), bottomRight, (-1.0, -1.0), (1.0, -1.0)),
        };

        // The two half-planes meet in a wedge that reaches across the whole box, so the side is also kept
        // to the band it lays: without that a bottom border's wedge would reach up into the top border and
        // paint over it.
        var band = side switch
        {
            BorderSide.Top => new SKRect((Single)box.X, (Single)box.Y, (Single)box.Right, (Single)(box.Y + command.Widths.Top + radii.TopLeft.Vertical + radii.TopRight.Vertical)),
            BorderSide.Bottom => new SKRect((Single)box.X, (Single)(box.Bottom - command.Widths.Bottom - radii.BottomLeft.Vertical - radii.BottomRight.Vertical), (Single)box.Right, (Single)box.Bottom),
            BorderSide.Left => new SKRect((Single)box.X, (Single)box.Y, (Single)(box.X + command.Widths.Left + radii.TopLeft.Horizontal + radii.BottomLeft.Horizontal), (Single)box.Bottom),
            _ => new SKRect((Single)(box.Right - command.Widths.Right - radii.TopRight.Horizontal - radii.BottomRight.Horizontal), (Single)box.Y, (Single)box.Right, (Single)box.Bottom),
        };

        using var one = HalfPlane(first.X, first.Y, firstDir, firstNormal, box);
        using var two = HalfPlane(second.X, second.Y, secondDir, secondNormal, box);

        canvas.ClipRect(band, SKClipOperation.Intersect, antialias: false);
        canvas.ClipPath(one, SKClipOperation.Intersect, antialias: true);
        canvas.ClipPath(two, SKClipOperation.Intersect, antialias: true);
    }

    /// <summary>
    /// Gets the half of the plane on one side of the line through a point along a direction, as a rectangle
    /// large enough to hold the whole box it is used to cut.
    /// </summary>
    private static SKPath HalfPlane(
        Double x,
        Double y,
        (Double X, Double Y) direction,
        (Double X, Double Y) normal,
        Rect box)
    {
        var reach = (Single)(Math.Abs(box.Width) + Math.Abs(box.Height) + 4 * (Math.Abs(box.X) + Math.Abs(box.Y)) + 64);
        var (dx, dy) = direction;
        var (nx, ny) = normal;

        var path = new SKPath();
        path.MoveTo((Single)x - reach * (Single)dx, (Single)y - reach * (Single)dy);
        path.LineTo((Single)x + reach * (Single)dx, (Single)y + reach * (Single)dy);
        path.LineTo((Single)(x + reach * dx + reach * nx), (Single)(y + reach * dy + reach * ny));
        path.LineTo((Single)(x - reach * dx + reach * nx), (Single)(y - reach * dy + reach * ny));
        path.Close();
        return path;
    }

    /// <summary>
    /// Gets whether every side that lays any ink lays one flat colour along one unbroken stroke - which
    /// is what lets the four sides be drawn as rectangles over the whole box rather than cut apart at the
    /// corners. A side written with no stroke, or with a band of no width, lays none: it has no colour to
    /// disagree with and no corner to split, and three sides of a border are as common as four.
    /// </summary>
    private static Boolean IsOneFlatStroke(BorderCommand command)
    {
        UInt32? ink = null;

        foreach (var side in BorderSides.All)
        {
            var style = command.Styles.Side(side);

            // A side that is written with no stroke, or with a band no wider than nothing, lays no ink at
            // all, so it neither has a colour to disagree with nor a corner to split.
            if (style == BorderLineStyle.None || command.Widths.Side(side) == 0)
            {
                continue;
            }

            if (style != BorderLineStyle.Solid)
            {
                return false;
            }

            var colour = command.ColorOf(side);

            if (ink is null)
            {
                ink = colour;
                continue;
            }

            if (ink != colour)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Draws the four sides as overlapping rectangles, which is right only when nothing rounds.</summary>
    private static void DrawSquareBorder(SKCanvas canvas, BorderCommand command)
    {
        var box = command.Bounds;
        var widths = command.Widths;

        DrawSide(canvas, new Rect(box.X, box.Y, box.Width, widths.Top), command.TopColor, snap: true);

        if (widths.Bottom > 0)
        {
            DrawSide(canvas, new Rect(box.X, box.Bottom - widths.Bottom, box.Width, widths.Bottom), command.BottomColor, snap: true);
        }

        if (widths.Left > 0)
        {
            DrawSide(canvas, new Rect(box.X, box.Y, widths.Left, box.Height), command.LeftColor, snap: true);
        }

        if (widths.Right > 0)
        {
            DrawSide(canvas, new Rect(box.Right - widths.Right, box.Y, widths.Right, box.Height), command.RightColor, snap: true);
        }
    }

    /// <summary>
    /// Draws a border whose sides are bevelled: groove and ridge split each side's band into a darkened
    /// and a lightened half, inset and outset give it one shade chosen by which corner the side meets,
    /// and double lays two lines with the background showing between them. A side that is not bevelled
    /// still draws flat, so a box may mix the two families.
    /// </summary>
    private static void DrawBevelBorder(SKCanvas canvas, BorderCommand command)
    {
        using var ring = BorderRing(command);
        using var middle = BorderMiddle(command);

        var box = command.Bounds;
        var widths = command.Widths;

        foreach (var side in BorderSides.All)
        {
            var style = command.Styles.Side(side);

            if (style == BorderLineStyle.None)
            {
                continue;
            }

            var width = widths.Side(side);
            var color = command.ColorOf(side);

            canvas.Save();
            canvas.ClipPath(SideQuad(box, widths, side), SKClipOperation.Intersect, antialias: true);

            if (BorderStyles.IsBevel(style))
            {
                DrawBevelSide(canvas, box, width, side, style, color);
            }
            else
            {
                DrawBorderStroke(canvas, width, color, style, ring, middle);
            }

            canvas.Restore();
        }
    }

    /// <summary>
    /// Fills one bevelled side within the clip already narrowed to its mitred quarter. The light is
    /// taken to fall from the top left, so a top or left side is the dark one of the pair and a bottom
    /// or right side the light one; groove and ridge then set which half of the width each shade claims.
    /// </summary>
    private static void DrawBevelSide(
        SKCanvas canvas,
        Rect box,
        Double width,
        BorderSide side,
        BorderLineStyle style,
        UInt32 color)
    {
        if (width <= 0)
        {
            return;
        }

        // CSS 2.1 §8.5.3: a double line needs three pixels to be two lines and the gap between them, and
        // a narrower border is drawn as a solid line instead.
        if (style == BorderLineStyle.Double && width < 3)
        {
            FillBand(canvas, box, side, 0, width, color);
            return;
        }

        var dark = Darken(color);
        var light = Lighten(color);
        var topOrLeft = side is BorderSide.Top or BorderSide.Left;
        var half = width / 2;

        switch (style)
        {
            case BorderLineStyle.Groove:
                FillBand(canvas, box, side, 0, half, topOrLeft ? dark : light);
                FillBand(canvas, box, side, half, width, topOrLeft ? light : dark);
                break;

            case BorderLineStyle.Ridge:
                FillBand(canvas, box, side, 0, half, topOrLeft ? light : dark);
                FillBand(canvas, box, side, half, width, topOrLeft ? dark : light);
                break;

            case BorderLineStyle.Inset:
                FillBand(canvas, box, side, 0, width, topOrLeft ? dark : light);
                break;

            case BorderLineStyle.Outset:
                FillBand(canvas, box, side, 0, width, topOrLeft ? light : dark);
                break;

            default:
                // Double: two lines in the border colour with the background left showing between them.
                var third = width / 3;
                FillBand(canvas, box, side, 0, third, color);
                FillBand(canvas, box, side, width - third, width, color);
                break;
        }
    }

    /// <summary>Fills the slice of a side that lies between two depths measured from its outer edge.</summary>
    private static void FillBand(SKCanvas canvas, Rect box, BorderSide side, Double from, Double to, UInt32 color) =>
        DrawSide(canvas, BandRect(box, side, from, to), color);

    private static Rect BandRect(Rect box, BorderSide side, Double from, Double to) => side switch
    {
        BorderSide.Top => new Rect(box.X, box.Y + from, box.Width, to - from),
        BorderSide.Bottom => new Rect(box.X, box.Bottom - to, box.Width, to - from),
        BorderSide.Left => new Rect(box.X + from, box.Y, to - from, box.Height),
        _ => new Rect(box.Right - to, box.Y, to - from, box.Height),
    };

    /// <summary>Gets a shade driven halfway toward black, keeping its alpha: the dark half of a bevel.</summary>
    private static UInt32 Darken(UInt32 argb)
    {
        var alpha = argb & 0xFF00_0000;
        var r = ((argb >> 16) & 0xFF) / 2;
        var g = ((argb >> 8) & 0xFF) / 2;
        var b = (argb & 0xFF) / 2;
        return alpha | (r << 16) | (g << 8) | b;
    }

    /// <summary>Gets a shade driven halfway toward white, keeping its alpha: the light half of a bevel.</summary>
    private static UInt32 Lighten(UInt32 argb)
    {
        var alpha = argb & 0xFF00_0000;
        var r = ((argb >> 16) & 0xFF);
        var g = ((argb >> 8) & 0xFF);
        var b = (argb & 0xFF);
        r += (255 - r) / 2;
        g += (255 - g) / 2;
        b += (255 - b) / 2;
        return alpha | (r << 16) | (g << 8) | b;
    }

    private static void DrawBorderStroke(
        SKCanvas canvas,
        Double width,
        UInt32 color,
        BorderLineStyle style,
        SKPath ring,
        SKPath middle)
    {
        if (width <= 0 || style == BorderLineStyle.None)
        {
            return;
        }

        if (style == BorderLineStyle.Solid)
        {
            using var fill = new SKPaint { Color = new SKColor(color), IsAntialias = true };
            canvas.DrawPath(ring, fill);
            return;
        }

        using var paint = new SKPaint
        {
            Color = new SKColor(color),
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = (Single)width,
        };

        if (style == BorderLineStyle.Dotted)
        {
            paint.StrokeCap = SKStrokeCap.Round;
        }

        using var dashes = Dashes(width, style);
        paint.PathEffect = dashes;
        canvas.DrawPath(middle, paint);
    }

    /// <summary>
    /// Gets the border area as two rounded rectangles, the outer one wound the other way so that an
    /// even-odd fill leaves the band between them.
    /// </summary>
    private static SKPath BorderRing(BorderCommand command)
    {
        var outer = ToRect(command.Bounds);
        var padding = Deflate(outer, command.Widths);

        var path = new SKPath { FillType = SKPathFillType.EvenOdd };
        path.AddRoundRect(SkiaRounded.RoundRect(outer, command.Radii));

        if (padding.Width > 0 && padding.Height > 0)
        {
            path.AddRoundRect(SkiaRounded.RoundRect(padding, command.Radii.Shrink(command.Widths)));
        }

        return path;
    }

    /// <summary>Gets the line through the middle of the border, which a pattern is stroked along.</summary>
    private static SKPath BorderMiddle(BorderCommand command)
    {
        var widths = command.Widths;
        var half = new Thickness(widths.Top / 2, widths.Right / 2, widths.Bottom / 2, widths.Left / 2);

        var path = new SKPath();
        path.AddRoundRect(SkiaRounded.RoundRect(Deflate(ToRect(command.Bounds), half), command.Radii.Shrink(half)));
        return path;
    }

    /// <summary>
    /// Draws the outline of a box, which is a band that begins <c>outline-offset</c> outside the border
    /// box and reaches <c>outline-width</c> further out.
    /// </summary>
    /// <remarks>
    /// An outline is drawn around the box rather than in it - that is the whole point of it - so there
    /// is nothing for it to sit against: the offset shifts it outwards instead of insetting it, and a
    /// solid one is the band between two rounded rectangles rather than a stroke laid on an edge. A
    /// pattern cannot be filled and has to be stroked, so to land in the same place it is stroked along
    /// the middle of that band. A rounded box's outline follows its corner radii, grown by the distance
    /// it stands away, which is what makes it run parallel to the border it surrounds.
    /// </remarks>
    private static void DrawOutline(SKCanvas canvas, OutlineCommand command)
    {
        var box = ToRect(command.Bounds);

        if (command.Style == BorderLineStyle.Solid)
        {
            using var fill = new SKPaint { Color = new SKColor(command.Color), IsAntialias = true };
            using var ring = OutlineRing(box, command);
            canvas.DrawPath(ring, fill);
            return;
        }

        using var paint = new SKPaint
        {
            Color = new SKColor(command.Color),
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = (Single)command.Width,
        };

        if (command.Style == BorderLineStyle.Dotted)
        {
            paint.StrokeCap = SKStrokeCap.Round;
        }

        using var dashes = Dashes(command.Width, command.Style);
        paint.PathEffect = dashes;
        using var middle = OutlineMiddle(box, command);
        canvas.DrawPath(middle, paint);
    }

    /// <summary>
    /// Gets the outline as the band between its inner and outer edges, the inner one wound the other
    /// way so that an even-odd fill leaves the band between them.
    /// </summary>
    private static SKPath OutlineRing(SKRect box, OutlineCommand command)
    {
        var outer = Inflate(box, command.Offset + command.Width);
        var inner = Inflate(box, command.Offset);

        var path = new SKPath { FillType = SKPathFillType.EvenOdd };
        path.AddRoundRect(SkiaRounded.RoundRect(outer, Grow(command.Radii, command.Offset + command.Width)));

        // A negative offset can pull the inner edge past the middle of a small box, and an inside-out
        // rectangle would make the even-odd fill cover the band instead of leaving it out.
        if (inner.Width > 0 && inner.Height > 0)
        {
            path.AddRoundRect(SkiaRounded.RoundRect(inner, Grow(command.Radii, command.Offset)));
        }

        return path;
    }

    /// <summary>Gets the line through the middle of the outline, which a pattern is stroked along.</summary>
    private static SKPath OutlineMiddle(SKRect box, OutlineCommand command)
    {
        var distance = command.Offset + (command.Width / 2);

        var path = new SKPath();
        path.AddRoundRect(SkiaRounded.RoundRect(Inflate(box, distance), Grow(command.Radii, distance)));
        return path;
    }

    /// <summary>
    /// Gets the corner radii of an outline standing <paramref name="distance"/> out from the border box,
    /// so that a rounded outline runs parallel to the border it surrounds. The radii grow by being
    /// shrunk with a negative inset; a corner that is square has nothing to grow and stays square, which
    /// the shrink alone does not say - a square corner shrunk by minus two would come out as a corner
    /// rounded by two, and the outline's four sharp corners would be painted with a small curve in them
    /// while the border they were compared against was mitred.
    /// </summary>
    private static CornerRadii Grow(CornerRadii radii, Double distance) =>
        radii.IsZero
            ? CornerRadii.None
            : radii.Shrink(new Thickness(-distance, -distance, -distance, -distance));

    private static SKRect Inflate(SKRect box, Double distance) => new(
        box.Left - (Single)distance,
        box.Top - (Single)distance,
        box.Right + (Single)distance,
        box.Bottom + (Single)distance);

    /// <summary>
    /// Gets the quarter of the border a side owns, bounded by the diagonals the two corners either end
    /// of it would miter along - which is what keeps two sides from painting over each other.
    /// </summary>
    private static SKPath SideQuad(Rect box, Thickness widths, BorderSide side)
    {
        var path = new SKPath();

        switch (side)
        {
            case BorderSide.Top:
                path.MoveTo((Single)box.X, (Single)box.Y);
                path.LineTo((Single)box.Right, (Single)box.Y);
                path.LineTo((Single)(box.Right - widths.Right), (Single)(box.Y + widths.Top));
                path.LineTo((Single)(box.X + widths.Left), (Single)(box.Y + widths.Top));
                break;

            case BorderSide.Bottom:
                path.MoveTo((Single)box.X, (Single)box.Bottom);
                path.LineTo((Single)(box.X + widths.Left), (Single)(box.Bottom - widths.Bottom));
                path.LineTo((Single)(box.Right - widths.Right), (Single)(box.Bottom - widths.Bottom));
                path.LineTo((Single)box.Right, (Single)box.Bottom);
                break;

            case BorderSide.Left:
                path.MoveTo((Single)box.X, (Single)box.Y);
                path.LineTo((Single)(box.X + widths.Left), (Single)(box.Y + widths.Top));
                path.LineTo((Single)(box.X + widths.Left), (Single)(box.Bottom - widths.Bottom));
                path.LineTo((Single)box.X, (Single)box.Bottom);
                break;

            default:
                path.MoveTo((Single)box.Right, (Single)box.Y);
                path.LineTo((Single)box.Right, (Single)box.Bottom);
                path.LineTo((Single)(box.Right - widths.Right), (Single)(box.Bottom - widths.Bottom));
                path.LineTo((Single)(box.Right - widths.Right), (Single)(box.Y + widths.Top));
                break;
        }

        path.Close();
        return path;
    }

    /// <summary>
    /// Gets the dash pattern a side is stroked with. The intervals follow the border width, which the
    /// spec leaves to the implementation; a dot is a dash of no length drawn with a round cap, so it
    /// comes out as a circle the width of the border.
    /// </summary>
    private static SKPathEffect? Dashes(Double width, BorderLineStyle style)
    {
        var gap = (Single)Math.Max(1, width * 2);
        var interval = style == BorderLineStyle.Dotted ? (Single)0.1 : gap;

        return SKPathEffect.CreateDash(new[] { interval, gap }, 0);
    }

    private static SKRect Deflate(SKRect box, Thickness insets) => new(
        box.Left + (Single)insets.Left,
        box.Top + (Single)insets.Top,
        box.Right - (Single)insets.Right,
        box.Bottom - (Single)insets.Bottom);

    private static void DrawSide(SKCanvas canvas, Rect side, UInt32 color, Boolean snap = false)
    {
        if (side.Width <= 0 || side.Height <= 0)
        {
            return;
        }

        using var paint = new SKPaint { Color = new SKColor(color), IsAntialias = false };

        // A plain side is a background in every way that matters: a browser lays both down as whole
        // pixels, so a side left at half a pixel and a fill left at the same half pixel have to round
        // alike. Without this a gap decoration painted as a border side and the same band painted as a
        // background in its reference part over one pixel column (grid-gap-decorations-052).
        var area = snap ? SnapToDevice(canvas, ToRect(side)) : ToRect(side);

        canvas.DrawRect(area, paint);
    }

    /// <summary>
    /// Draws a run of text as the blob the measurer's cache keeps for it: the glyphs are shaped and
    /// positioned once per text, style and spacing, and every frame after the first is a single draw.
    /// </summary>
    /// <remarks>
    /// The run is drawn in the two bands CSS Text Decoration 4 §5.1 names, which is what decides what
    /// covers what: a descender that reaches down to the rule under it stands in the letter's own ink
    /// rather than in the rule's, while a line that crosses the letters lies over them.
    /// </remarks>
    private void DrawText(SKCanvas canvas, TextCommand command)
    {
        var metrics = _measurer.GetMetrics(command.Font);

        using var paint = new SKPaint { Color = new SKColor(command.Color), IsAntialias = true };

        if (!command.Flow.IsVertical)
        {
            var baseline = (Single)(command.Bounds.Y + command.Baseline);
            var ink = Along(command, baseline);

            DrawDecorations(canvas, paint, command, metrics, baseline, alongLine: true, ink, through: false);

            // The letters, and the stroke that rings them, come between the two bands: §5.1 puts
            // underlines and overlines under the text and the line-through over it. A text stroke is drawn
            // before - and so behind - the fill. A transparent stroke paints nothing of its own and is
            // skipped; its width still matters, but only to a background clipped to the text, which
            // widens to it in its own pass. A shadow is the ink of the run cast again, so it stands under
            // the run and over the lines drawn beneath it.
            DrawShadows(canvas, command, metrics);

            StrokeRun(canvas, command);

            paint.Color = new SKColor(command.Color);
            DrawRun(canvas, command, metrics, paint);

            DrawDecorations(canvas, paint, command, metrics, baseline, alongLine: true, ink, through: true);

            return;
        }

        // In a column a decoration runs beside the text rather than under it, so it becomes a line
        // down the box at the distance from the baseline that the font asked for - and it is split the
        // same way, the lines stood off the baseline behind the turned glyphs and the crossing one in
        // front of them. The distances here are taken from the baseline the column's glyphs are drawn
        // about, which is what the overline and the line-through are stood off; the underline of a
        // column is the one line stood off something else - the font its own decorating box is set in -
        // and DrawDecorations lays that out (CSS Text Decoration 4 §2.5, §2.9).
        var across = (Single)Axis(command, metrics);
        var beside = Beside(command, metrics);

        DrawDecorations(canvas, paint, command, metrics, across, alongLine: false, beside, through: false);

        DrawShadows(canvas, command, metrics);

        StrokeRun(canvas, command);

        paint.Color = new SKColor(command.Color);
        DrawRun(canvas, command, metrics, paint);

        DrawDecorations(canvas, paint, command, metrics, across, alongLine: false, beside, through: true);
    }

    /// <summary>
    /// Draws the glyphs of a run with the paint it is handed - the whole of the run, whichever way it is set
    /// out: across a line, turned down a column, stood upright in one, or combined into the width of a
    /// single character.
    /// </summary>
    /// <remarks>
    /// A shadow of the run is drawn by the same road as the run itself, which is the only way it lands on
    /// the same pixels the layout measured out: a justified line, a column of turned glyphs and a run set
    /// with letter spacing each put their ink where that ink stands, moved by nothing but the offset the
    /// shadow asks for.
    /// </remarks>
    private void DrawRun(SKCanvas canvas, TextCommand command, FontMetrics metrics, SKPaint paint)
    {
        if (!command.Flow.IsVertical)
        {
            DrawRunBlob(canvas, command, (Single)(command.Bounds.Y + command.Baseline), paint, command.LetterSpacing, command.WordSpacing);
            return;
        }

        if (command.Flow.CombinesUpright)
        {
            DrawCombined(canvas, command, metrics, paint);
            return;
        }

        if (command.Flow.Orientation == TextOrientation.Upright)
        {
            DrawUprightColumn(canvas, command, metrics, paint);
            return;
        }

        DrawTurned(canvas, command, metrics, paint);
    }

    /// <summary>
    /// Draws the shadows the run of text is drawn with, each of them the whole run again moved by its own
    /// offsets and softened by its own radius, and every one of them under the ink it shadows.
    /// </summary>
    /// <remarks>
    /// They are laid last-first, because the list is written front to back and the first shadow is the one
    /// on top (CSS Text Decoration 4 §4.1). The offsets are physical rather than logical, so a column's
    /// shadow falls down and to the right exactly as a line's does; the run is moved by the canvas rather
    /// than by the place each glyph is drawn, which is what keeps a turned column's shadow in one piece.
    /// </remarks>
    private void DrawShadows(SKCanvas canvas, TextCommand command, FontMetrics metrics)
    {
        for (var index = command.Shadows.Count - 1; index >= 0; index--)
        {
            var shadow = command.Shadows[index];

            // A shadow with no ink in it is asked to draw nothing, and the run would be set out twice to find
            // out that it shows nowhere.
            if ((shadow.Color & 0xFF00_0000) == 0)
            {
                continue;
            }

            using var paint = new SKPaint
            {
                Color = new SKColor(shadow.Color),
                IsAntialias = true,
                ImageFilter = Blur(shadow.Blur),
            };

            canvas.Save();
            canvas.Translate((Single)shadow.OffsetX, (Single)shadow.OffsetY);
            DrawRun(canvas, command, metrics, paint);
            canvas.Restore();
        }
    }

    /// <summary>
    /// Rings the glyphs of a run, drawn behind its fill. Nothing happens where the page asked for no
    /// stroke, or for a transparent one.
    /// </summary>
    private void StrokeRun(SKCanvas canvas, TextCommand command)
    {
        if (command.StrokeWidth <= 0 || (command.StrokeColor & 0xFF00_0000) == 0)
        {
            return;
        }

        using var stroke = new SKPaint
        {
            Color = new SKColor(command.StrokeColor),
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = (Single)command.StrokeWidth,
            StrokeJoin = SKStrokeJoin.Round,
        };

        StrokeGlyphs(canvas, command, stroke);
    }

    /// <summary>
    /// Draws every line the boxes around this run asked for: outermost first, so the line an element
    /// itself asked for lies over the one its container did, and each in the shape and the colour of the
    /// box that decorates - not of the text it happens to be running across
    /// (CSS Text Decoration 4 §2.3, §5.1). Within one box the underline goes under the overline and the
    /// strike-through over both, which is the order the specification paints them in.
    /// </summary>
    /// <param name="from">
    /// The baseline the offsets are measured from: down from the top of the box along a line, and across
    /// from its left edge down a column.
    /// </param>
    /// <param name="alongLine">
    /// Whether the run reads along a line, so its decorations run with it, or down a column, so they run
    /// beside the text they decorate.
    /// </param>
    /// <param name="ink">
    /// Where the run's own glyphs put their ink, as the page sees it: a line asked to skip ink is broken
    /// where one of these stands across it (§2.10.4), and the list is empty where nothing is to be skipped.
    /// </param>
    /// <param name="through">
    /// Which of the two bands §5.1 this pass draws: false for the lines stood off the baseline - the
    /// underline and the overline, which are painted under the text - and true for the line-through, which
    /// is painted over it.
    /// </param>
    private void DrawDecorations(
        SKCanvas canvas,
        SKPaint paint,
        TextCommand command,
        FontMetrics metrics,
        Single from,
        Boolean alongLine,
        SKRect[] ink,
        Boolean through)
    {
        // Down a column the line is drawn along the page's other axis, so the boxes are turned to match:
        // what runs down the text is what a line asked to skip crosses.
        var across = alongLine
            ? ink
            : [.. ink.Select(box => new SKRect(box.Top, box.Left, box.Bottom, box.Right))];

        // A column's lines stand across the box instead of under it, and the run's own left edge is
        // where the distance from the baseline begins.
        var origin = alongLine ? 0f : (Single)command.Bounds.X;
        var sign = alongLine ? 1f : -1f;

        // Which way a line's band is laid from the edge it stands on: away from the text,
        // which is downward along a row and outward across a column.
        var grow = alongLine ? 1 : -1;

        for (var drawn = 0; drawn < command.Decorations.Count; drawn++)
        {
            var decoration = command.Decorations[drawn];

            // Where the layout settled the line this run stands on, the whole of it is drawn to that
            // answer: one decorating box uses one position and one thickness on each line for its
            // underlines and overlines, so a raised run does not carry the line under it up with itself
            // and a smaller one does not thin it (CSS Text Decoration 4 §2.9). A column keeps its own
            // baseline: its text is aligned by its central one, which is not the alphabetic baseline
            // §2.5 averages, and only the offsets the line was measured with travel.
            var placed = drawn < command.Placements.Count ? command.Placements[drawn] : null;
            var stood = from + (alongLine && placed is not null ? (Single)placed.BaselineShift : 0f);

            // The ends of the line are pulled in, or run outward where the distance is negative, by what
            // the decorating box asked for (§2.9.1). The line-through takes the same ends: the inset
            // controls every line the box draws, however the line itself is positioned.
            var pulls = placed?.TrimStart ?? 0.0;
            var pushes = placed?.TrimEnd ?? 0.0;

            paint.Color = new SKColor(decoration.Color);

            // A line-through is never broken; an underline or overline is broken where the page asked
            // for it, or where auto was left standing and the script of the text does not lie across the
            // rule (§2.10.4). The answer is the run's own: it is this text's ink the line would cross, and
            // a marker - or a descendant - that says otherwise about its own text is taken at its word.
            var skips = command.Skip switch
            {
                DecorationSkip.None => false,
                DecorationSkip.All => true,
                _ => !TextClusters.StandsAcrossTheLine(command.Text),
            };
            var blocked = skips ? across : Array.Empty<SKRect>();

            // Where the run's baseline stands on the axis its lines are measured along, in the same
            // coordinates as those lines: a row counts down from the page's top edge, a column across
            // from the box's own left one, and `from` is handed over in the local terms of each.
            // It is the row the line was averaged onto rather than the run's own baseline, which is what
            // the offset was counted from - and a run lifted out of that row by `vertical-align` leaves
            // the line standing on the row it was measured with.
            var text = origin + stood;

            if (!through && decoration.Underline)
            {
                // The face gives the line its zero position; text-underline-offset moves it outward
                // from there, and text-decoration-thickness replaces the face's own width
                // (CSS Text Decoration 4 §2.4, §2.8). Along a row the line stands off the baseline of
                // the text it runs across; down a column it stands off the font its own box is set in,
                // as the branch below lays out. A column that says right puts the line on the other
                // side of the text, where a row reads left and right as auto (§2.7).
                var extra = decoration.UnderlineOffset ?? 0;
                var fontOffset = placed?.UnderlineOffset ?? Math.Abs(metrics.UnderlineOffset);

                // Across a row of CJK text the alphabetic underline often crosses the glyphs - the
                // bottom stroke of an ideograph sits on the baseline - so the face's own position is
                // not a position a reader can see. `auto` and `under` may sit at the text-under edge
                // instead, which is where a CJK reader expects the rule and what the specification's
                // note about Asian scripts asks a UA to consider (CSS Text Decoration 4 §2.7).
                if (alongLine &&
                    decoration.Position is TextUnderlinePosition.Auto or TextUnderlinePosition.Under &&
                    TextClusters.StandsAcrossTheLine(command.Text))
                {
                    fontOffset = Math.Max(fontOffset, Math.Abs(metrics.Descent));
                }

                // Along a row the line stands its settled distance from the baseline the whole line
                // shares, so a run of Latin turned down a column and the run of ideographs beside it
                // draw one rule: the offset is the decorating box's own, averaged over the text it
                // reaches on the line, and not the run's, which would step the line wherever the
                // scripts meet (§2.9). A column's `right` asks for the other side of the text, where a
                // row reads left and right as auto (§2.7).
                Double at;
                Int32 underlineGrow;

                if (!alongLine)
                {
                    // A column stands the line off the font the decorating box itself is set in: the
                    // box's inline box is centred across the column, its baseline sits one ascent into
                    // it, and the line goes one underline below that. Taking it from the box rather than
                    // from the runs it crosses is what lines a note set smaller than the text beside it
                    // at the note's own size, while every run of one box still draws one line (§2.9).
                    var face = _measurer.GetMetrics(decoration.Font);
                    var under = (command.Bounds.Width / 2) -
                        ((face.Ascent - face.Descent) / 2) -
                        Math.Abs(face.UnderlineOffset);

                    if (decoration.Position == TextUnderlinePosition.Right)
                    {
                        at = command.Bounds.X + command.Bounds.Width - under + extra;
                        underlineGrow = 1;
                    }
                    else
                    {
                        at = command.Bounds.X + under - extra;
                        underlineGrow = -1;
                    }
                }
                else
                {
                    at = text + (sign * (fontOffset + extra));
                    underlineGrow = grow;
                }

                DrawLine(
                    canvas,
                    paint,
                    command,
                    (Single)at,
                    Thickness(decoration.Thickness, placed?.UnderlineThickness ?? metrics.UnderlineThickness),
                    decoration.Shape,
                    alongLine,
                    pulls,
                    pushes,
                    blocked,
                    // An underline stands under its text and thickens away from it: downward along a
                    // row, and out to the side it was measured to down a column. Which side that is
                    // follows the direction the offset was counted in, so it is the same sign as that
                    // one rather than the answer to where the line landed - a page that sets a negative
                    // <c>text-underline-offset</c> still asks for an underline (§2.4).
                    underlineGrow);
            }

            // The face says where its overline goes; a face that will not is answered with the top of
            // its own em box, which is the only other height the line could be taken from.
            if (!through && decoration.Overline)
            {
                var at = text - (sign * (placed?.OverlineOffset
                    ?? (metrics.OverlineOffset > 0
                        ? Math.Abs(metrics.OverlineOffset)
                        : metrics.Ascent)));

                DrawLine(
                    canvas,
                    paint,
                    command,
                    (Single)at,
                    Thickness(
                        decoration.Thickness,
                        placed?.OverlineThickness
                            ?? (metrics.OverlineThickness > 0
                                ? metrics.OverlineThickness
                                : metrics.UnderlineThickness)),
                    decoration.Shape,
                    alongLine,
                    pulls,
                    pushes,
                    blocked,
                    // An overline stands over its text and thickens away from it, the other way from
                    // its underline (§2.4).
                    -grow);
            }

            // A line-through is left to the run it crosses: §2.5 has its position recomputed wherever the
            // font size changes, so that the text stays crossed out rather than following one line.
            if (through && decoration.LineThrough)
            {
                DrawLine(
                    canvas,
                    paint,
                    command,
                    (Single)(origin + from - (sign * Math.Abs(metrics.StrikeoutOffset))),
                    Thickness(decoration.Thickness, metrics.StrikeoutThickness),
                    decoration.Shape,
                    alongLine,
                    pulls,
                    pushes,
                    blocked,
                    // A line-through is drawn across the letters it crosses, so a thicker one covers
                    // more of them rather than sliding off the text to one side (§2.5).
                    0);
            }
        }
    }

    /// <summary>
    /// Rings the glyphs of a run, in whichever flow it is set, without the decorations - a stroke belongs
    /// to the letterforms, not to the line under them.
    /// </summary>
    private void StrokeGlyphs(SKCanvas canvas, TextCommand command, SKPaint stroke)
    {
        if (!command.Flow.IsVertical)
        {
            var baseline = (Single)(command.Bounds.Y + command.Baseline);
            DrawRunBlob(canvas, command, baseline, stroke, command.LetterSpacing, command.WordSpacing);
            return;
        }

        var metrics = _measurer.GetMetrics(command.Font);

        if (command.Flow.CombinesUpright)
        {
            DrawCombined(canvas, command, metrics, stroke);
        }
        else if (command.Flow.Orientation == TextOrientation.Upright)
        {
            DrawUprightColumn(canvas, command, metrics, stroke);
        }
        else
        {
            DrawTurned(canvas, command, metrics, stroke);
        }
    }

    /// <summary>
    /// Draws the blob the cache keeps for the run, at the place the run is set. The blob carries its
    /// glyphs relative to its own origin, so one object answers every frame and every scroll: drawing
    /// it at the run's position is what lands each glyph where the layout measured it.
    /// </summary>
    private void DrawRunBlob(
        SKCanvas canvas, TextCommand command, Single baseline, SKPaint paint, Double letterSpacing, Double wordSpacing)
    {
        var blob = _measurer.Blobs.GetRunBlob(
            command.Font,
            command.Text,
            letterSpacing,
            command.Justification,
            command.Justifies,
            command.Expand,
            wordSpacing,
            command.Reversed ?? command.Flow.SetsTextBackwards,
            command.Trims,
            command.AutoSpaces,
            command.AutoSpaceRoom);

        if (blob is not null)
        {
            canvas.DrawText(blob, (Single)command.Bounds.X, baseline, paint);
        }
    }

    /// <summary>
    /// Gets how thick a decoration line is: what the decorating box asked for, or the face's own width
    /// where it asked for nothing. A line is never thinner than one pixel, however small the ask.
    /// </summary>
    private static Double Thickness(Double? asked, Double fromFont) =>
        Math.Max(1, asked ?? fromFont);

    /// <summary>
    /// Draws one decoration line across the run in the shape the element asked for. A solid line is a
    /// filled band; a dashed one is ink of three thicknesses with two of clear between, and a dotted one
    /// is a row of round dots, which is what CSS Text Decoration 4 §3.3 asks of them; a double is two
    /// bands with a band's width of clear between. The wave is the shape the specification leaves to the
    /// engine: the line's own thickness swung from side to side as it goes.
    /// </summary>
    /// <param name="at">
    /// Where the line sits across the text: the distance from the box's start edge along the axis the
    /// decoration is offset by, which is down the box for a line and across it for a column.
    /// </param>
    /// <param name="alongLine">
    /// Whether the run reads along a line, so the decoration runs with it, or down a column, so the
    /// decoration runs beside the text it decorates.
    /// </param>
    /// <param name="grow">
    /// Which way the band extends from <paramref name="at"/>: away from the text it stands off, or
    /// <c>0</c> to have the position fall in the middle of it.
    /// </param>
    private static void DrawLine(
        SKCanvas canvas,
        SKPaint paint,
        TextCommand command,
        Single at,
        Double thickness,
        DecorationShape shape,
        Boolean alongLine,
        Double trimStart,
        Double trimEnd,
        SKRect[] ink,
        Int32 grow = 1)
    {
        var height = (Single)thickness;
        var start = (Single)(alongLine ? command.Bounds.X : command.Bounds.Y);
        var end = (Single)(alongLine ? command.Bounds.Right : command.Bounds.Bottom);

        // Where the band is asked to stand off the edge rather than hang from it, the edge is its far
        // side; a line-through is struck through the middle of the text it crosses.
        at -= grow < 0 ? height : grow == 0 ? height / 2 : 0;

        // The distance the decorating box asked for is counted from the edge its text begins reading at,
        // which for a row written right-to-left is the far one, and a negative distance runs the line past
        // the text rather than back from it (CSS Text Decoration 4 §2.9.1).
        if (command.Flow.InlineSign > 0)
        {
            start += (Single)trimStart;
            end -= (Single)trimEnd;
        }
        else
        {
            end -= (Single)trimStart;
            start += (Single)trimEnd;
        }

        if (end <= start)
        {
            return;
        }

        // Where the line is asked to skip ink, it is laid down in the pieces left between the glyphs'
        // own ink and the room on either side of it; where it is not, the whole stretch is one piece.
        foreach (var (from, to) in Pieces(start, end, at, height, ink))
        {
            Shape(canvas, paint, command, at, height, shape, alongLine, from, to);
        }
    }

    /// <summary>
    /// Lays one shape of a decoration line across one stretch of the text it is drawn over.
    /// </summary>
    private static void Shape(
        SKCanvas canvas,
        SKPaint paint,
        TextCommand command,
        Single at,
        Single height,
        DecorationShape shape,
        Boolean alongLine,
        Single start,
        Single end)
    {
        if (end <= start)
        {
            return;
        }

        if (shape is DecorationShape.Solid or DecorationShape.Double)
        {
            Band(canvas, paint, start, end, at, height, alongLine);

            if (shape == DecorationShape.Double)
            {
                Band(canvas, paint, start, end, at + (height * 2), height, alongLine);
            }

            return;
        }

        if (shape == DecorationShape.Wavy)
        {
            using var stroke = new SKPaint
            {
                Color = paint.Color,
                IsAntialias = paint.IsAntialias,
                Style = SKPaintStyle.Stroke,
                StrokeWidth = height,
            };

            Swing(canvas, stroke, start, end, at, height, alongLine);
            return;
        }

        // A dash is three thicknesses of ink with two of clear, and a dot is a round of the line's own
        // thickness set twice its own thickness apart (CSS Text Decoration 4 §3.3). Both are laid down
        // as the pieces themselves rather than as a dash pattern, which is the one thing a line has to
        // have no opinion about: the pieces run along the text whichever way the text runs.
        var pitch = shape == DecorationShape.Dotted ? height * 2 : height * 5;

        for (var position = start; position < end; position += pitch)
        {
            if (shape == DecorationShape.Dotted)
            {
                var dot = Place(position + height, at + (height / 2), alongLine);

                canvas.DrawCircle(dot, height / 2, paint);
                continue;
            }

            var room = Math.Min(position + (height * 3), end);

            Band(canvas, paint, position, room, at, height, alongLine);
        }
    }

    /// <summary>
    /// Cuts the stretch a line is drawn over into the pieces that are left once the glyphs standing across
    /// it have been taken out, with a room on either side of each of them: the line is not asked to touch
    /// the letter it stops short of, and where a whole run of ink lies across the band the line is simply
    /// not drawn there at all (§2.10.4).
    /// </summary>
    private static IEnumerable<(Single From, Single To)> Pieces(
        Single start, Single end, Single at, Single height, SKRect[] ink)
    {
        if (ink.Length == 0)
        {
            yield return (start, end);
            yield break;
        }

        var pad = Math.Max(1, height);
        var blocked = new List<(Single From, Single To)>();

        foreach (var box in ink)
        {
            if (box.Bottom > at && box.Top < at + height)
            {
                blocked.Add((box.Left - pad, box.Right + pad));
            }
        }

        var position = start;

        foreach (var stop in blocked.OrderBy(box => box.From))
        {
            if (stop.To > position && stop.From < end)
            {
                if (stop.From > position)
                {
                    yield return (position, Math.Min(stop.From, end));
                }

                position = Math.Max(position, stop.To);
            }

            if (position >= end)
            {
                yield break;
            }
        }

        if (position < end)
        {
            yield return (position, end);
        }
    }

    /// <summary>
    /// Gets where the glyphs of a run along a line put their ink, as the page sees it. The boxes the blob
    /// was built with are counted from the run's own start and baseline, which is the place it is drawn at.
    /// </summary>
    private SKRect[] Along(TextCommand command, Single baseline) =>
        Moved(Ink(command), (Single)command.Bounds.X, baseline);

    /// <summary>
    /// Gets where the glyphs of a run down a column put their ink. Text turned on its side is drawn as the
    /// blob of a line would be and turned about the point its column starts at, so its boxes are turned the
    /// same way; text kept upright is drawn as the column's own blob, moved by the room the em box leaves
    /// across the column. A run welded into the single em it stands for answers with nothing to skip, being
    /// one glyph's room of its own.
    /// </summary>
    private SKRect[] Beside(TextCommand command, FontMetrics metrics)
    {
        if (command.Flow.CombinesUpright)
        {
            return [];
        }

        var ink = Ink(command);

        if (command.Flow.Orientation == TextOrientation.Upright)
        {
            var emBox = metrics.Ascent + metrics.Descent;

            return Moved(
                ink,
                (Single)(command.Bounds.X + ((command.Bounds.Width - emBox) / 2)),
                (Single)command.Bounds.Y);
        }

        var baselineX = (Single)(command.Bounds.X + Axis(command, metrics));
        var upward = command.Flow.TurnsCounterClockwise;
        var turn = SKMatrix.CreateRotationDegrees(
            upward ? -90 : 90,
            baselineX,
            (Single)(upward ? command.Bounds.Bottom : command.Bounds.Y));

        return [.. Moved(ink, (Single)command.Bounds.X, (Single)(upward ? command.Bounds.Bottom : command.Bounds.Y))
            .Select(box => turn.MapRect(box))];
    }

    /// <summary>Gets the ink of this run's glyphs, of the blob they are drawn as.</summary>
    private SKRect[] Ink(TextCommand command) =>
        command.Flow.IsVertical && command.Flow.Orientation == TextOrientation.Upright
            ? _measurer.Blobs.GetColumnInk(
                command.Font,
                command.Text,
                command.LetterSpacing,
                command.Justification,
                command.Justifies,
                command.Expand,
                command.WordSpacing,
                command.Reversed ?? command.Flow.SetsTextBackwards,
                command.Trims,
                command.AutoSpaces,
                command.AutoSpaceRoom)
            : _measurer.Blobs.GetRunInk(
                command.Font,
                command.Text,
                command.LetterSpacing,
                command.Justification,
                command.Justifies,
                command.Expand,
                command.WordSpacing,
                command.Reversed ?? command.Flow.SetsTextBackwards,
                command.Trims,
                command.AutoSpaces,
                command.AutoSpaceRoom);

    /// <summary>Moves a run's boxes into the space the page is drawn in.</summary>
    private static SKRect[] Moved(SKRect[] boxes, Single x, Single y) =>
        [.. boxes.Select(box => new SKRect(
            box.Left + x,
            box.Top + y,
            box.Right + x,
            box.Bottom + y))];

    private static void Band(
        SKCanvas canvas,
        SKPaint paint,
        Single start,
        Single end,
        Single at,
        Single height,
        Boolean alongLine) =>
        canvas.DrawRect(alongLine
            ? new SKRect(start, at, end, at + height)
            : new SKRect(at, start, at + height, end), paint);

    /// <summary>
    /// Swings the decoration from side to side of the height it sits at, a half turn to a length of
    /// twice its own thickness.
    /// </summary>
    private static void Swing(
        SKCanvas canvas,
        SKPaint stroke,
        Single start,
        Single end,
        Single at,
        Single height,
        Boolean alongLine)
    {
        var step = Math.Max(height * 2, 2);

        using var path = new SKPath();
        path.MoveTo(Place(start, at, alongLine));

        var above = true;

        for (var position = start; position < end; position += step)
        {
            var next = Math.Min(position + step, end);
            var control = position + (step / 2);
            var swung = at + (above ? -height : height);

            path.QuadTo(
                alongLine ? control : swung,
                alongLine ? swung : control,
                alongLine ? next : at,
                alongLine ? at : next);

            above = !above;
        }

        canvas.DrawPath(path, stroke);
    }

    private static SKPoint Place(Single along, Single across, Boolean alongLine) =>
        alongLine ? new SKPoint(along, across) : new SKPoint(across, along);

    /// <summary>
    /// Gets where a column's glyphs stand on the axis across the column: the middle of the box for text
    /// kept upright, and for text turned down the column the place that puts its own em box on that same
    /// middle. A turned run is measured out from its baseline, which stands with its ascent towards one
    /// edge of the column and its descent towards the other, so the middle of that box lies half the
    /// difference of the two beyond the baseline (CSS Writing Modes 4 §4.2).
    /// </summary>
    private static Double Axis(TextCommand command, FontMetrics metrics)
    {
        var middle = command.Bounds.Width / 2;

        if (!command.Flow.IsSideways && command.Flow.Orientation != TextOrientation.Sideways)
        {
            return middle;
        }

        // The turn decides which edge the ascent reaches towards, so the baseline stands half the
        // box's difference on that side of the middle rather than always on the same one.
        var half = (metrics.Ascent - metrics.Descent) / 2;

        return command.Flow.TurnsCounterClockwise ? middle + half : middle - half;
    }

    /// <summary>
    /// Draws a run whose glyphs are all turned the same quarter turn, which is what Latin text and
    /// digits become in a column. The baseline runs down the box, so the turn is taken about the point
    /// the run starts at and the glyphs are then drawn exactly as they would be along a line.
    /// </summary>
    /// <remarks>
    /// A turn to the right reads downward and puts the ascent to the right of the baseline, which is
    /// where a column's baseline sits in the right-to-left modes; the modes that read upward turn the
    /// other way and put it on the left, which is the way <see cref="Axis"/> counts the turn when it
    /// places the baseline on that side of the column's middle.
    /// </remarks>
    private void DrawTurned(SKCanvas canvas, TextCommand command, FontMetrics metrics, SKPaint paint)
    {
        // A column of mixed or upright text stands on its central baseline, and a sideways run stands
        // there too: its em box is centred across the column like the upright glyphs beside it, which
        // puts its own baseline half an em-box's difference in from that middle (CSS Writing Modes 4
        // §4.2). Standing it on the alphabetic baseline instead leaves a run of Latin - or a lone
        // middle dot - off to one side of the column it sits in.
        var axis = Axis(command, metrics);
        var baselineX = (Single)(command.Bounds.X + axis);
        var upward = command.Flow.TurnsCounterClockwise;
        var start = (Single)(upward ? command.Bounds.Bottom : command.Bounds.Y);

        canvas.Save();
        canvas.RotateDegrees(upward ? -90 : 90, baselineX, start);

        // The blob is set out from its baseline, and a column begins where the first glyph's advance
        // does: the baseline stands a half-leading and a descent into the box, so the run is moved that
        // far along the column to put its first character at the start of the box rather than that much
        // above it.
        canvas.Translate((Single)axis, 0);
        DrawRunBlob(canvas, command, start, paint, command.LetterSpacing, command.WordSpacing);
        canvas.Restore();
    }

    /// <summary>
    /// Draws a run of characters that stay upright, one under the other, each owning the step the
    /// layout gave it and centred across the em box. The whole run is one blob the cache keeps: the
    /// cells are resolved once and every frame after the first is a single draw.
    /// </summary>
    /// <remarks>
    /// The decisions a cell is made of - the font that carries the character, the form the font
    /// substitutes for it down a column, where the font puts its baseline inside the cell, and the
    /// width it is centred by - all live in the measurer's decomposition, which is where the layout's
    /// own steps come from too. The executor only asks for the blob and draws it, moved by the room
    /// the em box leaves in the column so the characters sit centred across the column's width.
    /// </remarks>
    private void DrawUprightColumn(SKCanvas canvas, TextCommand command, FontMetrics metrics, SKPaint paint)
    {
        var blob = _measurer.Blobs.GetColumnBlob(
            command.Font,
            command.Text,
            command.LetterSpacing,
            command.Justification,
            command.Justifies,
            command.Expand,
            command.WordSpacing,
            command.Reversed ?? command.Flow.SetsTextBackwards,
            command.Trims,
            command.AutoSpaces,
            command.AutoSpaceRoom);

        if (blob is not null)
        {
            // The blob's glyphs are positioned relative to the column and centred across the em box,
            // so drawing it at the origin the column's width leaves the em box at lands each
            // character in the cell the layout gave it.
            var emBox = metrics.Ascent + metrics.Descent;
            var x = (Single)(command.Bounds.X + ((command.Bounds.Width - emBox) / 2));
            canvas.DrawText(blob, x, (Single)command.Bounds.Y, paint);
        }
    }

    /// <summary>
    /// Draws a run <c>text-combine-upright: all</c> welded into the single em it stands for, which it
    /// occupies however wide the characters would be along a line.
    /// </summary>
    private void DrawCombined(SKCanvas canvas, TextCommand command, FontMetrics metrics, SKPaint paint)
    {
        var natural = _measurer.MeasureWidth(command.Text, command.Font);

        if (natural <= 0)
        {
            return;
        }

        // The run is set horizontally, as if its characters were upright, whichever way the column
        // turns the ones around it: that is what combining is for. The whole group is shrunk when it
        // is wider than the em it was given, and left at its own size when it is not, so a lone digit
        // is not stretched out to fill the space (CSS Writing Modes 3 §5.1).
        var em = command.Bounds.Height;
        var scale = (Single)Math.Min(1, em / natural);
        var emBox = metrics.Ascent + metrics.Descent;
        var x = (Single)(command.Bounds.X + ((command.Bounds.Width - (natural * scale)) / 2));
        var baseline = (Single)(command.Bounds.Y + ((em - (emBox * scale)) / 2) + (metrics.Ascent * scale));

        canvas.Save();
        canvas.Translate(x, baseline);
        canvas.Scale(scale);
        canvas.Translate((Single)(-command.Bounds.X), 0);
        DrawRunBlob(canvas, command, 0, paint, 0, 0);
        canvas.Restore();
    }

    /// <summary>
    /// Paints a background kept to the shape of the text it is clipped to: the layers are drawn into a
    /// temporary layer over the box, the glyphs are then cut out of that layer - each glyph widened by
    /// its own stroke - and the result is laid onto the page. Where the stroke is only transparent, the
    /// ring paints nothing yet still lets more of the background through than the bare glyph would.
    /// </summary>
    private void DrawTextClippedBackground(SKCanvas canvas, TextClippedBackgroundCommand command)
    {
        var box = ToRect(command.Bounds);

        // The background is drawn into a layer, then multiplied by a second layer that holds only the
        // ink of the glyphs. That ink layer is filled and stroked over transparent with the plain
        // source-over rule, so its alpha is the union of the two - a stroke widening the shape the
        // background is kept to - and it is brought down onto the background with DstIn, which keeps the
        // background exactly where the ink reaches and clears it where it does not.
        var background = canvas.SaveLayer(box, null);

        foreach (var inner in command.Layers)
        {
            DrawBackgroundLayer(canvas, inner);
        }

        using var ink = new SKPaint { Color = SKColors.Black, IsAntialias = true, Style = SKPaintStyle.Fill };
        using var ring = new SKPaint
        {
            Color = SKColors.Black,
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeJoin = SKStrokeJoin.Round,
        };

        using var mask = new SKPaint { BlendMode = SKBlendMode.DstIn };

        var layer = canvas.SaveLayer(box, mask);

        foreach (var run in command.Mask)
        {
            StrokeGlyphs(canvas, run, ink);

            if (run.StrokeWidth > 0)
            {
                ring.StrokeWidth = (Single)run.StrokeWidth;
                StrokeGlyphs(canvas, run, ring);
            }
        }

        canvas.RestoreToCount(layer);
        canvas.RestoreToCount(background);
    }

    /// <summary>Draws one background command - a colour, a gradient or an image - into the layer at hand.</summary>
    private void DrawBackgroundLayer(SKCanvas canvas, PaintCommand command)
    {
        switch (command)
        {
            case FillCommand fill:
                Fill(canvas, fill);
                break;

            case GradientCommand gradient:
                DrawGradient(canvas, gradient);
                break;

            case BackgroundImageCommand background:
                _images?.DrawBackground(canvas, background.Layer, ToRect(background.Placement), ToRect(background.Bounds), background.Radii);
                break;

            case BorderImageCommand border:
                DrawBorderImage(canvas, border);
                break;
        }
    }

    /// <summary>
    /// Draws one gradient layer over the tiles it fills, kept inside the background positioning area.
    /// A gradient has no picture to place, so a tile of it is not a piece of a larger drawing but the
    /// whole gradient again - the line it runs along is the one that box asks for.
    /// </summary>


    /// <summary>
    /// Draws a border image: the picture cut into nine regions and each region scaled, tiled and drawn
    /// into the room its width gives it (CSS Backgrounds 3 §5.6).
    /// </summary>
    /// <remarks>
    /// The algorithm is the chapter's four steps, and the order of them is what makes the picture look
    /// right: every piece is first scaled so that the slice it came from is as thick as the border-image
    /// width, which is what keeps a 2-pixel ring in a 12-pixel source a 12-pixel-thick frame; then the
    /// repeat keyword stretches or rescales each piece along its own axis; then the tiles are placed and
    /// drawn. A picture with no size of its own - a gradient - is drawn over the whole area first, so its
    /// slices are measures of that drawing.
    /// </remarks>
    private void DrawBorderImage(SKCanvas canvas, BorderImageCommand command)
    {
        var area = ToRect(command.Area);

        if (area.Width <= 0 || area.Height <= 0)
        {
            return;
        }

        using var picture = Picture(command, area);

        if (picture is null)
        {
            return;
        }

        var image = picture.Image;
        var source = new SKRect(0, 0, picture.Size.Width, picture.Size.Height);
        var widths = command.Widths;
        var top = (Single)Math.Max(0, widths.Top);
        var right = (Single)Math.Max(0, widths.Right);
        var bottom = (Single)Math.Max(0, widths.Bottom);
        var left = (Single)Math.Max(0, widths.Left);

        // The nine cuts of the picture. A pair that comes to more than the picture is scaled down together,
        // so that the middle of it keeps its share (CSS Backgrounds 3 §5.2).
        var slices = command.Image.Slices;
        var cutX = new[] { (Single)slices[3].Resolve(source.Width), (Single)slices[1].Resolve(source.Width) };
        var cutY = new[] { (Single)slices[0].Resolve(source.Height), (Single)slices[2].Resolve(source.Height) };

        if (cutX[0] + cutX[1] > source.Width && cutX[0] + cutX[1] > 0)
        {
            var scale = source.Width / (cutX[0] + cutX[1]);
            cutX[0] *= scale;
            cutX[1] *= scale;
        }

        if (cutY[0] + cutY[1] > source.Height && cutY[0] + cutY[1] > 0)
        {
            var scale = source.Height / (cutY[0] + cutY[1]);
            cutY[0] *= scale;
            cutY[1] *= scale;
        }

        var inner = new SKRect(area.Left + left, area.Top + top, area.Right - right, area.Bottom - bottom);

        // Step one: each edge is made as thick as its room, and as long as its own slice was - so a slice
        // that is thinner than the room it fills is stretched towards the corners along with the edge. The
        // middle follows the top and the left, which is what keeps the picture's proportions across it.
        var scaleTop = cutY[0] > 0 ? top / cutY[0] : 0;
        var scaleBottom = cutY[1] > 0 ? bottom / cutY[1] : 0;
        var scaleLeft = cutX[0] > 0 ? left / cutX[0] : 0;
        var scaleRight = cutX[1] > 0 ? right / cutX[1] : 0;

        var scaleAcross = scaleTop is > 0 and < Single.PositiveInfinity
            ? scaleTop
            : scaleBottom is > 0 and < Single.PositiveInfinity ? scaleBottom : 1;
        var scaleDown = scaleLeft is > 0 and < Single.PositiveInfinity
            ? scaleLeft
            : scaleRight is > 0 and < Single.PositiveInfinity ? scaleRight : 1;

        var edgeX = source.Width - cutX[0] - cutX[1];
        var edgeY = source.Height - cutY[0] - cutY[1];

        // A border image is not affected by border-radius (CSS Backgrounds 3, Corner Clipping): the
        // picture is cut into rectangles and drawn over the area as it stands, square corners and all.
        canvas.Save();
        canvas.ClipRect(area);

        using var paint = new SKPaint { IsAntialias = true };

        // The middle first, then the edges, then the corners, so that a corner is never painted over.
        if (command.Image.Fill)
        {
            DrawRegion(
                canvas,
                image,
                new SKRect(cutX[0], cutY[0], source.Width - cutX[1], source.Height - cutY[1]),
                inner,
                edgeX * scaleAcross,
                edgeY * scaleDown,
                command.Image.RepeatX,
                command.Image.RepeatY,
                paint);
        }

        DrawRegion(
            canvas,
            image,
            new SKRect(cutX[0], 0, source.Width - cutX[1], cutY[0]),
            new SKRect(inner.Left, area.Top, inner.Right, area.Top + top),
            edgeX * scaleTop,
            top,
            command.Image.RepeatX,
            BorderImageRepeat.Stretch,
            paint);

        DrawRegion(
            canvas,
            image,
            new SKRect(cutX[0], source.Height - cutY[1], source.Width - cutX[1], source.Height),
            new SKRect(inner.Left, area.Bottom - bottom, inner.Right, area.Bottom),
            edgeX * scaleBottom,
            bottom,
            command.Image.RepeatX,
            BorderImageRepeat.Stretch,
            paint);

        DrawRegion(
            canvas,
            image,
            new SKRect(0, cutY[0], cutX[0], source.Height - cutY[1]),
            new SKRect(area.Left, inner.Top, area.Left + left, inner.Bottom),
            left,
            edgeY * scaleLeft,
            BorderImageRepeat.Stretch,
            command.Image.RepeatY,
            paint);

        DrawRegion(
            canvas,
            image,
            new SKRect(source.Width - cutX[1], cutY[0], source.Width, source.Height - cutY[1]),
            new SKRect(area.Right - right, inner.Top, area.Right, inner.Bottom),
            right,
            edgeY * scaleRight,
            BorderImageRepeat.Stretch,
            command.Image.RepeatY,
            paint);

        // The corners are made as wide and as tall as their regions, whatever the slices say.
        DrawRegion(canvas, image, new SKRect(0, 0, cutX[0], cutY[0]),
            new SKRect(area.Left, area.Top, area.Left + left, area.Top + top),
            left, top, BorderImageRepeat.Stretch, BorderImageRepeat.Stretch, paint);

        DrawRegion(canvas, image, new SKRect(source.Width - cutX[1], 0, source.Width, cutY[0]),
            new SKRect(area.Right - right, area.Top, area.Right, area.Top + top),
            right, top, BorderImageRepeat.Stretch, BorderImageRepeat.Stretch, paint);

        DrawRegion(canvas, image, new SKRect(0, source.Height - cutY[1], cutX[0], source.Height),
            new SKRect(area.Left, area.Bottom - bottom, area.Left + left, area.Bottom),
            left, bottom, BorderImageRepeat.Stretch, BorderImageRepeat.Stretch, paint);

        DrawRegion(canvas, image, new SKRect(source.Width - cutX[1], source.Height - cutY[1], source.Width, source.Height),
            new SKRect(area.Right - right, area.Bottom - bottom, area.Right, area.Bottom),
            right, bottom, BorderImageRepeat.Stretch, BorderImageRepeat.Stretch, paint);

        canvas.Restore();
    }

    /// <summary>Gets the picture a border image is cut from, and the size its slices are measures of.</summary>
    private BorderImagePicture? Picture(BorderImageCommand command, SKRect area)
    {
        if (command.Gradient is { } gradient && gradient.Stops.Count > 0)
        {
            var width = (Int32)Math.Ceiling(area.Width);
            var height = (Int32)Math.Ceiling(area.Height);

            if (width <= 0 || height <= 0)
            {
                return null;
            }

            var colors = new SKColor[gradient.Stops.Count];
            var positions = new Single[gradient.Stops.Count];

            for (var index = 0; index < gradient.Stops.Count; index++)
            {
                colors[index] = new SKColor(gradient.Stops[index].Color);
                positions[index] = (Single)Math.Clamp(gradient.Stops[index].Offset, 0, 1);
            }

            var surface = SKSurface.Create(new SKImageInfo(width, height));
            surface.Canvas.Clear(SKColors.Transparent);
            surface.Canvas.Translate(-area.Left, -area.Top);

            // The picture is drawn over the whole bitmap rather than over the area it came from: an area
            // 146.67 wide is rasterized 147 pixels wide, and a picture stopping at 146.67 would leave its
            // last column - the very column a slice of the right edge is cut from - half transparent.
            DrawGradientTile(
                surface.Canvas,
                new SKRect(area.Left, area.Top, area.Left + width, area.Top + height),
                colors,
                positions,
                gradient);

            surface.Canvas.Flush();

            return new BorderImagePicture(surface.Snapshot(), new SKSize(width, height), surface);
        }

        if (command.Image.Url is { Length: > 0 } url && _images is not null &&
            _images.TryRaster(url, out var image, out var size) && image is not null)
        {
            return new BorderImagePicture(image, new SKSize((Single)size.Width, (Single)size.Height), null);
        }

        return null;
    }

    /// <summary>
    /// Draws one region of the border image: the cut of the picture, already scaled by the width it is
    /// drawn into, tiled across the room the way the page asked along each axis (CSS Backgrounds 3 §5.6).
    /// The room clips what does not fit, which is how a <c>repeat</c> ending mid-copy is cut off.
    /// </summary>
    private static void DrawRegion(
        SKCanvas canvas,
        SKImage image,
        SKRect source,
        SKRect region,
        Single naturalWidth,
        Single naturalHeight,
        BorderImageRepeat across,
        BorderImageRepeat down,
        SKPaint paint)
    {
        if (region.Width <= 0 || region.Height <= 0 || source.Width <= 0 || source.Height <= 0)
        {
            return;
        }

        var columns = Tiles(region.Left, region.Width, naturalWidth, across);
        var rows = Tiles(region.Top, region.Height, naturalHeight, down);

        if (columns.Count == 0 || rows.Count == 0)
        {
            return;
        }

        // The cut is taken out of the picture as a picture of its own, so that the filtering of a scaled
        // piece stops at its edges: a bilinear tap that reached into the neighbouring region would pull
        // that region's colour across the cut, and a frame cut two pixels into a picture shows a fringe
        // of the picture's middle that no browser draws.
        var cut = SKRectI.Round(source);

        if (cut.Width <= 0 || cut.Height <= 0)
        {
            return;
        }

        using var piece = image.Subset(cut);

        if (piece is null)
        {
            return;
        }

        var local = new SKRect(0, 0, piece.Width, piece.Height);
        var sampling = new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None);

        canvas.Save();
        canvas.ClipRect(region);

        foreach (var (x, width) in columns)
        {
            foreach (var (y, height) in rows)
            {
                canvas.DrawImage(piece, local, new SKRect(x, y, x + width, y + height), sampling, paint);
            }
        }

        canvas.Restore();
    }

    /// <summary>
    /// Lays the copies of a cut out along one axis of its room (CSS Backgrounds 3 §5.6): one copy stretched
    /// over the room, copies at their own size with the first centred, copies rescaled so a whole number
    /// fits, or copies at their own size with the room left over shared equally before, after and between
    /// them - which may leave the room empty.
    /// </summary>
    private static List<(Single At, Single Size)> Tiles(Single from, Single length, Single natural, BorderImageRepeat repeat)
    {
        var tiles = new List<(Single, Single)>();

        if (natural <= 0 || length <= 0)
        {
            return tiles;
        }

        switch (repeat)
        {
            case BorderImageRepeat.Repeat:
                // The first copy is centred, and the copies either side of it are cut off by the room.
                var first = from + (length - natural) / 2;

                while (first > from)
                {
                    first -= natural;
                }

                for (var at = first; at < from + length; at += natural)
                {
                    tiles.Add((at, natural));
                }

                break;

            case BorderImageRepeat.Round:
                var whole = Math.Max(1, (Int32)Math.Round(length / natural));
                var size = length / whole;

                for (var index = 0; index < whole; index++)
                {
                    tiles.Add((from + index * size, size));
                }

                break;

            case BorderImageRepeat.Space:
                // Whole copies only, with the room left over shared equally before, after and between them.
                var count = (Int32)Math.Floor(length / natural);

                if (count < 1)
                {
                    break;
                }

                var gap = (length - count * natural) / (count + 1);

                for (var index = 0; index < count; index++)
                {
                    tiles.Add((from + gap + index * (natural + gap), natural));
                }

                break;

            default:
                tiles.Add((from, length));
                break;
        }

        return tiles;
    }

    /// <summary>A picture a border image is cut from, with the surface it was drawn into when it is a gradient.</summary>
    private sealed record BorderImagePicture(SKImage Image, SKSize Size, SKSurface? Surface) : IDisposable
    {
        public void Dispose() => Surface?.Dispose();
    }

    private static void DrawGradient(SKCanvas canvas, GradientCommand command)
    {
        if (command.Stops.Count == 0)
        {
            return;
        }

        var area = ToRect(command.Bounds);
        var tile = ToRect(command.Tile);

        // A gradient tile is a background the same way a fill is, and covers whole pixels the same
        // way: an edge inside a pixel row is anti-aliased there while the same edge reached as a clip
        // boundary is not, and the two pictures then part over one faint row of the box. Rounded
        // corners keep their own edges, which are meant to be soft (CSS Backgrounds 3 §2.10).
        if (command.Radii.IsZero)
        {
            var alignedArea = Snap(area);
            var alignedTile = Snap(tile);

            if (alignedTile.Width >= 1 && alignedTile.Height >= 1)
            {
                area = alignedArea;
                tile = alignedTile;
            }
        }

        if (area.Width <= 0 || area.Height <= 0 || tile.Width <= 0 || tile.Height <= 0)
        {
            return;
        }

        var colors = new SKColor[command.Stops.Count];
        var positions = new Single[command.Stops.Count];

        for (var index = 0; index < command.Stops.Count; index++)
        {
            colors[index] = new SKColor(command.Stops[index].Color);
            positions[index] = (Single)Math.Clamp(command.Stops[index].Offset, 0, 1);
        }

        canvas.Save();
        SkiaRounded.Clip(canvas, area, command.Radii);

        // Each axis repeats on its own terms, the way an image layer does, and a tile that stands
        // wholly outside the area is clipped away by the same clip that keeps the rest of it.
        var firstX = command.RepeatX ? SkiaImageLoader.FirstTile(tile.Left, area.Left, tile.Width) : tile.Left;
        var lastX = command.RepeatX ? area.Right : tile.Right;
        var firstY = command.RepeatY ? SkiaImageLoader.FirstTile(tile.Top, area.Top, tile.Height) : tile.Top;
        var lastY = command.RepeatY ? area.Bottom : tile.Bottom;

        for (var y = firstY; y < lastY; y += tile.Height)
        {
            for (var x = firstX; x < lastX; x += tile.Width)
            {
                DrawGradientTile(
                    canvas,
                    new SKRect(x, y, x + tile.Width, y + tile.Height),
                    colors,
                    positions,
                    command);
            }
        }

        canvas.Restore();
    }

    private static void DrawGradientTile(
        SKCanvas canvas,
        SKRect box,
        SKColor[] colors,
        Single[] positions,
        GradientCommand command)
    {
        using var shader = command.Shape switch
        {
            GradientShape.Conic => SweepShader(box, colors, positions, command),
            GradientShape.Radial => RadialShader(box, colors, positions, command),
            _ => LinearShader(box, colors, positions, command.AngleDegrees),
        };

        using var paint = new SKPaint { Shader = shader, IsAntialias = true };

        canvas.DrawRect(box, paint);
    }

    /// <summary>
    /// Turns one radial gradient: the ending shape the core settled is drawn as a unit circle squashed onto
    /// it, so a stop's fraction of that shape lands where CSS Images 3 §3.4 stands it - along the radius
    /// for a circle, and along the corresponding axis for an ellipse.
    /// </summary>
    private static SKShader RadialShader(SKRect box, SKColor[] colors, Single[] positions, GradientCommand command)
    {
        var center = new SKPoint(
            box.Left + (Single)(command.CenterX - command.Tile.X),
            box.Top + (Single)(command.CenterY - command.Tile.Y));
        var radiusX = Math.Max((Single)command.RadiusX, 1e-4f);
        var radiusY = Math.Max((Single)command.RadiusY, 1e-4f);

        // The shader is built on a unit circle at the origin and sent to the ending shape by the matrix,
        // which is what an ellipse is: a circle with one axis stretched. A degenerate radius is kept off
        // zero because Skia answers a zero-radius gradient with nothing at all, and a shape flattened to
        // a line still paints the colour its last stop carries.
        var toShape = SKMatrix.CreateScaleTranslation(radiusX, radiusY, center.X, center.Y);

        return SKShader.CreateRadialGradient(
            SKPoint.Empty,
            1f,
            colors,
            positions,
            SKShaderTileMode.Clamp,
            toShape);
    }

    private static SKShader LinearShader(SKRect box, SKColor[] colors, Single[] positions, Double angleDegrees)
    {
        var (start, end) = GradientLine(box, angleDegrees);

        return SKShader.CreateLinearGradient(start, end, colors, positions, SKShaderTileMode.Clamp);
    }

    /// <summary>
    /// Turns one sweep of a conic gradient: the colours stand at the angles the page gave them, and the
    /// whole turn is what the stops were resolved over. Skia counts its angles from three o'clock and CSS
    /// from twelve, which is a quarter turn apart, and the centre the page placed - or left in the middle
    /// of the box - is the point the sweep turns about (CSS Images 3 §3.4).
    /// </summary>
    private static SKShader SweepShader(SKRect box, SKColor[] colors, Single[] positions, GradientCommand command)
    {
        var center = new SKPoint(
            box.Left + (Single)(command.CenterX - command.Tile.X),
            box.Top + (Single)(command.CenterY - command.Tile.Y));
        // Skia's sweep begins at three o'clock and CSS begins at twelve, so the shader is turned by the
        // quarter turn between them plus whatever angle the page wrote; the sweep itself covers the whole
        // turn, which is what the stops were resolved over.
        // The sweep is turned rather than started at an angle: Skia maps the colours onto an arc that
        // begins at three o'clock, and CSS counts its angles from twelve, so the quarter turn between
        // them - and whatever angle the page wrote - goes on the shader's own matrix. Asked for as a
        // start angle, a sweep that begins before three o'clock puts every colour outside the arc, and
        // the box comes back the last colour of the list.
        var turn = SKMatrix.CreateRotationDegrees((Single)(command.AngleDegrees - 90), center.X, center.Y);

        return SKShader.CreateSweepGradient(
            center,
            colors,
            positions,
            SKShaderTileMode.Clamp,
            0f,
            360f,
            turn);
    }

    /// <summary>
    /// Maps a CSS gradient angle onto the gradient line: 0deg points up and 90deg to the right, and
    /// the line runs through the centre with the length the direction projects onto the box. The core
    /// settles that length, because it is the same length the colour stops were resolved against.
    /// </summary>
    private static (SKPoint Start, SKPoint End) GradientLine(SKRect box, Double angleDegrees)
    {
        var radians = angleDegrees * Math.PI / 180.0;
        var dx = (Single)Math.Sin(radians);
        var dy = (Single)(-Math.Cos(radians));
        var length = (Single)GradientSpec.LineLength(box.Width, box.Height, angleDegrees);
        var halfX = dx * length / 2;
        var halfY = dy * length / 2;

        return (
            new SKPoint(box.MidX - halfX, box.MidY - halfY),
            new SKPoint(box.MidX + halfX, box.MidY + halfY));
    }

    private static SKRect ToRect(Rect rect) =>
        new((Single)rect.X, (Single)rect.Y, (Single)rect.Right, (Single)rect.Bottom);

    /// <summary>
    /// Folds the platform-neutral affine into a Skia matrix (CSS matrix(a, b, c, d, e, f)).
    /// </summary>
    /// <remarks>
    /// Skia reads its nine numbers a row at a time, while CSS names the two shear terms in the order the
    /// axes are written: b says how far y is carried into x and c how far x into y, and Skia puts c in the
    /// first row's middle and b in the second's. Read the other way the two are exchanged, which turns a
    /// clockwise quarter turn into an anti-clockwise one and a skew of one axis into a skew of the other.
    /// </remarks>
    private static SKBlendMode ToSkBlend(BlendMode blend) => blend switch
    {
        BlendMode.Multiply => SKBlendMode.Multiply,
        BlendMode.Screen => SKBlendMode.Screen,
        BlendMode.Overlay => SKBlendMode.Overlay,
        BlendMode.Darken => SKBlendMode.Darken,
        BlendMode.Lighten => SKBlendMode.Lighten,
        BlendMode.ColorDodge => SKBlendMode.ColorDodge,
        BlendMode.ColorBurn => SKBlendMode.ColorBurn,
        BlendMode.HardLight => SKBlendMode.HardLight,
        BlendMode.SoftLight => SKBlendMode.SoftLight,
        BlendMode.Difference => SKBlendMode.Difference,
        BlendMode.Exclusion => SKBlendMode.Exclusion,
        BlendMode.Hue => SKBlendMode.Hue,
        BlendMode.Saturation => SKBlendMode.Saturation,
        BlendMode.Color => SKBlendMode.Color,
        BlendMode.Luminosity => SKBlendMode.Luminosity,
        BlendMode.PlusLighter => SKBlendMode.Plus,
        _ => SKBlendMode.SrcOver,
    };

    private static SKMatrix ToSkMatrix(Affine m) => new(
        (Single)m.A, (Single)m.C, (Single)m.E,
        (Single)m.B, (Single)m.D, (Single)m.F,
        (Single)m.P0, (Single)m.P1, (Single)m.P2);
}

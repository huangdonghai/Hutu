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
        // of one box part over a single row of it. Snapping the edges puts both on the same terms.
        var area = command.Radii.IsZero ? Snap(ToRect(command.Bounds)) : ToRect(command.Bounds);

        SkiaRounded.Fill(canvas, area, command.Radii, paint);
    }

    /// <summary>Moves each edge of an area onto the whole pixel it is nearest to.</summary>
    private static SKRect Snap(SKRect rect) => new(
        (Single)Math.Round(rect.Left),
        (Single)Math.Round(rect.Top),
        (Single)Math.Round(rect.Right),
        (Single)Math.Round(rect.Bottom));

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

        if (command.HasUniformColor() && command.Styles.IsUniform && command.Widths.IsUniform())
        {
            DrawBorderStroke(canvas, command.Widths.Top, command.TopColor, command.Styles.Top, ring, middle);
            return;
        }

        foreach (var side in BorderSides.All)
        {
            canvas.Save();
            canvas.ClipPath(SideQuad(command.Bounds, command.Widths, side), SKClipOperation.Intersect, antialias: true);
            DrawBorderStroke(canvas, command.Widths.Side(side), command.ColorOf(side), command.Styles.Side(side), ring, middle);
            canvas.Restore();
        }
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

        DrawSide(canvas, new Rect(box.X, box.Y, box.Width, widths.Top), command.TopColor);

        if (widths.Bottom > 0)
        {
            DrawSide(canvas, new Rect(box.X, box.Bottom - widths.Bottom, box.Width, widths.Bottom), command.BottomColor);
        }

        if (widths.Left > 0)
        {
            DrawSide(canvas, new Rect(box.X, box.Y, widths.Left, box.Height), command.LeftColor);
        }

        if (widths.Right > 0)
        {
            DrawSide(canvas, new Rect(box.Right - widths.Right, box.Y, widths.Right, box.Height), command.RightColor);
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
    /// shrunk with a negative inset; a corner that is square has nothing to grow and stays square.
    /// </summary>
    private static CornerRadii Grow(CornerRadii radii, Double distance) =>
        radii.Shrink(new Thickness(-distance, -distance, -distance, -distance));

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

    private static void DrawSide(SKCanvas canvas, Rect side, UInt32 color)
    {
        if (side.Width <= 0 || side.Height <= 0)
        {
            return;
        }

        using var paint = new SKPaint { Color = new SKColor(color), IsAntialias = false };
        canvas.DrawRect(ToRect(side), paint);
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
        // front of them.
        var across = (Single)command.Baseline;
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

        DrawTurned(canvas, command, paint);
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

            if (!through && decoration.Underline)
            {
                // The face gives the line its zero position; text-underline-offset moves it outward
                // from there, and text-decoration-thickness replaces the face's own width
                // (CSS Text Decoration 4 §2.4, §2.8).
                DrawLine(
                    canvas,
                    paint,
                    command,
                    (Single)(origin + stood + (sign * (
                        (placed?.UnderlineOffset ?? Math.Abs(metrics.UnderlineOffset))
                        + (decoration.UnderlineOffset ?? 0)))),
                    Thickness(decoration.Thickness, placed?.UnderlineThickness ?? metrics.UnderlineThickness),
                    decoration.Shape,
                    alongLine,
                    pulls,
                    pushes,
                    blocked);
            }

            // The face says where its overline goes; a face that will not is answered with the top of
            // its own em box, which is the only other height the line could be taken from.
            if (!through && decoration.Overline)
            {
                DrawLine(
                    canvas,
                    paint,
                    command,
                    (Single)(origin + stood - (sign * (placed?.OverlineOffset
                        ?? (metrics.OverlineOffset > 0
                            ? Math.Abs(metrics.OverlineOffset)
                            : metrics.Ascent)))),
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
                    blocked);
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
                    blocked);
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
            DrawTurned(canvas, command, stroke);
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
            wordSpacing);

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
        SKRect[] ink)
    {
        var height = (Single)thickness;
        var start = (Single)(alongLine ? command.Bounds.X : command.Bounds.Y);
        var end = (Single)(alongLine ? command.Bounds.Right : command.Bounds.Bottom);

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

        var baselineX = (Single)(command.Bounds.X + command.Baseline);
        var upward = command.Flow.InlineSign < 0;
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
                command.WordSpacing)
            : _measurer.Blobs.GetRunInk(
                command.Font,
                command.Text,
                command.LetterSpacing,
                command.Justification,
                command.Justifies,
                command.Expand,
                command.WordSpacing);

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
    /// Draws a run whose glyphs are all turned the same quarter turn, which is what Latin text and
    /// digits become in a column. The baseline runs down the box, so the turn is taken about the point
    /// the run starts at and the glyphs are then drawn exactly as they would be along a line.
    /// </summary>
    /// <remarks>
    /// A turn to the right reads downward and puts the ascent to the right of the baseline, which is
    /// where a column's baseline sits in the right-to-left modes; the modes that read upward turn the
    /// other way and put it on the left, and the baseline the layout handed over is already on that
    /// side of the box.
    /// </remarks>
    private void DrawTurned(SKCanvas canvas, TextCommand command, SKPaint paint)
    {
        var baselineX = (Single)(command.Bounds.X + command.Baseline);
        var upward = command.Flow.InlineSign < 0;
        var start = (Single)(upward ? command.Bounds.Bottom : command.Bounds.Y);

        canvas.Save();
        canvas.RotateDegrees(upward ? -90 : 90, baselineX, start);
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
            command.WordSpacing);

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

        // The run was measured as one em, so squeezing the glyphs along the axis they are set on until
        // they are that long is what makes them line up with the characters around them.
        var squeeze = (Single)(command.Bounds.Height / natural);
        var baselineX = (Single)(command.Bounds.X + command.Baseline);

        canvas.Save();

        if (command.Flow.Orientation == TextOrientation.Upright)
        {
            // Upright characters lie side by side across the column; centring the group and squeezing
            // it sideways keeps them inside the em without turning them.
            var emBox = metrics.Ascent + metrics.Descent;
            var y = (Single)(command.Bounds.Y + ((command.Bounds.Height - emBox) / 2) + metrics.Ascent);
            var x = (Single)(command.Bounds.X + ((command.Bounds.Width - natural) / 2));

            canvas.Scale(squeeze, 1, x, (Single)command.Bounds.Y);
            DrawRunBlob(canvas, command, y, paint, 0, 0);
        }
        else
        {
            var start = (Single)command.Bounds.Y;

            canvas.RotateDegrees(90, baselineX, start);
            canvas.Scale(squeeze, 1, baselineX, start);
            DrawRunBlob(canvas, command, start, paint, 0, 0);
        }

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
        }
    }

    /// <summary>
    /// Draws one gradient layer over the tiles it fills, kept inside the background positioning area.
    /// A gradient has no picture to place, so a tile of it is not a piece of a larger drawing but the
    /// whole gradient again - the line it runs along is the one that box asks for.
    /// </summary>
    private static void DrawGradient(SKCanvas canvas, GradientCommand command)
    {
        if (command.Stops.Count == 0)
        {
            return;
        }

        var area = ToRect(command.Bounds);
        var tile = ToRect(command.Tile);

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

    /// <summary>Folds the platform-neutral affine into a Skia matrix (CSS matrix(a, b, c, d, e, f)).</summary>
    private static SKMatrix ToSkMatrix(Affine m) => new(
        (Single)m.A, (Single)m.B, (Single)m.E,
        (Single)m.C, (Single)m.D, (Single)m.F,
        0, 0, 1);
}

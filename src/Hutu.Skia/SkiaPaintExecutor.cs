using Hutu.Rendering;
using Hutu.Rendering.Layout;
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
                    _images?.DrawBackground(canvas, background.Layer, ToRect(background.Bounds), background.Radii);
                    break;

                case LinearGradientCommand gradient:
                    DrawGradient(canvas, gradient);
                    break;

                case TextClippedBackgroundCommand clipped:
                    DrawTextClippedBackground(canvas, clipped);
                    break;

                case PushClipCommand clip:
                    canvas.Save();
                    SkiaRounded.Clip(canvas, ToRect(clip.Bounds), clip.Radii);
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

                case TextCommand text:
                    DrawText(canvas, text);
                    break;
            }
        }
    }

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
        if (command is PushClipCommand or PopClipCommand or PushTransformCommand or PopTransformCommand)
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
    /// Draws the border of a box.
    /// </summary>
    /// <remarks>
    /// A border follows the corner radii of the box, so it is the area between two rounded rectangles -
    /// the border box outside and the padding box inside - rather than four rectangles that overlap at
    /// the corners. Sides that are all alike are drawn in one pass; when they differ, each side is
    /// confined to its own quarter by the diagonals the corner would miter along. A side that is dashed
    /// or dotted is stroked along the middle of the border instead of filled, which is what lets the
    /// pattern run around the curve; on a corner whose radius is smaller than half the border width
    /// that middle line degenerates and the pattern straightens out, which is the one visible
    /// approximation of the two.
    /// </remarks>
    private static void DrawBorder(SKCanvas canvas, BorderCommand command)
    {
        // The plain case - square corners and four unbroken strokes - is by far the most common one on
        // a page, and four rectangles are a good deal cheaper than two rounded paths.
        if (command.Radii.IsZero && command.Styles.IsSolid)
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
    private void DrawText(SKCanvas canvas, TextCommand command)
    {
        var metrics = _measurer.GetMetrics(command.Font);

        using var paint = new SKPaint { Color = new SKColor(command.Color), IsAntialias = true };

        // A text stroke rings each glyph, drawn before - and so behind - the fill. A transparent stroke
        // paints nothing of its own and is skipped; its width still matters, but only to a background
        // clipped to the text, which widens to it in its own pass.
        if (command.StrokeWidth > 0 && (command.StrokeColor & 0xFF00_0000) != 0)
        {
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

        if (!command.Flow.IsVertical)
        {
            var baseline = (Single)(command.Bounds.Y + command.Baseline);
            DrawRunBlob(canvas, command, baseline, paint, command.LetterSpacing);

            if (command.Underline)
            {
                DrawLine(
                    canvas,
                    paint,
                    command,
                    (Single)(baseline + Math.Abs(metrics.UnderlineOffset)),
                    Math.Max(1, metrics.UnderlineThickness),
                    command.Decoration,
                    alongLine: true);
            }

            // The face says where its overline goes; a face that will not is answered with the top of
            // its own em box, which is the only other height the line could be taken from.
            if (command.Overline)
            {
                DrawLine(
                    canvas,
                    paint,
                    command,
                    (Single)(baseline - (metrics.OverlineOffset > 0
                        ? Math.Abs(metrics.OverlineOffset)
                        : metrics.Ascent)),
                    Math.Max(1, metrics.OverlineThickness > 0
                        ? metrics.OverlineThickness
                        : metrics.UnderlineThickness),
                    command.Decoration,
                    alongLine: true);
            }

            if (command.LineThrough)
            {
                DrawLine(
                    canvas,
                    paint,
                    command,
                    (Single)(baseline - Math.Abs(metrics.StrikeoutOffset)),
                    Math.Max(1, metrics.StrikeoutThickness),
                    command.Decoration,
                    alongLine: true);
            }

            return;
        }

        if (command.Flow.CombinesUpright)
        {
            DrawCombined(canvas, command, metrics, paint);
        }
        else if (command.Flow.Orientation == TextOrientation.Upright)
        {
            DrawUprightColumn(canvas, command, metrics, paint);
        }
        else
        {
            DrawTurned(canvas, command, paint);
        }

        // In a column a decoration runs beside the text rather than under it, so it becomes a line
        // down the box at the distance from the baseline that the font asked for.
        if (command.Underline)
        {
            DrawLine(
                canvas,
                paint,
                command,
                (Single)(command.Bounds.X + command.Baseline - Math.Abs(metrics.UnderlineOffset)),
                Math.Max(1, metrics.UnderlineThickness),
                command.Decoration,
                alongLine: false);
        }

        if (command.Overline)
        {
            DrawLine(
                canvas,
                paint,
                command,
                (Single)(command.Bounds.X + command.Baseline + (metrics.OverlineOffset > 0
                    ? Math.Abs(metrics.OverlineOffset)
                    : metrics.Ascent)),
                Math.Max(1, metrics.OverlineThickness > 0
                    ? metrics.OverlineThickness
                    : metrics.UnderlineThickness),
                command.Decoration,
                alongLine: false);
        }

        if (command.LineThrough)
        {
            DrawLine(
                canvas,
                paint,
                command,
                (Single)(command.Bounds.X + command.Baseline + Math.Abs(metrics.StrikeoutOffset)),
                Math.Max(1, metrics.StrikeoutThickness),
                command.Decoration,
                alongLine: false);
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
            DrawRunBlob(canvas, command, baseline, stroke, command.LetterSpacing);
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
    private void DrawRunBlob(SKCanvas canvas, TextCommand command, Single baseline, SKPaint paint, Double letterSpacing)
    {
        var blob = _measurer.Blobs.GetRunBlob(
            command.Font,
            command.Text,
            letterSpacing,
            command.Justification,
            command.Justifies);

        if (blob is not null)
        {
            canvas.DrawText(blob, (Single)command.Bounds.X, baseline, paint);
        }
    }

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
        Boolean alongLine)
    {
        var height = (Single)thickness;
        var start = (Single)(alongLine ? command.Bounds.X : command.Bounds.Y);
        var end = (Single)(alongLine ? command.Bounds.Right : command.Bounds.Bottom);

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
        DrawRunBlob(canvas, command, start, paint, command.LetterSpacing);
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
        var blob = _measurer.Blobs.GetColumnBlob(command.Font, command.Text, command.LetterSpacing);

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
            DrawRunBlob(canvas, command, y, paint, 0);
        }
        else
        {
            var start = (Single)command.Bounds.Y;

            canvas.RotateDegrees(90, baselineX, start);
            canvas.Scale(squeeze, 1, baselineX, start);
            DrawRunBlob(canvas, command, start, paint, 0);
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

            case LinearGradientCommand gradient:
                DrawGradient(canvas, gradient);
                break;

            case BackgroundImageCommand background:
                _images?.DrawBackground(canvas, background.Layer, ToRect(background.Bounds), background.Radii);
                break;
        }
    }

    private static void DrawGradient(SKCanvas canvas, LinearGradientCommand command)
    {
        if (command.Stops.Count == 0)
        {
            return;
        }

        var box = ToRect(command.Bounds);
        var colors = new SKColor[command.Stops.Count];
        var positions = new Single[command.Stops.Count];

        for (var index = 0; index < command.Stops.Count; index++)
        {
            colors[index] = new SKColor(command.Stops[index].Color);
            positions[index] = (Single)Math.Clamp(command.Stops[index].Offset, 0, 1);
        }

        var (start, end) = GradientLine(box, command.AngleDegrees);

        using var shader = SKShader.CreateLinearGradient(start, end, colors, positions, SKShaderTileMode.Clamp);
        using var paint = new SKPaint { Shader = shader, IsAntialias = true };

        canvas.Save();
        SkiaRounded.Clip(canvas, box, command.Radii);
        canvas.DrawRect(box, paint);
        canvas.Restore();
    }

    /// <summary>
    /// Maps a CSS gradient angle onto the gradient line: 0deg points up and 90deg to the right, and
    /// the line runs through the centre with the length the direction projects onto the box.
    /// </summary>
    private static (SKPoint Start, SKPoint End) GradientLine(SKRect box, Double angleDegrees)
    {
        var radians = angleDegrees * Math.PI / 180.0;
        var dx = (Single)Math.Sin(radians);
        var dy = (Single)(-Math.Cos(radians));

        var length = Math.Abs(box.Width * dx) + Math.Abs(box.Height * dy);
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

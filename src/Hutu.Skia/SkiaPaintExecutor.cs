using Hutu.Rendering;
using Hutu.Rendering.Layout;
using Hutu.Rendering.Paint;
using SkiaSharp;
using System;
using System.Collections.Generic;

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

    /// <param name="images">
    /// The loader that decoded the document's images. Without it image commands are skipped, which
    /// is the right degradation for a host that does not render images.
    /// </param>
    public SkiaPaintExecutor(SkiaTextMeasurer measurer, SkiaImageLoader? images = null)
    {
        _measurer = measurer;
        _images = images;
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

                case BackgroundImageCommand background:
                    _images?.DrawBackground(canvas, background.Layer, ToRect(background.Bounds), background.Radii);
                    break;

                case LinearGradientCommand gradient:
                    DrawGradient(canvas, gradient);
                    break;

                case PushClipCommand clip:
                    canvas.Save();
                    SkiaRounded.Clip(canvas, ToRect(clip.Bounds), clip.Radii);
                    break;

                case PopClipCommand:
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
        if (command is PushClipCommand or PopClipCommand)
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
        SkiaRounded.Fill(canvas, ToRect(command.Bounds), command.Radii, paint);
    }

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

        if (!command.Flow.IsVertical)
        {
            var baseline = (Single)(command.Bounds.Y + command.Baseline);
            DrawRunBlob(canvas, command, baseline, paint, command.LetterSpacing);

            if (command.Underline)
            {
                DrawDecoration(canvas, command, baseline + (Single)Math.Abs(metrics.UnderlineOffset), metrics.UnderlineThickness, paint);
            }

            if (command.LineThrough)
            {
                DrawDecoration(canvas, command, baseline - (Single)Math.Abs(metrics.StrikeoutOffset), metrics.StrikeoutThickness, paint);
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
            DrawColumnDecoration(canvas, command, -Math.Abs(metrics.UnderlineOffset), metrics.UnderlineThickness, paint);
        }

        if (command.LineThrough)
        {
            DrawColumnDecoration(canvas, command, Math.Abs(metrics.StrikeoutOffset), metrics.StrikeoutThickness, paint);
        }
    }

    /// <summary>
    /// Draws the blob the cache keeps for the run, at the place the run is set. The blob carries its
    /// glyphs relative to its own origin, so one object answers every frame and every scroll: drawing
    /// it at the run's position is what lands each glyph where the layout measured it.
    /// </summary>
    private void DrawRunBlob(SKCanvas canvas, TextCommand command, Single baseline, SKPaint paint, Double letterSpacing)
    {
        var blob = _measurer.Blobs.GetRunBlob(command.Font, command.Text, letterSpacing);

        if (blob is not null)
        {
            canvas.DrawText(blob, (Single)command.Bounds.X, baseline, paint);
        }
    }

    private static void DrawDecoration(SKCanvas canvas, TextCommand command, Single y, Double thickness, SKPaint paint)
    {
        var height = (Single)Math.Max(1, thickness);
        canvas.DrawRect(new SKRect((Single)command.Bounds.X, y, (Single)command.Bounds.Right, y + height), paint);
    }

    private static void DrawColumnDecoration(SKCanvas canvas, TextCommand command, Double offset, Double thickness, SKPaint paint)
    {
        var width = (Single)Math.Max(1, thickness);
        var x = (Single)(command.Bounds.X + command.Baseline + offset);
        canvas.DrawRect(new SKRect(x, (Single)command.Bounds.Y, x + width, (Single)command.Bounds.Bottom), paint);
    }

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
}

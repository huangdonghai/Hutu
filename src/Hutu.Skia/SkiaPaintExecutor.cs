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
        canvas.Clear(SKColors.White);

        Draw(canvas, list.Commands);

        // A sticky box is pinned to an edge of the scrollport, so it is drawn shifted by whatever its
        // constraint asks for at this scroll position.
        foreach (var layer in list.Sticky)
        {
            var shift = layer.Constraint.Shift(scroll);

            canvas.Save();
            canvas.Translate((Single)shift.X, (Single)shift.Y);
            Draw(canvas, layer.Commands);
            canvas.Restore();
        }

        // A fixed box is already in viewport coordinates, so cancelling the scroll keeps it in place.
        if (list.FixedCommands.Count > 0)
        {
            canvas.Save();
            canvas.Translate((Single)scroll.X, (Single)scroll.Y);
            Draw(canvas, list.FixedCommands);
            canvas.Restore();
        }
    }

    private void Draw(SKCanvas canvas, List<PaintCommand> commands)
    {
        foreach (var command in commands)
        {
            switch (command)
            {
                case FillCommand fill:
                    Fill(canvas, fill);
                    break;

                case BorderCommand border:
                    DrawBorder(canvas, border);
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

    private void DrawText(SKCanvas canvas, TextCommand command)
    {
        var metrics = _measurer.GetMetrics(command.Font);

        using var paint = new SKPaint { Color = new SKColor(command.Color), IsAntialias = true };

        if (!command.Flow.IsVertical)
        {
            var baseline = (Single)(command.Bounds.Y + command.Baseline);
            DrawRuns(canvas, _measurer.GetRuns(command.Text, command.Font), (Single)command.Bounds.X, baseline, paint);

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
            DrawUpright(canvas, command, metrics, paint);
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
        DrawRuns(canvas, _measurer.GetRuns(command.Text, command.Font), baselineX, start, paint);
        canvas.Restore();
    }

    /// <summary>
    /// Draws a run of characters that stay upright, one under the other, each owning the step the
    /// layout gave it and centred across the column.
    /// </summary>
    private void DrawUpright(SKCanvas canvas, TextCommand command, FontMetrics metrics, SKPaint paint)
    {
        var emBox = metrics.Ascent + metrics.Descent;
        var inline = 0.0;

        // The steps are asked of the measurer rather than worked out again here, so a character is
        // drawn at the very place the layout put it.
        var measurer = (ITextMeasurer)_measurer;

        foreach (var character in command.Text)
        {
            var step = Math.Max(
                measurer.MeasureAdvance(character.ToString(), command.Font, command.Flow),
                command.Font.Size);

            // The step is the character's cell and it starts where the layout put it. Where the
            // baseline goes inside that cell is the font's to say - see DrawUprightCharacter - so the
            // middle of the em box stands in only for a font that will not.
            var cellTop = command.Bounds.Y + inline;
            var fallback = (Single)(cellTop + ((step - emBox) / 2) + metrics.Ascent);

            DrawUprightCharacter(canvas, command, character, cellTop, fallback, paint);
            inline += step;
        }
    }

    /// <summary>
    /// Draws one character of a column in the form a column sets it in, on the baseline the font puts
    /// it on.
    /// </summary>
    /// <remarks>
    /// A font declares where each glyph's baseline goes down a column, and it moves that place for the
    /// characters - the 句读 above all - that would otherwise sit in the corner of their cell: asking
    /// for the form alone leaves those marks half a character out of place. The form itself is either
    /// the glyph the font substitutes for a vertical run, drawn as it is because it need not stand for
    /// a character a document could name, or the vertical presentation form Unicode reserved for the
    /// character. Neither is allowed to cost the run anything: the forms are as wide as the characters
    /// they replace, so the step the layout measured still lands every character after them where it
    /// always did. A character with no form at all is drawn exactly as it was before.
    /// </remarks>
    private void DrawUprightCharacter(
        SKCanvas canvas,
        TextCommand command,
        Char character,
        Double cellTop,
        Single fallbackBaseline,
        SKPaint paint)
    {
        var font = _measurer.GetFont(command.Font, character);
        var vertical = _measurer.Shaper.GetVerticalForm(font, character);
        var text = character.ToString();
        var centre = ColumnCentre(command, text);
        var baseline = Baseline(command, vertical, cellTop, fallbackBaseline);

        if (vertical.IsSubstituted)
        {
            _measurer.Shaper.DrawVerticalGlyph(canvas, font, vertical.Glyph, centre, baseline, paint);
            return;
        }

        if (VerticalForms.TryGet(character, out var replacement) && _measurer.HasGlyph(command.Font, replacement))
        {
            text = replacement.ToString();
            font = _measurer.GetFont(command.Font, replacement);
            centre = ColumnCentre(command, text);

            // The form the author could have written is a glyph of the same font, drawn where the
            // font puts that glyph - so writing `。` and writing `︒` are one and the same picture.
            baseline = Baseline(command, _measurer.Shaper.GetVerticalForm(font, replacement), cellTop, fallbackBaseline);
        }

        canvas.DrawText(text, centre, baseline, font, paint);
    }

    /// <summary>
    /// Gets the baseline a column puts the glyph on: the place the font declares, or the stand-in the
    /// caller worked out for a font that will not declare one.
    /// </summary>
    private static Single Baseline(TextCommand command, VerticalForm vertical, Double cellTop, Single fallback) =>
        vertical.HasOrigin
            ? (Single)(cellTop + (vertical.Origin * command.Font.Size))
            : fallback;

    /// <summary>
    /// Gets the position across the column a character is centred at, which is asked of the text that
    /// ends up drawn rather than of the character the layout was given.
    /// </summary>
    private Single ColumnCentre(TextCommand command, String text) =>
        (Single)(command.Bounds.X + ((command.Bounds.Width - _measurer.MeasureWidth(text, command.Font)) / 2));

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

        var runs = _measurer.GetRuns(command.Text, command.Font);

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
            DrawRuns(canvas, runs, x, y, paint);
        }
        else
        {
            var start = (Single)command.Bounds.Y;

            canvas.RotateDegrees(90, baselineX, start);
            canvas.Scale(squeeze, 1, baselineX, start);
            DrawRuns(canvas, runs, baselineX, start, paint);
        }

        canvas.Restore();
    }

    /// <summary>
    /// Draws the runs of a text command one after another along its baseline, each advancing by the
    /// width it was measured with. A run changes font where the family had no glyph, which is how the
    /// characters a family cannot set are kept without a box in the middle of the text.
    /// </summary>
    private static void DrawRuns(SKCanvas canvas, IReadOnlyList<SkiaTextRun> runs, Single x, Single baseline, SKPaint paint)
    {
        foreach (var run in runs)
        {
            canvas.DrawText(run.Text, x, baseline, run.Font, paint);
            x += run.Font.MeasureText(run.Text);
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
}

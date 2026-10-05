using Hutu.Rendering.Layout;
using Hutu.Rendering.Paint;
using SkiaSharp;
using System;

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

    public void Execute(PaintList list, SKCanvas canvas)
    {
        canvas.Clear(SKColors.White);

        foreach (var command in list.Commands)
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
                    _images?.Draw(canvas, image.Url, ToRect(image.Bounds), image.Fit, image.CornerRadius);
                    break;

                case BackgroundImageCommand background:
                    _images?.DrawBackground(canvas, background.Layer, ToRect(background.Bounds), background.CornerRadius);
                    break;

                case LinearGradientCommand gradient:
                    DrawGradient(canvas, gradient);
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
        canvas.DrawRect(ToRect(command.Bounds), paint);
    }

    private static void DrawBorder(SKCanvas canvas, BorderCommand command)
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
        var font = _measurer.GetFont(command.Font);
        var baseline = (Single)(command.Bounds.Y + command.Baseline);

        using (var paint = new SKPaint { Color = new SKColor(command.Color), IsAntialias = true })
        {
            canvas.DrawText(command.Text, (Single)command.Bounds.X, baseline, font, paint);

            var metrics = _measurer.GetMetrics(command.Font);

            if (command.Underline)
            {
                DrawDecoration(canvas, command, baseline + (Single)Math.Abs(metrics.UnderlineOffset), metrics.UnderlineThickness, paint);
            }

            if (command.LineThrough)
            {
                DrawDecoration(canvas, command, baseline - (Single)Math.Abs(metrics.StrikeoutOffset), metrics.StrikeoutThickness, paint);
            }
        }
    }

    private static void DrawDecoration(SKCanvas canvas, TextCommand command, Single y, Double thickness, SKPaint paint)
    {
        var height = (Single)Math.Max(1, thickness);
        canvas.DrawRect(new SKRect((Single)command.Bounds.X, y, (Single)command.Bounds.Right, y + height), paint);
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
        canvas.ClipRoundRect(new SKRoundRect(box, (Single)Math.Max(0, command.CornerRadius)), antialias: true);
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

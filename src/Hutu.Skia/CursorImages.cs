using SkiaSharp;
using System;

namespace Hutu.Skia;

/// <summary>
/// Draws the pointer shapes no platform offers as a standard cursor, as images a host can hand to its
/// own cursor API.
/// </summary>
/// <remarks>
/// An I-beam is a stem with a serif standing at each end, and the one a column of text is read with is
/// that shape turned on its side. Every platform carries the upright one and none carries the turned
/// one, so it is drawn here rather than shipped as an asset: three strokes are less to keep correct
/// than a file nobody can edit.
/// </remarks>
public static class CursorImages
{
    /// <summary>
    /// Draws the I-beam a column of text is read with - a stem lying on its side with a serif standing
    /// at each end - and encodes it as a PNG.
    /// </summary>
    /// <param name="size">The width and height of the image in pixels.</param>
    public static Byte[] VerticalIBeam(Int32 size = 32)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(size, 4);

        using var bitmap = new SKBitmap(new SKImageInfo(size, size, SKColorType.Rgba8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(bitmap);

        canvas.Clear(SKColors.Transparent);

        var centre = size / 2f;
        var reach = size * 0.28f;
        var serif = size * 0.13f;

        using var stroke = new SKPaint
        {
            Color = SKColors.Black,
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = Math.Max(1f, size / 16f),
            StrokeCap = SKStrokeCap.Round,
        };

        canvas.DrawLine(centre - reach, centre, centre + reach, centre, stroke);
        canvas.DrawLine(centre - reach, centre - serif, centre - reach, centre + serif, stroke);
        canvas.DrawLine(centre + reach, centre - serif, centre + reach, centre + serif, stroke);

        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);

        return data.ToArray();
    }
}

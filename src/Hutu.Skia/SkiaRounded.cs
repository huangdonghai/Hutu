using Hutu.Rendering.Layout;
using SkiaSharp;
using System;

namespace Hutu.Skia;

/// <summary>Turns the corner radii of a box into the rounded rectangles Skia draws with.</summary>
/// <remarks>
/// Skia only takes one radius for the four corners of a rectangle, so a box whose corners differ - or
/// whose corners are ellipses rather than circles - has to go through the four pairs of
/// <see cref="SKRoundRect.SetRectRadii"/>. Every place that clips or fills a rounded area shares this,
/// so the geometry is translated in exactly one way.
/// </remarks>
internal static class SkiaRounded
{
    /// <summary>
    /// Builds the rounded rectangle of an area, squared off when it has no radii.
    /// </summary>
    public static SKRoundRect RoundRect(SKRect area, CornerRadii radii)
    {
        var rounded = new SKRoundRect(area, 0);

        if (radii.IsZero)
        {
            return rounded;
        }

        var corners = new SKPoint[4];

        // The order is the one Skia expects, starting at the top left and going clockwise.
        Set(corners, 0, radii.TopLeft);
        Set(corners, 1, radii.TopRight);
        Set(corners, 2, radii.BottomRight);
        Set(corners, 3, radii.BottomLeft);

        rounded.SetRectRadii(area, corners);
        return rounded;
    }

    /// <summary>Clips the canvas to a rounded area, pairing with <c>Restore</c> on the caller's side.</summary>
    public static void Clip(SKCanvas canvas, SKRect area, CornerRadii radii)
    {
        if (radii.IsZero)
        {
            canvas.ClipRect(area, SKClipOperation.Intersect, antialias: true);
            return;
        }

        canvas.ClipRoundRect(RoundRect(area, radii), SKClipOperation.Intersect, antialias: true);
    }

    /// <summary>
    /// Fills a rounded area. A square area keeps to the plain rectangle, which avoids the cost of
    /// building a path for every background on a page that has no rounded corners at all.
    /// </summary>
    public static void Fill(SKCanvas canvas, SKRect area, CornerRadii radii, SKPaint paint)
    {
        if (radii.IsZero)
        {
            canvas.DrawRect(area, paint);
            return;
        }

        using var path = new SKPath();
        path.AddRoundRect(RoundRect(area, radii));
        canvas.DrawPath(path, paint);
    }

    /// <summary>
    /// Gets a rounded rectangle as a path, so that several of them can be combined - which is how a
    /// border between an outer and an inner curve is filled in one go.
    /// </summary>
    public static SKPath ToPath(SKRect area, CornerRadii radii)
    {
        var path = new SKPath();
        path.AddRoundRect(RoundRect(area, radii));
        return path;
    }

    private static void Set(SKPoint[] corners, Int32 index, CornerRadius radius) =>
        corners[index] = new SKPoint((Single)radius.Horizontal, (Single)radius.Vertical);
}

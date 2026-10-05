using Hutu.Rendering.Boxes;
using Hutu.Rendering.Media;
using System;

namespace Hutu.Rendering.Layout;

/// <summary>
/// Resolves the used size of a replaced element (an image) from its intrinsic size and the CSS
/// constraints that apply to it.
/// </summary>
/// <remarks>
/// The specification's algorithm is a table of nine cases crossed with the min/max constraints; this
/// implements the practical subset: a specified axis wins, the other follows from the intrinsic
/// aspect ratio, and a min/max violation on one axis pulls the other along unless it was specified
/// itself. That is what makes <c>width: 100%; max-height: 320px</c> produce a box of the container
/// width and 320px tall - cropped by <c>object-fit</c> rather than squashed.
/// </remarks>
internal static class ReplacedSizing
{
    /// <summary>The object size the specification assumes when nothing else is known.</summary>
    private static readonly ImageSize Fallback = new(300, 150);

    public static (Double Width, Double Height) Resolve(Box box) =>
        Resolve(box.Style, box.IntrinsicSize is { IsValid: true } intrinsic ? intrinsic : Fallback);

    public static (Double Width, Double Height) Resolve(ComputedStyle style, ImageSize intrinsic)
    {
        var aspect = intrinsic.AspectRatio > 0 ? intrinsic.AspectRatio : 1;

        var specifiedWidth = style.Width;
        var specifiedHeight = style.Height;

        Double width;
        Double height;

        if (specifiedWidth.HasValue && specifiedHeight.HasValue)
        {
            width = specifiedWidth.Value;
            height = specifiedHeight.Value;
        }
        else if (specifiedWidth.HasValue)
        {
            width = specifiedWidth.Value;
            height = width / aspect;
        }
        else if (specifiedHeight.HasValue)
        {
            height = specifiedHeight.Value;
            width = height * aspect;
        }
        else
        {
            width = intrinsic.Width;
            height = intrinsic.Height;
        }

        if (style.MaxWidth is { } maxWidth && width > maxWidth)
        {
            width = maxWidth;

            if (!specifiedHeight.HasValue)
            {
                height = width / aspect;
            }
        }

        if (style.MinWidth is { } minWidth && width < minWidth)
        {
            width = minWidth;

            if (!specifiedHeight.HasValue)
            {
                height = width / aspect;
            }
        }

        if (style.MaxHeight is { } maxHeight && height > maxHeight)
        {
            height = maxHeight;

            if (!specifiedWidth.HasValue)
            {
                width = height * aspect;
            }
        }

        if (style.MinHeight is { } minHeight && height < minHeight)
        {
            height = minHeight;

            if (!specifiedWidth.HasValue)
            {
                width = height * aspect;
            }
        }

        return (Math.Max(0, width), Math.Max(0, height));
    }
}

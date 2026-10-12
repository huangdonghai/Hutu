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
/// <para>
/// Specified means declared in a style sheet <em>or</em> by the element's own <c>width</c>/
/// <c>height</c> attributes, which the rendering requirements map to those properties at the
/// presentational-hint level - below the author's sheet, above the picture. A page that asks for a
/// 197-pixel thumbnail out of a 250-pixel bitmap says so in the attribute alone, and honouring only
/// the bitmap displays a fifth more picture than the page wanted.
/// </para>
/// </remarks>
internal static class ReplacedSizing
{
    /// <summary>The object size the specification assumes when nothing else is known.</summary>
    private static readonly ImageSize Fallback = new(300, 150);

    /// <param name="containingBlockWidth">What a percentage width is measured from.</param>
    /// <param name="containingBlockHeight">
    /// What a percentage height is measured from. Zero or less means the containing block has no
    /// definite height, in which case a percentage height behaves as <c>auto</c>.
    /// </param>
    public static (Double Width, Double Height) Resolve(Box box, Double containingBlockWidth, Double containingBlockHeight) =>
        Resolve(
            box.Style,
            box.IntrinsicSize is { IsValid: true } intrinsic ? intrinsic : Fallback,
            box.AttributeSize,
            containingBlockWidth,
            containingBlockHeight);

    /// <summary>
    /// Resolves the used <em>content</em> size. Under <c>box-sizing: border-box</c> a declared size
    /// covers the padding and the border as well, so the insets come off before the intrinsic ratio
    /// is applied and the caller adds them back around the result.
    /// </summary>
    /// <param name="hint">
    /// What the element's <c>width</c>/<c>height</c> attributes declare, or null where they say nothing.
    /// An axis of zero counts as nothing declared, so the picture's own ratio still answers it.
    /// </param>
    public static (Double Width, Double Height) Resolve(
        ComputedStyle style,
        ImageSize intrinsic,
        ImageSize? hint,
        Double containingBlockWidth,
        Double containingBlockHeight)
    {
        var aspect = intrinsic.AspectRatio > 0 ? intrinsic.AspectRatio : 1;
        var isBorderBox = style.BoxSizing == BoxSizing.BorderBox;
        var insets = Insets(style, containingBlockWidth);
        var horizontal = isBorderBox ? insets.Horizontal : 0;
        var vertical = isBorderBox ? insets.Vertical : 0;

        var specifiedWidth = Specified(style.Width, style.HasDeclaredWidth, hint?.Width, containingBlockWidth, horizontal);
        var specifiedHeight = Specified(style.Height, style.HasDeclaredHeight, hint?.Height, containingBlockHeight, vertical);
        var maxWidth = ContentLength(style.MaxWidth, containingBlockWidth, horizontal);
        var maxHeight = ContentLength(style.MaxHeight, containingBlockHeight, vertical);
        var minWidth = ContentLength(style.MinWidth, containingBlockWidth, horizontal);
        var minHeight = ContentLength(style.MinHeight, containingBlockHeight, vertical);

        Double width;
        Double height;

        if (specifiedWidth is { } declaredWidth && specifiedHeight is { } declaredHeight)
        {
            width = declaredWidth;
            height = declaredHeight;
        }
        else if (specifiedWidth is { } onlyWidth)
        {
            width = onlyWidth;
            height = width / aspect;
        }
        else if (specifiedHeight is { } onlyHeight)
        {
            height = onlyHeight;
            width = height * aspect;
        }
        else
        {
            // With neither axis declared the box is the intrinsic size, which is already a content
            // size, so there is nothing to take the insets off.
            width = Math.Max(0, intrinsic.Width);
            height = Math.Max(0, intrinsic.Height);
        }

        if (maxWidth is { } maximumWidth && width > maximumWidth)
        {
            width = maximumWidth;

            if (specifiedHeight is null)
            {
                height = width / aspect;
            }
        }

        if (minWidth is { } minimumWidth && width < minimumWidth)
        {
            width = minimumWidth;

            if (specifiedHeight is null)
            {
                height = width / aspect;
            }
        }

        if (maxHeight is { } maximumHeight && height > maximumHeight)
        {
            height = maximumHeight;

            if (specifiedWidth is null)
            {
                width = height * aspect;
            }
        }

        if (minHeight is { } minimumHeight && height < minimumHeight)
        {
            height = minimumHeight;

            if (specifiedWidth is null)
            {
                width = height * aspect;
            }
        }

        return (Math.Max(0, width), Math.Max(0, height));
    }

    /// <summary>
    /// What an axis was settled to before the picture is consulted.
    /// </summary>
    /// <remarks>
    /// Three answers in the order the cascade puts them: a length the page declared, nothing where the
    /// page declared <c>auto</c> - which is a request that the content decide, so an attribute is no
    /// reply to it - and the attribute where the page said nothing about the axis at all. The last two
    /// carry the same length, an <c>auto</c>, and only the flag beside them says which of the two the
    /// page wrote.
    /// </remarks>
    private static Double? Specified(StyleLength length, Boolean declared, Double? hinted, Double basis, Double insets)
    {
        if (ContentLength(length, basis, insets) is { } resolved)
        {
            return resolved;
        }

        return declared ? null : ContentOfHint(hinted, insets);
    }

    /// <summary>
    /// One axis of the attributes' hint, in content box terms. A presentational hint and a stylesheet
    /// length say the same kind of thing - the size the box is to be - so the insets come off it under
    /// <c>box-sizing: border-box</c> just as they come off a declared length, and a zero says the
    /// document left that axis to the picture.
    /// </summary>
    private static Double? ContentOfHint(Double? hinted, Double insets) =>
        hinted is { } value && value > 0 ? Math.Max(0, value - insets) : null;

    /// <summary>Gets the padding plus the border, which the caller adds around the content size.</summary>
    private static Thickness Insets(ComputedStyle style, Double basis)
    {
        var padding = style.Padding.Resolve(basis);

        return new Thickness(
            padding.Top + style.BorderTopWidth,
            padding.Right + style.BorderRightWidth,
            padding.Bottom + style.BorderBottomWidth,
            padding.Left + style.BorderLeftWidth);
    }

    private static Double? ContentLength(StyleLength length, Double basis, Double insets)
    {
        if (!length.HasValue)
        {
            return null;
        }

        // A percentage against a containing block with no definite size behaves as `auto`.
        if (length.Kind == StyleLengthKind.Percent && basis <= 0)
        {
            return null;
        }

        var value = length.Resolve(basis);
        return Math.Max(0, insets > 0 ? value - insets : value);
    }
}

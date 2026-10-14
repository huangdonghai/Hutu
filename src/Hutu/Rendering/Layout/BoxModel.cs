using System;

namespace Hutu.Rendering.Layout;

/// <summary>
/// The shared box-model arithmetic: it turns the styles that size a box - the width, the height, the
/// min and max constraints, <c>box-sizing</c> and the margins - into used sizes, resolved against
/// the containing block.
/// </summary>
/// <remarks>
/// Every formatting context goes through this so that a box is sized the same way wherever it
/// appears. Two rules are load-bearing and easy to get wrong: the basis must be the containing
/// block rather than the viewport, and a min or max constraint must be applied to the width that was
/// already resolved, not only when the width was <c>auto</c>.
/// </remarks>
internal static class BoxModel
{
    /// <summary>
    /// Resolves a width into the width of the border box and of the content box. An <c>auto</c>
    /// width fills the containing block minus the margins and the insets; a declared one is taken as
    /// the content box, or as the border box under <c>box-sizing: border-box</c>. The min and max
    /// constraints are then applied to the result.
    /// </summary>
    /// <remarks>
    /// <paramref name="basis"/> is the room the box may fill, which a shrink-to-fit caller makes
    /// smaller than the containing block. <paramref name="containingWidth"/> is the containing block
    /// itself, which every percentage on the box - its width, its margins, its min and max - is
    /// measured against, per CSS. Keeping the two apart is what lets a floated box with a percentage
    /// margin shrink to its content instead of resolving that margin against the reduced room it was
    /// handed. For an ordinary block the two are the same number and nothing changes.
    /// </remarks>
    public static (Double BorderBox, Double Content) ResolveWidth(ComputedStyle style, Double basis, Double containingWidth, Double insets)
    {
        var isBorderBox = IsBorderBox(style);
        Double content;

        if (style.Width.HasValue)
        {
            var specified = Math.Max(0, style.Width.Resolve(containingWidth));
            content = Math.Max(0, isBorderBox ? specified - insets : specified);
        }
        else
        {
            content = Math.Max(0, basis - style.Margin.Resolve(containingWidth).Horizontal - insets);
        }

        if (ContentBound(style.MaxWidth, containingWidth, insets, isBorderBox) is { } maximum && content > maximum)
        {
            content = maximum;
        }

        if (ContentBound(style.MinWidth, containingWidth, insets, isBorderBox) is { } minimum && content < minimum)
        {
            content = minimum;
        }

        return (content + insets, content);
    }

    /// <summary>
    /// Resolves the size of a box along the block axis, which is the physical width of a box whose
    /// text runs down a column.
    /// </summary>
    /// <remarks>
    /// A block in a row takes the width it is given, and <c>auto</c> means all of it; a column is the
    /// other way round. There is no definite size to fill, so <c>auto</c> shrinks to the content and
    /// the box ends up exactly as wide as the columns it needs. The physical <c>width</c> property
    /// still bounds it, because physical sizes keep their physical meaning whatever the writing mode.
    /// </remarks>
    public static Double ResolveBlockExtent(ComputedStyle style, Double contentBlock, Double basis, Double insets)
    {
        var isBorderBox = IsBorderBox(style);
        var content = Math.Max(0, contentBlock);

        if (style.Width.HasValue)
        {
            var specified = Math.Max(0, style.Width.Resolve(basis));
            content = Math.Max(0, isBorderBox ? specified - insets : specified);
        }

        if (ContentBound(style.MaxWidth, basis, insets, isBorderBox) is { } maximum && content > maximum)
        {
            content = maximum;
        }

        if (ContentBound(style.MinWidth, basis, insets, isBorderBox) is { } minimum && content < minimum)
        {
            content = minimum;
        }

        return content;
    }

    /// <summary>
    /// Resolves a height declared as a length. Nothing is returned for a percentage against a
    /// containing block with no definite height, nor for <c>auto</c>: both mean the height comes
    /// from the content.
    /// </summary>
    public static Double? DeclaredHeight(ComputedStyle style, Double containingBlockHeight, Double insets)
    {
        var height = style.Height;

        if (!height.HasValue || (height.Kind == StyleLengthKind.Percent && containingBlockHeight <= 0))
        {
            return null;
        }

        var value = Math.Max(0, height.Resolve(containingBlockHeight));
        return IsBorderBox(style) ? Math.Max(0, value - insets) : value;
    }

    /// <summary>
    /// Resolves a width declared as a length. It is the block-axis counterpart of
    /// <see cref="DeclaredHeight"/>: a column's block extent is its physical width, so that is the
    /// declared size a percentage of a column's containing block resolves against.
    /// </summary>
    public static Double? DeclaredWidth(ComputedStyle style, Double containingBlockWidth, Double insets)
    {
        var width = style.Width;

        if (!width.HasValue || (width.Kind == StyleLengthKind.Percent && containingBlockWidth <= 0))
        {
            return null;
        }

        var value = Math.Max(0, width.Resolve(containingBlockWidth));
        return IsBorderBox(style) ? Math.Max(0, value - insets) : value;
    }

    /// <summary>Applies the width constraints to an already resolved border-box width.</summary>
    public static Double ClampWidth(ComputedStyle style, Double borderBoxWidth, Double basis, Double insets)
    {
        var isBorderBox = IsBorderBox(style);
        var content = Math.Max(0, borderBoxWidth - insets);

        if (ContentBound(style.MaxWidth, basis, insets, isBorderBox) is { } maximum && content > maximum)
        {
            content = maximum;
        }

        if (ContentBound(style.MinWidth, basis, insets, isBorderBox) is { } minimum && content < minimum)
        {
            content = minimum;
        }

        return content + insets;
    }

    /// <summary>Applies the height constraints to an already resolved content height.</summary>
    public static Double ClampHeight(ComputedStyle style, Double contentHeight, Double containingBlockHeight, Double insets)
    {
        var isBorderBox = IsBorderBox(style);

        if (ContentBound(style.MaxHeight, containingBlockHeight, insets, isBorderBox) is { } maximum && contentHeight > maximum)
        {
            contentHeight = maximum;
        }

        if (ContentBound(style.MinHeight, containingBlockHeight, insets, isBorderBox) is { } minimum && contentHeight < minimum)
        {
            contentHeight = minimum;
        }

        return contentHeight;
    }

    /// <summary>
    /// Resolves a min or max constraint into a bound on the <em>content</em> box, which is the box
    /// the rest of the sizing works in. Under <c>box-sizing: border-box</c> the constraint bounds the
    /// border box, so the insets come off it.
    /// </summary>
    public static Double? ContentBound(StyleLength length, Double basis, Double insets, Boolean isBorderBox)
    {
        if (!length.HasValue)
        {
            return null;
        }

        var value = length.Resolve(basis);
        return isBorderBox ? Math.Max(0, value - insets) : Math.Max(0, value);
    }

    public static Boolean IsBorderBox(ComputedStyle style) => style.BoxSizing == BoxSizing.BorderBox;

    /// <summary>
    /// Gets the margin a box ends up with once the horizontal <c>auto</c> sides have been given the
    /// space the used width left over. This is the one place the decision is made, so a parent and
    /// the box itself always agree on where the box sits.
    /// </summary>
    public static Thickness UsedMargin(ComputedStyle style, Double basis, Double borderBoxWidth) =>
        DistributeAutoMargins(style.Margin, style.Margin.Resolve(basis), basis - borderBoxWidth);

    /// <summary>
    /// Hands the space the used width did not take to the horizontal margins declared as
    /// <c>auto</c>. A single auto margin takes all of it and a pair shares it, which is what
    /// <c>margin: 0 auto</c> needs. An over-constrained box keeps the margins it resolved to.
    /// </summary>
    public static Thickness DistributeAutoMargins(StyleThickness declared, Thickness used, Double space)
    {
        var isLeftAuto = declared.Left.IsAuto;
        var isRightAuto = declared.Right.IsAuto;

        if (!isLeftAuto && !isRightAuto)
        {
            return used;
        }

        var available = space - (isLeftAuto ? 0 : used.Left) - (isRightAuto ? 0 : used.Right);

        if (available <= 0)
        {
            return used;
        }

        if (isLeftAuto && isRightAuto)
        {
            var half = available / 2;
            return new Thickness(used.Top, half, used.Bottom, half);
        }

        return isLeftAuto
            ? new Thickness(used.Top, used.Right, used.Bottom, available)
            : new Thickness(used.Top, available, used.Bottom, used.Left);
    }
}

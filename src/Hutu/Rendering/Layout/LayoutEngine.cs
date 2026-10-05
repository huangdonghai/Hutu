using Hutu.Rendering.Boxes;
using System;

namespace Hutu.Rendering.Layout;

/// <summary>
/// Turns the box tree into a layout tree with resolved geometry.
/// </summary>
/// <remarks>
/// This implements the block and inline formatting contexts the MVP needs: a block stacks its
/// block-level children and resolves the box model around them, while a block whose content is
/// entirely inline-level delegates to <see cref="InlineLayout"/>. Adjacent sibling margins collapse;
/// parent/child collapsing, floats, positioning, flex and grid are not implemented yet.
/// </remarks>
public sealed class LayoutEngine
{
    private readonly InlineLayout _inline;

    public LayoutEngine(ITextMeasurer measurer) => _inline = new InlineLayout(measurer);

    /// <summary>
    /// Lays the box tree out at the given viewport width. The root is placed at the origin; null
    /// means there is nothing to lay out.
    /// </summary>
    public LayoutResult? Layout(Box? root, Double viewportWidth)
    {
        if (root is null)
        {
            return null;
        }

        var width = Math.Max(1, viewportWidth);
        var rootBox = LayoutBlock(root, width, 0, 0);
        var height = rootBox.BorderBox.Bottom + rootBox.Margin.Bottom;

        return new LayoutResult(rootBox, width, height);
    }

    private LayoutBox LayoutBlock(Box box, Double availableWidth, Double borderX, Double borderY)
    {
        var style = box.Style;
        var margin = MarginOf(style);
        var border = BorderOf(style);
        var padding = PaddingOf(style);

        if (box.Kind == BoxKind.HorizontalRule && border.Horizontal == 0 && border.Vertical == 0)
        {
            // A rule with no border of its own still draws its line.
            border = new Thickness(1, 0, 0, 0);
        }

        if (box.IsReplaced)
        {
            return LayoutReplaced(box, style, margin, border, padding, borderX, borderY);
        }

        Double borderBoxWidth;
        Double contentWidth;

        if (style.Width is { } specifiedWidth)
        {
            contentWidth = Math.Max(0, specifiedWidth);
            borderBoxWidth = contentWidth + padding.Horizontal + border.Horizontal;
        }
        else
        {
            borderBoxWidth = Math.Max(0, availableWidth);
            contentWidth = Math.Max(0, borderBoxWidth - padding.Horizontal - border.Horizontal);

            // min-width/max-width constrain the content box; the border box follows.
            if (style.MaxWidth is { } maxWidth && contentWidth > maxWidth)
            {
                contentWidth = maxWidth;
                borderBoxWidth = contentWidth + padding.Horizontal + border.Horizontal;
            }

            if (style.MinWidth is { } minWidth && contentWidth < minWidth)
            {
                contentWidth = minWidth;
                borderBoxWidth = contentWidth + padding.Horizontal + border.Horizontal;
            }
        }

        var layout = new LayoutBox(box, box.Kind == BoxKind.HorizontalRule ? LayoutKind.HorizontalRule : LayoutKind.Block)
        {
            Margin = margin,
            Border = border,
            Padding = padding,
            BorderBox = new Rect(borderX, borderY, borderBoxWidth, 0),
        };

        var contentX = borderX + border.Left + padding.Left;
        var contentY = borderY + border.Top + padding.Top;
        Double contentHeight;

        if (box.Children.Count == 0)
        {
            contentHeight = 0;
        }
        else if (HasBlockChildren(box))
        {
            contentHeight = LayoutBlockChildren(box, layout, contentX, contentY, contentWidth);
        }
        else
        {
            contentHeight = _inline.Layout(box, layout, contentX, contentY, contentWidth);
        }

        var height = style.Height ?? contentHeight;

        if (style.MaxHeight is { } maxHeight && height > maxHeight)
        {
            height = maxHeight;
        }

        if (style.MinHeight is { } minHeight && height < minHeight)
        {
            height = minHeight;
        }

        layout.BorderBox = new Rect(borderX, borderY, borderBoxWidth, height + padding.Vertical + border.Vertical);

        return layout;
    }

    /// <summary>
    /// Lays out a replaced box - an image. Its used size comes from the intrinsic size and the CSS
    /// constraints, and it has no children to lay out, so the box is complete once sized.
    /// </summary>
    private static LayoutBox LayoutReplaced(
        Box box,
        ComputedStyle style,
        Thickness margin,
        Thickness border,
        Thickness padding,
        Double borderX,
        Double borderY)
    {
        var (usedWidth, usedHeight) = ReplacedSizing.Resolve(box);

        return new LayoutBox(box, LayoutKind.Image)
        {
            Margin = margin,
            Border = border,
            Padding = padding,
            ImageUrl = box.SourceUrl,
            ImageFit = style.ObjectFit,
            CornerRadius = style.CornerRadius,
            BorderBox = new Rect(
                borderX,
                borderY,
                usedWidth + padding.Horizontal + border.Horizontal,
                usedHeight + padding.Vertical + border.Vertical),
        };
    }

    private Double LayoutBlockChildren(Box box, LayoutBox parent, Double contentX, Double contentY, Double contentWidth)
    {
        var cursor = contentY;
        var previousBottomMargin = 0.0;
        var first = true;

        foreach (var child in box.Children)
        {
            if (!child.IsBlockLevel)
            {
                continue;
            }

            var margin = MarginOf(child.Style);
            var spacing = first ? margin.Top : Math.Max(previousBottomMargin, margin.Top);
            var childLayout = LayoutBlock(
                child,
                Math.Max(0, contentWidth - margin.Horizontal),
                contentX + margin.Left,
                cursor + spacing);

            parent.Children.Add(childLayout);
            cursor = childLayout.BorderBox.Bottom;
            previousBottomMargin = margin.Bottom;
            first = false;
        }

        return cursor + previousBottomMargin - contentY;
    }

    private static Boolean HasBlockChildren(Box box)
    {
        foreach (var child in box.Children)
        {
            if (child.IsBlockLevel)
            {
                return true;
            }
        }

        return false;
    }

    private static Thickness MarginOf(ComputedStyle style) =>
        new(style.MarginTop, style.MarginRight, style.MarginBottom, style.MarginLeft);

    private static Thickness BorderOf(ComputedStyle style) =>
        new(style.BorderTopWidth, style.BorderRightWidth, style.BorderBottomWidth, style.BorderLeftWidth);

    private static Thickness PaddingOf(ComputedStyle style) =>
        new(style.PaddingTop, style.PaddingRight, style.PaddingBottom, style.PaddingLeft);
}

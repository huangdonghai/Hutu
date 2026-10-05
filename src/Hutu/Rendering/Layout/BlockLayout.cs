using Hutu.Rendering.Boxes;
using System;
using System.Collections.Generic;

namespace Hutu.Rendering.Layout;

/// <summary>
/// The block formatting context: a block stacks its block-level children vertically and resolves the
/// box model around them.
/// </summary>
/// <remarks>
/// This is the axis the whole engine is built on. It is split into the two phases the rest of the
/// engine speaks: <see cref="Measure"/> answers how large the box wants to be at a given available
/// width, and <see cref="Arrange"/> fixes that size into a rectangle and positions the content. The
/// split is what lets flex, grid and table ask a child for its size before they know where it goes.
/// <para>
/// Adjacent sibling margins collapse to the larger of the two. Parent/child collapsing is not
/// modelled yet: deciding whether the margins are adjoining needs the parent's border and padding,
/// and letting a child's margin escape would move the content of every sample page, so it is left
/// for a later pass.
/// </para>
/// </remarks>
internal sealed class BlockLayout
{
    private readonly LayoutEngine _engine;
    private readonly InlineLayout _inline;

    public BlockLayout(LayoutEngine engine, InlineLayout inline)
    {
        _engine = engine;
        _inline = inline;
    }

    /// <summary>
    /// Measures a block box, returning the size of its <em>border</em> box. No geometry is produced:
    /// the children are only asked for their sizes, which is why the result can be cached.
    /// </summary>
    public Size Measure(Box box, MeasureConstraints constraints, LayoutContext context)
    {
        var style = box.Style;
        var basisWidth = constraints.AvailableWidth ?? context.ContainingBlock.Width;
        var border = BorderOf(box);
        var padding = style.Padding.Resolve(basisWidth);
        var insets = new Thickness(
            padding.Top + border.Top,
            padding.Right + border.Right,
            padding.Bottom + border.Bottom,
            padding.Left + border.Left);

        var (borderBoxWidth, contentWidth) = BoxModel.ResolveWidth(style, basisWidth, insets.Horizontal);
        var basisHeight = constraints.AvailableHeight ?? context.ContainingBlock.Height;
        var declaredHeight = BoxModel.DeclaredHeight(style, basisHeight, insets.Vertical);

        Double contentHeight;

        if (box.Children.Count == 0)
        {
            contentHeight = 0;
        }
        else if (HasBlockChildren(box))
        {
            contentHeight = MeasureBlockChildren(box, contentWidth, declaredHeight ?? 0, context);
        }
        else
        {
            contentHeight = _inline.MeasureHeight(box, contentWidth);
        }

        var height = BoxModel.ClampHeight(style, declaredHeight ?? contentHeight, basisHeight, insets.Vertical);

        return new Size(borderBoxWidth, height + insets.Vertical);
    }

    /// <summary>
    /// Measures the width the box's content wants when nothing constrains it. A flex item with
    /// <c>flex-basis: auto</c> and a grid track sized as <c>max-content</c> both land here.
    /// </summary>
    public IntrinsicSize MeasureIntrinsic(Box box, LayoutContext context)
    {
        var style = box.Style;
        var basis = context.ContainingBlock.Width;
        var insets = InsetsOf(box, basis);

        if (box.IsReplaced)
        {
            var (width, _) = ReplacedSizing.Resolve(box, basis, context.ContainingBlock.Height);
            var outer = width + insets;
            return new IntrinsicSize(outer, outer);
        }

        // A declared width settles both: the content cannot be narrower or wider than the box.
        if (style.Width.HasValue && !(style.Width.Kind == StyleLengthKind.Percent && basis <= 0))
        {
            var specified = Math.Max(0, style.Width.Resolve(basis));
            var borderBox = BoxModel.IsBorderBox(style) ? specified : specified + insets;
            return new IntrinsicSize(borderBox, borderBox);
        }

        return ContentIntrinsic(box, basis, insets, context);
    }

    /// <summary>
    /// The sizes the content wants once the box's own declared width is set aside. The automatic
    /// minimum size of a flex item is the smaller of this and the declared size, so the declared size
    /// must not be allowed to answer it.
    /// </summary>
    public IntrinsicSize MeasureContentIntrinsic(Box box, LayoutContext context)
    {
        var basis = context.ContainingBlock.Width;
        var insets = InsetsOf(box, basis);

        if (box.IsReplaced)
        {
            var (width, _) = ReplacedSizing.Resolve(box, basis, context.ContainingBlock.Height);
            var outer = width + insets;
            return new IntrinsicSize(outer, outer);
        }

        return ContentIntrinsic(box, basis, insets, context);
    }

    private IntrinsicSize ContentIntrinsic(Box box, Double basis, Double insets, LayoutContext context)
    {
        Double min;
        Double max;

        if (box.Children.Count == 0)
        {
            min = 0;
            max = 0;
        }
        else if (HasBlockChildren(box))
        {
            min = 0;
            max = 0;

            foreach (var child in box.Children)
            {
                if (!child.IsBlockLevel || PositionedLayout.IsOutOfFlow(child))
                {
                    continue;
                }

                var childIntrinsic = _engine.MeasureIntrinsic(child, context);
                var childMargin = child.Style.Margin.Resolve(basis).Horizontal;
                min = Math.Max(min, childIntrinsic.MinContent + childMargin);
                max = Math.Max(max, childIntrinsic.MaxContent + childMargin);
            }
        }
        else
        {
            var intrinsic = _inline.MeasureIntrinsic(box, basis);
            min = intrinsic.MinContent;
            max = intrinsic.MaxContent;
        }

        return new IntrinsicSize(min + insets, max + insets);
    }

    /// <summary>Gets the horizontal padding and border the box adds around its content.</summary>
    private Double InsetsOf(Box box, Double basis)
    {
        var border = BorderOf(box);
        var padding = box.Style.Padding.Resolve(basis);
        return padding.Horizontal + border.Horizontal;
    }

    /// <summary>
    /// Fixes a measured block into the given border box and lays its content out inside it. The
    /// distance between the box and that rectangle is already carried by the caller, which owns the
    /// vertical stacking.
    /// </summary>
    public LayoutBox Arrange(Box box, Rect borderBox, LayoutContext context)
    {
        var style = box.Style;
        var basis = context.ContainingBlock.Width;
        var border = BorderOf(box);
        var padding = style.Padding.Resolve(basis);

        var layout = new LayoutBox(box, KindOf(box))
        {
            Margin = BoxModel.UsedMargin(style, basis, borderBox.Width),
            Border = border,
            Padding = padding,
            BorderBox = borderBox,
        };

        if (box.Children.Count == 0)
        {
            return layout;
        }

        var contentX = borderBox.X + border.Left + padding.Left;
        var contentY = borderBox.Y + border.Top + padding.Top;
        var contentWidth = Math.Max(0, borderBox.Width - border.Horizontal - padding.Horizontal);

        // A declared height is known before the children are laid out, so it is also the containing
        // block height they resolve their own percentage heights against. A height that comes from
        // the content is not, which is why it is passed as zero there.
        var declaredHeight = BoxModel.DeclaredHeight(
            style,
            context.ContainingBlock.Height,
            padding.Vertical + border.Vertical);

        var childContext = context.ForContainingBlock(new Rect(contentX, contentY, contentWidth, declaredHeight ?? 0));

        // A positioned box is what an absolutely positioned descendant anchors to, so its padding
        // box - not its content box - becomes the containing block for the whole subtree.
        if (PositionedLayout.IsPositioned(box))
        {
            childContext = childContext.WithAbsoluteContainingBlock(layout.PaddingBox);
        }

        if (HasBlockChildren(box))
        {
            ArrangeBlockChildren(box, layout, contentX, contentY, contentWidth, childContext);
        }
        else
        {
            _inline.Layout(box, layout, contentX, contentY, contentWidth);
        }

        return layout;
    }

    /// <summary>Measures the block children and returns the height their stacking occupies.</summary>
    private Double MeasureBlockChildren(Box box, Double contentWidth, Double contentHeight, LayoutContext context)
    {
        var cursor = 0.0;
        var previousBottomMargin = 0.0;
        var first = true;
        var childContext = context.ForContainingBlock(new Size(contentWidth, contentHeight));
        var constraints = MeasureConstraints.FitWidth(contentWidth);

        foreach (var child in box.Children)
        {
            if (!child.IsBlockLevel)
            {
                continue;
            }

            // An out-of-flow child neither takes space nor lets its margins collapse with the ones
            // around it, so it is skipped before anything is measured.
            if (PositionedLayout.IsOutOfFlow(child))
            {
                continue;
            }

            // The parent's content box is the child's containing block, so the child is handed its
            // width rather than the width left after its own margins: resolving those is its job.
            var margin = child.Style.Margin.Resolve(contentWidth);
            var spacing = first ? margin.Top : Math.Max(previousBottomMargin, margin.Top);
            var size = _engine.Measure(child, constraints, childContext);

            cursor += spacing + size.Height;
            previousBottomMargin = margin.Bottom;
            first = false;
        }

        return cursor + previousBottomMargin;
    }

    private void ArrangeBlockChildren(
        Box box,
        LayoutBox parent,
        Double contentX,
        Double contentY,
        Double contentWidth,
        LayoutContext context)
    {
        var cursor = contentY;
        var previousBottomMargin = 0.0;
        var first = true;
        var constraints = MeasureConstraints.FitWidth(contentWidth);
        var outOfFlow = new List<(Box Box, Offset Static)>();

        foreach (var child in box.Children)
        {
            if (!child.IsBlockLevel)
            {
                continue;
            }

            var margin = child.Style.Margin.Resolve(contentWidth);
            var spacing = first ? margin.Top : Math.Max(previousBottomMargin, margin.Top);

            if (PositionedLayout.IsOutOfFlow(child))
            {
                // Its place in flow is remembered as the static position - the anchor an unset
                // offset falls back to - but the cursor does not advance, so the boxes after it
                // close the gap it leaves behind.
                outOfFlow.Add((child, new Offset(contentX + margin.Left, cursor + spacing)));
                continue;
            }

            var size = _engine.Measure(child, constraints, context);
            var used = BoxModel.UsedMargin(child.Style, contentWidth, size.Width);
            var rect = new Rect(contentX + used.Left, cursor + spacing, size.Width, size.Height);

            parent.Children.Add(_engine.Arrange(child, rect, context));

            cursor = rect.Bottom;
            previousBottomMargin = margin.Bottom;
            first = false;
        }

        // The out-of-flow boxes are placed last, against the nearest positioned ancestor that the
        // context already carries, and take no part in the stacking above.
        foreach (var (child, staticPosition) in outOfFlow)
        {
            parent.Children.Add(_engine.ArrangePositioned(child, staticPosition, context));
        }
    }

    public static Boolean HasBlockChildren(Box box)
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

    /// <summary>
    /// Gets the layout role the box plays. A table part is normally arranged by the table context,
    /// but a stray one that leaked into normal flow still has to announce what it is so the painter
    /// gives it the right treatment - a cell's background, for instance, is painted like a block's.
    /// </summary>
    private static LayoutKind KindOf(Box box) => box.Kind switch
    {
        BoxKind.HorizontalRule => LayoutKind.HorizontalRule,
        BoxKind.Table => LayoutKind.Table,
        BoxKind.TableRowGroup => LayoutKind.TableRowGroup,
        BoxKind.TableRow => LayoutKind.TableRow,
        BoxKind.TableCell => LayoutKind.TableCell,
        _ => LayoutKind.Block,
    };

    /// <summary>
    /// Gets the used border widths. A horizontal rule whose author left it borderless still draws
    /// the one-pixel line the element is for.
    /// </summary>
    public static Thickness BorderOf(Box box)
    {
        var style = box.Style;
        var border = new Thickness(
            style.BorderTopWidth,
            style.BorderRightWidth,
            style.BorderBottomWidth,
            style.BorderLeftWidth);

        return box.Kind == BoxKind.HorizontalRule && border.Horizontal == 0 && border.Vertical == 0
            ? new Thickness(1, 0, 0, 0)
            : border;
    }
}

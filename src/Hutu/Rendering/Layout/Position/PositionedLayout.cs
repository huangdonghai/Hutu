using AngleSharp.Css.Dom;
using Hutu.Rendering.Boxes;
using System;

namespace Hutu.Rendering.Layout;

/// <summary>
/// The positioning formatting context: it takes the boxes whose <c>position</c> removes them from
/// normal flow and places them against the right containing block, and it applies the smaller
/// offsets that <c>relative</c> and <c>sticky</c> add to a box's normal place.
/// </summary>
/// <remarks>
/// Only two of the four modes are actually laid out here. <c>relative</c> and <c>sticky</c> stay in
/// flow and are merely displaced (a translation, and a paint-time pin respectively); <c>absolute</c>
/// and <c>fixed</c> are removed from flow entirely and are measured and arranged on their own, which
/// is why the block context never sees them in its stack of children.
/// </remarks>
internal sealed class PositionedLayout
{
    private readonly LayoutEngine _engine;

    public PositionedLayout(LayoutEngine engine) => _engine = engine;

    /// <summary>
    /// Gets whether the box is removed from normal flow by <c>absolute</c> or <c>fixed</c>. An
    /// anonymous box never is: it borrows its parent's style, and taking that style literally would
    /// move a wrapper the parent itself already moved.
    /// </summary>
    public static Boolean IsOutOfFlow(Box box) => !box.IsAnonymous && box.Style.Position.IsOutOfFlow;

    /// <summary>Gets whether the box takes part in the stacking order, i.e. is anything but static.</summary>
    public static Boolean IsPositioned(Box box) => !box.IsAnonymous && box.Style.Position.IsPositioned;

    /// <summary>
    /// Applies the displacement a box's <c>position</c> asks for once it has been arranged in flow.
    /// </summary>
    public void Finish(LayoutBox layout, PositionStyle position, LayoutContext context)
    {
        switch (position.Position)
        {
            case PositionMode.Relative:
                // A relative box keeps its place in flow; only the drawing is moved, so the siblings
                // around it do not budge.
                Translate(layout, RelativeOffset(position, context.ContainingBlock));
                break;

            case PositionMode.Sticky:
                layout.Sticky = StickyRule(position, context, layout.BorderBox);
                break;
        }
    }

    /// <summary>
    /// Lays an out-of-flow box out against its containing block. The static position is where the box
    /// would have sat had it stayed in flow, which is what an unset offset falls back to.
    /// </summary>
    public LayoutBox Arrange(Box box, Offset staticPosition, LayoutContext context)
    {
        var position = box.Style.Position;

        // `fixed` always resolves against the viewport; `absolute` against the nearest positioned
        // ancestor, which the context already carries as its padding box.
        var containingBlock = position.Position == PositionMode.Fixed
            ? context.ViewportRect
            : context.AbsoluteContainingBlock;

        var childContext = context.ForContainingBlock(containingBlock);
        var measured = _engine.Measure(box, MeasureConstraints.FitWidth(containingBlock.Width), childContext);

        var style = box.Style;
        var margin = style.Margin.Resolve(containingBlock.Width);

        var (width, x) = Horizontal(position, style, containingBlock, staticPosition.X, measured.Width, margin);
        var (height, y) = Vertical(position, style, containingBlock, staticPosition.Y, measured.Height, margin);

        return _engine.Arrange(box, new Rect(x, y, width, height), childContext);
    }

    /// <summary>
    /// Shifts an arranged subtree. The whole subtree moves, so any absolutely positioned descendant
    /// rides along and stays anchored to the same padding box it was laid out against.
    /// </summary>
    internal static void Translate(LayoutBox box, Offset offset)
    {
        if (offset.X == 0 && offset.Y == 0)
        {
            return;
        }

        Shift(box, offset.X, offset.Y);
    }

    private static void Shift(LayoutBox box, Double dx, Double dy)
    {
        box.BorderBox = new Rect(
            box.BorderBox.X + dx,
            box.BorderBox.Y + dy,
            box.BorderBox.Width,
            box.BorderBox.Height);

        foreach (var child in box.Children)
        {
            Shift(child, dx, dy);
        }
    }

    /// <summary>
    /// Resolves the horizontal offset of a relatively positioned box. Two opposite edges
    /// over-constrain it; in a left-to-right writing mode the leading edge wins and the other is
    /// dropped, which is the rule the specification calls for.
    /// </summary>
    private static Offset RelativeOffset(PositionStyle position, Size containingBlock)
    {
        var dx = 0.0;
        var dy = 0.0;

        if (position.Left.HasValue)
        {
            dx = position.Left.Resolve(containingBlock.Width);
        }
        else if (position.Right.HasValue)
        {
            dx = -position.Right.Resolve(containingBlock.Width);
        }

        if (position.Top.HasValue)
        {
            dy = position.Top.Resolve(containingBlock.Height);
        }
        else if (position.Bottom.HasValue)
        {
            dy = -position.Bottom.Resolve(containingBlock.Height);
        }

        return new Offset(dx, dy);
    }

    private static StickyConstraint StickyRule(PositionStyle position, LayoutContext context, Rect box) => new(
        context.ViewportRect,
        context.ContainingBlockRect,
        box,
        Pixels(position.Top, context.ContainingBlock.Height),
        Pixels(position.Right, context.ContainingBlock.Width),
        Pixels(position.Bottom, context.ContainingBlock.Height),
        Pixels(position.Left, context.ContainingBlock.Width));

    /// <summary>Resolves an offset to pixels, or to <see cref="Double.NaN"/> for <c>auto</c>.</summary>
    private static Double Pixels(StyleLength length, Double basis) =>
        length.HasValue ? length.Resolve(basis) : Double.NaN;

    /// <summary>
    /// Resolves the used width and the left edge of an out-of-flow box. When the width is automatic
    /// and both edges are set, the box fills the space between them; otherwise the measured width is
    /// kept and the declared edge - or, failing that, the static position - decides the edge.
    /// </summary>
    private static (Double Width, Double X) Horizontal(
        PositionStyle position,
        ComputedStyle style,
        Rect containingBlock,
        Double staticX,
        Double measuredWidth,
        Thickness margin)
    {
        var hasLeft = position.Left.HasValue;
        var hasRight = position.Right.HasValue;
        var left = hasLeft ? position.Left.Resolve(containingBlock.Width) : 0;
        var right = hasRight ? position.Right.Resolve(containingBlock.Width) : 0;

        var declared = style.Width.HasValue &&
            !(style.Width.Kind == StyleLengthKind.Percent && containingBlock.Width <= 0);

        var width = !declared && hasLeft && hasRight
            ? Math.Max(0, containingBlock.Width - left - right - margin.Horizontal)
            : measuredWidth;

        Double x;

        if (hasLeft && hasRight && declared)
        {
            // Both edges and the width are definite, so a pair of `auto` margins share the slack and
            // centre the box, the same rule `margin: 0 auto` follows in normal flow.
            var slack = containingBlock.Width - left - right - width;
            var distributed = BoxModel.DistributeAutoMargins(style.Margin, margin, slack);
            x = containingBlock.X + left + distributed.Left;
        }
        else if (hasLeft)
        {
            x = containingBlock.X + left + margin.Left;
        }
        else if (hasRight)
        {
            x = containingBlock.Right - right - margin.Right - width;
        }
        else
        {
            x = staticX + margin.Left;
        }

        return (width, x);
    }

    /// <summary>Resolves the used height and the top edge of an out-of-flow box.</summary>
    private static (Double Height, Double Y) Vertical(
        PositionStyle position,
        ComputedStyle style,
        Rect containingBlock,
        Double staticY,
        Double measuredHeight,
        Thickness margin)
    {
        var hasTop = position.Top.HasValue;
        var hasBottom = position.Bottom.HasValue;
        var top = hasTop ? position.Top.Resolve(containingBlock.Height) : 0;
        var bottom = hasBottom ? position.Bottom.Resolve(containingBlock.Height) : 0;

        var declared = style.Height.HasValue &&
            !(style.Height.Kind == StyleLengthKind.Percent && containingBlock.Height <= 0);

        var height = !declared && hasTop && hasBottom
            ? Math.Max(0, containingBlock.Height - top - bottom - margin.Vertical)
            : measuredHeight;

        Double y;

        if (hasTop)
        {
            y = containingBlock.Y + top + margin.Top;
        }
        else if (hasBottom)
        {
            y = containingBlock.Bottom - bottom - margin.Bottom - height;
        }
        else
        {
            y = staticY + margin.Top;
        }

        return (height, y);
    }
}

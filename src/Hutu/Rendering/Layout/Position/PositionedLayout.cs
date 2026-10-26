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
    /// Gets whether the box is removed from normal flow by <c>absolute</c> or <c>fixed</c>.
    /// </summary>
    /// <remarks>
    /// Only a box that stands on its own can leave the flow, so a borrowed style is not read literally:
    /// an anonymous wrapper would otherwise be moved a second time by the declaration that moved the box
    /// it wraps, and a run of words - which is content of the line box rather than a box in it, and is
    /// fused from the style its container declared - would be taken for a box that left the flow too.
    /// Nothing places it then: the flex and grid item scans ask a child to stay in flow, and the
    /// positioning pass only walks the block-level ones, so an axis written as a positioned flex
    /// container loses every label it was given.
    /// </remarks>
    public static Boolean IsOutOfFlow(Box box) => CarriesItsOwn(box) && box.Style.Position.IsOutOfFlow;

    /// <summary>Gets whether the box takes part in the stacking order, i.e. is anything but static.</summary>
    public static Boolean IsPositioned(Box box) => CarriesItsOwn(box) && box.Style.Position.IsPositioned;

    /// <summary>
    /// Gets whether the box carries a style of its own, so that what its <c>position</c> says is a
    /// statement about this box rather than about the one it was made from.
    /// </summary>
    private static Boolean CarriesItsOwn(Box box) =>
        !box.IsAnonymous && box.Kind is not (BoxKind.Text or BoxKind.LineBreak);

    /// <summary>
    /// Gets the context a box's children see: the frame the box lends its descendants, if it lends one.
    /// </summary>
    /// <remarks>
    /// Two answers are handed down, because the properties that give a box a frame come in two strengths.
    /// A <c>position</c> that is not static makes the box the frame for <c>absolute</c> descendants only;
    /// a transform, a filter or containment makes it the frame for <c>fixed</c> ones as well, so those
    /// descendants cannot fall past it to the viewport. An anonymous box lends nothing: it borrows the
    /// style of the element it was made from, and the frame belongs to that element - and its padding box
    /// is not the same rectangle anyway, since the anonymous box stands inside the element's padding.
    /// </remarks>
    public static LayoutContext ForDescendants(LayoutContext context, Box box, Rect paddingBox, Rect borderBox)
    {
        // The boxes are recorded for every container, not only one that lends a frame: a shape an
        // offset-path draws sizes into the border, padding or content box of the element establishing
        // the containing block, which is this container whatever its positioning says.
        if (!box.IsAnonymous)
        {
            context = context.WithReferenceBox(
                borderBox,
                paddingBox,
                CornerRadii.Resolve(box.Style.BorderRadius, borderBox.Width, borderBox.Height));

            context = context.WithPerspective(PerspectiveOf(box, borderBox));
        }

        if (!LendsAFrame(box))
        {
            return context;
        }

        return box.Style.FormsContainingBlock(inlineLevel: false)
            ? context.WithCapturedContainingBlock(paddingBox)
            : context.WithAbsoluteContainingBlock(paddingBox);
    }

    /// <summary>
    /// The viewer a box puts between itself and its children, or null when it states no perspective. The
    /// distance is measured once against the box's own size, and the origin - a position - through the
    /// border box, which is where the eye stands in the page.
    /// </summary>
    private static PerspectiveView? PerspectiveOf(Box box, Rect borderBox)
    {
        var style = box.Style;
        var distance = TransformValue.ResolvePerspective(style.PerspectiveText, style.Dimensions, style.FoldingFontSize, style.Zoom);

        if (distance <= 0)
        {
            return null;
        }

        var (x, y) = TransformValue.ResolveOrigin(
            style.PerspectiveOriginText, borderBox.Width, borderBox.Height, style.Dimensions, style.FoldingFontSize, style.Zoom);

        return new PerspectiveView(distance, borderBox.X + x, borderBox.Y + y);
    }

    /// <summary>
    /// Gets whether the box lends the subtree below it a frame to resolve against, which is what makes an
    /// absolutely positioned child stand inside it rather than fall past it to the ancestor above.
    /// </summary>
    public static Boolean LendsAFrame(Box box, Boolean inlineLevel = false) =>
        !box.IsAnonymous && (box.Style.FormsContainingBlock(inlineLevel) || box.Style.Position.IsPositioned);

    /// <summary>
    /// Applies the displacement a box's <c>position</c> asks for once it has been arranged in flow.
    /// </summary>
    public void Finish(LayoutBox layout, PositionStyle position, LayoutContext context)
    {
        switch (position.Position)
        {
            case PositionMode.Sticky:
                layout.Sticky = StickyRule(position, context, layout.BorderBox);
                break;
        }
    }

    /// <summary>
    /// Lays an out-of-flow box out against its containing block. The static position is where the box
    /// would have sat had it stayed in flow, which is what an unset offset falls back to; where the
    /// container lends an area of its own, the box's own alignment moves it along that area first.
    /// </summary>
    public LayoutBox Arrange(
        Box box,
        Offset staticPosition,
        LayoutContext context,
        StaticStanding? standing = null)
    {
        var position = box.Style.Position;

        // `fixed` always resolves against the viewport; `absolute` against the nearest ancestor that
        // holds it, which the context already carries as its padding box. A transform, a filter or
        // containment on an ancestor is the exception that pulls `fixed` in too (CSS Transforms 1
        // 5.5, CSS Filter Effects 1 2.4, CSS Contain 2 2.2).
        var containingBlock = position.Position == PositionMode.Fixed
            ? context.CapturedContainingBlock ?? context.ViewportRect
            : context.AbsoluteContainingBlock;

        var childContext = context.ForContainingBlock(containingBlock);

        // An out-of-flow box resolves its offsets against this frame, so a shape its offset-path draws
        // sizes into this frame too - the padding box of the ancestor that answers for it, or the
        // viewport when none does - rather than into whatever box its parent happened to be laid out in.
        childContext = childContext.WithReferenceBox(containingBlock, containingBlock, default);

        var style = box.Style;
        var margin = style.Margin.Resolve(containingBlock.Width);
        var measured = Measure(box, position, style, containingBlock, margin, childContext);

        // The box's own padding and border, which the constraints are worked out against: the sizes
        // measured below are border-box ones, and ClampWidth speaks in those, while ClampHeight speaks
        // in the content box the rest of the sizing works in.
        var padding = style.Padding.Resolve(containingBlock.Width);
        var border = BlockLayout.BorderOf(box);
        var insetWidth = padding.Horizontal + border.Horizontal;
        var insetHeight = padding.Vertical + border.Vertical;

        // A constraint stated as one of the content keywords stands for what the box's own content comes
        // to, so it is measured here - once for each axis, and only when a constraint asks for it
        // (CSS Sizing 3 §5).
        Double? intrinsicWidth = null;
        Double? intrinsicHeight = null;

        if (style.MinWidth.IsContentSized || style.MaxWidth.IsContentSized)
        {
            intrinsicWidth = _engine.MeasureContentIntrinsic(box, childContext).MaxContent;
        }

        if (style.MinHeight.IsContentSized || style.MaxHeight.IsContentSized)
        {
            intrinsicHeight = _engine.Measure(
                box, MeasureConstraints.FitWidth(intrinsicWidth ?? measured.Width), childContext).Height;
        }

        var (width, x) = Horizontal(
            position, style, containingBlock, staticPosition.X, measured.Width, margin, insetWidth, standing, intrinsicWidth);
        var (height, y) = Vertical(
            position, style, containingBlock, staticPosition.Y, measured.Height, margin, insetHeight, standing, intrinsicHeight);

        var placed = _engine.Arrange(box, new Rect(x, y, width, height), childContext);

        return placed;
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
    /// Resolves the offset of a relatively positioned box. Two opposite edges over-constrain one axis,
    /// and which of them is dropped is the axis's to say: the inline axis keeps the edge its base
    /// direction begins at, and the block axis the edge the block flow begins at (CSS 2.1 §9.4.3 for a
    /// row; CSS Writing Modes 3 §7.3-7.4 turn the physical sides into the two axes' starts, so a column
    /// reading right to left keeps <c>right</c> where a row reading left to right keeps <c>left</c>).
    /// </summary>
    internal static Offset RelativeOffset(PositionStyle position, Size containingBlock, WritingModeInfo flow)
    {
        // Which physical pair belongs to which axis turns with the writing mode: in a column the
        // horizontal sides carry the block flow and the vertical ones the reading direction.
        var horizontal = flow.IsVertical ? flow.BlockSign : flow.InlineSign;
        var vertical = flow.IsVertical ? flow.InlineSign : flow.BlockSign;

        var dx = horizontal > 0
            ? position.Left.HasValue
                ? position.Left.Resolve(containingBlock.Width)
                : position.Right.HasValue ? -position.Right.Resolve(containingBlock.Width) : 0
            : position.Right.HasValue
                ? -position.Right.Resolve(containingBlock.Width)
                : position.Left.HasValue ? position.Left.Resolve(containingBlock.Width) : 0;

        var dy = vertical > 0
            ? position.Top.HasValue
                ? position.Top.Resolve(containingBlock.Height)
                : position.Bottom.HasValue ? -position.Bottom.Resolve(containingBlock.Height) : 0
            : position.Bottom.HasValue
                ? -position.Bottom.Resolve(containingBlock.Height)
                : position.Top.HasValue ? position.Top.Resolve(containingBlock.Height) : 0;

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
    /// Measures the out-of-flow box. An automatic width fills the containing block only when both
    /// horizontal edges are definite; with fewer than two, CSS 2.1 10.3.7 makes the box shrink to fit,
    /// so a lone <c>left</c> leaves the box the width its content asks for rather than the whole block.
    /// </summary>
    private Size Measure(
        Box box,
        PositionStyle position,
        ComputedStyle style,
        Rect containingBlock,
        Thickness margin,
        LayoutContext childContext)
    {
        // A width stated as one of the content keywords is as definite as a length is - the box's own
        // content is what settles it - so it is not a box that has to shrink to fit the room between
        // its edges (CSS Sizing 3 §4).
        var declared = style.Width.IsContentSized ||
            (style.Width.HasValue &&
                !(style.Width.Kind == StyleLengthKind.Percent && containingBlock.Width <= 0));
        var fills = position.Left.HasValue && position.Right.HasValue;

        if (declared || fills)
        {
            return _engine.Measure(box, MeasureConstraints.FitWidth(containingBlock.Width), childContext);
        }

        // The preferred width clamped between what the content cannot go below and the room the
        // containing block leaves it - the same shrink-to-fit a float uses, since both size a box that
        // the width constraint alone no longer solves for.
        var intrinsic = _engine.MeasureIntrinsic(box, childContext);
        var room = Math.Max(0, containingBlock.Width - margin.Horizontal);
        var fit = Math.Max(intrinsic.MinContent, Math.Min(room, intrinsic.MaxContent));

        return _engine.Measure(box, MeasureConstraints.FitWidth(fit + margin.Horizontal), childContext);
    }

    /// <summary>
    /// Resolves the used width and the left edge of an out-of-flow box. When the width is automatic
    /// and both edges are set, the box fills the space between them; otherwise the measured width is
    /// kept and the declared edge - or, failing that, the static position - decides the edge.
    /// </summary>
    /// <param name="insets">
    /// The box's own horizontal padding and border, which the width constraints are worked out against.
    /// They are the box's, not its margins': a constraint describes the box, while the margins are the
    /// space the constraint equation leaves around it.
    /// </param>
    private static (Double Width, Double X) Horizontal(
        PositionStyle position,
        ComputedStyle style,
        Rect containingBlock,
        Double staticX,
        Double measuredWidth,
        Thickness margin,
        Double insets,
        StaticStanding? standing,
        Double? intrinsicWidth = null)
    {
        var hasLeft = position.Left.HasValue;
        var hasRight = position.Right.HasValue;
        var left = hasLeft ? position.Left.Resolve(containingBlock.Width) : 0;
        var right = hasRight ? position.Right.Resolve(containingBlock.Width) : 0;

        var declared = style.Width.IsContentSized ||
            (style.Width.HasValue &&
                !(style.Width.Kind == StyleLengthKind.Percent && containingBlock.Width <= 0));

        var tentative = !declared && hasLeft && hasRight
            ? Math.Max(0, containingBlock.Width - left - right - margin.Horizontal)
            : measuredWidth;

        // CSS 2.1 §10.4: the tentative width is worked out without the constraints first, and a width
        // that falls outside them is worked out again with the bound standing in for the computed
        // width. Running the rules of §10.3.7 a second time is what decides which margin rule the box
        // falls under, because the bound has just made the width definite: a pair of auto margins only
        // shares the slack for a definite width, which is what centres a box stated
        // `left: 0; right: 0; width: auto; max-width: 1920px; margin: auto` rather than letting it fill
        // the containing block.
        var width = BoxModel.ClampWidth(style, tentative, containingBlock.Width, insets, intrinsicWidth);
        var settled = declared || width != tentative;

        Double x;

        if (hasLeft && hasRight && settled)
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
            // Neither edge is set, so the box stands where the container left it - moved along the area
            // it was lent by the alignment it asks for, since a box with no offsets of its own is aligned
            // within that area rather than dropped at its start (CSS Align 3 §5.2).
            x = staticX + margin.Left + (standing is { } area
                ? StaticStanding.Stand(area.Horizontal, area.Area.Width - width - margin.Horizontal)
                : 0);
        }

        return (width, x);
    }

    /// <summary>Resolves the used height and the top edge of an out-of-flow box.</summary>
    /// <inheritdoc cref="Horizontal" path="/param[@name='insets']"/>
    private static (Double Height, Double Y) Vertical(
        PositionStyle position,
        ComputedStyle style,
        Rect containingBlock,
        Double staticY,
        Double measuredHeight,
        Thickness margin,
        Double insets,
        StaticStanding? standing,
        Double? intrinsicHeight = null)
    {
        var hasTop = position.Top.HasValue;
        var hasBottom = position.Bottom.HasValue;
        var top = hasTop ? position.Top.Resolve(containingBlock.Height) : 0;
        var bottom = hasBottom ? position.Bottom.Resolve(containingBlock.Height) : 0;

        var declared = style.Height.IsContentSized ||
            (style.Height.HasValue &&
                !(style.Height.Kind == StyleLengthKind.Percent && containingBlock.Height <= 0));

        var tentative = !declared && hasTop && hasBottom
            ? Math.Max(0, containingBlock.Height - top - bottom - margin.Vertical)
            : measuredHeight;

        // The same second run of the rules as the width gets, against the vertical constraints: CSS 2.1
        // §10.7 clamps the tentative height against min-height and max-height, and a box the bound has
        // just made definite is one whose auto margins share the space left over (§10.6.4).
        var height = BoxModel.ClampHeight(
            style,
            Math.Max(0, tentative - insets),
            containingBlock.Height,
            insets,
            intrinsicHeight is { } content ? Math.Max(0, content - insets) : null) + insets;
        var settled = declared || height != tentative;

        Double y;

        if (hasTop && hasBottom && settled)
        {
            var slack = containingBlock.Height - top - bottom - height;
            var distributed = BoxModel.DistributeAutoVerticalMargins(style.Margin, margin, slack);
            y = containingBlock.Y + top + distributed.Top;
        }
        else if (hasTop)
        {
            y = containingBlock.Y + top + margin.Top;
        }
        else if (hasBottom)
        {
            y = containingBlock.Bottom - bottom - margin.Bottom - height;
        }
        else
        {
            y = staticY + margin.Top + (standing is { } area
                ? StaticStanding.Stand(area.Vertical, area.Area.Height - height - margin.Vertical)
                : 0);
        }

        return (height, y);
    }

    /// <summary>
    /// Gets how far from its area's start edge a box stands for the alignment it resolved to. Anything
    /// but the two that move a box along leave it where the area began, which is what a box with no room
    /// to be stretched into, and no line of text to join, comes to.
    /// </summary>
    /// <remarks>
    /// The room left over is not held at zero: a box bigger than the area it was aligned in overflows
    /// that area on both edges when it is centred, and past its far edge when it is pushed there, which
    /// is what a browser draws and the corpus's negative margins stand for.
    /// </remarks>
}

using Hutu.Rendering.Boxes;
using System;
using System.Collections.Generic;

namespace Hutu.Rendering.Layout;

/// <summary>
/// The flex formatting context: it lays a container's children out along a main axis, wrapping and
/// sharing out the space they do not fill.
/// </summary>
/// <remarks>
/// The algorithm is the one the specification walks through, and lives in the algorithm half of this
/// partial class: resolve each item's flex base size, break the items into lines, resolve the
/// flexible lengths on each line, size the lines on the cross axis, then align the items inside their
/// line and the lines inside the container.
/// <para>
/// Two simplifications are deliberate. The automatic minimum size of an item
/// (<c>min-width: auto</c>) is taken to be the content's min-content size on the main axis, which is
/// what stops a text item from shrinking to nothing. And a flex base size of <c>content</c> is
/// measured at the max-content size rather than refined iteratively.
/// </para>
/// </remarks>
internal sealed partial class FlexFormattingContext
{
    private const Double Epsilon = 0.0001;

    private readonly LayoutEngine _engine;

    public FlexFormattingContext(LayoutEngine engine)
    {
        _engine = engine;
    }

    public Size Measure(Box box, MeasureConstraints constraints, LayoutContext context)
    {
        var style = box.Style;
        var flex = style.Flex;
        var basisWidth = constraints.AvailableWidth ?? context.ContainingBlock.Width;
        var basisHeight = constraints.AvailableHeight ?? context.ContainingBlock.Height;
        var padding = style.Padding.Resolve(basisWidth);
        var border = BlockLayout.BorderOf(box);
        var insetH = padding.Horizontal + border.Horizontal;
        var insetV = padding.Vertical + border.Vertical;

        var (_, contentWidth) = _engine.ContentSizedWidth(box, basisWidth, context) is { } contentSized
            ? (contentSized, Math.Max(0, contentSized - insetH))
            : BoxModel.ResolveWidth(style, basisWidth, context.ContainingBlock.Width, insetH);
        var declaredHeight = BoxModel.DeclaredHeight(style, basisHeight, insetV);

        var insetMain = flex.IsRow ? insetH : insetV;
        var insetCross = flex.IsRow ? insetV : insetH;
        var mainSize = flex.IsRow ? contentWidth : declaredHeight;
        var crossSize = flex.IsRow ? declaredHeight : contentWidth;

        var itemContext = context.ForContainingBlock(new Size(contentWidth, declaredHeight ?? 0));
        var plan = Compute(box, flex, mainSize, crossSize, insetMain, insetCross, itemContext);

        var contentMain = flex.IsRow ? contentWidth : declaredHeight ?? plan.ContentMain;
        var contentCross = flex.IsRow ? declaredHeight ?? plan.ContentCross : contentWidth;
        var width = (flex.IsRow ? contentMain : contentCross) + insetH;
        var height = (flex.IsRow ? contentCross : contentMain) + insetV;

        return new Size(
            BoxModel.ClampWidth(style, width, basisWidth, insetH),
            BoxModel.ClampHeight(style, height - insetV, basisHeight, insetV) + insetV);
    }

    public LayoutBox Arrange(Box box, Rect borderBox, LayoutContext context)
    {
        var style = box.Style;
        var flex = style.Flex;
        var basis = context.ContainingBlock.Width;
        var padding = style.Padding.Resolve(basis);
        var border = BlockLayout.BorderOf(box);
        var insetH = padding.Horizontal + border.Horizontal;
        var insetV = padding.Vertical + border.Vertical;

        var layout = new LayoutBox(box, LayoutKind.Block)
        {
            Margin = BoxModel.UsedMargin(style, basis, borderBox.Width),
            Border = border,
            Padding = padding,
            BorderBox = borderBox,
            Flow = box.Flow,
        };

        if (box.Children.Count == 0)
        {
            return layout;
        }

        var contentWidth = Math.Max(0, borderBox.Width - insetH);
        var contentHeight = Math.Max(0, borderBox.Height - insetV);
        var contentX = borderBox.X + border.Left + padding.Left;
        var contentY = borderBox.Y + border.Top + padding.Top;
        var insetMain = flex.IsRow ? insetH : insetV;
        var insetCross = flex.IsRow ? insetV : insetH;
        var itemContext = context.ForContainingBlock(new Size(contentWidth, contentHeight));

        // A positioned flex container is what an absolutely positioned child anchors to.
        if (box.Style.IsTransformed)
        {
            itemContext = itemContext.WithTransformedContainingBlock(layout.BorderBox);
        }
        else if (PositionedLayout.IsPositioned(box))
        {
            itemContext = itemContext.WithAbsoluteContainingBlock(layout.PaddingBox);
        }

        var plan = Compute(
            box,
            flex,
            flex.IsRow ? contentWidth : contentHeight,
            flex.IsRow ? contentHeight : contentWidth,
            insetMain,
            insetCross,
            itemContext);

        foreach (var item in plan.Items)
        {
            var rect = new Rect(
                contentX + item.OffsetX,
                contentY + item.OffsetY,
                item.BorderBoxWidth,
                item.BorderBoxHeight);

            layout.Children.Add(_engine.Arrange(item.Box, rect, itemContext));
        }

        ArrangeOutOfFlow(box, layout, contentX, contentY, itemContext);
        return layout;
    }

    /// <summary>
    /// Places the absolutely and fixed positioned children, which are not items. Their static
    /// position is the content box's start corner, since they have no flex position of their own.
    /// </summary>
    private void ArrangeOutOfFlow(Box box, LayoutBox parent, Double contentX, Double contentY, LayoutContext context)
    {
        foreach (var child in box.Children)
        {
            if (child.IsBlockLevel && PositionedLayout.IsOutOfFlow(child))
            {
                parent.Children.Add(_engine.ArrangePositioned(child, new Offset(contentX, contentY), context));
            }
        }
    }

    /// <summary>Runs the algorithm and returns the container's sizes plus the placed items.</summary>
    private FlexPlan Compute(
        Box box,
        FlexStyle flex,
        Double? mainSize,
        Double? crossSize,
        Double insetMain,
        Double insetCross,
        LayoutContext context)
    {
        var isRow = flex.IsRow;
        var mainGap = Math.Max(0, isRow ? flex.ColumnGap : flex.RowGap);
        var crossGap = Math.Max(0, isRow ? flex.RowGap : flex.ColumnGap);

        var items = new List<FlexItem>();
        var sequence = 0;

        foreach (var child in box.Children)
        {
            // An absolutely or fixed positioned child is not a flex item: it is out of flow and is
            // placed against the container instead.
            if (child.IsBlockLevel && !PositionedLayout.IsOutOfFlow(child))
            {
                items.Add(CreateItem(child, isRow, mainSize, context, sequence++));
            }
        }

        // `order` is a stable sort key, so the document order of equal items is preserved - which takes
        // a second key to do: the sort trades items whose order is the same about as it partitions,
        // and above a container's handful of children the row arrives in an order no author wrote.
        items.Sort(static (left, right) =>
            left.Order != right.Order
                ? left.Order.CompareTo(right.Order)
                : left.Sequence.CompareTo(right.Sequence));

        foreach (var item in items)
        {
            item.BaseMain = ResolveBaseMain(item, isRow, mainSize, context);

            if (item.MinMain is null && isRow)
            {
                // CSS Flexbox 4.5: an item whose main-axis overflow is a scroll axis has an automatic
                // minimum size of zero - its content scrolls, so it sets no floor - which is what lets a
                // `flex-grow` item shrink below a wide (scrolling) child instead of ballooning the page.
                // `visible` and `clip` are not scroll axes, so they keep the content-based floor.
                if (item.Box.Style.ScrollsInline)
                {
                    item.MinMain = 0;
                }
                else
                {
                    // The automatic minimum size keeps a flex item from shrinking below its content, but it
                    // is the *smaller* of the content suggestion and the declared size, so a `width: 200px`
                    // item whose content wants less still shrinks when the line is too short. Measuring the
                    // content with the width set aside is what makes that distinction.
                    var contentMin = Math.Max(
                        0,
                        _engine.MeasureContentIntrinsic(item.Box, context).MinContent - item.InsetMain);
                    var declared = DeclaredContentMain(item, isRow, mainSize);
                    item.MinMain = declared is { } specified ? Math.Min(specified, contentMin) : contentMin;
                }
            }

            item.HypotheticalMain = item.ClampMain(item.BaseMain);
            item.UsedMain = item.HypotheticalMain;
        }

        var lines = BuildLines(items, mainSize, flex.Wraps, mainGap);

        foreach (var line in lines)
        {
            ResolveFlexibleLengths(line, mainSize, mainGap);
        }

        var contentCross = ResolveCrossSizes(
            lines, crossSize, crossGap, flex.AlignContent, flex.AlignItems, isRow, context);

        var contentMain = mainSize ?? MaxLineMain(lines);

        Place(lines, flex, contentMain, contentCross, mainGap);

        var plan = new FlexPlan
        {
            ContentMain = contentMain,
            ContentCross = contentCross,
        };

        plan.Items.AddRange(items);
        return plan;
    }

    private FlexItem CreateItem(
        Box child,
        Boolean isRow,
        Double? mainSize,
        LayoutContext context,
        Int32 sequence)
    {
        var style = child.Style;
        var flex = style.Flex;
        var basis = context.ContainingBlock.Width;
        var margin = style.Margin.Resolve(basis);
        var isBorderBox = BoxModel.IsBorderBox(style);

        // The item's own insets, never the container's: they are what turns a content size into a
        // border-box size, and a flex item is measured against its own padding and border.
        var padding = style.Padding.Resolve(basis);
        var border = BlockLayout.BorderOf(child);
        var insetMain = isRow ? padding.Horizontal + border.Horizontal : padding.Vertical + border.Vertical;
        var insetCross = isRow ? padding.Vertical + border.Vertical : padding.Horizontal + border.Horizontal;

        var item = new FlexItem
        {
            Box = child,
            Grow = Math.Max(0, flex.Grow),
            Shrink = Math.Max(0, flex.Shrink),
            Basis = flex.Basis,
            BasisIsContent = flex.BasisIsContent,
            Order = flex.Order,
            Sequence = sequence,
            AlignSelf = flex.AlignSelf,
            InsetMain = insetMain,
            InsetCross = insetCross,
            AutoCross = isRow ? !style.Height.HasValue : !style.Width.HasValue,
        };

        // Margins are gathered per logical axis, so the direction is applied only when placing.
        AssignMargins(item, isRow, style.Margin, margin);

        var mainBasis = mainSize ?? 0;
        var minMain = isRow ? style.MinWidth : style.MinHeight;
        var maxMain = isRow ? style.MaxWidth : style.MaxHeight;
        var minCross = isRow ? style.MinHeight : style.MinWidth;
        var maxCross = isRow ? style.MaxHeight : style.MaxWidth;
        var crossBasis = context.ContainingBlock.Height;

        item.MinMain = BoxModel.ContentBound(minMain, mainBasis, insetMain, isBorderBox);
        item.MaxMain = BoxModel.ContentBound(maxMain, mainBasis, insetMain, isBorderBox);
        item.MinCross = BoxModel.ContentBound(minCross, crossBasis, insetCross, isBorderBox);
        item.MaxCross = BoxModel.ContentBound(maxCross, crossBasis, insetCross, isBorderBox);
        return item;
    }

    private static void AssignMargins(FlexItem item, Boolean isRow, StyleThickness declared, Thickness resolved)
    {
        if (isRow)
        {
            item.MarginMainStart = resolved.Left;
            item.MarginMainEnd = resolved.Right;
            item.MarginCrossStart = resolved.Top;
            item.MarginCrossEnd = resolved.Bottom;
            item.AutoMarginMainStart = declared.Left.IsAuto;
            item.AutoMarginMainEnd = declared.Right.IsAuto;
            item.AutoMarginCrossStart = declared.Top.IsAuto;
            item.AutoMarginCrossEnd = declared.Bottom.IsAuto;
        }
        else
        {
            item.MarginMainStart = resolved.Top;
            item.MarginMainEnd = resolved.Bottom;
            item.MarginCrossStart = resolved.Left;
            item.MarginCrossEnd = resolved.Right;
            item.AutoMarginMainStart = declared.Top.IsAuto;
            item.AutoMarginMainEnd = declared.Bottom.IsAuto;
            item.AutoMarginCrossStart = declared.Left.IsAuto;
            item.AutoMarginCrossEnd = declared.Right.IsAuto;
        }
    }

    /// <summary>
    /// Resolves the flex base size: the declared <c>flex-basis</c>, else the main-size property, else
    /// the content. A percentage against an indefinite container falls through to the next source,
    /// which is what makes it behave as <c>auto</c>.
    /// </summary>
    private Double ResolveBaseMain(FlexItem item, Boolean isRow, Double? mainSize, LayoutContext context)
    {
        var style = item.Box.Style;
        var isBorderBox = BoxModel.IsBorderBox(style);

        // A base size of `content` is measured from what the item holds, and the main-size property
        // it would otherwise fall through to is exactly what `content` exists to set aside - so the
        // measurement is the one that keeps a declared width out of the answer.
        if (item.BasisIsContent)
        {
            return isRow
                ? Math.Max(0, _engine.MeasureContentIntrinsic(item.Box, context).MaxContent - item.InsetMain)
                : Math.Max(0, _engine.Measure(item.Box, MeasureConstraints.FitWidth(context.ContainingBlock.Width), context).Height - item.InsetMain);
        }

        if (item.Basis.HasValue && !(item.Basis.Kind == StyleLengthKind.Percent && mainSize is null))
        {
            var resolved = item.Basis.Resolve(mainSize ?? 0);
            return Math.Max(0, isBorderBox ? resolved - item.InsetMain : resolved);
        }

        return DeclaredContentMain(item, isRow, mainSize) ?? ContentMain(item, isRow, context);
    }

    /// <summary>
    /// Gets the main-size property as a content size, or <c>null</c> when it is absent or a percentage
    /// against an indefinite container. This is the <c>specified size suggestion</c>, which the
    /// automatic minimum size is built from.
    /// </summary>
    private static Double? DeclaredContentMain(FlexItem item, Boolean isRow, Double? mainSize)
    {
        var style = item.Box.Style;
        var main = isRow ? style.Width : style.Height;

        if (!main.HasValue || (main.Kind == StyleLengthKind.Percent && mainSize is null))
        {
            return null;
        }

        var resolved = Math.Max(0, main.Resolve(mainSize ?? 0));
        return BoxModel.IsBorderBox(style) ? Math.Max(0, resolved - item.InsetMain) : resolved;
    }

    /// <summary>Measures the content-box main size a flex base size of <c>content</c> asks for.</summary>
    private Double ContentMain(FlexItem item, Boolean isRow, LayoutContext context)
    {
        if (isRow)
        {
            return Math.Max(0, _engine.MeasureIntrinsic(item.Box, context).MaxContent - item.InsetMain);
        }

        var available = context.ContainingBlock.Width;
        var size = _engine.Measure(item.Box, MeasureConstraints.FitWidth(available), context);
        return Math.Max(0, size.Height - item.InsetMain);
    }

    /// <summary>Gets the horizontal padding and border of a box, which a constraint describes.</summary>
    private static Double InlineInsets(Box box, Double basis)
    {
        var border = BlockLayout.BorderOf(box);

        return box.Style.Padding.Resolve(basis).Horizontal + border.Horizontal;
    }

    private static Double MaxLineMain(List<FlexLine> lines)    {
        var maximum = 0.0;

        foreach (var line in lines)
        {
            maximum = Math.Max(maximum, line.MainSize);
        }

        return maximum;
    }

    /// <summary>
    /// The intrinsic inline sizes of the container: the widest item for <c>min-content</c>, and every
    /// item on a single line for <c>max-content</c>. Along a column the inline axis is the cross axis,
    /// so the widest item wins either way rather than the sum. A flex item whose basis is automatic
    /// asks for this before anything has been decided.
    /// </summary>
    /// <remarks>
    /// A container with a width of its own answers with that width: a box that states how wide it is
    /// contributes that width to whatever holds it, whether its content would have come to more or to
    /// less. The content only speaks when the width is left to it - which is the state
    /// <see cref="MeasureContentIntrinsic"/> answers in for the callers that have to set a declared
    /// width aside.
    /// </remarks>
    public IntrinsicSize MeasureIntrinsic(Box box, LayoutContext context)
    {
        var basis = context.ContainingBlock.Width;
        var insets = InlineInsets(box, basis);

        if (!box.Flow.IsVertical &&
            BoxModel.SettledInlineExtent(box.Style, basis, insets, context.BasisIsHypothetical) is { } settled)
        {
            return new IntrinsicSize(settled, settled);
        }

        return MeasureContentIntrinsic(box, context);
    }

    /// <summary>
    /// The sizes the container's items want, with the container's own declared width set aside. It is
    /// what a basis of <c>content</c> asks for, and what the automatic minimum size of a flex item is
    /// built from - a content size suggestion is the size of the content and nothing else
    /// (CSS Flexbox 1 §4.5), so the declared width must not be consulted here.
    /// </summary>
    public IntrinsicSize MeasureContentIntrinsic(Box box, LayoutContext context)
    {
        var style = box.Style;
        var flex = style.Flex;
        var basis = context.ContainingBlock.Width;
        var insets = InlineInsets(box, basis);
        var mainGap = Math.Max(0, flex.IsRow ? flex.ColumnGap : flex.RowGap);

        var min = 0.0;
        var max = 0.0;
        var widest = 0.0;
        var first = true;

        foreach (var child in box.Children)
        {
            if (!child.IsBlockLevel || PositionedLayout.IsOutOfFlow(child))
            {
                continue;
            }

            // An item's contribution along the container's main axis is the larger of what its content
            // wants and the size it states, clamped by its own min and max main size, so an item told
            // to be 14 wide contributes 14 however little its content comes to (CSS Flexbox 1 §9.9.3).
            // Across the axis a stated size is not read here: the item's outer content size is what the
            // line has to hold, and a definite cross size is honoured when the item is laid out.
            var childIntrinsic = _engine.MeasureContentIntrinsic(child, context);
            var margin = child.Style.Margin.Resolve(basis).Horizontal;

            var childMin = childIntrinsic.MinContent + margin;
            var childMax = childIntrinsic.MaxContent + margin;

            if (flex.IsRow &&
                BoxModel.SettledInlineExtent(
                    child.Style,
                    basis,
                    InlineInsets(child, basis),
                    context.BasisIsHypothetical) is { } settled)
            {
                childMin = Math.Max(childMin, settled + margin);
                childMax = Math.Max(childMax, settled + margin);
            }

            min = Math.Max(min, childMin);
            widest = Math.Max(widest, childMax);

            if (!first)
            {
                max += mainGap;
            }

            max += childMax;
            first = false;
        }

        // Along a column the inline axis is the cross axis: it is as wide as the widest item, and the
        // `max-content` there is that item's own `max-content` rather than, as the `min` half of the
        // pair would give, its `min-content`. A column holding a short title above a longer subtitle
        // is as wide as the subtitle wants to be, not as wide as one character.
        max = flex.IsRow ? max : widest;

        return new IntrinsicSize(min + insets, max + insets);
    }

    /// <summary>
    /// Turns the resolved sizes into positions. The items are walked along the main axis, the space
    /// they leave is handed to the auto margins or to <c>justify-content</c>, and each item is aligned
    /// inside its line on the cross axis. The logical axes are mapped onto X and Y here, once.
    /// </summary>
    /// <remarks>
    /// An auto margin on the main axis takes the free space itself and suppresses the justification,
    /// which is what makes a lone item sit in the middle of a row. <c>baseline</c> is approximated by
    /// the start edge: the first baseline of a child is not carried back up to the container.
    /// </remarks>
    private static void Place(List<FlexLine> lines, FlexStyle flex, Double contentMain, Double contentCross, Double mainGap)
    {
        var isRow = flex.IsRow;
        var reversed = flex.IsReversed;

        foreach (var line in lines)
        {
            var items = line.Items;
            var gaps = items.Count > 1 ? mainGap * (items.Count - 1) : 0;
            var used = gaps;
            var autoMargins = 0;

            foreach (var item in items)
            {
                used += item.OuterMain;

                if (item.AutoMarginMainStart)
                {
                    autoMargins++;
                }

                if (item.AutoMarginMainEnd)
                {
                    autoMargins++;
                }
            }

            // What the line has left over, which is negative when its items overflow it. The sign is
            // kept: `center` and `flex-end` answer for that case themselves - the specification has the
            // items of a centred line overflow "equally in both directions" when the free space is
            // negative - while the auto margins below only ever take space that is really there, and
            // the keyword that distributes space falls back rather than sharing a space that is not.
            var free = contentMain - used;
            Double offset = 0;
            Double extra = 0;

            if (autoMargins > 0)
            {
                ApplyAutoMainMargins(items, Math.Max(0, free) / autoMargins);
            }
            else
            {
                (offset, extra) = Justify(flex.JustifyContent, free, items.Count);
            }

            var lineCross = flex.IsWrapReversed
                ? contentCross - line.CrossStart - line.CrossSize
                : line.CrossStart;
            var cursor = offset;

            foreach (var item in items)
            {
                var cross = lineCross + CrossOffset(item, flex.AlignItems, line.CrossSize - item.OuterCross, line.Baseline);
                Double x;
                Double y;

                if (isRow)
                {
                    x = Main(reversed, contentMain, cursor, item);
                    y = cross + item.MarginCrossStart;
                }
                else
                {
                    y = Main(reversed, contentMain, cursor, item);
                    x = cross + item.MarginCrossStart;
                }

                item.OffsetX = x;
                item.OffsetY = y;
                item.BorderBoxWidth = isRow ? item.BorderBoxMain : item.BorderBoxCross;
                item.BorderBoxHeight = isRow ? item.BorderBoxCross : item.BorderBoxMain;
                cursor += item.OuterMain + mainGap + extra;
            }
        }
    }

    /// <summary>Gets where the item's border box starts along the main axis.</summary>
    private static Double Main(Boolean reversed, Double contentMain, Double cursor, FlexItem item) =>
        reversed
            ? contentMain - cursor - item.OuterMain + item.MarginMainStart
            : cursor + item.MarginMainStart;

    /// <summary>Hands the free space to the auto main margins, which is what centers a lone item.</summary>
    private static void ApplyAutoMainMargins(List<FlexItem> items, Double share)
    {
        if (share <= 0)
        {
            return;
        }

        foreach (var item in items)
        {
            if (item.AutoMarginMainStart)
            {
                item.MarginMainStart = share;
            }

            if (item.AutoMarginMainEnd)
            {
                item.MarginMainEnd = share;
            }
        }
    }

    /// <summary>Splits the free main-axis space according to <c>justify-content</c>.</summary>
    /// <summary>
    /// Gets where the line's first item starts and how much goes between the items, by the
    /// <c>justify-content</c> keyword the container states.
    /// </summary>
    /// <remarks>
    /// A space that is not there cannot be distributed: with the items overflowing the line, the three
    /// keywords that share space each fall back to the keyword the specification names for them -
    /// <c>space-between</c> to <c>safe flex-start</c>, and <c>space-around</c> and <c>space-evenly</c>
    /// to <c>safe center</c>, which is <c>flex-start</c> again once the box overflows. <c>center</c>
    /// and <c>flex-end</c> are not among them: a centred line's items "overflow equally in both
    /// directions" when the leftover space is negative, and an end-packed line keeps its last item's
    /// edge on the line's end edge, which puts the overflow on the start side.
    /// </remarks>
    private static (Double Offset, Double Extra) Justify(FlexAlignment justify, Double free, Int32 count)
    {
        if (free < 0)
        {
            justify = justify switch
            {
                FlexAlignment.SpaceBetween or FlexAlignment.SpaceAround or FlexAlignment.SpaceEvenly => FlexAlignment.Start,
                _ => justify,
            };
        }

        return justify switch
        {
            FlexAlignment.End => (free, 0.0),
            FlexAlignment.Center => (free / 2, 0.0),
            FlexAlignment.SpaceBetween when count > 1 => (0.0, free / (count - 1)),
            FlexAlignment.SpaceAround when count > 0 => (free / count / 2, free / count),
            FlexAlignment.SpaceEvenly when count > 0 => (free / (count + 1), free / (count + 1)),
            _ => (0.0, 0.0),
        };
    }

    /// <summary>
    /// Gets the distance from the line's cross start to the item's border box, letting the item's own
    /// <c>align-self</c> override the container's <c>align-items</c>. Auto cross margins take the
    /// space before the alignment keywords do.
    /// </summary>
    private static Double CrossOffset(FlexItem item, FlexAlignment alignItems, Double space, Double baseline)
    {
        if (item.AutoMarginCrossStart || item.AutoMarginCrossEnd)
        {
            // Auto margins take the space that is left over and none that is not, so an item too tall
            // for its line keeps its edge on the line's start rather than being pulled above it.
            var share = Math.Max(0, space);

            return item.AutoMarginCrossStart
                ? (item.AutoMarginCrossEnd ? share / 2 : share)
                : 0;
        }

        var align = item.AlignSelf == FlexAlignment.Auto ? alignItems : item.AlignSelf;

        if (align == FlexAlignment.Baseline)
        {
            // Baseline-aligned items share the line's baseline: each is set down as far below the
            // line's start as its own first baseline sits above the one they share. Nothing is
            // raised above the start - the lowest first baseline belongs to an item standing there.
            return Math.Max(0, baseline - item.FirstBaseline);
        }

        // Centred and end-packed items answer for their own overflow the same way the main axis does:
        // an item taller than its line is centred on it, hanging as far above the line's start as below
        // its end.
        return align switch
        {
            FlexAlignment.End => space,
            FlexAlignment.Center => space / 2,
            _ => 0,
        };
    }
}

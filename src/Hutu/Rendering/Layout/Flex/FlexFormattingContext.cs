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

        // A flex row follows the inline axis and a column the block one, so whether the main axis runs
        // across the page or down it is the writing mode's answer as much as the direction's. Every
        // physical size below is read through this, while which gap a rule decorates and which way a
        // line stacks stay the direction's own (CSS Flexbox 1 §2, §5).
        var mainIsHorizontal = flex.IsRow != box.Flow.IsVertical;
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

        // A container with a ratio takes its cross size from it (CSS Sizing 4 §4): a flex row whose
        // width is known takes its height from the ratio, and a flex column whose height is known
        // takes its width. The main size is the flex layout's own, and a ratio never settles it.
        var ratioHeight = mainIsHorizontal
            ? BoxModel.HeightFromRatio(style, contentWidth, null, insetV)
            : null;

        var ratioWidth = mainIsHorizontal
            ? null
            : BoxModel.WidthFromRatio(style, declaredHeight, insetH);

        // A column container whose height the page left to the content takes that height from its ratio
        // when its width is known, and a row container whose width was left to the content takes that
        // width from its ratio when its height is known (CSS Sizing 4 §4).
        var ratioMain = !mainIsHorizontal
            ? BoxModel.MainFromRatio(style, contentWidth, insetV)
            : null;

        var insetMain = mainIsHorizontal ? insetH : insetV;
        var insetCross = mainIsHorizontal ? insetV : insetH;
        var mainSize = mainIsHorizontal ? contentWidth : declaredHeight;
        var crossSize = mainIsHorizontal ? declaredHeight : contentWidth;

        var itemContext = context.ForContainingBlock(new Size(contentWidth, declaredHeight ?? 0));
        var plan = Compute(box, flex, mainSize, crossSize, insetMain, insetCross, itemContext);

        var contentMain = mainIsHorizontal ? contentWidth : declaredHeight ?? plan.ContentMain;
        var contentCross = mainIsHorizontal ? declaredHeight ?? plan.ContentCross : contentWidth;

        // The ratio settles the cross size where the page left it to the content, and the content may
        // be taller than it - then the content is what the box comes to (CSS Sizing 4 §4.4).
        if (mainIsHorizontal && ratioHeight is { } ratio)
        {
            contentCross = BoxModel.ContentRaisesTheRatio(style)
                ? Math.Max(ratio, plan.ContentCross)
                : ratio;
        }
        else if (!mainIsHorizontal && ratioWidth is { } wide)
        {
            contentCross = wide;
        }
        else if (!mainIsHorizontal && ratioMain is { } fromRatio)
        {
            // A column container whose height the page left to the content takes that height from its
            // ratio when its width is known: the ratio settles the size the flex layout does not
            // (CSS Sizing 4 §4).
            contentMain = fromRatio;
        }

        var width = (mainIsHorizontal ? contentMain : contentCross) + insetH;
        var height = (mainIsHorizontal ? contentCross : contentMain) + insetV;

        // Size containment answers both axes from the intrinsic size the page stated, whatever the items
        // want: the box must not change size because of what it holds (CSS Contain 1 §3.1.1, CSS Sizing
        // 4 §5).
        var contain = style.Contain;

        // The intrinsic size only stands where the page stated no size of its own for the axis.
        var declaredInline = box.Flow.IsVertical ? style.Height : style.Width;
        var declaredBlock = box.Flow.IsVertical ? style.Width : style.Height;

        if (contain.Inline && !ContainStyle.IsDefinite(declaredInline, basisWidth) &&
            (box.Flow.IsVertical || ContainStyle.InlineSizeIsContentBased(box)))
        {
            var stated = contain.InlineSize ?? (box.Flow.IsVertical ? contain.Height : contain.Width);
            width = (stated is { } inline ? Math.Max(0, inline.Resolve(0)) : 0) + insetH;
        }

        if (contain.Block && !ContainStyle.IsDefinite(declaredBlock, basisHeight))
        {
            var stated = contain.BlockSize ?? (box.Flow.IsVertical ? contain.Width : contain.Height);
            height = (stated is { } block ? Math.Max(0, block.Resolve(0)) : 0) + insetV;
        }

        return new Size(
            BoxModel.ClampWidth(style, width, basisWidth, insetH),
            BoxModel.ClampHeight(style, height - insetV, basisHeight, insetV) + insetV);
    }

    public LayoutBox Arrange(Box box, Rect borderBox, LayoutContext context)
    {
        var style = box.Style;
        var flex = style.Flex;
        var mainIsHorizontal = flex.IsRow != box.Flow.IsVertical;
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
        var insetMain = mainIsHorizontal ? insetH : insetV;
        var insetCross = mainIsHorizontal ? insetV : insetH;
        var itemContext = context.ForContainingBlock(new Size(contentWidth, contentHeight));

        // A container that holds its descendants is what an absolutely positioned child anchors to.
        itemContext = PositionedLayout.ForDescendants(itemContext, box, layout.PaddingBox, layout.BorderBox);

        var plan = Compute(
            box,
            flex,
            mainIsHorizontal ? contentWidth : contentHeight,
            mainIsHorizontal ? contentHeight : contentWidth,
            insetMain,
            insetCross,
            itemContext);

        layout.GapDecorations = GapDecorations(
            style.Gap, flex, box.Flow, plan, contentWidth, contentHeight, contentX, contentY);

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
    /// Works out the decorations of the gaps this container made. The room between two boxes standing
    /// next to each other is the gap - whether the page asked for it or the distribution left it, which
    /// is why a <c>justify-content</c> that spreads items apart rules the spaces it opened, while the
    /// room <c>align-content</c> leaves outside the lines is between no two boxes and gets nothing.
    /// </summary>
    /// <remarks>
    /// A gap between two items of a line runs the line's whole cross stretch, so a line whose items stand
    /// at different depths still has one rule across it; a gap between two lines runs the container's
    /// whole main stretch. Both are read in the order the container placed its boxes, because that is the
    /// order the decoration list is given out in (CSS Gaps 1 §4.6) - and a reversed direction reverses
    /// the order along with the boxes.
    /// </remarks>
    private static List<GapRuleSegment>? GapDecorations(
        GapRules rules,
        FlexStyle flex,
        WritingModeInfo flow,
        FlexPlan plan,
        Double contentWidth,
        Double contentHeight,
        Double contentX,
        Double contentY)
    {
        if (!rules.IsStated)
        {
            return null;
        }

        var isRow = flex.IsRow;
        var mainIsHorizontal = isRow != flow.IsVertical;
        var reversed = flex.IsReversed;
        var mainStart = mainIsHorizontal ? contentX : contentY;
        var crossStart = mainIsHorizontal ? contentY : contentX;
        var itemGaps = new List<Gap>();
        var lineGaps = new List<Gap>();
        var laid = new List<List<(Double From, Double To)>>(plan.Lines.Count);

        // The room between two lines is the crossing a rule between items of one line runs into at its
        // ends, and its size is what a percentage junction inset is read against.
        var between = new Double[Math.Max(0, plan.Lines.Count - 1)];

        for (var index = 0; index < between.Length; index++)
        {
            var line = plan.Lines[index];
            var next = plan.Lines[index + 1];
            between[index] = Math.Max(0,
                Math.Max(line.CrossStart, next.CrossStart) -
                Math.Min(line.CrossStart + line.CrossSize, next.CrossStart + next.CrossSize));
        }

        for (var lineIndex = 0; lineIndex < plan.Lines.Count; lineIndex++)
        {
            var line = plan.Lines[lineIndex];
            var lineStart = crossStart + line.CrossStart;
            var lineEnd = lineStart + line.CrossSize;
            var startCrossing = lineIndex > 0 ? between[lineIndex - 1] : (Double?)null;
            var endCrossing = lineIndex + 1 < plan.Lines.Count ? between[lineIndex] : (Double?)null;
            var previousLow = Double.NaN;
            var previousHigh = Double.NaN;
            var intervals = new List<(Double From, Double To)>();

            foreach (var item in line.Items)
            {
                var main = mainIsHorizontal ? item.OffsetX : item.OffsetY;
                var size = mainIsHorizontal ? item.BorderBoxWidth : item.BorderBoxHeight;
                var leading = reversed ? item.MarginMainEnd : item.MarginMainStart;
                var trailing = reversed ? item.MarginMainStart : item.MarginMainEnd;
                var low = main - leading;
                var high = main + size + trailing;

                if (!Double.IsNaN(previousLow))
                {
                    // The two boxes face each other across the space between them, whichever side of that
                    // space each of them stands on - a reversed container has placed the later one before.
                    var from = Math.Min(high, previousHigh);
                    var to = Math.Max(low, previousLow);

                    itemGaps.Add(new Gap(
                        mainIsHorizontal, mainStart + (from + to) / 2, lineStart, lineEnd,
                        startCrossing, endCrossing));
                    intervals.Add((mainStart + from, mainStart + to));
                }

                previousLow = low;
                previousHigh = high;
            }

            laid.Add(intervals);
        }

        var mainEnd = mainIsHorizontal ? contentX + contentWidth : contentY + contentHeight;
        var splits = new List<List<(Double From, Double To)>>(Math.Max(0, plan.Lines.Count - 1));
        var lineCrossings = new List<List<(Double From, Double To)>>(splits.Capacity);

        for (var index = 0; index + 1 < plan.Lines.Count; index++)
        {
            var line = plan.Lines[index];
            var next = plan.Lines[index + 1];
            var from = Math.Min(line.CrossStart + line.CrossSize, next.CrossStart + next.CrossSize);
            var to = Math.Max(line.CrossStart, next.CrossStart);

            lineGaps.Add(new Gap(!mainIsHorizontal, crossStart + (from + to) / 2, mainStart, mainEnd));

            // A column gap abutting this one from either line above or below is where an intersecting
            // decoration ends and the next one starts, so the two lines' gaps are joined into the
            // crossings this gap is split at (CSS Gaps 1 §3.1.1).
            var crossings = Unions(laid[index], laid[index + 1]);

            splits.Add(isRow && rules.Row.Break == GapRuleBreak.Intersection
                ? Complement(mainStart, mainEnd, crossings)
                : !isRow && rules.Column.Break == GapRuleBreak.Intersection
                    ? Complement(mainStart, mainEnd, crossings)
                    : new List<(Double, Double)> { (mainStart, mainEnd) });
            lineCrossings.Add(crossings);
        }

        var target = new List<GapRuleSegment>();

        // The room between two boxes runs along the axis the boxes are not stacked on, so a rule's two ends
        // follow whichever of them the container's writing mode turns back: a column rule down the block
        // axis, a row rule along the inline (CSS Gaps 1 §3.3).
        var blockReversed = flow.BlockSign < 0;
        var inlineReversed = flow.InlineSign < 0;
        var itemAxis = (isRow ? rules.Column : rules.Row).MeasuredFromTheEnd(isRow ? blockReversed : inlineReversed);
        var lineAxis = (isRow ? rules.Row : rules.Column).MeasuredFromTheEnd(isRow ? inlineReversed : blockReversed);
        var lineKind = isRow;

        GapRuleBands.Add(target, itemAxis, isRow: !isRow, itemGaps);

        for (var index = 0; index < lineGaps.Count; index++)
        {
            var gap = lineGaps[index];
            var rule = lineAxis.At(index, lineGaps.Count);

            if (!rule.Appears)
            {
                continue;
            }

            foreach (var (from, to) in splits[index])
            {
                // An end that stands at the container's edge is a cap; one that stands where a column gap
                // beside it begins is a junction, and takes its percentage of that gap (CSS Gaps 1 §3.3).
                var startAtBound = Math.Abs(from - mainStart) < 0.01;
                var endAtBound = Math.Abs(to - mainEnd) < 0.01;
                var start = from + (startAtBound ? lineAxis.CapStart : lineAxis.JunctionStart)
                    .Resolve(Crossing(lineCrossings[index], from, true), 0);
                var end = to - (endAtBound ? lineAxis.CapEnd : lineAxis.JunctionEnd)
                    .Resolve(Crossing(lineCrossings[index], to, false), 0);

                if (end > start)
                {
                    target.Add(new GapRuleSegment(
                        GapRuleBands.Band(rule, gap.CentreIsX, gap.Centre, start, end), rule, lineKind));
                }
            }
        }

        return target.Count > 0 ? target : null;
    }

    /// <summary>
    /// Joins the intervals the two lines' column gaps take into the crossings a row gap runs through.
    /// </summary>
    private static List<(Double From, Double To)> Unions(
        List<(Double From, Double To)> above,
        List<(Double From, Double To)> below)
    {
        var all = new List<(Double From, Double To)>(above);
        all.AddRange(below);
        all.Sort((left, right) => left.From.CompareTo(right.From));

        var joined = new List<(Double From, Double To)>();
        var from = 0.0;
        var to = 0.0;
        var open = false;

        foreach (var interval in all)
        {
            if (!open)
            {
                (from, to, open) = (interval.From, interval.To, true);
            }
            else if (interval.From <= to)
            {
                to = Math.Max(to, interval.To);
            }
            else
            {
                joined.Add((from, to));
                (from, to) = (interval.From, interval.To);
            }
        }

        if (open)
        {
            joined.Add((from, to));
        }

        return joined;
    }

    /// <summary>Gets the width of the crossing that stands at one end of a stretch.</summary>
    private static Double Crossing(List<(Double From, Double To)> crossings, Double at, Boolean start)
    {
        foreach (var crossing in crossings)
        {
            if (start ? Math.Abs(crossing.To - at) < 0.01 : Math.Abs(crossing.From - at) < 0.01)
            {
                return crossing.To - crossing.From;
            }
        }

        return 0;
    }

    /// <summary>Gets the stretches of [from, to] the crossings leave uncovered.</summary>
    private static List<(Double From, Double To)> Complement(
        Double from,
        Double to,
        List<(Double From, Double To)> crossings)
    {
        var pieces = new List<(Double From, Double To)>();
        var cursor = from;

        foreach (var crossing in crossings)
        {
            if (crossing.From > cursor)
            {
                pieces.Add((cursor, Math.Min(crossing.From, to)));
            }

            cursor = Math.Max(cursor, crossing.To);

            if (cursor >= to)
            {
                break;
            }
        }

        if (cursor < to)
        {
            pieces.Add((cursor, to));
        }

        return pieces;
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
        var mainIsHorizontal = isRow != box.Flow.IsVertical;
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
                items.Add(CreateItem(child, mainIsHorizontal, box.Flow, mainSize, context, sequence++));
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
            item.BaseMain = ResolveBaseMain(item, mainIsHorizontal, mainSize, context);

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
                    var declared = DeclaredContentMain(item, mainIsHorizontal, mainSize);
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
            lines, crossSize, crossGap, flex.AlignContent, flex.AlignItems, mainIsHorizontal, context);

        var contentMain = mainSize ?? MaxLineMain(lines);

        Place(lines, flex, box.Flow, contentMain, contentCross, mainGap);

        var plan = new FlexPlan
        {
            ContentMain = contentMain,
            ContentCross = contentCross,
        };

        plan.Items.AddRange(items);
        plan.Lines.AddRange(lines);
        return plan;
    }

    private FlexItem CreateItem(
        Box child,
        Boolean mainIsHorizontal,
        WritingModeInfo flow,
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
        var insetMain = mainIsHorizontal
            ? padding.Horizontal + border.Horizontal
            : padding.Vertical + border.Vertical;
        var insetCross = mainIsHorizontal
            ? padding.Vertical + border.Vertical
            : padding.Horizontal + border.Horizontal;

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
            AutoCross = mainIsHorizontal ? !style.Height.HasValue : !style.Width.HasValue,
        };

        // Margins are gathered per logical axis, so the direction is applied only when placing.
        AssignMargins(item, mainIsHorizontal, flow, style.Margin, margin);

        var mainBasis = mainSize ?? 0;
        var minMain = mainIsHorizontal ? style.MinWidth : style.MinHeight;
        var maxMain = mainIsHorizontal ? style.MaxWidth : style.MaxHeight;
        var minCross = mainIsHorizontal ? style.MinHeight : style.MinWidth;
        var maxCross = mainIsHorizontal ? style.MaxHeight : style.MaxWidth;
        var crossBasis = mainIsHorizontal ? context.ContainingBlock.Height : context.ContainingBlock.Width;

        item.MinMain = BoxModel.ContentBound(minMain, mainBasis, insetMain, isBorderBox);
        item.MaxMain = BoxModel.ContentBound(maxMain, mainBasis, insetMain, isBorderBox);
        item.MinCross = BoxModel.ContentBound(minCross, crossBasis, insetCross, isBorderBox);
        item.MaxCross = BoxModel.ContentBound(maxCross, crossBasis, insetCross, isBorderBox);
        return item;
    }

    private static void AssignMargins(
        FlexItem item,
        Boolean mainIsHorizontal,
        WritingModeInfo flow,
        StyleThickness declared,
        Thickness resolved)
    {
        // The side a logical start stands on is the writing mode's to say: a row reads rightward under
        // `direction: rtl`, a column of a right-to-left page runs leftward, and a vertical page's
        // columns either march rightward or leftward (CSS Writing Modes 3, section 6).
        Boolean mainFromEnd, crossFromEnd;

        if (mainIsHorizontal)
        {
            mainFromEnd = flow.InlineSign < 0;
            crossFromEnd = flow.BlockSign < 0;

            item.MarginMainStart = mainFromEnd ? resolved.Right : resolved.Left;
            item.MarginMainEnd = mainFromEnd ? resolved.Left : resolved.Right;
            item.AutoMarginMainStart = mainFromEnd ? declared.Right.IsAuto : declared.Left.IsAuto;
            item.AutoMarginMainEnd = mainFromEnd ? declared.Left.IsAuto : declared.Right.IsAuto;

            item.MarginCrossStart = crossFromEnd ? resolved.Bottom : resolved.Top;
            item.MarginCrossEnd = crossFromEnd ? resolved.Top : resolved.Bottom;
            item.AutoMarginCrossStart = crossFromEnd ? declared.Bottom.IsAuto : declared.Top.IsAuto;
            item.AutoMarginCrossEnd = crossFromEnd ? declared.Top.IsAuto : declared.Bottom.IsAuto;
        }
        else
        {
            mainFromEnd = flow.InlineSign < 0;
            crossFromEnd = flow.BlockSign < 0;

            item.MarginMainStart = mainFromEnd ? resolved.Bottom : resolved.Top;
            item.MarginMainEnd = mainFromEnd ? resolved.Top : resolved.Bottom;
            item.AutoMarginMainStart = mainFromEnd ? declared.Bottom.IsAuto : declared.Top.IsAuto;
            item.AutoMarginMainEnd = mainFromEnd ? declared.Top.IsAuto : declared.Bottom.IsAuto;

            item.MarginCrossStart = crossFromEnd ? resolved.Right : resolved.Left;
            item.MarginCrossEnd = crossFromEnd ? resolved.Left : resolved.Right;
            item.AutoMarginCrossStart = crossFromEnd ? declared.Right.IsAuto : declared.Left.IsAuto;
            item.AutoMarginCrossEnd = crossFromEnd ? declared.Left.IsAuto : declared.Right.IsAuto;
        }
    }

    /// <summary>
    /// Resolves the flex base size: the declared <c>flex-basis</c>, else the main-size property, else
    /// the content. A percentage against an indefinite container falls through to the next source,
    /// which is what makes it behave as <c>auto</c>.
    /// </summary>
    private Double ResolveBaseMain(FlexItem item, Boolean mainIsHorizontal, Double? mainSize, LayoutContext context)
    {
        var style = item.Box.Style;
        var isBorderBox = BoxModel.IsBorderBox(style);

        // A base size of `content` is measured from what the item holds, and the main-size property
        // it would otherwise fall through to is exactly what `content` exists to set aside - so the
        // measurement is the one that keeps a declared width out of the answer.
        if (item.BasisIsContent)
        {
            return mainIsHorizontal
                ? Math.Max(0, _engine.MeasureContentIntrinsic(item.Box, context).MaxContent - item.InsetMain)
                : Math.Max(0, _engine.Measure(item.Box, MeasureConstraints.FitWidth(context.ContainingBlock.Width), context).Height - item.InsetMain);
        }

        if (item.Basis.HasValue && !(item.Basis.Kind == StyleLengthKind.Percent && mainSize is null))
        {
            var resolved = item.Basis.Resolve(mainSize ?? 0);
            return Math.Max(0, isBorderBox ? resolved - item.InsetMain : resolved);
        }

        return DeclaredContentMain(item, mainIsHorizontal, mainSize) ?? ContentMain(item, mainIsHorizontal, context);
    }

    /// <summary>
    /// Gets the main-size property as a content size, or <c>null</c> when it is absent or a percentage
    /// against an indefinite container. This is the <c>specified size suggestion</c>, which the
    /// automatic minimum size is built from.
    /// </summary>
    private static Double? DeclaredContentMain(FlexItem item, Boolean mainIsHorizontal, Double? mainSize)
    {
        var style = item.Box.Style;
        var main = mainIsHorizontal ? style.Width : style.Height;

        if (!main.HasValue || (main.Kind == StyleLengthKind.Percent && mainSize is null))
        {
            return null;
        }

        var resolved = Math.Max(0, main.Resolve(mainSize ?? 0));
        return BoxModel.IsBorderBox(style) ? Math.Max(0, resolved - item.InsetMain) : resolved;
    }

    /// <summary>Measures the content-box main size a flex base size of <c>content</c> asks for.</summary>
    private Double ContentMain(FlexItem item, Boolean mainIsHorizontal, LayoutContext context)
    {
        if (mainIsHorizontal)
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
        var mainIsHorizontal = flex.IsRow != box.Flow.IsVertical;
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

            if (mainIsHorizontal &&
                BoxModel.SettledInlineExtent(
                    child.Style,
                    basis,
                    InlineInsets(child, basis),
                    context.BasisIsHypothetical) is { } settled)
            {
                // A stated width settles what the item contributes, whatever its content wants: the
                // container of a 200px item is 200px wide even when the item's text is longer or
                // shorter, and an item written `width: 0` contributes nothing (measured in Chromium;
                // CSS Flexbox 1 §9.9.3).
                childMin = settled + margin;
                childMax = settled + margin;
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
        max = mainIsHorizontal ? max : widest;

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
    private static void Place(
        List<FlexLine> lines,
        FlexStyle flex,
        WritingModeInfo flow,
        Double contentMain,
        Double contentCross,
        Double mainGap)
    {
        var mainIsHorizontal = flex.IsRow != flow.IsVertical;
        var mainSign = flex.IsRow ? flow.InlineSign : flow.BlockSign;
        var crossSign = flex.IsRow ? flow.BlockSign : flow.InlineSign;
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
                // Both coordinates are worked out in the logical frame the algorithm uses - from the
                // main start and from the line's cross start - and turned over once here where an axis
                // grows the other way: a right-to-left row, or the columns of a vertical page.
                var main = Main(reversed, contentMain, cursor, item);
                var cross = lineCross + CrossOffset(item, flex.AlignItems, line.CrossSize - item.OuterCross, line.Baseline) +
                    item.MarginCrossStart;

                var mainPhysical = mainSign < 0 ? contentMain - main - item.BorderBoxMain : main;
                var crossPhysical = crossSign < 0 ? contentCross - cross - item.BorderBoxCross : cross;

                if (mainIsHorizontal)
                {
                    item.OffsetX = mainPhysical;
                    item.OffsetY = crossPhysical;
                }
                else
                {
                    item.OffsetX = crossPhysical;
                    item.OffsetY = mainPhysical;
                }

                item.BorderBoxWidth = mainIsHorizontal ? item.BorderBoxMain : item.BorderBoxCross;
                item.BorderBoxHeight = mainIsHorizontal ? item.BorderBoxCross : item.BorderBoxMain;
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
            FlexAlignment.End or FlexAlignment.Right => (free, 0.0),
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

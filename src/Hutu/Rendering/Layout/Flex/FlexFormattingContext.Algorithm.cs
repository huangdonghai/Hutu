using System;
using System.Collections.Generic;

namespace Hutu.Rendering.Layout;

/// <summary>
/// The algorithm half of the flex formatting context: line breaking, the resolution of flexible
/// lengths and cross sizing. Placement lives in the companion partial file.
/// </summary>
/// <remarks>
/// Every loop here has a bound. The flexible-length resolution iterates until no further item is
/// clamped by its minimum or maximum, freezing at least one item per round, so it converges in at
/// most as many rounds as there are items; the cap guards against floating-point residue rather than
/// being a limit the algorithm is expected to reach.
/// </remarks>
internal sealed partial class FlexFormattingContext
{
    private const Int32 MaxFlexIterations = 16;

    /// <summary>Breaks the ordered items into lines that fit the container's main size.</summary>
    private static List<FlexLine> BuildLines(List<FlexItem> items, Double? mainSize, Boolean wrap, Double mainGap)
    {
        var lines = new List<FlexLine>();
        var line = new FlexLine();
        var used = 0.0;

        foreach (var item in items)
        {
            var outer = item.OuterHypothetical;

            if (wrap && mainSize is { } available && line.Items.Count > 0 &&
                used + mainGap + outer > available + Epsilon)
            {
                line.MainSize = used;
                lines.Add(line);
                line = new FlexLine();
                used = 0;
            }

            if (line.Items.Count > 0)
            {
                used += mainGap;
            }

            line.Items.Add(item);
            used += outer;
        }

        if (line.Items.Count > 0)
        {
            line.MainSize = used;
            lines.Add(line);
        }

        return lines;
    }

    /// <summary>
    /// Gives the items on a line their used main sizes. Free space goes to the grow factors, a
    /// shortage is taken from the shrink factors weighted by the base size, and either way an item
    /// clamped by its minimum or maximum is frozen and the remainder shared among the rest.
    /// </summary>
    private static void ResolveFlexibleLengths(FlexLine line, Double? mainSize, Double mainGap)
    {
        var items = line.Items;
        var count = items.Count;
        var sizes = new Double[count];

        // The flex base size, which is what the free space is measured against and what each item's
        // share is added to - not the hypothetical size, which has already been floored by the item's
        // minimum and would charge the item for room it is about to be given anyway (CSS Flexbox 1
        // §9.7). The used size of an item the clamp froze is kept in `sizes` beside it.
        var bases = new Double[count];

        var frozen = new Boolean[count];
        var factors = new Double[count];
        var gaps = count > 1 ? mainGap * (count - 1) : 0;

        var totalOuter = 0.0;

        for (var i = 0; i < count; i++)
        {
            sizes[i] = items[i].HypotheticalMain;
            bases[i] = items[i].BaseMain;
            totalOuter += Outer(items[i], sizes[i]);
        }

        if (mainSize is not { } available)
        {
            line.MainSize = totalOuter + gaps;
            Apply(items, sizes);
            return;
        }

        var inner = Math.Max(0, available - gaps);
        var free = inner - totalOuter;

        if (free > Epsilon)
        {
            for (var i = 0; i < count; i++)
            {
                factors[i] = items[i].Grow;
            }
        }
        else if (free < -Epsilon)
        {
            for (var i = 0; i < count; i++)
            {
                factors[i] = items[i].Shrink * Math.Max(0, sizes[i]);
            }
        }

        var factorSum = 0.0;

        foreach (var factor in factors)
        {
            factorSum += factor;
        }

        if (factorSum > Epsilon)
        {
            // An item whose factor is zero takes no share of the flexible space and is frozen at its
            // hypothetical main size. Run through the distribution, its share would be driven to
            // nothing and it would land on its minimum instead of the size it declared - a
            // `flex-shrink: 0` item at its basis, a `flex-grow: 0` one at its width.
            for (var i = 0; i < count; i++)
            {
                if (factors[i] <= Epsilon)
                {
                    frozen[i] = true;
                }
            }

            for (var iteration = 0; iteration < MaxFlexIterations; iteration++)
            {
                var frozenOuter = 0.0;
                var unfrozenOuter = 0.0;
                var remainingFactors = 0.0;

                for (var i = 0; i < count; i++)
                {
                    if (frozen[i])
                    {
                        frozenOuter += Outer(items[i], sizes[i]);
                    }
                    else
                    {
                        unfrozenOuter += Outer(items[i], bases[i]);
                        remainingFactors += factors[i];
                    }
                }

                if (remainingFactors <= Epsilon)
                {
                    break;
                }

                // What the container has left is measured against the outer size of every item, frozen
                // or not: the margins and borders come off the free space once, before it is shared, and
                // each unfrozen item adds its share to the base size it was measured at (CSS Flexbox 1
                // §9.7.3). Taking the container and dividing it out first would make an item with no
                // margins wider than its neighbours for no reason the page gave.
                var leftover = inner - frozenOuter - unfrozenOuter;
                var froze = false;

                for (var i = 0; i < count; i++)
                {
                    if (frozen[i])
                    {
                        continue;
                    }

                    var target = Math.Max(0, bases[i] + leftover * factors[i] / remainingFactors);
                    var clamped = items[i].ClampMain(target);
                    sizes[i] = clamped;

                    if (Math.Abs(clamped - target) > Epsilon)
                    {
                        frozen[i] = true;
                        froze = true;
                    }
                }

                if (!froze)
                {
                    break;
                }
            }
        }

        Apply(items, sizes);

        var used = 0.0;

        for (var i = 0; i < count; i++)
        {
            used += Outer(items[i], sizes[i]);
        }

        line.MainSize = used + gaps;
    }

    private static void Apply(List<FlexItem> items, Double[] sizes)
    {
        for (var i = 0; i < items.Count; i++)
        {
            items[i].UsedMain = sizes[i];
        }
    }

    private static Double Outer(FlexItem item, Double contentMain) =>
        contentMain + item.InsetMain + item.MarginMainStart + item.MarginMainEnd;

    /// <summary>
    /// Sizes the lines on the cross axis. When the container sizes its own cross axis the lines keep
    /// their content size; when the container's cross size is definite a single line fills it and
    /// several lines share it through <c>align-content</c>. The items are stretched afterwards, once
    /// the line they belong to is final.
    /// </summary>
    private Double ResolveCrossSizes(
        List<FlexLine> lines,
        Double? crossSize,
        Double crossGap,
        FlexAlignment alignContent,
        FlexAlignment alignItems,
        Boolean mainIsHorizontal,
        LayoutContext context)
    {
        if (lines.Count == 0)
        {
            return crossSize ?? 0;
        }

        foreach (var line in lines)
        {
            var maximum = 0.0;

            foreach (var item in line.Items)
            {
                item.UsedCross = NaturalCross(item, mainIsHorizontal, context);
                maximum = Math.Max(maximum, item.OuterCross);
            }

            line.CrossSize = maximum;
            ResolveBaselines(line, alignItems, mainIsHorizontal, context);
        }

        var gaps = lines.Count > 1 ? crossGap * (lines.Count - 1) : 0;
        var natural = 0.0;

        foreach (var line in lines)
        {
            natural += line.CrossSize;
        }

        if (crossSize is { } definite)
        {
            var inner = Math.Max(0, definite - gaps);

            if (lines.Count == 1)
            {
                lines[0].CrossSize = inner;
            }
            else if (alignContent == FlexAlignment.Stretch && inner > natural)
            {
                var add = (inner - natural) / lines.Count;

                foreach (var line in lines)
                {
                    line.CrossSize += add;
                }
            }

            // The room the lines share is the whole cross extent: the distribution takes the gaps out
            // itself, so handing it the extent already less the gaps took them out twice and pulled
            // every centred or spaced line layout short by half a gap (measured on flex-gap-
            // decorations-006, where the columns stood 15px left of the reference).
            DistributeLines(lines, definite, crossGap, alignContent);
            StretchItems(lines, alignItems);
            return definite;
        }

        DistributeLines(lines, natural, crossGap, FlexAlignment.Start);
        StretchItems(lines, alignItems);
        return natural + gaps;
    }

    /// <summary>
    /// Finds the baseline a line's baseline-aligned items share: the lowest first baseline among
    /// them, measured from the line's cross start. A line with none of them keeps the zero that
    /// leaves the alignment a start edge, which is what a column's cross axis asks for anyway.
    /// </summary>
    private void ResolveBaselines(FlexLine line, FlexAlignment alignItems, Boolean mainIsHorizontal, LayoutContext context)
    {
        if (!mainIsHorizontal)
        {
            return;
        }

        var shares = false;
        var baseline = 0.0;

        foreach (var item in line.Items)
        {
            var align = item.AlignSelf == FlexAlignment.Auto ? alignItems : item.AlignSelf;

            if (align != FlexAlignment.Baseline)
            {
                continue;
            }

            shares = true;
            item.FirstBaseline = ItemBaseline(item, context);
            baseline = Math.Max(baseline, item.FirstBaseline);
        }

        line.Baseline = shares ? baseline : 0;
    }

    /// <summary>Measures the item's first baseline: a probe arrangement, read for its first text.</summary>
    private Double ItemBaseline(FlexItem item, LayoutContext context)
    {
        var probe = _engine.Arrange(
            item.Box,
            new Rect(0, 0, item.BorderBoxMain, item.BorderBoxCross),
            context);
        var found = FindBaseline(probe);

        return found >= 0 ? found : 0;
    }

    private static Double FindBaseline(LayoutBox layout)
    {
        if (layout.Baseline > 0)
        {
            return layout.BorderBox.Y + layout.Baseline;
        }

        foreach (var child in layout.Children)
        {
            var found = FindBaseline(child);

            if (found >= 0)
            {
                return found;
            }
        }

        return -1;
    }

    /// <summary>Measures the item's content-box cross size, before any stretch is applied.</summary>
    private Double NaturalCross(FlexItem item, Boolean mainIsHorizontal, LayoutContext context)
    {
        var style = item.Box.Style;
        var isBorderBox = BoxModel.IsBorderBox(style);
        Double cross;

        if (mainIsHorizontal)
        {
            if (IsDefinite(style.Height, context.ContainingBlock.Height))
            {
                var resolved = style.Height.Resolve(context.ContainingBlock.Height);
                cross = isBorderBox ? resolved - item.InsetCross : resolved;
            }
            else if (style.AspectRatio is { Value: > 0 } &&
                BoxModel.HeightFromRatio(style, item.UsedMain, null, item.InsetCross) is { } fromRatio)
            {
                // The main size is settled and the cross was left to the ratio: the height is the
                // width over the ratio, which is what makes an item with `aspect-ratio: 1/1` as tall
                // as it is wide (CSS Sizing 4 §5.1).
                cross = fromRatio;
            }
            else
            {
                // The used main size is a content size, so the available width that produces it is
                // that size plus the item's own margins and insets.
                var available = item.UsedMain + item.InsetMain + item.MarginMainStart + item.MarginMainEnd;

                // A column of vertical text takes its inline extent from the room it is given rather
                // than from its content when it is measured as a block, which is right for a box filling
                // its container but not for a flex item whose cross size is being asked for. The item's
                // own content is the answer here, so the height it is handed is none at all
                // (gap-003-rl).
                var constraints = item.Box.Flow.IsVertical
                    ? MeasureConstraints.FitWidth(available).WithHeight(0)
                    : MeasureConstraints.FitWidth(available);

                cross = _engine.Measure(item.Box, constraints, context).Height - item.InsetCross;
            }
        }
        else if (IsDefinite(style.Width, context.ContainingBlock.Width))
        {
            var resolved = style.Width.Resolve(context.ContainingBlock.Width);
            cross = isBorderBox ? resolved - item.InsetCross : resolved;
        }
        else if (style.AspectRatio is { Value: > 0 } &&
            BoxModel.WidthFromRatio(
                style,
                isBorderBox ? item.UsedMain + item.InsetMain : item.UsedMain,
                item.InsetCross) is { } fromRatio)
        {
            // A column's cross size follows its main size through the ratio in the same way.
            cross = fromRatio;
        }
        else if (item.Box.Flow.IsVertical)
        {
            // The cross axis runs across the page while the item's own lines run down it: the size
            // asked for is its block size, which a vertical box takes from its content rather than
            // from the containing block (CSS 2.1 §10.6.3, CSS Writing Modes 3 §6). Its inline size is
            // the main size the line just gave it, so that is the height handed over.
            var available = item.UsedMain + item.InsetMain + item.MarginMainStart + item.MarginMainEnd;
            var constraints = MeasureConstraints
                .FitWidth(context.ContainingBlock.Width)
                .WithHeight(available);

            cross = _engine.Measure(item.Box, constraints, context).Width - item.InsetCross;
        }
        else
        {
            cross = _engine.MeasureIntrinsic(item.Box, context).MaxContent - item.InsetCross;
        }

        return item.ClampCross(Math.Max(0, cross));
    }

    /// <summary>Positions the lines along the cross axis according to <c>align-content</c>.</summary>
    private static void DistributeLines(List<FlexLine> lines, Double inner, Double crossGap, FlexAlignment alignContent)
    {
        var used = 0.0;

        foreach (var line in lines)
        {
            used += line.CrossSize;
        }

        var gaps = lines.Count > 1 ? crossGap * (lines.Count - 1) : 0;
        var free = Math.Max(0, inner - used - gaps);
        Double offset = 0;
        Double extra = 0;

        switch (alignContent)
        {
            case FlexAlignment.End or FlexAlignment.FlexEnd or FlexAlignment.Right:
                offset = free;
                break;

            case FlexAlignment.Center:
                offset = free / 2;
                break;

            case FlexAlignment.SpaceBetween when lines.Count > 1:
                extra = free / (lines.Count - 1);
                break;

            case FlexAlignment.SpaceAround:
                extra = free / lines.Count;
                offset = extra / 2;
                break;

            case FlexAlignment.SpaceEvenly:
                extra = free / (lines.Count + 1);
                offset = extra;
                break;
        }

        var cursor = offset;

        foreach (var line in lines)
        {
            line.CrossStart = cursor;
            cursor += line.CrossSize + crossGap + extra;
        }
    }

    /// <summary>
    /// Gives every item that asks to be stretched - and whose cross size and cross margins are
    /// <c>auto</c> - the cross size of its line.
    /// </summary>
    private static void StretchItems(List<FlexLine> lines, FlexAlignment alignItems)
    {
        foreach (var line in lines)
        {
            foreach (var item in line.Items)
            {
                var align = item.AlignSelf == FlexAlignment.Auto ? alignItems : item.AlignSelf;

                if (align is FlexAlignment.Stretch or FlexAlignment.Normal && item.AutoCross &&
                    !item.AutoMarginCrossStart && !item.AutoMarginCrossEnd)
                {
                    item.UsedCross = item.ClampCross(Math.Max(
                        0,
                        line.CrossSize - item.InsetCross - item.MarginCrossStart - item.MarginCrossEnd));
                }
            }
        }
    }

    /// <summary>Gets whether a length is usable as a definite size against the given basis.</summary>
    private static Boolean IsDefinite(StyleLength length, Double basis) =>
        length.HasValue && !(length.Kind == StyleLengthKind.Percent && basis <= 0);
}

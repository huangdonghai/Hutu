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
        var frozen = new Boolean[count];
        var factors = new Double[count];
        var gaps = count > 1 ? mainGap * (count - 1) : 0;

        var totalOuter = 0.0;

        for (var i = 0; i < count; i++)
        {
            sizes[i] = items[i].HypotheticalMain;
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
            for (var iteration = 0; iteration < MaxFlexIterations; iteration++)
            {
                var frozenOuter = 0.0;
                var remainingFactors = 0.0;

                for (var i = 0; i < count; i++)
                {
                    if (frozen[i])
                    {
                        frozenOuter += Outer(items[i], sizes[i]);
                    }
                    else
                    {
                        remainingFactors += factors[i];
                    }
                }

                if (remainingFactors <= Epsilon)
                {
                    break;
                }

                var remaining = inner - frozenOuter;
                var froze = false;

                for (var i = 0; i < count; i++)
                {
                    if (frozen[i])
                    {
                        continue;
                    }

                    var share = remaining * factors[i] / remainingFactors;
                    var target = Math.Max(0, share - items[i].MarginMainStart - items[i].MarginMainEnd - items[i].InsetMain);
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
        Boolean isRow,
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
                item.UsedCross = NaturalCross(item, isRow, context);
                maximum = Math.Max(maximum, item.OuterCross);
            }

            line.CrossSize = maximum;
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

            DistributeLines(lines, inner, crossGap, alignContent);
            StretchItems(lines, alignItems);
            return definite;
        }

        DistributeLines(lines, natural, crossGap, FlexAlignment.Start);
        StretchItems(lines, alignItems);
        return natural + gaps;
    }

    /// <summary>Measures the item's content-box cross size, before any stretch is applied.</summary>
    private Double NaturalCross(FlexItem item, Boolean isRow, LayoutContext context)
    {
        var style = item.Box.Style;
        var isBorderBox = BoxModel.IsBorderBox(style);
        Double cross;

        if (isRow)
        {
            if (IsDefinite(style.Height, context.ContainingBlock.Height))
            {
                var resolved = style.Height.Resolve(context.ContainingBlock.Height);
                cross = isBorderBox ? resolved - item.InsetCross : resolved;
            }
            else
            {
                // The used main size is a content size, so the available width that produces it is
                // that size plus the item's own margins and insets.
                var available = item.UsedMain + item.InsetMain + item.MarginMainStart + item.MarginMainEnd;
                cross = _engine.Measure(item.Box, MeasureConstraints.FitWidth(available), context).Height - item.InsetCross;
            }
        }
        else if (IsDefinite(style.Width, context.ContainingBlock.Width))
        {
            var resolved = style.Width.Resolve(context.ContainingBlock.Width);
            cross = isBorderBox ? resolved - item.InsetCross : resolved;
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
            case FlexAlignment.End:
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

                if (align == FlexAlignment.Stretch && item.AutoCross &&
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

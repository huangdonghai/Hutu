using Hutu.Rendering.Boxes;
using System;
using System.Collections.Generic;

namespace Hutu.Rendering.Layout;

/// <summary>
/// The grid formatting context: it turns a container's children into items, places each one on the
/// grid, sizes the tracks and then aligns both the tracks inside the container and the items inside
/// their areas.
/// </summary>
/// <remarks>
/// The shape of the algorithm is the specification's - resolve the explicit grid, place the items,
/// size the tracks in the order "definite, then content, then fractions", then align - but three
/// parts are deliberately simplified, because no sample page exercises them and the full versions are
/// iterative:
/// <list type="bullet">
/// <item>The demands of an item that spans several tracks are shared out evenly over those tracks
/// rather than through the specification's distribute-extra-space loop.</item>
/// <item>An item's percentage lengths resolve against the container's content box rather than against
/// its own grid area, which is what every other context in this engine does.</item>
/// <item>Named lines and named areas are read but not resolved, so they place like <c>auto</c>.</item>
/// <item>The <c>start</c> and <c>end</c> alignment keywords are unusable: AngleSharp.Css leaves both
/// out of its alignment keyword table, so a declaration using either is discarded before the read
/// layer can see it and the value cannot be recovered. <c>flex-start</c>, <c>flex-end</c>,
/// <c>center</c>, <c>stretch</c>, <c>baseline</c> and the distribution keywords all work.</item>
/// </list>
/// Everything else - a fixed <c>repeat()</c>, <c>auto-fill</c>/<c>auto-fit</c>, <c>minmax()</c>,
/// <c>fr</c>, implicit tracks, spans, <c>gap</c> and both levels of alignment - is implemented.
/// </remarks>
internal sealed class GridFormattingContext
{
    private const Double Epsilon = 0.0001;

    /// <summary>
    /// A cap on the sizing loops. The fraction pass freezes at least one track per turn and the
    /// growth pass stops as soon as nothing grows, so both converge well before this; the cap only
    /// guarantees that a pathological set of factors cannot hang a layout pass.
    /// </summary>
    private const Int32 MaxIterations = 64;

    private readonly LayoutEngine _engine;

    public GridFormattingContext(LayoutEngine engine)
    {
        _engine = engine;
    }

    public Size Measure(Box box, MeasureConstraints constraints, LayoutContext context)
    {
        var style = box.Style;
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

        var plan = Compute(box, contentWidth, declaredHeight, context, definiteWidth: true, sizeRows: true);
        var contentHeight = declaredHeight ?? plan.ContentHeight;

        // Size containment answers both axes from the intrinsic size the page stated, whatever the
        // tracks and items come to (CSS Contain 1 §3.1.1, CSS Sizing 4 §5).
        var contain = style.Contain;

        // The intrinsic size only stands where the page stated no size of its own for the axis.
        var declaredInline = box.Flow.IsVertical ? style.Height : style.Width;
        var declaredBlock = box.Flow.IsVertical ? style.Width : style.Height;

        if (contain.Inline && !ContainStyle.IsDefinite(declaredInline, basisWidth) &&
            (box.Flow.IsVertical || ContainStyle.InlineSizeIsContentBased(box)))
        {
            var stated = contain.InlineSize ?? (box.Flow.IsVertical ? contain.Height : contain.Width);
            contentWidth = stated is { } inline ? Math.Max(0, inline.Resolve(0)) : 0;
        }

        if (contain.Block && !ContainStyle.IsDefinite(declaredBlock, basisHeight))
        {
            var stated = contain.BlockSize ?? (box.Flow.IsVertical ? contain.Width : contain.Height);
            contentHeight = stated is { } block ? Math.Max(0, block.Resolve(0)) : 0;
        }

        return new Size(
            BoxModel.ClampWidth(style, contentWidth + insetH, basisWidth, insetH),
            BoxModel.ClampHeight(style, contentHeight, basisHeight, insetV, plan.IntrinsicHeight) + insetV);
    }

    public LayoutBox Arrange(Box box, Rect borderBox, LayoutContext context)
    {
        var style = box.Style;
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

        var plan = Compute(
            box, contentWidth, contentHeight, context, definiteWidth: true, sizeRows: true, forOutOfFlow: true);
        var itemContext = context.ForContainingBlock(new Size(contentWidth, contentHeight));

        // A container that holds its descendants is what an absolutely positioned child anchors to.
        var anchored = PositionedLayout.LendsAFrame(box);

        itemContext = PositionedLayout.ForDescendants(itemContext, box, layout.PaddingBox, layout.BorderBox);

        foreach (var item in plan.Items)
        {
            var rect = new Rect(
                contentX + item.OffsetX,
                contentY + item.OffsetY,
                item.BorderBoxWidth,
                item.BorderBoxHeight);

            // A subgrid item stands on the tracks of the area it spans, which only this pass knows:
            // it is handed the sizes and gutters before it lays its own children out (CSS Grid 2 ��8).
            var itemContextHere = itemContext;
            var itemGrid = item.Box.Style.Grid;

            if ((itemGrid.ColumnsSubgrid || itemGrid.RowsSubgrid) && plan.ColumnBases.Count > 0)
            {
                itemContextHere = itemContext.WithSubgrid(new SubgridTracks(
                    Slice(plan.ColumnBases, item.Column, item.ColumnSpan),
                    Slice(plan.RowBases, item.Row, item.RowSpan),
                    plan.ColumnStep,
                    plan.RowStep,
                    plan.ColumnNames?.Slice(item.Column, item.ColumnSpan),
                    plan.RowNames?.Slice(item.Row, item.RowSpan)));
            }

            layout.Children.Add(_engine.Arrange(item.Box, rect, itemContextHere, item.HeightIsDefinite));
        }

        layout.GapDecorations = GapDecorations(box, plan, contentX, contentY);
        ArrangeOutOfFlow(box, layout, plan, layout.PaddingBox, anchored, contentX, contentY, itemContext);
        return layout;
    }

    /// <summary>
    /// Places the absolutely and fixed positioned children, which are not grid items: they take no room
    /// in the grid and contribute nothing to its tracks. An <c>absolute</c> child's place in it is still
    /// the grid's own - <c>grid-row</c> and <c>grid-column</c> name the area that is its containing block
    /// (CSS Grid 2 ��10.1) and the area it stands in for its static position. A <c>fixed</c> child is
    /// offset from the viewport, so the grid lends it nothing but a place to stand: the content edges,
    /// which is where a box with no room of its own in the grid would sit.
    /// </summary>
    /// <param name="anchored">
    /// Whether this container is what the child's offsets count from. A container that is neither
    /// positioned nor transformed generates no containing block, and then the child anchors to an
    /// ancestor further up and only its static position is the grid's business.
    /// </param>
    private void ArrangeOutOfFlow(
        Box box,
        LayoutBox parent,
        GridPlan plan,
        Rect paddingBox,
        Boolean anchored,
        Double contentX,
        Double contentY,
        LayoutContext context)
    {
        var grid = box.Style.Grid;

        foreach (var child in box.Children)
        {
            if (!child.IsBlockLevel || !PositionedLayout.IsOutOfFlow(child))
            {
                continue;
            }

            if (!child.Style.Position.IsAbsolute)
            {
                parent.Children.Add(
                    _engine.ArrangePositioned(child, new Offset(contentX, contentY), context));
                continue;
            }

            var (block, standing) = OutFlowArea(
                child.Style.GridItem,
                grid,
                plan,
                paddingBox,
                contentX,
                contentY,
                vertical: box.Flow.IsVertical,
                inlineFromEnd: box.Flow.InlineSign < 0,
                blockFromEnd: box.Flow.BlockSign < 0);

            // A container that generates no containing block for the child lends it no area either: the
            // child's place is then the content edges it would have stood against as the sole grid item
            // in the grid (CSS Grid 2 ��10.2), and the offsets it is given count from wherever an ancestor
            // further up puts them.
            if (!anchored)
            {
                standing = new Rect(contentX, contentY, plan.ContentWidth, plan.ContentHeight);
            }

            var childContext = anchored ? context.WithAbsoluteContainingBlock(block) : context;

            // A box with no offsets of its own is put by its own alignment within the area it stands in,
            // which is the one place a grid child's justify-self and align-self reach without the child
            // being a grid item at all (CSS Align 3 ��5.2).
            var item = child.Style.GridItem;

            // A last baseline stands at the end of its axis whatever the text direction is. On the
            // inline axis the end is then turned over with the start below, where a right-to-left row
            // reads the other way.
            // A last baseline stands at the end of the axis it aligns on whatever the direction is; the
            // turn below moves it to the start where the axis itself grows the other way.
            var justify = StaticStanding.Resolve(item.JustifySelf, grid.JustifyItems, baselineAtEnd: true);
            var align = StaticStanding.Resolve(item.AlignSelf, grid.AlignItems, baselineAtEnd: true);

            // `justify-self` answers along the inline axis and `align-self` along the block one; the
            // writing mode decides which of the page's axes each is. A start turns over with its axis,
            // while left and right stay physical - and on a vertical axis, where left and right name
            // nothing, they fall back to the start (CSS Align 3, 5.2).
            FlexAlignment x, y;

            if (!box.Flow.IsVertical)
            {
                x = Turned(justify, box.Flow.InlineSign < 0);
                y = align is FlexAlignment.Left or FlexAlignment.Right ? FlexAlignment.Start : align;
            }
            else
            {
                x = Turned(align is FlexAlignment.Left or FlexAlignment.Right ? FlexAlignment.Start : align,
                    box.Flow.BlockSign < 0);
                // Down a vertical page the inline axis has no left or right to stand on: `left` reads as
                // the start of that axis and `right` as its end.
                y = justify switch
                {
                    FlexAlignment.Left => FlexAlignment.Start,
                    FlexAlignment.Right => FlexAlignment.End,
                    _ => Turned(justify, box.Flow.InlineSign < 0),
                };
            }

            var placed = new StaticStanding(standing, x, y);

            parent.Children.Add(
                _engine.ArrangePositioned(child, new Offset(standing.X, standing.Y), childContext, placed));
        }
    }

    /// <summary>
    /// Gets the two areas an out-of-flow child's placement names: the containing block its offsets count
    /// from and the area its static position stands in. A side named by a line number, a line name or an
    /// area line is the track edge it lands on; a side left auto - or one naming a line the grid has no
    /// room for, which is what the specification says to treat as auto instead of building implicit
    /// tracks for an out-of-flow box (��10.1) - is the container's padding edge. Where nothing in either
    /// axis is placed, the area the child stands in is the content box instead, as if it were the sole
    /// grid item in it (��10.2).
    /// </summary>
    private static (Rect Block, Rect Standing) OutFlowArea(
        GridItemStyle item,
        GridStyle grid,
        GridPlan plan,
        Rect paddingBox,
        Double contentX,
        Double contentY,
        Boolean vertical,
        Boolean inlineFromEnd,
        Boolean blockFromEnd)
    {
        var contentRight = contentX + plan.ContentWidth;
        var contentBottom = contentY + plan.ContentHeight;

        var columnStart = LineNumber(item.ColumnStart, grid, plan.ColumnLines.Count, horizontal: true, atEnd: false);
        var columnEnd = LineNumber(item.ColumnEnd, grid, plan.ColumnLines.Count, horizontal: true, atEnd: true);
        var rowStart = LineNumber(item.RowStart, grid, plan.RowLines.Count, horizontal: false, atEnd: false);
        var rowEnd = LineNumber(item.RowEnd, grid, plan.RowLines.Count, horizontal: false, atEnd: true);

        // A placement that names the same line for both edges spans one track rather than none: an
        // `auto` end from the same start is a span of one (CSS Grid 1 §8.3), and an out-of-flow item
        // gets the same area its in-flow sibling would (positioned-grid-items-023).
        if (columnStart is { } columnAt && columnEnd == columnAt && columnAt < plan.ColumnLines.Count)
        {
            columnEnd = columnAt + 1;
        }

        if (rowStart is { } rowAt && rowEnd == rowAt && rowAt < plan.RowLines.Count)
        {
            rowEnd = rowAt + 1;
        }

        // A column is the inline axis and a row the block one, so a vertical page turns the two
        // around: the columns of the template run down the page and its rows across it.
        Double blockLeft, blockRight, blockTop, blockBottom;

        if (!vertical)
        {
            blockLeft = columnStart is { } left
                ? LineAt(plan.ColumnLines, left, contentX, plan.ContentWidth, inlineFromEnd)
                : paddingBox.X;
            blockRight = columnEnd is { } right
                ? LineAt(plan.ColumnLines, right, contentX, plan.ContentWidth, inlineFromEnd)
                : paddingBox.Right;
            blockTop = rowStart is { } top ? LineAt(plan.RowLines, top, contentY) : paddingBox.Y;
            blockBottom = rowEnd is { } bottom ? LineAt(plan.RowLines, bottom, contentY) : paddingBox.Bottom;
        }
        else
        {
            blockLeft = rowStart is { } left
                ? LineAt(plan.RowLines, left, contentX, plan.ContentWidth, blockFromEnd)
                : paddingBox.X;
            blockRight = rowEnd is { } right
                ? LineAt(plan.RowLines, right, contentX, plan.ContentWidth, blockFromEnd)
                : paddingBox.Right;
            blockTop = columnStart is { } top
                ? LineAt(plan.ColumnLines, top, contentY, plan.ContentHeight, inlineFromEnd)
                : paddingBox.Y;
            blockBottom = columnEnd is { } bottom
                ? LineAt(plan.ColumnLines, bottom, contentY, plan.ContentHeight, inlineFromEnd)
                : paddingBox.Bottom;
        }

        // A line read from the right edge puts the area's start line to the right of its end line,
        // so the two edges are put in page order before the rect is built.
        var block = Between(
            Math.Min(blockLeft, blockRight),
            Math.Max(blockLeft, blockRight),
            Math.Min(blockTop, blockBottom),
            Math.Max(blockTop, blockBottom));


        // A placement named on no side at all has no area of its own to stand in, and the content edges
        // answer for it; the moment one side is named, the child stands where that area begins.
        var standing = columnStart is not null || columnEnd is not null || rowStart is not null || rowEnd is not null
            ? block
            : Between(contentX, contentRight, contentY, contentBottom);

        return (block, standing);
    }

    /// <summary>Gets the alignment a start or end keyword comes to where the axis grows the other way.</summary>
    private static FlexAlignment Turned(FlexAlignment align, Boolean fromEnd) =>
        fromEnd
            ? align switch
            {
                FlexAlignment.Start or FlexAlignment.FlexStart => FlexAlignment.End,
                FlexAlignment.End or FlexAlignment.FlexEnd => FlexAlignment.Start,
                _ => align,
            }
            : align;

    /// <summary>Gets a rect between two edges, holding it at nothing rather than letting it cross over.</summary>
    private static Rect Between(Double left, Double right, Double top, Double bottom) =>
        new(left, top, Math.Max(0, right - left), Math.Max(0, bottom - top));

    /// <summary>
    /// Gets the number of the line a side names, or null where it names none: an auto side, or a line the
    /// grid has no room for - which the specification says to treat as auto instead of building implicit
    /// tracks for an out-of-flow box (CSS Grid 2 §10.1).
    /// </summary>
    private static Int32? LineNumber(
        GridLine side,
        GridStyle grid,
        Int32 count,
        Boolean horizontal,
        Boolean atEnd)
    {
        return grid.Resolve(side, horizontal, atEnd).ResolveWithin(count);
    }

    /// <summary>
    /// Gets where a line stands along the page. A line numbered from the right names the same edge of a
    /// different track: line 1 is the list's last edge, and the last line its first (CSS Grid 1 ��7.1).
    /// </summary>
    private static Double LineAt(List<Double> lines, Int32 line, Double origin) =>
        origin + lines[line - 1];

    /// <summary>
    /// Gets where a line stands along an axis whose start edge is the far one: the line's offset is
    /// counted from the right instead of the left, so the first line of the list stands at the extent.
    /// </summary>
    private static Double LineAt(List<Double> lines, Int32 line, Double origin, Double extent, Boolean fromEnd) =>
        fromEnd ? origin + extent - lines[line - 1] : origin + lines[line - 1];



    /// <summary>
    /// Gets the stretch of track on either side of each gap: the areas a gap's decoration asks about
    /// before it is drawn at all (CSS Gaps 1 ��3.4).
    /// </summary>
    private static (List<(Double From, Double To)> Before, List<(Double From, Double To)> After) Sides(
        List<TrackGap> gaps,
        Double origin,
        Double end)
    {
        var before = new List<(Double From, Double To)>(gaps.Count);
        var after = new List<(Double From, Double To)>(gaps.Count);

        for (var index = 0; index < gaps.Count; index++)
        {
            before.Add((origin + (index == 0 ? 0 : gaps[index - 1].To), origin + gaps[index].From));
            after.Add((origin + gaps[index].To, origin + (index + 1 < gaps.Count ? gaps[index + 1].From : end)));
        }

        return (before, after);
    }

    /// <summary>
    /// Works out the decorations of the grid's gaps: the room between each pair of adjacent tracks, run
    /// the whole way across the tracks and split where an item stands in the way or, for the
    /// <c>intersection</c> break, where a perpendicular gap crosses it (CSS Gaps 1 ��3.1).
    /// </summary>
    private static List<GapRuleSegment>? GapDecorations(Box box, GridPlan plan, Double contentX, Double contentY)
    {
        var rules = box.Style.Gap;

        if (!rules.IsStated)
        {
            return null;
        }

        var items = new List<Rect>(plan.Items.Count);

        foreach (var item in plan.Items)
        {
            items.Add(new Rect(
                contentX + item.OffsetX,
                contentY + item.OffsetY,
                item.BorderBoxWidth,
                item.BorderBoxHeight));
        }

        var columns = new List<GridGapRun>(plan.ColumnGaps.Count);
        var rows = new List<GridGapRun>(plan.RowGaps.Count);
        var columnCrossings = new List<GridGapRun>(plan.ColumnGaps.Count);
        var rowCrossings = new List<GridGapRun>(plan.RowGaps.Count);

        foreach (var gap in plan.ColumnGaps)
        {
            var from = contentY;
            var to = contentY + plan.TracksHeight;

            columns.Add(new GridGapRun(true, contentX + (gap.From + gap.To) / 2, from, to, gap.To - gap.From));
            columnCrossings.Add(new GridGapRun(true, 0, contentX + gap.From, contentX + gap.To, 0));
        }

        foreach (var gap in plan.RowGaps)
        {
            var from = contentX;
            var to = contentX + plan.TracksWidth;

            rows.Add(new GridGapRun(false, contentY + (gap.From + gap.To) / 2, from, to, gap.To - gap.From));
            rowCrossings.Add(new GridGapRun(false, 0, contentY + gap.From, contentY + gap.To, 0));
        }

        var target = new List<GapRuleSegment>();

        var (columnBefore, columnAfter) = Sides(plan.ColumnGaps, contentX, plan.TracksWidth);
        var (rowBefore, rowAfter) = Sides(plan.RowGaps, contentY, plan.TracksHeight);

        // A column rule runs along the block axis and a row rule along the inline, so each takes its ends
        // from whichever of the two the container's writing mode turns back (CSS Gaps 1 ��3.3).
        var columnRules = rules.Column.MeasuredFromTheEnd(box.Flow.BlockSign < 0);
        var rowRules = rules.Row.MeasuredFromTheEnd(box.Flow.InlineSign < 0);

        // The stretches of both axes are worked out before either is inset, because an end standing at a
        // crossing joins with the other axis only where that axis is drawn beside it (CSS Gaps 1 §3.3).
        var columnStretches =
            GridGapBands.Stretches(columnRules, columns, rowCrossings, columnBefore, columnAfter, items);
        var rowStretches =
            GridGapBands.Stretches(rowRules, rows, columnCrossings, rowBefore, rowAfter, items);

        GridGapBands.Add(
            target, columnRules, rowRules, isRow: false, columns, rowCrossings, columnStretches, rowStretches, items);
        GridGapBands.Add(
            target, rowRules, columnRules, isRow: true, rows, columnCrossings, rowStretches, columnStretches, items);

        return target.Count > 0 ? target : null;
    }

    /// <summary>
    /// The intrinsic inline sizes. The max-content size is what the tracks come to when each is as big
    /// as what it holds; the min-content size is the sum of the tracks' base sizes before any of them
    /// is grown - each one its min sizing function resolved, so a <c>minmax(0, 1fr)</c> track, whose
    /// minimum is zero, contributes nothing. Reporting the max-content as the min-content (as this
    /// once did) makes a grid refuse to shrink below what it holds, which is what let a wide grid inside
    /// a <c>flex-grow</c> item balloon the whole document past the viewport.
    /// </summary>
    public IntrinsicSize MeasureIntrinsic(Box box, LayoutContext context)
    {
        var padding = box.Style.Padding.Resolve(context.ContainingBlock.Width);
        var border = BlockLayout.BorderOf(box);
        var insets = padding.Horizontal + border.Horizontal;

        var plan = Compute(box, 0, null, context, definiteWidth: false, sizeRows: false);
        var maxContent = plan.ContentWidth + insets;
        var minContent = Math.Min(plan.MinWidth + insets, maxContent);

        return new IntrinsicSize(minContent, maxContent);
    }

    /// <summary>Runs the whole algorithm and returns the content sizes plus the placed items.</summary>
    /// <param name="definiteWidth">Whether the width the tracks are sized against is settled.</param>
    /// <param name="sizeRows">
    /// Whether the caller needs the rows and the item positions, which is everyone but the intrinsic
    /// size. A row is sized from the widths the columns ended up with and a position is a place within
    /// them, so neither can change what the columns come to - and sizing the rows is the expensive
    /// half, because a row's height is whatever its items need at the width they were handed, which
    /// measures every one of them.
    /// <para>
    /// The rows cannot be dropped from the collection and placement above it, though: automatic
    /// placement walks the grid row by row, so which row an item lands in decides which column one
    /// that spans comes to rest at.
    /// </para>
    /// </param>
    private GridPlan Compute(
        Box box,
        Double contentWidth,
        Double? contentHeight,
        LayoutContext context,
        Boolean definiteWidth,
        Boolean sizeRows,
        Boolean forOutOfFlow = false)
    {
        var grid = box.Style.Grid;
        var items = new List<GridCell>();

        foreach (var child in box.Children)
        {
            // An absolutely or fixed positioned child is not a grid item: it is out of flow and is
            // placed against the container instead.
            if (child.IsBlockLevel && !PositionedLayout.IsOutOfFlow(child))
            {
                items.Add(Create(child));
            }
        }

        var columnBasis = definiteWidth ? contentWidth : 0;
        var rowBasis = contentHeight ?? 0;

        // A subgrid borrows the tracks of the area its parent put it in: no sizing, no contributions.
        // Its own gutter then stands on the line it borrows, centred on the room the parent left there,
        // so the tracks it borrows are cut by the difference between the two gutters - and an axis that
        // states no gutter of its own cuts nothing (CSS Grid 2, subgrid gutters).
        var borrowed = context.Subgrid;

        var gapX = grid.ColumnsSubgrid && borrowed is not null
            ? grid.ColumnGapStated
                ? grid.ColumnGapAgainst(columnBasis)
                : borrowed.ColumnGap
            : grid.ColumnGapAgainst(columnBasis);
        var gapY = grid.RowsSubgrid && borrowed is not null
            ? grid.RowGapStated
                ? grid.RowGapAgainst(rowBasis)
                : borrowed.RowGap
            : grid.RowGapAgainst(rowBasis);

        var subgridColumns = grid.ColumnsSubgrid ? Borrowed(borrowed?.Columns) : null;
        var subgridRows = grid.RowsSubgrid ? Borrowed(borrowed?.Rows) : null;

        // The cut is a question about the box the tracks fill, so it waits for the pass that knows the
        // size it fills. The intrinsic one, which is asking what the box would need, hands the parent's
        // sizes on untouched - and a subgrid's own gutter is nothing to the room it asks for.
        if (subgridColumns is not null && borrowed is not null && definiteWidth)
        {
            subgridColumns = Cut(subgridColumns, borrowed.ColumnGap, gapX, contentWidth, Inset(box, context).Left);
        }

        if (subgridRows is not null && borrowed is not null && contentHeight is { } room)
        {
            subgridRows = Cut(subgridRows, borrowed.RowGap, gapY, room, Inset(box, context).Top);
        }

        // The explicit grid is what the automatic placement wraps inside, so both counts are taken
        // before any item is placed.
        var explicitColumns = subgridColumns?.Count ?? grid.Columns.ExplicitCount(columnBasis, gapX);
        var explicitRows = subgridRows?.Count ?? grid.Rows.ExplicitCount(rowBasis, gapY);

        // A line named in a subgrid is one the parent wrote, so the borrowed names stand in for the
        // axis that borrows tracks; where the list is null the grid's own names answer.
        var handed = context.Subgrid;
        var namedColumns = grid.ColumnsSubgrid
            ? GridLineNames.Merged(handed?.ColumnLines, grid.ColumnLines)
            : null;
        var namedRows = grid.RowsSubgrid
            ? GridLineNames.Merged(handed?.RowLines, grid.RowLines)
            : null;

        foreach (var item in items)
        {
            ResolveAxis(item, true, explicitColumns, grid, namedColumns);
            ResolveAxis(item, false, explicitRows, grid, namedRows);
        }

        Place(items, grid, explicitColumns, explicitRows);

        // An `auto-fit` repetition leaves the tracks no item reaches as nothing at all: they hold no
        // gutter, they are not counted when the content is distributed, and the room they would have
        // taken is shared by the tracks that stand - which is why they go before any track is sized, so
        // that a track whose maximum is `auto` grows into the width the dropped ones leave (CSS Grid 1
        // ��7.2.3).
        var keptColumns = grid.ColumnsSubgrid
            ? null
            : Collapse(grid.Columns, items, true, columnBasis, gapX, definiteWidth);
        var keptRows = grid.RowsSubgrid
            ? null
            : Collapse(grid.Rows, items, false, rowBasis, gapY, contentHeight is not null);

        var itemContext = context.ForContainingBlock(new Size(contentWidth, contentHeight ?? 0));

        // How far the items reach, which is what takes an `auto-fit` repetition past its end.
        var usedColumns = Used(items, true);

        // Columns first. A row can only be sized once its items' final widths are known, because the
        // row's own size is whatever height the content needs at that width. A subgrid skips the
        // sizing altogether: its columns are the parent's, already resolved.
        List<Track> columnTracks;
        Double minContentWidth;

        if (subgridColumns is not null)
        {
            columnTracks = subgridColumns;
            minContentWidth = Total(columnTracks, gapX);
        }
        else
        {
            columnTracks = SizeColumns(
                grid,
                items,
                usedColumns,
                columnBasis,
                gapX,
                definiteWidth,
                itemContext,
                out minContentWidth,
                keptColumns);
        }

        var totalWidth = Total(columnTracks, gapX);
        var resolvedWidth = definiteWidth ? contentWidth : totalWidth;

        if (!sizeRows)
        {
            // The inline size is the width the columns come to, and nothing below this point can
            // change it. The items are not carried out either: the caller asking for this is asking
            // for the width.
            return new GridPlan { ContentWidth = resolvedWidth, MinWidth = minContentWidth };
        }

        foreach (var item in items)
        {
            item.AreaWidth = Span(columnTracks, item.Column, item.ColumnSpan, gapX);
        }

        var rowTracks = subgridRows ??
            SizeRows(
                grid,
                items,
                Used(items, false),
                rowBasis,
                gapY,
                contentHeight is not null,
                itemContext,
                columnTracks,
                gapX,
                namedColumns ?? grid.ColumnLines,
                namedRows ?? grid.RowLines,
                keptRows);

        var totalHeight = Total(rowTracks, gapY);
        var resolvedHeight = definiteWidth ? contentHeight ?? totalHeight : totalHeight;

        var (offsetX, extraX) = ContentOffset(grid.JustifyContent, resolvedWidth - totalWidth, columnTracks.Count);
        var (offsetY, extraY) = ContentOffset(grid.AlignContent, resolvedHeight - totalHeight, rowTracks.Count);

        var stepX = gapX + extraX;
        var stepY = gapY + extraY;

        foreach (var item in items)
        {
            Position(
                item,
                grid,
                offsetX + Prefix(columnTracks, item.Column, stepX),
                offsetY + Prefix(rowTracks, item.Row, stepY),
                Span(columnTracks, item.Column, item.ColumnSpan, stepX),
                Span(rowTracks, item.Row, item.RowSpan, stepY),
                context);
        }

        var plan = new GridPlan
        {
            ContentWidth = resolvedWidth,
            ContentHeight = resolvedHeight,
            IntrinsicHeight = totalHeight,
            MinWidth = minContentWidth,
            ColumnStep = stepX,
            RowStep = stepY,
            ColumnNames = namedColumns ?? grid.ColumnLines,
            RowNames = namedRows ?? grid.RowLines,
        };

        // A subgrid item is laid out against the tracks its area covers, so the sizes have to be
        // remembered where the item is arranged: the subgrid itself has no other way to learn them.
        foreach (var track in columnTracks)
        {
            plan.ColumnBases.Add(track.Base);
        }

        foreach (var track in rowTracks)
        {
            plan.RowBases.Add(track.Base);
        }

        // The room between two tracks is the step they stand apart by: the declared gutter plus whatever
        // the content alignment put between them, which is the gap a decoration centres itself in. Only a
        // container whose gaps carry decorations is asked for them: a grid of ten thousand tracks costs a
        // list of ten thousand gaps to describe, and nothing else reads one.
        var decorated = box.Style.Gap.IsStated;

        // An out-of-flow child is placed against the grid's lines, so they have to be remembered where
        // one exists: only the pass that arranges the container is asked, because a grid whose frame is
        // written with a clamped repetition has more lines to record than anything reads them for.
        var placed = forOutOfFlow && HasOutOfFlowChild(box);

        for (var index = 0; decorated && index + 1 < columnTracks.Count; index++)
        {
            plan.ColumnGaps.Add(new TrackGap(
                offsetX + Prefix(columnTracks, index, stepX) + columnTracks[index].Base,
                offsetX + Prefix(columnTracks, index + 1, stepX)));
        }

        for (var index = 0; decorated && index + 1 < rowTracks.Count; index++)
        {
            plan.RowGaps.Add(new TrackGap(
                offsetY + Prefix(rowTracks, index, stepY) + rowTracks[index].Base,
                offsetY + Prefix(rowTracks, index + 1, stepY)));
        }

        if (decorated && columnTracks.Count > 0)
        {
            plan.TracksWidth = offsetX + Prefix(columnTracks, columnTracks.Count - 1, stepX) + columnTracks[^1].Base;
        }

        if (decorated && rowTracks.Count > 0)
        {
            plan.TracksHeight = offsetY + Prefix(rowTracks, rowTracks.Count - 1, stepY) + rowTracks[^1].Base;
        }

        if (placed)
        {
            Lines(plan.ColumnLines, columnTracks, offsetX, stepX);
            Lines(plan.RowLines, rowTracks, offsetY, stepY);
        }

        plan.Items.AddRange(items);
        return plan;
    }

    /// <summary>
    /// Writes where each line of one axis stands: the edge of every track in order and then the edge the
    /// last one ends at, which is the axis's last line.
    /// </summary>
    private static void Lines(List<Double> target, List<Track> tracks, Double offset, Double step)
    {
        for (var index = 0; index < tracks.Count; index++)
        {
            target.Add(offset + Prefix(tracks, index, step));
        }

        if (tracks.Count > 0)
        {
            target.Add(offset + Prefix(tracks, tracks.Count - 1, step) + tracks[^1].Base);
        }
    }

    /// <summary>Gets whether the container has a child that its placement should position an offset from.</summary>
    private static Boolean HasOutOfFlowChild(Box box)
    {
        foreach (var child in box.Children)
        {
            if (child.IsBlockLevel && PositionedLayout.IsOutOfFlow(child))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Gets whether the item's own declared width may be used. An anonymous item has none: it exists
    /// only to hold a run of inline content but borrows the container's style, so asking that style
    /// for a width would hand back the container's own and stretch the text to the whole grid.
    /// </summary>
    private static Boolean HasWidth(Box box) => !box.IsAnonymous && box.Style.Width.HasValue;

    /// <summary>Gets whether the item states a width of its own that the layout can be sized from,
    /// including the content keywords the item's content settles.</summary>
    private static Boolean StatesWidth(Box box) =>
        !box.IsAnonymous && (box.Style.Width.HasValue || box.Style.Width.IsContentSized);

    /// <summary>Gets whether the item states a height of its own, on the same terms.</summary>
    private static Boolean StatesHeight(Box box) =>
        !box.IsAnonymous && (box.Style.Height.HasValue || box.Style.Height.IsContentSized);

    /// <summary>Gets whether the item's own declared height may be used, for the same reason.</summary>
    private static Boolean HasHeight(Box box) => !box.IsAnonymous && box.Style.Height.HasValue;

    /// <summary>
    /// Measures the height an item's content needs at a given width. An ordinary item answers through
    /// the usual measurement, but an anonymous wrapper borrows the container's style - so its declared
    /// height is the container's own rather than anything about the text - and has to be arranged at
    /// zero height and read back from where its content ended instead.
    /// </summary>
    /// <summary>Gets the inline size a content-based keyword asks for, capped by the room there is.</summary>
    private Double ContentWidth(StyleLength width, Box box, Double space, LayoutContext context)
    {
        var intrinsic = _engine.MeasureContentIntrinsic(box, context);
        var wanted = width.Kind == StyleLengthKind.MinContent ? intrinsic.MinContent : intrinsic.MaxContent;

        return Math.Min(Math.Max(0, wanted), space);
    }

    private Double ContentHeight(Box box, Double width, Double basis, LayoutContext context)
    {
        var available = Math.Max(0, width);

        // The item's own percentages resolve against its grid area, so the measurement has to be told
        // about that area rather than the grid's content box the shared context was built for. Its
        // height is what this measurement is finding out, so it is told with none: a `height: 100%` on
        // the item is then no height at all while the rows are being sized (CSS Grid 1 ��12.4), where
        // before it resolved against the whole grid and asked its row to be that tall.
        context = context.ForContainingBlock(new Size(Math.Max(0, basis), 0));

        if (!box.IsAnonymous)
        {
            return _engine.Measure(box, MeasureConstraints.FitWidth(available), context).Height;
        }

        var probe = _engine.Arrange(box, new Rect(0, 0, available, 0), context);
        var top = probe.BorderBox.Y + probe.Border.Top + probe.Padding.Top;
        var bottom = top;

        foreach (var child in probe.Children)
        {
            bottom = Math.Max(bottom, child.BorderBox.Bottom);
        }

        return Math.Max(0, bottom - top);
    }

    private static GridCell Create(Box box)
    {
        var item = box.Style.GridItem;

        return new GridCell
        {
            Box = box,
            ColumnStart = item.ColumnStart,
            ColumnEnd = item.ColumnEnd,
            RowStart = item.RowStart,
            RowEnd = item.RowEnd,
            JustifySelf = item.JustifySelf,
            AlignSelf = item.AlignSelf,
        };
    }

    /// <summary>
    /// Resolves one axis of an item's placement into a track index and a span. A definite side wins;
    /// otherwise a <c>span</c> fixes only the reach and the index is left to the placement pass.
    /// </summary>
    /// <remarks>
    /// A side written as a name - the area an item asked for, or a line the tracks were named - is
    /// settled by the grid first, because nothing else knows which line that name is: the same word
    /// means one line in one template and another in the next.
    /// </remarks>
    private static void ResolveAxis(
        GridCell item,
        Boolean horizontal,
        Int32 explicitTracks,
        GridStyle grid,
        GridLineNames? borrowed = null)
    {
        var start = grid.Resolve(horizontal ? item.ColumnStart : item.RowStart, horizontal, end: false, borrowed);
        var end = grid.Resolve(horizontal ? item.ColumnEnd : item.RowEnd, horizontal, end: true, borrowed);
        var lines = explicitTracks + 1;
        var startIndex = start.Resolve(lines);
        var endIndex = end.Resolve(lines);
        var span = 1;

        // `span <name>` reaches the nearest line of that name in the direction of the other side:
        // the previous one for a start, the next one for an end (CSS Grid 1 §8.3).
        if (start.Kind == GridLineKind.SpanName && endIndex is { } namedEnd)
        {
            startIndex = borrowed?.NearestBefore(start.Name!, namedEnd) ?? Math.Max(1, namedEnd - 1);
        }
        else if (end.Kind == GridLineKind.SpanName && startIndex is { } namedStart)
        {
            endIndex = borrowed?.NearestAfter(end.Name!, namedStart) ?? Math.Min(lines, namedStart + 1);
        }

        if (start.Kind == GridLineKind.Span)
        {
            span = Math.Max(1, start.Span);
        }
        else if (end.Kind == GridLineKind.Span)
        {
            span = Math.Max(1, end.Span);
        }

        Int32 index;
        Boolean automatic;

        if (startIndex is { } begin)
        {
            if (endIndex is { } finish && finish > begin)
            {
                span = finish - begin;
            }

            index = begin - 1;
            automatic = false;
        }
        else if (endIndex is { } finish2)
        {
            index = Math.Max(0, finish2 - 1 - span);
            automatic = false;
        }
        else
        {
            index = 0;
            automatic = true;
        }

        // A user agent keeps an enormous implicit grid from being built by clamping how far a placement
        // may reach; a span like `span 134217729` would otherwise ask for that many tracks, and the
        // placement would spend itself marking every cell. No real grid comes near this ceiling, so the
        // clamp only ever bites on the pathological case it exists to survive.
        const Int32 maxTracks = 65536;

        if (index < 0)
        {
            index = 0;
        }
        else if (index >= maxTracks)
        {
            index = maxTracks - 1;
        }

        if (span < 1)
        {
            span = 1;
        }

        if (index + span > maxTracks)
        {
            span = maxTracks - index;
        }

        if (horizontal)
        {
            item.Column = index;
            item.ColumnSpan = span;
            item.AutoColumn = automatic;
        }
        else
        {
            item.Row = index;
            item.RowSpan = span;
            item.AutoRow = automatic;
        }
    }

    /// <summary>
    /// The automatic placement algorithm, row-major and sparse. An item with both sides definite is
    /// placed where it says; every other item is packed into the first free run of cells at or after
    /// the cursor, and the cursor itself only ever moves forward - which is what "sparse" means.
    /// </summary>
    private static (Int32 Columns, Int32 Rows) Place(
        List<GridCell> items,
        GridStyle grid,
        Int32 explicitColumns,
        Int32 explicitRows)
    {
        // The walk is in order-modified document order: `order` numbers the items the automatic
        // placement is about, and an item that states no position is packed where its number puts it
        // (CSS Grid 1 ��8.5). The sort is stable, so two items of one number keep the order they were
        // written in.
        var ordered = items
            .Select((item, index) => (Item: item, Index: index, item.Box.Style.Order))
            .OrderBy(entry => entry.Order)
            .ThenBy(entry => entry.Index)
            .Select(entry => entry.Item)
            .ToList();

        items = ordered;

        var columns = Math.Max(1, explicitColumns);
        var rows = Math.Max(1, explicitRows);

        // An item placed by an explicit line can reach past the explicit grid, and the width the
        // automatic placement wraps at has to account for that.
        foreach (var item in items)
        {
            if (!item.AutoColumn)
            {
                columns = Math.Max(columns, item.Column + item.ColumnSpan);
            }

            if (!item.AutoRow)
            {
                rows = Math.Max(rows, item.Row + item.RowSpan);
            }
        }

        var occupied = new HashSet<Int64>();
        var cursorRow = 0;
        var cursorColumn = 0;

        foreach (var item in items)
        {
            if (!item.AutoColumn && !item.AutoRow)
            {
                Occupy(occupied, item);
                continue;
            }

            if (!item.AutoColumn)
            {
                var row = 0;

                while (!Fits(occupied, row, item.Column, item.RowSpan, item.ColumnSpan))
                {
                    row++;
                }

                item.Row = row;
            }
            else if (!item.AutoRow)
            {
                var column = 0;

                while (!Fits(occupied, item.Row, column, item.RowSpan, item.ColumnSpan))
                {
                    column++;
                }

                item.Column = column;
            }
            else
            {
                // Cells past the last column are never occupied - an item only ever claims the cells
                // it covers and the grid grows to fit it - so this always terminates, even for an item
                // wider than the grid.
                while (!Fits(occupied, cursorRow, cursorColumn, item.RowSpan, item.ColumnSpan))
                {
                    cursorColumn++;

                    if (cursorColumn >= columns)
                    {
                        cursorColumn = 0;
                        cursorRow++;
                    }
                }

                item.Row = cursorRow;
                item.Column = cursorColumn;
            }

            Occupy(occupied, item);
            columns = Math.Max(columns, item.Column + item.ColumnSpan);
            rows = Math.Max(rows, item.Row + item.RowSpan);

            // The cursor sits just past whatever was placed from it, so the next item continues the
            // row rather than filling a hole.
            if (item.Column + item.ColumnSpan > cursorColumn)
            {
                cursorColumn = item.Column + item.ColumnSpan;
            }

            if (cursorColumn >= columns)
            {
                cursorColumn = 0;
                cursorRow = item.Row + 1;
            }
        }

        return (columns, rows);
    }

    private static Boolean Fits(HashSet<Int64> occupied, Int32 row, Int32 column, Int32 rowSpan, Int32 columnSpan)
    {
        if (column < 0 || row < 0)
        {
            return false;
        }

        for (var r = row; r < row + rowSpan; r++)
        {
            for (var c = column; c < column + columnSpan; c++)
            {
                if (occupied.Contains(Key(r, c)))
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static void Occupy(HashSet<Int64> occupied, GridCell item)
    {
        for (var r = item.Row; r < item.Row + item.RowSpan; r++)
        {
            for (var c = item.Column; c < item.Column + item.ColumnSpan; c++)
            {
                occupied.Add(Key(r, c));
            }
        }
    }

    private static Int64 Key(Int32 row, Int32 column) => ((Int64)row << 32) | (UInt32)column;

    /// <summary>
    /// Drops the tracks of an <c>auto-fit</c> repetition that no item reaches, and moves the items onto
    /// the tracks that are left, which is what the axis then sizes, aligns and decorates. Nothing is
    /// dropped where the room to fill is not settled, where the list holds no such repetition, or where
    /// an item reaches past the list - what an item stands in beyond the repetition is an implicit track,
    /// which is a track of its own however empty the tracks beside it are.
    /// </summary>
    /// <returns>The sizes of the tracks that stand, or nothing to say the list is wanted as it is.</returns>
    private static IReadOnlyList<GridTrackSize>? Collapse(
        GridTrackList list,
        List<GridCell> items,
        Boolean horizontal,
        Double basis,
        Double gap,
        Boolean definite)
    {
        if (!definite || !list.IsAutoFit)
        {
            return null;
        }

        var all = list.Resolve(basis, gap, Int32.MaxValue);
        var total = all.Count;
        var (from, repeated) = list.Repeated(basis, gap);
        var taken = new Boolean[total];

        foreach (var item in items)
        {
            var start = horizontal ? item.Column : item.Row;
            var span = horizontal ? item.ColumnSpan : item.RowSpan;

            if (start < 0 || start + span > total)
            {
                return null;
            }

            for (var index = start; index < start + span; index++)
            {
                taken[index] = true;
            }
        }

        var rank = new Int32[total];
        var kept = new List<GridTrackSize>(total);

        for (var index = 0; index < total; index++)
        {
            rank[index] = kept.Count;

            if (index < from || index >= from + repeated || taken[index])
            {
                kept.Add(all[index]);
            }
        }

        if (kept.Count == total)
        {
            return null;
        }

        foreach (var item in items)
        {
            if (horizontal)
            {
                var last = item.Column + item.ColumnSpan - 1;
                item.Column = rank[item.Column];
                item.ColumnSpan = rank[last] - item.Column + 1;
            }
            else
            {
                var last = item.Row + item.RowSpan - 1;
                item.Row = rank[item.Row];
                item.RowSpan = rank[last] - item.Row + 1;
            }
        }

        return kept;
    }

    /// <summary>Counts the tracks the items reach on one axis.</summary>
    private static Int32 Used(List<GridCell> items, Boolean horizontal)
    {
        var last = 0;

        foreach (var item in items)
        {
            last = Math.Max(last, horizontal ? item.Column + item.ColumnSpan : item.Row + item.RowSpan);
        }

        return last;
    }

    /// <summary>
    /// Splices a track list and fills it out to the number of tracks the grid needs, taking the
    /// automatic size for every track past the explicit ones. <paramref name="used"/> narrows an
    /// <c>auto-fit</c> repetition to the tracks an item actually reaches.
    /// </summary>
    private static List<Track> Build(
        GridTrackList list,
        GridTrackSize auto,
        Int32 used,
        Double basis,
        Double gap,
        IReadOnlyList<GridTrackSize>? kept = null)
    {
        var resolved = kept ?? list.Resolve(basis, gap, used);
        var total = Math.Max(Math.Max(used, 1), resolved.Count);
        var tracks = new List<Track>(total);

        for (var index = 0; index < total; index++)
        {
            tracks.Add(new Track { Size = index < resolved.Count ? resolved[index] : auto });
        }

        return tracks;
    }

    private List<Track> SizeColumns(
        GridStyle grid,
        List<GridCell> items,
        Int32 used,
        Double basis,
        Double gap,
        Boolean definite,
        LayoutContext context,
        out Double minTotal,
        IReadOnlyList<GridTrackSize>? kept = null)
    {
        var tracks = Build(grid.Columns, grid.AutoColumn, used, basis, gap, kept);

        Initialize(tracks, basis);
        ContributeWidths(tracks, items, context, grid);

        // The base sizes before any growth are the tracks' minima - a fixed length, or the largest
        // min-content contribution for an intrinsic minimum - so their sum is the grid's min-content.
        minTotal = Total(tracks, gap);

        Resolve(tracks, basis, gap, definite, grid.JustifyContent);

        return tracks;
    }

    /// <summary>
    /// Sizes and places the cross (column) axis exactly as a grid would, and hands back the resolved
    /// column widths plus each item's column, column span, placement row and area width. A grid-lanes
    /// container borrows this wholesale: its cross axis is an ordinary grid axis - the track count, the
    /// <c>auto</c>/<c>max-content</c> widths and the placement cursor are settled by the very same code
    /// a grid uses - and only the lane axis, the rungs down each column, is packed differently after.
    /// </summary>
    /// <remarks>
    /// This runs the first half of <see cref="Compute"/> (collect, resolve both axes' lines, place,
    /// then size the columns) and stops before any row is sized or anything positioned, so nothing here
    /// can change what a grid computes; it just reaches into the shared helpers for the column result.
    /// </remarks>
    internal CrossAxisLayout LayoutCrossTracks(Box box, Double contentWidth, Boolean definiteWidth, LayoutContext context)
    {
        var grid = box.Style.Grid;
        var items = new List<GridCell>();

        foreach (var child in box.Children)
        {
            if (child.IsBlockLevel && !PositionedLayout.IsOutOfFlow(child))
            {
                items.Add(Create(child));
            }
        }

        var columnBasis = definiteWidth ? contentWidth : 0;
        var gapX = grid.ColumnGapAgainst(columnBasis);
        var gapY = grid.RowGap;

        // The explicit grid bounds the automatic placement wrap, so both counts are taken before placing.
        var explicitColumns = grid.Columns.ExplicitCount(columnBasis, gapX);
        var explicitRows = grid.Rows.ExplicitCount(0, gapY);

        // A line named in a subgrid is one the parent wrote, so the borrowed names stand in for the
        // axis that borrows tracks; where the list is null the grid's own names answer.
        var handed = context.Subgrid;
        var namedColumns = grid.ColumnsSubgrid
            ? GridLineNames.Merged(handed?.ColumnLines, grid.ColumnLines)
            : null;
        var namedRows = grid.RowsSubgrid
            ? GridLineNames.Merged(handed?.RowLines, grid.RowLines)
            : null;

        foreach (var item in items)
        {
            ResolveAxis(item, true, explicitColumns, grid, namedColumns);
            ResolveAxis(item, false, explicitRows, grid, namedRows);
        }

        Place(items, grid, explicitColumns, explicitRows);

        var itemContext = context.ForContainingBlock(new Size(contentWidth, 0));
        var usedColumns = Used(items, true);
        var columnTracks = SizeColumns(grid, items, usedColumns, columnBasis, gapX, definiteWidth, itemContext, out _);

        var layout = new CrossAxisLayout { GapX = gapX, GapY = gapY };

        foreach (var track in columnTracks)
        {
            layout.ColumnWidths.Add(track.Base);
        }

        foreach (var item in items)
        {
            var areaWidth = Span(columnTracks, item.Column, item.ColumnSpan, gapX);
            layout.Items.Add(new CrossPlacedItem(item.Box, item.Column, item.ColumnSpan, item.Row, item.RowSpan, areaWidth));
        }

        return layout;
    }

    /// <summary>
    /// Sizes the rows from what the items ask of them.
    /// </summary>
    /// <param name="columns">
    /// The columns as they ended up, with the gutter the grid laid them by and the names it wrote on their
    /// lines. A subgrid item reads them to know how wide its own items stand, because a row's height is
    /// whatever the content needs at the width the columns gave it.
    /// </param>
    private List<Track> SizeRows(
        GridStyle grid,
        List<GridCell> items,
        Int32 used,
        Double basis,
        Double gap,
        Boolean definite,
        LayoutContext context,
        List<Track> columns,
        Double columnGap,
        GridLineNames? columnNames,
        GridLineNames? rowNames,
        IReadOnlyList<GridTrackSize>? kept)
    {
        var tracks = Build(grid.Rows, grid.AutoRow, used, basis, gap, kept);

        Initialize(tracks, basis);
        ContributeHeights(
            tracks,
            items,
            basis,
            context,
            gap,
            columns,
            columnGap,
            columnNames,
            rowNames,
            // What a subgrid asks of the rows it borrows is only a question when there are tracks to
            // borrow: a grid whose own rows are borrowed from a container that lays none - a grid-lanes
            // lane, which is packed rather than tracked - has nothing to hand its items' demands to.
            !grid.RowsSubgrid || context.Subgrid?.Rows is { Count: > 0 });
        Resolve(tracks, basis, gap, definite, grid.AlignContent);

        return tracks;
    }

    /// <summary>Turns each track's declared sizing function into a starting size and a growth limit.</summary>
    private static void Initialize(List<Track> tracks, Double basis)
    {
        foreach (var track in tracks)
        {
            var min = track.Size.Min;
            var max = track.Size.Max;

            track.Base = min.Fixed(basis);
            track.IntrinsicMin = min.IsIntrinsic;
            track.Factor = max.IsFraction ? max.Value : 0;
            track.Flexible = max.IsFraction;
            track.Growth = max.IsFraction ? 0 : max.Fixed(basis);
            track.IntrinsicMax = max.IsIntrinsic;
            track.Stretchable = max.Kind == GridBreadthKind.Auto;
            track.ContentMin = 0;
            track.ContentMax = 0;
        }
    }

    /// <summary>
    /// Gathers what each column has to hold. An item with a declared width contributes that width
    /// instead of its content, and the margins come too, because a track holds an outer box.
    /// </summary>
    private void ContributeWidths(List<Track> tracks, List<GridCell> items, LayoutContext context, GridStyle grid)
    {
        var basis = context.ContainingBlock.Width;

        foreach (var item in items)
        {
            var style = item.Box.Style;
            var margin = style.Margin.Resolve(basis).Horizontal;
            var padding = style.Padding.Resolve(basis);
            var insetH = padding.Horizontal + BlockLayout.BorderOf(item.Box).Horizontal;
            var isBorderBox = BoxModel.IsBorderBox(style);
            Double? declared = null;

            // An item whose width is a percentage contributes as `auto` to the sizing of a track it stands
            // in (CSS Grid 1 ��12.4): the percentage is a share of the area that track gives it, so it
            // cannot be what decides that area (��6.6). Charging it to the container - which is all `basis`
            // can be before the tracks are known - made a `width: 100%` item inside a `repeat(2, 1fr)`
            // grid ask for the whole grid, and two 110px tracks were laid into a 110px container.
            if (HasWidth(item.Box) && style.Width.Kind != StyleLengthKind.Percent)
            {
                var width = Math.Max(0, style.Width.Resolve(basis));
                declared = isBorderBox ? width : width + insetH;
            }

            if (declared is { } fixedWidth)
            {
                item.MinWidth = fixedWidth + margin;
                item.MaxWidth = item.MinWidth;
                continue;
            }

            // An item with an aspect ratio and a block size its rows have already settled has a definite
            // inline size: the ratio carries one axis to the other (CSS Grid 1 ��6.6, CSS Sizing 4 ��4), and
            // the columns are sized from it. Only a fixed track has a size before the rows are sized, so
            // only a span of fixed tracks answers here - every other row waits for its content.
            if (!HasWidth(item.Box) && style.AspectRatio is { } ratio && FixedRows(grid, item) is { } blockSize)
            {
                // The ratio is asked of the block size the item *has*, which is the height it states where
                // that height is a length or a share of the settled row, and the whole row otherwise.
                var height = style.Height;
                var block = height.Kind switch
                {
                    StyleLengthKind.Percent => Math.Max(0, height.Value * blockSize),
                    StyleLengthKind.Pixels => Math.Max(0, height.Value),
                    _ => blockSize,
                };
                var inline = Math.Max(0, block * ratio.Value);
                item.MinWidth = inline + margin;
                item.MaxWidth = item.MinWidth;
                continue;
            }

            // The content intrinsic size is what the item needs once its own declared width is set
            // aside, which is the right question here for every item - and the only right one for an
            // anonymous wrapper, whose declared width belongs to the container.
            var intrinsic = _engine.MeasureContentIntrinsic(item.Box, context);
            var minimum = (BoxModel.ContentBound(style.MinWidth, basis, insetH, isBorderBox) ?? 0) + insetH;

            // CSS Grid 4.5, like Flexbox 4.5: an item whose inline overflow is a scroll axis has an
            // automatic minimum size of zero - its content scrolls, so it sets no floor - while an
            // explicit min-width still applies. This is what lets a `col-span-full overflow:hidden`
            // carousel shrink to its track instead of inflating the whole grid's min-content.
            var contentMin = item.Box.Style.ScrollsInline ? 0 : intrinsic.MinContent;

            var minContribution = Math.Max(contentMin, minimum) + margin;
            var maxContribution = Math.Max(intrinsic.MaxContent, minimum) + margin;

            if (BoxModel.ContentBound(style.MaxWidth, basis, insetH, isBorderBox) is { } cap)
            {
                maxContribution = Math.Min(maxContribution, cap + margin);
            }

            item.MinWidth = minContribution;
            item.MaxWidth = Math.Max(minContribution, maxContribution);
        }

        Contribute(tracks, items, true);
    }

    /// <summary>
    /// Gets the block size an item's rows already give it, or null where they give none: every row it
    /// spans has to be an absolute track, which is the one kind whose size is known before the rows are
    /// sized. The gutters it spans come too, because the ratio is asked of the item's whole border box.
    /// </summary>
    private static Double? FixedRows(GridStyle grid, GridCell item)
    {
        var rows = grid.Rows;

        // A list with an automatic repetition has no size at all until it is told what to repeat into,
        // which is a question about the container rather than about the item.
        if (rows is null || rows.HasAutoRepeat)
        {
            return null;
        }

        var tracks = rows.Resolve(0, 0, rows.StaticCount);

        if (item.RowSpan < 1 || item.Row < 0 || item.Row + item.RowSpan > tracks.Count)
        {
            return null;
        }

        var size = 0.0;

        for (var index = item.Row; index < item.Row + item.RowSpan; index++)
        {
            var breadth = tracks[index].Min;

            if (breadth.Kind != GridBreadthKind.Length)
            {
                return null;
            }

            size += breadth.Fixed(0);
        }

        return item.RowSpan > 1 ? size + grid.RowGap * (item.RowSpan - 1) : size;
    }

    /// <summary>
    /// Gathers what each row has to hold. The heights are measured here rather than alongside the
    /// widths, because an item's height depends on the width the columns ended up with.
    /// </summary>
    /// <param name="rowGap">The gutter this grid lays its rows by, which a subgrid item answers to.</param>
    /// <param name="columns">The columns as they were sized, with the gutter they were laid by.</param>
    /// <param name="columnNames">The names written on this grid's column lines, for a subgrid to borrow.</param>
    /// <param name="rowNames">The names written on this grid's row lines, for a subgrid to borrow.</param>
    /// <param name="borrowed">
    /// Whether the rows being sized are tracks this grid took from a container that laid them, which is
    /// what makes it worth asking what each of a subgrid's own items wants.
    /// </param>
    private void ContributeHeights(
        List<Track> tracks,
        List<GridCell> items,
        Double basis,
        LayoutContext context,
        Double rowGap,
        List<Track> columns,
        Double columnGap,
        GridLineNames? columnNames,
        GridLineNames? rowNames,
        Boolean borrowed)
    {
        // What the tracks are contributed by: every ordinary item stands in this list itself, while a
        // subgrid is replaced by the items it sets in the tracks it borrows.
        var cells = new List<GridCell>(items.Count);

        foreach (var item in items)
        {
            var style = item.Box.Style;

            if (borrowed && style.Grid.RowsSubgrid)
            {
                // The rows a subgrid borrows are the parent's to size, and what it asks of them is what
                // its own items ask of the tracks they stand in, each grown by the room the subgrid's
                // gutter and its own padding take at the edges they press on (CSS Grid 2, sizing
                // subgrids). Its own height belongs to the area the parent gave it, not to the rows.
                cells.AddRange(BorrowedRows(
                    item, context, rowGap, basis, columns, columnGap, columnNames, rowNames));
                continue;
            }

            // A grid item's containing block is its own grid area (CSS Grid 1 ��6.6), so a percentage on
            // its margin or padding counts against that area's inline size and not the whole grid's.
            // Padding is where this shows: `padding-bottom: 39.54%` is the classic way to give a box a
            // fixed ratio, and charging it to the container instead doubles the height the item asks for,
            // which stretches every row it lands in - and with them anything stretched inside a sibling.
            var areaWidth = item.AreaWidth;
            var margin = style.Margin.Resolve(areaWidth).Vertical;
            var padding = style.Padding.Resolve(areaWidth);
            var insetV = padding.Vertical + BlockLayout.BorderOf(item.Box).Vertical;
            // The same rule down the block axis (CSS Grid 1 ��12.4): a `height: 100%` item contributes as
            // `auto` to its row, since the percentage is a share of that row's height (��6.6) and a share of
            // a size cannot be what decides the size.
            var declared = HasHeight(item.Box) && style.Height.Kind != StyleLengthKind.Percent
                ? BoxModel.DeclaredHeight(style, basis, insetV)
                : null;
            Double height;

            if (declared is { } fixedContent)
            {
                height = BoxModel.ClampHeight(style, fixedContent, basis, insetV) + insetV;
            }
            else
            {
                // Measure answers with the item's border box, its insets and all, so they are added
                // here only for an anonymous wrapper, whose answer is its content alone. Adding them
                // for both would size the row to the item twice over - a padded cell would stand
                // taller than what it holds by its own padding again.
                height = ContentHeight(item.Box, item.AreaWidth, areaWidth, context);

                if (item.Box.IsAnonymous)
                {
                    height += insetV;
                }
            }

            // CSS Grid 6.6 gives an item whose block overflow is a scroll axis an automatic minimum
            // size of zero - the same rule ContributeWidths applies along the inline axis - so a cell
            // that clips what it holds does not push its row open with it. A row then keeps the height
            // the grid itself was given, and the content taller than that is clipped, which is what a
            // page stating a height on a grid of fixed-size cells asks for: the engine used to size the
            // row from the content and show a second line of it over whatever the grid covered.
            var floor = style.ScrollsBlock ? 0 : height;

            item.MinHeight = floor + margin;
            item.MaxHeight = height + margin;
            cells.Add(item);
        }

        Contribute(tracks, cells, false);
    }

    /// <summary>
    /// Gets what the items of a subgrid ask of the rows it borrows, each one landed on the borrowed row
    /// it stands in and grown by the room the subgrid's gutter and its own padding take at the edges that
    /// press on the tracks.
    /// </summary>
    /// <remarks>
    /// An item of the subgrid that is itself a subgrid along this axis is asked as a whole, the way any
    /// other item is: what a subgrid two levels down wants is its own parent's business before it is this
    /// grid's, and the two answers would have to be composed to tell them apart.
    /// </remarks>
    private List<GridCell> BorrowedRows(
        GridCell item,
        LayoutContext context,
        Double rowGap,
        Double rowBasis,
        List<Track> columns,
        Double columnGap,
        GridLineNames? columnNames,
        GridLineNames? rowNames)
    {
        var box = item.Box;
        var grid = box.Style.Grid;
        var area = item.AreaWidth;
        var gap = grid.RowGapStated ? grid.RowGapAgainst(rowBasis) : rowGap;
        var padding = box.Style.Padding.Resolve(area);
        var margin = box.Style.Margin.Resolve(area);
        var border = BlockLayout.BorderOf(box);
        var before = margin.Top + border.Top + padding.Top;
        var after = margin.Bottom + border.Bottom + padding.Bottom;
        var room = Math.Max(0, area - margin.Horizontal - border.Horizontal - padding.Horizontal);
        var rows = item.RowSpan;
        var inner = new List<GridCell>();

        foreach (var child in box.Children)
        {
            if (child.IsBlockLevel && !PositionedLayout.IsOutOfFlow(child))
            {
                inner.Add(Create(child));
            }
        }

        // The columns the subgrid lays its own items in are the parent's cut to its own gutter, because a
        // row is sized by what the content needs at the width the columns left it. Where the subgrid lays
        // columns of its own it is asked for them the same way any grid is.
        var borrows = grid.ColumnsSubgrid && columns.Count > 0;
        var step = borrows
            ? grid.ColumnGapStated ? grid.ColumnGapAgainst(room) : columnGap
            : grid.ColumnGapAgainst(room);
        var laid = borrows
            ? Math.Min(item.ColumnSpan, columns.Count)
            : grid.Columns.ExplicitCount(room, step);
        // The lines an item of the subgrid is placed against are the ones it borrows, named first by the
        // parent and then by the subgrid's own groups - the same reading its layout gives them, which the
        // row an item lands in has to agree with.
        var named = GridLineNames.Merged(columnNames?.Slice(item.Column, item.ColumnSpan), grid.ColumnLines);
        var namedRows = GridLineNames.Merged(rowNames?.Slice(item.Row, item.RowSpan), grid.RowLines);

        foreach (var cell in inner)
        {
            ResolveAxis(cell, true, laid, grid, borrows ? named : grid.ColumnLines);
            ResolveAxis(cell, false, rows, grid, borrows ? namedRows : grid.RowLines);
        }

        Place(inner, grid, laid, rows);

        var tracks = borrows
            ? Cut(
                Borrowed(Bases(columns)) ?? new List<Track>(),
                columnGap,
                step,
                room,
                margin.Left + border.Left + padding.Left)
            : SizeColumns(grid, inner, Used(inner, true), room, step, definite: true, context, out _);

        var cells = new List<GridCell>(inner.Count);

        foreach (var cell in inner)
        {
            var first = cell.Row;
            var span = Math.Min(cell.RowSpan, rows - first);

            if (span <= 0 || first < 0)
            {
                continue;
            }

            var areaWidth = Span(tracks, cell.Column, cell.ColumnSpan, step);

            cell.AreaWidth = areaWidth;
            cell.MaxHeight = RowDemand(cell, areaWidth, context) + cell.Box.Style.Margin.Resolve(areaWidth).Vertical;
            cell.MinHeight = cell.Box.Style.ScrollsBlock ? 0 : cell.MaxHeight;

            // The room between the row the item is measured in and the one the parent sized: the box's own
            // edge and half of each gutter at the ends of the subgrid, the whole difference between the two
            // gutters for a row inside it, and a boundary the item reaches across hands back the gutter the
            // parent left there but the subgrid did not ask for. A subgrid of one row has no line of its
            // own at all, so both of its edges press on that one track.
            var extra = (span - 1) * (rowGap - gap);

            for (var index = first; index < first + span; index++)
            {
                if (rows == 1)
                {
                    extra += before + after;
                    continue;
                }

                extra += index switch
                {
                    0 => before + gap / 2 - rowGap / 2,
                    var last when last == rows - 1 => after + gap / 2 - rowGap / 2,
                    _ => gap - rowGap,
                };
            }

            cell.Row = item.Row + first;
            cell.RowSpan = span;
            cell.MinHeight = Math.Max(0, cell.MinHeight + extra);
            cell.MaxHeight = Math.Max(0, cell.MaxHeight + extra);
            cells.Add(cell);
        }

        return cells;
    }

    /// <summary>Gets the size each track of one axis came to, in the order the axis lays them.</summary>
    private static List<Double> Bases(List<Track> tracks)
    {
        var sizes = new List<Double>(tracks.Count);

        foreach (var track in tracks)
        {
            sizes.Add(track.Base);
        }

        return sizes;
    }

    /// <summary>
    /// The height an item of a subgrid asks of its row: its declared height where it has one that is not
    /// a share of the row, and its content otherwise, in the same order the ordinary items are asked.
    /// </summary>
    private Double RowDemand(GridCell cell, Double areaWidth, LayoutContext context)
    {
        var style = cell.Box.Style;
        var insetV = style.Padding.Resolve(areaWidth).Vertical + BlockLayout.BorderOf(cell.Box).Vertical;

        if (HasHeight(cell.Box) && style.Height.Kind != StyleLengthKind.Percent)
        {
            return BoxModel.DeclaredHeight(style, 0, insetV) is { } fixedContent
                ? BoxModel.ClampHeight(style, fixedContent, 0, insetV) + insetV
                : insetV;
        }

        var height = ContentHeight(cell.Box, cell.AreaWidth, areaWidth, context);

        return cell.Box.IsAnonymous ? height + insetV : height;
    }

    /// <summary>
    /// Raises the base sizes and growth limits of the tracks the items cover, in the order the
    /// specification considers them: the items wholly inside one track first, then the spanning ones
    /// by increasing span. For a single track the contribution is a plain maximum; for a span the
    /// demand is shared out evenly, which is where the deficit against what the tracks already hold
    /// goes.
    /// </summary>
    /// <remarks>
    /// CSS Grid 1 ��11.5 resolves intrinsic track sizes "first ... based on items that are contained
    /// wholly within a single track", and only "then ... gradually adds in the space requirements of
    /// items that span multiple tracks", those being taken in increasing span order. Reading the
    /// items in document order instead gives a spanning item a share of a deficit that does not
    /// exist, because the tracks it covers have not been sized by their own items yet: Wikipedia's
    /// page body grid puts one item across its three content rows beside the 2515px row that item
    /// covers, and the 9.93px share the two small rows were handed stood the empty page title bar
    /// and the page toolbar off the top of the page as a band of white space.
    /// </remarks>
    private static void Contribute(List<Track> tracks, List<GridCell> items, Boolean horizontal)
    {
        var widest = 1;

        foreach (var item in items)
        {
            widest = Math.Max(widest, SpanOf(item, horizontal));
        }

        for (var span = 1; span <= widest; span++)
        {
            foreach (var item in items)
            {
                if (SpanOf(item, horizontal) == span)
                {
                    ContributeItem(tracks, item, horizontal);
                }
            }
        }
    }

    private static Int32 SpanOf(GridCell item, Boolean horizontal) =>
        horizontal ? item.ColumnSpan : item.RowSpan;

    private static void ContributeItem(List<Track> tracks, GridCell item, Boolean horizontal)
    {
        var index = horizontal ? item.Column : item.Row;
        var span = SpanOf(item, horizontal);
        var min = horizontal ? item.MinWidth : item.MinHeight;
        var max = horizontal ? item.MaxWidth : item.MaxHeight;
        var last = Math.Min(index + span, tracks.Count);

        if (last <= index)
        {
            return;
        }

        var count = last - index;

        if (count == 1)
        {
            var track = tracks[index];

            if (track.IntrinsicMin)
            {
                track.Base = Math.Max(track.Base, min);
            }

            if (track.IntrinsicMax)
            {
                track.Growth = Math.Max(track.Growth, max);
            }

            track.ContentMin = Math.Max(track.ContentMin, min);
            track.ContentMax = Math.Max(track.ContentMax, max);
            return;
        }

        // An item that spans a flexible track is measured against a set of tracks the specification
        // calls fixed - "distributing space only to flexible tracks (i.e. treating all other tracks as
        // having a fixed sizing function of their current base size)", with a flexible track itself
        // "having a max track sizing function equal to their min" (��11.5, Increase sizes to
        // accommodate spanning items crossing flexible tracks). So the demand of such an item is
        // shared out among the flexible tracks it spans, by their factors, and the content-sized
        // tracks it also covers are left at the size they hold. Sharing it with them instead is what
        // opened the two content-sized rows of Wikipedia's page body grid: one item is placed across
        // all three of its content rows beside a 1fr row that takes the page's whole height, and the
        // 9.93px each content-sized row was handed for it stood the empty page title bar, and the page
        // toolbar after it, off the top of the page as a band of white space.
        var flexible = 0;
        var receivers = 0;
        var factors = 0.0;

        for (var i = index; i < last; i++)
        {
            if (tracks[i].Flexible)
            {
                flexible++;

                // A flexible track takes the item's demand only where its own minimum is intrinsic:
                // `1fr` stands for `minmax(auto, 1fr)` and takes it, while `minmax(0, 1fr)` has a
                // definite minimum of zero that no content may raise - the rule that keeps a grid of
                // `minmax(0, 1fr)` columns from being blown out by a wide spanning item.
                if (tracks[i].IntrinsicMin)
                {
                    receivers++;
                    factors += tracks[i].Factor;
                }
            }
        }

        var held = 0.0;
        var limit = 0.0;

        for (var i = index; i < last; i++)
        {
            held += tracks[i].Base;
            limit += flexible > 0 ? tracks[i].Base : tracks[i].Growth;
        }

        if (min > held)
        {
            if (flexible > 0)
            {
                // The flexible tracks take it between them in proportion to their factors, which is
                // what the specification means by distributing "according to the ratios of their
                // flexible sizing functions rather than distributing space equally".
                if (receivers > 0 && factors > 0)
                {
                    var each = (min - held) / factors;

                    for (var i = index; i < last; i++)
                    {
                        if (tracks[i].Flexible && tracks[i].IntrinsicMin)
                        {
                            tracks[i].Base += each * tracks[i].Factor;
                        }
                    }
                }
            }
            else
            {
                // The extra a spanning item needs is shared only among the spanned tracks with an
                // intrinsic minimum (CSS Grid 12.6): a track whose minimum is a definite length -
                // `minmax(0, 1fr)` - is never raised by an item's content. That is what lets a
                // `grid-cols-*` of `minmax(0, 1fr)` tracks shrink instead of being blown out by a wide
                // spanning item, which is the difference between a page fitting its viewport and
                // ballooning past it.
                var floors = 0;

                for (var i = index; i < last; i++)
                {
                    if (tracks[i].IntrinsicMin)
                    {
                        floors++;
                    }
                }

                if (floors > 0)
                {
                    var share = (min - held) / floors;

                    for (var i = index; i < last; i++)
                    {
                        if (tracks[i].IntrinsicMin)
                        {
                            tracks[i].Base += share;
                        }
                    }
                }
            }
        }

        // A maximum needs nothing from an item that spans a flexible track: the flexible track it
        // would raise is one whose max is its own min, and the content-sized tracks it covers are
        // fixed against it - so there is nowhere for the room to go, and opening their growth limits
        // is what gives the free space of a definite-height grid somewhere to land that it should
        // never have had.
        if (max > limit && flexible == 0)
        {
            var share = (max - limit) / count;

            for (var i = index; i < last; i++)
            {
                if (tracks[i].IntrinsicMax)
                {
                    tracks[i].Growth += share;
                }
            }
        }

        for (var i = index; i < last; i++)
        {
            if (flexible > 0 && !tracks[i].Flexible)
            {
                continue;
            }

            tracks[i].ContentMin = Math.Max(tracks[i].ContentMin, min / count);
            tracks[i].ContentMax = Math.Max(tracks[i].ContentMax, max / count);
        }
    }

    /// <summary>
    /// Hands the free space out in the order the specification does: to the growth limits first, then
    /// to the fraction tracks, then to whatever is left over by stretching the <c>auto</c> tracks.
    /// </summary>
    private static void Resolve(
        List<Track> tracks,
        Double containerSize,
        Double gap,
        Boolean definite,
        FlexAlignment alignment)
    {
        if (!definite)
        {
            // Without a container size to fill, every track is as big as what it holds.
            foreach (var track in tracks)
            {
                track.Base = Math.Max(track.Base, track.ContentMax);
            }

            return;
        }

        var free = containerSize - Total(tracks, gap);

        if (free > Epsilon)
        {
            free = GrowToLimits(tracks, free);
        }

        if (free > Epsilon)
        {
            ExpandFlexible(tracks, containerSize, gap, tracks.Count);
            free = containerSize - Total(tracks, gap);
        }

        if (free > Epsilon && alignment is FlexAlignment.Stretch or FlexAlignment.Auto or FlexAlignment.Normal)
        {
            StretchAuto(tracks, free);
        }
    }

    /// <summary>
    /// Grows the tracks that still have room below their growth limit, sharing the space equally and
    /// giving up the share of a track that reaches its limit.
    /// </summary>
    private static Double GrowToLimits(List<Track> tracks, Double free)
    {
        var remaining = free;

        for (var pass = 0; pass < MaxIterations && remaining > Epsilon; pass++)
        {
            var candidates = 0;

            foreach (var track in tracks)
            {
                if (!track.Flexible && track.Growth - track.Base > Epsilon)
                {
                    candidates++;
                }
            }

            if (candidates == 0)
            {
                break;
            }

            var share = remaining / candidates;
            var used = 0.0;

            foreach (var track in tracks)
            {
                if (track.Flexible)
                {
                    continue;
                }

                var room = track.Growth - track.Base;

                if (room <= Epsilon)
                {
                    continue;
                }

                var added = Math.Min(room, share);
                track.Base += added;
                used += added;
            }

            if (used <= Epsilon)
            {
                break;
            }

            remaining -= used;
        }

        return remaining;
    }

    /// <summary>
    /// Gives the free space to the <c>fr</c> tracks in proportion to their factors. A track that
    /// cannot go below its own base size is frozen there and the rest are recomputed without it,
    /// which is the specification's "find the size of an fr" loop.
    /// </summary>
    private static void ExpandFlexible(List<Track> tracks, Double containerSize, Double gap, Int32 count)
    {
        var pool = new List<Track>();
        var inflexible = 0.0;

        foreach (var track in tracks)
        {
            if (track.Flexible && track.Factor > 0)
            {
                pool.Add(track);
            }
            else
            {
                inflexible += track.Base;
            }
        }

        if (pool.Count == 0)
        {
            return;
        }

        var leftover = containerSize - (count > 1 ? gap * (count - 1) : 0) - inflexible;

        for (var pass = 0; pass < MaxIterations && pool.Count > 0; pass++)
        {
            var factor = 0.0;

            foreach (var track in pool)
            {
                factor += track.Factor;
            }

            if (factor <= 0)
            {
                break;
            }

            // A total factor below one leaves the tracks short of the space; the specification
            // expresses that by treating the sum as one.
            var hypothetical = leftover / Math.Max(1.0, factor);
            Track? frozen = null;

            foreach (var track in pool)
            {
                if (track.Base > (track.Factor * hypothetical) + Epsilon)
                {
                    frozen = track;
                    break;
                }
            }

            if (frozen is null)
            {
                foreach (var track in pool)
                {
                    track.Base = Math.Max(0, track.Factor * hypothetical);
                }

                return;
            }

            leftover -= frozen.Base;
            pool.Remove(frozen);
        }
    }

    /// <summary>Grows the tracks whose maximum is <c>auto</c>, which is the last call on the space.</summary>
    private static void StretchAuto(List<Track> tracks, Double free)
    {
        var count = 0;

        foreach (var track in tracks)
        {
            if (track.Stretchable)
            {
                count++;
            }
        }

        if (count == 0)
        {
            return;
        }

        var share = free / count;

        foreach (var track in tracks)
        {
            if (track.Stretchable)
            {
                track.Base += share;
            }
        }
    }

    /// <summary>Places an item inside its area, resolving both of its alignment axes.</summary>
    private void Position(
        GridCell item,
        GridStyle grid,
        Double areaX,
        Double areaY,
        Double areaWidth,
        Double areaHeight,
        LayoutContext context)
    {
        var style = item.Box.Style;

        // The area the item was placed in is its containing block (CSS Grid 1 ��6.6), so percentages on
        // its own margin and padding count against the area, not against the grid's content box.
        var basis = areaWidth;
        var margin = style.Margin.Resolve(basis);
        var padding = style.Padding.Resolve(basis);
        var border = BlockLayout.BorderOf(item.Box);
        var insetH = padding.Horizontal + border.Horizontal;
        var insetV = padding.Vertical + border.Vertical;
        var isBorderBox = BoxModel.IsBorderBox(style);

        var justify = item.JustifySelf == FlexAlignment.Auto ? grid.JustifyItems : item.JustifySelf;
        var align = item.AlignSelf == FlexAlignment.Auto ? grid.AlignItems : item.AlignSelf;

        var width = ResolveWidth(style, item.Box, areaWidth, insetH, isBorderBox, justify, context, margin.Horizontal);
        var height = ResolveHeight(
            style, item.Box, areaHeight, basis, insetV, isBorderBox, align, context, margin.Vertical);

        // A declared height is definite, and so is a stretch: CSS Sizing 3 ��5.2 counts a size that can
        // be resolved from a `stretch` self-alignment as definite, so a percentage inside the item has
        // to resolve against the area it was stretched to rather than collapse to nothing.
        item.HeightIsDefinite =
            (StatesHeight(item.Box) && !(style.Height.Kind == StyleLengthKind.Percent && areaHeight <= 0)) ||
            align is FlexAlignment.Stretch or FlexAlignment.Auto or FlexAlignment.Normal;

        var outerX = width + margin.Left + margin.Right;
        var outerY = height + margin.Top + margin.Bottom;

        item.OffsetX = areaX + margin.Left + Space(justify, areaWidth - outerX);
        item.OffsetY = areaY + margin.Top + Space(align, areaHeight - outerY);

        item.BorderBoxWidth = width;
        item.BorderBoxHeight = height;
    }

    /// <summary>
    /// Resolves the item's border-box width inside its area. A declared width is used as it stands;
    /// otherwise <c>stretch</c> fills the area and every other alignment shrinks the item to its own
    /// content, capped by the area.
    /// </summary>
    private Double ResolveWidth(
        ComputedStyle style,
        Box box,
        Double area,
        Double insets,
        Boolean isBorderBox,
        FlexAlignment align,
        LayoutContext context,
        Double margins)
    {
        // The area is the item's containing block, and its margins stand inside that block: a stretched
        // item fills what the margins leave rather than the whole area, which is what makes a grid item
        // with `margin-left: 10px` end at the area's edge instead of past it (positioned-grid-items-013).
        var space = Math.Max(0, area - margins);
        Double value;

        if (HasWidth(box) && !(style.Width.Kind == StyleLengthKind.Percent && area <= 0))
        {
            // A content keyword is answered by the content, not by a length: `max-content` wants the
            // room the content takes on one line, `min-content` the least it fits in, and both are
            // capped by the area the item was given (CSS Sizing 3 §5).
            var resolved = style.Width.IsContentSized
                ? ContentWidth(style.Width, box, space, context)
                : Math.Max(0, style.Width.Resolve(area));

            value = isBorderBox ? resolved : resolved + insets;
        }
        else if (align is FlexAlignment.Stretch or FlexAlignment.Auto or FlexAlignment.Normal)
        {
            value = space;
        }
        else
        {
            // The content intrinsic size sets the item's own declared width aside, which is exactly
            // what an anonymous wrapper needs: its declared width belongs to the container. A column's
            // intrinsic size is measured along its line, which is its height; the width it asks for is
            // the block size the measure gives, not that inline extent (orthogonal-positioned-grid-items-001).
            var content = box.Flow.IsVertical
                ? _engine.Measure(box, MeasureConstraints.Unconstrained, context).Width
                : _engine.MeasureContentIntrinsic(box, context).MaxContent;

            value = Math.Min(Math.Max(0, content), space);
        }

        return BoxModel.ClampWidth(style, value, area, insets);
    }

    /// <summary>Resolves the item's border-box height inside its area, following the same rules.</summary>
    private Double ResolveHeight(
        ComputedStyle style,
        Box box,
        Double area,
        Double basis,
        Double insets,
        Boolean isBorderBox,
        FlexAlignment align,
        LayoutContext context,
        Double margins)
    {
        var space = Math.Max(0, area - margins);

        if (StatesHeight(box) && !(style.Height.Kind == StyleLengthKind.Percent && area <= 0))
        {
            // The content keywords are answered by the content itself, as on the inline axis: the block
            // is as tall as what it holds and does not stretch (CSS Sizing 3 §5).
            if (style.Height.IsContentSized)
            {
                return Math.Min(ContentHeight(box, space, basis, context) + insets, space);
            }

            var resolved = Math.Max(0, style.Height.Resolve(area));
            var content = isBorderBox ? Math.Max(0, resolved - insets) : resolved;
            return BoxModel.ClampHeight(style, content, area, insets) + insets;
        }

        if (align is FlexAlignment.Stretch or FlexAlignment.Auto or FlexAlignment.Normal)
        {
            return space;
        }

        var measured = ContentHeight(box, space, basis, context) + insets;
        return Math.Min(measured, space);
    }

    private static Double Space(FlexAlignment align, Double free) => align switch
    {
        FlexAlignment.Center => Math.Max(0, free) / 2,
        FlexAlignment.End or FlexAlignment.FlexEnd or FlexAlignment.Right => Math.Max(0, free),
        _ => 0.0,
    };

    /// <summary>
    /// Places a box horizontally inside an area of <paramref name="areaWidth"/>, honoring its
    /// <c>justify-self</c> over the container's <c>justify-items</c>: a definite width stands, <c>stretch</c>
    /// fills the area, and every other alignment shrinks the box to its content and offsets it. The
    /// grid-lanes (masonry) context borrows this for its cross axis, which is an ordinary grid axis, so a
    /// lanes container aligns items across its columns exactly as a grid does.
    /// </summary>
    internal (Double Offset, Double Width) AlignInline(Box box, Double areaWidth, FlexAlignment justifyItems, LayoutContext context)
    {
        var style = box.Style;

        // Same as placement: the box's containing block is the area it is aligned inside.
        var basis = areaWidth;
        var margin = style.Margin.Resolve(basis);
        var padding = style.Padding.Resolve(basis);
        var border = BlockLayout.BorderOf(box);
        var insetH = padding.Horizontal + border.Horizontal;
        var isBorderBox = BoxModel.IsBorderBox(style);

        var justify = style.GridItem.JustifySelf == FlexAlignment.Auto ? justifyItems : style.GridItem.JustifySelf;
        var width = ResolveWidth(style, box, areaWidth, insetH, isBorderBox, justify, context, margin.Horizontal);
        var outer = width + margin.Left + margin.Right;

        return (margin.Left + Space(justify, areaWidth - outer), width);
    }

    /// <summary>
    /// Gets the tracks a subgrid is handed, or null where the area gave it none: the sizes are the
    /// parent's resolved ones, and the sizing passes are skipped for a list already this size.
    /// </summary>
    private static List<Track>? Borrowed(IReadOnlyList<Double>? sizes)
    {
        if (sizes is not { Count: > 0 })
        {
            return null;
        }

        var tracks = new List<Track>(sizes.Count);

        foreach (var size in sizes)
        {
            tracks.Add(new Track { Size = GridTrackSize.Auto, Base = size, Growth = size });
        }

        return tracks;
    }

    /// <summary>
    /// Gets what stands between a subgrid's borrowed area and its content box: its own margins, border
    /// and padding, which is the edge the first track it borrows is cut to.
    /// </summary>
    private static Thickness Inset(Box box, LayoutContext context)
    {
        var style = box.Style;
        var basis = context.ContainingBlock.Width;
        var border = BlockLayout.BorderOf(box);
        var padding = style.Padding.Resolve(basis);
        var margin = style.Margin.Resolve(basis);

        return new Thickness(
            margin.Top + border.Top + padding.Top,
            margin.Right + border.Right + padding.Right,
            margin.Bottom + border.Bottom + padding.Bottom,
            margin.Left + border.Left + padding.Left);
    }

    /// <summary>
    /// Cuts the tracks a subgrid borrows to the gutters it stands on their lines. Every line it borrows
    /// is the middle of the room the parent left at that line, and its own gutter is centred on that
    /// middle, so a track keeps what lies between the two gutters about it and the box's own edges take
    /// the first and the last (CSS Grid 2, subgrid gutters).
    /// </summary>
    /// <param name="parentGap">The room the parent left at each of the lines, as the step it laid them by.</param>
    /// <param name="gap">The gutter the subgrid stands on those lines.</param>
    /// <param name="room">The box the cut tracks fill, which is what the last of them ends at.</param>
    /// <param name="before">How far that box starts behind the area the tracks were taken from.</param>
    private static List<Track> Cut(List<Track> tracks, Double parentGap, Double gap, Double room, Double before)
    {
        if (tracks.Count < 2)
        {
            // A single borrowed track has no line of its own to stand a gutter on, so the box it is
            // given is the whole of it.
            if (tracks.Count == 1)
            {
                tracks[0].Base = Math.Max(0, room);
                tracks[0].Growth = tracks[0].Base;
            }

            return tracks;
        }

        // Where the middle of each of the parent's gutters stands, counted from the content box's edge.
        var lines = new List<Double>(tracks.Count - 1);
        var at = -before;

        for (var index = 0; index < tracks.Count - 1; index++)
        {
            at += tracks[index].Base + parentGap / 2;
            lines.Add(at);
            at += parentGap / 2;
        }

        var edge = 0.0;

        for (var index = 0; index < tracks.Count; index++)
        {
            var end = index < lines.Count ? lines[index] - gap / 2 : room;
            var size = Math.Max(0, end - edge);

            tracks[index].Base = size;
            tracks[index].Growth = size;
            edge = end + gap;
        }

        return tracks;
    }

    /// <summary>Gets the sizes of the tracks an item's span covers, which is what its subgrid borrows.</summary>
    private static List<Double> Slice(IReadOnlyList<Double> sizes, Int32 from, Int32 span)
    {
        var slice = new List<Double>(Math.Min(span, sizes.Count));

        for (var index = from; index < from + span && index < sizes.Count; index++)
        {
            slice.Add(sizes[index]);
        }

        return slice;
    }

    private static Double Total(List<Track> tracks, Double gap)
    {
        var total = 0.0;

        foreach (var track in tracks)
        {
            total += track.Base;
        }

        return total + (tracks.Count > 1 ? gap * (tracks.Count - 1) : 0);
    }

    private static Double Prefix(List<Track> tracks, Int32 index, Double step)
    {
        var offset = 0.0;
        var last = Math.Min(index, tracks.Count);

        for (var i = 0; i < last; i++)
        {
            offset += tracks[i].Base;
        }

        return offset + (last * step);
    }

    private static Double Span(List<Track> tracks, Int32 index, Int32 span, Double step)
    {
        var size = 0.0;
        var count = 0;

        for (var i = index; i < index + span && i < tracks.Count; i++)
        {
            size += tracks[i].Base;
            count++;
        }

        return size + (count > 1 ? step * (count - 1) : 0);
    }

    /// <summary>Splits the space the tracks leave over according to a content alignment keyword.</summary>
    private static (Double Offset, Double Extra) ContentOffset(FlexAlignment align, Double free, Int32 count)
    {
        var space = Math.Max(0, free);

        return align switch
        {
            FlexAlignment.End or FlexAlignment.FlexEnd or FlexAlignment.Right => (space, 0.0),
            FlexAlignment.Center => (space / 2, 0.0),
            FlexAlignment.SpaceBetween when count > 1 => (0.0, space / (count - 1)),
            FlexAlignment.SpaceAround when count > 0 => (space / count / 2, space / count),
            FlexAlignment.SpaceEvenly when count > 0 => (space / (count + 1), space / (count + 1)),
            _ => (0.0, 0.0),
        };
    }

    /// <summary>One grid item's resolved placement and geometry, relative to the grid's content box.</summary>
    private sealed class GridCell
    {
        public Box Box = null!;

        public GridLine ColumnStart;

        public GridLine ColumnEnd;

        public GridLine RowStart;

        public GridLine RowEnd;

        public FlexAlignment JustifySelf;

        public FlexAlignment AlignSelf;

        public Int32 Row;

        public Int32 Column;

        public Int32 RowSpan = 1;

        public Int32 ColumnSpan = 1;

        public Boolean AutoRow;

        public Boolean AutoColumn;

        public Double MinWidth;

        public Double MaxWidth;

        public Double MinHeight;

        public Double MaxHeight;

        public Double AreaWidth;

        public Double OffsetX;

        public Double OffsetY;

        public Double BorderBoxWidth;

        public Double BorderBoxHeight;

        /// <summary>Gets or sets whether that height is definite, so a percentage inside resolves against it.</summary>
        public Boolean HeightIsDefinite;
    }

    /// <summary>One track's state while it is being sized.</summary>
    private sealed class Track
    {
        public GridTrackSize Size;

        /// <summary>Gets or sets the size being arrived at, which is also the starting size.</summary>
        public Double Base;

        /// <summary>Gets or sets the size the track may grow to, before the fraction tracks are served.</summary>
        public Double Growth;

        public Double Factor;

        public Boolean Flexible;

        public Boolean Stretchable;

        public Boolean IntrinsicMin;

        public Boolean IntrinsicMax;

        public Double ContentMin;

        public Double ContentMax;
    }

    private sealed class GridPlan
    {
        public Double ContentWidth;

        public Double ContentHeight;

        /// <summary>Gets the height the rows come to on their own, before a declared height is put in
        /// their place: what `max-content` and `min-content` stand for on the block axis.</summary>
        public Double IntrinsicHeight;

        /// <summary>The sum of the column base sizes before growth: the grid's min-content inline size.</summary>
        public Double MinWidth;

        /// <summary>Gets how far the column tracks reach, from the content box's start.</summary>
        public Double TracksWidth;

        /// <summary>Gets how far the row tracks reach, from the content box's start.</summary>
        public Double TracksHeight;

        /// <summary>Gets the step a subgrid takes between the columns it borrows: the gutter plus any
        /// room content alignment added.</summary>
        public Double ColumnStep;

        /// <inheritdoc cref="ColumnStep"/>
        public Double RowStep;

        /// <summary>Gets the resolved size of every column track, in order, for the subgrids among the items.</summary>
        public List<Double> ColumnBases { get; } = new();

        /// <inheritdoc cref="ColumnBases"/>
        public List<Double> RowBases { get; } = new();

        /// <summary>
        /// Gets the names that answer a line the page asked for by name along this axis - the ones the
        /// grid wrote, or the ones it borrowed from its parent - for the subgrids among the items.
        /// </summary>
        public GridLineNames? ColumnNames { get; set; }

        /// <inheritdoc cref="ColumnNames"/>
        public GridLineNames? RowNames { get; set; }

        /// <summary>Gets the room between adjacent column tracks, in the content box's coordinates.</summary>
        public List<TrackGap> ColumnGaps { get; } = new();

        /// <summary>Gets the room between adjacent row tracks, in the content box's coordinates.</summary>
        public List<TrackGap> RowGaps { get; } = new();

        /// <summary>
        /// Gets where each column line stands, from the content box's start, the last of them at the end
        /// of the last track. Only a container with an out-of-flow child is asked for them: what reads a
        /// line's place is an absolutely positioned child's own placement, and a grid of ten thousand
        /// tracks costs ten thousand positions to describe.
        /// </summary>
        public List<Double> ColumnLines { get; } = new();

        /// <inheritdoc cref="ColumnLines"/>
        public List<Double> RowLines { get; } = new();

        public List<GridCell> Items { get; } = new();
    }

    /// <summary>The room between two adjacent tracks, which is where a gap decoration stands.</summary>
    internal readonly struct TrackGap
    {
        public TrackGap(Double from, Double to)
        {
            From = from;
            To = to;
        }

        public Double From { get; }

        public Double To { get; }
    }

    /// <summary>The resolved cross (column) axis a grid-lanes container borrows from the grid sizer.</summary>
    internal sealed class CrossAxisLayout
    {
        public Double GapX;

        public Double GapY;

        public List<Double> ColumnWidths { get; } = new();

        public List<CrossPlacedItem> Items { get; } = new();
    }

    /// <summary>One in-flow item's cross-axis placement and area width, as the grid settled them.</summary>
    internal readonly struct CrossPlacedItem
    {
        public CrossPlacedItem(Box box, Int32 column, Int32 columnSpan, Int32 row, Int32 rowSpan, Double areaWidth)
        {
            Box = box;
            Column = column;
            ColumnSpan = columnSpan;
            Row = row;
            RowSpan = rowSpan;
            AreaWidth = areaWidth;
        }

        public Box Box { get; }

        public Int32 Column { get; }

        public Int32 ColumnSpan { get; }

        public Int32 Row { get; }

        public Int32 RowSpan { get; }

        public Double AreaWidth { get; }
    }
}

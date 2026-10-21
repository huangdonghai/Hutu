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

        return new Size(
            BoxModel.ClampWidth(style, contentWidth + insetH, basisWidth, insetH),
            BoxModel.ClampHeight(style, contentHeight, basisHeight, insetV) + insetV);
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

        // A positioned grid container is what an absolutely positioned child anchors to.
        var anchored = false;

        if (box.Style.IsTransformed)
        {
            itemContext = itemContext.WithTransformedContainingBlock(layout.BorderBox);
            anchored = true;
        }
        else if (PositionedLayout.IsPositioned(box))
        {
            itemContext = itemContext.WithAbsoluteContainingBlock(layout.PaddingBox);
            anchored = true;
        }

        foreach (var item in plan.Items)
        {
            var rect = new Rect(
                contentX + item.OffsetX,
                contentY + item.OffsetY,
                item.BorderBoxWidth,
                item.BorderBoxHeight);

            layout.Children.Add(_engine.Arrange(item.Box, rect, itemContext, item.HeightIsDefinite));
        }

        layout.GapDecorations = GapDecorations(box, plan, contentX, contentY);
        ArrangeOutOfFlow(box, layout, plan, layout.PaddingBox, anchored, contentX, contentY, itemContext);
        return layout;
    }

    /// <summary>
    /// Places the absolutely and fixed positioned children, which are not grid items: they take no room
    /// in the grid and contribute nothing to its tracks. An <c>absolute</c> child's place in it is still
    /// the grid's own - <c>grid-row</c> and <c>grid-column</c> name the area that is its containing block
    /// (CSS Grid 2 §10.1) and the area it stands in for its static position. A <c>fixed</c> child is
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

            var (block, standing) = OutFlowArea(child.Style.GridItem, grid, plan, paddingBox, contentX, contentY);
            var childContext = anchored ? context.WithAbsoluteContainingBlock(block) : context;

            // A box with no offsets of its own is put by its own alignment within the area it stands in,
            // which is the one place a grid child's justify-self and align-self reach without the child
            // being a grid item at all (CSS Align 3 §5.2).
            var item = child.Style.GridItem;
            var placed = new StaticStanding(
                standing,
                StaticStanding.Resolve(item.JustifySelf, grid.JustifyItems),
                StaticStanding.Resolve(item.AlignSelf, grid.AlignItems));

            parent.Children.Add(
                _engine.ArrangePositioned(child, new Offset(standing.X, standing.Y), childContext, placed));
        }
    }

    /// <summary>
    /// Gets the two areas an out-of-flow child's placement names: the containing block its offsets count
    /// from and the area its static position stands in. A side named by a line number, a line name or an
    /// area line is the track edge it lands on; a side left auto - or one naming a line the grid has no
    /// room for, which is what the specification says to treat as auto instead of building implicit
    /// tracks for an out-of-flow box (§10.1) - is the container's padding edge. Where nothing in either
    /// axis is placed, the area the child stands in is the content box instead, as if it were the sole
    /// grid item in it (§10.2).
    /// </summary>
    private static (Rect Block, Rect Standing) OutFlowArea(
        GridItemStyle item,
        GridStyle grid,
        GridPlan plan,
        Rect paddingBox,
        Double contentX,
        Double contentY)
    {
        var contentRight = contentX + plan.ContentWidth;
        var contentBottom = contentY + plan.ContentHeight;

        var (blockLeft, leftPlaced) = Edge(
            item.ColumnStart, grid, plan.ColumnLines, horizontal: true, atEnd: false, contentX, paddingBox.X);
        var (blockRight, rightPlaced) = Edge(
            item.ColumnEnd, grid, plan.ColumnLines, horizontal: true, atEnd: true, contentX, paddingBox.Right);
        var (blockTop, topPlaced) = Edge(
            item.RowStart, grid, plan.RowLines, horizontal: false, atEnd: false, contentY, paddingBox.Y);
        var (blockBottom, bottomPlaced) = Edge(
            item.RowEnd, grid, plan.RowLines, horizontal: false, atEnd: true, contentY, paddingBox.Bottom);

        var block = Between(blockLeft, blockRight, blockTop, blockBottom);

        // A placement named on no side at all has no area of its own to stand in, and the content edges
        // answer for it; the moment one side is named, the child stands where that area begins.
        var standing = leftPlaced || rightPlaced || topPlaced || bottomPlaced
            ? block
            : Between(contentX, contentRight, contentY, contentBottom);

        return (block, standing);
    }

    /// <summary>Gets a rect between two edges, holding it at nothing rather than letting it cross over.</summary>
    private static Rect Between(Double left, Double right, Double top, Double bottom) =>
        new(left, top, Math.Max(0, right - left), Math.Max(0, bottom - top));

    /// <summary>
    /// Gets where one side of an out-of-flow child's placement stands, and whether the side was placed at
    /// all: a named line is the track edge it lands on, and anything else is the container's padding
    /// edge, which is the special line an auto side contributes (§10.1).
    /// </summary>
    /// <param name="origin">
    /// Where the line list's own zero stands on the page: the content box's start along this axis.
    /// </param>
    private static (Double At, Boolean Placed) Edge(
        GridLine side,
        GridStyle grid,
        List<Double> lines,
        Boolean horizontal,
        Boolean atEnd,
        Double origin,
        Double paddingEdge)
    {
        var resolved = grid.Resolve(side, horizontal, atEnd).Resolve(lines.Count);

        if (resolved is null || resolved < 1 || resolved > lines.Count)
        {
            return (paddingEdge, false);
        }

        return (origin + lines[resolved.Value - 1], true);
    }


    /// <summary>
    /// Gets the stretch of track on either side of each gap: the areas a gap's decoration asks about
    /// before it is drawn at all (CSS Gaps 1 §3.4).
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
    /// <c>intersection</c> break, where a perpendicular gap crosses it (CSS Gaps 1 §3.1).
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

        GridGapBands.Add(target, rules.Column, rules.Row, isRow: false, columns, rowCrossings, columnBefore, columnAfter, rules.Column.Break, items);
        GridGapBands.Add(target, rules.Row, rules.Column, isRow: true, rows, columnCrossings, rowBefore, rowAfter, rules.Row.Break, items);

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
        var gapX = grid.ColumnGap;
        var gapY = grid.RowGap;

        // The explicit grid is what the automatic placement wraps inside, so both counts are taken
        // before any item is placed.
        var explicitColumns = grid.Columns.ExplicitCount(columnBasis, gapX);
        var explicitRows = grid.Rows.ExplicitCount(rowBasis, gapY);

        foreach (var item in items)
        {
            ResolveAxis(item, true, explicitColumns, grid);
            ResolveAxis(item, false, explicitRows, grid);
        }

        Place(items, grid, explicitColumns, explicitRows);
        var itemContext = context.ForContainingBlock(new Size(contentWidth, contentHeight ?? 0));

        // How far the items reach, which is what collapses an `auto-fit` repetition.
        var usedColumns = Used(items, true);

        // Columns first. A row can only be sized once its items' final widths are known, because the
        // row's own size is whatever height the content needs at that width.
        var columnTracks = SizeColumns(grid, items, usedColumns, columnBasis, gapX, definiteWidth, itemContext, out var minContentWidth);

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

        var rowTracks = SizeRows(grid, items, Used(items, false), rowBasis, gapY, contentHeight is not null, itemContext);

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
            MinWidth = minContentWidth,
        };

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

    /// <summary>Gets whether the item's own declared height may be used, for the same reason.</summary>
    private static Boolean HasHeight(Box box) => !box.IsAnonymous && box.Style.Height.HasValue;

    /// <summary>
    /// Measures the height an item's content needs at a given width. An ordinary item answers through
    /// the usual measurement, but an anonymous wrapper borrows the container's style - so its declared
    /// height is the container's own rather than anything about the text - and has to be arranged at
    /// zero height and read back from where its content ended instead.
    /// </summary>
    private Double ContentHeight(Box box, Double width, Double basis, LayoutContext context)
    {
        var available = Math.Max(0, width);

        // The item's own percentages resolve against its grid area, so the measurement has to be told
        // about that area rather than the grid's content box the shared context was built for.
        context = context.ForContainingBlock(new Size(Math.Max(0, basis), context.ContainingBlock.Height));

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
    private static void ResolveAxis(GridCell item, Boolean horizontal, Int32 explicitTracks, GridStyle grid)
    {
        var start = grid.Resolve(horizontal ? item.ColumnStart : item.RowStart, horizontal, end: false);
        var end = grid.Resolve(horizontal ? item.ColumnEnd : item.RowEnd, horizontal, end: true);
        var lines = explicitTracks + 1;
        var startIndex = start.Resolve(lines);
        var endIndex = end.Resolve(lines);
        var span = 1;

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
        Double gap)
    {
        var resolved = list.Resolve(basis, gap, used);
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
        out Double minTotal)
    {
        var tracks = Build(grid.Columns, grid.AutoColumn, used, basis, gap);

        Initialize(tracks, basis);
        ContributeWidths(tracks, items, context);

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
        var gapX = grid.ColumnGap;
        var gapY = grid.RowGap;

        // The explicit grid bounds the automatic placement wrap, so both counts are taken before placing.
        var explicitColumns = grid.Columns.ExplicitCount(columnBasis, gapX);
        var explicitRows = grid.Rows.ExplicitCount(0, gapY);

        foreach (var item in items)
        {
            ResolveAxis(item, true, explicitColumns, grid);
            ResolveAxis(item, false, explicitRows, grid);
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
            layout.Items.Add(new CrossPlacedItem(item.Box, item.Column, item.ColumnSpan, item.Row, areaWidth));
        }

        return layout;
    }

    private List<Track> SizeRows(
        GridStyle grid,
        List<GridCell> items,
        Int32 used,
        Double basis,
        Double gap,
        Boolean definite,
        LayoutContext context)
    {
        var tracks = Build(grid.Rows, grid.AutoRow, used, basis, gap);

        Initialize(tracks, basis);
        ContributeHeights(tracks, items, basis, context);
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
    private void ContributeWidths(List<Track> tracks, List<GridCell> items, LayoutContext context)
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

            if (HasWidth(item.Box) && !(style.Width.Kind == StyleLengthKind.Percent && basis <= 0))
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
    /// Gathers what each row has to hold. The heights are measured here rather than alongside the
    /// widths, because an item's height depends on the width the columns ended up with.
    /// </summary>
    private void ContributeHeights(List<Track> tracks, List<GridCell> items, Double basis, LayoutContext context)
    {
        foreach (var item in items)
        {
            var style = item.Box.Style;

            // A grid item's containing block is its own grid area (CSS Grid 1 §6.6), so a percentage on
            // its margin or padding counts against that area's inline size and not the whole grid's.
            // Padding is where this shows: `padding-bottom: 39.54%` is the classic way to give a box a
            // fixed ratio, and charging it to the container instead doubles the height the item asks for,
            // which stretches every row it lands in - and with them anything stretched inside a sibling.
            var areaWidth = item.AreaWidth;
            var margin = style.Margin.Resolve(areaWidth).Vertical;
            var padding = style.Padding.Resolve(areaWidth);
            var insetV = padding.Vertical + BlockLayout.BorderOf(item.Box).Vertical;
            var declared = HasHeight(item.Box) ? BoxModel.DeclaredHeight(style, basis, insetV) : null;
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
        }

        Contribute(tracks, items, false);
    }

    /// <summary>
    /// Raises the base sizes and growth limits of the tracks the items cover, in the order the
    /// specification considers them: the items wholly inside one track first, then the spanning ones
    /// by increasing span. For a single track the contribution is a plain maximum; for a span the
    /// demand is shared out evenly, which is where the deficit against what the tracks already hold
    /// goes.
    /// </summary>
    /// <remarks>
    /// CSS Grid 1 §11.5 resolves intrinsic track sizes "first ... based on items that are contained
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
        // "having a max track sizing function equal to their min" (§11.5, Increase sizes to
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

        if (free > Epsilon && alignment is FlexAlignment.Stretch or FlexAlignment.Auto)
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

        // The area the item was placed in is its containing block (CSS Grid 1 §6.6), so percentages on
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

        var width = ResolveWidth(style, item.Box, areaWidth, insetH, isBorderBox, justify, context);
        var height = ResolveHeight(style, item.Box, areaHeight, basis, insetV, isBorderBox, align, context);

        // A declared height is definite, and so is a stretch: CSS Sizing 3 §5.2 counts a size that can
        // be resolved from a `stretch` self-alignment as definite, so a percentage inside the item has
        // to resolve against the area it was stretched to rather than collapse to nothing.
        item.HeightIsDefinite =
            (HasHeight(item.Box) && !(style.Height.Kind == StyleLengthKind.Percent && areaHeight <= 0)) ||
            align is FlexAlignment.Stretch or FlexAlignment.Auto;

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
        LayoutContext context)
    {
        var space = Math.Max(0, area);
        Double value;

        if (HasWidth(box) && !(style.Width.Kind == StyleLengthKind.Percent && area <= 0))
        {
            var resolved = Math.Max(0, style.Width.Resolve(area));
            value = isBorderBox ? resolved : resolved + insets;
        }
        else if (align is FlexAlignment.Stretch or FlexAlignment.Auto)
        {
            value = space;
        }
        else
        {
            // The content intrinsic size sets the item's own declared width aside, which is exactly
            // what an anonymous wrapper needs: its declared width belongs to the container.
            value = Math.Min(Math.Max(0, _engine.MeasureContentIntrinsic(box, context).MaxContent), space);
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
        LayoutContext context)
    {
        var space = Math.Max(0, area);

        if (HasHeight(box) && !(style.Height.Kind == StyleLengthKind.Percent && area <= 0))
        {
            var resolved = Math.Max(0, style.Height.Resolve(area));
            var content = isBorderBox ? Math.Max(0, resolved - insets) : resolved;
            return BoxModel.ClampHeight(style, content, area, insets) + insets;
        }

        if (align is FlexAlignment.Stretch or FlexAlignment.Auto)
        {
            return space;
        }

        var measured = ContentHeight(box, space, basis, context) + insets;
        return Math.Min(measured, space);
    }

    private static Double Space(FlexAlignment align, Double free) => align switch
    {
        FlexAlignment.Center => Math.Max(0, free) / 2,
        FlexAlignment.End => Math.Max(0, free),
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
        var width = ResolveWidth(style, box, areaWidth, insetH, isBorderBox, justify, context);
        var outer = width + margin.Left + margin.Right;

        return (margin.Left + Space(justify, areaWidth - outer), width);
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
            FlexAlignment.End => (space, 0.0),
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

        /// <summary>The sum of the column base sizes before growth: the grid's min-content inline size.</summary>
        public Double MinWidth;

        /// <summary>Gets how far the column tracks reach, from the content box's start.</summary>
        public Double TracksWidth;

        /// <summary>Gets how far the row tracks reach, from the content box's start.</summary>
        public Double TracksHeight;

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
        public CrossPlacedItem(Box box, Int32 column, Int32 columnSpan, Int32 row, Double areaWidth)
        {
            Box = box;
            Column = column;
            ColumnSpan = columnSpan;
            Row = row;
            AreaWidth = areaWidth;
        }

        public Box Box { get; }

        public Int32 Column { get; }

        public Int32 ColumnSpan { get; }

        public Int32 Row { get; }

        public Double AreaWidth { get; }
    }
}

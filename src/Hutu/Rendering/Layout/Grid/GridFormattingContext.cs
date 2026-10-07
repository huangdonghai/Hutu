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

        var (_, contentWidth) = BoxModel.ResolveWidth(style, basisWidth, insetH);
        var declaredHeight = BoxModel.DeclaredHeight(style, basisHeight, insetV);

        var plan = Compute(box, contentWidth, declaredHeight, context, true);
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

        var plan = Compute(box, contentWidth, contentHeight, context, true);
        var itemContext = context.ForContainingBlock(new Size(contentWidth, contentHeight));

        // A positioned grid container is what an absolutely positioned child anchors to.
        if (PositionedLayout.IsPositioned(box))
        {
            itemContext = itemContext.WithAbsoluteContainingBlock(layout.PaddingBox);
        }

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
    /// Places the absolutely and fixed positioned children, which are not grid items. Their static
    /// position is the content box's start corner, since the grid placement does not apply to them.
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

    /// <summary>
    /// The intrinsic inline sizes. Sizing the tracks without a container size leaves every track as
    /// big as what it holds, so the sum is the max-content size. The min-content size is reported as
    /// the same number: narrowing a grid means letting its items wrap and re-running the track
    /// sizing, which the caller can do by measuring under a width constraint instead.
    /// </summary>
    public IntrinsicSize MeasureIntrinsic(Box box, LayoutContext context)
    {
        var padding = box.Style.Padding.Resolve(context.ContainingBlock.Width);
        var border = BlockLayout.BorderOf(box);
        var insets = padding.Horizontal + border.Horizontal;

        var plan = Compute(box, 0, null, context, false);
        var width = plan.ContentWidth + insets;

        return new IntrinsicSize(width, width);
    }

    /// <summary>Runs the whole algorithm and returns the content sizes plus the placed items.</summary>
    private GridPlan Compute(
        Box box,
        Double contentWidth,
        Double? contentHeight,
        LayoutContext context,
        Boolean definiteWidth)
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
            ResolveAxis(item, true, explicitColumns);
            ResolveAxis(item, false, explicitRows);
        }

        Place(items, grid, explicitColumns, explicitRows);
        var itemContext = context.ForContainingBlock(new Size(contentWidth, contentHeight ?? 0));

        // How far the items reach, which is what collapses an `auto-fit` repetition.
        var usedColumns = Used(items, true);
        var usedRows = Used(items, false);

        // Columns first. A row can only be sized once its items' final widths are known, because the
        // row's own size is whatever height the content needs at that width.
        var columnTracks = SizeColumns(grid, items, usedColumns, columnBasis, gapX, definiteWidth, itemContext);

        foreach (var item in items)
        {
            item.AreaWidth = Span(columnTracks, item.Column, item.ColumnSpan, gapX);
        }

        var rowTracks = SizeRows(grid, items, usedRows, rowBasis, gapY, contentHeight is not null, itemContext);

        var totalWidth = Total(columnTracks, gapX);
        var totalHeight = Total(rowTracks, gapY);
        var resolvedWidth = definiteWidth ? contentWidth : totalWidth;
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
        };

        plan.Items.AddRange(items);
        return plan;
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
    private Double ContentHeight(Box box, Double width, LayoutContext context)
    {
        var available = Math.Max(0, width);

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
        var style = box.Style;

        return new GridCell
        {
            Box = box,
            ColumnStart = GridStyle.Placement(style, "grid-column-start"),
            ColumnEnd = GridStyle.Placement(style, "grid-column-end"),
            RowStart = GridStyle.Placement(style, "grid-row-start"),
            RowEnd = GridStyle.Placement(style, "grid-row-end"),
            JustifySelf = FlexStyle.ReadAlignment(style, "justify-self", CssInitialValues.JustifySelfGrid),
            AlignSelf = FlexStyle.ReadAlignment(style, "align-self", CssInitialValues.AlignSelf),
        };
    }

    /// <summary>
    /// Resolves one axis of an item's placement into a track index and a span. A definite side wins;
    /// otherwise a <c>span</c> fixes only the reach and the index is left to the placement pass.
    /// </summary>
    private static void ResolveAxis(GridCell item, Boolean horizontal, Int32 explicitTracks)
    {
        var start = horizontal ? item.ColumnStart : item.RowStart;
        var end = horizontal ? item.ColumnEnd : item.RowEnd;
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
        LayoutContext context)
    {
        var tracks = Build(grid.Columns, grid.AutoColumn, used, basis, gap);

        Initialize(tracks, basis);
        ContributeWidths(tracks, items, context);
        Resolve(tracks, basis, gap, definite, grid.JustifyContent);

        return tracks;
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
            var minContribution = Math.Max(intrinsic.MinContent, minimum) + margin;
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
            var margin = style.Margin.Resolve(context.ContainingBlock.Width).Vertical;
            var padding = style.Padding.Resolve(context.ContainingBlock.Width);
            var insetV = padding.Vertical + BlockLayout.BorderOf(item.Box).Vertical;
            var declared = HasHeight(item.Box) ? BoxModel.DeclaredHeight(style, basis, insetV) : null;
            Double height;

            if (declared is { } fixedContent)
            {
                height = BoxModel.ClampHeight(style, fixedContent, basis, insetV) + insetV;
            }
            else
            {
                height = ContentHeight(item.Box, item.AreaWidth, context) + insetV;
            }

            item.MinHeight = height + margin;
            item.MaxHeight = item.MinHeight;
        }

        Contribute(tracks, items, false);
    }

    /// <summary>
    /// Raises the base sizes and growth limits of the tracks an item covers. For a single track that
    /// is a plain maximum; for a span the demand is shared out evenly, which is where the deficit
    /// against what the tracks already hold goes.
    /// </summary>
    private static void Contribute(List<Track> tracks, List<GridCell> items, Boolean horizontal)
    {
        foreach (var item in items)
        {
            var index = horizontal ? item.Column : item.Row;
            var span = horizontal ? item.ColumnSpan : item.RowSpan;
            var min = horizontal ? item.MinWidth : item.MinHeight;
            var max = horizontal ? item.MaxWidth : item.MaxHeight;
            var last = Math.Min(index + span, tracks.Count);

            if (last <= index)
            {
                continue;
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
                continue;
            }

            var held = 0.0;
            var limit = 0.0;

            for (var i = index; i < last; i++)
            {
                held += tracks[i].Base;
                limit += tracks[i].Growth;
            }

            if (min > held)
            {
                var share = (min - held) / count;

                for (var i = index; i < last; i++)
                {
                    tracks[i].Base += share;
                }
            }

            if (max > limit)
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
                tracks[i].ContentMin = Math.Max(tracks[i].ContentMin, min / count);
                tracks[i].ContentMax = Math.Max(tracks[i].ContentMax, max / count);
            }
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
        var basis = context.ContainingBlock.Width;
        var margin = style.Margin.Resolve(basis);
        var padding = style.Padding.Resolve(basis);
        var border = BlockLayout.BorderOf(item.Box);
        var insetH = padding.Horizontal + border.Horizontal;
        var insetV = padding.Vertical + border.Vertical;
        var isBorderBox = BoxModel.IsBorderBox(style);

        var justify = item.JustifySelf == FlexAlignment.Auto ? grid.JustifyItems : item.JustifySelf;
        var align = item.AlignSelf == FlexAlignment.Auto ? grid.AlignItems : item.AlignSelf;

        var width = ResolveWidth(style, item.Box, areaWidth, insetH, isBorderBox, justify, context);
        var height = ResolveHeight(style, item.Box, areaHeight, insetV, isBorderBox, align, context);

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

        var measured = ContentHeight(box, space, context) + insets;
        return Math.Min(measured, space);
    }

    private static Double Space(FlexAlignment align, Double free) => align switch
    {
        FlexAlignment.Center => Math.Max(0, free) / 2,
        FlexAlignment.End => Math.Max(0, free),
        _ => 0.0,
    };

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

        public List<GridCell> Items { get; } = new();
    }
}

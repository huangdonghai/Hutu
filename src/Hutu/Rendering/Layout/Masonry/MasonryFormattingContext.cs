using Hutu.Rendering.Boxes;
using System;
using System.Collections.Generic;

namespace Hutu.Rendering.Layout;

/// <summary>
/// The grid-lanes (masonry) formatting context: the cross axis keeps its declared tracks (the
/// <c>grid-template-columns</c> of a column-lane grid) while the lane axis grows as items are stacked,
/// each item dropping into the shortest run of lanes it fits. This is the milestone-one core - a fixed
/// cross track set, default (row-lane) direction, gaps, and column spans - and deliberately leaves the
/// reverse/fill packing, baseline, and subgrid to later passes.
/// </summary>
/// <remarks>
/// The cross tracks are settled by the grid itself: <see cref="GridFormattingContext.LayoutCrossTracks"/>
/// sizes the columns and places each item into one exactly as a grid would, since a grid-lanes cross
/// axis is an ordinary grid axis. Only the lane axis - the rungs down each column - is packed here,
/// collapsing each column's items independently instead of aligning them to shared rows.
/// </remarks>
internal sealed class MasonryFormattingContext
{
    private readonly LayoutEngine _engine;
    private readonly GridFormattingContext _grid;

    public MasonryFormattingContext(LayoutEngine engine, GridFormattingContext grid)
    {
        _engine = engine;
        _grid = grid;
    }

    public Size Measure(Box box, MeasureConstraints constraints, LayoutContext context)
    {
        var style = box.Style;
        var basisWidth = constraints.AvailableWidth ?? context.ContainingBlock.Width;
        var basisHeight = constraints.AvailableHeight ?? context.ContainingBlock.Height;
        var inset = Insets(box, basisWidth);

        var (_, contentWidth) = BoxModel.ResolveWidth(style, basisWidth, context.ContainingBlock.Width, inset.Horizontal);
        var declaredHeight = BoxModel.DeclaredHeight(style, basisHeight, inset.Vertical);

        var plan = Compute(box, contentWidth, true, context);
        var height = declaredHeight ?? plan.TotalHeight;

        return new Size(
            BoxModel.ClampWidth(style, plan.TotalWidth + inset.Horizontal, basisWidth, inset.Horizontal),
            BoxModel.ClampHeight(style, height, basisHeight, inset.Vertical) + inset.Vertical);
    }

    public IntrinsicSize MeasureIntrinsic(Box box, LayoutContext context)
    {
        var style = box.Style;
        var inset = Insets(box, context.ContainingBlock.Width);

        // Without a container width the cross tracks take the width of what they hold; measuring at zero
        // available width lets each item report its min-content and the sum is the container's own.
        var plan = Compute(box, 0, false, context);
        var width = plan.TotalWidth + inset.Horizontal;

        return new IntrinsicSize(width, width);
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
        var contentX = borderBox.X + border.Left + padding.Left;
        var contentY = borderBox.Y + border.Top + padding.Top;

        var plan = Compute(box, contentWidth, true, context);
        var itemContext = context.ForContainingBlock(new Size(contentWidth, borderBox.Height - insetV));

        if (PositionedLayout.IsPositioned(box))
        {
            itemContext = itemContext.WithAbsoluteContainingBlock(layout.PaddingBox);
        }

        foreach (var item in plan.Items)
        {
            var rect = new Rect(contentX + item.X, contentY + item.Y, item.Width, item.Height);
            layout.Children.Add(_engine.Arrange(item.Box, rect, itemContext));
        }

        // Absolutely and fixed positioned children are not lane items: they anchor to the container and
        // take the content box's start corner as their static position, exactly as in a grid. Leaving them
        // out drops them from the picture altogether.
        ArrangeOutOfFlow(box, layout, contentX, contentY, itemContext);

        return layout;
    }

    /// <summary>
    /// Places the out-of-flow children, which lane packing skips. Their static position is the content
    /// box's start corner; the engine resolves offsets and sizes against the absolute containing block.
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

    /// <summary>One placed lane item, in the container's content coordinates.</summary>
    private sealed record PlacedItem(Box Box, Double X, Double Y, Double Width, Double Height);

    /// <summary>The result of packing the items: their places, and the content size the pack came to.</summary>
    private sealed class LanePlan
    {
        public List<PlacedItem> Items = new();
        public Double TotalWidth;
        public Double TotalHeight;
    }

    /// <summary>
    /// Packs every in-flow item down its column. The cross (column) axis is settled by the grid - the
    /// track count, the widths, and which column each item belongs to are whatever an ordinary grid
    /// would come to - and only the lane axis differs: each item drops just below the deepest floor among
    /// the columns it spans, so a column that has held nothing takes no gap above it.
    /// </summary>
    private LanePlan Compute(Box box, Double contentWidth, Boolean definiteWidth, LayoutContext context)
    {
        var plan = new LanePlan();
        var cross = _grid.LayoutCrossTracks(box, contentWidth, definiteWidth, context);
        var grid = box.Style.Grid;
        var columnWidths = cross.ColumnWidths;
        var trackCount = columnWidths.Count;

        if (trackCount == 0)
        {
            plan.TotalWidth = contentWidth;
            return plan;
        }

        var gapX = cross.GapX;
        var gapY = cross.GapY;

        // The x of each column's start, and a running floor for how far down it has been filled.
        var laneX = new Double[trackCount];
        var floor = new Double[trackCount];
        var cursor = 0.0;

        for (var c = 0; c < trackCount; c++)
        {
            laneX[c] = cursor;
            cursor += columnWidths[c] + gapX;
        }

        var itemContext = context.ForContainingBlock(new Size(contentWidth, 0));

        // Each column is packed top to bottom in the order the grid placed its items: an item's row is
        // its rung, so ordering by row stacks a column's items in their flow while leaving items that
        // share a row in the order they were written.
        var ordered = new List<GridFormattingContext.CrossPlacedItem>(cross.Items);
        ordered.Sort(CompareByRow);

        foreach (var item in ordered)
        {
            var start = Math.Clamp(item.Column, 0, trackCount - 1);
            var span = item.ColumnSpan;

            if (start + span > trackCount)
            {
                span = Math.Max(1, trackCount - start);
            }

            var areaWidth = item.AreaWidth;

            // The cross (inline) axis is an ordinary grid axis: a definite-width item sits wherever
            // justify-self / justify-items puts it inside its lane track, and only stretch fills the track.
            var (offset, width) = _grid.AlignInline(item.Box, areaWidth, grid.JustifyItems, itemContext);
            var size = _engine.Measure(item.Box, MeasureConstraints.FitWidth(width), itemContext);
            var height = size.Height;

            // The item drops just below the deepest floor among the columns it covers.
            var y = 0.0;

            for (var c = start; c < start + span; c++)
            {
                var top = floor[c] > 0 ? floor[c] + gapY : 0;
                y = Math.Max(y, top);
            }

            for (var c = start; c < start + span; c++)
            {
                floor[c] = y + height;
            }

            plan.Items.Add(new PlacedItem(item.Box, laneX[start] + offset, y, width, height));
        }

        for (var c = 0; c < trackCount; c++)
        {
            plan.TotalHeight = Math.Max(plan.TotalHeight, floor[c]);
        }

        plan.TotalWidth = definiteWidth
            ? contentWidth
            : laneX[trackCount - 1] + columnWidths[trackCount - 1];

        return plan;
    }

    /// <summary>Orders two cross-placed items by their grid row, then by the column they land in.</summary>
    private static Int32 CompareByRow(GridFormattingContext.CrossPlacedItem a, GridFormattingContext.CrossPlacedItem b)
    {
        if (a.Row != b.Row)
        {
            return a.Row.CompareTo(b.Row);
        }

        return a.Column.CompareTo(b.Column);
    }

    private static (Double Horizontal, Double Vertical) Insets(Box box, Double basis)
    {
        var padding = box.Style.Padding.Resolve(basis);
        var border = BlockLayout.BorderOf(box);

        return (padding.Horizontal + border.Horizontal, padding.Vertical + border.Vertical);
    }
}

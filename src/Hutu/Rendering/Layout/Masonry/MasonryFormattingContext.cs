using Hutu.Rendering.Boxes;
using System;
using System.Collections.Generic;

namespace Hutu.Rendering.Layout;

/// <summary>
/// The grid-lanes (masonry) formatting context. A column-lane grid packs each item down the column the
/// grid gave it, the lane axis growing as the items stack; a row-lane grid's lanes are its declared rows
/// - real tracks the items sit in, spanning the lanes their span covers. Both leave the reverse/fill
/// packing, baseline, and subgrid to later passes.
/// </summary>
/// <remarks>
/// The cross tracks are settled by the grid itself: <see cref="GridFormattingContext.LayoutCrossTracks"/>
/// sizes the columns and places each item into one exactly as a grid would, since a grid-lanes cross
/// axis is an ordinary grid axis. The lane axis is what differs from a grid: a column-lane grid packs
/// each column's items independently, and a row-lane grid stands each item in the lane its row names.
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

        var (_, contentWidth) = _engine.ContentSizedWidth(box, basisWidth, context) is { } contentSized
            ? (contentSized, Math.Max(0, contentSized - inset.Horizontal))
            : BoxModel.ResolveWidth(style, basisWidth, context.ContainingBlock.Width, inset.Horizontal);
        var declaredHeight = BoxModel.DeclaredHeight(style, basisHeight, inset.Vertical);

        var plan = Compute(box, contentWidth, true, context);
        var height = declaredHeight ?? plan.TotalHeight;

        // The used width is the one the page sized: a content-sized width is the measurement it asked
        // for, a stated one its own size, and only an auto width takes the lanes' packed extent.
        var used = _engine.ContentSizedWidth(box, basisWidth, context)
            ?? (BoxModel.DeclaredWidth(style, context.ContainingBlock.Width, inset.Horizontal) is { } stated
                ? (Double?)stated + inset.Horizontal
                : null)
            ?? plan.TotalWidth + inset.Horizontal;

        return new Size(
            BoxModel.ClampWidth(style, used, basisWidth, inset.Horizontal),
            BoxModel.ClampHeight(style, height, basisHeight, inset.Vertical) + inset.Vertical);
    }

    public IntrinsicSize MeasureIntrinsic(Box box, LayoutContext context)
    {
        var style = box.Style;
        var inset = Insets(box, context.ContainingBlock.Width);

        // A width the page states is the intrinsic one: min-content and max-content are both the stated
        // size, whatever the packed lanes would have asked for (CSS Sizing 3 §5.1).
        if (BoxModel.DeclaredWidth(style, context.ContainingBlock.Width, inset.Horizontal) is { } stated)
        {
            return new IntrinsicSize(stated + inset.Horizontal, stated + inset.Horizontal);
        }

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

        itemContext = PositionedLayout.ForDescendants(itemContext, box, layout.PaddingBox);

        foreach (var item in plan.Items)
        {
            var rect = new Rect(contentX + item.X, contentY + item.Y, item.Width, item.Height);
            layout.Children.Add(_engine.Arrange(item.Box, rect, itemContext));
        }

        // Absolutely and fixed positioned children are not lane items: they anchor to the container and
        // take the content box's start corner as their static position, exactly as in a grid. Leaving them
        // out drops them from the picture altogether.
        ArrangeOutOfFlow(box, layout, contentX, contentY, itemContext);

        var gapRules = box.Style.Gap;

        if (gapRules.IsStated)
        {
            layout.GapDecorations = LaneDecorations(plan, gapRules, plan.RowLanes);
        }

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

    /// <summary>
    /// One placed lane item, in the container's content coordinates: where it stands, how big it is, and
    /// the lanes and columns its declared area covers.
    /// </summary>
    private sealed record PlacedItem(
        Box Box,
        Double X,
        Double Y,
        Double Width,
        Double Height,
        Int32 Lane,
        Int32 LaneSpan,
        Int32 Column,
        Int32 ColumnSpan);

    /// <summary>The result of packing the items: their places, and the content size the pack came to.</summary>
    private sealed class LanePlan
    {
        public List<PlacedItem> Items = new();
        public Double TotalWidth;
        public Double TotalHeight;
        public GridFormattingContext.CrossAxisLayout Cross = new();

        /// <summary>The lane axis's tracks: the top and the height of each lane, in lane order.</summary>
        public List<(Double Top, Double Height)> Lanes = new();

        /// <summary>Whether the lanes run in rows (and the cross axis down the columns).</summary>
        public Boolean RowLanes;
    }

    /// <summary>
    /// Packs every in-flow item. The cross (column) axis is settled by the grid - the track count, the
    /// widths, and which column each item belongs to are whatever an ordinary grid would come to - and
    /// only the lane axis differs: a column-lane grid packs each item down the column the grid gave it,
    /// and a row-lane grid stands each item in the lane its declared row names.
    /// </summary>
    private LanePlan Compute(Box box, Double contentWidth, Boolean definiteWidth, LayoutContext context)
    {
        var plan = new LanePlan();
        var cross = plan.Cross = _grid.LayoutCrossTracks(box, contentWidth, definiteWidth, context);
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

        // The direction the lanes run in: the declared value, or - where the page declared lane tracks of
        // its own - the axis those tracks sit on. A row-lane grid's lanes are its declared rows - real
        // tracks the items sit in - while a column-lane grid packs each item down the column the grid
        // gave it.
        var declared = box.Style.LanesDirection;
        var rowLanes = plan.RowLanes = declared is { } direction
            ? String.Equals(direction, "row", StringComparison.OrdinalIgnoreCase)
            : LaneTracksDefinite(grid, gapY);

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

        if (rowLanes)
        {
            PackRows(plan, cross, grid, gapY, itemContext);
            return plan;
        }

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

            plan.Items.Add(new PlacedItem(item.Box, laneX[start] + offset, y, width, height, start, span, start, span));
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

    /// <summary>Gets whether every lane track the container declares is a definite size.</summary>
    private static Boolean LaneTracksDefinite(GridStyle grid, Double gapY)
    {
        var count = grid.Rows.ExplicitCount(0, gapY);

        if (count == 0)
        {
            return false;
        }

        foreach (var size in grid.Rows.Resolve(0, gapY, count))
        {
            if (size.Max.Kind != GridBreadthKind.Length)
            {
                return false;
            }
        }

        return true;
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

    /// <summary>
    /// Packs a row-lane grid: the lanes are the container's declared rows - real tracks of the height
    /// their sizing states, or of the tallest item they hold when that is auto - and each item stands at
    /// the top of the lane its declared row names, spanning the lanes its span covers. The container's
    /// height is the lanes' own extent (CSS Grid Lanes §5).
    /// </summary>
    private void PackRows(LanePlan plan, GridFormattingContext.CrossAxisLayout cross, GridStyle grid, Double gapY, LayoutContext itemContext)
    {
        var trackCount = cross.ColumnWidths.Count;

        var ordered = new List<GridFormattingContext.CrossPlacedItem>(cross.Items);
        ordered.Sort(CompareByRow);

        var laneCount = grid.Rows.ExplicitCount(0, gapY);

        foreach (var item in ordered)
        {
            laneCount = Math.Max(laneCount, item.Row + item.RowSpan);
        }

        var resolved = new List<GridTrackSize>(grid.Rows.Resolve(0, gapY, laneCount));

        while (resolved.Count < laneCount)
        {
            resolved.Add(GridTrackSize.Auto);
        }

        // Each item is measured at its area width before the lanes are sized: an auto lane takes the
        // tallest item whose declared row names it. The inline axis packs each lane on its own: an item
        // stands where the lane's cursor puts it, the cursor crossing its own width and the cross gap -
        // a row-lane grid's lanes flow like lines, not like shared tracks (CSS Grid Lanes).
        var laneCursor = new Double[laneCount];
        var measured = new List<(GridFormattingContext.CrossPlacedItem Item, Double Height)>();
        var placements = new Dictionary<Box, (Double X, Double Y, Double Width, Double Height, Int32 Lane, Int32 LaneSpan, Int32 Column, Int32 ColumnSpan)>();

        foreach (var item in ordered)
        {
            var start = Math.Clamp(item.Column, 0, trackCount - 1);
            var span = item.ColumnSpan;

            if (start + span > trackCount)
            {
                span = Math.Max(1, trackCount - start);
            }

            var (_, width) = _grid.AlignInline(item.Box, item.AreaWidth, grid.JustifyItems, itemContext);
            var size = _engine.Measure(item.Box, MeasureConstraints.FitWidth(width), itemContext);

            measured.Add((item, size.Height));

            var lane = Math.Clamp(item.Row, 0, laneCount - 1);
            placements[item.Box] = (laneCursor[lane], 0, width, size.Height, item.Row, item.RowSpan, start, span);
            laneCursor[lane] += width + cross.GapX;
        }

        // Each lane's height: the definite size its sizing states, or the tallest item it holds.
        var laneHeights = new Double[laneCount];
        var laneDefinite = new Boolean[laneCount];

        for (var lane = 0; lane < laneCount && lane < resolved.Count; lane++)
        {
            if (resolved[lane].Max.Kind == GridBreadthKind.Length)
            {
                laneHeights[lane] = resolved[lane].Max.Value;
                laneDefinite[lane] = true;
            }
        }

        foreach (var (item, height) in measured)
        {
            var first = Math.Clamp(item.Row, 0, laneCount - 1);
            var last = Math.Clamp(item.Row + item.RowSpan - 1, 0, laneCount - 1);

            for (var lane = first; lane <= last; lane++)
            {
                if (!laneDefinite[lane])
                {
                    laneHeights[lane] = Math.Max(laneHeights[lane], height);
                }
            }
        }

        // The items stand at the tops of the first lanes their rows name.
        var top = 0.0;

        for (var lane = 0; lane < laneCount; lane++)
        {
            var laneTop = top;
            plan.Lanes.Add((laneTop, laneHeights[lane]));
            top += laneHeights[lane] + gapY;

            foreach (var (item, height) in measured)
            {
                if (item.Row == lane && placements.TryGetValue(item.Box, out var spot))
                {
                    placements[item.Box] = (spot.X, laneTop, spot.Width, spot.Height, spot.Lane, spot.LaneSpan, spot.Column, spot.ColumnSpan);
                }
            }
        }

        foreach (var (item, spot) in placements)
        {
            plan.Items.Add(new PlacedItem(item, spot.X, spot.Y, spot.Width, spot.Height, spot.Lane, spot.LaneSpan, spot.Column, spot.ColumnSpan));
        }

        plan.TotalHeight = Math.Max(0, top - gapY);

        var extent = 0.0;

        foreach (var cursor in laneCursor)
        {
            extent = Math.Max(extent, cursor - cross.GapX);
        }

        plan.TotalWidth = extent;
    }

    /// <summary>
    /// Appends the decorations of the grid-lanes gaps. The lane axis's rules stand centred in each gap
    /// between two consecutive lane tracks - every gap for <c>all</c> and <c>normal</c>, and, for
    /// <c>between</c> and <c>around</c>, only where the items whose areas cover the lanes on each side
    /// satisfy the visibility, a gap a spanning item crosses being interior to it (CSS Gaps 1 §3.4). The
    /// cross axis's rules stand in the gaps between consecutive items across a lane: a column-lane grid
    /// cuts them by the union of the lanes' own item gaps merged into bands, the ends drawn in by the
    /// caps the rule states, the percentages resolving against the band they inset into (CSS Gaps 1
    /// §3.3); a row-lane grid runs each across a lane whose flanking items satisfy the visibility.
    /// </summary>
    private static List<GapRuleSegment>? LaneDecorations(LanePlan plan, GapRules rules, Boolean rowLanes)
    {
        var rowAxis = rules.Row;
        var columnAxis = rules.Column;

        if (!rowAxis.IsStated && !columnAxis.IsStated)
        {
            return null;
        }

        var segments = new List<GapRuleSegment>();

        if (rowLanes)
        {
            for (var lane = 0; lane + 1 < plan.Lanes.Count; lane++)
            {
                var top = plan.Lanes[lane].Top + plan.Lanes[lane].Height;
                var bottom = plan.Lanes[lane + 1].Top;

                if (bottom - top <= 0.01)
                {
                    continue;
                }

                var rule = rowAxis.At(lane, plan.Lanes.Count - 1);

                if (!rule.Appears)
                {
                    continue;
                }

                if (rowAxis.Break == GapRuleBreak.Intersection)
                {
                    // The junctions where either flanking lane's own item gap, or a spanning item's
                    // crossing, abuts the row gap: each is an end and a start segment endpoint, and the
                    // segments pair between them (CSS Gaps 1 §3.1.1-3.1.2). No junction of a lane gap is
                    // ever flanked - the lane's own absence is what made it - so no two segments merge.
                    var junctions = new List<(Double From, Double To)>();

                    foreach (var acrossLane in new[] { lane, lane + 1 })
                    {
                        var across = ItemsAcross(plan, acrossLane);

                        for (var k = 0; k + 1 < across.Count; k++)
                        {
                            if (across[k + 1].X - (across[k].X + across[k].Width) > 0.01)
                            {
                                junctions.Add((across[k].X + across[k].Width, across[k + 1].X));
                            }
                        }
                    }

                    foreach (var crossing in plan.Items)
                    {
                        if (crossing.Lane <= lane && lane + 1 < crossing.Lane + crossing.LaneSpan)
                        {
                            junctions.Add((crossing.X, crossing.X + crossing.Width));
                        }
                    }

                    foreach (var (from, to) in ComplementOf(JunctionBands(junctions), plan.TotalWidth))
                    {
                        DrawLaneRuleSegment(segments, plan, rule, rowAxis, lane, top, bottom, from, to);
                    }

                    continue;
                }

                // A none break runs through the junctions: the rule is one stretch from bound to
                // bound - save where a spanning item crosses, which is interior to it; and the rule may
                // not end inside a junction, so the crossing's stretch is widened to the item gaps that
                // abut it in the lanes it covers (CSS Gaps 1 §3.1.1, §3.4).
                var crossings = new List<(Double From, Double To)>();

                foreach (var crossing in plan.Items)
                {
                    if (!(crossing.Lane <= lane && lane + 1 < crossing.Lane + crossing.LaneSpan))
                    {
                        continue;
                    }

                    var from = crossing.X;
                    var to = crossing.X + crossing.Width;

                    for (var covered = Math.Max(crossing.Lane, lane); covered <= Math.Min(crossing.Lane + crossing.LaneSpan - 1, lane + 1); covered++)
                    {
                        var across = ItemsAcross(plan, covered);

                        for (var k = 0; k < across.Count; k++)
                        {
                            if (!ReferenceEquals(across[k], crossing))
                            {
                                continue;
                            }

                            if (k > 0)
                            {
                                from = Math.Min(from, across[k - 1].X + across[k - 1].Width);
                            }

                            if (k + 1 < across.Count)
                            {
                                to = Math.Max(to, across[k + 1].X);
                            }
                        }
                    }

                    crossings.Add((from, to));
                }

                foreach (var (from, to) in ComplementOf(JunctionBands(crossings), plan.TotalWidth))
                {
                    var draw = rowAxis.Visibility switch
                    {
                        GapRuleVisibility.Between => LaneHolds(plan, lane, from, to) && LaneHolds(plan, lane + 1, from, to),
                        GapRuleVisibility.Around => LaneHolds(plan, lane, from, to) || LaneHolds(plan, lane + 1, from, to),
                        _ => true,
                    };

                    if (draw)
                    {
                        segments.Add(new GapRuleSegment(
                            GapRuleBands.Band(rule, false, (top + bottom) / 2, from, to),
                            rule,
                            isRow: true));
                    }
                }
            }

            // The cross axis's rules: per lane, in the gaps between the consecutive items across it, each
            // running the height of the lane it flanks.
            for (var lane = 0; lane < plan.Lanes.Count; lane++)
            {
                var across = ItemsAcross(plan, lane);

                for (var k = 0; k + 1 < across.Count; k++)
                {
                    var from = across[k].X + across[k].Width;
                    var to = across[k + 1].X;
                    var rule = columnAxis.At(k, across.Count - 1);

                    if (!rule.Appears || to - from <= 0.01)
                    {
                        continue;
                    }

                    segments.Add(new GapRuleSegment(
                        GapRuleBands.Band(rule, true, (from + to) / 2, plan.Lanes[lane].Top, plan.Lanes[lane].Top + plan.Lanes[lane].Height),
                        rule,
                        isRow: false));
                }
            }

            return segments.Count > 0 ? segments : null;
        }

        // A column-lane grid: the lanes run down the page, so the column rules stand in the cross gaps
        // between the lanes, cut by the union of the lanes' own item gaps merged into bands, the ends
        // drawn in by the caps the rule states.
        if (columnAxis.IsStated)
        {
            var laneGaps = new List<(Double From, Double To)>();

            for (var lane = 0; lane < plan.Cross.ColumnWidths.Count; lane++)
            {
                var across = ItemsAcross(plan, lane);

                for (var k = 0; k + 1 < across.Count; k++)
                {
                    var gapFrom = across[k].Y + across[k].Height;
                    var gapTo = across[k + 1].Y;

                    if (gapTo - gapFrom > 0.01)
                    {
                        laneGaps.Add((gapFrom, gapTo));
                    }
                }
            }

            laneGaps.Sort((a, b) => a.From.CompareTo(b.From));

            var merged = new List<(Double From, Double To)>();

            foreach (var gap in laneGaps)
            {
                if (merged.Count > 0 && gap.From <= merged[^1].To + 0.01)
                {
                    merged[^1] = (merged[^1].From, Math.Max(merged[^1].To, gap.To));
                }
                else
                {
                    merged.Add(gap);
                }
            }

            var columnX = 0.0;
            var band = -1.0;

            for (var column = 0; column + 1 < plan.Cross.ColumnWidths.Count; column++)
            {
                var gapMid = columnX + plan.Cross.ColumnWidths[column] + plan.Cross.GapX / 2;
                var rule = columnAxis.At(column, plan.Cross.ColumnWidths.Count - 1);

                if (rule.Appears)
                {
                    foreach (var (cutFrom, cutTo) in merged)
                    {
                        var before = cutFrom - band;
                        var crossing = cutTo - cutFrom;

                        if (before > 0.01)
                        {
                            var start = band;
                            var end = cutFrom - columnAxis.CapEnd.Resolve(crossing, 0);

                            if (end > start)
                            {
                                segments.Add(new GapRuleSegment(
                                    GapRuleBands.Band(rule, true, gapMid, start, end),
                                    rule,
                                    isRow: false));
                            }
                        }

                        band = cutTo;
                    }

                    var tail = plan.TotalHeight - band;
                    var tailCrossing = merged[^1].To - merged[^1].From;

                    if (tail > 0.01)
                    {
                        var start = band + columnAxis.CapStart.Resolve(tailCrossing, 0);
                        var end = plan.TotalHeight;

                        if (end > start)
                        {
                            segments.Add(new GapRuleSegment(
                                GapRuleBands.Band(rule, true, gapMid, start, end),
                                rule,
                                isRow: false));
                        }
                    }

                    band = -1;
                }

                columnX += plan.Cross.ColumnWidths[column] + plan.Cross.GapX;
            }
        }

        return segments.Count > 0 ? segments : null;
    }

    /// <summary>Gets the items placed in <paramref name="lane"/>, ordered across it.</summary>
    private static List<PlacedItem> ItemsAcross(LanePlan plan, Int32 lane)
    {
        var across = new List<PlacedItem>();

        foreach (var item in plan.Items)
        {
            if (item.Lane == lane)
            {
                across.Add(item);
            }
        }

        across.Sort((a, b) => a.X.CompareTo(b.X));

        return across;
    }

    /// <summary>
    /// Emits one stretch of a lane gap's rule the break left whole: the visibility is asked of the items
    /// of the lanes on either side over the stretch, an item standing on each side for <c>between</c>, on
    /// either for <c>around</c> (CSS Gaps 1 §3.4).
    /// </summary>
    private static void DrawLaneRuleSegment(
        List<GapRuleSegment> segments,
        LanePlan plan,
        GapRule rule,
        GapRuleAxis rowAxis,
        Int32 lane,
        Double top,
        Double bottom,
        Double from,
        Double to)
    {
        var shown = rowAxis.Visibility switch
        {
            GapRuleVisibility.Between => LaneHolds(plan, lane, from, to) && LaneHolds(plan, lane + 1, from, to),
            GapRuleVisibility.Around => LaneHolds(plan, lane, from, to) || LaneHolds(plan, lane + 1, from, to),
            _ => true,
        };

        if (shown)
        {
            segments.Add(new GapRuleSegment(
                GapRuleBands.Band(rule, false, (top + bottom) / 2, from, to),
                rule,
                isRow: true));
        }
    }

    /// <summary>
    /// Gets whether the lane's area counts as occupied over the stretch: an item covering it, an edge
    /// touching the stretch being as good as overlapping it - the stretch an item bounds is one it
    /// stands beside (CSS Gaps 1 §3.4).
    /// </summary>
    private static Boolean LaneHolds(LanePlan plan, Int32 lane, Double from, Double to)
    {
        foreach (var item in plan.Items)
        {
            if (item.Lane <= lane && lane < item.Lane + item.LaneSpan && item.X <= to + 0.01 && item.X + item.Width >= from - 0.01)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Gets whether a spanning item crosses the gap over the stretch, leaving it interior to itself.</summary>
    private static Boolean LaneCrossed(LanePlan plan, Int32 lane, Double from, Double to)
    {
        foreach (var item in plan.Items)
        {
            if (item.Lane <= lane && lane + 1 < item.Lane + item.LaneSpan && item.X < to && item.X + item.Width > from)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Merges stretches that touch or overlap into the bands the junctions take.</summary>
    private static List<(Double From, Double To)> JunctionBands(List<(Double From, Double To)> junctions)
    {
        junctions.Sort((a, b) => a.From.CompareTo(b.From));

        var bands = new List<(Double From, Double To)>();

        foreach (var junction in junctions)
        {
            if (bands.Count > 0 && junction.From <= bands[^1].To + 0.01)
            {
                bands[^1] = (bands[^1].From, Math.Max(bands[^1].To, junction.To));
            }
            else
            {
                bands.Add(junction);
            }
        }

        return bands;
    }

    /// <summary>Walks the stretches of [0, <paramref name="width"/>] the bands leave open.</summary>
    private static IEnumerable<(Double From, Double To)> ComplementOf(List<(Double From, Double To)> bands, Double width)
    {
        var cut = 0.0;

        foreach (var (from, to) in bands)
        {
            if (from - cut > 0.01)
            {
                yield return (cut, from);
            }

            cut = Math.Max(cut, to);
        }

        if (width - cut > 0.01)
        {
            yield return (cut, width);
        }
    }

    private static (Double Horizontal, Double Vertical) Insets(Box box, Double basis)
    {
        var padding = box.Style.Padding.Resolve(basis);
        var border = BlockLayout.BorderOf(box);

        return (padding.Horizontal + border.Horizontal, padding.Vertical + border.Vertical);
    }

    /// <summary>Gets the item whose area covers the lane and column, or null.</summary>
    private static PlacedItem? ItemCovering(LanePlan plan, Int32 lane, Int32 column)
    {
        foreach (var item in plan.Items)
        {
            if (lane >= item.Lane && lane < item.Lane + item.LaneSpan
                && column >= item.Column && column < item.Column + item.ColumnSpan)
            {
                return item;
            }
        }

        return null;
    }
}

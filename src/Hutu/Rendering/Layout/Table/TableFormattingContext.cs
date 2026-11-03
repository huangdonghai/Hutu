using Hutu.Rendering.Boxes;
using System;
using System.Collections.Generic;
using AngleSharp.Css.Dom;

namespace Hutu.Rendering.Layout;

/// <summary>
/// The table formatting context: it turns the table, row group, row and cell boxes into the grid the
/// table algorithms work on, sizes the columns and rows, and places every cell.
/// </summary>
/// <remarks>
/// The context is the only place that knows the difference between the two border models. In the
/// separated model a cell owns its whole border and the grid leaves <c>border-spacing</c> between the
/// cells; in the collapsing model the borders are resolved per grid line and each cell paints half of
/// it, so that two adjacent cells combine into a single line. The rest of the engine only sees the
/// used border the context settled on.
/// <para>
/// The grid is rebuilt by both phases rather than shared, because measuring is cached and arranging
/// must not depend on which measure call happened to run first. The work that is genuinely expensive -
/// text measurement - is therefore done twice per cell, which the layout is small enough to absorb.
/// </para>
/// </remarks>
internal sealed partial class TableFormattingContext
{
    private const Double Epsilon = 0.0001;

    private readonly LayoutEngine _engine;

    public TableFormattingContext(LayoutEngine engine)
    {
        _engine = engine;
    }

    /// <summary>
    /// Measures the table, returning the size of its border box. The columns are shared out from the
    /// width the table is allowed and the rows are measured from the cells, so the height is known
    /// before anything is placed.
    /// </summary>
    public Size Measure(Box box, MeasureConstraints constraints, LayoutContext context)
    {
        var style = box.Style;
        var basisWidth = constraints.AvailableWidth ?? context.ContainingBlock.Width;
        var basisHeight = constraints.AvailableHeight ?? context.ContainingBlock.Height;
        var build = Build(box, basisWidth, context);
        var vertical = build.Flow.IsVertical;

        // The grid shares its columns out along the table's inline axis and stacks its rows along its
        // block axis, and which physical extent each of those is turns with the writing mode: a table
        // that reads down the page takes its `height` for the room its columns have and its `width` for
        // the band its rows stack across (CSS Writing Modes 3 §9.1).
        var availableInline = vertical ? basisHeight : basisWidth;
        var availableBlock = vertical ? basisWidth : basisHeight;

        var spacingTotal = InlineSpacingTotal(build);
        var minContent = build.Grid.SumMinimum() + spacingTotal;
        var maxContent = build.Grid.SumMaximum() + spacingTotal;

        var contentInline = ResolveContentInline(
            build,
            vertical ? constraints.AvailableHeight : constraints.AvailableWidth,
            availableInline,
            minContent,
            maxContent);

        // The captions are measured first because the size the table declared along the row axis covers
        // them too; what is left of it once they have taken their share is the size the rows have to fill.
        MeasureCaptions(build, contentInline, context);

        // The children that are not table parts are blocks of the table's own content, and they are
        // measured against the width the table settled on.
        MeasureOthers(build, contentInline, context);

        var declared = DeclaredBlockExtent(build, availableBlock);
        var gridBlock = declared is { } stated
            ? stated - build.CaptionTop - build.CaptionBottom
            : (Double?)null;

        CompleteGrid(build, contentInline, context, gridBlock);

        var othersBlock = build.OtherHeights.Count > 0
            ? build.OtherHeights.Sum()
            : 0.0;

        var contentBlock = build.Grid.Height + build.CaptionTop + build.CaptionBottom + othersBlock;

        // A declared size is a floor, as CSS 2.1 §17.5.3 has it: a table asked to be smaller than its
        // rows need is as large as the rows make it. The inline axis answers the same way, one step
        // further up: a table asked to be narrower than its columns can go is as wide as the columns
        // make it. `ClampWidth` bounds a border box and `ClampHeight` a content box, which is the pair
        // of physical properties each axis is read from.
        var usedBlock = vertical
            ? BoxModel.ClampWidth(
                style,
                Math.Max(declared ?? 0, contentBlock) + build.InsetsBlock,
                availableBlock,
                build.InsetsBlock)
            : BoxModel.ClampHeight(
                style,
                Math.Max(declared ?? 0, contentBlock),
                availableBlock,
                build.InsetsBlock) + build.InsetsBlock;
        var usedInline = Math.Max(contentInline, build.Grid.Width) + build.InsetsInline;

        return build.Flow.SizeOf(usedInline, usedBlock);
    }

    /// <summary>
    /// The intrinsic inline sizes of the table: the sum of the column minimums and of the column
    /// maximums. A flex item or a grid track that sizes itself from a table asks for this.
    /// </summary>
    public IntrinsicSize MeasureIntrinsic(Box box, LayoutContext context)
    {
        var basis = context.ContainingBlock.Width;
        var build = Build(box, basis, context);
        var spacingTotal = InlineSpacingTotal(build);
        var vertical = build.Flow.IsVertical;
        var min = build.Grid.SumMinimum() + spacingTotal + build.InsetsInline;
        var max = build.Grid.SumMaximum() + spacingTotal + build.InsetsInline;

        // A size the author stated along the axis the columns run is part of what the table brings to a
        // box that shrinks to fit, as the preferred size it would rather be: a table with no cells is
        // still as large as it was told (CSS 2.1
        // §17.5.2.2, and §17.5.3 under the collapsed model, where the table keeps no padding of its own to
        // make the difference up). Asked by the columns alone such a table is nothing, and the float holding
        // one collapses with it - which is what wpt's collapsing-border-model-013 and -014 hold to.
        //
        // Only the preferred size is raised, never the narrowest one: the automatic minimum of an item is
        // read off this pair (CSS Flexbox 1 §4.5, §9.9.1) and a stated width is not a min-width there
        // (wpt's table-as-item-specified-width holds to exactly that). And a percentage is left out, since
        // the width it would resolve against is the very width being asked about - CSS Tables 3 §4 puts
        // such a table at its preferred width instead, which the columns already answer.
        var stated = vertical ? box.Style.Height : box.Style.Width;
        var percentBasis = vertical ? context.ContainingBlock.Height : basis;

        if (stated.HasValue && stated.Kind != StyleLengthKind.Percent && !stated.ReadsContainingBlock)
        {
            max = Math.Max(max, BoxModel.IsBorderBox(box.Style)
                ? Math.Max(0, stated.Resolve(percentBasis))
                : stated.Resolve(percentBasis) + build.InsetsInline);
        }

        return new IntrinsicSize(min, Math.Max(min, max));
    }

    /// <summary>Places the captions, the rows and the cells inside the table's border box.</summary>
    public LayoutBox Arrange(Box box, Rect borderBox, LayoutContext context)
    {
        var style = box.Style;
        var basis = context.ContainingBlock.Width;
        var build = Build(box, basis, context);
        var flow = build.Flow;
        var contentInline = Math.Max(
            0,
            flow.InlineExtent(borderBox.Width, borderBox.Height) - build.InsetsInline);

        MeasureCaptions(build, contentInline, context);
        MeasureOthers(build, contentInline, context);

        // The box the table was given already accounts for the height it declared, so whatever the
        // captions do not claim is the size the rows have to fill, measured along the axis the rows run.
        var gridBlock = Math.Max(
            0,
            flow.BlockExtent(borderBox.Width, borderBox.Height) - build.InsetsBlock -
            build.CaptionTop - build.CaptionBottom);
        CompleteGrid(build, contentInline, context, gridBlock);

        var layout = new LayoutBox(box, LayoutKind.Table)
        {
            Margin = BoxModel.UsedMargin(style, basis, borderBox.Width),
            Border = build.Border,
            Padding = build.Padding,
            BorderBox = borderBox,
            Flow = flow,
        };

        // Everything the grid places is a pair of offsets from the content box along the two axes the
        // table works in, and `Map` is what turns that pair back into the rectangle the page draws -
        // on whichever physical edges the writing mode puts the two axes, and mirrored along the line
        // for a table that reads the other way (CSS Writing Modes 3 §9.1).
        var content = new Rect(
            borderBox.X + build.Border.Left + build.Padding.Left,
            borderBox.Y + build.Border.Top + build.Padding.Top,
            Math.Max(0, borderBox.Width - build.InsetsH),
            Math.Max(0, borderBox.Height - build.InsetsV));
        var blockCursor = 0.0;

        // A positioned table anchors the absolutely positioned descendants the captions and cells hand
        // up to it, the same way a positioned block does: a static caption is not a containing block,
        // so its abs child passes up to the nearest positioned ancestor - this table. The anchor is the
        // outer box, not the padded one: position belongs to the table's wrapper box, and that box
        // carries no border of its own (the border the drawing shows sits on the inner table box), so
        // its padding edge is the outer edge. Insetting by this box's border would push every abs
        // descendant off by that border, the way the gecko table-abs-pos references say it should not.
        // A table that holds its descendants - positioned, transformed, filtered, contained - is what the
        // absolutely positioned descendants the captions and cells hand up to it anchor against, the same
        // way a block does: a static caption is not a containing block, so its abs child passes up to the
        // nearest ancestor that holds one - this table. The rectangle handed down is the outer box rather
        // than the padded one: position belongs to the table's wrapper box, and that box carries no border
        // of its own (the border the drawing shows sits on the inner table box), so its padding edge is the
        // outer edge. Insetting by this box's border would push every abs descendant off by that border,
        // the way the gecko table-abs-pos references say it should not.
        var childContext = PositionedLayout.ForDescendants(context, box, layout.BorderBox, layout.BorderBox);

        foreach (var caption in build.Captions)
        {
            if (caption.Side != CaptionSide.Top)
            {
                continue;
            }

            layout.Children.Add(ArrangeCaption(caption, content, blockCursor, flow, childContext));
            blockCursor += caption.OuterBlock(flow.IsVertical);
        }

        ArrangeColumns(build, layout, content, blockCursor, flow);

        ArrangeRows(build, layout, content, blockCursor, flow, childContext);

        var bottom = blockCursor + build.Grid.Height;

        // The children that are not table parts are blocks of the table's own content, and they stand
        // below the rows - the table holds them beside its grid rather than around it.
        for (var index = 0; index < build.Others.Count; index++)
        {
            var other = build.Others[index];
            var margin = build.OtherMargins[index];
            var (inlineStart, _) = flow.InlineMargins(margin);
            var (blockStart, _) = flow.BlockMargins(margin);
            var inline = build.OtherWidths[index];
            var block = build.OtherHeights[index] -
                (flow.IsVertical ? margin.Horizontal : margin.Vertical);

            var otherContext = childContext.ForContainingBlock(flow.SizeOf(inline, block), flow.IsVertical);

            layout.Children.Add(_engine.Arrange(
                other,
                flow.Map(content, inlineStart, inline, bottom + blockStart, block),
                otherContext));

            bottom += build.OtherHeights[index];
        }

        foreach (var caption in build.Captions)
        {
            if (caption.Side != CaptionSide.Bottom)
            {
                continue;
            }

            layout.Children.Add(ArrangeCaption(caption, content, bottom, flow, childContext));
            bottom += caption.OuterBlock(flow.IsVertical);
        }

        return layout;
    }

    private LayoutBox ArrangeCaption(
        CaptionEntry caption,
        Rect content,
        Double blockOffset,
        WritingModeInfo flow,
        LayoutContext context)
    {
        var margin = caption.Margin;
        var (inlineStart, _) = flow.InlineMargins(margin);
        var (blockStart, _) = flow.BlockMargins(margin);
        var box = flow.Map(content, inlineStart, caption.Width, blockOffset + blockStart, caption.Height);

        // A caption is a block box in the table's flow, so its own margins shift it inside the band
        // the table gave it, the same way a block's margins sit it off its containing box. Its width
        // is its own measured one - the table's content width when it left the width to the content,
        // but its declared width when it named one, even wider than the table. The absolutely
        // positioned descendants anchor to where that box actually landed.
        var captionContext = context.ForContainingBlock(new Size(box.Width, box.Height), flow.IsVertical);

        // A positioned caption anchors its own absolutely positioned descendants, as a block would.
        if (PositionedLayout.IsPositioned(caption.Box))
        {
            captionContext = captionContext.WithAbsoluteContainingBlock(box);
        }

        return _engine.Arrange(caption.Box, box, captionContext);
    }

    /// <summary>
    /// Gives each <c>col</c> and <c>colgroup</c> the band of the grid it stands for, so that its
    /// background paints across the columns it covers (CSS 2.1 §17.5.2). The boxes hold no content and
    /// paint no border of their own: a column's borders belong to the grid lines, which the cells
    /// already draw, and under the separated model a column has none at all.
    /// </summary>
    private static void ArrangeColumns(
        TableBuild build,
        LayoutBox table,
        Rect content,
        Double blockStart,
        WritingModeInfo flow)
    {
        var grid = build.Grid;

        // A group lies under the columns inside it, so the runs of columns each element stands for are
        // walked twice: the groups first, then the <c>col</c> boxes themselves.
        foreach (var byGroup in new[] { false, true })
        {
            var boxes = byGroup ? build.ColumnGroupBoxes : build.ColumnBoxes;
            var at = 0;

            while (at < grid.Columns)
            {
                if (at >= boxes.Count || boxes[at] is not { } owner)
                {
                    at++;
                    continue;
                }

                var end = at;

                while (end < grid.Columns && end < boxes.Count && ReferenceEquals(boxes[end], owner))
                {
                    end++;
                }

                table.Children.Add(ColumnBox(build, owner, content, blockStart, at, end, flow));
                at = Math.Max(end, at + 1);
            }
        }
    }

    private static LayoutBox ColumnBox(
        TableBuild build,
        Box box,
        Rect content,
        Double blockStart,
        Int32 start,
        Int32 end,
        WritingModeInfo flow)
    {
        var grid = build.Grid;
        var offset = grid.ColumnX[start];
        var inline = Math.Max(0.0, grid.ColumnX[end - 1] + grid.Widths[end - 1] - offset);

        return new LayoutBox(box, LayoutKind.TableColumn)
        {
            BorderBox = flow.Map(content, offset, inline, blockStart, grid.Height),
            Flow = flow,
            SuppressBorder = true,
        };
    }

    /// <summary>
    /// Lays the rows out, nesting each run of rows that share a row group inside the group's box so
    /// that a row group background can be painted behind its rows.
    /// </summary>
    private void ArrangeRows(
        TableBuild build,
        LayoutBox table,
        Rect content,
        Double gridBlockStart,
        WritingModeInfo flow,
        LayoutContext context)
    {
        var grid = build.Grid;
        var vertical = flow.IsVertical;
        var spacing = build.Style.Collapse ? 0.0 : build.BlockSpacing;
        // The room between two columns and the room between two rows are two different numbers, and a
        // cell that spans over one of them grows across that one only: `border-spacing: 0 2px` leaves the
        // width of a colspan alone while it adds to a rowspan. Reading the vertical distance on both axes
        // made a cell across two columns as wide as the columns plus a row gap, which is a box no row of
        // the table occupies (§17.5.0.1) - and down a column the two swap, because the columns are the
        // ones that run across the page there.
        var inlineSpacing = build.Style.Collapse ? 0.0 : build.InlineSpacing;
        Box? groupBox = null;
        LayoutBox? group = null;
        Double groupStart = 0.0;
        Double groupEnd = 0.0;
        // A row group that is moved takes its rows with it, so a row's own displacement is the group's
        // plus whatever the row states.
        Offset groupShift = Offset.Zero;
        Offset rowShift = Offset.Zero;
        // What a percentage offset on a part is a share of is the size its containing block *stated*
        // along the axis the rows run: a group's is the table's, a row's is the group around it or the
        // table where it stands outside one. A part whose row or group or table is only as large as the
        // rows happen to make has no such whole to be a share of, and the offset then says nothing rather
        // than reaching all the way down the grid it grew to (CSS 2.1 §10.5 - "if the height of the
        // containing block is specified explicitly" - which is what the borrowed pages state in their own
        // asserts: the same `top: 100%` resolves to a row's ten pixels and not to the hundred the row was
        // grown to by its content).
        var tableBlock = StatedBlockExtent(build.Box, vertical);

        void CloseGroup()
        {
            if (group is null)
            {
                return;
            }

            // The rows the group has taken were moved already, and a group's box is grown from their
            // unshifted places: the group's own ground moves on its own terms, or the background would
            // slide out from under the content it belongs to.
            var box = flow.Map(
                content,
                0.0,
                grid.Width,
                gridBlockStart + groupStart,
                Math.Max(0.0, groupEnd - groupStart));
            group.BorderBox = new Rect(
                box.X + groupShift.X,
                box.Y + groupShift.Y,
                box.Width,
                box.Height);
        }

        foreach (var row in grid.Rows)
        {
            var rowBlock = gridBlockStart + row.Y;

            if (!ReferenceEquals(row.Group, groupBox))
            {
                CloseGroup();
                groupBox = row.Group;
                group = groupBox is null
                    ? null
                    : new LayoutBox(groupBox, LayoutKind.TableRowGroup)
                    {
                        BorderBox = flow.Map(content, 0.0, grid.Width, rowBlock, row.Height),
                        // The part says its own position, as every other box built by the engine does: a
                        // row group standing `relative` is drawn among the positioned descendants rather
                        // than with the in-flow content, which is what puts its ground over the marker a
                        // page leaves showing underneath it.
                        Position = groupBox.Style.Position.Position,
                    };
                groupStart = row.Y;
                groupEnd = row.Y + row.Height;

                groupShift = PartOffset(groupBox, grid.Width, tableBlock, flow);

                if (group is not null)
                {
                    table.Children.Add(group);
                }
            }

            var rowLayout = new LayoutBox(row.Box, LayoutKind.TableRow)
            {
                BorderBox = flow.Map(content, 0.0, grid.Width, rowBlock, row.Height),
                Position = row.Box.Style.Position.Position,
            };

            var own = PartOffset(
                row.Box,
                grid.Width,
                group is null ? tableBlock : StatedBlockExtent(groupBox, vertical),
                flow);
            rowShift = new Offset(groupShift.X + own.X, groupShift.Y + own.Y);

            if (group is null)
            {
                table.Children.Add(rowLayout);
            }
            else
            {
                group.Children.Add(rowLayout);
                groupEnd = Math.Max(groupEnd, row.Y + row.Height + (spacing / 2));
            }

            // A positioned row - or, failing that, the row group around it - is what an absolutely
            // positioned descendant anchors to. What `position` does to a table part is left undefined by
            // CSS 2.1, and a suite that keeps its green square inside the row is saying it means the row:
            // left unset, such a descendant falls all the way back to the initial containing block and is
            // drawn wherever the page starts, rather than beside the content it belongs to.
            //
            // The box used is the row's content box - the span its cells cover - rather than its border
            // box. A row is laid out across the table's grid, and under the separate model its cells begin
            // a spacing in from either edge; anchoring to the outer edge would put a descendant two pixels
            // out from the content it belongs beside.
            var inset = grid.ColumnX.Length > 0 ? grid.ColumnX[0] : 0.0;
            Rect? anchored = null;

            if (PositionedLayout.IsPositioned(row.Box))
            {
                anchored = flow.Map(
                    content,
                    inset,
                    Math.Max(0, grid.Width - (2 * inset)),
                    rowBlock,
                    row.Height);
            }
            else if (group is { } around && PositionedLayout.IsPositioned(groupBox!))
            {
                anchored = flow.Map(
                    content,
                    inset,
                    Math.Max(0, grid.Width - (2 * inset)),
                    gridBlockStart + groupStart,
                    Math.Max(0.0, groupEnd - groupStart));
            }

            var rowContext = anchored is { } at ? context.WithAbsoluteContainingBlock(at) : context;

            foreach (var cell in row.Cells)
            {
                rowLayout.Children.Add(ArrangeCell(
                    build,
                    cell,
                    content,
                    rowBlock,
                    inlineSpacing,
                    spacing,
                    rowContext));
            }

            // The row is moved whole, content and all, once it has been placed: what stands inside a row
            // that stepped out of its grid place steps along with it, an absolutely positioned descendant
            // included - its containing block is this row's border box as the row is actually drawn.
            MoveWithContent(rowLayout, rowShift);
        }

        CloseGroup();
    }

    /// <summary>
    /// Gets the distance a table part that states a relative offset is moved by: a row group carries its
    /// own, and the rows inside it carry that distance as well as theirs, because a row's place in the
    /// flow is a place inside the group's box (CSS Positioned 3 §6.2, which puts a table part on the same
    /// footing as a block box; §6.3 that a relative offset is a displacement of the box and its content).
    /// </summary>
    /// <param name="inlineExtent">
    /// The size the part's containing block has along the axis the columns run, which the offsets named
    /// along that axis are a share of.
    /// </param>
    /// <param name="blockExtent">
    /// The size the containing block <em>stated</em> along the axis the rows run, or null where it stated
    /// nothing. The physical pair these two are is the writing mode's, since a share of a column's
    /// vertical offset is a share of its physical width.
    /// </param>
    private static Offset PartOffset(Box? part, Double inlineExtent, Double? blockExtent, WritingModeInfo flow)
    {
        if (part is null || !PositionedLayout.IsPositioned(part) ||
            part.Style.Position.Position != PositionMode.Relative)
        {
            return Offset.Zero;
        }

        return PositionedLayout.RelativeOffset(
            part.Style.Position,
            flow.SizeOf(inlineExtent, blockExtent ?? 0.0),
            part.Flow);
    }

    /// <summary>
    /// Gets the size a part stated in its own style along the axis the rows run - the physical height of a
    /// horizontal table and its width of a vertical one - or nothing where it left that size to its rows.
    /// A share named in per cent is read against that number rather than against the size the rows came
    /// to, so the question is only ever what the page wrote; a per cent is not an answer here, since the
    /// whole it would be a share of is the very size being asked for.
    /// </summary>
    private static Double? StatedBlockExtent(Box? part, Boolean vertical)
    {
        if (part is null)
        {
            return null;
        }

        var stated = vertical
            ? (part.Style.Width.HasValue ? part.Style.Width : part.HintedWidth)
            : (part.Style.Height.HasValue ? part.Style.Height : part.HintedHeight);

        return stated is { HasValue: true } length && length.Kind != StyleLengthKind.Percent
            ? Math.Max(0, length.Resolve(0))
            : (Double?)null;
    }

    /// <summary>
    /// Moves a box and everything it holds. Unlike the shift that aligns a cell's content, this one takes
    /// the out-of-flow descendants with it: an absolutely positioned box standing inside a row that moved
    /// was placed against that row's own offset, so the two move together - leaving it behind would keep
    /// it pinned to where the row would have stood.
    /// </summary>
    private static void MoveWithContent(LayoutBox box, Offset shift)
    {
        if (shift.X == 0 && shift.Y == 0)
        {
            return;
        }

        box.BorderBox = new Rect(
            box.BorderBox.X + shift.X,
            box.BorderBox.Y + shift.Y,
            box.BorderBox.Width,
            box.BorderBox.Height);

        foreach (var child in box.Children)
        {
            MoveWithContent(child, shift);
        }
    }

    private LayoutBox ArrangeCell(
        TableBuild build,
        TableCellInfo cell,
        Rect content,
        Double rowBlock,
        Double inlineSpacing,
        Double spacing,
        LayoutContext context)
    {
        var grid = build.Grid;
        var flow = build.Flow;
        var width = ColumnSpanWidth(grid, cell, inlineSpacing);
        var height = RowSpanHeight(grid, cell, spacing);
        // The cell's place on the grid is a pair of offsets along the two axes the table works in; the
        // writing mode turns them into the rectangle the page draws, mirroring the line as it goes
        // (CSS Writing Modes 3 §4.3, §9.1).
        var rect = flow.Map(
            content,
            grid.ColumnX[cell.Column],
            width,
            rowBlock,
            height);

        // The rectangle above is the grid's answer; from here on the cell is laid out in its own
        // writing mode, which may be orthogonal to the table's. Its content box, and the axis its
        // `vertical-align` moves the content along, are the cell's own rather than the table's
        // (CSS Writing Modes 3 §9.2, which is what the orthogonal cell references hold to).
        var cellFlow = cell.Box.Flow;
        var contentWidth = Math.Max(0.0, rect.Width - cell.Insets.Horizontal);
        var contentHeight = Math.Max(0.0, rect.Height - cell.Insets.Vertical);
        // The rectangle above is the containing block the cell is laid out in, and its axes belong to
        // the table rather than to the cell: a cell turned aside from its table is contained by a box
        // that runs the other way, and that is what decides whether the cell is stretched along its
        // own line or sized by what it holds (CSS Writing Modes 3 §4.1). The cell's own writing mode
        // is what the layout inside it reads, and it is taken from the box, not from here.
        var cellContext = context.ForContainingBlock(
            new Size(contentWidth, contentHeight), flow.IsVertical);

        // A cell written `position: relative` is displaced by the table rather than by the general path,
        // because what a share of its offset is a share of is the size the row was *stated* along the
        // axis the rows run - and the size the grid ended up making it says nothing about that. A row
        // that named no size gives a `top: 100%` nothing to be taken from, so the cell stays where the
        // grid laid it instead of reaching all the way down the content that grew the row; a row that
        // named ten moves the cell ten, not the hundred its cell made it (CSS 2.1 §10.5 - "if the height
        // of the containing block is specified explicitly" - which is what the borrowed pages state, in
        // the two directions, as 010 and 013). The other extent a share of the offsets along it is
        // taken from is the row's span across the grid, which is the footing a row or a group standing
        // off that grid is given too.
        var displacement = PartOffset(
            cell.Box,
            grid.Width,
            StatedBlockExtent(grid.Rows[cell.Row].Box, flow.IsVertical),
            flow);

        // A positioned cell anchors its own absolutely positioned descendants, the same way a positioned
        // block does: without this they look past the cell to whatever encloses the table.
        if (PositionedLayout.IsPositioned(cell.Box))
        {
            cellContext = cellContext.WithAbsoluteContainingBlock(new Rect(
                rect.X + cell.UsedBorder.Left,
                rect.Y + cell.UsedBorder.Top,
                Math.Max(0, rect.Width - cell.UsedBorder.Horizontal),
                Math.Max(0, rect.Height - cell.UsedBorder.Vertical)));
        }

        // A cell whose room the table's stated height filled has a definite height, so a percentage
        // inside it resolves; one a row or a neighbour made tall does not (CSS Sizing 3 §5.2, measured
        // in Chrome). A height the cell itself wrote is already definite through its own style.
        var definite = cell.Box.HintedHeight is { HasValue: true } hint && hint.Kind != StyleLengthKind.Percent;

        for (var row = cell.Row; row < cell.Row + cell.RowSpan && row < grid.Rows.Count; row++)
        {
            definite |= grid.Rows[row].FilledByTable;
        }

        var layout = _engine.Arrange(cell.Box, rect, cellContext, definite, displacement: displacement);

        // The used border replaces whatever the cell's own style asked for: under the collapsing model
        // it is half of a shared line, so the box's border and the one it paints are not the same.
        layout.Border = cell.UsedBorder;
        layout.Padding = cell.Padding;

        if (cell.Colors is { } colors)
        {
            layout.BorderColors = colors;
            layout.BorderStyles = cell.LineStyles;
        }

        var hide = build.Style.HideEmptyCells && !build.Style.Collapse && cell.IsEmpty;
        layout.SuppressBackground = hide;
        layout.SuppressBorder = hide;

        // The alignment belongs to the cell, not to the table it sits in: `vertical-align` moves the
        // content along the cell's own block axis, which is the table's only while the two agree.
        AlignContent(
            layout,
            cell.Box.Style.Table.VerticalAlign,
            cellFlow.BlockExtent(contentWidth, contentHeight),
            cellFlow);

        return layout;
    }

    /// <summary>
    /// Shifts a cell's content down when it is shorter than the row and the cell asks for middle or
    /// bottom alignment. The height comes from the row, so a one-line cell in a tall row has to be
    /// moved rather than stretched.
    /// </summary>
    /// <remarks>
    /// A box that left the flow is none of this method's business: it is measured against its own
    /// containing block rather than stacked with the cell's content, so it neither says how tall that
    /// content is nor moves when it is aligned. Only the in-flow children are counted and shifted.
    /// <para>
    /// What is counted is where each child stood in the flow rather than where it is drawn. A child set
    /// <c>position: relative</c> keeps the room it had and steps out of it (CSS 2.1 §9.4.3), so a cell
    /// holding one hundred pixels of content that was moved ten up still holds one hundred pixels of
    /// content - and reading the moved place would leave the alignment to charge the same move a second
    /// time, which is how a cell asked to stand at the top of its row came to sit half a move off it.
    /// </para>
    /// </remarks>
    private static void AlignContent(
        LayoutBox cell,
        CellVerticalAlignment align,
        Double contentBlock,
        WritingModeInfo flow)
    {
        if (cell.Children.Count == 0 || align == CellVerticalAlignment.Top)
        {
            return;
        }

        var start = BlockStartOf(cell.ContentBox, flow);
        var end = start;

        foreach (var child in cell.Children)
        {
            if (child.IsOutOfFlow)
            {
                continue;
            }

            // The content reaches away from the edge the block axis starts at, which is the right one
            // down a `vertical-rl` column: the furthest it gets there is the smallest coordinate, and
            // the room it fills is the distance back towards the start rather than away from it.
            end = flow.BlockSign < 0
                ? Math.Min(end, BlockEndOf(child, flow))
                : Math.Max(end, BlockEndOf(child, flow));
        }

        var used = Math.Max(0.0, flow.BlockSign * (end - start));
        var free = contentBlock - used;

        if (free <= Epsilon)
        {
            return;
        }

        var shift = align switch
        {
            CellVerticalAlignment.Middle => free / 2,
            CellVerticalAlignment.Bottom => free,
            _ => 0.0,
        };

        if (shift <= 0)
        {
            return;
        }

        foreach (var child in cell.Children)
        {
            if (!child.IsOutOfFlow)
            {
                Shift(child, shift, flow);
            }
        }
    }

    /// <summary>
    /// Gets how far a box's content reaches along the axis the rows run, counting the margin that opens
    /// it from the next row and the offset a `position: relative` moved it by but not the room it keeps.
    /// </summary>
    private static Double BlockEndOf(LayoutBox box, WritingModeInfo flow) => flow.IsVertical
        ? flow.BlockSign < 0
            ? box.BorderBox.X - box.RelativeDisplacement.X - box.Margin.Left
            : box.BorderBox.Right + box.RelativeDisplacement.X + box.Margin.Right
        : box.BorderBox.Bottom - box.RelativeDisplacement.Y + box.Margin.Bottom;

    private static Double BlockStartOf(Rect contentBox, WritingModeInfo flow) => flow.IsVertical
        ? flow.BlockSign < 0 ? contentBox.Right : contentBox.X
        : contentBox.Y;

    /// <summary>
    /// Moves a box and its whole in-flow subtree along the axis the rows run by the given distance, which
    /// is downwards in a row and across the page down a column.
    /// </summary>
    private static void Shift(LayoutBox box, Double amount, WritingModeInfo flow)
    {
        var dx = flow.IsVertical ? flow.BlockSign * amount : 0.0;
        var dy = flow.IsVertical ? 0.0 : amount;

        box.BorderBox = new Rect(
            box.BorderBox.X + dx,
            box.BorderBox.Y + dy,
            box.BorderBox.Width,
            box.BorderBox.Height);

        foreach (var child in box.Children)
        {
            // An out-of-flow descendant inside the content - an absolutely positioned box in a line,
            // say - was placed against its own containing block and is not part of what was aligned.
            if (!child.IsOutOfFlow)
            {
                Shift(child, amount, flow);
            }
        }
    }

    /// <summary>Gets the border-box width a cell occupies, including the spacing it spans over.</summary>
    private static Double ColumnSpanWidth(TableGrid grid, TableCellInfo cell, Double spacing)
    {
        var width = 0.0;
        var last = Math.Min(cell.Column + cell.Span, grid.Columns);

        for (var i = cell.Column; i < last; i++)
        {
            width += grid.Widths[i];
        }

        return width + (spacing * Math.Max(0, cell.Span - 1));
    }

    /// <summary>Gets the border-box height a cell occupies, including the spacing it spans over.</summary>
    private static Double RowSpanHeight(TableGrid grid, TableCellInfo cell, Double spacing)
    {
        var height = 0.0;
        var last = Math.Min(cell.Row + cell.RowSpan, grid.Rows.Count);

        for (var i = cell.Row; i < last; i++)
        {
            height += grid.Rows[i].Height;
        }

        return height + (spacing * Math.Max(0, cell.RowSpan - 1));
    }

    /// <summary>Gets the space the columns and their border spacing need between them.</summary>
    private static Double InlineSpacingTotal(TableBuild build) =>
        build.Style.Collapse || build.Grid.Columns == 0
            ? 0.0
            : build.InlineSpacing * (build.Grid.Columns + 1);

    /// <summary>
    /// Resolves the inline size of the table's content box. A declared size on that axis wins, subject to
    /// the columns' minimum; an automatic one shrinks to fit, which is what makes a table without a
    /// stated size only as large as its content. The size is the physical <c>width</c> of a horizontal
    /// table and the <c>height</c> of one that reads down the page.
    /// </summary>
    private static Double ResolveContentInline(
        TableBuild build,
        Double? available,
        Double basis,
        Double minContent,
        Double maxContent)
    {
        var style = build.Box.Style;
        var vertical = build.Flow.IsVertical;
        var insets = build.InsetsInline;
        var declared = DeclaredContent(
            vertical ? style.Height : style.Width,
            basis,
            insets,
            BoxModel.IsBorderBox(style));
        Double content;

        if (declared is { } stated)
        {
            content = build.Style.Fixed ? stated : Math.Max(stated, minContent);
        }
        else if (available is { } room)
        {
            content = Math.Clamp(Math.Max(0, room - insets), minContent, Math.Max(minContent, maxContent));
        }
        else
        {
            content = Math.Max(minContent, maxContent);
        }

        var isBorderBox = BoxModel.IsBorderBox(style);

        if (BoxModel.ContentBound(vertical ? style.MaxHeight : style.MaxWidth, basis, insets, isBorderBox) is { } maximum &&
            content > maximum)
        {
            content = maximum;
        }

        if (BoxModel.ContentBound(vertical ? style.MinHeight : style.MinWidth, basis, insets, isBorderBox) is { } minimum &&
            content < minimum)
        {
            content = minimum;
        }

        return Math.Max(0, content);
    }

    /// <summary>
    /// Gets the size the table declared along the axis its rows run, as a content-box size, or nothing
    /// where it left that to the rows.
    /// </summary>
    private static Double? DeclaredBlockExtent(TableBuild build, Double basis)
    {
        var style = build.Box.Style;

        return build.Flow.IsVertical
            ? BoxModel.DeclaredWidth(style, basis, build.InsetsBlock)
            : BoxModel.DeclaredHeight(style, basis, build.InsetsBlock);
    }

    /// <summary>Gets a declared size as a content-box size, or null when there is none.</summary>
    private static Double? DeclaredContent(StyleLength length, Double basis, Double insets, Boolean isBorderBox)
    {
        if (!length.HasValue || (length.Kind == StyleLengthKind.Percent && basis <= 0))
        {
            return null;
        }

        var resolved = Math.Max(0, length.Resolve(basis));
        return isBorderBox ? Math.Max(0, resolved - insets) : resolved;
    }

}

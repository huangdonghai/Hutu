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

        var spacingTotal = HorizontalSpacingTotal(build);
        var minContent = build.Grid.SumMinimum() + spacingTotal;
        var maxContent = build.Grid.SumMaximum() + spacingTotal;

        var contentWidth = ResolveContentWidth(
            style,
            build,
            minContent,
            maxContent,
            basisWidth,
            constraints);

        // The captions are measured first because the height the table declared covers them too; what is
        // left of it once they have taken their share is the height the rows have to fill.
        MeasureCaptions(build, contentWidth, context);

        // The children that are not table parts are blocks of the table's own content, and they are
        // measured against the width the table settled on.
        MeasureOthers(build, contentWidth, context);

        var declared = BoxModel.DeclaredHeight(style, basisHeight, build.InsetsV);
        var gridHeight = declared is { } tableHeight
            ? tableHeight - build.CaptionTop - build.CaptionBottom
            : (Double?)null;

        CompleteGrid(build, contentWidth, context, gridHeight);

        var othersHeight = build.OtherHeights.Count > 0
            ? build.OtherHeights.Sum()
            : 0.0;

        var contentHeight = build.Grid.Height + build.CaptionTop + build.CaptionBottom + othersHeight;

        // A declared height is a floor, as CSS 2.1 §17.5.3 has it: a table asked to be shorter than its
        // rows need is as tall as the rows make it. The width answers the same way, one step further up:
        // a table asked to be narrower than its columns can go is as wide as the columns make it.
        var usedHeight = BoxModel.ClampHeight(style, Math.Max(declared ?? 0, contentHeight), basisHeight, build.InsetsV);

        return new Size(Math.Max(contentWidth, build.Grid.Width) + build.InsetsH, usedHeight + build.InsetsV);
    }

    /// <summary>
    /// The intrinsic inline sizes of the table: the sum of the column minimums and of the column
    /// maximums. A flex item or a grid track that sizes itself from a table asks for this.
    /// </summary>
    public IntrinsicSize MeasureIntrinsic(Box box, LayoutContext context)
    {
        var basis = context.ContainingBlock.Width;
        var build = Build(box, basis, context);
        var spacingTotal = HorizontalSpacingTotal(build);
        var min = build.Grid.SumMinimum() + spacingTotal + build.InsetsH;
        var max = build.Grid.SumMaximum() + spacingTotal + build.InsetsH;

        // A width the author stated is part of what the table brings to a box that shrinks to fit, as the
        // preferred size it would rather be: a table with no cells is still as wide as it was told (CSS 2.1
        // §17.5.2.2, and §17.5.3 under the collapsed model, where the table keeps no padding of its own to
        // make the difference up). Asked by the columns alone such a table is nothing, and the float holding
        // one collapses with it - which is what wpt's collapsing-border-model-013 and -014 hold to.
        //
        // Only the preferred size is raised, never the narrowest one: the automatic minimum of an item is
        // read off this pair (CSS Flexbox 1 §4.5, §9.9.1) and a stated width is not a min-width there
        // (wpt's table-as-item-specified-width holds to exactly that). And a percentage is left out, since
        // the width it would resolve against is the very width being asked about - CSS Tables 3 §4 puts
        // such a table at its preferred width instead, which the columns already answer.
        var stated = box.Style.Width;

        if (stated.HasValue && stated.Kind != StyleLengthKind.Percent && !stated.ReadsContainingBlock)
        {
            max = Math.Max(max, BoxModel.IsBorderBox(box.Style)
                ? Math.Max(0, stated.Resolve(basis))
                : stated.Resolve(basis) + build.InsetsH);
        }

        return new IntrinsicSize(min, Math.Max(min, max));
    }

    /// <summary>Places the captions, the rows and the cells inside the table's border box.</summary>
    public LayoutBox Arrange(Box box, Rect borderBox, LayoutContext context)
    {
        var style = box.Style;
        var basis = context.ContainingBlock.Width;
        var build = Build(box, basis, context);
        var contentWidth = Math.Max(0, borderBox.Width - build.InsetsH);

        MeasureCaptions(build, contentWidth, context);
        MeasureOthers(build, contentWidth, context);

        // The box the table was given already accounts for the height it declared, so whatever the
        // captions do not claim is the height the rows have to fill.
        var gridHeight = Math.Max(0, borderBox.Height - build.InsetsV - build.CaptionTop - build.CaptionBottom);
        CompleteGrid(build, contentWidth, context, gridHeight);

        var layout = new LayoutBox(box, LayoutKind.Table)
        {
            Margin = BoxModel.UsedMargin(style, basis, borderBox.Width),
            Border = build.Border,
            Padding = build.Padding,
            BorderBox = borderBox,
            Flow = box.Flow,
        };

        var gridX = borderBox.X + build.Border.Left + build.Padding.Left;
        var topY = borderBox.Y + build.Border.Top + build.Padding.Top;

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

            layout.Children.Add(ArrangeCaption(caption, gridX, topY, contentWidth, childContext));
            topY += caption.OuterHeight;
        }

        var gridY = topY;
        ArrangeRows(build, layout, gridX, gridY, childContext);

        var bottomY = gridY + build.Grid.Height;

        // The children that are not table parts are blocks of the table's own content, and they stand
        // below the rows - the table holds them beside its grid rather than around it.
        for (var index = 0; index < build.Others.Count; index++)
        {
            var other = build.Others[index];
            var margin = build.OtherMargins[index];
            var width = build.OtherWidths[index];
            var height = build.OtherHeights[index] - margin.Vertical;

            var otherContext = childContext.ForContainingBlock(new Size(width, 0));

            layout.Children.Add(_engine.Arrange(
                other,
                new Rect(gridX + margin.Left, bottomY + margin.Top, width, height),
                otherContext));

            bottomY += build.OtherHeights[index];
        }

        foreach (var caption in build.Captions)
        {
            if (caption.Side != CaptionSide.Bottom)
            {
                continue;
            }

            layout.Children.Add(ArrangeCaption(caption, gridX, bottomY, contentWidth, childContext));
            bottomY += caption.OuterHeight;
        }

        return layout;
    }

    private LayoutBox ArrangeCaption(CaptionEntry caption, Double x, Double y, Double width, LayoutContext context)
    {
        var margin = caption.Margin;

        // A caption is a block box in the table's flow, so its own margins shift it inside the band
        // the table gave it, the same way a block's margins sit it off its containing box. Its width
        // is its own measured one - the table's content width when it left the width to the content,
        // but its declared width when it named one, even wider than the table. The absolutely
        // positioned descendants anchor to where that box actually landed.
        var boxX = x + margin.Left;
        var boxY = y + margin.Top;
        var boxWidth = caption.Width;

        var captionContext = context.ForContainingBlock(new Size(boxWidth, 0));

        // A positioned caption anchors its own absolutely positioned descendants, as a block would.
        if (PositionedLayout.IsPositioned(caption.Box))
        {
            captionContext = captionContext.WithAbsoluteContainingBlock(new Rect(boxX, boxY, boxWidth, caption.Height));
        }

        return _engine.Arrange(caption.Box, new Rect(boxX, boxY, boxWidth, caption.Height), captionContext);
    }

    /// <summary>
    /// Lays the rows out, nesting each run of rows that share a row group inside the group's box so
    /// that a row group background can be painted behind its rows.
    /// </summary>
    private void ArrangeRows(TableBuild build, LayoutBox table, Double gridX, Double gridY, LayoutContext context)
    {
        var grid = build.Grid;
        var spacing = build.Style.Collapse ? 0.0 : build.Style.VerticalSpacing;
        // The room between two columns and the room between two rows are two different numbers, and a
        // cell that spans over one of them grows across that one only: `border-spacing: 0 2px` leaves the
        // width of a colspan alone while it adds to a rowspan. Reading the vertical distance on both axes
        // made a cell across two columns as wide as the columns plus a row gap, which is a box no row of
        // the table occupies (§17.5.0.1).
        var inlineSpacing = build.Style.Collapse ? 0.0 : build.Style.HorizontalSpacing;
        Box? groupBox = null;
        LayoutBox? group = null;
        // A row group that is moved takes its rows with it, so a row's own displacement is the group's
        // plus whatever the row states.
        Offset groupShift = Offset.Zero;
        Offset rowShift = Offset.Zero;
        // What a percentage offset on a part is a share of is the height its containing block *stated*:
        // a group's is the table's, a row's is the group around it or the table where it stands outside
        // one. A part whose row or group or table is only as tall as the rows happen to make has no such
        // whole to be a share of, and the offset then says nothing rather than reaching all the way down
        // the grid it grew to (CSS 2.1 §10.5 - "if the height of the containing block is specified
        // explicitly" - which is what the borrowed pages state in their own asserts: the same `top: 100%`
        // resolves to a row's ten pixels and not to the hundred the row was grown to by its content).
        var tableHeight = StatedHeight(build.Box, 0.0);

        foreach (var row in grid.Rows)
        {
            if (!ReferenceEquals(row.Group, groupBox))
            {
                if (group is not null)
                {
                    // The rows this group has taken were moved already, and a group's box was grown from
                    // their unshifted places: the group's own ground moves on its own terms, or the
                    // background would slide out from under the content it belongs to.
                    group.BorderBox = new Rect(
                        group.BorderBox.X + groupShift.X,
                        group.BorderBox.Y + groupShift.Y,
                        group.BorderBox.Width,
                        group.BorderBox.Height);
                }

                groupBox = row.Group;
                group = groupBox is null
                    ? null
                    : new LayoutBox(groupBox, LayoutKind.TableRowGroup)
                    {
                        BorderBox = new Rect(gridX, gridY + row.Y, grid.Width, row.Height),
                        // The part says its own position, as every other box built by the engine does: a
                        // row group standing `relative` is drawn among the positioned descendants rather
                        // than with the in-flow content, which is what puts its ground over the marker a
                        // page leaves showing underneath it.
                        Position = groupBox.Style.Position.Position,
                    };

                groupShift = PartOffset(groupBox, grid.Width, tableHeight);

                if (group is not null)
                {
                    table.Children.Add(group);
                }
            }

            var rowLayout = new LayoutBox(row.Box, LayoutKind.TableRow)
            {
                BorderBox = new Rect(gridX, gridY + row.Y, grid.Width, row.Height),
                Position = row.Box.Style.Position.Position,
            };

            var own = PartOffset(row.Box, grid.Width, group is null ? tableHeight : StatedHeight(groupBox, 0.0));
            rowShift = new Offset(groupShift.X + own.X, groupShift.Y + own.Y);

            if (group is null)
            {
                table.Children.Add(rowLayout);
            }
            else
            {
                group.Children.Add(rowLayout);
                ExpandGroup(group, gridY + row.Y, row.Height, spacing);
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
                anchored = new Rect(
                    gridX + inset,
                    gridY + row.Y,
                    Math.Max(0, grid.Width - 2 * inset),
                    row.Height);
            }
            else if (group is { } around && PositionedLayout.IsPositioned(groupBox!))
            {
                anchored = new Rect(
                    around.BorderBox.X + inset,
                    around.BorderBox.Y,
                    Math.Max(0, around.BorderBox.Width - 2 * inset),
                    around.BorderBox.Height);
            }

            var rowContext = anchored is { } at ? context.WithAbsoluteContainingBlock(at) : context;

            foreach (var cell in row.Cells)
            {
                rowLayout.Children.Add(ArrangeCell(build, cell, gridX, gridY, inlineSpacing, spacing, rowContext));
            }

            // The row is moved whole, content and all, once it has been placed: what stands inside a row
            // that stepped out of its grid place steps along with it, an absolutely positioned descendant
            // included - its containing block is this row's border box as the row is actually drawn.
            MoveWithContent(rowLayout, rowShift);
        }

        if (group is not null)
        {
            group.BorderBox = new Rect(
                group.BorderBox.X + groupShift.X,
                group.BorderBox.Y + groupShift.Y,
                group.BorderBox.Width,
                group.BorderBox.Height);
        }
    }

    /// <summary>
    /// Gets the distance a table part that states a relative offset is moved by: a row group carries its
    /// own, and the rows inside it carry that distance as well as theirs, because a row's place in the
    /// flow is a place inside the group's box (CSS Positioned 3 §6.2, which puts a table part on the same
    /// footing as a block box; §6.3 that a relative offset is a displacement of the box and its content).
    /// </summary>
    private static Offset PartOffset(Box? part, Double width, Double? height)
    {
        if (part is null || !PositionedLayout.IsPositioned(part) ||
            part.Style.Position.Position != PositionMode.Relative)
        {
            return Offset.Zero;
        }

        return PositionedLayout.RelativeOffset(part.Style.Position, new Size(width, height ?? 0.0), part.Flow);
    }

    /// <summary>
    /// Gets the height a part stated in its own style, or nothing where it left the height to its rows.
    /// A share named in per cent is read against that number rather than against the height the rows
    /// came to, so the question is only ever what the page wrote; a per cent is not an answer here, since
    /// the whole it would be a share of is the very height being asked for.
    /// </summary>
    private static Double? StatedHeight(Box? part, Double basis)
    {
        if (part is null)
        {
            return null;
        }

        var height = part.Style.Height.HasValue ? part.Style.Height : part.HintedHeight;

        return height is { HasValue: true } stated && stated.Kind != StyleLengthKind.Percent
            ? Math.Max(0, stated.Resolve(basis))
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

    /// <summary>Grows a row group's box so that it covers the rows it has received so far.</summary>
    private static void ExpandGroup(LayoutBox group, Double rowY, Double rowHeight, Double spacing)
    {
        var bottom = Math.Max(group.BorderBox.Bottom, rowY + rowHeight + spacing / 2);

        group.BorderBox = new Rect(
            group.BorderBox.X,
            group.BorderBox.Y,
            group.BorderBox.Width,
            Math.Max(0, bottom - group.BorderBox.Y));
    }

    private LayoutBox ArrangeCell(
        TableBuild build,
        TableCellInfo cell,
        Double gridX,
        Double gridY,
        Double inlineSpacing,
        Double spacing,
        LayoutContext context)
    {
        var grid = build.Grid;
        var x = gridX + grid.ColumnX[cell.Column];
        var y = gridY + grid.Rows[cell.Row].Y;
        var width = ColumnSpanWidth(grid, cell, inlineSpacing);
        var height = RowSpanHeight(grid, cell, spacing);
        var rect = new Rect(x, y, width, height);

        var contentWidth = Math.Max(0, width - cell.Insets.Horizontal);
        var contentHeight = Math.Max(0, height - cell.Insets.Vertical);
        var cellContext = context.ForContainingBlock(new Size(contentWidth, contentHeight));

        // A cell written `position: relative` is displaced by the table rather than by the general path,
        // because what a share of its offset is a share of is the height the row was *stated* - and the
        // height the grid ended up making it says nothing about that. A row that named no height gives a
        // `top: 100%` nothing to be taken from, so the cell stays where the grid laid it instead of
        // reaching all the way down the content that grew the row; a row that named ten moves the cell
        // ten, not the hundred its cell made it (CSS 2.1 §10.5 - "if the height of the containing block
        // is specified explicitly" - which is what the borrowed pages state, in the two directions, as
        // 010 and 013). The width a share of the horizontal offsets is taken from is the row's span
        // across the grid, which is the footing a row or a group standing off that grid is given too.
        var displacement = PartOffset(cell.Box, grid.Width, StatedHeight(grid.Rows[cell.Row].Box, 0.0));

        // A positioned cell anchors its own absolutely positioned descendants, the same way a positioned
        // block does: without this they look past the cell to whatever encloses the table.
        if (PositionedLayout.IsPositioned(cell.Box))
        {
            cellContext = cellContext.WithAbsoluteContainingBlock(new Rect(
                x + cell.UsedBorder.Left,
                y + cell.UsedBorder.Top,
                Math.Max(0, width - cell.UsedBorder.Horizontal),
                Math.Max(0, height - cell.UsedBorder.Vertical)));
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

        // The alignment belongs to the cell, not to the table it sits in.
        AlignContent(layout, cell.Box.Style.Table.VerticalAlign, contentHeight);

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
    private static void AlignContent(LayoutBox cell, CellVerticalAlignment align, Double contentHeight)
    {
        if (cell.Children.Count == 0 || align == CellVerticalAlignment.Top)
        {
            return;
        }

        var top = cell.ContentBox.Y;
        var bottom = top;

        foreach (var child in cell.Children)
        {
            if (child.IsOutOfFlow)
            {
                continue;
            }

            bottom = Math.Max(
                bottom,
                child.BorderBox.Bottom - child.RelativeDisplacement.Y + child.Margin.Bottom);
        }

        var used = bottom - top;
        var free = contentHeight - used;

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
                Shift(child, shift);
            }
        }
    }

    /// <summary>Moves a box and its whole in-flow subtree down by the given distance.</summary>
    private static void Shift(LayoutBox box, Double dy)
    {
        box.BorderBox = new Rect(box.BorderBox.X, box.BorderBox.Y + dy, box.BorderBox.Width, box.BorderBox.Height);

        foreach (var child in box.Children)
        {
            // An out-of-flow descendant inside the content - an absolutely positioned box in a line,
            // say - was placed against its own containing block and is not part of what was aligned.
            if (!child.IsOutOfFlow)
            {
                Shift(child, dy);
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
    private static Double HorizontalSpacingTotal(TableBuild build) =>
        build.Style.Collapse || build.Grid.Columns == 0
            ? 0.0
            : build.Style.HorizontalSpacing * (build.Grid.Columns + 1);

    /// <summary>
    /// Resolves the width of the table's content box. A declared width wins, subject to the columns'
    /// minimum; an automatic width shrinks to fit, which is what makes a table without a width only
    /// as wide as its content.
    /// <para>
    /// The floor is the automatic algorithm's, and the fixed one says otherwise: a table laid out fixed
    /// takes the width it declared, its remaining columns dividing "the remaining horizontal table space"
    /// and "cells in subsequent rows do not affect column widths" (CSS 2.1 §17.5.2.1). What a cell holds
    /// is then content that overflows the column it stands in, to be hidden or left standing by that
    /// column's own overflow - not a reason for the table to grow. A fixed table asked to be narrower than
    /// its words are wide is narrow, and its words are cut.
    /// </para>
    /// </summary>
    private static Double ResolveContentWidth(
        ComputedStyle style,
        TableBuild build,
        Double minContent,
        Double maxContent,
        Double basis,
        MeasureConstraints constraints)
    {
        var declared = DeclaredContentWidth(style, basis, build.InsetsH);
        Double content;

        if (declared is { } width)
        {
            content = build.Style.Fixed ? width : Math.Max(width, minContent);
        }
        else if (constraints.AvailableWidth is { } available)
        {
            var room = Math.Max(0, available - build.InsetsH);
            content = Math.Clamp(room, minContent, Math.Max(minContent, maxContent));
        }
        else
        {
            content = Math.Max(minContent, maxContent);
        }

        var isBorderBox = BoxModel.IsBorderBox(style);

        if (BoxModel.ContentBound(style.MaxWidth, basis, build.InsetsH, isBorderBox) is { } maximum && content > maximum)
        {
            content = maximum;
        }

        if (BoxModel.ContentBound(style.MinWidth, basis, build.InsetsH, isBorderBox) is { } minimum && content < minimum)
        {
            content = minimum;
        }

        return Math.Max(0, content);
    }

    /// <summary>Gets the declared width as a content-box width, or null when there is none.</summary>
    private static Double? DeclaredContentWidth(ComputedStyle style, Double basis, Double insets)
    {
        var width = style.Width;

        if (!width.HasValue || (width.Kind == StyleLengthKind.Percent && basis <= 0))
        {
            return null;
        }

        var resolved = Math.Max(0, width.Resolve(basis));
        return BoxModel.IsBorderBox(style) ? Math.Max(0, resolved - insets) : resolved;
    }
}

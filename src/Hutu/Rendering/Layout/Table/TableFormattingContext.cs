using Hutu.Rendering.Boxes;
using System;
using System.Collections.Generic;

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
        var childContext = PositionedLayout.ForDescendants(context, box, layout.BorderBox);

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
        Box? groupBox = null;
        LayoutBox? group = null;

        foreach (var row in grid.Rows)
        {
            if (!ReferenceEquals(row.Group, groupBox))
            {
                groupBox = row.Group;
                group = groupBox is null
                    ? null
                    : new LayoutBox(groupBox, LayoutKind.TableRowGroup)
                    {
                        BorderBox = new Rect(gridX, gridY + row.Y, grid.Width, row.Height),
                    };

                if (group is not null)
                {
                    table.Children.Add(group);
                }
            }

            var rowLayout = new LayoutBox(row.Box, LayoutKind.TableRow)
            {
                BorderBox = new Rect(gridX, gridY + row.Y, grid.Width, row.Height),
            };

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
                rowLayout.Children.Add(ArrangeCell(build, cell, gridX, gridY, spacing, rowContext));
            }
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
        Double spacing,
        LayoutContext context)
    {
        var grid = build.Grid;
        var x = gridX + grid.ColumnX[cell.Column];
        var y = gridY + grid.Rows[cell.Row].Y;
        var width = ColumnSpanWidth(grid, cell, spacing);
        var height = RowSpanHeight(grid, cell, spacing);
        var rect = new Rect(x, y, width, height);

        var contentWidth = Math.Max(0, width - cell.Insets.Horizontal);
        var contentHeight = Math.Max(0, height - cell.Insets.Vertical);
        var cellContext = context.ForContainingBlock(new Size(contentWidth, contentHeight));

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

        var layout = _engine.Arrange(cell.Box, rect, cellContext);

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

            bottom = Math.Max(bottom, child.BorderBox.Bottom + child.Margin.Bottom);
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
            content = Math.Max(width, minContent);
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

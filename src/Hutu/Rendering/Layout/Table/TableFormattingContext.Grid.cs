using AngleSharp.Dom;
using Hutu.Rendering.Boxes;
using System;
using System.Collections.Generic;
using System.Globalization;

namespace Hutu.Rendering.Layout;

/// <summary>
/// The structural half of the table context: it walks the table's children into the grid, resolves
/// the borders of the two models, and measures the cells.
/// </summary>
internal sealed partial class TableFormattingContext
{
    /// <summary>One caption together with the height it wants and the side it sits on.</summary>
    private sealed class CaptionEntry
    {
        public Box Box = null!;

        public CaptionSide Side;

        public Double Height;
    }

    /// <summary>Everything the table context computes for one table, shared by both phases.</summary>
    private sealed class TableBuild
    {
        public Box Box = null!;

        public TableStyle Style;

        public TableGrid Grid = new();

        public Thickness Padding;

        public Thickness Border;

        public Double InsetsH;

        public Double InsetsV;

        /// <summary>Gets the width the percentages of the table and its cells resolve against.</summary>
        public Double Basis;

        public List<CaptionEntry> Captions { get; } = new();

        public List<Double?> ColumnHints { get; } = new();

        public Double CaptionTop;

        public Double CaptionBottom;
    }

    /// <summary>
    /// Builds the grid: structure, borders, the cells' intrinsic widths. The result is ready for the
    /// columns to be sized, which needs a width the caller only knows after this has run.
    /// </summary>
    private TableBuild Build(Box table, Double basis, LayoutContext context)
    {
        var style = table.Style;
        var tableStyle = style.Table;
        var padding = style.Padding.Resolve(basis);

        // Under the collapsing model the table's own border is folded into the outermost grid lines,
        // so it must not be added to the table box as well; the cells paint it instead.
        var border = tableStyle.Collapse ? Thickness.Zero : BlockLayout.BorderOf(table);

        var build = new TableBuild
        {
            Box = table,
            Style = tableStyle,
            Padding = padding,
            Border = border,
            InsetsH = padding.Horizontal + border.Horizontal,
            InsetsV = padding.Vertical + border.Vertical,
            Basis = basis,
        };

        CollectRows(build, basis);
        ResolveBorders(build);
        ResolveCellIntrinsics(build, context);
        build.Grid.Prepare();
        build.Grid.ApplyColumnHints(build.ColumnHints);
        return build;
    }

    /// <summary>
    /// Shares out the columns and measures the rows at the width the table settled on. When the table
    /// declared a height greater than its rows need, the grid is grown to that height so the rows fill
    /// the box rather than leaving it empty below them (CSS 2.1 §17.5.3).
    /// </summary>
    private void CompleteGrid(TableBuild build, Double contentWidth, LayoutContext context, Double? targetHeight = null)
    {
        var tableStyle = build.Style;
        var spacing = tableStyle.Collapse ? 0.0 : tableStyle.HorizontalSpacing;
        var rowSpacing = tableStyle.Collapse ? 0.0 : tableStyle.VerticalSpacing;

        build.Grid.SizeColumns(Math.Max(0, contentWidth - HorizontalSpacingTotal(build)), tableStyle.Fixed);
        MeasureCellHeights(build, context, spacing, targetHeight);
        build.Grid.SizeRows();
        build.Grid.PlaceColumns(spacing, tableStyle.Collapse);
        build.Grid.PlaceRows(rowSpacing, tableStyle.Collapse);

        if (targetHeight is { } target)
        {
            var surplus = target - build.Grid.Height;

            if (surplus > Epsilon)
            {
                build.Grid.StretchRows(surplus);
                build.Grid.PlaceRows(rowSpacing, tableStyle.Collapse);
            }
        }
    }

    /// <summary>
    /// Walks the table's children into rows and cells, allocating a slot for every cell. Rows may
    /// arrive through a row group or directly, and a cell may be pushed sideways by the row spans and
    /// column spans of the cells before it.
    /// </summary>
    private static void CollectRows(TableBuild build, Double basis)
    {
        var table = build.Box;
        var source = new List<(Box Box, Box? Group)>();

        foreach (var child in table.Children)
        {
            switch (child.Kind)
            {
                case BoxKind.TableCaption:
                    build.Captions.Add(new CaptionEntry
                    {
                        Box = child,
                        Side = child.Style.Table.CaptionSide,
                    });

                    break;

                case BoxKind.TableRowGroup:
                    foreach (var row in child.Children)
                    {
                        if (row.Kind == BoxKind.TableRow)
                        {
                            source.Add((row, child));
                        }
                    }

                    break;

                case BoxKind.TableRow:
                    source.Add((child, null));
                    break;
            }
        }

        var occupied = new HashSet<(Int32 Row, Int32 Column)>();
        var columns = 0;

        for (var r = 0; r < source.Count; r++)
        {
            var info = new TableRowInfo { Box = source[r].Box, Group = source[r].Group };

            foreach (var child in source[r].Box.Children)
            {
                if (child.Kind != BoxKind.TableCell)
                {
                    continue;
                }

                var (columnSpan, rowSpan) = ReadSpans(child);
                var column = 0;

                while (Occupied(occupied, r, column, columnSpan, rowSpan))
                {
                    column++;
                }

                Mark(occupied, r, column, columnSpan, rowSpan);

                info.Cells.Add(new TableCellInfo
                {
                    Box = child,
                    Row = r,
                    Column = column,
                    Span = columnSpan,
                    RowSpan = rowSpan,
                    IsEmpty = IsEmptyCell(child),
                    Padding = child.Style.Padding.Resolve(basis),
                });

                columns = Math.Max(columns, column + columnSpan);
            }

            build.Grid.Rows.Add(info);
        }

        // A row span may reach past the last row, and the span needs the rows to exist to be sized.
        var needed = 0;

        foreach (var row in build.Grid.Rows)
        {
            foreach (var cell in row.Cells)
            {
                needed = Math.Max(needed, cell.Row + cell.RowSpan);
            }
        }

        while (build.Grid.Rows.Count < needed)
        {
            build.Grid.Rows.Add(new TableRowInfo
            {
                Box = new Box(BoxKind.TableRow, table.Style) { IsAnonymous = true },
            });
        }

        build.Grid.Columns = columns;
        CollectColumnHints(build, basis);
    }

    /// <summary>
    /// Settles the border of every cell. In the separated model a cell keeps its own border. In the
    /// collapsing model the borders meeting on each grid line are resolved once and each cell paints
    /// half of the line, except on the table's outer edge where the cell paints all of it.
    /// </summary>
    private static void ResolveBorders(TableBuild build)
    {
        var grid = build.Grid;

        if (!build.Style.Collapse)
        {
            foreach (var row in grid.Rows)
            {
                foreach (var cell in row.Cells)
                {
                    cell.UsedBorder = StyleBorder(cell.Box);
                    cell.Colors = null;
                    cell.Insets = Add(cell.Padding, cell.UsedBorder);
                }
            }

            return;
        }

        var rows = grid.Rows.Count;
        var vertical = new CollapsedBorder[grid.Columns + 1];
        var horizontal = new CollapsedBorder[rows + 1];
        Array.Fill(vertical, CollapsedBorder.None);
        Array.Fill(horizontal, CollapsedBorder.None);

        foreach (var row in grid.Rows)
        {
            foreach (var cell in row.Cells)
            {
                cell.Top = CollapsedBorder.Of(cell.Box.Style, BorderSide.Top, CollapsedBorder.OriginCell);
                cell.Right = CollapsedBorder.Of(cell.Box.Style, BorderSide.Right, CollapsedBorder.OriginCell);
                cell.Bottom = CollapsedBorder.Of(cell.Box.Style, BorderSide.Bottom, CollapsedBorder.OriginCell);
                cell.Left = CollapsedBorder.Of(cell.Box.Style, BorderSide.Left, CollapsedBorder.OriginCell);

                var lastColumn = Math.Min(cell.Column + cell.Span, grid.Columns);
                var lastRow = Math.Min(cell.Row + cell.RowSpan, rows);

                vertical[cell.Column] = CollapsedBorder.Resolve(vertical[cell.Column], cell.Left);
                vertical[lastColumn] = CollapsedBorder.Resolve(vertical[lastColumn], cell.Right);
                horizontal[cell.Row] = CollapsedBorder.Resolve(horizontal[cell.Row], cell.Top);
                horizontal[lastRow] = CollapsedBorder.Resolve(horizontal[lastRow], cell.Bottom);
            }
        }

        // The table's own border only competes on the outermost lines, where it takes the lowest
        // origin and therefore only wins when no cell asked for something wider.
        var style = build.Box.Style;
        vertical[0] = CollapsedBorder.Resolve(vertical[0], CollapsedBorder.Of(style, BorderSide.Left, CollapsedBorder.OriginTable));
        vertical[grid.Columns] = CollapsedBorder.Resolve(vertical[grid.Columns], CollapsedBorder.Of(style, BorderSide.Right, CollapsedBorder.OriginTable));
        horizontal[0] = CollapsedBorder.Resolve(horizontal[0], CollapsedBorder.Of(style, BorderSide.Top, CollapsedBorder.OriginTable));
        horizontal[rows] = CollapsedBorder.Resolve(horizontal[rows], CollapsedBorder.Of(style, BorderSide.Bottom, CollapsedBorder.OriginTable));

        foreach (var row in grid.Rows)
        {
            foreach (var cell in row.Cells)
            {
                var lastColumn = Math.Min(cell.Column + cell.Span, grid.Columns);
                var lastRow = Math.Min(cell.Row + cell.RowSpan, rows);

                var left = cell.Column == 0 ? vertical[0].Width : vertical[cell.Column].Width * 0.5;
                var right = lastColumn == grid.Columns ? vertical[grid.Columns].Width : vertical[lastColumn].Width * 0.5;
                var top = cell.Row == 0 ? horizontal[0].Width : horizontal[cell.Row].Width * 0.5;
                var bottom = lastRow == rows ? horizontal[rows].Width : horizontal[lastRow].Width * 0.5;

                cell.UsedBorder = new Thickness(top, right, bottom, left);
                cell.Colors = new BorderColors(
                    horizontal[cell.Row].Color,
                    vertical[lastColumn].Color,
                    horizontal[lastRow].Color,
                    vertical[cell.Column].Color);
                cell.Insets = Add(cell.Padding, cell.UsedBorder);
            }
        }
    }

    /// <summary>
    /// Measures the content of every cell to get the widths the columns have to negotiate. The
    /// suggestions are stored as border-box sizes so they can be compared with a declared width.
    /// </summary>
    private void ResolveCellIntrinsics(TableBuild build, LayoutContext context)
    {
        var basis = build.Basis;
        var cellContext = context.ForContainingBlock(new Size(0, 0));

        foreach (var row in build.Grid.Rows)
        {
            foreach (var cell in row.Cells)
            {
                var intrinsic = _engine.MeasureContentIntrinsic(cell.Box, cellContext);
                var styleInsets = StyleInsetsH(cell.Box, basis);
                var min = Math.Max(0, intrinsic.MinContent - styleInsets) + cell.Insets.Horizontal;
                var max = Math.Max(0, intrinsic.MaxContent - styleInsets) + cell.Insets.Horizontal;

                cell.MinContent = min;
                cell.MaxContent = Math.Max(min, max);
                cell.Specified = ResolveSpecified(cell.Box, basis, cell.Insets.Horizontal);
            }
        }
    }

    /// <summary>Measures each cell's height at the width its column ended up with.</summary>
    private void MeasureCellHeights(TableBuild build, LayoutContext context, Double spacing, Double? tableHeight)
    {
        var basis = build.Basis;

        foreach (var row in build.Grid.Rows)
        {
            // A row may declare a height of its own, and CSS 2.1 §17.5.3 makes it a floor rather than a
            // cap: the row ends up the taller of what it asked for and what its cells need. A per cent is
            // relative to the table's own height, so it can only be read when the table declared one;
            // against an auto height the per cent says nothing and the row takes its content instead.
            var declared = row.Box.Style.Height;

            row.Declared = declared.Kind == StyleLengthKind.Percent
                ? tableHeight is { } height ? Math.Max(0, declared.Resolve(height)) : 0
                : declared.HasValue
                    ? Math.Max(0, declared.Resolve(context.ContainingBlock.Height))
                    : 0;

            foreach (var cell in row.Cells)
            {
                var width = ColumnSpanWidth(build.Grid, cell, spacing);
                var styleInsetsH = StyleInsetsH(cell.Box, basis);
                var styleInsetsV = StyleInsetsV(cell.Box, basis);
                var margin = cell.Box.Style.Margin.Resolve(basis).Horizontal;
                var contentWidth = Math.Max(0, width - cell.Insets.Horizontal);
                var cellContext = context.ForContainingBlock(new Size(contentWidth, 0));

                // The constraint describes the border box the cell's own style would produce, so the
                // block layout inside it resolves to the content width the column settled on.
                var measured = _engine.Measure(
                    cell.Box,
                    MeasureConstraints.FitWidth(contentWidth + styleInsetsH + margin),
                    cellContext);

                var contentHeight = Math.Max(0, measured.Height - styleInsetsV);
                cell.Height = contentHeight + cell.Insets.Vertical;
            }
        }
    }

    /// <summary>Measures the captions at the table's content width and splits them by side.</summary>
    private void MeasureCaptions(TableBuild build, Double contentWidth, LayoutContext context)
    {
        build.CaptionTop = 0;
        build.CaptionBottom = 0;

        if (build.Captions.Count == 0)
        {
            return;
        }

        var captionContext = context.ForContainingBlock(new Size(contentWidth, 0));

        foreach (var caption in build.Captions)
        {
            caption.Height = _engine.Measure(caption.Box, MeasureConstraints.FitWidth(contentWidth), captionContext).Height;

            if (caption.Side == CaptionSide.Top)
            {
                build.CaptionTop += caption.Height;
            }
            else
            {
                build.CaptionBottom += caption.Height;
            }
        }
    }

    /// <summary>Reads the width hints a <c>col</c> or <c>colgroup</c> declared, in column order.</summary>
    private static void CollectColumnHints(TableBuild build, Double basis)
    {
        foreach (var child in build.Box.Children)
        {
            switch (child.Kind)
            {
                case BoxKind.TableColumn:
                    build.ColumnHints.Add(HintOf(child, basis));
                    break;

                case BoxKind.TableColumnGroup:
                    var group = HintOf(child, basis);

                    foreach (var column in child.Children)
                    {
                        if (column.Kind == BoxKind.TableColumn)
                        {
                            build.ColumnHints.Add(HintOf(column, basis) ?? group);
                        }
                    }

                    break;
            }
        }
    }

    private static Double? HintOf(Box box, Double basis) =>
        ResolveSpecified(box, basis, StyleInsetsH(box, basis));

    /// <summary>Gets a declared width as a border-box width, or null when there is none.</summary>
    private static Double? ResolveSpecified(Box box, Double basis, Double insets)
    {
        var width = box.Style.Width;

        if (!width.HasValue || (width.Kind == StyleLengthKind.Percent && basis <= 0))
        {
            return null;
        }

        var resolved = Math.Max(0, width.Resolve(basis));

        if (BoxModel.IsBorderBox(box.Style))
        {
            return resolved;
        }

        // A percentage width sizes the grid area itself: the per cent is of the table's width and
        // the insets sit inside it rather than on top of it, which is what keeps a row of cells
        // declared in per cents from adding their padding past the width the table was given.
        // A length is still a content-box width, so it carries the insets as before.
        return width.Kind == StyleLengthKind.Percent ? resolved : resolved + insets;
    }

    private static Boolean Occupied(HashSet<(Int32 Row, Int32 Column)> occupied, Int32 row, Int32 column, Int32 columnSpan, Int32 rowSpan)
    {
        for (var r = row; r < row + rowSpan; r++)
        {
            for (var c = column; c < column + columnSpan; c++)
            {
                if (occupied.Contains((r, c)))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static void Mark(HashSet<(Int32 Row, Int32 Column)> occupied, Int32 row, Int32 column, Int32 columnSpan, Int32 rowSpan)
    {
        for (var r = row; r < row + rowSpan; r++)
        {
            for (var c = column; c < column + columnSpan; c++)
            {
                occupied.Add((r, c));
            }
        }
    }

    /// <summary>Reads the <c>colspan</c> and <c>rowspan</c> attributes, defaulting to one and capped
    /// so a hostile value cannot make the slot search run away.</summary>
    private static (Int32 Columns, Int32 Rows) ReadSpans(Box cell)
    {
        if (cell.Element is not IElement element)
        {
            return (1, 1);
        }

        return (Span(element, "colspan"), Span(element, "rowspan"));
    }

    private static Int32 Span(IElement element, String name)
    {
        var text = element.GetAttribute(name);

        return Int32.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && value > 0
            ? Math.Min(value, 1000)
            : 1;
    }

    private static Boolean IsEmptyCell(Box cell)
    {
        foreach (var child in cell.Children)
        {
            if (child.Kind != BoxKind.Text || !String.IsNullOrWhiteSpace(child.Text))
            {
                return false;
            }
        }

        return true;
    }

    private static Thickness Add(Thickness left, Thickness right) => new(
        left.Top + right.Top,
        left.Right + right.Right,
        left.Bottom + right.Bottom,
        left.Left + right.Left);

    private static Thickness StyleBorder(Box box) => new(
        box.Style.BorderTopWidth,
        box.Style.BorderRightWidth,
        box.Style.BorderBottomWidth,
        box.Style.BorderLeftWidth);

    private static Double StyleInsetsH(Box box, Double basis) =>
        box.Style.Padding.Resolve(basis).Horizontal + StyleBorder(box).Horizontal;

    private static Double StyleInsetsV(Box box, Double basis) =>
        box.Style.Padding.Resolve(basis).Vertical + StyleBorder(box).Vertical;
}

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

        /// <summary>
        /// Gets the caption's own measured width. A caption takes the table's content width when it
        /// leaves its width to the content, but a declared width wins over the table - the same way a
        /// block that is wider than its container still draws at the width it asked for. The table
        /// context used to force every caption to its own content width, which crushed a caption wider
        /// than an empty table to nothing.
        /// </summary>
        public Double Width;

        /// <summary>
        /// Gets the caption's own used margins. A caption is a block box in the table's flow, so its
        /// margins apply like any block's: they shift the box and add to the space it claims along the
        /// stack. They are resolved once here against the table's content width and reused by both
        /// phases, so the height the table was measured with and the box that gets drawn agree.
        /// </summary>
        public Thickness Margin;

        /// <summary>
        /// Gets the height the caption claims in the stack: its border box plus the margin that opens
        /// it from what is above. Its bottom margin is left out on purpose - a caption sits against the
        /// grid it borders, and the space below it is the grid's own to place, not a gap the caption's
        /// outer edge pushes the rows by. Reserving it here would shove the rows down and overflow the
        /// table out of an <c>overflow: hidden</c> box that was sized to hold them.
        /// </summary>
        public Double OuterHeight => Margin.Top + Height;
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

        /// <summary>
        /// Gets the children of the table that are not table parts: a container of its own that the page
        /// wrote for a row group, say. The table holds them beside its rows rather than around them, and
        /// their height is the table's own.
        /// </summary>
        public List<Box> Others { get; } = new();

        /// <summary>Gets the height each of the table's other children claims, margin included.</summary>
        public List<Double> OtherHeights { get; } = new();

        /// <summary>Gets the margins each of the table's other children carries.</summary>
        public List<Thickness> OtherMargins { get; } = new();

        /// <summary>Gets the width each of the table's other children was measured at.</summary>
        public List<Double> OtherWidths { get; } = new();

        public List<Double?> ColumnHints { get; } = new();

        /// <summary>Gets the <c>col</c> box standing for each grid column, or null where none does.</summary>
        public List<Box?> ColumnBoxes { get; } = new();

        /// <summary>Gets the <c>colgroup</c> box standing over each grid column, or null where none does.</summary>
        public List<Box?> ColumnGroupBoxes { get; } = new();

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
        var columnTarget = Math.Max(0, contentWidth - HorizontalSpacingTotal(build));

        // A cell's per cent is a share of the room the columns have between them, so it can only be read
        // now that the table's own width is settled - and only where the table has a width of its own to
        // be a share of. A table that takes its width from its content has no whole for the share, and the
        // share is left unread rather than deciding how wide the table it sits in becomes. Rebuilding the
        // column bounds also clears the widths a column declaration asked for, so those are handed back
        // before the columns are shared out.
        if (build.Box.Style.Width.HasValue && build.Grid.ResolvePercentages(columnTarget))
        {
            build.Grid.ApplyColumnHints(build.ColumnHints);
        }

        build.Grid.SizeColumns(columnTarget, tableStyle.Fixed);
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

                default:
                    // A child that is not a table part at all - a flex container the page wrote for a
                    // row group, say - is held by the table beside its rows, not around them.
                    if (child.Kind is BoxKind.Flex or BoxKind.Grid or BoxKind.Masonry)
                    {
                        build.Others.Add(child);
                    }

                    break;
            }
        }

        // The table's first header group is rendered before every row and its first footer after them,
        // however early or late the page writes them; any further header or footer is an ordinary body
        // group and keeps its place. That is the HTML table model, and a page that writes its groups out
        // of order relies on it. The whole group moves, not a row of it.
        Box? head = null;
        Box? foot = null;

        foreach (var (_, group) in source)
        {
            if (group is null)
            {
                continue;
            }

            if (head is null && Section(group) == 0)
            {
                head = group;
            }

            if (foot is null && Section(group) == 2)
            {
                foot = group;
            }
        }

        if (head is not null || foot is not null)
        {
            var ordered = new List<(Box Box, Box? Group)>(source.Count);

            foreach (var entry in source)
            {
                if (head is not null && ReferenceEquals(entry.Group, head))
                {
                    ordered.Add(entry);
                }
            }

            foreach (var entry in source)
            {
                if (!ReferenceEquals(entry.Group, head) && !ReferenceEquals(entry.Group, foot))
                {
                    ordered.Add(entry);
                }
            }

            foreach (var entry in source)
            {
                if (foot is not null && ReferenceEquals(entry.Group, foot))
                {
                    ordered.Add(entry);
                }
            }

            source = ordered;
        }

        var occupied = new HashSet<(Int32 Row, Int32 Column)>();

        // A row settles the columns its cells reach across; a row of a single cell settles one, however
        // far that cell's span reaches. Chrome answers a table of one cell and a row whose only cell
        // spans two with one column, the same span in a row of two cells with the columns those cells
        // reach across, and a row of two cells spanning a hundred each with a hundred and one columns.
        var columns = 0;

        foreach (var (row, _) in source)
        {
            var cells = 0;
            var spans = 0;

            foreach (var child in row.Children)
            {
                if (child.Kind == BoxKind.TableCell)
                {
                    cells++;
                    spans += ReadSpans(child).Item1;
                }
            }

            columns = Math.Max(columns, cells == 1 ? 1 : spans);
        }

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

                // A span reaches at most to the table's last column, and never shortens to nothing.
                columnSpan = Math.Max(1, Math.Min(columnSpan, columns));

                while (Occupied(occupied, r, column, columnSpan, rowSpan))
                {
                    column++;
                }

                // A cell pushed sideways by a row span may still reach past the last column counted from
                // the rows' cells; the table grows then rather than dropping the cell's last columns.
                columns = Math.Max(columns, column + columnSpan);

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

            }

            // A row that names fewer cells than the grid has columns is filled to the end with anonymous
            // cells, which is what CSS 2.1 §17.2.1 asks for and what keeps a short row's edge on the same
            // grid line as the rows above it. They carry the row's style: there is no element to match a
            // cell rule against, so they have no border and the row's inherited properties.
            for (var c = 0; c < columns; c++)
            {
                if (!occupied.Contains((r, c)))
                {
                    info.Cells.Add(new TableCellInfo
                    {
                        Box = new Box(BoxKind.TableCell, info.Box.Style) { IsAnonymous = true },
                        Row = r,
                        Column = c,
                        Span = 1,
                        RowSpan = 1,
                        IsEmpty = true,
                        Padding = Thickness.Zero,
                    });
                }
            }

            info.Cells.Sort(static (left, right) => left.Column.CompareTo(right.Column));

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

    /// <summary>Gets where a row group stands in the rendering order: header, body, footer.</summary>
    private static Int32 Section(Box? group) => group?.Element?.LocalName.ToLowerInvariant() switch
    {
        "thead" => 0,
        "tfoot" => 2,
        _ => 1,
    };

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

        // The rows, row groups, columns and column groups meet the same lines the cells do (CSS 2.1
        // §17.6.2): a row's top and bottom meet the two horizontal lines it sits between, a column's
        // left and right the vertical ones, and a group's borders only the lines at its own outer
        // edges. A row's left and right reach no line but the table's outer ones, and a column's top
        // and bottom likewise: the grid keeps one line per line, so the widest of them stands for all.
        for (var r = 0; r < rows; r++)
        {
            var row = grid.Rows[r];
            var rowStyle = row.Box.Style;

            horizontal[r] = CollapsedBorder.Resolve(horizontal[r], CollapsedBorder.Of(rowStyle, BorderSide.Top, CollapsedBorder.OriginRow));
            horizontal[r + 1] = CollapsedBorder.Resolve(horizontal[r + 1], CollapsedBorder.Of(rowStyle, BorderSide.Bottom, CollapsedBorder.OriginRow));
            vertical[0] = CollapsedBorder.Resolve(vertical[0], CollapsedBorder.Of(rowStyle, BorderSide.Left, CollapsedBorder.OriginRow));
            vertical[grid.Columns] = CollapsedBorder.Resolve(vertical[grid.Columns], CollapsedBorder.Of(rowStyle, BorderSide.Right, CollapsedBorder.OriginRow));

            if (row.Group is { } group)
            {
                var first = r == 0 || !ReferenceEquals(grid.Rows[r - 1].Group, group);
                var last = r == rows - 1 || !ReferenceEquals(grid.Rows[r + 1].Group, group);

                if (first)
                {
                    horizontal[r] = CollapsedBorder.Resolve(horizontal[r], CollapsedBorder.Of(group.Style, BorderSide.Top, CollapsedBorder.OriginRowGroup));
                    vertical[0] = CollapsedBorder.Resolve(vertical[0], CollapsedBorder.Of(group.Style, BorderSide.Left, CollapsedBorder.OriginRowGroup));
                }

                if (last)
                {
                    horizontal[r + 1] = CollapsedBorder.Resolve(horizontal[r + 1], CollapsedBorder.Of(group.Style, BorderSide.Bottom, CollapsedBorder.OriginRowGroup));
                    vertical[grid.Columns] = CollapsedBorder.Resolve(vertical[grid.Columns], CollapsedBorder.Of(group.Style, BorderSide.Right, CollapsedBorder.OriginRowGroup));
                }
            }
        }

        for (var c = 0; c < grid.Columns; c++)
        {
            if (c < build.ColumnBoxes.Count && build.ColumnBoxes[c] is { } column)
            {
                vertical[c] = CollapsedBorder.Resolve(vertical[c], CollapsedBorder.Of(column.Style, BorderSide.Left, CollapsedBorder.OriginColumn));
                vertical[c + 1] = CollapsedBorder.Resolve(vertical[c + 1], CollapsedBorder.Of(column.Style, BorderSide.Right, CollapsedBorder.OriginColumn));
                horizontal[0] = CollapsedBorder.Resolve(horizontal[0], CollapsedBorder.Of(column.Style, BorderSide.Top, CollapsedBorder.OriginColumn));
                horizontal[rows] = CollapsedBorder.Resolve(horizontal[rows], CollapsedBorder.Of(column.Style, BorderSide.Bottom, CollapsedBorder.OriginColumn));
            }

            if (c < build.ColumnGroupBoxes.Count && build.ColumnGroupBoxes[c] is { } columnGroup)
            {
                var first = c == 0 || !ReferenceEquals(build.ColumnGroupBoxes[c - 1], columnGroup);
                var last = c == grid.Columns - 1 || c + 1 >= build.ColumnGroupBoxes.Count || !ReferenceEquals(build.ColumnGroupBoxes[c + 1], columnGroup);

                if (first)
                {
                    vertical[c] = CollapsedBorder.Resolve(vertical[c], CollapsedBorder.Of(columnGroup.Style, BorderSide.Left, CollapsedBorder.OriginColumnGroup));
                    horizontal[0] = CollapsedBorder.Resolve(horizontal[0], CollapsedBorder.Of(columnGroup.Style, BorderSide.Top, CollapsedBorder.OriginColumnGroup));
                }

                if (last)
                {
                    vertical[c + 1] = CollapsedBorder.Resolve(vertical[c + 1], CollapsedBorder.Of(columnGroup.Style, BorderSide.Right, CollapsedBorder.OriginColumnGroup));
                    horizontal[rows] = CollapsedBorder.Resolve(horizontal[rows], CollapsedBorder.Of(columnGroup.Style, BorderSide.Bottom, CollapsedBorder.OriginColumnGroup));
                }
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
                cell.LineStyles = new BorderStyles(
                    horizontal[cell.Row].Line,
                    vertical[lastColumn].Line,
                    horizontal[lastRow].Line,
                    vertical[cell.Column].Line);
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

                // A per-cent width is a share of the table's own width, which is not settled yet at this
                // point - a share resolved against the box the table sits in would make the table as wide
                // as its own share, so it is kept as the length it was read from and resolved later. A
                // length is read here, where the content and the cell's own margins already are.
                cell.SpecifiedLength = cell.Box.Style.Width.HasValue ? cell.Box.Style.Width : cell.Box.HintedWidth;
                cell.Specified = cell.SpecifiedLength is { Kind: StyleLengthKind.Percent }
                    ? null
                    : Pixels(cell.SpecifiedLength, basis, cell.Insets.Horizontal, BoxModel.IsBorderBox(cell.Box.Style));
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
            var declared = row.Box.Style.Height.HasValue
                ? row.Box.Style.Height
                : row.Box.HintedHeight ?? row.Box.Style.Height;

            row.HeightDeclared = declared.HasValue;

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

                // A height a cell's attribute asks for is a floor under the row, not a size for the cell
                // box: the cell is as tall as the row the floor helped settle.
                if (cell.Box.HintedHeight is { HasValue: true } hinted && hinted.Kind != StyleLengthKind.Percent)
                {
                    contentHeight = Math.Max(contentHeight, Math.Max(0, hinted.Resolve(0)));
                    row.HeightDeclared = true;
                }

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
            caption.Margin = caption.Box.Style.Margin.Resolve(contentWidth);
            var avail = Math.Max(0, contentWidth - caption.Margin.Horizontal);
            var size = _engine.Measure(caption.Box, MeasureConstraints.FitWidth(avail), captionContext);

            caption.Width = size.Width;
            caption.Height = size.Height;

            if (caption.Side == CaptionSide.Top)
            {
                build.CaptionTop += caption.OuterHeight;
            }
            else
            {
                build.CaptionBottom += caption.OuterHeight;
            }
        }
    }

    /// <summary>
    /// Measures the children of the table that are not table parts - a container of its own that the
    /// page wrote for a row group, say. They are blocks of the table's own content box, and the table
    /// is as tall as they are.
    /// </summary>
    private void MeasureOthers(TableBuild build, Double contentWidth, LayoutContext context)
    {
        if (build.Others.Count == 0)
        {
            return;
        }

        var childContext = context.ForContainingBlock(new Size(contentWidth, 0));

        foreach (var other in build.Others)
        {
            var margin = other.Style.Margin.Resolve(contentWidth);
            var avail = Math.Max(0, contentWidth - margin.Horizontal);
            var size = _engine.Measure(other, MeasureConstraints.FitWidth(avail), childContext);

            build.OtherHeights.Add(size.Height + margin.Vertical);
            build.OtherMargins.Add(margin);
            build.OtherWidths.Add(size.Width);
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
                    build.ColumnBoxes.Add(child);
                    build.ColumnGroupBoxes.Add(null);
                    break;

                case BoxKind.TableColumnGroup:
                    var group = HintOf(child, basis);

                    foreach (var column in child.Children)
                    {
                        if (column.Kind == BoxKind.TableColumn)
                        {
                            build.ColumnHints.Add(HintOf(column, basis) ?? group);
                            build.ColumnBoxes.Add(column);
                            build.ColumnGroupBoxes.Add(child);
                        }
                    }

                    break;
            }
        }
    }

    private static Double? HintOf(Box box, Double basis) =>
        ResolveSpecified(box, basis, StyleInsetsH(box, basis));

    /// <summary>Gets a declared width as a border-box width, or null when there is none.</summary>
    private static Double? ResolveSpecified(Box box, Double basis, Double insets) =>
        Pixels(box.Style.Width, basis, insets, BoxModel.IsBorderBox(box.Style));

    /// <summary>
    /// Turns a width a cell or column asked for into the border-box width the grid settles by. A per-cent
    /// width sizes the grid area itself: the per cent is of the table's width and the insets sit inside it
    /// rather than on top of it, which is what keeps a row of cells declared in per cents from adding
    /// their padding past the width the table was given. A length is still a content-box width, so it
    /// carries the insets. A per cent with no whole to be a share of yet stays unresolved.
    /// </summary>
    private static Double? Pixels(StyleLength? width, Double basis, Double insets, Boolean borderBox)
    {
        if (width is not { } length || !length.HasValue || (length.Kind == StyleLengthKind.Percent && basis <= 0))
        {
            return null;
        }

        var resolved = Math.Max(0, length.Resolve(basis));

        if (borderBox)
        {
            return resolved;
        }

        return length.Kind == StyleLengthKind.Percent ? resolved : resolved + insets;
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

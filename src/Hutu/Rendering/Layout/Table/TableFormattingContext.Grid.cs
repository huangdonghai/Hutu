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
        public Double OuterHeight => Margin.Vertical + Height;

        /// <summary>
        /// Gets the room the caption claims along the axis the table's rows run, which is the physical
        /// height of a horizontal table and its width of a vertical one: the same arithmetic as
        /// <see cref="OuterHeight"/>, read on the axis the stack runs down.
        /// </summary>
        public Double OuterBlock(Boolean vertical) =>
            (vertical ? Margin.Horizontal : Margin.Vertical) + Height;
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

        /// <summary>Gets how the table's columns and rows run, which decides which physical pair the
        /// grid's two axes are: the columns of a horizontal table run along its width and its rows
        /// along its height, and a vertical table exchanges the two.</summary>
        public WritingModeInfo Flow;

        /// <summary>Gets the table's own insets along the axis its columns run.</summary>
        public Double InsetsInline => Flow.IsVertical ? InsetsV : InsetsH;

        /// <summary>Gets the table's own insets along the axis its rows run.</summary>
        public Double InsetsBlock => Flow.IsVertical ? InsetsH : InsetsV;

        /// <summary>Gets the <c>border-spacing</c> that falls between two neighbouring columns. The
        /// pair stays physical where the grid does not: the distance between two columns is the
        /// vertical one once the columns run down the page (CSS Tables 3 §3.3).</summary>
        public Double InlineSpacing => Flow.IsVertical ? Style.VerticalSpacing : Style.HorizontalSpacing;

        /// <summary>Gets the <c>border-spacing</c> that falls between two neighbouring rows.</summary>
        public Double BlockSpacing => Flow.IsVertical ? Style.HorizontalSpacing : Style.VerticalSpacing;

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

        // Under the collapsing model the table's own border is folded into the outermost grid lines,
        // so it must not be added to the table box as well; the cells paint it instead.
        var border = tableStyle.Collapse ? Thickness.Zero : BlockLayout.BorderOf(table);

        // The same model takes the table's padding away with it (CSS 2.1 §17.6.1: "the table ... has no
        // padding" once the borders collapse), and it is the padding of the table box that goes: a cell
        // keeps its own. The width and height the author stated are therefore the grid's, plus whatever
        // the collapsed border adds, with nothing inset between the two.
        var padding = tableStyle.Collapse ? Thickness.Zero : style.Padding.Resolve(basis);

        var build = new TableBuild
        {
            Box = table,
            Style = tableStyle,
            Padding = padding,
            Border = border,
            InsetsH = padding.Horizontal + border.Horizontal,
            InsetsV = padding.Vertical + border.Vertical,
            Basis = basis,
            // The grid's two axes are the table's own: which physical extent each of them is, and
            // which way along the line it runs, the writing mode decides (CSS Writing Modes 3 §4.3,
            // §9.1).
            Flow = table.Flow,
        };

        CollectRows(build, basis);
        ResolveBorders(build);
        ResolveCellIntrinsics(build, context);
        build.Grid.Prepare();
        build.Grid.ApplyColumnHints(build.ColumnHints);
        return build;
    }

    /// <summary>
    /// Shares out the columns and measures the rows at the inline size the table settled on. When the
    /// table declared a block size greater than its rows need, the grid is grown to that size so the
    /// rows fill the box rather than leaving it empty beside them (CSS 2.1 §17.5.3).
    /// </summary>
    private void CompleteGrid(TableBuild build, Double contentInline, LayoutContext context, Double? targetBlock = null)
    {
        var tableStyle = build.Style;
        var spacing = tableStyle.Collapse ? 0.0 : build.InlineSpacing;
        var rowSpacing = tableStyle.Collapse ? 0.0 : build.BlockSpacing;
        var columnTarget = Math.Max(0, contentInline - InlineSpacingTotal(build));

        // A cell's per cent is a share of the room the columns have between them, so it can only be read
        // now that the table's own inline size is settled - and only where the table has one of its own
        // to be a share of. A table that takes that size from its content has no whole for the share, and
        // the share is left unread rather than deciding how large the table it sits in becomes.
        // Rebuilding the column bounds also clears the widths a column declaration asked for, so those
        // are handed back before the columns are shared out.
        if (InlineLength(build.Box, build.Flow.IsVertical).HasValue && build.Grid.ResolvePercentages(columnTarget))
        {
            build.Grid.ApplyColumnHints(build.ColumnHints);
        }

        build.Grid.SizeColumns(columnTarget, tableStyle.Fixed);
        MeasureCellHeights(build, context, spacing, targetBlock);
        build.Grid.SizeRows();
        build.Grid.PlaceColumns(spacing, tableStyle.Collapse);
        build.Grid.PlaceRows(rowSpacing, tableStyle.Collapse);

        if (targetBlock is { } target)
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

                // A zero rowspan is not "no rows": HTML §4.9.11 reads it as the rest of the row group the
                // cell stands in (and the rest of the table where it stands in none), which is the number
                // the reference pages state directly. Read as a span of one it made the cell's text spill
                // into the rows below it, which is what wpt's zero-rowspan-001 and -002 draw.
                if (ZeroSpan(child, "rowspan"))
                {
                    rowSpan = RestOfGroup(source, r, source[r].Group);
                }

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
        var columns = grid.Columns;
        var columnLines = new CollapsedBorder[columns + 1];
        var rowLines = new CollapsedBorder[rows + 1];
        Array.Fill(columnLines, CollapsedBorder.None);
        Array.Fill(rowLines, CollapsedBorder.None);

        // The two families of grid lines are numbered from the edges the markup's first column and
        // first row stand at, and the physical edge each of those is drawn on is the writing mode's to
        // say: down a column the line between two columns runs across the page and the line between two
        // rows runs down it (CSS Writing Modes 3 §9.1).
        var flow = build.Flow;
        var inlineStart = InlineStartSide(flow);
        var inlineEnd = InlineEndSide(flow);
        var blockStart = BlockStartSide(flow);
        var blockEnd = BlockEndSide(flow);

        foreach (var row in grid.Rows)
        {
            foreach (var cell in row.Cells)
            {
                cell.BlockStart = CollapsedBorder.Of(cell.Box.Style, blockStart, CollapsedBorder.OriginCell);
                cell.InlineEnd = CollapsedBorder.Of(cell.Box.Style, inlineEnd, CollapsedBorder.OriginCell);
                cell.BlockEnd = CollapsedBorder.Of(cell.Box.Style, blockEnd, CollapsedBorder.OriginCell);
                cell.InlineStart = CollapsedBorder.Of(cell.Box.Style, inlineStart, CollapsedBorder.OriginCell);

                var lastColumn = Math.Min(cell.Column + cell.Span, columns);
                var lastRow = Math.Min(cell.Row + cell.RowSpan, rows);

                columnLines[cell.Column] = CollapsedBorder.Resolve(columnLines[cell.Column], cell.InlineStart);
                columnLines[lastColumn] = CollapsedBorder.Resolve(columnLines[lastColumn], cell.InlineEnd);
                rowLines[cell.Row] = CollapsedBorder.Resolve(rowLines[cell.Row], cell.BlockStart);
                rowLines[lastRow] = CollapsedBorder.Resolve(rowLines[lastRow], cell.BlockEnd);
            }
        }

        // The rows, row groups, columns and column groups meet the same lines the cells do (CSS 2.1
        // §17.6.2): a row's two block sides meet the two lines it sits between, a column's two inline
        // sides the two it stands between, and a group's borders only the lines at its own outer edges.
        // A row's inline sides reach no line but the table's outer ones, and a column's block sides
        // likewise: the grid keeps one line per line, so the widest of them stands for all.
        for (var r = 0; r < rows; r++)
        {
            var row = grid.Rows[r];
            var rowStyle = row.Box.Style;

            rowLines[r] = CollapsedBorder.Resolve(rowLines[r], CollapsedBorder.Of(rowStyle, blockStart, CollapsedBorder.OriginRow));
            rowLines[r + 1] = CollapsedBorder.Resolve(rowLines[r + 1], CollapsedBorder.Of(rowStyle, blockEnd, CollapsedBorder.OriginRow));
            columnLines[0] = CollapsedBorder.Resolve(columnLines[0], CollapsedBorder.Of(rowStyle, inlineStart, CollapsedBorder.OriginRow));
            columnLines[columns] = CollapsedBorder.Resolve(columnLines[columns], CollapsedBorder.Of(rowStyle, inlineEnd, CollapsedBorder.OriginRow));

            if (row.Group is { } group)
            {
                var first = r == 0 || !ReferenceEquals(grid.Rows[r - 1].Group, group);
                var last = r == rows - 1 || !ReferenceEquals(grid.Rows[r + 1].Group, group);

                if (first)
                {
                    rowLines[r] = CollapsedBorder.Resolve(rowLines[r], CollapsedBorder.Of(group.Style, blockStart, CollapsedBorder.OriginRowGroup));
                    columnLines[0] = CollapsedBorder.Resolve(columnLines[0], CollapsedBorder.Of(group.Style, inlineStart, CollapsedBorder.OriginRowGroup));
                }

                if (last)
                {
                    rowLines[r + 1] = CollapsedBorder.Resolve(rowLines[r + 1], CollapsedBorder.Of(group.Style, blockEnd, CollapsedBorder.OriginRowGroup));
                    columnLines[columns] = CollapsedBorder.Resolve(columnLines[columns], CollapsedBorder.Of(group.Style, inlineEnd, CollapsedBorder.OriginRowGroup));
                }
            }
        }

        for (var c = 0; c < columns; c++)
        {
            if (c < build.ColumnBoxes.Count && build.ColumnBoxes[c] is { } column)
            {
                columnLines[c] = CollapsedBorder.Resolve(columnLines[c], CollapsedBorder.Of(column.Style, inlineStart, CollapsedBorder.OriginColumn));
                columnLines[c + 1] = CollapsedBorder.Resolve(columnLines[c + 1], CollapsedBorder.Of(column.Style, inlineEnd, CollapsedBorder.OriginColumn));
                rowLines[0] = CollapsedBorder.Resolve(rowLines[0], CollapsedBorder.Of(column.Style, blockStart, CollapsedBorder.OriginColumn));
                rowLines[rows] = CollapsedBorder.Resolve(rowLines[rows], CollapsedBorder.Of(column.Style, blockEnd, CollapsedBorder.OriginColumn));
            }

            if (c < build.ColumnGroupBoxes.Count && build.ColumnGroupBoxes[c] is { } columnGroup)
            {
                var first = c == 0 || !ReferenceEquals(build.ColumnGroupBoxes[c - 1], columnGroup);
                var last = c == columns - 1 || c + 1 >= build.ColumnGroupBoxes.Count || !ReferenceEquals(build.ColumnGroupBoxes[c + 1], columnGroup);

                if (first)
                {
                    columnLines[c] = CollapsedBorder.Resolve(columnLines[c], CollapsedBorder.Of(columnGroup.Style, inlineStart, CollapsedBorder.OriginColumnGroup));
                    rowLines[0] = CollapsedBorder.Resolve(rowLines[0], CollapsedBorder.Of(columnGroup.Style, blockStart, CollapsedBorder.OriginColumnGroup));
                }

                if (last)
                {
                    columnLines[c + 1] = CollapsedBorder.Resolve(columnLines[c + 1], CollapsedBorder.Of(columnGroup.Style, inlineEnd, CollapsedBorder.OriginColumnGroup));
                    rowLines[rows] = CollapsedBorder.Resolve(rowLines[rows], CollapsedBorder.Of(columnGroup.Style, blockEnd, CollapsedBorder.OriginColumnGroup));
                }
            }
        }

        // The table's own border only competes on the outermost lines, where it takes the lowest
        // origin and therefore only wins when no cell asked for something wider.
        var style = build.Box.Style;
        columnLines[0] = CollapsedBorder.Resolve(columnLines[0], CollapsedBorder.Of(style, inlineStart, CollapsedBorder.OriginTable));
        columnLines[columns] = CollapsedBorder.Resolve(columnLines[columns], CollapsedBorder.Of(style, inlineEnd, CollapsedBorder.OriginTable));
        rowLines[0] = CollapsedBorder.Resolve(rowLines[0], CollapsedBorder.Of(style, blockStart, CollapsedBorder.OriginTable));
        rowLines[rows] = CollapsedBorder.Resolve(rowLines[rows], CollapsedBorder.Of(style, blockEnd, CollapsedBorder.OriginTable));

        foreach (var row in grid.Rows)
        {
            foreach (var cell in row.Cells)
            {
                var lastColumn = Math.Min(cell.Column + cell.Span, columns);
                var lastRow = Math.Min(cell.Row + cell.RowSpan, rows);

                // Which line each physical edge of the cell stands on, and whether that line is one of
                // the table's outer edges, where the cell paints the whole of it rather than the half
                // it would share with a neighbour that is not there.
                CollapsedBorder Edge(BorderSide side) =>
                    side == inlineStart ? columnLines[cell.Column] :
                    side == inlineEnd ? columnLines[lastColumn] :
                    side == blockStart ? rowLines[cell.Row] : rowLines[lastRow];

                Boolean Outer(BorderSide side) =>
                    side == inlineStart ? cell.Column == 0 :
                    side == inlineEnd ? lastColumn == columns :
                    side == blockStart ? cell.Row == 0 : lastRow == rows;

                Double WidthOf(BorderSide side)
                {
                    var line = Edge(side).Width;
                    return Outer(side) ? line : line * 0.5;
                }

                cell.UsedBorder = new Thickness(
                    WidthOf(BorderSide.Top),
                    WidthOf(BorderSide.Right),
                    WidthOf(BorderSide.Bottom),
                    WidthOf(BorderSide.Left));
                cell.Colors = new BorderColors(
                    Edge(BorderSide.Top).Color,
                    Edge(BorderSide.Right).Color,
                    Edge(BorderSide.Bottom).Color,
                    Edge(BorderSide.Left).Color);
                cell.LineStyles = new BorderStyles(
                    Edge(BorderSide.Top).Line,
                    Edge(BorderSide.Right).Line,
                    Edge(BorderSide.Bottom).Line,
                    Edge(BorderSide.Left).Line);
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
        var vertical = build.Flow.IsVertical;
        var cellContext = context.ForContainingBlock(new Size(0, 0), vertical);

        foreach (var row in build.Grid.Rows)
        {
            foreach (var cell in row.Cells)
            {
                // A cell turned aside from its table lays its own line across the axis the columns run,
                // so what the column has to hold is the room its content takes *across* that line: the
                // height a horizontal cell's text fills in the column of a vertical table, and the
                // thickness of the single column a vertical cell lays down in a row of a horizontal one
                // (CSS Writing Modes 3 §9.2). Its inline intrinsics are the other size - the one the
                // rows negotiate over - and handing them to the column is what made a cell as tall as
                // the text it was asked to wrap.
                var intrinsic = cell.Box.Flow.IsVertical != vertical
                    ? CrossIntrinsic(cell.Box, context, build.Flow)
                    : _engine.MeasureContentIntrinsic(cell.Box, cellContext);
                var styleInsets = StyleInsets(cell.Box, basis, vertical);
                var insets = InlineInsets(cell.Insets, vertical);
                var min = Math.Max(0, intrinsic.MinContent - styleInsets) + insets;
                var max = Math.Max(0, intrinsic.MaxContent - styleInsets) + insets;

                cell.MinContent = min;
                cell.MaxContent = Math.Max(min, max);

                // A per-cent inline size is a share of the table's own inline size, which is not settled
                // yet at this point - a share resolved against the box the table sits in would make the
                // table as large as its own share, so it is kept as the length it was read from and
                // resolved later. A length is read here, where the content and the cell's own margins
                // already are.
                cell.SpecifiedLength = InlineLength(cell.Box, vertical);
                cell.Specified = cell.SpecifiedLength is { Kind: StyleLengthKind.Percent }
                    ? null
                    : Pixels(cell.SpecifiedLength, basis, insets, BoxModel.IsBorderBox(cell.Box.Style));
            }
        }
    }

    /// <summary>
    /// Gets the room a cell that reads across its table asks along the axis its columns run. The cell's
    /// own line runs the other way, so the column is not handed how wide the cell's content is but how
    /// thick it is: the extent the content takes across its own line, which is what the cell comes to
    /// when it is asked at nothing and so wraps to none (CSS Writing Modes 3 §9.2). The two
    /// suggestions are the same number for the same reason - content that is not wrapped fills just
    /// one line however little it is offered - and both are read with the cell's own insets already
    /// in them, the way <see cref="MeasureContentIntrinsic"/> answers.
    /// </summary>
    private IntrinsicSize CrossIntrinsic(Box box, LayoutContext context, WritingModeInfo flow)
    {
        // The cell's own writing mode decides which physical pair its lines run along, so it is the
        // context the content is laid out in; the answer is then read on the table's axis.
        var own = context.ForContainingBlock(new Size(0, 0), box.Flow.IsVertical);
        var laid = _engine.Measure(box, MeasureConstraints.Unconstrained, own);
        var across = Math.Max(0.0, flow.InlineExtent(laid.Width, laid.Height));

        return new IntrinsicSize(across, across);
    }

    /// <summary>Measures each cell at the size its column ended up with, along the axis the columns run.</summary>
    private void MeasureCellHeights(TableBuild build, LayoutContext context, Double spacing, Double? targetBlock)
    {
        var basis = build.Basis;
        var vertical = build.Flow.IsVertical;

        foreach (var row in build.Grid.Rows)
        {
            // A row may declare a size of its own along the axis the rows run - the physical height of a
            // horizontal table and its width of a vertical one - and CSS 2.1 §17.5.3 makes it a floor
            // rather than a cap: the row ends up the larger of what it asked for and what its cells need.
            // A per cent is relative to the table's own size on that axis, so it can only be read where
            // the table declared one; against an auto size the per cent says nothing and the row takes
            // its content instead.
            var declared = BlockLength(row.Box, vertical);

            row.HeightDeclared = declared.HasValue;

            row.Declared = declared.Kind == StyleLengthKind.Percent
                ? targetBlock is { } stated ? Math.Max(0, declared.Resolve(stated)) : 0
                : declared.HasValue
                    ? Math.Max(0, declared.Resolve(vertical ? context.ContainingBlock.Width : context.ContainingBlock.Height))
                    : 0;

            foreach (var cell in row.Cells)
            {
                var width = ColumnSpanWidth(build.Grid, cell, spacing);
                var styleInsetsBlock = StyleInsets(cell.Box, basis, !vertical);
                var contentInline = Math.Max(0, width - InlineInsets(cell.Insets, vertical));

                // The cell may be orthogonal to the table it stands in, and it is the cell's own
                // writing mode that tells the layout inside it which axis is which. What the grid has
                // settled is the extent along the axis its columns run, handed down as that physical
                // pair with the other axis left open (CSS Writing Modes 3 §9.2).
                var cellContext = context.ForContainingBlock(
                    build.Flow.SizeOf(contentInline, 0), vertical);

                // The constraint describes the border box the cell's own style would produce, so the
                // block layout inside it resolves to the size the column settled on. Down a column the
                // columns run along the physical height, so the constraint carries it there and the
                // context says so. The cell's own width and min/max declarations have already taken part
                // in that column sizing; asking them again here would resolve a percentage against the
                // cell's content box - a 33.33% cell inside a 198-pixel box measured its content at 66
                // pixels and wrapped several times, making the row taller than the content it ended up
                // drawing.
                var measured = _engine.Measure(
                    cell.Box,
                    vertical ? MeasureConstraints.FitHeight(width) : MeasureConstraints.Definite(width),
                    cellContext);

                // A cell turned aside from the table it stands in can have its own line running along
                // the axis the rows run, and a block along its own line fills the room it is given
                // rather than asking its content - given no room, it reports none, and the row with it.
                // Where the measurement did settle that axis its answer stands; where it settled
                // nothing, the size the cell stated for that axis is what the row takes, and failing
                // that the size its content asks for - the same suggestion the columns negotiate from
                // (CSS Writing Modes 3 §9.2, which is what the orthogonal cell references hold to). A
                // stated size is the cell's own used size here, so content past it overflows rather
                // than growing the row (§17.5.3 read with CSS Sizing 3 §5.2).
                // Only a cell turned aside from its table has its own line running along the axis the
                // rows run, and only there can the measurement come back with nothing because the room
                // it was given was nothing. A cell that runs the same way as its table answered for
                // itself above, and its answer stands.
                var turned = cell.Box.Flow.IsVertical != vertical;
                var measuredBlock = build.Flow.BlockExtent(measured.Width, measured.Height);
                var settled = !turned || measuredBlock > BlockInsets(cell.Insets, vertical) + Epsilon;
                var rowSized = BlockLength(cell.Box, vertical);
                var askedFor = settled
                    ? measuredBlock
                      : rowSized is { HasValue: true } named && named.Kind != StyleLengthKind.Percent
                        ? Math.Max(0, named.Resolve(0))
                        : _engine.MeasureContentIntrinsic(cell.Box, cellContext).MaxContent;

                var contentHeight = Math.Max(
                    0,
                    askedFor - styleInsetsBlock);

                // A height a cell states is a floor under the row, not a size for the cell box: the cell
                // is as tall as the row the floor helped settle. The row is one that asked for a height,
                // so a table taller than its rows leaves the room to the rows that asked for none
                // (CSS Tables 3 §5.2) - which is what keeps a fixed row beside an auto one at its size.
                // A border-box height already holds the cell's padding and border, so only what is left
                // over is content.
                if (BlockLength(cell.Box, vertical) is { HasValue: true } block && block.Kind != StyleLengthKind.Percent)
                {
                    var floor = Math.Max(0, block.Resolve(0));
                    var contentFloor = BoxModel.IsBorderBox(cell.Box.Style)
                        ? Math.Max(0, floor - BlockInsets(cell.Insets, vertical))
                        : floor;

                    contentHeight = Math.Max(contentHeight, contentFloor);
                    row.HeightDeclared = true;
                }

                // A height a cell's attribute asks for is a floor under the row, not a size for the cell
                // box either.
                if (HintedBlock(cell.Box, vertical) is { HasValue: true } hinted && hinted.Kind != StyleLengthKind.Percent)
                {
                    contentHeight = Math.Max(contentHeight, Math.Max(0, hinted.Resolve(0)));
                    row.HeightDeclared = true;
                }

                cell.Height = contentHeight + BlockInsets(cell.Insets, vertical);
            }
        }
    }

    /// <summary>Measures the captions at the table's content inline size and splits them by side.</summary>
    private void MeasureCaptions(TableBuild build, Double contentInline, LayoutContext context)
    {
        build.CaptionTop = 0;
        build.CaptionBottom = 0;

        if (build.Captions.Count == 0)
        {
            return;
        }

        var vertical = build.Flow.IsVertical;
        var captionContext = context.ForContainingBlock(build.Flow.SizeOf(contentInline, 0), vertical);

        foreach (var caption in build.Captions)
        {
            caption.Margin = caption.Box.Style.Margin.Resolve(contentInline);
            var avail = Math.Max(0, contentInline - InlineInsets(caption.Margin, vertical));
            var size = _engine.Measure(
                caption.Box,
                vertical ? MeasureConstraints.FitHeight(avail) : MeasureConstraints.FitWidth(avail),
                captionContext);

            caption.Width = build.Flow.InlineExtent(size.Width, size.Height);
            caption.Height = build.Flow.BlockExtent(size.Width, size.Height);

            if (caption.Side == CaptionSide.Top)
            {
                build.CaptionTop += caption.OuterBlock(vertical);
            }
            else
            {
                build.CaptionBottom += caption.OuterBlock(vertical);
            }
        }
    }

    /// <summary>
    /// Measures the children of the table that are not table parts - a container of its own that the
    /// page wrote for a row group, say. They are blocks of the table's own content box, and the table
    /// is as large along the row axis as they are.
    /// </summary>
    private void MeasureOthers(TableBuild build, Double contentInline, LayoutContext context)
    {
        if (build.Others.Count == 0)
        {
            return;
        }

        var vertical = build.Flow.IsVertical;
        var childContext = context.ForContainingBlock(build.Flow.SizeOf(contentInline, 0), vertical);

        foreach (var other in build.Others)
        {
            var margin = other.Style.Margin.Resolve(contentInline);
            var avail = Math.Max(0, contentInline - InlineInsets(margin, vertical));
            var size = _engine.Measure(
                other,
                vertical ? MeasureConstraints.FitHeight(avail) : MeasureConstraints.FitWidth(avail),
                childContext);

            build.OtherHeights.Add(build.Flow.BlockExtent(size.Width, size.Height) + BlockInsets(margin, vertical));
            build.OtherMargins.Add(margin);
            build.OtherWidths.Add(build.Flow.InlineExtent(size.Width, size.Height));
        }
    }

    /// <summary>
    /// Reads the width hints a <c>col</c> or <c>colgroup</c> declared, in column order. The lists are
    /// keyed by <em>column</em> rather than by element, because a <c>col span=2</c> stands for two of
    /// them and a <c>colgroup span=4</c> with no <c>col</c> inside for four (CSS 2.1 §11.2.1) - and a
    /// group that names more columns than its children cover still stands for the rest.
    /// </summary>
    private static void CollectColumnHints(TableBuild build, Double basis)
    {
        var vertical = build.Flow.IsVertical;

        foreach (var child in build.Box.Children)
        {
            switch (child.Kind)
            {
                case BoxKind.TableColumn:
                    AddColumns(build, child, null, SpanOf(child) ?? 1, basis, vertical);
                    break;

                case BoxKind.TableColumnGroup:
                    var groupHint = HintOf(child, basis, vertical);
                    var covered = 0;

                    foreach (var column in child.Children)
                    {
                        if (column.Kind != BoxKind.TableColumn)
                        {
                            continue;
                        }

                        var each = SpanOf(column) ?? 1;
                        AddColumns(build, column, child, each, basis, vertical);
                        covered += each;
                    }

                    var rest = Math.Max(0, (SpanOf(child) ?? covered) - covered);

                    for (var at = 0; at < rest; at++)
                    {
                        build.ColumnHints.Add(groupHint);
                        build.ColumnBoxes.Add(null);
                        build.ColumnGroupBoxes.Add(child);
                    }

                    break;
            }
        }
    }

    /// <summary>Gives the columns a <c>col</c> covers its hint and its boxes.</summary>
    private static void AddColumns(
        TableBuild build,
        Box column,
        Box? group,
        Int32 count,
        Double basis,
        Boolean vertical)
    {
        var hint = HintOf(column, basis, vertical);
        var groupHint = group is null ? null : HintOf(group, basis, vertical);

        for (var at = 0; at < count; at++)
        {
            build.ColumnHints.Add(hint ?? groupHint);
            build.ColumnBoxes.Add(column);
            build.ColumnGroupBoxes.Add(group);
        }
    }

    /// <summary>Reads a <c>col</c> or <c>colgroup</c> span, or null where it names none.</summary>
    private static Int32? SpanOf(Box box) =>
        box.Element?.GetAttribute("span") is { } text &&
        Int32.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) &&
        value > 0
            ? Math.Min(value, 1000)
            : null;

    private static Double? HintOf(Box box, Double basis, Boolean vertical) =>
        ResolveSpecified(box, basis, StyleInsets(box, basis, vertical), vertical);

    /// <summary>Gets a declared column-axis size as a border-box size, or null when there is none.</summary>
    private static Double? ResolveSpecified(Box box, Double basis, Double insets, Boolean vertical) =>
        Pixels(InlineLength(box, vertical), basis, insets, BoxModel.IsBorderBox(box.Style));

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

    /// <summary>
    /// Gets whether the attribute was written as a zero, which HTML gives its own meaning to rather than
    /// treating it as the attribute being absent.
    /// </summary>
    private static Boolean ZeroSpan(Box cell, String name)
    {
        var text = cell.Element?.GetAttribute(name);

        return text is not null &&
            Int32.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) &&
            value == 0;
    }

    /// <summary>
    /// Gets how many rows are left in the group a cell stands in, counting the cell's own row: the span a
    /// <c>rowspan="0"</c> asks for (HTML §4.9.11), and the rows of the whole table where the cell stands in
    /// no group.
    /// </summary>
    private static Int32 RestOfGroup(List<(Box Box, Box? Group)> source, Int32 row, Box? group)
    {
        var last = row;

        for (var index = row; index < source.Count; index++)
        {
            if (!ReferenceEquals(source[index].Group, group))
            {
                break;
            }

            last = index;
        }

        return last - row + 1;
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

    /// <summary>
    /// Gets a box's own insets along one of the table's axes. A percentage of either is read against the
    /// containing block's inline size, which is the physical width of a row and its height down a column
    /// (CSS Writing Modes 3 §9.1), and <paramref name="alongInline"/> names the column axis.
    /// </summary>
    private static Double StyleInsets(Box box, Double basis, Boolean alongInline) =>
        alongInline
            ? box.Style.Padding.Resolve(basis).Vertical + StyleBorder(box).Vertical
            : box.Style.Padding.Resolve(basis).Horizontal + StyleBorder(box).Horizontal;

    /// <summary>Gets the sum a thickness takes along the axis the columns run.</summary>
    private static Double InlineInsets(Thickness insets, Boolean vertical) =>
        vertical ? insets.Vertical : insets.Horizontal;

    /// <summary>Gets the sum a thickness takes along the axis the rows run.</summary>
    private static Double BlockInsets(Thickness insets, Boolean vertical) =>
        vertical ? insets.Horizontal : insets.Vertical;

    /// <summary>
    /// Gets the length a box states its column-axis size with: its <c>width</c>, or its <c>height</c>
    /// where the table reads down the page. An HTML attribute states the same physical extent the style
    /// sheet would, so it follows the same exchange.
    /// </summary>
    private static StyleLength InlineLength(Box box, Boolean vertical) => vertical
        ? box.Style.Height.HasValue ? box.Style.Height : box.HintedHeight ?? box.Style.Height
        : box.Style.Width.HasValue ? box.Style.Width : box.HintedWidth ?? box.Style.Width;

    /// <summary>Gets the length a box states its row-axis size with.</summary>
    private static StyleLength BlockLength(Box box, Boolean vertical) => vertical
        ? box.Style.Width.HasValue ? box.Style.Width : box.HintedWidth ?? box.Style.Width
        : box.Style.Height.HasValue ? box.Style.Height : box.HintedHeight ?? box.Style.Height;

    /// <summary>Gets the attribute that states a box's row-axis size, if one does.</summary>
    private static StyleLength? HintedBlock(Box box, Boolean vertical) =>
        vertical ? box.HintedWidth : box.HintedHeight;

    /// <summary>Gets the physical edge the inline axis starts at: where a table's first column stands.</summary>
    private static BorderSide InlineStartSide(WritingModeInfo flow) => flow.IsVertical
        ? flow.InlineSign > 0 ? BorderSide.Top : BorderSide.Bottom
        : flow.InlineSign > 0 ? BorderSide.Left : BorderSide.Right;

    /// <summary>Gets the physical edge the inline axis ends at.</summary>
    private static BorderSide InlineEndSide(WritingModeInfo flow) => flow.IsVertical
        ? flow.InlineSign > 0 ? BorderSide.Bottom : BorderSide.Top
        : flow.InlineSign > 0 ? BorderSide.Right : BorderSide.Left;

    /// <summary>Gets the physical edge the block axis starts at: where a table's first row stands.</summary>
    private static BorderSide BlockStartSide(WritingModeInfo flow) => flow.IsVertical
        ? flow.BlockSign > 0 ? BorderSide.Left : BorderSide.Right
        : BorderSide.Top;

    /// <summary>Gets the physical edge the block axis ends at.</summary>
    private static BorderSide BlockEndSide(WritingModeInfo flow) => flow.IsVertical
        ? flow.BlockSign > 0 ? BorderSide.Right : BorderSide.Left
        : BorderSide.Bottom;
}

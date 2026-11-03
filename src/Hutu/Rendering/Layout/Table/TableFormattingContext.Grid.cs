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

        /// <summary>Gets whether the table runs across the page, which turns the caption's own axes
        /// around: its block extent is then its width and the side it straddles is its height.</summary>
        public Boolean Vertical;

        /// <summary>Gets what the caption claims of the stack the table's wrapper keeps for it: its
        /// border box along the block axis plus the margin that opens it from what came before - the
        /// height and the vertical margins in a horizontal table, the width and the horizontal margins
        /// in a vertical one (CSS 2.1 §17.4 read through CSS Writing Modes 3 §4.1).</summary>
        public Double OuterBlock => Vertical ? Margin.Horizontal + Width : OuterHeight;
    }

    /// <summary>Everything the table context computes for one table, shared by both phases.</summary>
    private sealed class TableBuild
    {
        public Box Box = null!;

        public TableStyle Style;

        public TableGrid Grid = new();

        public Thickness Padding;

        public Thickness Border;

        /// <summary>Gets the outer halves of the table's top and bottom grid lines, which fall outside
        /// the rows and are carried by the table box (CSS 2.1 §17.6.2).</summary>
        public Double OuterTop;
        public Double OuterBottom;

        /// <summary>Gets the colour and the style of the table box's own outer halves.</summary>
        public BorderColors OuterColors;
        public BorderStyles OuterStyles;

        /// <summary>Gets how far the table box's own outer halves stand in from the table's edges along
        /// the inline axis, at the top and at the bottom: each half reaches only where the outermost row's
        /// own box reaches, and a row whose line on that edge is narrower than the widest one there stands
        /// in by half the difference (CSS 2.1 §17.6.2, measured:
        /// wpt/css-break/table/table-collapsed-borders-paint-at-boundary).</summary>
        public Double OuterStartTop;
        public Double OuterEndTop;
        public Double OuterStartBottom;
        public Double OuterEndBottom;

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

        /// <summary>Gets the group the table hoists to the head of its grid, or null where none does.</summary>
        public Box? HeadGroup;

        /// <summary>Gets the group the table hoists to the foot of its grid, or null where none does.</summary>
        public Box? FootGroup;
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
        };

        CollectRows(build, basis);
        ResolveBorders(build);

        // Under the collapsing model the table's own border is the outer halves of its top and bottom
        // lines. It insets the block axis only: the width the author stated already covers the left and
        // right lines, whose halves the edge cells keep (CSS 2.1 §17.5.2.2, §17.6.2).
        if (tableStyle.Collapse && build.OuterTop + build.OuterBottom > 0.001)
        {
            border = new Thickness(build.OuterTop, 0, build.OuterBottom, 0);
            build.Border = border;
            build.InsetsV = border.Vertical + padding.Vertical;
        }
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
        build.Grid.PlaceColumns(spacing, tableStyle.Collapse, build.Box.Flow.IsRightToLeft);
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
        // group and keeps its place (CSS Tables 3 §3.1). The role is the one the display computes to, so
        // a `div` told to be a `table-header-group` moves exactly as a `thead` does. The whole group
        // moves, not a row of it.
        Box? head = null;
        Box? foot = null;

        foreach (var (_, group) in source)
        {
            if (group is null)
            {
                continue;
            }

            if (head is null && RoleOf(group) == 0)
            {
                head = group;
            }

            if (foot is null && RoleOf(group) == 2)
            {
                foot = group;
            }
        }

        // The two groups the table hoists are also the only ones a fragment may copy: the copy rule
        // reads the group that stands at the head or the foot of the grid, not any further group of
        // the same role (CSS Tables 3 §6.2).
        build.HeadGroup = head;
        build.FootGroup = foot;

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
                // A row of no group at all - a bare `tr`, an anonymous row made around loose content -
                // is neither the header nor the footer, however the two of them are settled.
                var isHead = head is not null && ReferenceEquals(entry.Group, head);
                var isFoot = foot is not null && ReferenceEquals(entry.Group, foot);

                if (!isHead && !isFoot)
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

    /// <summary>Gets which of the table's three sections a row group is.</summary>
    private static Int32 RoleOf(Box? group) => TableStyle.SectionRole(group?.Style.Display);

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
        var bounds = grid.Columns + 1;
        var vertical = new CollapsedBorder[bounds];
        var horizontal = new CollapsedBorder[rows + 1];
        Array.Fill(vertical, CollapsedBorder.None);
        Array.Fill(horizontal, CollapsedBorder.None);

        // A vertical line's colour and style belong to the segment a row makes, not to the whole line:
        // the cell on one side and the cell on the other meet there for that row only, while the row,
        // the row group, the column and the table ask for the same thing along every line they cover.
        // So the cells' own edges are kept per row and the rest is kept as one candidate per line
        // (CSS 2.1 §17.6.2.1, measured: table-collapsed-borders-paint-htb-ltr, whose left line is lime
        // beside the first row and black beside the second).
        var outer = new CollapsedBorder[bounds];
        Array.Fill(outer, CollapsedBorder.None);
        var cellLeft = new CollapsedBorder[rows][];
        var cellRight = new CollapsedBorder[rows][];

        // The lines are numbered from the table's inline start, which is the physical left only when the
        // table reads left to right: a table that reads the other way meets its start line at the
        // physical right edge, and a border that is whole there - or halved with the neighbour beyond it
        // - has to be put on that side of the box. What follows keeps the pair as start and end and hands
        // the physical fields at the end (CSS 2.1 §17.6.2 with CSS Writing Modes 3 §4.3, measured in
        // Chrome: direction:rtl, three cells of 20, a 6-pixel line against a 2-pixel one - the first
        // cell reads 23 wide at the table's right edge and keeps 3 of the line, the second 23 and 3).
        var mirrored = !build.Box.Flow.IsVertical && build.Box.Flow.IsRightToLeft;

        for (var r = 0; r < rows; r++)
        {
            cellLeft[r] = new CollapsedBorder[bounds];
            cellRight[r] = new CollapsedBorder[bounds];
            Array.Fill(cellLeft[r], CollapsedBorder.None);
            Array.Fill(cellRight[r], CollapsedBorder.None);
        }

        void AskOuter(Int32 line, CollapsedBorder candidate) =>
            outer[line] = CollapsedBorder.Resolve(outer[line], candidate);

        foreach (var row in grid.Rows)
        {
            foreach (var cell in row.Cells)
            {
                cell.Top = CollapsedBorder.Of(cell.Box.Style, BorderSide.Top, CollapsedBorder.OriginCell);
                cell.Right = CollapsedBorder.Of(cell.Box.Style, BorderSide.Right, CollapsedBorder.OriginCell);
                cell.Bottom = CollapsedBorder.Of(cell.Box.Style, BorderSide.Bottom, CollapsedBorder.OriginCell);
                cell.Left = CollapsedBorder.Of(cell.Box.Style, BorderSide.Left, CollapsedBorder.OriginCell);
                
                // The style names the physical sides, while the lines are numbered along the flow: a
                // table that reads right to left meets its start line with the cell's own right border.
                var startBorder = mirrored ? cell.Right : cell.Left;
                var endBorder = mirrored ? cell.Left : cell.Right;

                var lastColumn = Math.Min(cell.Column + cell.Span, grid.Columns);
                var lastRow = Math.Min(cell.Row + cell.RowSpan, rows);

                vertical[cell.Column] = CollapsedBorder.Resolve(vertical[cell.Column], startBorder);
                vertical[lastColumn] = CollapsedBorder.Resolve(vertical[lastColumn], endBorder);
                var through = Math.Min(cell.Row + cell.RowSpan, rows);

                for (var r = cell.Row; r < through; r++)
                {
                        cellLeft[r][cell.Column] = CollapsedBorder.Resolve(cellLeft[r][cell.Column], startBorder);
                        cellRight[r][lastColumn] = CollapsedBorder.Resolve(cellRight[r][lastColumn], endBorder);
                }
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
            AskOuter(0, CollapsedBorder.Of(rowStyle, BorderSide.Left, CollapsedBorder.OriginRow));
            vertical[grid.Columns] = CollapsedBorder.Resolve(vertical[grid.Columns], CollapsedBorder.Of(rowStyle, BorderSide.Right, CollapsedBorder.OriginRow));
            AskOuter(grid.Columns, CollapsedBorder.Of(rowStyle, BorderSide.Right, CollapsedBorder.OriginRow));

            if (row.Group is { } group)
            {
                var first = r == 0 || !ReferenceEquals(grid.Rows[r - 1].Group, group);
                var last = r == rows - 1 || !ReferenceEquals(grid.Rows[r + 1].Group, group);

                if (first)
                {
                    horizontal[r] = CollapsedBorder.Resolve(horizontal[r], CollapsedBorder.Of(group.Style, BorderSide.Top, CollapsedBorder.OriginRowGroup));
                    vertical[0] = CollapsedBorder.Resolve(vertical[0], CollapsedBorder.Of(group.Style, BorderSide.Left, CollapsedBorder.OriginRowGroup));
                    AskOuter(0, CollapsedBorder.Of(group.Style, BorderSide.Left, CollapsedBorder.OriginRowGroup));
                }

                if (last)
                {
                    horizontal[r + 1] = CollapsedBorder.Resolve(horizontal[r + 1], CollapsedBorder.Of(group.Style, BorderSide.Bottom, CollapsedBorder.OriginRowGroup));
                    vertical[grid.Columns] = CollapsedBorder.Resolve(vertical[grid.Columns], CollapsedBorder.Of(group.Style, BorderSide.Right, CollapsedBorder.OriginRowGroup));
                    AskOuter(grid.Columns, CollapsedBorder.Of(group.Style, BorderSide.Right, CollapsedBorder.OriginRowGroup));
                }
            }
        }

        for (var c = 0; c < grid.Columns; c++)
        {
            if (c < build.ColumnBoxes.Count && build.ColumnBoxes[c] is { } column)
            {
                vertical[c] = CollapsedBorder.Resolve(vertical[c], CollapsedBorder.Of(column.Style, BorderSide.Left, CollapsedBorder.OriginColumn));
                AskOuter(c, CollapsedBorder.Of(column.Style, BorderSide.Left, CollapsedBorder.OriginColumn));
                vertical[c + 1] = CollapsedBorder.Resolve(vertical[c + 1], CollapsedBorder.Of(column.Style, BorderSide.Right, CollapsedBorder.OriginColumn));
                AskOuter(c + 1, CollapsedBorder.Of(column.Style, BorderSide.Right, CollapsedBorder.OriginColumn));
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
                    AskOuter(c, CollapsedBorder.Of(columnGroup.Style, BorderSide.Left, CollapsedBorder.OriginColumnGroup));
                    horizontal[0] = CollapsedBorder.Resolve(horizontal[0], CollapsedBorder.Of(columnGroup.Style, BorderSide.Top, CollapsedBorder.OriginColumnGroup));
                }

                if (last)
                {
                    vertical[c + 1] = CollapsedBorder.Resolve(vertical[c + 1], CollapsedBorder.Of(columnGroup.Style, BorderSide.Right, CollapsedBorder.OriginColumnGroup));
                    AskOuter(c + 1, CollapsedBorder.Of(columnGroup.Style, BorderSide.Right, CollapsedBorder.OriginColumnGroup));
                    horizontal[rows] = CollapsedBorder.Resolve(horizontal[rows], CollapsedBorder.Of(columnGroup.Style, BorderSide.Bottom, CollapsedBorder.OriginColumnGroup));
                }
            }
        }

        // The table's own border only competes on the outermost lines, where it takes the lowest
        // origin and therefore only wins when no cell asked for something wider.
        var style = build.Box.Style;
        vertical[0] = CollapsedBorder.Resolve(vertical[0], CollapsedBorder.Of(style, BorderSide.Left, CollapsedBorder.OriginTable));
        AskOuter(0, CollapsedBorder.Of(style, BorderSide.Left, CollapsedBorder.OriginTable));
        vertical[grid.Columns] = CollapsedBorder.Resolve(vertical[grid.Columns], CollapsedBorder.Of(style, BorderSide.Right, CollapsedBorder.OriginTable));
        AskOuter(grid.Columns, CollapsedBorder.Of(style, BorderSide.Right, CollapsedBorder.OriginTable));
        horizontal[0] = CollapsedBorder.Resolve(horizontal[0], CollapsedBorder.Of(style, BorderSide.Top, CollapsedBorder.OriginTable));
        horizontal[rows] = CollapsedBorder.Resolve(horizontal[rows], CollapsedBorder.Of(style, BorderSide.Bottom, CollapsedBorder.OriginTable));
        build.OuterTop = horizontal[0].Width * 0.5;
        build.OuterBottom = horizontal[rows].Width * 0.5;
        // The table's own box paints the outer halves of the outer lines, and which physical side a
        // line's colour stands on turns with the flow (CSS 2.1 §17.6.2, CSS Writing Modes 3 §4.3).
        build.OuterColors = mirrored
            ? new BorderColors(
                horizontal[0].Color,
                vertical[0].Color,
                horizontal[rows].Color,
                vertical[grid.Columns].Color)
            : new BorderColors(
                horizontal[0].Color,
                vertical[grid.Columns].Color,
                horizontal[rows].Color,
                vertical[0].Color);
        build.OuterStyles = mirrored
            ? new BorderStyles(
                horizontal[0].Line,
                vertical[0].Line,
                horizontal[rows].Line,
                vertical[grid.Columns].Line)
            : new BorderStyles(
                horizontal[0].Line,
                vertical[grid.Columns].Line,
                horizontal[rows].Line,
                vertical[0].Line);

        // The widest line each of the table's own two outer edges meets. The edge is one straight line,
        // so it is the widest row that decides where the edge stands, and every narrower row keeps its
        // own width centred there instead of filling the edge whole.
        var outerInlineMax = 0.0;
        var outerEndMax = 0.0;

        foreach (var row in grid.Rows)
        {
            foreach (var cell in row.Cells)
            {
                var lastColumn = Math.Min(cell.Column + cell.Span, grid.Columns);
                var lastRow = Math.Min(cell.Row + cell.RowSpan, rows);

                // The line this row's stretch settled on decides the width as well as the colour and the
                // stroke: on the table's own outer edge each row paints the whole of the line it meets
                // there, so a row whose cell asks for ten stands ten wide beside a row that asked for
                // fifteen (CSS 2.1 §17.6.2.1, measured: table-collapsed-borders-paint-at-boundary,
                // where the black row's right edge is ten wide the whole way down and the blue row's
                // fifteen belongs to that row alone).
                var startEdge = cell.Column == 0
                    ? CollapsedBorder.Resolve(outer[0], cellLeft[cell.Row][0])
                    : CollapsedBorder.Resolve(
                        CollapsedBorder.Resolve(outer[cell.Column], cellRight[cell.Row][cell.Column]),
                        cellLeft[cell.Row][cell.Column]);
                var endEdge = lastColumn == grid.Columns
                    ? CollapsedBorder.Resolve(outer[lastColumn], cellRight[cell.Row][lastColumn])
                    : CollapsedBorder.Resolve(
                        CollapsedBorder.Resolve(outer[lastColumn], cellRight[cell.Row][lastColumn]),
                        cellLeft[cell.Row][lastColumn]);

                // A stretch that asks for nothing still belongs to the line the whole of it settled on.
                if (startEdge.Width <= 0.001)
                {
                    startEdge = vertical[cell.Column];
                }

                if (endEdge.Width <= 0.001)
                {
                    endEdge = vertical[lastColumn];
                }

                var startWidth = cell.Column == 0 ? startEdge.Width : startEdge.Width * 0.5;
                var endWidth = lastColumn == grid.Columns ? endEdge.Width : endEdge.Width * 0.5;

                // Every horizontal line is shared: half stands in the row above and half in the row
                // below, the table's outer halves included - those fall outside the rows, and the table
                // box carries them (CSS 2.1 §17.6.2, measured in Chrome: a row of 50 stated with a 10px
                // line begins 5 below the caption, and its own box keeps the stated 50 with 40 of it
                // left for the content).
                var top = horizontal[cell.Row].Width * 0.5;
                var bottom = horizontal[lastRow].Width * 0.5;

                cell.UsedBorder = mirrored
                    ? new Thickness(top, startWidth, bottom, endWidth)
                    : new Thickness(top, endWidth, bottom, startWidth);

                if (cell.Column == 0)
                {
                    outerInlineMax = Math.Max(outerInlineMax, startWidth);
                }

                if (lastColumn == grid.Columns)
                {
                    outerEndMax = Math.Max(outerEndMax, endWidth);
                }

                // The colour and the style of the two vertical edges come from this row's segment of
                // the line, where the cells that meet there are counted with the candidates that run
                // the whole way down (CSS 2.1 §17.6.2.1).
                var startLine = CollapsedBorder.Resolve(
                    CollapsedBorder.Resolve(outer[cell.Column], cellRight[cell.Row][cell.Column]),
                    cellLeft[cell.Row][cell.Column]);
                var endLine = CollapsedBorder.Resolve(
                    CollapsedBorder.Resolve(outer[lastColumn], cellRight[cell.Row][lastColumn]),
                    cellLeft[cell.Row][lastColumn]);

                // Where this row's stretch of the line asks for nothing at all, the line is still one
                // line and carries the colour and the stroke the whole of it settled on: a table whose
                // first row borders the left edge and whose second borders the right keeps one green
                // line down each side (CSS 2.1 §17.6.2.1, measured: border-collapse-006, which shows
                // red through the empty stretch as soon as the segment is answered on its own).
                if (startLine.Width <= 0.001)
                {
                    startLine = vertical[cell.Column];
                }

                if (endLine.Width <= 0.001)
                {
                    endLine = vertical[lastColumn];
                }

                cell.Colors = mirrored
                    ? new BorderColors(
                        horizontal[cell.Row].Color,
                        startLine.Color,
                        horizontal[lastRow].Color,
                        endLine.Color)
                    : new BorderColors(
                        horizontal[cell.Row].Color,
                        endLine.Color,
                        horizontal[lastRow].Color,
                        startLine.Color);
                cell.LineStyles = mirrored
                    ? new BorderStyles(
                        horizontal[cell.Row].Line,
                        startLine.Line,
                        horizontal[lastRow].Line,
                        endLine.Line)
                    : new BorderStyles(
                        horizontal[cell.Row].Line,
                        endLine.Line,
                        horizontal[lastRow].Line,
                        startLine.Line);
                cell.Insets = Add(cell.Padding, cell.UsedBorder);
            }
        }

        // Where a row's own line on the table's outer edge is narrower than the widest one there, the
        // difference halves out on that side: the cell's box stands in by it, and the border painted
        // whole inside the box that is left lands on the line the widest row makes (CSS 2.1 §17.6.2,
        // measured: wpt/css-break/table/table-collapsed-borders-paint-at-boundary, whose 10-pixel lines
        // begin 2.5 in from a column whose widest line is 15, on both of its edges - and Chrome's own
        // cell rects say the same: every cell of that table reads 77 wide at columnLeft + 7.5, while the
        // table itself reads 92 wide at the column).
        foreach (var row in grid.Rows)
        {
            foreach (var cell in row.Cells)
            {
                var lastColumn = Math.Min(cell.Column + cell.Span, grid.Columns);

                cell.InlineStartInset = cell.Column == 0
                    ? Math.Max(0.0, (outerInlineMax - (mirrored ? cell.UsedBorder.Right : cell.UsedBorder.Left)) * 0.5)
                    : 0.0;
                cell.InlineEndInset = lastColumn == grid.Columns
                    ? Math.Max(0.0, (outerEndMax - (mirrored ? cell.UsedBorder.Left : cell.UsedBorder.Right)) * 0.5)
                    : 0.0;

                // The table's own outer half of a horizontal line runs with the row it stands against:
                // the first row decides where the top half begins and ends, the last row the bottom one.
                if (cell.Row != 0 && cell.Row != rows - 1)
                {
                    continue;
                }

                ref var start = ref cell.Row == 0 ? ref build.OuterStartTop : ref build.OuterStartBottom;
                ref var end = ref cell.Row == 0 ? ref build.OuterEndTop : ref build.OuterEndBottom;

                if (cell.Column == 0)
                {
                    start = cell.InlineStartInset;
                }

                if (lastColumn == grid.Columns)
                {
                    end = cell.InlineEndInset;
                }
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

        // The column a cell stands in is its extent along the axis the table sets its cells on, which is
        // the page's y for a table that runs across it: what the cell states for that axis, what it holds
        // along it, and the thicknesses that go around it all belong to that pair (CSS Writing Modes 3
        // §4.1, measured: wpt/css-break/table/table-col-paint-vlr-rtl, whose two columns of a 100-long
        // table come to 35 each, the 30 of border-spacing included).
        var vertical = build.Box.Flow.IsVertical;

        foreach (var row in build.Grid.Rows)
        {
            foreach (var cell in row.Cells)
            {
                var intrinsic = _engine.MeasureContentIntrinsic(cell.Box, cellContext);
                var styleInsets = vertical ? StyleInsetsV(cell.Box, basis) : StyleInsetsH(cell.Box, basis);
                var inlineInsets = vertical ? cell.Insets.Vertical : cell.Insets.Horizontal;
                var min = Math.Max(0, intrinsic.MinContent - styleInsets) + inlineInsets;
                var max = Math.Max(0, intrinsic.MaxContent - styleInsets) + inlineInsets;

                // A cell that runs aside from its table turns the question over: the track it stands in
                // is measured along the axis the table sets its cells on, which for this cell is the axis
                // its content stacks on. What the column wants is therefore how far the cell reaches
                // along its own block axis between the narrowest and the widest stretch of its line, not
                // how long that line is; reading the length for the reach left each cell shorter than
                // the content it was given and made two identical cells of one row come back unequal
                // (measured in Chrome: two horizontal-tb cells of one line of 18.4 with padding-block 2
                // and 3 in a vertical-lr table stand 23 down the page each, and the table comes to 46).
                if (vertical != cell.Box.Flow.IsVertical)
                {
                    var aside = cell.Box.Flow.IsVertical;

                    // The pair above is reckoned in the table's axes; this cell answers in its own.
                    var ownStyle = aside ? StyleInsetsV(cell.Box, basis) : StyleInsetsH(cell.Box, basis);
                    var ownInsets = aside ? cell.Insets.Vertical : cell.Insets.Horizontal;
                    var narrow = Math.Max(0, intrinsic.MinContent - ownStyle) + ownInsets;
                    var wide = Math.Max(0, intrinsic.MaxContent - ownStyle) + ownInsets;

                    // Squeeze along the line the cell itself writes, and read the reach across it.
                    var atNarrow = _engine.Measure(
                        cell.Box,
                        aside
                            ? MeasureConstraints.FitHeight(narrow)
                            : MeasureConstraints.FitWidth(narrow),
                        cellContext);
                    var atWide = _engine.Measure(
                        cell.Box,
                        aside
                            ? MeasureConstraints.FitHeight(wide)
                            : MeasureConstraints.FitWidth(wide),
                        cellContext);

                    min = Math.Max(0, aside ? atNarrow.Width : atNarrow.Height);
                    max = Math.Max(0, aside ? atWide.Width : atWide.Height);
                }

                cell.MinContent = min;
                cell.MaxContent = Math.Max(min, max);

                // A per-cent width is a share of the table's own width, which is not settled yet at this
                // point - a share resolved against the box the table sits in would make the table as wide
                // as its own share, so it is kept as the length it was read from and resolved later. A
                // length is read here, where the content and the cell's own margins already are.
                cell.SpecifiedLength = vertical
                    ? cell.Box.Style.Height.HasValue
                        ? cell.Box.Style.Height
                        : cell.Box.HintedHeight ?? cell.Box.Style.Height
                    : cell.Box.Style.Width.HasValue
                        ? cell.Box.Style.Width
                        : cell.Box.HintedWidth;
                cell.Specified = cell.SpecifiedLength is { Kind: StyleLengthKind.Percent }
                    ? null
                    : Pixels(cell.SpecifiedLength, basis, inlineInsets, BoxModel.IsBorderBox(cell.Box.Style));
            }
        }
    }

    /// <summary>Measures each cell's extent along the axis the rows stack on, at the room its column left
    /// it along the axis they run.</summary>
    private void MeasureCellHeights(TableBuild build, LayoutContext context, Double spacing, Double? tableHeight)
    {
        var basis = build.Basis;

        // What a row or a cell states for the axis the rows stack on is its block size, and the style
        // keeps that under the physical name the writing mode puts the axis on: the width of a table that
        // runs across the page, its height of one that runs down it. The measure asks the same question
        // of the other pair of axes, so a table that runs across the page is measured with the room its
        // columns leave fixed along y and reads its answer along x (CSS Logical 1 §2, §4).
        var vertical = build.Box.Flow.IsVertical;
        var blockInsets = new Func<Thickness, Double>(
            insets => vertical ? insets.Horizontal : insets.Vertical);

        foreach (var row in build.Grid.Rows)
        {
            // A row may declare a height of its own, and CSS 2.1 §17.5.3 makes it a floor rather than a
            // cap: the row ends up the taller of what it asked for and what its cells need. A per cent is
            // relative to the table's own height, so it can only be read when the table declared one;
            // against an auto height the per cent says nothing and the row takes its content instead.
            var declared = vertical
                ? row.Box.Style.Width.HasValue
                    ? row.Box.Style.Width
                    : row.Box.HintedWidth ?? row.Box.Style.Width
                : row.Box.Style.Height.HasValue
                ? row.Box.Style.Height
                : row.Box.HintedHeight ?? row.Box.Style.Height;

            row.HeightDeclared = declared.HasValue;

            row.Declared = declared.Kind == StyleLengthKind.Percent
                ? tableHeight is { } height ? Math.Max(0, declared.Resolve(height)) : 0
                : declared.HasValue
                    ? Math.Max(0, declared.Resolve(
                        vertical ? context.ContainingBlock.Width : context.ContainingBlock.Height))
                    : 0;

            foreach (var cell in row.Cells)
            {
                var inline = ColumnSpanWidth(build.Grid, cell, spacing);
                var styleInsetsInline = vertical ? StyleInsetsV(cell.Box, basis) : StyleInsetsH(cell.Box, basis);
                var styleInsetsBlock = vertical ? StyleInsetsH(cell.Box, basis) : StyleInsetsV(cell.Box, basis);
                var margin = vertical
                    ? cell.Box.Style.Margin.Resolve(basis).Vertical
                    : cell.Box.Style.Margin.Resolve(basis).Horizontal;
                var contentInline = Math.Max(
                    0,
                    inline - (vertical ? cell.Insets.Vertical : cell.Insets.Horizontal));
                var cellContext = context.ForContainingBlock(
                    vertical ? new Size(0, contentInline) : new Size(contentInline, 0));

                // A cell that runs the way its table runs reaches along the block axis with the band its
                // content makes, and the measure of it is asked with the room the column leaves along the
                // inline axis. A cell turned aside from its table runs by its own writing mode instead:
                // what it brings to the row's thickness is then the extent its content *asks for* along
                // that axis - its own inline size - rather than a share of the room it is handed, which
                // for a row whose thickness the table has not settled yet would read as nothing
                // (CSS Writing Modes 3 §4.1, measured: wpt/css-writing-modes/table-cell-valign-001, three
                // horizontal cells of Ahem in a vertical-lr table, each 102 across the page and 102 long).
                Double contentHeight;

                if (vertical != cell.Box.Flow.IsVertical)
                {
                    // The extent is asked along the cell's own inline axis, and the sizes it states for
                    // that axis hold of it: a `max-height` on a cell that runs down the page inside one
                    // that runs across it caps the row's thickness the way `max-width` would cap a
                    // horizontal one (CSS Writing Modes 3 §4.1, measured: the same page, where "xx xx"
                    // of 50-pixel Ahem in a vertical cell would make 250 of the row and its
                    // `max-height: 100px` leaves 100 with the text wrapped into two columns).
                    var cellVertical = cell.Box.Flow.IsVertical;
                    var across = _engine.MeasureContentIntrinsic(cell.Box, cellContext).MaxContent;
                    var borderBox = BoxModel.IsBorderBox(cell.Box.Style);
                    var wanted = BoxModel.ContentBound(
                        cellVertical ? cell.Box.Style.Height : cell.Box.Style.Width,
                        basis,
                        styleInsetsBlock,
                        borderBox) ?? across;

                    if (BoxModel.ContentBound(
                        cellVertical ? cell.Box.Style.MaxHeight : cell.Box.Style.MaxWidth,
                        basis,
                        styleInsetsBlock,
                        borderBox) is { } cap)
                    {
                        wanted = Math.Min(wanted, cap);
                    }

                    if (BoxModel.ContentBound(
                        cellVertical ? cell.Box.Style.MinHeight : cell.Box.Style.MinWidth,
                        basis,
                        styleInsetsBlock,
                        borderBox) is { } floor)
                    {
                        wanted = Math.Max(wanted, floor);
                    }

                    contentHeight = Math.Max(0, wanted);
                }
                else
                {
                    // The constraint describes the border box the column settled, so the block layout
                    // inside it resolves to that width rather than to the one the cell's own style would
                    // produce: the cell's width and min/max have already taken part in the column sizing,
                    // and asking them again resolves its percentage against its own content box - a
                    // 33.33% cell inside a 198-pixel box measured its content at 66 pixels and wrapped
                    // several times, making the row taller than the content it drew (CSS Tables 3 §4.5,
                    // measured: gecko/table/percentage-*.html and the two names in TableLayoutTests). A
                    // table that reads down the page settles its cells along the physical height, which
                    // MeasureConstraints has no definite room for yet, so that axis keeps the old ask.
                    var measured = _engine.Measure(
                        cell.Box,
                        vertical
                            ? MeasureConstraints.FitHeight(contentInline + styleInsetsInline + margin)
                            : MeasureConstraints.Definite(inline),
                        cellContext);

                    contentHeight = Math.Max(
                        0,
                        (vertical ? measured.Width : measured.Height) - styleInsetsBlock);
                }

                // A height a cell states is a floor under the row, not a size for the cell box: the cell
                // is as tall as the row the floor helped settle. The row is one that asked for a height,
                // so a table taller than its rows leaves the room to the rows that asked for none
                // (CSS Tables 3 §5.2) - which is what keeps a fixed row beside an auto one at its size.
                // A border-box height already holds the cell's padding and border, so only what is left
                // over is content.
                if ((vertical ? cell.Box.Style.Width : cell.Box.Style.Height) is { HasValue: true } stated &&
                    stated.Kind != StyleLengthKind.Percent)
                {
                    var floor = Math.Max(0, stated.Resolve(0));
                    var contentFloor = BoxModel.IsBorderBox(cell.Box.Style)
                        ? Math.Max(0, floor - blockInsets(cell.Insets))
                        : floor;

                    contentHeight = Math.Max(contentHeight, contentFloor);
                    row.HeightDeclared = true;
                }

                // A height a cell's attribute asks for is a floor under the row, not a size for the cell
                // box either.
                if ((vertical ? cell.Box.HintedWidth : cell.Box.HintedHeight) is { HasValue: true } hinted &&
                    hinted.Kind != StyleLengthKind.Percent)
                {
                    contentHeight = Math.Max(contentHeight, Math.Max(0, hinted.Resolve(0)));
                    row.HeightDeclared = true;
                }

                cell.Height = contentHeight + blockInsets(cell.Insets);
            }
        }
    }

    /// <summary>Measures the captions at the table's extent along its inline axis and splits them by
    /// side. What a caption claims of the stack is its block extent - the width of the box in a table
    /// that runs across the page - and the side it straddles is its inline extent, which a caption left
    /// to its own content fills (CSS Writing Modes 3 §4.1, measured: table-grid-paint-vrl-rtl, whose
    /// 140-long caption shows 100 of itself in each column and is 100 wide across the column it stands
    /// in, not the nothing an empty caption of a horizontal table would make of it).</summary>
    private void MeasureCaptions(TableBuild build, Double inlineExtent, LayoutContext context)
    {
        build.CaptionTop = 0;
        build.CaptionBottom = 0;

        if (build.Captions.Count == 0)
        {
            return;
        }

        var vertical = build.Box.Flow.IsVertical;
        var captionContext = context.ForContainingBlock(
            vertical ? new Size(0, inlineExtent) : new Size(inlineExtent, 0));

        foreach (var caption in build.Captions)
        {
            caption.Vertical = vertical;
            caption.Margin = caption.Box.Style.Margin.Resolve(inlineExtent);

            // Percentages of the caption's own box read the containing block's inline size, which is the
            // table's height when the table runs across the page.
            var avail = Math.Max(
                0,
                inlineExtent - (vertical ? caption.Margin.Vertical : caption.Margin.Horizontal));
            var size = _engine.Measure(
                caption.Box,
                vertical
                    ? MeasureConstraints.Unconstrained.WithHeight(avail)
                    : MeasureConstraints.FitWidth(avail),
                captionContext);

            caption.Width = size.Width;
            caption.Height = vertical ? Math.Max(size.Height, avail) : size.Height;

            if (caption.Side == CaptionSide.Top)
            {
                build.CaptionTop += caption.OuterBlock;
            }
            else
            {
                build.CaptionBottom += caption.OuterBlock;
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
    /// <remarks>
    /// A <c>col</c> or <c>colgroup</c> with a <c>span</c> stands for that many columns, and each of them
    /// takes the box's width and paints its background (HTML §4.9.11, measured:
    /// gecko/table-background/table-col-span-1).
    /// </remarks>
    private static void CollectColumnHints(TableBuild build, Double basis)
    {
        foreach (var child in build.Box.Children)
        {
            switch (child.Kind)
            {
                case BoxKind.TableColumn:
                    var hint = HintOf(child, basis);
                    var span = child.Element is IElement element ? Span(element, "span") : 1;

                    for (var i = 0; i < span; i++)
                    {
                        build.ColumnHints.Add(hint);
                        build.ColumnBoxes.Add(child);
                        build.ColumnGroupBoxes.Add(null);
                    }

                    break;

                case BoxKind.TableColumnGroup:
                    var group = HintOf(child, basis);
                    var groups = child.Element is IElement groupElement ? Span(groupElement, "span") : 1;

                    // A group that names its columns does not also claim the ones its span counts: the
                    // children are the columns, and what is left of the span stands for no column of its
                    // own (HTML 4.9.11.5 read the way the agent lays the band out, measured in Chrome:
                    // `<colgroup span=3><col span=2></colgroup>` over three columns of 20 pixels gives
                    // both the group and the col a band of 40, not 60). Only a group with no `col`
                    // children lays out as many columns as its span names.
                    var named = child.Children.Exists(c => c.Kind == BoxKind.TableColumn);

                    if (named)
                    {
                        foreach (var column in child.Children)
                        {
                            if (column.Kind != BoxKind.TableColumn)
                            {
                                continue;
                            }

                            var own = HintOf(column, basis);
                            var columns = column.Element is IElement columnElement
                                ? Span(columnElement, "span")
                                : 1;

                            for (var i = 0; i < columns; i++)
                            {
                                build.ColumnHints.Add(own ?? group);
                                build.ColumnBoxes.Add(column);
                                build.ColumnGroupBoxes.Add(child);
                            }
                        }
                    }
                    else
                    {
                        for (var g = 0; g < groups; g++)
                        {
                            build.ColumnHints.Add(group);
                            build.ColumnBoxes.Add(null);
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

    private static Double StyleInsetsH(Box box, Double basis) =>
        box.Style.Padding.Resolve(basis).Horizontal + StyleBorder(box).Horizontal;

    private static Double StyleInsetsV(Box box, Double basis) =>
        box.Style.Padding.Resolve(basis).Vertical + StyleBorder(box).Vertical;
}

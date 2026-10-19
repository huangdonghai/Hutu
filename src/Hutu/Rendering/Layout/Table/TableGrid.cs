using Hutu.Rendering.Boxes;
using System;
using System.Collections.Generic;

namespace Hutu.Rendering.Layout;

/// <summary>
/// One cell as it sits in the table grid: the rectangle it occupies in grid coordinates, the widths
/// it negotiated, and the four collapsed borders that will be painted on its edges.
/// </summary>
internal sealed class TableCellInfo
{
    public Box Box = null!;

    public Int32 Row;

    public Int32 Column;

    /// <summary>Gets or sets how many columns the cell spans (<c>colspan</c>).</summary>
    public Int32 Span = 1;

    /// <summary>Gets or sets how many rows the cell spans (<c>rowspan</c>).</summary>
    public Int32 RowSpan = 1;

    /// <summary>Gets or sets the content's min-content inline size, used by the automatic layout.</summary>
    public Double MinContent;

    /// <summary>Gets or sets the content's max-content inline size, used by the automatic layout.</summary>
    public Double MaxContent;

    /// <summary>Gets or sets the declared width as a border-box size, or <c>null</c> when there is
    /// none. It is a border-box size so that it can be compared with the min-content and max-content
    /// suggestions, which the grid also holds as border-box sizes. A per-cent width has none until the
    /// table's own width is known, and is resolved then.</summary>
    public Double? Specified;

    /// <summary>Gets or sets the length the specified width was read from, per cent included.</summary>
    public StyleLength? SpecifiedLength;

    /// <summary>Gets or sets the used border-box height.</summary>
    public Double Height;

    /// <summary>Gets or sets the cell's padding.</summary>
    public Thickness Padding;

    /// <summary>Gets or sets the border actually painted on the cell. In the collapsing model this is
    /// half of the border that won on each edge, so that two adjacent cells share one line.</summary>
    public Thickness UsedBorder;

    /// <summary>Gets or sets the insets of the cell's content: padding plus <see cref="UsedBorder"/>.</summary>
    public Thickness Insets;

    /// <summary>Gets or sets the colours of <see cref="UsedBorder"/>, when the collapsing model moved
    /// them away from the cell's own style.</summary>
    public BorderColors? Colors;

    /// <summary>Gets or sets whether the cell has no content, which <c>empty-cells</c> acts on.</summary>
    public Boolean IsEmpty = true;

    /// <summary>Gets or sets the border that wins on each edge in the collapsing model.</summary>
    public CollapsedBorder Top;
    public CollapsedBorder Right;
    public CollapsedBorder Bottom;
    public CollapsedBorder Left;
}

/// <summary>One row of the grid, with its height and the cells that start in it.</summary>
internal sealed class TableRowInfo
{
    public Box Box = null!;

    /// <summary>Gets or sets the row group the row belongs to, or <c>null</c> when it is a direct child.</summary>
    public Box? Group;

    public List<TableCellInfo> Cells { get; } = new();

    /// <summary>Gets or sets the used border-box height.</summary>
    public Double Height;

    /// <summary>
    /// Gets or sets the height the row itself declared, as opposed to the one its cells ask for. A row
    /// is as tall as the larger of the two, which is what CSS 2.1 §17.5.3 says: a declared height is a
    /// floor under the cells, not a cap over them.
    /// </summary>
    public Double Declared;

    /// <summary>
    /// Gets or sets whether the row or any of its cells asked for a height. The surplus of a table
    /// taller than its rows goes to the rows that asked for none, which is how a row asked to be 15
    /// pixels stays 15 while the rows around it take up the rest.
    /// </summary>
    public Boolean HeightDeclared;

    /// <summary>Gets or sets the border-box offset from the grid origin.</summary>
    public Double Y;
}

/// <summary>
/// The resolved table grid: the column widths, the row heights, and where every cell sits.
/// </summary>
/// <remarks>
/// The grid is built once per layout pass and then queried by both phases, so measuring and arranging
/// cannot disagree about a column width. The order of the steps is what the two border models force:
/// the structure comes first, because the collapsing model sizes the table's own border from the
/// cells' borders; only then can the table's content width be known, and only then can the columns be
/// shared out and the rows measured.
/// </remarks>
internal sealed class TableGrid
{
    /// <summary>Gets or sets the number of columns. Set before <see cref="Prepare"/>.</summary>
    public Int32 Columns;

    /// <summary>Gets the width each column needs at its narrowest.</summary>
    public Double[] Minimum = [];

    /// <summary>Gets the width each column wants when nothing forces it to wrap.</summary>
    public Double[] Maximum = [];

    /// <summary>Gets the used width of each column, which is the width of a cell's border box.</summary>
    public Double[] Widths = [];

    /// <summary>
    /// Gets the width each column was declared to have, from a cell's <c>width</c> or a <c>col</c>
    /// element, or <c>null</c> where nothing was declared. The fixed layout keeps these and shares
    /// what is left equally, so it has to tell a declared column from one that merely has content.
    /// </summary>
    public Double?[] Declared = [];

    /// <summary>Gets the offset of each column's border box from the grid origin.</summary>
    public Double[] ColumnX = [];

    public List<TableRowInfo> Rows { get; } = new();

    /// <summary>Gets the captions in document order.</summary>
    public List<Box> Captions { get; } = new();

    /// <summary>Gets or sets the height of the grid area, excluding the table's own insets.</summary>
    public Double Height;

    /// <summary>Gets or sets the width of the grid area, excluding the table's own insets.</summary>
    public Double Width;

    /// <summary>Allocates the per-column arrays and derives the min-content and max-content bounds.</summary>
    public void Prepare()
    {
        Minimum = new Double[Columns];
        Maximum = new Double[Columns];
        Widths = new Double[Columns];
        ColumnX = new Double[Columns];
        Declared = new Double?[Columns];

        var spanning = new List<TableCellInfo>();

        foreach (var row in Rows)
        {
            foreach (var cell in row.Cells)
            {
                if (cell.Span == 1)
                {
                    Minimum[cell.Column] = Math.Max(Minimum[cell.Column], cell.MinContent);
                    Maximum[cell.Column] = Math.Max(Maximum[cell.Column], cell.MaxContent);

                    if (cell.Specified is { } specified)
                    {
                        // A declared width fixes the column: its used width is the declared size,
                        // clamped up to the content minimum, and it never absorbs extra space. The
                        // floor is recorded on Minimum; the ceiling is pinned to the same value so the
                        // column cannot grow past what was asked for. It is remembered in Declared so
                        // that the fixed layout treats it as the column's whole width and the automatic
                        // layout keeps it out of the extra-space sharing.
                        Minimum[cell.Column] = Math.Max(Minimum[cell.Column], specified);
                        Maximum[cell.Column] = Minimum[cell.Column];
                        Declared[cell.Column] = Math.Max(Declared[cell.Column] ?? 0, specified);
                    }
                }
                else
                {
                    spanning.Add(cell);
                }
            }
        }

        // Cells that span several columns are folded in from the narrowest span outwards, so a
        // <c>colspan="2"</c> cell cannot claim more than its share of the columns it covers. The row
        // and column settle what the span alone cannot: a sort that only compares the span trades
        // equal spans about, and which column a cell's width was spread over then depends on how many
        // cells the table has.
        spanning.Sort(static (left, right) =>
            left.Span != right.Span
                ? left.Span.CompareTo(right.Span)
                : left.Row != right.Row
                    ? left.Row.CompareTo(right.Row)
                    : left.Column.CompareTo(right.Column));

        foreach (var cell in spanning)
        {
            Spread(Minimum, cell, cell.MinContent);
            Spread(Maximum, cell, cell.MaxContent);
        }
    }

    /// <summary>
    /// Resolves the widths a cell declared in per cents against the width the columns are shared out of,
    /// and rebuilds the column bounds when any was waiting. The per cent is of the room the columns have
    /// between them - what is left of the table's content width once the distance between columns is
    /// taken out - which is the basis Chrome measures a cell's per cent against.
    /// </summary>
    public Boolean ResolvePercentages(Double basis)
    {
        if (basis <= 0)
        {
            return false;
        }

        var any = false;

        foreach (var row in Rows)
        {
            foreach (var cell in row.Cells)
            {
                if (cell.SpecifiedLength is { Kind: StyleLengthKind.Percent } length)
                {
                    // A per-cent width sizes the grid area itself, insets included, so nothing is added
                    // to it here.
                    cell.Specified = Math.Max(0, length.Resolve(basis));
                    any = true;
                }
            }
        }

        if (any)
        {
            Prepare();
        }

        return any;
    }

    /// <summary>Gets the narrowest width the columns can take between them.</summary>
    public Double SumMinimum() => Sum(Minimum);

    /// <summary>Gets the width the columns want when nothing forces them to wrap.</summary>
    public Double SumMaximum() => Sum(Maximum);

    /// <summary>
    /// Shares the space the columns are allowed to occupy. In the fixed model the declared widths are
    /// kept and the rest is split evenly; in the automatic model the columns are interpolated between
    /// their min-content and max-content sizes, so a table with room to spare grows each column in
    /// proportion to how much it wanted.
    /// </summary>
    public void SizeColumns(Double available, Boolean fixedLayout)
    {
        if (Columns == 0)
        {
            return;
        }

        var target = Math.Max(0, available);

        if (fixedLayout)
        {
            DistributeFixed(target);
            return;
        }

        var sumMin = SumMinimum();
        var sumMax = SumMaximum();

        // Narrower than the columns can go: they take their minimum and the table overflows, which is
        // the one case where the table's width is not the width it was offered.
        if (target <= sumMin)
        {
            for (var i = 0; i < Columns; i++)
            {
                Widths[i] = Minimum[i];
            }

            return;
        }

        if (target <= sumMax)
        {
            var stretch = sumMax - sumMin;

            if (stretch <= 0.0001)
            {
                // Every column already wants all the room it can get, so there is nothing to
                // interpolate between; the maximum is the answer.
                for (var i = 0; i < Columns; i++)
                {
                    Widths[i] = Maximum[i];
                }

                return;
            }

            var room = target - sumMin;

            for (var i = 0; i < Columns; i++)
            {
                Widths[i] = Minimum[i] + (room * (Maximum[i] - Minimum[i]) / stretch);
            }

            return;
        }

        // Room to spare: the declared columns already hold their asked-for width, so the leftover
        // space goes only to the columns without a declared width, split between them in proportion
        // to the room each wanted (its max-content). Declared columns never grow.
        var extra = target - sumMax;
        var autoMax = 0.0;
        var autoCount = 0;

        for (var i = 0; i < Columns; i++)
        {
            if (Declared[i] is null)
            {
                autoMax += Maximum[i];
                autoCount++;
            }
        }

        // A table whose every column was given a width has no column left to take the room up, and
        // Chrome shares it over the columns that asked for one in proportion to what each asked for -
        // it is how a lone column declared at one per cent still fills the table it stands in. A column
        // left to itself takes the room even when it wants none of its own: an empty cell with no
        // padding keeps the rest of the row beside the column declared at one per cent.
        if (autoCount == 0)
        {
            for (var i = 0; i < Columns; i++)
            {
                Widths[i] = Maximum[i] + (sumMax > 0 ? extra * Maximum[i] / sumMax : extra / Columns);
            }

            return;
        }

        for (var i = 0; i < Columns; i++)
        {
            if (Declared[i] is null)
            {
                var share = autoMax > 0 ? extra * Maximum[i] / autoMax : extra / autoCount;
                Widths[i] = Maximum[i] + share;
            }
            else
            {
                Widths[i] = Maximum[i];
            }
        }
    }

    /// <summary>
    /// Positions the columns relative to the grid origin. The separated model puts the border spacing
    /// around and between the columns; the collapsing model has none, because the borders are centred
    /// on the grid lines instead. The grid's width therefore includes the spacing on both outer edges.
    /// </summary>
    public void PlaceColumns(Double spacing, Boolean collapse)
    {
        var cursor = collapse ? 0.0 : spacing;

        for (var i = 0; i < Columns; i++)
        {
            ColumnX[i] = cursor;
            cursor += Widths[i] + spacing;
        }

        Width = cursor;
    }

    /// <summary>
    /// Raises the minimum and maximum of the columns that a <c>col</c> or <c>colgroup</c> declared a
    /// width for. A column declaration is a floor, not a cap, so it never narrows a column below what
    /// its cells need.
    /// </summary>
    public void ApplyColumnHints(IReadOnlyList<Double?> hints)
    {
        var last = Math.Min(Columns, hints.Count);

        for (var i = 0; i < last; i++)
        {
            if (hints[i] is not { } hint)
            {
                continue;
            }

            Minimum[i] = Math.Max(Minimum[i], hint);
            Maximum[i] = Minimum[i];
            Declared[i] = Math.Max(Declared[i] ?? 0, hint);
        }
    }

    /// <summary>
    /// Gives the rows their heights: each row is as tall as the larger of what it declared and what its
    /// tallest single-row cell asks for, and a cell that spans several rows then pushes any deficit onto
    /// them.
    /// </summary>
    public void SizeRows()
    {
        foreach (var row in Rows)
        {
            var height = row.Declared;

            foreach (var cell in row.Cells)
            {
                if (cell.RowSpan == 1)
                {
                    height = Math.Max(height, cell.Height);
                }
            }

            row.Height = height;
        }

        var maximumSpan = 1;

        foreach (var row in Rows)
        {
            foreach (var cell in row.Cells)
            {
                maximumSpan = Math.Max(maximumSpan, cell.RowSpan);
            }
        }

        for (var span = 2; span <= maximumSpan; span++)
        {
            foreach (var row in Rows)
            {
                foreach (var cell in row.Cells)
                {
                    if (cell.RowSpan != span || cell.Row + span > Rows.Count)
                    {
                        continue;
                    }

                    var current = 0.0;

                    for (var i = cell.Row; i < cell.Row + span; i++)
                    {
                        current += Rows[i].Height;
                    }

                    SpreadRows(cell.Row, span, cell.Height - current);
                }
            }
        }
    }

    /// <summary>
    /// Raises the rows by a surplus height. A table that declared a height taller than its cells wanted
    /// is that tall, and CSS 2.1 §17.5.3 leaves how the surplus is shared unspecified; an equal share
    /// over the rows is what keeps a table of empty cells even, which is the shape the suite tests.
    /// </summary>
    public void StretchRows(Double surplus)
    {
        if (Rows.Count == 0 || surplus <= 0.0001)
        {
            return;
        }

        var open = 0;

        foreach (var row in Rows)
        {
            if (!row.HeightDeclared)
            {
                open++;
            }
        }

        // A row that asked for a height keeps it while any row that asked for none is left to take the
        // room; a table whose rows all asked for a height shares the surplus over all of them equally.
        var share = surplus / (open > 0 ? open : Rows.Count);

        foreach (var row in Rows)
        {
            if (open == 0 || !row.HeightDeclared)
            {
                row.Height += share;
            }
        }
    }

    /// <summary>Stacks the rows, leaving the given spacing between them in the separated model.</summary>
    public void PlaceRows(Double spacing, Boolean collapse)
    {
        var cursor = collapse ? 0.0 : spacing;

        foreach (var row in Rows)
        {
            row.Y = cursor;
            cursor += row.Height + spacing;
        }

        Height = cursor;
    }

    /// <summary>Adds the deficit of a spanning cell to the columns it covers, in equal shares.</summary>
    private void Spread(Double[] sizes, TableCellInfo cell, Double wanted)
    {
        var last = Math.Min(cell.Column + cell.Span, sizes.Length);
        var current = 0.0;

        for (var i = cell.Column; i < last; i++)
        {
            current += sizes[i];
        }

        var deficit = wanted - current;

        if (deficit <= 0 || cell.Span <= 0)
        {
            return;
        }

        var share = deficit / cell.Span;

        for (var i = cell.Column; i < last; i++)
        {
            sizes[i] += share;
        }
    }

    private void SpreadRows(Int32 start, Int32 span, Double deficit)
    {
        if (deficit <= 0)
        {
            return;
        }

        var share = deficit / span;

        for (var i = start; i < start + span && i < Rows.Count; i++)
        {
            Rows[i].Height += share;
        }
    }

    private void DistributeFixed(Double target)
    {
        var specified = Declared;
        var count = 0;

        foreach (var value in specified)
        {
            if (value is not null)
            {
                count++;
            }
        }

        var total = 0.0;

        for (var i = 0; i < Columns; i++)
        {
            if (specified[i] is { } value)
            {
                total += value;
            }
        }

        var remaining = target - total;
        var flexible = Columns - count;

        for (var i = 0; i < Columns; i++)
        {
            Widths[i] = specified[i] ?? (flexible > 0 ? Math.Max(0, remaining / flexible) : 0);
        }

        if (flexible == 0 && remaining > 0)
        {
            // Every column was declared and the table is wider still: the surplus is shared out.
            var share = remaining / Columns;

            for (var i = 0; i < Columns; i++)
            {
                Widths[i] += share;
            }
        }
    }

    private Double Sum(Double[] values)
    {
        var total = 0.0;

        for (var i = 0; i < Columns && i < values.Length; i++)
        {
            total += values[i];
        }

        return total;
    }
}

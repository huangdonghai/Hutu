using System;
using System.Collections.Generic;

namespace Hutu.Rendering.Layout;

/// <summary>
/// The named cells of <c>grid-template-areas</c>: which rectangle of the grid each name owns, which is
/// what lets an item say <c>grid-area: toolbar</c> instead of counting lines.
/// </summary>
/// <remarks>
/// A template area is a rectangle of repeated names, one quoted string per row, with a <c>.</c> standing
/// for a cell no name claims. Each area in turn gives two names to the lines around it -
/// <c>name-start</c> and <c>name-end</c> - and the bare name refers to the starting one, which is what
/// an item asking for <c>toolbar</c> resolves against on both axes.
/// <para>
/// Rows that disagree about how many cells they have make the whole declaration invalid, so this returns
/// nothing rather than laying out a ragged grid; a name that does not form a rectangle is dropped on its
/// own, because the rest of the template can still be honoured.
/// </para>
/// </remarks>
internal sealed class GridAreas
{
    private readonly String?[,] _cells;

    private GridAreas(String?[,] cells)
    {
        _cells = cells;
        RowCount = cells.GetLength(0);
        ColumnCount = cells.GetLength(1);
    }

    /// <summary>Gets how many rows the template names, which is how many row tracks it creates.</summary>
    public Int32 RowCount { get; }

    /// <summary>Gets how many columns the template names, which is how many column tracks it creates.</summary>
    public Int32 ColumnCount { get; }

    /// <summary>
    /// Reads the value of <c>grid-template-areas</c>, or of the area block of <c>grid-template</c> once
    /// the cascade has split it into its longhands. <c>none</c> and anything unparseable yield no
    /// template at all, which leaves the placement to the automatic algorithm as it was.
    /// </summary>
    public static GridAreas? Parse(String? text)
    {
        if (String.IsNullOrWhiteSpace(text) || text.Equals("none", StringComparison.Ordinal))
        {
            return null;
        }

        var rows = new List<String?[]>();
        var width = 0;

        for (var at = 0; at < text.Length; at++)
        {
            var quote = text[at];

            if (quote is not ('"' or '\''))
            {
                continue;
            }

            var close = text.IndexOf(quote, at + 1);

            if (close < 0)
            {
                return null;
            }

            var cells = new List<String?>();

            foreach (var cell in text[(at + 1)..close].Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                cells.Add(cell.Equals(".", StringComparison.Ordinal) ? null : cell.ToLowerInvariant());
            }

            if (cells.Count == 0)
            {
                return null;
            }

            if (width == 0)
            {
                width = cells.Count;
            }
            else if (cells.Count != width)
            {
                return null;
            }

            rows.Add(cells.ToArray());
            at = close;
        }

        if (rows.Count == 0)
        {
            return null;
        }

        var cells2 = new String?[rows.Count, width];

        for (var row = 0; row < rows.Count; row++)
        {
            for (var column = 0; column < width; column++)
            {
                cells2[row, column] = rows[row][column];
            }
        }

        return new GridAreas(cells2);
    }

    /// <summary>
    /// Gets the lines the area named <paramref name="name"/> sits between, one-based and in the same
    /// shape as a written line number: the area's own starting line and the line after its last track.
    /// </summary>
    public Boolean TryBounds(String name, out Int32 columnStart, out Int32 columnEnd, out Int32 rowStart, out Int32 rowEnd)
    {
        var firstRow = -1;
        var lastRow = -1;
        var firstColumn = Int32.MaxValue;
        var lastColumn = -1;
        var rows = _cells.GetLength(0);
        var columns = _cells.GetLength(1);

        for (var row = 0; row < rows; row++)
        {
            for (var column = 0; column < columns; column++)
            {
                if (!String.Equals(_cells[row, column], name, StringComparison.Ordinal))
                {
                    continue;
                }

                if (firstRow < 0)
                {
                    firstRow = row;
                }

                // The leftmost cell of the area is not necessarily in the row it first appears on - a
                // template like `x a` over `a a` starts one column further right than it ends up
                // spanning - so each edge keeps its own extreme.
                firstColumn = Math.Min(firstColumn, column);
                lastRow = Math.Max(lastRow, row);
                lastColumn = Math.Max(lastColumn, column);
            }
        }

        columnStart = columnEnd = rowStart = rowEnd = 0;

        if (firstRow < 0)
        {
            return false;
        }

        // A name scattered over the grid in a shape that is not a rectangle owns no area: the
        // specification invalidates the whole declaration for it, and dropping that one name is the
        // gentler half of that, since nothing else in the template was wrong.
        var reached = (lastRow - firstRow + 1) * (lastColumn - firstColumn + 1);
        var counted = 0;

        for (var row = firstRow; row <= lastRow; row++)
        {
            for (var column = firstColumn; column <= lastColumn; column++)
            {
                if (String.Equals(_cells[row, column], name, StringComparison.Ordinal))
                {
                    counted++;
                }
            }
        }

        if (counted != reached)
        {
            return false;
        }

        columnStart = firstColumn + 1;
        columnEnd = lastColumn + 2;
        rowStart = firstRow + 1;
        rowEnd = lastRow + 2;
        return true;
    }
}

/// <summary>
/// The names written in front of and after the tracks of one axis - <c>[side-start] 200px [side-end]</c>
/// - which is the other half of what an item's <c>grid-column: side-start</c> can refer to.
/// </summary>
/// <remarks>
/// Taken from the track list's text rather than its parsed value, because the line names are exactly
/// what the track classifier walks past: they are not tracks, and nothing keeps them once the value has
/// been read as sizes.
/// </remarks>
internal sealed class GridLineNames
{
    private readonly String[][] _lines;

    private GridLineNames(List<List<String>> lines) => _lines = Array.ConvertAll(lines.ToArray(), list => list.ToArray());

    /// <summary>
    /// Reads the bracketed names out of a track list, one entry per line - which is always one more than
    /// the tracks, even where nothing was named.
    /// </summary>
    public static GridLineNames? Parse(String? text)
    {
        if (String.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var lines = new List<List<String>> { new() };
        var tracks = 0;

        // A group of names belongs to the line the next track starts at, which is the one after any
        // track already read - so the first group after a track opens the line it names rather than
        // adding to the line before it.
        var passed = false;
        var at = 0;

        while (at < text.Length)
        {
            var c = text[at];

            if (c == '[')
            {
                var close = text.IndexOf(']', at + 1);

                if (close < 0)
                {
                    return null;
                }

                if (passed)
                {
                    lines.Add(new List<String>());
                    passed = false;
                }

                foreach (var name in text[(at + 1)..close].Split(' ', StringSplitOptions.RemoveEmptyEntries))
                {
                    lines[^1].Add(name.ToLowerInvariant());
                }

                at = close + 1;
                continue;
            }

            if (Char.IsWhiteSpace(c))
            {
                at++;
                continue;
            }

            // A track begins here: it reaches to the next space outside its own parentheses, which is
            // what keeps `minmax(0, 600px)` one token.
            var depth = 0;

            while (at < text.Length)
            {
                var c2 = text[at];

                if (c2 == '(')
                {
                    depth++;
                }
                else if (c2 == ')')
                {
                    depth--;
                }
                else if (Char.IsWhiteSpace(c2) && depth <= 0)
                {
                    break;
                }

                at++;
            }

            tracks++;
            passed = true;
        }

        return tracks == 0 ? null : new GridLineNames(lines);
    }

    /// <summary>Gets the first line named <paramref name="name"/>, one-based, or null for no such line.</summary>
    public Int32? IndexOf(String name)
    {
        for (var at = 0; at < _lines.Length; at++)
        {
            if (Array.IndexOf(_lines[at], name) >= 0)
            {
                return at + 1;
            }
        }

        return null;
    }
}

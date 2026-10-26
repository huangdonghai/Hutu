using AngleSharp.Css;
using AngleSharp.Css.Dom;
using AngleSharp.Css.Values;
using System;
using System.Collections.Generic;

namespace Hutu.Rendering.Layout;

/// <summary>How a single track breadth was written.</summary>
internal enum GridBreadthKind : byte
{
    /// <summary><c>auto</c>: the breadth comes from the items.</summary>
    Auto,

    /// <summary>An absolute length, already converted to pixels.</summary>
    Length,

    /// <summary>A percentage of the container's content box. The value is a fraction, so 50% is 0.5.</summary>
    Percent,

    /// <summary>A share of the free space, as in <c>1fr</c>.</summary>
    Fraction,

    /// <summary><c>min-content</c>.</summary>
    MinContent,

    /// <summary><c>max-content</c>.</summary>
    MaxContent,

    /// <summary><c>fit-content()</c>, whose value is the limit in pixels.</summary>
    FitContent,
}

/// <summary>One side of a track size, after the value has been classified.</summary>
internal readonly struct GridBreadth
{
    public GridBreadth(GridBreadthKind kind, Double value)
    {
        Kind = kind;
        Value = value;
    }

    public GridBreadthKind Kind { get; }

    /// <summary>Gets the pixels for a length, the fraction for a percentage, or the factor for an fr.</summary>
    public Double Value { get; }

    public Boolean IsFraction => Kind == GridBreadthKind.Fraction;

    /// <summary>Gets whether the breadth is known without measuring any item.</summary>
    public Boolean IsDefinite => Kind is GridBreadthKind.Length or GridBreadthKind.Percent;

    /// <summary>Gets whether the breadth is one of the content keywords, or <c>auto</c>.</summary>
    public Boolean IsIntrinsic =>
        Kind is GridBreadthKind.Auto or GridBreadthKind.MinContent or GridBreadthKind.MaxContent or GridBreadthKind.FitContent;

    /// <summary>Gets the breadth once <paramref name="basis"/> is known, for the definite kinds.</summary>
    public Double Fixed(Double basis) => Kind switch
    {
        GridBreadthKind.Length => Value,
        GridBreadthKind.Percent => basis * Value,
        _ => 0,
    };

    public override String ToString() => Kind switch
    {
        GridBreadthKind.Length => Value.ToString("0.##") + "px",
        GridBreadthKind.Percent => (Value * 100).ToString("0.##") + "%",
        GridBreadthKind.Fraction => Value.ToString("0.##") + "fr",
        GridBreadthKind.MinContent => "min-content",
        GridBreadthKind.MaxContent => "max-content",
        GridBreadthKind.FitContent => "fit-content(" + Value.ToString("0.##") + ")",
        _ => "auto",
    };
}

/// <summary>A track's minimum and maximum breadth, which is what a track sizing function really is.</summary>
internal readonly struct GridTrackSize
{
    public GridTrackSize(GridBreadth min, GridBreadth max)
    {
        Min = min;
        Max = max;
    }

    public GridBreadth Min { get; }

    public GridBreadth Max { get; }

    /// <summary>A track with no declared size, which takes its size from its items.</summary>
    public static GridTrackSize Auto => new(
        new GridBreadth(GridBreadthKind.Auto, 0),
        new GridBreadth(GridBreadthKind.Auto, 0));

    /// <summary>
    /// Expands a single breadth into a track sizing function. A lone <c>fr</c> is
    /// <c>minmax(auto, Nfr)</c> and a lone <c>fit-content()</c> is <c>minmax(auto, fit-content())</c>;
    /// every other breadth is its own minimum and maximum.
    /// </summary>
    public static GridTrackSize From(GridBreadth breadth) => breadth.Kind switch
    {
        GridBreadthKind.Fraction or GridBreadthKind.FitContent =>
            new GridTrackSize(new GridBreadth(GridBreadthKind.Auto, 0), breadth),
        _ => new GridTrackSize(breadth, breadth),
    };

    public override String ToString() => Min.Kind == Max.Kind && Min.Value == Max.Value
        ? Max.ToString()
        : "minmax(" + Min + ", " + Max + ")";
}

/// <summary>
/// A track list, with any <c>repeat(auto-fill, ...)</c> or <c>repeat(auto-fit, ...)</c> kept apart from
/// the fixed tracks around it. The number of repetitions depends on the container's width, which is
/// not known while the style is being read, so the list has to be spliced together later.
/// </summary>
internal sealed class GridTrackList
{
    private readonly List<GridTrackSize> _tracks = new();
    private RepeatCount _repeat = RepeatCount.Unknown;
    private Int32 _repeatIndex = -1;

    /// <summary>
    /// Gets or sets whether the list is the <c>subgrid</c> keyword: the tracks are the ones the parent
    /// grid gave the area this box spans, rather than any written here (CSS Grid 2 §8).
    /// </summary>
    public Boolean Subgrid { get; set; }

    /// <summary>Gets how many tracks the list holds before the automatic repetition is spliced in.</summary>
    public Int32 StaticCount => _tracks.Count;

    public Boolean HasAutoRepeat => _repeat.IsKnown && _repeat.Tracks.Count > 0;

    /// <summary>Gets whether the repetition collapses the tracks no item reaches.</summary>
    public Boolean IsAutoFit => HasAutoRepeat && _repeat.IsAutoFit;

    /// <summary>
    /// Gets where the automatic repetition stands, and how many tracks it fills, in the list spliced out
    /// in full. These are the tracks an <c>auto-fit</c> repetition may collapse; the tracks the list holds
    /// beside them are written out by the page, and what an item reaches past the whole list is an
    /// implicit track, which nothing collapses (CSS Grid 1 §7.2.3).
    /// </summary>
    public (Int32 From, Int32 Count) Repeated(Double basis, Double gap) =>
        _repeatIndex < 0 || !HasAutoRepeat
            ? (0, 0)
            : (_repeatIndex, Repetitions(basis, gap) * _repeat.Tracks.Count);

    /// <summary>Adds one explicit track at the end of the list.</summary>
    public void Add(GridTrackSize track) => _tracks.Add(track);

    /// <summary>Records an automatic repetition together with the position it sits at.</summary>
    public void Repeat(String kind, IReadOnlyList<GridTrackSize> repeated)
    {
        _repeatIndex = _tracks.Count;
        _repeat = new RepeatCount(kind, repeated);
    }

    /// <summary>
    /// Gets how many tracks the list defines before any item is placed. This is the count the
    /// placement algorithm sees as the explicit grid, so an <c>auto-fit</c> repetition is counted in
    /// full here and only narrowed later, once it is known which of them an item reaches.
    /// </summary>
    public Int32 ExplicitCount(Double basis, Double gap) =>
        _repeatIndex < 0 || !HasAutoRepeat
            ? _tracks.Count
            : _tracks.Count + (Repetitions(basis, gap) * _repeat.Tracks.Count);

    /// <summary>
    /// Splices the automatic repetition in for a container whose content box is <paramref name="basis"/>
    /// wide, with <paramref name="gap"/> between tracks. <paramref name="used"/> is how many tracks the
    /// items actually reach: where the room to fill is not settled, that is as far as an <c>auto-fit</c>
    /// repetition goes, since nothing is known of the tracks past it. Where the room is settled, the
    /// items are already placed and the caller drops the tracks among them that stand empty.
    /// </summary>
    public IReadOnlyList<GridTrackSize> Resolve(Double basis, Double gap, Int32 used)
    {
        if (_repeatIndex < 0 || !HasAutoRepeat)
        {
            return _tracks;
        }

        var repetitions = Repetitions(basis, gap);

        if (_repeat.IsAutoFit)
        {
            repetitions = Math.Min(repetitions, Math.Max(1, used));
        }

        var resolved = new List<GridTrackSize>(_tracks.Count + (repetitions * _repeat.Tracks.Count));

        for (var index = 0; index < _repeatIndex; index++)
        {
            resolved.Add(_tracks[index]);
        }

        for (var copy = 0; copy < repetitions; copy++)
        {
            resolved.AddRange(_repeat.Tracks);
        }

        for (var index = _repeatIndex; index < _tracks.Count; index++)
        {
            resolved.Add(_tracks[index]);
        }

        return resolved;
    }

    /// <summary>
    /// Counts how many repetitions fit. Each copy is as wide as the definite minimums of the tracks
    /// it holds, which is the part of a repetition that does not depend on the space it is given.
    /// </summary>
    private Int32 Repetitions(Double basis, Double gap)
    {
        if (basis <= 0)
        {
            return 1;
        }

        var perRepeat = 0.0;

        foreach (var track in _repeat.Tracks)
        {
            if (track.Min.IsDefinite)
            {
                perRepeat += Math.Max(0, track.Min.Fixed(basis));
            }
        }

        if (perRepeat <= 0)
        {
            // Nothing about the repetition is definite, so no finite number of copies can be said to
            // fit; one is the only answer that does not overflow.
            return 1;
        }

        return Math.Max(1, (Int32)Math.Floor((basis + gap) / (perRepeat + gap)));
    }
}

/// <summary>
/// The tracks a subgrid takes from the grid area it spans (CSS Grid 2 §8): the parent's resolved sizes
/// for the tracks the area covers, the gutters it left between them, and the names it wrote on the
/// lines they stand between. The sizes stand in the same order the area's columns or rows do, and the
/// gutter is the parent's step, which includes any room content alignment added.
/// </summary>
internal sealed record SubgridTracks(
    IReadOnlyList<Double> Columns,
    IReadOnlyList<Double> Rows,
    Double ColumnGap,
    Double RowGap,
    GridLineNames? ColumnLines = null,
    GridLineNames? RowLines = null);

/// <summary>How a grid line was written, which is what decides an item's placement.</summary>
internal enum GridLineKind : byte
{
    /// <summary><c>auto</c>: the placement algorithm decides.</summary>
    Auto,

    /// <summary>A line number, counting from one, or backwards from the end when negative.</summary>
    Index,

    /// <summary><c>span N</c>: the item covers a number of tracks from wherever it lands.</summary>
    Span,

    /// <summary>A named line. Named lines are parsed but not resolved, so they place like <c>auto</c>.</summary>
    Name,
}

/// <summary>One side of an item's placement: where it starts, or how far it reaches.</summary>
internal readonly struct GridLine
{
    private GridLine(GridLineKind kind, Int32 index, Int32 span, String? name)
    {
        Kind = kind;
        Index = index;
        Span = span;
        Name = name;
    }

    public GridLineKind Kind { get; }

    /// <summary>Gets the line number as written, so a negative number still counts from the end.</summary>
    public Int32 Index { get; }

    public Int32 Span { get; }

    public String? Name { get; }

    /// <summary>Gets which line of the ones carrying the name the page asked for, as <c>a 8</c> does.</summary>
    public Int32 Repeat => Kind == GridLineKind.Name ? Index : 1;

    public static GridLine Auto => new(GridLineKind.Auto, 0, 1, null);

    public static GridLine At(Int32 index) => new(GridLineKind.Index, index, 1, null);

    public static GridLine Spans(Int32 span) => new(GridLineKind.Span, 0, Math.Max(1, span), null);

    public static GridLine Named(String name, Int32 repeat = 1) => new(GridLineKind.Name, repeat, 1, name);

    /// <summary>
    /// Resolves the line to a one-based number, wrapping a negative number from the end so that
    /// <c>-1</c> is the last line of the explicit grid.
    /// </summary>
    /// <remarks>
    /// A negative number past the first line would, in a browser, land in the implicit grid before the
    /// explicit one. This model keeps no lines there, so the wrap is held to the first line rather than
    /// allowed to run past it into a number no track list index can carry.
    /// </remarks>
    public Int32? Resolve(Int32 lineCount) => Kind switch
    {
        GridLineKind.Index when Index > 0 => Index,
        GridLineKind.Index => Math.Max(1, lineCount + 1 + Index),
        _ => null,
    };

    /// <summary>
    /// Resolves the line to a one-based number only when it names a line of the explicit grid, and to
    /// null when it falls outside it. An absolutely positioned grid child never builds implicit tracks,
    /// so a line outside the explicit grid is treated as <c>auto</c> instead (CSS Grid 2 §10.1), which
    /// is what makes a negative index past the first line name the container's padding edge.
    /// </summary>
    public Int32? ResolveWithin(Int32 lineCount)
    {
        var line = Kind switch
        {
            GridLineKind.Index when Index > 0 => Index,
            GridLineKind.Index => lineCount + 1 + Index,
            _ => (Int32?)null,
        };

        return line is { } number && number >= 1 && number <= lineCount ? number : null;
    }

    public override String ToString() => Kind switch
    {
        GridLineKind.Index => Index.ToString(),
        GridLineKind.Span => "span " + Span.ToString(),
        GridLineKind.Name => Name ?? "auto",
        _ => "auto",
    };
}

/// <summary>
/// The grid longhands of a box, read without the shorthands. The declarations serve both roles: a
/// container reads the tracks, the flow and the alignment, and an item reads its own placement and
/// its own alignment through the same struct.
/// </summary>
/// <remarks>
/// The <c>grid</c> and <c>grid-template</c> shorthands are still not read - the cascade splits them
/// into the longhands this does read before anything is looked at, which is what makes a page whose
/// frame is written as <c>grid-template: "toolbar side" 1fr / ... </c> legible here at all.
/// <c>grid-template-areas</c> is read as text, and so are the names written in front of the tracks,
/// because a track list's line names are exactly what the track classifier walks past: they are not
/// tracks. An item placed by a name is resolved against the two of them by <see cref="Resolve"/>;
/// a name nothing answers stays unresolved and places like <c>auto</c>, which is the same answer as
/// before there were any names to read.
/// </remarks>
internal readonly struct GridStyle
{
    private GridStyle(
        GridTrackList columns,
        GridTrackList rows,
        GridTrackSize autoColumn,
        GridTrackSize autoRow,
        Boolean autoFlowColumn,
        Boolean dense,
        FlexAlignment justifyContent,
        FlexAlignment alignContent,
        FlexAlignment justifyItems,
        FlexAlignment alignItems,
        Double rowGap,
        Double columnGap,
        StyleLength rowGapLength,
        StyleLength columnGapLength,
        Boolean rowGapStated,
        Boolean columnGapStated,
        GridAreas? areas,
        GridLineNames? columnLines,
        GridLineNames? rowLines)
    {
        Columns = columns;
        Rows = rows;
        AutoColumn = autoColumn;
        AutoRow = autoRow;
        AutoFlowColumn = autoFlowColumn;
        Dense = dense;
        JustifyContent = justifyContent;
        AlignContent = alignContent;
        JustifyItems = justifyItems;
        AlignItems = alignItems;
        RowGap = rowGap;
        ColumnGap = columnGap;
        RowGapLength = rowGapLength;
        ColumnGapLength = columnGapLength;
        RowGapStated = rowGapStated;
        ColumnGapStated = columnGapStated;
        Areas = areas;
        ColumnLines = columnLines;
        RowLines = rowLines;
    }

    /// <summary>Gets the explicit column tracks, with a fixed <c>repeat()</c> already expanded.</summary>
    public GridTrackList Columns { get; }

    /// <summary>Gets the explicit row tracks, with a fixed <c>repeat()</c> already expanded.</summary>
    public GridTrackList Rows { get; }

    /// <summary>Gets whether the columns are the parent's, as <c>grid-template-columns: subgrid</c> says.</summary>
    public Boolean ColumnsSubgrid => Columns.Subgrid;

    /// <summary>Gets whether the rows are the parent's, as <c>grid-template-rows: subgrid</c> says.</summary>
    public Boolean RowsSubgrid => Rows.Subgrid;

    /// <summary>Gets the size of a column created past the explicit grid.</summary>
    public GridTrackSize AutoColumn { get; }

    /// <summary>Gets the size of a row created past the explicit grid.</summary>
    public GridTrackSize AutoRow { get; }

    /// <summary>Gets whether the automatic placement fills columns before rows.</summary>
    public Boolean AutoFlowColumn { get; }

    /// <summary>Gets whether the automatic placement backfills holes rather than advancing past them.</summary>
    public Boolean Dense { get; }

    public FlexAlignment JustifyContent { get; }

    public FlexAlignment AlignContent { get; }

    public FlexAlignment JustifyItems { get; }

    public FlexAlignment AlignItems { get; }

    public Double RowGap { get; }

    public Double ColumnGap { get; }

    /// <summary>
    /// Gets the column gutter taken off the width the tracks are laid in. A gap written as a percentage
    /// or a <c>min()</c> of one is a share of the box the tracks fill (CSS Box Alignment 3 §8), which is
    /// only known once the box is being laid out, so the length is kept apart from the pixels it answers
    /// with when the style is read.
    /// </summary>
    public Double ColumnGapAgainst(Double basis) =>
        ColumnGapLength.Kind is StyleLengthKind.Percent or StyleLengthKind.Math
            ? Math.Max(0, ColumnGapLength.Resolve(basis))
            : ColumnGap;

    /// <summary>Gets the row gutter taken off the height the rows are laid in.</summary>
    public Double RowGapAgainst(Double basis) =>
        RowGapLength.Kind is StyleLengthKind.Percent or StyleLengthKind.Math
            ? Math.Max(0, RowGapLength.Resolve(basis))
            : RowGap;

    /// <summary>The column gap as it was written, kept so a percentage can be resolved later.</summary>
    public StyleLength ColumnGapLength { get; }

    /// <summary>The row gap as it was written.</summary>
    public StyleLength RowGapLength { get; }

    /// <summary>
    /// Gets whether the row gap names a gutter of its own rather than leaving <c>normal</c> behind.
    /// Only a subgrid asks: <c>normal</c> there means the parent's gutter, so the difference the
    /// subgrid's own tracks are cut by is nothing (CSS Grid 2, subgrid gutters).
    /// </summary>
    public Boolean RowGapStated { get; }

    /// <summary>Gets whether the column gap names a gutter of its own rather than leaving it <c>normal</c>.</summary>
    public Boolean ColumnGapStated { get; }

    /// <summary>Gets the named cells of the template, or null when it names none.</summary>
    public GridAreas? Areas { get; }

    /// <summary>Gets the names written on the column lines, or null when none were written.</summary>
    public GridLineNames? ColumnLines { get; }

    /// <summary>Gets the names written on the row lines, or null when none were written.</summary>
    public GridLineNames? RowLines { get; }

    /// <summary>
    /// The longhands' initial values: what a style that was never handed a grid context answers
    /// with. A fused style carries no declaration, so a box that is not part of a grid cannot be
    /// read at all - and the struct's own default is not this, because an unset alignment is
    /// <see cref="FlexAlignment.Auto"/> and the two axes it applies to read that as
    /// <see cref="FlexAlignment.Stretch"/>.
    /// </summary>
    internal static GridStyle Initial { get; } = new(
        new GridTrackList(),
        new GridTrackList(),
        GridTrackSize.Auto,
        GridTrackSize.Auto,
        false,
        false,
        CssInitialValues.JustifyContentGrid,
        CssInitialValues.AlignContent,
        FlexAlignment.Stretch,
        FlexAlignment.Stretch,
        0,
        0,
        default,
        default,
        false,
        false,
        null,
        null,
        null);

    internal static GridStyle Read(ComputedStyle style)
    {
        var gaps = style.Flex;
        return Read(Source.Of(style), gaps.RowGap, gaps.ColumnGap);
    }

    /// <summary>
    /// Reads the grid longhands from the element's own cascaded declarations, for the fused path.
    /// The gaps come from the reader the flex context uses, because <c>gap</c> means the same thing
    /// in both and the two contexts have to agree about a page that sets it.
    /// </summary>
    internal static GridStyle ReadFused(
        Func<String, ICssProperty?> find, LengthReader.FontSource font, IRenderDimensions dimensions)
    {
        var (row, column) = FlexStyle.GapsFused(find, font, dimensions);
        return Read(Source.Of(find, font, dimensions), row, column);
    }

    private static GridStyle Read(Source source, Double rowGap, Double columnGap)
    {
        var autoFlow = ReadAutoFlow(source);
        var place = ReadPlaceItems(source);

        var alignItems = Alignment(source, "align-items", CssInitialValues.AlignItemsGrid);
        var justifyItems = Alignment(source, "justify-items", CssInitialValues.JustifyItemsGrid);

        var columns = ReadTracks(source, "grid-template-columns");
        var rows = ReadTracks(source, "grid-template-rows");
        var areas = GridAreas.Parse(source.Text("grid-template-areas"));

        // A template area creates the tracks its cells cover: the names say how many rows and columns
        // there are even where no sizes were written for them, and a track that is explicit but has no
        // size is sized as <c>auto</c>, which is the same answer an item placed past the edge gets.
        if (areas is { } template)
        {
            while (columns.StaticCount < template.ColumnCount)
            {
                columns.Add(GridTrackSize.Auto);
            }

            while (rows.StaticCount < template.RowCount)
            {
                rows.Add(GridTrackSize.Auto);
            }
        }

        return new GridStyle(
            columns,
            rows,
            ReadAutoTrack(source, "grid-auto-columns"),
            ReadAutoTrack(source, "grid-auto-rows"),
            autoFlow.Column,
            autoFlow.Dense,
            Alignment(source, "justify-content", CssInitialValues.JustifyContentGrid),
            Alignment(source, "align-content", CssInitialValues.AlignContent),
            justifyItems == FlexAlignment.Auto
                ? (place.Justify == FlexAlignment.Auto ? FlexAlignment.Stretch : place.Justify)
                : justifyItems,
            alignItems == FlexAlignment.Auto
                ? (place.Align == FlexAlignment.Auto ? FlexAlignment.Stretch : place.Align)
                : alignItems,
            Math.Max(0, rowGap),
            Math.Max(0, columnGap),
            GapLength(source, "row"),
            GapLength(source, "column"),
            GapStated(source, "row"),
            GapStated(source, "column"),
            areas,
            // The names written after `subgrid` stand one per borrowed line rather than beside a
            // track of their own, so the axis says which reading its text wants.
            columns.Subgrid
                ? GridLineNames.Groups(source.Text("grid-template-columns"))
                : GridLineNames.Parse(source.Text("grid-template-columns")),
            rows.Subgrid
                ? GridLineNames.Groups(source.Text("grid-template-rows"))
                : GridLineNames.Parse(source.Text("grid-template-rows")));
    }

    /// <summary>
    /// Gets whether one axis's gap was written as a gutter of its own. <c>normal</c> - and a declaration
    /// list that says nothing - is not one: it is the value a subgrid answers with its parent's gutter,
    /// which is why the two have to be told apart before the borrowed tracks are cut.
    /// </summary>
    private static Boolean GapStated(Source source, String axis)
    {
        // The shorthand fills both axes and the legacy alias stands where the standard name says
        // nothing, so an axis is stated by any of the three that carries a length.
        return IsGutter(source.Text($"grid-{axis}-gap")) ||
            IsGutter(source.Text($"{axis}-gap")) ||
            IsGutter(source.Text("gap"));
    }

    private static Boolean IsGutter(String text) =>
        text.Length > 0 && !text.Equals("normal", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Reads one axis's gap as the length it was written as, keeping a percentage whole for the box the
    /// tracks are later laid in. The legacy alias answers where the standard name says nothing.
    /// </summary>
    private static StyleLength GapLength(Source source, String axis)
    {
        if (source.Fetch($"{axis}-gap") is { } stated && source.TryLength(stated, out var length))
        {
            return length;
        }

        if (source.Fetch($"grid-{axis}-gap") is { } legacy && source.TryLength(legacy, out length))
        {
            return length;
        }

        return default;
    }

    /// <summary>
    /// Turns one side of an item's placement from a name into the line the template says that name is,
    /// leaving every other kind of line - and a name nothing claims - exactly as it was read.
    /// </summary>
    /// <param name="end">
    /// Which side of the item this line is. The cascade expands <c>grid-area: toolbar</c> into four
    /// longhands that all say <c>toolbar</c>, while the specification reads the two that end a span as
    /// asking for the area's far line - so the side has to be known here or an area would cover one
    /// track of its own template.
    /// </param>
    /// <remarks>
    /// Names written on the tracks come first, then the area's own: a template that calls one of its
    /// cells by a name the tracks also wrote on a line is the page's problem, and the written line is
    /// the more specific answer. The <c>-start</c> and <c>-end</c> spellings name the lines on either
    /// side of what an area covers whichever side is asking, which is what lets
    /// <c>grid-column: toolbar</c> and <c>grid-column-end: toolbar-end</c> say the same rectangle.
    /// </remarks>
    internal GridLine Resolve(GridLine line, Boolean horizontal, Boolean end, GridLineNames? borrowed = null)
    {
        if (line.Kind != GridLineKind.Name || line.Name is not { } name)
        {
            return line;
        }

        // A subgrid's lines are named by the grid it borrows from, where the page wrote the names; its
        // own template names none, so the borrowed list is the answer for that axis (CSS Grid 2 §8.1).
        if ((borrowed ?? (horizontal ? ColumnLines : RowLines))?.IndexOf(name, line.Repeat) is { } written)
        {
            return GridLine.At(written);
        }

        if (Areas is { } areas)
        {
            // The name of an area, on its own, is the line it starts at - unless the side asking is
            // the one that ends there, which the specification reads as the line after its last track.
            if (areas.TryBounds(name, out var startColumn, out var endColumn, out var startRow, out var endRow))
            {
                return GridLine.At(end
                    ? (horizontal ? endColumn : endRow)
                    : (horizontal ? startColumn : startRow));
            }

            var starting = name.EndsWith("-start", StringComparison.Ordinal);
            var ending = !starting && name.EndsWith("-end", StringComparison.Ordinal);

            if (starting || ending)
            {
                // Cut the suffix this spelling adds before asking for the area it names: "-start" is six
                // characters, "-end" four. Cutting one length for both reads the wrong stem, and for a
                // name no longer than the slice it asks for a substring of negative length.
                var stem = starting ? name[..^6] : name[..^4];

                if (stem.Length > 0 &&
                    areas.TryBounds(stem, out var areaColumn, out var areaColumnEnd, out var areaRow, out var areaRowEnd))
                {
                    var reached = ending
                        ? (horizontal ? areaColumnEnd : areaRowEnd)
                        : (horizontal ? areaColumn : areaRow);

                    return GridLine.At(reached);
                }
            }
        }

        return GridLine.Auto;
    }

    /// <summary>Reads one of the alignment longhands, from whichever source the reader was given.</summary>
    private static FlexAlignment Alignment(Source source, String name, FlexAlignment fallback) =>
        source.Style is { } style
            ? FlexStyle.ReadAlignment(style, name, fallback)
            : FlexStyle.ReadAlignmentFused(source.Property(name), fallback);

    /// <summary>Reads one side of an item's placement, as in <c>grid-column-start</c>.</summary>
    public static GridLine Placement(ComputedStyle style, String name) => ReadLine(Fetch(style, name));

    /// <summary>
    /// Reads <c>place-items</c> as a fallback for the two <c>*-items</c> longhands. The shorthand is
    /// read from its text because AngleSharp.Css does not register it, so it never reaches the
    /// longhands; one value applies to both axes, two are align then justify.
    /// </summary>
    private static (FlexAlignment Align, FlexAlignment Justify) ReadPlaceItems(Source source)
    {
        // The old path reads the shorthand from its text: AngleSharp.Css does not register it, so a
        // declaration keeps it as itself. The cascade a fused style is built from has no such
        // luxury - it carries the shorthand as its two longhands, and the one the page did not
        // write is left present but empty. That empty value is what a single-value `place-items`
        // looks like from here, and it asks for the other axis's value; a longhand written on its
        // own leaves the other one out altogether. One value applies to both axes, two are align
        // then justify.
        if (source.Style is { } style)
        {
            var text = FlexStyle.Text(style, "place-items");

            if (text.Length == 0)
            {
                return (FlexAlignment.Auto, FlexAlignment.Auto);
            }

            var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var align = FlexStyle.ParseAlignment(parts[0], FlexAlignment.Auto);
            var justify = parts.Length > 1
                ? FlexStyle.ParseAlignment(parts[1], FlexAlignment.Auto)
                : align;

            return (align, justify);
        }

        var alignItems = Alignment(source, "align-items", CssInitialValues.AlignItemsGrid);
        var justifyItems = Alignment(source, "justify-items", CssInitialValues.JustifyItemsGrid);

        if (alignItems != FlexAlignment.Auto && justifyItems == FlexAlignment.Auto && source.IsEmpty("justify-items"))
        {
            justifyItems = alignItems;
        }

        if (justifyItems != FlexAlignment.Auto && alignItems == FlexAlignment.Auto && source.IsEmpty("align-items"))
        {
            alignItems = justifyItems;
        }

        return (alignItems, justifyItems);
    }

    /// <summary>
    /// Reads <c>grid-auto-flow</c>. AngleSharp turns it into a two-slot tuple holding a
    /// <see cref="Boolean"/> for the axis and one for <c>dense</c>, and there is no enum to lean on.
    /// </summary>
    private static (Boolean Column, Boolean Dense) ReadAutoFlow(Source source)
    {
        var column = false;
        var dense = false;
        var items = LengthReader.Items(source.Fetch("grid-auto-flow"));

        if (items is { Count: > 0 })
        {
            column = Flag(items, 0) ?? false;
            dense = Flag(items, 1) ?? false;
        }

        // The tuple form is the only one the converter produces, but the serialised keyword is cheap
        // to consult and keeps the reader working if the upstream value ever changes shape.
        var text = source.Text("grid-auto-flow");
        column |= text.Contains("column", StringComparison.Ordinal);
        dense |= text.Contains("dense", StringComparison.Ordinal);

        return (column, dense);
    }

    private static Boolean? Flag(IReadOnlyList<ICssValue> items, Int32 index) =>
        index < items.Count && items[index] is CssConstantValue<Boolean> flag ? flag.Value : null;

    private static GridTrackSize ReadAutoTrack(Source source, String name) =>
        Tracks(source, source.Fetch(name)).Resolve(0, 0, 1) is { Count: > 0 } tracks ? tracks[0] : GridTrackSize.Auto;

    private static GridTrackList ReadTracks(Source source, String name) =>
        Tracks(source, source.Fetch(name));

    /// <summary>
    /// Reads a track list. It is normally a multiple value whose entries are line names (which
    /// <see cref="Track"/> declines), single track sizes, or <c>repeat()</c> functions standing for
    /// several tracks at once.
    /// </summary>
    private static GridTrackList Tracks(Source source, ICssValue? value)
    {
        var tracks = new GridTrackList();

        // `subgrid` is not a track list: the tracks are the parent's, and only the parent can say
        // which ones this box spans. The empty list stands in until the grid around it is laid out.
        if (value is CssSubgridValue ||
            (value is CssIdentifierValue identifier &&
             String.Equals(identifier.Value, "subgrid", StringComparison.OrdinalIgnoreCase)))
        {
            tracks.Subgrid = true;
            return tracks;
        }

        Expand(source, value, tracks);
        return tracks;
    }

    private static void Expand(Source source, ICssValue? value, GridTrackList tracks)
    {
        if (LengthReader.Items(value) is not { Count: > 0 } items)
        {
            // A lone `none` is a constant rather than a tuple and means there are no tracks at all;
            // a lone `1fr` is a single track. Both go through the same classifier.
            if (Track(source, value) is { } single)
            {
                tracks.Add(single);
            }

            return;
        }

        foreach (var item in items)
        {
            if (item is null)
            {
                continue;
            }

            if (item is ICssFunctionValue { Name: "repeat" } function)
            {
                Repeat(source, function, tracks);
                continue;
            }

            if (Track(source, item) is { } track)
            {
                tracks.Add(track);
            }
        }
    }

    /// <summary>
    /// Expands a <c>repeat()</c>. A fixed count repeats the track list that many times and lands in
    /// the list directly; an <c>auto-fill</c>/<c>auto-fit</c> count is recorded where it sits, because
    /// only the caller knows the width the repetitions have to fit into.
    /// </summary>
    private static void Repeat(Source source, ICssFunctionValue function, GridTrackList tracks)
    {
        if (function.Arguments.Length < 2)
        {
            return;
        }

        var repeated = Tracks(source, function.Arguments[1]);

        switch (function.Arguments[0])
        {
            case CssIntegerValue count when count.IntValue > 0:
                for (var index = 0; index < count.IntValue; index++)
                {
                    foreach (var track in repeated.Resolve(0, 0, 1))
                    {
                        tracks.Add(track);
                    }
                }

                break;

            case CssIdentifierValue identifier:
                tracks.Repeat(identifier.Value, repeated.Resolve(0, 0, 1));
                break;
        }
    }

    /// <summary>Classifies one track size value, or declines it when it is not one.</summary>
    private static GridTrackSize? Track(Source source, ICssValue? value) => value switch
    {
        CssLengthValue length => GridTrackSize.From(LengthBreadth(source, length)),
        CssPercentageValue percentage when !Double.IsNaN(percentage.Value) =>
            GridTrackSize.From(new GridBreadth(GridBreadthKind.Percent, percentage.Value / 100.0)),
        CssFractionValue fraction => GridTrackSize.From(new GridBreadth(GridBreadthKind.Fraction, fraction.Value)),
        CssMinMaxValue minmax => new GridTrackSize(Breadth(source, minmax.Minimum), Breadth(source, minmax.Maximum)),
        CssIdentifierValue identifier => GridTrackSize.From(Identifier(identifier.Value)),
        ICssFunctionValue { Name: "fit-content" } fit =>
            GridTrackSize.From(new GridBreadth(GridBreadthKind.FitContent, Length(source, fit))),
        _ => null,
    };

    private static GridBreadth Breadth(Source source, ICssValue? value) => value switch
    {
        CssIdentifierValue identifier => Identifier(identifier.Value),
        CssLengthValue length => LengthBreadth(source, length),
        CssFractionValue fraction => new GridBreadth(GridBreadthKind.Fraction, fraction.Value),
        CssPercentageValue percentage when !Double.IsNaN(percentage.Value) =>
            new GridBreadth(GridBreadthKind.Percent, percentage.Value / 100.0),
        _ => Identifier("auto"),
    };

    private static GridBreadth Identifier(String value) => value switch
    {
        "min-content" => new GridBreadth(GridBreadthKind.MinContent, 0),
        "max-content" => new GridBreadth(GridBreadthKind.MaxContent, 0),
        _ => new GridBreadth(GridBreadthKind.Auto, 0),
    };

    private static GridBreadth LengthBreadth(Source source, CssLengthValue length) =>
        source.TryLength(length, out var parsed) && parsed.HasValue
            ? parsed.Kind == StyleLengthKind.Percent
                ? new GridBreadth(GridBreadthKind.Percent, parsed.Value)
                : new GridBreadth(GridBreadthKind.Length, Math.Max(0, parsed.Value))
            : new GridBreadth(GridBreadthKind.Auto, 0);

    /// <summary>
    /// Evaluates the single length argument of a <c>fit-content()</c> or a <c>minmax()</c> side. A
    /// percentage limit is relative to the container, which is not known here, so it degrades to
    /// zero - the track then behaves as if it had no limit rather than as if it were empty.
    /// </summary>
    private static Double Length(Source source, ICssValue? value)
    {
        if (value is CssLengthValue length &&
            source.TryLength(length, out var parsed) &&
            parsed.HasValue &&
            parsed.Kind != StyleLengthKind.Percent)
        {
            return Math.Max(0, parsed.Value);
        }

        return 0;
    }

    private static GridLine ReadLine(ICssValue? value)
    {
        if (value is null or CssConstantValue<String>)
        {
            return GridLine.Auto;
        }

        if (LengthReader.Items(value) is not { Count: > 0 } slots)
        {
            return GridLine.Auto;
        }

        // The tuple is `[span?, integer?, identifier?]` with the unused slots left null.
        var span = false;
        var index = 0;
        String? name = null;

        for (var slot = 0; slot < slots.Count; slot++)
        {
            switch (slots[slot])
            {
                case CssConstantValue<Boolean> flag when slot == 0 && flag.Value:
                    span = true;
                    break;

                case CssIntegerValue number:
                    index = number.IntValue;
                    break;

                case CssIdentifierValue identifier:
                    name = identifier.Value;
                    break;
            }
        }

        if (span)
        {
            return GridLine.Spans(index > 0 ? index : 1);
        }

        if (name is { } named)
        {
            return GridLine.Named(named, index == 0 ? 1 : index);
        }

        return index == 0 ? GridLine.Auto : GridLine.At(index);
    }

    private static ICssValue? Fetch(ComputedStyle style, String name) =>
        style.RawValue(name);

    /// <summary>Reads one side of an item's placement from the element's own cascaded declaration.</summary>
    public static GridLine PlacementFused(Func<String, ICssProperty?> find, String name) =>
        ReadLine(find(name)?.RawValue);

    /// <summary>
    /// Where the reader's values come from: the computed declaration behind a render-tree style, or
    /// the cascaded declarations behind a fused one. Everything it asks for - a value with its type
    /// intact, a keyword as written, a length resolved against the element's own font size - is one
    /// of those three, and the two sources answer them differently.
    /// </summary>
    private readonly struct Source
    {
        private readonly ComputedStyle? _style;
        private readonly Func<String, ICssProperty?>? _find;
        private readonly IRenderDimensions _dimensions;
        private readonly LengthReader.FontSource _size;

        private Source(
            ComputedStyle? style, Func<String, ICssProperty?>? find, IRenderDimensions dimensions, LengthReader.FontSource size)
        {
            _style = style;
            _find = find;
            _dimensions = dimensions;
            _size = size;
        }

        public static Source Of(ComputedStyle style) => new(style, null, style.Dimensions, 0);

        public static Source Of(
            Func<String, ICssProperty?> find, LengthReader.FontSource size, IRenderDimensions dimensions) =>
            new(null, find, dimensions, size);

        public ComputedStyle? Style => _style;

        public ICssProperty? Property(String name) => _style is null ? _find!(name) : null;

        public ICssValue? Fetch(String name)
        {
            if (_style is { } style)
            {
                return style.RawValue(name);
            }

            return _find!(name)?.RawValue;
        }

        public String Text(String name)
        {
            if (_style is { } style)
            {
                return FlexStyle.Text(style, name);
            }

            return _find!(name)?.Value?.Trim().ToLowerInvariant() ?? String.Empty;
        }

        /// <summary>
        /// Gets whether the longhand is there but carries no value, rather than not being there: the
        /// mark a shorthand leaves on the longhand it expanded into but did not fill.
        /// </summary>
        public Boolean IsEmpty(String name)
        {
            if (_style is not null)
            {
                return false;
            }

            return _find!(name) is { } property && String.IsNullOrWhiteSpace(property.Value);
        }

        public Boolean TryLength(ICssValue? value, out StyleLength length)
        {
            if (_style is { } style)
            {
                return LengthReader.TryReadValue(value, RenderMode.Horizontal, style, out length);
            }

            return LengthReader.TryReadValue(value, RenderMode.Horizontal, _dimensions, _size, out length);
        }
    }
}

/// <summary>
/// What a grid item carries: where it is placed and how it aligns itself. The four lines are read
/// together because the placement algorithm wants all of them at once, and the two alignments are
/// the item's own rather than its container's.
/// </summary>
internal readonly struct GridItemStyle
{
    private GridItemStyle(
        GridLine columnStart,
        GridLine columnEnd,
        GridLine rowStart,
        GridLine rowEnd,
        FlexAlignment justifySelf,
        FlexAlignment alignSelf)
    {
        ColumnStart = columnStart;
        ColumnEnd = columnEnd;
        RowStart = rowStart;
        RowEnd = rowEnd;
        JustifySelf = justifySelf;
        AlignSelf = alignSelf;
    }

    public GridLine ColumnStart { get; }

    public GridLine ColumnEnd { get; }

    public GridLine RowStart { get; }

    public GridLine RowEnd { get; }

    public FlexAlignment JustifySelf { get; }

    public FlexAlignment AlignSelf { get; }

    /// <summary>An item that asked for nothing: placed by the algorithm, aligned by the container.</summary>
    public static GridItemStyle Auto { get; } = new(
        GridLine.Auto,
        GridLine.Auto,
        GridLine.Auto,
        GridLine.Auto,
        CssInitialValues.JustifySelfGrid,
        CssInitialValues.AlignSelf);

    internal static GridItemStyle Read(ComputedStyle style) => new(
        GridStyle.Placement(style, "grid-column-start"),
        GridStyle.Placement(style, "grid-column-end"),
        GridStyle.Placement(style, "grid-row-start"),
        GridStyle.Placement(style, "grid-row-end"),
        FlexStyle.ReadAlignment(style, "justify-self", CssInitialValues.JustifySelfGrid),
        FlexStyle.ReadAlignment(style, "align-self", CssInitialValues.AlignSelf));

    internal static GridItemStyle ReadFused(Func<String, ICssProperty?> find) => new(
        GridStyle.PlacementFused(find, "grid-column-start"),
        GridStyle.PlacementFused(find, "grid-column-end"),
        GridStyle.PlacementFused(find, "grid-row-start"),
        GridStyle.PlacementFused(find, "grid-row-end"),
        FlexStyle.ReadAlignmentFused(find("justify-self"), CssInitialValues.JustifySelfGrid),
        FlexStyle.ReadAlignmentFused(find("align-self"), CssInitialValues.AlignSelf));
}

/// <summary>An <c>auto-fill</c>/<c>auto-fit</c> repetition, held back until the width is known.</summary>
internal readonly struct RepeatCount
{
    public RepeatCount(String kind, IReadOnlyList<GridTrackSize> tracks)
    {
        Kind = kind;
        Tracks = tracks;
    }

    public static RepeatCount Unknown => new(String.Empty, Array.Empty<GridTrackSize>());

    public String Kind { get; }

    public IReadOnlyList<GridTrackSize> Tracks { get; }

    public Boolean IsAutoFit => Kind == "auto-fit";

    public Boolean IsKnown => Kind.Length > 0;
}

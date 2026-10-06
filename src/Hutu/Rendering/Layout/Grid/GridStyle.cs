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

    /// <summary>Gets how many tracks the list holds before the automatic repetition is spliced in.</summary>
    public Int32 StaticCount => _tracks.Count;

    public Boolean HasAutoRepeat => _repeat.IsKnown && _repeat.Tracks.Count > 0;

    /// <summary>Gets whether the repetition collapses the tracks no item reaches.</summary>
    public Boolean IsAutoFit => HasAutoRepeat && _repeat.IsAutoFit;

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
    /// items actually reach, which is what an <c>auto-fit</c> repetition collapses down to.
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

    public static GridLine Auto => new(GridLineKind.Auto, 0, 1, null);

    public static GridLine At(Int32 index) => new(GridLineKind.Index, index, 1, null);

    public static GridLine Spans(Int32 span) => new(GridLineKind.Span, 0, Math.Max(1, span), null);

    public static GridLine Named(String name) => new(GridLineKind.Name, 0, 1, name);

    /// <summary>
    /// Resolves the line to a one-based number, wrapping a negative number from the end so that
    /// <c>-1</c> is the last line of the explicit grid.
    /// </summary>
    public Int32? Resolve(Int32 lineCount) => Kind switch
    {
        GridLineKind.Index when Index > 0 => Index,
        GridLineKind.Index => lineCount + 1 + Index,
        _ => null,
    };

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
/// <c>grid</c>, <c>grid-template</c> and <c>grid-template-areas</c> are deliberately not read. The
/// two shorthands rebuild their value into an internal type that cannot be named here, and nothing in
/// the sample pages uses either. Named lines and named areas are parsed but not resolved, so they
/// place like <c>auto</c>.
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
        Double columnGap)
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
    }

    /// <summary>Gets the explicit column tracks, with a fixed <c>repeat()</c> already expanded.</summary>
    public GridTrackList Columns { get; }

    /// <summary>Gets the explicit row tracks, with a fixed <c>repeat()</c> already expanded.</summary>
    public GridTrackList Rows { get; }

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

    internal static GridStyle Read(ComputedStyle style)
    {
        var autoFlow = ReadAutoFlow(style);
        var place = ReadPlaceItems(style);
        var gaps = style.Flex;

        var alignItems = FlexStyle.ReadAlignment(style, "align-items", FlexAlignment.Auto);
        var justifyItems = FlexStyle.ReadAlignment(style, "justify-items", FlexAlignment.Auto);

        return new GridStyle(
            ReadTracks(style, "grid-template-columns"),
            ReadTracks(style, "grid-template-rows"),
            ReadAutoTrack(style, "grid-auto-columns"),
            ReadAutoTrack(style, "grid-auto-rows"),
            autoFlow.Column,
            autoFlow.Dense,
            FlexStyle.ReadAlignment(style, "justify-content", FlexAlignment.Stretch),
            FlexStyle.ReadAlignment(style, "align-content", FlexAlignment.Stretch),
            justifyItems == FlexAlignment.Auto
                ? (place.Justify == FlexAlignment.Auto ? FlexAlignment.Stretch : place.Justify)
                : justifyItems,
            alignItems == FlexAlignment.Auto
                ? (place.Align == FlexAlignment.Auto ? FlexAlignment.Stretch : place.Align)
                : alignItems,
            Math.Max(0, gaps.RowGap),
            Math.Max(0, gaps.ColumnGap));
    }

    /// <summary>Reads one side of an item's placement, as in <c>grid-column-start</c>.</summary>
    public static GridLine Placement(ComputedStyle style, String name) => ReadLine(Fetch(style, name));

    /// <summary>
    /// Reads <c>place-items</c> as a fallback for the two <c>*-items</c> longhands. The shorthand is
    /// read from its text because AngleSharp.Css does not register it, so it never reaches the
    /// longhands; one value applies to both axes, two are align then justify.
    /// </summary>
    private static (FlexAlignment Align, FlexAlignment Justify) ReadPlaceItems(ComputedStyle style)
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

    /// <summary>
    /// Reads <c>grid-auto-flow</c>. AngleSharp turns it into a two-slot tuple holding a
    /// <see cref="Boolean"/> for the axis and one for <c>dense</c>, and there is no enum to lean on.
    /// </summary>
    private static (Boolean Column, Boolean Dense) ReadAutoFlow(ComputedStyle style)
    {
        var column = false;
        var dense = false;
        var items = LengthReader.Items(Fetch(style, "grid-auto-flow"));

        if (items is { Count: > 0 })
        {
            column = Flag(items, 0) ?? false;
            dense = Flag(items, 1) ?? false;
        }

        // The tuple form is the only one the converter produces, but the serialised keyword is cheap
        // to consult and keeps the reader working if the upstream value ever changes shape.
        var text = FlexStyle.Text(style, "grid-auto-flow");
        column |= text.Contains("column", StringComparison.Ordinal);
        dense |= text.Contains("dense", StringComparison.Ordinal);

        return (column, dense);
    }

    private static Boolean? Flag(IReadOnlyList<ICssValue> items, Int32 index) =>
        index < items.Count && items[index] is CssConstantValue<Boolean> flag ? flag.Value : null;

    private static GridTrackSize ReadAutoTrack(ComputedStyle style, String name) =>
        Tracks(style, Fetch(style, name)).Resolve(0, 0, 1) is { Count: > 0 } tracks ? tracks[0] : GridTrackSize.Auto;

    private static GridTrackList ReadTracks(ComputedStyle style, String name) =>
        Tracks(style, Fetch(style, name));

    /// <summary>
    /// Reads a track list. It is normally a multiple value whose entries are line names (which
    /// <see cref="Track"/> declines), single track sizes, or <c>repeat()</c> functions standing for
    /// several tracks at once.
    /// </summary>
    private static GridTrackList Tracks(ComputedStyle style, ICssValue? value)
    {
        var tracks = new GridTrackList();

        Expand(style, value, tracks);
        return tracks;
    }

    private static void Expand(ComputedStyle style, ICssValue? value, GridTrackList tracks)
    {
        if (LengthReader.Items(value) is not { Count: > 0 } items)
        {
            // A lone `none` is a constant rather than a tuple and means there are no tracks at all;
            // a lone `1fr` is a single track. Both go through the same classifier.
            if (Track(style, value) is { } single)
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
                Repeat(style, function, tracks);
                continue;
            }

            if (Track(style, item) is { } track)
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
    private static void Repeat(ComputedStyle style, ICssFunctionValue function, GridTrackList tracks)
    {
        if (function.Arguments.Length < 2)
        {
            return;
        }

        var repeated = Tracks(style, function.Arguments[1]);

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
    private static GridTrackSize? Track(ComputedStyle style, ICssValue? value) => value switch
    {
        CssLengthValue length => GridTrackSize.From(LengthBreadth(style, length)),
        CssPercentageValue percentage when !Double.IsNaN(percentage.Value) =>
            GridTrackSize.From(new GridBreadth(GridBreadthKind.Percent, percentage.Value / 100.0)),
        CssFractionValue fraction => GridTrackSize.From(new GridBreadth(GridBreadthKind.Fraction, fraction.Value)),
        CssMinMaxValue minmax => new GridTrackSize(Breadth(style, minmax.Minimum), Breadth(style, minmax.Maximum)),
        CssIdentifierValue identifier => GridTrackSize.From(Identifier(identifier.Value)),
        ICssFunctionValue { Name: "fit-content" } fit =>
            GridTrackSize.From(new GridBreadth(GridBreadthKind.FitContent, Length(style, fit))),
        _ => null,
    };

    private static GridBreadth Breadth(ComputedStyle style, ICssValue? value) => value switch
    {
        CssIdentifierValue identifier => Identifier(identifier.Value),
        CssLengthValue length => LengthBreadth(style, length),
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

    private static GridBreadth LengthBreadth(ComputedStyle style, CssLengthValue length) =>
        LengthReader.TryReadValue(length, RenderMode.Horizontal, style.Dimensions, style.FontSize, out var parsed) && parsed.HasValue
            ? parsed.Kind == StyleLengthKind.Percent
                ? new GridBreadth(GridBreadthKind.Percent, parsed.Value)
                : new GridBreadth(GridBreadthKind.Length, Math.Max(0, parsed.Value))
            : new GridBreadth(GridBreadthKind.Auto, 0);

    /// <summary>
    /// Evaluates the single length argument of a <c>fit-content()</c> or a <c>minmax()</c> side. A
    /// percentage limit is relative to the container, which is not known here, so it degrades to
    /// zero - the track then behaves as if it had no limit rather than as if it were empty.
    /// </summary>
    private static Double Length(ComputedStyle style, ICssValue? value)
    {
        if (value is CssLengthValue length &&
            LengthReader.TryReadValue(length, RenderMode.Horizontal, style.Dimensions, style.FontSize, out var parsed) &&
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
            return GridLine.Named(named);
        }

        return index == 0 ? GridLine.Auto : GridLine.At(index);
    }

    private static ICssValue? Fetch(ComputedStyle style, String name) =>
        style.Declaration.GetProperty(name)?.RawValue;
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

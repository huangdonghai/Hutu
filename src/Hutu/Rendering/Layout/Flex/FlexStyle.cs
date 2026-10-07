using AngleSharp.Css;
using AngleSharp.Css.Dom;
using AngleSharp.Css.Values;
using System;
using System.Globalization;

namespace Hutu.Rendering.Layout;

/// <summary>
/// The alignment keywords flex shares between justification and alignment.
/// </summary>
/// <remarks>
/// This mirrors <see cref="FlexContentMode"/> but adds <c>space-evenly</c> and makes
/// <c>start</c>/<c>end</c> explicit. AngleSharp.Css' keyword table is missing those, so its enum
/// cannot be trusted to round-trip them; the reader falls back to the serialised string for the ones
/// it cannot map.
/// </remarks>
public enum FlexAlignment : byte
{
    /// <summary>Not set: a flex item defers to its container, a container defers to its initial value.</summary>
    Auto,
    Start,
    End,
    Center,
    SpaceBetween,
    SpaceAround,
    SpaceEvenly,
    Stretch,
    Baseline,
}

/// <summary>
/// The flex longhands of a box, read without the shorthands and without trusting the computed
/// declaration's keyword types.
/// </summary>
/// <remarks>
/// Several upstream defects are contained here rather than worked around at every use:
/// <list type="bullet">
/// <item><c>position</c>'s initial value is keyed <c>inline</c>, so the string is never read for a
/// keyword that has a typed value.</item>
/// <item><c>flex-grow</c>, <c>flex-shrink</c> and <c>order</c> carry their initial values as a
/// <see cref="CssLengthValue"/> rather than a number, so both shapes are accepted.</item>
/// <item><c>align-items</c>' and <c>align-content</c>'s initial values serialise as <c>normal</c>
/// while meaning <c>stretch</c>, so the typed value wins when it is present.</item>
/// </list>
/// A shorthand such as <c>flex</c> is deliberately not read: AngleSharp.Css rebuilds a shorthand
/// from its longhands lossily, so the longhands are the only faithful source.
/// </remarks>
public readonly struct FlexStyle
{
    private FlexStyle(
        FlexDirection direction,
        FlexWrapMode wrap,
        FlexAlignment justifyContent,
        FlexAlignment alignItems,
        FlexAlignment alignContent,
        Double rowGap,
        Double columnGap,
        Double grow,
        Double shrink,
        StyleLength basis,
        Int32 order,
        FlexAlignment alignSelf)
    {
        Direction = direction;
        Wrap = wrap;
        JustifyContent = justifyContent;
        AlignItems = alignItems;
        AlignContent = alignContent;
        RowGap = rowGap;
        ColumnGap = columnGap;
        Grow = grow;
        Shrink = shrink;
        Basis = basis;
        Order = order;
        AlignSelf = alignSelf;
    }

    public FlexDirection Direction { get; }

    public FlexWrapMode Wrap { get; }

    public FlexAlignment JustifyContent { get; }

    public FlexAlignment AlignItems { get; }

    public FlexAlignment AlignContent { get; }

    public Double RowGap { get; }

    public Double ColumnGap { get; }

    public Double Grow { get; }

    public Double Shrink { get; }

    /// <summary>Gets the flex base size. <c>auto</c> and <c>content</c> both end up without a value.</summary>
    public StyleLength Basis { get; }

    public Int32 Order { get; }

    public FlexAlignment AlignSelf { get; }

    /// <summary>Gets whether the main axis runs horizontally.</summary>
    public Boolean IsRow => Direction is FlexDirection.Row or FlexDirection.RowReverse;

    /// <summary>Gets whether the main axis runs backwards.</summary>
    public Boolean IsReversed => Direction is FlexDirection.RowReverse or FlexDirection.ColumnReverse;

    /// <summary>Gets whether the items break onto more than one line when they do not fit.</summary>
    public Boolean Wraps => Wrap != FlexWrapMode.NoWrap;

    /// <summary>Gets whether the lines are stacked in the reverse order along the cross axis.</summary>
    public Boolean IsWrapReversed => Wrap == FlexWrapMode.WrapReverse;

    internal static FlexStyle Read(ComputedStyle style)
    {
        var (rowGap, columnGap) = ReadGaps(style);

        return new FlexStyle(
            ReadDirection(style),
            ReadWrap(style),
            ReadAlignment(style, "justify-content", FlexAlignment.Start),
            ReadAlignment(style, "align-items", FlexAlignment.Stretch),
            ReadAlignment(style, "align-content", FlexAlignment.Stretch),
            rowGap,
            columnGap,
            ReadNumber(style, "flex-grow", 0),
            ReadNumber(style, "flex-shrink", 1),
            ReadBasis(style),
            (Int32)ReadNumber(style, "order", 0),
            ReadAlignment(style, "align-self", FlexAlignment.Auto));
    }

    /// <summary>
    /// Reads the two gaps together, because neither source has both. AngleSharp.Css expands the
    /// <c>gap</c> shorthand into <c>row-gap</c> and leaves <c>column-gap</c> in the computed
    /// declaration with no value at all, so the shorthand - a two-item tuple - is the only place the
    /// column gap can be found. A single-value <c>gap</c> applies to both axes; a two-value one is
    /// <c>row</c> then <c>column</c>, in that order.
    /// </summary>
    private static (Double Row, Double Column) ReadGaps(ComputedStyle style)
    {
        Double? first = null;
        Double? second = null;

        // The shorthand is read through the multiple-value interface, not the tuple class: AngleSharp
        // produces a generic instantiation that the tuple class alone does not match.
        if (LengthReader.Items(style.RawValue("gap")) is { Count: > 0 } items)
        {
            first = GapOf(style, items[0]);
            second = items.Count > 1 ? GapOf(style, items[1]) : first;
        }

        var row = ReadGap(style, "row-gap") ?? first ?? 0;
        var column = ReadGap(style, "column-gap") ?? second ?? first ?? 0;

        return (row, column);
    }

    private static Double? ReadGap(ComputedStyle style, String name) =>
        LengthReader.TryRead(style.Specified, style.Declaration, name, RenderMode.Horizontal, style.Dimensions, style.FontSize, out var gap) &&
        gap.HasValue
            ? Math.Max(0, gap.Resolve(0))
            : null;

    private static Double? GapOf(ComputedStyle style, ICssValue item) =>
        LengthReader.TryReadValue(item, RenderMode.Horizontal, style.Dimensions, style.FontSize, out var gap) && gap.HasValue
            ? Math.Max(0, gap.Resolve(0))
            : null;

    private static FlexDirection ReadDirection(ComputedStyle style)
    {
        if (style.RawValue("flex-direction") is CssConstantValue<FlexDirection> constant)
        {
            return constant.Value;
        }

        return Text(style, "flex-direction") switch
        {
            "row-reverse" => FlexDirection.RowReverse,
            "column" => FlexDirection.Column,
            "column-reverse" => FlexDirection.ColumnReverse,
            _ => FlexDirection.Row,
        };
    }

    private static FlexWrapMode ReadWrap(ComputedStyle style)
    {
        if (style.RawValue("flex-wrap") is CssConstantValue<FlexWrapMode> constant)
        {
            return constant.Value;
        }

        return Text(style, "flex-wrap") switch
        {
            "wrap" => FlexWrapMode.Wrap,
            "wrap-reverse" => FlexWrapMode.WrapReverse,
            _ => FlexWrapMode.NoWrap,
        };
    }

    /// <summary>
    /// Reads one of the alignment longhands. It is shared with the grid context, because
    /// <c>justify-content</c>, <c>align-content</c> and <c>align-items</c> mean the same thing in both
    /// formatting contexts; only <c>justify-items</c> and the <c>*-self</c> pair are read elsewhere.
    /// A fallback of <see cref="FlexAlignment.Auto"/> is how a caller asks whether the property was
    /// set at all.
    /// </summary>
    internal static FlexAlignment ReadAlignment(ComputedStyle style, String name, FlexAlignment fallback)
    {
        if (style.RawValue(name) is CssConstantValue<FlexContentMode> constant)
        {
            var mapped = Map(constant.Value);

            if (mapped != FlexAlignment.Auto)
            {
                return mapped;
            }
        }

        var text = Text(style, name);
        return text.Length == 0 ? fallback : Parse(text, fallback);
    }

    private static FlexAlignment Map(FlexContentMode mode) => mode switch
    {
        FlexContentMode.Start => FlexAlignment.Start,
        FlexContentMode.End => FlexAlignment.End,
        FlexContentMode.Center => FlexAlignment.Center,
        FlexContentMode.SpaceBetween => FlexAlignment.SpaceBetween,
        FlexContentMode.SpaceAround => FlexAlignment.SpaceAround,
        FlexContentMode.Stretch => FlexAlignment.Stretch,
        FlexContentMode.Baseline => FlexAlignment.Baseline,
        _ => FlexAlignment.Auto,
    };

    /// <summary>
    /// Parses an alignment keyword. The mapping table is the reader's own rather than AngleSharp's,
    /// because the latter is missing <c>space-evenly</c> and does not know <c>start</c>/<c>end</c>.
    /// </summary>
    internal static FlexAlignment ParseAlignment(String text, FlexAlignment fallback) => Parse(text, fallback);

    private static FlexAlignment Parse(String text, FlexAlignment fallback) => text switch
    {
        "flex-start" or "start" => FlexAlignment.Start,
        "flex-end" or "end" => FlexAlignment.End,
        "center" => FlexAlignment.Center,
        "space-between" => FlexAlignment.SpaceBetween,
        "space-around" => FlexAlignment.SpaceAround,
        "space-evenly" => FlexAlignment.SpaceEvenly,
        "stretch" => FlexAlignment.Stretch,
        "baseline" => FlexAlignment.Baseline,
        _ => fallback,
    };

    private static StyleLength ReadBasis(ComputedStyle style) =>
        LengthReader.TryRead(style.Specified, style.Declaration, "flex-basis", RenderMode.Horizontal, style.Dimensions, style.FontSize, out var basis) &&
        basis.HasValue
            ? basis
            : StyleLength.Auto;

    /// <summary>
    /// Reads a plain number. The initial values of <c>flex-grow</c>, <c>flex-shrink</c> and
    /// <c>order</c> are stored as lengths, so both a number and a length are accepted.
    /// </summary>
    private static Double ReadNumber(ComputedStyle style, String name, Double fallback)
    {
        switch (style.RawValue(name))
        {
            case CssNumberValue number:
                return number.Value;

            case CssIntegerValue integer:
                return integer.Value;

            case CssLengthValue length when !Double.IsNaN(length.Value) && !Double.IsInfinity(length.Value):
                return length.Value;
        }

        var text = Text(style, name);
        return Double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : fallback;
    }

    /// <summary>Gets a keyword, preferring the cascaded declaration, lowered and trimmed.</summary>
    internal static String Text(ComputedStyle style, String name)
    {
        StyleProbe.NoteTextRead(name);

        var text = style.Specified?.GetPropertyValue(name);

        if (String.IsNullOrWhiteSpace(text))
        {
            text = style.Declaration.GetPropertyValue(name);
        }

        return text is null ? String.Empty : text.Trim().ToLowerInvariant();
    }
}

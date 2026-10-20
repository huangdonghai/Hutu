using AngleSharp.Css;
using AngleSharp.Css.Dom;
using AngleSharp.Css.Values;
using Hutu.Rendering.Media;
using System;

namespace Hutu.Rendering.Layout;

/// <summary>
/// The stroke a gap asks to be drawn in the middle of it: how thick, in what style and in what colour
/// (CSS Gaps 1 §4). A rule is a border side turned to face down the gap, so it takes the same strokes -
/// a dashed one dashes along its length and a double one is two lines within its width.
/// </summary>
internal readonly struct GapRule
{
    internal GapRule(Double width, BorderLineStyle style, UInt32 color)
    {
        Width = Math.Max(0, width);
        Style = style;
        Color = color;
    }

    /// <summary>Gets how thick the rule is, in pixels.</summary>
    public Double Width { get; }

    /// <summary>Gets the stroke it is drawn in.</summary>
    public BorderLineStyle Style { get; }

    /// <summary>Gets the colour it is drawn in, as packed ARGB.</summary>
    public UInt32 Color { get; }

    /// <summary>
    /// Gets whether anything is drawn at all: a rule of no width, or one in the style that says nothing,
    /// is no rule.
    /// </summary>
    public Boolean Appears => Width > 0 && Style != BorderLineStyle.None;
}

/// <summary>
/// The list of values one gap decoration property holds, read out of the cascaded declaration.
/// </summary>
/// <remarks>
/// A gap decoration property takes a comma-separated list, one decoration per gap (CSS Gaps 1 §4.5), and
/// a gap that runs past the end of the list takes the list again from its start (§4.6). A <c>repeat()</c>
/// notation is written out as it is read - an integer repeater stands for its values that many times -
/// unless its count is <c>auto</c>: that one stands for the gaps no other value reaches, so how many times
/// it turns is only known once the container holding the gaps has been laid out.
/// </remarks>
internal sealed class GapRuleTerms<T> where T : struct
{
    private static readonly T[] Empty = Array.Empty<T>();

    private readonly T[] _values;
    private readonly T[] _leading;
    private readonly T[] _auto;
    private readonly T[] _trailing;
    private readonly T _initial;

    internal GapRuleTerms(T initial, T[]? values)
        : this(initial, values, null, null)
    {
    }

    internal GapRuleTerms(T initial, T[]? leading, T[]? auto, T[]? trailing)
    {
        _initial = initial;
        _leading = leading ?? Empty;
        _auto = auto ?? Empty;
        _trailing = trailing ?? Empty;

        // With no auto repeater the values are one list and a gap takes it again from the start; that is
        // the common case, and keeping the two ends joined answers it without a branch per gap.
        _values = _auto.Length == 0 ? Joined(_leading, _trailing) : Empty;
    }

    /// <summary>Gets whether the page wrote this property at all.</summary>
    public Boolean IsStated => _values.Length > 0 || _auto.Length > 0;

    /// <summary>
    /// Gets the value the <paramref name="index"/>th gap takes. A property the page wrote nothing about
    /// keeps its initial value for every gap, which is how a list of colours and a list of widths, read
    /// from lists of different lengths, still come to one decoration per gap.
    /// </summary>
    public T At(Int32 index, Int32 count)
    {
        if (_values.Length > 0)
        {
            return _values[index % _values.Length];
        }

        if (_auto.Length == 0)
        {
            return _initial;
        }

        // The values written before the auto repeater take the first gaps, those written after it take the
        // last ones, and the repeater itself takes everything between them (§4.6).
        var leading = _leading.Length;

        if (index < leading)
        {
            return _leading[index];
        }

        var trailing = Math.Max(leading, count - _trailing.Length);

        return index >= trailing
            ? _trailing[index - trailing]
            : _auto[(index - leading) % _auto.Length];
    }

    /// <summary>
    /// Reads one property: its list with the integer repeaters written out and the automatic one kept
    /// apart, ready for the gaps to ask for their own values.
    /// </summary>
    internal static GapRuleTerms<T> Read(ICssProperty? declared, T initial, Func<ICssValue, T> value)
    {
        var items = Items(declared);

        if (items is null)
        {
            return new GapRuleTerms<T>(initial, null);
        }

        var leading = new List<T>();
        var auto = default(List<T>);
        var trailing = new List<T>();
        var section = leading;

        foreach (var item in items)
        {
            if (Repeats(item, out var count, out var inner))
            {
                if (count is null)
                {
                    auto ??= new List<T>();
                    section = auto;
                    Expand(section, inner, 1, value);
                }
                else
                {
                    Expand(section, inner, Math.Clamp(count.Value, 0, MaxRepeat), value);
                }

                continue;
            }

            if (auto is not null && ReferenceEquals(section, auto))
            {
                section = trailing;
            }

            section.Add(value(item));
        }

        return new GapRuleTerms<T>(initial, Frozen(leading), Frozen(auto), Frozen(trailing));
    }

    /// <summary>
    /// Keeps an integer repeater from asking for more values than a page could ever show: the notation is
    /// written out, so an enormous count would otherwise be an enormous list.
    /// </summary>
    private const Int32 MaxRepeat = 1000;

    private static void Expand(List<T> target, IReadOnlyList<ICssValue> inner, Int32 times, Func<ICssValue, T> value)
    {
        for (var turn = 0; turn < times; turn++)
        {
            foreach (var one in inner)
            {
                target.Add(value(one));
            }
        }
    }

    /// <summary>
    /// Reads a <c>repeat()</c> notation: the count it turns (<c>null</c> where it is <c>auto</c>) and the
    /// values it holds. Anything else is not one.
    /// </summary>
    private static Boolean Repeats(ICssValue item, out Int32? count, out IReadOnlyList<ICssValue> values)
    {
        count = null;
        values = Array.Empty<ICssValue>();

        if (item is not ICssFunctionValue function ||
            !String.Equals(function.Name, "repeat", StringComparison.OrdinalIgnoreCase) ||
            function.Arguments is not { Length: 2 } arguments)
        {
            return false;
        }

        var written = arguments[0].CssText?.Trim();

        if (String.Equals(written, "auto", StringComparison.OrdinalIgnoreCase))
        {
            values = Inner(arguments[1]);
            return true;
        }

        if (!Int32.TryParse(written, out var turns))
        {
            return false;
        }

        count = turns;
        values = Inner(arguments[1]);
        return true;
    }

    private static IReadOnlyList<ICssValue> Inner(ICssValue value) =>
        LengthReader.Items(value) ?? new[] { value };

    private static ICssValue[]? Items(ICssProperty? declared)
    {
        var value = declared?.RawValue;

        if (value is null)
        {
            return null;
        }

        if (LengthReader.Items(value) is { } list)
        {
            var items = new ICssValue[list.Count];

            for (var index = 0; index < items.Length; index++)
            {
                items[index] = list[index];
            }

            return items;
        }

        return new[] { value };
    }

    private static T[]? Frozen(List<T>? values) => values is null || values.Count == 0 ? null : values.ToArray();

    private static T[] Joined(T[] leading, T[] trailing)
    {
        if (trailing.Length == 0)
        {
            return leading;
        }

        if (leading.Length == 0)
        {
            return trailing;
        }

        var joined = new T[leading.Length + trailing.Length];

        leading.CopyTo(joined, 0);
        trailing.CopyTo(joined, leading.Length);

        return joined;
    }
}

/// <summary>
/// One gap's decoration, standing where the container's layout put the gap.
/// </summary>
internal readonly struct GapRuleSegment
{
    internal GapRuleSegment(Rect band, GapRule rule, Boolean isRow)
    {
        Band = band;
        Rule = rule;
        IsRow = isRow;
    }

    /// <summary>Gets the band the rule is drawn in: as wide as the rule, centred on the gap.</summary>
    public Rect Band { get; }

    /// <summary>Gets the stroke it is drawn with.</summary>
    public GapRule Rule { get; }

    /// <summary>
    /// Gets whether this decorates a row gap - one that separates along the block axis, and which stands
    /// above a column decoration where the two cross unless the page says otherwise.
    /// </summary>
    public Boolean IsRow { get; }
}

/// <summary>
/// The three lists of one axis's gap decorations, and the rule the gap at a given number comes to.
/// </summary>
internal sealed class GapRuleAxis
{
    private readonly GapRuleTerms<Double> _width;
    private readonly GapRuleTerms<BorderLineStyle> _style;
    private readonly GapRuleTerms<UInt32> _color;

    internal GapRuleAxis(
        GapRuleTerms<Double> width,
        GapRuleTerms<BorderLineStyle> style,
        GapRuleTerms<UInt32> color)
    {
        _width = width;
        _style = style;
        _color = color;
    }

    /// <summary>Gets whether the page asked for decorations along this axis.</summary>
    public Boolean IsStated => _width.IsStated || _style.IsStated || _color.IsStated;

    /// <summary>Gets the decoration the <paramref name="index"/>th gap of <paramref name="count"/> takes.</summary>
    public GapRule At(Int32 index, Int32 count) =>
        new(_width.At(index, count), _style.At(index, count), _color.At(index, count));

    /// <summary>
    /// Reads one axis, from the longhands the page wrote.
    /// </summary>
    /// <remarks>
    /// A shorthand needs no reading here: AngleSharp expands <c>row-rule: 5px solid gold</c> into the
    /// three longhands it stands for, so the width, style and colour below are already the values the
    /// cascade settled - which is what lets a longhand written beside a shorthand, or a shorthand written
    /// <c>!important</c> over a longhand, come out the way the cascade ordered them.
    /// </remarks>
    /// <param name="initial">The colour a gap whose list ran out takes: <c>currentcolor</c>.</param>
    internal static GapRuleAxis Read(
        Func<String, ICssProperty?> find,
        String prefix,
        LengthReader.FontSource font,
        IRenderDimensions dimensions,
        CssColorValue initial) =>
        new(
            GapRuleTerms<Double>.Read(find(prefix + "-width"), InitialWidth, item => ToWidth(item, dimensions, font)),
            GapRuleTerms<BorderLineStyle>.Read(find(prefix + "-style"), BorderLineStyle.None, ToStyle),
            GapRuleTerms<UInt32>.Read(find(prefix + "-color"), ComputedStyle.ToArgb(initial), item => ToColor(item, initial)));

    /// <summary>Gets the width a rule whose page said nothing about it takes: <c>medium</c>.</summary>
    internal const Double InitialWidth = 3.0;

    private static Double ToWidth(ICssValue item, IRenderDimensions dimensions, LengthReader.FontSource font) =>
        item.CssText?.Trim() switch
        {
            "thin" => 1.0,
            "medium" => 3.0,
            "thick" => 5.0,
            _ => LengthReader.TryReadValue(item, RenderMode.Horizontal, dimensions, font, out var stated) &&
                stated.HasValue
                    ? Math.Max(0, stated.Resolve(0))
                    : 0.0,
        };

    private static BorderLineStyle ToStyle(ICssValue item) =>
        ComputedStyle.ToLineStyle(item.CssText?.Trim() ?? String.Empty);

    private static UInt32 ToColor(ICssValue item, CssColorValue initial) =>
        item is CssColorValue color
            ? ComputedStyle.ToArgb(color == CssColorValue.CurrentColor ? initial : color)
            : BackgroundLayerParser.ParseColor(item.CssText ?? String.Empty) ?? ComputedStyle.ToArgb(initial);
}

/// <summary>
/// The gap decorations of a container: the rules each of its two axes asks for, and which of the two
/// stands above the other where they cross (CSS Gaps 1 §3, §4).
/// </summary>
/// <remarks>
/// The rules are read for the container and drawn per gap by the layout that makes the gaps, because a
/// gap's decoration is the gap's own entry in a list - and only the layout knows how many gaps there are
/// and where each of them stands.
/// </remarks>
internal sealed class GapRules
{
    /// <summary>The rules of a box the page said nothing about: nothing is drawn anywhere.</summary>
    internal static readonly GapRules None = new(
        new GapRuleAxis(
            new GapRuleTerms<Double>(GapRuleAxis.InitialWidth, null),
            new GapRuleTerms<BorderLineStyle>(BorderLineStyle.None, null),
            new GapRuleTerms<UInt32>(0, null)),
        new GapRuleAxis(
            new GapRuleTerms<Double>(GapRuleAxis.InitialWidth, null),
            new GapRuleTerms<BorderLineStyle>(BorderLineStyle.None, null),
            new GapRuleTerms<UInt32>(0, null)),
        false);

    internal GapRules(GapRuleAxis column, GapRuleAxis row, Boolean columnOverRow)
    {
        Column = column;
        Row = row;
        ColumnOverRow = columnOverRow;
    }

    /// <summary>Gets the decorations of the gaps between columns.</summary>
    public GapRuleAxis Column { get; }

    /// <summary>Gets the decorations of the gaps between rows.</summary>
    public GapRuleAxis Row { get; }

    /// <summary>Gets whether column decorations are painted above the row ones (CSS Gaps 1 §3.5).</summary>
    public Boolean ColumnOverRow { get; }

    /// <summary>Gets whether the page asked for any gap decoration on this box.</summary>
    public Boolean IsStated => Column.IsStated || Row.IsStated;

    /// <summary>
    /// Reads the two axes' decorations and the overlap order from the cascaded declarations. A box that
    /// writes none of them gets nothing: <see cref="None"/>, whose rules are all of no style.
    /// </summary>
    public static GapRules Read(
        Func<String, ICssProperty?> find,
        LengthReader.FontSource font,
        IRenderDimensions dimensions,
        CssColorValue color)
    {
        var rules = new GapRules(
            GapRuleAxis.Read(find, "column-rule", font, dimensions, color),
            GapRuleAxis.Read(find, "row-rule", font, dimensions, color),
            IsColumnOverRow(find("rule-overlap")));

        return rules.IsStated ? rules : None;
    }

    /// <summary>
    /// Reads the decorations of a style the box builder resolved rather than fused. The same longhands
    /// answer both paths, so a rule a page asks for stands wherever its container puts a gap, whichever
    /// way the style came together.
    /// </summary>
    public static GapRules Read(ComputedStyle style) => Read(
        name => style.Declaration.TryGetProperty(name),
        new LengthReader.FontSource(style),
        style.Dimensions,
        style.Color);

    private static Boolean IsColumnOverRow(ICssProperty? declared) =>
        String.Equals(declared?.Value?.Trim(), "column-over-row", StringComparison.OrdinalIgnoreCase);
}

using AngleSharp.Css;
using AngleSharp.Css.Dom;
using AngleSharp.Css.Values;
using Hutu.Rendering.Media;
using System;

namespace Hutu.Rendering.Layout;

/// <summary>
/// The stroke a gap asks to be drawn in the middle of it: how thick, in what style and in what colour
/// (CSS Multicol 3 §4). A rule is a border side turned to face down the gap, so it takes the same
/// strokes - a dashed one dashes along its length and a double one is two lines within its width.
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
/// The multi-column longhands of a box, and the column count and width they come to for a given content
/// extent (CSS Multicol 3 §3.4).
/// </summary>
/// <remarks>
/// <c>column-count</c> and <c>column-width</c> are both optional and answer each other: whichever is
/// <c>auto</c> is worked out from the other and from the room the columns have. A box that writes
/// neither is no multi-column container, which is what <see cref="IsMultiColumn"/> says.
/// </remarks>
internal readonly struct MulticolStyle
{
    internal MulticolStyle(Int32? count, Double? width, Double gap, Boolean fills, GapRule rule = default)
    {
        Count = count is { } declared ? Math.Max(1, declared) : null;
        Width = width is { } sized ? Math.Max(0, sized) : null;
        Gap = Math.Max(0, gap);
        Fills = fills;
        Rule = rule;
    }

    /// <summary>Gets the declared column count, or null where it is <c>auto</c>.</summary>
    public Int32? Count { get; }

    /// <summary>Gets the declared column width in pixels, or null where it is <c>auto</c>.</summary>
    public Double? Width { get; }

    /// <summary>Gets the gap between adjacent columns, in pixels.</summary>
    public Double Gap { get; }

    /// <summary>Gets whether <c>column-fill</c> is <c>auto</c>: columns are filled to the container's
    /// height in turn rather than balanced.</summary>
    public Boolean Fills { get; }

    /// <summary>Gets the rule the column gaps are asked to carry.</summary>
    public GapRule Rule { get; }

    /// <summary>Gets whether the box asks for columns at all.</summary>
    public Boolean IsMultiColumn => Count is not null || Width is not null;

    /// <summary>
    /// Works out the column count and the width each column takes in a content box of
    /// <paramref name="available"/> pixels: the pseudo-algorithm of CSS Multicol 3 §3.4.
    /// </summary>
    /// <remarks>
    /// A declared width is a least: the count is what fits at that width, clamped to the declared count
    /// where both are given, and the columns then share the room - so a table asked for three columns of
    /// eighty pixels in three hundred shares ninety-three each, and one asked for six in a hundred gets
    /// the single column its room allows.
    /// </remarks>
    public (Int32 Count, Double Width) Used(Double available)
    {
        var room = Math.Max(0, available);
        var gap = Gap;

        if (Count is null && Width is null)
        {
            return (1, room);
        }

        if (Count is null)
        {
            return (Fitting(room, Width!.Value, gap), Width!.Value);
        }

        if (Width is null)
        {
            var count = Count.Value;
            return (count, Math.Max(0, (room - (count - 1) * gap) / count));
        }

        var used = Math.Clamp(Count.Value, 1, Fitting(room, Width.Value, gap));

        return (used, Math.Max(Width.Value, Math.Max(0, (room - (used - 1) * gap) / used)));
    }

    private static Int32 Fitting(Double room, Double width, Double gap) =>
        width + gap <= 0 ? 1 : Math.Max(1, (Int32)Math.Floor((room + gap) / (width + gap)));

    /// <summary>
    /// Reads the multi-column longhands from the element's own cascaded declarations. A box that declares
    /// neither of the two sizing longhands gets no style at all: it is not a container, and nothing about
    /// it should change.
    /// </summary>
    public static MulticolStyle? ReadFused(
        Func<String, ICssProperty?> find,
        LengthReader.FontSource font,
        IRenderDimensions dimensions,
        CssColorValue color)
    {
        var count = CountOf(find("column-count"));
        var width = WidthOf(find("column-width"), font, dimensions);

        if (count is null && width is null)
        {
            return null;
        }

        // `column-gap: normal` is one em in a multi-column container (CSS Gaps 1 §2.1), which is where
        // this differs from the zero a row or a grid track takes. A gap the page wrote as zero is a
        // zero, so the keyword is told from the number by the declaration being there at all.
        var declaredGap = find("column-gap") is not null || find("grid-column-gap") is not null;
        var gap = declaredGap ? FlexStyle.GapsFused(find, font, dimensions).Column : font.Value;

        var fill = find("column-fill")?.Value?.Trim();
        var fills = String.Equals(fill, "auto", StringComparison.OrdinalIgnoreCase);

        return new MulticolStyle(count, width, gap, fills, RuleOf(find, font, dimensions, color));
    }

    /// <summary>
    /// Reads the three <c>column-rule</c> longhands. The shorthand expands into exactly these, and the
    /// values it leaves out land on the longhands' own initials - <c>medium</c>, <c>none</c> and the
    /// text's own colour - which is the default the shorthand asks for (§4.5).
    /// </summary>
    private static GapRule RuleOf(
        Func<String, ICssProperty?> find,
        LengthReader.FontSource font,
        IRenderDimensions dimensions,
        CssColorValue color)
    {
        var style = find("column-rule-style")?.Value?.Trim();

        var stroke = String.IsNullOrEmpty(style)
            ? BorderLineStyle.None
            : ComputedStyle.ToLineStyle(style);

        // The three keywords a border width takes, and the length it usually is. `medium` is what the
        // property's initial value says, so a page that states a style and no width gets a rule of three.
        var declared = find("column-rule-width")?.Value?.Trim();

        var width = declared switch
        {
            null or "" => 3.0,
            "thin" => 1.0,
            "medium" => 3.0,
            "thick" => 5.0,
            _ => LengthReader.TryReadProperty(find("column-rule-width")!, RenderMode.Horizontal, dimensions, font, out var stated) &&
                stated.HasValue
                    ? stated.Resolve(0)
                    : 0.0,
        };

        var painted = find("column-rule-color")?.Value?.Trim();

        var ruleColor = String.IsNullOrEmpty(painted) || BackgroundLayerParser.ParseColor(painted!) is not { } argb
            ? ComputedStyle.ToArgb(color)
            : argb;

        return new GapRule(width, stroke, ruleColor);
    }

    private static Int32? CountOf(ICssProperty? declared)
    {
        var text = declared?.Value?.Trim();

        return Int32.TryParse(text, out var count) && count > 0 ? count : null;
    }

    private static Double? WidthOf(ICssProperty? declared, LengthReader.FontSource font, IRenderDimensions dimensions)
    {
        if (declared is null)
        {
            return null;
        }

        return LengthReader.TryReadProperty(declared, RenderMode.Horizontal, dimensions, font, out var width) &&
            width.HasValue &&
            width.Kind != StyleLengthKind.Percent
                ? Math.Max(0, width.Resolve(0))
                : null;
    }
}

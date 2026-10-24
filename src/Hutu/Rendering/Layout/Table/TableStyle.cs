using AngleSharp.Css;
using AngleSharp.Css.Dom;
using AngleSharp.Css.Values;
using System;

namespace Hutu.Rendering.Layout;

/// <summary>Where a caption is placed relative to the rows.</summary>
internal enum CaptionSide : byte
{
    Top,
    Bottom,
}

/// <summary>How a cell's content is aligned along the block (vertical) axis of the cell box.</summary>
internal enum CellVerticalAlignment : byte
{
    Baseline,
    Top,
    Middle,
    Bottom,
}

/// <summary>
/// The table longhands of a box, plus the cell's <c>vertical-align</c>.
/// </summary>
/// <remarks>
/// The keywords are read from the serialised value rather than from the typed constants. That is the
/// opposite of the rule the flex reader follows, and it is deliberate: AngleSharp.Css models
/// <c>border-collapse</c>, <c>caption-side</c>, <c>table-layout</c> and <c>empty-cells</c> with
/// <see cref="ValueConverters.Toggle"/>, which produces a <c>CssConstantValue&lt;Boolean&gt;</c> whose
/// polarity is not the one the keyword suggests - <c>border-collapse</c> reads <c>true</c> for
/// <c>separate</c> and <c>false</c> for <c>collapse</c>. The serialisation round-trips faithfully, so
/// the text is the only source that is not a trap.
/// </remarks>
internal readonly struct TableStyle
{
    private TableStyle(
        Boolean collapse,
        Double horizontalSpacing,
        Double verticalSpacing,
        Boolean fixedLayout,
        Boolean hideEmptyCells,
        CaptionSide captionSide,
        CellVerticalAlignment verticalAlign)
    {
        Collapse = collapse;
        HorizontalSpacing = horizontalSpacing;
        VerticalSpacing = verticalSpacing;
        Fixed = fixedLayout;
        HideEmptyCells = hideEmptyCells;
        CaptionSide = captionSide;
        VerticalAlign = verticalAlign;
    }

    /// <summary>Gets whether the borders of adjacent boxes are merged into a single line.</summary>
    public Boolean Collapse { get; }

    /// <summary>Gets the distance between adjacent column borders, and between them and the table edge.</summary>
    public Double HorizontalSpacing { get; }

    /// <summary>
    /// Gets the spacing of the table grown by the zoom the box carries. The two distances are lengths the
    /// page wrote, and a scale reaches every length of the box it is put on; what the style holds stays the
    /// declaration's own, so one table style serves the table at two and the table at one.
    /// </summary>
    internal TableStyle Scaled(Double factor) =>
        factor == 1
            ? this
            : new TableStyle(
                Collapse,
                HorizontalSpacing * factor,
                VerticalSpacing * factor,
                Fixed,
                HideEmptyCells,
                CaptionSide,
                VerticalAlign);

    /// <summary>Gets the distance between adjacent row borders, and between them and the table edge.</summary>
    public Double VerticalSpacing { get; }

    /// <summary>Gets whether column widths come from the first row and the column declarations.</summary>
    public Boolean Fixed { get; }

    /// <summary>Gets whether a cell with no content paints nothing.</summary>
    public Boolean HideEmptyCells { get; }

    public CaptionSide CaptionSide { get; }

    /// <summary>Gets the cell's vertical alignment. Meaningful on a cell box.</summary>
    public CellVerticalAlignment VerticalAlign { get; }

    internal static TableStyle Read(ComputedStyle style, Double spacing, Boolean collapses)
    {
        var (horizontal, vertical) = ReadSpacing(style, spacing);
        var declared = Text(style, "border-collapse");

        return new TableStyle(
            declared.Length > 0 ? declared == "collapse" : collapses,
            horizontal,
            vertical,
            Text(style, "table-layout") == "fixed",
            Text(style, "empty-cells") == "hide",
            Text(style, "caption-side") == "bottom" ? CaptionSide.Bottom : CaptionSide.Top,
            ReadVerticalAlign(style));
    }

    /// <summary>
    /// Reads the table longhands from the element's own cascaded declarations, for the fused path.
    /// Nothing here is inherited, so a miss takes the property's initial value.
    /// </summary>
    /// <param name="verticalAlign">
    /// The <c>vertical-align</c> keyword the caller has already resolved for this element. The cell
    /// alignment is the one longhand that is a text property everywhere else, and asking for it twice
    /// would mean reading the same declaration twice.
    /// </param>
    /// <param name="spacing">
    /// The distance the table's own <c>cellspacing</c> hints at, or the distance HTML gives a table that
    /// writes no such attribute. It stands in for every side the page left unwritten, and a page that
    /// wrote <c>border-spacing</c> itself is read instead of it.
    /// </param>
    internal static TableStyle ReadFused(
        Func<String, ICssProperty?> find,
        String verticalAlign,
        LengthReader.FontSource font,
        IRenderDimensions dimensions,
        Double spacing,
        Boolean collapses)
    {
        var (horizontal, vertical) = ReadSpacingFused(find("border-spacing"), spacing, font, dimensions);
        var declared = Keyword(find("border-collapse"));

        return new TableStyle(
            declared.Length > 0 ? declared == "collapse" : collapses,
            horizontal,
            vertical,
            Keyword(find("table-layout")) == "fixed",
            Keyword(find("empty-cells")) == "hide",
            Keyword(find("caption-side")) == "bottom" ? CaptionSide.Bottom : CaptionSide.Top,
            Align(verticalAlign));
    }

    /// <summary>
    /// Reads the two <c>border-spacing</c> lengths from the cascaded declaration, or the distance the
    /// table's attributes leave the engine to supply where the page wrote neither. A single value
    /// applies to both axes; two are the horizontal then the vertical one, in that order.
    /// </summary>
    private static (Double Horizontal, Double Vertical) ReadSpacingFused(
        ICssProperty? declared, Double spacing, LengthReader.FontSource font, IRenderDimensions dimensions)
    {
        if (declared is null)
        {
            return (spacing, spacing);
        }

        // The two lengths are read through the multiple-value interface rather than the tuple class:
        // AngleSharp produces a generic instantiation the tuple class alone does not match.
        if (LengthReader.Items(declared.RawValue) is { Count: > 0 } items)
        {
            var horizontal = SpacingOf(items[0], font, dimensions);

            if (horizontal is { } first)
            {
                return (first, items.Count > 1 ? SpacingOf(items[1], font, dimensions) ?? first : first);
            }
        }

        if (LengthReader.TryReadProperty(declared, RenderMode.Horizontal, dimensions, font, out var single) &&
            single.HasValue)
        {
            var value = Math.Max(0, single.Resolve(0));
            return (value, value);
        }

        return (0, 0);
    }

    private static Double? SpacingOf(ICssValue item, LengthReader.FontSource font, IRenderDimensions dimensions) =>
        LengthReader.TryReadValue(item, RenderMode.Horizontal, dimensions, font, out var length) && length.HasValue
            ? Math.Max(0, length.Resolve(0))
            : null;

    /// <summary>Gets a keyword as the cascade wrote it, lowered and trimmed.</summary>
    private static String Keyword(ICssProperty? declared) =>
        declared?.Value?.Trim().ToLowerInvariant() ?? String.Empty;

    /// <summary>
    /// Reads the two <c>border-spacing</c> lengths, taking the distance the table's attributes leave to
    /// the engine where the page wrote neither. A single value applies to both axes; two are the
    /// horizontal then the vertical one, in that order.
    /// </summary>
    private static (Double Horizontal, Double Vertical) ReadSpacing(ComputedStyle style, Double spacing)
    {
        // The two lengths are read through the multiple-value interface rather than the tuple class:
        // AngleSharp produces a generic instantiation the tuple class alone does not match.
        if (LengthReader.Items(style.RawValue("border-spacing")) is { Count: > 0 } items)
        {
            var horizontal = SpacingOf(style, items[0]);

            if (horizontal is { } first)
            {
                return (first, items.Count > 1 ? SpacingOf(style, items[1]) ?? first : first);
            }
        }

        if (LengthReader.TryRead(style.Specified, style.Declaration, "border-spacing", RenderMode.Horizontal, style.Dimensions, style.FontSize, out var single) &&
            single.HasValue)
        {
            var value = Math.Max(0, single.Resolve(0));
            return (value, value);
        }

        return (spacing, spacing);
    }

    private static Double? SpacingOf(ComputedStyle style, ICssValue item) =>
        LengthReader.TryReadValue(item, RenderMode.Horizontal, style.Dimensions, style.FontSize, out var length) && length.HasValue
            ? Math.Max(0, length.Resolve(0))
            : null;

    private static CellVerticalAlignment ReadVerticalAlign(ComputedStyle style) => Align(Text(style, "vertical-align"));

    private static CellVerticalAlignment Align(String text) => text switch
    {
        "top" or "text-top" => CellVerticalAlignment.Top,
        "middle" => CellVerticalAlignment.Middle,
        "bottom" or "text-bottom" => CellVerticalAlignment.Bottom,
        _ => CellVerticalAlignment.Baseline,
    };

    /// <summary>Gets a keyword, preferring the cascaded declaration, lowered and trimmed.</summary>
    private static String Text(ComputedStyle style, String name)
    {
        var text = style.Specified?.GetPropertyValue(name);

        if (String.IsNullOrWhiteSpace(text))
        {
            text = style.Declaration.GetPropertyValue(name);
        }

        return text is null ? String.Empty : text.Trim().ToLowerInvariant();
    }
}

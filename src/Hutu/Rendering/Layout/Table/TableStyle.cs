using AngleSharp.Css;
using AngleSharp.Css.Dom;
using AngleSharp.Css.Values;
using System;

namespace Hutu.Rendering.Layout;

/// <summary>Where a caption is placed relative to the rows.</summary>
public enum CaptionSide : byte
{
    Top,
    Bottom,
}

/// <summary>How a cell's content is aligned along the block (vertical) axis of the cell box.</summary>
public enum CellVerticalAlignment : byte
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
public readonly struct TableStyle
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

    /// <summary>Gets the distance between adjacent row borders, and between them and the table edge.</summary>
    public Double VerticalSpacing { get; }

    /// <summary>Gets whether column widths come from the first row and the column declarations.</summary>
    public Boolean Fixed { get; }

    /// <summary>Gets whether a cell with no content paints nothing.</summary>
    public Boolean HideEmptyCells { get; }

    public CaptionSide CaptionSide { get; }

    /// <summary>Gets the cell's vertical alignment. Meaningful on a cell box.</summary>
    public CellVerticalAlignment VerticalAlign { get; }

    internal static TableStyle Read(ComputedStyle style)
    {
        var (horizontal, vertical) = ReadSpacing(style);

        return new TableStyle(
            Text(style, "border-collapse") == "collapse",
            horizontal,
            vertical,
            Text(style, "table-layout") == "fixed",
            Text(style, "empty-cells") == "hide",
            Text(style, "caption-side") == "bottom" ? CaptionSide.Bottom : CaptionSide.Top,
            ReadVerticalAlign(style));
    }

    /// <summary>
    /// Reads the two <c>border-spacing</c> lengths. A single value applies to both axes; two are the
    /// horizontal then the vertical one, in that order.
    /// </summary>
    private static (Double Horizontal, Double Vertical) ReadSpacing(ComputedStyle style)
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

        return (0, 0);
    }

    private static Double? SpacingOf(ComputedStyle style, ICssValue item) =>
        LengthReader.TryReadValue(item, RenderMode.Horizontal, style.Dimensions, style.FontSize, out var length) && length.HasValue
            ? Math.Max(0, length.Resolve(0))
            : null;

    private static CellVerticalAlignment ReadVerticalAlign(ComputedStyle style) => Text(style, "vertical-align") switch
    {
        "top" or "text-top" => CellVerticalAlignment.Top,
        "middle" => CellVerticalAlignment.Middle,
        "bottom" or "text-bottom" => CellVerticalAlignment.Bottom,
        _ => CellVerticalAlignment.Baseline,
    };

    /// <summary>Gets a keyword, preferring the cascaded declaration, lowered and trimmed.</summary>
    private static String Text(ComputedStyle style, String name)
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

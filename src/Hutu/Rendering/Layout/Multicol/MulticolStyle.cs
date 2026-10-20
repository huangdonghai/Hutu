using AngleSharp.Css;
using AngleSharp.Css.Dom;
using AngleSharp.Css.Values;
using Hutu.Rendering.Media;
using System;

namespace Hutu.Rendering.Layout;

/// <summary>
/// The multi-column longhands of a box, and the column count and width they come to for a given content
/// extent (CSS Multicol 3 §3.4).
/// </summary>
/// <remarks>
/// <c>column-count</c> and <c>column-width</c> are both optional and answer each other: whichever is
/// <c>auto</c> is worked out from the other and from the room the columns have. A box that writes
/// neither is no multi-column container, which is what <see cref="IsMultiColumn"/> says.
/// The stroke a column gap asks for is not read here: CSS Gaps 1 §4 takes the <c>column-rule</c>
/// properties over for every container that has gaps, so <see cref="GapRules"/> holds them.
/// </remarks>
internal readonly struct MulticolStyle
{
    internal MulticolStyle(
        Int32? count,
        Double? width,
        Double gap,
        Boolean fills,
        Double? height = null,
        Boolean wraps = false,
        Double rowGap = 0)
    {
        Count = count is { } declared ? Math.Max(1, declared) : null;
        Width = width is { } sized ? Math.Max(0, sized) : null;
        Gap = Math.Max(0, gap);
        Fills = fills;
        Height = height is { } tall ? Math.Max(0, tall) : null;
        Wraps = wraps;
        RowGap = Math.Max(0, rowGap);
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

    /// <summary>Gets the height a column box takes, or null where it is <c>auto</c>.</summary>
    public Double? Height { get; }

    /// <summary>Gets whether column boxes that run out of room start another row of them.</summary>
    public Boolean Wraps { get; }

    /// <summary>Gets the gap between two rows of column boxes.</summary>
    public Double RowGap { get; }

    /// <summary>Gets whether the box asks for columns at all.</summary>
    public Boolean IsMultiColumn => Count is not null || Width is not null;

    /// <summary>
    /// Gets the column boxes a content box of this width and height makes: how many columns, how wide,
    /// how tall a box is, and how many rows of them the container's height allows when the columns wrap
    /// (CSS Multicol 2 §3).
    /// </summary>
    public (Int32 Columns, Double Width, Double Height, Int32 Rows) Grid(Double available, Double height)
    {
        var (columns, width) = Used(available);
        var box = Height ?? Math.Max(0, height);

        if (!Wraps || Height is not { } stated || stated <= 0 || height <= 0)
        {
            return (columns, width, box, 1);
        }

        var rows = Math.Max(1, (Int32)Math.Floor((height + RowGap) / (box + RowGap)));

        return (columns, width, box, rows);
    }

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
        IRenderDimensions dimensions)
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

        var height = HeightOf(find("column-height"), font, dimensions);
        var wraps = String.Equals(find("column-wrap")?.Value?.Trim(), "wrap", StringComparison.OrdinalIgnoreCase);
        var rowGap = FlexStyle.GapsFused(find, font, dimensions).Row;

        return new MulticolStyle(count, width, gap, fills, height, wraps, rowGap);
    }

    private static Int32? CountOf(ICssProperty? declared)
    {
        var text = declared?.Value?.Trim();

        return Int32.TryParse(text, out var count) && count > 0 ? count : null;
    }

    private static Double? HeightOf(ICssProperty? declared, LengthReader.FontSource font, IRenderDimensions dimensions)
    {
        if (declared is null)
        {
            return null;
        }

        return LengthReader.TryReadProperty(declared, RenderMode.Horizontal, dimensions, font, out var height) &&
            height.HasValue &&
            height.Kind != StyleLengthKind.Percent
                ? Math.Max(0, height.Resolve(0))
                : null;
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

using AngleSharp.Css;
using AngleSharp.Css.Dom;
using System;

namespace Hutu.Rendering.Layout;

/// <summary>
/// The size containment a box asks for, and the size it states for itself where it contains one
/// (CSS Contain 2 §2.1, CSS Sizing 4 §5).
/// </summary>
/// <remarks>
/// A box that contains its size is not measured from its content at all: its content could be anything
/// and the box must not change size because of it. What it comes to is <c>contain-intrinsic-size</c>,
/// which is <c>auto</c> - nothing - unless the page states one.
/// </remarks>
internal readonly struct ContainStyle
{
    internal ContainStyle(Boolean inline, Boolean block, StyleLength? width, StyleLength? height)
    {
        Inline = inline;
        Block = block;
        Width = width;
        Height = height;
    }

    /// <summary>Gets whether the box's inline size comes from its stated intrinsic size.</summary>
    public Boolean Inline { get; }

    /// <summary>Gets whether the box's block size comes from its stated intrinsic size.</summary>
    public Boolean Block { get; }

    /// <summary>Gets the intrinsic width, or null where it is <c>auto</c>.</summary>
    public StyleLength? Width { get; }

    /// <summary>Gets the intrinsic height, or null where it is <c>auto</c>.</summary>
    public StyleLength? Height { get; }

    /// <summary>Gets whether either axis is contained.</summary>
    public Boolean IsContained => Inline || Block;

    /// <summary>
    /// Reads the containment from the box's own declarations, or null where the page asked for none -
    /// which is every box that writes no <c>contain</c> at all.
    /// </summary>
    public static ContainStyle? ReadFused(
        Func<String, ICssProperty?> find,
        LengthReader.FontSource font,
        IRenderDimensions dimensions)
    {
        var contain = find("contain")?.Value?.Trim();

        if (String.IsNullOrEmpty(contain))
        {
            return null;
        }

        // `strict` is size, layout, paint and style; `content` is the other three without size, so it
        // does not contain either axis.
        var size = Has(contain!, "size") || Has(contain!, "strict");

        // The shorthand states one size for both axes or one for each; it is not expanded into the
        // longhands by the parser, so it is read whole and taken apart here.
        var stated = Lengths(find("contain-intrinsic-size")?.RawValue, dimensions, font);

        return new ContainStyle(
            size || Has(contain!, "inline-size"),
            size || Has(contain!, "block-size"),
            stated.Count > 0 ? stated[0] : LengthOf(find("contain-intrinsic-width"), dimensions, font),
            stated.Count > 1 ? stated[1] : stated.Count > 0 ? stated[0] : LengthOf(find("contain-intrinsic-height"), dimensions, font));
    }

    private static Boolean Has(String text, String keyword) =>
        Array.Exists(
            text.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries),
            token => String.Equals(token, keyword, StringComparison.OrdinalIgnoreCase));

    /// <summary>Gets the lengths an intrinsic size states, one for both axes or one for each.</summary>
    private static System.Collections.Generic.List<StyleLength> Lengths(
        AngleSharp.Css.Dom.ICssValue? value,
        IRenderDimensions dimensions,
        LengthReader.FontSource font)
    {
        var lengths = new System.Collections.Generic.List<StyleLength>();

        if (value is null)
        {
            return lengths;
        }

        if (LengthReader.Items(value) is { } items)
        {
            foreach (var item in items)
            {
                if (LengthReader.TryReadValue(item, RenderMode.Horizontal, dimensions, font, out var length) &&
                    length.HasValue &&
                    length.Kind != StyleLengthKind.Percent)
                {
                    lengths.Add(length);
                }
            }

            return lengths;
        }

        if (LengthReader.TryReadValue(value, RenderMode.Horizontal, dimensions, font, out var single) &&
            single.HasValue &&
            single.Kind != StyleLengthKind.Percent)
        {
            lengths.Add(single);
        }

        return lengths;
    }

    private static StyleLength? LengthOf(ICssProperty? declared, IRenderDimensions dimensions, LengthReader.FontSource font)
    {
        if (declared is null)
        {
            return null;
        }

        return LengthReader.TryReadProperty(declared, RenderMode.Horizontal, dimensions, font, out var length) &&
            length.HasValue &&
            length.Kind != StyleLengthKind.Percent
                ? length
                : null;
    }
}

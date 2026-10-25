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
    internal ContainStyle(
        Boolean inline,
        Boolean block,
        StyleLength? width,
        StyleLength? height,
        StyleLength? inlineSize,
        StyleLength? blockSize,
        Boolean stated,
        Boolean layout,
        Boolean paint)
    {
        Inline = inline;
        Block = block;
        Width = width;
        Height = height;
        InlineSize = inlineSize;
        BlockSize = blockSize;
        Stated = stated;
        Layout = layout;
        Paint = paint;
    }

    /// <summary>
    /// Gets whether the box asks for any containment at all, of whichever kind. Only the two size axes
    /// reach the layout, but the canvas background reads the whole answer: CSS Backgrounds 3 §2.11.2's note
    /// says a body with containment has no background to hand to the canvas, and the containment that stops
    /// the handing is any of it - paint, layout, style or size - not only the size the layout contains.
    /// </summary>
    public Boolean Stated { get; }

    /// <summary>Gets whether the box's inline size comes from its stated intrinsic size.</summary>
    public Boolean Inline { get; }

    /// <summary>Gets whether the box's block size comes from its stated intrinsic size.</summary>
    public Boolean Block { get; }

    /// <summary>Gets the intrinsic width, or null where it is <c>auto</c>.</summary>
    public StyleLength? Width { get; }

    /// <summary>
    /// Gets the intrinsic size along the inline axis, as <c>contain-intrinsic-inline-size</c> states
    /// it, or null where the page left it to the physical longhand. It is the axis the writing mode
    /// decides, which is why it is carried apart from <see cref="Width"/> and <see cref="Height"/>.
    /// </summary>
    public StyleLength? InlineSize { get; }

    /// <summary>Gets the intrinsic size along the block axis, as <c>contain-intrinsic-block-size</c>
    /// states it, or null where the page left it to the physical longhand.</summary>
    public StyleLength? BlockSize { get; }

    /// <summary>Gets the intrinsic height, or null where it is <c>auto</c>.</summary>
    public StyleLength? Height { get; }

    /// <summary>Gets whether either axis is contained.</summary>
    public Boolean IsContained => Inline || Block;

    /// <summary>
    /// Gets whether a size the page stated settles an axis on its own, so the intrinsic size the page
    /// also stated has nothing to answer: a length, or a share of a basis that is itself known. A share
    /// of an indefinite basis states nothing, which is why a <c>height: 100%</c> inside a block of auto
    /// height still lets the contained size through.
    /// </summary>
    public static Boolean IsDefinite(StyleLength stated, Double basis) =>
        stated.HasValue && !(stated.Kind == StyleLengthKind.Percent && basis <= 0);

    /// <summary>
    /// Gets whether the box contains its layout, which makes it a stacking context and the containing
    /// block for any descendant taken out of the flow (CSS Contain 2 §2.5).
    /// </summary>
    public Boolean Layout { get; }

    /// <summary>
    /// Gets whether the box contains its paint, which is layout containment and a clip to its own box
    /// (§2.6).
    /// </summary>
    public Boolean Paint { get; }

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
        var layout = Has(contain!, "layout") || Has(contain!, "strict") || Has(contain!, "content");
        var paint = Has(contain!, "paint") || Has(contain!, "strict") || Has(contain!, "content");

        // The shorthand states one size for both axes or one for each; it is not expanded into the
        // longhands by the parser, so it is read whole and taken apart here.
        var stated = ReadStated(find("contain-intrinsic-size"), dimensions, font);

        return new ContainStyle(
            size || Has(contain!, "inline-size"),
            size || Has(contain!, "block-size"),
            stated.Count > 0 ? stated[0] : LengthOf(find("contain-intrinsic-width"), dimensions, font),
            stated.Count > 1 ? stated[1] : stated.Count > 0 ? stated[0] : LengthOf(find("contain-intrinsic-height"), dimensions, font),
            LengthOf(find("contain-intrinsic-inline-size"), dimensions, font),
            LengthOf(find("contain-intrinsic-block-size"), dimensions, font),
            // Any containment at all, not only the axes the layout contains: the canvas background asks the
            // whole question - §2.11.2's note says containment disables the body's special handling, whether
            // the containment is of paint, layout, style or size - and `none` is the only answer that says
            // nothing.
            !Has(contain!, "none"),
            layout,
            paint);
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

    /// <summary>
    /// Reads the intrinsic size a declaration states, whether the parser kept it as a value list or as
    /// the text the page wrote - <c>contain-intrinsic-size</c> is one AngleSharp has no grammar for, so
    /// its declarations arrive whole (CSS Sizing 4 §5.1).
    /// </summary>
    private static System.Collections.Generic.List<StyleLength> ReadStated(
        ICssProperty? declared,
        IRenderDimensions dimensions,
        LengthReader.FontSource font)
    {
        if (declared is null)
        {
            return new System.Collections.Generic.List<StyleLength>();
        }

        var lengths = Lengths(declared.RawValue, dimensions, font);

        if (lengths.Count > 0 || declared.Value is not { Length: > 0 } text)
        {
            return lengths;
        }

        foreach (var token in text.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (LengthReader.FromText(token, RenderMode.Horizontal, dimensions, font) is { } length &&
                length.HasValue &&
                length.Kind != StyleLengthKind.Percent)
            {
                lengths.Add(length);
            }
        }

        return lengths;
    }

    private static StyleLength? LengthOf(ICssProperty? declared, IRenderDimensions dimensions, LengthReader.FontSource font)
    {
        if (declared is null)
        {
            return null;
        }

        if (LengthReader.TryReadProperty(declared, RenderMode.Horizontal, dimensions, font, out var length) &&
            length.HasValue &&
            length.Kind != StyleLengthKind.Percent)
        {
            return length;
        }

        return declared.Value is { Length: > 0 } text
            ? LengthReader.FromText(text.Trim(), RenderMode.Horizontal, dimensions, font) is { HasValue: true } stated &&
              stated.Kind != StyleLengthKind.Percent
                ? stated
                : null
            : null;
    }
}

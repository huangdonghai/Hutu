using System;
using System.Collections.Generic;
using AngleSharp.Css;
using AngleSharp.Css.Dom;
using AngleSharp.Css.Values;

namespace Hutu.Rendering;

/// <summary>
/// Reads a CSS length without resolving its percentage, so that the layout pass can resolve it
/// against the containing block instead.
/// </summary>
/// <remarks>
/// The computed declaration cannot be used on its own. <c>CssLengthValue.Compute</c> folds every
/// non-pixel unit - percentages included - into pixels while the style is computed, and it does so
/// against the render device, whose width is the viewport. That is the whole reason
/// <c>width: 100%</c> used to mean "one window" rather than "one container". The cascaded
/// declaration still holds the percentage, so it is asked first and the computed one only serves as
/// a fallback for properties that were never declared - and those can only hold an initial value,
/// which is never a percentage.
/// <para>
/// The same device also stands in for the font size, so a length in <c>em</c> would be resolved
/// against the root font size rather than the element's own. <see cref="ToPixel"/> takes the font
/// size the length is really relative to, and every read here is handed the element's.
/// </para>
/// </remarks>
internal static class LengthReader
{
    /// <summary>
    /// Supplies the font size a font-relative length resolves against, but only if a length turns out
    /// to need one.
    /// </summary>
    /// <remarks>
    /// Most lengths are pixels or percentages and never consult it, yet every caller used to resolve
    /// the size up front because this reader's signature demanded a number - and resolving it walks
    /// the declarations again, at several times the cost of the read that wanted it. Measured on the
    /// sample's index page, that was one wasted resolution per length read, 5,340 of them in a pass.
    /// <para>
    /// A plain number stands for a size the caller already has: <c>font-size</c> itself is read
    /// against the parent's, so that caller must not be handed the element's own.
    /// </para>
    /// </remarks>
    internal readonly struct FontSource
    {
        private readonly ComputedStyle _style;
        private readonly Double _size;
        private readonly Boolean _fromStyle;

        public FontSource(ComputedStyle style)
        {
            _style = style;
            _size = 0;
            _fromStyle = true;
        }

        public FontSource(Double size)
        {
            _style = default;
            _size = size;
            _fromStyle = false;
        }

        /// <summary>Lets a caller that already has the number go on passing it.</summary>
        public static implicit operator FontSource(Double size) => new(size);

        /// <summary>Gets the size, asking the style for it only now. Zero means unknown.</summary>
        public Double Value => _fromStyle ? _style.FontSize : _size;
    }

    public static Boolean TryRead(
        ICssStyleDeclaration? specified,
        ICssStyleDeclaration? computed,
        String name,
        RenderMode mode,
        IRenderDimensions dimensions,
        FontSource font,
        out StyleLength length)
    {
        return TryRead(specified, name, mode, dimensions, font, out length) ||
               TryRead(computed, name, mode, dimensions, font, out length);
    }

    /// <summary>
    /// Reads a length for a style, asking for its font size only if the length needs one. This is the
    /// entry point the box model uses, where most reads never touch the font at all.
    /// </summary>
    public static Boolean TryRead(ComputedStyle style, String name, RenderMode mode, out StyleLength length) =>
        TryRead(style.Specified, style.Declaration, name, mode, style.Dimensions, new FontSource(style), out length);

    public static Boolean TryRead(
        ICssStyleDeclaration? declaration,
        String name,
        RenderMode mode,
        IRenderDimensions dimensions,
        FontSource font,
        out StyleLength length)
    {
        length = default;

        if (declaration is null)
        {
            return false;
        }

        if (CssMathLengths.Evaluate(declaration.TryGetValue(name), text => FromText(text, mode, dimensions, font)) is { } math)
        {
            length = math;
            return true;
        }

        if (TryReadValue(declaration.TryGetProperty(name)?.RawValue, mode, dimensions, font, out length))
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// Reads a value that has already been fetched from a declaration. Track lists, <c>minmax</c> and
    /// the items of a shorthand tuple all arrive as loose values rather than as named properties.
    /// </summary>
    /// <summary>The same, for a value already fetched from a style the caller holds.</summary>
    public static Boolean TryReadValue(ICssValue? value, RenderMode mode, ComputedStyle style, out StyleLength length) =>
        TryReadValue(value, mode, style.Dimensions, new FontSource(style), out length);

    public static Boolean TryReadValue(ICssValue? value, RenderMode mode, IRenderDimensions dimensions, FontSource font, out StyleLength length)
    {
        length = default;

        switch (value)
        {
            case CssCalcValue calc:
                // calc() expressions are evaluated by AngleSharp's own Compute pipeline, which
                // folds every operand to pixels against the device and then combines them. The
                // result is a plain pixel length - percentages inside calc() are resolved against
                // the viewport at this point, which is what the old render-tree path did.
                if (TryFoldCalc(calc, mode, dimensions, font, out length))
                {
                    return true;
                }

                return false;

            case CssLengthValue css:
                // `auto`, `normal` and `content` share the length type but carry no number, and the
                // only way to tell them apart is the text they serialise to.
                if (Double.IsNaN(css.Value) || Double.IsInfinity(css.Value))
                {
                    length = css.CssText.Trim().Equals("auto", StringComparison.OrdinalIgnoreCase)
                        ? StyleLength.Auto
                        : StyleLength.None;
                    return true;
                }

                if (FromCssLength(css, mode, dimensions, font) is not { } resolved)
                {
                    return false;
                }

                length = resolved;
                return true;

            case CssPercentageValue percentage when !Double.IsNaN(percentage.Value):
                length = StyleLength.FromPercent(percentage.Value / 100.0);
                return true;

            case CssConstantValue<CssLengthValue> constant when Double.IsNaN(constant.Value.Value):
                length = constant.Value.CssText.Trim().Equals("auto", StringComparison.OrdinalIgnoreCase)
                    ? StyleLength.Auto
                    : StyleLength.None;
                return true;

            // A keyword standing alone where a length is read: only `auto` and `none` serialise
            // to exactly themselves, so either one is itself. This catches the constant shapes
            // (a selected enum rather than a length) that the cases above do not name - `margin:
            // auto` arrives as one, where the computed declaration the old path falls back to has
            // already normalised it into the length above.
            case ICssValue keyword when keyword.CssText.Trim().Equals("auto", StringComparison.OrdinalIgnoreCase):
                length = StyleLength.Auto;
                return true;

            case ICssValue keyword when keyword.CssText.Trim().Equals("none", StringComparison.OrdinalIgnoreCase):
                length = StyleLength.None;
                return true;

            default:
                return false;
        }
    }

    /// <summary>
    /// Reads a corner radius: the pair of axes a <c>border-*-radius</c> declaration carries, with any
    /// percentage left to the layout pass.
    /// </summary>
    /// <remarks>
    /// A radius holds two values - the horizontal axis first, the vertical one second - and a single
    /// value stands for both, which is what makes <c>border-radius: 20px</c> a circle rather than a
    /// flat line. The value arrives as an instance of the generic <c>CssRadiusValue</c>, so it has to
    /// be taken apart through <see cref="ICssMultipleValue"/>.
    /// </remarks>
    public static StyleCornerRadius? TryReadCorner(
        ICssStyleDeclaration? specified,
        ICssStyleDeclaration? computed,
        String name,
        IRenderDimensions dimensions,
        FontSource font)
    {
        return TryReadCorner(specified, name, dimensions, font) ?? TryReadCorner(computed, name, dimensions, font);
    }

    private static StyleCornerRadius? TryReadCorner(ICssStyleDeclaration? declaration, String name, IRenderDimensions dimensions, FontSource font)
    {
        if (declaration?.TryGetProperty(name)?.RawValue is not ICssMultipleValue radius || radius.Count == 0)
        {
            return null;
        }

        var horizontal = TryReadAxis(radius[0], RenderMode.Horizontal, dimensions, font);
        var vertical = radius.Count > 1 ? TryReadAxis(radius[1], RenderMode.Vertical, dimensions, font) ?? horizontal : horizontal;

        return new StyleCornerRadius(horizontal ?? StyleLength.Zero, vertical ?? StyleLength.Zero);
    }

    private static StyleLength? TryReadAxis(ICssValue? value, RenderMode mode, IRenderDimensions dimensions, FontSource font) =>
        value is not null && TryReadValue(value, mode, dimensions, font, out var length) && length.HasValue ? length : null;

    /// <summary>
    /// Reads a corner radius from a loose value rather than a named property: the horizontal
    /// axis first, the vertical second, a single value standing for both. The fused
    /// box-builder path reads matched declarations this way.
    /// </summary>
    internal static StyleCornerRadius? TryReadCornerValue(ICssValue? value, IRenderDimensions dimensions, FontSource font)
    {
        if (value is not ICssMultipleValue radius || radius.Count == 0)
        {
            return null;
        }

        var horizontal = TryReadAxis(radius[0], RenderMode.Horizontal, dimensions, font);
        var vertical = radius.Count > 1 ? TryReadAxis(radius[1], RenderMode.Vertical, dimensions, font) ?? horizontal : horizontal;

        return new StyleCornerRadius(horizontal ?? StyleLength.Zero, vertical ?? StyleLength.Zero);
    }

    /// <summary>
    /// Gets the individual values of a shorthand that carries more than one, or null when the value is
    /// a single one. AngleSharp declares a non-generic <c>CssTupleValue</c> deriving from the generic
    /// one, but the values a shorthand actually produces are instantiations of the generic type;
    /// matching that class therefore silently fails, and only the interface catches them all.
    /// </summary>
    public static IReadOnlyList<ICssValue>? Items(ICssValue? value)
    {
        if (value is not ICssMultipleValue multiple || multiple.Count == 0)
        {
            return null;
        }

        var items = new ICssValue[multiple.Count];

        for (var index = 0; index < items.Length; index++)
        {
            items[index] = multiple[index];
        }

        return items;
    }

    /// <summary>
    /// Converts a length to pixels, resolving the font-relative units the way CSS asks: <c>em</c> is
    /// the font size passed in - the element's own, or its parent's when the length being read is
    /// <c>font-size</c> itself - and <c>ex</c> and <c>ch</c> the half of it this engine uses for both,
    /// because the x-height and the width of a zero are font metrics the render device does not
    /// expose. <c>rem</c> and every other unit are left to AngleSharp, which resolves the former
    /// against the device - the root - as it should.
    /// </summary>
    /// <remarks>
    /// The reference is resolved inside the two arms that need it rather than ahead of the switch, so
    /// a pixel length never asks the style for a font size it has no use for. That is the whole point
    /// of <see cref="FontSource"/> being handed around instead of a number.
    /// </remarks>
    public static Double ToPixel(CssLengthValue value, RenderMode mode, IRenderDimensions dimensions, FontSource font) =>
        value.Type switch
        {
            CssLengthValue.Unit.Em => value.Value * Reference(font, dimensions),
            CssLengthValue.Unit.Ex or CssLengthValue.Unit.Ch => value.Value * Reference(font, dimensions) * 0.5,
            _ => value.ToPixel(dimensions, mode),
        };

    private static Double Reference(FontSource font, IRenderDimensions dimensions)
    {
        var size = font.Value;

        return size > 0 ? size : dimensions.FontSize;
    }

    /// <summary>
    /// Reads a length that arrives as a loose value rather than a named property: math
    /// functions first, then the value itself. The fused box-builder path reads matched
    /// declarations this way, without the intermediate declaration the box model reads.
    /// </summary>
    internal static Boolean TryReadProperty(
        ICssProperty property,
        RenderMode mode,
        IRenderDimensions dimensions,
        FontSource font,
        out StyleLength length)
    {
        length = default;

        if (CssMathLengths.Evaluate(property.Value, text => FromText(text, mode, dimensions, font)) is { } math)
        {
            length = math;
            return true;
        }

        return TryReadValue(property.RawValue, mode, dimensions, font, out length);
    }

    /// <summary>Reads a single argument of a length function, which is always plain text.</summary>
    private static StyleLength? FromText(String text, RenderMode mode, IRenderDimensions dimensions, FontSource font)
    {
        var trimmed = text.Trim();

        if (trimmed.Length == 0)
        {
            return null;
        }

        if (trimmed.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            return StyleLength.Auto;
        }

        if (trimmed.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            return StyleLength.None;
        }

        if (!CssLengthValue.TryParse(trimmed, out var parsed) || Double.IsNaN(parsed.Value) || Double.IsInfinity(parsed.Value))
        {
            return null;
        }

        return FromCssLength(parsed, mode, dimensions, font);
    }

    /// <summary>
    /// Evaluates a <c>calc()</c> expression to a pixel length by running AngleSharp's own
    /// compute pipeline over it. The compute context supplies the device dimensions so that
    /// percentages and font-relative units resolve the way the render tree used to.
    /// </summary>
    private static Boolean TryFoldCalc(CssCalcValue calc, RenderMode mode, IRenderDimensions dimensions, FontSource font, out StyleLength length)
    {
        length = default;

        var context = new CalcComputeContext(dimensions);
        var computed = ((ICssValue)calc).Compute(context);

        if (computed is CssLengthValue result && result.Type != CssLengthValue.Unit.None &&
            !Double.IsNaN(result.Value) && !Double.IsInfinity(result.Value))
        {
            var pixels = result.Type == CssLengthValue.Unit.Percent
                ? result.Value / 100.0 * (mode == RenderMode.Horizontal ? dimensions.RenderWidth : dimensions.RenderHeight)
                : ToPixel(result, mode, dimensions, font);

            if (!Double.IsNaN(pixels) && !Double.IsInfinity(pixels))
            {
                length = StyleLength.FromPixels(pixels);
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Folds a <c>calc()</c> to its computed number and unit against a caller-supplied basis, rather than
    /// the viewport and root. Line height is that caller: there a percentage and a font-relative unit are
    /// multiples of the element's own font size, so the whole expression is folded against <paramref name="basis"/>.
    /// A plain number comes back as such (<see cref="CssLengthValue.Unit.None"/>) - it is a multiplier, not
    /// a length, and has nothing to convert.
    /// </summary>
    internal static Boolean TryFoldCalcRaw(CssCalcValue calc, Double basis, out CssLengthValue.Unit unit, out Double value)
    {
        var context = new CalcComputeContext(new FontBasis(basis));

        if (((ICssValue)calc).Compute(context) is CssLengthValue result &&
            !Double.IsNaN(result.Value) && !Double.IsInfinity(result.Value))
        {
            unit = result.Type;
            value = result.Value;
            return true;
        }

        unit = default;
        value = 0;
        return false;
    }

    /// <summary>
    /// A render box that is the caller's font size on every side, so a calc that resolves percentages
    /// and font-relative units does so against that size rather than against the viewport or the root.
    /// Only the font-relative and percentage fold reads it, which is exactly what a line height wants.
    /// </summary>
    private sealed class FontBasis : IRenderDimensions
    {
        public FontBasis(Double basis) => RenderWidth = RenderHeight = FontSize = basis;

        public Double RenderWidth { get; }

        public Double RenderHeight { get; }

        public Double FontSize { get; }
    }

    /// <summary>
    /// Folds a <c>calc()</c> to a number of pixels, for a caller that wants the answer rather than a
    /// length to hand down. Reading the expression as a plain length gives zero - it is a math value
    /// that only becomes a number once its operands have been resolved - so a caller that skipped the
    /// fold sized its box at nothing.
    /// </summary>
    internal static Double? FoldCalc(CssCalcValue calc, RenderMode mode, IRenderDimensions dimensions, FontSource font) =>
        TryFoldCalc(calc, mode, dimensions, font, out var length) ? length.Value : null;

    private static StyleLength? FromCssLength(CssLengthValue value, RenderMode mode, IRenderDimensions dimensions, FontSource font)
    {
        if (value.Type == CssLengthValue.Unit.Percent)
        {
            return StyleLength.FromPercent(value.Value / 100.0);
        }

        try
        {
            var pixels = ToPixel(value, mode, dimensions, font);
            return Double.IsNaN(pixels) || Double.IsInfinity(pixels) ? null : StyleLength.FromPixels(pixels);
        }
        catch (InvalidOperationException)
        {
            // A unitless non-zero number, and anything else the device cannot convert. Both are
            // invalid where a length is expected, so this is a downgrade rather than a failure.
            return null;
        }
    }
}

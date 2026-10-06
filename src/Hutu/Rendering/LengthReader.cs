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
    public static Boolean TryRead(
        ICssStyleDeclaration? specified,
        ICssStyleDeclaration? computed,
        String name,
        RenderMode mode,
        IRenderDimensions dimensions,
        Double fontSize,
        out StyleLength length)
    {
        return TryRead(specified, name, mode, dimensions, fontSize, out length) ||
               TryRead(computed, name, mode, dimensions, fontSize, out length);
    }

    public static Boolean TryRead(
        ICssStyleDeclaration? declaration,
        String name,
        RenderMode mode,
        IRenderDimensions dimensions,
        Double fontSize,
        out StyleLength length)
    {
        length = default;

        if (declaration is null)
        {
            return false;
        }

        if (CssMathLengths.Evaluate(declaration.GetPropertyValue(name), text => FromText(text, mode, dimensions, fontSize)) is { } math)
        {
            length = math;
            return true;
        }

        return TryReadValue(declaration.GetProperty(name)?.RawValue, mode, dimensions, fontSize, out length);
    }

    /// <summary>
    /// Reads a value that has already been fetched from a declaration. Track lists, <c>minmax</c> and
    /// the items of a shorthand tuple all arrive as loose values rather than as named properties.
    /// </summary>
    public static Boolean TryReadValue(ICssValue? value, RenderMode mode, IRenderDimensions dimensions, Double fontSize, out StyleLength length)
    {
        length = default;

        switch (value)
        {
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

                if (FromCssLength(css, mode, dimensions, fontSize) is not { } resolved)
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
        Double fontSize)
    {
        return TryReadCorner(specified, name, dimensions, fontSize) ?? TryReadCorner(computed, name, dimensions, fontSize);
    }

    private static StyleCornerRadius? TryReadCorner(ICssStyleDeclaration? declaration, String name, IRenderDimensions dimensions, Double fontSize)
    {
        if (declaration?.GetProperty(name)?.RawValue is not ICssMultipleValue radius || radius.Count == 0)
        {
            return null;
        }

        var horizontal = TryReadAxis(radius[0], RenderMode.Horizontal, dimensions, fontSize);
        var vertical = radius.Count > 1 ? TryReadAxis(radius[1], RenderMode.Vertical, dimensions, fontSize) ?? horizontal : horizontal;

        return new StyleCornerRadius(horizontal ?? StyleLength.Zero, vertical ?? StyleLength.Zero);
    }

    private static StyleLength? TryReadAxis(ICssValue? value, RenderMode mode, IRenderDimensions dimensions, Double fontSize) =>
        value is not null && TryReadValue(value, mode, dimensions, fontSize, out var length) && length.HasValue ? length : null;

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
    public static Double ToPixel(CssLengthValue value, RenderMode mode, IRenderDimensions dimensions, Double fontSize)
    {
        var reference = fontSize > 0 ? fontSize : dimensions.FontSize;

        return value.Type switch
        {
            CssLengthValue.Unit.Em => value.Value * reference,
            CssLengthValue.Unit.Ex or CssLengthValue.Unit.Ch => value.Value * reference * 0.5,
            _ => value.ToPixel(dimensions, mode),
        };
    }

    /// <summary>Reads a single argument of a length function, which is always plain text.</summary>
    private static StyleLength? FromText(String text, RenderMode mode, IRenderDimensions dimensions, Double fontSize)
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

        return FromCssLength(parsed, mode, dimensions, fontSize);
    }

    private static StyleLength? FromCssLength(CssLengthValue value, RenderMode mode, IRenderDimensions dimensions, Double fontSize)
    {
        if (value.Type == CssLengthValue.Unit.Percent)
        {
            return StyleLength.FromPercent(value.Value / 100.0);
        }

        try
        {
            var pixels = ToPixel(value, mode, dimensions, fontSize);
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

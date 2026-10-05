using AngleSharp.Css;
using AngleSharp.Css.Dom;
using AngleSharp.Css.Values;
using AngleSharp.Dom;
using Hutu.Rendering.Media;
using System;
using System.Collections.Generic;
using System.Globalization;

namespace Hutu.Rendering;

/// <summary>
/// Strongly-typed access to the CSS values the layout engine needs.
/// </summary>
/// <remarks>
/// AngleSharp.Css exposes computed values either as strings
/// (<see cref="ICssProperties.GetPropertyValue"/>) or as untyped <see cref="ICssValue"/> instances
/// (<c>GetProperty(name).RawValue</c>). Relative units are resolved here, against the render
/// device, rather than by AngleSharp - which is why every length accessor names the axis the value
/// resolves against and why the device must carry a real viewport before layout runs.
/// <para>
/// This is a value type: it is created per box and must stay cheap to copy.
/// </para>
/// </remarks>
public readonly struct ComputedStyle
{
    private readonly ICssStyleDeclaration _style;
    private readonly ICssStyleDeclaration? _specified;
    private readonly IRenderDimensions _dimensions;
    private readonly IElement? _element;

    /// <param name="specified">
    /// The cascaded declaration, when it is available. It is preferred for backgrounds: the computed
    /// declaration loses the position, size and repeat of a layered background, because AngleSharp
    /// rebuilds the shorthand's longhands lossily.
    /// </param>
    /// <param name="element">
    /// The element the style belongs to. Needed to resolve the relative URLs of background images,
    /// which the image cache is keyed by.
    /// </param>
    public ComputedStyle(
        ICssStyleDeclaration style,
        IRenderDimensions dimensions,
        ICssStyleDeclaration? specified = null,
        IElement? element = null)
    {
        _style = style ?? throw new ArgumentNullException(nameof(style));
        _dimensions = dimensions ?? throw new ArgumentNullException(nameof(dimensions));
        _specified = specified;
        _element = element;
    }

    /// <summary>Gets the underlying declaration, for values this wrapper does not surface.</summary>
    public ICssStyleDeclaration Declaration => _style;

    // --- Box generation -------------------------------------------------

    public String Display => Keyword("display", "inline");

    public Boolean IsDisplayNone => Is("display", "none");

    public String WhiteSpace => Keyword("white-space", "normal");

    /// <summary>
    /// Gets whether runs of white space collapse to a single space, which is the case for every
    /// value except the <c>pre</c> family.
    /// </summary>
    public Boolean IsCollapsibleWhiteSpace =>
        WhiteSpace.Equals("normal", StringComparison.OrdinalIgnoreCase) ||
        WhiteSpace.Equals("nowrap", StringComparison.OrdinalIgnoreCase);

    public Boolean IsPreformatted =>
        WhiteSpace.StartsWith("pre", StringComparison.OrdinalIgnoreCase);

    // --- Text -----------------------------------------------------------

    public String FontFamily => Keyword("font-family", "sans-serif");

    public Double FontSize => Length("font-size", RenderMode.Vertical, _dimensions.FontSize);

    public Int32 FontWeight
    {
        get
        {
            var text = Keyword("font-weight", "400");

            if (text.Equals("bold", StringComparison.OrdinalIgnoreCase) ||
                text.Equals("bolder", StringComparison.OrdinalIgnoreCase))
            {
                return 700;
            }

            if (text.Equals("normal", StringComparison.OrdinalIgnoreCase) ||
                text.Equals("lighter", StringComparison.OrdinalIgnoreCase))
            {
                return 400;
            }

            return Int32.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var weight)
                ? weight
                : 400;
        }
    }

    public Boolean IsItalic =>
        Is("font-style", "italic") || Is("font-style", "oblique");

    /// <summary>
    /// Gets the line height in pixels, or null for <c>normal</c> (which defers to the font metrics).
    /// A unitless value is a factor of the font size.
    /// </summary>
    public Double? LineHeight
    {
        get
        {
            var text = _style.GetPropertyValue("line-height");

            if (String.IsNullOrWhiteSpace(text))
            {
                return null;
            }

            text = text.Trim();

            if (text.Equals("normal", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            if (Double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var factor))
            {
                return factor * FontSize;
            }

            return TryLength("line-height", RenderMode.Vertical, out var pixels) ? pixels : null;
        }
    }

    public String TextAlign => Keyword("text-align", "start");

    public String VerticalAlign => Keyword("vertical-align", "baseline");

    public CssColorValue Color => ColorValue("color") ?? CssColorValue.Black;

    public CssColorValue? BackgroundColor => ColorValue("background-color");

    /// <summary>
    /// Gets the background layers in declaration order. The first layer paints on top of the ones
    /// after it, so a painter has to emit them in reverse.
    /// </summary>
    public IReadOnlyList<BackgroundLayer> Backgrounds
    {
        get
        {
            var specified = ParseBackgrounds(_specified);

            return specified.Count > 0 ? specified : ParseBackgrounds(_style);
        }
    }

    private IReadOnlyList<BackgroundLayer> ParseBackgrounds(ICssStyleDeclaration? declaration)
    {
        if (declaration is null)
        {
            return Array.Empty<BackgroundLayer>();
        }

        // Copied into locals because a lambda inside a struct cannot capture `this`.
        var element = _element;

        return BackgroundLayerParser.Parse(
            declaration.GetPropertyValue("background-image"),
            declaration.GetPropertyValue("background-position"),
            declaration.GetPropertyValue("background-size"),
            declaration.GetPropertyValue("background-repeat"),
            _dimensions,
            element is null ? null : url => ImageUrlResolver.Resolve(element, url));
    }

    public Boolean HasUnderline => HasDecoration("underline");

    public Boolean HasLineThrough => HasDecoration("line-through");

    // --- Box model ------------------------------------------------------

    public Double? Width => NullableLength("width", RenderMode.Horizontal);

    public Double? Height => NullableLength("height", RenderMode.Vertical);

    public Double? MinWidth => NullableLength("min-width", RenderMode.Horizontal);

    public Double? MaxWidth => NullableLength("max-width", RenderMode.Horizontal);

    public Double? MinHeight => NullableLength("min-height", RenderMode.Vertical);

    public Double? MaxHeight => NullableLength("max-height", RenderMode.Vertical);

    /// <summary>
    /// Gets how replaced content (an image) is fitted into its box. Geometry is decided elsewhere;
    /// this only tells the painter how to fill what layout resolved.
    /// </summary>
    public Layout.ObjectFit ObjectFit
    {
        get
        {
            var value = Keyword("object-fit", "fill");

            return value.ToLowerInvariant() switch
            {
                "contain" => Layout.ObjectFit.Contain,
                "cover" => Layout.ObjectFit.Cover,
                "none" => Layout.ObjectFit.None,
                "scale-down" => Layout.ObjectFit.ScaleDown,
                _ => Layout.ObjectFit.Fill,
            };
        }
    }

    /// <summary>
    /// Gets the radius painted content is clipped to. Only one radius is modelled - the largest of
    /// the four corners - which is exactly what a uniform <c>border-radius</c> produces.
    /// </summary>
    public Double CornerRadius
    {
        get
        {
            var topLeft = Length("border-top-left-radius", RenderMode.Horizontal);
            var topRight = Length("border-top-right-radius", RenderMode.Horizontal);
            var bottomRight = Length("border-bottom-right-radius", RenderMode.Horizontal);
            var bottomLeft = Length("border-bottom-left-radius", RenderMode.Horizontal);

            return Math.Max(Math.Max(topLeft, topRight), Math.Max(bottomRight, bottomLeft));
        }
    }

    public Double MarginTop => Length("margin-top", RenderMode.Horizontal);

    public Double MarginRight => Length("margin-right", RenderMode.Horizontal);

    public Double MarginBottom => Length("margin-bottom", RenderMode.Horizontal);

    public Double MarginLeft => Length("margin-left", RenderMode.Horizontal);

    public Double PaddingTop => Length("padding-top", RenderMode.Horizontal);

    public Double PaddingRight => Length("padding-right", RenderMode.Horizontal);

    public Double PaddingBottom => Length("padding-bottom", RenderMode.Horizontal);

    public Double PaddingLeft => Length("padding-left", RenderMode.Horizontal);

    public Double BorderTopWidth => BorderWidth("border-top-width", "border-top-style");

    public Double BorderRightWidth => BorderWidth("border-right-width", "border-right-style");

    public Double BorderBottomWidth => BorderWidth("border-bottom-width", "border-bottom-style");

    public Double BorderLeftWidth => BorderWidth("border-left-width", "border-left-style");

    public CssColorValue? BorderTopColor => ResolveBorderColor(ColorValue("border-top-color"));

    public CssColorValue? BorderRightColor => ResolveBorderColor(ColorValue("border-right-color"));

    public CssColorValue? BorderBottomColor => ResolveBorderColor(ColorValue("border-bottom-color"));

    public CssColorValue? BorderLeftColor => ResolveBorderColor(ColorValue("border-left-color"));

    public Boolean BorderTopVisible => HasBorder("border-top-style");

    public Boolean BorderRightVisible => HasBorder("border-right-style");

    public Boolean BorderBottomVisible => HasBorder("border-bottom-style");

    public Boolean BorderLeftVisible => HasBorder("border-left-style");

    // --- Helpers --------------------------------------------------------

    /// <summary>
    /// Reads a length, or returns false when the property is absent or a keyword such as
    /// <c>auto</c>. Use this for the properties where "not set" differs from zero (width, height).
    /// </summary>
    public Boolean TryLength(String name, RenderMode mode, out Double pixels)
    {
        pixels = 0;

        // min()/max()/clamp() are resolved here: AngleSharp.Css models them as a math expression
        // rather than a length, so reading one as pixels yields zero and `width: min(100%, 640px)`
        // would collapse instead of picking an argument.
        var text = _style.GetPropertyValue(name);

        if (!String.IsNullOrWhiteSpace(text))
        {
            // The state is copied into locals because a lambda inside a struct cannot capture `this`.
            var dimensions = _dimensions;
            var math = CssMathLengths.Evaluate(
                text,
                argument => CssLengthValue.TryParse(argument.Trim(), out var length)
                    ? Resolve(length, mode, dimensions)
                    : null);

            if (math is not null)
            {
                pixels = math.Value;
                return true;
            }
        }

        var value = _style.GetProperty(name)?.RawValue;

        if (value is null || value.Is("auto") || value.Is("normal") || value.Is("none"))
        {
            return false;
        }

        var resolved = Resolve(value, mode);

        if (resolved is null)
        {
            return false;
        }

        pixels = resolved.Value;
        return true;
    }

    public Double Length(String name, RenderMode mode, Double fallback = 0) =>
        TryLength(name, mode, out var pixels) ? pixels : fallback;

    private Double? NullableLength(String name, RenderMode mode) =>
        TryLength(name, mode, out var pixels) ? pixels : null;

    private Double BorderWidth(String widthName, String styleName)
    {
        if (!HasBorder(styleName))
        {
            return 0;
        }

        return Length(widthName, RenderMode.Horizontal, 0);
    }

    private Boolean HasBorder(String styleName)
    {
        var style = Keyword(styleName, "none");
        return !style.Equals("none", StringComparison.OrdinalIgnoreCase) &&
               !style.Equals("hidden", StringComparison.OrdinalIgnoreCase);
    }

    private Boolean HasDecoration(String kind)
    {
        var text = _style.GetPropertyValue("text-decoration-line");

        if (String.IsNullOrWhiteSpace(text))
        {
            text = _style.GetPropertyValue("text-decoration");
        }

        return !String.IsNullOrWhiteSpace(text) &&
               text.Contains(kind, StringComparison.OrdinalIgnoreCase);
    }

    private CssColorValue? ColorValue(String name)
    {
        var value = _style.GetProperty(name)?.RawValue;

        return value is CssColorValue color ? color : null;
    }

    /// <summary>
    /// Resolves the <c>currentcolor</c> keyword, which is the initial value of every border colour.
    /// AngleSharp keeps the keyword in the computed declaration, so leaving it unresolved would
    /// paint the sentinel value - that is, an invisible border.
    /// </summary>
    private CssColorValue? ResolveBorderColor(CssColorValue? value)
    {
        if (value is not { } color)
        {
            return null;
        }

        return color == CssColorValue.CurrentColor ? Color : color;
    }

    private String Keyword(String name, String fallback)
    {
        var text = _style.GetPropertyValue(name);
        return String.IsNullOrWhiteSpace(text) ? fallback : text.Trim();
    }

    private Boolean Is(String name, String keyword)
    {
        var text = _style.GetPropertyValue(name);
        return text is not null && text.Trim().Equals(keyword, StringComparison.OrdinalIgnoreCase);
    }

    private Double? Resolve(ICssValue value, RenderMode mode)
    {
        try
        {
            var pixels = value.AsPx(_dimensions, mode);

            // "auto" and friends surface as NaN; treat those as "not a length".
            return Double.IsNaN(pixels) || Double.IsInfinity(pixels) ? null : pixels;
        }
        catch (Exception)
        {
            // A value that cannot be expressed in pixels (a keyword, a color, ...) is not a length.
            return null;
        }
    }

    private static Double? Resolve(CssLengthValue value, RenderMode mode, IRenderDimensions dimensions)
    {
        try
        {
            var pixels = value.ToPixel(dimensions, mode);
            return Double.IsNaN(pixels) || Double.IsInfinity(pixels) ? null : pixels;
        }
        catch (Exception)
        {
            return null;
        }
    }
}

using AngleSharp.Css;
using AngleSharp.Css.Dom;
using AngleSharp.Css.Values;
using AngleSharp.Dom;
using Hutu.Rendering.Layout;
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
/// (<c>GetProperty(name).RawValue</c>). Font-relative units are resolved here, against the render
/// device, which is why those accessors name the axis the value resolves against and why the device
/// must carry a real viewport before layout runs.
/// <para>
/// The box model is the exception: <c>width</c>, <c>height</c>, their min and max constraints and
/// the margin and padding insets come back as <see cref="StyleLength"/> and
/// <see cref="StyleThickness"/>, with their percentages still unresolved, because a percentage is
/// relative to the containing block and only the layout pass knows that.
/// </para>
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

    /// <summary>
    /// Gets which box a declared width and height describe.
    /// </summary>
    public BoxSizing BoxSizing
    {
        get
        {
            // The type is named for the Css module because the layout namespace has a BoxModel too.
            if (_style.GetProperty("box-sizing")?.RawValue is CssConstantValue<AngleSharp.Css.Dom.BoxModel> constant &&
                constant.Value == AngleSharp.Css.Dom.BoxModel.BorderBox)
            {
                return BoxSizing.BorderBox;
            }

            return _style.GetPropertyValue("box-sizing").Trim()
                .Equals("border-box", StringComparison.OrdinalIgnoreCase)
                    ? BoxSizing.BorderBox
                    : BoxSizing.ContentBox;
        }
    }

    /// <summary>
    /// Gets the flex longhands, both the container's and the item's. They are read together because
    /// the same declaration serves both roles depending on the parent.
    /// </summary>
    public FlexStyle Flex => FlexStyle.Read(this);

    /// <summary>
    /// Gets the grid longhands: the track lists, the automatic track sizes, the automatic flow, the
    /// container's alignment and the gaps. Items also read their placement and their own alignment
    /// through this, because the same declarations serve both roles.
    /// </summary>
    /// <remarks>
    /// Internal, unlike the flex and table readers, because the grid value model - tracks, breadths
    /// and lines - is a richer shape than the other contexts need and there is no reason to publish
    /// it. The layout engine is the only consumer.
    /// </remarks>
    internal GridStyle Grid => GridStyle.Read(this);

    /// <summary>
    /// Gets the table longhands. <c>border-collapse</c>, <c>border-spacing</c> and friends only
    /// matter on a table box; <c>vertical-align</c> is meaningful on a cell.
    /// </summary>
    public TableStyle Table => TableStyle.Read(this);

    /// <summary>
    /// Gets the cascaded declaration. It is the one that still carries a percentage, because
    /// computing the style folds a percentage into pixels against the render device - the viewport -
    /// and the layout engine has to resolve it against the containing block instead.
    /// </summary>
    internal ICssStyleDeclaration? Specified => _specified;

    internal IRenderDimensions Dimensions => _dimensions;

    public StyleLength Width => ReadLength("width", RenderMode.Horizontal, StyleLength.Auto);

    public StyleLength Height => ReadLength("height", RenderMode.Vertical, StyleLength.Auto);

    public StyleLength MinWidth => ReadLength("min-width", RenderMode.Horizontal, StyleLength.None);

    public StyleLength MaxWidth => ReadLength("max-width", RenderMode.Horizontal, StyleLength.None);

    public StyleLength MinHeight => ReadLength("min-height", RenderMode.Vertical, StyleLength.None);

    public StyleLength MaxHeight => ReadLength("max-height", RenderMode.Vertical, StyleLength.None);

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

    /// <summary>
    /// Gets the four margins, with their units intact: <c>auto</c> survives, which is what makes
    /// <c>margin: 0 auto</c> able to centre a box. A percentage margin resolves against the
    /// containing block's width on all four sides.
    /// </summary>
    public StyleThickness Margin => ReadThickness("margin", StyleLength.Zero);

    /// <summary>Gets the four paddings. As with margins, every side resolves against the width.</summary>
    public StyleThickness Padding => ReadThickness("padding", StyleLength.Zero);

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

    /// <summary>
    /// Gets the four border style keywords. The collapsing border model breaks a tie between two
    /// borders of equal width by their styles, so the keyword itself has to survive, not just the
    /// question of whether the border is drawn.
    /// </summary>
    public String BorderTopStyle => Keyword("border-top-style", "none");

    public String BorderRightStyle => Keyword("border-right-style", "none");

    public String BorderBottomStyle => Keyword("border-bottom-style", "none");

    public String BorderLeftStyle => Keyword("border-left-style", "none");

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

    /// <summary>
    /// Reads a box-model length, deferring its percentage to the layout pass. The cascaded
    /// declaration is asked first because computing the style folds percentages into pixels.
    /// </summary>
    private StyleLength ReadLength(String name, RenderMode mode, StyleLength fallback) =>
        LengthReader.TryRead(_specified, _style, name, mode, _dimensions, out var length) ? length : fallback;

    private StyleThickness ReadThickness(String prefix, StyleLength fallback) => new(
        ReadLength(prefix + "-top", RenderMode.Horizontal, fallback),
        ReadLength(prefix + "-right", RenderMode.Horizontal, fallback),
        ReadLength(prefix + "-bottom", RenderMode.Horizontal, fallback),
        ReadLength(prefix + "-left", RenderMode.Horizontal, fallback));

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

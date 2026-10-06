using AngleSharp.Css;
using AngleSharp.Css.Dom;
using AngleSharp.Css.Values;
using AngleSharp.Dom;
using AngleSharp.Html.Dom;
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
    private readonly Double _parentFontSize;
    private readonly CssColorValue? _parentColor;
    private readonly Boolean _declaresFontSize;

    /// <param name="specified">
    /// The cascaded declaration, when it is available. It is preferred for backgrounds: the computed
    /// declaration loses the position, size and repeat of a layered background, because AngleSharp
    /// rebuilds the shorthand's longhands lossily.
    /// </param>
    /// <param name="element">
    /// The element the style belongs to. Needed to resolve the relative URLs of background images,
    /// which the image cache is keyed by.
    /// </param>
    /// <param name="parentFontSize">
    /// The font size the parent box resolved to. A percentage <c>font-size</c> is relative to it, and
    /// since AngleSharp computes that percentage against the render device - the viewport - instead, the
    /// box builder hands the real reference down and it is applied here. Zero means it is unknown and
    /// the device's own font size stands in.
    /// </param>
    /// <param name="parentColor">
    /// The colour the parent box resolved to, which is not always the colour the parent's computed
    /// declaration holds: a link is painted in the engine's own link colour without that colour ever
    /// reaching the declaration, so the words inside the link take it from here.
    /// </param>
    /// <param name="declaresFontSize">
    /// Whether the element declares its own <c>font-size</c> rather than taking one over from a
    /// parent. Only a declared relative size is measured against the parent; working that out means a
    /// second cascade, so the box builder settles it once and the answer is carried here.
    /// </param>
    public ComputedStyle(
        ICssStyleDeclaration style,
        IRenderDimensions dimensions,
        ICssStyleDeclaration? specified = null,
        IElement? element = null,
        Double parentFontSize = 0,
        CssColorValue? parentColor = null,
        Boolean declaresFontSize = false)
    {
        _style = style ?? throw new ArgumentNullException(nameof(style));
        _dimensions = dimensions ?? throw new ArgumentNullException(nameof(dimensions));
        _specified = specified;
        _element = element;
        _parentFontSize = parentFontSize;
        _parentColor = parentColor;
        _declaresFontSize = declaresFontSize;
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

    /// <summary>
    /// Gets the font size in pixels. A percentage is the one length that resolves against the parent's
    /// font size rather than the containing block, and the cascade folds it into pixels against the
    /// render device - the viewport - so a declared percentage is read back here and scaled by the size
    /// the box builder passed down.
    /// </summary>
    public Double FontSize
    {
        get
        {
            // A relative font-size is relative to the parent, and the cascade resolves it against the
            // device - the root font size - instead. It is resolved here, because the element's own
            // font size is the reference every other length of this style resolves its em against.
            if (_specified?.GetProperty("font-size")?.RawValue is CssLengthValue declared && IsFontRelative(declared.Type))
            {
                // An inherited value is not the element's own declaration; the parent already
                // resolved it, and taking that size over as it is stops nested em font sizes from
                // stacking. Only a value the element declares is measured against the parent.
                if (_declaresFontSize)
                {
                    return ResolveFontRelative(declared);
                }

                return _parentFontSize > 0 ? _parentFontSize : _dimensions.FontSize;
            }

            return TryLengthAgainst("font-size", RenderMode.Vertical, _dimensions.FontSize, out var pixels)
                ? pixels
                : _dimensions.FontSize;
        }
    }

    /// <summary>Gets whether a length is relative to the font size, which the cascade cannot resolve.</summary>
    internal static Boolean IsFontRelative(CssLengthValue.Unit unit) =>
        unit is CssLengthValue.Unit.Em or CssLengthValue.Unit.Ex or CssLengthValue.Unit.Ch or CssLengthValue.Unit.Percent;

    /// <summary>
    /// Resolves a relative <c>font-size</c> against the parent's font size. A percentage is a share of
    /// it; an <c>em</c> is a multiple of it; <c>ex</c> and <c>ch</c> keep the half-em approximation the
    /// rest of the engine uses for them.
    /// </summary>
    private Double ResolveFontRelative(CssLengthValue value)
    {
        var reference = _parentFontSize > 0 ? _parentFontSize : _dimensions.FontSize;

        return value.Type switch
        {
            CssLengthValue.Unit.Em => value.Value * reference,
            CssLengthValue.Unit.Ex or CssLengthValue.Unit.Ch => value.Value * reference * 0.5,
            _ => value.Value * 0.01 * reference,
        };
    }

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

    /// <summary>
    /// Gets the extra space added to every character's advance, in pixels. <c>normal</c>, the initial
    /// value, means none; a length is resolved against the element's own font size, so <c>.16em</c> is
    /// that share of the size the text is set at. The space follows the last character too, as CSS
    /// asks, which is why a run is that much wider than its glyphs.
    /// </summary>
    public Double LetterSpacing => Length("letter-spacing", RenderMode.Horizontal, 0);

    public String VerticalAlign => Keyword("vertical-align", "baseline");

    /// <summary>
    /// Gets the writing mode the element runs its text in. <c>writing-mode</c> is a registered,
    /// inherited property, so its computed value already carries what an ancestor asked for and this
    /// answers with the mode the element itself settles on.
    /// </summary>
    public WritingMode WritingMode => ReadKeyword("writing-mode")?.ToLowerInvariant() switch
    {
        "vertical-rl" => Layout.WritingMode.VerticalRl,
        "vertical-lr" => Layout.WritingMode.VerticalLr,
        "sideways-rl" => Layout.WritingMode.SidewaysRl,
        "sideways-lr" => Layout.WritingMode.SidewaysLr,
        _ => Layout.WritingMode.HorizontalTb,
    };

    /// <summary>
    /// Gets the orientation the element runs its characters in. <c>text-orientation</c> is inherited,
    /// so the cascade already carries what an ancestor asked for; the box builder still walks it down
    /// the tree because an inline box may set it apart from the block it sits in.
    /// </summary>
    public TextOrientation? DeclaredTextOrientation => ReadKeyword("text-orientation")?.ToLowerInvariant() switch
    {
        "mixed" => TextOrientation.Mixed,
        "upright" => TextOrientation.Upright,
        "sideways" => TextOrientation.Sideways,
        _ => null,
    };

    /// <summary>
    /// Gets whether short runs stand as one upright box. Like <c>text-orientation</c> it is inherited,
    /// and null means the declaration said something this reader does not know.
    /// </summary>
    public Boolean? DeclaredTextCombineUpright => ReadKeyword("text-combine-upright")?.ToLowerInvariant() switch
    {
        "all" => true,
        "none" => false,
        _ => null,
    };

    /// <summary>
    /// Gets the writing mode, text orientation and upright combining the element itself declares, with
    /// the values it inherits left out. The box builder folds in what the ancestors run in.
    /// </summary>
    public WritingModeInfo Flow
    {
        get
        {
            var mode = WritingMode;

            if (mode == Layout.WritingMode.HorizontalTb)
            {
                return WritingModeInfo.Horizontal;
            }

            return new WritingModeInfo(
                mode,
                DeclaredTextOrientation ?? TextOrientation.Mixed,
                DeclaredTextCombineUpright ?? false);
        }
    }

    /// <summary>
    /// Gets where the annotation of a <c>ruby</c> element is set relative to its base text. Only
    /// <c>over</c> - the initial value - is laid out; the other two are read so that a page asking for
    /// them is not silently mistaken for one asking for <c>over</c>.
    /// </summary>
    public RubyPosition RubyPosition =>
        ReadKeyword("ruby-position")?.ToLowerInvariant() switch
        {
            "under" => RubyPosition.Under,
            "inter-character" => RubyPosition.InterCharacter,
            _ => RubyPosition.Over,
        };

    /// <summary>
    /// Gets whether the annotation is set after its base along the block axis rather than before it,
    /// which is the one distinction the layout of a pair has to make.
    /// </summary>
    public Boolean HasRubyUnder => RubyPosition == RubyPosition.Under;

    /// <summary>
    /// Gets the colour text is painted in.
    /// </summary>
    /// <remarks>
    /// A link the page never coloured is painted in the engine's own link colour: blue, purple once it
    /// has been read, red while it is being followed. That default cannot be left to a rule in the
    /// engine's sheet. Naming a link takes a pseudo-class, a pseudo-class outranks the plain element
    /// selector an author writes, and this engine cascades its own sheet in the same origin as the
    /// page's - so the default would beat the page. It is resolved here instead, where the declaration
    /// the page wrote on the element is still told apart from the one the element merely took over from
    /// its parent: a link that inherits the page's text colour is still drawn as a link.
    /// </remarks>
    public CssColorValue Color
    {
        get
        {
            var declared = ColorValue("color") ?? CssColorValue.Black;

            if (IsFollowable)
            {
                return Declares("color") ? declared : LinkColor;
            }

            // Anything but a link is drawn in what was declared for it. Only an element no one declared
            // a colour for falls back to the colour its parent resolved to, and that is how the words
            // inside a link take its colour. The declared value is asked for its value rather than for
            // its presence: the cascaded declaration answers with a placeholder of its own invention
            // when it holds nothing, so a null property would never be seen.
            return _specified?.GetProperty("color")?.RawValue is null ? _parentColor ?? declared : declared;
        }
    }

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
            FontSize,
            element is null ? null : url => ImageUrlResolver.Resolve(element, url));
    }

    public Boolean HasUnderline => HasDecoration("underline");

    public Boolean HasLineThrough => HasDecoration("line-through");

    // --- Pointer --------------------------------------------------------

    /// <summary>
    /// Gets the pointer the element asks for, i.e. what <c>cursor</c> resolves to.
    /// </summary>
    /// <remarks>
    /// A list of values is read from its last entry, which is the one that stands in when the shapes
    /// before it cannot be used - this engine draws none of them, so the keyword at the end is what
    /// is left. An element that says nothing asks for <see cref="CursorKind.Auto"/>, which leaves the
    /// choice to whatever the pointer is over.
    /// </remarks>
    public CursorKind Cursor
    {
        get
        {
            var value = ReadKeyword("cursor");
            var kind = CursorKind.Auto;

            if (!String.IsNullOrWhiteSpace(value))
            {
                var comma = value.LastIndexOf(',');
                var keyword = (comma < 0 ? value : value[(comma + 1)..]).Trim();

                kind = keyword.ToLowerInvariant() switch
                {
                    "pointer" or "hand" => CursorKind.Pointer,
                    "text" => CursorKind.Text,
                    "vertical-text" => CursorKind.VerticalText,
                    "move" => CursorKind.Move,
                    "wait" => CursorKind.Wait,
                    "help" => CursorKind.Help,
                    "not-allowed" => CursorKind.NotAllowed,
                    "crosshair" => CursorKind.Crosshair,
                    "none" => CursorKind.None,
                    "default" => CursorKind.Default,
                    _ => CursorKind.Auto,
                };
            }

            // A link asks for the hand even though it names no cursor: `auto` is not a preference but a
            // question - it leaves the choice to the engine - and the engine's answer for a link is the
            // pointer. A cursor the page did write stands, which is why this is not a rule in the
            // engine's sheet: that one would outrank an author's plain `a { cursor: ... }`.
            return kind == CursorKind.Auto && IsFollowable ? CursorKind.Pointer : kind;
        }
    }

    /// <summary>
    /// Gets whether the element is a link, i.e. an anchor with somewhere to go. The words inside a link
    /// are not themselves links, so only the anchor carries the engine's own link colours.
    /// </summary>
    private Boolean IsFollowable => _element is IHtmlAnchorElement anchor && anchor.HasAttribute("href");

    /// <summary>
    /// Gets the colour the engine paints a link in: blue for one that has not been followed, purple for
    /// one that has, red for the one being followed right now.
    /// </summary>
    private CssColorValue LinkColor =>
        _element?.GetPseudoClass(PseudoClassNames.Active) == true
            ? CssColorValue.FromRgb(0xee, 0x00, 0x00)
            : _element?.GetPseudoClass(PseudoClassNames.Visited) == true
                ? CssColorValue.FromRgb(0x55, 0x1a, 0x8b)
                : CssColorValue.FromRgb(0x00, 0x00, 0xee);

    /// <summary>
    /// Gets whether the page wrote a declaration for the property on the element itself, as opposed to
    /// the element merely taking one over from a parent.
    /// </summary>
    /// <remarks>
    /// The declaration the element was cascaded from does not say which of the two a value is: the
    /// inherited ones are folded into it, and the property that was inherited is the very object the
    /// parent holds. So the declarations are computed once more, from the rules that match the element
    /// and nothing else. That is a second selector match, which is why it is only asked about a link.
    /// Not being able to tell counts as "the page wrote nothing", so a link is drawn as a link.
    /// <para>
    /// The property is asked for its value and not merely for its presence: a declaration the element
    /// does not carry still answers with a property, one the declaration factory invents to stand for
    /// the name that was asked about, and that placeholder holds no value at all.
    /// </para>
    /// </remarks>
    private Boolean Declares(String name) => Declares(_element, _dimensions, name);

    /// <summary>
    /// Gets whether the page declared the property on the given element itself, rather than the element
    /// merely taking it over from a parent.
    /// </summary>
    /// <remarks>
    /// The declaration the element was cascaded from does not say which of the two a value is: the
    /// inherited ones are folded into it, and the property that was inherited is the very object the
    /// parent holds. So the declarations are computed once more, from the rules that match the element
    /// and nothing else. That is a second selector match, which is why a caller settles it once and
    /// only for the elements it matters to.
    /// <para>
    /// The property is asked for its value and not merely for its presence: a declaration the element
    /// does not carry still answers with a property, one the declaration factory invents to stand for
    /// the name that was asked about, and that placeholder holds no value at all.
    /// </para>
    /// </remarks>
    internal static Boolean Declares(IElement? element, IRenderDimensions dimensions, String name)
    {
        if (element?.Owner is not { } document || document.DefaultView is not { } window)
        {
            return false;
        }

        var device = dimensions as IRenderDevice ?? document.Context.GetService<IRenderDevice>();

        return device is not null &&
               window.GetStyleCollection(device).ComputeExplicitStyle(element).GetProperty(name)?.RawValue is not null;
    }

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
    /// Gets the four corner radii as they were written, so that the layout pass can resolve a
    /// percentage against the size of the box itself - which is what a percentage radius is relative
    /// to - and shrink radii that would overlap.
    /// </summary>
    public StyleCornerRadii BorderRadius => new(
        LengthReader.TryReadCorner(_specified, _style, "border-top-left-radius", _dimensions, FontSize) ?? StyleCornerRadius.Zero,
        LengthReader.TryReadCorner(_specified, _style, "border-top-right-radius", _dimensions, FontSize) ?? StyleCornerRadius.Zero,
        LengthReader.TryReadCorner(_specified, _style, "border-bottom-right-radius", _dimensions, FontSize) ?? StyleCornerRadius.Zero,
        LengthReader.TryReadCorner(_specified, _style, "border-bottom-left-radius", _dimensions, FontSize) ?? StyleCornerRadius.Zero);

    /// <summary>
    /// Gets the box the background is painted into: <c>border-box</c> (the initial value) reaches
    /// under a transparent border, <c>padding-box</c> stops at the inner edge of the border, and
    /// <c>content-box</c> at the padding.
    /// </summary>
    public BackgroundClip BackgroundClip
    {
        get
        {
            // One keyword per background layer, and a box that paints a single background layer can
            // only be told apart by the first one.
            var value = ReadKeyword("background-clip") ?? String.Empty;
            var comma = value.IndexOf(',');
            var keyword = (comma < 0 ? value : value[..comma]).Trim();

            return keyword.ToLowerInvariant() switch
            {
                "padding-box" => BackgroundClip.PaddingBox,
                "content-box" => BackgroundClip.ContentBox,
                _ => BackgroundClip.BorderBox,
            };
        }
    }

    /// <summary>
    /// Gets whether descendants are clipped to the box, which is what every <c>overflow</c> value
    /// other than <c>visible</c> asks for, and with the rounded corners if there are any.
    /// </summary>
    /// <remarks>
    /// Hutu scrolls the document rather than a box, so <c>scroll</c>, <c>auto</c> and <c>clip</c> have
    /// nothing to do beyond hiding the overflow, and they are treated as one. Either axis clipping is
    /// enough: with one axis <c>visible</c> and the other not, CSS makes the visible one <c>auto</c>.
    /// </remarks>
    public Boolean ClipsOverflow =>
        !IsOverflowVisible("overflow-x") || !IsOverflowVisible("overflow-y");

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
    /// Gets the stroke each side of the border is drawn with. A keyword Hutu cannot draw - <c>double</c>,
    /// <c>groove</c>, <c>ridge</c>, <c>inset</c> and <c>outset</c> - is drawn as a solid stroke rather
    /// than dropped, since a side with a width but no stroke would leave a hole in the outline.
    /// </summary>
    public BorderStyles BorderStyles => new(
        ToLineStyle(BorderTopStyle),
        ToLineStyle(BorderRightStyle),
        ToLineStyle(BorderBottomStyle),
        ToLineStyle(BorderLeftStyle));

    /// <summary>
    /// Gets the four border style keywords. The collapsing border model breaks a tie between two
    /// borders of equal width by their styles, so the keyword itself has to survive, not just the
    /// question of whether the border is drawn.
    /// </summary>
    public String BorderTopStyle => Keyword("border-top-style", "none");

    public String BorderRightStyle => Keyword("border-right-style", "none");

    public String BorderBottomStyle => Keyword("border-bottom-style", "none");

    public String BorderLeftStyle => Keyword("border-left-style", "none");

    // --- Outline --------------------------------------------------------

    /// <summary>
    /// Gets the stroke the outline is drawn with. As with a border, a keyword Hutu cannot draw is drawn
    /// as a solid stroke rather than dropped, since dropping it would lose the outline altogether.
    /// </summary>
    public BorderLineStyle OutlineStyle => ToLineStyle(Keyword("outline-style", "none"));

    /// <summary>
    /// Gets how thick the outline is, or zero when there is nothing to draw.
    /// </summary>
    /// <remarks>
    /// An outline is only drawn once it has been given a stroke, and its width is initialised to
    /// <c>medium</c> rather than to nothing, so a page that names a style and no width is given the
    /// three pixels the spec pins that keyword to instead of an outline that silently disappears. The
    /// number is taken from the keyword AngleSharp holds rather than written out here, so that a
    /// declared <c>thin</c> or <c>thick</c> is read the same way.
    /// </remarks>
    public Double OutlineWidth => OutlineStyle == BorderLineStyle.None
        ? 0
        : Length("outline-width", RenderMode.Horizontal, CssLengthValue.Medium.Value);

    /// <summary>
    /// Gets how far outside the border edge the outline begins. It shifts the outline without taking
    /// any room in the layout, which is what distinguishes it from a margin.
    /// </summary>
    public Double OutlineOffset => Length("outline-offset", RenderMode.Horizontal, 0);

    /// <summary>
    /// Gets the colour the outline is drawn in.
    /// </summary>
    /// <remarks>
    /// The initial keyword is <c>invert</c>, which asks the platform to turn whatever lies beneath the
    /// stroke inside out - a system colour Hutu has no way to resolve, and one it deliberately does not
    /// pretend to draw. The element's own colour stands in, so an outline given no colour of its own
    /// still shows up rather than vanishing.
    /// </remarks>
    public CssColorValue OutlineColor
    {
        get
        {
            if (OutlineColorValue is { } value && value != CssColorValue.InvertedColor)
            {
                return value;
            }

            return Color;
        }
    }

    /// <summary>
    /// Reads the outline colour as it was computed. The declaration can hold it either as a colour or
    /// as a named constant standing for one, which is how <c>invert</c> survives the cascade.
    /// </summary>
    private CssColorValue? OutlineColorValue => _style.GetProperty("outline-color")?.RawValue switch
    {
        CssColorValue color => color,
        CssConstantValue<CssColorValue> constant => constant.Value,
        _ => null,
    };

    // --- Helpers --------------------------------------------------------

    /// <summary>
    /// Reads a length against the element's own font size, or returns false when the property is
    /// absent or a keyword such as <c>auto</c>. Use this for the properties where "not set" differs
    /// from zero (width, height).
    /// </summary>
    public Boolean TryLength(String name, RenderMode mode, out Double pixels) =>
        TryLengthAgainst(name, mode, FontSize, out pixels);

    /// <summary>
    /// Reads a length against the given font size. <c>font-size</c> itself is read this way: the size
    /// being worked out is the parent's, not the one this style will resolve other lengths against.
    /// </summary>
    private Boolean TryLengthAgainst(String name, RenderMode mode, Double fontSize, out Double pixels) =>
        TryReadLength(_specified, name, mode, fontSize, out pixels) ||
        TryReadLength(_style, name, mode, fontSize, out pixels);

    private Boolean TryReadLength(ICssStyleDeclaration? declaration, String name, RenderMode mode, Double fontSize, out Double pixels)
    {
        pixels = 0;

        if (declaration is null)
        {
            return false;
        }

        // The cascaded declaration is preferred because computing the style has already folded
        // font-relative units into pixels against the device; only a property that was never declared
        // falls back to it, and an initial value never needs the element's font size.
        // min()/max()/clamp() are resolved here: AngleSharp.Css models them as a math expression
        // rather than a length, so reading one as pixels yields zero and `width: min(100%, 640px)`
        // would collapse instead of picking an argument.
        var text = declaration.GetPropertyValue(name);

        if (!String.IsNullOrWhiteSpace(text))
        {
            // The state is copied into locals because a lambda inside a struct cannot capture `this`.
            var dimensions = _dimensions;
            var math = CssMathLengths.Evaluate(
                text,
                argument => CssLengthValue.TryParse(argument.Trim(), out var length)
                    ? Resolve(length, mode, dimensions, fontSize)
                    : null);

            if (math is not null)
            {
                pixels = math.Value;
                return true;
            }
        }

        var value = declaration.GetProperty(name)?.RawValue;

        if (value is null || value.Is("auto") || value.Is("normal") || value.Is("none"))
        {
            return false;
        }

        var resolved = Resolve(value, mode, fontSize);

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
        LengthReader.TryRead(_specified, _style, name, mode, _dimensions, FontSize, out var length) ? length : fallback;

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

    private static BorderLineStyle ToLineStyle(String keyword) => keyword.ToLowerInvariant() switch
    {
        "none" or "hidden" => BorderLineStyle.None,
        "dashed" => BorderLineStyle.Dashed,
        "dotted" => BorderLineStyle.Dotted,
        _ => BorderLineStyle.Solid,
    };

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

    /// <summary>
    /// Reads a keyword, preferring the cascaded declaration: computing a style rebuilds a shorthand's
    /// longhands lossily, so the value as it was written is the more faithful of the two.
    /// </summary>
    private String? ReadKeyword(String name)
    {
        var text = _specified?.GetPropertyValue(name);

        if (String.IsNullOrWhiteSpace(text))
        {
            text = _style.GetPropertyValue(name);
        }

        return String.IsNullOrWhiteSpace(text) ? null : text.Trim();
    }

    private Boolean IsOverflowVisible(String name)
    {
        var value = ReadKeyword(name);
        return value is null || value.Equals("visible", StringComparison.OrdinalIgnoreCase);
    }

    private Boolean Is(String name, String keyword)
    {
        var text = _style.GetPropertyValue(name);
        return text is not null && text.Trim().Equals(keyword, StringComparison.OrdinalIgnoreCase);
    }

    private Double? Resolve(ICssValue value, RenderMode mode, Double fontSize)
    {
        // A keyword standing for a length - `medium`, which is what `border-width` and `outline-width`
        // start out as, and `thin` and `thick` beside it - is carried as a named constant wrapping the
        // length it stands for, and only the length it wraps can be converted. Left wrapped the
        // conversion fails, which silently read every border and outline that named no width as zero.
        if (value is CssConstantValue<CssLengthValue> keyword)
        {
            value = keyword.Value;
        }

        // A length is resolved through the reader so that its font-relative units land on the
        // element's own font size; anything else is left to AngleSharp.
        if (value is CssLengthValue length)
        {
            return Resolve(length, mode, _dimensions, fontSize);
        }

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

    private static Double? Resolve(CssLengthValue value, RenderMode mode, IRenderDimensions dimensions, Double fontSize)
    {
        try
        {
            var pixels = LengthReader.ToPixel(value, mode, dimensions, fontSize);
            return Double.IsNaN(pixels) || Double.IsInfinity(pixels) ? null : pixels;
        }
        catch (Exception)
        {
            return null;
        }
    }
}

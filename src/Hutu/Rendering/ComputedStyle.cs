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
    private readonly FontFaceTable? _faces;

    /// <summary>
    /// The resolved areas, shared with every box whose style agrees. Filled once during construction
    /// and never written again; the areas it holds are not written at all.
    /// </summary>
    private readonly StyleValues _values;

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
        Boolean declaresFontSize = false,
        FontFaceTable? faces = null)
        : this(style, dimensions, specified, element, parentFontSize, parentColor, declaresFontSize, null, null, faces)
    {
    }

    /// <param name="pool">
    /// The intern table the box builder fills in for the tree it is building, so that two boxes whose
    /// areas agree share one instance. Null leaves each style with its own copies, which costs memory
    /// and changes nothing else - which is why a caller that has no table, a test building a style by
    /// hand, can simply pass none.
    /// </param>
    internal ComputedStyle(
        ICssStyleDeclaration style,
        IRenderDimensions dimensions,
        ICssStyleDeclaration? specified,
        IElement? element,
        Double parentFontSize,
        CssColorValue? parentColor,
        Boolean declaresFontSize,
        StyleIntern? pool,
        ComputedStyle? parent,
        FontFaceTable? faces = null)
    {
        _style = style ?? throw new ArgumentNullException(nameof(style));
        _dimensions = dimensions ?? throw new ArgumentNullException(nameof(dimensions));
        _specified = specified;
        _element = element;
        _parentFontSize = parentFontSize;
        _parentColor = parentColor;
        _declaresFontSize = declaresFontSize;
        _faces = faces;

        // Assigned before it is filled because a struct cannot call its own methods until every field
        // has a value, and the resolver is one of those methods. Anything it read through this object
        // before the fill would therefore see the shared default rather than fail loudly, so the
        // resolver reads declarations and nothing else - which keeps a mistake here to the shape of a
        // border that quietly stops being drawn, the one a border test cannot miss.
        _values = new StyleValues();
        ResolveAreas(pool, parent);
    }

    /// <summary>
    /// Resolves the areas whose properties cannot be inherited, and interns what it builds.
    /// </summary>
    /// <remarks>
    /// A property that cannot be inherited is in the element's declaration if and only if the element
    /// itself declared it, because AngleSharp merges only inheritable properties down from the parent.
    /// The names being absent from both declarations is therefore proof that every value in the area
    /// would come out at its initial value, and the area is left at the shared default: no reads, no
    /// allocation, no table lookup. That is the path the whole design rests on - it is what makes the
    /// cost depend on what a document declares rather than on how many properties the engine knows.
    /// </remarks>
    private void ResolveAreas(StyleIntern? pool, ComputedStyle? parent)
    {
        // The font first, and not by preference. Every other area reads lengths, a length in em, ex or
        // ch resolves against the element's own font size, and the reader asks this style for that size
        // - so an area resolved before the font would measure against a size of zero and quietly fall
        // back to the device's. There is no dependency the other way: the font resolves against the
        // parent's size, which the constructor is handed.
        ResolveFont(pool, parent);
        ResolveTextDecoration(pool, parent);

        ResolveBorder(pool);
        ResolveBoxModel(pool);
        ResolveDisplayPositioning(pool);
        ResolveVisualEffects(pool);
        ResolveText(pool, parent);
    }

    /// <summary>The names a font property can begin with, the <c>font</c> shorthand included.</summary>
    private static readonly String[] FontNames =
        { "font-size", "font-family", "font-weight", "font-style", "letter-spacing", "line-height" };

    /// <summary>The names a text property can begin with.</summary>
    private static readonly String[] TextNames = { "white-space", "text-align", "vertical-align", "text-indent" };

    /// <summary>The name a decoration property begins with, both longhands included.</summary>
    private static readonly String[] DecorationNames = { "text-decoration" };

    /// <summary>
    /// Resolves the font, taking the parent's where the element authored none of its own.
    /// </summary>
    /// <remarks>
    /// Every property here is inherited, so the declaration cannot say whether a value was authored or
    /// handed down - which is what makes the areas that cannot be inherited cheap and this one not.
    /// What it can say is whether the value is the parent's: the cascade leaves a property the element
    /// never authored as the very object the parent resolved from.
    /// </remarks>
    private void ResolveFont(StyleIntern? pool, ComputedStyle? parent)
    {
        if (parent is { } inherited && Inherits(inherited, FontNames))
        {
            _values.Font = inherited._values.Font;

            return;
        }

        // Resolved before anything that measures against it, and passed on by value: letter spacing
        // and line height resolve their em against this size, and reading the property instead would
        // read the area being built.
        var size = ResolveFontSize();

        var font = new FontArea
        {
            Size = size,
            Family = ResolveFontFamily(),
            Weight = ResolveFontWeight(),
            Italic = Is("font-style", "italic") || Is("font-style", "oblique"),
            LetterSpacing = Length("letter-spacing", RenderMode.Horizontal, new LengthReader.FontSource(size), CssInitialValues.LetterSpacing),
            LineHeight = ResolveLineHeight(size),
        };

        _values.Font = pool is null ? font : pool.Font(font);
    }

    /// <summary>
    /// Gets the family list the element asks for, with the names the document renamed with
    /// <c>@font-face</c> standing for the local fonts behind them.
    /// </summary>
    /// <remarks>
    /// A book names its faces once and asks for them by name, so <c>font-family: st</c> means the
    /// typeface the rule points at - and a platform asked for <c>st</c> answers with its generic
    /// fallback instead. Reading the list through the document's faces is what turns the name into the
    /// list of families it stands for; the backend is left to search the names it is handed.
    /// </remarks>
    private String ResolveFontFamily()
    {
        var family = Keyword("font-family", CssInitialValues.FontFamily);

        return _faces is { } faces ? faces.Expand(family) : family;
    }

    private void ResolveText(StyleIntern? pool, ComputedStyle? parent)
    {
        if (parent is { } inherited && Inherits(inherited, TextNames))
        {
            _values.Text = inherited._values.Text;

            return;
        }

        var text = new TextArea
        {
            WhiteSpace = Keyword("white-space", CssInitialValues.WhiteSpace),
            Align = Keyword("text-align", CssInitialValues.TextAlign),
            VerticalAlign = Keyword("vertical-align", CssInitialValues.VerticalAlign),
            Indent = ReadLength("text-indent", RenderMode.Horizontal, StyleLength.Zero),
        };

        _values.Text = pool is null ? text : pool.Text(text);
    }

    private void ResolveTextDecoration(StyleIntern? pool, ComputedStyle? parent)
    {
        if (parent is { } inherited && Inherits(inherited, DecorationNames))
        {
            _values.Decoration = inherited._values.Decoration;

            return;
        }

        var decoration = new TextDecorationArea
        {
            Underline = HasDecoration("underline"),
            LineThrough = HasDecoration("line-through"),
        };

        _values.Decoration = pool is null ? decoration : pool.Decoration(decoration);
    }

    /// <summary>
    /// Gets whether every named property this element holds is the very instance its parent holds.
    /// </summary>
    /// <remarks>
    /// That is what the cascade leaves behind for a property the element never authored: a parent's
    /// declarations are merged down by reference, so an inherited value is literally the parent's
    /// property object. Two absences match as well - a property neither declares is one neither
    /// resolved, and the parent's area already holds what that means.
    /// <para>
    /// Deliberately not <c>ICssProperty.IsInherited</c>, which reads as though it answered this and
    /// does not: it is true when a property can be inherited and is still at its initial value. A
    /// parent that authored a font family hands the child a property carrying the parent's value and
    /// a false flag, so that test would call the value the child's own.
    /// </para>
    /// </remarks>
    private Boolean Inherits(ComputedStyle parent, String[] names)
    {
        foreach (var name in names)
        {
            if (!Same(_specified?.GetProperty(name), parent._specified?.GetProperty(name)))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Gets whether two declarations answer a name the same way: with the same property, or with
    /// nothing at all.
    /// </summary>
    /// <remarks>
    /// The second case is not a formality. A declaration answers a name it does not carry with a
    /// property the factory invents for the occasion, so two declarations that both hold nothing
    /// return two different objects that both hold nothing - and a name nobody declared is the common
    /// case for the properties an area's check covers. Comparing what they hold rather than which
    /// object they are is what tells the two apart, and the typed value answers that without building
    /// the string that <c>Value</c> would.
    /// </remarks>
    private static Boolean Same(ICssProperty? mine, ICssProperty? theirs) =>
        ReferenceEquals(mine, theirs) || (mine?.RawValue is null && theirs?.RawValue is null);

    /// <summary>The font size, resolved before anything that measures against it.</summary>
    private Double ResolveFontSize()
    {
        // A relative font-size is relative to the parent, and the cascade resolves it against the
        // device - the root font size - instead. It is resolved here, because the element's own font
        // size is the reference every other length of this style resolves its em against.
        if (_specified?.GetProperty("font-size")?.RawValue is CssLengthValue declared && IsFontRelative(declared.Type))
        {
            // An inherited value is not the element's own declaration; the parent already resolved it,
            // and taking that size over as it is stops nested em font sizes from stacking. Only a
            // value the element declares is measured against the parent.
            if (_declaresFontSize)
            {
                return ResolveFontRelative(declared);
            }

            return _parentFontSize > 0 ? _parentFontSize : _dimensions.FontSize;
        }

        return TryLengthAgainst("font-size", RenderMode.Vertical, new LengthReader.FontSource(_dimensions.FontSize), out var pixels)
            ? pixels
            : _dimensions.FontSize;
    }

    private Int32 ResolveFontWeight()
    {
        var text = Keyword("font-weight", CssInitialValues.FontWeight);

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

    /// <summary>The line height, measured against the size the font area is being built with.</summary>
    private Double? ResolveLineHeight(Double size)
    {
        // A percentage is a share of the element's own font size, the same reference the number below
        // is measured against. It is taken here rather than left to the length reader, which resolves
        // a percentage that reaches the device against the page: `line-height: 100%` came back as the
        // viewport height, and a heading set in a 1200-tall viewport grew a line 1200 tall.
        if (LineHeightShare() is { } share)
        {
            return share * 0.01 * size;
        }

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
            return factor * size;
        }

        return TryLengthAgainst("line-height", RenderMode.Vertical, new LengthReader.FontSource(size), out var pixels)
            ? pixels
            : null;
    }

    /// <summary>
    /// Gets the declared <c>line-height</c> as a percentage, or null when it was declared as a number,
    /// a length or a keyword.
    /// </summary>
    /// <remarks>
    /// Both declarations are asked, in the order the length reader asks them, because a declared
    /// percentage is the one thing here the computed declaration does not necessarily carry: computing
    /// a style leaves a length in pixels, so the element's own declaration is the only place a share is
    /// still recognisable as one.
    /// </remarks>
    private Double? LineHeightShare() =>
        ShareOf(_specified) ?? ShareOf(_style);

    private static Double? ShareOf(ICssStyleDeclaration? declaration) =>
        declaration?.GetProperty("line-height")?.RawValue is CssLengthValue { Type: CssLengthValue.Unit.Percent } share
            ? share.Value
            : null;

    /// <summary>The names a display or a clipping property can begin with.</summary>
    private static readonly String[] DisplayNames = { "display", "object-fit", "overflow" };

    /// <summary>The name an outline property begins with. The colour matches too, and resolving the
    /// area for it alone is harmless - it reads the three that are here and finds nothing.</summary>
    private static readonly String[] OutlineNames = { "outline" };

    private void ResolveDisplayPositioning(StyleIntern? pool)
    {
        if (!DeclaresAny(DisplayNames))
        {
            return;
        }

        var value = Keyword("object-fit", CssInitialValues.ObjectFit);

        var area = new DisplayPositioningArea
        {
            Display = Keyword("display", CssInitialValues.Display),
            ClipsOverflow = !IsOverflowVisible("overflow-x") || !IsOverflowVisible("overflow-y"),
            ObjectFit = value.ToLowerInvariant() switch
            {
                "contain" => Layout.ObjectFit.Contain,
                "cover" => Layout.ObjectFit.Cover,
                "none" => Layout.ObjectFit.None,
                "scale-down" => Layout.ObjectFit.ScaleDown,
                _ => Layout.ObjectFit.Fill,
            },
        };

        _values.DisplayPositioning = pool is null ? area : pool.DisplayPositioning(area);
    }

    private void ResolveVisualEffects(StyleIntern? pool)
    {
        if (!DeclaresAny(OutlineNames))
        {
            return;
        }

        var style = ToLineStyle(Keyword("outline-style", CssInitialValues.OutlineStyle));

        var area = new VisualEffectsArea
        {
            OutlineStyle = style,
            OutlineWidth = style == BorderLineStyle.None
                ? 0
                : Length("outline-width", RenderMode.Horizontal, CssLengthValue.Medium.Value),
            OutlineOffset = Length("outline-offset", RenderMode.Horizontal, CssInitialValues.OutlineOffset),
        };

        _values.VisualEffects = pool is null ? area : pool.VisualEffects(area);
    }

    /// <summary>The names a border property can begin with, shorthands included.</summary>
    private static readonly String[] BorderNames = { "border" };

    /// <summary>
    /// The names a box model property can begin with. <c>min-</c> and <c>max-</c> catch the height
    /// constraints; nothing else the engine reads starts with those, and a name that merely ends in
    /// <c>width</c> - a border's, an outline's - does not match <c>width</c> here, because this asks
    /// what a name begins with.
    /// </summary>
    private static readonly String[] BoxModelNames =
        { "margin", "padding", "width", "height", "min-", "max-", "box-sizing" };

    private void ResolveBorder(StyleIntern? pool)
    {
        if (!DeclaresAny(BorderNames))
        {
            return;
        }

        var topStyle = Keyword("border-top-style", CssInitialValues.BorderStyle);
        var rightStyle = Keyword("border-right-style", CssInitialValues.BorderStyle);
        var bottomStyle = Keyword("border-bottom-style", CssInitialValues.BorderStyle);
        var leftStyle = Keyword("border-left-style", CssInitialValues.BorderStyle);

        var border = new BorderArea
        {
            TopStyle = topStyle,
            RightStyle = rightStyle,
            BottomStyle = bottomStyle,
            LeftStyle = leftStyle,
            TopWidth = BorderWidth(topStyle, "border-top-width"),
            RightWidth = BorderWidth(rightStyle, "border-right-width"),
            BottomWidth = BorderWidth(bottomStyle, "border-bottom-width"),
            LeftWidth = BorderWidth(leftStyle, "border-left-width"),
            Lines = new BorderStyles(
                ToLineStyle(topStyle),
                ToLineStyle(rightStyle),
                ToLineStyle(bottomStyle),
                ToLineStyle(leftStyle)),
            Radius = ResolveBorderRadius(),
        };

        _values.Border = pool is null ? border : pool.Border(border);
    }

    private void ResolveBoxModel(StyleIntern? pool)
    {
        // An element that declares nothing still has a box model to resolve where the reader gives it
        // margins of its own - which is every heading and paragraph of a page that states none.
        if (!DeclaresAny(BoxModelNames) && Defaults("margin") is (null, null, null, null))
        {
            return;
        }

        var model = new BoxModelArea
        {
            BoxSizing = ResolveBoxSizing(),
            Width = ReadLength("width", RenderMode.Horizontal, StyleLength.Auto),
            Height = ReadLength("height", RenderMode.Vertical, StyleLength.Auto),
            MinWidth = ReadLength("min-width", RenderMode.Horizontal, StyleLength.None),
            MaxWidth = ReadLength("max-width", RenderMode.Horizontal, StyleLength.None),
            MinHeight = ReadLength("min-height", RenderMode.Vertical, StyleLength.None),
            MaxHeight = ReadLength("max-height", RenderMode.Vertical, StyleLength.None),
            Margin = ReadThickness("margin", StyleLength.Zero),
            Padding = ReadThickness("padding", StyleLength.Zero),
        };

        _values.BoxModel = pool is null ? model : pool.BoxModel(model);
    }

    /// <summary>
    /// Gets which box a declared width and height describe. The typed value is asked first because the
    /// serialised one costs a string to build, and the string is only a fallback for a declaration the
    /// typed read cannot make sense of.
    /// </summary>
    private BoxSizing ResolveBoxSizing()
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

    /// <summary>
    /// Gets whether either declaration carries a property whose name begins with one of the prefixes.
    /// A declaration enumerates only the names that were set, so one pass over those answers for a
    /// whole area - and the answer is what decides whether the area needs resolving at all.
    /// </summary>
    /// <remarks>
    /// A prefix rather than the exact names because a shorthand has to be caught too: <c>margin</c>
    /// declares four sides, and none of them would match if the test were for <c>margin-top</c>.
    /// </remarks>
    private Boolean DeclaresAny(String[] prefixes) =>
        DeclaresAny(_specified, prefixes) || DeclaresAny(_style, prefixes);

    private static Boolean DeclaresAny(ICssStyleDeclaration? declaration, String[] prefixes)
    {
        if (declaration is null)
        {
            return false;
        }

        foreach (var property in declaration)
        {
            foreach (var prefix in prefixes)
            {
                if (property.Name.StartsWith(prefix, StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>The corner radii as declared, with any percentage still unresolved.</summary>
    private StyleCornerRadii ResolveBorderRadius() => new(
        LengthReader.TryReadCorner(_specified, _style, "border-top-left-radius", _dimensions, FontSourceOf()) ?? StyleCornerRadius.Zero,
        LengthReader.TryReadCorner(_specified, _style, "border-top-right-radius", _dimensions, FontSourceOf()) ?? StyleCornerRadius.Zero,
        LengthReader.TryReadCorner(_specified, _style, "border-bottom-right-radius", _dimensions, FontSourceOf()) ?? StyleCornerRadius.Zero,
        LengthReader.TryReadCorner(_specified, _style, "border-bottom-left-radius", _dimensions, FontSourceOf()) ?? StyleCornerRadius.Zero);

    /// <summary>
    /// The resolved areas of one element's style.
    /// </summary>
    /// <remarks>
    /// A reference type so the struct holds one field instead of sixteen. <c>Box.Style</c> hands out a
    /// copy of the struct on every read, and a copy carrying the areas itself would memcpy every one of
    /// them - fifteen thousand times a pass on the sample, before anything is even resolved.
    /// </remarks>
    private sealed class StyleValues
    {
        internal BorderArea Border = BorderArea.Default;

        internal BoxModelArea BoxModel = BoxModelArea.Default;

        internal DisplayPositioningArea DisplayPositioning = DisplayPositioningArea.Default;

        internal VisualEffectsArea VisualEffects = VisualEffectsArea.Default;

        internal FontArea Font = new();

        internal TextArea Text = new();

        internal TextDecorationArea Decoration = new();

        // Null until something asks. See ComputedStyle.Position and .Flex for why these four wait
        // while the areas above do not.
        internal FlexStyle? Flex;

        internal GridStyle? Grid;

        internal TableStyle? Table;

        internal PositionStyle? Position;
    }

    /// <summary>
    /// Gets the out-of-flow mode, the offsets and the stacking order as one value. The layout asks for
    /// these together and repeatedly - once per child to decide whether a box is in flow, then again to
    /// place it - and reading them as one keeps that from costing twenty declaration reads a time.
    /// </summary>
    public PositionStyle Position => _values.Position ??= PositionStyle.Read(this);

    /// <summary>Gets the underlying declaration, for values this wrapper does not surface.</summary>
    public ICssStyleDeclaration Declaration => _style;

    // --- Box generation -------------------------------------------------

    public String Display => _values.DisplayPositioning.Display;

    public Boolean IsDisplayNone =>
        _values.DisplayPositioning.Display.Equals("none", StringComparison.OrdinalIgnoreCase);

    public String WhiteSpace => _values.Text.WhiteSpace;

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

    public String FontFamily => _values.Font.Family;

    /// <summary>
    /// Gets the font size in pixels. A percentage is the one length that resolves against the parent's
    /// font size rather than the containing block, and the cascade folds it into pixels against the
    /// render device - the viewport - so a declared percentage is read back here and scaled by the size
    /// the box builder passed down.
    /// </summary>
    public Double FontSize => _values.Font.Size;

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

    public Int32 FontWeight => _values.Font.Weight;

    public Boolean IsItalic => _values.Font.Italic;

    /// <summary>
    /// Gets the line height in pixels, or null for <c>normal</c> (which defers to the font metrics).
    /// A unitless value is a factor of the font size.
    /// </summary>
    public Double? LineHeight => _values.Font.LineHeight;

    public String TextAlign => _values.Text.Align;

    /// <summary>
    /// Gets the distance the first line of the box is set in from the edge it begins at. A negative
    /// one hangs the first line out of the box, which is how a note's marker is set beside its text.
    /// </summary>
    public StyleLength TextIndent => _values.Text.Indent;

    /// <summary>
    /// Gets the extra space added to every character's advance, in pixels. <c>normal</c>, the initial
    /// value, means none; a length is resolved against the element's own font size, so <c>.16em</c> is
    /// that share of the size the text is set at. The space follows the last character too, as CSS
    /// asks, which is why a run is that much wider than its glyphs.
    /// </summary>
    public Double LetterSpacing => _values.Font.LetterSpacing;

    public String VerticalAlign => _values.Text.VerticalAlign;

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
    /// Gets the direction the element's lines read in. The <c>direction</c> property is inherited, so
    /// the cascade already carries what an ancestor asked for; the <c>dir</c> attribute is a
    /// presentational hint that stands in when no rule declares the property, and it is read from the
    /// nearest ancestor that carries one because HTML inherits it the same way.
    /// </summary>
    public TextDirection Direction
    {
        get
        {
            if (DirectionAttribute() is { } attribute && !Declares("direction"))
            {
                return attribute;
            }

            return String.Equals(ReadKeyword("direction"), "rtl", StringComparison.OrdinalIgnoreCase)
                ? TextDirection.RightToLeft
                : TextDirection.LeftToRight;
        }
    }

    /// <summary>
    /// Gets the direction the nearest <c>dir</c> attribute asks for, or null when the element and its
    /// ancestors carry none. <c>dir="auto"</c> is not modelled and is passed over, which leaves the
    /// <c>direction</c> property to answer.
    /// </summary>
    private TextDirection? DirectionAttribute()
    {
        for (var element = _element; element is not null; element = element.ParentElement)
        {
            var dir = element.GetAttribute("dir")?.Trim();

            if (String.Equals(dir, "rtl", StringComparison.OrdinalIgnoreCase))
            {
                return TextDirection.RightToLeft;
            }

            if (String.Equals(dir, "ltr", StringComparison.OrdinalIgnoreCase))
            {
                return TextDirection.LeftToRight;
            }
        }

        return null;
    }

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
            var direction = Direction;

            if (mode == Layout.WritingMode.HorizontalTb)
            {
                return direction == TextDirection.LeftToRight
                    ? WritingModeInfo.Horizontal
                    : new WritingModeInfo(Layout.WritingMode.HorizontalTb, TextOrientation.Mixed, false, direction);
            }

            return new WritingModeInfo(
                mode,
                DeclaredTextOrientation ?? TextOrientation.Mixed,
                DeclaredTextCombineUpright ?? false,
                direction);
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

    public Boolean HasUnderline => _values.Decoration.Underline;

    public Boolean HasLineThrough => _values.Decoration.LineThrough;

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

    /// <summary>
    /// Gets whether the element the declaration belongs to declares the property on itself, as opposed
    /// to taking the parent's value over by inheritance, without running a second cascade.
    /// </summary>
    /// <remarks>
    /// A property the element did not declare itself is the very object the parent holds: the cascade
    /// merges an inheritable declaration down by reference. So a property that is the parent's own is
    /// inherited, and a property that is not was declared on the element. This is the same test
    /// <see cref="Inherits"/> uses, applied to any property rather than to a set of names.
    /// <para>
    /// A parent that is not in hand means there is nothing to inherit from, so a property present is
    /// taken as declared.
    /// </para>
    /// </remarks>
    internal static Boolean DeclaresOwn(
        ICssStyleDeclaration specified,
        ComputedStyle? parent,
        String name)
    {
        var mine = specified.TryGetProperty(name);

        if (parent is null)
        {
            return mine is not null;
        }

        var theirs = parent.Value._specified?.TryGetProperty(name);
        return !Same(mine, theirs);
    }

    // --- Box model ------------------------------------------------------

    /// <summary>
    /// Gets which box a declared width and height describe.
    /// </summary>
    public BoxSizing BoxSizing => _values.BoxModel.BoxSizing;

    /// <summary>
    /// Gets the flex longhands, both the container's and the item's. They are read together because
    /// the same declaration serves both roles depending on the parent.
    /// </summary>
    /// <summary>
    /// Gets the flex longhands as one value.
    /// </summary>
    /// <remarks>
    /// Resolved on the first ask and kept, rather than resolved with the areas. A reader that decides
    /// "is this a flex property" is what the other areas are gated on, and it cannot answer here:
    /// <c>align-items</c>, <c>justify-content</c> and <c>gap</c> belong to grid as much as to flex, so
    /// a gate would resolve this one for every grid item on the page and never use it. Waiting until
    /// something asks costs an element that is not a flex container one null check.
    /// </remarks>
    public FlexStyle Flex => _values.Flex ??= FlexStyle.Read(this);

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
    internal GridStyle Grid => _values.Grid ??= GridStyle.Read(this);

    /// <summary>
    /// Gets the table longhands. <c>border-collapse</c>, <c>border-spacing</c> and friends only
    /// matter on a table box; <c>vertical-align</c> is meaningful on a cell.
    /// </summary>
    public TableStyle Table => _values.Table ??= TableStyle.Read(this);

    /// <summary>
    /// Gets the cascaded declaration. It is the one that still carries a percentage, because
    /// computing the style folds a percentage into pixels against the render device - the viewport -
    /// and the layout engine has to resolve it against the containing block instead.
    /// </summary>
    internal ICssStyleDeclaration? Specified => _specified;

    /// <summary>
    /// Gets a declaration's value with its type intact, which is where every reader that wants more
    /// than the text starts. Counted like the string readers, because the lookup is the same and only
    /// the materialisation differs.
    /// </summary>
    internal ICssValue? RawValue(String name)
    {
        return _style.GetProperty(name)?.RawValue;
    }

    /// <summary>
    /// Gets the same for a name a declaration expands into longhands and so never carries, and which
    /// can therefore only be answered by rebuilding the shorthand - <c>gap</c> is one, and the reader
    /// that wants the column gap has nowhere else to find it.
    /// </summary>
    /// <remarks>
    /// This is the expensive one: where the declaration carries nothing,
    /// the rebuild walks the shorthand's own longhands, converts each and allocates a property to hold
    /// what they come to - and a caller that reads again pays all of that again.
    /// </remarks>
    internal ICssValue? RawShorthand(String name)
    {
        return _style.GetProperty(name)?.RawValue;
    }

    internal IRenderDimensions Dimensions => _dimensions;

    public StyleLength Width => _values.BoxModel.Width;

    public StyleLength Height => _values.BoxModel.Height;

    public StyleLength MinWidth => _values.BoxModel.MinWidth;

    public StyleLength MaxWidth => _values.BoxModel.MaxWidth;

    public StyleLength MinHeight => _values.BoxModel.MinHeight;

    public StyleLength MaxHeight => _values.BoxModel.MaxHeight;

    /// <summary>
    /// Gets how replaced content (an image) is fitted into its box. Geometry is decided elsewhere;
    /// this only tells the painter how to fill what layout resolved.
    /// </summary>
    public Layout.ObjectFit ObjectFit => _values.DisplayPositioning.ObjectFit;

    /// <summary>
    /// Gets the four corner radii as they were written, so that the layout pass can resolve a
    /// percentage against the size of the box itself - which is what a percentage radius is relative
    /// to - and shrink radii that would overlap.
    /// </summary>
    public StyleCornerRadii BorderRadius => _values.Border.Radius;

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
    public Boolean ClipsOverflow => _values.DisplayPositioning.ClipsOverflow;

    /// <summary>
    /// Gets the four margins, with their units intact: <c>auto</c> survives, which is what makes
    /// <c>margin: 0 auto</c> able to centre a box. A percentage margin resolves against the
    /// containing block's width on all four sides.
    /// </summary>
    public StyleThickness Margin => _values.BoxModel.Margin;

    /// <summary>Gets the four paddings. As with margins, every side resolves against the width.</summary>
    public StyleThickness Padding => _values.BoxModel.Padding;

    public Double BorderTopWidth => _values.Border.TopWidth;

    public Double BorderRightWidth => _values.Border.RightWidth;

    public Double BorderBottomWidth => _values.Border.BottomWidth;

    public Double BorderLeftWidth => _values.Border.LeftWidth;

    public CssColorValue? BorderTopColor => ResolveBorderColor(ColorValue("border-top-color"));

    public CssColorValue? BorderRightColor => ResolveBorderColor(ColorValue("border-right-color"));

    public CssColorValue? BorderBottomColor => ResolveBorderColor(ColorValue("border-bottom-color"));

    public CssColorValue? BorderLeftColor => ResolveBorderColor(ColorValue("border-left-color"));

    public Boolean BorderTopVisible => BorderArea.Draws(_values.Border.TopStyle);

    public Boolean BorderRightVisible => BorderArea.Draws(_values.Border.RightStyle);

    public Boolean BorderBottomVisible => BorderArea.Draws(_values.Border.BottomStyle);

    public Boolean BorderLeftVisible => BorderArea.Draws(_values.Border.LeftStyle);

    /// <summary>
    /// Gets the stroke each side of the border is drawn with. A keyword Hutu cannot draw - <c>double</c>,
    /// <c>groove</c>, <c>ridge</c>, <c>inset</c> and <c>outset</c> - is drawn as a solid stroke rather
    /// than dropped, since a side with a width but no stroke would leave a hole in the outline.
    /// </summary>
    public BorderStyles BorderStyles => _values.Border.Lines;

    /// <summary>
    /// Gets the four border style keywords. The collapsing border model breaks a tie between two
    /// borders of equal width by their styles, so the keyword itself has to survive, not just the
    /// question of whether the border is drawn.
    /// </summary>
    public String BorderTopStyle => _values.Border.TopStyle;

    public String BorderRightStyle => _values.Border.RightStyle;

    public String BorderBottomStyle => _values.Border.BottomStyle;

    public String BorderLeftStyle => _values.Border.LeftStyle;

    // --- Outline --------------------------------------------------------

    /// <summary>
    /// Gets the stroke the outline is drawn with. As with a border, a keyword Hutu cannot draw is drawn
    /// as a solid stroke rather than dropped, since dropping it would lose the outline altogether.
    /// </summary>
    public BorderLineStyle OutlineStyle => _values.VisualEffects.OutlineStyle;

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
    public Double OutlineWidth => _values.VisualEffects.OutlineWidth;

    /// <summary>
    /// Gets how far outside the border edge the outline begins. It shifts the outline without taking
    /// any room in the layout, which is what distinguishes it from a margin.
    /// </summary>
    public Double OutlineOffset => _values.VisualEffects.OutlineOffset;

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
        TryLengthAgainst(name, mode, new LengthReader.FontSource(this), out pixels);

    /// <summary>
    /// Reads a length against the given font size. <c>font-size</c> itself is read this way: the size
    /// being worked out is the parent's, not the one this style will resolve other lengths against.
    /// </summary>
    /// <remarks>
    /// The size arrives as a <see cref="LengthReader.FontSource"/> rather than as a number so that a
    /// length in pixels or a percentage never asks for one - which is most of them, and which used to
    /// cost a resolution of <c>font-size</c> per read. A caller that already has the size, as the
    /// resolver for the font area does, hands the number over instead.
    /// </remarks>
    private Boolean TryLengthAgainst(String name, RenderMode mode, LengthReader.FontSource font, out Double pixels)
    {
        return TryReadLength(_specified, name, mode, font, out pixels) ||
               TryReadLength(_style, name, mode, font, out pixels);
    }

    private Boolean TryReadLength(ICssStyleDeclaration? declaration, String name, RenderMode mode, LengthReader.FontSource font, out Double pixels)
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
                    ? Resolve(length, mode, dimensions, font)
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

        var resolved = Resolve(value, mode, font);

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
    /// The same, against a font size the caller already has. Used while an area is being resolved,
    /// where reading the element's own size would read the area that is not filled yet.
    /// </summary>
    private Double Length(String name, RenderMode mode, LengthReader.FontSource font, Double fallback) =>
        TryLengthAgainst(name, mode, font, out var pixels) ? pixels : fallback;

    /// <summary>
    /// Reads a box-model length, deferring its percentage to the layout pass. The cascaded
    /// declaration is asked first because computing the style folds percentages into pixels.
    /// </summary>
    private StyleLength ReadLength(String name, RenderMode mode, StyleLength fallback) =>
        ReadDeclared(name, mode) ?? fallback;

    /// <summary>Reads a length the page declared, or null when it declared none.</summary>
    private StyleLength? ReadDeclared(String name, RenderMode mode) =>
        LengthReader.TryRead(_specified, _style, name, mode, _dimensions, FontSourceOf(), out var length) ? length : null;

    /// <summary>
    /// Gets the font size a length read resolves against - the element's own, which the reader asks for
    /// only if the length it is reading turns out to need one. Handing over the style rather than the
    /// size is what makes that possible, and it is worth about a quarter of a layout pass: a length in
    /// pixels or a percentage has no use for a font size, and resolving one walks the declarations.
    /// </summary>
    private LengthReader.FontSource FontSourceOf() => new(this);

    /// <summary>
    /// Reads a box-model thickness: the four sides a page names by name, the four it names by the axis
    /// they lie on, and the four the reader supplies for an element the page says nothing about.
    /// </summary>
    /// <remarks>
    /// A sheet written for a vertical book states its insets the flow-relative way - the block axis is
    /// the space beside the column and the inline axis the space along it - because that is the naming
    /// that keeps meaning something once the text is set the other way. Nothing in AngleSharp resolves
    /// one onto a physical side: it parses the properties and keeps them apart, so a page that named
    /// them alone would lay out as if it had declared no margin at all.
    /// <para>
    /// The three are read in order of what they mean. A side the page states by name and by axis both
    /// is the statement it meant, so the axis wins: a book that resets its margins with <c>margin: 0</c>
    /// and then says which one it wants with <c>-webkit-margin-start</c> is asking for that one. Where
    /// the page states a side by name alone, that is what it says. Where it says nothing, the defaults
    /// below stand - which is the only way a reader's own default can be made to yield to the page, the
    /// cascade being unable to tell a reader's declaration from a page's.
    /// </para>
    /// </remarks>
    private StyleThickness ReadThickness(String prefix, StyleLength fallback)
    {
        var top = ReadDeclared(prefix + "-top", RenderMode.Horizontal);
        var right = ReadDeclared(prefix + "-right", RenderMode.Horizontal);
        var bottom = ReadDeclared(prefix + "-bottom", RenderMode.Horizontal);
        var left = ReadDeclared(prefix + "-left", RenderMode.Horizontal);

        var inlineStart = ReadDeclared(prefix + "-inline-start", RenderMode.Horizontal);
        var inlineEnd = ReadDeclared(prefix + "-inline-end", RenderMode.Horizontal);
        var blockStart = ReadDeclared(prefix + "-block-start", RenderMode.Horizontal);
        var blockEnd = ReadDeclared(prefix + "-block-end", RenderMode.Horizontal);

        // A keyword that stands for a value rather than naming one is a statement about the side it
        // names, so it stands where that side is otherwise stated too - which is the whole point of
        // writing one: `margin-block-end: revert` is a page taking back a margin it gave that side in a
        // declaration earlier in the same rule.
        var topKeyword = KeywordOf(prefix + "-top");
        var rightKeyword = KeywordOf(prefix + "-right");
        var bottomKeyword = KeywordOf(prefix + "-bottom");
        var leftKeyword = KeywordOf(prefix + "-left");
        var inlineStartKeyword = KeywordOf(prefix + "-inline-start");
        var inlineEndKeyword = KeywordOf(prefix + "-inline-end");
        var blockStartKeyword = KeywordOf(prefix + "-block-start");
        var blockEndKeyword = KeywordOf(prefix + "-block-end");

        var (defaultBlockStart, defaultBlockEnd, defaultInlineStart, defaultInlineEnd) = Defaults(prefix);

        if (inlineStart is null && inlineEnd is null && blockStart is null && blockEnd is null &&
            inlineStartKeyword is null && inlineEndKeyword is null &&
            blockStartKeyword is null && blockEndKeyword is null &&
            defaultBlockStart is null && defaultBlockEnd is null &&
            defaultInlineStart is null && defaultInlineEnd is null)
        {
            // Nothing was named by axis and nothing is left unsupplied, which is every box of a page
            // that was set the usual way and every padding in the world.
            return new StyleThickness(
                top ?? fallback,
                right ?? fallback,
                bottom ?? fallback,
                left ?? fallback);
        }

        var flow = Flow;

        if (flow.IsVertical)
        {
            // The line runs down the page, so the inline axis lands on the top and the bottom and the
            // block axis on the right and the left - beginning at the right where the columns run
            // leftward, which is what a right-to-left mode does.
            var (byInlineStart, byInlineEnd) = flow.InlineSign > 0
                ? (inlineStart, inlineEnd)
                : (inlineEnd, inlineStart);

            var (byBlockStart, byBlockEnd) = flow.BlockSign < 0
                ? (blockStart, blockEnd)
                : (blockEnd, blockStart);

            var (keywordTop, keywordBottom) = flow.InlineSign > 0
                ? (inlineStartKeyword, inlineEndKeyword)
                : (inlineEndKeyword, inlineStartKeyword);

            var (keywordRight, keywordLeft) = flow.BlockSign < 0
                ? (blockStartKeyword, blockEndKeyword)
                : (blockEndKeyword, blockStartKeyword);

            var (defaultTop, defaultBottom) = flow.InlineSign > 0
                ? (defaultInlineStart, defaultInlineEnd)
                : (defaultInlineEnd, defaultInlineStart);

            var (defaultRight, defaultLeft) = flow.BlockSign < 0
                ? (defaultBlockStart, defaultBlockEnd)
                : (defaultBlockEnd, defaultBlockStart);

            return new StyleThickness(
                Side(top, topKeyword, byInlineStart, keywordTop, defaultTop, fallback),
                Side(right, rightKeyword, byBlockStart, keywordRight, defaultRight, fallback),
                Side(bottom, bottomKeyword, byInlineEnd, keywordBottom, defaultBottom, fallback),
                Side(left, leftKeyword, byBlockEnd, keywordLeft, defaultLeft, fallback));
        }

        // A row runs across the page, so the inline axis is the horizontal pair and the block axis the
        // vertical one, which begins at the top whichever way the line itself reads.
        var (byStart, byEnd) = flow.InlineSign > 0
            ? (inlineStart, inlineEnd)
            : (inlineEnd, inlineStart);

        var (keywordStart, keywordEnd) = flow.InlineSign > 0
            ? (inlineStartKeyword, inlineEndKeyword)
            : (inlineEndKeyword, inlineStartKeyword);

        return new StyleThickness(
            Side(top, topKeyword, blockStart, blockStartKeyword, defaultBlockStart, fallback),
            Side(right, rightKeyword, byEnd, keywordEnd, defaultInlineEnd, fallback),
            Side(bottom, bottomKeyword, blockEnd, blockEndKeyword, defaultBlockEnd, fallback),
            Side(left, leftKeyword, byStart, keywordStart, defaultInlineStart, fallback));
    }

    /// <summary>
    /// Gets the value one physical side of a thickness comes to out of everything that named it.
    /// </summary>
    /// <param name="named">What the page said about the side under its own name, and
    /// <paramref name="keyword"/> the keyword it said it with.</param>
    /// <param name="namedByAxis">What it said under the name of the axis the side lies on, and
    /// <paramref name="axisKeyword"/> the keyword it said that with.</param>
    /// <param name="supplied">What the reader gives the side when the page says nothing.</param>
    /// <remarks>
    /// A side named both ways by a page that speaks both languages takes the axis name, which is where
    /// the statement that means something whichever way the page is read is written. A side the page
    /// said nothing about is the reader's to supply, and a keyword it named the side with is the page
    /// saying so itself: <c>initial</c> and <c>unset</c> take the side to nothing at all, and
    /// <c>revert</c> hands it back to the reader - the level that supplies these defaults in the first
    /// place, which is what it comes to.
    /// </remarks>
    private static StyleLength Side(
        StyleLength? named,
        CssWideKeyword? keyword,
        StyleLength? namedByAxis,
        CssWideKeyword? axisKeyword,
        StyleLength? supplied,
        StyleLength fallback)
    {
        if (axisKeyword is { } byAxis)
        {
            return Stands(byAxis, supplied);
        }

        if (namedByAxis is { } byAxisValue)
        {
            return byAxisValue;
        }

        if (keyword is { } byName)
        {
            return Stands(byName, supplied);
        }

        return named ?? supplied ?? fallback;
    }

    /// <summary>Gets what a keyword that stands for a value comes to on the side it named.</summary>
    private static StyleLength Stands(CssWideKeyword keyword, StyleLength? supplied) =>
        keyword.CssText.ToLowerInvariant() is "initial" or "unset"
            ? StyleLength.Zero
            : supplied ?? StyleLength.Zero;

    /// <summary>Gets the keyword the page named a property with, or null when it named a value.</summary>
    private CssWideKeyword? KeywordOf(String name) =>
        KeywordIn(_specified, name) ?? KeywordIn(_style, name);

    private static CssWideKeyword? KeywordIn(ICssStyleDeclaration? declaration, String name) =>
        declaration?.GetProperty(name)?.RawValue as CssWideKeyword;

    /// <summary>
    /// Gets the space HTML leaves around an element whose page says nothing about it, as the four
    /// flow-relative distances the reader supplies: the block axis is the space between blocks, and
    /// the inline axis the space along the line.
    /// </summary>
    /// <remarks>
    /// These are the values the CSS 2.2 sample stylesheet states, which is where a browser's defaults
    /// come from as well - stated to the flow rather than to the page, so that a column reads the
    /// space between blocks beside itself instead of down it. Nothing is returned for a padding, for
    /// an element HTML gives no margins to, or for a list inside a list, which the sample leaves to its
    /// outer one.
    /// </remarks>
    /// <summary>Gets whether a list sits inside another one, whose own margins the sample leaves it to.</summary>
    private static Boolean IsNested(IElement element) =>
        element.ParentElement?.LocalName is "ul" or "ol";

    private (StyleLength? BlockStart, StyleLength? BlockEnd, StyleLength? InlineStart, StyleLength? InlineEnd) Defaults(String prefix)
    {
        if (!String.Equals(prefix, "margin", StringComparison.Ordinal) || _element is not { } element)
        {
            return default;
        }

        var tag = element.LocalName.ToLowerInvariant();

        // The space between blocks, as a multiple of the element's own font size - which the font area
        // has already settled, this being read from the box model.
        var block = tag switch
        {
            "h1" => 0.67,
            "h2" => 0.75,
            "h3" => 0.83,
            "h4" => 1.12,
            "h5" => 1.5,
            "h6" => 1.67,
            "p" or "blockquote" or "dl" or "dir" or "menu" or "fieldset" or "form" => 1.12,
            "ul" or "ol" => IsNested(element) ? (Double?)null : 1.12,
            _ => null,
        };

        // The distance a list or a quotation is set in from the edge its lines begin at, which is the
        // one the sample states in pixels.
        var indent = tag is "ul" or "ol" or "dir" or "menu" or "dd" or "blockquote" ? 40.0 : (Double?)null;
        var indentEnd = tag == "blockquote" ? indent : null;
        var space = block is { } size ? size * _values.Font.Size : (Double?)null;

        return (
            Pixels(space),
            Pixels(space),
            Pixels(indent),
            Pixels(indentEnd));

        static StyleLength? Pixels(Double? value) =>
            value is { } pixels ? StyleLength.FromPixels(pixels) : null;
    }

    /// <summary>
    /// Reads how thick a side is. The caller has already resolved the side's style, so this does not
    /// ask for it again - which is what used to make four sides cost eight reads.
    /// </summary>
    private Double BorderWidth(String style, String widthName) =>
        BorderArea.Draws(style) ? Length(widthName, RenderMode.Horizontal, 0) : 0;

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

    private Double? Resolve(ICssValue value, RenderMode mode, LengthReader.FontSource font)
    {
        // A keyword that stands for no particular value - `inherit`, `initial`, `unset`, `revert` - is
        // not a length, and AngleSharp answers every one of them with zero when asked for pixels. Read
        // that way a `font: inherit` gave an element a font size of nothing at all, and every line of
        // its text no height either. A caller reads the absence as "the page said nothing here" and
        // falls back to what the property would have been - which for `inherit` is the value the
        // element was handed anyway.
        if (value is CssWideKeyword || (value is not CssLengthValue && CssWideKeyword.Is(value.CssText)))
        {
            return null;
        }

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
            return Resolve(length, mode, _dimensions, font);
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

    private static Double? Resolve(CssLengthValue value, RenderMode mode, IRenderDimensions dimensions, LengthReader.FontSource font)
    {
        try
        {
            var pixels = LengthReader.ToPixel(value, mode, dimensions, font);
            return Double.IsNaN(pixels) || Double.IsInfinity(pixels) ? null : pixels;
        }
        catch (Exception)
        {
            return null;
        }
    }
}

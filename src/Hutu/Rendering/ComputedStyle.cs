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
internal readonly struct ComputedStyle
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

    /// <summary>
    /// Builds a style from areas the fused box-builder path already resolved, with no
    /// declaration behind it: the accessors that would read one answer from the fused
    /// keywords instead.
    /// </summary>
    internal ComputedStyle(
        IRenderDimensions dimensions,
        IElement? element,
        FusedKeywords fused,
        StyleValues values,
        FontFaceTable? faces)
    {
        _style = null!;
        _specified = null;
        _dimensions = dimensions;
        _element = element;
        _parentFontSize = 0;
        _parentColor = null;
        _declaresFontSize = false;
        _faces = faces;
        values.Fused = fused;
        _values = values;
    }

    /// <summary>
    /// The style an anonymous box is given: this one, with every property that is not inherited taken
    /// off it.
    /// </summary>
    /// <remarks>
    /// CSS 2.1 §9.2.1.1 leaves an anonymous box at the initial value of every property that is not
    /// inherited. Margin, padding and border are not inherited, so an anonymous box has none of them:
    /// it takes exactly the place of the run of content it wraps, with nothing around it. Neither are
    /// <c>display</c>, <c>float</c>, <c>clear</c>, <c>overflow</c>, <c>outline</c> or <c>position</c>,
    /// so an anonymous box is a plain in-flow block that draws nothing of its own.
    /// <para>
    /// Handing the parent's style over wholesale hands it all of those too, and the damage is not
    /// theoretical: a body's eight-pixel margin is counted a second time inside the body, which pushes
    /// every line of text eight pixels in and eight pixels down from where it belongs; a parent's
    /// <c>position: relative</c> displaces a wrapper the parent itself already displaced; and a
    /// parent's <c>margin-bottom</c> swallows the top margin of the sibling that follows, because the
    /// two collapse into a margin the anonymous box was never meant to have.
    /// </para>
    /// <para>
    /// What <em>is</em> inherited travels on unchanged - the font, the text behaviour, the decorations
    /// and the list marker style - because a word inside an anonymous block is set the way a word
    /// beside it is, and the colours and background layers stay with the box that carries them.
    /// </para>
    /// </remarks>
    internal ComputedStyle AsAnonymous()
    {
        var values = new StyleValues
        {
            Border = BorderArea.Default,
            BoxModel = BoxModelArea.Default,
            DisplayPositioning = DisplayPositioningArea.Block,
            VisualEffects = VisualEffectsArea.Default,
            Font = _values.Font,
            Text = _values.Text,
            Decoration = _values.Decoration,
            List = _values.List,
            Position = PositionStyle.Static,

            // Orphans and widows are inherited, so a wrapper the page never wrote keeps them: losing them
            // here let an anonymous block's lines be split at a column edge that the block it wraps
            // forbade (CSS Break 3 §4.3).
            Breaks = _values.Breaks,
            Fused = _values.Fused,
        };

        return new ComputedStyle(_dimensions, _element, _values.Fused!, values, _faces);
    }

    /// <summary>The names a font property can begin with, the <c>font</c> shorthand included.</summary>
    private static readonly String[] FontNames =
        {
            "font-size", "font-family", "font-weight", "font-style", "letter-spacing", "word-spacing",
            "line-height",
        };

    /// <summary>
    /// The names a text property can begin with.
    /// </summary>
    /// <remarks>
    /// Every longhand the area holds has to be named here: the test asks whether this element authored
    /// none of them, and a property left out is a property whose declaration the area would throw away
    /// by taking the parent's whole area instead of resolving its own.
    /// </remarks>
    private static readonly String[] TextNames =
        {
            "white-space", "text-align", "text-align-last", "text-justify", "vertical-align",
            "text-indent", "visibility", "overflow-wrap", "word-wrap", "word-break", "line-break",
        };

    /// <summary>
    /// The names a list marker property can begin with. The three longhands, not the <c>list-style</c>
    /// shorthand: the shorthand is absent from a declaration that only ever states a longhand, and
    /// comparing that alone would take an item that picked its own type for the parent's.
    /// </summary>
    private static readonly String[] ListNames =
        { "list-style-type", "list-style-position", "list-style-image" };

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
            WordSpacing = Length("word-spacing", RenderMode.Horizontal, new LengthReader.FontSource(size), CssInitialValues.WordSpacing),
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

        var (align, alignLast) = Alignments(
            Keyword("text-align", CssInitialValues.TextAlign),
            Keyword("text-align-last", CssInitialValues.TextAlignLast),
            parent?.TextAlign,
            parent?.Direction ?? TextDirection.LeftToRight);

        var text = new TextArea
        {
            WhiteSpace = Keyword("white-space", CssInitialValues.WhiteSpace),
            OverflowWrap = Keyword("overflow-wrap", Keyword("word-wrap", CssInitialValues.OverflowWrap)),
            WordBreak = Keyword("word-break", CssInitialValues.WordBreak),
            LineBreak = Keyword("line-break", CssInitialValues.LineBreak),
            Align = align,
            AlignLast = alignLast,
            TextJustify = Keyword("text-justify", CssInitialValues.TextJustify),
            VerticalAlign = Keyword("vertical-align", CssInitialValues.VerticalAlign),
            Indent = ReadLength("text-indent", RenderMode.Horizontal, StyleLength.Zero),
            Visibility = Keyword("visibility", CssInitialValues.Visibility),
        };

        _values.Text = pool is null ? text : pool.Text(text);
    }

    /// <summary>
    /// Settles the two alignments a block's text is read with: <c>text-align</c> says how the lines stand
    /// and <c>text-align-last</c> how the affected ones do, and the one value that speaks for both is
    /// <c>justify-all</c> - every line justified, the last of them included (CSS Text 3 §6.1).
    /// </summary>
    /// <param name="matched">
    /// The alignment the parent came to, which <c>match-parent</c> asks for. Null where there is no parent
    /// to match: at the root the value computes to <c>start</c>.
    /// </param>
    internal static (String All, String Last) Alignments(
        String align,
        String alignLast,
        String? matched,
        TextDirection matchedDirection)
    {
        if (align.Equals("match-parent", StringComparison.OrdinalIgnoreCase))
        {
            align = matched is null ? CssInitialValues.TextAlign : MatchedEdge(matched, matchedDirection);
        }

        return align.Equals("justify-all", StringComparison.OrdinalIgnoreCase)
            ? ("justify", "justify")
            : (align, alignLast);
    }

    /// <summary>
    /// Gets the edge of the line a parent came to: <c>start</c> and <c>end</c> are read against the
    /// direction the parent's own lines ran in, and everything else is already an edge. This is the whole
    /// of what <c>match-parent</c> does beyond inherit (CSS Text 3 §6.1), and it is why the value survives
    /// a child that reads the other way.
    /// </summary>
    private static String MatchedEdge(String parentAlign, TextDirection parentDirection)
    {
        if (parentAlign.Equals("start", StringComparison.OrdinalIgnoreCase))
        {
            return parentDirection == TextDirection.RightToLeft ? "right" : "left";
        }

        if (parentAlign.Equals("end", StringComparison.OrdinalIgnoreCase))
        {
            return parentDirection == TextDirection.RightToLeft ? "left" : "right";
        }

        return parentAlign;
    }

    /// <summary>
    /// Resolves the list marker style, taking the parent's where the element authored none of it.
    /// </summary>
    /// <remarks>
    /// All three properties are inherited, so an item that says nothing about its marker is marked the
    /// way the list around it says - which is how a <c>ul</c> gets its discs and the <c>li</c> inside it
    /// never has to be told.
    /// </remarks>
    private void ResolveList(StyleIntern? pool, ComputedStyle? parent)
    {
        // The marker's look (type/position/image) is inherited, but the `list-item` counter operations are
        // not - a counter belongs to the item that declares it. So the look may be taken from the parent
        // unchanged, yet the counter fields are always recomputed from this element's own declarations
        // (null when it names none), so an item that inherits its bullet does not also inherit a
        // neighbour's `counter-set: list-item`.
        ListArea area;

        if (parent is { } inherited && Inherits(inherited, ListNames))
        {
            area = inherited._values.List with { ListItemReset = null, ListItemSet = null, ListItemIncrement = null };
        }
        else
        {
            // Read into a local because a struct's methods cannot reach `this` from inside a local function,
            // and the three resolvers below are handed one lookup rather than three reads.
            var declaration = _style;

            ICssProperty? Find(String name) => declaration.GetProperty(name);

            area = new ListArea
            {
                Type = ResolveListStyleTypeFused(Find(PropertyNames.ListStyleType), parent?._values.List.Type ?? "disc"),
                Position = ResolveMarkerPositionFused(Find(PropertyNames.ListStylePosition), parent?._values.List.Position ?? MarkerPosition.Outside),
                Image = ResolveListStyleImageFused(Find(PropertyNames.ListStyleImage), parent?._values.List.Image),
            };
        }

        _values.List = pool is null
            ? ApplyListItemCounters(area)
            : pool.List(ApplyListItemCounters(area));
    }

    /// <summary>
    /// Reads this element's own `list-item` counter operations out of the three counter properties, the
    /// only counter this step cares about, and lays them on the area. Absent means null: a `list-item`
    /// term in <c>counter-increment</c> with no number counts one, in <c>counter-reset</c> or
    /// <c>counter-set</c> it counts zero, the same defaults CSS gives a bare counter name.
    /// </summary>
    private ListArea ApplyListItemCounters(ListArea area) =>
        area with
        {
            ListItemReset = ReadListItemCounter("counter-reset"),
            ListItemSet = ReadListItemCounter("counter-set"),
            ListItemIncrement = ReadListItemCounter("counter-increment"),
        };

    /// <summary>
    /// Pulls the <c>list-item</c> term's integer out of a counter property's value, or null when the
    /// element never names <c>list-item</c> there.
    /// </summary>
    private Int32? ReadListItemCounter(String property)
    {
        var value = _style.GetPropertyValue(property);

        if (String.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var tokens = value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        for (var i = 0; i < tokens.Length; i++)
        {
            if (!tokens[i].Equals("list-item", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (i + 1 < tokens.Length &&
                Int32.TryParse(tokens[i + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var stated))
            {
                return stated;
            }

            return property == "counter-increment" ? 1 : 0;
        }

        return null;
    }

    /// <summary>
    /// Reads the <c>list-item</c> term out of one counter declaration for the fused path - the same scan
    /// <see cref="ReadListItemCounter"/> makes, off the element's own declared value. Null when the
    /// declaration never names <c>list-item</c>; <paramref name="bareDefault"/> is what a bare
    /// <c>list-item</c> (no number) means: zero to reset or set, one to increment.
    /// </summary>
    public static Int32? ResolveListItemCounterFused(ICssProperty? declared, Int32 bareDefault)
    {
        var value = declared?.Value;

        if (String.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var tokens = value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        for (var i = 0; i < tokens.Length; i++)
        {
            if (!tokens[i].Equals("list-item", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (i + 1 < tokens.Length &&
                Int32.TryParse(tokens[i + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var stated))
            {
                return stated;
            }

            return bareDefault;
        }

        return null;
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
            Overline = HasDecoration("overline"),
            LineThrough = HasDecoration("line-through"),
            Style = DecorationText.Shape(
                _style.GetPropertyValue("text-decoration-style"),
                _style.GetPropertyValue("text-decoration")),
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

    /// <summary>
    /// Resolves the font size from the element's own cascaded declaration, for the fused
    /// box-builder path that never merges the parent's declarations down.
    /// </summary>
    /// <remarks>
    /// Without the merge there is no declared-versus-inherited ambiguity to settle: a hit in
    /// the element's own cascade is declared, a miss is inherited. A relative hit resolves
    /// against the parent; a miss takes the parent's resolved size outright, which stops
    /// nested relative sizes from stacking. <c>inherit</c> and <c>unset</c> name the parent's
    /// size directly; <c>initial</c> is the medium size the device stands in for.
    /// </remarks>
    public static Double ResolveFontSizeFused(
        ICssProperty? declared, LengthReader.FontSource parent, IRenderDimensions dimensions)
    {
        var parentFontSize = parent.Value;

        if (declared?.RawValue is CssLengthValue relative && IsFontRelative(relative.Type))
        {
            return ResolveFontRelative(relative, parent, dimensions);
        }

        if (declared?.RawValue is { } raw)
        {
            if (IsInheritKeyword(raw))
            {
                return parentFontSize > 0 ? parentFontSize : HutuRenderDevice.InitialFontSize;
            }

            if (TryReadFontSizeValue(declared, dimensions, out var pixels))
            {
                return pixels;
            }

            // A keyword the reader cannot turn into a length - `medium`, `larger`, a name from a
            // font-size adjustment the engine does not model - starts from the size a reader does.
            return HutuRenderDevice.InitialFontSize;
        }

        return parentFontSize > 0 ? parentFontSize : HutuRenderDevice.InitialFontSize;
    }

    private static Boolean IsInheritKeyword(ICssValue value) =>
        (value is CssWideKeyword keyword &&
            IsInheritText(keyword.CssText)) ||
        (value is not CssLengthValue && IsInheritText(value.CssText));

    private static Boolean IsInheritText(String text)
    {
        var keyword = text.Trim();
        return keyword.Equals("inherit", StringComparison.OrdinalIgnoreCase) ||
               keyword.Equals("unset", StringComparison.OrdinalIgnoreCase);
    }

    private static Boolean TryReadFontSizeValue(ICssProperty declared, IRenderDimensions dimensions, out Double pixels) =>
        TryReadLengthValue(declared, RenderMode.Vertical, new LengthReader.FontSource(dimensions.FontSize), dimensions, out pixels);

    internal static Boolean TryReadLengthValue(
        ICssProperty declared,
        RenderMode mode,
        LengthReader.FontSource font,
        IRenderDimensions dimensions,
        out Double pixels)
    {
        pixels = 0;

        var text = declared.Value;

        if (!String.IsNullOrWhiteSpace(text))
        {
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

        var value = declared.RawValue;

        if (value is null || value.Is("auto") || value.Is("normal") || value.Is("none"))
        {
            return false;
        }

        var resolved = ResolveValue(value, mode, dimensions, font);

        if (resolved is null)
        {
            return false;
        }

        pixels = resolved.Value;
        return true;
    }

    /// <summary>
    /// Resolves the font weight from the element's own cascaded declaration, for the fused
    /// path. A miss takes the parent's resolved weight. The mapping copies
    /// <see cref="ResolveFontWeight"/> exactly: <c>bolder</c> and <c>lighter</c> are flat
    /// keywords here because the cascade leaves them unresolved.
    /// </summary>
    public static Int32 ResolveFontWeightFused(ICssProperty? declared, Int32 parentWeight)
    {
        if (declared is null)
        {
            return parentWeight;
        }

        // The `font` shorthand a reset hands every control reaches the cascade as its longhands,
        // each naming the parent's value rather than one of its own.
        if (IsInherit(declared))
        {
            return parentWeight;
        }

        var text = declared.Value?.Trim() ?? String.Empty;

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

    /// <summary>
    /// Resolves the font style from the element's own cascaded declaration, for the fused
    /// path. A miss takes the parent's resolved style.
    /// </summary>
    public static Boolean ResolveFontStyleFused(ICssProperty? declared, Boolean parentItalic)
    {
        if (declared is null)
        {
            return parentItalic;
        }

        if (IsInherit(declared))
        {
            return parentItalic;
        }

        var text = declared.Value?.Trim() ?? String.Empty;
        return text.Equals("italic", StringComparison.OrdinalIgnoreCase) ||
               text.Equals("oblique", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Resolves the line height from the element's own cascaded declaration, for the fused path, and
    /// from the declaration an ancestor stated when this element states none of its own.
    /// </summary>
    /// <remarks>
    /// The three forms inherit differently, and CSS 2.2 §10.8.1 is what tells them apart. A
    /// <c>&lt;number&gt;</c> computes to the number itself - "the used value is this number multiplied
    /// by the element's font size" - so it travels as a number and is multiplied again at every
    /// element that ends up using it. A percentage "computes to the specified value multiplied by the
    /// element's computed font size", which is the element that *declared* it: what reaches a descendant
    /// is the length it came to, and a descendant's own font size has nothing to do with it. A length
    /// and <c>normal</c> inherit as themselves, <c>normal</c> being measured against the fonts each
    /// element ends up using.
    /// </remarks>
    public static Double? ResolveLineHeightFused(
        ICssProperty? declared,
        ICssProperty? parentDeclared,
        LengthReader.FontSource font,
        Double? parentResolved,
        IRenderDimensions dimensions)
    {
        // `inherit` (and `unset`) is a wide keyword the cascade leaves as written: it asks for the
        // value the parent computed rather than one of its own, which is the answer the branch below
        // gives - the declaration behind that value is what is read again, not the value itself.
        if (declared is not null && !IsInherit(declared))
        {
            return ResolveLineHeightValue(declared, font, dimensions);
        }

        if (parentResolved is null)
        {
            // `normal`, which every element measures against its own fonts.
            return null;
        }

        var text = parentDeclared?.Value?.Trim();

        return !String.IsNullOrWhiteSpace(text) &&
            Double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var factor)
            ? factor * font.Value
            : parentResolved;
    }

    /// <summary>
    /// Resolves the white space handling from the element's own cascaded declaration, for the
    /// fused path. The property is inherited: a miss takes the parent's resolved value.
    /// </summary>
    public static String ResolveWhiteSpaceFused(ICssProperty? declared, String parentValue) =>
        ResolveKeywordFused(declared, parentValue, CssInitialValues.WhiteSpace, inherited: true);

    /// <summary>
    /// Resolves <c>overflow-wrap</c> from the element's own cascaded declarations, for the fused path.
    /// The property is inherited, and the legacy <c>word-wrap</c> spells the same thing: the modern name
    /// wins where a page wrote both, which is what the specification's own compatibility note says.
    /// </summary>
    public static String ResolveOverflowWrapFused(ICssProperty? declared, ICssProperty? legacy, String parentValue) =>
        ResolveKeywordFused(
            IsBlank(declared) ? legacy : declared, parentValue, CssInitialValues.OverflowWrap, inherited: true);

    /// <summary>Resolves <c>word-break</c>, which is inherited, for the fused path.</summary>
    public static String ResolveWordBreakFused(ICssProperty? declared, String parentValue) =>
        ResolveKeywordFused(declared, parentValue, CssInitialValues.WordBreak, inherited: true);

    /// <summary>Resolves <c>line-break</c>, which is inherited, for the fused path.</summary>
    public static String ResolveLineBreakFused(ICssProperty? declared, String parentValue) =>
        ResolveKeywordFused(declared, parentValue, CssInitialValues.LineBreak, inherited: true);

    /// <summary>Gets whether a property carries no declaration of the value asked for.</summary>
    private static Boolean IsBlank(ICssProperty? property) =>
        property is null || String.IsNullOrWhiteSpace(property.Value);

    /// <summary>
    /// Resolves whether a box draws itself from the element's own cascaded declaration, for the fused
    /// path. The property is inherited: a miss takes the parent's resolved value, which is what lets a
    /// hidden menu hide the hundred boxes inside it without any of them saying so.
    /// </summary>
    public static String ResolveVisibilityFused(ICssProperty? declared, String parentValue) =>
        ResolveKeywordFused(declared, parentValue, CssInitialValues.Visibility, inherited: true);

    /// <summary>
    /// Resolves the text alignment from the element's own cascaded declaration, for the fused
    /// path. The cascade treats the property as inherited (CSS 2.1 model): a miss takes the
    /// parent's resolved value.
    /// </summary>
    public static String ResolveTextAlignFused(
        ICssProperty? declared, String parentValue, String? hint = null, String? ownDefault = null)
    {
        if (!String.IsNullOrWhiteSpace(declared?.Value))
        {
            return declared!.Value!.Trim();
        }

        // A declaration the page is taken to have written - the align attribute among them - stands
        // above what the element would inherit.
        if (hint is not null)
        {
            return hint;
        }

        // A header's centring is the value it begins with, not a declaration of its own, which is why
        // the alignment a row above it states is handed down: measured in Chrome, a th under a row
        // set to the right computes right.
        if (ownDefault is not null && parentValue.Equals(CssInitialValues.TextAlign, StringComparison.OrdinalIgnoreCase))
        {
            return ownDefault;
        }

        return parentValue;
    }

    /// <summary>
    /// Resolves how the last line of a block, and a line a forced break ended, stand, for the fused
    /// path. The property is inherited: a miss takes the parent's resolved value, which is how a
    /// paragraph under a block that chose its last line's alignment keeps that choice.
    /// </summary>
    public static String ResolveTextAlignLastFused(ICssProperty? declared, String parentValue) =>
        ResolveKeywordFused(declared, parentValue, CssInitialValues.TextAlignLast, inherited: true);

    /// <summary>
    /// Resolves where a justified line may put its room, for the fused path. The property is inherited,
    /// and its initial value is a delegation rather than a method: <c>auto</c> is the sentence above
    /// spelled out by the specification, and the layout reads it as expanding at the seams of a script
    /// that writes no spaces.
    /// </summary>
    public static String ResolveTextJustifyFused(ICssProperty? declared, String parentValue) =>
        ResolveKeywordFused(declared, parentValue, CssInitialValues.TextJustify, inherited: true);

    /// <summary>
    /// Resolves the vertical alignment from the element's own cascaded declaration, for the
    /// fused path. The property is not inherited: a miss takes the initial value.
    /// </summary>
    /// <param name="parentValue">
    /// The parent's own resolved alignment, which is what <c>inherit</c> asks for. The property is
    /// not inherited, so this is the only way it can matter - and it is not rare: the user-agent
    /// sheet sets <c>vertical-align: inherit</c> on every row and cell, so a table is nothing but
    /// cells asking for the alignment of the group around them.
    /// </param>
    public static String ResolveVerticalAlignFused(ICssProperty? declared, String parentValue, String? hint = null)
    {
        // The user-agent sheet gives every row and cell `vertical-align: inherit`, so a cell that says
        // nothing of its own still has a declaration to read. The legacy attribute is a declaration the
        // page is taken to have written, and it stands above that one.
        if (hint is not null && (String.IsNullOrWhiteSpace(declared?.Value) ||
            declared!.Value!.Trim().Equals("inherit", StringComparison.OrdinalIgnoreCase)))
        {
            return hint;
        }

        var text = ResolveKeywordFused(
            declared, CssInitialValues.VerticalAlign, CssInitialValues.VerticalAlign, inherited: false);

        // Every other keyword stays as written, `initial` and `unset` among them, because that is
        // what the old path reads and matching it is what keeps the two trees identical. `inherit`
        // is the exception: the cascade leaves it unresolved but the computed declaration behind the
        // old path has already folded it in - a `td` under a `tbody` reads `middle`, never `inherit`.
        return text.Equals("inherit", StringComparison.OrdinalIgnoreCase) ? parentValue : text;
    }

    /// <summary>
    /// Resolves what a list item is marked with, from the element's own cascaded declaration, for the
    /// fused path. The property is inherited, so a miss takes the parent's resolved name - which is
    /// also how the <c>disc</c> and <c>decimal</c> the user-agent sheet states on the list reach the
    /// items below it: the cascade never puts them on the item, since no rule speaks of it there.
    /// </summary>
    /// <remarks>
    /// The name is kept as written, wide keywords and all, which is what <see cref="ResolveKeywordFused"/>
    /// does for the other inherited keywords and what keeps the two build paths reading the same value.
    /// A name no counter style answers for - <c>inherit</c> among them - then spells no marker rather
    /// than guessing at one.
    /// </remarks>
    public static String ResolveListStyleTypeFused(ICssProperty? declared, String parent)
    {
        var text = declared?.Value?.Trim();

        return String.IsNullOrWhiteSpace(text) ? parent : text!;
    }

    /// <summary>
    /// Resolves where an item's marker stands, for the fused path. Inherited, so a miss takes the
    /// parent's answer.
    /// </summary>
    public static MarkerPosition ResolveMarkerPositionFused(ICssProperty? declared, MarkerPosition parent)
    {
        if (declared is null)
        {
            return parent;
        }

        var text = declared.Value?.Trim();

        if (String.IsNullOrWhiteSpace(text))
        {
            return parent;
        }

        return text.Equals("inside", StringComparison.OrdinalIgnoreCase)
            ? MarkerPosition.Inside
            : text.Equals("outside", StringComparison.OrdinalIgnoreCase)
                ? MarkerPosition.Outside
                : parent;
    }

    /// <summary>
    /// Resolves the image an item is marked with, for the fused path. The reference is kept as written:
    /// resolving it is the document's work, not the style's, and <c>none</c> and a bare keyword both
    /// answer as no image.
    /// </summary>
    public static String? ResolveListStyleImageFused(ICssProperty? declared, String? parent)
    {
        if (declared is null)
        {
            return parent;
        }

        var text = declared.Value?.Trim();

        // The property's initial value is no image at all, which is what the wide keywords that fall back
        // to it answer with too - including the mark a `list-style` shorthand leaves on the image longhand
        // it never filled. Only `inherit` reaches past the box for another element's picture.
        if (String.IsNullOrWhiteSpace(text) ||
            text.Equals("none", StringComparison.OrdinalIgnoreCase) ||
            text.Equals("initial", StringComparison.OrdinalIgnoreCase) ||
            text.Equals("unset", StringComparison.OrdinalIgnoreCase) ||
            text.Equals("revert", StringComparison.OrdinalIgnoreCase) ||
            text.Equals("revert-layer", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return text.Equals("inherit", StringComparison.OrdinalIgnoreCase) ? parent : text;
    }

    private static String ResolveKeywordFused(ICssProperty? declared, String parentValue, String initial, Boolean inherited)
    {
        // Verbatim, wide keywords included: the cascade leaves `inherit` and friends
        // unresolved on keyword properties, and the old path reads that text as the value.
        // Matching it exactly is what keeps the box trees identical; reinterpreting the
        // keywords is a behaviour change with its own tests, not part of this rewrite.
        var text = declared?.Value?.Trim();

        if (String.IsNullOrWhiteSpace(text))
        {
            return declared is null && inherited ? parentValue : initial;
        }

        return text!;
    }

    /// <summary>
    /// Resolves the first-line indent from the element's own cascaded declaration, for the
    /// fused path. The cascade treats the property as inherited (CSS 2.1 model): a miss takes
    /// the parent's resolved length. Percentages stay unresolved for the layout pass, as in
    /// the box-model read.
    /// </summary>
    public static StyleLength ResolveTextIndentFused(
        ICssProperty? declared, StyleLength parentIndent, LengthReader.FontSource font, IRenderDimensions dimensions)
    {
        if (declared is null)
        {
            return parentIndent;
        }

        // Wide keywords fall through to the read below and resolve to zero there, exactly as
        // the old path reads them: the cascade leaves them unresolved and the length reader
        // answers every one of them with nothing.
        return LengthReader.TryReadProperty(declared, RenderMode.Horizontal, dimensions, font, out var length)
            ? length
            : StyleLength.Zero;
    }

    /// <summary>
    /// The axis name each physical side of the box goes by, in the order a thickness is read - top,
    /// right, bottom, left.
    /// </summary>
    /// <remarks>
    /// A sheet written for a vertical book, or for a line that reads right to left, states a side by the
    /// axis it stands on: the foot of a page is the head of a column. Nothing in AngleSharp folds one
    /// name onto the other, so a border written <c>border-block-end</c> reaches no side at all and an
    /// axis drawn that way is simply missing. Which side an axis name lands on is the element's own flow,
    /// and where a page names a side both ways the axis is the statement it meant - the order the
    /// thicknesses and the sizes of this engine already keep (CSS Logical 1 §4).
    /// </remarks>
    public static (String Top, String Right, String Bottom, String Left) AxisSides(WritingModeInfo flow)
    {
        var (inlineStart, inlineEnd) = flow.InlineSign > 0
            ? ("inline-start", "inline-end")
            : ("inline-end", "inline-start");

        if (!flow.IsVertical)
        {
            // The line runs across the page, so the block axis is the vertical pair, and it begins at the
            // top whichever way the line itself reads.
            return ("block-start", inlineEnd, "block-end", inlineStart);
        }

        var (blockStart, blockEnd) = flow.BlockSign < 0
            ? ("block-start", "block-end")
            : ("block-end", "block-start");

        return (inlineStart, blockStart, inlineEnd, blockEnd);
    }

    /// <summary>
    /// Gets the declaration that named one side of the border, under the name of its axis where the page
    /// used that one.
    /// </summary>
    public static ICssProperty? BorderSideDeclaration(
        Func<String, ICssProperty?> find,
        String side,
        String physical,
        String channel) =>
        find("border-" + side + "-" + channel) ?? find("border-" + physical + "-" + channel);

    /// <summary>
    /// Resolves the four border styles from the element's own cascaded declarations, for the
    /// fused path. Borders are not inherited: a miss takes the initial value, verbatim -
    /// wide keywords included, as the old path reads them.
    /// </summary>
    public static (String Top, String Right, String Bottom, String Left) ResolveBorderStylesFused(
        Func<String, ICssProperty?> find, IElement? element, WritingModeInfo flow)
    {
        // The style a legacy border attribute asks for - a bevel, or the flat line a frame or a rule
        // draws - stands in on every side the page left unwritten, and on those sides only: a rule that
        // speaks of one side of the border takes that side back whatever the attributes say.
        var hinted = PresentationalHints.Border(element);
        var sides = AxisSides(flow);

        return
        (
            KeywordText(BorderSideDeclaration(find, sides.Top, "top", "style"), CssInitialValues.BorderStyle, hinted?.TopStyle),
            KeywordText(BorderSideDeclaration(find, sides.Right, "right", "style"), CssInitialValues.BorderStyle, hinted?.RightStyle),
            KeywordText(BorderSideDeclaration(find, sides.Bottom, "bottom", "style"), CssInitialValues.BorderStyle, hinted?.BottomStyle),
            KeywordText(BorderSideDeclaration(find, sides.Left, "left", "style"), CssInitialValues.BorderStyle, hinted?.LeftStyle)
        );
    }

    private static String KeywordText(ICssProperty? declared, String initial, String? fallback = null)
    {
        var text = declared?.Value?.Trim();

        if (!String.IsNullOrWhiteSpace(text))
        {
            return text!;
        }

        // A declaration that says nothing - the cascade hands back its placeholder for a property no rule
        // wrote - is where the hint stands, and a hint of no style at all is the initial one.
        return fallback ?? initial;
    }

    /// <summary>
    /// Resolves the four border widths from the element's own cascaded declarations, for the
    /// fused path. A side whose style draws nothing is zero without reading further, as in
    /// the old path.
    /// </summary>
    public static (Double Top, Double Right, Double Bottom, Double Left) ResolveBorderWidthsFused(
        Func<String, ICssProperty?> find,
        (String Top, String Right, String Bottom, String Left) styles,
        LengthReader.FontSource font,
        IRenderDimensions dimensions,
        BorderArea? parent,
        PresentationalHints.Ring? hint,
        WritingModeInfo flow)
    {
        var sides = AxisSides(flow);

        return
        (
            BorderWidthFused(styles.Top, BorderSideDeclaration(find, sides.Top, "top", "width"), font, dimensions, parent?.TopWidth, hint?.Top),
            BorderWidthFused(styles.Right, BorderSideDeclaration(find, sides.Right, "right", "width"), font, dimensions, parent?.RightWidth, hint?.Right),
            BorderWidthFused(styles.Bottom, BorderSideDeclaration(find, sides.Bottom, "bottom", "width"), font, dimensions, parent?.BottomWidth, hint?.Bottom),
            BorderWidthFused(styles.Left, BorderSideDeclaration(find, sides.Left, "left", "width"), font, dimensions, parent?.LeftWidth, hint?.Left)
        );
    }

    private static Double BorderWidthFused(
        String style,
        ICssProperty? declared,
        LengthReader.FontSource font,
        IRenderDimensions dimensions,
        Double? parentWidth,
        Double? hint)
    {
        if (!BorderArea.Draws(style))
        {
            return 0;
        }

        // `inherit` takes the parent's computed width, which was resolved to pixels at the parent's own
        // font size - and that is the point of the keyword: a child set to a larger font does not read
        // the parent's `1em` again as its own bigger em, it takes the length the parent came to. Only a
        // plain `inherit` behaves so; `unset` on this non-inherited property falls to the initial medium,
        // as every value the property itself cannot take does.
        if (parentWidth is { } inherited && DeclaresInherit(declared))
        {
            return inherited;
        }

        if (declared is not null &&
            TryReadLengthValue(declared, RenderMode.Horizontal, font, dimensions, out var pixels))
        {
            return pixels;
        }

        // No width declared: the thickness the legacy border attribute asked for stands in, and a table
        // that wrote none leaves the computed declaration's initial medium, which the old path resolves
        // the same way. Pinned by the differential test.
        return hint ?? 3;
    }

    /// <summary>
    /// Gets whether a box-model declaration is the bare <c>inherit</c> keyword, which hands the side the
    /// parent's computed value rather than a length or the property's own initial.
    /// </summary>
    private static Boolean DeclaresInherit(ICssProperty? declared) => declared is not null &&
        (declared.RawValue is CssWideKeyword wide
            ? wide.CssText.Equals("inherit", StringComparison.OrdinalIgnoreCase)
            : declared.Value?.Trim().Equals("inherit", StringComparison.OrdinalIgnoreCase) == true);

    /// <summary>
    /// Resolves the corner radii from the element's own cascaded declarations, for the fused
    /// path. Percentages stay unresolved for the layout pass.
    /// </summary>
    public static StyleCornerRadii ResolveBorderRadiusFused(
        Func<String, ICssProperty?> find, LengthReader.FontSource font, IRenderDimensions dimensions) => new(
        LengthReader.TryReadCornerValue(find("border-top-left-radius")?.RawValue, dimensions, font) ?? StyleCornerRadius.Zero,
        LengthReader.TryReadCornerValue(find("border-top-right-radius")?.RawValue, dimensions, font) ?? StyleCornerRadius.Zero,
        LengthReader.TryReadCornerValue(find("border-bottom-right-radius")?.RawValue, dimensions, font) ?? StyleCornerRadius.Zero,
        LengthReader.TryReadCornerValue(find("border-bottom-left-radius")?.RawValue, dimensions, font) ?? StyleCornerRadius.Zero);

    /// <summary>
    /// Maps the four border style keywords to the strokes the painter draws, for the fused path.
    /// </summary>
    public static BorderStyles ResolveBorderLinesFused((String Top, String Right, String Bottom, String Left) styles) => new(
        ToLineStyle(styles.Top),
        ToLineStyle(styles.Right),
        ToLineStyle(styles.Bottom),
        ToLineStyle(styles.Left));

    /// <summary>
    /// Resolves the display keyword from the element's own cascaded declaration, for the fused
    /// path. Display is not inherited: a miss takes the initial value, verbatim.
    /// </summary>
    public static String ResolveDisplayFused(ICssProperty? declared) =>
        KeywordText(declared, CssInitialValues.Display);

    /// <summary>
    /// Resolves which edge a box floats to from the element's own cascaded declaration, for the
    /// fused path. Neither the keyword nor a keyword this engine does not place maps to anything
    /// but staying in flow.
    /// </summary>
    public static Layout.FloatSide ResolveFloatFused(ICssProperty? declared, Layout.FloatSide? hint = null) =>
        declared is null && hint is { } hinted
            ? hinted
            : ToFloat(KeywordText(declared, CssInitialValues.Float));

    /// <summary>Resolves which side's floats a box clears, for the fused path.</summary>
    public static Layout.ClearSide ResolveClearFused(ICssProperty? declared) =>
        ToClear(KeywordText(declared, CssInitialValues.Clear));

    /// <summary>
    /// Maps the <c>float</c> keyword. The logical spellings are kept as logical sides: which edge the
    /// line begins at is the writing mode's answer, and only the layout knows it.
    /// </summary>
    private static Layout.FloatSide ToFloat(String text) =>
        text.Trim().ToLowerInvariant() switch
        {
            "left" => Layout.FloatSide.Left,
            "right" => Layout.FloatSide.Right,
            "inline-start" => Layout.FloatSide.InlineStart,
            "inline-end" => Layout.FloatSide.InlineEnd,
            _ => Layout.FloatSide.None,
        };

    /// <summary>Maps the <c>clear</c> keyword, with the same rule about the logical spellings.</summary>
    private static Layout.ClearSide ToClear(String text) =>
        text.Trim().ToLowerInvariant() switch
        {
            "left" => Layout.ClearSide.Left,
            "right" => Layout.ClearSide.Right,
            "both" => Layout.ClearSide.Both,
            _ => Layout.ClearSide.None,
        };

    /// <summary>
    /// Resolves whether either overflow axis clips, from the keywords the two axes come to, for the fused
    /// path. A miss takes visible.
    /// </summary>
    public static Boolean ResolveClipsOverflowFused(String? x, String? y) =>
        !IsOverflowVisibleText(x) || !IsOverflowVisibleText(y);

    /// <summary>
    /// Which axes are scroll axes (hidden/scroll/auto) for the automatic minimum size of a flex or grid
    /// item. <c>clip</c> is deliberately excluded - it is not a scroll container, and behaves like
    /// <c>visible</c> for sizing (CSS Overflow 3, and the WPT <c>min-size-auto-overflow-clip</c>).
    /// </summary>
    public static (Boolean Inline, Boolean Block) ResolveScrollsFused(String? x, String? y) =>
        (IsScrollOverflowText(x), IsScrollOverflowText(y));

    /// <summary>
    /// Gets whether a cascaded overflow value is a keyword that stands for another value rather than
    /// naming one, which is the moment the two axes have to be read from the computed style instead.
    /// </summary>
    private static Boolean IsOverflowWideKeyword(String? text) =>
        text is not null && CssWideKeyword.Is(text.Trim());

    private static Boolean IsScrollOverflowText(String? text) =>
        text is not null &&
        (text.Trim().Equals("hidden", StringComparison.OrdinalIgnoreCase) ||
         text.Trim().Equals("scroll", StringComparison.OrdinalIgnoreCase) ||
         text.Trim().Equals("auto", StringComparison.OrdinalIgnoreCase));

    private static Boolean IsOverflowVisibleText(String? text) =>
        String.IsNullOrWhiteSpace(text) || text.Trim().Equals("visible", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Resolves the object fit from the element's own cascaded declaration, for the fused path.
    /// </summary>
    public static Layout.ObjectFit ResolveObjectFitFused(ICssProperty? declared) =>
        declared?.Value?.Trim().ToLowerInvariant() switch
        {
            "contain" => Layout.ObjectFit.Contain,
            "cover" => Layout.ObjectFit.Cover,
            "none" => Layout.ObjectFit.None,
            "scale-down" => Layout.ObjectFit.ScaleDown,
            _ => Layout.ObjectFit.Fill,
        };

    /// <summary>
    /// Resolves the direction from the element's own cascaded declaration, for the fused path.
    /// The <c>dir</c> attribute stands in when no rule declares the property; the property is
    /// inherited, so a miss otherwise takes the parent's resolved direction.
    /// </summary>
    public static TextDirection ResolveDirectionFused(IElement? element, ICssProperty? declared, TextDirection parent)
    {
        if (DirectionAttribute(element) is { } attribute && declared is null)
        {
            return attribute;
        }

        if (declared is null)
        {
            return parent;
        }

        // Any text but `rtl` reads left to right, as in the old path.
        return String.Equals(declared.Value?.Trim(), "rtl", StringComparison.OrdinalIgnoreCase)
            ? TextDirection.RightToLeft
            : TextDirection.LeftToRight;
    }

    private static TextDirection? DirectionAttribute(IElement? element)
    {
        for (var current = element; current is not null; current = current.ParentElement)
        {
            var dir = current.GetAttribute("dir")?.Trim();

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
    /// Resolves the writing mode from the element's own cascaded declaration, for the fused
    /// path. The property is inherited: a miss takes the parent's resolved mode.
    /// </summary>
    public static Layout.WritingMode ResolveWritingModeFused(ICssProperty? declared, Layout.WritingMode parent) =>
        declared?.Value?.Trim().ToLowerInvariant() switch
        {
            "vertical-rl" => Layout.WritingMode.VerticalRl,
            "vertical-lr" => Layout.WritingMode.VerticalLr,
            "sideways-rl" => Layout.WritingMode.SidewaysRl,
            "sideways-lr" => Layout.WritingMode.SidewaysLr,
            "horizontal-tb" => Layout.WritingMode.HorizontalTb,
            _ => declared is null ? parent : Layout.WritingMode.HorizontalTb,
        };

    /// <summary>
    /// Resolves the declared text orientation, for the fused path. The property is inherited:
    /// a miss takes the parent's.
    /// </summary>
    public static TextOrientation? ResolveTextOrientationFused(ICssProperty? declared, TextOrientation? parent) =>
        declared?.Value?.Trim().ToLowerInvariant() switch
        {
            "mixed" => TextOrientation.Mixed,
            "upright" => TextOrientation.Upright,
            "sideways" => TextOrientation.Sideways,
            _ => declared is null ? parent : null,
        };

    /// <summary>
    /// Resolves the declared upright combining, for the fused path. The property is
    /// inherited: a miss takes the parent's.
    /// </summary>
    public static Boolean? ResolveTextCombineFused(ICssProperty? declared, Boolean? parent) =>
        declared?.Value?.Trim().ToLowerInvariant() switch
        {
            "all" => true,
            "none" => false,
            _ => declared is null ? parent : null,
        };

    /// <summary>
    /// Resolves the outline style from the element's own cascaded declaration, for the fused path.
    /// </summary>
    public static BorderLineStyle ResolveOutlineStyleFused(ICssProperty? declared)
    {
        var style = ToLineStyle(KeywordText(declared, CssInitialValues.OutlineStyle));

        // An outline is one ring with no sides to bevel, so a bevelled keyword falls back to solid for
        // it even though a border draws the shade - a stroke the outline cannot express still shows.
        return BorderStyles.IsBevel(style) ? BorderLineStyle.Solid : style;
    }

    /// <summary>
    /// Resolves the outline width from the element's own cascaded declaration, for the fused
    /// path. No stroke means no width; no width declared means medium.
    /// </summary>
    public static Double ResolveOutlineWidthFused(
        BorderLineStyle style, ICssProperty? declared, LengthReader.FontSource font, IRenderDimensions dimensions)
    {
        if (style == BorderLineStyle.None)
        {
            return 0;
        }

        if (declared is not null &&
            TryReadLengthValue(declared, RenderMode.Horizontal, font, dimensions, out var pixels))
        {
            return pixels;
        }

        return CssLengthValue.Medium.Value;
    }

    /// <summary>
    /// Resolves the outline offset from the element's own cascaded declaration, for the fused path.
    /// </summary>
    public static Double ResolveOutlineOffsetFused(
        ICssProperty? declared, LengthReader.FontSource font, IRenderDimensions dimensions)
    {
        if (declared is not null &&
            TryReadLengthValue(declared, RenderMode.Horizontal, font, dimensions, out var pixels))
        {
            return pixels;
        }

        return CssInitialValues.OutlineOffset;
    }

    /// <summary>
    /// Resolves the text colour from the element's own cascaded declaration, for the fused
    /// path. Links without their own colour take the link colour; anything else without one
    /// takes the parent's resolved colour, which is also what <c>currentcolor</c> writes.
    /// </summary>
    public static CssColorValue ResolveColorFused(
        IElement? element, ICssProperty? declared, CssColorValue? parentColor)
    {
        if (declared is null)
        {
            // A link asks for nothing from its parent: the colour it is drawn in is the engine's own
            // answer, so it is only read once it is known that nothing was declared here. Anything else
            // takes the colour the body's legacy <c>text</c> attribute hints at where it writes one, and
            // the parent's colour otherwise.
            return IsFollowableLink(element)
                ? ResolveLinkColor(element)
                : PresentationalHints.TextColor(element) ?? parentColor ?? CssColorValue.Black;
        }

        // A wide keyword stands for a value: `inherit`/`unset`/`revert` name the parent's
        // colour, `initial` the initial one. The cascade resolves these; the fused path must
        // too, since it reads the declaration as written.
        if (declared.RawValue is { } raw && (IsInheritKeyword(raw) || IsCurrentColorKeyword(raw)))
        {
            return parentColor ?? CssColorValue.Black;
        }

        return declared.RawValue is CssColorValue color ? color : CssColorValue.Black;
    }

    /// <summary>
    /// Gets whether a declared value is the <c>currentcolor</c> keyword. On the colour property that
    /// keyword cannot stand for the element's own colour, which is what it would then be defined by:
    /// CSS Color 4 §4 gives its used value there as the resolved inherited one.
    /// </summary>
    private static Boolean IsCurrentColorKeyword(ICssValue value) =>
        (value is CssColorValue ink && ink == CssColorValue.CurrentColor) ||
        (value is not CssLengthValue &&
         value.CssText.Trim().Equals("currentcolor", StringComparison.OrdinalIgnoreCase));

    private static Boolean IsFollowableLink(IElement? element) =>
        element is IHtmlAnchorElement anchor && anchor.HasAttribute("href");

    /// <summary>
    /// Resolves a border colour from the element's own cascaded declaration, for the fused
    /// path. A miss takes nothing: null is the signal the painter reads the text colour for.
    /// An explicit currentcolor takes the element's own resolved colour.
    /// </summary>
    public static CssColorValue? ResolveBorderColorFused(
        ICssProperty? declared, CssColorValue ownColor) =>
        declared?.RawValue is not CssColorValue color
            ? null
            : color == CssColorValue.CurrentColor ? ownColor : color;

    /// <summary>
    /// Resolves the background colour from the element's own cascaded declaration, for the
    /// fused path. A miss takes the colour a legacy <c>bgcolor</c> attribute hints at, and nothing
    /// where the element writes neither.
    /// </summary>
    /// <remarks>
    /// A background is not an inherited property, so <c>unset</c> and <c>initial</c> are the property's
    /// own first value - nothing painted - where <c>inherit</c> is the box above, carried down as the
    /// value it was cascaded rather than the colour it was painted with: a <c>currentcolor</c> handed
    /// down that way stays the keyword, and each box answers for its own ink (CSS Color 4 5).
    /// </remarks>
    public static CssColorValue? ResolveBackgroundColorFused(
        IElement? element, ICssProperty? declared, CssColorValue? parentBackground = null)
    {
        if (declared is null)
        {
            return PresentationalHints.BackgroundColor(element);
        }

        if (declared.RawValue is CssColorValue color)
        {
            return color;
        }

        return IsInheritWord(declared.RawValue) ? parentBackground : null;
    }

    private static Boolean IsInheritWord(ICssValue? value) =>
        value is not null && value.CssText.Trim().Equals("inherit", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Resolves the text decoration flags from the element's own cascaded declarations, for
    /// the fused path.
    /// </summary>
    internal static TextDecorationArea ResolveDecorationFused(
        Func<String, ICssProperty?> find,
        Double? parentUnderlineOffset,
        DecorationSkip parentSkip,
        LengthReader.FontSource font,
        IRenderDimensions dimensions)
    {
        var line = find("text-decoration-line")?.Value;
        var shorthand = find("text-decoration")?.Value;

        return new TextDecorationArea
        {
            Underline = DecorationText.Names(line, shorthand, "underline"),
            Overline = DecorationText.Names(line, shorthand, "overline"),
            LineThrough = DecorationText.Names(line, shorthand, "line-through"),
            Style = DecorationText.Shape(find("text-decoration-style")?.Value, shorthand),

            // The cascade expands the shorthand into its longhands, so the colour the shorthand carries
            // is read from the same place the line and the shape are.
            Color = find("text-decoration-color")?.RawValue is CssColorValue { } color ? ToArgb(color) : null,
            Thickness = ResolveDecorationThicknessFused(find("text-decoration-thickness"), font, dimensions),
            UnderlineOffset = ResolveUnderlineOffsetFused(
                find("text-underline-offset"), parentUnderlineOffset, font, dimensions),
            Inset = ResolveDecorationInsetFused(
                find("text-decoration-inset"), find("box-decoration-break"), font, dimensions),
            Skip = ResolveSkipInkFused(find("text-decoration-skip-ink"), parentSkip),
        };
    }

    /// <summary>
    /// Resolves how thick a decoration line is, for the fused path. The property is not inherited: a miss
    /// says nothing, and the face's own metrics are what the line then takes - which is also what
    /// <c>auto</c> and <c>from-font</c> ask for, since this engine reads its metrics from the face
    /// (CSS Text Decoration 4 §2.4).
    /// </summary>
    public static Double? ResolveDecorationThicknessFused(
        ICssProperty? declared, LengthReader.FontSource font, IRenderDimensions dimensions) =>
        DecorationLength(declared, font, dimensions) is { } asked ? Math.Round(asked) : null;

    /// <summary>
    /// Resolves how far an underline stands from where the font puts it, for the fused path. The property
    /// is inherited, so a miss takes the distance the parent came to; <c>auto</c> states no distance of
    /// its own and answers as nothing was said, which is what lets the face speak (CSS Text Decoration 4
    /// §2.8).
    /// </summary>
    public static Double? ResolveUnderlineOffsetFused(
        ICssProperty? declared, Double? parent, LengthReader.FontSource font, IRenderDimensions dimensions) =>
        declared is null || IsInherit(declared) ? parent : DecorationLength(declared, font, dimensions);

    /// <summary>
    /// Resolves whether a line is broken where the ink crosses it, for the fused path. The property is
    /// inherited, so a miss takes the answer standing above it; the initial answer is <c>auto</c>, which
    /// is the engine's own rather than a straight line (§2.10.4).
    /// </summary>
    public static DecorationSkip ResolveSkipInkFused(ICssProperty? declared, DecorationSkip parent) =>
        declared?.Value?.Trim() switch
        {
            "none" => DecorationSkip.None,
            "all" => DecorationSkip.All,
            "auto" => DecorationSkip.Auto,
            _ => parent,
        };

    /// <summary>
    /// Reads how far back the ends of the lines this element asks for stand. A length is taken in pixels
    /// and a percentage is kept as a share, because what a share measures here is the inline size of the
    /// text the decoration runs across - not the size the text is set at, which is the meaning the other
    /// two decoration lengths give it (§2.9.1). One value speaks for both ends and two give the start then
    /// the end; <c>auto</c> leaves the distance to the engine, and anything the grammar cannot read leaves
    /// the line to run to the edges it was given, which is the initial zero. Whether the box repeats its
    /// decoration on every line it reaches comes from <c>box-decoration-break</c>, and it is what decides
    /// the size a share is taken of.
    /// </summary>
    public static DecorationInsets? ResolveDecorationInsetFused(
        ICssProperty? declared,
        ICssProperty? broken,
        LengthReader.FontSource font,
        IRenderDimensions dimensions)
    {
        var clone = broken?.Value?.IndexOf("clone", StringComparison.OrdinalIgnoreCase) >= 0;
        var text = declared?.Value?.Trim();

        if (String.IsNullOrEmpty(text) || IsInherit(declared))
        {
            return clone ? new DecorationInsets(null, null, null, null, false, true) : null;
        }

        var terms = InsetTerms(text!);

        if (terms.Count == 0 || terms.Count > 2)
        {
            return null;
        }

        if (terms.Count == 1 && terms[0].Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            return new DecorationInsets(null, null, null, null, true, clone);
        }

        var (start, startShare) = ReadInsetTerm(terms[0], font, dimensions);
        var (end, endShare) = terms.Count > 1
            ? ReadInsetTerm(terms[1], font, dimensions)
            : (start, startShare);

        if (start is null && end is null && startShare is null && endShare is null)
        {
            return null;
        }

        return new DecorationInsets(start, end, startShare, endShare, false, clone);
    }

    /// <summary>
    /// Splits what was written into its values, keeping the spaces inside a <c>calc()</c> part of the one
    /// term they belong to.
    /// </summary>
    private static List<String> InsetTerms(String text)
    {
        var terms = new List<String>();
        var from = 0;
        var depth = 0;

        for (var index = 0; index <= text.Length; index++)
        {
            var at = index < text.Length ? text[index] : ' ';

            if (at == '(')
            {
                depth++;
            }
            else if (at == ')')
            {
                depth = Math.Max(0, depth - 1);
            }

            if (depth == 0 && (index == text.Length || Char.IsWhiteSpace(at) || at == ','))
            {
                if (index > from)
                {
                    terms.Add(text[from..index]);
                }

                from = index + 1;
            }
        }

        return terms;
    }

    /// <summary>
    /// Reads one end of the inset: a share where the term is a percentage, and a distance in pixels
    /// otherwise. A share standing inside a <c>calc()</c> is left unread, since the size it would be a
    /// share of is not known while the style is being resolved.
    /// </summary>
    private static (Double? Pixels, Double? Share) ReadInsetTerm(
        String term, LengthReader.FontSource font, IRenderDimensions dimensions)
    {
        if (CssLengthValue.TryParse(term, out var share) && share.Type == CssLengthValue.Unit.Percent)
        {
            return (null, share.Value / 100);
        }

        if (CssMathLengths.Evaluate(term, argument => InsetTerm(argument, font, dimensions)) is { } value)
        {
            return (value, null);
        }

        return CssLengthValue.TryParse(term, out var length) && InsetLength(length, font, dimensions) is { } pixels
            ? (pixels, null)
            : (null, null);
    }

    private static Double? InsetTerm(String term, LengthReader.FontSource font, IRenderDimensions dimensions) =>
        CssLengthValue.TryParse(term, out var length) && length.Type != CssLengthValue.Unit.Percent
            ? InsetLength(length, font, dimensions)
            : null;

    private static Double? InsetLength(CssLengthValue length, LengthReader.FontSource font, IRenderDimensions dimensions)
    {
        try
        {
            var pixels = LengthReader.ToPixel(length, RenderMode.Horizontal, dimensions, font);
            return Double.IsNaN(pixels) || Double.IsInfinity(pixels) ? null : pixels;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Reads a length of a decoration, with the one unit these two properties define for themselves: a
    /// percentage is a share of the size the text is set at, not of the box it sits in - and a share holds
    /// that meaning inside <c>calc()</c> as well, where <c>calc(50% + 4px)</c> is the same rule as
    /// <c>50%</c> (CSS Text Decoration 4 §2.4, §2.8). A keyword - <c>auto</c>, <c>from-font</c>,
    /// <c>normal</c> - says nothing, which is what leaves the line to the face.
    /// </summary>
    private static Double? DecorationLength(ICssProperty? declared, LengthReader.FontSource font, IRenderDimensions dimensions)
    {
        if (declared is null)
        {
            return null;
        }

        if (declared.RawValue is CssLengthValue { Type: CssLengthValue.Unit.Percent } share)
        {
            return (share.Value / 100) * font.Value;
        }

        var text = declared.Value;

        if (!String.IsNullOrWhiteSpace(text) &&
            CssMathLengths.Evaluate(text, argument => DecorationTerm(argument, font, dimensions)) is { } value)
        {
            return value;
        }

        return TryReadLengthValue(declared, RenderMode.Horizontal, font, dimensions, out var pixels) ? pixels : null;
    }

    /// <summary>
    /// Reads one term of a decoration length: a share of the text's own size for a percentage, and
    /// otherwise whatever the common reader makes of the unit.
    /// </summary>
    private static Double? DecorationTerm(String term, LengthReader.FontSource font, IRenderDimensions dimensions)
    {
        if (!CssLengthValue.TryParse(term, out var length))
        {
            return null;
        }

        return length.Type == CssLengthValue.Unit.Percent
            ? (length.Value / 100) * font.Value
            : LengthReader.ToPixel(length, RenderMode.Horizontal, dimensions, font);
    }

    internal static UInt32 ToArgb(CssColorValue color) =>
        (UInt32)(((UInt32)color.A << 24) | ((UInt32)color.R << 16) | ((UInt32)color.G << 8) | color.B);

    /// <summary>
    /// Resolves the cursor from the element's own cascaded declaration, for the fused path.
    /// </summary>
    public static CursorKind ResolveCursorFused(IElement? element, ICssProperty? declared, CursorKind parent)
    {
        // `cursor` is inherited, so a miss takes the parent's resolved kind - which is how a
        // `cursor: pointer` on a container reaches the text inside it.
        var kind = declared is null
            ? parent
            : LastCursorKeyword(declared.Value) switch
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

        return kind == CursorKind.Auto && IsFollowableLink(element) ? CursorKind.Pointer : kind;
    }

    private static String LastCursorKeyword(String? value)
    {
        if (String.IsNullOrWhiteSpace(value))
        {
            return String.Empty;
        }

        var comma = value!.LastIndexOf(',');
        return (comma < 0 ? value : value[(comma + 1)..]).Trim();
    }

    /// <summary>
    /// Resolves the ruby position from the element's own cascaded declaration, for the fused path.
    /// The property is inherited - a page states <c>under</c> on a paragraph as readily as on the
    /// pair itself - so a miss takes the value the parent resolved rather than the initial one.
    /// </summary>
    public static RubyPosition ResolveRubyPositionFused(ICssProperty? declared, RubyPosition inherited) =>
        declared?.Value?.Trim().ToLowerInvariant() switch
        {
            null => inherited,
            "under" => RubyPosition.Under,
            "inter-character" => RubyPosition.InterCharacter,
            _ => RubyPosition.Over,
        };

    /// <summary>
    /// Resolves the colour the engine paints a link in, for the fused path: what the page hints at with
    /// the <c>body</c> element's legacy attributes where it writes one, and blue, purple or red where it
    /// does not.
    /// </summary>
    /// <remarks>
    /// HTML 2.3.6 treats <c>link</c>, <c>vlink</c> and <c>alink</c> on the <c>body</c> element as
    /// presentational hints naming the colour of every element in the document that matches
    /// <c>:link</c>, <c>:visited</c> or <c>:active</c>, and each value is read by the rules for parsing a
    /// legacy color value. A hint stands below anything the page writes - Chrome lets even an author
    /// <c>* { color: ... }</c> take a link back - and above the colour the link would otherwise inherit,
    /// which is exactly the place the engine's own link colour holds.
    /// </remarks>
    public static CssColorValue ResolveLinkColor(IElement? element)
    {
        var active = element?.GetPseudoClass(PseudoClassNames.Active) == true;
        var visited = !active && element?.GetPseudoClass(PseudoClassNames.Visited) == true;

        return HintColor(element, active ? "alink" : visited ? "vlink" : "link")
            ?? (active
                ? CssColorValue.FromRgb(0xee, 0x00, 0x00)
                : visited
                    ? CssColorValue.FromRgb(0x55, 0x1a, 0x8b)
                    : CssColorValue.FromRgb(0x00, 0x00, 0xee));
    }

    /// <summary>
    /// Reads one of the <c>body</c> element's legacy colour attributes from the document the element
    /// stands in, or null where the document carries no such element, writes no such attribute, or writes
    /// a value that names no colour.
    /// </summary>
    private static CssColorValue? HintColor(IElement? element, String name) =>
        PresentationalHints.LinkColor(element, name);

    /// <summary>
    /// Resolves the positioning mode from the element's own cascaded declaration, for the
    /// fused path. The typed value is asked first because the initial value's key is wrong.
    /// </summary>
    public static PositionMode ResolvePositionModeFused(ICssProperty? declared)
    {
        if (declared?.RawValue is CssConstantValue<PositionMode> constant)
        {
            return constant.Value;
        }

        return declared?.Value?.Trim().ToLowerInvariant() switch
        {
            "relative" => PositionMode.Relative,
            "absolute" => PositionMode.Absolute,
            "fixed" => PositionMode.Fixed,
            "sticky" => PositionMode.Sticky,
            _ => CssInitialValues.Position,
        };
    }

    /// <summary>
    /// Resolves one positioning offset from the element's own cascaded declaration, for the
    /// fused path. Percentages stay unresolved for the layout pass.
    /// </summary>
    public static StyleLength ResolveInsetFused(
        ICssProperty? declared, LengthReader.FontSource font, IRenderDimensions dimensions) =>
        declared is not null &&
        LengthReader.TryReadProperty(declared, RenderMode.Horizontal, dimensions, font, out var length) &&
        length.HasValue
            ? length
            : StyleLength.Auto;

    /// <summary>
    /// Resolves the stacking level from the element's own cascaded declaration, for the fused path.
    /// </summary>
    public static (Int32 ZIndex, Boolean HasZIndex) ResolveZIndexFused(ICssProperty? declared) =>
        declared?.RawValue is CssIntegerValue integer
            ? ((Int32)integer.Value, true)
            : (0, false);

    /// <summary>
    /// Resolves the font family list from the element's own cascaded declaration, for the fused
    /// path. Document-registered faces expand through the table the walk carries.
    /// </summary>
    public static String ResolveFontFamilyFused(ICssProperty? declared, String? parentFamily, FontFaceTable? faces)
    {
        // `font-family` is inherited: a miss takes the parent's list, which is already expanded, and
        // so does an explicit `inherit` - the keyword nearly every reset hands a form control.
        var family = declared is null || IsInherit(declared)
            ? parentFamily ?? CssInitialValues.FontFamily
            : KeywordText(declared, CssInitialValues.FontFamily);

        return faces is not null ? faces.Expand(family) : family;
    }

    /// <summary>
    /// Resolves the letter spacing from the element's own cascaded declaration, for the fused
    /// path. The property is inherited: a miss takes the parent's resolved spacing.
    /// </summary>
    public static Double ResolveLetterSpacingFused(
        ICssProperty? declared, Double parent, LengthReader.FontSource font, IRenderDimensions dimensions)
    {
        if (declared is null)
        {
            return parent;
        }

        if (IsInherit(declared))
        {
            return parent;
        }

        return TryReadLengthValue(declared, RenderMode.Horizontal, font, dimensions, out var pixels)
            ? pixels
            : CssInitialValues.LetterSpacing;
    }

    /// <summary>
    /// Resolves the room added between words from the element's own cascaded declaration, for the fused
    /// path. The property is inherited: a miss takes the parent's resolved spacing. <c>normal</c> - the
    /// initial value, and the only keyword the property has - is no room at all, and a value that is
    /// neither a length nor that keyword is not a declaration of this property, so it answers the same.
    /// </summary>
    public static Double ResolveWordSpacingFused(
        ICssProperty? declared, Double parent, LengthReader.FontSource font, IRenderDimensions dimensions)
    {
        if (declared is null || IsInherit(declared))
        {
            return parent;
        }

        return TryReadLengthValue(declared, RenderMode.Horizontal, font, dimensions, out var pixels)
            ? pixels
            : CssInitialValues.WordSpacing;
    }

    /// <summary>
    /// Resolves how far a tab runs the line on, from the element's own cascaded declaration, for the
    /// fused path. The property is inherited, and it is written two ways: a count of character widths -
    /// which is what the initial eight is, and which only the layout can turn into pixels because the
    /// width of a space belongs to the font - or a length, which is settled here. A percentage is not one
    /// of the forms the property has, so it is no declaration at all.
    /// </summary>
    /// <returns>
    /// The length in pixels and the count of characters, one of which is the answer and the other null.
    /// </returns>
    public static (Double? Length, Double Units) ResolveTabSizeFused(
        ICssProperty? declared,
        Double? parentLength,
        Double parentUnits,
        LengthReader.FontSource font,
        IRenderDimensions dimensions)
    {
        if (declared is null || IsInherit(declared))
        {
            return (parentLength, parentUnits);
        }

        var text = declared.Value?.Trim();

        if (text is not null &&
            Double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var units) &&
            units >= 0)
        {
            return (null, units);
        }

        if (declared.RawValue is CssLengthValue { Type: CssLengthValue.Unit.Percent })
        {
            return (parentLength, parentUnits);
        }

        return TryReadLengthValue(declared, RenderMode.Horizontal, font, dimensions, out var pixels)
            ? (Math.Max(0, pixels), parentUnits)
            : (parentLength, parentUnits);
    }

    /// <summary>
    /// Resolves the background layers from the element's own cascaded declarations, for the
    /// fused path. A page with no background properties paints none.
    /// </summary>
    /// <summary>
    /// Reads the border image a box paints its border area with, off the cascade the fused path already
    /// holds (CSS Backgrounds 3 §6.2 to §6.4). The border's own widths are needed for the widths and
    /// outsets that are written as multiples of them.
    /// </summary>
    public static BorderImageSpec? ResolveBorderImageFused(
        Func<String, ICssProperty?> find,
        IElement? element,
        IRenderDimensions dimensions,
        Double fontSize,
        BorderArea border) =>
        BorderImageParser.Parse(
            find("border-image-source")?.Value,
            find("border-image-slice")?.Value,
            find("border-image-width")?.Value,
            find("border-image-outset")?.Value,
            find("border-image-repeat")?.Value,
            new Thickness(border.TopWidth, border.RightWidth, border.BottomWidth, border.LeftWidth),
            dimensions,
            fontSize,
            element is null ? null : url => ImageUrlResolver.Resolve(element, url));

    public static IReadOnlyList<BackgroundLayer> ResolveBackgroundsFused(
        Func<String, ICssProperty?> find,
        IElement? element,
        IRenderDimensions dimensions,
        Double fontSize)
    {
        // The author's own background-image, or the element's legacy `background` attribute standing in
        // for it at the presentational-hint level when the author said nothing. The attribute is honoured
        // only on the elements HTML still maps it to - the body and the table's parts - and never on a
        // general block such as a div, exactly as a browser draws those and not the rest.
        var image = BackgroundImageWithHint(find("background-image"), element);

        return BackgroundLayerParser.Parse(
            image,
            BackgroundLayerParser.CombineAxes(
                find("background-position")?.Value,
                find("background-position-x")?.Value,
                find("background-position-y")?.Value),
            find("background-size")?.Value,
            BackgroundLayerParser.CombineAxes(
                find("background-repeat")?.Value,
                find("background-repeat-x")?.Value,
                find("background-repeat-y")?.Value),
            dimensions,
            fontSize,
            element is null ? null : url => ImageUrlResolver.Resolve(element, url));
    }

    /// <summary>
    /// The background-image the element ends up with: its own cascaded value, or, when the author said
    /// nothing, the legacy <c>background</c> attribute standing in at the presentational-hint level on the
    /// few elements that still carry one. Both the resolved style and the picture collector read it here,
    /// so a picture named only in the attribute is fetched as surely as one named in the stylesheet.
    /// </summary>
    internal static String? BackgroundImageWithHint(ICssProperty? declared, IElement? element)
    {
        var image = declared?.Value;

        if (String.IsNullOrWhiteSpace(image) && element is { } box && HonoursBackgroundAttribute(box.LocalName) &&
            box.GetAttribute("background") is { Length: > 0 } picture)
        {
            image = $"url(\"{picture.Replace("\"", String.Empty)}\")";
        }

        return image;
    }

    /// <summary>
    /// Gets whether an element's <c>background</c> attribute is a presentational hint the rendering
    /// requirements still honour. Only the body and the parts of a table carry one; a div or any other
    /// block ignores it outright.
    /// </summary>
    private static Boolean HonoursBackgroundAttribute(String localName) => localName.ToLowerInvariant() switch
    {
        "body" or "table" or "thead" or "tbody" or "tfoot" or "tr" or "td" or "th" => true,
        _ => false,
    };

    /// <summary>
    /// Resolves the background clip from the element's own cascaded declarations, for the fused path.
    /// The <c>-webkit-</c> spelling is read alongside the standard one because that is where a page still
    /// says <c>text</c>: whichever names it wins, and the box values come from the standard property as
    /// they always did.
    /// </summary>
    public static BackgroundClip ResolveBackgroundClipFused(ICssProperty? declared, ICssProperty? webkitDeclared)
    {
        var clip = MapBackgroundClip(KeywordText(declared, String.Empty));

        if (clip != BackgroundClip.Text &&
            MapBackgroundClip(KeywordText(webkitDeclared, String.Empty)) == BackgroundClip.Text)
        {
            return BackgroundClip.Text;
        }

        return clip;
    }

    private static BackgroundClip MapBackgroundClip(String value)
    {
        var comma = value.IndexOf(',');
        var keyword = (comma < 0 ? value : value[..comma]).Trim();

        return keyword.ToLowerInvariant() switch
        {
            "padding-box" => BackgroundClip.PaddingBox,
            "content-box" => BackgroundClip.ContentBox,
            "text" => BackgroundClip.Text,
            _ => BackgroundClip.BorderBox,
        };
    }

    /// <summary>
    /// Resolves the shadows a declaration lists for the fused path. A shadow list is a whole: an item the
    /// grammar refuses takes the declaration down with it rather than leaving the well-formed ones, which
    /// is what makes <c>box-shadow: 10px, 1px 1px red</c> say nothing at all.
    /// </summary>
    /// <remarks>
    /// What the two properties differ in is inheritance and nothing else: <c>text-shadow</c> is inherited,
    /// so a box that says nothing is drawn with the list its parent was, while <c>box-shadow</c> begins
    /// again at none. <c>inherit</c> asks for the parent's list whichever of the two it is written on.
    /// </remarks>
    public static IReadOnlyList<Shadow> ResolveShadowsFused(
        ICssProperty? declared,
        IRenderDimensions dimensions,
        LengthReader.FontSource font,
        CssColorValue ink,
        IReadOnlyList<Shadow> parent,
        Boolean inherits)
    {
        if (declared is null)
        {
            return inherits ? parent : Array.Empty<Shadow>();
        }

        var read = ReadShadowList(declared.RawValue, dimensions, font, ink);

        if (read is not null)
        {
            return read;
        }

        // <c>unset</c> takes the parent's list only where the property inherits; on <c>box-shadow</c>,
        // which does not, it means the initial value - and <c>none</c>, <c>initial</c> and a declaration
        // the grammar dropped all come to the same nothing.
        return IsInheritWord(declared.RawValue) || inherits && IsInheritKeyword(declared.RawValue!)
            ? parent
            : Array.Empty<Shadow>();
    }

    /// <summary>
    /// Reads the shadows of a value, or null where the value lists none: the keyword <c>none</c>, a wide
    /// keyword and a declaration the grammar refused are all answers this cannot give from the value alone.
    /// </summary>
    /// <remarks>
    /// The lengths are read from the declaration rather than from its computed self, because a computed
    /// <c>em</c> was folded against the device's root size instead of the element's own; a percentage is
    /// no answer at all, since the grammar asks for a length and nothing else. A blur that stands below
    /// zero is refused the same way, for the grammar bounds it at zero (CSS Backgrounds 3 §6.1).
    /// </remarks>
    private static IReadOnlyList<Shadow>? ReadShadowList(
        ICssValue? value,
        IRenderDimensions dimensions,
        LengthReader.FontSource font,
        CssColorValue ink)
    {
        if (value is not ICssMultipleValue written || written.Count == 0)
        {
            return null;
        }

        var shadows = new List<Shadow>(written.Count);

        for (var index = 0; index < written.Count; index++)
        {
            if (written[index] is not CssShadowValue item)
            {
                return null;
            }

            var x = ShadowLength(item.OffsetX, dimensions, font);
            var y = ShadowLength(item.OffsetY, dimensions, font);
            var blur = item.BlurRadius is { } blurred ? ShadowLength(blurred, dimensions, font) : 0.0;
            var spread = item.SpreadRadius is { } spreadBy ? ShadowLength(spreadBy, dimensions, font) : 0.0;

            if (x is null || y is null || blur is null || spread is null || blur < 0)
            {
                return null;
            }

            var asked = item.DeclaredColor;
            var follows = asked is null || asked == CssColorValue.CurrentColor;

            shadows.Add(new Shadow
            {
                OffsetX = x.Value,
                OffsetY = y.Value,
                Blur = blur.Value,
                Spread = spread.Value,
                // The colour a shadow left out is the element's own ink, and so is the keyword that asks for
                // it (§6.1: "omitted colors default to currentColor") - but the asking is what travels down
                // a tree that inherits the shadow, and the ink is each box's own, so the answer kept here is
                // only the one for the box the declaration was read on.
                Color = follows ? ToArgb(ink) : ToArgb(asked!.Value),
                FollowsInk = follows,
                Inset = item.IsInset,
            });
        }

        return shadows;
    }

    /// <summary>
    /// Gets the shadow length a value states, or null where it states none. A percentage, a keyword and a
    /// length the device cannot convert are all answers null.
    /// </summary>
    private static Double? ShadowLength(ICssValue? value, IRenderDimensions dimensions, LengthReader.FontSource font) =>
        value is not null && LengthReader.TryReadValue(value, RenderMode.Horizontal, dimensions, font, out var length) &&
        length.Kind == StyleLengthKind.Pixels
            ? length.Value
            : null;

    /// <summary>
    /// Resolves the rectangle a <c>clip</c> declaration states, for the fused path. The property is not
    /// inherited, so a box that says nothing is not clipped whatever its parent was - the one answer that
    /// does reach down is <c>inherit</c>, which takes the parent's four distances and applies them to this
    /// box's own border box.
    /// </summary>
    public static ClipShape? ResolveClipFused(
        ICssProperty? declared,
        IRenderDimensions dimensions,
        LengthReader.FontSource font,
        ClipShape? parent)
    {
        if (declared is null)
        {
            return null;
        }

        if (declared.RawValue is CssShapeValue shape)
        {
            return ReadShape(shape, dimensions, font);
        }

        return IsInheritWord(declared.RawValue) ? parent : null;
    }

    /// <summary>
    /// Resolves the shape a <c>clip-path</c> declaration states, for the fused path. The property is not
    /// inherited, so a box that says nothing is cut by nothing; <c>inherit</c> is the one answer that does
    /// reach down, and it hands the parent's shape to a box that would otherwise have had none.
    /// </summary>
    public static ClipPathShape? ResolveClipPathFused(
        ICssProperty? declared,
        IRenderDimensions dimensions,
        LengthReader.FontSource font,
        ClipPathShape? parent)
    {
        if (declared is null)
        {
            return null;
        }

        if (IsInheritWord(declared.RawValue))
        {
            return parent;
        }

        return ClipPathShape.Read(declared.RawValue?.CssText, dimensions, font);
    }

    /// <summary>
    /// Reads the four offsets of a clip shape. A side that is neither the keyword that leaves it to the
    /// edge nor a length the device can fold is no clip at all, which is also what the grammar says.
    /// </summary>
    private static ClipShape? ReadShape(CssShapeValue shape, IRenderDimensions dimensions, LengthReader.FontSource font)
    {
        if (!ReadSide(shape.Top, dimensions, font, out var top) ||
            !ReadSide(shape.Right, dimensions, font, out var right) ||
            !ReadSide(shape.Bottom, dimensions, font, out var bottom) ||
            !ReadSide(shape.Left, dimensions, font, out var left))
        {
            return null;
        }

        return new ClipShape(top, right, bottom, left);
    }

    /// <summary>
    /// Reads one side of a clip shape, which is a length - the <c>auto</c> keyword arrives as a length with
    /// no number in it - or nothing at all where the side is left to the edge of the box.
    /// </summary>
    private static Boolean ReadSide(
        ICssValue? value,
        IRenderDimensions dimensions,
        LengthReader.FontSource font,
        out Double? offset)
    {
        offset = null;

        if (value is null)
        {
            return false;
        }

        if (!LengthReader.TryReadValue(value, RenderMode.Horizontal, dimensions, font, out var length))
        {
            return false;
        }

        if (length.IsAuto)
        {
            return true;
        }

        if (length.Kind != StyleLengthKind.Pixels)
        {
            return false;
        }

        offset = length.Value;
        return true;
    }

    /// <summary>
    /// Resolves the text stroke - the width the glyphs are ringed with and the colour of that ring - for
    /// the fused path. Both are inherited, and the shorthand <c>-webkit-text-stroke</c> can set either;
    /// a longhand written beside it wins for its own half, and anything left unsaid comes from the parent.
    /// A width no one gave is none, and a colour no one gave is transparent.
    /// </summary>
    public static (Double Width, UInt32 Color) ResolveTextStrokeFused(
        Func<String, ICssProperty?> find,
        Double parentWidth,
        UInt32 parentColor,
        Double ownSize,
        Double rootSize)
    {
        var shorthand = find("-webkit-text-stroke")?.Value;
        Double? shorthandWidth = null;
        UInt32? shorthandColor = null;

        if (!String.IsNullOrWhiteSpace(shorthand))
        {
            foreach (var token in SplitValues(shorthand!))
            {
                if (shorthandWidth is null && ParseVendorLength(token, ownSize, rootSize) is { } length)
                {
                    shorthandWidth = length;
                }
                else if (shorthandColor is null && ParseVendorColor(token) is { } parsed)
                {
                    shorthandColor = parsed;
                }
            }
        }

        var width = ParseVendorLength(find("-webkit-text-stroke-width")?.Value ?? String.Empty, ownSize, rootSize)
                    ?? shorthandWidth
                    ?? parentWidth;

        var color = ParseVendorColor(find("-webkit-text-stroke-color")?.Value ?? String.Empty) ?? shorthandColor ?? parentColor;

        return (width, color);
    }

    /// <summary>
    /// Resolves <c>-webkit-text-fill-color</c>, which stands in for the text colour where it is given and
    /// is otherwise inherited; null means "not said", and the element's own colour paints the glyphs.
    /// </summary>
    public static UInt32? ResolveTextFillColorFused(String? declared, UInt32? parent)
    {
        if (!String.IsNullOrWhiteSpace(declared) && ParseVendorColor(declared!) is { } color)
        {
            return color;
        }

        return parent;
    }

    private static Double? ParseVendorLength(String text, Double ownSize, Double rootSize)
    {
        text = text.Trim();

        switch (text.ToLowerInvariant())
        {
            case "": return null;
            case "thin": return 1;
            case "medium": return 3;
            case "thick": return 5;
        }

        var match = VendorLengthPattern.Match(text);

        if (!match.Success || !Double.TryParse(match.Groups["number"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
        {
            return null;
        }

        return match.Groups["unit"].Value.ToLowerInvariant() switch
        {
            "" or "px" => value,
            "em" => value * ownSize,
            "rem" => value * rootSize,
            "%" => value / 100 * ownSize,
            "pt" => value * 96 / 72,
            "pc" => value * 16,
            "in" => value * 96,
            "cm" => value * 96 / 2.54,
            "mm" => value * 96 / 25.4,
            _ => null,
        };
    }

    private static UInt32? ParseVendorColor(String text) =>
        String.IsNullOrWhiteSpace(text) ? null : BackgroundLayerParser.ParseColor(text.Trim());

    /// <summary>Captures a number and the unit that follows it, for a length read from raw text.</summary>
    private static readonly System.Text.RegularExpressions.Regex VendorLengthPattern = new(
        @"^(?<number>[+-]?(?:\d+\.?\d*|\.\d+))(?<unit>[a-zA-Z%]*)$",
        System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>Splits a value into its whitespace-separated pieces, keeping what parentheses hold.</summary>
    private static IEnumerable<String> SplitValues(String text)
    {
        var depth = 0;
        var run = new System.Text.StringBuilder();

        foreach (var c in text)
        {
            if (c == '(')
            {
                depth++;
            }
            else if (c == ')')
            {
                depth = Math.Max(0, depth - 1);
            }

            if (depth == 0 && Char.IsWhiteSpace(c))
            {
                if (run.Length > 0)
                {
                    yield return run.ToString();
                    run.Clear();
                }

                continue;
            }

            run.Append(c);
        }

        if (run.Length > 0)
        {
            yield return run.ToString();
        }
    }

    /// <summary>
    /// Resolves the outline colour from the element's own cascaded declaration, for the fused
    /// path. Invert and a miss both take the element's own resolved colour.
    /// </summary>
    public static CssColorValue ResolveOutlineColorFused(ICssProperty? declared, CssColorValue ownColor)
    {
        var resolved = declared?.RawValue switch
        {
            CssColorValue color => color,
            CssConstantValue<CssColorValue> constant => constant.Value,
            _ => ownColor,
        };

        return resolved == CssColorValue.InvertedColor ? ownColor : resolved;
    }

    /// <summary>
    /// Gets whether the declaration is the wide keyword <c>inherit</c>, which the cascade leaves
    /// unresolved and which asks for the value its parent computed rather than one of its own.
    /// </summary>
    internal static Boolean IsInherit(ICssProperty? declared) =>
        declared is not null &&
        ((declared.RawValue is { } raw && IsInheritKeyword(raw)) ||
         declared.Value?.Trim().Equals("inherit", StringComparison.OrdinalIgnoreCase) == true);

    private static Double? ResolveLineHeightValue(ICssProperty declared, LengthReader.FontSource font, IRenderDimensions dimensions)
    {
        if (declared.RawValue is CssLengthValue share && share.Type == CssLengthValue.Unit.Percent)
        {
            return share.Value * 0.01 * font.Value;
        }

        var text = declared.Value;

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
            return factor * font.Value;
        }

        // A line height written as a calc() keeps line-height's own rules: a percentage and a
        // font-relative unit are multiples of the element's own font size, not of the viewport or the
        // root. Folding against that size returns every length already in pixels; only a plain number is
        // left bare, and a line height reads it as a multiple of the same size.
        if (declared.RawValue is CssCalcValue calc &&
            LengthReader.TryFoldCalcRaw(calc, font.Value, out var unit, out var number))
        {
            return unit == CssLengthValue.Unit.None ? number * font.Value : number;
        }

        return TryReadLengthValue(declared, RenderMode.Vertical, font, dimensions, out var pixels)
            ? pixels
            : null;
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

    /// <summary>The names a display, a clipping or a float property can begin with.</summary>
    private static readonly String[] DisplayNames = { "display", "object-fit", "overflow", "float", "clear", "align-content" };

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
            Float = ToFloat(Keyword("float", CssInitialValues.Float)),
            Clear = ToClear(Keyword("clear", CssInitialValues.Clear)),
            ClipsOverflow = !IsOverflowVisible("overflow-x") || !IsOverflowVisible("overflow-y"),
            ScrollsInline = IsScrollOverflowText(OverflowKeyword("overflow-x")),
            ScrollsBlock = IsScrollOverflowText(OverflowKeyword("overflow-y")),
            AlignContent = FlexStyle.ReadAlignment(this, "align-content", CssInitialValues.AlignContent),
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

        // An outline is one ring with no sides to bevel, so a bevelled keyword falls back to solid for
        // it even though a border draws the shade - a stroke the outline cannot express still shows.
        if (BorderStyles.IsBevel(style))
        {
            style = BorderLineStyle.Solid;
        }

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
        { "margin", "padding", "width", "height", "min-", "max-", "box-sizing", "inline-size", "block-size" };

    private void ResolveBorder(StyleIntern? pool)
    {
        if (!DeclaresAny(BorderNames))
        {
            return;
        }

        var sides = AxisSides(Flow);

        var topStyle = Keyword(BorderSide(sides.Top, "top", "style"), CssInitialValues.BorderStyle);
        var rightStyle = Keyword(BorderSide(sides.Right, "right", "style"), CssInitialValues.BorderStyle);
        var bottomStyle = Keyword(BorderSide(sides.Bottom, "bottom", "style"), CssInitialValues.BorderStyle);
        var leftStyle = Keyword(BorderSide(sides.Left, "left", "style"), CssInitialValues.BorderStyle);

        var border = new BorderArea
        {
            TopStyle = topStyle,
            RightStyle = rightStyle,
            BottomStyle = bottomStyle,
            LeftStyle = leftStyle,
            TopWidth = BorderWidth(topStyle, BorderSide(sides.Top, "top", "width")),
            RightWidth = BorderWidth(rightStyle, BorderSide(sides.Right, "right", "width")),
            BottomWidth = BorderWidth(bottomStyle, BorderSide(sides.Bottom, "bottom", "width")),
            LeftWidth = BorderWidth(leftStyle, BorderSide(sides.Left, "left", "width")),
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
        // margins of its own - which is every heading and paragraph of a page that states none - or a
        // padding of its own, which is every list the page leaves its markers to.
        if (!DeclaresAny(BoxModelNames) &&
            Defaults("margin") is (null, null, null, null) &&
            Defaults("padding") is (null, null, null, null))
        {
            return;
        }

        // A size named for the axis the text runs along is the statement about the side that axis lies on,
        // and where the page names both, the axis is what it meant (CSS Logical 1 §4, the same order the
        // thicknesses below keep).
        var alongWidth = Flow.IsVertical ? "block-size" : "inline-size";
        var alongHeight = Flow.IsVertical ? "inline-size" : "block-size";

        var declaredWidth = ReadDeclared(alongWidth, RenderMode.Horizontal) ??
            ReadDeclared("width", RenderMode.Horizontal);
        var declaredHeight = ReadDeclared(alongHeight, RenderMode.Vertical) ??
            ReadDeclared("height", RenderMode.Vertical);

        var model = new BoxModelArea
        {
            BoxSizing = ResolveBoxSizing(),
            Width = declaredWidth ?? StyleLength.Auto,
            Height = declaredHeight ?? StyleLength.Auto,
            WidthDeclared = declaredWidth is not null,
            HeightDeclared = declaredHeight is not null,
            MinWidth = ReadLength("min-" + alongWidth, RenderMode.Horizontal,
                ReadLength("min-width", RenderMode.Horizontal, StyleLength.None)),
            MaxWidth = ReadLength("max-" + alongWidth, RenderMode.Horizontal,
                ReadLength("max-width", RenderMode.Horizontal, StyleLength.None)),
            MinHeight = ReadLength("min-" + alongHeight, RenderMode.Vertical,
                ReadLength("min-height", RenderMode.Vertical, StyleLength.None)),
            MaxHeight = ReadLength("max-" + alongHeight, RenderMode.Vertical,
                ReadLength("max-height", RenderMode.Vertical, StyleLength.None)),
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
    internal sealed class StyleValues
    {
        internal BorderArea Border = BorderArea.Default;

        internal BoxModelArea BoxModel = BoxModelArea.Default;

        internal DisplayPositioningArea DisplayPositioning = DisplayPositioningArea.Default;

        internal VisualEffectsArea VisualEffects = VisualEffectsArea.Default;

        internal FontArea Font = new();

        internal TextArea Text = new();

        internal TextDecorationArea Decoration = new();

        internal ListArea List = ListArea.Default;

        // Null until something asks. See ComputedStyle.Position and .Flex for why these four wait
        // while the areas above do not.
        internal FlexStyle? Flex;

        internal GridStyle? Grid;

        internal TableStyle? Table;

        internal MulticolStyle? Multi;

        internal GapRules? Gap;

        internal ContainStyle? Contain;

        internal BreakStyle Breaks;

        internal PositionStyle? Position;
        internal GridItemStyle? GridItem;

        // Filled only by the fused box-builder path. The accessors that would otherwise read the
        // declaration answer from here when it is present, so a style assembled from matched
        // declarations and inheritance carries no declaration at all. Null on the render-tree path.
        internal FusedKeywords? Fused;

        // The shadows with every colour that asked for the ink settled to this element's own, which is what
        // a box is painted with. Kept because the asking travels down a tree that inherits a shadow while
        // the answer does not: a shadow written in red and inherited by a green box is drawn green. Null
        // until one is asked for, since a list that asks for nothing is already the answer.
        internal IReadOnlyList<Shadow>? BoxPaint;
        internal IReadOnlyList<Shadow>? TextPaint;
    }

    /// <summary>
    /// The values the fused path resolves that are not areas: the keywords, colours and lists
    /// the render-tree path would otherwise read from the computed declaration.
    /// </summary>
    internal sealed class FusedKeywords
    {
        internal Layout.WritingMode WritingMode = Layout.WritingMode.HorizontalTb;
        internal TextDirection Direction = TextDirection.LeftToRight;

        /// <summary>
        /// Gets the declared <c>unicode-bidi</c>, or null where the page wrote none. Only its
        /// <c>plaintext</c> value is read, for the direction a paragraph takes from its own text.
        /// </summary>
        internal String? UnicodeBidi;
        internal TextOrientation? Orientation;
        internal Boolean? CombinesUpright;
        internal CssColorValue Color = CssColorValue.Black;
        internal CssColorValue? BackgroundColor;
        internal IReadOnlyList<BackgroundLayer> Backgrounds = Array.Empty<BackgroundLayer>();

        /// <summary>
        /// Gets the picture the box paints its border area with, or null where it has none. Not inherited,
        /// so this is the element's own answer whatever its parent came to.
        /// </summary>
        internal BorderImageSpec? BorderImage;

        /// <summary>
        /// Gets the shadows the box is drawn with. The property is not inherited, so this is the element's
        /// own answer whatever its parent came to.
        /// </summary>
        internal IReadOnlyList<Shadow> BoxShadows = Array.Empty<Shadow>();

        /// <summary>Gets the shadows the element's text is drawn with, inherited like every other value here.</summary>
        internal IReadOnlyList<Shadow> TextShadows = Array.Empty<Shadow>();

        /// <summary>
        /// Gets the rectangle this box cuts itself to, or null where it states none. The distances are the
        /// element's own; the area they come to is worked out when the box is painted.
        /// </summary>
        internal ClipShape? Clip;

        /// <summary>
        /// Gets the shape this box cuts its own picture to, or null where it states none. The shape keeps
        /// its percentages until the box it belongs to has been laid out.
        /// </summary>
        internal ClipPathShape? ClipPath;

        /// <summary>
        /// Gets the shape the lines beside this float stand clear of, or null where it states none. Like the
        /// clip above it keeps its percentages, but they are folded at the float's placement rather than at
        /// its painting, because the area is what a line is measured against.
        /// </summary>
        internal ClipPathShape? ShapeOutside;

        /// <summary>Gets how much further out the shape above is stood, kept as the value it was parsed to.</summary>
        internal ICssValue? ShapeMargin;
        internal CursorKind Cursor;
        internal RubyPosition Ruby;
        internal CssColorValue OutlineColor = CssColorValue.Black;
        internal BackgroundClip BackgroundClip;

        /// <summary>
        /// Gets whether a box broken across lines, columns or pages is wrapped independently on every
        /// piece - <c>box-decoration-break: clone</c> - rather than drawn once and cut
        /// (CSS Break 3 §4.7).
        /// </summary>
        internal Boolean ClonesDecoration;
        internal CssColorValue? BorderTopColor;
        internal CssColorValue? BorderRightColor;
        internal CssColorValue? BorderBottomColor;
        internal CssColorValue? BorderLeftColor;

        /// <summary>Gets the width the glyphs are ringed with, in pixels; zero is no stroke.</summary>
        internal Double TextStrokeWidth;

        /// <summary>Gets the packed ARGB of that ring; transparent by default, so a stroke can widen the
        /// ink a background is clipped to without painting a colour of its own.</summary>
        internal UInt32 TextStrokeColor;

        /// <summary>Gets the colour the glyph body is filled with, standing in for <c>color</c> where the
        /// page set <c>-webkit-text-fill-color</c>; null means it did not.</summary>
        internal UInt32? TextFillColor;

        /// <summary>Gets the declared <c>transform</c>, or null when the box carries none.</summary>
        internal String? Transform;

        /// <summary>Gets the declared <c>filter</c>, or null when the box carries none.</summary>
        internal String? Filter;

        /// <summary>Gets the declared <c>backdrop-filter</c>, or null when the box carries none.</summary>
        internal String? BackdropFilter;

        /// <summary>Gets the declared <c>opacity</c>, or null when the box says nothing of its own.</summary>
        internal String? Opacity;

        /// <summary>Gets the declared <c>will-change</c> list, or null where the page wrote nothing.</summary>
        internal String? WillChange;

        /// <summary>Gets the declared <c>transform-style</c>, or null for the flat default.</summary>
        internal String? TransformStyle;

        /// <summary>Gets the declared <c>transform-origin</c>, or null for the default centre.</summary>
        internal String? TransformOrigin;

        // CSS Motion Path longhands (and the <c>offset</c> shorthand), kept as raw text for the motion
        // path reader; AngleSharp has no grammar for them.
        internal String? OffsetPath;
        internal String? OffsetDistance;
        internal String? OffsetRotate;
        internal String? OffsetAnchor;
        internal String? OffsetPosition;
        internal String? Offset;
    }

    /// <summary>
    /// Gets the out-of-flow mode, the offsets and the stacking order as one value. The layout asks for
    /// these together and repeatedly - once per child to decide whether a box is in flow, then again to
    /// place it - and reading them as one keeps that from costing twenty declaration reads a time.
    /// </summary>
    public PositionStyle Position =>
        _values.Fused is not null
            ? _values.Position!.Value
            : _values.Position ??= PositionStyle.Read(this);

    /// <summary>
    /// Gets the declared <c>transform</c> text, or null/empty when the box carries none. Read as text on
    /// both style paths and turned into a matrix later, once the box's own size is known (a translate
    /// percentage and the origin are relative to the border box).
    /// </summary>
    public String? TransformText =>
        _values.Fused is { } fused ? fused.Transform : _style.GetPropertyValue("transform");

    /// <summary>Gets the declared <c>transform-origin</c> text, or null for the default centre.</summary>
    public String? TransformOriginText =>
        _values.Fused is { } fused ? fused.TransformOrigin : _style.GetPropertyValue("transform-origin");

    /// <summary>
    /// Gets whether the box carries a <c>transform</c> or an <c>offset-path</c> that is not <c>none</c>.
    /// Such a box becomes the containing block for its absolute and fixed descendants and a stacking
    /// context, whether or not the resulting matrix moves anything (CSS Transforms 1 §transform-rendering).
    /// </summary>
    public Boolean IsTransformed =>
        (TransformText is { Length: > 0 } transform && !transform.Equals("none", StringComparison.OrdinalIgnoreCase)) ||
        (OffsetValue("offset-path") is { Length: > 0 } path && !path.Equals("none", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Gets the declared <c>filter</c> text, or null/empty when the box carries none. Read as text on both
    /// style paths, the same way <c>transform</c> is: what the layout asks is whether the box says a filter
    /// at all, and the picture the effect makes is a question for the drawing.
    /// </summary>
    public String? FilterText =>
        _values.Fused is { } fused ? fused.Filter : _style.GetPropertyValue("filter");

    /// <summary>Gets the declared <c>backdrop-filter</c> text, or null/empty when the box carries none.</summary>
    public String? BackdropFilterText =>
        _values.Fused is { } fused ? fused.BackdropFilter : _style.GetPropertyValue("backdrop-filter");

    /// <summary>
    /// Gets whether the box carries a <c>filter</c> or a <c>backdrop-filter</c> that is not
    /// <c>none</c> - which is what gives it the frame its descendants resolve against
    /// (CSS Filter Effects 1 §2.4).
    /// </summary>
    public Boolean IsFiltered =>
        SaysAFilter(FilterText) || SaysAFilter(BackdropFilterText);

    private static Boolean SaysAFilter(String? stated) =>
        stated is { Length: > 0 } && !stated.Equals("none", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Gets the declared <c>opacity</c> text, or null/empty when the box carries none. Read as text on
    /// both style paths, the way <c>filter</c> is; the number is what the drawing wants, not a length,
    /// so there is nothing for the style layer to resolve first.
    /// </summary>
    public String? OpacityText =>
        _values.Fused is { } fused ? fused.Opacity : _style.GetPropertyValue("opacity");

    /// <summary>
    /// Gets how strongly the box is laid down, one for a box that asks for nothing less. The number is
    /// clamped at computed-value time - what is written above one or below nought comes to that bound -
    /// and a percentage is the same value over a hundred (CSS Color 3 §4.4, CSS Compositing 1 §9.1).
    /// Text that is a number in none of these shapes leaves the box at full strength.
    /// </summary>
    public Double Opacity
    {
        get
        {
            var stated = OpacityText?.Trim();

            if (stated is not { Length: > 0 })
            {
                return 1;
            }

            if (stated.EndsWith("%", StringComparison.OrdinalIgnoreCase) &&
                Double.TryParse(stated[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var share))
            {
                return Math.Clamp(share / 100, 0d, 1d);
            }

            return Double.TryParse(stated, NumberStyles.Float, CultureInfo.InvariantCulture, out var alpha)
                ? Math.Clamp(alpha, 0d, 1d)
                : 1;
        }
    }

    /// <summary>
    /// Gets whether the box is drawn through one layer at less than full strength - which stands it on a
    /// level of its own as well, though it gives its descendants no frame to resolve against
    /// (CSS Compositing 1 §9.1, CSS 2.1 §Appendix E).
    /// </summary>
    public Boolean IsTransparent => Opacity < 1;

    /// <summary>
    /// Gets whether the box is the frame its absolute and fixed descendants resolve against whatever its
    /// own <c>position</c> says: a filter or a backdrop filter (CSS Filter Effects 1 §2.4), a transform or
    /// an offset path (CSS Transforms 1 §transform-rendering), containment of layout or paint
    /// (CSS Contain 2 §2.2), or a <c>will-change</c> that hints at one of them
    /// (CSS Will Change 1 §2).
    /// </summary>
    /// <param name="inlineLevel">
    /// Whether the box is a non-replaced inline-level one. A transform does not stand on such a box, so it
    /// makes no frame there, while a filter does, because the filter chapter writes the containing block
    /// as a consequence of the value rather than of the effect being drawn. Measured against Chrome on a
    /// span at <c>build/probe-cb-props.html</c>: <c>filter</c>, <c>backdrop-filter</c> and
    /// <c>will-change: filter</c> each hold an absolute descendant of the span, while a transform leaves
    /// it at the page corner, and <c>build/probe-cb-rect.html</c> says containment behaves the same way.
    /// </param>
    public Boolean FormsContainingBlock(Boolean inlineLevel)
    {
        if (IsFiltered)
        {
            return true;
        }

        if (!inlineLevel)
        {
            // Containment is the one property here that the display has to be asked about as well as the
            // box tree: an inline element that a block standing inside it forced into a shape of its own
            // is still a non-atomic inline-level box to CSS Contain 2 §2.4.
            var contain = Contain;

            if (IsTransformed || (ContainmentApplies && (contain.Layout || contain.Paint)))
            {
                return true;
            }
        }

        return WillChangeNamesAFrame(inlineLevel);
    }

    /// <summary>
    /// Gets the declared text of a CSS Motion Path longhand (or the <c>offset</c> shorthand), or null
    /// when the box says nothing. Read as text on both style paths; the motion path reader turns it into
    /// an offset transform once the box is sized.
    /// </summary>
    public String? OffsetValue(String name) =>
        _values.Fused is { } fused
            ? name switch
            {
                "offset-path" => fused.OffsetPath,
                "offset-distance" => fused.OffsetDistance,
                "offset-rotate" => fused.OffsetRotate,
                "offset-anchor" => fused.OffsetAnchor,
                "offset-position" => fused.OffsetPosition,
                "offset" => fused.Offset,
                _ => null,
            }
            : _style.GetPropertyValue(name);

    /// <summary>
    /// Gets whether the box keeps its third dimension, which - like a transform - makes it the frame
    /// everything inside it is drawn in (CSS Transforms 2 §transform-style).
    /// </summary>
    public Boolean PreservesThirdDimension
    {
        get
        {
            var stated = _values.Fused is { } fused
                ? fused.TransformStyle
                : _style.GetPropertyValue("transform-style");

            return stated is not null &&
                stated.Contains("preserve-3d", StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// Gets whether the box asks the device to get ready for a change that would make it a stacking context
    /// if it had happened (CSS Will Change 1 §2). <c>will-change</c> changes nothing about the picture, and
    /// that is exactly why the levels inside such a box are its own to answer.
    /// </summary>
    /// <param name="positioned">
    /// Whether the box is out of the static flow. <c>z-index</c> and <c>position</c> are the two hints that
    /// only come to a context for a box that is positioned, because the properties they name do nothing to a
    /// box that is not.
    /// </param>
    private Boolean WillChangeNamesALayer(Boolean positioned)
    {
        foreach (var hint in WillChangeHints())
        {
            if (!LayeredProperties.Contains(hint, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            if (positioned || !String.Equals(hint, "z-index", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Gets whether the box hints - with <c>will-change</c> - at one of the properties that would give it
    /// the frame its descendants resolve against (CSS Will Change 1 §2). A hint stands for the property it
    /// names, at the level that property reaches: a hint at a filter holds an inline box because a filter
    /// holds one, and a hint at a transform holds only a box the transform is drawn on.
    /// </summary>
    private Boolean WillChangeNamesAFrame(Boolean inlineLevel)
    {
        foreach (var hint in WillChangeHints())
        {
            if (IsFilterName(hint) ||
                (!inlineLevel && FramedProperties.Contains(hint, StringComparer.OrdinalIgnoreCase)))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Gets whether a <c>will-change</c> token names one of the two filter properties.</summary>
    private static Boolean IsFilterName(String hint) =>
        hint.Equals("filter", StringComparison.OrdinalIgnoreCase) ||
        hint.Equals("backdrop-filter", StringComparison.OrdinalIgnoreCase);

    /// <summary>Gets the property names the box's <c>will-change</c> list writes, as the page spelled them.</summary>
    private IEnumerable<String> WillChangeHints()
    {
        var stated = _values.Fused is { } fused
            ? fused.WillChange
            : _style.GetPropertyValue("will-change");

        if (String.IsNullOrEmpty(stated))
        {
            return Array.Empty<String>();
        }

        return stated!.Split([','], StringSplitOptions.RemoveEmptyEntries).Select(token => token.Trim());
    }

    /// <summary>
    /// The properties, besides the two filters, whose non-initial value gives the box the frame its
    /// descendants resolve against: the transform chapter's own two and the four shorthands that stand in
    /// for them, motion path, and containment (CSS Transforms 1 §transform-rendering, CSS Motion Path 1,
    /// CSS Contain 2 §2.2).
    /// </summary>
    private static readonly String[] FramedProperties =
    [
        "transform", "translate", "rotate", "scale", "perspective", "offset-path", "contain",
    ];

    /// <summary>
    /// Gets whether the box forms a stacking context: its own picture and everything inside it is
    /// answered at one level of the context above, and the levels its descendants name are its own to
    /// answer (CSS 2.1 §Appendix E). The ways the engine reads one are a positioned box that states a
    /// level; a transform, an offset path or a <c>transform-style</c> that keeps the third dimension; a
    /// filter or a backdrop filter, which the chapter gives both consequences for one value
    /// (CSS Filter Effects 1 §2.4); an <c>opacity</c> below one (CSS Compositing 1 §9.1); containment of
    /// layout or paint (CSS Contain 2 §2.5, §2.6); and a <c>will-change</c> naming any property that
    /// would do it.
    /// </summary>
    public Boolean EstablishesStackingContext
    {
        get
        {
            var position = Position;
            var positioned = position.Position != PositionMode.Static;

            if (positioned && position.HasZIndex)
            {
                return true;
            }

            // A shape is a cut the whole of the box's picture has to sit inside, and CSS Masking 1 §5.1
            // gives the box that has one a stacking context of its own the same way opacity does - which is
            // also what keeps anything the box paints from being answered a level above the cut.
            if (IsTransformed || IsFiltered || IsTransparent || HasClipPath || PreservesThirdDimension ||
                WillChangeNamesALayer(positioned))
            {
                return true;
            }

            // Containment is a property of the principal box, so an inline or a `display: contents`
            // element that contains its paint forms no context and clips nothing - see
            // <see cref="ContainmentApplies"/> for the whole of that, which is what the
            // `contain-*-ignored-cases-*` family measures pair by pair.
            return ContainmentApplies && (Contain.Layout || Contain.Paint);
        }
    }

    /// <summary>
    /// Gets whether containment reaches this box at all. A non-atomic inline-level box is not a principal
    /// box, and neither is one the display leaves out altogether, so a span or a `display: contents`
    /// element that asks to be contained is not (CSS Contain 2 §2.4, §2.6 - what
    /// `contain-*-ignored-cases-ib-split` and `...-no-principal-box-*` measure). An inline box that gets
    /// split - by a block standing inside it, or by its own white space - comes back as anonymous blocks,
    /// and an anonymous box borrows the geometry and the font of the style it was split from rather than
    /// the element's declarations, so the split cannot smuggle the containment back in: what decides is
    /// the display the element was given, not the shape the box tree ended with.
    /// </summary>
    public Boolean ContainmentApplies =>
        !Display.Equals("inline", StringComparison.OrdinalIgnoreCase) &&
        !Display.Equals("contents", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The properties whose non-initial value would put a box on a layer of its own, and whose
    /// <c>will-change</c> hint does the same (CSS Will Change 1 §2, read against CSS 2.1 §Appendix E,
    /// CSS Transforms 1, CSS Masking 1 and CSS Contain 2 - the list is what the
    /// <c>css-will-change/will-change-stacking-context-*</c> family measures pair by pair).
    /// </summary>
    private static readonly String[] LayeredProperties =
    [
        "transform", "translate", "rotate", "scale", "perspective", "transform-style", "offset-path",
        "opacity", "filter", "backdrop-filter", "mix-blend-mode", "isolation", "mask",
        "mask-image", "clip-path", "position", "z-index", "contain", "view-transition-name",
    ];

    /// <summary>Gets the underlying declaration, for values this wrapper does not surface.</summary>
    public ICssStyleDeclaration Declaration => _style;

    // --- Box generation -------------------------------------------------

    public String Display => _values.DisplayPositioning.Display;

    /// <summary>Gets which edge this box floats to, or <see cref="FloatSide.None"/> when it stays in flow.</summary>
    public FloatSide Float => _values.DisplayPositioning.Float;

    /// <summary>Gets which side's floats this box moves down past.</summary>
    public ClearSide Clear => _values.DisplayPositioning.Clear;

    public Boolean IsDisplayNone =>
        _values.DisplayPositioning.Display.Equals("none", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Gets the font area this style answers from. Exposed to the builder rather than read through the
    /// properties below, because the instance is itself an answer: areas are interned, so two styles that
    /// set the same font hold the same object, and an element that inherits a font inherits something a
    /// cache can name by reference instead of by walking its six values.
    /// </summary>
    internal FontArea ResolvedFont => _values.Font;

    /// <summary>Gets the border area this style answers from, shared with every style whose border agrees.</summary>
    internal BorderArea ResolvedBorder => _values.Border;

    /// <summary>Gets the text area this style answers from, shared with every style whose text agrees.</summary>
    internal TextArea ResolvedText => _values.Text;

    /// <summary>
    /// Gets the list marker style the element's items are marked with. Read as an area by the builder,
    /// which hands it to the children the way it hands the font and the text.
    /// </summary>
    internal ListArea ResolvedList => _values.List;

    /// <summary>Gets the box model area this style answers from, shared with every style whose lengths agree.</summary>
    internal BoxModelArea ResolvedBoxModel => _values.BoxModel;

    /// <summary>
    /// Gets the display and positioning area this style answers from, shared with every style whose
    /// display, float, clear and overflow agree.
    /// </summary>
    internal DisplayPositioningArea ResolvedDisplayPositioning => _values.DisplayPositioning;

    /// <summary>
    /// Gets whether this style lays a box out exactly the way <paramref name="other"/> does: every area
    /// the layout pass reads is the very same one.
    /// </summary>
    /// <remarks>
    /// The areas are compared by reference rather than by value because they are interned - two styles
    /// that set the same box model hold the same instance, and a rebuild that only moved the colours
    /// hands every untouched box back the object it already had. That is what makes the question cheap
    /// enough to ask of every box of the document on every state change.
    /// <para>
    /// What is deliberately not here is what only the painter reads: the colours, the background layers,
    /// the cursor, the outline and the text decoration. A style that differs in those alone lays its box
    /// out the same way and paints it differently, which is the case a restyle-in-place exists for.
    /// </para>
    /// <para>
    /// What this cannot answer is what is not an area: the positioning, the flex, grid and table values,
    /// the transform, the background clip and the overflow are built on demand and share nothing, so two
    /// styles that agree on every area can still lay a box out differently. Those are settled once for
    /// the whole document instead - see <see cref="Boxes.StyleResolution.RestylesWithoutRelayout"/> -
    /// and a box is only ever asked this question when that gate is open.
    /// </para>
    /// </remarks>
    internal Boolean SameLayoutAreas(ComputedStyle other) =>
        ReferenceEquals(_values.BoxModel, other._values.BoxModel) &&
        ReferenceEquals(_values.Font, other._values.Font) &&
        ReferenceEquals(_values.Text, other._values.Text) &&
        ReferenceEquals(_values.Border, other._values.Border) &&
        ReferenceEquals(_values.DisplayPositioning, other._values.DisplayPositioning) &&
        ReferenceEquals(_values.List, other._values.List);

    /// <summary>
    /// Gets whether this style hands a descendant exactly what <paramref name="other"/> handed it:
    /// every area a child reads off its parent is the very same instance, and every keyword that
    /// travels down is the same value.
    /// </summary>
    /// <remarks>
    /// A parent that was resolved again does not have to say so. The question that matters is not
    /// "was I computed over" but "is the answer still the answer" - and a mark that moved very often
    /// leaves it alone: a rule that paints a link red says nothing to the <c>html</c> above it, which
    /// is recomputed and comes to every value it already had. A parent that says so lets its whole
    /// subtree stand down; a parent that says nothing makes every descendant resolve again only to be
    /// handed back what it had, which is what a pointer crossing one link costs today.
    /// <para>
    /// What is compared is exactly what the builder hands down, no more and no less: the four areas a
    /// child's cached font, text, list and border are keyed on, the display area that says whether it
    /// is a flex or grid item, and the keywords that are inherited or carried - the colour, the flow,
    /// the annotation position, the cursor and the text stroke and fill. The rest of the keywords
    /// belong to the box that holds them and travel nowhere, so a style that moved them hands its
    /// subtree the inheritance it always had.
    /// </para>
    /// </remarks>
    internal Boolean SameInheritance(ComputedStyle other)
    {
        var mine = _values;
        var theirs = other._values;

        return ReferenceEquals(mine.Font, theirs.Font) &&
            ReferenceEquals(mine.Text, theirs.Text) &&
            ReferenceEquals(mine.List, theirs.List) &&
            ReferenceEquals(mine.Border, theirs.Border) &&
            ReferenceEquals(mine.DisplayPositioning, theirs.DisplayPositioning) &&
            SameInheritedKeywords(mine.Fused, theirs.Fused);
    }

    private static Boolean SameInheritedKeywords(FusedKeywords? mine, FusedKeywords? theirs)
    {
        if (mine is null || theirs is null)
        {
            return mine is null && theirs is null;
        }

        // The colour is compared by value rather than by reference because two declarations of the
        // same colour are two instances of it, and the one that was not declared at all is the
        // parent's instance. Either way the value is what a child inherits.
        return mine.Color.Equals(theirs.Color) &&
            mine.WritingMode == theirs.WritingMode &&
            mine.Direction == theirs.Direction &&
            mine.Orientation == theirs.Orientation &&
            mine.CombinesUpright == theirs.CombinesUpright &&
            mine.Ruby == theirs.Ruby &&
            mine.Cursor == theirs.Cursor &&
            mine.TextStrokeWidth == theirs.TextStrokeWidth &&
            mine.TextStrokeColor == theirs.TextStrokeColor &&
            SameShadows(mine.TextShadows, theirs.TextShadows) &&
            mine.TextFillColor == theirs.TextFillColor;
    }

    /// <summary>
    /// Gets whether two shadow lists draw the same thing. The answer is by value rather than by reference,
    /// because two elements that each wrote the same declaration hold two lists of it; a box that wrote
    /// nothing holds its parent's instance, which is where the reference test pays.
    /// </summary>
    private static Boolean SameShadows(IReadOnlyList<Shadow> mine, IReadOnlyList<Shadow> theirs)
    {
        if (ReferenceEquals(mine, theirs))
        {
            return true;
        }

        if (mine.Count != theirs.Count)
        {
            return false;
        }

        for (var index = 0; index < mine.Count; index++)
        {
            if (mine[index] != theirs[index])
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Gets what an item of this element's list is marked with, as the name or string written.</summary>
    public String ListStyleType => _values.List.Type;

    /// <summary>Gets whether an item's marker stands beside its text or at the head of its first line.</summary>
    public MarkerPosition ListStylePosition => _values.List.Position;

    /// <summary>Gets the image an item is marked with, or null for a glyph or a counter.</summary>
    public String? ListStyleImage => _values.List.Image;

    /// <summary>Gets the value this element resets the `list-item` counter to, or null if it does not.</summary>
    public Int32? ListItemReset => _values.List.ListItemReset;

    /// <summary>Gets the value this element sets the existing `list-item` counter to, or null if it does not.</summary>
    public Int32? ListItemSet => _values.List.ListItemSet;

    /// <summary>Gets by how much this element moves the `list-item` counter, or null to take the list's default step.</summary>
    public Int32? ListItemIncrement => _values.List.ListItemIncrement;

    public String WhiteSpace => _values.Text.WhiteSpace;

    /// <summary>Gets the length form of <c>tab-size</c> in pixels, or null where the box counts characters.</summary>
    public Double? TabLength => _values.Text.TabLength;

    /// <summary>Gets how many character widths a tab runs the line on, the count form of <c>tab-size</c>.</summary>
    public Double TabUnits => _values.Text.TabUnits;

    /// <summary>Gets whether this box is seen, as the element and the ancestors above it settled it.</summary>
    public String Visibility => _values.Text.Visibility;

    /// <summary>Gets which ends of the block have their leading trimmed away (CSS Inline 3).</summary>
    public TextBoxTrim TextBoxTrim => _values.Text.Trim;

    /// <summary>Gets the line the block's start is trimmed to.</summary>
    public TextBoxEdge TextBoxEdgeStart => _values.Text.EdgeStart;

    /// <summary>Gets the line the block's end is trimmed to.</summary>
    public TextBoxEdge TextBoxEdgeEnd => _values.Text.EdgeEnd;

    /// <summary>
    /// Gets whether the box draws nothing of itself. <c>collapse</c> is counted in: a box that keeps its
    /// space and paints no lines of its own is painted the same way here as one that is hidden.
    /// </summary>
    /// <remarks>
    /// Layout is untouched by this - a hidden box holds its ground and its descendants' geometry stands -
    /// so it is only the painting and the pointing that ask.
    /// </remarks>
    public Boolean IsHidden => !_values.Text.Visibility.Equals("visible", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Gets whether runs of white space collapse to a single space, which is the case for every
    /// value except the <c>pre</c> family.
    /// </summary>
    public Boolean IsCollapsibleWhiteSpace =>
        WhiteSpace.Equals("normal", StringComparison.OrdinalIgnoreCase) ||
        WhiteSpace.Equals("nowrap", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Gets whether the spaces of this white-space mode collapse away: every mode but the pre spellings
    /// that keep them. <c>pre-line</c> keeps its breaks and still collapses the spaces around them.
    /// </summary>
    public Boolean CollapsesSpaces =>
        WhiteSpace.Equals("normal", StringComparison.OrdinalIgnoreCase) ||
        WhiteSpace.Equals("nowrap", StringComparison.OrdinalIgnoreCase) ||
        WhiteSpace.Equals("pre-line", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Gets whether a segment break in this element's text stands as a line break of its own: every
    /// <c>pre</c> spelling and <c>break-spaces</c> keep it, while <c>normal</c> and <c>nowrap</c> fold it
    /// into the white space around it. <c>pre-line</c> collapses the spaces and keeps the breaks.
    /// </summary>
    public Boolean KeepsSegmentBreaks => !IsCollapsibleWhiteSpace;

    /// <summary>
    /// Gets whether a line may break inside this element's text where the text itself offers an
    /// opportunity to - between words, beside a boundary a script allows, or around an atomic inline.
    /// </summary>
    /// <remarks>
    /// CSS 2.1 §16.6 and CSS Text 3 §5.1 give two values that suppress those breaks: <c>nowrap</c>,
    /// which collapses white space exactly as <c>normal</c> does but keeps the text on one line, and
    /// <c>pre</c>, which breaks only where the author wrote a newline and preserves the spaces around
    /// it. Text under either overflows its line rather than taking a second one - and with
    /// <c>overflow: hidden</c> the overflowing text is what the box clips, which is how a browser
    /// shows a long label cut off instead of a paragraph the page never asked for.
    /// </remarks>
    public Boolean WrapsLines =>
        !WhiteSpace.Equals("nowrap", StringComparison.OrdinalIgnoreCase) &&
        !WhiteSpace.Equals("pre", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Gets whether a run of preserved white space breaks between its own characters:
    /// CSS Text 3 §4.1.3 gives <c>break-spaces</c> a soft wrap opportunity after every preserved white
    /// space character, adjacent spaces included, where every other mode has one only after the whole
    /// run. The run's first character is not offered one either way - a break before a space is not a
    /// break this property can make - so a space that does not fit at all stays where it was written and
    /// overflows, which is the case the specification's own note calls unavoidable.
    /// </summary>
    public Boolean WrapsInsideSpaces =>
        WhiteSpace.Equals("break-spaces", StringComparison.OrdinalIgnoreCase);

    /// <summary>Gets how a word too long for its line is treated, as <c>overflow-wrap</c> settled it.</summary>
    public String OverflowWrap => _values.Text.OverflowWrap;

    /// <summary>Gets how the script's own word rules are applied, as <c>word-break</c> settled it.</summary>
    public String WordBreak => _values.Text.WordBreak;

    /// <summary>Gets how strictly the breaking rules are read, as <c>line-break</c> settled it.</summary>
    public String LineBreak => _values.Text.LineBreak;

    /// <summary>
    /// Gets whether a line may end between any two characters of this element's text at all:
    /// <c>line-break: anywhere</c> sets the breaking rules themselves aside, the ones about punctuation
    /// included (CSS Text 3 §5.4), and the breaks it gives are counted as the narrowest size.
    /// </summary>
    public Boolean BreaksAnywhere => LineBreak.Equals("anywhere", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Gets whether this element's text offers a break between any two of its letters:
    /// <c>word-break: break-all</c> puts the opportunity between the typographic letter units and leaves
    /// the rules about white space and about punctuation standing, which is what the property says of
    /// itself (CSS Text 3 §5.6). The breaks count as the narrowest size.
    /// </summary>
    public Boolean BreaksBetweenLetters =>
        WordBreak.Equals("break-all", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Gets whether this element's text is to be held together within its words:
    /// <c>word-break: keep-all</c> sets the implicit opportunity between two of the letter units aside,
    /// so a run the script would break between every one of its characters runs on as one word instead,
    /// while the opportunities white space and the rules about punctuation make are left standing
    /// (CSS Text 3 §5.6).
    /// </summary>
    /// <remarks>
    /// <c>line-break: anywhere</c> is the one setting that says otherwise: it sets the breaking rules
    /// themselves aside rather than trimming the opportunities they give, and the property says so of
    /// itself - "regardless of line-break settings other than anywhere".
    /// </remarks>
    public Boolean KeepsWordsWhole =>
        WordBreak.Equals("keep-all", StringComparison.OrdinalIgnoreCase) && !BreaksAnywhere;

    /// <summary>
    /// Gets what this element's text asks of the breaks inside its words, which is how the line-breaking
    /// rules are to be read (CSS Text 3 §5.6).
    /// </summary>
    /// <remarks>
    /// <c>line-break: anywhere</c> answers first and alone: it sets the rules aside rather than trimming
    /// the opportunities they give, so no value of <c>word-break</c> has anything left to say about where
    /// a line may end.
    /// </remarks>
    public LineBreaks.WordBreaking WordBreakingRules =>
        BreaksAnywhere
            ? LineBreaks.WordBreaking.Normal
            : BreaksBetweenLetters
                ? LineBreaks.WordBreaking.BreakAll
                : KeepsWordsWhole
                    ? LineBreaks.WordBreaking.KeepAll
                    : LineBreaks.WordBreaking.Normal;

    /// <summary>
    /// Gets whether a word that will not fit on a line of its own may be broken to keep it from overflowing
    /// the box (CSS Text 3 §5.5). Where this is the only licence, the word is moved to a line of its own
    /// first: the break inside it is the last resort the property names, not a filling of the room a
    /// previous opportunity left. <c>word-break: break-word</c> is the legacy spelling of the licence, read
    /// as <c>overflow-wrap: anywhere</c> - which of the three a page wrote never changed how the line broke,
    /// measured.
    /// </summary>
    public Boolean WrapsLongWords =>
        OverflowWrap.Equals("break-word", StringComparison.OrdinalIgnoreCase) ||
        OverflowWrap.Equals("anywhere", StringComparison.OrdinalIgnoreCase) ||
        WordBreak.Equals("break-word", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Gets whether the breaks inside a word are counted as the content's narrowest size.
    /// <c>overflow-wrap: anywhere</c> and the legacy <c>word-break: break-word</c> say they are where
    /// <c>overflow-wrap: break-word</c> says it is not (CSS Text 3 §5.5, and Chrome's min-content of the
    /// same word under the three spellings is one character, one character and the whole of the word).
    /// </summary>
    public Boolean CountsLongWordBreaks =>
        OverflowWrap.Equals("anywhere", StringComparison.OrdinalIgnoreCase) ||
        WordBreak.Equals("break-word", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Gets whether an opportunity exists before the first character of a run of preserved white space,
    /// which the white-space mode alone never offers. <c>overflow-wrap</c> in either of its two values,
    /// <c>word-break: break-word</c> and <c>line-break: anywhere</c> give it, to keep a line from
    /// overflowing; <c>word-break: break-all</c> does not, because the rule it sets aside is the one about
    /// words.
    /// </summary>
    public Boolean WrapsBeforeSpaces => WrapsLongWords || BreaksAnywhere;

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
    /// Resolves a relative <c>font-size</c> against the parent's font. A percentage is a share of its
    /// size; an <c>em</c> is a multiple of it; <c>ex</c> and <c>ch</c> are heights of the parent's face,
    /// when the caller has that face - and the half of its size when it has only the size.
    /// </summary>
    private Double ResolveFontRelative(CssLengthValue value) =>
        ResolveFontRelative(value, new LengthReader.FontSource(_parentFontSize), _dimensions);

    private static Double ResolveFontRelative(
        CssLengthValue value, LengthReader.FontSource parent, IRenderDimensions dimensions)
    {
        // The root is the one element with no parent to take a size from, and its share is of the
        // initial value rather than of the size it is deciding - which is also why the device is not
        // asked here: by the time a child is read the device carries the root's size, and the root's
        // own was resolved while it still carried the initial one.
        var reference = parent.Value > 0 ? parent.Value : HutuRenderDevice.InitialFontSize;

        // The parent's face, not the size being decided: a child written in ex is measured by the
        // lowercase its parent was set in.
        if (LengthReader.FaceMeasure(value.Type, dimensions, parent) is { } measure)
        {
            return value.Value * measure;
        }

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
    /// Gets how the last line of a block, and a line a forced break ended, are aligned. <c>auto</c> says
    /// the line takes <c>TextAlign</c> unless that justifies it, in which case the line keeps its ragged
    /// edge at the start (CSS Text 3 §6.3).
    /// </summary>
    public String TextAlignLast => _values.Text.AlignLast;

    /// <summary>
    /// Gets where a justified line may put its room. <c>auto</c>, the initial value, is not a method of
    /// its own but the specification's instruction to expand at spaces and, where the text is written
    /// without word separators, between its characters too (CSS Text 3 §3.3.1).
    /// </summary>
    public String TextJustify => _values.Text.TextJustify;

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

    /// <summary>
    /// Gets the room added between words, in pixels. The space is added to each word separator drawn -
    /// which after the document's own white space processing is one per place two words meet, however
    /// many spaces were written there - and to none of the other space separators the eye reads as a gap,
    /// which is what the property says it leaves to the font.
    /// </summary>
    public Double WordSpacing => _values.Font.WordSpacing;

    public String VerticalAlign => _values.Text.VerticalAlign;

    /// <summary>
    /// Gets the writing mode the element runs its text in. <c>writing-mode</c> is a registered,
    /// inherited property, so its computed value already carries what an ancestor asked for and this
    /// answers with the mode the element itself settles on.
    /// </summary>
    public WritingMode WritingMode => _values.Fused is { } fused
        ? fused.WritingMode
        : ReadKeyword("writing-mode")?.ToLowerInvariant() switch
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
            if (_values.Fused is { } fused)
            {
                return fused.Direction;
            }

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
    public TextOrientation? DeclaredTextOrientation => _values.Fused is { } fused
        ? fused.Orientation
        : ReadKeyword("text-orientation")?.ToLowerInvariant() switch
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
    public Boolean? DeclaredTextCombineUpright => _values.Fused is { } fused
        ? fused.CombinesUpright
        : ReadKeyword("text-combine-upright")?.ToLowerInvariant() switch
        {
            "all" => true,
            "none" => false,
            _ => null,
        };

    /// <summary>
    /// Gets the declared <c>unicode-bidi</c> keyword, or null where the page wrote none.
    /// </summary>
    internal String? UnicodeBidiKeyword => _values.Fused is { } fused
        ? fused.UnicodeBidi
        : ReadKeyword("unicode-bidi");

    /// <summary>
    /// Gets whether the element asks that its paragraphs take their direction from the text written in
    /// them rather than from the <c>dir</c> attribute or the <c>direction</c> property - the
    /// <c>plaintext</c> value of <c>unicode-bidi</c>, which is inherited like the property itself
    /// (CSS Display 3 §5.3.7, UAX#9 §3.4).
    /// </summary>
    public Boolean PlaintextParagraph =>
        String.Equals(UnicodeBidiKeyword?.Trim(), "plaintext", StringComparison.OrdinalIgnoreCase);

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
    public RubyPosition RubyPosition => _values.Fused is { } fused
        ? fused.Ruby
        : ReadKeyword("ruby-position")?.ToLowerInvariant() switch
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
            if (_values.Fused is { } fused)
            {
                return fused.Color;
            }

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
            if (_specified?.GetProperty("color")?.RawValue is not null)
            {
                return declared;
            }

            return PresentationalHints.TextColor(_element) ?? _parentColor ?? declared;
        }
    }

    /// <summary>
    /// Gets the background colour the element is painted with. A <c>currentcolor</c> written here is the
    /// element's own ink rather than the ink it was handed, and it is read at this value's time for the
    /// same reason: a page that inherits the keyword down a tree wants each box to paint in the colour of
    /// its own text (CSS Color 4 5).
    /// </summary>
    public CssColorValue? BackgroundColor => ResolveCurrentColor(CascadedBackgroundColor);

    /// <summary>
    /// Gets the background colour as the cascade left it, where <c>currentcolor</c> is still the keyword.
    /// This is what <c>background-color: inherit</c> hands down, so a box under one painted with its own
    /// ink is painted with its own rather than with the colour above.
    /// </summary>
    internal CssColorValue? CascadedBackgroundColor => _values.Fused is { } fused
        ? fused.BackgroundColor
        : ColorValue("background-color") ?? PresentationalHints.BackgroundColor(_element);

    /// <summary>
    /// Takes a colour that names the element's own ink and gives the ink, and leaves any other colour -
    /// and the absence of one - as it was.
    /// </summary>
    private CssColorValue? ResolveCurrentColor(CssColorValue? colour) =>
        colour == CssColorValue.CurrentColor ? Color : colour;

    /// <summary>
    /// Gets the background layers in declaration order. The first layer paints on top of the ones
    /// after it, so a painter has to emit them in reverse.
    /// </summary>
    public IReadOnlyList<BackgroundLayer> Backgrounds
    {
        get
        {
            if (_values.Fused is { } fused)
            {
                return fused.Backgrounds;
            }

            var specified = ParseBackgrounds(_specified);

            return specified.Count > 0 ? specified : ParseBackgrounds(_style);
        }
    }

    /// <summary>
    /// Gets the border image the box paints its border area with, or null where it has none
    /// (CSS Backgrounds 3 §6). The picture is a file or a gradient, cut into nine regions and drawn into
    /// the border image area, which is the border box grown by the outsets and takes no room in the layout.
    /// </summary>
    public BorderImageSpec? BorderImage
    {
        get
        {
            if (_values.Fused is { } fused)
            {
                return fused.BorderImage;
            }

            var element = _element;
            var resolve = element is null ? null : new Func<String, String?>(url => ImageUrlResolver.Resolve(element, url));

            // The specified declaration is asked first, since a computed one has already folded the
            // lengths it states; where it holds a wide keyword instead - `inherit`, which is how a border
            // image carries down a tree - the computed declaration is the one that has the answer.
            return BorderImageParser.Parse(
                Value("border-image-source"),
                Value("border-image-slice"),
                Value("border-image-width"),
                Value("border-image-outset"),
                Value("border-image-repeat"),
                new Thickness(ResolvedBorder.TopWidth, ResolvedBorder.RightWidth, ResolvedBorder.BottomWidth, ResolvedBorder.LeftWidth),
                _dimensions,
                FontSize,
                resolve);
        }
    }

    /// <summary>
    /// Reads one border image longhand off the declaration the style was written in, falling back to the
    /// computed one where the author wrote none.
    /// </summary>
    private String? Value(String name) =>
        _specified?.GetPropertyValue(name) is { Length: > 0 } stated
            ? stated
            : _style?.GetPropertyValue(name);

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
            BackgroundLayerParser.CombineAxes(
                declaration.GetPropertyValue("background-position"),
                declaration.GetPropertyValue("background-position-x"),
                declaration.GetPropertyValue("background-position-y")),
            declaration.GetPropertyValue("background-size"),
            BackgroundLayerParser.CombineAxes(
                declaration.GetPropertyValue("background-repeat"),
                declaration.GetPropertyValue("background-repeat-x"),
                declaration.GetPropertyValue("background-repeat-y")),
            _dimensions,
            FontSize,
            element is null ? null : url => ImageUrlResolver.Resolve(element, url));
    }

    /// <summary>
    /// Gets the shadows the box is drawn with, in the order the page wrote them - and the first of those is
    /// the one that ends up on top (CSS Backgrounds 3 §6.1.3).
    /// </summary>
    public IReadOnlyList<Shadow> BoxShadows
    {
        get
        {
            var written = WrittenBoxShadows;

            return AnyFollowsInk(written) ? _values.BoxPaint ??= Inked(written) : written;
        }
    }

    /// <summary>
    /// Gets the shadows the element's text is drawn with. The property is inherited, so an ancestor that
    /// wrote a shadow gives it to every word below it as surely as the element that wrote it.
    /// </summary>
    public IReadOnlyList<Shadow> TextShadows
    {
        get
        {
            var written = WrittenTextShadows;

            return AnyFollowsInk(written) ? _values.TextPaint ??= Inked(written) : written;
        }
    }

    /// <summary>
    /// Gets the box's shadows as the declaration left them, with any colour that asks for the ink still
    /// asking. This is what a child inherits, and what the accessor above settles to each box's own ink.
    /// </summary>
    internal IReadOnlyList<Shadow> WrittenBoxShadows =>
        _values.Fused is { } fused ? fused.BoxShadows : CascadedShadows("box-shadow");

    /// <summary>Gets the text's shadows as the declaration left them. See <see cref="WrittenBoxShadows"/>.</summary>
    internal IReadOnlyList<Shadow> WrittenTextShadows =>
        _values.Fused is { } fused ? fused.TextShadows : CascadedShadows("text-shadow");

    /// <summary>
    /// Gets whether any shadow of the list asks for the ink of the box it is drawn with - which most do not,
    /// and a list that none does is already the answer and needs no copy.
    /// </summary>
    private static Boolean AnyFollowsInk(IReadOnlyList<Shadow> shadows)
    {
        for (var index = 0; index < shadows.Count; index++)
        {
            if (shadows[index].FollowsInk)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Gives every shadow that asks for it the ink of this element, leaving the rest as they were.
    /// </summary>
    private IReadOnlyList<Shadow> Inked(IReadOnlyList<Shadow> shadows)
    {
        var ink = ToArgb(Color);

        var settled = new List<Shadow>(shadows.Count);

        for (var index = 0; index < shadows.Count; index++)
        {
            var shadow = shadows[index];

            settled.Add(shadow.FollowsInk ? shadow with { Color = ink, FollowsInk = false } : shadow);
        }

        return settled;
    }

    /// <summary>
    /// Reads a shadow list off the declarations of a style that was not built fused.
    /// </summary>
    /// <remarks>
    /// The specified declaration is asked first, because a computed one has already folded the element's
    /// own <c>em</c> against the root rather than the size the length is written in. Where the author put a
    /// wide keyword there instead of a list - and <c>inherit</c>, which is what carries a text shadow down a
    /// tree, is the usual one - the answer is not in it, so the computed declaration is read: it has
    /// resolved the inheritance already.
    /// </remarks>
    private IReadOnlyList<Shadow> CascadedShadows(String name)
    {
        var font = FontSourceOf();

        return ReadShadowList(_specified?.GetProperty(name)?.RawValue, _dimensions, font, Color) ??
            ReadShadowList(_style.GetProperty(name)?.RawValue, _dimensions, font, Color) ??
            Array.Empty<Shadow>();
    }

    /// <summary>
    /// Turns a first letter into its initial-letter box: the letter is drawn at the font size the lines
    /// it spans ask for, stands on its own line's baseline, and floats so that the lines beside it are
    /// shortened until its box ends (CSS Inline 3 §3.2). Only a style the first-letter arm built is
    /// changed this way - it belongs to that one box and to the text inside it.
    /// </summary>
    /// <param name="width">
    /// The block extent the letter's box takes, or zero where it takes the width of the glyph - which is
    /// what a row does. A column states it, because a floated letter's block extent is its width.
    /// </param>
    /// <param name="extra">
    /// The margins the letter stands off by on top of the declared ones: the cap alignment along the
    /// block axis, and the line's indentation where the letter stands at the start of a row.
    /// </param>
    internal void MakeInitialLetter(
        Double fontSize,
        Double lineHeight,
        Double width,
        Double height,
        Thickness extra,
        FloatSide side,
        StyleThickness indent)
    {
        // The letter sits where the line's own indent has already put it, and §7.8.2 shifts the start of
        // the line's contents again by that indent - so the two add up, which Chrome measures as twice
        // the text-indent and the WPT pairs draw as such. A declared margin is kept and added to.
        var margin = _values.BoxModel.Margin;

        StyleLength With(StyleLength declared, Double offset, StyleLength indented) =>
            offset == 0 && indented.IsAuto
                ? declared
                : indented.IsAuto
                    ? StyleLength.Sum(new[] { declared, StyleLength.FromPixels(offset) })
                    : StyleLength.Sum(new[] { declared, StyleLength.FromPixels(offset), indented });

        _values.Font = _values.Font with { Size = fontSize, LineHeight = lineHeight };
        _values.BoxModel = _values.BoxModel with
        {
            Width = width > 0 ? StyleLength.FromPixels(width) : StyleLength.Auto,
            WidthDeclared = width > 0,
            Height = StyleLength.FromPixels(height),
            HeightDeclared = true,
            Margin = new StyleThickness(
                With(margin.Top, extra.Top, indent.Top),
                With(margin.Right, extra.Right, indent.Right),
                With(margin.Bottom, extra.Bottom, indent.Bottom),
                With(margin.Left, extra.Left, indent.Left)),
        };
        _values.DisplayPositioning = _values.DisplayPositioning with { Float = side, Display = "block" };
    }

    /// <summary>
    /// Moves a box's top margin out by one more length, which is how the lines an initial letter
    /// stands over are pushed down by the lines it does not sink through (CSS Inline 3 §7.6.1).
    /// </summary>
    internal void AddTopMargin(StyleLength extra)
    {
        var margin = _values.BoxModel.Margin;

        _values.BoxModel = _values.BoxModel with
        {
            Margin = new StyleThickness(
                StyleLength.Sum(new[] { margin.Top, extra }),
                margin.Right,
                margin.Bottom,
                margin.Left),
        };
    }

    /// <summary>Gets whether a broken box is wrapped independently on every piece.</summary>
    public Boolean ClonesDecoration =>
        _values.Fused is { } fused
            ? fused.ClonesDecoration
            : String.Equals(
                (_specified?.GetPropertyValue("box-decoration-break") ?? _style?.GetPropertyValue("box-decoration-break"))?.Trim(),
                "clone",
                StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Gets the rectangle the box cuts itself to, or null where it states none. The property reaches an
    /// absolutely positioned box and nothing else, and what it states are four distances rather than a
    /// rectangle - the box they measure has to be laid out before it has an edge to measure from, so the
    /// painter is the one that works out the area (CSS 2.1 §11.1.1, CSS Masking 1 §1.3).
    /// </summary>
    public ClipShape? Clip => _values.Fused is { } fused ? fused.Clip : CascadedClip();

    /// <summary>
    /// Gets the shape the box cuts its own picture to, or null where it states none. The numbers are folded
    /// against the box rather than against the style, so the area is only worked out when the box is painted
    /// (CSS Masking 1 §5.1, CSS Shapes 1 §3).
    /// </summary>
    public ClipPathShape? ClipPath =>
        _values.Fused is { } fused
            ? fused.ClipPath
            : ClipPathShape.Read(ClipPathText, _dimensions, FontSourceOf());

    /// <summary>Gets the words a <c>clip-path</c> declaration was written with, or null where it says nothing.</summary>
    public String? ClipPathText => _style.GetPropertyValue("clip-path");

    /// <summary>
    /// Gets the shape the lines beside this float stand clear of, or null where it states none. The numbers
    /// are shares of a box the placement has not settled yet, so the area is worked out where the float is
    /// put down (CSS Shapes 1 §6.1).
    /// </summary>
    public ClipPathShape? ShapeOutside =>
        _values.Fused is { } fused
            ? fused.ShapeOutside
            : ClipPathShape.ReadForFloat(ShapeOutsideText, _dimensions, FontSourceOf());

    /// <summary>Gets the words a <c>shape-outside</c> declaration was written with.</summary>
    public String? ShapeOutsideText => _style.GetPropertyValue("shape-outside");

    /// <summary>
    /// Gets how much further out a float's shape is stood, or null where the box states none. A percentage is
    /// a share of the containing block's inline size, which only the placement has, so the distance is kept
    /// as it was written (CSS Shapes 1 §6.3).
    /// </summary>
    public StyleLength? ShapeMargin =>
        _values.Fused is { } fused ? ReadShapeMargin(fused.ShapeMargin) : ReadDeclared("shape-margin", RenderMode.Horizontal);

    private StyleLength? ReadShapeMargin(ICssValue? stated) =>
        stated is not null &&
        LengthReader.TryReadValue(stated, RenderMode.Horizontal, _dimensions, FontSourceOf(), out var length)
            ? length
            : null;

    /// <summary>
    /// Resolves the shape a <c>shape-outside</c> declaration states for the fused path. The property is not
    /// inherited, and <c>inherit</c> is the one answer that reaches down and hands a float the shape of the
    /// box it came out of.
    /// </summary>
    public static ClipPathShape? ResolveShapeOutsideFused(
        ICssProperty? declared,
        IRenderDimensions dimensions,
        LengthReader.FontSource font,
        ClipPathShape? parent)
    {
        if (declared is null)
        {
            return null;
        }

        if (IsInheritWord(declared.RawValue))
        {
            return parent;
        }

        return ClipPathShape.ReadForFloat(declared.RawValue?.CssText, dimensions, font);
    }

    /// <summary>
    /// Gets whether the box cuts its own picture to a shape - which is what puts it on a layer of its own
    /// and keeps everything it paints inside the cut, whatever the shape turns out to be.
    /// </summary>
    public Boolean HasClipPath =>
        _values.Fused is { } fused
            ? fused.ClipPath is not null
            : SaysAClip(ClipPathText);

    private static Boolean SaysAClip(String? stated) =>
        stated is { Length: > 0 } && !stated.Equals("none", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Reads the clip off the declarations of a style that was not built fused. The shape is the only
    /// answer this can give: <c>auto</c> and <c>inherit</c> arrive as keywords, and the computed
    /// declaration has resolved the inheritance the same way it does for any other property.
    /// </summary>
    private ClipShape? CascadedClip()
    {
        var font = FontSourceOf();

        if (_specified?.GetProperty("clip")?.RawValue is CssShapeValue stated)
        {
            return ReadShape(stated, _dimensions, font);
        }

        return _style.GetProperty("clip")?.RawValue is CssShapeValue computed
            ? ReadShape(computed, _dimensions, font)
            : null;
    }

    public Boolean HasUnderline => _values.Decoration.Underline;

    public Boolean HasOverline => _values.Decoration.Overline;

    public Boolean HasLineThrough => _values.Decoration.LineThrough;

    /// <summary>Gets the shape the element's decoration lines are drawn in.</summary>
    public DecorationShape Decoration => _values.Decoration.Style;

    /// <summary>
    /// Gets the colour the element asked its decoration to be drawn in, or null where it asked for none
    /// and its own text colour is what the line takes (CSS Text Decoration 4 §2.3).
    /// </summary>
    public UInt32? DecorationColor => _values.Decoration.Color;

    /// <summary>Gets how thick this element's decoration lines are, or null where the face decides.</summary>
    public Double? DecorationThickness => _values.Decoration.Thickness;

    /// <summary>
    /// Gets how far this element's underlines stand from where the font puts them, or null where the face
    /// decides. Inherited, and read only for the underlines this element asks for.
    /// </summary>
    public Double? UnderlineOffset => _values.Decoration.UnderlineOffset;

    /// <summary>
    /// Gets how far back the ends of this element's own lines stand, or null where it said nothing: the
    /// distance is settled once the text the decoration runs across has been laid out, since a percentage
    /// is a share of that inline size (CSS Text Decoration 4 §2.9.1).
    /// </summary>
    internal DecorationInsets? DecorationInset => _values.Decoration.Inset;

    /// <summary>
    /// Gets whether this element's underlines and overlines are broken where the glyphs' ink crosses them
    /// (CSS Text Decoration 4 §2.10.4).
    /// </summary>
    internal DecorationSkip SkipInk => _values.Decoration.Skip;

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
            if (_values.Fused is { } fused)
            {
                return fused.Cursor;
            }

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
    /// Gets the colour the engine paints a link in: the colour the page hints at with the <c>body</c>
    /// element's legacy attributes, or blue for a link that has not been followed, purple for one that
    /// has, and red for the one being followed right now.
    /// </summary>
    private CssColorValue LinkColor => ResolveLinkColor(_element);

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
    /// <remarks>
    /// A fused style cannot be read, so it is handed the longhands the box builder resolved - and it
    /// is handed them only for a box that is a flex container or a flex item, which is every box the
    /// flex context asks about. Anything else gets the initial values rather than a read that would
    /// find no declaration.
    /// </remarks>
    public FlexStyle Flex =>
        _values.Fused is null ? _values.Flex ??= FlexStyle.Read(this) : _values.Flex ?? FlexStyle.Initial;

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
    /// <remarks>
    /// A fused style cannot be read, so it is handed the longhands the box builder resolved - and
    /// only for a grid container, which is the only box that asks. Anything else gets the initial
    /// values rather than a read that would find no declaration.
    /// </remarks>
    internal GridStyle Grid =>
        _values.Fused is null ? _values.Grid ??= GridStyle.Read(this) : _values.Grid ?? GridStyle.Initial;

    /// <summary>
    /// Gets where a grid item is placed and how it aligns itself, which the old path reads from the
    /// declaration and the fused path resolves with the rest of the item's style.
    /// </summary>
    internal GridItemStyle GridItem =>
        _values.Fused is null
            ? _values.GridItem ??= GridItemStyle.Read(this)
            : _values.GridItem ?? GridItemStyle.Auto;

    /// <summary>
    /// Gets the table longhands. <c>border-collapse</c>, <c>border-spacing</c> and friends only
    /// matter on a table box; <c>vertical-align</c> is meaningful on a cell.
    /// </summary>
    /// <remarks>
    /// A fused style carries no declaration, so the reader below cannot answer for it - it is handed
    /// the table longhands the box builder resolved instead. One that was not given any is not a
    /// table box, and the initial values are what every longhand of a box that is not one comes to.
    /// </remarks>
    public MulticolStyle Multi =>
        _values.Fused is null ? default : _values.Multi ?? default;

    /// <summary>
    /// Gets the decorations the box's gaps ask for. Only a box that puts a gap between anything - a flex
    /// or grid container, a grid lanes one, a multi-column one - is read for them; every other box gets
    /// the set whose rules all have no style, which draws nothing.
    /// </summary>
    public GapRules Gap =>
        _values.Fused is null ? _values.Gap ??= GapRules.Read(this) : _values.Gap ?? GapRules.None;

    /// <summary>
    /// Gets the size containment the box asks for, and the intrinsic size it states for the axes it
    /// contains. A box that writes no <c>contain</c> gets a value that contains nothing.
    /// </summary>
    public ContainStyle Contain
    {
        get
        {
            if (_values.Fused is not null)
            {
                return _values.Contain ?? default;
            }

            if (_values.Contain is { } known)
            {
                return known;
            }

            var declaration = Declaration;
            var font = new LengthReader.FontSource(this);
            var contain = ContainStyle.ReadFused(name => declaration?.TryGetProperty(name), font, Dimensions) ?? default;

            _values.Contain = contain;

            return contain;
        }
    }

    /// <summary>Gets where the box lets a fragmentainer break around and inside it.</summary>
    public BreakStyle Breaks => _values.Breaks;

    /// <summary>Gets whether the box asks to span every column of the multi-column container it stands in.</summary>
    public Boolean SpansColumns => _values.Breaks.SpansColumns;

    public TableStyle Table =>
        _values.Fused is null
            ? _values.Table ??= TableStyle.Read(this, PresentationalHints.Spacing(_element) ?? 0, PresentationalHints.Collapses(_element))
            : _values.Table ?? default;

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

    /// <summary>
    /// Gets whether the page spoke about the width, as opposed to the initial <c>auto</c> standing in for
    /// silence. A replaced box asks, because an element's own <c>width</c> attribute answers only where
    /// nothing was said - an author's <c>width: auto</c> is a request that the content decide, and the
    /// attribute is not a reply to it.
    /// </summary>
    public Boolean HasDeclaredWidth => _values.BoxModel.WidthDeclared;

    /// <summary>Gets whether the page spoke about the height. See <see cref="HasDeclaredWidth"/>.</summary>
    public Boolean HasDeclaredHeight => _values.BoxModel.HeightDeclared;

    /// <summary>
    /// Gets the ratio the width and height keep to, or null where the page named none.
    /// </summary>
    /// <remarks>
    /// A box with a ratio and one definite axis takes the other from it (CSS Sizing 4 §4).
    /// </remarks>
    public AspectRatio? AspectRatio => _values.BoxModel.AspectRatio;

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
            if (_values.Fused is { } fused)
            {
                return fused.BackgroundClip;
            }

            // One keyword per background layer, and a box that paints a single background layer can
            // only be told apart by the first one.
            var value = ReadKeyword("background-clip") ?? String.Empty;
            var comma = value.IndexOf(',');
            var keyword = (comma < 0 ? value : value[..comma]).Trim();

            return keyword.ToLowerInvariant() switch
            {
                "padding-box" => BackgroundClip.PaddingBox,
                "content-box" => BackgroundClip.ContentBox,
                "text" => BackgroundClip.Text,
                _ => BackgroundClip.BorderBox,
            };
        }
    }

    /// <summary>
    /// Gets the width the text stroke is drawn at, in pixels; zero when the page asked for none. It is
    /// carried down the tree, for the stroke is inherited and a text run only knows its own through the
    /// element it belongs to.
    /// </summary>
    internal Double TextStrokeWidth => _values.Fused is { } fused ? fused.TextStrokeWidth : 0;

    /// <summary>Gets the packed ARGB of the text stroke's colour; transparent when none was said.</summary>
    internal UInt32 TextStrokeColor => _values.Fused is { } fused ? fused.TextStrokeColor : 0;

    /// <summary>
    /// Gets the colour the glyph body is filled with where <c>-webkit-text-fill-color</c> set one, or null
    /// to let the element's own <c>color</c> paint it.
    /// </summary>
    internal UInt32? TextFillColor => _values.Fused is { } fused ? fused.TextFillColor : null;

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

    /// <summary>Gets the keyword the box's inline overflow axis was left with.</summary>
    public String OverflowX => _values.DisplayPositioning.OverflowX;

    /// <summary>Gets the keyword the box's block overflow axis was left with.</summary>
    public String OverflowY => _values.DisplayPositioning.OverflowY;

    /// <summary>Gets whether the inline (x) axis is a scroll axis, which zeroes a flex/grid item's
    /// automatic minimum size on that axis. <c>clip</c> does not count (it sizes like <c>visible</c>).</summary>
    public Boolean ScrollsInline => _values.DisplayPositioning.ScrollsInline;

    /// <summary>Gets whether the block (y) axis is a scroll axis, by the same rule as <see cref="ScrollsInline"/>.</summary>
    public Boolean ScrollsBlock => _values.DisplayPositioning.ScrollsBlock;

    /// <summary>
    /// Gets where the content stands along the block axis, as <c>align-content</c> asks. A block
    /// container lays its content out as one subject, so this moves it rather than sharing space out
    /// between several (CSS Box Alignment 3 §5.4) - which is how a form control's label ends up in the
    /// middle of the box the control was given (HTML §15.3.10).
    /// </summary>
    public FlexAlignment AlignContent => _values.DisplayPositioning.AlignContent;

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

    public CssColorValue? BorderTopColor => _values.Fused is { } fused
        ? fused.BorderTopColor
        : ResolveBorderColor(ColorValue(BorderSide(AxisSides(Flow).Top, "top", "color")));

    public CssColorValue? BorderRightColor => _values.Fused is { } fused
        ? fused.BorderRightColor
        : ResolveBorderColor(ColorValue(BorderSide(AxisSides(Flow).Right, "right", "color")));

    public CssColorValue? BorderBottomColor => _values.Fused is { } fused
        ? fused.BorderBottomColor
        : ResolveBorderColor(ColorValue(BorderSide(AxisSides(Flow).Bottom, "bottom", "color")));

    public CssColorValue? BorderLeftColor => _values.Fused is { } fused
        ? fused.BorderLeftColor
        : ResolveBorderColor(ColorValue(BorderSide(AxisSides(Flow).Left, "left", "color")));

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
            if (_values.Fused is { } fused)
            {
                return fused.OutlineColor;
            }

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

    /// <summary>Gets whether a list sits inside another one, whose own margins the sample leaves it to.</summary>
    private static Boolean IsNested(IElement element) =>
        element.ParentElement?.LocalName is "ul" or "ol";

    /// <summary>
    /// Gets which box a declared width and height describe, from the element's own cascaded
    /// declaration, for the fused path.
    /// </summary>
    public static BoxSizing ResolveBoxSizingFused(ICssProperty? declared)
    {
        if (declared?.RawValue is CssConstantValue<AngleSharp.Css.Dom.BoxModel> constant &&
            constant.Value == AngleSharp.Css.Dom.BoxModel.BorderBox)
        {
            return BoxSizing.BorderBox;
        }

        return declared?.Value?.Trim().Equals("border-box", StringComparison.OrdinalIgnoreCase) == true
            ? BoxSizing.BorderBox
            : BoxSizing.ContentBox;
    }

    /// <summary>
    /// Reads a box-model length from the element's own cascaded declaration, for the fused
    /// path. Percentages stay unresolved for the layout pass.
    /// </summary>
    public static StyleLength ResolveBoxLengthFused(
        ICssProperty? declared, RenderMode mode, LengthReader.FontSource font, IRenderDimensions dimensions, StyleLength fallback)
    {
        if (declared is null)
        {
            return fallback;
        }

        // A length, or one of the content keywords: MaxContent is a size the box's content settles
        // rather than a length the reader can resolve, so it travels as itself and the layout engine
        // answers for it where it has the content in hand (CSS Sizing 3 §4).
        return LengthReader.TryReadProperty(declared, mode, dimensions, font, out var length) &&
            (length.HasValue || length.IsContentSized)
            ? length
            : fallback;
    }

    /// <summary>
    /// Reads a box-model thickness from the element's own cascaded declarations, for the
    /// fused path: physical sides, flow-relative axes, wide keywords, and the reader's own
    /// defaults for headings and paragraphs, mapped through the flow the caller hands over.
    /// </summary>
    public static StyleThickness ResolveThicknessFused(
        Func<String, ICssProperty?> find,
        IElement? element,
        LengthReader.FontSource font,
        IRenderDimensions dimensions,
        WritingModeInfo flow,
        String prefix,
        StyleLength fallback)
    {
        StyleLength? ReadSide(String name) =>
            find(prefix + name) is { } declared &&
            LengthReader.TryReadProperty(declared, RenderMode.Horizontal, dimensions, font, out var length)
                ? length
                : null;

        CssWideKeyword? SideKeyword(String name)
        {
            var raw = find(prefix + name)?.RawValue;

            // The inline parser carries `initial` as AngleSharp's own initial value; a rule
            // carries it as the reader's wide keyword. Both mean the same thing here.
            if (raw is CssWideKeyword wide)
            {
                return wide;
            }

            var text = raw?.CssText;

            return text is not null && CssWideKeyword.Is(text) ? new CssWideKeyword(text) : null;
        }

        var top = ReadSide("-top");
        var right = ReadSide("-right");
        var bottom = ReadSide("-bottom");
        var left = ReadSide("-left");

        var inlineStart = ReadSide("-inline-start");
        var inlineEnd = ReadSide("-inline-end");
        var blockStart = ReadSide("-block-start");
        var blockEnd = ReadSide("-block-end");

        var topKeyword = SideKeyword("-top");
        var rightKeyword = SideKeyword("-right");
        var bottomKeyword = SideKeyword("-bottom");
        var leftKeyword = SideKeyword("-left");
        var inlineStartKeyword = SideKeyword("-inline-start");
        var inlineEndKeyword = SideKeyword("-inline-end");
        var blockStartKeyword = SideKeyword("-block-start");
        var blockEndKeyword = SideKeyword("-block-end");

        var (defaultBlockStart, defaultBlockEnd, defaultInlineStart, defaultInlineEnd) =
            ReaderDefaults(prefix, element, font.Value);

        if (inlineStart is null && inlineEnd is null && blockStart is null && blockEnd is null &&
            inlineStartKeyword is null && inlineEndKeyword is null &&
            blockStartKeyword is null && blockEndKeyword is null &&
            defaultBlockStart is null && defaultBlockEnd is null &&
            defaultInlineStart is null && defaultInlineEnd is null)
        {
            return new StyleThickness(
                top ?? fallback,
                right ?? fallback,
                bottom ?? fallback,
                left ?? fallback);
        }

        if (flow.IsVertical)
        {
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

    private (StyleLength? BlockStart, StyleLength? BlockEnd, StyleLength? InlineStart, StyleLength? InlineEnd) Defaults(String prefix) =>
        ReaderDefaults(prefix, _element, _values.Font.Size);

    /// <summary>
    /// Gets what the reader supplies for an element the page says nothing about, as the four
    /// flow-relative distances: the block axis is the space between blocks, and the inline axis the
    /// space along the line.
    /// </summary>
    /// <remarks>
    /// These are the values the CSS 2.2 sample stylesheet states, which is where a browser's defaults
    /// come from as well - stated to the flow rather than to the page, so that a column reads the
    /// space between blocks beside itself instead of down it. Nothing is returned for an element HTML
    /// gives nothing to, or for a list inside a list, which the sample leaves to its outer one.
    /// <para>
    /// What lands here rather than in the sheet is decided by who has to yield: a value the sheet states
    /// is read back as a declaration of the page, since nothing in the cascade tells the two sheets
    /// apart, so a page that resets the same property would be overruled by the reader. A value supplied
    /// here is what the reader gives an unsaid side, which any word of the page's beats.
    /// </para>
    /// </remarks>
    private static (StyleLength? BlockStart, StyleLength? BlockEnd, StyleLength? InlineStart, StyleLength? InlineEnd) ReaderDefaults(
        String prefix, IElement? element, Double fontSize)
    {
        if (element is null)
        {
            return default;
        }

        var tag = element.LocalName.ToLowerInvariant();

        if (String.Equals(prefix, "padding", StringComparison.Ordinal))
        {
            // The room a list keeps at the edge its items begin at, which is where their markers hang.
            // A page that zeroes its list margins says nothing about it - so it is no margin - and a page
            // that zeroes its list padding means to take it back - so it is no sheet declaration either,
            // which would read as the page's own and win. Every level of nesting keeps its own, as a
            // browser's sheet gives it.
            return tag is "ul" or "ol" or "dir" or "menu"
                ? (null, null, Pixels(40), null)
                : default;
        }

        if (!String.Equals(prefix, "margin", StringComparison.Ordinal))
        {
            return default;
        }

        // A centred table is centred by the inline margins the standard's sheet gives it, and those
        // sit beside whatever block margins the reader above supplies.
        if (tag == "table" && PresentationalHints.CentersTable(element))
        {
            return (null, null, StyleLength.Auto, StyleLength.Auto);
        }

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

        // Only the indentation that is genuinely spacing around content is left as a margin: the
        // definition list's term and the quotation, which no marker depends on.
        var indent = tag is "dd" or "blockquote" ? 40.0 : (Double?)null;
        var indentEnd = tag == "blockquote" ? indent : null;
        var space = block is { } size ? size * fontSize : (Double?)null;

        return (
            Pixels(space),
            Pixels(space),
            Pixels(indent),
            Pixels(indentEnd));
    }

    private static StyleLength? Pixels(Double? value) =>
        value is { } pixels ? StyleLength.FromPixels(pixels) : null;

    /// <summary>
    /// Reads how thick a side is. The caller has already resolved the side's style, so this does not
    /// ask for it again - which is what used to make four sides cost eight reads.
    /// </summary>
    private Double BorderWidth(String style, String widthName) =>
        BorderArea.Draws(style) ? Length(widthName, RenderMode.Horizontal, 0) : 0;

    /// <summary>
    /// Gets the name one channel of one border side is read under: the name of the axis the side lies on
    /// where the page named it that way, and the side's own name wherever it did not.
    /// </summary>
    /// <remarks>
    /// The computed style answers an undeclared property with its initial value, which is a value rather
    /// than a statement, so the declaration is what tells the two apart here - a page that named the
    /// foot of a column <c>border-block-end</c> has said nothing about <c>border-bottom</c>, and the one
    /// thing the fold must not do is let the silence win.
    /// </remarks>
    private String BorderSide(String side, String physical, String channel)
    {
        var byAxis = "border-" + side + "-" + channel;

        return String.IsNullOrWhiteSpace(_specified?.GetPropertyValue(byAxis))
            ? "border-" + physical + "-" + channel
            : byAxis;
    }

    internal static BorderLineStyle ToLineStyle(String keyword) => keyword.ToLowerInvariant() switch
    {
        "none" or "hidden" => BorderLineStyle.None,
        "dashed" => BorderLineStyle.Dashed,
        "dotted" => BorderLineStyle.Dotted,
        "groove" => BorderLineStyle.Groove,
        "ridge" => BorderLineStyle.Ridge,
        "inset" => BorderLineStyle.Inset,
        "outset" => BorderLineStyle.Outset,
        "double" => BorderLineStyle.Double,
        _ => BorderLineStyle.Solid,
    };

    private Boolean HasDecoration(String kind) =>
        DecorationText.Names(
            _style.GetPropertyValue("text-decoration-line"),
            _style.GetPropertyValue("text-decoration"),
            kind);

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
            text = _style?.GetPropertyValue(name);
        }

        return String.IsNullOrWhiteSpace(text) ? null : text.Trim();
    }

    private Boolean IsOverflowVisible(String name)
    {
        var value = OverflowKeyword(name);
        return value is null || value.Equals("visible", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Gets the overflow one axis comes to, read through a keyword that states none of its own.
    /// </summary>
    /// <remarks>
    /// <c>initial</c> and <c>unset</c> on this non-inherited property are its first value -
    /// <c>visible</c>, which is no clip - and <c>inherit</c> is the box above, which is what computing the
    /// style has already worked out. Taking the word for the value made every table the author's sheet
    /// resets with <c>overflow: initial</c> clip its own descendants.
    /// </remarks>
    private String? OverflowKeyword(String name)
    {
        var text = ReadKeyword(name);

        return IsOverflowWideKeyword(text) ? _style.GetPropertyValue(name)?.Trim() : text;
    }

    /// <summary>
    /// Resolves one overflow axis from its cascaded text, for the fused path.
    /// </summary>
    /// <remarks>
    /// The same reading <see cref="OverflowKeyword"/> comes to by asking the computed style, answered
    /// without leaving the cascade so the answer stays a function of the declarations and the box above -
    /// which is what the area cache keys on.
    /// </remarks>
    public static String ResolveOverflowAxisFused(ICssProperty? declared, String parent)
    {
        var text = declared?.Value?.Trim();

        if (String.IsNullOrEmpty(text))
        {
            return CssInitialValues.Overflow;
        }

        if (!CssWideKeyword.Is(text))
        {
            return text;
        }

        // Of the four, only `inherit` reaches past the property's own first value (CSS Values 4 §4.1);
        // `revert` hands the axis back to the user agent sheet, which leaves it visible as well.
        return text.Equals("inherit", StringComparison.OrdinalIgnoreCase) ? parent : CssInitialValues.Overflow;
    }

    private Boolean Is(String name, String keyword)
    {
        var text = _style.GetPropertyValue(name);
        return text is not null && text.Trim().Equals(keyword, StringComparison.OrdinalIgnoreCase);
    }

    private Double? Resolve(ICssValue value, RenderMode mode, LengthReader.FontSource font) =>
        ResolveValue(value, mode, _dimensions, font);

    internal static Double? ResolveValue(ICssValue value, RenderMode mode, IRenderDimensions dimensions, LengthReader.FontSource font)
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

        // A `calc()` is a math value that AngleSharp will also answer as a length, and its number is
        // zero until the expression has been folded - so a reader that asked this function for pixels
        // was told the expression measured nothing. That is how a portal's `font-size: calc(18px * var(
        // --clientfont-scale, 1))`, the form it writes near two hundred of its sizes in, came out as a
        // font of no size at all: the text held no line, the box holding it collapsed, and a reader saw
        // an empty page where the sheet had said what to draw.
        if (value is CssCalcValue calc)
        {
            return LengthReader.FoldCalc(calc, mode, dimensions, font);
        }

        // A length is resolved through the reader so that its font-relative units land on the
        // element's own font size; anything else is left to AngleSharp.
        if (value is CssLengthValue length)
        {
            return Resolve(length, mode, dimensions, font);
        }

        try
        {
            var pixels = value.AsPx(dimensions, mode);

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

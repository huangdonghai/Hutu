using AngleSharp.Css;
using AngleSharp.Css.Dom;
using AngleSharp.Css.Values;
using AngleSharp.Dom;
using Hutu.Rendering.Layout;
using Hutu.Rendering.Media;
using System;
using System.Collections.Generic;

namespace Hutu.Rendering.Boxes;

/// <summary>
/// What a fused style inherits from its parent: the resolved values the resolvers take on a
/// miss, plus the parent's own cascaded <c>line-height</c> for the size-dependent inherit.
/// </summary>
internal sealed class FusedParent
{
    /// <summary>
    /// What the root inherits from: the initial values, and nothing else.
    /// </summary>
    /// <remarks>
    /// It stands, because what a root inherits is the same on every build of the same document - the
    /// device's root font size is put back to the initial value before each walk, and every other
    /// value here is a constant. Were it not, the root could never take its style from the one it
    /// already had, and neither could anything under it.
    /// </remarks>
    public static readonly FusedParent Root = new() { Stands = true };

    public Double FontSize { get; init; }
    public CssColorValue? Color { get; init; }
    public String FontFamily { get; init; } = "sans-serif";
    public Double LetterSpacing { get; init; }
    public Double WordSpacing { get; init; }
    public Int32 Weight { get; init; } = 400;
    public Boolean Italic { get; init; }
    public Double? LineHeight { get; init; }

    /// <summary>Gets the parent's text stroke, carried down because both halves of it are inherited.</summary>
    public Double TextStrokeWidth { get; init; }
    public UInt32 TextStrokeColor { get; init; }

    /// <summary>Gets the parent's text fill colour, null where neither it nor an ancestor set one.</summary>
    public UInt32? TextFillColor { get; init; }
    public ICssProperty? LineHeightHit { get; init; }
    public String WhiteSpace { get; init; } = "normal";
    public String Align { get; init; } = "start";

    /// <summary>Gets how the parent's affected lines stand, which is what an element that says nothing gives its own.</summary>
    public String AlignLast { get; init; } = CssInitialValues.TextAlignLast;

    public String TextJustify { get; init; } = "auto";

    /// <summary>
    /// Gets whether the parent is seen, which is what decides whether this element is seen when it says
    /// nothing: a dropdown's hundred boxes are hidden by the one container around them.
    /// </summary>
    public String Visibility { get; init; } = CssInitialValues.Visibility;

    /// <summary>
    /// The parent's own resolved vertical alignment. The property is not inherited, so this
    /// travels only to answer an <c>inherit</c> - which the user-agent sheet declares on every
    /// row and cell of a table.
    /// </summary>
    public String VerticalAlign { get; init; } = CssInitialValues.VerticalAlign;

    /// <summary>
    /// Gets how far the parent's underlines stood from the position the font gives them. The property is
    /// inherited, so this is the only channel through which it reaches the text below.
    /// </summary>
    public Double? UnderlineOffset { get; init; }

    public DecorationSkip Skip { get; init; }

    /// <summary>
    /// Whether the parent is a flex container, which is what makes this element a flex item - the
    /// only thing in the cascade that says so, and the only reason <c>flex-grow</c>, <c>flex-shrink</c>,
    /// <c>flex-basis</c>, <c>order</c> and <c>align-self</c> mean anything on it.
    /// </summary>
    public Boolean InFlex { get; init; }

    /// <summary>
    /// Whether the parent is a grid container, which is what makes this element a grid item - the
    /// only thing that says so, and the only reason <c>grid-column-start</c> and friends mean
    /// anything on it.
    /// </summary>
    public Boolean InGrid { get; init; }

    public StyleLength Indent { get; init; } = StyleLength.Zero;
    public TextDirection Direction { get; init; } = TextDirection.LeftToRight;
    public WritingMode WritingMode { get; init; } = WritingMode.HorizontalTb;

    /// <summary>
    /// Gets the parent's font area, which stands for every value above that came out of it. The areas are
    /// interned, so the reference says all six at once - and says them cheaply enough that resolving a
    /// font can be skipped outright when nothing about it has changed.
    /// </summary>
    internal FontArea? Font { get; init; }

    /// <summary>Gets the parent's text area, standing the same way for the four inherited text values.</summary>
    internal TextArea? Text { get; init; }

    /// <summary>
    /// Gets the parent's list marker style. Every property in it is inherited, so an item is marked the
    /// way its list was - and this is the only channel through which the user-agent sheet's
    /// <c>disc</c> on a <c>ul</c> reaches the <c>li</c> under it, since the fused path reads each element
    /// from the rules that matched that element alone.
    /// </summary>
    internal ListArea? List { get; init; }

    /// <summary>
    /// Gets the parent's border area. Border is not an inherited property, so this travels for one
    /// reason only: a child that writes <c>border-width: inherit</c> takes the width the parent came to,
    /// resolved at the parent's font size rather than re-read at the child's.
    /// </summary>
    internal BorderArea? Border { get; init; }

    /// <summary>
    /// Gets the scope this element's own <c>var()</c> references were read through, which is what its
    /// children inherit unless they redeclare a custom property of their own. Null for a page that
    /// declares none anywhere, which is every sample page and most of the tests.
    /// </summary>
    public VariableScope? Variables { get; init; }

    /// <summary>
    /// Gets the tag name of the element this parent is, folded the way the box builder folds it. A list
    /// inside a list is left to its outer one's margins, so which element an area belongs to is part of
    /// what its children resolve - and the parent's own name is the whole of that answer.
    /// </summary>
    internal String? Tag { get; init; }

    /// <summary>
    /// Where an annotation goes, which <c>ruby-position</c> states on an ancestor as often as on
    /// the pair itself and carries down like any other inherited property.
    /// </summary>
    public RubyPosition Ruby { get; init; } = RubyPosition.Over;

    public TextOrientation? Orientation { get; init; }
    public Boolean? CombinesUpright { get; init; }
    public CursorKind Cursor { get; init; }

    /// <summary>
    /// Gets whether the values a child inherits from this parent are the same instances the previous
    /// build computed, so the child may take its style from the one it already has.
    /// </summary>
    /// <remarks>
    /// A style is a function of the element's own match, the values it inherits and the scope its
    /// <c>var()</c> references are read through. The last two travel down from here, so a parent
    /// whose style, match and scope all stand is a parent a child can be resolved against without
    /// resolving anything - which is what keeps a gesture that moves one mark from resolving every
    /// style in the document.
    /// </remarks>
    internal Boolean Stands { get; init; }

    /// <summary>The values a parent hands down to its children.</summary>
    /// <param name="stands">
    /// Whether this parent's own style, match and scope are the instances the previous build
    /// computed. False for a parent that was resolved again, whatever the answer came to.
    /// </param>
    public FusedParent Next(
        ComputedStyle style,
        MatchedCascade cascade,
        VariableScope? variables,
        String? tag = null,
        Boolean stands = false) => new()
    {
        Stands = stands,
        FontSize = style.FontSize,
        Color = style.Color,
        FontFamily = style.FontFamily,
        LetterSpacing = style.LetterSpacing,
        WordSpacing = style.WordSpacing,
        Weight = style.FontWeight,
        Italic = style.IsItalic,
        LineHeight = style.LineHeight,
        TextStrokeWidth = style.TextStrokeWidth,
        TextStrokeColor = style.TextStrokeColor,
        TextFillColor = style.TextFillColor,

        // The nearest declaration at or above this element, not merely its own. A number is the form
        // that is re-read at every element - its computed value is the number, and each element it
        // reaches multiplies it by its own size - so it has to keep travelling down: taken from the
        // element alone it survived one level and no more, and a grandchild fell back to the pixels
        // its parent had already resolved - a body's 1.3333 reached a 12.8px note as 21.3px instead of
        // 17.1px. A share does not re-read that way (CSS 2.2 §10.8.1 computes it once, against the font
        // size of the element that declared it), and the resolver takes the parent's own resolved value
        // for it; a length is unaffected either way.
        //
        // `inherit` is left out on purpose: it states no value of its own, so what it asks for - and
        // what everything under it asks for - is the declaration it inherited. Storing it here would
        // bury that declaration, and a `line-height: inherit` on a wrapper would stop the number behind
        // it from ever being read again.
        LineHeightHit = cascade.Find("line-height") is { } lineHeight && !ComputedStyle.IsInherit(lineHeight)
            ? lineHeight
            : LineHeightHit,
        WhiteSpace = style.WhiteSpace,
        Align = style.TextAlign,
        AlignLast = style.TextAlignLast,
        TextJustify = style.TextJustify,
        Visibility = style.Visibility,
        VerticalAlign = style.VerticalAlign,
        UnderlineOffset = style.UnderlineOffset,
        Skip = style.SkipInk,
        InFlex = style.Display.Contains("flex", StringComparison.OrdinalIgnoreCase),
        InGrid = style.Display.Contains("grid", StringComparison.OrdinalIgnoreCase),
        Indent = style.TextIndent,
        Direction = style.Direction,
        WritingMode = style.WritingMode,

        // The areas, not the values above, are what a child's cached font and text are keyed on: one
        // interned instance already carries every value a child could inherit from them.
        Font = style.ResolvedFont,
        Text = style.ResolvedText,
        List = style.ResolvedList,
        Border = style.ResolvedBorder,
        Variables = variables,
        Tag = tag,
        Ruby = style.RubyPosition,
        Orientation = style.DeclaredTextOrientation,
        CombinesUpright = style.DeclaredTextCombineUpright,
        Cursor = style.Cursor,
    };
}

/// <summary>
/// Assembles a <see cref="ComputedStyle"/> from the element's own matched declarations and the
/// values it inherits, with no render tree and no intermediate declaration.
/// </summary>
/// <remarks>
/// Every resolver reads the element's own cascade, where a hit is a declared value and a miss
/// is an inherited one - the distinction the render-tree path settles by a second cascade.
/// </remarks>
internal static class FusedStyleFactory
{
    public static ComputedStyle Build(
        IElement element,
        MatchedCascade cascade,
        FusedParent parent,
        HutuRenderDevice dimensions,
        FontFaceTable? faces,
        StyleIntern? pool,
        FusedAreaCache? cache = null,
        VariableScope? variables = null,
        Func<String, Boolean>? applies = null)
    {
        // The scope is read through only where one exists: a page that declares no custom property
        // never asks for a substitution, and the cascade's own lookup - with the answers it remembers -
        // is then the reader, exactly as it was before variables came in.
        ICssStyleDeclaration? computed = null;

        Func<String, ICssProperty?> find = name =>
        {
            // A pseudo-element that is not fully styleable has the declarations it was given taken off
            // it: what it comes to for that property is then whatever it inherits, or the initial value
            // where nothing is inherited. Lists 3 §3.1.1 gives ::marker that reading, and Chrome answers
            // a decoration, a background or a visibility written on a marker exactly that way.
            if (applies is not null && !applies(name))
            {
                return null;
            }

            var property = variables is null ? cascade.Find(name) : variables.ReadProperty(cascade, name);

            // A longhand that the cascade holds with no text at all is the shadow a shorthand written
            // as a variable leaves behind: <c>padding: var(--pad)</c> reaches the declaration list as
            // four empty children, because the expansion cannot be worked out until the variable can be
            // - and the shorthand itself is nowhere in the list to be read through. Computing this one
            // element's style is what expands it, and doing it only here keeps every other name on the
            // fused path's own cheap reading.
            if (property is not null && String.IsNullOrEmpty(property.Value))
            {
                computed ??= element.Owner?.DefaultView?.GetComputedStyle(element, String.Empty);
                property = computed?.GetProperty(name) ?? property;
            }

            return property;
        };

        // The parent's face travels with its size, because a relative font-size written in ex or ch is
        // measured off that face rather than off the number.
        var size = ComputedStyle.ResolveFontSizeFused(
            find("font-size"),
            new LengthReader.FontSource(
                new Hutu.Rendering.Layout.FontStyle(parent.FontFamily, parent.FontSize, parent.Weight, parent.Italic)),
            dimensions);

        // The root's size is the number every `rem` further down the document is a share of, and the
        // length conversion has only the device to ask for it - so this is the one moment it is known,
        // and it is handed over before any other length on the element is read. The root's own size was
        // just resolved against the initial value rather than against itself, which is what keeps the
        // two from chasing each other round.
        if (ReferenceEquals(parent, FusedParent.Root))
        {
            dimensions.RootFontSize = size;
        }

        // A font is decided by the declarations, the size it is set at, the parent's font - family,
        // weight, slant, spacing and line height all travel down - and one thing no area holds: the
        // property a relative <c>line-height</c> was authored in, which keeps travelling until someone
        // declares their own. Two elements can share a font and still inherit different ones of those,
        // so both go into the key; a cache that left the second out would hand a paragraph the leading
        // of the div whose font it happens to match.
        var fontKey = new AreaKey(cascade, size, parent.Font, parent.LineHeightHit, variables: variables);

        // The whole font a length may be written in. ex and ch are heights of the face rather than
        // fractions of the size, so a fold that is handed the number alone has nothing to measure them
        // against - and the answer is settled here, once, whether the font was read or reused.
        LengthReader.FontSource face;

        if (cache is not null && cache.TryFont(fontKey, out var font))
        {
            // Nothing read: family, weight, slant, spacing and leading all came from an element that
            // matched alike, at the same size, under the same inherited font and the same authored
            // line-height.
            face = new LengthReader.FontSource(
                new Hutu.Rendering.Layout.FontStyle(font.Family, font.Size, font.Weight, font.Italic));
        }
        else
        {
            var family = ComputedStyle.ResolveFontFamilyFused(find("font-family"), parent.FontFamily, faces);
            var weight = ComputedStyle.ResolveFontWeightFused(find("font-weight"), parent.Weight);
            var italic = ComputedStyle.ResolveFontStyleFused(find("font-style"), parent.Italic);

            // The whole font, settled before any length is folded: ex and ch are heights of the face a
            // length is written in rather than fractions of its size, and a reader handed the number
            // alone has nothing to measure them against.
            face = new LengthReader.FontSource(
                new Hutu.Rendering.Layout.FontStyle(family, size, weight, italic));

            var built = new FontArea
            {
                Size = size,
                Family = family,
                Weight = weight,
                Italic = italic,
                LetterSpacing = ComputedStyle.ResolveLetterSpacingFused(find("letter-spacing"), parent.LetterSpacing, face, dimensions),
                WordSpacing = ComputedStyle.ResolveWordSpacingFused(find("word-spacing"), parent.WordSpacing, face, dimensions),
                LineHeight = ComputedStyle.ResolveLineHeightFused(
                    find("line-height"), parent.LineHeightHit, face, parent.LineHeight, dimensions),
            };

            font = pool is null ? built : pool.Font(built);

            if (cache is not null)
            {
                cache.Store(fontKey, font);
            }
        }

        // A text behaviour reads the same three inputs - the cascade, the size the indent is measured
        // at, and the parent's text - and nothing of the element's identity, so it caches the same way.
        // The parent's direction is packed into the key beside its text for one value: `match-parent` is
        // read against the direction the parent's lines ran in, so two parents that aligned alike and
        // read the other way hand down two different answers.
        var textKey = new AreaKey(
            cascade, size, parent.Text, stamp: (Int32)parent.Direction, variables: variables);

        if (cache is not null && cache.TryText(textKey, out var text))
        {
            // Nothing read.
        }
        else
        {
            var (all, last) = ComputedStyle.Alignments(
                ComputedStyle.ResolveTextAlignFused(
                    find("text-align"),
                    parent.Align,
                    PresentationalHints.Align(element),
                    PresentationalHints.DefaultTextAlign(element)),
                ComputedStyle.ResolveTextAlignLastFused(find("text-align-last"), parent.AlignLast),

                // The root has no parent to match; `FusedParent.Root` stands for what the root inherits,
                // and `match-parent` said at the root computes to `start` rather than to that.
                ReferenceEquals(parent, FusedParent.Root) ? null : parent.Align,
                parent.Direction);

            var built = new TextArea
            {
                WhiteSpace = ComputedStyle.ResolveWhiteSpaceFused(find("white-space"), parent.WhiteSpace),
                Align = all,
                AlignLast = last,
                TextJustify = ComputedStyle.ResolveTextJustifyFused(find("text-justify"), parent.TextJustify),
                VerticalAlign = ComputedStyle.ResolveVerticalAlignFused(find("vertical-align"), parent.VerticalAlign, PresentationalHints.Valign(element)),
                Indent = ComputedStyle.ResolveTextIndentFused(find("text-indent"), parent.Indent, face, dimensions),
                Visibility = ComputedStyle.ResolveVisibilityFused(find("visibility"), parent.Visibility),
                Trim = Trim(find("text-box-trim")?.Value),
                EdgeStart = Edge(find("text-box-edge")?.Value, first: true),
                EdgeEnd = Edge(find("text-box-edge")?.Value, first: false),
            };

            text = pool is null ? built : pool.Text(built);

            if (cache is not null)
            {
                cache.Store(textKey, text);
            }
        }

        // A border is read from the cascade and the font size alone - the size is in the key because a
        // width in em is that many pixels of it - so an element that matched the same cascade at the
        // same size as an earlier one has nothing left to resolve. The parent's border rides in the key
        // as well, for the one value that reaches across to it: a width the child inherits is the parent's
        // computed length, not the child's own. What the cache holds is the interned instance, so a hit
        // hands back the very object the value table would have found.
        var key = new AreaKey(cascade, size, parent.Border, identity: cache?.Hints(element), variables: variables);

        if (cache is not null && cache.TryBorder(key, out var border))
        {
            // Resolved for this cascade and size already: the four style longhands, the four widths,
            // the line kinds and the four radii are not read again.
        }
        else
        {
            var borderStyles = ComputedStyle.ResolveBorderStylesFused(find, element);
            var borderWidths = ComputedStyle.ResolveBorderWidthsFused(
                find, borderStyles, face, dimensions, parent.Border, PresentationalHints.Border(element));
            var built = new BorderArea
            {
                TopStyle = borderStyles.Top,
                RightStyle = borderStyles.Right,
                BottomStyle = borderStyles.Bottom,
                LeftStyle = borderStyles.Left,
                TopWidth = borderWidths.Top,
                RightWidth = borderWidths.Right,
                BottomWidth = borderWidths.Bottom,
                LeftWidth = borderWidths.Left,
                Lines = ComputedStyle.ResolveBorderLinesFused(borderStyles),
                Radius = ComputedStyle.ResolveBorderRadiusFused(find, face, dimensions),
            };

            border = pool is null ? built : pool.Border(built);

            if (cache is not null)
            {
                cache.Store(key, border);
            }
        }

        // A marker style is decided by the declarations and the list around the element - never by the
        // element's identity - so it is an area like the text and joins the shared ones below.
        var list = new ListArea
        {
            Type = ComputedStyle.ResolveListStyleTypeFused(find("list-style-type"), parent.List?.Type ?? "disc"),
            Position = ComputedStyle.ResolveMarkerPositionFused(find("list-style-position"), parent.List?.Position ?? MarkerPosition.Outside),
            Image = ComputedStyle.ResolveListStyleImageFused(find("list-style-image"), parent.List?.Image),

            // The list-item counter operations are never inherited, so they read only from this element's
            // own declarations - a plain null when it says nothing about that counter.
            ListItemReset = ComputedStyle.ResolveListItemCounterFused(find("counter-reset"), 0),
            ListItemSet = ComputedStyle.ResolveListItemCounterFused(find("counter-set"), 0),
            ListItemIncrement = ComputedStyle.ResolveListItemCounterFused(find("counter-increment"), 1),
        };

        // The flow an element's text runs in, resolved once. The box model needs it to map its four
        // logical sides onto physical ones and the keywords need it as they stand, so it is worked out
        // here rather than where each reader asks - a heading's margins asked for it twice over, and the
        // mode it lives in depends on a property that was being read a third time below.
        var writingMode = ComputedStyle.ResolveWritingModeFused(find("writing-mode"), parent.WritingMode);
        var direction = ComputedStyle.ResolveDirectionFused(element, find("direction"), parent.Direction);
        var orientation = ComputedStyle.ResolveTextOrientationFused(find("text-orientation"), parent.Orientation);
        var combine = ComputedStyle.ResolveTextCombineFused(find("text-combine-upright"), parent.CombinesUpright);
        var flow = Flow(writingMode, direction, orientation, combine);

        // A box model is decided by four things: the declarations, the size a percentage or an em is
        // measured at, the flow its sides are mapped through, and which element it is - its own tag name,
        // since the sample stylesheet gives headings and paragraphs margins of their own, and its
        // parent's, since a list inside a list is left to the outer one's margins. Nothing else about the
        // element enters, so the four cache like the rest.
        var boxKey = cache is null
            ? default
            : new AreaKey(cascade, size, parent.Tag, cache.Kind(element), FlowStamp(parent), variables);

        if (cache is not null && cache.TryBoxModel(boxKey, out var boxModel))
        {
            // Resolved for this kind of place in the tree already: neither the eight lengths nor the two
            // thicknesses - nor the flow they are mapped through - was read again.
        }
        else
        {
            // The two axes the replaced sizing asks about separately: a declaration there means the page
            // spoke about that axis, and its absence is what leaves room for the element's own attribute.
            var widthDeclaration = find("width");
            var heightDeclaration = find("height");

            // The size a legacy attribute hints at for a table. A cell's or a row's attribute is not read
            // here: it sizes a column or a row, not the box, and travels on the box for the table to read.
            var widthHint = PresentationalHints.Size(element, "width");
            var heightHint = PresentationalHints.Size(element, "height");

            var built = new BoxModelArea
            {
                BoxSizing = ComputedStyle.ResolveBoxSizingFused(find("box-sizing")),
                Width = ComputedStyle.ResolveBoxLengthFused(widthDeclaration, RenderMode.Horizontal, face, dimensions,
                    widthDeclaration is null ? widthHint ?? StyleLength.Auto : StyleLength.Auto),
                Height = ComputedStyle.ResolveBoxLengthFused(heightDeclaration, RenderMode.Vertical, face, dimensions,
                    heightDeclaration is null ? heightHint ?? StyleLength.Auto : StyleLength.Auto),
                WidthDeclared = widthDeclaration is not null || widthHint is not null,
                HeightDeclared = heightDeclaration is not null || heightHint is not null,
                MinWidth = ComputedStyle.ResolveBoxLengthFused(find("min-width"), RenderMode.Horizontal, size, dimensions, StyleLength.None),
                MaxWidth = ComputedStyle.ResolveBoxLengthFused(find("max-width"), RenderMode.Horizontal, size, dimensions, StyleLength.None),
                MinHeight = ComputedStyle.ResolveBoxLengthFused(find("min-height"), RenderMode.Vertical, size, dimensions, StyleLength.None),
                MaxHeight = ComputedStyle.ResolveBoxLengthFused(find("max-height"), RenderMode.Vertical, size, dimensions, StyleLength.None),
                Margin = ComputedStyle.ResolveThicknessFused(find, element, face, dimensions, flow, "margin", StyleLength.Zero),

                // The room a cell keeps is its table's, and the room it keeps where its table says nothing
                // is the pixel every browser's sheet gives a cell.
                Padding = ComputedStyle.ResolveThicknessFused(
                    find, element, face, dimensions, flow, "padding",
                    PresentationalHints.CellPadding(element) ?? StyleLength.Zero),
            };

            boxModel = pool is null ? built : pool.BoxModel(built);

            if (cache is not null)
            {
                cache.Store(boxKey, boxModel);
            }
        }

        // Neither an overflow nor a fit nor a display keyword is a length, so these two read the cascade
        // alone: the key is the bare one, which keeps them to one entry per cascade rather than one per
        // size the page happens to set.
        var plain = new AreaKey(cascade, 0, variables: variables);

        if (cache is not null && cache.TryDisplay(plain, out var display))
        {
            // Nothing read.
        }
        else
        {
            var scrolls = ComputedStyle.ResolveScrollsFused(find("overflow-x"), find("overflow-y"));
            var built = new DisplayPositioningArea
            {
                Display = ComputedStyle.ResolveDisplayFused(find("display")),
                Float = ComputedStyle.ResolveFloatFused(find("float"), PresentationalHints.Float(element)),
                Clear = ComputedStyle.ResolveClearFused(find("clear")),
                ObjectFit = ComputedStyle.ResolveObjectFitFused(find("object-fit")),
                ClipsOverflow = ComputedStyle.ResolveClipsOverflowFused(find("overflow-x"), find("overflow-y")),
                ScrollsInline = scrolls.Inline,
                ScrollsBlock = scrolls.Block,

                // A block container reads this to stand its content where it was asked to along the
                // block axis, which is what centres a form control's label in the height the control
                // was given (HTML §15.3.10).
                AlignContent = FlexStyle.ReadAlignmentFused(find("align-content"), CssInitialValues.AlignContent),
            };

            display = pool is null ? built : pool.DisplayPositioning(built);

            if (cache is not null)
            {
                cache.Store(plain, display);
            }
        }

        if (cache is not null && cache.TryVisual(key, out var visual))
        {
            // The outline was resolved for this cascade and size already.
        }
        else
        {
            var outlineStyle = ComputedStyle.ResolveOutlineStyleFused(find("outline-style"));
            var built = new VisualEffectsArea
            {
                OutlineStyle = outlineStyle,
                OutlineWidth = ComputedStyle.ResolveOutlineWidthFused(outlineStyle, find("outline-width"), face, dimensions),
                OutlineOffset = ComputedStyle.ResolveOutlineOffsetFused(find("outline-offset"), face, dimensions),
            };

            visual = pool is null ? built : pool.VisualEffects(built);

            if (cache is not null)
            {
                cache.Store(key, visual);
            }
        }

        if (cache is not null && cache.TryDecoration(plain, out var textDecoration))
        {
            // Neither stroke was read.
        }
        else
        {
            var built = ComputedStyle.ResolveDecorationFused(
                find, parent.UnderlineOffset, parent.Skip, face, dimensions);

            textDecoration = pool is null ? built : pool.Decoration(built);

            if (cache is not null)
            {
                cache.Store(plain, textDecoration);
            }
        }

        var color = ComputedStyle.ResolveColorFused(element, find("color"), parent.Color);

        // The text stroke and fill are read here rather than in the keywords below because they reach
        // across to the parent: each is inherited, and a shorthand sets both halves of the stroke at once.
        var (strokeWidth, strokeColor) = ComputedStyle.ResolveTextStrokeFused(
            find, parent.TextStrokeWidth, parent.TextStrokeColor, size, dimensions.RootFontSize);

        var fillColor = ComputedStyle.ResolveTextFillColorFused(
            find("-webkit-text-fill-color")?.Value, parent.TextFillColor);

        var keywords = new ComputedStyle.FusedKeywords
        {
            WritingMode = writingMode,
            Direction = direction,
            Orientation = orientation,
            CombinesUpright = combine,
            Color = color,
            BackgroundColor = ComputedStyle.ResolveBackgroundColorFused(element, find("background-color")),
            Backgrounds = ComputedStyle.ResolveBackgroundsFused(find, element, dimensions, size),
            Cursor = ComputedStyle.ResolveCursorFused(element, find("cursor"), parent.Cursor),
            Ruby = ComputedStyle.ResolveRubyPositionFused(find("ruby-position"), parent.Ruby),
            OutlineColor = ComputedStyle.ResolveOutlineColorFused(find("outline-color"), color),
            BackgroundClip = ComputedStyle.ResolveBackgroundClipFused(find("background-clip"), find("-webkit-background-clip")),
            TextStrokeWidth = strokeWidth,
            TextStrokeColor = strokeColor,
            TextFillColor = fillColor,
            Transform = find("transform")?.Value,
            TransformOrigin = find("transform-origin")?.Value,
            OffsetPath = find("offset-path")?.Value,
            OffsetDistance = find("offset-distance")?.Value,
            OffsetRotate = find("offset-rotate")?.Value,
            OffsetAnchor = find("offset-anchor")?.Value,
            OffsetPosition = find("offset-position")?.Value,
            Offset = find("offset")?.Value,
            BorderTopColor = ComputedStyle.ResolveBorderColorFused(find("border-top-color"), color) ?? PresentationalHints.BorderColor(element),
            BorderRightColor = ComputedStyle.ResolveBorderColorFused(find("border-right-color"), color) ?? PresentationalHints.BorderColor(element),
            BorderBottomColor = ComputedStyle.ResolveBorderColorFused(find("border-bottom-color"), color) ?? PresentationalHints.BorderColor(element),
            BorderLeftColor = ComputedStyle.ResolveBorderColorFused(find("border-left-color"), color) ?? PresentationalHints.BorderColor(element),
        };

        var values = new ComputedStyle.StyleValues();

        // Every area is shared rather than duplicated. An area record is written to be immutable and
        // shared - "the same instance stands for every box whose values agree", as the discipline on them
        // says - so each one goes through the intern table as it is built, and the four the area cache
        // answered first are the same instance by the other route: what the cache hands out is exactly
        // what the table had kept. A null pool, which is what a caller with no tree to share within
        // passes, keeps the copies this path made before any of it was wired up.
        //
        // What is shared is exactly the seven areas whose properties cannot depend on the element's own
        // live state. The colours, the background layers, the cursor and the positioning are not: a
        // border colour resolves `currentcolor` against a colour that depends on whether an anchor has
        // been visited, and that answer belongs to the box holding it rather than to a table two boxes
        // might agree with today and disagree with after a click. All seven read the cascade, the font
        // size and the parent's interned instance alone - the box model adds the two tag names and the
        // inherited flow, all of which a key can hold exactly - so they sit behind the area cache as
        // well and are not even resolved twice when nothing about them has changed.
        values.Font = font;
        values.Text = text;
        values.Border = border;
        values.BoxModel = boxModel;
        values.DisplayPositioning = display;
        values.VisualEffects = visual;
        values.Decoration = textDecoration;
        values.List = pool is null ? list : pool.List(list);
        values.Position = PositionStyle.ReadFused(find, face, dimensions);

        // The table longhands are five property lookups no element that is not part of a table has a
        // use for, so they are only read where the display says the box is one. Every spelling of it
        // - table, inline-table, table-row, table-cell and the rest - carries the word.
        // The column longhands are read for every box: a page that writes none of them costs two lookups,
        // and a box that writes either is a multi-column container whatever its display says.
        values.Multi = MulticolStyle.ReadFused(find, face, dimensions);

        values.Table = display.Display.Contains("table", StringComparison.OrdinalIgnoreCase)
            ? TableStyle.ReadFused(find, text.VerticalAlign, face, dimensions, PresentationalHints.Spacing(element) ?? 0, PresentationalHints.Collapses(element))
            : null;

        // The flex longhands are read for a flex container - every spelling carries the word - and
        // for a flex item, which only the parent's display can tell apart. Nothing else reads them,
        // and a box that reads one without being either gets the initial values.
        var flex = display.Display.Contains("flex", StringComparison.OrdinalIgnoreCase);

        values.Flex = flex || parent.InFlex
            ? FlexStyle.ReadFused(find, face, dimensions)
            : null;

        // The grid longhands are read for a container - every spelling carries the word - while an
        // item is recognised by its parent's display and asked only for its own placement.
        values.Grid = display.Display.Contains("grid", StringComparison.OrdinalIgnoreCase)
            ? GridStyle.ReadFused(find, face, dimensions)
            : null;

        values.GridItem = parent.InGrid ? GridItemStyle.ReadFused(find) : null;

        values.Fused = keywords;

        return new ComputedStyle(dimensions, element, keywords, values, faces);
    }

    /// <summary>
    /// Packs the flow a parent passes down into the number a key holds it in: the writing mode, the
    /// direction, and the two upright properties - each null or one of a handful of values, each given a
    /// field wide enough to hold all of them. Nothing is folded away, so no two flows come out alike.
    /// </summary>
    private static Int32 FlowStamp(FusedParent parent)
    {
        var orientation = parent.Orientation is { } named ? (Int32)named + 1 : 0;
        var combines = parent.CombinesUpright is { } stated ? (stated ? 2 : 1) : 0;

        return (Int32)parent.WritingMode
            | ((Int32)parent.Direction << 3)
            | (orientation << 4)
            | (combines << 7);
    }

    /// <summary>
    /// Settles the flow an element's text runs in from the mode, the direction and the two upright
    /// properties, which the caller has already resolved.
    /// </summary>
    /// <remarks>
    /// Along a line the orientation and the combining mean nothing, so a horizontal mode is handed back
    /// as the shared horizontal flow - or as that flow turned around for a right-to-left one, which is
    /// what <c>rtl</c> along a line asks for. A column takes the orientation it was set to, or mixed
    /// when none was, and the combining with it.
    /// </remarks>
    private static WritingModeInfo Flow(
        WritingMode mode,
        TextDirection direction,
        TextOrientation? orientation,
        Boolean? combine) =>
        mode == WritingMode.HorizontalTb
            ? direction == TextDirection.LeftToRight
                ? WritingModeInfo.Horizontal
                : new WritingModeInfo(mode, TextOrientation.Mixed, false, direction)
            : new WritingModeInfo(mode, orientation ?? TextOrientation.Mixed, combine ?? false, direction);

    /// <summary>Reads the ends a <c>text-box-trim</c> value names.</summary>
    private static TextBoxTrim Trim(String? value) => value?.Trim().ToLowerInvariant() switch
    {
        "trim-start" => TextBoxTrim.Start,
        "trim-end" => TextBoxTrim.End,
        "trim-both" => TextBoxTrim.Both,
        _ => TextBoxTrim.None,
    };

    /// <summary>
    /// Reads one of the two edges a <c>text-box-edge</c> value may name: a single edge is the one both
    /// ends take, and a value that names no edge the engine knows leaves the text box to be trimmed to.
    /// </summary>
    private static TextBoxEdge Edge(String? value, Boolean first)
    {
        var parts = value?.Trim().ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);

        if (parts is not { Length: > 0 })
        {
            return TextBoxEdge.Text;
        }

        var part = first || parts.Length == 1 ? parts[0] : parts[1];

        return part switch
        {
            "cap" => TextBoxEdge.Cap,
            "ex" => TextBoxEdge.Ex,
            "alphabetic" => TextBoxEdge.Alphabetic,
            "ideographic" => TextBoxEdge.Ideographic,
            "ideographic-ink" => TextBoxEdge.IdeographicInk,
            _ => TextBoxEdge.Text,
        };
    }
}

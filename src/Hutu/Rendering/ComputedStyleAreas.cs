using Hutu.Rendering.Layout;
using System;
using System.Collections.Generic;

namespace Hutu.Rendering;

/// <summary>
/// The copy-on-write discipline every area record obeys. Declared once here rather than as instance
/// methods on each record.
/// </summary>
/// <remarks>
/// An area is immutable and shared: the same instance stands for every box whose values agree, which
/// is what keeps a document from paying for a value per element. Nothing may write to an area after
/// it is built - not to "fix up" one field, not to cache a derived value on it - because a write is
/// visible to every box sharing it, and the mistake would show up as one box's declaration quietly
/// changing another's.
/// </remarks>
internal static class ComputedStyleCow
{
    /// <summary>
    /// Produces the record that results from changing one property, or returns <paramref name="self"/>
    /// when it already holds that value - so a write that changes nothing allocates nothing.
    /// </summary>
    internal static TSelf SetPropertyValue<TSelf, TValue>(
        this TSelf self,
        TValue currentValue,
        TValue newValue,
        Func<TSelf, TValue, TSelf> apply) =>
        EqualityComparer<TValue>.Default.Equals(currentValue, newValue) ? self : apply(self, newValue);

    /// <summary>
    /// The area-level counterpart: swaps <paramref name="newArea"/> onto <paramref name="self"/> unless
    /// it is already there.
    /// </summary>
    /// <remarks>
    /// Compared by reference, deliberately. A structural comparison would agree with a reference
    /// comparison at every call site here - the incoming area is either the one already held or a
    /// freshly built, therefore different, object - while costing a scan of every field to say so.
    /// </remarks>
    internal static TSelf AdoptArea<TSelf, TArea>(
        this TSelf self,
        TArea currentArea,
        TArea newArea,
        Func<TSelf, TArea, TSelf> apply)
        where TArea : class =>
        ReferenceEquals(currentArea, newArea) ? self : apply(self, newArea);
}

/// <summary>
/// What the four border sides are drawn with, how thick they are, and the corner radii. Everything
/// here is resolved: a width that was <c>medium</c> is a number of pixels, and a style that was
/// <c>double</c> keeps its keyword because the collapsing model breaks ties by it.
/// </summary>
/// <remarks>
/// The colours are not here. A border colour resolves <c>currentcolor</c> against the element's own
/// colour, and that colour depends on live state - whether the anchor has been visited, and a second
/// selector match to tell a declared colour from an inherited one - so putting the colours in would
/// move that cost from "when the colour is asked for" to "every box at construction".
/// </remarks>
internal sealed record BorderArea
{
    /// <summary>
    /// What a box that declares no border property at all shares: no strokes, nothing drawn, square
    /// corners.
    /// </summary>
    internal static readonly BorderArea Default = new();

    internal String TopStyle { get; init; } = CssInitialValues.BorderStyle;

    internal String RightStyle { get; init; } = CssInitialValues.BorderStyle;

    internal String BottomStyle { get; init; } = CssInitialValues.BorderStyle;

    internal String LeftStyle { get; init; } = CssInitialValues.BorderStyle;

    /// <summary>Gets how thick each side is, which is zero whenever its style draws nothing.</summary>
    internal Double TopWidth { get; init; }

    internal Double RightWidth { get; init; }

    internal Double BottomWidth { get; init; }

    internal Double LeftWidth { get; init; }

    /// <summary>
    /// Gets the four strokes as the values the painter and the collapsing model want. Stored rather
    /// than derived per read because the derivation lowercases the keyword, which allocates.
    /// </summary>
    internal BorderStyles Lines { get; init; }

    /// <summary>Gets the corner radii, with any percentage still unresolved - a radius is relative to
    /// the box's own size, which layout is what knows.</summary>
    internal StyleCornerRadii Radius { get; init; }

    /// <summary>Gets whether the side draws anything, which is the question the box model asks.</summary>
    internal static Boolean Draws(String style) =>
        !style.Equals("none", StringComparison.OrdinalIgnoreCase) &&
        !style.Equals("hidden", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// The box a declared width and height describe, and the lengths that place and fill it.
/// </summary>
/// <remarks>
/// Every length here keeps its unit: a percentage is relative to the containing block, which only the
/// layout pass knows, so resolving one here would be wrong rather than early. What is saved is the
/// parse - the declaration is read once instead of on every access - not the resolution.
/// <para>
/// The initialisers are not decoration: the resolver is skipped entirely for an element that declares
/// none of these properties, and what it would have produced for such an element is exactly this.
/// A default that disagreed with the reader's own fallback would make skipping it wrong.
/// </para>
/// </remarks>
internal sealed record BoxModelArea
{
    /// <summary>What a box that declares no width, height, margin or padding shares.</summary>
    internal static readonly BoxModelArea Default = new();

    internal BoxSizing BoxSizing { get; init; } = BoxSizing.ContentBox;

    internal StyleLength Width { get; init; } = StyleLength.Auto;

    internal StyleLength Height { get; init; } = StyleLength.Auto;

    /// <summary>
    /// Gets whether the cascade was told anything about the width at all, as opposed to answering with
    /// the initial <c>auto</c> because nothing was said.
    /// </summary>
    /// <remarks>
    /// The two read the same as a length and mean opposite things: an author's <c>width: auto</c> asks
    /// that the content decide, while the initial value asks nothing and leaves room for a presentational
    /// hint - the element's own <c>width</c> attribute - to answer. Only a reader that kept the two apart
    /// can tell which boxes that hint applies to.
    /// </remarks>
    internal Boolean WidthDeclared { get; init; }

    /// <summary>Gets whether the cascade was told anything about the height. See <see cref="WidthDeclared"/>.</summary>
    internal Boolean HeightDeclared { get; init; }

    internal StyleLength MinWidth { get; init; } = StyleLength.None;

    internal StyleLength MaxWidth { get; init; } = StyleLength.None;

    internal StyleLength MinHeight { get; init; } = StyleLength.None;

    internal StyleLength MaxHeight { get; init; } = StyleLength.None;

    /// <summary>
    /// Gets the ratio the width and height keep to, or null where the page named none.
    /// </summary>
    /// <remarks>
    /// A box with a ratio and one definite axis takes the other from it (CSS Sizing 4 §4), which is
    /// what gives a plot its height from its width and a picture its shape from its content.
    /// </remarks>
    internal AspectRatio? AspectRatio { get; init; }

    internal StyleThickness Margin { get; init; } = new(StyleLength.Zero, StyleLength.Zero, StyleLength.Zero, StyleLength.Zero);

    internal StyleThickness Padding { get; init; } = new(StyleLength.Zero, StyleLength.Zero, StyleLength.Zero, StyleLength.Zero);
}

/// <summary>
/// How a box takes part in its container's flow, how it fits replaced content, and whether it clips
/// what overflows. The position offsets deliberately are not here - they are lengths, and
/// <c>PositionStyle</c> reads them with the mode and the stacking order as one value.
/// </summary>
internal sealed record DisplayPositioningArea
{
    internal static readonly DisplayPositioningArea Default = new();

    /// <summary>
    /// What a box that stands for a run of inline content the author never wrapped shares:
    /// block-level, in flow, clipping nothing. Such a box has no presentation of its own, so it takes
    /// this rather than anything its parent declared - and takes the same instance every time, the
    /// way every other area is shared between the boxes that agree on it.
    /// </summary>
    internal static readonly DisplayPositioningArea Block = new() { Display = "block" };

    internal String Display { get; init; } = CssInitialValues.Display;

    /// <summary>
    /// Gets which edge the box floats to, which is the reason the box left normal flow and the edge
    /// its container reserves room against. <see cref="Layout.FloatSide.None"/> is the common case and
    /// needs no declaration at all, which is why it is the record's default rather than a value the
    /// resolver answers with.
    /// </summary>
    internal Layout.FloatSide Float { get; init; }

    /// <summary>Gets which side's floats the box moves down past before it takes its place.</summary>
    internal Layout.ClearSide Clear { get; init; }

    /// <summary>
    /// Gets how a replaced box fits its content, resolved here rather than left as a keyword: the
    /// reader lowercases the declaration's text to answer, which allocates on a path the painter
    /// walks for every image.
    /// </summary>
    internal Layout.ObjectFit ObjectFit { get; init; } = Layout.ObjectFit.Fill;

    /// <summary>Gets whether either axis clips its overflow, which is all this engine does with
    /// <c>overflow</c>: it scrolls the document rather than the box.</summary>
    internal Boolean ClipsOverflow { get; init; }

    /// <summary>
    /// Gets the keyword the inline axis of the box was left with. The two are kept because the property is
    /// not inherited and still has a keyword - <c>inherit</c> - that asks for the box above: what travels
    /// down is these two words, and a child that asks takes them rather than a guess.
    /// </summary>
    internal String OverflowX { get; init; } = CssInitialValues.Overflow;

    /// <summary>Gets the keyword the block axis of the box was left with.</summary>
    internal String OverflowY { get; init; } = CssInitialValues.Overflow;

    /// <summary>Gets whether the inline (x) axis is a scroll axis - <c>hidden</c>/<c>scroll</c>/<c>auto</c>,
    /// but not <c>visible</c> or <c>clip</c>. A scroll axis zeroes a flex/grid item's automatic minimum
    /// size on that axis (CSS Flexbox 4.5); <c>clip</c> behaves like <c>visible</c> for sizing.</summary>
    internal Boolean ScrollsInline { get; init; }

    /// <summary>Gets whether the block (y) axis is a scroll axis, by the same rule as <see cref="ScrollsInline"/>.</summary>
    internal Boolean ScrollsBlock { get; init; }

    /// <summary>
    /// Gets where the content stands along the block axis, as <c>align-content</c> asks, which is how
    /// a form control's label is centred in the box the control was given (HTML §15.3.10).
    /// </summary>
    /// <remarks>
    /// A block container holds a single subject - its content - so this moves that content rather than
    /// sharing space out between subjects (CSS Box Alignment 3 §5.4). The initial <c>normal</c> is
    /// read as <c>stretch</c>, which asks a block container for nothing: the content stands where the
    /// flow put it.
    /// </remarks>
    internal Layout.FlexAlignment AlignContent { get; init; } = CssInitialValues.AlignContent;
}

/// <summary>
/// The outline's stroke, its width and its offset.
/// </summary>
/// <remarks>
/// Its colour is not here, for the reason the border colours are not in theirs: <c>outline-color</c>
/// begins as <c>invert</c> and falls back to the element's own colour, which depends on live state.
/// </remarks>
internal sealed record VisualEffectsArea
{
    internal static readonly VisualEffectsArea Default = new();

    internal BorderLineStyle OutlineStyle { get; init; } = BorderLineStyle.None;

    /// <summary>
    /// Gets how thick the outline is, which is zero when there is no stroke. An outline is given no
    /// width until it has a style, and its width is initialised to <c>medium</c> rather than to
    /// nothing - so a page naming a style alone gets the three pixels the specification pins that
    /// keyword to, rather than an outline that silently disappears.
    /// </summary>
    internal Double OutlineWidth { get; init; }

    internal Double OutlineOffset { get; init; }
}

/// <summary>
/// The font the element's text is set in: its size, family, weight, slant, letter spacing and line
/// height. Every one of these is inherited, so what a box has is usually what its parent had.
/// </summary>
/// <remarks>
/// Unlike the areas whose properties cannot be inherited, this one has no meaningful default to fall
/// back to: a font size that nobody declared is the render device's, not a constant, and the
/// inheriting element's answer is its parent's rather than the initial value. The resolver therefore
/// always assigns this area - either by adopting the parent's instance or by resolving - and the
/// initialisers below are only what an unfilled holder would read, never a real answer.
/// </remarks>
internal sealed record FontArea
{
    internal Double Size { get; init; }

    internal String Family { get; init; } = CssInitialValues.FontFamily;

    internal Int32 Weight { get; init; } = 400;

    internal Boolean Italic { get; init; }

    internal Double LetterSpacing { get; init; } = CssInitialValues.LetterSpacing;

    /// <summary>Gets the room added between words, in pixels: <c>normal</c> is none.</summary>
    internal Double WordSpacing { get; init; } = CssInitialValues.WordSpacing;

    /// <summary>Gets the line height in pixels, or null for <c>normal</c>, which defers to the
    /// font's own metrics.</summary>
    /// <remarks>
    /// Null means <c>normal</c> here rather than "not resolved", because this area is resolved in
    /// full before anything reads it - the ambiguity a lazily filled field would have does not arise.
    /// </remarks>
    internal Double? LineHeight { get; init; }

    /// <summary>Gets the <c>font-size-adjust</c> factor, or null where the face keeps its own size.</summary>
    internal Double? Adjust { get; init; }
}

/// <summary>
/// How the element's text is broken up and where it sits: whether white space collapses, how a line
/// is aligned, and how an inline box stands against its line.
/// </summary>
/// <remarks>
/// Inherited, like the font area, and resolved the same way - from the parent's instance where nothing
/// was authored, and from the declarations otherwise. The initialisers are what an unfilled holder
/// would read rather than a real answer, for the same reason.
/// </remarks>
/// <summary>Which ends of a block have their leading trimmed away (CSS Inline 3).</summary>
internal enum TextBoxTrim : Byte
{
    None,
    Start,
    End,
    Both,
}

/// <summary>The line a <c>text-box-edge</c> measures a block's edge to (CSS Inline 3).</summary>
internal enum TextBoxEdge : Byte
{
    Text,
    Cap,
    Ex,
    Alphabetic,
    Ideographic,
    IdeographicInk,
}

internal sealed record TextArea
{
    internal String WhiteSpace { get; init; } = CssInitialValues.WhiteSpace;

    internal String Align { get; init; } = CssInitialValues.TextAlign;

    /// <summary>
    /// Gets how the last line of a block, and a line a forced break ended, are aligned. <c>auto</c> leaves
    /// them to <c>Align</c> unless that justifies them, in which case they stand at their start edge
    /// (CSS Text 3 §6.3).
    /// </summary>
    internal String AlignLast { get; init; } = CssInitialValues.TextAlignLast;

    /// <summary>Gets where the room of a justified line may be put: between words, between characters, or both.</summary>
    internal String TextJustify { get; init; } = CssInitialValues.TextJustify;

    /// <summary>
    /// Gets the line-breaking style the block's text asks for: <c>auto</c>, <c>balance</c>,
    /// <c>stable</c> or <c>pretty</c> (CSS Text 4 §5). Only <c>balance</c> has an answer here.
    /// </summary>
    internal String WrapStyle { get; init; } = "auto";

    /// <summary>Gets where hyphenation opportunities are taken: <c>none</c>, <c>manual</c> or
    /// <c>auto</c> (CSS Text 3 §6.1). Only the soft hyphen is read here; `auto` has no dictionary.</summary>
    internal String Hyphens { get; init; } = "manual";

    /// <summary>Gets the text drawn at the end of a line a soft hyphen broke (CSS Text 4 §6.2).</summary>
    internal String HyphenateCharacter { get; init; } = "-";

    internal String VerticalAlign { get; init; } = CssInitialValues.VerticalAlign;

    /// <summary>
    /// The distance the first line of a box is set in from the edge it begins at, with its units still
    /// attached: a percentage among them is a share of the containing block, which only the layout
    /// pass has.
    /// </summary>
    internal StyleLength Indent { get; init; } = StyleLength.Zero;

    /// <summary>
    /// Gets whether the box draws itself, which is inherited like the rest of the area: an element that
    /// says nothing is seen or hidden the way its parent was, and a descendant can always ask to be seen
    /// again. <c>collapse</c> is kept with <c>hidden</c> as not visible, which is what a box that takes
    /// space and draws nothing comes to here.
    /// </summary>
    internal String Visibility { get; init; } = CssInitialValues.Visibility;

    /// <summary>Gets which ends of the block are trimmed to the edge of its first and last line.</summary>
    internal TextBoxTrim Trim { get; init; } = TextBoxTrim.None;

    /// <summary>Gets the line the block's start is trimmed to.</summary>
    internal TextBoxEdge EdgeStart { get; init; } = TextBoxEdge.Text;

    /// <summary>Gets the line the block's end is trimmed to.</summary>
    internal TextBoxEdge EdgeEnd { get; init; } = TextBoxEdge.Text;

    /// <summary>
    /// Gets how far a tab runs the line on, in pixels, or null where the box counts characters:
    /// <c>tab-size</c> takes either form, and the character form is a multiple of the space the element's
    /// font gives it (CSS Text 3 §4.2), which is a question only the layout pass can ask.
    /// </summary>
    internal Double? TabLength { get; init; }

    /// <summary>Gets how many character widths a tab runs to, which is the initial and the integer form.</summary>
    internal Double TabUnits { get; init; } = 8;

    /// <summary>
    /// Gets whether a word that no line can hold is broken where it stops fitting: <c>normal</c>,
    /// <c>break-word</c> or <c>anywhere</c> (CSS Text 3 §5.5).
    /// </summary>
    internal String OverflowWrap { get; init; } = CssInitialValues.OverflowWrap;

    /// <summary>
    /// Gets whether the script's own word rules are set aside so that a line may end between any two
    /// characters: <c>normal</c>, <c>break-all</c> or <c>keep-all</c> (CSS Text 3 §5.6).
    /// </summary>
    internal String WordBreak { get; init; } = CssInitialValues.WordBreak;

    /// <summary>
    /// Gets how strictly the line-breaking rules are applied: <c>auto</c>, <c>loose</c>, <c>normal</c>,
    /// <c>strict</c> or <c>anywhere</c> (CSS Text 3 §5.4).
    /// </summary>
    internal String LineBreak { get; init; } = CssInitialValues.LineBreak;
}

/// <summary>
/// The two decorations the painter draws.
/// </summary>
/// <remarks>
/// Inherited. What is saved is not the resolution of a value - the reader asks whether the declaration's
/// text contains a word - but doing it twice per box, once per kind, over the same two properties.
/// </remarks>
internal sealed record TextDecorationArea
{
    internal Boolean Underline { get; init; }

    internal Boolean Overline { get; init; }

    internal Boolean LineThrough { get; init; }

    /// <summary>Gets the shape every line the element asks for is drawn in.</summary>
    internal DecorationShape Style { get; init; }

    /// <summary>
    /// Gets how thick the element's lines are, in pixels, or null where the page left it to the face:
    /// <c>auto</c> and <c>from-font</c> both want the font's own underline metrics, and a percentage is
    /// already a share of the size the text is set at (CSS Text Decoration 4 §2.4).
    /// </summary>
    internal Double? Thickness { get; init; }

    /// <summary>
    /// Gets how far this element's underlines stand from the position the font gives them, in pixels, or
    /// null for <c>auto</c>. Of the decoration properties this one is inherited, and it says nothing
    /// about a line an ancestor asked for (CSS Text Decoration 4 §2.8).
    /// </summary>
    internal Double? UnderlineOffset { get; init; }

    /// <summary>
    /// Gets the colour the element's lines are drawn in, or null where it said none: the initial value is
    /// <c>currentcolor</c>, and the box that decorates supplies its own text colour then - not the colour
    /// of the text the line happens to be running across (CSS Text Decoration 4 §2.3).
    /// </summary>
    internal UInt32? Color { get; init; }

    /// <summary>
    /// Gets how far back the ends of this element's own lines stand, or null where it said nothing: the
    /// distance the page asked for, in pixels or as a share of the inline size the decorated text fills,
    /// or the engine's own answer for <c>auto</c> (CSS Text Decoration 4 §2.9.1). A share cannot be turned
    /// into pixels here, because what it measures is the text the element decorates, which is only known
    /// once the lines have been laid out.
    /// </summary>
    internal DecorationInsets? Inset { get; init; }

    /// <summary>
    /// Gets whether this element's underlines and overlines are broken where the glyphs' ink crosses
    /// them. The property is inherited, so a page that says nothing takes what stands above it - and the
    /// initial answer is the engine's own, not a straight line (CSS Text Decoration 4 §2.10.4).
    /// </summary>
    internal DecorationSkip Skip { get; init; }
}

/// <summary>
/// Where a list item's marker stands relative to its text: beside it, in the space the list was given,
/// or at the head of the first line.
/// </summary>
internal enum MarkerPosition : Byte
{
    Outside,
    Inside,
}

/// <summary>
/// How an element's list items are marked, as <c>list-style</c> and its longhands ask for it.
/// </summary>
/// <remarks>
/// All three properties are inherited, so this area travels to children the same way the font and the
/// text do. The type is the name the page wrote - <c>disc</c>, <c>cjk-decimal</c> - or, for a marker
/// written as a string, that string with its quotes: the parser hands back both, and it hands back some
/// names as plain text because it has no enumeration entry for them, so the name is the one form that
/// survives everything. Spelling a number out of it is <see cref="Boxes.CounterStyles"/>'s work.
/// <para>
/// The image is the reference as written - a URL is resolved against the document rather than the
/// style, and nothing paints one yet - so it stays a string and stays unresolved.
/// </para>
/// </remarks>
internal sealed record ListArea
{
    /// <summary>
    /// What an element that is not a list item, or a list that says nothing, shares. The type is
    /// <c>disc</c> and the position <c>outside</c>, which is what CSS makes both initial; a
    /// <c>ul</c> that reaches them through the user-agent sheet and an element that inherits them from
    /// its parent end up on this same value either way.
    /// </summary>
    internal static readonly ListArea Default = new();

    internal String Type { get; init; } = "disc";

    internal MarkerPosition Position { get; init; } = MarkerPosition.Outside;

    internal String? Image { get; init; }

    // The `list-item` counter operations this element declares on itself, if any, read out of
    // `counter-reset`/`counter-set`/`counter-increment`. They are null when the element names `list-item`
    // in none of those, which is the ordinary case. Unlike the three above they are NOT inherited - a
    // marker number belongs to the item that wears it - so `ResolveList` layers them onto whichever base
    // area it settled on rather than inheriting them with the marker's look.
    internal Int32? ListItemReset { get; init; }

    internal Int32? ListItemSet { get; init; }

    internal Int32? ListItemIncrement { get; init; }
}

/// <summary>
/// One intern table per area, alive for one box tree.
/// </summary>
/// <remarks>
/// Two boxes that resolve to the same values should be one object, whether or not they are related:
/// sibling list items that both declare <c>font-weight: bold</c> agree as much as a parent and child
/// that inherit it do, and only a table keyed by value catches both. Sharing by parent alone would not.
/// <para>
/// The table is built per box tree and dropped with it. It holds the strings the declarations handed
/// over, so one alive across documents would keep every value ever seen.
/// </para>
/// <para>
/// Interning is the last resort, not the first: a reader that can tell from the declaration that an
/// area is untouched, or that a parent's area is already in hand, should take that path and never
/// build or hash anything. The table cannot help there - it can only be consulted once the candidate
/// exists, by which point the allocation it saves has already been paid.
/// </para>
/// </remarks>
internal sealed class StyleIntern
{
    private readonly Dictionary<BorderArea, BorderArea> _border = new();

    private readonly Dictionary<BoxModelArea, BoxModelArea> _boxModel = new();

    private readonly Dictionary<DisplayPositioningArea, DisplayPositioningArea> _display = new();

    private readonly Dictionary<VisualEffectsArea, VisualEffectsArea> _visualEffects = new();

    private readonly Dictionary<FontArea, FontArea> _font = new();

    private readonly Dictionary<TextArea, TextArea> _text = new();

    private readonly Dictionary<TextDecorationArea, TextDecorationArea> _decoration = new();

    private readonly Dictionary<ListArea, ListArea> _list = new();

    /// <summary>Gets the instance standing for a list marker style.</summary>
    internal ListArea List(ListArea area)
    {
        if (_list.TryGetValue(area, out var existing))
        {
            return existing;
        }

        _list[area] = area;

        return area;
    }

    /// <summary>Gets the instance standing for a text decoration area.</summary>
    internal TextDecorationArea Decoration(TextDecorationArea area)
    {
        if (_decoration.TryGetValue(area, out var existing))
        {
            return existing;
        }

        _decoration[area] = area;

        return area;
    }

    /// <summary>Gets the instance standing for a text area.</summary>
    internal TextArea Text(TextArea area)
    {
        if (_text.TryGetValue(area, out var existing))
        {
            return existing;
        }

        _text[area] = area;

        return area;
    }

    /// <summary>Gets the instance standing for a font area.</summary>
    internal FontArea Font(FontArea area)
    {
        if (_font.TryGetValue(area, out var existing))
        {
            return existing;
        }

        _font[area] = area;

        return area;
    }

    /// <summary>Gets the instance standing for a display and positioning area.</summary>
    internal DisplayPositioningArea DisplayPositioning(DisplayPositioningArea area)
    {
        if (_display.TryGetValue(area, out var existing))
        {
            return existing;
        }

        _display[area] = area;

        return area;
    }

    /// <summary>Gets the instance standing for a visual effects area.</summary>
    internal VisualEffectsArea VisualEffects(VisualEffectsArea area)
    {
        if (_visualEffects.TryGetValue(area, out var existing))
        {
            return existing;
        }

        _visualEffects[area] = area;

        return area;
    }

    /// <summary>Gets the instance standing for a box model, building it only if the value is new.</summary>
    internal BoxModelArea BoxModel(BoxModelArea area)
    {
        if (_boxModel.TryGetValue(area, out var existing))
        {
            return existing;
        }

        _boxModel[area] = area;

        return area;
    }

    /// <summary>
    /// Gets the instance standing for <paramref name="area"/>, which is <paramref name="area"/> itself
    /// the first time that value is seen.
    /// </summary>
    internal BorderArea Border(BorderArea area)
    {
        if (_border.TryGetValue(area, out var existing))
        {
            return existing;
        }

        _border[area] = area;

        return area;
    }
}

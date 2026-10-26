using AngleSharp.Css;
using AngleSharp.Css.Dom;
using AngleSharp.Css.Parser;
using AngleSharp.Text;
using System;
using System.Collections.Generic;

namespace Hutu.Rendering;

/// <summary>
/// Reads declarations the way AngleSharp reads them, except that the keywords every property accepts
/// are accepted for every property.
/// </summary>
/// <remarks>
/// A page is entitled to write <c>margin-block-end: revert</c>, and one that does means the opposite of
/// what the box model's converter makes of it: that converter knows no such keyword, drops the
/// declaration, and leaves whatever else the page said about that side standing - which is exactly the
/// declaration the page was reverting. The keywords come out of here as a <see cref="CssWideKeyword"/>
/// instead, for the reader to give a meaning to.
/// <para>
/// It wraps the factory AngleSharp ships rather than replacing it, so every property keeps the grammar
/// and the initial value it had; the reader registers it in place of that factory because a service of
/// this kind is asked for by name, and only the first one answers.
/// </para>
/// </remarks>
internal sealed class WideKeywordDeclarationFactory : IDeclarationFactory
{
    private readonly IDeclarationFactory _inner;

    /// <summary>
    /// The wrapped declaration info, held per property name. The inner factory already caches its own
    /// <see cref="DeclarationInfo"/>, so the converter and aggregator are stable for a given name - but
    /// the wrapper we put around them is not, and would otherwise be re-allocated on every declaration
    /// of a property that recurs all over a page. Caching the wrapped result keeps that work to once.
    /// </summary>
    private readonly Dictionary<String, DeclarationInfo> _cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The properties AngleSharp has no grammar for, kept whole as text for a reader to interpret. Each is
    /// a vendor spelling the rendering requirements honour and no converter of AngleSharp's recognises, so
    /// without this the declaration would never survive the parse at all.
    /// </summary>
    private static readonly HashSet<String> RawValueNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "-webkit-background-clip",
        "-webkit-text-stroke",
        "-webkit-text-stroke-width",
        "-webkit-text-stroke-color",
        "-webkit-text-fill-color",
        // Text Decoration 4's inset, which AngleSharp has no grammar for at all: without this the whole
        // declaration is dropped and the line runs to the edges of the text however the page asked.
        "text-decoration-inset",
        // CSS Overflow's text-overflow, which AngleSharp has no grammar for: the value decides whether
        // a clipped line ends in an ellipsis (or a named string), so the layout reads it as text.
        "text-overflow",
        // CSS Sizing 4's aspect ratio, which AngleSharp has no grammar for either: the value is a ratio
        // written "21/9" or "auto 16/9", and the box model reads it as text.
        "aspect-ratio",
        // CSS Grid Lanes' direction, which AngleSharp has no grammar for: the value is the keyword
        // row or column, and the masonry layout reads it as text.
        "grid-lanes-direction",
        // CSS Inline 3's leading trim, which AngleSharp has no grammar for either: the two properties
        // are kept as text and read by the block layout.
        "text-box-trim",
        "text-box-edge",
        // CSS Fragmentation 3's break values, which AngleSharp's grammar predates (recto, verso and the
        // avoid-page / avoid-column refinements are dropped without this). The fragmentation reader keeps
        // each value as text.
        "break-before",
        "break-after",
        "break-inside",
        "page-break-before",
        "page-break-after",
        "page-break-inside",
        // The grid-lanes axis/packing descriptor, which AngleSharp has no grammar for at all; the lanes
        // formatting context reads it as text.
        "grid-lanes-direction",
        // CSS Motion Path (offset-*). AngleSharp has no grammar for these, so without this the whole
        // declaration is dropped and an element positioned along a path would never be moved. The motion
        // path reader keeps each value as text and computes the offset transform from it.
        "offset-path",
        "offset-distance",
        "offset-rotate",
        "offset-anchor",
        "offset-position",
        "offset",
        // `transform` is kept whole so a value written with var() - Tailwind's
        // `translate(var(--tw-translate-x), …)` - survives to the VariableScope. AngleSharp's own
        // transform grammar rejects an unresolved var() and discards the declaration to `none`, which
        // would leave nothing for our substitution to resolve.
        "transform",
        // CSS Transforms 1's reference box for the transform-origin. AngleSharp's grammar has no entry
        // for it, so without this the whole declaration is dropped and an origin stated against the
        // content box would be measured from the border box instead.
        "transform-box",
        // CSS Sizing 4 §5.1's override for the size a size-contained box would otherwise have none of.
        // AngleSharp has no grammar for the shorthand or its longhands, so a page stating one had the
        // declaration dropped and the box came out empty.
        "contain-intrinsic-size",
        "contain-intrinsic-width",
        "contain-intrinsic-height",
        "contain-intrinsic-inline-size",
        "contain-intrinsic-block-size",
        // CSS Will Change 1's hint list, kept as the text the page wrote: AngleSharp reads the list
        // against the property names it knows, and rejects the ones its own registry does not carry
        // (`contain`, `backdrop-filter`), which would drop half of a hint that the stacking answer needs
        // whole. The style reads the names itself.
        "will-change",
        // CSS Shapes 1 §6.1's float area. AngleSharp's grammar for this one recognizes only the keyword
        // `none`, so every shape a page draws a float's edge with - and the images, which are the second
        // alternative of the same grammar - would be dropped at the parse and the float would keep its
        // margin box. The shape reader takes the text from here.
        "shape-outside",
        // CSS Viewport's zoom, which AngleSharp names but gives no grammar to: the value is a plain
        // number or a percentage, either of which its length-or-keyword converter rejects, so without
        // this the declaration is dropped and the box is drawn at the size the page wrote rather than
        // the size it asked to scale that by. The style reads the factor from the text.
        "zoom",
        // CSS Text Decoration 4 §5's emphasis marks. AngleSharp has no grammar for any of these, so
        // without this the declaration is dropped at the parse and a page marking its text is left with
        // none of the marks it asked for. The style reads each value as the text it was written in, and
        // every one of them is inherited down to the runs it marks.
        "text-emphasis",
        "text-emphasis-style",
        "text-emphasis-color",
        "text-emphasis-position",
        "text-emphasis-skip",
        // CSS Lists 3 §2.3's ten descriptors of `@counter-style`. AngleSharp has no grammar for any of
        // them - they name no property of an element - so every one of them was dropped at the parse and
        // the style the page wrote arrived with nothing in it. The table reads each value as the text it
        // was written in, which is what a list of symbols and a system name are anyway.
        "system",
        "symbols",
        "additive-symbols",
        "negative",
        "prefix",
        "suffix",
        "pad",
        "range",
        "fallback",
        "speak-as",
    };

    /// <summary>
    /// The raw-kept names that are inherited rather than answered from the box's own rule, which is what
    /// carries an element's marks down to the text inside it (CSS Text Decoration 4 §5).
    /// </summary>
    private static readonly HashSet<String> InheritedRawNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "text-emphasis",
        "text-emphasis-style",
        "text-emphasis-color",
        "text-emphasis-position",
        "text-emphasis-skip",
    };

    /// <summary>
    /// The keywords a known property's own grammar rejects but the requirements accept, gathered per
    /// property. <c>background-clip</c> takes <c>text</c>; the colour properties take the <c>-moz-</c>
    /// system colours standing for the user's default page ink and canvas, which the renderer resolves to
    /// its own defaults. They are kept as raw text so the declaration survives and - being the one the
    /// page wrote last - overrides what came before it, which is what a "default" keyword is for.
    /// </summary>
    private static readonly Dictionary<String, String[]> KeywordExtraValues = new(StringComparer.OrdinalIgnoreCase)
    {
        ["background-clip"] = ["text"],
        // `currentcolor` is part of the color production (CSS Color 4 §4) but AngleSharp's colour
        // grammar stops short of it, so a page writing `color: currentcolor` had the declaration
        // dropped and the link drawn in the engine's own colour. The colour reader resolves the keyword.
        ["color"] = ["-moz-default-color", "currentcolor"],
        ["background-color"] = ["-moz-default-background-color"],
        // CSS Text 3's break-spaces, which AngleSharp's grammar predates: dropped, the declaration
        // would leave the element on the white-space it inherited and its spaces would collapse.
        ["white-space"] = ["break-spaces"],
        // The two values of the tailorable break properties that AngleSharp's grammars stop short of.
        // Dropped, an element asking for either inherits `normal` and its words overflow the box, which
        // is the one thing those values were written to stop.
        ["overflow-wrap"] = ["anywhere"],
        ["word-wrap"] = ["anywhere"],
        ["line-break"] = ["normal"],
        // The spellings of alignment AngleSharp's grammar for these properties stops short of: the two
        // physical edges (CSS Box Alignment 3 §6.2), the two that name the item's own line
        // (`self-start`, `self-end`, which its FlexContentMode lists but its keyword converter does
        // not), and `normal`, which its converter reads as `auto` - losing the difference between the
        // layout's own answer and the container's. Dropped or merged, an item asking for one of them is
        // left on the container's default instead of the edge it named.
        ["align-self"] = ["left", "right", "self-start", "self-end", "normal", "last baseline"],
        ["justify-self"] = ["left", "right", "self-start", "self-end", "normal", "last baseline"],
        ["align-items"] = ["left", "right", "self-start", "self-end", "normal", "last baseline"],
        ["justify-items"] = ["left", "right", "self-start", "self-end", "normal", "last baseline"],
        ["align-content"] = ["left", "right", "self-start", "self-end"],
        ["justify-content"] = ["left", "right", "self-start", "self-end"],
        // CSS Sizing 3 §5's sizes that settle from the content rather than from a length. AngleSharp's
        // grammar for the size properties takes only `auto`, a length or a share, so a page asking for
        // one of these had the whole declaration dropped and the box fell back to `auto` - a
        // `height: max-content` item then stretched instead of standing at its content's size, and a
        // `min-width: min-content` floor vanished.
        ["width"] = ContentSizeKeywords,
        ["height"] = ContentSizeKeywords,
        ["min-width"] = ContentSizeKeywords,
        ["min-height"] = ContentSizeKeywords,
        ["max-width"] = ContentSizeKeywords,
        ["max-height"] = ContentSizeKeywords,
        ["inline-size"] = ContentSizeKeywords,
        ["block-size"] = ContentSizeKeywords,
        ["min-inline-size"] = ContentSizeKeywords,
        ["min-block-size"] = ContentSizeKeywords,
        ["max-inline-size"] = ContentSizeKeywords,
        ["max-block-size"] = ContentSizeKeywords,
    };

    /// <summary>Gets the content-based sizes of CSS Sizing 3 §5, which the layout reads as themselves.</summary>
    private static String[] ContentSizeKeywords => ["max-content", "min-content", "fit-content"];

    /// <summary>
    /// The properties whose value is kept whole when it merely names one of these keywords somewhere in
    /// it, rather than being exactly that keyword. <c>display</c> is the case: the grid-lanes spelling
    /// arrives as a two-value form (<c>inline grid-lanes</c>, <c>block masonry</c>) that an exact
    /// whole-value match would miss, and AngleSharp rejects the whole declaration for the unknown inner
    /// keyword. Reading it keeps the page's own text for the box builder to route.
    /// </summary>
    /// <summary>
    /// The tokens that make a known property's value unreadable to its own grammar but meaningful to the
    /// reader: a value holding one of them is kept whole as the text the page wrote.
    /// </summary>
    /// <remarks>
    /// A relative colour names its origin and then the channels it comes to - <c>rgb(from currentcolor r
    /// g b)</c>, CSS Color 5 §4 - and AngleSharp's colour grammar stops short of the form, so the whole
    /// declaration was dropped and the colour the page said before it stood in its place. The reader
    /// takes the text from here.
    /// </remarks>
    private static readonly Dictionary<String, String[]> KeywordExtraSubstrings = new(StringComparer.OrdinalIgnoreCase)
    {
        ["color"] = ["(from ", "light-dark("],
        ["background-color"] = ["(from ", "light-dark("],
        // CSS Lists 3 §3.3's `symbols()` function, which AngleSharp's grammar for list-style-type stops
        // short of: dropped, a list marked with its own symbols keeps the disc the sheet did not ask for.
        ["list-style-type"] = ["symbols("],
        // A `counter()` written with a symbols() list - `content: counter(a, symbols('*'))`, CSS Lists 3
        // §5.2 - is rejected by AngleSharp's grammar for the counter function, which stops at an
        // identifier. Dropped, the generated content vanishes; kept whole, the content reader takes the
        // marks from the same place the marker does.
        ["content"] = ["symbols("],
        // `fit-content(<length>)` is the same content-based size with a cap, and it reaches the reader
        // as one token rather than as a bare keyword.
        ["width"] = ["fit-content("],
        ["height"] = ["fit-content("],
        ["min-width"] = ["fit-content("],
        ["min-height"] = ["fit-content("],
        ["max-width"] = ["fit-content("],
        ["max-height"] = ["fit-content("],
        ["inline-size"] = ["fit-content("],
        ["block-size"] = ["fit-content("],
        ["min-inline-size"] = ["fit-content("],
        ["min-block-size"] = ["fit-content("],
        ["max-inline-size"] = ["fit-content("],
        ["max-block-size"] = ["fit-content("],
    };

    /// <summary>
    /// The properties whose value is kept whole when it merely names one of these keywords somewhere in
    /// it, rather than being exactly that keyword. <c>display</c> is the case: the grid-lanes spelling
    /// arrives as a two-value form (<c>inline grid-lanes</c>, <c>block masonry</c>) that an exact
    /// whole-value match would miss, and AngleSharp rejects the whole declaration for the unknown inner
    /// keyword. Reading it keeps the page's own text for the box builder to route.
    /// </summary>
    private static readonly Dictionary<String, String[]> KeywordSubstringValues = new(StringComparer.OrdinalIgnoreCase)
    {
        // `contents` rides along for the same reason the two above do: the element keeps a box here until
        // the box tree learns to leave one out (CSS Display 3 §2.5), but what the display *says* is what
        // CSS Contain 2 §2.4 and CSS 2.1 §Appendix E ask about - a contents box is not a principal box, so
        // containment does not reach it and it forms no context - and dropped at the parse the element
        // read as the plain block its declaration was standing in for.
        ["display"] = ["grid-lanes", "masonry", "flow-root", "contents"],
    };

    /// <param name="inner">
    /// The factory whose grammar every declaration keeps. Without one, the reader reads declarations
    /// the way AngleSharp's own factory does.
    /// </param>
    public WideKeywordDeclarationFactory(IDeclarationFactory? inner = null) =>
        _inner = inner ?? new DefaultDeclarationFactory();

    public DeclarationInfo Create(String propertyName)
    {
        if (_cache.TryGetValue(propertyName, out var cached))
        {
            return cached;
        }

        // A vendor name AngleSharp cannot parse is kept whole rather than dropped, so the reader that
        // meets it can make of the text what the property means. It has no AngleSharp grammar to keep, so
        // no shorthands or longhands ride with it.
        if (RawValueNames.Contains(propertyName))
        {
            // The emphasis properties mark the text of everything inside the box asking for them, so they
            // carry the inherited mark: the cascade then passes the declaration down to the children,
            // which is what puts a mark over a run the page never wrote a property on.
            var flags = InheritedRawNames.Contains(propertyName)
                ? PropertyFlags.Inherited
                : PropertyFlags.None;

            var raw = new DeclarationInfo(
                propertyName,
                new RawKeep(),
                flags,
                new CssRawValue(String.Empty),
                Array.Empty<String>(),
                Array.Empty<String>());

            _cache[propertyName] = raw;
            return raw;
        }

        var info = _inner.Create(propertyName);

        // A name AngleSharp has no grammar for is marked unknown on the declaration it hands back, and a
        // supports condition reads that mark off the converter's identity: `Object.Equals(converter,
        // ValueConverters.Any)` is how it answers that the property is not one this engine knows. Wrapping
        // the converter - which is what every known property gets below - hides that identity, and a name
        // nobody recognizes then answers as a feature the page can claim to have (CSS Conditional 3 §3).
        if (info.Flags.HasFlag(PropertyFlags.Unknown))
        {
            _cache[propertyName] = info;
            return info;
        }

        // A known property whose grammar stops short of a keyword the requirements accept is wrapped so
        // only that keyword is taken aside - every other value goes to AngleSharp as before, leaving its
        // own grammar and a shorthand's decomposition through it untouched. The rest keep their wide-keyword
        // reading.
        // A property can be given keywords to take aside, tokens that make a value worth keeping whole,
        // or both; `list-style-type` is the one that needs the second without the first, since its own
        // grammar knows every symbol name but not the `symbols()` function.
        var hasExtras = KeywordExtraValues.TryGetValue(propertyName, out var extra);
        var hasSubstrings = KeywordExtraSubstrings.TryGetValue(propertyName, out var extraContains);

        IValueConverter converter =
            hasExtras || hasSubstrings
                ? new ExtraKeywords(info.Converter, extra ?? Array.Empty<String>(), hasSubstrings ? extraContains : null) :
            KeywordSubstringValues.TryGetValue(propertyName, out var contains) ? new AnyTokenPassThrough(info.Converter, contains) :
            new KeywordsFirst(info.Converter, info.Aggregator);

        var wrapped = new DeclarationInfo(
            info.Name,
            converter,
            info.Flags,
            info.InitialValue,
            info.Shorthands,
            info.Longhands);

        _cache[propertyName] = wrapped;

        return wrapped;
    }

    /// <summary>
    /// Reads a value the way the declaration it belongs to would, having first taken the keywords that
    /// stand for a value rather than naming one.
    /// </summary>
    /// <remarks>
    /// The keyword is read and then put back, because a converter is handed the declaration's text and
    /// has to find it where it was left: this one is in front of another, and the other has to see the
    /// whole of what it was written for.
    /// <para>
    /// It answers for the collector the declaration had as well, and for the same reason: a declaration
    /// is told how to take a shorthand apart by asking its converter whether it is one, so a wrapper that
    /// were only a converter would leave every <c>margin</c>, <c>padding</c> and <c>font</c> in the book
    /// unable to reach the longhands they are written for. The collector is taken from the declaration
    /// rather than from the converter it is handed, because a converter that has already been wrapped to
    /// carry an initial value is no longer recognisable as one.
    /// </para>
    /// </remarks>
    private sealed class KeywordsFirst : IValueConverter, IValueAggregator
    {
        private readonly IValueConverter _inner;
        private readonly IValueAggregator? _aggregator;

        public KeywordsFirst(IValueConverter inner, IValueAggregator? aggregator)
        {
            _inner = inner;
            _aggregator = aggregator;
        }

        public ICssValue? Convert(StringSource source)
        {
            var start = source.Index;
            var text = source.ParseIdent();

            if (CssWideKeyword.Is(text))
            {
                return new CssWideKeyword(text!);
            }

            while (source.Index > start)
            {
                source.Back();
            }

            return _inner.Convert(source);
        }

        public ICssValue[]? Split(ICssValue value) => _aggregator?.Split(value);

        public ICssValue? Merge(ICssValue[] values) => _aggregator?.Merge(values);
    }

    /// <summary>
    /// Keeps the whole value of a property AngleSharp has no grammar for, exactly as the page wrote it,
    /// save that a value which is only a wide keyword still reads as one.
    /// </summary>
    /// <remarks>
    /// The source for a declaration is bounded to that declaration, so what remains from here to the end
    /// is the value; it is taken whole and consumed to the end, which is what the parser expects a
    /// converter to do. The reader downstream splits it apart again the way the property means it.
    /// </remarks>
    private sealed class RawKeep : IValueConverter
    {
        public ICssValue? Convert(StringSource source)
        {
            var raw = source.Content.Substring(source.Index);

            while (!source.IsDone)
            {
                source.Next();
            }

            var trimmed = raw.Trim();

            return CssWideKeyword.Is(trimmed)
                ? new CssWideKeyword(trimmed)
                : new CssRawValue(raw);
        }
    }

    /// <summary>
    /// Reads a known property's value, accepting a short list of keywords its own grammar does not and
    /// keeping each as raw text for the reader to resolve; a wide keyword still reads as one.
    /// </summary>
    /// <remarks>
    /// The whole value is looked at before anything is consumed, so a value that is none of the accepted
    /// keywords is still sitting at its start for AngleSharp's own converter - the same discipline
    /// <see cref="KeywordsFirst"/> keeps, and the reason a background shorthand's decomposition is not
    /// disturbed by a keyword read here.
    /// </remarks>
    private sealed class ExtraKeywords : IValueConverter
    {
        private readonly IValueConverter _inner;
        private readonly String[] _extra;
        private readonly String[]? _contains;

        public ExtraKeywords(IValueConverter inner, String[] extra, String[]? contains = null)
        {
            _inner = inner;
            _extra = extra;
            _contains = contains;
        }

        public ICssValue? Convert(StringSource source)
        {
            var whole = source.Content.Substring(source.Index).Trim();

            if (CssWideKeyword.Is(whole))
            {
                Consume(source);
                return new CssWideKeyword(whole);
            }

            if (Array.Exists(_extra, keyword => keyword.Equals(whole, StringComparison.OrdinalIgnoreCase)) ||
                (_contains is not null &&
                 Array.Exists(_contains, token => whole.Contains(token, StringComparison.OrdinalIgnoreCase))))
            {
                Consume(source);
                return new CssRawValue(whole);
            }

            return _inner.Convert(source);
        }

        private static void Consume(StringSource source)
        {
            while (!source.IsDone)
            {
                source.Next();
            }
        }
    }

    /// <summary>
    /// Reads a known property's value and keeps the whole declaration when any token of it names one of a
    /// short list of keywords AngleSharp's grammar does not - <c>display: inline grid-lanes</c>, whose
    /// two-value form an exact whole-value match would miss. A value naming none of them is left at its
    /// start for AngleSharp's own converter, so a wide keyword or an ordinary value is read as it always
    /// was.
    /// </summary>
    private sealed class AnyTokenPassThrough : IValueConverter
    {
        private readonly IValueConverter _inner;
        private readonly String[] _tokens;

        public AnyTokenPassThrough(IValueConverter inner, String[] tokens)
        {
            _inner = inner;
            _tokens = tokens;
        }

        public ICssValue? Convert(StringSource source)
        {
            var whole = source.Content.Substring(source.Index).Trim();

            if (CssWideKeyword.Is(whole))
            {
                Consume(source);
                return new CssWideKeyword(whole);
            }

            if (Array.Exists(_tokens, token => whole.Contains(token, StringComparison.OrdinalIgnoreCase)))
            {
                Consume(source);
                return new CssRawValue(whole);
            }

            return _inner.Convert(source);
        }

        private static void Consume(StringSource source)
        {
            while (!source.IsDone)
            {
                source.Next();
            }
        }
    }
}

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
        ["color"] = ["-moz-default-color"],
        ["background-color"] = ["-moz-default-background-color"],
        // CSS Text 3's break-spaces, which AngleSharp's grammar predates: dropped, the declaration
        // would leave the element on the white-space it inherited and its spaces would collapse.
        ["white-space"] = ["break-spaces"],
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
        ["display"] = ["grid-lanes", "masonry", "flow-root"],
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
            var raw = new DeclarationInfo(
                propertyName,
                new RawKeep(),
                PropertyFlags.None,
                new CssRawValue(String.Empty),
                Array.Empty<String>(),
                Array.Empty<String>());

            _cache[propertyName] = raw;
            return raw;
        }

        var info = _inner.Create(propertyName);

        // A known property whose grammar stops short of a keyword the requirements accept is wrapped so
        // only that keyword is taken aside - every other value goes to AngleSharp as before, leaving its
        // own grammar and a shorthand's decomposition through it untouched. The rest keep their wide-keyword
        // reading.
        IValueConverter converter =
            KeywordExtraValues.TryGetValue(propertyName, out var extra) ? new ExtraKeywords(info.Converter, extra) :
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

        public ExtraKeywords(IValueConverter inner, String[] extra)
        {
            _inner = inner;
            _extra = extra;
        }

        public ICssValue? Convert(StringSource source)
        {
            var whole = source.Content.Substring(source.Index).Trim();

            if (CssWideKeyword.Is(whole))
            {
                Consume(source);
                return new CssWideKeyword(whole);
            }

            if (Array.Exists(_extra, keyword => keyword.Equals(whole, StringComparison.OrdinalIgnoreCase)))
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

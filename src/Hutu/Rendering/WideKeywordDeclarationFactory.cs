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

        // background-clip is one AngleSharp knows, yet its grammar stops short of the "text" the
        // requirements accept. Only that keyword is taken aside and every other value handed to AngleSharp
        // as before, so a background shorthand's own decomposition - which writes the box values here - is
        // left untouched.
        var converter = propertyName.Equals("background-clip", StringComparison.OrdinalIgnoreCase)
            ? (IValueConverter)new TextAlsoPassesThrough(info.Converter)
            : new KeywordsFirst(info.Converter, info.Aggregator);

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
    /// Hands <c>background-clip</c> the one value AngleSharp's own grammar rejects - <c>text</c> - while
    /// leaving every value it does know to be read as it always was.
    /// </summary>
    /// <remarks>
    /// The keyword is read and then put back when it is not one of the two this converter claims, so the
    /// wrapped converter sees exactly what it would have seen had this never stood in front of it - the
    /// same discipline <see cref="KeywordsFirst"/> keeps for the wide keywords.
    /// </remarks>
    private sealed class TextAlsoPassesThrough : IValueConverter
    {
        private readonly IValueConverter _inner;

        public TextAlsoPassesThrough(IValueConverter inner) => _inner = inner;

        public ICssValue? Convert(StringSource source)
        {
            var start = source.Index;
            var ident = source.ParseIdent();

            if (CssWideKeyword.Is(ident))
            {
                return new CssWideKeyword(ident!);
            }

            if (!String.IsNullOrEmpty(ident) && ident!.Equals("text", StringComparison.OrdinalIgnoreCase))
            {
                return new CssRawValue("text");
            }

            while (source.Index > start)
            {
                source.Back();
            }

            return _inner.Convert(source);
        }
    }
}

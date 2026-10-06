using Hutu.Rendering.Layout;
using SkiaSharp;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace Hutu.Skia;

/// <summary>
/// Maps a CSS font request onto an <see cref="SKTypeface"/>, resolving the family list in order and
/// falling back to a platform default when nothing matches.
/// </summary>
/// <remarks>
/// Typefaces are expensive to create and are safe to share, so they are cached per family/weight/
/// slant for the lifetime of the resolver. Matched typefaces are owned by the caller through
/// <see cref="Dispose"/>.
/// </remarks>
public sealed class SkiaFontResolver : IDisposable
{
    private readonly ConcurrentDictionary<FaceKey, SKTypeface> _typefaces = new();
    private readonly ConcurrentDictionary<FallbackKey, Fallback> _fallbacks = new();

    public SKTypeface Resolve(FontStyle style) =>
        _typefaces.GetOrAdd(new FaceKey(style.Family, style.Weight, style.IsItalic), CreateTypeface);

    /// <summary>
    /// Gets a typeface the system offers for a single character, which is what a family is asked for
    /// when it has no glyph of its own - a Latin family meeting CJK text, say.
    /// </summary>
    /// <remarks>
    /// The styles are searched with the weight and slant of the request so a bold face stays bold
    /// where the system has one. The answer is cached per character and style, misses included, so a
    /// page that repeats an unusual glyph asks the platform only once. <c>null</c> means the platform
    /// has nothing better than the resolved family, whose own .notdef stands.
    /// </remarks>
    public SKTypeface? ResolveFallback(FontStyle style, Int32 codepoint) =>
        _fallbacks.GetOrAdd(
            new FallbackKey(codepoint, style.Weight, style.IsItalic),
            _ => FindFallback(codepoint, style)).Typeface;

    /// <summary>
    /// Splits a CSS family list into individual names, stripping the quotes a family containing
    /// spaces is required to carry.
    /// </summary>
    public static IEnumerable<String> SplitFamilies(String familyList)
    {
        if (String.IsNullOrWhiteSpace(familyList))
        {
            yield break;
        }

        foreach (var part in familyList.Split(','))
        {
            var name = part.Trim().Trim('"', '\'');

            if (name.Length > 0)
            {
                yield return name;
            }
        }
    }

    private static SKTypeface CreateTypeface(FaceKey key)
    {
        var style = CreateStyle(key.Weight, key.Italic);

        foreach (var family in SplitFamilies(key.Family))
        {
            var match = SKFontManager.Default.MatchFamily(family, style);

            if (match is not null)
            {
                return match;
            }
        }

        // "sans-serif" is the CSS generic; platforms that do not know the name fall through to the
        // process default, which is what the initial value of font-family resolves to anyway.
        return SKFontManager.Default.MatchFamily("sans-serif", style) ?? SKTypeface.Default;
    }

    private static SKFontStyle CreateStyle(Int32 weight, Boolean italic) =>
        new(weight, (Int32)SKFontStyleWidth.Normal, italic ? SKFontStyleSlant.Italic : SKFontStyleSlant.Upright);

    private static Fallback FindFallback(Int32 codepoint, FontStyle style)
    {
        // The family is left unset so the platform searches its whole system for the character rather
        // than the one family that failed to have it.
        var match = SKFontManager.Default.MatchCharacter(null, CreateStyle(style.Weight, style.IsItalic), null, codepoint);

        // The manager is documented to answer with the character's font, but a face that merely
        // belongs to the same script is no improvement, so only one that carries the glyph is kept.
        return match is not null && match.GetGlyph(codepoint) != 0 ? new Fallback(match) : default;
    }

    public void Dispose()
    {
        foreach (var typeface in _typefaces.Values)
        {
            typeface.Dispose();
        }

        _typefaces.Clear();

        foreach (var fallback in _fallbacks.Values)
        {
            fallback.Typeface?.Dispose();
        }

        _fallbacks.Clear();
    }

    private readonly struct FaceKey : IEquatable<FaceKey>
    {
        private readonly String _family;

        public FaceKey(String family, Int32 weight, Boolean italic)
        {
            _family = family;
            Weight = weight;
            Italic = italic;
        }

        public String Family => _family;

        public Int32 Weight { get; }

        public Boolean Italic { get; }

        public Boolean Equals(FaceKey other) =>
            String.Equals(_family, other._family, StringComparison.Ordinal) &&
            Weight == other.Weight &&
            Italic == other.Italic;

        public override Boolean Equals(Object? obj) => obj is FaceKey other && Equals(other);

        public override Int32 GetHashCode()
        {
            unchecked
            {
                var hash = StringComparer.Ordinal.GetHashCode(_family ?? String.Empty);
                hash = (hash * 397) ^ Weight;
                hash = (hash * 397) ^ (Italic ? 1 : 0);
                return hash;
            }
        }
    }

    private readonly struct FallbackKey : IEquatable<FallbackKey>
    {
        public FallbackKey(Int32 codepoint, Int32 weight, Boolean italic)
        {
            Codepoint = codepoint;
            Weight = weight;
            Italic = italic;
        }

        public Int32 Codepoint { get; }

        public Int32 Weight { get; }

        public Boolean Italic { get; }

        public Boolean Equals(FallbackKey other) =>
            Codepoint == other.Codepoint && Weight == other.Weight && Italic == other.Italic;

        public override Boolean Equals(Object? obj) => obj is FallbackKey other && Equals(other);

        public override Int32 GetHashCode()
        {
            unchecked
            {
                var hash = Codepoint;
                hash = (hash * 397) ^ Weight;
                hash = (hash * 397) ^ (Italic ? 1 : 0);
                return hash;
            }
        }
    }

    /// <summary>
    /// The cached answer for a character: the system typeface that carries it, or the default value
    /// when there is none. A value type is what lets a miss be cached, since the dictionary refuses a
    /// null entry.
    /// </summary>
    private readonly struct Fallback
    {
        public Fallback(SKTypeface typeface) => Typeface = typeface;

        public SKTypeface? Typeface { get; }
    }
}

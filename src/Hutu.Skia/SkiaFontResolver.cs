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

    public SKTypeface Resolve(FontStyle style) =>
        _typefaces.GetOrAdd(new FaceKey(style.Family, style.Weight, style.IsItalic), CreateTypeface);

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

    public void Dispose()
    {
        foreach (var typeface in _typefaces.Values)
        {
            typeface.Dispose();
        }

        _typefaces.Clear();
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
}

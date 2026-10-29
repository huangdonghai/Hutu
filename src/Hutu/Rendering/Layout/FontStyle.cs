using System;
using System.Collections.Generic;

namespace Hutu.Rendering.Layout;

/// <summary>
/// A resolved font request: the family list from CSS after inheritance, plus the size, weight and
/// slant. Two instances with the same values must compare equal - it is the key the backends cache
/// their typefaces and fonts under.
/// </summary>
public readonly struct FontStyle : IEquatable<FontStyle>
{
    public FontStyle(
        String family,
        Double size,
        Int32 weight = 400,
        Boolean italic = false,
        Double? adjust = null,
        String? features = null)
    {
        Family = String.IsNullOrWhiteSpace(family) ? "sans-serif" : family;
        Size = size;
        Weight = weight;
        IsItalic = italic;
        Adjust = adjust;
        Features = String.IsNullOrWhiteSpace(features) ? null : features;
    }

    /// <summary>Gets the CSS family list, e.g. <c>"Helvetica Neue", Arial, sans-serif</c>.</summary>
    public String Family { get; }

    /// <summary>Gets the names of that list, in the order they are asked for.</summary>
    public IEnumerable<String> Families => Split(Family);

    /// <summary>
    /// Takes a CSS family list apart into the names a font has to be looked up by.
    /// </summary>
    /// <remarks>
    /// A name is quoted when it has spaces in it, and the quotes are not part of it: leaving them on
    /// would ask the platform for a family called <c>"Helvetica Neue"</c>, which nothing is called.
    /// </remarks>
    public static IEnumerable<String> Split(String familyList)
    {
        if (String.IsNullOrWhiteSpace(familyList))
        {
            yield break;
        }

        foreach (var part in familyList.Split(','))
        {
            var name = Unquote(part);

            if (name.Length > 0)
            {
                yield return name;
            }
        }
    }

    /// <summary>Gets a family name without the quotes a name with spaces in it is written in.</summary>
    public static String Unquote(String name) => name.Trim().Trim('"', '\'');

    public Double Size { get; }

    public Int32 Weight { get; }

    public Boolean IsItalic { get; }

    /// <summary>
    /// Gets the <c>font-size-adjust</c> the text asks for, or null where it asks for none. The size
    /// above stays the CSS size the lengths around the text are measured in; the face is drawn at the
    /// size this factor works out (CSS Fonts 3 §3.6).
    /// </summary>
    public Double? Adjust { get; }

    /// <summary>
    /// Gets the OpenType features <c>font-feature-settings</c> asks this text to be set with, written
    /// the way the property was resolved - <c>tag=value</c> entries separated by commas - or null
    /// where it asks for none (CSS Fonts 4 §6.4).
    /// </summary>
    /// <remarks>
    /// Kept as the property's own text rather than as parsed tags because this type is the key every
    /// backend caches its fonts and shaped runs under: a backend that cannot read a value still has to
    /// tell two values apart, and one that can parses it once for its own table.
    /// </remarks>
    public String? Features { get; }

    public Boolean Equals(FontStyle other) =>
        String.Equals(Family, other.Family, StringComparison.Ordinal) &&
        Size.Equals(other.Size) &&
        Weight == other.Weight &&
        IsItalic == other.IsItalic &&
        Nullable.Equals(Adjust, other.Adjust) &&
        String.Equals(Features, other.Features, StringComparison.Ordinal);

    public override Boolean Equals(Object? obj) => obj is FontStyle other && Equals(other);

    public override Int32 GetHashCode()
    {
        unchecked
        {
            var hash = StringComparer.Ordinal.GetHashCode(Family);
            hash = (hash * 397) ^ Size.GetHashCode();
            hash = (hash * 397) ^ Adjust.GetHashCode();
            hash = (hash * 397) ^ Weight;
            hash = (hash * 397) ^ (IsItalic ? 1 : 0);
            hash = (hash * 397) ^ (Features is null ? 0 : StringComparer.Ordinal.GetHashCode(Features));
            return hash;
        }
    }
}

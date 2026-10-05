using System;

namespace Hutu.Rendering.Layout;

/// <summary>
/// A resolved font request: the family list from CSS after inheritance, plus the size, weight and
/// slant. Two instances with the same values must compare equal - it is the key the backends cache
/// their typefaces and fonts under.
/// </summary>
public readonly struct FontStyle : IEquatable<FontStyle>
{
    public FontStyle(String family, Double size, Int32 weight = 400, Boolean italic = false)
    {
        Family = String.IsNullOrWhiteSpace(family) ? "sans-serif" : family;
        Size = size;
        Weight = weight;
        IsItalic = italic;
    }

    /// <summary>Gets the CSS family list, e.g. <c>"Helvetica Neue", Arial, sans-serif</c>.</summary>
    public String Family { get; }

    public Double Size { get; }

    public Int32 Weight { get; }

    public Boolean IsItalic { get; }

    public Boolean Equals(FontStyle other) =>
        String.Equals(Family, other.Family, StringComparison.Ordinal) &&
        Size.Equals(other.Size) &&
        Weight == other.Weight &&
        IsItalic == other.IsItalic;

    public override Boolean Equals(Object? obj) => obj is FontStyle other && Equals(other);

    public override Int32 GetHashCode()
    {
        unchecked
        {
            var hash = StringComparer.Ordinal.GetHashCode(Family);
            hash = (hash * 397) ^ Size.GetHashCode();
            hash = (hash * 397) ^ Weight;
            hash = (hash * 397) ^ (IsItalic ? 1 : 0);
            return hash;
        }
    }
}

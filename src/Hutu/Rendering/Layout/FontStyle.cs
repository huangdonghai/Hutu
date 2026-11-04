using System;
using System.Collections.Generic;

namespace Hutu.Rendering.Layout;

/// <summary>
/// A handle to a resolved font request: the family list from CSS after inheritance, plus the size,
/// weight and slant.
/// </summary>
/// <remarks>
/// The values themselves live once in a <see cref="FontStyleManager"/>. Equal values are filed
/// under the same entry, the entry's sequential number is its id, and that id is the hash. A public
/// constructor interns into the shared manager so a caller that only has a <see cref="FontStyle"/>
/// still gets one entry per value; the renderer uses its own manager so a document's entries are
/// released with the document.
/// </remarks>
public readonly struct FontStyle : IEquatable<FontStyle>
{
    private readonly FontStyleEntry? _entry;

    public FontStyle(
        String family,
        Double size,
        Int32 weight = 400,
        Boolean italic = false,
        Double? adjust = null,
        String? features = null) =>
        _entry = FontStyleManager.Shared.GetOrAdd(family, size, weight, italic, adjust, features)._entry;

    internal FontStyle(FontStyleEntry entry) => _entry = entry;

    /// <summary>Gets the entry's sequential number in its manager, or zero for a default handle.</summary>
    public Int32 Id => _entry?.Index ?? 0;

    /// <summary>Gets the manager that owns this handle, or null for a default handle.</summary>
    internal FontStyleManager? Manager => _entry?.Manager;

    /// <summary>Gets the CSS family list, e.g. <c>"Helvetica Neue", Arial, sans-serif</c>.</summary>
    public String Family => _entry?.Family ?? "sans-serif";

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

    public Double Size => _entry?.Size ?? 0;

    public Int32 Weight => _entry?.Weight ?? 400;

    public Boolean IsItalic => _entry?.IsItalic ?? false;

    /// <summary>
    /// Gets the <c>font-size-adjust</c> the text asks for, or null where it asks for none. The size
    /// above stays the CSS size the lengths around the text are measured in; the face is drawn at the
    /// size this factor works out (CSS Fonts 3 §3.6).
    /// </summary>
    public Double? Adjust => _entry?.Adjust;

    /// <summary>
    /// Gets the OpenType features <c>font-feature-settings</c> asks this text to be set with, written
    /// the way the property was resolved - <c>tag=value</c> entries separated by commas - or null
    /// where it asks for none (CSS Fonts 4 §6.4).
    /// </summary>
    public String? Features => _entry?.Features;

    /// <summary>
    /// Gets whether the features ask for the font's vertical width forms (<c>vhal</c> or
    /// <c>vpal</c>), which change how far an upright character advances down a column.
    /// </summary>
    public Boolean AsksForVerticalWidth => _entry?.AsksForVerticalWidth ?? false;

    /// <summary>Gets this face at another used size, keeping its manager and interned strings.</summary>
    internal FontStyle WithSize(Double size) =>
        _entry is { } entry
            ? entry.Manager.GetOrAdd(entry.Family, size, entry.Weight, entry.IsItalic, entry.Adjust, entry.Features)
            : new FontStyle(Family, size, Weight, IsItalic, Adjust, Features);

    public Boolean Equals(FontStyle other)
    {
        return ReferenceEquals(_entry, other._entry);
    }

    public override Boolean Equals(Object? obj) => obj is FontStyle other && Equals(other);

    /// <summary>
    /// Gets the entry's manager id. Equal values share the entry inside one manager, so the id is a
    /// valid and allocation-free hash for every cache keyed by this type.
    /// </summary>
    public override Int32 GetHashCode() => Id;
}

/// <summary>
/// The immutable values behind a <see cref="FontStyle"/> handle.
/// </summary>
internal sealed class FontStyleEntry
{
    internal FontStyleEntry(
        FontStyleManager manager,
        Int32 index,
        String family,
        Double size,
        Int32 weight,
        Boolean italic,
        Double? adjust,
        String? features)
    {
        Manager = manager;
        Index = index;
        Family = family;
        Size = size;
        Weight = weight;
        IsItalic = italic;
        Adjust = adjust;
        Features = features;
        AsksForVerticalWidth = Features is { } text && (Has(text, "vhal") || Has(text, "vpal"));
    }

    internal FontStyleManager Manager { get; }

    internal Int32 Index { get; }

    internal String Family { get; }

    internal Double Size { get; }

    internal Int32 Weight { get; }

    internal Boolean IsItalic { get; }

    internal Double? Adjust { get; }

    internal String? Features { get; }

    internal Boolean AsksForVerticalWidth { get; }

    private static Boolean Has(String features, String tag)
    {
        foreach (var part in features.Split(','))
        {
            var text = part.Trim();
            var equals = text.IndexOf('=');
            var name = (equals < 0 ? text : text[..equals]).Trim();
            var value = 1;

            if (equals >= 0 &&
                !Int32.TryParse(text[(equals + 1)..].Trim(), out value))
            {
                value = 0;
            }

            if (value != 0 && name.Equals(tag, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}

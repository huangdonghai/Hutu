using AngleSharp.Css;
using AngleSharp.Css.Dom;
using AngleSharp.Dom;
using System;
using System.Collections.Generic;

namespace Hutu.Rendering;

/// <summary>
/// The families a document renames with <c>@font-face</c>, and the local fonts each one stands for.
/// </summary>
/// <remarks>
/// A book names its faces once and then asks for them by name: <c>font-family: st</c> means nothing to
/// a platform, so a reader that hands the name straight to the font manager gets its generic fallback
/// instead of the typeface the book was set in. An <c>@font-face</c> whose sources are local fonts is
/// a renaming of fonts the machine already has, so the name is expanded into the list of them, in the
/// order the rule asks for them - which is the order a browser reaches for them in too, once it has
/// failed to fetch the ones before.
/// <para>
/// A source that names a file is skipped rather than fetched: this reader has no font loader, so a
/// face that ships a font of its own is read as the local ones behind it, and a face with nothing but
/// files behind it stays a name the platform does not know. The weight and slant a face declares are
/// not read either - every face of a family becomes the one list of local names, the first face
/// declared being the one that answers. The rules are read once for a build rather than per element.
/// </para>
/// </remarks>
internal sealed class FontFaceTable
{
    private static readonly String[] NoFamilies = Array.Empty<String>();

    private readonly Dictionary<String, String> _families;

    private FontFaceTable(Dictionary<String, String> families) => _families = families;

    /// <summary>Gets the faces a document declares, or an empty table when it declares none.</summary>
    public static FontFaceTable From(IDocument? document)
    {
        var families = new Dictionary<String, String>(StringComparer.OrdinalIgnoreCase);

        foreach (var sheet in document?.StyleSheets ?? Enumerable.Empty<IStyleSheet>())
        {
            if (sheet is not ICssStyleSheet css)
            {
                continue;
            }

            foreach (var rule in css.Rules.OfType<ICssFontFaceRule>())
            {
                var name = Layout.FontStyle.Unquote(rule.Family ?? String.Empty);
                var locals = Locals(rule.Source);

                if (name.Length > 0 && locals.Count > 0 && !families.ContainsKey(name))
                {
                    families.Add(name, String.Join(", ", locals));
                }
            }
        }

        return new FontFaceTable(families);
    }

    /// <summary>
    /// Gets the list a family list stands for, with every name an <c>@font-face</c> defines replaced
    /// by the local families behind it and every other name left where it was.
    /// </summary>
    public String Expand(String familyList)
    {
        if (_families.Count == 0)
        {
            return familyList;
        }

        var parts = new List<String>();
        var expanded = false;

        foreach (var name in Layout.FontStyle.Split(familyList))
        {
            if (_families.TryGetValue(name, out var locals))
            {
                parts.Add(locals);
                expanded = true;
            }
            else
            {
                parts.Add(name);
            }
        }

        return expanded ? String.Join(", ", parts) : familyList;
    }

    /// <summary>
    /// Gets the families a <c>src</c> list asks for by name, in the order it asks for them.
    /// </summary>
    /// <remarks>
    /// The list is read as text rather than as a value because AngleSharp keeps the descriptor as the
    /// text the page wrote. A source is recognised by the function it is written with, which is all
    /// the reading there is to do: everything between a <c>local(</c> and its closing bracket is one
    /// name, quotes and all, and a file source before or between them is passed over.
    /// </remarks>
    private static List<String> Locals(String? source)
    {
        var locals = new List<String>();

        if (String.IsNullOrWhiteSpace(source))
        {
            return locals;
        }

        var index = 0;

        while (index < source.Length)
        {
            var open = source.IndexOf("local(", index, StringComparison.OrdinalIgnoreCase);

            if (open < 0)
            {
                break;
            }

            var close = source.IndexOf(')', open);

            if (close < 0)
            {
                break;
            }

            var name = Layout.FontStyle.Unquote(source[(open + "local(".Length)..close]);

            if (name.Length > 0)
            {
                locals.Add(name);
            }

            index = close + 1;
        }

        return locals;
    }
}

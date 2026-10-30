using AngleSharp.Css;
using AngleSharp.Css.Dom;
using AngleSharp.Dom;
using Hutu.Rendering.Media;
using System;
using System.Collections.Generic;

namespace Hutu.Rendering;

/// <summary>
/// The families a document renames with <c>@font-face</c>, and what each one stands for: the local
/// fonts it names, and the files it asks to be fetched.
/// </summary>
/// <remarks>
/// A book names its faces once and then asks for them by name: <c>font-family: st</c> means nothing to
/// a platform, so a reader that hands the name straight to the font manager gets its generic fallback
/// instead of the typeface the book was set in.
/// <para>
/// A <c>src</c> is a prioritized list of references, and the rule it obeys is the one CSS Fonts 3
/// §4.3 gives it: "the user agent iterates over the set of references listed, using the first one it
/// can successfully activate. Fonts containing invalid data or local font faces that are not found are
/// ignored and the user agent loads the next font in the list." A <c>local()</c> is therefore a
/// reference like any other and is read in the place it stands, which is what lets a sheet say "use
/// the copy the machine already has, and only fetch one if it has none".
/// </para>
/// <para>
/// What makes a file worth fetching is its <c>format()</c> hint: §4.3 has a conformant agent "skip
/// downloading a font resource if the format hints indicate only unsupported or unknown font formats",
/// so a format this reader cannot decode is dropped here rather than fetched and thrown away. WOFF 2
/// is the one that costs a page its first choice today - it needs a decoder this reader does not have -
/// and a sheet that lists a TrueType behind it, as most do, still comes out with a real typeface.
/// </para>
/// <para>
/// The weight and slant a face declares are not read: every face of a family becomes the one list
/// behind the name, the first face declared being the one that answers. The rules are read once for a
/// build rather than per element.
/// </para>
/// </remarks>
internal sealed class FontFaceTable
{
    /// <summary>
    /// The font formats this reader can decode, named the way <c>format()</c> names them.
    /// </summary>
    /// <remarks>
    /// <c>truetype</c> and <c>opentype</c> are one entry because §4.3 makes them synonymous: "a format
    /// hint of opentype does not imply that the font contains Postscript CFF style glyph data". Left
    /// out are the formats nobody ships any more - <c>embedded-opentype</c> was never anything but a
    /// concession to one browser, and <c>svg</c> fonts were dropped from the platform altogether.
    /// <para>
    /// A hint naming a format that is not here is what §4.3 means by "unsupported": the file is not
    /// fetched at all, and the next source is the one that gets used. That is why the list has to be
    /// exactly what the reader can really unpack - a name on it that cannot be decoded would mean
    /// fetching a file for nothing and then having no font.
    /// </para>
    /// </remarks>
    private static readonly HashSet<String> Decodable = new(StringComparer.OrdinalIgnoreCase)
    {
        "truetype", "opentype", "woff", "woff2",
    };

    private readonly Dictionary<String, String> _families;
    private readonly Dictionary<String, List<Source>> _sources;

    private FontFaceTable(Dictionary<String, String> families, Dictionary<String, List<Source>> sources)
    {
        _families = families;
        _sources = sources;
    }

    /// <summary>Gets the faces a document declares, or an empty table when it declares none.</summary>
    public static FontFaceTable From(IDocument? document)
    {
        var families = new Dictionary<String, String>(StringComparer.OrdinalIgnoreCase);
        var sources = new Dictionary<String, List<Source>>(StringComparer.OrdinalIgnoreCase);

        foreach (var sheet in document?.StyleSheets ?? Enumerable.Empty<IStyleSheet>())
        {
            if (sheet is not ICssStyleSheet css)
            {
                continue;
            }

            // A face's url() is resolved against the stylesheet it was written in, not against the
            // document: a sheet reached by a relative address of its own names its files from where
            // the sheet stands (CSS Fonts 4 §4.1, CSS Values 4 §4.1).
            var baseUri = css.Href ?? document?.BaseUri;

            foreach (var rule in css.Rules.OfType<ICssFontFaceRule>())
            {
                var name = Layout.FontStyle.Unquote(rule.Family ?? String.Empty);

                if (name.Length == 0)
                {
                    // §4.1: a rule that names no family is not considered at all.
                    continue;
                }

                var locals = new List<String>();
                var files = new List<Source>();

                Read(rule.Source, baseUri, locals, files);

                if (locals.Count > 0 && !families.ContainsKey(name))
                {
                    families.Add(name, String.Join(", ", locals));
                }

                if (files.Count > 0)
                {
                    if (sources.TryGetValue(name, out var known))
                    {
                        known.AddRange(files);
                    }
                    else
                    {
                        sources[name] = files;
                    }
                }
            }
        }

        return new FontFaceTable(families, sources);
    }

    /// <summary>
    /// Gets the family names and the files behind them, in the order a face asks for them - which is
    /// the order a fetch has to try them in.
    /// </summary>
    internal IReadOnlyDictionary<String, List<Source>> Files => _sources;

    /// <summary>
    /// Gets what a fetch has to ask for: every family that has a file behind it, and that family's
    /// files in the order the rule wrote them.
    /// </summary>
    /// <remarks>
    /// Only the first file of a family that can be activated is used (§4.3), but which one that is
    /// cannot be known until one has been fetched - so the whole list is handed over and the loader
    /// stops where it succeeds.
    /// </remarks>
    internal List<FontRequest> Requests()
    {
        var requests = new List<FontRequest>();

        foreach (var family in _sources)
        {
            foreach (var source in family.Value)
            {
                requests.Add(new FontRequest(family.Key, source.Url));
            }
        }

        return requests;
    }

    /// <summary>
    /// Gets the list a family list stands for, with every name an <c>@font-face</c> defines replaced
    /// by the local families behind it and every other name left where it was.
    /// </summary>
    /// <remarks>
    /// A family that has a file behind it is deliberately left alone: the name is the one the sheet
    /// asked for, and it is the name the host registered the fetched font under, so rewriting it into
    /// local names - or into nothing - would hide the very face that came down the wire.
    /// </remarks>
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
    /// Reads a <c>src</c> list into the local names and the decodable files it names, in the order it
    /// names them.
    /// </summary>
    /// <remarks>
    /// The list is read as text rather than as a value because AngleSharp keeps the descriptor as the
    /// text the page wrote. A reference is recognised by the function it is written with, which is all
    /// the reading there is to do: everything between a <c>local(</c> or <c>url(</c> and its closing
    /// bracket is the name or the address, quotes and all, and a <c>format()</c> that follows is the
    /// hint for the reference before it.
    /// </remarks>
    private static void Read(String? source, String? baseUri, List<String> locals, List<Source> files)
    {
        if (String.IsNullOrWhiteSpace(source))
        {
            return;
        }

        var text = source!;
        var i = 0;

        while (i < text.Length)
        {
            var open = Next(text, i, out var local);

            if (open < 0)
            {
                break;
            }

            var close = text.IndexOf(')', open);

            if (close < 0)
            {
                break;
            }

            var body = text[(open + 1)..close].Trim();
            var after = close + 1;
            var hint = Format(text, ref after);

            if (local)
            {
                var name = Layout.FontStyle.Unquote(body);

                if (name.Length > 0)
                {
                    locals.Add(name);
                }
            }
            else
            {
                var url = body.Trim('"', '\'');

                // §4.3: no hint means the agent should fetch it; a hint this reader cannot decode
                // means it must not.
                if (url.Length > 0 && (hint is null || Decodable.Contains(hint)))
                {
                    files.Add(new Source(Absolute(baseUri, url), hint ?? String.Empty));
                }
            }

            i = after;
        }
    }

    /// <summary>
    /// Gets the offset of the bracket that opens the next <c>local(</c> or <c>url(</c> at or after
    /// <paramref name="from"/>, or -1 when the list has no more references.
    /// </summary>
    private static Int32 Next(String text, Int32 from, out Boolean local)
    {
        var named = text.IndexOf("local(", from, StringComparison.OrdinalIgnoreCase);
        var file = text.IndexOf("url(", from, StringComparison.OrdinalIgnoreCase);

        local = named >= 0 && (file < 0 || named < file);

        if (local)
        {
            return named + 5;
        }

        return file < 0 ? -1 : file + 3;
    }

    /// <summary>
    /// Reads the <c>format(...)</c> that follows a reference, if one does, leaving
    /// <paramref name="at"/> past it. A hint may name several formats, in which case any one of them
    /// being decodable is enough to fetch the file.
    /// </summary>
    private static String? Format(String text, ref Int32 at)
    {
        var i = at;

        while (i < text.Length && (text[i] == ' ' || text[i] == ','))
        {
            i++;
        }

        if (i + 7 > text.Length || !text.AsSpan(i, 7).Equals("format(", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var open = text.IndexOf('(', i);
        var close = text.IndexOf(')', open);

        if (close < 0)
        {
            return null;
        }

        at = close + 1;

        foreach (var name in text[(open + 1)..close].Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var format = name.Trim().Trim('"', '\'');

            if (Decodable.Contains(format))
            {
                return format;
            }
        }

        return text[(open + 1)..close].Trim().Trim('"', '\'');
    }

    /// <summary>
    /// Makes a font URL absolute against the document, which is what a fetch needs. A URL baked into
    /// the page is left as it stands: there is nothing to resolve it against.
    /// </summary>
    private static String Absolute(String? baseUri, String url)
    {
        if (url.StartsWith("data:", StringComparison.OrdinalIgnoreCase) || String.IsNullOrEmpty(baseUri))
        {
            return url;
        }

        try
        {
            return new Uri(new Uri(baseUri), url).AbsoluteUri;
        }
        catch (Exception)
        {
            return url;
        }
    }

    /// <summary>One file a <c>src</c> list asks to be fetched, and the format it says it is.</summary>
    internal readonly record struct Source(String Url, String Format);
}

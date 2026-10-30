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
internal sealed class SkiaFontResolver : IDisposable
{
    /// <summary>How many files a name is opened against before the search is given up.</summary>
    /// <remarks>
    /// A name that resembles a great many files is a name that names none of them, so the search is
    /// bounded rather than exhaustive: the closest names are tried and the rest are left alone.
    /// </remarks>
    private const Int32 Candidates = 12;

    private readonly ConcurrentDictionary<FaceKey, SKTypeface> _typefaces = new();

    /// <summary>
    /// The faces that came down the wire, by the family name the sheet asked for them under.
    /// </summary>
    /// <remarks>
    /// A name an <c>@font-face</c> rule defines is not a name the platform knows - it is a name the
    /// page made up, and the font that answers to it only exists because it was fetched. CSS Fonts 3
    /// §5.2 has the agent look for the family "among fonts defined via @font-face rules and then among
    /// available system fonts", so these are asked before the font manager is.
    /// </remarks>
    private readonly ConcurrentDictionary<String, SKTypeface> _fetched = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<FallbackKey, Fallback> _fallbacks = new();
    private readonly ConcurrentDictionary<FaceKey, IReadOnlyList<SKTypeface>> _listedFamilies = new();
    private readonly List<(String Name, String Path)> _files = new(FontFiles.All());

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
    /// Gets the typeface another family in the list carries the character with. CSS Fonts 3 §5.2 has
    /// the agent look through the families the page named before it looks for one of its own, so a
    /// list such as "Arial, Noto Sans CJK" is a promise about where the CJK text comes from - and the
    /// features the style asks for travel with that face.
    /// </summary>
    public SKTypeface? ResolveListed(FontStyle style, Int32 codepoint)
    {
        // A fetched family is asked before the cache: it may have landed after an earlier answer was
        // worked out, and the fetched faces are owned by the dictionary that holds them.
        foreach (var family in FontStyle.Split(style.Family))
        {
            if (_fetched.TryGetValue(FontStyle.Unquote(family), out var fetched) &&
                fetched.GetGlyph(codepoint) != 0)
            {
                return fetched;
            }
        }

        // Resolving the list is the expensive half: matching family names and searching files. It does
        // not depend on the character, so it is done once per family list and style. A character then
        // only asks the resolved faces whether they carry its glyph.
        var families = _listedFamilies.GetOrAdd(
            new FaceKey(style.Family, style.Weight, style.IsItalic),
            _ => ResolveListedFamilies(style));

        foreach (var face in families)
        {
            if (face.GetGlyph(codepoint) != 0)
            {
                return face;
            }
        }

        return null;
    }

    private IReadOnlyList<SKTypeface> ResolveListedFamilies(FontStyle style)
    {
        var slant = CreateStyle(style.Weight, style.IsItalic);
        var faces = new List<SKTypeface>();
        var seen = new HashSet<SKTypeface>();

        void Keep(SKTypeface? face)
        {
            if (face is null)
            {
                return;
            }

            // A candidate can be named twice by the same list - a family and its generic, say. The
            // same wrapper is kept once; separate wrappers of one native face are each owned here.
            if (!seen.Add(face))
            {
                return;
            }

            faces.Add(face);
        }

        foreach (var family in FontStyle.Split(style.Family))
        {
            var name = FontStyle.Unquote(family);

            if (_fetched.ContainsKey(name))
            {
                continue;
            }

            foreach (var stand in StandsFor(name))
            {
                Keep(SKFontManager.Default.MatchFamily(stand, slant));
            }

            Keep(Named(name));
        }

        return faces;
    }

    /// <summary>
    /// Raised when a fetched family lands, so the answers that were computed while it was still on the
    /// wire - a resolved typeface, a font, a measured width, a drawn blob - can be let go and asked for
    /// again against the face that finally answered.
    /// </summary>
    internal event Action? Registered;

    /// <summary>
    /// Hands the resolver a font that was fetched, under the family name the page gave it. The
    /// resolver owns it from here, and disposes it when it is disposed.
    /// </summary>
    /// <remarks>
    /// The family is very often asked for before its font arrives - the first layout is built the
    /// moment the document is parsed, and the font is still on the wire - and that earlier question was
    /// answered with a system font, or with the default one. That answer is cached, so it has to be
    /// thrown away here: the relayout this registration is for would otherwise be handed the same
    /// wrong face it was given before, and the font would have been fetched for nothing.
    /// </remarks>
    public void Register(String family, SKTypeface typeface)
    {
        var name = FontStyle.Unquote(family.Trim());
        _fetched[name] = typeface;

        // A key is built from the whole family list, so a face it resolved to goes stale when any one
        // of the names in that list changes answer - not only when the list is that single name.
        foreach (var key in _typefaces.Keys)
        {
            if (Names(key.Family, name))
            {
                _typefaces.TryRemove(key, out _);
            }
        }

        // The last layout measured, and the last paint drew, with the face this family was standing in
        // for, and those answers are cached higher up than the resolver can reach. Whoever holds them
        // listens here, so the relayout that follows reads the face that actually arrived.
        Registered?.Invoke();
    }

    /// <summary>Gets whether a family has already been fetched, so a relayout never asks twice.</summary>
    public Boolean Holds(String family) => _fetched.ContainsKey(FontStyle.Unquote(family.Trim()));

    /// <summary>
    /// Gets whether a family list asks for a name, which is what makes a key built from that list go
    /// stale when the name's answer changes.
    /// </summary>
    private static Boolean Names(String familyList, String name)
    {
        foreach (var family in FontStyle.Split(familyList))
        {
            if (String.Equals(family, name, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private SKTypeface CreateTypeface(FaceKey key)
    {
        var style = CreateStyle(key.Weight, key.Italic);

        foreach (var family in FontStyle.Split(key.Family))
        {
            if (_fetched.TryGetValue(FontStyle.Unquote(family), out var fetched))
            {
                return fetched;
            }

            // The generic names ask for a class of face rather than one the platform keeps under that
            // name, so the class is answered with the families a reader keeps for it, in order: asking
            // the font manager for "sans-serif" outright is asking it for a family it has never heard
            // of, and it answers with nothing - which leaves the default face standing in for every
            // weight and slant the page asked for.
            foreach (var name in StandsFor(family))
            {
                var match = SKFontManager.Default.MatchFamily(name, style);

                if (match is not null)
                {
                    return match;
                }
            }

            if (Named(family) is { } named)
            {
                return named;
            }
        }

        // A page that names no family at all is set in the user agent's standard font, and Chromium's
        // standard font is a serif one: an unset `font-family` computes to "Times New Roman" there, and
        // `First item` at 16px measures 61.78 - the same as `font-family: serif`, and three pixels four
        // narrower than the Arial that answers `sans-serif`. Falling through to the process default
        // rather than to that serif set left every page without a stated family on the wrong face.
        foreach (var name in StandsFor("serif"))
        {
            var serif = SKFontManager.Default.MatchFamily(name, style);

            if (serif is not null)
            {
                return serif;
            }
        }

        return SKFontManager.Default.MatchFamily("sans-serif", style) ?? SKTypeface.Default;
    }

    /// <summary>
    /// Gets the families a written name stands for: the name itself, and for one of the CSS generics the
    /// families a reader keeps behind that class, in the order they are tried.
    /// </summary>
    /// <remarks>
    /// Which family a generic means is the user agent's to decide (CSS Fonts 3 §6), and this follows what
    /// Chrome keeps on the platform it runs on: Arial for its sans-serif, Times New Roman for its serif,
    /// Courier New for its monospace. The rest of the list is the names the same class goes by on the
    /// other platforms, so one answer covers them without asking the machine which it is.
    /// </remarks>
    private static IEnumerable<String> StandsFor(String family)
    {
        var written = family.Trim();

        return Generics.TryGetValue(written, out var behind) ? behind : new[] { written };
    }

    private static readonly Dictionary<String, String[]> Generics = new(StringComparer.OrdinalIgnoreCase)
    {
        ["sans-serif"] = new[] { "Arial", "Helvetica", "Liberation Sans", "DejaVu Sans", "Segoe UI" },
        ["serif"] = new[] { "Times New Roman", "Times", "Liberation Serif", "DejaVu Serif", "Georgia" },
        ["monospace"] = new[] { "Courier New", "Courier", "Liberation Mono", "DejaVu Sans Mono", "Consolas" },
        ["cursive"] = new[] { "Comic Sans MS", "Brush Script MT", "Segoe Script", "Apple Chancery" },
        ["fantasy"] = new[] { "Impact", "Trebuchet MS", "Copperplate" },
        ["system-ui"] = new[] { "Segoe UI", "system-ui", "-apple-system", "Roboto" },
        ["ui-sans-serif"] = new[] { "Arial", "Helvetica Neue", "sans-serif" },
        ["ui-serif"] = new[] { "Times New Roman", "Times New Roman PS", "serif" },
        ["ui-monospace"] = new[] { "Courier New", "Menlo", "monospace" },
        ["ui-rounded"] = new[] { "Arial Rounded MT Bold", "Nunito", "sans-serif" },
        ["math"] = new[] { "Cambria Math", "STIX Two Math", "DejaVu Math TeX Gyre" },
        ["emoji"] = new[] { "Segoe UI Emoji", "Apple Color Emoji", "Noto Color Emoji" },
    };

    /// <summary>
    /// Gets the font a name stands for when the name is the one the font carries rather than the one
    /// its family is called by.
    /// </summary>
    /// <remarks>
    /// <c>local()</c> in an <c>@font-face</c> source names a font by its full or PostScript name -
    /// <c>TsukuMinPr6-M</c>, say, where the family is called <c>FOT-TsukuMin Pr6</c> - and the font
    /// manager matches families, so the name has to be looked for among the files. A name that is also
    /// a family never gets here, and a name that resembles no file name is not found this way at all,
    /// which is the same answer the platform gives: the next source is tried instead.
    /// </remarks>
    private SKTypeface? Named(String name)
    {
        var wanted = FontFiles.Normalize(name);

        if (wanted.Length < 3)
        {
            return null;
        }

        foreach (var (file, path) in Nearest(wanted))
        {
            // A collection holds several faces under one name, so a file that carries the name is
            // asked for each of them until one answers to it.
            for (var index = 0; index < 8; index++)
            {
                var face = SKTypeface.FromFile(path, index);

                if (face is null)
                {
                    break;
                }

                if (Holds(face, wanted))
                {
                    return face;
                }

                face.Dispose();
            }
        }

        return null;
    }

    /// <summary>
    /// Gets the files whose names are most like the name asked for, beginning with the likeliest.
    /// </summary>
    private IEnumerable<(String Name, String Path)> Nearest(String wanted)
    {
        var ranked = new List<(Int32 Alike, String Name, String Path)>();

        foreach (var file in _files)
        {
            var alike = Alike(file.Name, wanted);

            if (alike > 0)
            {
                ranked.Add((alike, file.Name, file.Path));
            }
        }

        ranked.Sort((a, b) => b.Alike - a.Alike);

        var taken = 0;

        foreach (var (_, name, path) in ranked)
        {
            if (taken++ == Candidates)
            {
                yield break;
            }

            yield return (name, path);
        }
    }

    /// <summary>
    /// Gets how much of a name a file name is made of, as the share of the pairs of letters next to
    /// each other in it that appear in the file name as well.
    /// </summary>
    /// <remarks>
    /// Adjacent pairs rather than letters, because a name written in a different order - a style put
    /// before a family, a decoration wrapped around it - keeps its pairs and loses its spelling. The
    /// count is a share of the name asked for rather than of both, so a short name matching a long
    /// file name does not score as well as the file name it is really written in.
    /// </remarks>
    private static Int32 Alike(String file, String wanted)
    {
        var alike = 0;

        for (var index = 0; index + 1 < wanted.Length; index++)
        {
            if (file.AsSpan().IndexOf(wanted.AsSpan(index, 2), StringComparison.Ordinal) >= 0)
            {
                alike++;
            }
        }

        return alike;
    }

    /// <summary>
    /// Gets whether a font answers to a name - by the name its family is called, or by the name it
    /// carries itself, which is what CSS asks a font by. The full name is not read: Skia offers the
    /// family and the PostScript name, and a full name that is neither is one this reader cannot see.
    /// </summary>
    private static Boolean Holds(SKTypeface face, String wanted) =>
        FontFiles.Normalize(face.FamilyName) == wanted ||
        (face.PostScriptName is { Length: > 0 } postscript && FontFiles.Normalize(postscript) == wanted);

    private static SKFontStyle CreateStyle(Int32 weight, Boolean italic) =>
        new(weight, (Int32)SKFontStyleWidth.Normal, italic ? SKFontStyleSlant.Italic : SKFontStyleSlant.Upright);

    /// <summary>
    /// Gets whether a code point sits in one of Unicode's three private use areas.
    /// </summary>
    private static Boolean IsPrivateUse(Int32 codepoint) =>
        (codepoint >= 0xE000 && codepoint <= 0xF8FF) ||
        (codepoint >= 0xF0000 && codepoint <= 0xFFFFD) ||
        (codepoint >= 0x100000 && codepoint <= 0x10FFFD);

    private static Fallback FindFallback(Int32 codepoint, FontStyle style)
    {
        // §5.4: a code point in a private use area "must only match font families named in the
        // font-family list", and where none of them has it the page "must display some form of missing
        // glyph symbol rather than attempting system font fallback". What a private use area holds is
        // decided by whoever put the font there - an icon font's pictures - so a character taken from
        // some other family's font would be a picture of the wrong thing, or a letter.
        if (IsPrivateUse(codepoint))
        {
            return default;
        }

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

        foreach (var families in _listedFamilies.Values)
        {
            foreach (var face in families)
            {
                face.Dispose();
            }
        }

        _listedFamilies.Clear();

        foreach (var fetched in _fetched.Values)
        {
            fetched.Dispose();
        }

        _fetched.Clear();
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

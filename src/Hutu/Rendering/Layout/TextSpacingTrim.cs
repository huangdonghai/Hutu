using System;
using System.Globalization;

namespace Hutu.Rendering.Layout;

/// <summary>How fullwidth punctuation is set at the start of a line (CSS Text 4 §8.5).</summary>
internal enum SpacingTrimStart
{
    /// <summary>Full-width, as <c>space-start</c> asks.</summary>
    Space,

    /// <summary>Half-width, as <c>trim-start</c> asks.</summary>
    Trim,

    /// <summary>Full-width on the first line and after a forced break, half-width on the rest.</summary>
    SpaceFirst,
}

/// <summary>How fullwidth punctuation is set at the end of a line (CSS Text 4 §8.5).</summary>
internal enum SpacingTrimEnd
{
    /// <summary>Full-width, as <c>space-end</c> asks.</summary>
    Space,

    /// <summary>Half-width, as <c>trim-end</c> asks, whether or not the line still fits.</summary>
    Trim,

    /// <summary>Half-width only where the line would not otherwise fit, as <c>allow-end</c> asks.</summary>
    Allow,
}

/// <summary>The character classes the property's collapsing rules are written in (CSS Text 4 §8.5.3).</summary>
internal enum PunctuationClass
{
    None,
    Opening,
    Closing,
    Middle,
}

/// <summary>The language conventions that decide where a fullwidth colon or dot is drawn.</summary>
internal enum TrimLanguage
{
    Other,
    SimplifiedChinese,
    TraditionalChinese,
    Japanese,
    Korean,
}

/// <summary>
/// The computed value of <c>text-spacing-trim</c>: one setting for each of the three positions the
/// property controls, with <c>trim-all</c> kept apart because it trims wherever the character stands.
/// </summary>
internal readonly record struct TextSpacingTrimValue(
    SpacingTrimStart Start,
    Boolean Adjacent,
    SpacingTrimEnd End)
{
    /// <summary>Gets whether the value is <c>trim-all</c>, which trims every punctuation regardless of context.</summary>
    internal Boolean All { get; init; }

    /// <summary>Gets the initial value, <c>normal</c>: <c>space-start trim-adjacent allow-end</c>.</summary>
    internal static TextSpacingTrimValue Initial =>
        new(SpacingTrimStart.Space, true, SpacingTrimEnd.Allow);

    /// <summary>
    /// Reads a declaration into the three positions it names, filling an omitted group from
    /// <c>trim-start trim-adjacent allow-end</c>. Returns false for a value this property does not
    /// accept, leaving <paramref name="value"/> as the inherited one the caller passed in.
    /// </summary>
    internal static Boolean TryParse(
        String? declared,
        TextSpacingTrimValue inherited,
        out TextSpacingTrimValue value)
    {
        value = inherited;

        if (String.IsNullOrWhiteSpace(declared))
        {
            return false;
        }

        var tokens = declared!.Split((Char[]?)null, StringSplitOptions.RemoveEmptyEntries);

        if (tokens.Length == 0)
        {
            return false;
        }

        if (tokens.Length == 1 && TrySingle(tokens[0], inherited, out value))
        {
            return true;
        }

        SpacingTrimStart? start = null;
        Boolean? adjacent = null;
        SpacingTrimEnd? end = null;

        foreach (var token in tokens)
        {
            if (IsStandalone(token) || IsCssWide(token))
            {
                return false;
            }

            if (TryStart(token, out var calledStart))
            {
                if (start is not null)
                {
                    return false;
                }

                start = calledStart;
            }
            else if (TryAdjacent(token, out var calledAdjacent))
            {
                if (adjacent is not null)
                {
                    return false;
                }

                adjacent = calledAdjacent;
            }
            else if (TryEnd(token, out var calledEnd))
            {
                if (end is not null)
                {
                    return false;
                }

                end = calledEnd;
            }
            else
            {
                return false;
            }
        }

        value = new(
            start ?? SpacingTrimStart.Trim,
            adjacent ?? true,
            end ?? SpacingTrimEnd.Allow);
        return true;
    }

    private static Boolean TrySingle(
        String token,
        TextSpacingTrimValue inherited,
        out TextSpacingTrimValue value)
    {
        if (Is(token, "initial"))
        {
            value = Initial;
            return true;
        }

        if (IsCssWide(token))
        {
            value = inherited;
            return true;
        }

        if (Is(token, "normal") || Is(token, "auto"))
        {
            value = Initial;
            return true;
        }

        if (Is(token, "space-all"))
        {
            value = new(SpacingTrimStart.Space, false, SpacingTrimEnd.Space);
            return true;
        }

        if (Is(token, "trim-all"))
        {
            value = new(SpacingTrimStart.Trim, true, SpacingTrimEnd.Trim) { All = true };
            return true;
        }

        if (Is(token, "trim-both"))
        {
            value = new(SpacingTrimStart.Trim, true, SpacingTrimEnd.Trim);
            return true;
        }

        if (IsStandalone(token))
        {
            value = inherited;
            return false;
        }

        // A single group keyword is parsed by the group grammar below, with the omitted groups taking
        // their defaults: trim-start is trim-start trim-adjacent allow-end, and so on.
        value = inherited;
        return false;
    }

    private static Boolean IsStandalone(String token) =>
        Is(token, "normal") ||
        Is(token, "space-all") ||
        Is(token, "trim-all") ||
        Is(token, "trim-both") ||
        Is(token, "auto");

    private static Boolean IsCssWide(String token) =>
        Is(token, "inherit") ||
        Is(token, "initial") ||
        Is(token, "unset") ||
        Is(token, "revert") ||
        Is(token, "revert-layer");

    private static Boolean Is(String token, String keyword) =>
        token.Equals(keyword, StringComparison.OrdinalIgnoreCase);

    private static Boolean TryStart(String token, out SpacingTrimStart start)
    {
        if (Is(token, "trim-start"))
        {
            start = SpacingTrimStart.Trim;
            return true;
        }

        if (Is(token, "space-start"))
        {
            start = SpacingTrimStart.Space;
            return true;
        }

        if (Is(token, "space-first"))
        {
            start = SpacingTrimStart.SpaceFirst;
            return true;
        }

        start = default;
        return false;
    }

    private static Boolean TryAdjacent(String token, out Boolean adjacent)
    {
        if (Is(token, "trim-adjacent"))
        {
            adjacent = true;
            return true;
        }

        if (Is(token, "space-adjacent"))
        {
            adjacent = false;
            return true;
        }

        adjacent = default;
        return false;
    }

    private static Boolean TryEnd(String token, out SpacingTrimEnd end)
    {
        if (Is(token, "trim-end"))
        {
            end = SpacingTrimEnd.Trim;
            return true;
        }

        if (Is(token, "space-end"))
        {
            end = SpacingTrimEnd.Space;
            return true;
        }

        if (Is(token, "allow-end"))
        {
            end = SpacingTrimEnd.Allow;
            return true;
        }

        end = default;
        return false;
    }
}

/// <summary>
/// The character standing on one side of a punctuation: what class it belongs to, whether it is the
/// ideographic space, whether its glyph is still full-width, and the size it is set at. The adjacency
/// rules read these facts and nothing else.
/// </summary>
internal readonly record struct TrimNeighbor(
    PunctuationClass Class,
    Boolean IdeographicSpace = false,
    Boolean Fullwidth = false,
    Double Size = 0);

/// <summary>
/// The rules of <c>text-spacing-trim</c>: which characters belong to which class, which neighbors make
/// a punctuation collapse, and which position on the line the computed value trims. The rules are pure;
/// the layout supplies the measured advances and glyph bounds.
/// </summary>
internal static class TextSpacingTrim
{
    private static readonly String[] ExclusiveWidthFeatures =
    {
        "halt", "hwid", "palt", "pwid", "qwid", "twid", "valt", "vhal", "vpal",
    };

    /// <summary>
    /// Gets whether a code point can be trimmed by this property. The narrow ASCII brackets are
    /// punctuation the collapse rules read as neighbors, but their advance is already below the
    /// fullwidth one, so no line can ever trim them and the layout need not ask.
    /// </summary>
    internal static Boolean IsCandidate(Int32 codePoint) =>
        codePoint is >= 0x3000 and <= 0x303F or
            >= 0xFF00 and <= 0xFFEF or
            0x2018 or 0x2019 or 0x201C or 0x201D or
            0x00B7 or 0x2027 or 0x30FB;

    /// <summary>Gets the class a code point belongs to under the given language's conventions.</summary>
    internal static PunctuationClass Classify(Int32 codePoint, TrimLanguage language)
    {
        switch (codePoint)
        {
            // The ideographic space collapses an opening punctuation beside it, but is no punctuation
            // of its own and takes no trimming.
            case 0x3000:
                return PunctuationClass.None;

            case 0x2018: // ‘
            case 0x201C: // “
                return PunctuationClass.Opening;

            case 0x2019: // ’
            case 0x201D: // ”
                return PunctuationClass.Closing;

            case 0x00B7: // ·
            case 0x2027: // ‧
            case 0x30FB: // ・
                return PunctuationClass.Middle;

            // The place of a fullwidth colon or dot is a language convention: Simplified Chinese draws
            // both to the left, Traditional Chinese centers both, and Japanese and Korean center the
            // colon while drawing the dot to the left (CSS Text 4 §8.5.3).
            case 0x3001: // 、
            case 0x3002: // 。
            case 0xFF0C: // ，
            case 0xFF0E: // ．
                return language == TrimLanguage.TraditionalChinese
                    ? PunctuationClass.Middle
                    : PunctuationClass.Closing;

            case 0xFF1A: // ：
            case 0xFF1B: // ；
                return language == TrimLanguage.SimplifiedChinese
                    ? PunctuationClass.Closing
                    : PunctuationClass.Middle;
        }

        // Narrow opening and closing punctuation - the ASCII bracket, the halfwidth forms - belong to
        // the same sides as their fullwidth neighbors. They are never trimmed themselves because their
        // advance is already below the fullwidth one; they still collapse the punctuation beside them.
        return CategoryOf(codePoint) switch
        {
            UnicodeCategory.OpenPunctuation => PunctuationClass.Opening,
            UnicodeCategory.ClosePunctuation => PunctuationClass.Closing,
            _ => PunctuationClass.None,
        };
    }

    /// <summary>Gets the language convention a BCP 47 tag names, or <see cref="TrimLanguage.Other"/>.</summary>
    internal static TrimLanguage LanguageOf(String? language)
    {
        if (String.IsNullOrWhiteSpace(language))
        {
            return TrimLanguage.Other;
        }

        var parts = language!.Trim().Replace('_', '-').ToLowerInvariant()
            .Split('-', StringSplitOptions.RemoveEmptyEntries);

        if (parts.Length == 0)
        {
            return TrimLanguage.Other;
        }

        switch (parts[0])
        {
            case "ja":
                return TrimLanguage.Japanese;

            case "ko":
                return TrimLanguage.Korean;

            case "zh":
                // The script names the convention when it is written; a region names it otherwise.
                // The script wins where both are present.
                foreach (var part in parts)
                {
                    if (part == "hant")
                    {
                        return TrimLanguage.TraditionalChinese;
                    }

                    if (part == "hans")
                    {
                        return TrimLanguage.SimplifiedChinese;
                    }
                }

                foreach (var part in parts)
                {
                    if (part is "tw" or "hk" or "mo")
                    {
                        return TrimLanguage.TraditionalChinese;
                    }
                }

                return TrimLanguage.SimplifiedChinese;

            default:
                return TrimLanguage.Other;
        }
    }

    /// <summary>
    /// Gets whether a punctuation's advance is still the fullwidth one. A glyph already set
    /// proportionally - by the font's design or by a width feature - is simultaneously full- and
    /// half-width, and the property must not take more room off it.
    /// </summary>
    internal static Boolean IsFullwidth(Double advance, Double fullwidth) =>
        fullwidth > 0 && advance >= fullwidth * 0.99;

    /// <summary>
    /// Gets whether the page's features already set the glyph's width, in which case the property
    /// leaves the glyph alone rather than trimming a second time. The list is the one Chrome's
    /// implementation treats as exclusive; `chws` is not on it, because a contextual feature still
    /// leaves the line-edge trims to the property.
    /// </summary>
    internal static Boolean HasExclusiveFeature(String? features)
    {
        if (String.IsNullOrWhiteSpace(features))
        {
            return false;
        }

        foreach (var part in features!.Split(','))
        {
            var text = part.Trim();

            if (text.Length == 0)
            {
                continue;
            }

            var equals = text.IndexOf('=');
            var tag = (equals < 0 ? text : text[..equals]).Trim();
            var value = 1;

            if (equals >= 0 &&
                !Int32.TryParse(
                    text[(equals + 1)..].Trim(),
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out value))
            {
                value = 0;
            }

            if (value != 0 && Array.Exists(
                    ExclusiveWidthFeatures,
                    exclusive => tag.Equals(exclusive, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Gets whether an opening punctuation collapses after the given previous character.</summary>
    internal static Boolean TrimsOpening(in TrimNeighbor previous, Double size) =>
        previous.IdeographicSpace ||
        previous.Class is PunctuationClass.Opening or PunctuationClass.Middle ||
        (previous.Class == PunctuationClass.Closing && previous.Fullwidth && previous.Size >= size);

    /// <summary>Gets whether a closing punctuation collapses before the given next character.</summary>
    internal static Boolean TrimsClosing(in TrimNeighbor next, Double size) =>
        next.IdeographicSpace ||
        next.Class is PunctuationClass.Closing or PunctuationClass.Middle ||
        (next.Class == PunctuationClass.Opening && next.Size > size);

    /// <summary>
    /// Gets whether the punctuation collapses because of a neighbor in the middle of a line.
    /// </summary>
    internal static Boolean TrimsByAdjacency(
        TextSpacingTrimValue value,
        PunctuationClass kind,
        in TrimNeighbor previous,
        in TrimNeighbor next,
        Double size)
    {
        if (kind == PunctuationClass.None)
        {
            return false;
        }

        if (value.All)
        {
            return true;
        }

        if (!value.Adjacent)
        {
            return false;
        }

        return kind switch
        {
            PunctuationClass.Opening => TrimsOpening(previous, size),
            PunctuationClass.Closing => TrimsClosing(next, size),
            _ => false,
        };
    }

    /// <summary>
    /// Gets whether the punctuation is trimmed because it begins a line. A <c>space-first</c> line
    /// that begins a paragraph, or follows a forced break, is not.
    /// </summary>
    internal static Boolean TrimsAtLineStart(
        TextSpacingTrimValue value,
        PunctuationClass kind,
        Boolean firstLineOrAfterForcedBreak)
    {
        if (kind == PunctuationClass.None)
        {
            return false;
        }

        if (value.All)
        {
            return true;
        }

        return kind == PunctuationClass.Opening &&
            (value.Start == SpacingTrimStart.Trim ||
             (value.Start == SpacingTrimStart.SpaceFirst && !firstLineOrAfterForcedBreak));
    }

    /// <summary>
    /// Gets whether the punctuation is trimmed because it ends a line. <c>allow-end</c> only trims
    /// where the line would not otherwise fit.
    /// </summary>
    internal static Boolean TrimsAtLineEnd(
        TextSpacingTrimValue value,
        PunctuationClass kind,
        Boolean lineOverflows)
    {
        if (kind == PunctuationClass.None)
        {
            return false;
        }

        if (value.All)
        {
            return true;
        }

        return kind == PunctuationClass.Closing &&
            (value.End == SpacingTrimEnd.Trim ||
             (value.End == SpacingTrimEnd.Allow && lineOverflows));
    }

    private static UnicodeCategory CategoryOf(Int32 codePoint) =>
        codePoint is >= 0 and <= 0xFFFF
            ? CharUnicodeInfo.GetUnicodeCategory((Char)codePoint)
            : CharUnicodeInfo.GetUnicodeCategory(Char.ConvertFromUtf32(codePoint), 0);
}

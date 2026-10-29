using System;
using System.Globalization;

namespace Hutu.Rendering.Layout;

/// <summary>The kinds of neighbour a <c>text-autospace</c> value asks to be spaced from an ideograph.</summary>
[Flags]
internal enum AutoSpaceKind
{
    None = 0,

    /// <summary>Letters of a script that is not written with ideographs, <c>ideograph-alpha</c>.</summary>
    Alpha = 1,

    /// <summary>Decimal digits, <c>ideograph-numeric</c>.</summary>
    Numeric = 2,

    /// <summary>The no-break spaces language conventions ask for, <c>punctuation</c>.</summary>
    Punctuation = 4,
}

/// <summary>The classes <c>text-autospace</c> writes its rules in (CSS Text 4 §8.4).</summary>
internal enum AutoSpaceClass
{
    /// <summary>No class the property spaces: a space, a control, a wide or fullwidth character.</summary>
    Other,

    /// <summary>A character of the ideographic scripts.</summary>
    Ideograph,

    /// <summary>A letter or any other narrow character standing beside an ideograph spaces as one.</summary>
    Letter,

    /// <summary>A decimal digit.</summary>
    Numeric,
}

/// <summary>Which side of a punctuation the language convention puts its space on.</summary>
internal enum PunctuationSpace
{
    None,
    Before,
    After,
}

/// <summary>
/// The computed value of <c>text-autospace</c>: the neighbour kinds it spaces, and whether it also
/// replaces a space already standing between them.
/// </summary>
internal readonly record struct TextAutospaceValue(AutoSpaceKind Kinds, Boolean Replace)
{
    /// <summary>Gets the initial value, <c>normal</c>: the letters and the digits, inserting only.</summary>
    internal static TextAutospaceValue Initial =>
        new(AutoSpaceKind.Alpha | AutoSpaceKind.Numeric, false);

    /// <summary>Gets whether the value spaces nothing at all, which is <c>no-autospace</c>.</summary>
    internal Boolean Off => Kinds == AutoSpaceKind.None;

    internal static TextAutospaceValue Of(AutoSpaceKind kinds, Boolean replace) => new(kinds, replace);

    /// <summary>
    /// Reads a declaration into the kinds it names and its handling of an existing space. Returns
    /// false for a value this property does not accept, leaving <paramref name="value"/> as the
    /// inherited one the caller passed in.
    /// </summary>
    /// <remarks>
    /// The grammar takes one standalone keyword, or any of the three neighbour kinds - each once, in
    /// any order - with at most one of <c>insert</c> and <c>replace</c>. A bare <c>insert</c> or
    /// <c>replace</c> implies <c>normal</c>, and <c>auto</c> is read as it is: the user agent's own
    /// choice, which this engine spends on the initial value (CSS Text 4 §8.4).
    /// </remarks>
    internal static Boolean TryParse(
        String? declared,
        TextAutospaceValue inherited,
        out TextAutospaceValue value)
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

        var kinds = AutoSpaceKind.None;
        Boolean? replace = null;

        foreach (var token in tokens)
        {
            if (IsStandalone(token) || IsCssWide(token))
            {
                return false;
            }

            if (TryKind(token, out var kind))
            {
                if ((kinds & kind) != 0)
                {
                    return false;
                }

                kinds |= kind;
            }
            else if (TryMode(token, out var calledReplace))
            {
                if (replace is not null)
                {
                    return false;
                }

                replace = calledReplace;
            }
            else
            {
                return false;
            }
        }

        if (kinds == AutoSpaceKind.None)
        {
            return false;
        }

        value = new(kinds, replace ?? false);
        return true;
    }

    private static Boolean TrySingle(
        String token,
        TextAutospaceValue inherited,
        out TextAutospaceValue value)
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

        if (Is(token, "no-autospace"))
        {
            value = new(AutoSpaceKind.None, false);
            return true;
        }

        if (TryMode(token, out var replace))
        {
            value = Initial with { Replace = replace };
            return true;
        }

        value = inherited;
        return false;
    }

    private static Boolean TryKind(String token, out AutoSpaceKind kind)
    {
        switch (token.ToLowerInvariant())
        {
            case "ideograph-alpha":
                kind = AutoSpaceKind.Alpha;
                return true;
            case "ideograph-numeric":
                kind = AutoSpaceKind.Numeric;
                return true;
            case "punctuation":
                kind = AutoSpaceKind.Punctuation;
                return true;
            default:
                kind = AutoSpaceKind.None;
                return false;
        }
    }

    private static Boolean TryMode(String token, out Boolean replace)
    {
        replace = false;

        if (Is(token, "insert"))
        {
            return true;
        }

        if (Is(token, "replace"))
        {
            replace = true;
            return true;
        }

        return false;
    }

    private static Boolean IsStandalone(String token) =>
        Is(token, "normal") || Is(token, "auto") || Is(token, "no-autospace");

    private static Boolean IsCssWide(String token) =>
        Is(token, "inherit") ||
        Is(token, "unset") ||
        Is(token, "revert") ||
        Is(token, "revert-layer");

    private static Boolean Is(String token, String keyword) =>
        token.Equals(keyword, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// The rules of <c>text-autospace</c> apart from its value: where a character stands among the classes
/// the spacing is written in, and which neighbouring pairs a value separates (CSS Text 4 §8.4).
/// </summary>
internal static class TextAutospace
{
    /// <summary>Gets the share of the CJK advance a boundary is widened by, which is <c>0.125ic</c>.</summary>
    internal const Double SpaceFraction = 0.125;

    /// <summary>
    /// Gets where a character stands for this property. A character set upright by the column - by
    /// <c>text-orientation: upright</c> or by <c>text-combine-upright</c> - is no non-ideographic
    /// neighbour at all, which is why a combined run spaces nothing around it.
    /// </summary>
    /// <remarks>
    /// The spec's non-ideographic classes are the letters and the digits; the corpus asks for the
    /// narrow punctuation too, and the classes are under review (CSS Text 4 Issue 9503). What the rule
    /// keeps from the spec is the wide side: a fullwidth or wide character is never a neighbour, which
    /// is why <c>国。X</c> gains nothing beside the full stop and <c>国!国</c> does beside the bang.
    /// </remarks>
    internal static AutoSpaceClass ClassOf(Int32 codePoint, Boolean upright)
    {
        if (IsIdeograph(codePoint))
        {
            return AutoSpaceClass.Ideograph;
        }

        if (upright || IsSpaceOrControl(codePoint) || IsWideOrFullwidth(codePoint))
        {
            return AutoSpaceClass.Other;
        }

        return CharUnicodeInfo.GetUnicodeCategory(codePoint) == UnicodeCategory.DecimalDigitNumber
            ? AutoSpaceClass.Numeric
            : AutoSpaceClass.Letter;
    }

    /// <summary>Gets whether a value separates this pair of neighbouring characters.</summary>
    internal static Boolean ShouldSpace(TextAutospaceValue value, AutoSpaceClass left, AutoSpaceClass right)
    {
        if (left == AutoSpaceClass.Ideograph)
        {
            return right switch
            {
                AutoSpaceClass.Letter => Has(value, AutoSpaceKind.Alpha),
                AutoSpaceClass.Numeric => Has(value, AutoSpaceKind.Numeric),
                _ => false,
            };
        }

        if (right == AutoSpaceClass.Ideograph)
        {
            return left switch
            {
                AutoSpaceClass.Letter => Has(value, AutoSpaceKind.Alpha),
                AutoSpaceClass.Numeric => Has(value, AutoSpaceKind.Numeric),
                _ => false,
            };
        }

        return false;
    }

    /// <summary>
    /// Gets which side of a punctuation the language convention puts its no-break space on, or
    /// <see cref="PunctuationSpace.None"/> where the value or the language asks for none.
    /// </summary>
    /// <remarks>Only French has conventions this level names (CSS Text 4 §8.4).</remarks>
    internal static PunctuationSpace Punctuation(
        TextAutospaceValue value, String? language, Int32 codePoint)
    {
        if (!Has(value, AutoSpaceKind.Punctuation) || !IsFrench(language))
        {
            return PunctuationSpace.None;
        }

        return codePoint switch
        {
            0x003B or 0x003A or 0x0021 or 0x003F => PunctuationSpace.Before,
            0x00AB => PunctuationSpace.After,
            0x00BB => PunctuationSpace.Before,
            _ => PunctuationSpace.None,
        };
    }

    /// <summary>Gets whether a language tag names French.</summary>
    internal static Boolean IsFrench(String? language) =>
        language is { Length: >= 2 } tag &&
        tag.StartsWith("fr", StringComparison.OrdinalIgnoreCase) &&
        (tag.Length == 2 || tag[2] == '-' || tag[2] == '_');

    private static Boolean Has(TextAutospaceValue value, AutoSpaceKind kind) =>
        (value.Kinds & kind) != 0;

    /// <summary>Gets whether a character is one of the ideographic scripts (CSS Text 4 §8.4).</summary>
    private static Boolean IsIdeograph(Int32 codePoint)
    {
        if (codePoint is >= 0x3041 and <= 0x30FF)
        {
            return CharUnicodeInfo.GetUnicodeCategory(codePoint) != UnicodeCategory.OtherPunctuation;
        }

        if (codePoint is >= 0x31C0 and <= 0x31FF)
        {
            return true;
        }

        return IsHan(codePoint);
    }

    /// <summary>
    /// Gets whether the character's script is Han, the ideographs proper: the radicals, the iteration
    /// marks Han writes with, the blocks of the BMP, and the extensions of the planes above it.
    /// </summary>
    private static Boolean IsHan(Int32 codePoint) =>
        codePoint is >= 0x2E80 and <= 0x2EFF or
            0x3005 or
            0x3007 or
            >= 0x3021 and <= 0x3029 or
            >= 0x3038 and <= 0x303B or
            >= 0x3400 and <= 0x4DBF or
            >= 0x4E00 and <= 0x9FFF or
            >= 0xF900 and <= 0xFAFF or
            >= 0x20000 and <= 0x2FA1F or
            >= 0x30000 and <= 0x323AF;

    private static Boolean IsSpaceOrControl(Int32 codePoint) =>
        CharUnicodeInfo.GetUnicodeCategory(codePoint) is
            UnicodeCategory.SpaceSeparator or
            UnicodeCategory.LineSeparator or
            UnicodeCategory.ParagraphSeparator or
            UnicodeCategory.Control or
            UnicodeCategory.Format or
            UnicodeCategory.Surrogate;

    /// <summary>
    /// Gets whether the character is East Asian Wide or Fullwidth (UAX#11). The blocks listed are the
    /// ones a CJK line meets: the ideographic and kana blocks, the compatibility ideographs and forms,
    /// the wide symbols, and the wide planes above the BMP.
    /// </summary>
    private static Boolean IsWideOrFullwidth(Int32 codePoint) =>
        codePoint is >= 0x1100 and <= 0x115F or
            0x2329 or
            0x232A or
            >= 0x2E80 and <= 0x303E or
            >= 0x3041 and <= 0x33FF or
            >= 0x3400 and <= 0x4DBF or
            >= 0x4E00 and <= 0x9FFF or
            >= 0xA000 and <= 0xA4CF or
            >= 0xA960 and <= 0xA97F or
            >= 0xAC00 and <= 0xD7A3 or
            >= 0xF900 and <= 0xFAFF or
            >= 0xFE10 and <= 0xFE19 or
            >= 0xFE30 and <= 0xFE6B or
            >= 0xFF00 and <= 0xFF60 or
            >= 0xFFE0 and <= 0xFFE6 or
            >= 0x1F300 and <= 0x1F64F or
            >= 0x1F900 and <= 0x1F9FF or
            >= 0x20000 and <= 0x3FFFD;
}

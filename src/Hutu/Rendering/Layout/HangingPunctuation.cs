using System;
using System.Globalization;

namespace Hutu.Rendering.Layout;

/// <summary>The edges <c>hanging-punctuation</c> asks a punctuation to hang from (CSS Text 3 §8.2.1).</summary>
[Flags]
internal enum HangingPunctuationKind
{
    None = 0,

    /// <summary>An opening bracket, quote or ideographic space at the start of the first line.</summary>
    First = 1,

    /// <summary>A stop or comma at the end of a line, which is hung only where it would not otherwise fit.</summary>
    AllowEnd = 2,

    /// <summary>A stop or comma at the end of a line, hung whether it fits or not.</summary>
    ForceEnd = 4,

    /// <summary>A closing bracket or quote at the end of the last line.</summary>
    Last = 8,
}

/// <summary>The computed value of <c>hanging-punctuation</c>: the edges it lets a punctuation hang from.</summary>
internal readonly record struct HangingPunctuationValue(HangingPunctuationKind Kinds)
{
    /// <summary>Gets the initial value, <c>none</c>: nothing hangs.</summary>
    internal static HangingPunctuationValue Initial => new(HangingPunctuationKind.None);

    /// <summary>Gets whether the value lets nothing hang at all.</summary>
    internal Boolean Off => Kinds == HangingPunctuationKind.None;

    internal static HangingPunctuationValue Of(HangingPunctuationKind kinds) => new(kinds);

    /// <summary>
    /// Reads a declaration into the edges it names. Returns false for a value this property does not
    /// accept, leaving <paramref name="value"/> as the inherited one the caller passed in.
    /// </summary>
    /// <remarks>
    /// The grammar is <c>none | [ first || [ force-end | allow-end ] || last ]</c>: each of the three
    /// groups at most once, in any order, and the two end keywords are alternatives of one another.
    /// </remarks>
    internal static Boolean TryParse(
        String? declared,
        HangingPunctuationValue inherited,
        out HangingPunctuationValue value)
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

        var kinds = HangingPunctuationKind.None;

        foreach (var token in tokens)
        {
            if (Is(token, "none") || Is(token, "initial") || IsCssWide(token))
            {
                return false;
            }

            var kind = token.ToLowerInvariant() switch
            {
                "first" => HangingPunctuationKind.First,
                "last" => HangingPunctuationKind.Last,
                "allow-end" => HangingPunctuationKind.AllowEnd,
                "force-end" => HangingPunctuationKind.ForceEnd,
                _ => HangingPunctuationKind.None,
            };

            if (kind == HangingPunctuationKind.None)
            {
                return false;
            }

            // The two end keywords are alternatives, so either one repeated - or one written after the
            // other - names the same group twice.
            var group = kind is HangingPunctuationKind.AllowEnd or HangingPunctuationKind.ForceEnd
                ? HangingPunctuationKind.AllowEnd | HangingPunctuationKind.ForceEnd
                : kind;

            if ((kinds & group) != 0)
            {
                return false;
            }

            kinds |= kind;
        }

        value = new(kinds);
        return true;
    }

    private static Boolean TrySingle(
        String token,
        HangingPunctuationValue inherited,
        out HangingPunctuationValue value)
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

        if (Is(token, "none"))
        {
            value = Initial;
            return true;
        }

        if (Is(token, "first"))
        {
            value = new(HangingPunctuationKind.First);
            return true;
        }

        if (Is(token, "last"))
        {
            value = new(HangingPunctuationKind.Last);
            return true;
        }

        if (Is(token, "allow-end"))
        {
            value = new(HangingPunctuationKind.AllowEnd);
            return true;
        }

        if (Is(token, "force-end"))
        {
            value = new(HangingPunctuationKind.ForceEnd);
            return true;
        }

        value = inherited;
        return false;
    }

    private static Boolean IsCssWide(String token) =>
        Is(token, "inherit") ||
        Is(token, "unset") ||
        Is(token, "revert") ||
        Is(token, "revert-layer");

    private static Boolean Is(String token, String keyword) =>
        token.Equals(keyword, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// The characters <c>hanging-punctuation</c> hangs, apart from its value (CSS Text 3 §8.2.1).
/// </summary>
internal static class HangingPunctuation
{
    /// <summary>
    /// Gets whether the character may hang at the start of the first formatted line: an opening
    /// bracket or quote, the two ASCII quotes, or the ideographic space.
    /// </summary>
    internal static Boolean IsOpening(Int32 codePoint)
    {
        if (codePoint is 0x0027 or 0x0022 or 0x3000)
        {
            return true;
        }

        return Category(codePoint) is UnicodeCategory.OpenPunctuation
            or UnicodeCategory.InitialQuotePunctuation
            or UnicodeCategory.FinalQuotePunctuation;
    }

    /// <summary>
    /// Gets whether the character may hang at the end of the last formatted line: a closing bracket
    /// or quote, or one of the two ASCII quotes.
    /// </summary>
    internal static Boolean IsClosing(Int32 codePoint)
    {
        if (codePoint is 0x0027 or 0x0022)
        {
            return true;
        }

        return Category(codePoint) is UnicodeCategory.ClosePunctuation
            or UnicodeCategory.InitialQuotePunctuation
            or UnicodeCategory.FinalQuotePunctuation;
    }

    /// <summary>Gets whether the character is one of the stops and commas an East Asian line hangs.</summary>
    internal static Boolean IsStopOrComma(Int32 codePoint) =>
        codePoint switch
        {
            0x002C or 0x002E or 0x060C or 0x06D4 or
            0x3001 or 0x3002 or 0xFF0C or 0xFF0E or
            0xFE50 or 0xFE51 or 0xFE52 or 0xFF61 or 0xFF64 => true,
            _ => false,
        };

    /// <summary>
    /// Gets whether the character hangs from the line's start edge under the value: <c>first</c> is
    /// the only value that reaches the start edge.
    /// </summary>
    internal static Boolean HangsAtStart(HangingPunctuationValue value) =>
        (value.Kinds & HangingPunctuationKind.First) != 0;

    /// <summary>
    /// Gets whether the character hangs from the line's end edge under the value. <c>allow-end</c>
    /// hangs a stop or comma only where the line does not otherwise fit; <c>force-end</c> always
    /// does; <c>last</c> reaches closing brackets and quotes on the block's last formatted line.
    /// </summary>
    internal static Boolean HangsAtEnd(
        HangingPunctuationValue value,
        Int32 codePoint,
        Boolean overflows,
        Boolean lastFormattedLine)
    {
        if (lastFormattedLine &&
            (value.Kinds & HangingPunctuationKind.Last) != 0 &&
            IsClosing(codePoint))
        {
            return true;
        }

        if (!IsStopOrComma(codePoint))
        {
            return false;
        }

        return (value.Kinds & HangingPunctuationKind.ForceEnd) != 0 ||
            (overflows && (value.Kinds & HangingPunctuationKind.AllowEnd) != 0);
    }

    private static UnicodeCategory Category(Int32 codePoint) =>
        CharUnicodeInfo.GetUnicodeCategory(codePoint);
}

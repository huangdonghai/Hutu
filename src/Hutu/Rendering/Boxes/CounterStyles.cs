using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Hutu.Rendering.Boxes;

/// <summary>
/// The counter styles a list marker can be spelled with: what an item's number comes to as text.
/// </summary>
/// <remarks>
/// Systems are looked up by the name the page wrote them with rather than by a parsed enumeration,
/// because the parser does not know every name: <c>upper-greek</c> and <c>simplified-chinese-informal</c>
/// reach us as plain text, and a quoted marker - <c>list-style-type: "->"</c> - is not the name of a
/// system at all. One table of names answers all three, so a style spelled here later costs an entry
/// rather than a change to how the value is read.
/// <para>
/// A name that is not here spells no marker, which is the same rule the caller follows for a number a
/// system cannot hold: an Armenian item marked <c>i.</c> is a page telling its reader something its
/// author did not say, and no marker is the smaller lie. What is left out is the spoken and additive
/// family - the Chinese, Japanese and Korean counting words, Hebrew, Armenian, Georgian, Ethiopic -
/// each of which wants its own symbols and its own arithmetic, and the two kana lists, whose tables
/// belong in the spec rather than in memory.
/// </para>
/// </remarks>
internal static class CounterStyles
{
    /// <summary>Markers that say the same thing whatever the number is.</summary>
    private static readonly Dictionary<String, String> Symbols = new(StringComparer.OrdinalIgnoreCase)
    {
        ["disc"] = "•",
        ["circle"] = "◦",
        ["square"] = "▪",
        ["disclosure-open"] = "▾",
        ["disclosure-closed"] = "▸",
    };

    /// <summary>
    /// Positional systems, each holding its script's ten numerals in order from its own zero. Every one
    /// of those runs is consecutive code points in its block - which is what lets them be built from the
    /// zero rather than typed out, and what the tests read back as Unicode's own answer for a decimal
    /// digit.
    /// </summary>
    private static readonly Dictionary<String, String> Numerals = new(StringComparer.OrdinalIgnoreCase)
    {
        ["decimal"] = "0123456789",
        ["arabic-indic"] = From('٠'),
        ["bengali"] = From('০'),
        ["cambodian"] = From('០'),
        ["devanagari"] = From('०'),
        ["gujarati"] = From('૦'),
        ["gurmukhi"] = From('੦'),
        ["kannada"] = From('೦'),
        ["lao"] = From('໐'),
        ["malayalam"] = From('൦'),
        ["mongolian"] = From('༠'),
        ["myanmar"] = From('၀'),
        ["oriya"] = From('୦'),
        ["persian"] = From('۰'),
        ["tamil"] = From('௦'),
        ["telugu"] = From('౦'),
        ["thai"] = From('๐'),
        ["tibetan"] = From('༠'),
        ["cjk-decimal"] = "〇一二三四五六七八九",
    };

    /// <summary>
    /// Alphabetic systems, which run through their symbols and then turn over into two of them, the way
    /// a count of twenty-seven comes out as <c>aa</c>.
    /// </summary>
    private static readonly Dictionary<String, String> Alphabetic = new(StringComparer.OrdinalIgnoreCase)
    {
        ["lower-latin"] = "abcdefghijklmnopqrstuvwxyz",
        ["lower-alpha"] = "abcdefghijklmnopqrstuvwxyz",
        ["upper-latin"] = "ABCDEFGHIJKLMNOPQRSTUVWXYZ",
        ["upper-alpha"] = "ABCDEFGHIJKLMNOPQRSTUVWXYZ",
        ["lower-greek"] = "αβγδεζηθικλμνξοπρστυφχψω",
        ["upper-greek"] = "ΑΒΓΔΕΖΗΘΙΚΛΜΝΞΟΠΡΣΤΥΦΧΨΩ",
    };

    /// <summary>
    /// Systems that hold a fixed run of symbols and cannot count past it. The twelfth earthly branch is
    /// the last of them, and a thirteenth item falls back to plain decimal, which is what the spec asks
    /// for and what a browser shows - not to no marker, because the item still has a place in the list.
    /// </summary>
    private static readonly Dictionary<String, String> FixedLists = new(StringComparer.OrdinalIgnoreCase)
    {
        ["cjk-earthly-branch"] = "子丑寅卯辰巳午未申酉戌亥",
        ["cjk-heavenly-stem"] = "甲乙丙丁戊己庚辛壬癸",
    };

    /// <summary>Roman numerals, largest symbol first, the subtractive pairs among them.</summary>
    private static readonly (Int32 Value, String Numeral)[] RomanNumerals =
    {
        (1000, "m"), (900, "cm"), (500, "d"), (400, "cd"), (100, "c"), (90, "xc"),
        (50, "l"), (40, "xl"), (10, "x"), (9, "ix"), (5, "v"), (4, "iv"), (1, "i"),
    };

    /// <summary>Gets whether the name asks for no marker at all.</summary>
    public static Boolean IsNone(String type) => type.Equals("none", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Gets whether the marker is a count rather than a fixed symbol, which is what earns the period
    /// after it: <c>i.</c> and <c>14.</c> are counts, a bullet is not, and a string is whatever its
    /// author wrote, punctuation included.
    /// </summary>
    public static Boolean IsCounted(String type) =>
        !IsString(type) && !Symbols.ContainsKey(type) && !IsNone(type);

    /// <summary>
    /// Gets whether the marker is a string its author wrote, which is then used exactly as written -
    /// suffix, spaces and all.
    /// </summary>
    public static Boolean IsString(String type) => IsQuoted(type);

    /// <summary>
    /// Spells one item's marker: neither the period a count carries nor the space that gaps a marker
    /// from its text. Answers null for a style not spelled here.
    /// </summary>
    public static String? Spell(String type, Int32 number)
    {
        // A quoted marker is used as it was written, escapes read and spaces kept.
        if (IsQuoted(type))
        {
            return Unquote(type);
        }

        if (Symbols.TryGetValue(type, out var symbol))
        {
            return symbol;
        }

        if (type.Equals("decimal-leading-zero", StringComparison.OrdinalIgnoreCase))
        {
            // Padded to two, and no further: a hundredth item is not turned into three digits.
            return Math.Abs(number) < 10
                ? (number < 0 ? "-" : string.Empty) + Math.Abs(number).ToString("D2", CultureInfo.InvariantCulture)
                : number.ToString(CultureInfo.InvariantCulture);
        }

        if (type.Equals("lower-roman", StringComparison.OrdinalIgnoreCase))
        {
            return Roman(number, false);
        }

        if (type.Equals("upper-roman", StringComparison.OrdinalIgnoreCase))
        {
            return Roman(number, true);
        }

        if (Alphabetic.TryGetValue(type, out var alphabet))
        {
            return Spelled(number, alphabet);
        }

        if (Numerals.TryGetValue(type, out var numerals))
        {
            return Positional(number, numerals);
        }

        if (FixedLists.TryGetValue(type, out var list) && number >= 1 && number <= list.Length)
        {
            return list[number - 1].ToString();
        }

        // Past the end of a fixed run the count carries on in decimal; a name nothing answers for here
        // spells nothing.
        return FixedLists.ContainsKey(type)
            ? number.ToString(CultureInfo.InvariantCulture)
            : null;
    }

    /// <summary>Gets the ten numerals of a script, from the code point of its zero.</summary>
    private static String From(Char zero) =>
        new([.. Enumerable.Range(0, 10).Select(offset => (Char)(zero + offset))]);

    /// <summary>Gets whether the value a page wrote is a string rather than the name of a system.</summary>
    private static Boolean IsQuoted(String type) =>
        type.Length >= 2 && (type[0] == '"' || type[0] == '\'') && type[^1] == type[0];

    /// <summary>
    /// Takes the quotes off a marker written as a string and reads the escapes inside it, which is how
    /// <c>"\2192  "</c> comes out as the arrow and the two spaces its author wanted - the spaces kept,
    /// because a string marker carries whatever room it asks for and nothing is added to it.
    /// </summary>
    private static String Unquote(String type)
    {
        var text = type.Substring(1, type.Length - 2);
        var result = new StringBuilder(text.Length);

        for (var index = 0; index < text.Length; index++)
        {
            if (text[index] != '\\')
            {
                result.Append(text[index]);

                continue;
            }

            index++;

            if (index >= text.Length)
            {
                result.Append('\\');

                break;
            }

            // Up to six hex digits are the code point, and one space directly after them is the
            // escape's own terminator rather than a character of the marker.
            var start = index;

            while (index < text.Length && index - start < 6 && Char.IsAsciiHexDigit(text[index]))
            {
                index++;
            }

            if (index == start)
            {
                // Not a number at all: a backslash before an ordinary character means that character.
                result.Append(text[index]);

                continue;
            }

            result.Append(Char.ConvertFromUtf32(Convert.ToInt32(text.Substring(start, index - start), 16)));

            if (index < text.Length && text[index] == ' ')
            {
                index++;
            }

            // The loop moves on by itself, so step back over the character just consumed.
            index--;
        }

        return result.ToString();
    }

    /// <summary>Writes a number in a script's own numerals, signed, the way decimal writes it.</summary>
    private static String Positional(Int32 number, String numerals)
    {
        var text = Math.Abs(number).ToString(CultureInfo.InvariantCulture);
        var result = new StringBuilder(text.Length + 1);

        if (number < 0)
        {
            result.Append('-');
        }

        foreach (var digit in text)
        {
            result.Append(numerals[digit - '0']);
        }

        return result.ToString();
    }

    /// <summary>
    /// Writes a number in an alphabetic system. Below one there is no letter to write, and none is
    /// claimed: the alphabet has no item nil, and a borrowed decimal would not be the letter its author
    /// asked for.
    /// </summary>
    private static String? Spelled(Int32 number, String alphabet)
    {
        if (number < 1)
        {
            return null;
        }

        var result = new StringBuilder();
        var rest = number;
        var count = alphabet.Length;

        do
        {
            rest--;
            result.Insert(0, alphabet[rest % count]);
            rest /= count;
        }
        while (rest > 0);

        return result.ToString();
    }

    /// <summary>Writes a number as roman numerals, which have no zero and no symbol past a thousand.</summary>
    private static String? Roman(Int32 number, Boolean upper)
    {
        if (number < 1 || number > 3999)
        {
            return null;
        }

        var result = new StringBuilder();

        foreach (var (value, numeral) in RomanNumerals)
        {
            while (number >= value)
            {
                result.Append(numeral);
                number -= value;
            }
        }

        return upper ? result.ToString().ToUpperInvariant() : result.ToString();
    }
}

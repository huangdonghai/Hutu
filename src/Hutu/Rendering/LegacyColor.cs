using AngleSharp.Css.Values;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Hutu.Rendering;

/// <summary>
/// Reads the colour that an obsolete HTML colour attribute names.
/// </summary>
/// <remarks>
/// HTML 2.3.6 writes the rules for parsing a legacy color value, and the <c>body</c> element's
/// <c>link</c>, <c>vlink</c> and <c>alink</c> attributes are read by them. Three of its steps are things
/// a reader of CSS colours would get the wrong way round: the empty string names no colour,
/// <c>transparent</c> names no colour, and the CSS2 system colours are no names at all here - AngleSharp
/// keeps all three in the one table the keywords live in, so each is turned away before the table is
/// asked.
/// <para>
/// What is left is read as digits rather than as a word, the way these attributes were read in the 1990s:
/// every character that is no hexadecimal digit becomes a zero, the run is padded out to three equal
/// parts, and each part is one channel of the colour on its own. That is why a page written with
/// <c>link="notacolor"</c> is drawn green rather than in the blue a page with no hint is drawn in - and
/// Chrome answers every one of these values the algorithm says it should, which is why the reading is
/// kept whole rather than cut down to the two forms a modern page would use.
/// </para>
/// </remarks>
internal static class LegacyColor
{
    private static readonly char[] Whitespace = { '\t', '\n', '\f', '\r', ' ' };

    /// <summary>
    /// The system colours, which HTML says name no colour in these attributes. Every one of the names
    /// CSS Color 4 added has to stand here too: the list is what keeps a page written
    /// <c>color="canvas"</c> on the reading Chrome gives it - the digits, not the name - and a name that
    /// only became parseable when the colour itself became one is the easiest of them to forget.
    /// </summary>
    private static readonly HashSet<String> SystemColors = new(StringComparer.OrdinalIgnoreCase)
    {
        "accentcolor", "accentcolortext", "activeborder", "activecaption", "appworkspace", "background",
        "buttonborder", "buttonface", "buttonhighlight", "buttonshadow", "buttontext", "canvas",
        "canvastext", "captiontext", "field", "fieldtext", "graytext", "highlight", "highlighttext",
        "inactiveborder", "inactivecaption", "inactivecaptiontext", "infobackground", "infotext",
        "linktext", "mark", "marktext", "menu", "menutext", "scrollbar", "selecteditem", "threeddarkshadow",
        "threedface", "threedhighlight", "threedlightshadow", "threedshadow", "visitedtext", "window",
        "windowframe", "windowtext",
    };

    /// <summary>
    /// Reads the colour a value names, or null when it names none: the empty string, and the word
    /// <c>transparent</c>, are the two the specification says fail.
    /// </summary>
    public static CssColorValue? Parse(String? text)
    {
        if (String.IsNullOrEmpty(text))
        {
            return null;
        }

        var value = text.Trim(Whitespace);

        if (value.Equals("transparent", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (!SystemColors.Contains(value) && CssColorValue.FromName(value) is { } named)
        {
            return named;
        }

        if (value.Length == 4 && value[0] == '#' && Uri.IsHexDigit(value[1]) &&
            Uri.IsHexDigit(value[2]) && Uri.IsHexDigit(value[3]))
        {
            // The three-digit form stands for the colour whose every channel is that digit twice.
            return CssColorValue.FromRgb(
                (Byte)(Digit(value[1]) * 17),
                (Byte)(Digit(value[2]) * 17),
                (Byte)(Digit(value[3]) * 17));
        }

        return ParseChannels(ToDigits(value));
    }

    /// <summary>
    /// Turns a value into the run of hexadecimal digits it carries: a character outside the basic
    /// multilingual plane becomes two zeros, the run is cut short at 128 characters, a leading number sign
    /// is taken off, and every character that is no digit becomes one.
    /// </summary>
    private static StringBuilder ToDigits(String value)
    {
        var digits = new StringBuilder(value.Length + 2);

        foreach (var rune in value.EnumerateRunes())
        {
            if (rune.Value > 0xFFFF)
            {
                digits.Append("00");
            }
            else
            {
                digits.Append((Char)rune.Value);
            }
        }

        if (digits.Length > 128)
        {
            digits.Length = 128;
        }

        if (digits.Length > 0 && digits[0] == '#')
        {
            digits.Remove(0, 1);
        }

        for (var i = 0; i < digits.Length; i++)
        {
            if (!Uri.IsHexDigit(digits[i]))
            {
                digits[i] = '0';
            }
        }

        while (digits.Length == 0 || digits.Length % 3 != 0)
        {
            digits.Append('0');
        }

        return digits;
    }

    /// <summary>
    /// Reads the three channels the run of digits spells. Each is shortened to eight digits and then to
    /// two - from the front, unless the leading digit of every one of them is a zero, in which case the
    /// three lose it together - and what stands is taken as a hexadecimal number.
    /// </summary>
    private static CssColorValue ParseChannels(StringBuilder digits)
    {
        var length = digits.Length / 3;
        var parts = new[]
        {
            digits.ToString(0, length),
            digits.ToString(length, length),
            digits.ToString(length * 2, length),
        };

        if (length > 8)
        {
            var drop = length - 8;

            for (var i = 0; i < parts.Length; i++)
            {
                parts[i] = parts[i].Substring(drop);
            }

            length = 8;
        }

        while (length > 2 && parts[0][0] == '0' && parts[1][0] == '0' && parts[2][0] == '0')
        {
            for (var i = 0; i < parts.Length; i++)
            {
                parts[i] = parts[i].Substring(1);
            }

            length--;
        }

        if (length > 2)
        {
            for (var i = 0; i < parts.Length; i++)
            {
                parts[i] = parts[i].Substring(0, 2);
            }
        }

        return CssColorValue.FromRgb(Channel(parts[0]), Channel(parts[1]), Channel(parts[2]));
    }

    private static Byte Channel(String digits) =>
        Byte.Parse(digits, NumberStyles.HexNumber, CultureInfo.InvariantCulture);

    private static Int32 Digit(Char c) => Convert.ToInt32(c.ToString(), 16);
}

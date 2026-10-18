using System;

namespace Hutu.Rendering.Layout;

/// <summary>
/// The line breaking class of a character, which is what decides where a line may end beside it.
/// </summary>
/// <remarks>
/// These are the classes UAX #14 names, kept to the ones the rules in <see cref="LineBreaks"/> ask an
/// answer for. A character none of them covers is alphabetic, and the two ways that answer is reached
/// without naming every code point are told from the character itself: an ideograph by its script, a
/// combining mark by its general category.
/// </remarks>
internal enum BreakClass
{
    /// <summary>Alphabetic: the answer for anything this table does not name.</summary>
    Al,

    /// <summary>Ideographic: a line may end on either side of it.</summary>
    Id,

    /// <summary>Break after.</summary>
    Ba,

    /// <summary>Break before.</summary>
    Bb,

    /// <summary>Hyphen-minus: a break after, except in a number or at the start of a word.</summary>
    Hy,

    /// <summary>Hyphen: a break after, except at the start of a word.</summary>
    Hh,

    /// <summary>Break before and after, as an em dash does.</summary>
    B2,

    /// <summary>Close punctuation: no break before it.</summary>
    Cl,

    /// <summary>Closing parenthesis: no break before it, and none beside a number.</summary>
    Cp,

    /// <summary>Exclamation or interrogation: no break before it.</summary>
    Ex,

    /// <summary>Infix numeric separator, the period and the comma: no break before a number after it.</summary>
    Is,

    /// <summary>Solidus: no break before it, and a break after it before a number.</summary>
    Sy,

    /// <summary>Inseparable leader, the ellipses: no break before it.</summary>
    In,

    /// <summary>Non-starter: no break before it.</summary>
    Ns,

    /// <summary>Open punctuation: no break after it.</summary>
    Op,

    /// <summary>Prefix, the currency signs: no break between it and what it prices.</summary>
    Pr,

    /// <summary>Postfix, the percent sign: no break between it and what it counts.</summary>
    Po,

    /// <summary>Numeric: a digit, which forms the number a line may not come apart.</summary>
    Nu,

    /// <summary>Glue, the no-break space: no break on either side of it.</summary>
    Gl,

    /// <summary>Word joiner: no break on either side of it.</summary>
    Wj,

    /// <summary>Zero width space: a break after it, and nothing drawn for it.</summary>
    Zw,

    /// <summary>Zero width joiner: keeps the pair it joins together.</summary>
    Zwj,

    /// <summary>Combining mark: takes the class of the character it stands on.</summary>
    Cm,
}

/// <summary>
/// The classes the Unicode Character Database assigns, for the characters whose class moves a break.
/// </summary>
/// <remarks>
/// The rows are the ones of LineBreak.txt (version 18.0.0) whose class the rules ask about, merged where
/// they touch and sorted by first code point because the answer is found by binary search. What is left
/// out is left out on purpose: a class that behaves as alphabetic (AI, QU, HL, SA, SG, XX and the Brahmic
/// ones) or as ideographic (CJ, the Hangul jamo and syllable blocks, the emoji bases) already has the
/// answer a lookup by script and category gives, the space, carriage return, line feed and mandatory break
/// classes are decided by white space processing before a line ever asks, and the planes above U+FFFF are
/// omitted because the characters they carry are named by no rule this engine reaches for - a line
/// beside an astral character is a line beside an alphabetic one, which is what the default answers for
/// the rare ones and what the ideograph test already corrects for the many.
/// <para>
/// U+0085, U+2028 and U+2029 are entered here as break-after rather than as the mandatory break their
/// own class states. They were measured in Chromium, which breaks a line after each of them and does not
/// force one; the document's own segment breaks are what the white space rules make of them, and this is
/// the place that has to agree with them.
/// </para>
/// </remarks>
internal static class LineBreakTable
{
    /// <summary>Gets the class the table names for a code point, or null when it names none.</summary>
    public static BreakClass? Of(Int32 codePoint)
    {
        var low = 0;
        var high = Rows.Length - 1;

        while (low <= high)
        {
            var middle = (low + high) / 2;
            var (first, last, classification) = Rows[middle];

            if (codePoint < first)
            {
                high = middle - 1;
            }
            else if (codePoint > last)
            {
                low = middle + 1;
            }
            else
            {
                return classification;
            }
        }

        return null;
    }

    private static readonly (Int32 First, Int32 Last, BreakClass Class)[] Rows =
    {
        (0x0009, 0x0009, BreakClass.Ba),
        (0x0021, 0x0021, BreakClass.Ex),
        (0x0024, 0x0024, BreakClass.Pr),
        (0x0025, 0x0025, BreakClass.Po),
        (0x0028, 0x0028, BreakClass.Op),
        (0x0029, 0x0029, BreakClass.Cp),
        (0x002B, 0x002B, BreakClass.Pr),
        (0x002C, 0x002C, BreakClass.Is),
        (0x002D, 0x002D, BreakClass.Hy),
        (0x002E, 0x002E, BreakClass.Is),
        (0x002F, 0x002F, BreakClass.Sy),
        (0x0030, 0x0039, BreakClass.Nu),
        (0x003A, 0x003B, BreakClass.Is),
        (0x003F, 0x003F, BreakClass.Ex),
        (0x005B, 0x005B, BreakClass.Op),
        (0x005C, 0x005C, BreakClass.Pr),
        (0x005D, 0x005D, BreakClass.Cp),
        (0x007B, 0x007B, BreakClass.Op),
        (0x007C, 0x007C, BreakClass.Ba),
        (0x007D, 0x007D, BreakClass.Cl),
        (0x0085, 0x0085, BreakClass.Ba),
        (0x00A0, 0x00A0, BreakClass.Gl),
        (0x00A1, 0x00A1, BreakClass.Op),
        (0x00A2, 0x00A2, BreakClass.Po),
        (0x00A3, 0x00A5, BreakClass.Pr),
        (0x00AD, 0x00AD, BreakClass.Hh),
        (0x00B0, 0x00B0, BreakClass.Po),
        (0x00B1, 0x00B1, BreakClass.Pr),
        (0x00B4, 0x00B4, BreakClass.Bb),
        (0x00B7, 0x00B7, BreakClass.Is),
        (0x00BF, 0x00BF, BreakClass.Op),
        (0x02C8, 0x02C8, BreakClass.Bb),
        (0x02CC, 0x02CC, BreakClass.Bb),
        (0x02DF, 0x02DF, BreakClass.Bb),
        (0x035C, 0x0362, BreakClass.Gl),
        (0x037E, 0x037E, BreakClass.Is),
        (0x0387, 0x0387, BreakClass.Is),
        (0x0589, 0x0589, BreakClass.Is),
        (0x058A, 0x058A, BreakClass.Hh),
        (0x058F, 0x058F, BreakClass.Pr),
        (0x05BE, 0x05BE, BreakClass.Hh),
        (0x05C6, 0x05C6, BreakClass.Ex),
        (0x0600, 0x0605, BreakClass.Nu),
        (0x0609, 0x060B, BreakClass.Po),
        (0x060C, 0x060D, BreakClass.Is),
        (0x061B, 0x061B, BreakClass.Ex),
        (0x061D, 0x061F, BreakClass.Ex),
        (0x0660, 0x0669, BreakClass.Nu),
        (0x066A, 0x066A, BreakClass.Po),
        (0x066B, 0x066C, BreakClass.Nu),
        (0x06D4, 0x06D4, BreakClass.Ex),
        (0x06DD, 0x06DD, BreakClass.Nu),
        (0x06F0, 0x06F9, BreakClass.Nu),
        (0x07C0, 0x07C9, BreakClass.Nu),
        (0x07F8, 0x07F8, BreakClass.Is),
        (0x07F9, 0x07F9, BreakClass.Ex),
        (0x07FE, 0x07FF, BreakClass.Pr),
        (0x0890, 0x0891, BreakClass.Nu),
        (0x08E2, 0x08E2, BreakClass.Nu),
        (0x0964, 0x0965, BreakClass.Ba),
        (0x0966, 0x096F, BreakClass.Nu),
        (0x09E6, 0x09EF, BreakClass.Nu),
        (0x09F2, 0x09F3, BreakClass.Po),
        (0x09F9, 0x09F9, BreakClass.Po),
        (0x09FB, 0x09FB, BreakClass.Pr),
        (0x0A66, 0x0A6F, BreakClass.Nu),
        (0x0AE6, 0x0AEF, BreakClass.Nu),
        (0x0AF1, 0x0AF1, BreakClass.Pr),
        (0x0B66, 0x0B6F, BreakClass.Nu),
        (0x0BE6, 0x0BEF, BreakClass.Nu),
        (0x0BF9, 0x0BF9, BreakClass.Pr),
        (0x0C66, 0x0C6F, BreakClass.Nu),
        (0x0C77, 0x0C77, BreakClass.Bb),
        (0x0C84, 0x0C84, BreakClass.Bb),
        (0x0CE6, 0x0CEF, BreakClass.Nu),
        (0x0D66, 0x0D6F, BreakClass.Nu),
        (0x0D79, 0x0D79, BreakClass.Po),
        (0x0DE6, 0x0DEF, BreakClass.Nu),
        (0x0E3F, 0x0E3F, BreakClass.Pr),
        (0x0E50, 0x0E59, BreakClass.Nu),
        (0x0E5A, 0x0E5B, BreakClass.Ba),
        (0x0ED0, 0x0ED9, BreakClass.Nu),
        (0x0F01, 0x0F04, BreakClass.Bb),
        (0x0F06, 0x0F07, BreakClass.Bb),
        (0x0F08, 0x0F08, BreakClass.Gl),
        (0x0F09, 0x0F0A, BreakClass.Bb),
        (0x0F0B, 0x0F0B, BreakClass.Ba),
        (0x0F0C, 0x0F0C, BreakClass.Gl),
        (0x0F0D, 0x0F11, BreakClass.Ex),
        (0x0F12, 0x0F12, BreakClass.Gl),
        (0x0F14, 0x0F14, BreakClass.Ex),
        (0x0F20, 0x0F29, BreakClass.Nu),
        (0x0F34, 0x0F34, BreakClass.Ba),
        (0x0F3A, 0x0F3A, BreakClass.Op),
        (0x0F3B, 0x0F3B, BreakClass.Cl),
        (0x0F3C, 0x0F3C, BreakClass.Op),
        (0x0F3D, 0x0F3D, BreakClass.Cl),
        (0x0F7F, 0x0F7F, BreakClass.Ba),
        (0x0F85, 0x0F85, BreakClass.Ba),
        (0x0FBE, 0x0FBF, BreakClass.Ba),
        (0x0FD0, 0x0FD1, BreakClass.Bb),
        (0x0FD2, 0x0FD2, BreakClass.Ba),
        (0x0FD3, 0x0FD3, BreakClass.Bb),
        (0x0FD9, 0x0FDA, BreakClass.Gl),
        (0x1040, 0x1049, BreakClass.Nu),
        (0x104A, 0x104B, BreakClass.Ba),
        (0x1090, 0x1099, BreakClass.Nu),
        (0x1361, 0x1361, BreakClass.Ba),
        (0x1400, 0x1400, BreakClass.Hh),
        (0x1680, 0x1680, BreakClass.Ba),
        (0x169B, 0x169B, BreakClass.Op),
        (0x169C, 0x169C, BreakClass.Cl),
        (0x16EB, 0x16ED, BreakClass.Ba),
        (0x1735, 0x1736, BreakClass.Ba),
        (0x17D4, 0x17D5, BreakClass.Ba),
        (0x17D6, 0x17D6, BreakClass.Ns),
        (0x17D8, 0x17D8, BreakClass.Ba),
        (0x17DA, 0x17DA, BreakClass.Ba),
        (0x17DB, 0x17DB, BreakClass.Pr),
        (0x17E0, 0x17E9, BreakClass.Nu),
        (0x1802, 0x1803, BreakClass.Ex),
        (0x1804, 0x1805, BreakClass.Ba),
        (0x1806, 0x1806, BreakClass.Bb),
        (0x1808, 0x1809, BreakClass.Ex),
        (0x180E, 0x180E, BreakClass.Gl),
        (0x1810, 0x1819, BreakClass.Nu),
        (0x1944, 0x1945, BreakClass.Ex),
        (0x1946, 0x194F, BreakClass.Nu),
        (0x19D0, 0x19DA, BreakClass.Nu),
        (0x1A80, 0x1A89, BreakClass.Nu),
        (0x1A90, 0x1A99, BreakClass.Nu),
        (0x1AEB, 0x1AEB, BreakClass.Gl),
        (0x1B4E, 0x1B4F, BreakClass.Ba),
        (0x1B5A, 0x1B5B, BreakClass.Ba),
        (0x1B5D, 0x1B60, BreakClass.Ba),
        (0x1B7D, 0x1B7F, BreakClass.Ba),
        (0x1BB0, 0x1BB9, BreakClass.Nu),
        (0x1C3B, 0x1C3F, BreakClass.Ba),
        (0x1C40, 0x1C49, BreakClass.Nu),
        (0x1C50, 0x1C59, BreakClass.Nu),
        (0x1C7E, 0x1C7F, BreakClass.Ba),
        (0x1DCD, 0x1DCD, BreakClass.Gl),
        (0x1DFC, 0x1DFC, BreakClass.Gl),
        (0x1FFD, 0x1FFD, BreakClass.Bb),
        (0x2000, 0x2006, BreakClass.Ba),
        (0x2007, 0x2007, BreakClass.Gl),
        (0x2008, 0x200A, BreakClass.Ba),
        (0x200B, 0x200B, BreakClass.Zw),
        (0x200D, 0x200D, BreakClass.Zwj),
        (0x2010, 0x2010, BreakClass.Hh),
        (0x2011, 0x2011, BreakClass.Gl),
        (0x2012, 0x2013, BreakClass.Ba),
        (0x2014, 0x2014, BreakClass.B2),
        (0x201A, 0x201A, BreakClass.Op),
        (0x201E, 0x201E, BreakClass.Op),
        (0x2024, 0x2026, BreakClass.In),
        (0x2027, 0x2029, BreakClass.Ba),
        (0x202F, 0x202F, BreakClass.Gl),
        (0x2030, 0x2037, BreakClass.Po),
        (0x203C, 0x203D, BreakClass.Ns),
        (0x2044, 0x2044, BreakClass.Is),
        (0x2045, 0x2045, BreakClass.Op),
        (0x2046, 0x2046, BreakClass.Cl),
        (0x2047, 0x2049, BreakClass.Ns),
        (0x2056, 0x2056, BreakClass.Ba),
        (0x2057, 0x2057, BreakClass.Po),
        (0x2058, 0x205B, BreakClass.Ba),
        (0x205D, 0x205F, BreakClass.Ba),
        (0x2060, 0x2060, BreakClass.Wj),
        (0x207D, 0x207D, BreakClass.Op),
        (0x207E, 0x207E, BreakClass.Cl),
        (0x208D, 0x208D, BreakClass.Op),
        (0x208E, 0x208E, BreakClass.Cl),
        (0x208F, 0x208F, BreakClass.Bb),
        (0x20A0, 0x20A6, BreakClass.Pr),
        (0x20A7, 0x20A7, BreakClass.Po),
        (0x20A8, 0x20B5, BreakClass.Pr),
        (0x20B6, 0x20B6, BreakClass.Po),
        (0x20B7, 0x20BA, BreakClass.Pr),
        (0x20BB, 0x20BB, BreakClass.Po),
        (0x20BC, 0x20BD, BreakClass.Pr),
        (0x20BE, 0x20BE, BreakClass.Po),
        (0x20BF, 0x20BF, BreakClass.Pr),
        (0x20C0, 0x20C0, BreakClass.Po),
        (0x20C1, 0x20CF, BreakClass.Pr),
        (0x2103, 0x2103, BreakClass.Po),
        (0x2109, 0x2109, BreakClass.Po),
        (0x2116, 0x2116, BreakClass.Pr),
        (0x2212, 0x2213, BreakClass.Pr),
        (0x22EF, 0x22EF, BreakClass.In),
        (0x2308, 0x2308, BreakClass.Op),
        (0x2309, 0x2309, BreakClass.Cl),
        (0x230A, 0x230A, BreakClass.Op),
        (0x230B, 0x230B, BreakClass.Cl),
        (0x2329, 0x2329, BreakClass.Op),
        (0x232A, 0x232A, BreakClass.Cl),
        (0x2762, 0x2763, BreakClass.Ex),
        (0x2768, 0x2768, BreakClass.Op),
        (0x2769, 0x2769, BreakClass.Cl),
        (0x276A, 0x276A, BreakClass.Op),
        (0x276B, 0x276B, BreakClass.Cl),
        (0x276C, 0x276C, BreakClass.Op),
        (0x276D, 0x276D, BreakClass.Cl),
        (0x276E, 0x276E, BreakClass.Op),
        (0x276F, 0x276F, BreakClass.Cl),
        (0x2770, 0x2770, BreakClass.Op),
        (0x2771, 0x2771, BreakClass.Cl),
        (0x2772, 0x2772, BreakClass.Op),
        (0x2773, 0x2773, BreakClass.Cl),
        (0x2774, 0x2774, BreakClass.Op),
        (0x2775, 0x2775, BreakClass.Cl),
        (0x27C5, 0x27C5, BreakClass.Op),
        (0x27C6, 0x27C6, BreakClass.Cl),
        (0x27E6, 0x27E6, BreakClass.Op),
        (0x27E7, 0x27E7, BreakClass.Cl),
        (0x27E8, 0x27E8, BreakClass.Op),
        (0x27E9, 0x27E9, BreakClass.Cl),
        (0x27EA, 0x27EA, BreakClass.Op),
        (0x27EB, 0x27EB, BreakClass.Cl),
        (0x27EC, 0x27EC, BreakClass.Op),
        (0x27ED, 0x27ED, BreakClass.Cl),
        (0x27EE, 0x27EE, BreakClass.Op),
        (0x27EF, 0x27EF, BreakClass.Cl),
        (0x2800, 0x2800, BreakClass.Ba),
        (0x2983, 0x2983, BreakClass.Op),
        (0x2984, 0x2984, BreakClass.Cl),
        (0x2985, 0x2985, BreakClass.Op),
        (0x2986, 0x2986, BreakClass.Cl),
        (0x2987, 0x2987, BreakClass.Op),
        (0x2988, 0x2988, BreakClass.Cl),
        (0x2989, 0x2989, BreakClass.Op),
        (0x298A, 0x298A, BreakClass.Cl),
        (0x298B, 0x298B, BreakClass.Op),
        (0x298C, 0x298C, BreakClass.Cl),
        (0x298D, 0x298D, BreakClass.Op),
        (0x298E, 0x298E, BreakClass.Cl),
        (0x298F, 0x298F, BreakClass.Op),
        (0x2990, 0x2990, BreakClass.Cl),
        (0x2991, 0x2991, BreakClass.Op),
        (0x2992, 0x2992, BreakClass.Cl),
        (0x2993, 0x2993, BreakClass.Op),
        (0x2994, 0x2994, BreakClass.Cl),
        (0x2995, 0x2995, BreakClass.Op),
        (0x2996, 0x2996, BreakClass.Cl),
        (0x2997, 0x2997, BreakClass.Op),
        (0x2998, 0x2998, BreakClass.Cl),
        (0x29D8, 0x29D8, BreakClass.Op),
        (0x29D9, 0x29D9, BreakClass.Cl),
        (0x29DA, 0x29DA, BreakClass.Op),
        (0x29DB, 0x29DB, BreakClass.Cl),
        (0x29FC, 0x29FC, BreakClass.Op),
        (0x29FD, 0x29FD, BreakClass.Cl),
        (0x2CF9, 0x2CF9, BreakClass.Ex),
        (0x2CFA, 0x2CFC, BreakClass.Ba),
        (0x2CFE, 0x2CFE, BreakClass.Ex),
        (0x2CFF, 0x2CFF, BreakClass.Ba),
        (0x2D70, 0x2D70, BreakClass.Ba),
        (0x2E0E, 0x2E15, BreakClass.Ba),
        (0x2E17, 0x2E17, BreakClass.Hh),
        (0x2E18, 0x2E18, BreakClass.Op),
        (0x2E19, 0x2E19, BreakClass.Ba),
        (0x2E22, 0x2E22, BreakClass.Op),
        (0x2E23, 0x2E23, BreakClass.Cl),
        (0x2E24, 0x2E24, BreakClass.Op),
        (0x2E25, 0x2E25, BreakClass.Cl),
        (0x2E26, 0x2E26, BreakClass.Op),
        (0x2E27, 0x2E27, BreakClass.Cl),
        (0x2E28, 0x2E28, BreakClass.Op),
        (0x2E29, 0x2E29, BreakClass.Cl),
        (0x2E2A, 0x2E2D, BreakClass.Ba),
        (0x2E2E, 0x2E2E, BreakClass.Ex),
        (0x2E30, 0x2E31, BreakClass.Ba),
        (0x2E33, 0x2E34, BreakClass.Ba),
        (0x2E3A, 0x2E3B, BreakClass.B2),
        (0x2E3C, 0x2E3E, BreakClass.Ba),
        (0x2E40, 0x2E40, BreakClass.Hh),
        (0x2E41, 0x2E41, BreakClass.Ba),
        (0x2E42, 0x2E42, BreakClass.Op),
        (0x2E43, 0x2E4A, BreakClass.Ba),
        (0x2E4C, 0x2E4C, BreakClass.Ba),
        (0x2E4E, 0x2E4F, BreakClass.Ba),
        (0x2E53, 0x2E54, BreakClass.Ex),
        (0x2E55, 0x2E55, BreakClass.Op),
        (0x2E56, 0x2E56, BreakClass.Cp),
        (0x2E57, 0x2E57, BreakClass.Op),
        (0x2E58, 0x2E58, BreakClass.Cp),
        (0x2E59, 0x2E59, BreakClass.Op),
        (0x2E5A, 0x2E5A, BreakClass.Cp),
        (0x2E5B, 0x2E5B, BreakClass.Op),
        (0x2E5C, 0x2E5C, BreakClass.Cp),
        (0x2E5D, 0x2E5D, BreakClass.Hh),
        (0x2E60, 0x2E61, BreakClass.Ex),
        (0x2E62, 0x2E62, BreakClass.Op),
        (0x2E63, 0x2E63, BreakClass.Cl),
        (0x3000, 0x3000, BreakClass.Ba),
        (0x3001, 0x3002, BreakClass.Cl),
        (0x3005, 0x3005, BreakClass.Ns),
        (0x3008, 0x3008, BreakClass.Op),
        (0x3009, 0x3009, BreakClass.Cl),
        (0x300A, 0x300A, BreakClass.Op),
        (0x300B, 0x300B, BreakClass.Cl),
        (0x300C, 0x300C, BreakClass.Op),
        (0x300D, 0x300D, BreakClass.Cl),
        (0x300E, 0x300E, BreakClass.Op),
        (0x300F, 0x300F, BreakClass.Cl),
        (0x3010, 0x3010, BreakClass.Op),
        (0x3011, 0x3011, BreakClass.Cl),
        (0x3014, 0x3014, BreakClass.Op),
        (0x3015, 0x3015, BreakClass.Cl),
        (0x3016, 0x3016, BreakClass.Op),
        (0x3017, 0x3017, BreakClass.Cl),
        (0x3018, 0x3018, BreakClass.Op),
        (0x3019, 0x3019, BreakClass.Cl),
        (0x301A, 0x301A, BreakClass.Op),
        (0x301B, 0x301B, BreakClass.Cl),
        (0x301C, 0x301C, BreakClass.Ns),
        (0x301D, 0x301D, BreakClass.Op),
        (0x301E, 0x301F, BreakClass.Cl),
        (0x303B, 0x303C, BreakClass.Ns),
        (0x309B, 0x309E, BreakClass.Ns),
        (0x30A0, 0x30A0, BreakClass.Ns),
        (0x30FB, 0x30FB, BreakClass.Ns),
        (0x30FD, 0x30FE, BreakClass.Ns),
        (0xA015, 0xA015, BreakClass.Ns),
        (0xA4FE, 0xA4FF, BreakClass.Ba),
        (0xA60D, 0xA60D, BreakClass.Ba),
        (0xA60E, 0xA60E, BreakClass.Ex),
        (0xA60F, 0xA60F, BreakClass.Ba),
        (0xA620, 0xA629, BreakClass.Nu),
        (0xA6F3, 0xA6F7, BreakClass.Ba),
        (0xA838, 0xA838, BreakClass.Po),
        (0xA874, 0xA875, BreakClass.Bb),
        (0xA876, 0xA877, BreakClass.Ex),
        (0xA8CE, 0xA8CF, BreakClass.Ba),
        (0xA8D0, 0xA8D9, BreakClass.Nu),
        (0xA8FC, 0xA8FC, BreakClass.Bb),
        (0xA900, 0xA909, BreakClass.Nu),
        (0xA92E, 0xA92F, BreakClass.Ba),
        (0xA9C7, 0xA9C9, BreakClass.Ba),
        (0xA9CF, 0xA9CF, BreakClass.Ba),
        (0xA9F0, 0xA9F9, BreakClass.Nu),
        (0xAA40, 0xAA42, BreakClass.Ba),
        (0xAA44, 0xAA4B, BreakClass.Ba),
        (0xAA5D, 0xAA5F, BreakClass.Ba),
        (0xAAF0, 0xAAF1, BreakClass.Ba),
        (0xABEB, 0xABEB, BreakClass.Ba),
        (0xABF0, 0xABF9, BreakClass.Nu),
        (0xFD3E, 0xFD3E, BreakClass.Cl),
        (0xFD3F, 0xFD3F, BreakClass.Op),
        (0xFDFC, 0xFDFC, BreakClass.Po),
        (0xFE10, 0xFE12, BreakClass.Cl),
        (0xFE13, 0xFE14, BreakClass.Ns),
        (0xFE15, 0xFE16, BreakClass.Ex),
        (0xFE17, 0xFE17, BreakClass.Op),
        (0xFE18, 0xFE18, BreakClass.Cl),
        (0xFE19, 0xFE19, BreakClass.In),
        (0xFE20, 0xFE20, BreakClass.Gl),
        (0xFE22, 0xFE22, BreakClass.Gl),
        (0xFE24, 0xFE24, BreakClass.Gl),
        (0xFE26, 0xFE27, BreakClass.Gl),
        (0xFE29, 0xFE29, BreakClass.Gl),
        (0xFE2B, 0xFE2B, BreakClass.Gl),
        (0xFE2D, 0xFE2E, BreakClass.Gl),
        (0xFE35, 0xFE35, BreakClass.Op),
        (0xFE36, 0xFE36, BreakClass.Cl),
        (0xFE37, 0xFE37, BreakClass.Op),
        (0xFE38, 0xFE38, BreakClass.Cl),
        (0xFE39, 0xFE39, BreakClass.Op),
        (0xFE3A, 0xFE3A, BreakClass.Cl),
        (0xFE3B, 0xFE3B, BreakClass.Op),
        (0xFE3C, 0xFE3C, BreakClass.Cl),
        (0xFE3D, 0xFE3D, BreakClass.Op),
        (0xFE3E, 0xFE3E, BreakClass.Cl),
        (0xFE3F, 0xFE3F, BreakClass.Op),
        (0xFE40, 0xFE40, BreakClass.Cl),
        (0xFE41, 0xFE41, BreakClass.Op),
        (0xFE42, 0xFE42, BreakClass.Cl),
        (0xFE43, 0xFE43, BreakClass.Op),
        (0xFE44, 0xFE44, BreakClass.Cl),
        (0xFE47, 0xFE47, BreakClass.Op),
        (0xFE48, 0xFE48, BreakClass.Cl),
        (0xFE50, 0xFE52, BreakClass.Cl),
        (0xFE54, 0xFE55, BreakClass.Ns),
        (0xFE56, 0xFE57, BreakClass.Ex),
        (0xFE59, 0xFE59, BreakClass.Op),
        (0xFE5A, 0xFE5A, BreakClass.Cl),
        (0xFE5B, 0xFE5B, BreakClass.Op),
        (0xFE5C, 0xFE5C, BreakClass.Cl),
        (0xFE5D, 0xFE5D, BreakClass.Op),
        (0xFE5E, 0xFE5E, BreakClass.Cl),
        (0xFE69, 0xFE69, BreakClass.Pr),
        (0xFE6A, 0xFE6A, BreakClass.Po),
        (0xFEFF, 0xFEFF, BreakClass.Wj),
        (0xFF01, 0xFF01, BreakClass.Ex),
        (0xFF04, 0xFF04, BreakClass.Pr),
        (0xFF05, 0xFF05, BreakClass.Po),
        (0xFF08, 0xFF08, BreakClass.Op),
        (0xFF09, 0xFF09, BreakClass.Cl),
        (0xFF0C, 0xFF0C, BreakClass.Cl),
        (0xFF0E, 0xFF0E, BreakClass.Cl),
        (0xFF1A, 0xFF1B, BreakClass.Ns),
        (0xFF1F, 0xFF1F, BreakClass.Ex),
        (0xFF3B, 0xFF3B, BreakClass.Op),
        (0xFF3D, 0xFF3D, BreakClass.Cl),
        (0xFF5B, 0xFF5B, BreakClass.Op),
        (0xFF5D, 0xFF5D, BreakClass.Cl),
        (0xFF5F, 0xFF5F, BreakClass.Op),
        (0xFF60, 0xFF61, BreakClass.Cl),
        (0xFF62, 0xFF62, BreakClass.Op),
        (0xFF63, 0xFF64, BreakClass.Cl),
        (0xFF65, 0xFF65, BreakClass.Ns),
        (0xFF9E, 0xFF9F, BreakClass.Ns),
        (0xFFE0, 0xFFE0, BreakClass.Po),
        (0xFFE1, 0xFFE1, BreakClass.Pr),
        (0xFFE5, 0xFFE6, BreakClass.Pr)
    };
}

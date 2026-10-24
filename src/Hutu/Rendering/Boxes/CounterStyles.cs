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
        ["mongolian"] = From((Char)0x1810),
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

    /// <summary>
    /// Hebrew numerals, added up from the largest worth. The alphabet's twenty-two letters are one run
    /// of consecutive code points, and the five final forms are not used for numbers, so the tens run
    /// from <c>כ</c> to <c>צ</c> without them; past four hundred the hundreds are built up out of the
    /// same pieces. The two values that would spell out a name are written the way every Hebrew
    /// bookkeeper writes them: <c>טו</c> and <c>טז</c> rather than <c>יה</c> and <c>וה</c>.
    /// </summary>
    private static readonly (Int32 Value, String Numeral)[] HebrewNumerals =
    {
        (400, "ת"), (300, "ש"), (200, "ר"), (100, "ק"),
        (90, "צ"), (80, "פ"), (70, "ע"), (60, "ס"), (50, "נ"), (40, "מ"), (30, "ל"), (20, "כ"),
        (19, "יט"), (18, "יח"), (16, "טז"), (15, "טו"), (10, "י"),
        (9, "ט"), (8, "ח"), (7, "ז"), (6, "ו"), (5, "ה"), (4, "ד"), (3, "ג"), (2, "ב"), (1, "א"),
    };

    /// <summary>
    /// The Chinese, Japanese and Korean counting styles, which spell a number out as it is read: the
    /// digits, a unit word on every place, and <c>零</c> where a place was skipped. Nothing here needed
    /// a table copied from anywhere - the characters are the ones the language writes, and the shape of a
    /// number follows the shape of the word for it.
    /// </summary>
    /// <param name="Digits">The nine digits, one through nine, in the reading the style is written in.</param>
    /// <param name="Zero">The word for nought, which stands alone on the item numbered nil.</param>
    /// <param name="Negative">
    /// The word said before a number below nought. Korean says it with a room after it, and Chinese and
    /// Japanese run it into the number.
    /// </param>
    /// <param name="Formal">
    /// Whether the style is the anti-fraud formal reading (the <c>大写</c>), which keeps the one of
    /// <c>拾</c> - <c>一十</c> rather than the plain reading's <c>十</c> - because a bare <c>十</c> is a
    /// character away from a forged number.
    /// </param>
    /// <param name="Short">
    /// Whether the style also drops the one before its <c>百</c> and <c>千</c>: <c>百</c> is how Japanese
    /// and the Korean hanja readings say a hundred, where Chinese says <c>一百</c>.
    /// </param>
    /// <param name="WritesZero">
    /// Whether a place left empty is read out as a nought. Chinese does - <c>一千零五</c> is not the number
    /// <c>一千五</c> suggests - and Japanese and Korean leave the hole to be felt.
    /// </param>
    private sealed record Counting(
        String Digits,
        String Zero,
        String Negative,
        String Ten,
        String Hundred,
        String Thousand,
        String Myriad,
        String HundredMyriad,
        Boolean Formal,
        Boolean Short,
        Boolean WritesZero);

    private static readonly Dictionary<String, Counting> CountingStyles = new(StringComparer.OrdinalIgnoreCase)
    {
        ["simplified-chinese-informal"] = new("一二三四五六七八九", "零", "负", "十", "百", "千", "万", "亿", false, false, true),
        ["simplified-chinese-formal"] = new("壹贰叁肆伍陆柒捌玖", "零", "负", "拾", "佰", "仟", "万", "亿", true, false, true),
        ["traditional-chinese-informal"] = new("一二三四五六七八九", "零", "負", "十", "百", "千", "萬", "億", false, false, true),
        ["traditional-chinese-formal"] = new("壹貳參肆伍陸柒捌玖", "零", "負", "拾", "佰", "仟", "萬", "億", true, false, true),

        // What the older name on the block means, spelled the traditional way.
        ["cjk-ideographic"] = new("一二三四五六七八九", "零", "負", "十", "百", "千", "萬", "億", false, false, true),

        // The two Japanese readings. The formal one keeps the one of its hundreds and writes the
        // anti-fraud digits for the first three places only - 肆 onward are the plain characters, which
        // is what the corpus shows a browser spelling.
        ["japanese-informal"] = new("一二三四五六七八九", "〇", "マイナス", "十", "百", "千", "万", "億", false, true, false),
        ["japanese-formal"] = new("壱弐参肆伍陸柒捌玖", "零", "マイナス", "拾", "百", "阡", "萬", "億", true, false, false),

        // The Korean readings: the hangul one spelled out in the alphabet, the two hanja ones in the
        // characters, the formal of those keeping 壹貳參 where the plain reading drops the one.
        ["korean-hangul-formal"] = new("일이삼사오육칠팔구", "영", "마이너스 ", "십", "백", "천", "만", "억", true, false, false),
        ["korean-hanja-informal"] = new("一二三四五六七八九", "零", "마이너스 ", "十", "百", "千", "万", "億", false, true, false),
        ["korean-hanja-formal"] = new("壹貳參四五六七八九", "零", "마이너스 ", "拾", "百", "仟", "萬", "億", true, false, false),
    };

    /// <summary>
    /// The short names the same styles answer to (CSS Lists 3 §3.1.1 gives each an "A.K.A." beside the
    /// name of the language it is read in), and the two scripts that are one another's other name.
    /// </summary>
    private static readonly Dictionary<String, String> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["simp-chinese-informal"] = "simplified-chinese-informal",
        ["simp-chinese-formal"] = "simplified-chinese-formal",
        ["trad-chinese-informal"] = "traditional-chinese-informal",
        ["trad-chinese-formal"] = "traditional-chinese-formal",
        ["khmer"] = "cambodian",
        ["lower-georgian"] = "georgian",
        ["simp-chinese"] = "simplified-chinese-informal",
        ["trad-chinese"] = "traditional-chinese-informal",
    };

    /// <summary>
    /// The symbolic lists that are a run of characters rather than an arithmetic: the two kana orders,
    /// each holding its own forty-eight syllables, and the two <c>iroha</c> runs of forty-seven, which
    /// count the same letters in the order of the old poem. Past the last symbol the count keeps the
    /// first over, the way every symbolic system does (CSS Counter Styles 3 §3.2).
    /// </summary>
    private static readonly Dictionary<String, String> Symbolic = new(StringComparer.OrdinalIgnoreCase)
    {
        ["hiragana"] = Kana(
            0x3042, 0x3044, 0x3046, 0x3048, 0x304A, 0x304B, 0x304D, 0x304F, 0x3051, 0x3053,
            0x3055, 0x3057, 0x3059, 0x305B, 0x305D, 0x305F, 0x3061, 0x3064, 0x3066, 0x3068,
            0x306A, 0x306B, 0x306C, 0x306D, 0x306E, 0x306F, 0x3072, 0x3075, 0x3078, 0x307B,
            0x307E, 0x307F, 0x3080, 0x3081, 0x3082, 0x3084, 0x3086, 0x3088, 0x3089, 0x308A,
            0x308B, 0x308C, 0x308D, 0x308F, 0x3090, 0x3091, 0x3092, 0x3093),
        ["katakana"] = Kana(
            0x30A2, 0x30A4, 0x30A6, 0x30A8, 0x30AA, 0x30AB, 0x30AD, 0x30AF, 0x30B1, 0x30B3,
            0x30B5, 0x30B7, 0x30B9, 0x30BB, 0x30BD, 0x30BF, 0x30C1, 0x30C4, 0x30C6, 0x30C8,
            0x30CA, 0x30CB, 0x30CC, 0x30CD, 0x30CE, 0x30CF, 0x30D2, 0x30D5, 0x30D8, 0x30DB,
            0x30DE, 0x30DF, 0x30E0, 0x30E1, 0x30E2, 0x30E4, 0x30E6, 0x30E8, 0x30E9, 0x30EA,
            0x30EB, 0x30EC, 0x30ED, 0x30EF, 0x30F0, 0x30F1, 0x30F2, 0x30F3),
        ["hiragana-iroha"] = Kana(
            0x3044, 0x308D, 0x306F, 0x306B, 0x307B, 0x3078, 0x3068, 0x3061, 0x308A, 0x306C,
            0x308B, 0x3092, 0x308F, 0x304B, 0x3088, 0x305F, 0x308C, 0x305D, 0x3064, 0x306D,
            0x306A, 0x3089, 0x3080, 0x3046, 0x3090, 0x306E, 0x304A, 0x304F, 0x3084, 0x307E,
            0x3051, 0x3075, 0x3053, 0x3048, 0x3066, 0x3042, 0x3055, 0x304D, 0x3086, 0x3081,
            0x307F, 0x3057, 0x3091, 0x3072, 0x3082, 0x305B, 0x3059),
        ["katakana-iroha"] = Kana(
            0x30A4, 0x30ED, 0x30CF, 0x30CB, 0x30DB, 0x30D8, 0x30C8, 0x30C1, 0x30EA, 0x30CC,
            0x30EB, 0x30F2, 0x30EF, 0x30AB, 0x30E8, 0x30BF, 0x30EC, 0x30BD, 0x30C4, 0x30CD,
            0x30CA, 0x30E9, 0x30E0, 0x30A6, 0x30F0, 0x30CE, 0x30AA, 0x30AF, 0x30E4, 0x30DE,
            0x30B1, 0x30D5, 0x30B3, 0x30A8, 0x30C6, 0x30A2, 0x30B5, 0x30AD, 0x30E6, 0x30E1,
            0x30DF, 0x30B7, 0x30F1, 0x30D2, 0x30E2, 0x30BB, 0x30B9),
    };

    /// <summary>
    /// The Armenian and Georgian additive systems: every place worth once, added up from the largest.
    /// The two armenian spellings are the same letters a case apart, and Georgian's <c>8</c>, <c>60</c>,
    /// <c>7000</c> and <c>8000</c> are the archaic letters the alphabet keeps for the purpose - which is
    /// why the runs are not consecutive.
    /// </summary>
    private static readonly (Int32 Value, String Numeral)[] ArmenianNumerals = Additive(
        0x0531, 0x053A, 0x0543, 0x054C);

    private static readonly (Int32 Value, String Numeral)[] GeorgianNumerals =
    {
        (10000, "\u10F5"),
        (9000, "\u10F0"), (8000, "\u10F3"), (7000, "\u10F4"), (6000, "\u10EE"), (5000, "\u10ED"),
        (4000, "\u10EC"), (3000, "\u10EB"), (2000, "\u10EA"), (1000, "\u10E9"),
        (900, "\u10E8"), (800, "\u10E7"), (700, "\u10E6"), (600, "\u10E5"), (500, "\u10E4"),
        (400, "\u10E3"), (300, "\u10E2"), (200, "\u10E1"), (100, "\u10E0"),
        (90, "\u10DF"), (80, "\u10DE"), (70, "\u10DD"), (60, "\u10F2"), (50, "\u10DC"),
        (40, "\u10DB"), (30, "\u10DA"), (20, "\u10D9"), (10, "\u10D8"),
        (9, "\u10D7"), (8, "\u10F1"), (7, "\u10D6"), (6, "\u10D5"), (5, "\u10D4"),
        (4, "\u10D3"), (3, "\u10D2"), (2, "\u10D1"), (1, "\u10D0"),
    };

    /// <summary>
    /// Gets the symbols a built-in symbolic or cyclic style counts with, or null where the style is a
    /// way of writing a number rather than a run of marks. A page that extends one of these takes its
    /// marks from here when it states none of its own (CSS Lists 3 §2.4).
    /// </summary>
    internal static String[]? SymbolsOf(String type) =>
        Symbols.TryGetValue(Aliases.TryGetValue(type, out var alias) ? alias : type, out var symbol)
            ? [symbol]
            : null;

    /// <summary>Gets whether the name asks for no marker at all.</summary>
    public static Boolean IsNone(String type) => type.Equals("none", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Gets the text a count is followed by: a period and a room for the counts that read as numbers,
    /// the comma the Korean readings and the full stop the Chinese and Japanese ones are written with,
    /// and a room alone for a symbol. <c>i.</c> and <c>14.</c> are counts, a bullet is not, and a string
    /// is whatever its author wrote, punctuation included.
    /// </summary>
    public static String Suffix(String type)
    {
        if (IsString(type) || !IsCounted(type))
        {
            return " ";
        }

        return SpokenStyle(type) switch
        {
            null => ". ",
            var spoken when spoken.StartsWith("korean", StringComparison.Ordinal) => ", ",
            _ => "、 ",
        };
    }

    /// <summary>
    /// Gets whether the marker is a count rather than a fixed symbol, which is what earns the period
    /// after it.
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

        // The chapter gives several of the styles a second, shorter name; both are the same list.
        var name = Aliases.TryGetValue(type, out var alias) ? alias : type;

        if (Symbols.TryGetValue(name, out var symbol))
        {
            return symbol;
        }

        if (type.Equals("decimal-leading-zero", StringComparison.OrdinalIgnoreCase))
        {
            // Padded to two, and no further: a hundredth item is not turned into three digits.
            // The magnitude is taken as a long: the number a list saturates to can be the smallest an
            // Int32 holds, whose own magnitude is one larger than the largest it can carry.
            return Math.Abs((long)number) < 10
                ? (number < 0 ? "-" : string.Empty) + Math.Abs((long)number).ToString("D2", CultureInfo.InvariantCulture)
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

        if (CountingStyles.TryGetValue(name, out var counting))
        {
            return Counted(counting, number);
        }

        if (Symbolic.TryGetValue(name, out var run))
        {
            return number >= 1 ? Spelled(number, run) : null;
        }

        if (name.Equals("armenian", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("upper-armenian", StringComparison.OrdinalIgnoreCase))
        {
            return Added(number, ArmenianNumerals, 9999);
        }

        if (name.Equals("lower-armenian", StringComparison.OrdinalIgnoreCase))
        {
            return Added(number, ArmenianNumerals, 9999, lower: true);
        }

        if (name.Equals("georgian", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("lower-georgian", StringComparison.OrdinalIgnoreCase))
        {
            return Added(number, GeorgianNumerals, 19999);
        }

        if (name.Equals("upper-georgian", StringComparison.OrdinalIgnoreCase))
        {
            return Added(number, GeorgianNumerals, 19999, mtavruli: true);
        }

        if (type.Equals("hebrew", StringComparison.OrdinalIgnoreCase))
        {
            // Four digits is where a Hebrew number stops having a shape of its own; past it the count
            // is written the way a fixed list beyond its end is - in decimal.
            return number is >= 1 and <= 999 ? Additive(number, HebrewNumerals) : Decimal(number);
        }

        if (Alphabetic.TryGetValue(name, out var alphabet))
        {
            return Spelled(number, alphabet);
        }

        if (Numerals.TryGetValue(name, out var numerals))
        {
            return Positional(number, numerals);
        }

        if (FixedLists.TryGetValue(name, out var list) && number >= 1 && number <= list.Length)
        {
            return list[number - 1].ToString();
        }

        // Past the end of a fixed run the count carries on in decimal; a name nothing answers for here
        // spells nothing.
        return FixedLists.ContainsKey(name)
            ? number.ToString(CultureInfo.InvariantCulture)
            : null;
    }

    /// <summary>Gets the ten numerals of a script, from the code point of its zero.</summary>
    private static String From(Char zero) =>
        new([.. Enumerable.Range(0, 10).Select(offset => (Char)(zero + offset))]);

    /// <summary>Gets a run of symbols from their code points, in the order the count walks them in.</summary>
    private static String Kana(params Int32[] codePoints) =>
        string.Concat(codePoints.Select(char.ConvertFromUtf32));

    /// <summary>
    /// Builds an additive table out of the four letters a script writes its first 1, 10, 100 and 1000
    /// with: each series runs nine letters up from its own start, each worth ten of the one below.
    /// </summary>
    private static (Int32 Value, String Numeral)[] Additive(Int32 ones, Int32 tens, Int32 hundreds, Int32 thousands)
    {
        var worth = new List<(Int32 Value, String Numeral)>(36);

        foreach (var (start, size) in new[] { (ones, 1), (tens, 10), (hundreds, 100), (thousands, 1000) })
        {
            for (var digit = 1; digit <= 9; digit++)
            {
                worth.Add((digit * size, char.ConvertFromUtf32(start + digit - 1)));
            }
        }

        worth.Sort(static (left, right) => right.Value.CompareTo(left.Value));
        return [.. worth];
    }

    /// <summary>
    /// Adds a number up from one of the additive systems, or leaves it in decimal where the system has
    /// no shape for it - below its first symbol, or past the last worth it holds.
    /// </summary>
    private static String Added(Int32 number, (Int32 Value, String Numeral)[] worth, Int32 largest, Boolean lower = false, Boolean mtavruli = false)
    {
        if (number < 1 || number > largest)
        {
            return Decimal(number);
        }

        var spelled = Additive(number, worth);

        if (!lower && !mtavruli)
        {
            return spelled;
        }

        var shift = lower ? 0x30 : 0xC0;
        var turned = new StringBuilder(spelled.Length);

        foreach (var letter in spelled)
        {
            turned.Append((Char)(letter + shift));
        }

        return turned.ToString();
    }

    /// <summary>
    /// Gets the name of the spoken style a marker asks for, or null where it is a count of digits. This
    /// is what decides whether the count is followed by a period, a <c>、</c> or a <c>,</c>.
    /// </summary>
    private static String? SpokenStyle(String type)
    {
        var name = Aliases.TryGetValue(type, out var alias) ? alias : type;

        if (CountingStyles.ContainsKey(name))
        {
            return name;
        }

        return name.Equals("cjk-decimal", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("cjk-earthly-branch", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("cjk-heavenly-stem", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("cjk-ideographic", StringComparison.OrdinalIgnoreCase) ||
            Symbolic.ContainsKey(name)
            ? name
            : null;
    }

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

    /// <summary>
    /// Writes a number in one of the Chinese counting styles: the groups of <c>亿</c> and <c>万</c> first,
    /// each read as a number of its own, and the remainder below ten thousand after them.
    /// </summary>
    /// <remarks>
    /// There is no upper case to fall back on: an item's number is a 32-bit count, and two billion fits
    /// inside the hundred million group, which is the last one a Chinese number nests.
    /// </remarks>
    private static String? Counted(Counting style, Int32 number)
    {
        if (number == 0)
        {
            return style.Zero;
        }

        if (number < 0)
        {
            // The word for "minus" is part of the reading, and the number under it is spelled the same
            // way it is above: 负十一 is what the page said, and a borrowed hyphen would not be.
            return style.Negative + Counted(style, -number);
        }

        var result = new StringBuilder();
        var rest = number;

        foreach (var (size, unit) in new[] { (100_000_000, style.HundredMyriad), (10_000, style.Myriad) })
        {
            if (rest < size)
            {
                continue;
            }

            var group = rest / size;
            rest %= size;

            // A group is read out whole - 一十万 is not shortened to 万 - so it is not the start of the
            // number however empty the text in front of it still is.
            result.Append(UnderTenThousand(style, group, atStart: result.Length == 0, grouped: true));
            result.Append(unit);

            // A group that begins below its own thousand leaves a hole that is read out as a zero -
            // the one in <c>一亿零五</c>, which is not the same number as <c>一亿五</c> would suggest.
            if (style.WritesZero && rest > 0 && rest < size / 10)
            {
                result.Append(style.Zero);
            }
        }

        result.Append(UnderTenThousand(style, rest, atStart: result.Length == 0));

        return result.ToString();
    }

    /// <summary>
    /// Writes a number below ten thousand in the style's own characters, reading each place with its
    /// unit and skipping the one of a leading ten - <c>十二</c> is how twelve is said, while the formal
    /// hand writes it out as <c>壹拾贰</c> so that nothing stands ready to be added in front of it.
    /// </summary>
    private static String UnderTenThousand(Counting style, Int32 number, Boolean atStart, Boolean grouped = false)
    {
        if (number == 0)
        {
            return string.Empty;
        }

        var result = new StringBuilder();

        var thousands = number / 1000;
        var hundreds = number / 100 % 10;
        var tens = number / 10 % 10;
        var units = number % 10;

        // Japanese and the Korean hanja readings say a bare 百 and a bare 千 where Chinese says 一百 and
        // 一千; both say it only at the front of the number, and the formal readings say it nowhere.
        var shortHere = style.Short && !style.Formal && atStart && !grouped;

        if (thousands > 0)
        {
            if (!(shortHere && thousands == 1))
            {
                result.Append(style.Digits[thousands - 1]);
            }

            result.Append(style.Thousand);
        }

        if (hundreds > 0)
        {
            if (result.Length > 0 && tens == 0 && units > 0 && style.WritesZero)
            {
                result.Append(style.Zero);
            }

            if (!(shortHere && hundreds == 1 && result.Length == 0))
            {
                result.Append(style.Digits[hundreds - 1]);
            }

            result.Append(style.Hundred);
        }
        else if (thousands > 0 && (tens > 0 || units > 0) && style.WritesZero)
        {
            result.Append(style.Zero);
        }

        if (tens > 0)
        {
            var leading = result.Length == 0 && atStart && tens == 1 && !style.Formal;

            if (!leading)
            {
                result.Append(style.Digits[tens - 1]);
            }

            result.Append(style.Ten);
        }
        else if ((thousands > 0 || hundreds > 0) && units > 0 && !(tens == 0 && hundreds == 0) && style.WritesZero)
        {
            // A place with nothing in it, with something written before it and something after. One nought is read
            // across a run of empty places, not one each.
            result.Append(style.Zero);
        }

        if (units > 0)
        {
            result.Append(style.Digits[units - 1]);
        }

        return result.ToString();
    }

    /// <summary>Adds up a number from the largest worth down, the way the additive systems are read.</summary>
    private static String Additive(Int32 number, (Int32 Value, String Numeral)[] worth)
    {
        var result = new StringBuilder();

        foreach (var (value, numeral) in worth)
        {
            while (number >= value)
            {
                result.Append(numeral);
                number -= value;
            }
        }

        return result.ToString();
    }

    /// <summary>The count a style could not hold, written in plain decimal.</summary>
    private static String Decimal(Int32 number) => number.ToString(CultureInfo.InvariantCulture);

    /// <summary>Writes a number in a script's own numerals, signed, the way decimal writes it.</summary>
    private static String Positional(Int32 number, String numerals)
    {
        // A long, because the smallest Int32 a list settles on has no positive twin within its own width.
        var text = Math.Abs((long)number).ToString(CultureInfo.InvariantCulture);
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

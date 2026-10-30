using System;

namespace Hutu.Rendering.Layout;

/// <summary>
/// The symbols and separators a column of Chinese or Japanese text keeps upright even though their
/// East Asian Width is not Wide.
/// </summary>
/// <remarks>
/// UAX#50 marks many of these <c>R</c> because they are not ideographs, but a column sets them as
/// full-width symbols between the ideographs: the dashes and ellipses with a presentation form, the
/// currency and mathematical signs, the enclosed numbers, the box and geometric shapes, and the
/// symbols and dingbats the scripts draw upright.
/// </remarks>
public static class CjkSymbols
{
    /// <summary>Gets whether the code point is one of those symbols.</summary>
    public static Boolean IsSymbol(Int32 codePoint) =>
        codePoint is 0x00A7 or 0x00A9 or 0x00AE or 0x00B1 or 0x00D7 or 0x00F7 or
            >= 0x00BC and <= 0x00BE or
            >= 0x2013 and <= 0x2014 or
            0x2016 or
            >= 0x2020 and <= 0x2021 or
            0x2025 or
            0x2026 or
            >= 0x2030 and <= 0x2031 or
            0x203B or
            0x203C or
            0x2042 or
            >= 0x2047 and <= 0x2049 or
            0x2051 or
            >= 0x2100 and <= 0x218F or
            0x221E or
            >= 0x2234 and <= 0x2235 or
            >= 0x2300 and <= 0x23FF or
            >= 0x2460 and <= 0x24FF or
            >= 0x2500 and <= 0x25FF or
            >= 0x2600 and <= 0x27BF or
            >= 0x2B00 and <= 0x2BFF or
            >= 0xFE50 and <= 0xFE6F or
            >= 0x1F100 and <= 0x1F2FF;
}

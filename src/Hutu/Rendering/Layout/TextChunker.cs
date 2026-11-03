using System;
using System.Collections.Generic;
using System.Text;

namespace Hutu.Rendering.Layout;

/// <summary>
/// Splits text into the pieces line breaking may not separate: a run of white space, which the document's
/// own rules collapse and the line breaks beside, and everything else, which <see cref="LineBreaks"/>
/// comes apart where the Unicode rules say a line may end.
/// </summary>
/// <remarks>
/// This lives in the platform-neutral layer because line breaking itself does: the measurer only supplies
/// advance widths, while the decision of where a line may break is script logic. The pieces are cut rather
/// than the breaks recorded because the line builds itself out of whole pieces - it takes a piece or
/// leaves it for the next line, and never splits one - which is also what makes the narrowest width a run
/// of text can be given be the widest of the pieces it is made of.
/// </remarks>
internal static class TextChunker
{
    /// <summary>
    /// A chunk line breaking may separate, as a range of the string it was cut from rather than as a
    /// string of its own.
    /// </summary>
internal readonly struct ChunkRange
    {
        public ChunkRange(Int32 start, Int32 length)
        {
            Start = start;
            Length = length;
        }

        public Int32 Start { get; }

        public Int32 Length { get; }
    }

    /// <summary>
    /// Gets the chunks of the text as ranges of it, which is what a line is built out of.
    /// </summary>
    /// <remarks>
    /// A range and not a substring because a chunk is a piece of a run the line goes on collecting:
    /// turning it into a string copies characters the run was about to copy again when it merged with
    /// the chunk after it. A string is made where one is really asked for - by the measurer, and by the
    /// layout node the painter draws text out of.
    /// </remarks>
    /// <param name="word">
    /// What the text asks of the breaks inside its words, which decides how the letters are classed: a
    /// chunk is what a line may not be ended inside, so a property that moves those boundaries has to be
    /// answered here and not only where a line is measured (CSS Text 3 §5.6).
    /// </param>
    public static ChunkRanges Ranges(
        String text,
        LineBreaks.WordBreaking word = LineBreaks.WordBreaking.Normal,
        Boolean continuesWord = false)
        => new(text, word, continuesWord);

    public static IEnumerable<String> Chunks(String text)
    {
        foreach (var range in Ranges(text))
        {
            yield return text.Substring(range.Start, range.Length);
        }
    }

    /// <summary>
    /// Gets how many runs of collapsible white space the range holds, which is how many places between
    /// its words a justified line can put the room it did not use.
    /// </summary>
    public static Int32 Spaces(String text, Int32 start, Int32 length)
    {
        var runs = 0;
        var inside = false;

        for (var index = start; index < start + length; index++)
        {
            if (IsCollapsible(text[index]))
            {
                if (!inside)
                {
                    runs++;
                }

                inside = true;
                continue;
            }

            inside = false;
        }

        return runs;
    }

    /// <summary>Gets whether the range ends in white space, which is what a line does not justify into.</summary>
    public static Boolean EndsInSpace(String text, Int32 start, Int32 length) =>
        length > 0 && IsCollapsible(text[start + length - 1]);

    /// <summary>
    /// Gets how many word separators the range holds, which is how many times the room
    /// <c>word-spacing</c> adds.
    /// </summary>
    /// <remarks>
    /// The separators are the characters whose purpose is to stand between words: the space, the no-break
    /// space and the word separators of the scripts that write them (CSS Text 3 §7.1). The tab, the
    /// ideographic space and the rest of the fixed-width gaps are left to the font, as the property says
    /// they are - and Chromium, measured, leaves them alone too.
    /// <para>
    /// Every separator the text is left with takes the room, with no exception for one that opens it: the
    /// room is what the property adds at each of them, and a space that opens a line of ordinary text is
    /// gone before the drawing, rather than standing there unpaid for (CSS Text 3 §7.1, measured against
    /// Chromium along a preformatted line, where the leading space is kept and is given the room).
    /// </para>
    /// </remarks>
    public static Int32 WordSeparators(String text, Int32 start, Int32 length)
    {
        var separators = 0;

        for (var index = start; index < start + length;)
        {
            var units = UnitsAt(text, index);

            separators += TextClusters.IsWordSeparator(CodePointAt(text, index)) ? 1 : 0;
            index += units;
        }

        return separators;
    }


    /// <summary>
    /// Gets whether the string is nothing but collapsible white space, which is what a line may break
    /// at and what the line rules collapse and trim.
    /// </summary>
    public static Boolean IsCollapsible(String chunk) => IsCollapsible(chunk, 0, chunk.Length);

    /// <summary>Gets whether the range is nothing but collapsible white space, which is what a line may break at.</summary>
    public static Boolean IsCollapsible(String text, Int32 start, Int32 length)
    {
        if (length == 0)
        {
            return false;
        }

        for (var index = start; index < start + length; index++)
        {
            if (!IsCollapsible(text[index]))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Gets whether the character is white space the line rules may collapse and break at.
    /// </summary>
    /// <remarks>
    /// CSS Text 3 §4 puts white space processing on the document white space characters and nothing
    /// else: the space (U+0020), the tab (U+0009) and the segment breaks (the line feed U+000A, with
    /// the carriage return U+000D standing in for a space in all respects). A no-break space
    /// (U+00A0), the ideographic space (U+3000) and the rest of the Unicode Zs spaces are "other
    /// space separators": white space to the eye, but never collapsed into a neighbour, never
    /// trimmed off either end of a line, and a break after one is up to <see cref="IsBreakSpace"/>
    /// rather than to this predicate. Treating them as collapsible folds five no-break spaces into one
    /// and hangs the end of a line with a space that was written to stay.
    /// <para>
    /// The control characters are left out for the other half of the same rule: §4.2 asks that a
    /// control character other than the tab, the line feed and the carriage return be drawn as a
    /// visible glyph and otherwise treated as an ordinary character. So a form feed (U+000C) and a
    /// vertical tabulation (U+000B) are content between two words - measured, kept at either end of a
    /// line, and no place to break - and the node that folded one into a space was reading the
    /// document language's list of spaces where the layout has its own.
    /// </para>
    /// </remarks>
    public static Boolean IsCollapsible(Char c) =>
        c == ' ' || c == '\t' || c == '\n' || c == '\r';

    /// <summary>
    /// Gets whether the character is a space that a line may end after: the collapsible white space, and
    /// the other space separators of CSS Text 3 §4.1.1 - the general/four-per-em/three-per-em/six-per-em/
    /// figure/punctuation/thin/hair spaces, the narrow no-break space, the medium mathematical space and
    /// the ideographic space. §4.1.3 puts a soft wrap opportunity after every one of them where the mode
    /// keeps its spaces, adjacent separators included.
    /// </summary>
    /// <remarks>
    /// The no-break space is left out, though §4.1.1 counts it among the separators: it is written to hold
    /// the two sides together, its Unicode class is GL, and Chrome breaks neither after it nor between two
    /// of them (measured: "X\u00A0\u00A0X" in a box three characters wide stays one line and measures four
    /// wide, while the same with U+2000 breaks between the two spaces).
    /// </remarks>
    /// <summary>
    /// Gets whether the character is a space that a line may end after: the collapsible white space, and
    /// the other space separators of CSS Text 3 §4.1.1 - the ogham space mark, the en/em/three-per-em/
    /// four-per-em/six-per-em/punctuation/thin/hair spaces, the medium mathematical space and the
    /// ideographic space. §4.1.3 puts a soft wrap opportunity after every one of them where the mode keeps
    /// its spaces, adjacent separators included.
    /// </summary>
    /// <remarks>
    /// The three separators whose Unicode line breaking class is GL - the no-break space, the figure space
    /// and the narrow no-break space - are left out: the rules hold those to the character before them, and
    /// neither Chrome nor the corpus breaks after one (measured: "X\u00A0\u00A0X" in a box three characters
    /// wide stays one line and measures four, while the same with U+2000 breaks between the two spaces).
    /// </remarks>
    public static Boolean IsBreakSpace(Char c) =>
        IsCollapsible(c) ||
        c == '\u1680' ||
        (c >= '\u2000' && c <= '\u2006') ||
        (c >= '\u2008' && c <= '\u200A') ||
        c == '\u205F' ||
        c == '\u3000';

    /// <summary>Gets whether the range holds a character a line may end after.</summary>
    public static Boolean HoldsBreakSpace(String text, Int32 start, Int32 length)
    {
        for (var index = start; index < start + length; index++)
        {
            if (IsBreakSpace(text[index]))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Gets the code point at an index, joining a surrogate pair into the one character it stands for.
    /// </summary>
    public static Int32 CodePointAt(String text, Int32 index)
    {
        var first = text[index];

        return IsPair(text, index) ? Char.ConvertToUtf32(first, text[index + 1]) : first;
    }

    /// <summary>
    /// Gets the code point that ends just before an index, joining a surrogate pair into the one
    /// character it stands for.
    /// </summary>
    public static Int32 CodePointBefore(String text, Int32 index)
    {
        var start = index - 1;

        if (start > 0 && Char.IsLowSurrogate(text[start]) && Char.IsHighSurrogate(text[start - 1]))
        {
            start--;
        }

        return CodePointAt(text, start);
    }

    /// <summary>
    /// Gets how many of the string's characters the code point at an index occupies.
    /// </summary>
    public static Int32 UnitsAt(String text, Int32 index) => IsPair(text, index) ? 2 : 1;

    private static Boolean IsPair(String text, Int32 index) =>
        Char.IsHighSurrogate(text[index]) &&
        index + 1 < text.Length &&
        Char.IsLowSurrogate(text[index + 1]);

    /// <summary>
    /// Gets whether a line may break on either side of the code point, which is how CJK and Hangul
    /// text wraps.
    /// </summary>
    /// <remarks>
    /// The break set is wider than the ideographic one and wider than the BMP. Hangul joins it because
    /// a Korean line comes apart between syllables without any space to help it, and the extension
    /// planes join it because the ideographs a name is written with live above U+FFFF. So the test is
    /// on a code point and not on a character: a high surrogate on its own is nothing to break at, and
    /// asking a character whether it is CJK is how an entire run of rare ideographs came to be treated
    /// as one unbreakable word.
    /// </remarks>
    public static Boolean IsCjk(Int32 codePoint) =>
        IsWide(codePoint) ||
        (codePoint >= 0x1100 && codePoint <= 0x11FF) ||
        (codePoint >= 0xA960 && codePoint <= 0xA97F) ||
        (codePoint >= 0xAC00 && codePoint <= 0xD7FF);

    /// <summary>Gets whether the code point at an index may start a new line without a space.</summary>
    public static Boolean IsCjk(String text, Int32 index) => IsCjk(CodePointAt(text, index));

    /// <summary>
    /// Gets whether a code point belongs to a writing system whose word boundaries are not marked and
    /// cannot be found without lexical or orthographic analysis: the Southeast Asian scripts UAX #14
    /// leaves to "other means" (its SA class) - Thai, Lao, Myanmar, Khmer and the scripts written
    /// against the same convention.
    /// </summary>
    /// <remarks>
    /// The ranges are the script blocks, marks and all: a line ends beside one of these letters
    /// wherever the analysis would have put a boundary, and the combining marks of the same scripts
    /// are the marks the line-breaking rules already glue to the letter before them. The set follows
    /// UAX #14's own assignment rather than a language list, because the fallback the specification
    /// asks for is a property of the writing system, not of knowing which language is being set.
    /// </remarks>
    public static Boolean RequiresAnalysis(Int32 codePoint) =>
        (codePoint >= 0x0E00 && codePoint <= 0x0EFF) ||   // Thai, Lao
        (codePoint >= 0x1000 && codePoint <= 0x109F) ||   // Myanmar
        (codePoint >= 0x1780 && codePoint <= 0x17FF) ||   // Khmer
        (codePoint >= 0x1950 && codePoint <= 0x19DF) ||   // Tai Le, New Tai Lue, Khmer symbols
        (codePoint >= 0x1A00 && codePoint <= 0x1AAF) ||   // Buginese, Tai Tham
        (codePoint >= 0x1B00 && codePoint <= 0x1BBF) ||   // Balinese, Sundanese
        (codePoint >= 0x1BC0 && codePoint <= 0x1BFF) ||   // Batak
        (codePoint >= 0xA900 && codePoint <= 0xA9DF) ||   // Kayah Li, Rejang, Javanese
        (codePoint >= 0xAA60 && codePoint <= 0xAADF) ||   // Shan and Tai Nua, Tai Viet
        (codePoint >= 0x10000 && codePoint <= 0x1007F);   // Cham

    /// <summary>Gets whether the code point at an index belongs to a script that needs analysis.</summary>
    public static Boolean RequiresAnalysis(String text, Int32 index) => RequiresAnalysis(CodePointAt(text, index));

    /// <summary>
    /// Gets whether a character keeps its own orientation when it is set in a column.
    /// </summary>
    /// <remarks>
    /// The wide characters - CJK, kana, the fullwidth forms, the ideographs of the extension planes and
    /// the vertical presentation forms of CJK punctuation - are already drawn the way a column wants
    /// them, so <c>text-orientation: mixed</c> leaves them upright. Everything else, Latin and digits
    /// above all, is turned a quarter turn so that it reads down the column rather than one letter per
    /// em. Hangul is turned with the rest of them: it breaks like an ideograph and is set like an
    /// alphabetic.
    /// </remarks>
    public static Boolean IsUpright(Int32 codePoint) =>
        IsWide(codePoint) ||
        (codePoint >= 0xFE10 && codePoint <= 0xFE6F) ||
        CjkSymbols.IsSymbol(codePoint);

    /// <summary>Gets whether the character keeps its own orientation when it is set in a column.</summary>
    public static Boolean IsUpright(Char character) => IsUpright((Int32)character);

    /// <summary>
    /// Gets the direction a piece of text reads in, which is that of its first character of strong
    /// direction: a letter, or one of the two marks that stand for a direction on their own. White
    /// space, punctuation and digits are neutral or weak and say nothing, so text that holds no strong
    /// character at all leaves the question open and answers null (UAX#9 §3.4).
    /// </summary>
    public static TextDirection? StrongDirection(String text)
    {
        foreach (var rune in text.EnumerateRunes())
        {
            if (rune.Value is 0x200E)
            {
                return TextDirection.LeftToRight;
            }

            if (rune.Value is 0x200F)
            {
                return TextDirection.RightToLeft;
            }

            if (Rune.IsLetter(rune))
            {
                return IsRightToLeftScript(rune.Value)
                    ? TextDirection.RightToLeft
                    : TextDirection.LeftToRight;
            }
        }

        return null;
    }

    /// <summary>
    /// Gets whether a letter belongs to a script that reads right to left.
    /// </summary>
    /// <remarks>
    /// The blocks are the ones Unicode gives an RTL direction: Hebrew, Arabic with its supplements and
    /// its two runs of presentation forms, Syriac, Thaana, NKo, Samaritan, Mandaic and Adlam. The
    /// historic right-to-left scripts of the first plane are not among them, which is a gap rather than
    /// a claim - a page written in Imperial Aramaic reads left to right here until the range is added.
    /// </remarks>
    private static Boolean IsRightToLeftScript(Int32 codePoint)
    {
        foreach (var (from, to) in RightToLeftScripts)
        {
            if (codePoint >= from && codePoint <= to)
            {
                return true;
            }
        }

        return false;
    }

    private static readonly (Int32 From, Int32 To)[] RightToLeftScripts =
    [
        (0x0590, 0x05FF),
        (0x0600, 0x06FF),
        (0x0700, 0x074F),
        (0x0750, 0x077F),
        (0x0780, 0x07BF),
        (0x07C0, 0x07FF),
        (0x0800, 0x085F),
        (0x08A0, 0x08FF),
        (0xFB1D, 0xFB4F),
        (0xFE70, 0xFEFF),
        (0x1E900, 0x1E95F),
    ];

    /// <summary>
    /// Gets whether a character may take part in a <c>text-combine-upright</c> run: the narrow
    /// characters a column would otherwise have to turn one by one. Digits are what the property is
    /// normally used for, but any non-wide character qualifies.
    /// </summary>
    /// <remarks>
    /// The white space test is only made of a character of the BMP, because the only code point above it
    /// that asks to be unturned is the ideographic space - and that one is wide, so it is answered by
    /// <see cref="IsUpright"/> before the question of turning is ever reached.
    /// </remarks>
    public static Boolean IsCombinable(Int32 codePoint) =>
        !(codePoint <= Char.MaxValue && Char.IsWhiteSpace((Char)codePoint)) && !IsUpright(codePoint);

    /// <summary>Gets whether a character may take part in a <c>text-combine-upright</c> run.</summary>
    public static Boolean IsCombinable(Char character) => IsCombinable((Int32)character);

    /// <summary>
    /// Gets whether the code point is one of the wide forms - the ideographic blocks of the BMP and the
    /// planes of rare ideographs above it. A column stands each of them in a box of its own, and a line
    /// comes apart between two of them.
    /// </summary>
    private static Boolean IsWide(Int32 codePoint) =>
        IsIdeographic(codePoint) ||
        (codePoint >= 0x20000 && codePoint <= 0x2FFFD) ||
        (codePoint >= 0x30000 && codePoint <= 0x3134A);

    /// <summary>Gets whether the code point is one of the wide ideographic forms of the BMP.</summary>
    private static Boolean IsIdeographic(Int32 codePoint) =>
        (codePoint >= 0x2E80 && codePoint <= 0x9FFF) ||
        (codePoint >= 0xF900 && codePoint <= 0xFAFF) ||
        (codePoint >= 0xFF00 && codePoint <= 0xFFEF);

    /// <summary>The chunks of one string, walked without materialising any of them.</summary>
internal readonly struct ChunkRanges
    {
        private readonly String _text;

        private readonly LineBreaks.WordBreaking _word;

        public ChunkRanges(String text, LineBreaks.WordBreaking word, Boolean continuesWord = false)
        {
            _text = text;
            _word = word;
            _continuesWord = continuesWord;
        }

        private readonly Boolean _continuesWord;

        public Enumerator GetEnumerator() => new(_text, _word, _continuesWord);

        /// <summary>The walk itself: the same rule <see cref="Chunks"/> applies, over indices.</summary>
internal struct Enumerator
        {
            private readonly String _text;

            private readonly LineBreaks.WordBreaking _word;

            private Int32 _index;

            public Enumerator(String text, LineBreaks.WordBreaking word, Boolean continuesWord)
            {
                _text = text;
                _word = word;
                _continuesWord = continuesWord;
                _index = 0;
                Current = default;
            }

            private readonly Boolean _continuesWord;

            public ChunkRange Current { get; private set; }

            public Boolean MoveNext()
            {
                var text = _text;

                if (text is null || _index >= text.Length)
                {
                    return false;
                }

                var index = _index;
                var current = text[index];

                if (IsCollapsible(current))
                {
                    var end = index;

                    while (end < text.Length && IsCollapsible(text[end]))
                    {
                        end++;
                    }

                    Current = new ChunkRange(index, end - index);
                    _index = end;
                    return true;
                }

                // A chunk runs to the next place a line may end: the decision is the Unicode rules', and
                // the walk stops where they say. The white space a run of text is written between is not
                // asked of them, because the document's own rules make that run a piece of its own above.
                var start = index;

                while (true)
                {
                    var length = UnitsAt(text, index);
                    var next = index + length;

                    if (next >= text.Length ||
                        IsCollapsible(text[next]) ||
                        (next > start && LineBreaks.MayBreak(text, index, next, _word, continuesWord: index == 0 && _continuesWord)))
                    {
                        _index = next;
                        Current = new ChunkRange(start, next - start);
                        return true;
                    }

                    index = next;
                }
            }
        }
    }
}

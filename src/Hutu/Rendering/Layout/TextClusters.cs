using System;
using System.Collections.Generic;
using System.Globalization;

namespace Hutu.Rendering.Layout;

/// <summary>
/// Counts text the way a reader counts characters: the extended grapheme clusters of the text, not the
/// units of the encoding the string is stored in.
/// </summary>
/// <remarks>
/// Letter spacing is charged after every character, so both the width a line gives a run and the place
/// each glyph is drawn at have to answer the same question about the same text - which is why the rule
/// is public rather than a private arithmetic of the layout. An ideograph of the extension planes is
/// one character and two of UTF-16, a vowel under its tone mark is one character and two, a family
/// emoji is one character and eleven: counting the halves instead of the characters is how the drawing
/// and the measurement came to disagree about how wide a run was.
/// </remarks>
public static class TextClusters
{
    /// <summary>Gets how many characters the range of the text is made of.</summary>
    public static Int32 Count(String text, Int32 start, Int32 length)
    {
        if (length <= 0)
        {
            return 0;
        }

        if (IsAscii(text, start, length))
        {
            // Nothing ASCII stands for is a mark, a joiner or half a pair, so each of them is a
            // character of its own and the walk over the clusters would only agree with the count.
            return length;
        }

        var end = start + length;
        var count = 0;

        var elements = StringInfo.GetTextElementEnumerator(text);

        while (elements.MoveNext())
        {
            if (elements.ElementIndex >= end)
            {
                break;
            }

            if (elements.ElementIndex >= start)
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>
    /// Gets, for every one of the text's characters, the number of the character it belongs to - or
    /// nothing, which says the same thing more cheaply: that every character of the text stands on its
    /// own and the index of a character is its number.
    /// </summary>
    /// <remarks>
    /// The glyphs a shaper answers carry the place in the text of the character they were made from,
    /// which is a position rather than a count - so the number is looked up, not derived, and a run
    /// whose glyphs come back in the order they are drawn in still gets its spacing in the order it
    /// reads in.
    /// </remarks>
    public static Int32[]? OrdinalByCharacter(String text)
    {
        if (IsAscii(text, 0, text.Length))
        {
            return null;
        }

        var ordinals = new Int32[text.Length];

        var elements = StringInfo.GetTextElementEnumerator(text);
        var ordinal = 0;

        while (elements.MoveNext())
        {
            var start = elements.ElementIndex;
            var size = ((String)elements.Current!).Length;
            var end = start + size;

            for (var index = start; index < end && index < text.Length; index++)
            {
                ordinals[index] = ordinal;
            }

            ordinal++;
        }

        return ordinals;
    }

    /// <summary>
    /// Gets where a justified line may put the room it owes: after every run of white space, and - when
    /// the line is set to expand between characters, which is what <c>text-justify: auto</c> asks of a
    /// script that writes no spaces - after every ideograph that another ideograph follows.
    /// </summary>
    /// <remarks>
    /// A place is named by the last unit of the character the room follows, so the drawing side knows
    /// which glyphs move: those after it, and not those before it. Putting a boundary after the
    /// character it follows rather than before the one it opens is what lets a run that ends at one pay
    /// the room from its own advance - the way the trailing space of a run does - and leave what comes
    /// next where the layout placed it.
    /// </remarks>
    public static Int32[] ExpansionPoints(String text, Int32 start, Int32 length, String expand)
    {
        var points = new List<Int32>();
        var end = start + length;
        var inside = false;

        for (var index = start; index < end;)
        {
            var lengthOf = TextChunker.UnitsAt(text, index);
            var next = index + lengthOf;

            index += lengthOf;

            if (TextChunker.IsCollapsible(text[index - lengthOf]))
            {
                // A run of spaces is one place, not several, and the room goes where the next word
                // begins - after the last of them.
                inside = true;
                continue;
            }

            if (inside)
            {
                points.Add(index - lengthOf - 1);
                inside = false;
            }

            if (next >= end || !Offers(expand, text, index - lengthOf, next))
            {
                continue;
            }

            points.Add(next - 1);
        }

        if (inside)
        {
            points.Add(end - 1);
        }

        return points.ToArray();
    }

    /// <summary>
    /// Gets whether the character is a word separator: one whose purpose is to stand between two words,
    /// as the specification names them rather than as the gaps that happen to fall there. The fixed-width
    /// spaces - the ideographic space among them - are not among them, and are left to the font
    /// (CSS Text 3 §7.1).
    /// </summary>
    public static Boolean IsWordSeparator(Int32 codePoint) =>
        codePoint is ' ' or 0x00A0 or 0x1361 or 0x10100 or 0x10101 or 0x1039F or 0x1091F;

    /// <summary>
    /// Gets whether the text is set in a script whose glyphs are drawn across the rule rather than clear
    /// of it, where a line broken at the ink would cut the letters to pieces. Whether
    /// <c>text-decoration-skip-ink: auto</c> skips at all is asked of the script, which is what CSS Text
    /// Decoration 4 §2.10.4 says a user agent should do: the ideographs, the kana that stand among them
    /// and the hangul syllables keep the line whole.
    /// </summary>
    public static Boolean StandsAcrossTheLine(String text)
    {
        for (var index = 0; index < text.Length;)
        {
            var units = TextChunker.UnitsAt(text, index);

            if (StandsAcrossTheLine(TextChunker.CodePointAt(text, index)))
            {
                return true;
            }

            index += units;
        }

        return false;
    }

    /// <summary>Gets whether one character belongs to a script that keeps the rule across it.</summary>
    public static Boolean StandsAcrossTheLine(Int32 codePoint) =>
        codePoint is >= 0x2E80 and <= 0x33FF or
            >= 0x3400 and <= 0x4DBF or
            >= 0x4E00 and <= 0x9FFF or
            >= 0xA000 and <= 0xA4CF or
            >= 0xAC00 and <= 0xD7AF or
            >= 0xF900 and <= 0xFAFF or
            >= 0xFE10 and <= 0xFE6F or
            >= 0xFF00 and <= 0xFFEF or
            >= 0x1F200 and <= 0x1F2FF or
            >= 0x20000 and <= 0x3FFFF;

    /// <summary>
    /// Gets where the room of <c>word-spacing</c> goes in this text: after every word separator it holds.
    /// </summary>
    /// <remarks>
    /// Named the way <see cref="ExpansionPoints"/> names its places: by the last unit of the character the
    /// room follows, so what is drawn after it moves and the separator keeps its own advance. The
    /// separation of this list from the justified line's places is deliberate: the room of
    /// <c>word-spacing</c> is owed after every separator, a run of spaces written as two included, while
    /// a justified line puts its room at the one place the run makes.
    /// </remarks>
    public static Int32[] WordSeparatorPlaces(String text)
    {
        var places = new List<Int32>();

        for (var index = 0; index < text.Length;)
        {
            var units = TextChunker.UnitsAt(text, index);
            var next = index + units;

            if (IsWordSeparator(TextChunker.CodePointAt(text, index)))
            {
                places.Add(next - 1);
            }

            index = next;
        }

        return places.ToArray();
    }

    /// <summary>
    /// Gets whether the boundary between two characters is a place the room goes, under the expansion
    /// the style asks for.
    /// </summary>
    private static Boolean Offers(String expand, String text, Int32 index, Int32 next) =>
        expand switch
        {
            // The initial value: spaces, and the seams between characters of a script that does not
            // separate its words (CSS Text 3 §3.3.1).
            "auto" => TextChunker.IsCjk(text, index) && TextChunker.IsCjk(text, next),

            // Every boundary of the text, which is what distribute and inter-character ask for: the
            // room goes between the letters as well as between the words.
            "inter-character" or "distribute" => true,

            // inter-word, and anything a reader does not know, which leaves only the spaces.
            _ => false,
        };

    /// <summary>
    /// Gets whether the seam between two pieces of a line - the boundary the text of neither piece can
    /// see, because its two characters are split between them - is a place the room of a justified line
    /// goes. It is the same question the boundary inside a piece answers, asked of the neighbours the
    /// piece has on either side of it.
    /// </summary>
    public static Boolean Seam(String left, Int32 leftEnd, String right, Int32 rightStart, String expand) =>
        expand switch
        {
            "auto" => TextChunker.IsCjk(left, leftEnd) && TextChunker.IsCjk(right, rightStart),
            "inter-character" or "distribute" => true,
            _ => false,
        };


    private static Boolean IsAscii(String text, Int32 start, Int32 length)
    {
        var end = start + length;

        for (var index = start; index < end; index++)
        {
            if (text[index] >= '\u0080')
            {
                return false;
            }
        }

        return true;
    }
}

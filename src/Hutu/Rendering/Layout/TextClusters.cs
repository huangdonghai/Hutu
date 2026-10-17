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
    /// Gets where each run of collapsible white space in the text ends, which is where a justified line
    /// puts the room it owes: everything set after that place stands further along.
    /// </summary>
    public static Int32[] SpaceRunEnds(String text)
    {
        var ends = new List<Int32>();
        var inside = false;

        for (var index = 0; index < text.Length; index++)
        {
            if (!TextChunker.IsCollapsible(text[index]))
            {
                if (inside)
                {
                    ends.Add(index - 1);
                }

                inside = false;
                continue;
            }

            inside = true;
        }

        if (inside)
        {
            ends.Add(text.Length - 1);
        }

        return ends.ToArray();
    }

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

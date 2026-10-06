using System;
using System.Collections.Generic;

namespace Hutu.Rendering.Layout;

/// <summary>
/// Splits text into the smallest units line breaking may separate: runs of white space, single CJK
/// characters, and everything else up to the next space or CJK character.
/// </summary>
/// <remarks>
/// This lives in the platform-neutral layer because line breaking itself does: the measurer only
/// supplies advance widths, while the decision of where a line may break is script logic.
/// </remarks>
public static class TextChunker
{
    public static IEnumerable<String> Chunks(String text)
    {
        var index = 0;

        while (index < text.Length)
        {
            var current = text[index];

            if (Char.IsWhiteSpace(current))
            {
                var end = index;

                while (end < text.Length && Char.IsWhiteSpace(text[end]))
                {
                    end++;
                }

                yield return text[index..end];
                index = end;
                continue;
            }

            var length = Char.IsHighSurrogate(current) && index + 1 < text.Length ? 2 : 1;

            if (IsCjk(current))
            {
                yield return text.Substring(index, length);
                index += length;
                continue;
            }

            var wordEnd = index + length;

            while (wordEnd < text.Length && !Char.IsWhiteSpace(text[wordEnd]) && !IsCjk(text[wordEnd]))
            {
                wordEnd++;
            }

            yield return text[index..wordEnd];
            index = wordEnd;
        }
    }

    public static Boolean IsWhiteSpace(String chunk)
    {
        if (chunk.Length == 0)
        {
            return false;
        }

        foreach (var character in chunk)
        {
            if (!Char.IsWhiteSpace(character))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Gets whether a character may start a new line without a space, which is how CJK text wraps.
    /// </summary>
    public static Boolean IsCjk(Char character) =>
        (character >= '\u2E80' && character <= '\u9FFF') ||
        (character >= '\uF900' && character <= '\uFAFF') ||
        (character >= '\uFF00' && character <= '\uFFEF');

    /// <summary>
    /// Gets whether a character keeps its own orientation when it is set in a column.
    /// </summary>
    /// <remarks>
    /// The wide characters - CJK, kana, the fullwidth forms, and the vertical presentation forms of
    /// their punctuation - are already drawn the way a column wants them, so <c>text-orientation:
    /// mixed</c> leaves them upright. Everything else, Latin and digits above all, is turned a quarter
    /// turn so that it reads down the column rather than one letter per em.
    /// </remarks>
    public static Boolean IsUpright(Char character) =>
        IsCjk(character) || (character >= '\uFE10' && character <= '\uFE4F');

    /// <summary>
    /// Gets whether a character may take part in a <c>text-combine-upright</c> run: the narrow
    /// characters a column would otherwise have to turn one by one. Digits are what the property is
    /// normally used for, but any non-wide character qualifies.
    /// </summary>
    public static Boolean IsCombinable(Char character) =>
        !Char.IsWhiteSpace(character) && !IsUpright(character);
}

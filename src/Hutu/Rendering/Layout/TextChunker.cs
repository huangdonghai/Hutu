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
    /// <summary>
    /// A chunk line breaking may separate, as a range of the string it was cut from rather than as a
    /// string of its own.
    /// </summary>
    public readonly struct ChunkRange
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
    public static ChunkRanges Ranges(String text) => new(text);

    public static IEnumerable<String> Chunks(String text)
    {
        foreach (var range in Ranges(text))
        {
            yield return text.Substring(range.Start, range.Length);
        }
    }

    public static Boolean IsWhiteSpace(String chunk) => IsWhiteSpace(chunk, 0, chunk.Length);

    /// <summary>Gets whether the range is nothing but white space, which is what a line may break at.</summary>
    public static Boolean IsWhiteSpace(String text, Int32 start, Int32 length)
    {
        if (length == 0)
        {
            return false;
        }

        for (var index = start; index < start + length; index++)
        {
            if (!Char.IsWhiteSpace(text[index]))
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

    /// <summary>The chunks of one string, walked without materialising any of them.</summary>
    public readonly struct ChunkRanges
    {
        private readonly String _text;

        public ChunkRanges(String text) => _text = text;

        public Enumerator GetEnumerator() => new(_text);

        /// <summary>The walk itself: the same rule <see cref="Chunks"/> applies, over indices.</summary>
        public struct Enumerator
        {
            private readonly String _text;
            private Int32 _index;

            public Enumerator(String text)
            {
                _text = text;
                _index = 0;
                Current = default;
            }

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

                if (Char.IsWhiteSpace(current))
                {
                    var end = index;

                    while (end < text.Length && Char.IsWhiteSpace(text[end]))
                    {
                        end++;
                    }

                    Current = new ChunkRange(index, end - index);
                    _index = end;
                    return true;
                }

                var length = Char.IsHighSurrogate(current) && index + 1 < text.Length ? 2 : 1;

                if (IsCjk(current))
                {
                    Current = new ChunkRange(index, length);
                    _index = index + length;
                    return true;
                }

                var wordEnd = index + length;

                while (wordEnd < text.Length && !Char.IsWhiteSpace(text[wordEnd]) && !IsCjk(text[wordEnd]))
                {
                    wordEnd++;
                }

                Current = new ChunkRange(index, wordEnd - index);
                _index = wordEnd;
                return true;
            }
        }
    }
}

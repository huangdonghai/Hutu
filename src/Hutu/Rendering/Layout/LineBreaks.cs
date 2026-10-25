using System;
using System.Globalization;

namespace Hutu.Rendering.Layout;

/// <summary>
/// Where a line may end between two characters that stand next to each other, as the Unicode line
/// breaking algorithm decides it.
/// </summary>
/// <remarks>
/// The rules are UAX #14 section 6, asked in the order the annex numbers them, because the first one
/// that matches is the answer and the last of them - break everywhere else - is the reason a pair a
/// rule does not name is left breakable. The question is only ever asked of two characters with nothing
/// between them: white space is what the document's own white space rules settle before a line asks,
/// and a run of it is already a piece the line may break beside, so this never sees a space, a tab or a
/// segment break.
/// <para>
/// Three of the annex's resolutions are deliberately not made here. Quotation marks (LB15a, LB15b,
/// LB19) are asked to decide whether they open or close from which side of the pair they answer to, and
/// are treated as alphabetic instead, which keeps the two common cases - no break inside <c>"foo"</c>,
/// no break after an opening quote - and gives up only the break a closing quote might otherwise take
/// from the word before it. The Brahmic orthographic syllable (LB28a) needs the vowel-sign classes of a
/// dozen Indic scripts, none of which this engine has been asked to lay out; a character of those
/// classes comes out alphabetic here, and a run of them stays together, which is what LB28 does for a
/// word. And the planes above the BMP are not in <see cref="LineBreakTable"/>, so an astral character
/// is alphabetic unless the script test knows it as an ideograph - which is how the many extension
/// ideographs stay breakable and the rare astral punctuation is not.
/// </para>
/// <para>
/// What the table's own narrowing costs is visible in one corner: LB13, LB14 and LB15d forbid a break
/// before a closing mark or after an opening one <em>even across a space</em>, and this engine takes the
/// break the space offers. The two positions draw the same line, because a line's trailing white space
/// is removed and the next line's leading white space is never drawn - which is why a browser asked to
/// break <c>foo 。</c> across a space puts the space where the spec puts it, on no line at all.
/// </para>
/// </remarks>
internal static class LineBreaks
{
    /// <summary>
    /// What the page asked of the breaks inside its words, which the Unicode rules are read through.
    /// </summary>
    /// <remarks>
    /// The two values of <c>word-break</c> work on the same pair of characters from opposite ends:
    /// <c>break-all</c> takes the letter units and reads them as ideographic, so the rules run on their
    /// neighbourhood as they would on a run of CJK text; <c>keep-all</c> sets the break those rules give
    /// between two such units aside (CSS Text 3 §5.6).
    /// </remarks>
    internal enum WordBreaking : Byte
    {
        /// <summary>Nothing was asked: the rules stand as they are.</summary>
        Normal,

        /// <summary><c>word-break: break-all</c> - the letter units are read as ideographic.</summary>
        BreakAll,

        /// <summary><c>word-break: keep-all</c> - no implicit break between two letter units.</summary>
        KeepAll,
    }
    /// <summary>
    /// Gets whether a line may end between the code point at <paramref name="left"/> and the one at
    /// <paramref name="right"/>, which is the next code point in the string.
    /// </summary>
    /// <param name="word">
    /// What the page asked of the breaks inside its words, which decides how the letters themselves are
    /// classed (CSS Text 3 §5.6).
    /// </param>
    /// <param name="continuesWord">
    /// Whether the piece begins inside a word the characters before it have already begun - a text
    /// node whose first character stands after an inline element's letter. LB20a asks whether a hyphen
    /// opens a word, and a piece beginning with a soft hyphen after a letter is inside one, not at its
    /// head, however the piece alone reads.
    /// </param>
    public static Boolean MayBreak(
        String text,
        Int32 left,
        Int32 right,
        WordBreaking word = WordBreaking.Normal,
        Boolean continuesWord = false)
    {
        var after = ClassOf(TextChunker.CodePointAt(text, right), word);

        // LB9, LB10: a mark does not begin a line, whatever it would have stood for on its own.
        if (after is BreakClass.Cm or BreakClass.Zwj)
        {
            return false;
        }

        // LB8a, LB9: a joiner or a mark written after a character is written onto it, and nothing may
        // come between the two. A mark with a character before it in the same piece is read as that
        // character, which is what lets a line end after an accented letter and not inside it.
        var own = ClassOf(TextChunker.CodePointAt(text, left), word);

        if (own is BreakClass.Zwj)
        {
            return false;
        }

        var before = own is BreakClass.Cm ? Base(text, left, word) : own;

        // A hyphen at the head of this piece need not be at the head of the word: the piece may stand
        // where the letters before it left off, and then LB20a has nothing to hold.
        var startsWord = left == 0 && continuesWord ? false : (Boolean?)null;

        return Decide(text, left, right, before, after, word, startsWord);
    }

    /// <summary>
    /// Gets whether text beginning after the given code point begins inside a word - the question LB20a
    /// asks of a hyphen that heads the text, when the character before the text is in another piece.
    /// </summary>
    public static Boolean ContinuesWord(Int32 codePoint, WordBreaking word = WordBreaking.Normal)
    {
        if (codePoint < 0)
        {
            return false;
        }

        if (codePoint <= Char.MaxValue && TextChunker.IsCollapsible((Char)codePoint))
        {
            return false;
        }

        return ClassOf(codePoint, word) is not (BreakClass.Op or BreakClass.Gl);
    }

    /// <summary>
    /// Gets whether a line may end between two code points that stand in different runs - the two
    /// sides of an inline element's edge, which no single string holds. The rules that need the
    /// characters around the pair - whether a hyphen opens a word, whether a number runs on - are not
    /// asked, since the runs do not carry their neighbours; the pair's own rules are.
    /// </summary>
    public static Boolean MayBreak(Int32 beforeCodePoint, Int32 afterCodePoint, WordBreaking word = WordBreaking.Normal)
    {
        // The document's white space processing is what settled the spaces around this pair; the line
        // breaker sees the space itself only where a run of it reaches the edge of a box. A space
        // before the pair is the opportunity itself (LB31 after SP), and a space after it is never a
        // place to begin a line (LB7).
        if (beforeCodePoint <= Char.MaxValue && TextChunker.IsCollapsible((Char)beforeCodePoint))
        {
            return true;
        }

        if (afterCodePoint <= Char.MaxValue && TextChunker.IsCollapsible((Char)afterCodePoint))
        {
            return false;
        }

        var after = ClassOf(afterCodePoint, word);

        if (after is BreakClass.Cm or BreakClass.Zwj)
        {
            return false;
        }

        var before = ClassOf(beforeCodePoint, word);

        if (before is BreakClass.Zwj)
        {
            return false;
        }

        return Decide(null, 0, 0, before, after, word);
    }

    private static Boolean Decide(
        String? text,
        Int32 left,
        Int32 right,
        BreakClass before,
        BreakClass after,
        WordBreaking word,
        Boolean? startsWord = null)
    {
        // LB11: a word joiner holds both sides of itself.
        if (before is BreakClass.Wj || after is BreakClass.Wj)
        {
            return false;
        }

        // LB12, LB12a: no-break glue holds both sides too, except that a hyphen may still be broken
        // after, which is what lets a word keep running against a no-break space it was written for.
        if (before is BreakClass.Gl || (after is BreakClass.Gl && before is not (BreakClass.Hy or BreakClass.Hh)))
        {
            return false;
        }

        // LB7: a space or a zero width space is not a place to begin a line.
        if (after is BreakClass.Zw)
        {
            return false;
        }

        // LB13, LB15d, LB21, LB22: a closing mark, a separator, a non-starter and an ellipsis do not
        // begin a line, and neither does a dash that was written to end the one before it.
        if (after is BreakClass.Cl or BreakClass.Cp or BreakClass.Ex or BreakClass.Sy or BreakClass.Is or
            BreakClass.Ba or BreakClass.Hh or BreakClass.Hy or BreakClass.Ns or BreakClass.In)
        {
            return false;
        }

        // LB14: opening punctuation does not end one.
        if (before is BreakClass.Op)
        {
            return false;
        }

        // LB17: a dash written twice is one mark.
        if (before is BreakClass.B2 && after is BreakClass.B2)
        {
            return false;
        }

        // LB20a: a hyphen that begins a word begins it, and does not break after itself.
        if (text is not null && before is BreakClass.Hy or BreakClass.Hh && after is BreakClass.Al &&
            (startsWord ?? StartsWord(text, left, word)))
        {
            return false;
        }

        // LB21: a mark that breaks before what it introduces is written against it.
        if (before is BreakClass.Bb)
        {
            return false;
        }

        if (text is not null && EndsNumber(text, left, before, word))
        {
            // LB23, LB25: a number is one thing - the digits, the letter stuck to the end of them, the
            // sign that counts them and the bracket that closes them.
            if (after is BreakClass.Nu or BreakClass.Po or BreakClass.Pr or BreakClass.Al)
            {
                return false;
            }
        }
        else if ((before is BreakClass.Al && after is BreakClass.Nu) ||
            (before is BreakClass.Pr && after is BreakClass.Al or BreakClass.Id or BreakClass.Nu) ||
            (before is BreakClass.Po && after is BreakClass.Al or BreakClass.Nu) ||
            (after is BreakClass.Pr && before is BreakClass.Al) ||
            (after is BreakClass.Po && before is BreakClass.Al or BreakClass.Id))
        {
            // LB23, LB23a, LB24: a digit and a letter, a price and what it prices, a count and what it counts.
            return false;
        }

        // LB28: two letters are one word.
        if (before is BreakClass.Al && after is BreakClass.Al)
        {
            return false;
        }

        // LB29: the period of "e.g." goes with the letters it stands after.
        if (before is BreakClass.Is && after is BreakClass.Al)
        {
            return false;
        }

        // LB30: a parenthesis inside a word is part of it, as in "(s)he", and an opening one beside a
        // letter is not a place to end.
        if ((before is BreakClass.Al && after is BreakClass.Op) ||
            (before is BreakClass.Cp && after is BreakClass.Al))
        {
            return false;
        }

        // LB25 again, from the other side: a sign before a bracketed number holds to the bracket.
        if (text is not null && before is BreakClass.Pr or BreakClass.Po && after is BreakClass.Op && StartsNumber(text, right, word))
        {
            return false;
        }

        // LB31: everywhere else a line may end - except that a page asking for its words to be kept
        // whole sets exactly this opportunity aside between two of the letter units, which is what makes
        // a run of CJK characters one word rather than a word at every character.
        return word is not WordBreaking.KeepAll || !HoldsTogether(before, after);
    }

    /// <summary>
    /// Gets whether a pair of classes is the kind <c>keep-all</c> holds: a typographic letter unit, or a
    /// character of the numeric, alphabetic, ambiguous or ideographic class. The engine reads the
    /// ambiguous class and the Southeast Asian letters as alphabetic, so the three it names are the four
    /// the property does.
    /// </summary>
    private static Boolean HoldsTogether(BreakClass before, BreakClass after) =>
        IsHeld(before) && IsHeld(after);

    private static Boolean IsHeld(BreakClass classification) =>
        classification is BreakClass.Al or BreakClass.Nu or BreakClass.Id;

    /// <summary>
    /// Gets the line breaking class of a code point, resolving the two the table leaves out and reading
    /// the letters as the page asked them to be read.
    /// </summary>
    /// <remarks>
    /// <c>break-all</c> is the property's own mechanism rather than a licence laid on top of the rules:
    /// the typographic letter units - and the numeric, alphabetic and Southeast Asian classes, which this
    /// table reads as alphabetic where it does not name them - are treated as ideographic. The rules that
    /// speak of the alphabetic class therefore step back from them, which is what gives the break between
    /// two letters, and the ones that forbid a break before a closing mark or after an opening one hold
    /// over them, which is what withholds it (CSS Text 3 §5.6, [UAX14]). Chrome is measured to keep to
    /// exactly that: an eleven-letter run fills its line to the character, while "AB.," breaks after the
    /// A and nowhere near the comma.
    /// </remarks>
    private static BreakClass ClassOf(Int32 codePoint, WordBreaking word)
    {
        var named = LineBreakTable.Of(codePoint);

        if (named is not null)
        {
            return Held(named.Value, word);
        }

        // LB1: the marks and the controls the table does not name are marks, and a mark is its base
        // character's - which is what keeps an accent, a variation selector or a joiner from ever being
        // the place a line ends.
        if (codePoint <= Char.MaxValue && IsMark((Char)codePoint))
        {
            return BreakClass.Cm;
        }

        return Held(TextChunker.IsCjk(codePoint) ? BreakClass.Id : BreakClass.Al, word);
    }

    /// <summary>Gets the class a letter unit stands for, which is its own unless the page broke within words.</summary>
    private static BreakClass Held(BreakClass classification, WordBreaking word) =>
        word is WordBreaking.BreakAll && classification is BreakClass.Al or BreakClass.Nu
            ? BreakClass.Id
            : classification;

    private static Boolean IsMark(Char character) =>
        Char.GetUnicodeCategory(character) is
            UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or
            UnicodeCategory.EnclosingMark or UnicodeCategory.Format or UnicodeCategory.Control;

    /// <summary>
    /// Gets the class of the character a mark stands on, which is the class the sequence of the two is
    /// read as (LB9).
    /// </summary>
    private static BreakClass Base(String text, Int32 index, WordBreaking word)
    {
        var start = index;

        while (start > 0)
        {
            var classification = ClassOf(TextChunker.CodePointAt(text, start), word);

            if (classification is not (BreakClass.Cm or BreakClass.Zwj))
            {
                return classification;
            }

            var behind = CodePointBefore(text, start);

            // LB10: a mark with nothing under it - the character it was written on being a space the
            // document already broke the word at - is an alphabetic of its own.
            if (TextChunker.IsCollapsible((Char)behind))
            {
                return BreakClass.Al;
            }

            start -= PreviousLength(text, start);
        }

        return ClassOf(TextChunker.CodePointAt(text, start), word);
    }

    /// <summary>Gets whether a number - digits, and the marks written between digits - ends here.</summary>
    private static Boolean EndsNumber(String text, Int32 index, BreakClass at, WordBreaking word)
    {
        // A digit is a number whole by itself; the rest of the question is whether the marks behind it
        // are the ones a number is written between - and a character with nothing behind it, the first
        // of a piece of text that opens on a closing mark, is asked about nothing.
        if (at is BreakClass.Nu)
        {
            return true;
        }

        if (index == 0 || at is not (BreakClass.Sy or BreakClass.Is or BreakClass.Cl or BreakClass.Cp))
        {
            return false;
        }

        // The annex's number is NU (SY|IS)* (CL|CP)?, so the run is walked back over the separators until
        // a digit is found or something else is, and a number a bracket closed still counts as one.
        var walk = index;

        if (at is BreakClass.Cl or BreakClass.Cp)
        {
            walk -= PreviousLength(text, walk);
        }

        while (walk > 0)
        {
            var length = PreviousLength(text, walk);
            var previous = ClassOf(CodePointBefore(text, walk), word);

            if (previous is BreakClass.Nu)
            {
                return true;
            }

            if (previous is not (BreakClass.Sy or BreakClass.Is))
            {
                return false;
            }

            walk -= length;
        }

        return false;
    }

    /// <summary>Gets whether an opening bracket at an index is the one a number is written inside.</summary>
    private static Boolean StartsNumber(String text, Int32 index, WordBreaking word)
    {
        if (ClassOf(TextChunker.CodePointAt(text, index), word) is not BreakClass.Op)
        {
            return false;
        }

        var after = index + TextChunker.UnitsAt(text, index);

        while (after < text.Length &&
               ClassOf(TextChunker.CodePointAt(text, after), word) is BreakClass.Op or BreakClass.Sy or BreakClass.Is)
        {
            after += TextChunker.UnitsAt(text, after);
        }

        return after < text.Length && ClassOf(TextChunker.CodePointAt(text, after), word) is BreakClass.Nu;
    }

    /// <summary>
    /// Gets whether the character before a position begins a word, which is what makes a leading hyphen a
    /// minus sign rather than a place to break.
    /// </summary>
    private static Boolean StartsWord(String text, Int32 index, WordBreaking word)
    {
        if (index == 0)
        {
            return true;
        }

        var before = CodePointBefore(text, index);

        // A space is where the document already broke the word, and an opening mark is what a word is
        // written inside of, as "(self-" reads.
        return TextChunker.IsCollapsible((Char)before) ||
               ClassOf(before, word) is BreakClass.Op or BreakClass.Gl;
    }

    /// <summary>Gets the code point standing before an index, joining a pair into the one it stands for.</summary>
    private static Int32 CodePointBefore(String text, Int32 index)
    {
        var length = PreviousLength(text, index);

        return index - length >= 0 ? Char.ConvertToUtf32(text, index - length) : text[index - 1];
    }

    /// <summary>Gets how many of the string's characters the code point before an index occupies.</summary>
    private static Int32 PreviousLength(String text, Int32 index)
    {
        var character = text[index - 1];

        return Char.IsLowSurrogate(character) && index >= 2 && Char.IsHighSurrogate(text[index - 2])
            ? 2
            : 1;
    }
}

using Hutu.Rendering.Layout;
using System;
using System.Text;

namespace Hutu.Rendering.Boxes;

/// <summary>
/// Applies <c>text-transform</c> to the text the tree holds, in the order the page reads it.
/// </summary>
/// <remarks>
/// <c>capitalize</c> asks whether a letter begins a word, and a word can run across the boxes of
/// several elements: <c>p&lt;span&gt;&lt;/span&gt;ass</c> is one word, and an out-of-flow box between
/// the two is not a letter of it (CSS Text 3 2.1, measured by text-transform-capitalize-033). The
/// state is therefore kept while the whole box tree is walked, and reset where a block begins.
/// </remarks>
internal static class TextTransformResolver
{
    internal static void Apply(Box root)
    {
        var inWord = false;
        Walk(root, ref inWord);
    }

    private static void Walk(Box box, ref Boolean inWord)
    {
        // A box out of the flow is not read in the word its neighbours spell.
        if (PositionedLayout.IsOutOfFlow(box))
        {
            return;
        }

        if (box.IsBlockLevel || box.Kind == BoxKind.LineBreak)
        {
            inWord = false;
        }

        if (box.Kind == BoxKind.Text)
        {
            if (box.Text is { Length: > 0 } text)
            {
                box.Text = Transform(text, box.TextTransform, ref inWord);
            }

            return;
        }

        foreach (var child in box.Children)
        {
            Walk(child, ref inWord);
        }
    }

    private static String Transform(String text, String? transform, ref Boolean inWord)
    {
        var upper = Keyword(transform, "uppercase");
        var lower = Keyword(transform, "lowercase");
        var capitalize = Keyword(transform, "capitalize");

        if (!upper && !lower && !capitalize)
        {
            Track(text, ref inWord);
            return text;
        }

        var drawn = new StringBuilder(text.Length);

        for (var at = 0; at < text.Length; at++)
        {
            var character = text[at];

            // A Greek eta standing alone as a word is the disjunctive "or", and keeps its tonos when
            // the sentence is written in capitals (text-transform-tailoring-005, measured in Chromium).
            if (upper && !inWord && (character == '\u03AE' || character == '\u0389') &&
                (at + 1 == text.Length || !WordCharacter(text[at + 1])))
            {
                drawn.Append('\u0389');
                inWord = true;
                continue;
            }

            // A Greek diphthong accented on its first vowel takes the dialytika on the second when the
            // word is written in capitals: Nepsilon-alpha-iota with a tonos becomes ALPHA + IOTA with a
            // dialytika (text-transform-tailoring-003).
            if (upper && at + 1 < text.Length && Diphthong(character, text[at + 1]) is { } diphthong)
            {
                drawn.Append(diphthong.First).Append(diphthong.Second);
                inWord = true;
                at++;
                continue;
            }

            var letter = WordCharacter(character);

            var drawnCharacter =
                capitalize && letter && !inWord ? Char.ToUpperInvariant(character) :
                upper ? GreekUppercase(character) :
                lower ? Char.ToLowerInvariant(character) :
                character;

            // A combining tonos removed by the Greek rule leaves nothing to draw.
            if (drawnCharacter != '\u0000')
            {
                drawn.Append(drawnCharacter);
            }

            inWord = letter;
        }

        return drawn.ToString();
    }

    private static void Track(String text, ref Boolean inWord)
    {
        foreach (var character in text)
        {
            inWord = WordCharacter(character);
        }
    }

    /// <summary>
    /// Gets whether a character is a letter of the word being capitalized. The middle dot is a
    /// letter of Catalan, Occitan and Franco-Provencal words, so it does not begin a new word
    /// (text-transform-capitalize-035, measured in Chromium).
    /// </summary>
    private static Boolean WordCharacter(Char character) => Char.IsLetterOrDigit(character) || character == '\u00B7';

    /// <summary>
    /// Uppercases one character as Greek does when the word is entirely uppercase: the tonos is
    /// removed, whether it was written precomposed or as a combining mark (CSS Text 3 2.1;
    /// text-transform-tailoring-002/002a).
    /// </summary>
    private static Char GreekUppercase(Char character) =>
        character == '\u0301' ? '\u0000' : StripTonos(Char.ToUpperInvariant(character));

    private static Char StripTonos(Char character) => character switch
    {
        '\u0386' => '\u0391',
        '\u0388' => '\u0395',
        '\u0389' => '\u0397',
        '\u038A' => '\u0399',
        '\u038C' => '\u039F',
        '\u038E' => '\u03A5',
        '\u038F' => '\u03A9',
        '\u0390' => '\u03AA',
        '\u03B0' => '\u03AB',
        _ => character,
    };

    /// <summary>
    /// Gets how an accented Greek diphthong is written in capitals: the first vowel loses its tonos and
    /// the second takes the dialytika. Only the accent marks the engine reads for this rule are named.
    /// </summary>
    private static (Char First, Char Second)? Diphthong(Char first, Char second)
    {
        var next = Char.ToLowerInvariant(second);

        if (next is not ('\u03B9' or '\u03C5'))
        {
            return null;
        }

        var plain = first switch
        {
            '\u03AC' => '\u03B1',
            '\u03AD' => '\u03B5',
            '\u03AE' => '\u03B7',
            '\u03AF' => '\u03B9',
            '\u03CC' => '\u03BF',
            '\u03CD' => '\u03C5',
            '\u03CE' => '\u03C9',
            _ => '\u0000',
        };

        return plain == '\u0000'
            ? null
            : (Char.ToUpperInvariant(plain), next == '\u03B9' ? '\u03AA' : '\u03AB');
    }

    private static Boolean Keyword(String? value, String keyword) =>
        value is { Length: > 0 } && value.Contains(keyword, StringComparison.OrdinalIgnoreCase);
}

using Hutu.Rendering.Layout;
using NUnit.Framework;
using System;
using System.Linq;

namespace Hutu.Tests;

/// <summary>
/// Covers the bidi algorithm's levels and the order a line's runs are drawn in (UAX #9): a left-to-right
/// line is left alone, a neutral at the end of a right-to-left paragraph takes the paragraph's direction,
/// a word of the other direction is drawn as one piece of its own, and digits keep their own order.
/// </summary>
/// <remarks>
/// The expectations are the rules' own: N1 and N2 give the neutrals their direction, I1 and I2 the
/// implicit levels, and L2 the order. They are the cases the alignment pages of the corpus show.
/// </remarks>
[TestFixture]
public sealed class BidiTests
{
    [Test]
    public void ALeftToRightParagraphStandsAsItIs()
    {
        var levels = Bidi.Levels("nine ten.", 0);

        Assert.That(levels, Is.All.EqualTo(0), "nothing in a left-to-right paragraph is reordered");
        Assert.That(Order(levels), Is.EqualTo(new[] { 0, 1, 2, 3, 4, 5, 6, 7, 8 }));
    }

    [Test]
    public void ATrailingFullStopTakesTheDirectionOfARightToLeftParagraph()
    {
        // "nine ten." in a right-to-left paragraph: the Latin letters stand a step further in (level 2),
        // and the full stop, a neutral with no strong text after it, takes the paragraph's own direction
        // (N2) - so it is drawn at the left, before the words.
        var levels = Bidi.Levels("nine ten.", 1);

        Assert.That(levels.Take(8), Is.All.EqualTo(2), "the words are a left-to-right island");
        Assert.That(levels[8], Is.EqualTo(1), "and the full stop is set at the paragraph's level");
        Assert.That(Order(levels), Is.EqualTo(new[] { 8, 0, 1, 2, 3, 4, 5, 6, 7 }));
    }

    [Test]
    public void AHebrewSentenceKeepsItsWordsWhole()
    {
        // Three Hebrew letters, a space, and a Latin word. The Hebrew is drawn right to left, the Latin
        // word is one piece drawn as it reads, and the space between them takes the paragraph's
        // direction because the strong text either side of it points two ways (N2).
        var text = "\u05D0\u05D1\u05D2 abc";
        var levels = Bidi.Levels(text, 1);

        Assert.That(levels[0], Is.EqualTo(1));
        Assert.That(levels[2], Is.EqualTo(1));
        Assert.That(levels[3], Is.EqualTo(1), "the space takes the paragraph's direction");
        Assert.That(levels[4], Is.EqualTo(2), "the Latin word is an island at the next level");
        Assert.That(Order(levels), Is.EqualTo(new[] { 4, 5, 6, 3, 2, 1, 0 }));
    }

    [Test]
    public void DigitsAfterLatinTextReadWithItInsideARightToLeftLine()
    {
        // W7 makes the digits part of the Latin run they follow, so the number is drawn left to right
        // inside the island, and the island stands at the left of the right-to-left line.
        var levels = Bidi.Levels("abc 123", 1);

        Assert.That(levels[4], Is.EqualTo(2), "the digits follow the Latin text they stand after");
        Assert.That(levels, Is.All.EqualTo(2), "the whole line is one left-to-right island");
        Assert.That(Order(levels), Is.EqualTo(new[] { 0, 1, 2, 3, 4, 5, 6 }));
    }

    private static Int32[] Order(Int32[] levels) => Bidi.VisualOrder(levels);
}

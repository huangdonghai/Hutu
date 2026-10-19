using Hutu.Rendering;
using NUnit.Framework;
using System;
using System.Linq;

namespace Hutu.Tests;

/// <summary>
/// Covers the reading HTML 2.3.6 writes for the obsolete colour attributes: the two values that name no
/// colour, the keywords, the three- and six-digit forms, and the digit reading that turns whatever is
/// left into a colour rather than into nothing.
/// </summary>
/// <remarks>
/// Every expected value is what Chrome paints a link for the same value written on
/// <c>body link="..."</c>, measured page by page. The digit reading is the part worth that trouble: it
/// looks like a mistake - a <c>currentcolor</c> becoming a shade of chartreuse is nothing a page means -
/// and it is what the specification says, what every browser of the 1990s did, and what Chrome still
/// answers today.
/// </remarks>
[TestFixture]
public sealed class LegacyColorTests
{
    [TestCase("#008000", 0xFF008000u)]
    [TestCase("008000", 0xFF008000u)]
    [TestCase("pink", 0xFFFFC0CBu)]
    [TestCase("Fuchsia", 0xFFFF00FFu)]
    [TestCase("rebeccapurple", 0xFF663399u)]
    [TestCase("#00f", 0xFF0000FFu)]
    [TestCase("#080", 0xFF008800u)]
    [TestCase("#0aB0Ff", 0xFF0AB0FFu)]
    [TestCase(" #008000", 0xFF008000u)]
    [TestCase("gray ", 0xFF808080u)]
    public void AValueThatNamesAColourIsThatColour(String text, UInt32 expected) =>
        Assert.That(Rgb(text), Is.EqualTo(expected), "the value reads as the colour it names");

    [TestCase(null)]
    [TestCase("")]
    [TestCase("transparent")]
    [TestCase("TRANSPARENT")]
    public void AValueThatNamesNoColourIsNothingAtAll(String? text) =>
        Assert.That(LegacyColor.Parse(text), Is.Null, "the reading fails rather than guessing");
    [TestCase("#0800", 0xFF080000u)]
    [TestCase("#00f8", 0xFF00F800u)]
    [TestCase("0080", 0xFF008000u)]
    [TestCase("#00f00", 0xFF00F000u)]
    [TestCase("#0000ff88", 0xFF000F88u)]
    [TestCase("rgb(0, 128, 255)", 0xFF000125u)]
    [TestCase("hsl(120, 100%, 50%)", 0xFF000150u)]
    [TestCase("notacolor", 0xFF00AC00u)]
    [TestCase("currentcolor", 0xFFC0E000u)]
    [TestCase("inherit", 0xFF00E000u)]
    [TestCase("activeborder", 0xFFAC0E0Du)]
    [TestCase("0000ff", 0xFF0000FFu)]
    [TestCase("00ffff", 0xFF00FFFFu)]
    [TestCase("   ", 0xFF000000u)]
    [TestCase("#", 0xFF000000u)]
    [TestCase("8", 0xFF080000u)]
    public void AValueThatNamesNoColourIsReadAsTheDigitsItCarries(String text, UInt32 expected)
    {
        // The digits of the value are what stands: each character that is no hexadecimal digit is a
        // zero, the run is padded to three equal parts, and the first two digits of each part are one
        // channel. Chrome paints every one of these values exactly that colour.
        Assert.That(Rgb(text), Is.EqualTo(expected), "the old attribute reading spells a colour out of it");
    }

    [Test]
    public void TheSystemColoursAreNoNamesHere()
    {
        // HTML says the CSS2 system colours are not recognized by this reading, and AngleSharp keeps them
        // in the same table as the keywords - so white is the wrong answer and the digits are the right
        // one, which is what Chrome draws for every three of them.
        Assert.That(Rgb("activeborder"), Is.EqualTo(0xFFAC0E0Du), "the name is read as digits, not as white");
        Assert.That(Rgb("window"), Is.EqualTo(0xFF000D00u), "nor is the second one read as a name");
        Assert.That(Rgb("menutext"), Is.EqualTo(0xFFE00E00u), "and the third loses its leading zeros together");
    }

    [Test]
    public void AValueLongerThanTheReadingAllowsIsCutShort()
    {
        var text = String.Concat(Enumerable.Repeat("#ff00ee", 30));

        Assert.That(text.Length, Is.EqualTo(210), "the value runs past the 128 characters the reading keeps");
        Assert.That(Rgb(text), Is.EqualTo(0xFFFFF000u), "only the front of it is read");
    }

    [Test]
    public void ACharacterOutsideTheBasicPlaneIsTwoZeros()
    {
        var text = Char.ConvertFromUtf32(0x1F600) + "ff";

        Assert.That(Rgb(text), Is.EqualTo(0xFF00FF00u), "the character becomes zeros and the digits stand");
    }

    [Test]
    public void AColourTheReadingSpellsIsTheColourTheEngineDraws()
    {
        // The reader hands back what the rest of the engine paints with, so a channel-by-channel answer
        // and the packed colour the painter carries have to agree.
        var color = LegacyColor.Parse("#009933");

        Assert.That(color, Is.Not.Null);
        Assert.That(ComputedStyle.ToArgb(color!.Value), Is.EqualTo(0xFF009933u));
    }

    private static UInt32? Rgb(String? text) =>
        LegacyColor.Parse(text) is { } color ? ComputedStyle.ToArgb(color) : null;
}

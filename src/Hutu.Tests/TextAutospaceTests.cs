using Hutu.Rendering.Layout;
using NUnit.Framework;
using System;

namespace Hutu.Tests;

/// <summary>
/// Covers the rules of <c>text-autospace</c>: the value grammar, the character classes the spacing is
/// written in, which neighbouring pairs it separates, and the French punctuation it adds (CSS Text 4
/// §8.4).
/// </summary>
[TestFixture]
public sealed class TextAutospaceTests
{
    private static readonly TextAutospaceValue Inherited =
        TextAutospaceValue.Of(AutoSpaceKind.Punctuation, replace: true);

    private static TextAutospaceValue Parse(String declared) =>
        TextAutospaceValue.TryParse(declared, Inherited, out var value)
            ? value
            : throw new AssertionException($"'{declared}' was not read as a value");

    [Test]
    public void TheInitialValueSpacesIdeographsAndBothKindsOfNeighbour()
    {
        Assert.Multiple(() =>
        {
            Assert.That(TextAutospaceValue.Initial.Kinds, Is.EqualTo(AutoSpaceKind.Alpha | AutoSpaceKind.Numeric));
            Assert.That(TextAutospaceValue.Initial.Replace, Is.False);
            Assert.That(TextAutospaceValue.Initial.Off, Is.False);
        });
    }

    [TestCase("normal", AutoSpaceKind.Alpha | AutoSpaceKind.Numeric)]
    [TestCase("auto", AutoSpaceKind.Alpha | AutoSpaceKind.Numeric)]
    [TestCase("ideograph-alpha", AutoSpaceKind.Alpha)]
    [TestCase("ideograph-numeric", AutoSpaceKind.Numeric)]
    [TestCase("punctuation", AutoSpaceKind.Punctuation)]
    [TestCase("ideograph-alpha ideograph-numeric", AutoSpaceKind.Alpha | AutoSpaceKind.Numeric)]
    [TestCase("ideograph-numeric ideograph-alpha", AutoSpaceKind.Alpha | AutoSpaceKind.Numeric)]
    [TestCase("ideograph-alpha punctuation", AutoSpaceKind.Alpha | AutoSpaceKind.Punctuation)]
    [TestCase("insert", AutoSpaceKind.Alpha | AutoSpaceKind.Numeric)]
    [TestCase("replace", AutoSpaceKind.Alpha | AutoSpaceKind.Numeric)]
    [TestCase("no-autospace", AutoSpaceKind.None)]
    public void EveryValueTheGrammarAllowsIsRead(String declared, Int32 kinds)
    {
        Assert.That(Parse(declared).Kinds, Is.EqualTo((AutoSpaceKind)kinds));
    }

    [Test]
    public void ReplaceIsKeptAndImpliedByNothingElse()
    {
        Assert.Multiple(() =>
        {
            Assert.That(Parse("ideograph-alpha replace").Replace, Is.True);
            Assert.That(Parse("ideograph-alpha").Replace, Is.False);
            Assert.That(Parse("replace").Replace, Is.True);
            Assert.That(Parse("normal").Replace, Is.False);
        });
    }

    [TestCase("no-autospace ideograph-alpha")]
    [TestCase("normal replace")]
    [TestCase("ideograph-alpha ideograph-alpha")]
    [TestCase("insert replace")]
    [TestCase("no-autospace no-autospace")]
    [TestCase("ideograph-alpha nonsense")]
    [TestCase("")]
    [TestCase("   ")]
    public void AValueTheGrammarRefusesLeavesTheInheritedOne(String declared)
    {
        Assert.Multiple(() =>
        {
            Assert.That(TextAutospaceValue.TryParse(declared, Inherited, out var value), Is.False);
            Assert.That(value, Is.EqualTo(Inherited));
        });
    }

    [TestCase("initial", AutoSpaceKind.Alpha | AutoSpaceKind.Numeric)]
    [TestCase("inherit", AutoSpaceKind.Punctuation)]
    [TestCase("unset", AutoSpaceKind.Punctuation)]
    [TestCase("revert", AutoSpaceKind.Punctuation)]
    public void CssWideKeywordsAnswerTheInitialOrTheInheritedValue(String declared, Int32 kinds)
    {
        Assert.That(Parse(declared).Kinds, Is.EqualTo((AutoSpaceKind)kinds));
    }

    [TestCase(0x56FD, AutoSpaceClass.Ideograph)] // 国
    [TestCase(0x6C38, AutoSpaceClass.Ideograph)] // 永
    [TestCase(0x6C34, AutoSpaceClass.Ideograph)] // 水
    [TestCase(0x3005, AutoSpaceClass.Ideograph)] // 々, Han
    [TestCase(0x30FC, AutoSpaceClass.Ideograph)] // ー, in the kana range and no punctuation
    [TestCase(0x31F0, AutoSpaceClass.Ideograph)] // katakana phonetic extension
    [TestCase(0x20000, AutoSpaceClass.Ideograph)] // supplementary Han
    [TestCase(0x30FB, AutoSpaceClass.Other)] // ・ is punctuation, so not an ideograph
    [TestCase(0xFF1A, AutoSpaceClass.Other)] // ： fullwidth punctuation
    [TestCase(0x3002, AutoSpaceClass.Other)] // 。 fullwidth closing, outside the Han script
    [TestCase(0x3001, AutoSpaceClass.Other)] // 、
    [TestCase(0x0041, AutoSpaceClass.Letter)] // A
    [TestCase(0x00E9, AutoSpaceClass.Letter)] // é
    [TestCase(0x05D0, AutoSpaceClass.Letter)] // Hebrew alef
    [TestCase(0x0021, AutoSpaceClass.Letter)] // !, narrow punctuation is a neighbour
    [TestCase(0x0023, AutoSpaceClass.Letter)] // #
    [TestCase(0x0031, AutoSpaceClass.Numeric)] // 1
    [TestCase(0x0661, AutoSpaceClass.Numeric)] // Arabic-Indic one
    [TestCase(0xFF18, AutoSpaceClass.Other)] // ８ is fullwidth
    [TestCase(0xFF38, AutoSpaceClass.Other)] // Ｘ is fullwidth
    [TestCase(0xFF71, AutoSpaceClass.Letter)] // ｱ is halfwidth, not wide
    [TestCase(0x0301, AutoSpaceClass.Letter)] // combining acute
    [TestCase(0x0020, AutoSpaceClass.Other)] // space suppresses by being there
    [TestCase(0x200B, AutoSpaceClass.Other)] // zero width space is a character between
    [TestCase(0x200D, AutoSpaceClass.Other)] // zero width joiner
    [TestCase(0x000A, AutoSpaceClass.Other)] // line feed
    public void EveryKindOfCharacterFallsWhereThePropertyNeedsIt(Int32 codePoint, Int32 expected)
    {
        Assert.That(TextAutospace.ClassOf(codePoint, upright: false), Is.EqualTo((AutoSpaceClass)expected));
    }

    [Test]
    public void AnUprightNeighbourIsNoNeighbourAtAll()
    {
        Assert.Multiple(() =>
        {
            Assert.That(TextAutospace.ClassOf('X', upright: true), Is.EqualTo(AutoSpaceClass.Other));
            Assert.That(TextAutospace.ClassOf('1', upright: true), Is.EqualTo(AutoSpaceClass.Other));
            Assert.That(TextAutospace.ClassOf(0x56FD, upright: true), Is.EqualTo(AutoSpaceClass.Ideograph));
        });
    }

    [Test]
    public void TheSpacedPairsAreTheIdeographFacingOnesTheValueAsksFor()
    {
        var both = TextAutospaceValue.Initial;
        var alpha = TextAutospaceValue.Of(AutoSpaceKind.Alpha, replace: false);
        var numeric = TextAutospaceValue.Of(AutoSpaceKind.Numeric, replace: false);

        Assert.Multiple(() =>
        {
            Assert.That(TextAutospace.ShouldSpace(both, AutoSpaceClass.Ideograph, AutoSpaceClass.Letter), Is.True);
            Assert.That(TextAutospace.ShouldSpace(both, AutoSpaceClass.Letter, AutoSpaceClass.Ideograph), Is.True);
            Assert.That(TextAutospace.ShouldSpace(both, AutoSpaceClass.Ideograph, AutoSpaceClass.Numeric), Is.True);
            Assert.That(TextAutospace.ShouldSpace(both, AutoSpaceClass.Numeric, AutoSpaceClass.Ideograph), Is.True);
            Assert.That(TextAutospace.ShouldSpace(both, AutoSpaceClass.Ideograph, AutoSpaceClass.Ideograph), Is.False);
            Assert.That(TextAutospace.ShouldSpace(both, AutoSpaceClass.Letter, AutoSpaceClass.Numeric), Is.False);
            Assert.That(TextAutospace.ShouldSpace(both, AutoSpaceClass.Other, AutoSpaceClass.Ideograph), Is.False);
            Assert.That(TextAutospace.ShouldSpace(alpha, AutoSpaceClass.Ideograph, AutoSpaceClass.Numeric), Is.False);
            Assert.That(TextAutospace.ShouldSpace(numeric, AutoSpaceClass.Ideograph, AutoSpaceClass.Letter), Is.False);
            Assert.That(TextAutospace.ShouldSpace(numeric, AutoSpaceClass.Ideograph, AutoSpaceClass.Numeric), Is.True);
        });
    }

    [Test]
    public void TheSpacedAmountIsAnEighthOfTheCjkAdvance()
    {
        Assert.That(TextAutospace.SpaceFraction, Is.EqualTo(0.125));
    }

    [TestCase("fr", 0x003B, PunctuationSpace.Before)] // ;
    [TestCase("fr", 0x003A, PunctuationSpace.Before)] // :
    [TestCase("fr", 0x0021, PunctuationSpace.Before)] // !
    [TestCase("fr", 0x003F, PunctuationSpace.Before)] // ?
    [TestCase("fr", 0x00AB, PunctuationSpace.After)] // «
    [TestCase("fr", 0x00BB, PunctuationSpace.Before)] // »
    public void FrenchPunctuationTakesItsNoBreakSpace(String language, Int32 codePoint, Int32 side)
    {
        var value = TextAutospaceValue.Of(AutoSpaceKind.Punctuation, replace: false);

        Assert.That(TextAutospace.Punctuation(value, language, codePoint), Is.EqualTo((PunctuationSpace)side));
    }

    [TestCase("en", 0x003B)]
    [TestCase("fr", 0x002C)]
    [TestCase(null, 0x003B)]
    public void OtherLanguagesAndMarksTakeNone(String? language, Int32 codePoint)
    {
        var value = TextAutospaceValue.Of(AutoSpaceKind.Punctuation, replace: false);

        Assert.That(TextAutospace.Punctuation(value, language, codePoint), Is.EqualTo(PunctuationSpace.None));
    }

    [Test]
    public void PunctuationOnlySpeaksWhenTheValueAsksForIt()
    {
        Assert.That(
            TextAutospace.Punctuation(TextAutospaceValue.Initial, "fr", 0x003B),
            Is.EqualTo(PunctuationSpace.None));
    }
}

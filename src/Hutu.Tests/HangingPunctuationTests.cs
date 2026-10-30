using Hutu.Rendering.Layout;
using NUnit.Framework;

namespace Hutu.Tests;

/// <summary>
/// Covers the value <c>hanging-punctuation</c> takes and the characters each edge it names reaches
/// (CSS Text 3 §8.2.1).
/// </summary>
[TestFixture]
public sealed class HangingPunctuationTests
{
    private static HangingPunctuationValue Parse(String declared) =>
        HangingPunctuationValue.TryParse(declared, HangingPunctuationValue.Initial, out var value)
            ? value
            : throw new AssertionException($"'{declared}' is not a value this property takes");

    [Test]
    public void TheInitialValueHangsNothing()
    {
        Assert.That(HangingPunctuationValue.Initial.Off, Is.True);
        Assert.That(Parse("none").Off, Is.True);
    }

    [TestCase("first", 1)]
    [TestCase("last", 8)]
    [TestCase("allow-end", 2)]
    [TestCase("force-end", 4)]
    public void EachKeywordNamesItsEdge(String declared, Int32 kind)
    {
        Assert.That((Int32)Parse(declared).Kinds, Is.EqualTo(kind));
    }

    [TestCase("first last", 1 | 8)]
    [TestCase("last first", 1 | 8)]
    [TestCase("allow-end last", 2 | 8)]
    [TestCase("first force-end", 1 | 4)]
    [TestCase("first allow-end last", 1 | 2 | 8)]
    public void TheKeywordsCombine(String declared, Int32 kinds)
    {
        Assert.That((Int32)Parse(declared).Kinds, Is.EqualTo(kinds));
    }

    [TestCase("first first")]
    [TestCase("last last")]
    [TestCase("allow-end force-end")]
    [TestCase("force-end allow-end")]
    [TestCase("auto")]
    [TestCase("normal")]
    [TestCase("none first")]
    [TestCase("hanging")]
    public void AValueTheGrammarDoesNotTakeLeavesTheInheritedOne(String declared)
    {
        var inherited = HangingPunctuationValue.Of(HangingPunctuationKind.Last);

        Assert.That(HangingPunctuationValue.TryParse(declared, inherited, out var value), Is.False);
        Assert.That(value, Is.EqualTo(inherited));
    }

    [Test]
    public void ACssWideKeywordKeepsTheInheritedValue()
    {
        Assert.That(Parse("inherit").Kinds, Is.EqualTo(HangingPunctuationValue.Initial.Kinds));
        Assert.That(Parse("initial").Kinds, Is.EqualTo(HangingPunctuationKind.None));
    }

    [TestCase(0x0028)]
    [TestCase(0x005B)]
    [TestCase(0x201C)]
    [TestCase(0x2018)]
    [TestCase(0x300C)]
    [TestCase(0x0022)]
    [TestCase(0x0027)]
    [TestCase(0x3000)]
    public void AnOpeningBracketQuoteOrIdeographicSpaceHangsAtTheStart(Int32 codePoint)
    {
        Assert.That(HangingPunctuation.IsOpening(codePoint), Is.True);
        Assert.That(
            HangingPunctuation.HangsAtStart(HangingPunctuationValue.Of(HangingPunctuationKind.First)),
            Is.True);
    }

    [TestCase(0x0029)]
    [TestCase(0x005D)]
    [TestCase(0x201D)]
    [TestCase(0x300D)]
    [TestCase(0x0022)]
    [TestCase(0x0027)]
    public void AClosingBracketOrQuoteHangsAtTheEnd(Int32 codePoint)
    {
        Assert.That(HangingPunctuation.IsClosing(codePoint), Is.True);
        Assert.That(
            HangingPunctuation.HangsAtEnd(
                HangingPunctuationValue.Of(HangingPunctuationKind.Last), codePoint, overflows: false, lastFormattedLine: true),
            Is.True);
    }

    [TestCase(0x3001)]
    [TestCase(0x3002)]
    [TestCase(0xFF0C)]
    [TestCase(0xFF0E)]
    [TestCase(0x002C)]
    [TestCase(0x002E)]
    [TestCase(0xFE50)]
    [TestCase(0xFF61)]
    public void AStopOrCommaIsWhatAllowEndAndForceEndReach(Int32 codePoint)
    {
        var allow = HangingPunctuationValue.Of(HangingPunctuationKind.AllowEnd);
        var force = HangingPunctuationValue.Of(HangingPunctuationKind.ForceEnd);

        Assert.That(HangingPunctuation.IsStopOrComma(codePoint), Is.True);
        Assert.That(HangingPunctuation.HangsAtEnd(allow, codePoint, overflows: false, lastFormattedLine: false), Is.False);
        Assert.That(HangingPunctuation.HangsAtEnd(allow, codePoint, overflows: true, lastFormattedLine: false), Is.True);
        Assert.That(HangingPunctuation.HangsAtEnd(force, codePoint, overflows: false, lastFormattedLine: false), Is.True);
    }

    [Test]
    public void LastReachesOnlyTheFinalLineAndOnlyClosingGlyphs()
    {
        var last = HangingPunctuationValue.Of(HangingPunctuationKind.Last);

        Assert.That(HangingPunctuation.HangsAtEnd(last, 0x0029, overflows: false, lastFormattedLine: false), Is.False);
        Assert.That(HangingPunctuation.HangsAtEnd(last, 0x002C, overflows: false, lastFormattedLine: true), Is.False);
    }
}

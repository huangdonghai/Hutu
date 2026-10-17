using Hutu.Rendering.Layout;
using NUnit.Framework;
using System;
using System.Text;

namespace Hutu.Tests;

/// <summary>
/// Covers what a character of the text is: the clusters the letter spacing is charged by, counted over
/// the encoding the string is stored in rather than by it.
/// </summary>
/// <remarks>
/// The strings are built from the code points they stand for, because a mark, a joiner and half a pair
/// are exactly the things a source file cannot be trusted to spell out.
/// </remarks>
[TestFixture]
public sealed class TextClusterTests
{
    [Test]
    public void TheCountIsOfCharacters()
    {
        Check(3, "abc");
        Check(1, Join(0x00E9));
        Check(1, Join(0x0065, 0x0301));
        Check(1, Join(0x20000));
        Check(2, Join(0x20000, 0x0078));
        Check(1, Join(0x1F468, 0x200D, 0x1F469, 0x200D, 0x1F467, 0x200D, 0x1F466));
        Check(1, Join(0x1F1EF, 0x1F1F5));
    }

    [Test]
    public void ARangeIsCountedOnItsOwn()
    {
        var text = Join(0x0061, 0x0062, 0x20000, 0x0063);

        Assert.That(
            TextClusters.Count(text, 2, 2),
            Is.EqualTo(1),
            "the ideograph and both of the units that stand for it");
        Assert.That(TextClusters.Count(text, 0, 2), Is.EqualTo(2), "the two letters before it");
        Assert.That(TextClusters.Count(text, 0, 0), Is.EqualTo(0), "nothing is no characters");
    }

    [Test]
    public void EveryCharacterOfTheTextIsNumbered()
    {
        var text = Join(0x0078, 0x0065, 0x0301, 0x20000, 0x007A);

        Assert.That(
            TextClusters.OrdinalByCharacter(text),
            Is.EqualTo(new[] { 0, 1, 1, 2, 2, 3 }),
            "the units of a character carry its number, not one of their own");
    }

    [Test]
    public void TextThatNeedsNoNumbersGoesWithoutThem()
    {
        Assert.That(
            TextClusters.OrdinalByCharacter("plain text"),
            Is.Null,
            "a stretch of nothing but letters has one character in it per unit of the encoding");
    }

    private static void Check(Int32 expected, String text) =>
        Assert.That(
            TextClusters.Count(text, 0, text.Length),
            Is.EqualTo(expected),
            $"the encoding holds {text.Length} units of '{text}'");

    /// <summary>Builds the text the given code points are set in.</summary>
    private static String Join(params Int32[] codePoints)
    {
        var text = new StringBuilder();

        foreach (var codePoint in codePoints)
        {
            text.Append(Char.ConvertFromUtf32(codePoint));
        }

        return text.ToString();
    }
}

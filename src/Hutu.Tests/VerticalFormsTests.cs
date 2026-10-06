using Hutu.Rendering.Layout;
using NUnit.Framework;
using System;

namespace Hutu.Tests;

/// <summary>
/// Covers the table a column falls back on when the font itself carries no <c>vert</c> feature: the
/// horizontal punctuation of CJK text against the vertical presentation form Unicode reserved for it.
/// </summary>
[TestFixture]
public sealed class VerticalFormsTests
{
    [TestCase('\u3001', '\uFE11')]   // 、 ideographic comma
    [TestCase('\u3002', '\uFE12')]   // 。 ideographic full stop
    [TestCase('\uFF0C', '\uFE10')]   // ， fullwidth comma
    [TestCase('\u300C', '\uFE41')]   // 「
    [TestCase('\u300D', '\uFE42')]   // 」
    [TestCase('\u300E', '\uFE43')]   // 『
    [TestCase('\u300F', '\uFE44')]   // 』
    [TestCase('\u300A', '\uFE3D')]   // 《
    [TestCase('\u300B', '\uFE3E')]   // 》
    [TestCase('\u3010', '\uFE3B')]   // 【
    [TestCase('\u3011', '\uFE3C')]   // 】
    [TestCase('\uFF08', '\uFE35')]   // （
    [TestCase('\uFF09', '\uFE36')]   // ）
    [TestCase('\u2014', '\uFE31')]   // — em dash
    [TestCase('\u2026', '\uFE19')]   // … horizontal ellipsis
    public void WidePunctuationHasAVerticalForm(Char character, Char expected)
    {
        Assert.That(VerticalForms.TryGet(character, out var vertical), Is.True);
        Assert.That(vertical, Is.EqualTo(expected));
    }

    [TestCase('春')]
    [TestCase('あ')]
    [TestCase('A')]
    [TestCase(' ')]
    [TestCase(':')]   // the narrow marks are turned by `mixed` rather than stood upright
    [TestCase('(')]
    [TestCase('ー')]   // the prolonged sound mark has no form of its own, only the font's `vert`
    public void CharactersWithNoVerticalFormAreHandedBack(Char character)
    {
        Assert.That(VerticalForms.TryGet(character, out var vertical), Is.False);
        Assert.That(vertical, Is.EqualTo(character));
    }

    [TestCase('\uFE11')]   // already the vertical form of 、
    [TestCase('\uFE12')]   // already the vertical form of 。
    [TestCase('\uFE41')]   // already the vertical form of 「
    [TestCase('\uFE35')]   // already the vertical form of （
    public void AVerticalFormIsAlreadyWhatAColumnWants(Char character)
    {
        Assert.That(VerticalForms.TryGet(character, out var vertical), Is.False);
        Assert.That(vertical, Is.EqualTo(character));
    }

    /// <summary>
    /// Every form the table names has to be something a column stands upright: substituting a
    /// character for one the column would then turn back over on its side would be no improvement.
    /// </summary>
    [Test]
    public void EveryFormIsOneAColumnStandsUpright()
    {
        for (var codepoint = 0; codepoint <= Char.MaxValue; codepoint++)
        {
            if (!VerticalForms.TryGet((Char)codepoint, out var vertical))
            {
                continue;
            }

            Assert.That(
                TextChunker.IsUpright(vertical),
                Is.True,
                $"U+{codepoint:X4} maps to U+{(Int32)vertical:X4}, which a column would not stand upright");
        }
    }

    /// <summary>
    /// A form is the mark one character stands for, so no two characters may claim the same one - the
    /// table would otherwise be answering with a glyph that belongs to something else.
    /// </summary>
    [Test]
    public void NoTwoCharactersShareAForm()
    {
        var claimed = new System.Collections.Generic.Dictionary<Char, Char>();

        for (var codepoint = 0; codepoint <= Char.MaxValue; codepoint++)
        {
            if (!VerticalForms.TryGet((Char)codepoint, out var vertical))
            {
                continue;
            }

            Assert.That(
                claimed.TryAdd(vertical, (Char)codepoint),
                Is.True,
                $"U+{(Int32)vertical:X4} is claimed by both U+{(Int32)claimed[vertical]:X4} and U+{codepoint:X4}");
        }
    }
}

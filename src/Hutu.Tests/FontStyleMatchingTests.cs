using Hutu.Rendering.Layout;
using Hutu.Skia;
using NUnit.Framework;
using System;
using System.Diagnostics;

namespace Hutu.Tests;

/// <summary>
/// Covers the face a family name answers with when the page asks for a weight or a slant: the platform is
/// asked with the style in hand, and a name that stands for whichever face the machine keeps under it -
/// the CSS generics - answers the same way whatever is asked, so the style has to be asked again of the
/// family the alias really means.
/// </summary>
[TestFixture]
public sealed class FontStyleMatchingTests
{
    [Test]
    public void AGenericFamilyIsDrawnAtTheWeightItIsAskedFor()
    {
        using var measurer = new SkiaTextMeasurer();

        var plain = measurer.MeasureWidth("Bongo", new FontStyle("sans-serif", 96));
        var bold = measurer.MeasureWidth("Bongo", new FontStyle("sans-serif", 96, 700));

        // A bold of the same family reaches wider than its regular, which is what a page asking for
        // font-weight: bold is drawn with; the two being equal is the alias swallowing the request.
        Assert.That(bold, Is.GreaterThan(plain), "the bold of the family the generic name means");
    }

    [Test]
    public void AGenericFamilyIsDrawnWithTheSlantItIsAskedFor()
    {
        using var measurer = new SkiaTextMeasurer();

        var upright = measurer.Blobs.GetRunInk(new FontStyle("sans-serif", 96), "a", 0)[0];
        var slanted = measurer.Blobs.GetRunInk(new FontStyle("sans-serif", 96, italic: true), "a", 0)[0];

        // An italic form is a different drawing of the letter, so the room its ink fills differs - the
        // advances may well be the same, which is why this is asked of the ink rather than the width.
        Assert.That(
            (slanted.Left, slanted.Right),
            Is.Not.EqualTo((upright.Left, upright.Right)),
            "the italic of the family the generic name means");
    }

    [Test]
    [NonParallelizable]
    public void AFamilyListIsResolvedOnceForManyCharacters()
    {
        using var resolver = new SkiaFontResolver();
        var style = new FontStyle("sans-serif", 16);
        var stopwatch = Stopwatch.StartNew();

        // These characters all miss the primary face. Searching the family list for each one is the
        // regression this guards: the list and its style decide the candidate faces, not the glyph.
        for (var codepoint = 0x4E00; codepoint < 0x4EC8; codepoint++)
        {
            _ = resolver.ResolveListed(style, codepoint);
        }

        stopwatch.Stop();

        Assert.That(
            stopwatch.Elapsed,
            Is.LessThan(TimeSpan.FromSeconds(5)),
            "the family list is resolved once and each character only checks its glyphs");
    }
}

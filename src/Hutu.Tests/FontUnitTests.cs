using Hutu.Rendering.Layout;
using Hutu.Skia;
using NUnit.Framework;
using System;
using System.Threading.Tasks;

namespace Hutu.Tests;

/// <summary>
/// Covers <c>ex</c> and <c>ch</c>: that they are read off the face the text is set in rather than
/// guessed from its size, and that a face which states nothing is left to the approximation the engine
/// used before the faces could be asked.
/// </summary>
/// <remarks>
/// Ahem is the font these are measured in because it states both: its lowercase reaches the whole top of
/// its em box, so <c>1ex</c> is four fifths of the size, and its zero is the em wide, so <c>1ch</c> is
/// the whole of it. The fake measurer states neither, and answers with the fixed five pixels a character.
/// </remarks>
[TestFixture]
public sealed class FontUnitTests
{
    [Test]
    public async Task ExIsTheHeightOfTheFaceTheTextIsSetIn()
    {
        using var view = CreateView(new SkiaTextMeasurer());

        // Ahem stands its x-height at eight of its ten units, so four ex is four times sixteen and not
        // four times half the size, which is what the engine used while it had no face to ask.
        await view.SetHtml(Page("font-family:Ahem;font-size:20px;width:4ex"));

        Assert.That(Box(view).Width, Is.EqualTo(64), "four of the face's own x-heights");
    }

    [Test]
    public async Task ChIsTheWidthOfTheFaceTheTextIsSetIn()
    {
        using var view = CreateView(new SkiaTextMeasurer());

        await view.SetHtml(Page("font-family:Ahem;font-size:20px;width:2ch"));

        Assert.That(Box(view).Width, Is.EqualTo(40), "two of the face's own zeros");
    }

    [Test]
    public async Task TheSameUnitsHoldAcrossTheBoxModel()
    {
        using var view = CreateView(new SkiaTextMeasurer());

        // The inset is the same unit read for a different property, and it has to be read the same way or
        // a page written in ex lays out as a set of unrelated fractions.
        await view.SetHtml(Page("font-family:Ahem;font-size:20px;margin-left:1ex;width:0"));

        Assert.That(Box(view).X, Is.EqualTo(16), "one x-height of room beside the box");
    }

    [Test]
    public async Task AFaceThatStatesNothingIsLeftToTheHalfEm()
    {
        using var view = CreateView(new FakeTextMeasurer());

        // The fake answers no x-height for any font, and the fallback is the fraction of the size the
        // engine measured these units from before the faces could be asked at all.
        await view.SetHtml(Page("font-size:10px;width:4ex"));

        Assert.That(Box(view).Width, Is.EqualTo(20), "four of the half-em the size leaves to the unit");
    }

    private static String Page(String style) =>
        "<html><body style='margin:0'><div id='x' style='" + style + "'>x</div></body></html>";

    private static HutuViewInternal CreateView(ITextMeasurer measurer)
    {
        var view = new HutuViewInternal(measurer);
        view.SetViewportSize(600, 600);
        return view;
    }

    private static Rect Box(HutuViewInternal view)
    {
        var element = view.Document!.GetElementById("x")
                      ?? throw new AssertionException("The document holds no box to read.");

        return view.GetElementBounds(element)!.Value;
    }
}

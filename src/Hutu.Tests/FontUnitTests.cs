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

    /// <summary>
    /// A run of a line that reads up the page is set with its first character at the far end of the room
    /// the line gave it, while every glyph keeps the face the font drew it in. This is how Chrome lays
    /// Latin text in a <c>vertical-rl</c> column of <c>direction: rtl</c>: the run stands at the foot of
    /// the page and the F of "Fab" is the topmost glyph of it.
    /// </summary>
    [Test]
    public void ABackToFrontRunStandsItsCharactersAtTheFarEndOfTheirRoom()
    {
        using var measurer = new SkiaTextMeasurer();
        var style = new FontStyle("Ahem", 20);
        var width = measurer.MeasureWidth("abc", style);

        var forward = measurer.Blobs.GetRunInk(style, "abc", 0);
        var back = measurer.Blobs.GetRunInk(style, "abc", 0, reverse: true);

        Assert.That(forward, Has.Length.EqualTo(3), "one box for each character");
        Assert.That(back[0].Left, Is.GreaterThan(back[2].Left), "the first character stands past the last");

        for (var index = 0; index < forward.Length; index++)
        {
            Assert.That(
                back[index].Left,
                Is.EqualTo(width - forward[index].Right).Within(0.5),
                "the room is mirrored about the run's own extent");
            Assert.That(back[index].Right, Is.EqualTo(width - forward[index].Left).Within(0.5));
        }
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

    [Test]
    public async Task ARelativeFontSizeIsMeasuredFromTheParentsFace()
    {
        using var view = CreateView(new SkiaTextMeasurer());

        // The child asks for two ex of the font it was handed, and the parent is set in Ahem at twenty:
        // the size the child comes out at is twice the parent's x-height, and Ahem's advance is the whole
        // of its em, so the width of one of its characters reads the size back.
        await view.SetHtml(
            "<html><body style='margin:0'><div style='font-family:Ahem;font-size:20px'>" +
            "<span id='x' style='font-size:2ex'>x</span></div></body></html>");

        Assert.That(Box(view).Width, Is.EqualTo(32), "two of the parent's x-heights");
    }

    [Test]
    public async Task ALineHeightWrittenInExIsMeasuredFromTheFaceToo()
    {
        using var view = CreateView(new SkiaTextMeasurer());

        // line-height is the one length the reader parses out of the string it was given rather than as
        // a length value, and it has to land on the same unit as every other property.
        await view.SetHtml(
            "<html><body style='margin:0'><div id='x' style='font-family:Ahem;font-size:20px;" +
            "line-height:1ex'>x</div></body></html>");

        Assert.That(Box(view).Height, Is.EqualTo(16), "one x-height of leading");
    }

    [Test]
    public async Task AGapWrittenInChIsMeasuredFromTheFace()
    {
        using var view = CreateView(new SkiaTextMeasurer());

        // The track readers of flex, grid and table took the size alone; a gap written in ch is the
        // width of the face's zero, and Ahem's zero is its whole em.
        await view.SetHtml(
            "<html><body style='margin:0'><div style='display:flex;column-gap:1ch;font-family:Ahem;" +
            "font-size:20px'><div style='width:10px;height:10px'></div><div id='x' style='width:10px;height:10px'></div>" +
            "</div></body></html>");

        Assert.That(Box(view).X, Is.EqualTo(30), "the first box, then the zero-wide gap of twenty");
    }

    [Test]
    public async Task AnInsetWrittenInExIsMeasuredFromTheFace()
    {
        using var view = CreateView(new SkiaTextMeasurer());

        await view.SetHtml(
            "<html><body style='margin:0'><div id='x' style='position:relative;top:1ex;" +
            "font-family:Ahem;font-size:20px;width:0;height:0'></div></body></html>");

        Assert.That(Box(view).Y, Is.EqualTo(16), "one x-height of offset");
    }

    /// <summary>
    /// A character that draws nothing lends no face to the line it sits in: the line stands on the
    /// metrics of the font that draws the text around it. Chrome is measured this way - a word joiner
    /// between two Ahem squares leaves the line fifty of its own height and its baseline at the square's
    /// own ascent - because an invisible character has no shape of its own to ask a font for, and the
    /// face borrowed to carry it has a taller one.
    /// </summary>
    [Test]
    [TestCase(0x2060)]
    [TestCase(0x200B)]
    [TestCase(0x200D)]
    [TestCase(0xFEFF)]
    public async Task AnInvisibleCharacterDoesNotMoveTheLineItSitsIn(Int32 codepoint)
    {
        using var view = CreateView(new SkiaTextMeasurer());

        // Fifty of its own height is what the face says the line is: Ahem stands at eight tenths above
        // the baseline and two tenths below, so a line of it at fifty is fifty tall.
        await view.SetHtml(
            "<html><body style='margin:0'><div id='x' style='font:50px Ahem;width:200px'>X" +
            Char.ConvertFromUtf32(codepoint) + "X</div></body></html>");

        Assert.That(Box(view).Height, Is.EqualTo(50), "the invisible character brings no face to the line");
    }

    /// <summary>
    /// A character the font has to borrow a face for does raise the line, which is the other half of the
    /// rule: it is only the character that draws nothing that leaves the metrics alone.
    /// </summary>
    [Test]
    public async Task ABorrowedCharacterRaisesTheLineItStandsOn()
    {
        using var measurer = new SkiaTextMeasurer();
        var style = new FontStyle("Ahem", 50);
        var ideograph = Char.ConvertFromUtf32(0x4E2D);

        var borrowed = measurer.GetRunMetrics("X" + ideograph + "X", style);
        var own = measurer.GetMetrics(style);

        Assert.That(
            measurer.MeasureWidth(ideograph, style),
            Is.GreaterThan(0),
            "a machine that draws nothing here cannot show which face was borrowed");

        Assert.That(borrowed.Ascent, Is.GreaterThan(own.Ascent), "the face that draws it decides the line");
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

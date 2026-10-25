using Hutu.Rendering.Layout;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Hutu.Tests;

/// <summary>
/// Covers <c>word-spacing</c>: the room it adds where two words meet, how many times it is added when a
/// run of spaces was written there, and the separators it leaves to the font - the ideographic space among
/// them.
/// </summary>
/// <remarks>
/// The fake measurer gives every character five pixels, so the space of a written document is five and a
/// style that asks for four more makes nine. A width is the layout's own arithmetic under it, which is
/// what makes these assertions independent of the machine's fonts.
/// </remarks>
[TestFixture]
public sealed class WordSpacingTests
{
    [Test]
    public async Task RoomIsAddedWhereTwoWordsMeet()
    {
        using var view = CreateView();

        await view.SetHtml(Page("<p style='word-spacing:4px'>ab cd</p>"));

        Assert.That(
            Text(view, "ab cd").BorderBox.Width,
            Is.EqualTo(29),
            "four characters of five, the space of five, and the room of four");
    }

    [Test]
    public async Task APercentageIsAShareOfTheFontSize()
    {
        using var view = CreateView();

        await view.SetHtml(Page("<p style='font-size:20px;word-spacing:50%'>ab cd</p>"));

        Assert.That(
            Text(view, "ab cd").BorderBox.Width,
            Is.EqualTo(35),
            "the five pixels of the space plus ten, half of the twenty-pixel font");
    }

    [Test]
    public async Task AProductInCalcIsTheRoomItsFactorsComeTo()
    {
        using var view = CreateView();

        // `calc(0.5*40px)` is twenty pixels, which the layout reads as readily as the plain length;
        // AngleSharp's own fold left the product at zero, so the words came out unspaced.
        await view.SetHtml(Page("<p style='word-spacing:calc(0.5*40px)'>ab cd</p>"));

        Assert.That(
            Text(view, "ab cd").BorderBox.Width,
            Is.EqualTo(45),
            "the five characters and the space of five, the space and twenty more");
    }

    [Test]
    public async Task NormalLeavesTheWordsAsTheyWere()
    {
        using var view = CreateView();

        await view.SetHtml(Page("<p>ab cd</p>"));

        Assert.That(Text(view, "ab cd").BorderBox.Width, Is.EqualTo(25), "the initial value adds nothing");
    }

    [Test]
    public async Task ACollapsedRunOfSpacesIsChargedOnce()
    {
        using var view = CreateView();

        // Three spaces written between two words are drawn as one, and the room is added to the separator
        // that is drawn rather than to each of the three that were written.
        await view.SetHtml(Page("<p style='word-spacing:4px'>a   b</p>"));

        Assert.That(Text(view, "a b").BorderBox.Width, Is.EqualTo(19));
    }

    [Test]
    public async Task APreformattedRunIsChargedForEachSpaceItDraws()
    {
        using var view = CreateView();

        // Preformatted text keeps both spaces, and both of them separate the words the line is made of.
        await view.SetHtml(Page("<p style='white-space:pre;word-spacing:4px'>a  b</p>"));

        Assert.That(Text(view, "a  b").BorderBox.Width, Is.EqualTo(28), "two spaces of nine each");
    }

    [Test]
    public async Task EverySeparatorTheTextIsLeftWithTakesTheRoom()
    {
        using var view = CreateView();

        // The room is added at every separator the white space processing leaves behind, with no
        // exception for one that opens the text (CSS Text 3 §7.1). Chromium, measured along a
        // preformatted line, keeps a leading space and gives it the room; along a line of ordinary text
        // it pays nothing at the head, because there is nothing there - the space was removed, which is
        // the white-space rule rather than this property's.
        await view.SetHtml(Page("<p style='white-space:pre;word-spacing:4px'> ab</p>"));

        Assert.That(Text(view, " ab").BorderBox.Width, Is.EqualTo(19), "the space of five and the room of four");
    }

    [Test]
    public async Task AnOrdinaryLineBeginsWithoutTheSpaceBeforeIt()
    {
        using var view = CreateView();

        await view.SetHtml(Page("<p style='word-spacing:4px'> ab</p>"));

        Assert.That(Text(view, "ab").BorderBox.Width, Is.EqualTo(10), "the space at the head of the line is gone, room and all");
        Assert.That(Text(view, "ab").BorderBox.X, Is.EqualTo(0), "so the line starts with the first word");
    }

    [Test]
    public async Task APreformattedLineKeepsTheSpaceItBeginsWith()
    {
        using var view = CreateView();

        await view.SetHtml(Page("<p style='white-space:pre-wrap;word-spacing:4px'> ab</p>"));

        Assert.That(Text(view, " ab").BorderBox.Width, Is.EqualTo(19), "a mode that keeps its spaces keeps them at the head too");
    }

    [Test]
    public async Task ALineAfterAPreservedBreakDropsItsCollapsibleSpace()
    {
        using var view = CreateView();

        await view.SetHtml(Page("<p style='white-space:pre-line'>a" + (Char)10 + " b</p>"));

        Assert.That(Lines(view), Is.EqualTo(2), "the preserved break starts a line of its own");
        Assert.That(Text(view, "b").BorderBox.X, Is.EqualTo(0), "and the space after it collapses away");
    }

    [Test]
    public async Task ANoBreakSpaceIsAWordSeparatorToo()
    {
        using var view = CreateView();

        // The no-break space is named among the word separators by the specification, and Chromium,
        // measured, adds the room to it exactly as to a space - which it also does when the line is
        // justified, where the room of this one is shared out with the spaces around it.
        await view.SetHtml(Page("<p style='word-spacing:4px'>a" + (Char)0x00A0 + "b</p>"));

        Assert.That(
            Text(view, "a" + (Char)0x00A0 + "b").BorderBox.Width,
            Is.EqualTo(19),
            "three characters of five and the room of four");
    }

    [Test]
    public async Task ANegativeRoomNarrowsTheGap()
    {
        using var view = CreateView();

        await view.SetHtml(Page("<p style='word-spacing:-2px'>ab cd</p>"));

        Assert.That(Text(view, "ab cd").BorderBox.Width, Is.EqualTo(23));
    }

    [Test]
    public async Task TheIdeographicSpaceIsLeftToTheFont()
    {
        using var view = CreateView();

        // U+3000 reads as a gap and is not a word separator: word-spacing says the effect on the other
        // space characters is the font's, and Chromium, measured, adds nothing to this one.
        await view.SetHtml(Page("<p style='word-spacing:4px'>a" + (Char)0x3000 + "b</p>"));

        Assert.That(
            Text(view, "a" + (Char)0x3000 + "b").BorderBox.Width,
            Is.EqualTo(15),
            "three characters of five, and no room added");
    }

    [Test]
    public async Task ThePropertyTravelsDownToTheTextItInherits()
    {
        using var view = CreateView();

        await view.SetHtml(Page("<div style='word-spacing:4px'><span>ab cd</span></div>"));

        Assert.That(Text(view, "ab cd").BorderBox.Width, Is.EqualTo(29));
    }

    [Test]
    public async Task TheRoomCountsTowardWhereTheLineComesApart()
    {
        // Two words of ten and a separator of five come to twenty-five, which is exactly the width the
        // paragraph is given. The four added to the separator is what sends the second word below.
        using var spaced = CreateView();
        await spaced.SetHtml(Page("<p style='width:25px;word-spacing:4px'>ab cd</p>"));

        using var plain = CreateView();
        await plain.SetHtml(Page("<p style='width:25px'>ab cd</p>"));

        Assert.That(Lines(spaced), Is.EqualTo(2), "the room made the line too long");
        Assert.That(Lines(plain), Is.EqualTo(1), "and without it the words stand together");
    }

    [Test]
    public void TheWordsThemselvesStandWhereTheRoomPutThem()
    {
        // The room has to reach the glyphs and not only the box around them, or the text is drawn short
        // of where the line says it ends. Chromium, measured on this page with Ahem at twenty, sets the
        // word after the space at 66 with the room asked for and at 60 without it.
        Assert.That(Ink("ab cd", String.Empty), Is.EqualTo(new[] { 0, 60 }));
        Assert.That(Ink("ab cd", "word-spacing:6px;"), Is.EqualTo(new[] { 0, 66 }));
    }

    [Test]
    public void ANoBreakSpaceCarriesTheRoomIntoTheGlyphsAsWell()
    {
        // The separator the specification names and this engine had left out. Chromium, measured on the
        // same page, sets the three characters at 0, 46 and 92: both the no-break space and the space
        // between the words are given the six.
        Assert.That(Ink("a" + (Char)0x00A0 + "b c", "word-spacing:6px;"), Is.EqualTo(new[] { 0, 46, 92 }));
    }

    /// <summary>Draws the content for real and reports where along its first line the ink begins.</summary>
    private static List<Int32> Ink(String content, String style)
    {
        using var measurer = new Hutu.Skia.SkiaTextMeasurer();
        using var view = new HutuViewInternal(measurer);
        view.SetViewportSize(300, 300);

        view.SetHtml(
            "<html><body style='margin:0'><div style='margin:0;font-family:Ahem;font-size:20px;" +
            "line-height:1;width:200px;" + style + "'>" + content + "</div></body></html>")
            .GetAwaiter().GetResult();

        var paint = view.Paint!;
        using var surface = SkiaSharp.SKSurface.Create(
            new SkiaSharp.SKImageInfo((Int32)paint.Bounds.Width, (Int32)paint.Bounds.Height));
        surface.Canvas.Clear(SkiaSharp.SKColors.White);
        new Hutu.Skia.SkiaPaintExecutor(measurer).Execute(paint, surface.Canvas);

        using var image = surface.Snapshot();
        using var bitmap = SkiaSharp.SKBitmap.FromImage(image);

        var starts = new List<Int32>();
        var inked = false;

        for (var x = 0; x < Math.Min(200, bitmap.Width); x++)
        {
            var on = false;

            for (var y = 0; y < Math.Min(20, bitmap.Height); y++)
            {
                if (bitmap.GetPixel(x, y) != SkiaSharp.SKColors.White)
                {
                    on = true;
                    break;
                }
            }

            if (on && !inked)
            {
                starts.Add(x);
            }

            inked = on;
        }

        return starts;
    }

    private static String Page(String content) =>
        "<html><body style='margin:0;font-size:10px;line-height:2'>" + content + "</body></html>";

    private static HutuViewInternal CreateView(Int32 viewportWidth = 600)
    {
        var view = new HutuViewInternal(new FakeTextMeasurer());
        view.SetViewportSize(viewportWidth, 600);
        return view;
    }

    private static Int32 Lines(HutuViewInternal view) =>
        Walk(view).Count(node => node.Kind == LayoutKind.Line);

    private static LayoutBox Text(HutuViewInternal view, String text) =>
        Walk(view).Single(node => node.Kind == LayoutKind.Text && node.Text == text);

    private static IEnumerable<LayoutBox> Walk(HutuViewInternal view)
    {
        var root = view.Layout?.Root;

        return root is null ? Enumerable.Empty<LayoutBox>() : Walk(root);
    }

    private static IEnumerable<LayoutBox> Walk(LayoutBox node)
    {
        yield return node;

        foreach (var child in node.Children)
        {
            foreach (var descendant in Walk(child))
            {
                yield return descendant;
            }
        }
    }
}

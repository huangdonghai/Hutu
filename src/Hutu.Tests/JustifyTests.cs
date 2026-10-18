using Hutu.Rendering.Layout;
using Hutu.Skia;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Hutu.Tests;

/// <summary>
/// Covers <c>text-align: justify</c>: the room a line was given but did not use is spread across the
/// places between its words, and nowhere else - not the line that ends the block, not one the page broke
/// by force, not the white space a line trails into at its end.
/// </summary>
/// <remarks>
/// The fake measurer gives five pixels a character, so a line given a hundred of them and holding three
/// words of ten has sixty to spread over its two spaces.
/// </remarks>
[TestFixture]
public sealed class JustifyTests
{
    [Test]
    public async Task TheRoomGoesIntoTheWidthTheRunTakes()
    {
        using var view = CreateView();

        // "aa bb cc dd" is fifty-five of the measurer's pixels, so the line given sixty-five breaks after
        // three of its four words and is left with ten to spread over the two places between them.
        await view.SetHtml(Page("width:65px;text-align:justify", "aa bb cc dd ee"));

        Assert.That(Text(view, "aa bb cc dd").BorderBox.Width, Is.EqualTo(65), "fifty-five measured, ten spread");
        Assert.That(Text(view, "ee").BorderBox.X, Is.EqualTo(0), "and the line that ends the block is not");
    }

    [Test]
    public async Task WordsSpreadAcrossTheRoomTheLineWasGiven()
    {
        using var view = CreateView();

        // Set apart by element so each word keeps a box of its own, which is what shows where the room
        // went: two and a half after each of the two places on the line that broke.
        await view.SetHtml(Page(
            "width:45px;text-align:justify",
            "<span id='a'>aa</span> <span id='b'>bb</span> <span id='c'>cc</span> <span id='d'>dd</span>"));

        // Ten pixels of room over two places: five after the first space and five after the second, so
        // each word stands seven and a half further along than the space before it left it.
        Assert.That(Left(view, "a"), Is.EqualTo(0));
        Assert.That(Left(view, "b"), Is.EqualTo(17.5).Within(0.001), "ten, the space, and its share");
        Assert.That(Left(view, "c"), Is.EqualTo(35).Within(0.001), "and the same again");
        Assert.That(Right(view, "c"), Is.EqualTo(45).Within(0.001), "the broken line reaches its edge");
        Assert.That(Left(view, "d"), Is.EqualTo(0), "and the line that ends the block starts at its own");
    }

    [Test]
    public async Task AnOddShareStillLandsExactlyOnTheEdge()
    {
        using var view = CreateView();

        // Ten pixels of room between three places is no whole number each. The shares fall on a running
        // total, so the fractions move on to the next place and the last word of the line stops exactly
        // on the edge the line was given.
        await view.SetHtml(Page(
            "width:65px;text-align:justify",
            "<span id='a'>aa</span> <span id='b'>bb</span> <span id='c'>cc</span>" +
            " <span id='d'>dd</span> <span id='e'>ee</span>"));

        Assert.That(Right(view, "d"), Is.EqualTo(65).Within(0.001));
        Assert.That(
            Left(view, "e"),
            Is.EqualTo(0),
            "and the word that fell to the line that ends the block keeps the edge");
        Assert.That(
            Left(view, "d") - Right(view, "c"),
            Is.EqualTo(5 + (10.0 / 3)).Within(0.001),
            "the place before it took its share as well as its own advance");
    }

    [Test]
    public async Task TheLastLineKeepsItsRaggedEdge()
    {
        using var view = CreateView();

        // The first line is broken by the room and justified; the second is where the content ends, and
        // a line ending a block is not spread however much room it left.
        await view.SetHtml(Page(
            "width:45px;text-align:justify",
            "<span id='a'>aa</span> <span id='b'>bb</span> <span id='c'>cc</span> <span id='d'>dd</span>"));

        Assert.That(Lines(view).Count(), Is.EqualTo(2));
        Assert.That(Right(view, "c"), Is.EqualTo(45).Within(0.001), "the broken line reaches its edge");
        Assert.That(Left(view, "d"), Is.EqualTo(0), "and the last one starts at the edge it was given");
    }

    [Test]
    public async Task ALineThePageBrokeByForceKeepsItsRaggedEdge()
    {
        using var view = CreateView();

        // A break the author wrote ends a line, and the line before it keeps its edge ragged rather than
        // reaching out for the room it was given.
        await view.SetHtml(Page(
            "width:45px;text-align:justify",
            "<span id='a'>aa</span> <span id='b'>bb</span><br><span id='c'>cc</span> <span id='d'>dd</span>"));

        Assert.That(Right(view, "b"), Is.EqualTo(25), "not spread across the room");
        Assert.That(Left(view, "c"), Is.EqualTo(0));
    }

    [Test]
    public async Task ALineThatTrailsIntoWhiteSpaceJustifiesOnlyItsInnerPlaces()
    {
        using var view = CreateView();

        // Preformatted text keeps the space a line ends with, and that one is not between two words: it
        // holds its own five pixels, hangs past the edge the line was given, and is offered none of the
        // room - which goes whole into the place inside the line. Had the trailing one counted as a place
        // too, each would have taken five pixels and "bb" would stand twenty along rather than twenty-five.
        await view.SetHtml(Page(
            "width:35px;text-align:justify;white-space:pre-wrap",
            "<span id='a'>aa</span> <span id='b'>bb</span> <span id='c'>cc</span>"));

        Assert.That(Left(view, "b"), Is.EqualTo(25).Within(0.001));
        Assert.That(Right(view, "b"), Is.EqualTo(35).Within(0.001), "the word ends on the edge");
        Assert.That(Left(view, "c"), Is.EqualTo(0), "the next line begins where any other does");
    }

    [Test]
    public async Task NothingIsSpreadUnlessThePageAsksForIt()
    {
        using var view = CreateView();

        await view.SetHtml(Page(
            "width:65px",
            "<span id='a'>aa</span> <span id='b'>bb</span> <span id='c'>cc</span> <span id='d'>dd</span>"));

        Assert.That(Right(view, "c"), Is.EqualTo(40), "every space kept its own advance");
    }

    [Test]
    public async Task ALineWithNothingBetweenItsWordsIsLeftAsItStands()
    {
        using var view = CreateView();

        // The room has to go between words or nowhere: a run of letters the engine cannot come apart is
        // not spaced out letter by letter, and the line overflows as one that was never justified.
        await view.SetHtml(Page("width:20px;text-align:justify;white-space:nowrap", "aaaaaaaaaa bb"));

        Assert.That(Text(view, "aaaaaaaaaa bb").BorderBox.Width, Is.EqualTo(65), "nothing to spread over");
    }

    [Test]
    public async Task TheGlyphsThemselvesStandOutWhereTheRoomWent()
    {
        // The last test of the same page against the real shaper, where nothing is known beforehand:
        // the ink of a justified line reaches as far right as the line it was measured into, and the
        // ink of the same content unjustified stops with the words themselves. The two differ by just
        // as much as the two runs' own widths do - which is what says the room went between the glyphs
        // rather than only into the box around them.
        var content = string.Join(" ", Enumerable.Repeat("justification", 12));

        var spread = await Ink("text-align:justify;", content);
        var plain = await Ink(String.Empty, content);

        Assert.That(
            spread.Right - plain.Right,
            Is.EqualTo(spread.Width - plain.Width).Within(2.0),
            "the ink moved out by the room the line was given");
        Assert.That(
            spread.Width - plain.Width,
            Is.GreaterThan(40),
            "the line had real room to spread over - without it the equality above proves nothing");
    }

    [Test]
    public async Task ALineOfChineseSharesItsRoomBetweenItsCharacters()
    {
        // A line of Chinese has no word separators to put its room into, and the initial value of
        // text-justify says the seams between its characters take it instead (CSS Text 3 §3.3.1).
        // Chromium, measured on this page with five ideographs of twenty on a line of a hundred and
        // ten, sets them at 0, 23, 45, 68, 90: the four seams sharing the ten units at two and a half
        // each. Ahem carries no CJK glyph, so the characters go to a face that does.
        var ideographs = string.Concat(Enumerable.Repeat(Char.ConvertFromUtf32(0x6F22), 11));

        var plain = await Ink("font-family:Ahem;font-size:20px;width:110px;", ideographs);
        var spread = await Ink("font-family:Ahem;font-size:20px;width:110px;text-align:justify;", ideographs);
        var words = await Ink(
            "font-family:Ahem;font-size:20px;width:110px;text-align:justify;text-justify:inter-word;",
            ideographs);

        Assert.That(plain.Right, Is.LessThan(105), "five cells of twenty stop short of the edge");
        Assert.That(spread.Right, Is.GreaterThanOrEqualTo(105), "the seams carry the line to it");
        Assert.That(words.Right, Is.LessThan(105), "and a line asked to expand only between words does not move");
    }

    [Test]
    public async Task AFragmentThatCarriesNoTextIsNotAPlaceToPayInto()
    {
        using var view = CreateView();

        // A borrowed page (gecko/text/justification-2d) sets an empty inline-block on either side of a
        // figure space in a justified line. The seam between two pieces of the line asks what the last
        // character of one and the first of the other are, and a box that carries no text can answer
        // neither - which is the question that used to walk off the end of the string.
        await view.SetHtml(Page(
            "width:40px;text-align:justify",
            "<span style='display:inline-block;width:5px;height:5px'></span>ab cd" +
            "<span style='display:inline-block;width:5px;height:5px'></span><br>next"));

        Assert.That(Lines(view).Count(), Is.GreaterThan(1), "the page laid out rather than threw");

        Assert.That(Text(view, "ab cd").BorderBox.Width, Is.GreaterThan(0), "and the words still stand on it");
    }

    /// <summary>Draws the first line of the content for real and reports how far its ink reaches.</summary>
    private static async Task<(Double Right, Double Width)> Ink(String style, String content)
    {
        using var measurer = new SkiaTextMeasurer();
        using var view = new HutuViewInternal(measurer);
        view.SetViewportSize(600, 400);

        await view.SetHtml(
            "<html><body style='margin:0'><div style='margin:0;font-family:Segoe UI;font-size:20px;" +
            "width:400px;" + style + "white-space:normal'>" + content + "</div></body></html>");

        var line = Lines(view).First();
        var run = Walk(view).First(n => n.Kind == LayoutKind.Text);

        using var surface = SkiaSharp.SKSurface.Create(
            new SkiaSharp.SKImageInfo((Int32)Math.Ceiling(view.Paint!.Bounds.Width), 400));
        surface.Canvas.Clear(SkiaSharp.SKColors.White);
        new SkiaPaintExecutor(measurer).Execute(view.Paint!, surface.Canvas);

        using var image = surface.Snapshot();
        using var bitmap = SkiaSharp.SKBitmap.FromImage(image);

        var top = Math.Max(0, (Int32)Math.Floor(line.BorderBox.Y));
        var bottom = Math.Min(bitmap.Height - 1, (Int32)Math.Ceiling(line.BorderBox.Bottom));
        var right = 0.0;

        for (var y = top; y <= bottom; y++)
        {
            for (var x = bitmap.Width - 1; x > 0; x--)
            {
                if (bitmap.GetPixel(x, y) != SkiaSharp.SKColors.White)
                {
                    right = Math.Max(right, x);
                    break;
                }
            }
        }

        return (right, run.BorderBox.Width);
    }

    private static String Page(String style, String content) =>
        "<html><body style='margin:0'><div style='margin:0;font-size:10px;line-height:1;" + style +
        "'>" + content + "</div></body></html>";

    private static HutuViewInternal CreateView(Int32 viewportWidth = 600)
    {
        var view = new HutuViewInternal(new FakeTextMeasurer());
        view.SetViewportSize(viewportWidth, 600);
        return view;
    }

    private static Double Left(HutuViewInternal view, String id) => Bounds(view, id).X;

    private static Double Right(HutuViewInternal view, String id) => Bounds(view, id).Right;

    private static Rect Bounds(HutuViewInternal view, String id)
    {
        var element = view.Document!.GetElementById(id);

        Assert.That(element, Is.Not.Null, $"the document holds no element with id '{id}'");

        var bounds = view.GetElementBounds(element!);

        Assert.That(bounds, Is.Not.Null, $"the element '{id}' was given no box to measure");

        return bounds!.Value;
    }

    private static LayoutBox Text(HutuViewInternal view, String text) =>
        Walk(view).First(node => node.Kind == LayoutKind.Text && node.Text == text);

    private static IEnumerable<LayoutBox> Lines(HutuViewInternal view) =>
        Walk(view).Where(node => node.Kind == LayoutKind.Line);

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

using Hutu.Rendering.Layout;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Hutu.Tests;

/// <summary>
/// Covers how a ruby pair is laid out: the reading takes no room in the line, and it may hang over the
/// spaces beside its base when the page asks it to (CSS Ruby 1 §3.6 and §5.1).
/// </summary>
/// <remarks>
/// The fake measurer gives every character five pixels and every font an ascent of eight and a descent
/// of two, so the numbers below are the layout's own arithmetic rather than the machine's fonts.
/// </remarks>
[TestFixture]
public sealed class RubyLayoutTests
{
    [Test]
    public async Task AReadingDoesNotMakeTheLineTallerThanItsBase()
    {
        using var view = CreateView();

        // Twenty pixels of line height over a ten-pixel font leaves five pixels of leading above the
        // base's content edge, which is more than the reading's own ten pixels? No - the reading is half
        // the size, so it is five pixels tall and fits in the leading. The line stays twenty.
        await view.SetHtml(Document("<ruby>ab<rt>cd</rt></ruby>"));

        Assert.That(
            Lines(view).Single().BorderBox.Height,
            Is.EqualTo(20),
            "the pair's line is the base's line-height, not that plus the reading");
    }

    [Test]
    public async Task AReadingTallerThanTheLeadingGrowsTheLineByTheDifference()
    {
        using var view = CreateView();

        // A line height of one is ten pixels, which leaves no leading: the ten-pixel reading (eight of
        // ascent and two of descent at half the size) reaches five pixels past the base's content edge,
        // and the line grows by exactly that much rather than by the reading's whole height.
        await view.SetHtml(Document("<ruby>ab<rt>cd</rt></ruby>", lineHeight: "1"));

        Assert.That(Lines(view).Single().BorderBox.Height, Is.EqualTo(15), "5 + 5 + 5");
    }

    [Test]
    public async Task AReadingHangsOverTheSpacesBesideItWhenThePageAsksForSpaces()
    {
        using var view = CreateView();

        // The base is ten pixels wide and the reading fifty, so thirty-five of it wants to past each
        // side. One five-pixel space stands on each side, and those are all the reading may cover: the
        // pair takes 10 + 30 + 30 = ... no: the excess is forty, twenty a side, of which five is covered,
        // so the pair grows by fifteen on each side.
        await view.SetHtml(Document(
            "x <ruby style='ruby-overhang: spaces'>ab<rt>abcdefghij</rt></ruby> y"));

        Assert.That(
            Text(view, " y").BorderBox.X - Text(view, "x ").BorderBox.Right,
            Is.EqualTo(40),
            "the pair is the base plus what the spaces beside it could not cover");
    }

    [Test]
    public async Task AReadingCoversNoTextWhenTheSpaceBesideItRunsOut()
    {
        using var view = CreateView();

        // No spaces stand beside the pair, so nothing may be covered and the pair is as wide as its
        // reading.
        await view.SetHtml(Document("x<ruby style='ruby-overhang: spaces'>ab<rt>abcdefghij</rt></ruby>y"));

        Assert.That(
            Text(view, "y").BorderBox.X - Text(view, "x").BorderBox.Right,
            Is.EqualTo(50),
            "the reading takes its whole width between the letters beside it");
    }

    [Test]
    public async Task AnAutomaticReadingHangsNoMoreThanHalfAnEm()
    {
        using var view = CreateView();

        // What `auto` allows is the user agent's choice; this engine follows Chrome, which lets a
        // reading hang half an em in total past its base - five pixels either side at this size.
        await view.SetHtml(Document("x<ruby>ab<rt>abcdefghij</rt></ruby>y"));

        Assert.That(
            Text(view, "y").BorderBox.X - Text(view, "x").BorderBox.Right,
            Is.EqualTo(45),
            "fifty pixels of reading, less half an em of overhang");
    }

    [Test]
    public async Task AReadingCoversTheMarkStandingPastTheSpaceBesideIt()
    {
        using var view = CreateView();

        // The room beside a pair is gathered outward character by character, not only from the glyph
        // that touches it: a space gives up all five of its pixels and the fullwidth mark behind it the
        // blank half that looks at the pair, so seven and a half are covered each side and the pair
        // grows by the other twelve and a half (CSS Ruby 1 §5.1).
        await view.SetHtml(Document(
            "x〕 <ruby style='ruby-overhang: spaces'>ab<rt>abcdefghij</rt></ruby> 〔y"));

        Assert.That(
            Text(view, " 〔y").BorderBox.X - Text(view, "x〕 ").BorderBox.Right,
            Is.EqualTo(35),
            "the pair is the base plus what the space and the mark beside it could not cover");
    }

    [Test]
    public async Task AReadingCoversNoMarkWhoseBlankTheLineHasAlreadyTaken()
    {
        using var view = CreateView();

        // A mark whose blank half has been trimmed by `text-spacing-trim` has none left to give, and
        // the reading stops there rather than reaching on to the marks behind it: only the space
        // touches the pair, so five are covered a side and the pair grows by fifteen.
        await view.SetHtml(Document(
            "<span style='text-spacing-trim:trim-all'>〕〕 <ruby style='ruby-overhang: spaces'>" +
            "ab<rt>abcdefghij</rt></ruby> 〔〔</span>"));

        Assert.That(
            Text(view, " 〔〔").BorderBox.X - Text(view, "〕〕 ").BorderBox.Right,
            Is.EqualTo(40),
            "a trimmed mark gives the reading nothing to cover");
    }

    [Test]
    public async Task AReadingTakesTheWholeRoomATabRunsTheLineOn()
    {
        using var view = CreateView();

        // A tab is no glyph with an advance of its own: it runs the line on to the next stop, and that
        // distance is what the reading may cover. Two letters bring the line to ten and the stop stands
        // at twenty, so ten pixels of tab are covered on each side and the pair grows by the other ten.
        await view.SetHtml(Document(
            "<span style='white-space:pre;tab-size:20px'>ab\t<ruby style='ruby-overhang: spaces'>" +
            "cd<rt>abcdefghij</rt></ruby>\tef</span>"));

        // The line stands at twenty where the pair begins, and the base is pushed on by the ten pixels
        // the tab could not cover. Measuring the tab by its glyph instead would have left room of five
        // and put the base at twenty-five.
        Assert.That(
            Text(view, "cd").BorderBox.X,
            Is.EqualTo(30),
            "the pair grows only by what the tab's stop did not cover");
    }

    private static String Document(String content, String lineHeight = "2", Int32 width = 0) =>
        "<html><body style='margin:0'><p style='margin:0;font-size:10px;line-height:" + lineHeight +
        (width > 0 ? ";width:" + width + "px" : String.Empty) +
        "'>" + content + "</p></body></html>";

    private static HutuViewInternal CreateView(Int32 viewportWidth = 600)
    {
        var view = new HutuViewInternal(new FakeTextMeasurer());
        view.SetViewportSize(viewportWidth, 600);
        return view;
    }

    private static LayoutBox Text(HutuViewInternal view, String text) =>
        Walk(view).SingleOrDefault(node => node.Kind == LayoutKind.Text && node.Text == text)
            ?? throw new AssertionException($"The layout holds no text box for '{text}'.");

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

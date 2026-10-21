using Hutu.Rendering.Layout;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Hutu.Tests;

/// <summary>
/// Covers where the tailorable line-breaking properties put their opportunities:
/// <c>overflow-wrap</c> cuts a word that no line can hold, <c>word-break: break-all</c> and
/// <c>line-break: anywhere</c> cut wherever the line gives out, and only the values that speak of white
/// space give a break before the first character of a preserved run (CSS Text 3 5.4-5.6).
/// </summary>
/// <remarks>
/// The stand-in measurer gives every character five pixels and the boxes are twenty wide, so a line holds
/// four characters. Every placement below is the one Chrome gives the same numbers, read off the
/// character boxes of a page set in one uniform advance.
/// </remarks>
[TestFixture]
public sealed class TailorableBreakTests
{
    [Test]
    public async Task AWordNoLineCanHoldIsCutWhereTheRoomGivesOut()
    {
        using var view = await Render(
            "<style>div { width: 20px; overflow-wrap: break-word }</style><div id='a'>A BCDEFGH</div>");

        Assert.Multiple(() =>
        {
            // The word is begun on a line of its own and only then cut: the room the space left on the
            // line above is not filled with the end of it, which is what makes it the last resort.
            Assert.That(Text(view, "A").BorderBox.Y, Is.EqualTo(0), "the first word stands where it was written");
            Assert.That(Text(view, "BCDE").BorderBox.Y, Is.EqualTo(10), "the long word begins the line below");
            Assert.That(Text(view, "FGH").BorderBox.Y, Is.EqualTo(20), "and is cut where the room gives out");
        });
    }

    [Test]
    public async Task AWordThatFitsOnALineOfItsOwnIsNotCut()
    {
        using var view = await Render(
            "<style>div { width: 20px; overflow-wrap: break-word }</style><div id='a'>AB CDEF</div>");

        Assert.Multiple(() =>
        {
            Assert.That(Text(view, "CDEF").BorderBox.Y, Is.EqualTo(10), "there were earlier opportunities to take");
            Assert.That(Text(view, "CDEF").BorderBox.Width, Is.EqualTo(20), "and the word is whole on its own line");
        });
    }

    [Test]
    public async Task AWordBrokenAnywhereFillsTheLineItStandsOn()
    {
        using var view = await Render(
            "<style>div { width: 20px; word-break: break-all }</style><div id='a'>A BCDEF</div>");

        Assert.Multiple(() =>
        {
            Assert.That(Text(view, "A BC").BorderBox.Width, Is.EqualTo(20), "the line is filled to its end edge");
            Assert.That(Text(view, "DEF").BorderBox.Y, Is.EqualTo(10), "and the rest of the word carries on below");
        });
    }

    [Test]
    public async Task ALineBreakAnywhereGivesTheRoomBeforeAPreservedSpace()
    {
        using var view = await Render(
            "<style>div { width: 20px; white-space: break-spaces; line-break: anywhere }</style>" +
            "<div id='a'>ABCD </div>");

        Assert.Multiple(() =>
        {
            Assert.That(Text(view, "ABCD").BorderBox.Width, Is.EqualTo(20), "the line ends where it was given to end");
            Assert.That(Text(view, " ").BorderBox.Y, Is.EqualTo(10), "the space the word would have overflowed falls");
        });
    }

    [Test]
    public async Task ABreakAllLeavesTheSpaceToTheWordItCameFrom()
    {
        using var view = await Render(
            "<style>div { width: 20px; white-space: break-spaces; word-break: break-all }</style>" +
            "<div id='a'>ABCD </div>");

        Assert.That(Text(view, "ABCD ").BorderBox.Width, Is.EqualTo(25), "the space overflows with the word");
    }

    [Test]
    public async Task TheNarrowestSizeCountsTheBreaksTheValueIsWorth()
    {
        using var view = await Render(
            "<style>div { width: min-content }" +
            "#bw { overflow-wrap: break-word }" +
            "#aw { overflow-wrap: anywhere }" +
            "#ba { word-break: break-all }</style>" +
            "<div id='bw'>ABCDEFGH</div><div id='aw'>ABCDEFGH</div><div id='ba'>ABCDEFGH</div>");

        Assert.Multiple(() =>
        {
            // `break-word` cuts only when the line has nowhere else to go, so it leaves the content as
            // wide as its longest word; the two values that give the cut as an opportunity are measured
            // with it, and go down to one character (CSS Text 3 5.5, 5.6).
            Assert.That(Find(view, "bw").BorderBox.Width, Is.EqualTo(40), "the last resort is not an offer");
            Assert.That(Find(view, "aw").BorderBox.Width, Is.EqualTo(5), "anywhere is");
            Assert.That(Find(view, "ba").BorderBox.Width, Is.EqualTo(5), "and so is break-all");
        });
    }

    [Test]
    public async Task TheNarrowestSizeKeepsAPreservedSpaceWithWhatCameBeforeIt()
    {
        using var view = await Render(
            "<style>div { width: min-content; white-space: break-spaces }" +
            "#ba { word-break: break-all }" +
            "#aw { overflow-wrap: anywhere }</style>" +
            "<div id='ba'>A BB C</div><div id='aw'>A BB C</div>");

        Assert.Multiple(() =>
        {
            Assert.That(Find(view, "ba").BorderBox.Width, Is.EqualTo(10), "two characters: the space and the word before it");
            Assert.That(Find(view, "aw").BorderBox.Width, Is.EqualTo(5), "one, where the value speaks of white space too");
        });
    }

    [Test]
    public async Task AColumnCutsItsWordAtTheSameRoom()
    {
        using var view = await Render(
            "<style>div { writing-mode: vertical-rl; height: 20px; word-break: break-all }</style>" +
            "<div id='a'>ABCDEFGH</div>");

        var first = Text(view, "ABCD");
        var second = Text(view, "EFGH");

        Assert.Multiple(() =>
        {
            Assert.That(first.BorderBox.Height, Is.EqualTo(20), "the column is filled to its end edge");
            Assert.That(second.BorderBox.X, Is.LessThan(first.BorderBox.X), "and the rest runs on in the next");
        });
    }

    [Test]
    public async Task ThePropertiesTravelDownTheTreeAsInheritedOnes()
    {
        using var view = await Render(
            "<style>div { overflow-wrap: anywhere; line-break: anywhere; word-break: break-all }" +
            "span { color: green }</style><div><span id='a'>ABCDEFGH</span></div>");

        var style = Find(view, "a").Source.Style;

        Assert.Multiple(() =>
        {
            Assert.That(style.OverflowWrap, Is.EqualTo("anywhere"), "the modern spelling reached the box");
            Assert.That(style.LineBreak, Is.EqualTo("anywhere"), "and the line-break value with it");
            Assert.That(style.WordBreak, Is.EqualTo("break-all"), "and the word-break value");
        });
    }

    [Test]
    public async Task TheLegacySpellingOfOverflowWrapStandsWhereTheModernOneIsSilent()
    {
        using var view = await Render(
            "<style>div { word-wrap: anywhere } #b { word-wrap: anywhere; overflow-wrap: normal }</style>" +
            "<div id='a'>ABCDEFGH</div><div id='b'>ABCDEFGH</div>");

        Assert.Multiple(() =>
        {
            Assert.That(Find(view, "a").Source.Style.OverflowWrap, Is.EqualTo("anywhere"), "the legacy name is read");
            Assert.That(Find(view, "b").Source.Style.OverflowWrap, Is.EqualTo("normal"), "and the modern name wins over it");
        });
    }

    [Test]
    public async Task ABreakAllStopsAtThePunctuationItIsGiven()
    {
        using var view = await Render(
            "<style>div { width: min-content } #ba { word-break: break-all } " +
            "#aw { line-break: anywhere }</style>" +
            "<div id='ba'>AB., CD</div><div id='aw'>AB., CD</div>");

        Assert.Multiple(() =>
        {
            // `break-all` puts its opportunity between two letters and says of itself that it leaves the
            // rules about punctuation standing, so the comma keeps the piece it was written on (CSS Text 3
            // §5.6). Chrome measures the same content at three characters under one and one under the other.
            Assert.That(Find(view, "ba").BorderBox.Width, Is.EqualTo(15), "the letter, the stop and the comma");
            Assert.That(Find(view, "aw").BorderBox.Width, Is.EqualTo(5), "anywhere sets that rule aside too");
        });
    }

    [Test]
    public async Task ALegacyBreakWordBreaksTheWordAndIsCountedForTheSize()
    {
        using var view = await Render(
            "<style>div { width: min-content; word-break: break-word }</style><div id='a'>ABCDEFGH</div>");

        Assert.That(Find(view, "a").BorderBox.Width, Is.EqualTo(5), "the legacy spelling counts like anywhere");
    }

    [Test]
    public async Task ALegacyBreakWordNeedsNoOverflowWrapToCut()
    {
        using var view = await Render(
            "<style>div { width: 20px; word-break: break-word }</style><div id='a'>ABCDEFGH</div>");

        Assert.Multiple(() =>
        {
            Assert.That(Text(view, "ABCD").BorderBox.Y, Is.EqualTo(0), "the word is cut on the line it stands on");
            Assert.That(Text(view, "EFGH").BorderBox.Y, Is.EqualTo(10), "and the rest of it carries on below");
        });
    }

    [Test]
    public async Task APieceBroughtFromAnotherElementKeepsItsNeighbour()
    {
        using var view = await Render(
            "<style>div { width: min-content; word-break: break-all }</style>" +
            "<div id='a'><span>X</span><span>.</span></div>");

        // The stop is a non-starter whatever element the two pieces were written on, and the narrowest
        // size has no answer for a boundary the line itself has not yet met.
        Assert.That(Find(view, "a").BorderBox.Width, Is.EqualTo(10), "the two stand as one piece");
    }

    [Test]
    public async Task CharactersKeptWholeRunOnTogether()
    {
        using var view = await Render(
            "<style>div { width: 15px; word-break: keep-all }</style><div>汉字符文</div>");

        var lines = Texts(view).ToList();

        // The opportunity keep-all sets aside is the implicit one between two of the letter units, which
        // is what a run of CJK characters is made of: the run becomes one word, and a word no line can
        // hold is not cut by this property (CSS Text 3 §5.6).
        Assert.That(lines, Has.Count.EqualTo(1), "the run is one word");
        Assert.That(lines[0].Text, Is.EqualTo("汉字符文"), "and it stands whole");
        Assert.That(lines[0].BorderBox.Width, Is.EqualTo(20), "overflowing the fifteen it was given");
    }

    [Test]
    public async Task KeepingWordsWholeStillBreaksWhereASpaceStands()
    {
        using var view = await Render(
            "<style>div { width: 15px; word-break: keep-all }</style><div>汉字 符文 字</div>");

        var lines = Texts(view).ToList();

        Assert.That(lines.Select(box => box.Text).ToList(), Is.EqualTo(new[] { "汉字", "符文", "字" }),
            "the spaces go on offering the breaks, and each word fills a line");
    }

    [Test]
    public async Task AnywhereBreaksAWordThePageAskedToKeepWhole()
    {
        using var view = await Render(
            "<style>div { width: 15px; word-break: keep-all; line-break: anywhere }</style><div>汉字符文</div>");

        var lines = Texts(view).ToList();

        // `anywhere` is the one line-breaking setting keep-all bows to: it sets the rules themselves
        // aside, so the breaks it gives stand even inside a word the page asked to keep whole.
        Assert.That(lines.Select(box => box.Text).ToList(), Is.EqualTo(new[] { "汉字符", "文" }),
            "the run is cut where the room gives out");
    }

    [Test]
    public async Task AWordThatCannotBeCutInsideIsMovedDownEvenUnderBreakAll()
    {
        using var view = await Render(
            "<style>div { width: 20px; word-break: break-all }</style><div>AB CD..</div>");

        var lines = Texts(view).ToList();

        // The licence to break inside a word is only a licence where the word can actually be cut.
        // A period takes no break before itself and none between its own two marks, so "D.." is one
        // piece: filling the line with it would overflow the line rather than end it, and the piece
        // goes down to a line of its own (CSS Text 3 §5.6 read with [UAX14] LB13).
        Assert.That(lines.Select(box => box.Text).ToList(), Is.EqualTo(new[] { "AB C", "D.." }),
            "the line ends where the room gives out and the uncuttable piece follows on the line below");
        Assert.That(lines[0].BorderBox.Width, Is.EqualTo(20), "nothing overflowed the twenty it was given");
    }

    private static async Task<HutuViewInternal> Render(String content)
    {
        var view = new HutuViewInternal(new FakeTextMeasurer());
        view.SetViewportSize(400, 600);
        await view.SetHtml(
            "<html><head><style>div { margin: 0 }</style></head>" +
            "<body style='margin:0;font-size:10px;line-height:10px'>" + content + "</body></html>");
        return view;
    }

    private static LayoutBox Find(HutuViewInternal view, String id) =>
        Walk(view.Layout!.Root).First(box => box.Source.Element?.GetAttribute("id") == id);

    private static LayoutBox Text(HutuViewInternal view, String text) =>
        Texts(view).First(box => box.Text == text);

    private static IEnumerable<LayoutBox> Texts(HutuViewInternal view) =>
        Walk(view.Layout!.Root).Where(box => box.Kind == LayoutKind.Text);

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

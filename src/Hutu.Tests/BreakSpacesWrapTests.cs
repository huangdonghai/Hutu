using Hutu.Rendering.Layout;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Hutu.Tests;

/// <summary>
/// Covers where a line may break inside the white space a mode keeps: <c>break-spaces</c> offers an
/// opportunity after every character of such a run, while every mode offers none before the run's first
/// character, so a space that fits nowhere overflows from where it stands (CSS Text 3 4.1.3).
/// </summary>
/// <remarks>
/// The fake measurer gives every character five pixels and the containers are twenty pixels - or twenty
/// device pixels of column - across, so a line holds four characters and a run of three spaces between
/// two words is the case that splits. The same numbers are read back from Chrome, which puts its spaces
/// in the same places.
/// </remarks>
[TestFixture]
public sealed class BreakSpacesWrapTests
{
    [Test]
    public async Task TheSpacesThatFitStayAndTheRestBeginTheLineBelow()
    {
        using var view = await Render(
            "<style>div { white-space: break-spaces; width: 20px }</style><div>AB   CD</div>");

        Assert.Multiple(() =>
        {
            // Before the run could break between its characters, all three spaces jumped down together
            // and the line above ended short of the room it had been given.
            Assert.That(Text(view, "AB  ").BorderBox.Width, Is.EqualTo(20), "two spaces fit inside the line");
            Assert.That(Text(view, " CD").BorderBox.Y, Is.EqualTo(10), "the third begins the line below");
            Assert.That(Text(view, " CD").BorderBox.Width, Is.EqualTo(15), "and takes room there as it stands");
        });
    }

    [Test]
    public async Task AFirstSpaceNeverMovesOffTheLineItOverflows()
    {
        using var view = await Render(
            "<style>div { white-space: break-spaces; width: 20px }</style><div>ABCD </div>");

        Assert.Multiple(() =>
        {
            Assert.That(Text(view, "ABCD ").BorderBox.Y, Is.EqualTo(0), "the word and its space stand together");
            Assert.That(Text(view, "ABCD ").BorderBox.Width, Is.EqualTo(25), "and the space overflows from there");
        });
    }

    [Test]
    public async Task ASpaceBehindAWordThatFitsNowhereGoesDownWithTheWord()
    {
        using var view = await Render(
            "<style>div { white-space: break-spaces; width: 20px }</style><div>ABCDE FG</div>");

        Assert.Multiple(() =>
        {
            Assert.That(Text(view, "ABCDE ").BorderBox.Width, Is.EqualTo(30), "the long word keeps its space");
            Assert.That(Text(view, "FG").BorderBox.Y, Is.EqualTo(10), "and only the next word falls to the line below");
        });
    }

    [Test]
    public async Task ASecondTabFallsToTheLineBelowTheFirstThatOverflowed()
    {
        using var view = await Render(
            "<style>div { white-space: break-spaces; width: 20px }</style><div>AB\t\tCD</div>");

        Assert.Multiple(() =>
        {
            Assert.That(Text(view, "AB ").BorderBox.Width, Is.EqualTo(40), "the first tab runs past the line");
            Assert.That(Text(view, " ").BorderBox.Y, Is.EqualTo(10), "the second stands alone on the line below");
            Assert.That(Text(view, " ").BorderBox.Width, Is.EqualTo(40), "and runs to the same stop from the line's start");
            Assert.That(Text(view, "CD").BorderBox.Y, Is.EqualTo(20), "and the word follows the room the tab took");
        });
    }

    [Test]
    public async Task APreWrapRunStillMovesAsOnePiece()
    {
        using var view = await Render(
            "<style>div { white-space: pre-wrap; width: 20px }</style><div>AB   CD</div>");

        Assert.Multiple(() =>
        {
            Assert.That(Text(view, "AB").BorderBox.Y, Is.EqualTo(0), "the run stays on the line it was written on");
            Assert.That(Text(view, "AB").BorderBox.Width, Is.EqualTo(10), "hung, so the line stops at the word");
            Assert.That(Text(view, "CD").BorderBox.Y, Is.EqualTo(10), "and the run takes none of the room below");
        });
    }

    [Test]
    public async Task AColumnBreaksItsPreservedSpacesBetweenThemToo()
    {
        using var view = await Render(
            "<style>div { writing-mode: vertical-rl; white-space: break-spaces; height: 20px }</style>" +
            "<div>AB   CD</div>");

        Assert.Multiple(() =>
        {
            Assert.That(Text(view, "AB  ").BorderBox.Height, Is.EqualTo(20), "two spaces fit inside the column");
            Assert.That(Text(view, " CD").BorderBox.X, Is.LessThan(Text(view, "AB  ").BorderBox.X), "the third begins the next");
        });
    }

    [Test]
    public async Task ALineEndsAfterAnIdeographicSpace()
    {
        // The mode's opportunity stands after every other space separator too, not only after the
        // collapsible white space, and the chunker keeps the separator with the word before it - so the
        // piece that has to move is "word, two ideographic spaces" and the break falls between the two.
        using var view = await Render(
            "<style>div { white-space: break-spaces; width: 15px }</style>" +
            "<div>AB\u3000\u3000CD</div>");

        Assert.Multiple(() =>
        {
            Assert.That(Text(view, "AB\u3000").BorderBox.Width, Is.EqualTo(15), "the first space ends the line");
            Assert.That(Text(view, "\u3000CD").BorderBox.Y, Is.EqualTo(10), "the second begins the next, taking room");
        });
    }

    [Test]
    public async Task ANoBreakSpaceHoldsTheLineTogether()
    {
        using var view = await Render(
            "<style>div { white-space: break-spaces; width: 15px }</style>" +
            "<div>AB\u00A0\u00A0CD</div>");

        Assert.Multiple(() =>
        {
            Assert.That(Text(view, "AB\u00A0\u00A0CD").BorderBox.Y, Is.EqualTo(0), "the piece never breaks");
            Assert.That(Text(view, "AB\u00A0\u00A0CD").BorderBox.Width, Is.EqualTo(30), "and overflows from where it stands");
        });
    }

    [Test]
    public async Task AWordGoesDownWithTheSpaceBehindItWhenThatSpaceWouldOverflow()
    {
        using var view = await Render(
            "<style>div { white-space: break-spaces; overflow-wrap: break-word; width: 20px }</style>" +
            "<div>XX X X</div>");

        Assert.Multiple(() =>
        {
            // The space after "XX" ends the first line. The word after it would drag a space that no
            // longer fits, and `break-word` gives no break between a word and the spaces behind it while
            // the line has an opportunity of its own to end at - the one after that space.
            Assert.That(Text(view, "XX ").BorderBox.Width, Is.EqualTo(15), "the first line ends at the space it was given");
            Assert.That(Text(view, "X X").BorderBox.Y, Is.EqualTo(10), "and the word goes down with the space behind it");
            Assert.That(Text(view, "X X").BorderBox.Width, Is.EqualTo(15), "which takes room on the line below");
        });
    }

    [Test]
    public async Task AKanaKeepsTheFirstOfTheSpacesBehindItOnTheLine()
    {
        // The chunker holds a word together with the ideographic spaces behind it, and the mode lets the
        // line end after the first of those: the ask stops at that place rather than asking for the whole
        // piece, so the line is filled to its edge and the rest of the run follows below. Measuring the
        // piece as one unit moved the second kana down and left the line a character short (the same
        // arithmetic Chrome shows on "ああ" with four ideographic spaces in a three-character box).
        using var view = await Render(
            "<style>div { white-space: break-spaces; width: 20px }</style>" +
            "<div>\u3042\u3042\u3000\u3000\u3000\u3000\u3042\u3042</div>");

        Assert.Multiple(() =>
        {
            Assert.That(Text(view, "\u3042\u3042\u3000\u3000").BorderBox.Width, Is.EqualTo(20), "the word and two spaces fill the line");
            Assert.That(Text(view, "\u3000\u3000\u3042\u3042").BorderBox.Y, Is.EqualTo(10), "the rest of the run takes room on the line below");
            Assert.That(Texts(view).Count(), Is.EqualTo(2), "and nothing was left over for a third line");
        });
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

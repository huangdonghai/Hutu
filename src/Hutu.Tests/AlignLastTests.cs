using Hutu.Rendering.Layout;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Hutu.Tests;

/// <summary>
/// Covers <c>text-align-last</c>, which speaks for the line that ends a block and for every line a
/// forced break ended: those take the alignment the property states, and for <c>auto</c> they take
/// <c>text-align</c> unless that asks for justification, in which case they keep their ragged edge at
/// the start (CSS Text 3 §6.3).
/// </summary>
/// <remarks>
/// The fake measurer gives five pixels a character, spaces included. Every page below is forty-five of
/// them wide and holds "aa bb cc dd ee": the first line takes "aa bb cc" at forty pixels and has five
/// to spread over its two places, and the line that ends the block takes "dd ee" at twenty-five with
/// twenty of room and one place of its own.
/// </remarks>
[TestFixture]
public sealed class AlignLastTests
{
    [Test]
    public async Task TheAffectedLineKeepsItsEdgeWhenTheBlockOnlyJustifies()
    {
        using var view = CreateView();

        // The control the rest of this fixture is read against: the broken line reaches its edge and the
        // line that ends the block does not, which is what `auto` comes to.
        await view.SetHtml(Page("width:45px;text-align:justify", Words));

        Assert.That(Right(view, "c"), Is.EqualTo(45).Within(0.001), "the broken line was spread");
        Assert.That(Left(view, "d"), Is.EqualTo(0), "and the line that ends the block was not");
        Assert.That(Right(view, "e"), Is.EqualTo(25));
    }

    [Test]
    public async Task TheLineThatEndsTheBlockIsSpreadWhenTheBlockSaysToSpreadIt()
    {
        using var view = CreateView();

        // The room a line left is spread whenever the line is justified, and `text-align-last: justify`
        // justifies the affected one; the twenty pixels go into the single place inside the line.
        await view.SetHtml(Page("width:45px;text-align:justify;text-align-last:justify", Words));

        Assert.That(Left(view, "d"), Is.EqualTo(0), "the line still begins at its start edge");
        Assert.That(Right(view, "e"), Is.EqualTo(45).Within(0.001), "and reaches it");
    }

    [Test]
    public async Task AJustifiedBlockCentersItsLastLineWhenToldTo()
    {
        using var view = await Viewed("text-align:justify;text-align-last:center");

        // A positional value is not an addition to the justification but a replacement for it: the line
        // is left ragged and moved to where the keyword puts it.
        Assert.That(Left(view, "d"), Is.EqualTo(10).Within(0.001), "twenty of room, half of it before");
        Assert.That(Right(view, "e"), Is.EqualTo(35).Within(0.001));
    }

    [Test]
    public async Task ALinedBlockJustifiesItsLastLineWhenToldTo()
    {
        using var view = await Viewed("text-align:left;text-align-last:justify");

        // The other way round, and the case the specification spells out: `text-align` never asked for
        // justification and `text-align-last` asks for it alone - which spreads the affected line and
        // leaves the broken one ragged.
        Assert.That(Right(view, "c"), Is.EqualTo(40).Within(0.001), "the broken line keeps its own edge");
        Assert.That(Right(view, "e"), Is.EqualTo(45).Within(0.001), "and the last one is spread");
    }

    [Test]
    public async Task TheKeywordAtTheEndOfTheLineMeansTheEdgeTheLineReadsFrom()
    {
        using var view = CreateView();

        // `end` is logical, like `start`: along a left-to-right line both of them come to the right, and
        // the line is moved without being spread.
        await view.SetHtml(Page("width:45px;text-align:justify;text-align-last:end", Words));

        Assert.That(Left(view, "d"), Is.EqualTo(20).Within(0.001));
        Assert.That(Right(view, "e"), Is.EqualTo(45).Within(0.001), "at the edge, but not spread to it");
    }

    [Test]
    public async Task ALineThePageBrokeByForceIsAnAffectedLineToo()
    {
        using var view = CreateView();

        // The property is named for the last line and reads for two kinds of it: the break the author
        // wrote ends a line the same way, and the line above it takes the alignment as if it were last.
        await view.SetHtml(Page(
            "width:45px;text-align:justify;text-align-last:center",
            Words + "<br><span id='f'>ff</span>"));

        Assert.That(Left(view, "d"), Is.EqualTo(10).Within(0.001), "the line the break ended is centered");
        Assert.That(Left(view, "f"), Is.EqualTo(17.5).Within(0.001), "and so is the one after it");
    }

    [Test]
    public async Task AForcedLineIsSpreadUpToTheBreakAndNotPastItsOwnEnd()
    {
        using var view = CreateView();

        // Spread, then: the line a break ended takes the room it was given, and the space the line
        // trails into is offered none of it (CSS Text 3 §5.7.1). Preformatted text is what lets a line
        // end in a space at all - ordinary text throws one away before the line is measured.
        await view.SetHtml(Page(
            "width:45px;text-align:justify;text-align-last:justify;white-space:pre-wrap",
            "<span id='d'>dd</span> <span id='e'>ee</span> "));

        // The line is thirty of the measurer's pixels with its trailing space and fifteen of room: all
        // of it goes into the place between the words, and the space that trails keeps only its own. Had
        // the trailing one counted as a place too, each would have taken seven and a half and the second
        // word would have stopped at thirty-seven and a half.
        Assert.That(Left(view, "d"), Is.EqualTo(0));
        Assert.That(Right(view, "e"), Is.EqualTo(45).Within(0.001), "the word ends on the edge");
    }

    [Test]
    public async Task ALineBeforeABreakReachesTheEdgeAndTheLastLineBehindItDoesToo()
    {
        using var view = CreateView();

        // Both kinds of affected line on one page: the one the author's break ended, and the one the
        // content ends with. Each has a single place of its own and reaches the edge through it.
        await view.SetHtml(Page(
            "width:45px;text-align:justify;text-align-last:justify",
            Words + "<br><span id='f'>ff</span> <span id='g'>gg</span>"));

        Assert.That(Right(view, "e"), Is.EqualTo(45).Within(0.001), "the line before the break reached it");
        Assert.That(Right(view, "g"), Is.EqualTo(45).Within(0.001), "and so did the last line of all");
    }

    [Test]
    public async Task JustifyAllSpreadsEveryLineOfTheBlock()
    {
        using var view = CreateView();

        // The one value of `text-align` that speaks for both properties at once (CSS Text 3 §6.1): it
        // justifies the lines and the affected one with them.
        await view.SetHtml(Page("width:45px;text-align:justify-all", Words));

        Assert.That(Right(view, "c"), Is.EqualTo(45).Within(0.001), "the broken line");
        Assert.That(Right(view, "e"), Is.EqualTo(45).Within(0.001), "and the last one");
    }

    [Test]
    public async Task TheLastLineAlignmentTravelsDownToAChildThatJustifies()
    {
        using var view = CreateView();

        // Chromium, measured on this shape: a paragraph that asks for justification under a block that
        // chose its last line's alignment still centers its last line. The property is inherited, and a
        // child that declares `text-align` does not thereby reset it.
        await view.SetHtml(
            "<html><body style='margin:0'><div style='margin:0;text-align-last:center'>" +
            "<div style='margin:0;font-size:10px;line-height:1;width:45px;text-align:justify'>" +
            Words + "</div></div></body></html>");

        Assert.That(Right(view, "c"), Is.EqualTo(45).Within(0.001), "the broken line is spread");
        Assert.That(Left(view, "d"), Is.EqualTo(10).Within(0.001), "and the last one stands centered");
    }

    [Test]
    public async Task AJustifiedBlockLeavesItsOwnFirstAndOnlyLineAtTheStart()
    {
        using var view = CreateView();

        // A block of one line is a block whose last line is that line, and `auto` leaves it ragged - the
        // page below asks for it in the order the two properties are read.
        await view.SetHtml(Page("width:45px;text-align:justify;text-align-last:auto", "aa bb cc"));

        Assert.That(Text(view, "aa bb cc").BorderBox.Width, Is.EqualTo(40), "the line is not spread");
    }

    private const String Words = "<span id='a'>aa</span> <span id='b'>bb</span> <span id='c'>cc</span> " +
        "<span id='d'>dd</span> <span id='e'>ee</span>";

    /// <summary>Lays out the fixture's five words in a block aligned as the test asks and reads its lines.</summary>
    private static async Task<HutuViewInternal> Viewed(String style)
    {
        var view = CreateView();
        await view.SetHtml(Page("width:45px;" + style, Words));
        return view;
    }

    private static String Page(String style, String content) =>
        "<html><body style='margin:0'><div style='margin:0;font-size:10px;line-height:1;" + style +
        "'>" + content + "</div></body></html>";

    private static HutuViewInternal CreateView()
    {
        var view = new HutuViewInternal(new FakeTextMeasurer());
        view.SetViewportSize(600, 600);
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

using Hutu.Rendering.Layout;
using Hutu.Rendering.Paint;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Linq;
using Hutu.Rendering.Boxes;
using System.Threading.Tasks;

namespace Hutu.Tests;

/// <summary>
/// Covers what an arm ending in <c>::first-line</c> reaches: the fragments of the first line the block
/// actually makes, the text of a block nested inside it, the mark an item wears inside, and nothing at all
/// where the content opens with a break the white-space rules keep.
/// </summary>
/// <remarks>
/// <para>
/// Pseudo-Elements 4 §5.2 makes the pseudo-element stand for the first line box of a block container, so
/// the answer depends on where the line ends - which is why these are read off the painted page rather than
/// out of the resolver. The borrowed pairs fix the edges of the model: the style outranks a colour a
/// descendant declares for its own text (<c>first-line-on-ancestor-block</c>, whose reference simply paints
/// the first line green and the second blue), an atomic inline keeps the lines inside it to itself
/// (<c>first-line-with-inline-block</c>), and a line that holds nothing is still the block's first line
/// (<c>first-letter-with-preceding-new-line</c>).
/// </para>
/// <para>
/// One thing is deliberately not honoured yet, and the class says so where it is decided: a font size, a
/// line height or a spacing written on a first line would move where the line broke, and the breaks were
/// settled with the block's own font. Nothing here asserts that those reach the line.
/// </para>
/// </remarks>
[TestFixture]
public sealed class FirstLineTests
{
    private const UInt32 Red = 0xFFFF_0000;
    private const UInt32 Green = 0xFF00_8000;
    private const UInt32 Cyan = 0xFF00_FF_FF;
    private const UInt32 Black = 0xFF00_0000;

    [Test]
    public async Task OnlyTheFirstLineTakesTheStyleWrittenForIt()
    {
        var page = await LaidOut(
            "p { width: 100px; color: black } p::first-line { color: green }",
            "<p>alpha beta gamma delta</p>");

        var lines = Runs(page);

        Assert.That(lines.Count, Is.GreaterThan(1), "the paragraph has to wrap to be worth asking");
        Assert.That(lines[0].Color, Is.EqualTo(Green), "the line the block reaches first");
        Assert.That(lines[1].Color, Is.EqualTo(Black), "and no further");
    }

    [Test]
    public async Task EveryFragmentStandingOnTheLineTakesIt()
    {
        var page = await LaidOut(
            "p::first-line { color: green }",
            "<p>one <span>two</span> three</p>");

        Assert.That(Runs(page).Select(run => run.Color).ToList(), Is.EqualTo(new[] { Green, Green, Green }),
            "the text of the inline child is on the line too");
    }

    [Test]
    public async Task TheFirstLineOfANestedBlockIsTheBlocksOwn()
    {
        var page = await LaidOut(
            "#block::first-line { color: green }",
            "<div id='block'><div><span>text</span></div></div>");

        Assert.That(Runs(page)[0].Color, Is.EqualTo(Green), "the line inside the nested block opens the outer");
    }

    [Test]
    public async Task AStyleWrittenForTheFirstLineOutranksTheTextsOwnElement()
    {
        var page = await LaidOut(
            "#block::first-line { color: green }",
            "<div id='block'><div style='color: red'><span>text</span></div></div>");

        // The reference of first-line-on-ancestor-block paints exactly this: the first line green though a
        // descendant says red, and the line below it red.
        Assert.That(Runs(page)[0].Color, Is.EqualTo(Green), "the first line is nearer than the declaration");
    }

    [Test]
    public async Task TheBackgroundOfTheFirstLineCoversTheMarkWornInside()
    {
        var page = await LaidOut(
            "li { list-style-position: inside } li::marker { content: '3. ' }" +
            "li::first-line { background: cyan }",
            "<ul><li>inside content</li></ul>");

        var marker = Runs(page).First(run => run.Text == "3. ");
        var words = Runs(page).First(run => run.Text.StartsWith("inside"));

        Assert.That(Fill(page, marker).HasValue, Is.True, "the mark the item wears inside is covered");
        Assert.That(Fill(page, words).Value, Is.EqualTo(Cyan), "with the same colour as its text");
    }

    [Test]
    public async Task ALineThatOpensWithAKeptBreakIsStillTheFirstLine()
    {
        var page = await LaidOut(
            "p { white-space: pre } p::first-line { color: green }",
            "<p>\ntext</p>");

        Assert.That(Runs(page).Select(run => run.Color).Contains(Green), Is.False,
            "the first line holds nothing, so nothing takes the style");
    }

    [Test]
    public async Task AnAtomicChildKeepsTheLinesItMakesToItself()
    {
        var page = await LaidOut(
            ".outer::first-line { color: green } .atomic { display: inline-block }" +
            ".atomic::first-line { color: red }",
            "<div class='outer'>one <div class='atomic'>two</div> three</div>");

        Assert.That(Runs(page).First(run => run.Text.Contains("one")).Color, Is.EqualTo(Green),
            "the outer's line is the one holding the atomic box");
        Assert.That(Runs(page).First(run => run.Text.Contains("two")).Color, Is.EqualTo(Red),
            "and the lines inside the atomic box are its own");
    }

    [Test]
    public async Task AColourTheTextsOwnInlineElementStatesOutranksTheLine()
    {
        // The first line's box reaches the text it covers by inheritance, and an inherited value never
        // outranks a written one: display-contents-first-line-002 draws "ASS" in the span's own green
        // though the container's line is red.
        var page = await LaidOut(
            "#block::first-line { color: red } span { color: green }",
            "<div id='block'><span>text</span></div>");

        Assert.That(Runs(page)[0].Color, Is.EqualTo(Green), "the nearer element keeps its own colour");
    }

    [Test]
    public async Task NothingIsPaintedForAnArmThePageNeverWrites()
    {
        var page = await LaidOut("p { color: green }", "<p>alpha beta gamma delta</p>");

        Assert.That(Runs(page).Select(run => run.Color).Distinct().ToList(), Is.EqualTo(new[] { Green }),
            "every line is drawn the same way");
    }

    [Test]
    public async Task TheFirstColumnOfAColumnIsTheFirstLineOfThePage()
    {
        // The style is handed to the fragments of the first line box, and in a sideways block those boxes
        // are columns - so the same arm answers one column down as one line across.
        var page = await LaidOut(
            "p { writing-mode: vertical-rl; color: black } p::first-line { color: green }",
            "<p>alpha<br>beta</p>");

        Assert.That(Runs(page)[0].Color, Is.EqualTo(Green), "the column the block reaches first");
        Assert.That(Runs(page)[1].Color, Is.EqualTo(Black), "and the next one is not it");
    }


    private sealed record Page(List<PaintCommand> Commands);

    /// <summary>A document laid out and painted, as one page of it would be for the reader.</summary>
    private static async Task<Page> LaidOut(String css, String body)
    {
        using var view = new HutuViewInternal(new FakeTextMeasurer());
        view.SetViewportSize(400, 600);

        await view.SetHtml($"<html><head><style>{css}</style></head><body style='margin:0'>{body}</body></html>");

        Assert.That(view.Paint, Is.Not.Null, "the document has to be painted");

        return new Page(view.Paint!.Commands.ToList());
    }

    /// <summary>Gets the text runs of the document, in the order they were drawn.</summary>
    private static List<TextCommand> Runs(Page page) => page.Commands.OfType<TextCommand>().ToList();

    /// <summary>Gets the colour of the background band painted under one run, where one was painted.</summary>
    private static UInt32? Fill(Page page, TextCommand run) =>
        page.Commands.OfType<FillCommand>()
            .FirstOrDefault(fill => fill.Bounds.Equals(run.Bounds))?.Color;
}

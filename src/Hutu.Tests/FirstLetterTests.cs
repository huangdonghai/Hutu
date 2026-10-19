using Hutu.Rendering.Layout;
using Hutu.Rendering.Paint;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Hutu.Tests;

/// <summary>
/// Covers what <c>::first-letter</c> takes and what it leaves: the letter of the block's own opening, the
/// punctuation standing before it, the whole of a grapheme cluster, the generated text that comes first,
/// the marker that is no part of the search, and the child that keeps a formatting context of its own.
/// </summary>
/// <remarks>
/// <para>
/// Each expected value is what Chrome measures for the same page. The letter was read out of the width a
/// 64px <c>::first-letter</c> adds to a line of Courier New, one advance step per character it took:
/// <c>item</c> one, <c>...x</c> four, <c>“quoted”</c> two, a leading space one, and an image at the head
/// none at all. The boundary rules are the borrowed <c>css-pseudo</c> pairs, whose references are drawn
/// from the same measurements.
/// </para>
/// <para>
/// One case is settled by the specification rather than by that measurement: a digit followed by a period
/// is two characters wide in Chrome, whose reading takes the separator with the digit. The rule
/// Pseudo-Elements 4 §5.1 writes is about the punctuation <em>before</em> the letter, so this engine stops
/// with the digit and says so in its own test.
/// </para>
/// </remarks>
[TestFixture]
public sealed class FirstLetterTests
{
    private const UInt32 Green = 0xFF00_8000;
    private const UInt32 Black = 0xFF00_0000;

    [Test]
    public async Task TheLetterOpeningTheBlockTakesTheStyleWrittenForIt()
    {
        var page = await LaidOut("p::first-letter { color: green }", "<p>text</p>");

        Assert.That(Run(page, "t").Color, Is.EqualTo(Green), "the opening letter is what the arm styles");
        Assert.That(Run(page, "ext").Color, Is.EqualTo(Black), "and nothing after it is");
    }

    [Test]
    public async Task ThePunctuationStandingBeforeTheLetterComesWithIt()
    {
        var page = await LaidOut(
            "p::first-letter { color: green }",
            "<p>" + Dots() + "x</p>");

        Assert.That(Run(page, Dots() + "x").Color, Is.EqualTo(Green), "three marks and the letter, four");

        var quote = await LaidOut(
            "p::first-letter { color: green }",
            "<p>" + Open() + "q" + Close() + "</p>");

        Assert.That(Run(quote, Open() + "q").Color, Is.EqualTo(Green), "the opening quote comes along");
        Assert.That(Run(quote, Close().ToString()).Color, Is.EqualTo(Black), "and the closing one stays behind");
    }

    [Test]
    public async Task TheWhiteSpaceOpeningTheLineIsLeftOutOfTheLetter()
    {
        var page = await LaidOut("p::first-letter { color: green }", "<p>  text</p>");

        Assert.That(Run(page, "t").Color, Is.EqualTo(Green), "the letter, not the space before it");
    }

    [Test]
    public async Task TheLetterIsAWholeGraphemeCluster()
    {
        // An e under an acute is one character to a reader and two units of the encoding, and the mark
        // travels with the base it sits on.
        var base_ = ((Char)0x65).ToString();
        var mark = ((Char)0x301).ToString();
        var page = await LaidOut("p::first-letter { color: green }", $"<p>{base_}{mark}x</p>");

        Assert.That(Run(page, base_ + mark).Color, Is.EqualTo(Green), "the base and its mark together");
        Assert.That(Run(page, "x").Color, Is.EqualTo(Black), "and the next character on its own");
    }

    [Test]
    public async Task ADigitTakesNoFollowingSeparatorWithIt()
    {
        // Chrome measures `1. item` as two characters, the period going with the digit it follows. The
        // specification's rule is about the punctuation before the letter, so the digit stands alone here
        // - the one case in this file settled by the text of §5.1 rather than by the measurement.
        var page = await LaidOut("p::first-letter { color: green }", "<p>1. item</p>");

        Assert.That(Run(page, "1").Color, Is.EqualTo(Green), "the digit is the letter");
        Assert.That(Run(page, ". item").Color, Is.EqualTo(Black), "and what follows it is not");
    }

    [Test]
    public async Task TheLetterOfAGeneratedRunIsTheBlocksOwnFirstLetter()
    {
        var page = await LaidOut(
            "p::before { content: 'PA' } p::first-letter { color: green }",
            "<p>ssed</p>");

        Assert.That(Run(page, "P").Color, Is.EqualTo(Green), "the ::before run opens the line");
        Assert.That(Run(page, "A").Color, Is.EqualTo(Black), "and the rest of it stands behind the letter");
        Assert.That(Run(page, "ssed").Color, Is.EqualTo(Black), "as does the text the document says");
    }

    [Test]
    public async Task TheLetterInsideAnInlineChildTakesTheAncestorsStyle()
    {
        var page = await LaidOut(
            "p::first-letter { color: green }",
            "<p><span>item</span></p>");

        Assert.That(Run(page, "i").Color, Is.EqualTo(Green), "the letter is inside the span and still his");
        Assert.That(Run(page, "tem").Color, Is.EqualTo(Black), "while the span keeps the rest");
    }

    [Test]
    public async Task AMarkerIsNoPartOfTheSearchForTheLetter()
    {
        var page = await LaidOut(
            "li { list-style-position: inside } li::marker { content: 'marker ' }" +
            "li::first-letter { color: green }",
            "<ul><li>item</li></ul>");

        Assert.That(Run(page, "i").Color, Is.EqualTo(Green), "the item's own letter, past the mark");
        Assert.That(Run(page, "marker ").Color, Is.EqualTo(Black), "and the mark itself is not the letter");
    }

    [Test]
    public async Task AChildThatKeepsAFormattingContextLeavesTheBlockWithNoFirstLetter()
    {
        // The atomic child stands at the head of the line, and §5.1 asks the letter to be there. The
        // borrowed first-letter-exclude-inline-child-marker shows exactly this for its
        // <c>inline flow-root</c> rows: no letter is styled in the parent at all - the search does not
        // skip on to the text after the child.
        var page = await LaidOut(
            "li { list-style-position: inside } span { display: inline flow-root list-item }" +
            "li::first-letter { color: green }",
            "<ul><li><span>item</span>after</li></ul>");

        Assert.That(Run(page, "after").Color, Is.EqualTo(Black), "the text behind the child is not the opening");
        Assert.That(Run(page, "item").Color, Is.EqualTo(Black), "and the child keeps its own unstyled opening");
    }

    [Test]
    public async Task AFlowRootChildTakesTheStyleWrittenForItself()
    {
        var page = await LaidOut(
            "span { display: inline flow-root list-item } span::first-letter { color: green }",
            "<p><span>item</span></p>");

        Assert.That(Run(page, "i").Color, Is.EqualTo(Green), "an atomic inline is a block container too");
        Assert.That(Run(page, "tem").Color, Is.EqualTo(Black), "and only its opening letter is styled");
    }

    [Test]
    public async Task ALineOpeningWithAPictureHasNoFirstLetter()
    {
        var page = await LaidOut(
            "p::first-letter { color: green }",
            "<p><img src=\"nothing.png\" alt=\"\" width=\"10\" height=\"10\">item</p>");

        Assert.That(Run(page, "item").Color, Is.EqualTo(Black), "the letter is not selectable behind a picture");
    }

    [Test]
    public async Task NothingIsCutForAnArmThePageNeverWrites()
    {
        var page = await LaidOut("p { color: green }", "<p>text</p>");

        Assert.That(Run(page, "text").Color, Is.EqualTo(Green), "one run for the whole of the text");
        Assert.That(page.Commands.OfType<TextCommand>().Count(run => run.Text == "t"), Is.Zero,
            "and no letter cut out of it");
    }

    [Test]
    public async Task TheOpeningLetterOfAColumnIsTheFirstLetterToo()
    {
        // The letter is cut from the head of the content, so what the writing mode makes of that head is
        // the layout's business and the same rule answers in a column as on a line.
        var page = await LaidOut(
            "p { writing-mode: vertical-rl } p::first-letter { color: green }",
            "<p>text</p>");

        Assert.That(Run(page, "t").Color, Is.EqualTo(Green), "the column opens with the styled letter");
        Assert.That(Run(page, "ext").Color, Is.EqualTo(Black), "and the rest of it goes down as it did before");
    }

    [Test]
    public async Task AnArmThatAsksForNothingTheEngineDrawsLeavesTheWordWhole()
    {
        // CSS Text 3 §4.4 keeps shaping whole across a boundary nothing at it disagrees about, and a letter
        // cut out of a run is such a boundary. An arm that only asks for a capitalisation this engine never
        // reads therefore changes nothing, and the borrowed text-transform-shaping pairs draw the word as
        // one piece.
        var page = await LaidOut(
            "p { color: green } p::first-letter { text-transform: capitalize }",
            "<p>text</p>");

        Assert.That(Run(page, "text").Color, Is.EqualTo(Green), "the whole of the text is one run");
        Assert.That(page.Commands.OfType<TextCommand>().Count(run => run.Text == "t"), Is.Zero,
            "and no letter was cut out of it");
    }

    [Test]
    public async Task ABreakTheWhiteSpaceKeepsLeavesTheBlockWithNoFirstLetter()
    {
        // In `pre` the opening break is a line of its own, empty, and the letter of the line after it is
        // nobody's first letter.
        var page = await LaidOut(
            "p { white-space: pre } p::first-letter { color: green }",
            "<p>\ntext</p>");

        Assert.That(page.Commands.OfType<TextCommand>().Count(run => run.Text == "t"), Is.Zero,
            "the first line holds nothing to style");
        Assert.That(page.Commands.OfType<TextCommand>().Any(run => run.Color == Green), Is.False,
            "and no letter takes the colour");
    }

    private sealed record Page(LayoutBox Root, List<PaintCommand> Commands);

    /// <summary>A document laid out and painted, as one page of it would be for the reader.</summary>
    private static async Task<Page> LaidOut(String css, String body)
    {
        using var view = new HutuViewInternal(new FakeTextMeasurer());
        view.SetViewportSize(900, 600);

        await view.SetHtml($"<html><head><style>{css}</style></head><body style='margin:0'>{body}</body></html>");

        Assert.That(view.Layout, Is.Not.Null, "the document has to be laid out");
        Assert.That(view.Paint, Is.Not.Null, "and painted");

        return new Page(view.Layout!.Root, view.Paint!.Commands.ToList());
    }

    /// <summary>Gets the command that draws one run of the document's own text.</summary>
    private static TextCommand Run(Page page, String text) =>
        page.Commands.OfType<TextCommand>().Single(run => run.Text == text);

    private static String Dots() => new String('.', 3);

    private static Char Open() => (Char)0x201C;

    private static Char Close() => (Char)0x201D;
}

using Hutu.Rendering.Layout;
using Hutu.Rendering.Paint;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Hutu.Tests;

/// <summary>
/// Covers the markers that the boxes <c>::before</c> and <c>::after</c> generate when they are shown
/// <c>list-item</c>: the arms written <c>::before::marker</c> style those markers (Pseudo-Elements 4 §4.2
/// lists them as valid), while the plain <c>::marker</c> - which Selectors 4 expands to <c>*::marker</c> -
/// and an arm written for the element do not select them, as Lists 3 §3.1.1 repeats.
/// </summary>
/// <remarks>
/// <para>
/// The two borrowed pairs that hold this are read here as the engine reads a page: what the marker says,
/// what colour it came to, and which of the boxes it belongs to. Their references are Chrome's own answer,
/// so the shape of a generated item's marker - the mark standing inside the item's first line, before its
/// text - is what the reference draws and the tests ask for.
/// </para>
/// <para>
/// The last of these covers the other half of what a generated box can change: a block inserted among the
/// element's own inline content leaves that content a run standing between blocks, and §9.2.1.1 gives such
/// a run an anonymous block of its own. Left without one, the element's words are content no block layout
/// looks at.
/// </para>
/// </remarks>
[TestFixture]
public sealed class NestedMarkerTests
{
    private const UInt32 Red = 0xFFFF_0000;
    private const UInt32 Green = 0xFF00_8000;
    private const UInt32 Black = 0xFF00_0000;

    [Test]
    public async Task TheMarkerContentWrittenForAGeneratedItemIsWhatItsMarkerHolds()
    {
        var page = await LaidOut(
            "li::before { content: 'before'; display: list-item; list-style-position: inside }" +
            "li::before::marker { content: 'gen' }",
            "<ul><li>x</li></ul>");

        Assert.That(MarkerRun(page).Text, Is.EqualTo("gen"), "the mark says what its own arm wrote");
        Assert.That(MarkerOf(page).Text, Is.EqualTo("• "), "while the item's own mark keeps its bullet");
    }

    [Test]
    public async Task TheColourWrittenForAGeneratedItemsMarkerReachesThatMarker()
    {
        var page = await LaidOut(
            "li::before { content: 'before'; display: list-item; list-style-position: inside }" +
            "li::before::marker { color: green }",
            "<ul><li>x</li></ul>");

        Assert.That(MarkerRun(page).Color, Is.EqualTo(Green), "the marker of the generated box is styled");
        Assert.That(Run(page, "before").Color, Is.EqualTo(Black), "and the text it hangs on is not");
    }

    [Test]
    public async Task WhatIsWrittenForEveryMarkerDoesNotReachAGeneratedItemsMarker()
    {
        // `::marker` is `*::marker`, and a marker whose originating element is a pseudo-element is not
        // selected by it - which is the whole of what nested-marker.html and its reference disagree over
        // until the rule is honoured.
        var page = await LaidOut(
            "li::before { content: 'before'; display: list-item; list-style-position: inside }" +
            "::marker { color: green }",
            "<ul><li>x</li></ul>");

        Assert.That(MarkerRun(page).Color, Is.EqualTo(Black), "the global arm leaves the generated mark alone");
        Assert.That(MarkerOf(page).Color, Is.EqualTo(Green), "while the item's own marker takes it");
    }

    [Test]
    public async Task WhatIsWrittenForTheElementDoesNotReachTheMarkerOfItsGeneratedItem()
    {
        var page = await LaidOut(
            "li::before { content: 'before'; display: list-item; list-style-position: inside }" +
            "li::marker { color: green }",
            "<ul><li>x</li></ul>");

        Assert.That(MarkerOf(page).Color, Is.EqualTo(Green), "the li's own marker is what the arm styles");
        Assert.That(MarkerRun(page).Color, Is.EqualTo(Black), "and the mark of its ::before is not it");
    }

    [Test]
    public async Task EachGeneratedArmTakesTheMarkerWrittenForIt()
    {
        var page = await LaidOut(
            "div::before, div::after { display: list-item; list-style-position: inside }" +
            "div::before { content: 'before' } div::after { content: 'after' }" +
            "div::before::marker { content: 'MB' } div::after::marker { content: 'MA' }",
            "<div></div>");

        Assert.That(MarkerRuns(page).Select(run => run.Text), Is.EqualTo(new[] { "MB", "MA" }),
            "each mark says the piece written for the arm it hangs on");
        Assert.That(Markers(page), Is.Empty, "and neither keeps the bullet the list would have drawn");
    }

    [Test]
    public async Task TheOneColonSpellingOfAGeneratedItemsMarkerIsTheSameArm()
    {
        // Pseudo-Elements 4 §8 keeps the single-colon spelling of the generated-content names working, and
        // an arm written that way is written for the same marker.
        var page = await LaidOut(
            "li::before { content: 'before'; display: list-item; list-style-position: inside }" +
            "li:before::marker { content: 'gen' }",
            "<ul><li>x</li></ul>");

        Assert.That(MarkerRun(page).Text, Is.EqualTo("gen"), "one colon or two, the arm is the same arm");
    }

    [Test]
    public async Task AGeneratedItemsMarkerOutranksTheArmWrittenForEveryMarker()
    {
        // Two pseudo-elements in an arm are two type selectors of their own (Selectors 4 §17), so the arm
        // written for one marker wins over the one written for all of them - however the two stand in the
        // sheet's order.
        var page = await LaidOut(
            "li::before { content: 'before'; display: list-item; list-style-position: inside }" +
            "li::before::marker { content: 'gen' }" +
            "::marker { content: 'GLOBAL'; color: red }",
            "<ul><li>x</li></ul>");

        Assert.That(MarkerRun(page).Text, Is.EqualTo("gen"), "the more specific arm answers");
        Assert.That(MarkerRun(page).Color, Is.EqualTo(Black), "and so the global one's colour with it");
    }

    [Test]
    public async Task AMarkerHasNoMarkerOfItsOwn()
    {
        // An arm that asks for the marker of a marker selects nothing, so the list keeps what it would
        // have drawn.
        var page = await LaidOut(
            "li::before { content: 'before'; display: list-item; list-style-position: inside }" +
            "li::before::marker::marker { content: 'gen' }",
            "<ul><li>x</li></ul>");

        Assert.That(MarkerRun(page).Text, Is.EqualTo("• "), "the list keeps what it would have drawn");
        Assert.That(page.Commands.OfType<TextCommand>().Any(run => run.Text == "gen"), Is.False,
            "and nothing of that arm reaches the page");
    }

    [Test]
    public async Task TheElementsOwnWordsStandBetweenTheBlocksItsGeneratedContentMakes()
    {
        // A block-level generated box among the element's inline content makes that content a run between
        // blocks, which §9.2.1.1 puts in an anonymous block of its own - and without one the words are
        // content no block layout looks at.
        var page = await LaidOut(
            "li::before { content: 'Before'; display: list-item }" +
            "li::after { content: 'After'; display: list-item }",
            "<ol><li>Foo</li></ol>");

        var before = Run(page, "Before").Bounds.Y;
        var words = Run(page, "Foo").Bounds.Y;
        var after = Run(page, "After").Bounds.Y;

        Assert.That(words, Is.GreaterThan(before), "the element's own line stands under the first block");
        Assert.That(words, Is.LessThan(after), "and above the last one, where the text was written");
    }

    [Test]
    public async Task GeneratedContentAloneChangesNothingAboutTheWordsOfAPage()
    {
        // The same document with inline generated content, where nothing is inserted between blocks: the
        // words stand where they always did.
        var page = await LaidOut(
            "p::before { content: 'a' } p::after { content: 'b' }",
            "<p>text</p>");

        Assert.That(Run(page, "text").Text, Is.EqualTo("text"), "the paragraph's own words survive");
        Assert.That(Run(page, "a").Bounds.Y, Is.EqualTo(Run(page, "text").Bounds.Y), "on one line");
        Assert.That(Run(page, "b").Bounds.Y, Is.EqualTo(Run(page, "text").Bounds.Y), "with the two beside them");
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

    /// <summary>Gets the command that draws a marker hanging beside its item.</summary>
    private static TextCommand MarkerOf(Page page)
    {
        var item = Walk(page.Root).Single(node => node.Source.Marker is { Length: > 0 } && node.MarkerBounds is not null);

        return page.Commands.OfType<TextCommand>().Single(run => run.Bounds.Equals(item.MarkerBounds.Value));
    }

    /// <summary>Gets the command that draws the one marker stated inside its item.</summary>
    private static TextCommand MarkerRun(Page page) => MarkerRuns(page).Single();

    /// <summary>Gets the commands that draw every marker stated inside its item, in the order they stand.</summary>
    private static List<TextCommand> MarkerRuns(Page page)
    {
        var drawn = page.Commands.OfType<TextCommand>().ToList();

        return Walk(page.Root)
            .Where(node => node.Source.IsMarker)
            .Select(node => drawn.Single(run => run.Bounds.Equals(node.BorderBox)))
            .ToList();
    }

    /// <summary>Gets the commands that draw the bullet a list would have drawn.</summary>
    private static List<TextCommand> Markers(Page page) =>
        page.Commands.OfType<TextCommand>().Where(run => run.Text == "• ").ToList();

    /// <summary>Gets the command that draws one run of the document's own text.</summary>
    private static TextCommand Run(Page page, String text) =>
        page.Commands.OfType<TextCommand>().Single(run => run.Text == text);

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

using Hutu.Rendering;
using Hutu.Rendering.Boxes;
using Hutu.Rendering.Layout;
using Hutu.Rendering.Media;
using Hutu.Rendering.Paint;
using Hutu.Skia;
using NUnit.Framework;
using SkiaSharp;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Hutu.Tests;

/// <summary>
/// Covers the styles an arm written for <c>::marker</c> reaches: the colour and the whole font of the
/// marker it styles, what the marker takes from its item when the arm says nothing, the properties Lists 3
/// §3.1.1 keeps from the marker, and the row the marker stands on when its size is not its item's.
/// </summary>
/// <remarks>
/// <para>
/// The answers are read off the box tree and the paint commands rather than out of the resolver, because
/// a marker is a box the engine makes for itself: what the page sees is the run the layout set down and the
/// colour the painter asked for. A marker that hangs beside its item is drawn from the item's box, and one
/// stated <c>inside</c> is a text box of its own - both are covered, since the style reaches them by
/// different routes.
/// </para>
/// <para>
/// Set in the fake measurer except where the ink of a real face is the question: what is being settled is
/// which style a marker wears, and that is answered before any glyph is measured.
/// </para>
/// </remarks>
[TestFixture]
public sealed class MarkerStyleTests
{
    private const UInt32 Red = 0xFFFF_0000;
    private const UInt32 Green = 0xFF00_8000;

    [Test]
    public async Task AMarkerTakesTheColourItsOwnRuleDeclares()
    {
        var page = await LaidOut("li { color: red } li::marker { color: green }", "<ul><li>one</li></ul>");

        Assert.That(MarkerOf(page).Color, Is.EqualTo(Green),
            "the arm written for the marker styles the marker");
        Assert.That(Run(page, "one").Color, Is.EqualTo(Red),
            "and leaves the item's own text alone");
    }

    [Test]
    public async Task AMarkerTakesTheFontItsOwnRuleDeclares()
    {
        var page = await LaidOut(
            "li::marker { font-size: 40px; font-family: Georgia; font-weight: 700; letter-spacing: 3px }",
            "<ul><li>one</li></ul>");

        var marker = MarkerOf(page);

        Assert.Multiple(() =>
        {
            Assert.That(marker.Font.Size, Is.EqualTo(40), "the size the marker was given is the marker's");
            Assert.That(marker.Font.Family, Does.Contain("Georgia"), "and so is the face");
            Assert.That(marker.Font.Weight, Is.EqualTo(700), "and the weight");
            Assert.That(Run(page, "one").Font.Size, Is.EqualTo(16), "the item is set at its own size");
        });
    }

    [Test]
    public async Task AMarkerInheritsWhatItsItemCameTo()
    {
        // Nothing is written for the marker, so every inherited property it has is the item's - the one
        // reading Pseudo-Elements 4 §4.2 gives a tree-abiding pseudo-element.
        var page = await LaidOut("li { color: red; font-size: 30px; letter-spacing: 2px }", "<ul><li>one</li></ul>");

        var marker = MarkerOf(page);

        Assert.Multiple(() =>
        {
            Assert.That(marker.Color, Is.EqualTo(Red), "the item's colour reaches the marker");
            Assert.That(marker.Font.Size, Is.EqualTo(30), "and its size");
            Assert.That(marker.LetterSpacing, Is.EqualTo(2), "and the room its letters carry");
        });
    }

    [Test]
    public async Task AnArmWrittenForEveryMarkerStylesEveryItem()
    {
        var page = await LaidOut("::marker { color: green }", "<ul><li>one</li><li>two</li></ul>");

        Assert.That(Markers(page).Select(run => run.Color), Is.EqualTo(new[] { Green, Green }),
            "an arm with nothing before the colon is written for every marker on the page");
    }

    [Test]
    public async Task AMarkersRowIsMeasuredAtItsOwnSize()
    {
        // A number of line-height is re-read at the size of the box that takes it, so a marker set bigger
        // than its item stands in a row of its own height. Chrome answers 60px for an item at 16px with
        // line-height: 3 and a marker at 20px, measured against it.
        var items = await Items(
            "li { font-size: 16px; line-height: 3; list-style-position: inside }" +
            "li::marker { font-size: 20px }",
            "<ul><li>one</li></ul>");

        Assert.That(MarkerBox(items).Style.LineHeight, Is.EqualTo(60).Within(0.5),
            "the number the item was given is read again at the marker's size");
    }

    [Test]
    public async Task AStateOfTheItemsOwnLineHeightReachesAMarkerThatSaysNothing()
    {
        var items = await Items(
            "ol { line-height: 3 } li { font-size: 16px; list-style-position: inside }",
            "<ol><li>one</li></ol>");

        Assert.That(MarkerBox(items).Style.LineHeight, Is.EqualTo(48).Within(0.5),
            "a line-height stated above the item is still the number its marker is measured with");
    }

    [Test]
    public async Task TheLinesAMarkerAsksForAreNotDrawn()
    {
        // Lists 3 §3.1.1 lets a handful of properties apply to the marker box, and a decoration is not one
        // of them; Chrome answers the property as not applying, measured against it. The line the item is
        // under still runs across the marker, which is the other half of what this test reads.
        var page = await LaidOut(
            "ul { text-decoration: underline } li { list-style-position: inside } li::marker { text-decoration: underline overline }",
            "<ul><li>one</li></ul>");

        var marker = MarkerRun(page);

        Assert.That(marker.Decorations.Count, Is.EqualTo(1), "one box decorates the marker, not two");
        Assert.That(Run(page, "one").Decorations.Count, Is.EqualTo(1), "and the same one decorates the item");
    }

    [Test]
    public async Task AFillOrAStrokeWrittenOnAMarkerDoesNotReachIt()
    {
        // Both are inherited, and Chrome enforces the item's value over anything the marker was given
        // (measured): a marker's glyphs are filled and ringed the way the list item's would be, and a
        // marker is never hidden.
        var items = await Items(
            "li { list-style-position: inside } li::marker" +
            " { -webkit-text-fill-color: green; -webkit-text-stroke: 4px red; visibility: hidden }",
            "<ul><li>one</li></ul>");

        var marker = MarkerBox(items).Style;

        Assert.Multiple(() =>
        {
            Assert.That(marker.TextFillColor, Is.Null, "the fill the marker asked for is not taken from it");
            Assert.That(marker.TextStrokeWidth, Is.EqualTo(0), "nor its stroke");
            Assert.That(marker.Visibility, Is.EqualTo("visible"), "and a hidden marker is still drawn");
        });
    }

    [Test]
    public async Task APageThatNamesNoMarkerPaysForNothing()
    {
        // Where no arm is written for a marker there is no second style to resolve, and the marker wears
        // the item's own - which is the shape of every list drawn before this was implemented.
        var items = await Items("li { color: red }", "<ul><li>one</li></ul>");

        Assert.That(items[0].MarkerStyle.HasValue, Is.False, "no marker style is resolved at all");

        var page = await LaidOut("li { color: red }", "<ul><li>one</li></ul>");

        Assert.That(MarkerOf(page).Color, Is.EqualTo(Red), "and the marker is drawn in the item's colour");
    }

    [Test]
    public async Task AMarkersOwnContentReplacesWhatItsListWouldHaveSaid()
    {
        var items = await Items("li::marker { content: 'X' }", "<ol><li>one</li></ol>");

        Assert.That(items[0].Marker, Is.EqualTo("X"),
            "the marker's own content decides what it holds, and the list's style is left aside");
    }

    [Test]
    public async Task AContentAskingForNothingLeavesTheItemUnmarked()
    {
        var items = await Items("li::marker { content: none }", "<ol><li>one</li></ol>");

        Assert.That(items[0].Marker, Is.Null, "content: none takes the marker away");
    }

    [Test]
    public async Task AContentOfNormalLeavesTheListStyleToAnswer()
    {
        var items = await Items("li::marker { content: normal }", "<ol><li>one</li></ol>");

        Assert.That(items[0].Marker, Is.EqualTo("1. "), "normal is what an arm that says nothing comes to");
    }

    [Test]
    public async Task ACountersNameIsSpelledInTheStyleThePageNames()
    {
        // The number the item was counted as, spelled in the counter style the content asked for - and with
        // no period or gap added, since a marker written out by the page carries its own.
        var items = await Items(
            "li::marker { content: counter(list-item, lower-roman) '. ' }",
            "<ol><li>one</li><li>two</li><li value='9'>three</li></ol>");

        Assert.That(items.Select(item => item.Marker),
            Is.EqualTo(new[] { "i. ", "ii. ", "ix. " }),
            "the counting is spelled in the style the content named, and the value attribute moves it");
    }

    [Test]
    public async Task ACounterWithoutAStyleIsSpelledInDecimal()
    {
        var items = await Items("li::marker { content: '[' counter(list-item) ']' }", "<ul><li>one</li></ul>");

        Assert.That(items[0].Marker, Is.EqualTo("[1]"),
            "an omitted counter style is decimal, not the item's list style");
    }

    [Test]
    public async Task CountersJoinTheListsAnItemStandsIn()
    {
        var items = await Items(
            ".deep::marker { content: '[' counters(list-item, '.') ']' }",
            "<ul><li class='deep'>one<ul><li class='deep'>two<ul><li class='deep'>three</li></ul></li></ul></li></ul>");

        Assert.That(items.Select(item => item.Marker),
            Is.EqualTo(new[] { "[1]", "[1.1]", "[1.1.1]" }),
            "counters() spells every list the item stands in, outermost first (Lists 3 §4.7)");
    }

    [Test]
    public async Task AContentThisEngineCannotDrawIsLeftOutWhole()
    {
        var items = await Items("li::marker { content: url(flag.png) }", "<ol><li>one</li></ol>");

        Assert.That(items[0].Marker, Is.Null,
            "a picture marker is content this engine has no way of drawing, and the value is left out whole");
    }

    [Test]
    public async Task AMarkerSpellsTheAttributeItsItemCarries()
    {
        // Content 3 §1 reads an attribute as the text it holds, and Lists 3 §3.2 has the marker's content
        // spelled exactly as ::before's is. An item that carries no such attribute is marked with nothing.
        var items = await Items(
            "li::marker { content: attr(icon) }",
            "<ul><li icon='@ '>one</li><li>two</li></ul>");

        Assert.That(items.Select(item => item.Marker),
            Is.EqualTo(new[] { "@ ", "" }),
            "the attribute the item carries is its marker, and an item that carries none is left unmarked");
    }

    [Test]
    public async Task AMarkerSkipsItsOwnInkWhenItSaysTo()
    {
        // The rule is drawn by the list and the ink is the marker's, so it is the marker's own
        // text-decoration-skip-ink that decides whether the line is broken - which is the whole point of
        // wpt/css-pseudo/marker-text-decoration-skip-ink.
        var whole = await Columns("");
        var skipped = await Columns("li::marker { text-decoration-skip-ink: auto }");
        var forced = await Columns("li::marker { text-decoration-skip-ink: none }");

        Assert.That(whole - skipped, Is.GreaterThan(4), "the marker's own auto breaks the rule at its ink");
        Assert.That(forced, Is.EqualTo(whole).Within(2), "and a marker that says none leaves it whole");
    }

    /// <summary>
    /// Counts the page columns in the band a marker's underline stands in that carry ink, over a list whose
    /// items hold nothing but their markers. The list is underlined and says nothing is to be skipped, so
    /// only what the marker itself says can break the rule.
    /// </summary>
    private static async Task<Int32> Columns(String markerRule)
    {
        const String Family = "Segoe UI";

        using var checking = new SkiaTextMeasurer();

        Assume.That(
            checking.GetMetrics(new FontStyle(Family, 40)).Ascent,
            Is.GreaterThan(0),
            $"no {Family} on this machine");

        using var view = new HutuViewInternal(new SkiaTextMeasurer());
        view.SetViewportSize(400, 300);

        await view.SetHtml(
            "<html><head><style>" +
            "li { list-style-position: inside; list-style-type: \"p. \"; }" + markerRule +
            "</style></head><body style='margin:0'>" +
            "<ol style='margin:0;font-family:" + Family + ";font-size:40px;line-height:2;" +
            "text-decoration:underline;text-decoration-color:red;text-decoration-skip-ink:none'>" +
            "<li></li></ol></body></html>");

        var run = Walk(view.Layout!.Root).Single(node => node.Source.IsMarker);
        var paint = view.Paint!;

        using var measuring = new SkiaTextMeasurer();
        var width = Math.Max(1, (Int32)Math.Ceiling(paint.Bounds.Width));
        var height = Math.Max(1, (Int32)Math.Ceiling(paint.Bounds.Height));

        using var surface = SKSurface.Create(new SKImageInfo(width, height));
        surface.Canvas.Clear(SKColors.White);
        new SkiaPaintExecutor(measuring).Execute(paint, surface.Canvas);
        using var image = surface.Snapshot();
        using var bitmap = SKBitmap.FromImage(image);

        var box = run.BorderBox;
        var metrics = measuring.GetMetrics(run.Font);

        // The band the rule stands in: below the baseline as far as a thick underline can reach.
        var top = (Int32)Math.Round(box.Y + run.Baseline - paint.Bounds.Y);
        var bottom = top + (Int32)Math.Ceiling(Math.Max(metrics.UnderlineThickness, 4) + 12);
        var left = (Int32)Math.Round(box.X - paint.Bounds.X);
        var right = (Int32)Math.Round(box.Right - paint.Bounds.X);

        var inked = 0;

        for (var at = left; at < right; at++)
        {
            for (var side = top; side < Math.Min(bottom, height); side++)
            {
                if (bitmap.GetPixel(at, side).Red > 60 &&
                    bitmap.GetPixel(at, side).Green < 120 &&
                    bitmap.GetPixel(at, side).Blue < 120)
                {
                    inked++;
                    break;
                }
            }
        }

        return inked;
    }

    private static IEnumerable<LayoutBox> Walk(LayoutBox node)
    {
        yield return node;

        foreach (var child in node.Children)
        {
            foreach (var under in Walk(child))
            {
                yield return under;
            }
        }
    }

    /// <summary>The item boxes of a document, in the order the tree holds them.</summary>
    private static async Task<List<Box>> Items(String css, String body)
    {
        using var view = new HutuViewInternal(new FakeTextMeasurer());
        view.SetViewportSize(900, 600);

        var document = await view.SetHtml($"<html><head><style>{css}</style></head><body>{body}</body></html>");

        var tree = BoxBuilder.TryBuildFused(document, new HutuRenderDevice(900, 600), (IImageProvider?)null);

        Assert.That(tree, Is.Not.Null, "these documents have to stay on the fused path");

        var items = new List<Box>();
        CollectItems(tree!, items);

        return items;
    }

    private static void CollectItems(Box box, List<Box> into)
    {
        if (box.Element is not null &&
            box.Style.Display.Equals("list-item", StringComparison.OrdinalIgnoreCase))
        {
            into.Add(box);
        }

        foreach (var child in box.Children)
        {
            CollectItems(child, into);
        }
    }

    /// <summary>The text box a marker stated inside its item became.</summary>
    private static Box MarkerBox(List<Box> items)
    {
        var found = WalkBox(items.SelectMany(item => item.Children))
            .Single(box => box.IsMarker);

        return found;
    }

    private static IEnumerable<Box> WalkBox(IEnumerable<Box> roots)
    {
        foreach (var root in roots)
        {
            yield return root;

            foreach (var under in WalkBox(root.Children))
            {
                yield return under;
            }
        }
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

    /// <summary>
    /// Gets the command that draws a marker hanging beside its item: the run of the marker's text, set down
    /// where the layout placed it.
    /// </summary>
    private static TextCommand MarkerOf(Page page)
    {
        var item = Walk(page.Root).Single(node => node.Source.Marker is { Length: > 0 } && node.MarkerBounds is not null);

        Assert.That(item.MarkerBounds, Is.Not.Null, "the layout gave the marker a place");

        return page.Commands.OfType<TextCommand>().Single(run => run.Bounds.Equals(item.MarkerBounds.Value));
    }

    /// <summary>Gets the command that draws a marker stated inside its item.</summary>
    private static TextCommand MarkerRun(Page page)
    {
        var marker = Walk(page.Root).Single(node => node.Source.IsMarker);

        return page.Commands.OfType<TextCommand>().Single(run => run.Bounds.Equals(marker.BorderBox));
    }

    private static List<TextCommand> Markers(Page page) =>
        page.Commands.OfType<TextCommand>().Where(run => run.Text == "• ").ToList();

    /// <summary>Gets the command that draws one run of the document's own text.</summary>
    private static TextCommand Run(Page page, String text) =>
        page.Commands.OfType<TextCommand>().Single(run => run.Text == text);
}

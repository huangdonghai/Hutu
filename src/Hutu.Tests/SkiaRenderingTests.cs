using Hutu.Rendering.Layout;
using Hutu.Rendering.Paint;
using Hutu.Skia;
using NUnit.Framework;
using SkiaSharp;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace Hutu.Tests;

/// <summary>
/// End-to-end coverage through the real Skia backend, so font resolution, text metrics and the
/// paint executor are exercised together rather than against a fake.
/// </summary>
[TestFixture]
public sealed class SkiaRenderingTests
{
    [Test]
    public async Task DocumentRendersToANonEmptyBitmap()
    {
        using var measurer = new SkiaTextMeasurer();
        using var view = new HutuViewInternal(measurer);
        view.SetViewportSize(600, 400);

        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<h1 style='margin:0'>Heading</h1>" +
            "<p style='margin:0'>Hello <strong>world</strong>, this paragraph is long enough to wrap across lines.</p>" +
            "<pre style='margin:0'>preformatted</pre>" +
            "</body></html>");

        var paint = view.Paint;
        var layout = view.Layout;

        Assert.That(layout, Is.Not.Null);
        Assert.That(layout!.Height, Is.GreaterThan(0));
        Assert.That(paint, Is.Not.Null);

        var nonWhite = CountNonWhitePixels(paint!, measurer);

        Assert.That(nonWhite, Is.GreaterThan(0), "the document should have painted something");
    }

    [Test]
    public void CharactersAFamilyCannotSetFallBackToASystemFontThatCan()
    {
        using var measurer = new SkiaTextMeasurer();
        var style = new FontStyle("Arial", 16);

        // Arial carries no CJK glyph, so the run has to leave it for a system font that does. This
        // only means anything on a machine that offers one, so a system without is left inconclusive.
        var runs = measurer.GetRuns("中文", style);
        var carrier = runs.FirstOrDefault(run => run.Font.Typeface?.GetGlyph('中') != 0);

        Assume.That(carrier, Is.Not.Null, "no system font carries CJK on this machine");

        Assert.That(measurer.MeasureWidth("中文", style), Is.GreaterThan(0));
        Assert.That(carrier!.Text, Is.EqualTo("中文"), "the ideographs are one stretch of the fallback font");
        Assert.That(
            runs.Any(run => run.Font.Typeface?.GetGlyph('中') == 0),
            Is.False,
            "no run is left to draw a box for the ideographs");
    }

    [Test]
    public async Task HeadingsAreLaidOutLargerThanBodyText()
    {
        using var measurer = new SkiaTextMeasurer();
        using var view = new HutuViewInternal(measurer);
        view.SetViewportSize(600, 400);

        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<h1 style='margin:0'>Heading</h1>" +
            "<p style='margin:0'>Body</p>" +
            "</body></html>");

        var runs = view.Paint!.Commands.OfType<TextCommand>().ToList();
        var heading = runs.Single(run => run.Text == "Heading");
        var body = runs.Single(run => run.Text == "Body");

        Assert.That(heading.Font.Size, Is.GreaterThan(body.Font.Size));
        Assert.That(heading.Font.Weight, Is.GreaterThanOrEqualTo(body.Font.Weight));
    }

    [Test]
    public async Task TypographySamplePageRendersThroughTheEmbeddedResourcePipeline()
    {
        using var measurer = new SkiaTextMeasurer();
        using var view = new HutuViewInternal(measurer);
        view.AddEmbeddedResourceSource(typeof(Sample.Common.SampleUtils).Assembly, "Samples");
        view.SetViewportSize(900, 600);

        await view.LoadUrlAsync("Pages/typography.htm");

        Assert.That(view.Document, Is.Not.Null, "the embedded resource requester should have served the page");
        Assert.That(view.Layout, Is.Not.Null);
        Assert.That(view.Paint, Is.Not.Null);

        var runs = view.Paint!.Commands
            .OfType<TextCommand>()
            .Select(command => command.Text)
            .ToList();

        var joined = String.Join(" | ", runs);

        Assert.That(runs, Has.Count.GreaterThan(5), "runs: " + joined);
        Assert.That(runs.Any(text => text.Contains("Semantic")), Is.True, "runs: " + joined);
        Assert.That(runs.Any(text => text.Contains("Level One Heading")), Is.True, "runs: " + joined);

        var nonWhite = CountNonWhitePixels(view.Paint!, measurer);

        Assert.That(nonWhite, Is.GreaterThan(1000), "the document should have painted text and backgrounds");
    }

    [Test]
    public async Task ImagesSamplePageRendersItsImagesAndBackgroundPanel()
    {
        using var measurer = new SkiaTextMeasurer();
        using var images = new SkiaImageLoader();
        using var view = new HutuViewInternal(measurer, images);
        view.AddEmbeddedResourceSource(typeof(Sample.Common.SampleUtils).Assembly, "Samples");
        view.SetViewportSize(900, 600);

        await view.LoadUrlAsync("Pages/images.htm");

        Assert.That(view.Document, Is.Not.Null, "the embedded resource requester should have served the page");
        Assert.That(view.Paint, Is.Not.Null);

        var paint = view.Paint!;
        var imageCommands = paint.Commands.OfType<ImageCommand>().ToList();

        Assert.That(imageCommands, Has.Count.GreaterThanOrEqualTo(2), "the page has two img elements");
        Assert.That(
            imageCommands.All(command => images.GetSize(command.Url) is not null),
            Is.True,
            "both images should have been fetched and decoded");

        // The panel combines a gradient with a background image, which is what used to crash.
        Assert.That(paint.Commands.OfType<LinearGradientCommand>(), Is.Not.Empty);
        Assert.That(paint.Commands.OfType<BackgroundImageCommand>(), Is.Not.Empty);

        Assert.That(view.Layout!.Height, Is.GreaterThan(600), "the images make the page taller than the viewport");
        Assert.That(CountNonWhitePixels(paint, measurer), Is.GreaterThan(1000));
    }

    [Test]
    public async Task ImageIsDecodedAndPaintedIntoItsBox()
    {
        using var measurer = new SkiaTextMeasurer();
        using var images = new SkiaImageLoader();
        using var view = new HutuViewInternal(measurer, images);
        view.AddEmbeddedResourceSource(typeof(SkiaRenderingTests).Assembly, "TestImages");
        view.SetViewportSize(100, 100);

        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<img src='res:/red-square.svg' style='display:block;width:40px;height:20px'>" +
            "</body></html>");

        var paint = view.Paint!;
        var image = paint.Commands.OfType<ImageCommand>().SingleOrDefault();

        Assert.That(image, Is.Not.Null, "the img should have produced an image command");
        Assert.That(image!.Bounds.Width, Is.EqualTo(40).Within(0.5));
        Assert.That(image.Bounds.Height, Is.EqualTo(20).Within(0.5));

        using var surface = SKSurface.Create(new SKImageInfo(100, 100));
        new SkiaPaintExecutor(measurer, images).Execute(paint, surface.Canvas);

        using var snapshot = surface.Snapshot();
        using var bitmap = SKBitmap.FromImage(snapshot);
        var pixel = bitmap.GetPixel(20, 10);

        Assert.That(pixel.Red, Is.GreaterThan(200), "the middle of the box should be covered by the red square");
        Assert.That(pixel.Green, Is.LessThan(80));
    }

    [Test]
    public async Task LayoutSamplePagePlacesTheFlexCardsInARow()
    {
        using var measurer = new SkiaTextMeasurer();
        using var view = new HutuViewInternal(measurer);
        view.AddEmbeddedResourceSource(typeof(Sample.Common.SampleUtils).Assembly, "Samples");
        view.SetViewportSize(900, 600);

        await view.LoadUrlAsync("Pages/layout.htm");

        Assert.That(view.Layout, Is.Not.Null);

        // The item's own text runs share its element, so the outermost box of each element is the one
        // that carries the geometry.
        var cards = LayoutOf(view)
            .Where(box => box.Source.Element?.ClassList.Contains("flex-item") == true)
            .DistinctBy(box => box.Source.Element)
            .ToList();

        Assert.That(cards, Has.Count.EqualTo(3), "the page has three flex cards");

        // `flex: 1 1 145px` with a gap of 12px: the three cards share the row, so each is wider than its
        // 145px base and they all start at the same height. A card that came out zero-width would mean
        // the shorthand had been lost, which is exactly the sort of silent failure this test is for.
        foreach (var card in cards)
        {
            Assert.That(card.BorderBox.Width, Is.GreaterThan(150), $"card at {card.BorderBox.X}");

            // The demo reset sets `box-sizing: border-box`, so `min-height: 100px` bounds the border box.
            // Two lines of text plus 16px of padding come to less than that, so the minimum wins - the
            // padding is only visible in the content box, which is what the check below pins down.
            Assert.That(card.BorderBox.Height, Is.EqualTo(100).Within(1), "min-height under border-box");
            Assert.That(card.Padding.Vertical, Is.EqualTo(32).Within(0.5), "the item's own padding");
            Assert.That(card.ContentBox.Height, Is.EqualTo(68).Within(1), "padding comes off the border box");
            Assert.That(card.BorderBox.Y, Is.EqualTo(cards[0].BorderBox.Y).Within(0.5), "all three share a row");
        }

        Assert.That(cards[1].BorderBox.X, Is.GreaterThan(cards[0].BorderBox.X));
        Assert.That(cards[2].BorderBox.X, Is.GreaterThan(cards[1].BorderBox.X));
        Assert.That(
            cards[1].BorderBox.X - cards[0].BorderBox.Right,
            Is.EqualTo(12).Within(1),
            "the gap between the cards");

        Assert.That(CountNonWhitePixels(view.Paint!, measurer), Is.GreaterThan(1000));
    }

    [Test]
    public async Task ListsAndTablesSamplePageLaysTheTableOutInColumns()
    {
        using var measurer = new SkiaTextMeasurer();
        using var view = new HutuViewInternal(measurer);
        view.AddEmbeddedResourceSource(typeof(Sample.Common.SampleUtils).Assembly, "Samples");
        view.SetViewportSize(900, 600);

        await view.LoadUrlAsync("Pages/lists-tables.htm");

        Assert.That(view.Layout, Is.Not.Null);

        var cells = LayoutOf(view)
            .Where(box => box.Kind == LayoutKind.TableCell && box.Source.Element is not null)
            .DistinctBy(box => box.Source.Element)
            .ToList();

        var headers = cells.Where(cell => cell.Source.Element!.LocalName == "th").ToList();
        var body = cells.Where(cell => cell.Source.Element!.LocalName == "td").ToList();

        Assert.That(headers, Has.Count.EqualTo(3), "the head row has three cells");
        Assert.That(body, Has.Count.EqualTo(9), "the body has three rows of three cells");

        // The three columns have to line up across every row, which is the whole point of the table
        // algorithms: the left edges form one set of x values shared by the head and the body.
        var columns = headers
            .Select(header => Math.Round(header.BorderBox.X, 1))
            .OrderBy(x => x)
            .ToList();

        Assert.That(columns[1], Is.GreaterThan(columns[0]));
        Assert.That(columns[2], Is.GreaterThan(columns[1]));

        foreach (var cell in body)
        {
            var x = Math.Round(cell.BorderBox.X, 1);
            Assert.That(columns, Does.Contain(x), $"the cell at {x} should align with a column");
        }

        // A row shares one vertical position, and the head sits above the body.
        Assert.That(headers.Select(cell => Math.Round(cell.BorderBox.Y, 1)).Distinct().Count(), Is.EqualTo(1));
        Assert.That(body.Min(cell => cell.BorderBox.Y), Is.GreaterThan(headers[0].BorderBox.Y));

        // `border-collapse: collapse` makes the columns abut, so the cells in a row draw one line.
        Assert.That(headers[1].BorderBox.X, Is.EqualTo(headers[0].BorderBox.Right).Within(0.05));

        var paint = view.Paint!;

        var texts = paint.Commands.OfType<TextCommand>().Select(command => command.Text).ToList();
        Assert.That(texts, Does.Contain("Element"));
        Assert.That(texts, Does.Contain("Purpose"));
        Assert.That(texts, Does.Contain("Demo"));

        // The header's background and the zebra stripe are the two colours the page declares for the
        // table, so a missing one means the table's boxes were never painted.
        var fills = paint.Commands.OfType<FillCommand>().Select(command => command.Color).ToList();
        Assert.That(fills, Does.Contain(0xFFEAF2F9u), "the header background");
        Assert.That(fills, Does.Contain(0xFFF8FAFCu), "the zebra stripe");

        Assert.That(
            paint.Commands.OfType<BorderCommand>().Any(command => command.Widths.Vertical >= 0.99),
            Is.True,
            "the collapsed cell borders should be painted");

        Assert.That(CountNonWhitePixels(paint, measurer), Is.GreaterThan(1000));
    }

    [Test]
    public async Task LayoutSamplePageLaysTheGridOutInThreeColumns()
    {
        using var measurer = new SkiaTextMeasurer();
        using var view = new HutuViewInternal(measurer);
        view.AddEmbeddedResourceSource(typeof(Sample.Common.SampleUtils).Assembly, "Samples");
        view.SetViewportSize(900, 600);

        await view.LoadUrlAsync("Pages/layout.htm");

        Assert.That(view.Layout, Is.Not.Null);

        var items = OfClass(view, "grid-item");

        Assert.That(items, Has.Count.EqualTo(5));

        var spanning = items[0];
        var first = items[2];
        var middle = items[3];
        var last = items[4];

        // The spanning item takes two columns and shares its row with the item that follows it; the
        // three remaining items fill the next row, one per column.
        Assert.That(items[1].BorderBox.Y, Is.EqualTo(spanning.BorderBox.Y).Within(0.5));
        Assert.That(items[1].BorderBox.X, Is.EqualTo(spanning.BorderBox.Right + 10).Within(1), "the gap follows the span");

        Assert.That(first.BorderBox.Y, Is.EqualTo(middle.BorderBox.Y).Within(0.5));
        Assert.That(last.BorderBox.Y, Is.EqualTo(middle.BorderBox.Y).Within(0.5));
        Assert.That(first.BorderBox.Y, Is.GreaterThan(spanning.BorderBox.Y + 1), "the row wrapped");

        Assert.That(first.BorderBox.X, Is.EqualTo(spanning.BorderBox.X).Within(0.5), "column one is shared by both rows");
        Assert.That(last.BorderBox.X, Is.EqualTo(items[1].BorderBox.X).Within(0.5), "column three is shared by both rows");

        Assert.That(first.BorderBox.Width, Is.EqualTo(middle.BorderBox.Width).Within(0.5));
        Assert.That(last.BorderBox.Width, Is.EqualTo(middle.BorderBox.Width).Within(0.5));
        Assert.That(spanning.BorderBox.Width, Is.EqualTo((2 * first.BorderBox.Width) + 10).Within(1), "two columns plus the gap");

        Assert.That(first.BorderBox.Width, Is.GreaterThan(first.BorderBox.Height), "a third of the page is wider than it is tall");
        Assert.That(spanning.BorderBox.Height, Is.AtLeast(76), "the item's own min-height");

        var paint = view.Paint!;
        var fills = paint.Commands.OfType<FillCommand>().Select(command => command.Color).ToList();

        Assert.That(fills, Does.Contain(0xFFE0E7FFu), "the item background the page declares");

        Assert.That(CountNonWhitePixels(paint, measurer), Is.GreaterThan(1000));
    }

    [Test]
    public async Task BoxModelSamplePageFitsAsManySwatchesAsTheWidthAllows()
    {
        using var measurer = new SkiaTextMeasurer();
        using var view = new HutuViewInternal(measurer);
        view.AddEmbeddedResourceSource(typeof(Sample.Common.SampleUtils).Assembly, "Samples");
        view.SetViewportSize(900, 600);

        await view.LoadUrlAsync("Pages/box-model.htm");

        Assert.That(view.Layout, Is.Not.Null);

        var swatches = OfClass(view, "swatch");

        Assert.That(swatches, Has.Count.EqualTo(4));

        // Six tracks of 125 pixels would fit in the page, but only four items exist, so `auto-fit`
        // collapses the two empty ones and the four that remain share the whole width.
        var row = swatches[0].BorderBox.Y;

        foreach (var swatch in swatches)
        {
            Assert.That(swatch.BorderBox.Y, Is.EqualTo(row).Within(0.5), "every swatch is on the same row");
            Assert.That(swatch.BorderBox.Width, Is.GreaterThan(125), "the minimum is a floor and the fractions widen it");
        }

        foreach (var pair in swatches.Zip(swatches.Skip(1)))
        {
            Assert.That(pair.Second.BorderBox.X - pair.First.BorderBox.Right, Is.EqualTo(12).Within(0.5), "the gap");
        }

        Assert.That(CountNonWhitePixels(view.Paint!, measurer), Is.GreaterThan(1000));
    }

    [Test]
    public async Task IndexSamplePageFillsTheViewportWithItsTwoRowGrid()
    {
        using var measurer = new SkiaTextMeasurer();
        using var view = new HutuViewInternal(measurer);
        view.AddEmbeddedResourceSource(typeof(Sample.Common.SampleUtils).Assembly, "Samples");
        view.SetViewportSize(900, 600);

        await view.LoadUrlAsync("index.htm");

        Assert.That(view.Layout, Is.Not.Null);

        var topbar = OneOfClass(view, "topbar");
        var workspace = OneOfClass(view, "workspace");
        var sidebar = OneOfClass(view, "sidebar");
        var content = OneOfClass(view, "content");

        // `body` is a grid of `auto minmax(0, 1fr)` at the full height of the viewport, so the bar
        // takes what its content needs and the workspace takes every pixel that is left.
        Assert.That(topbar.BorderBox.Y, Is.EqualTo(0).Within(1));
        Assert.That(workspace.BorderBox.Y, Is.EqualTo(topbar.BorderBox.Bottom).Within(1), "the second row starts after the first");
        Assert.That(workspace.BorderBox.Bottom, Is.EqualTo(600).Within(1), "the fraction row reaches the bottom of the viewport");

        // The workspace is a grid of `240px minmax(0, 1fr)` with a 14 pixel gap and padding.
        Assert.That(sidebar.BorderBox.Width, Is.EqualTo(240).Within(1), "the fixed column");
        Assert.That(sidebar.BorderBox.X, Is.EqualTo(14).Within(1), "the padding");
        Assert.That(content.BorderBox.X, Is.EqualTo(sidebar.BorderBox.Right + 14).Within(1), "the gap");
        Assert.That(content.BorderBox.Right, Is.EqualTo(900 - 14).Within(1), "the fraction column takes the rest");

        // Both items sit in the grid's one implicit row with `stretch`, so they share a top edge and a
        // height. That row is an `auto` track sized by the sidebar's long list of links, so it is
        // taller than the workspace's own box - which overflows, exactly as it does in a browser.
        Assert.That(content.BorderBox.Y, Is.EqualTo(workspace.BorderBox.Y + 14).Within(1), "the workspace padding");
        Assert.That(content.BorderBox.Height, Is.EqualTo(sidebar.BorderBox.Height).Within(1), "both items stretch to the row");
        Assert.That(sidebar.BorderBox.Height, Is.GreaterThan(workspace.BorderBox.Height - 28), "the row is as tall as the tallest item");

        Assert.That(CountNonWhitePixels(view.Paint!, measurer), Is.GreaterThan(1000));
    }

    [Test]
    public async Task IndexSamplePageCentresTheBrandMarkInBothDirections()
    {
        using var measurer = new SkiaTextMeasurer();
        using var view = new HutuViewInternal(measurer);
        view.AddEmbeddedResourceSource(typeof(Sample.Common.SampleUtils).Assembly, "Samples");
        view.SetViewportSize(900, 600);

        await view.LoadUrlAsync("index.htm");

        // The brand mark is a grid around a bare word, so its whole content is one anonymous item -
        // and `place-items: center` has to centre that item on both axes. The wrapper borrows the
        // container's own 42 pixel box, so this only holds if the item's height is taken from what
        // the text needs rather than from the container's declaration.
        var mark = OneOfClass(view, "brand-mark");
        var line = FirstLine(mark);

        Assert.That(mark.BorderBox.Width, Is.EqualTo(42).Within(0.5));
        Assert.That(mark.BorderBox.Height, Is.EqualTo(42).Within(0.5));
        Assert.That(line.BorderBox.X - mark.BorderBox.X, Is.EqualTo(mark.BorderBox.Right - line.BorderBox.Right).Within(1), "centred horizontally");
        Assert.That(line.BorderBox.Y - mark.BorderBox.Y, Is.EqualTo(mark.BorderBox.Bottom - line.BorderBox.Bottom).Within(1), "centred vertically");
    }

    [Test]
    public async Task FixedContentStaysAtTheViewportWhenTheDocumentIsScrolled()
    {
        using var measurer = new SkiaTextMeasurer();
        using var view = new HutuViewInternal(measurer);
        view.SetViewportSize(100, 100);

        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div style='height:1000px'></div>" +
            "<div style='position:fixed;top:0;left:0;width:20px;height:20px;background:#ff0000'></div>" +
            "</body></html>");

        var paint = view.Paint!;
        var executor = new SkiaPaintExecutor(measurer);

        Assert.That(paint.FixedCommands, Is.Not.Empty);

        // Unscrolled, the fixed box is at the viewport origin.
        using (var top = SKSurface.Create(new SKImageInfo(100, 100)))
        {
            executor.Execute(paint, top.Canvas, Offset.Zero);
            using var bitmap = SKBitmap.FromImage(top.Snapshot());

            Assert.That(bitmap.GetPixel(10, 10).Red, Is.GreaterThan(200));
        }

        // Scrolled, a ScrollViewer would translate the document content up; the fixed box has to be
        // drawn back down so that it stays at the viewport origin instead of scrolling away.
        using (var scrolled = SKSurface.Create(new SKImageInfo(100, 100)))
        {
            scrolled.Canvas.Translate(0, -500);
            executor.Execute(paint, scrolled.Canvas, new Offset(0, 500));
            using var bitmap = SKBitmap.FromImage(scrolled.Snapshot());

            Assert.That(bitmap.GetPixel(10, 10).Red, Is.GreaterThan(200), "the fixed box is pinned to the viewport");
        }
    }

    [Test]
    public async Task FormsSamplePageLaysTheFieldGridOutInColumns()
    {
        using var measurer = new SkiaTextMeasurer();
        using var view = new HutuViewInternal(measurer);
        view.AddEmbeddedResourceSource(typeof(Sample.Common.SampleUtils).Assembly, "Samples");
        view.SetViewportSize(900, 600);

        await view.LoadUrlAsync("Pages/forms.htm");

        Assert.That(view.Layout, Is.Not.Null);

        // The five fields are the direct children of the `.form-grid` container.
        var cells = LayoutOf(view)
            .Where(box => box.Kind == LayoutKind.Block &&
                box.Source.Element?.ParentElement is { } parent &&
                parent.ClassList.Contains("form-grid"))
            .DistinctBy(box => box.Source.Element)
            .ToList();

        Assert.That(cells, Has.Count.EqualTo(5), "the field grid has five cells");

        // `repeat(auto-fit, minmax(220px, 1fr))` fits three columns in the card, so the fields wrap
        // three-then-two and the two rows share the same three column edges.
        Assert.That(cells[1].BorderBox.Y, Is.EqualTo(cells[0].BorderBox.Y).Within(0.5));
        Assert.That(cells[2].BorderBox.Y, Is.EqualTo(cells[0].BorderBox.Y).Within(0.5));
        Assert.That(cells[3].BorderBox.Y, Is.GreaterThan(cells[0].BorderBox.Y + 1), "the fourth field wraps");
        Assert.That(cells[4].BorderBox.Y, Is.EqualTo(cells[3].BorderBox.Y).Within(0.5));

        Assert.That(cells[3].BorderBox.X, Is.EqualTo(cells[0].BorderBox.X).Within(0.5), "column one");
        Assert.That(cells[4].BorderBox.X, Is.EqualTo(cells[1].BorderBox.X).Within(0.5), "column two");
        Assert.That(cells[1].BorderBox.X - cells[0].BorderBox.Right, Is.EqualTo(16).Within(0.5), "the gap");
        Assert.That(cells[2].BorderBox.Width, Is.EqualTo(cells[0].BorderBox.Width).Within(0.5));

        // The choice rows are flex containers around a control and its label, so they stack in the
        // block flow rather than collapsing to nothing.
        var choices = OfClass(view, "choice");

        Assert.That(choices, Has.Count.EqualTo(3));
        Assert.That(choices[1].BorderBox.Y, Is.GreaterThan(choices[0].BorderBox.Y));
        Assert.That(choices[2].BorderBox.Y, Is.GreaterThan(choices[1].BorderBox.Y));

        var texts = view.Paint!.Commands.OfType<TextCommand>().Select(command => command.Text).ToList();
        Assert.That(texts.Any(text => text.Contains("Light Mode")), Is.True, "the choice label renders");
        Assert.That(texts.Any(text => text.Contains("Submit Form")), Is.True);

        Assert.That(CountNonWhitePixels(view.Paint!, measurer), Is.GreaterThan(1000));
    }

    [Test]
    public async Task BorderRadiusSamplePageRendersRoundedCornersAndClipsToTheCurve()
    {
        using var measurer = new SkiaTextMeasurer();
        using var view = new HutuViewInternal(measurer);
        view.AddEmbeddedResourceSource(typeof(Sample.Common.SampleUtils).Assembly, "Samples");
        view.SetViewportSize(900, 800);

        await view.LoadUrlAsync("Pages/border-radius.htm");

        Assert.That(view.Document, Is.Not.Null, "the embedded resource requester should have served the page");

        var paint = view.Paint!;
        var boxes = OfClass(view, "rbox");

        // Sections 1-6 each show four swatches, so the page declares twenty-four of them.
        Assert.That(boxes, Has.Count.EqualTo(24), "six sections of four swatches");

        // Section 1: a single value rounds every corner the same way.
        Assert.That(boxes[0].Radii.TopLeft.Horizontal, Is.EqualTo(20).Within(0.5));
        Assert.That(boxes[0].Radii.IsUniform, Is.True);

        // Section 2: the trailing `border-radius: 0` resets the longhands written before it.
        foreach (var box in boxes.Skip(4).Take(4))
        {
            Assert.That(box.Radii.IsZero, Is.True, "a later shorthand overrides the earlier longhands");
        }

        // Section 3: the slash syntax flattens the corner, so the two axes differ.
        Assert.That(boxes[8].Radii.TopLeft.Horizontal, Is.EqualTo(40).Within(0.5));
        Assert.That(boxes[8].Radii.TopLeft.Vertical, Is.EqualTo(10).Within(0.5));

        // Section 5: a radius too long for the box is scaled down to what it can hold. The swatch is
        // 80px wide but 60px tall with a 2px border, so the border box is 84x64: the height is the
        // tighter side, and both the 60px and the 100px declaration shrink to fit it - 60 * 64/120 = 32.
        Assert.That(boxes[16].Radii.TopLeft.Horizontal, Is.EqualTo(32).Within(0.5));
        Assert.That(boxes[17].Radii.TopLeft.Horizontal, Is.EqualTo(32).Within(0.5));
        Assert.That(boxes[19].Radii.TopLeft.Horizontal, Is.EqualTo(20).Within(0.5), "a radius that fits is untouched");

        // Sections 7 and 8: every `overflow: hidden` box opens a clip before its children and closes
        // it after them, so the stack is balanced.
        var pushes = paint.Commands.OfType<PushClipCommand>().ToList();
        var pops = paint.Commands.OfType<PopClipCommand>().ToList();

        Assert.That(pushes, Is.Not.Empty, "the page hides the overflow of several boxes");
        Assert.That(pops, Has.Count.EqualTo(pushes.Count), "clips are opened and closed in pairs");

        // Section 8: a 6px border around a 14px corner leaves an 8px curve on the inner edge, and the
        // `background-clip: padding-box` gradient is painted against that inner rectangle.
        var thick = LayoutOf(view)
            .Where(box => box.Border.Top >= 5.9 && box.Border.Horizontal >= 11.8)
            .DistinctBy(box => box.Source.Element)
            .ToList();

        Assert.That(thick, Has.Count.EqualTo(2), "section 8 shows two thick-border boxes");

        foreach (var box in thick)
        {
            Assert.That(box.Radii.TopLeft.Horizontal, Is.EqualTo(14).Within(0.5), "the outer curve");
            Assert.That(box.PaddingRadii.TopLeft.Horizontal, Is.EqualTo(8).Within(0.5), "14 - 6 on the inner edge");
        }

        Assert.That(
            paint.Commands.OfType<LinearGradientCommand>().Any(gradient =>
                Math.Abs(gradient.Bounds.X - thick[0].PaddingBox.X) < 0.5 &&
                Math.Abs(gradient.Bounds.Width - thick[0].PaddingBox.Width) < 0.5),
            Is.True,
            "the padding-box clip pulls the gradient in to the border's inner edge");

        // Pixel level: the middle of the first (20px radius) swatch is filled and its square corner is
        // cut away, which is the whole point of a rounded box.
        var width = Math.Max(1, (Int32)Math.Ceiling(paint.Bounds.Width));
        var height = Math.Max(1, (Int32)Math.Ceiling(paint.Bounds.Height));

        using var surface = SKSurface.Create(new SKImageInfo(width, height));
        new SkiaPaintExecutor(measurer).Execute(paint, surface.Canvas);

        using var bitmap = SKBitmap.FromImage(surface.Snapshot());
        var swatch = boxes[0].BorderBox;
        var centre = bitmap.GetPixel((Int32)(swatch.X + (swatch.Width / 2)), (Int32)(swatch.Y + (swatch.Height / 2)));
        var corner = bitmap.GetPixel((Int32)swatch.X + 1, (Int32)swatch.Y + 1);

        Assert.That(centre.Blue, Is.GreaterThan(140), "steelblue fills the middle of the swatch");
        Assert.That(centre.Green, Is.GreaterThan(90));
        Assert.That(
            Math.Min(corner.Red, Math.Min(corner.Green, corner.Blue)),
            Is.GreaterThan(230),
            "the corner sits outside the 20px curve and stays white");
    }

    [Test]
    public async Task VerticalChineseSamplePageRunsItsColumnsRightToLeft()
    {
        using var measurer = new SkiaTextMeasurer();
        using var view = new HutuViewInternal(measurer);
        view.AddEmbeddedResourceSource(typeof(Sample.Common.SampleUtils).Assembly, "Samples");
        view.SetViewportSize(900, 700);

        await view.LoadUrlAsync("Pages/vertical-chinese.htm");

        Assert.That(view.Layout, Is.Not.Null, "the embedded resource requester should have served the page");
        Assert.That(view.Paint, Is.Not.Null);

        var panel = OneOfClass(view, "vertical-panel");
        Assert.That(panel.Flow.IsVertical, Is.True, "the panel is the column the copy runs down");

        var copy = OneOfClass(view, "vertical-copy");
        var columns = Columns(copy);

        Assert.That(columns.Count, Is.GreaterThan(1), "the copy is long enough to need more than one column");
        Assert.That(columns[0].Flow.IsVertical, Is.True);
        Assert.That(columns[0].BorderBox.Height, Is.GreaterThan(columns[0].BorderBox.Width), "a column is taller than it is wide");

        // vertical-rl grows towards the left, so every column starts to the left of the one before it.
        for (var i = 1; i < columns.Count; i++)
        {
            Assert.That(
                columns[i].BorderBox.X,
                Is.LessThan(columns[i - 1].BorderBox.X),
                "each column is set to the left of the previous one");
        }

        var digits = TextsIn(copy).Single(text => text.Text == "2026");
        Assert.That(digits.Flow.CombinesUpright, Is.True, "the tcy span stands as one upright box");
        Assert.That(digits.Flow.IsVertical, Is.True);

        Assert.That(CountNonWhitePixels(view.Paint!, measurer), Is.GreaterThan(1000));
    }

    [Test]
    public async Task VerticalJapaneseSamplePageSetsItsRubyBesideTheColumn()
    {
        using var measurer = new SkiaTextMeasurer();
        using var view = new HutuViewInternal(measurer);
        view.AddEmbeddedResourceSource(typeof(Sample.Common.SampleUtils).Assembly, "Samples");
        view.SetViewportSize(900, 700);

        await view.LoadUrlAsync("Pages/vertical-japanese.htm");

        Assert.That(view.Layout, Is.Not.Null);

        var panel = OneOfClass(view, "vertical-panel");
        var baseText = TextsIn(panel).Single(text => text.Text == "春");
        var annotation = TextsIn(panel).Single(text => text.Text == "はる");

        Assert.That(baseText.Flow.IsVertical, Is.True);
        Assert.That(annotation.Flow.IsVertical, Is.True);
        Assert.That(
            baseText.Flow.Orientation,
            Is.EqualTo(TextOrientation.Upright),
            "the kanji stands upright under its reading, as it does without one");
        Assert.That(annotation.Font.Size, Is.LessThan(baseText.Font.Size), "a reading is set smaller than its base");
        Assert.That(
            annotation.BorderBox.X + (annotation.BorderBox.Width / 2),
            Is.GreaterThan(baseText.BorderBox.X + (baseText.BorderBox.Width / 2)),
            "the reading sits on the far side of the column from the text it annotates");

        var digits = TextsIn(panel).FirstOrDefault(text => text.Text == "2026");
        Assert.That(digits, Is.Not.Null, "the tcy digits were laid out");
        Assert.That(digits!.Flow.CombinesUpright, Is.True);

        Assert.That(CountNonWhitePixels(view.Paint!, measurer), Is.GreaterThan(1000));
    }

    [Test]
    public async Task VerticalMixedSamplePageTurnsEachBoxItsOwnWay()
    {
        using var measurer = new SkiaTextMeasurer();
        using var view = new HutuViewInternal(measurer);
        view.AddEmbeddedResourceSource(typeof(Sample.Common.SampleUtils).Assembly, "Samples");
        view.SetViewportSize(900, 760);

        await view.LoadUrlAsync("Pages/vertical-mixed.htm");

        Assert.That(view.Layout, Is.Not.Null);

        var mixed = Orientations(OneOfClass(view, "mixed"));
        var upright = Orientations(OneOfClass(view, "upright"));
        var sideways = Orientations(OneOfClass(view, "sideways"));

        Assert.That(mixed, Does.Contain(TextOrientation.Upright), "an ideograph stands upright under mixed");
        Assert.That(mixed, Does.Contain(TextOrientation.Sideways), "a latin run is turned under mixed");
        Assert.That(upright, Is.EquivalentTo(new[] { TextOrientation.Upright }), "every character stands upright");
        Assert.That(sideways, Is.EquivalentTo(new[] { TextOrientation.Sideways }), "every character is turned");

        Assert.That(CountNonWhitePixels(view.Paint!, measurer), Is.GreaterThan(1000));
    }

    /// <summary>Finds the line boxes under a box, in document order.</summary>
    private static System.Collections.Generic.List<LayoutBox> Columns(LayoutBox root)
    {
        return LayoutBoxes(root).Where(node => node.Kind == LayoutKind.Line).ToList();
    }

    /// <summary>Finds the text boxes under a box, in document order.</summary>
    private static System.Collections.Generic.List<LayoutBox> TextsIn(LayoutBox root)
    {
        return LayoutBoxes(root).Where(node => node.Kind == LayoutKind.Text).ToList();
    }

    /// <summary>Gets the orientations the text under a box is set in, without repetition.</summary>
    private static System.Collections.Generic.List<TextOrientation> Orientations(LayoutBox root)
    {
        return TextsIn(root)
            .Select(text => text.Flow.Orientation)
            .Distinct()
            .ToList();
    }

    private static System.Collections.Generic.IEnumerable<LayoutBox> LayoutBoxes(LayoutBox root)
    {
        yield return root;

        foreach (var child in root.Children)
        {
            foreach (var descendant in LayoutBoxes(child))
            {
                yield return descendant;
            }
        }
    }

    /// <summary>Finds the one box an element of a given class produced.</summary>
    private static LayoutBox OneOfClass(HutuViewInternal view, String name)
    {
        var found = OfClass(view, name);

        Assert.That(found, Has.Count.EqualTo(1), $"expected one {name} box");
        return found[0];
    }

    /// <summary>Finds the first line box inside a box, which is where its text geometry lives.</summary>
    private static LayoutBox FirstLine(LayoutBox box)
    {
        if (box.Kind == LayoutKind.Line)
        {
            return box;
        }

        foreach (var child in box.Children)
        {
            var line = FindLine(child);

            if (line is not null)
            {
                return line;
            }
        }

        Assert.Fail("the box holds no line box");
        return null!;
    }

    private static LayoutBox? FindLine(LayoutBox box)
    {
        if (box.Kind == LayoutKind.Line)
        {
            return box;
        }

        foreach (var child in box.Children)
        {
            var line = FindLine(child);

            if (line is not null)
            {
                return line;
            }
        }

        return null;
    }

    /// <summary>Finds the boxes an element of a given class produced, in document order.</summary>
    private static System.Collections.Generic.List<LayoutBox> OfClass(HutuViewInternal view, String name)
    {
        var found = new System.Collections.Generic.List<LayoutBox>();

        foreach (var box in LayoutOf(view))
        {
            var element = box.Source.Element;

            if (element is null || !element.ClassList.Contains(name))
            {
                continue;
            }

            if (!found.Any(match => ReferenceEquals(match.Source.Element, element)))
            {
                found.Add(box);
            }
        }

        return found;
    }

    private static System.Collections.Generic.IEnumerable<LayoutBox> LayoutOf(HutuViewInternal view)
    {
        return Walk(view.Layout!.Root);

        static System.Collections.Generic.IEnumerable<LayoutBox> Walk(LayoutBox node)
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

    private static Int32 CountNonWhitePixels(PaintList paint, SkiaTextMeasurer measurer)
    {
        var width = Math.Max(1, (Int32)Math.Ceiling(paint.Bounds.Width));
        var height = Math.Max(1, (Int32)Math.Ceiling(paint.Bounds.Height));

        using var surface = SKSurface.Create(new SKImageInfo(width, height));
        var executor = new SkiaPaintExecutor(measurer);
        executor.Execute(paint, surface.Canvas);

        using var image = surface.Snapshot();
        using var bitmap = SKBitmap.FromImage(image);

        // Reading the pixel array in one go keeps the check off the per-pixel interop path.
        var pixels = bitmap.Pixels;
        var count = 0;

        foreach (var pixel in pixels)
        {
            if (pixel != SKColors.White)
            {
                count++;
            }
        }

        return count;
    }
}

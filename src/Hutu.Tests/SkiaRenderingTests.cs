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

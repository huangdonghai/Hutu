using Hutu.Skia;
using NUnit.Framework;
using SkiaSharp;
using System.Threading.Tasks;

namespace Hutu.Tests;

/// <summary>
/// Covers inline <c>&lt;svg&gt;</c>: an <c>&lt;svg&gt;</c> element is a replaced box whose vector content
/// is rendered through the backend's SVG picture, so a shape inside it paints where the box sits.
/// </summary>
[TestFixture]
public sealed class SvgRenderingTests
{
    [Test]
    public async Task AnInlineSvgRectPaintsItsFill()
    {
        using var measurer = new SkiaTextMeasurer();
        using var images = new SkiaImageLoader();
        using var view = new HutuViewInternal(measurer, images);
        view.SetViewportSize(100, 100);

        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<svg width='60' height='60' viewBox='0 0 60 60' style='display:block'>" +
            "<rect x='0' y='0' width='60' height='60' fill='#00cc00'/></svg>" +
            "</body></html>");

        using var bitmap = Render(view, measurer, images);

        var centre = bitmap.GetPixel(30, 30);

        Assert.That(centre.Green, Is.GreaterThan(150), "the SVG rect's fill is painted");
        Assert.That(centre.Red, Is.LessThan(150), "and it is green, not the page's white");
    }

    [Test]
    public async Task AnInlineSvgPathPaintsItsShape()
    {
        using var measurer = new SkiaTextMeasurer();
        using var images = new SkiaImageLoader();
        using var view = new HutuViewInternal(measurer, images);
        view.SetViewportSize(100, 100);

        // A triangle over the lower half: the point above it stays the page's white, the fill below paints.
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<svg width='60' height='60' viewBox='0 0 60 60' style='display:block'>" +
            "<path d='M30 20 L50 50 L10 50 Z' fill='#0000ff'/></svg>" +
            "</body></html>");

        using var bitmap = Render(view, measurer, images);

        Assert.That(bitmap.GetPixel(30, 45).Blue, Is.GreaterThan(150), "inside the triangle is blue");
        Assert.That(bitmap.GetPixel(30, 5).Red, Is.GreaterThan(200), "above the triangle is untouched");
    }

    private static SKBitmap Render(HutuViewInternal view, SkiaTextMeasurer measurer, SkiaImageLoader images)
    {
        using var surface = SKSurface.Create(new SKImageInfo(100, 100))!;
        surface.Canvas.Clear(SKColors.White);
        new SkiaPaintExecutor(measurer, images).Execute(view.Paint!, surface.Canvas);

        return SKBitmap.FromImage(surface.Snapshot());
    }
}

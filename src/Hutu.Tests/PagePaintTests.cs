using AngleSharp.Css;
using Hutu.Rendering;
using Hutu.Skia;
using NUnit.Framework;
using SkiaSharp;
using System.Threading.Tasks;

namespace Hutu.Tests;

/// <summary>
/// Covers drawing a page: the slice a page names is moved onto its page area and clipped there, so
/// the page before and the page after never show through.
/// </summary>
[TestFixture]
public sealed class PagePaintTests
{
    [Test]
    public async Task EachPageDrawsItsOwnSliceInsideThePageArea()
    {
        using var measurer = new SkiaTextMeasurer();
        using var view = new HutuViewInternal(measurer);
        view.SetViewportSize(800, 600);
        view.SetMediaType(DeviceCategory.Printer);
        await view.SetHtml(
            "<style>@page { size: 100px; margin: 10px } html, body, div { margin: 0 }" +
            " div { height: 40px }</style>" +
            "<div style='background:red'></div><div style='background:blue'></div><div style='background:green'></div>");

        var pages = view.Pages!;
        var executor = new SkiaPaintExecutor(measurer);

        Assert.That(pages.Count, Is.EqualTo(2), "three forty-pixel boxes in an eighty-pixel page area");

        using var first = Draw(executor, view, pages.Pages[0]);
        Assert.Multiple(() =>
        {
            Assert.That(first.GetPixel(5, 5), Is.EqualTo(SKColors.White), "the page margin is blank");
            Assert.That(first.GetPixel(15, 15), Is.EqualTo(SKColors.Red), "the first box");
            Assert.That(first.GetPixel(15, 55), Is.EqualTo(SKColors.Blue), "the second box");
            Assert.That(first.GetPixel(15, 95), Is.EqualTo(SKColors.White), "the third box belongs to the next page");
        });

        using var second = Draw(executor, view, pages.Pages[1]);
        Assert.Multiple(() =>
        {
            Assert.That(second.GetPixel(5, 5), Is.EqualTo(SKColors.White), "the margin is blank on every page");
            Assert.That(second.GetPixel(15, 15), Is.EqualTo(SKColors.Green), "the second page begins with its own slice");
            Assert.That(second.GetPixel(15, 55), Is.EqualTo(SKColors.White), "and the slice ends where the page does");
        });
    }

    [Test]
    public async Task ThePageBoxPaintsItsBackgroundAndBorderUnderAndAroundTheContent()
    {
        using var measurer = new SkiaTextMeasurer();
        using var view = new HutuViewInternal(measurer);
        view.SetViewportSize(800, 600);
        view.SetMediaType(DeviceCategory.Printer);
        await view.SetHtml(
            "<style>@page { size: 100px; margin: 10px; background: red; border: 10px solid blue }" +
            " html, body, div { margin: 0 }</style><div style='height:20px'></div>");

        var page = view.Pages!.Pages[0];
        var executor = new SkiaPaintExecutor(measurer);
        using var drawn = Draw(executor, view, page);

        Assert.Multiple(() =>
        {
            Assert.That(drawn.GetPixel(2, 2), Is.EqualTo(SKColors.Blue), "the page border");
            Assert.That(drawn.GetPixel(15, 15), Is.EqualTo(SKColors.Red), "the page background reaches the page area");
            Assert.That(drawn.GetPixel(50, 50), Is.EqualTo(SKColors.Red), "and the rest of the box");
            Assert.That(drawn.GetPixel(50, 95), Is.EqualTo(SKColors.Blue), "the border closes the page");
        });
    }

    [Test]
    public async Task APageNumberIsDrawnInTheMarginItWasSetIn()
    {
        using var measurer = new SkiaTextMeasurer();
        using var view = new HutuViewInternal(measurer);
        view.SetViewportSize(800, 600);
        view.SetMediaType(DeviceCategory.Printer);
        await view.SetHtml(
            "<style>@page { size: 200px 100px; margin: 20px;" +
            " @bottom-center { content: counter(page); color: #ff0000; font-size: 10px } }" +
            " html, body, div { margin: 0 } div { height: 120px; background: blue }</style><div></div>");

        var pages = view.Pages!;
        var executor = new SkiaPaintExecutor(measurer);

        Assert.That(pages.Count, Is.EqualTo(2), "a hundred and twenty pixel box in a sixty-pixel page area");

        using var first = Draw(executor, view, pages.Pages[0]);
        Assert.Multiple(() =>
        {
            Assert.That(Ink(first, 20, 80, 180, 100), Is.GreaterThan(0), "the number stands in the bottom margin");
            Assert.That(Ink(first, 20, 0, 180, 80), Is.EqualTo(0), "and not in the page area");
            Assert.That(Ink(first, 0, 0, 20, 100), Is.EqualTo(0), "nor in the side margins");
        });
    }

    /// <summary>Counts the pixels of the given colour in one area of the page.</summary>
    private static Int32 Ink(SKBitmap page, Int32 left, Int32 top, Int32 right, Int32 bottom)
    {
        var count = 0;

        for (var y = top; y < bottom; y++)
        {
            for (var x = left; x < right; x++)
            {
                if (page.GetPixel(x, y) == SKColors.Red)
                {
                    count++;
                }
            }
        }

        return count;
    }

    private static SKBitmap Draw(SkiaPaintExecutor executor, HutuViewInternal view, PageSlice page)
    {
        var turned = page.Geometry.Orientation != PageOrientation.None;
        using var surface = SKSurface.Create(new SKImageInfo(
            (int)(turned ? page.Geometry.Height : page.Geometry.Width),
            (int)(turned ? page.Geometry.Width : page.Geometry.Height)));

        executor.ExecutePage(view.Paint!, surface.Canvas, page);

        using var image = surface.Snapshot();
        return SKBitmap.FromImage(image);
    }
}

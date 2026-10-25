using Hutu.Rendering.Paint;
using Hutu.Skia;
using NUnit.Framework;
using SkiaSharp;
using System;
using System.Threading.Tasks;

namespace Hutu.Tests;

/// <summary>
/// Covers which way a turned box lies: <c>transform</c> names a matrix in CSS's own order, and the
/// backend has to fold it into Skia without exchanging the two terms that say how one axis leans on
/// the other, because those two are the difference between a clockwise quarter turn and an
/// anti-clockwise one.
/// </summary>
[TestFixture]
public sealed class TransformPaintTests
{
    [Test]
    public async Task AQuarterTurnTurnsInTheDirectionTheAngleSays()
    {
        // The box is a hundred wide and fifty tall, standing at 100,100, and is turned a quarter about
        // its own top-left corner. Clockwise lays it down the left of that corner and below it; the
        // other way round would put it to the right and above.
        var drawn = await Paint(
            "<div style='position:absolute;left:100px;top:100px;width:100px;height:50px;" +
            "background:#00ff00;transform:rotate(90deg);transform-origin:0 0'></div>");

        Assert.That(Pixel(drawn, 75, 150), Is.EqualTo(new SKColor(0, 255, 0)), "the turn lies below and left");
        Assert.That(Pixel(drawn, 125, 50), Is.Not.EqualTo(new SKColor(0, 255, 0)), "it does not lie above and right");
    }

    [Test]
    public async Task ACornerSaysWhereTheTurnedBoxStands()
    {
        // The same fifty of height stood on the box's bottom-right corner: turning that quarter leaves
        // the box to the right of the corner it hung from, which is where Chrome puts it.
        var drawn = await Paint(
            "<div style='position:absolute;left:0;top:200px;width:100px;height:50px;" +
            "background:#00ff00;transform:rotate(90deg);transform-origin:right bottom'></div>");

        Assert.That(Pixel(drawn, 125, 200), Is.EqualTo(new SKColor(0, 255, 0)), "the box stands right of its corner");
        Assert.That(Pixel(drawn, 75, 275), Is.Not.EqualTo(new SKColor(0, 255, 0)), "not below it");
    }

    [Test]
    public async Task ASkewLeansTheAxisTheFunctionNames()
    {
        // skewX carries every row to the right by as much as it stands below the origin, so the head of a
        // box stayed at 100 while its foot, fifty below, runs from 150 out to 250. A skew that leaned the
        // other axis would leave the foot where the head left it.
        var drawn = await Paint(
            "<div style='position:absolute;left:100px;top:100px;width:100px;height:50px;" +
            "background:#00ff00;transform:skewX(45deg);transform-origin:0 0'></div>");

        Assert.That(Pixel(drawn, 240, 148), Is.EqualTo(new SKColor(0, 255, 0)), "the foot of the box is skewed out");
        Assert.That(Pixel(drawn, 120, 148), Is.Not.EqualTo(new SKColor(0, 255, 0)), "and its heel left");
        Assert.That(Pixel(drawn, 110, 104), Is.EqualTo(new SKColor(0, 255, 0)), "the head has barely moved");
    }

    /// <summary>
    /// A keyword says which axis it stands on, whatever else the value writes: top and bottom are the
    /// block's own height, left and right its width, and the axis nobody names is read as half of it. A
    /// single <c>top</c> therefore moves the y and leaves the x at the middle - Chrome computes it as
    /// 50px 0px on a box a hundred wide and fifty tall, measured here within the box's own border box.
    /// </summary>
    [Test]
    [TestCase("top", 50, 0)]
    [TestCase("bottom", 50, 50)]
    [TestCase("left", 0, 25)]
    [TestCase("right", 100, 25)]
    [TestCase("top right", 100, 0)]
    [TestCase("center top", 50, 0)]
    [TestCase("left 20px", 0, 20)]
    [TestCase("20px", 20, 25)]
    public async Task ALoneKeywordStandsOnTheAxisItNames(String origin, Double x, Double y)
    {
        using var view = new HutuViewInternal(new SkiaTextMeasurer());
        view.SetViewportSize(300, 300);
        await view.SetHtmlDocumentAsync(
            "<html><body style='margin:0'><div id='x' style='position:absolute;left:0;top:200px;" +
            "width:100px;height:50px;transform:rotate(90deg);transform-origin:" + origin + "'></div>" +
            "</body></html>",
            "file:///probe/origin.html");

        var box = view.Layout!.FindBox(view.Document!.GetElementById("x")!)!;

        Assert.That(box.TransformOrigin.X, Is.EqualTo(x), $"{origin} x");
        Assert.That(box.TransformOrigin.Y, Is.EqualTo(y), $"{origin} y");
    }

    private static async Task<SKBitmap> Paint(String body)
    {
        using var view = new HutuViewInternal(new SkiaTextMeasurer());
        view.SetViewportSize(300, 300);
        await view.SetHtmlDocumentAsync(
            "<html><body style='margin:0'>" + body + "</body></html>",
            "file:///probe/transform.html");

        using var surface = SKSurface.Create(new SKImageInfo(300, 300));
        var executor = new SkiaPaintExecutor(new SkiaTextMeasurer());

        executor.Execute(view.Paint!, surface.Canvas);

        using var image = surface.Snapshot();

        return SKBitmap.FromImage(image);
    }

    private static SKColor Pixel(SKBitmap bitmap, Int32 x, Int32 y) => bitmap.GetPixel(x, y);
}

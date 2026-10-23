using Hutu.Rendering;
using Hutu.Rendering.Paint;
using Hutu.Skia;
using NUnit.Framework;
using SkiaSharp;
using System;
using System.Threading.Tasks;

namespace Hutu.Tests;

/// <summary>
/// The ink a rounded border lays in its corners, which is the part of a border that two sides meet in.
/// </summary>
/// <remarks>
/// A border is drawn as the ring between the box's outer and inner rounded rectangles, and where the sides
/// disagree on colour the ring is cut between them. The cut has to run through the corner's arc - both
/// halves of the arc belong to a side - and it has to be kept to the band the side lays, or a corner is
/// left showing the background through a notch, or one side paints over the other.
/// </remarks>
[TestFixture]
public sealed class BorderCornerTests
{
    [Test]
    public async Task TheArcOfACommonCornerIsPaintedByTheSideThatOwnsIt()
    {
        using var view = new HutuViewInternal(new SkiaTextMeasurer());
        view.SetViewportSize(120, 120);

        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div style='width:40px;height:40px;border:4px solid;border-color:rgb(255,0,0) rgb(0,128,0) rgb(0,0,255) rgb(255,165,0);" +
            "border-radius:20px;background:rgb(234,244,255)'></div>" +
            "</body></html>");

        using var bitmap = Render(view);

        // The top-left corner's arc is centred at (20,20) and the band is four pixels thick, so it runs
        // between twelve and twenty pixels out from that centre. The forty-five degree line through the
        // centre divides it: above that line the top side's ink, below it the left side's - and no part of
        // the arc belongs to neither, which is what a mitre drawn between the sides' tangent points left.
        Assert.Multiple(() =>
        {
            Assert.That(Pixel(bitmap, 9, 5), Is.EqualTo(new SKColor(255, 0, 0)), "the top side's half of the arc");
            Assert.That(Pixel(bitmap, 5, 9), Is.EqualTo(new SKColor(255, 165, 0)), "the left side's half");
            Assert.That(Pixel(bitmap, 4, 8), Is.EqualTo(new SKColor(255, 165, 0)), "and the corner holds no background");
        });
    }

    [Test]
    public async Task ACutInlineFragmentKeepsTheArcOfTheEndItWasCutAt()
    {
        using var view = new HutuViewInternal(new SkiaTextMeasurer());
        view.SetViewportSize(200, 160);

        // A box broken across a line has no border on the side the break falls on, so its sides disagree
        // about their widths and the ring is cut between them - which is where the corner's arc used to be
        // cut away with the mitre and the background showed through.
        await view.SetHtml(
            "<html><body style='margin:0;font:16px Arial'>" +
            "<div style='width:120px;line-height:2'>" +
            "<span style='border:6px solid rgb(43,124,211);border-radius:12px;padding:0 6px;" +
            "background:rgb(234,244,255)'>words that wrap onto another line</span></div>" +
            "</body></html>");

        using var bitmap = Render(view);

        // The first fragment's left end is a complete corner. Its arc is centred at (18, 12) with a band from
        // 6 to 12 pixels out; (9, 12) stands in the middle of that band, where the corner used to be empty.
        Assert.That(Pixel(bitmap, 5, 6), Is.EqualTo(new SKColor(43, 124, 211)), "the border runs through the corner");
    }

    private static SKBitmap Render(HutuViewInternal view)
    {
        var paint = view.Paint ?? throw new InvalidOperationException("the page produced no paint list");
        var width = Math.Max(1, (Int32)Math.Ceiling(paint.Bounds.Width));
        var height = Math.Max(1, (Int32)Math.Ceiling(paint.Bounds.Height));

        using var surface = SKSurface.Create(new SKImageInfo(width, height));
        surface.Canvas.Clear(SKColors.White);
        new SkiaPaintExecutor(new SkiaTextMeasurer()).Execute(paint, surface.Canvas);

        using var image = surface.Snapshot();
        return SKBitmap.FromImage(image);
    }

    private static SKColor Pixel(SKBitmap bitmap, Int32 x, Int32 y) => bitmap.GetPixel(x, y);
}

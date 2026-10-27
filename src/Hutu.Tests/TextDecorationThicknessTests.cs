using Hutu.Skia;
using NUnit.Framework;
using SkiaSharp;
using System;
using System.Threading.Tasks;

namespace Hutu.Tests;

/// <summary>
/// Covers which way a decoration line's band is laid from the edge it stands on: an underline thickens
/// away from the text it stands under, an overline away from the text it stands over, and a line-through
/// out from the middle of the letters it crosses (CSS Text Decoration 4 §2.4, §2.5).
/// </summary>
/// <remarks>
/// The answers are read out of the ink the engine really drew, measured twice for each kind of line - once
/// thin and once thick - so the ruler is the thin line's own edges. A band that grew towards the text
/// rather than away from it would keep one edge where it was and move the other the wrong way, which is
/// what the borrowed pages look for: they set a line thicker than the box it stands in and ask that the
/// box be covered, which only the far side of the edge can do.
/// </remarks>
[TestFixture]
public sealed class TextDecorationThicknessTests
{
    private const String Family = "Segoe UI";

    [Test]
    public async Task AThickerUnderlineGrowsAwayFromTheTextItStandsUnder()
    {
        var thin = await Rows("underline", "4px");
        var thick = await Rows("underline", "24px");

        Assert.Multiple(() =>
        {
            Assert.That(thick.Head, Is.EqualTo(thin.Head),
                "the edge the line was measured to does not move when the line is thickened");
            Assert.That(thick.Foot - thin.Foot, Is.GreaterThan(10),
                "and the band reaches further down, away from the text above it");
        });
    }

    [Test]
    public async Task AThickerOverlineGrowsAwayFromTheTextItStandsOver()
    {
        var thin = await Rows("overline", "4px");
        var thick = await Rows("overline", "24px");

        Assert.Multiple(() =>
        {
            Assert.That(thick.Foot, Is.EqualTo(thin.Foot),
                "the edge the line was measured to holds its place");
            Assert.That(thin.Head - thick.Head, Is.GreaterThan(10),
                "and the band reaches further up, away from the text below it");
        });
    }

    [Test]
    public async Task AThickerLineThroughGrowsOnBothSidesOfTheLettersItCrosses()
    {
        var thin = await Rows("line-through", "4px");
        var thick = await Rows("line-through", "24px");

        // Measured: the thin band spans rows 84..87 and the thick one 74..97 - each edge is ten further
        // out than it was, so the middle the line was struck at stayed where it is.
        var up = thin.Head - thick.Head;
        var down = thick.Foot - thin.Foot;

        Assert.Multiple(() =>
        {
            Assert.That(up, Is.GreaterThan(5), "the band reaches above where the thin line was");
            Assert.That(down, Is.GreaterThan(5), "and below where it was");
            Assert.That(up, Is.EqualTo(down).Within(1),
                "and it grows evenly on both sides, which is what having the line through the middle of " +
                "the letters it crosses asks of a thicker one");
        });
    }

    /// <summary>
    /// A length of a decoration written in <c>calc()</c> answers to the same footing the property gives a
    /// bare value of the same unit: a percentage of the size the text is set at, and an <c>em</c> of that
    /// size too - so <c>calc(50% + 20px)</c> on text set at forty pixels is forty pixels wide, the same
    /// line as <c>40px</c> writes (CSS Text Decoration 4 §2.4). Settling the sum the way the box model's
    /// reader does instead - a share of the window the page is drawn across, an <c>em</c> of the device's
    /// own font - turns a eight-pixel line into a band of hundreds.
    /// </summary>
    [Test]
    public async Task ASumInADecorationLengthKeepsTheUnitsThePropertyGivesThem()
    {
        var stated = await Rows("underline", "40px");
        var share = await Rows("underline", "calc(50% + 20px)");
        var distance = await Rows("underline", "calc(0.5em + 20px)");

        Assert.Multiple(() =>
        {
            Assert.That(share, Is.EqualTo(stated),
                "a half of the text's size added to twenty pixels is the forty pixels the line asked for");
            Assert.That(distance, Is.EqualTo(stated),
                "and an em means this text's em, the same forty");
        });
    }

    /// <summary>
    /// Gets the first and last page row that carry the line's ink, for one kind of line set at the given
    /// thickness.
    /// </summary>
    private static async Task<(Int32 Head, Int32 Foot)> Rows(String kind, String thickness)
    {
        using var checking = new SkiaTextMeasurer();

        Assume.That(
            checking.GetMetrics(new Hutu.Rendering.Layout.FontStyle(Family, 40)).Ascent,
            Is.GreaterThan(0),
            $"no {Family} on this machine");

        using var view = new HutuViewInternal(new SkiaTextMeasurer());
        view.SetViewportSize(400, 300);

        await view.SetHtml(
            // A block above the line's own box, so that a line thickened past the head of its text still
            // has page above it to be drawn on: read against the top of the window a band that ran out of
            // room would look as though it stopped where it was placed.
            "<html><body style='margin:0'><div style='margin:0;height:40px'></div>" +
            "<div style='margin:0;font-family:" + Family + ";font-size:40px;" +
            "line-height:2;text-decoration:" + kind + ";text-decoration-color:red;" +
            "text-decoration-skip-ink:none;text-decoration-thickness:" + thickness + "'>ooo</div></body></html>");

        var paint = view.Paint ?? throw new InvalidOperationException("the page produced no paint list");
        var width = Math.Max(1, (Int32)Math.Ceiling(paint.Bounds.Width));
        var height = Math.Max(1, (Int32)Math.Ceiling(paint.Bounds.Height));

        using var surface = SKSurface.Create(new SKImageInfo(width, height));
        surface.Canvas.Clear(SKColors.White);
        new SkiaPaintExecutor(new SkiaTextMeasurer()).Execute(paint, surface.Canvas);
        using var image = surface.Snapshot();
        using var bitmap = SKBitmap.FromImage(image);

        var head = -1;
        var foot = -1;

        for (var row = 0; row < height; row++)
        {
            for (var column = 0; column < width; column++)
            {
                var pixel = bitmap.GetPixel(column, row);

                if (pixel.Red > 120 && pixel.Green < 80 && pixel.Blue < 80)
                {
                    if (head < 0)
                    {
                        head = row;
                    }

                    foot = row;
                    break;
                }
            }
        }

        Assert.That(head, Is.GreaterThan(-1), $"no {kind} ink was drawn at all");

        return (head, foot);
    }
}

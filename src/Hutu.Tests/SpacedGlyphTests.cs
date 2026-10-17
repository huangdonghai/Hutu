using Hutu.Rendering.Layout;
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
/// Covers where the ink of spaced text falls against the room the layout charged for it: a space is
/// owed after every character of the run, and the drawing has to reach the last one of them.
/// </summary>
/// <remarks>
/// Set against the real shaper, because it is the shaped run that carries the spacing into the canvas.
/// The assertions bound the ink rather than naming it, so any face that answers for the text will do.
/// </remarks>
[TestFixture]
public sealed class SpacedGlyphTests
{
    /// <summary>The spacing the run is set at, and the room it is owed after the last character.</summary>
    private const Double Spacing = 20;

    [Test]
    public async Task TheInkOfASpacedRunReachesTheRoomItWasCharged()
    {
        var text = Char.ConvertFromUtf32(0x20000) + Char.ConvertFromUtf32(0x20001);

        using var measurer = new SkiaTextMeasurer();
        using var view = new HutuViewInternal(measurer);
        view.SetViewportSize(300, 300);

        await view.SetHtml(
            "<html><body style='margin:0;font-size:40px'>" +
            "<p style='margin:0;letter-spacing:" + Spacing + "px'>" + text + "</p></body></html>");

        var box = Run(view).BorderBox;
        var ink = Ink(view.Paint!, measurer, box);

        Assume.That(ink.Right, Is.GreaterThan(ink.Left), "nothing was drawn for the ideographs on this machine");

        // The two characters are one run of ink and one space each: the second character is set a
        // spacing after the first, and only the space owed after the last is left uninked. Charging the
        // run for the units of the encoding instead leaves the whole of the drawing short of its box.
        Assert.That(
            box.Right - ink.Right,
            Is.LessThanOrEqualTo(Spacing + 6),
            "the run is not wider than its ink by more than the space after the last character");
        Assert.That(
            box.Right - ink.Right,
            Is.GreaterThanOrEqualTo(0),
            "and the ink does not reach past the room the layout gave it");
    }

    private static LayoutBox Run(HutuViewInternal view) =>
        Walk(view.Layout!.Root).Single(node => node.Kind == LayoutKind.Text);

    private static (Double Left, Double Right) Ink(PaintList paint, SkiaTextMeasurer measurer, Rect box)
    {
        var width = Math.Max(1, (Int32)Math.Ceiling(paint.Bounds.Width));
        var height = Math.Max(1, (Int32)Math.Ceiling(paint.Bounds.Height));

        using var surface = SKSurface.Create(new SKImageInfo(width, height));
        surface.Canvas.Clear(SKColors.White);
        new SkiaPaintExecutor(measurer).Execute(paint, surface.Canvas);

        using var image = surface.Snapshot();
        using var bitmap = SKBitmap.FromImage(image);

        var left = Double.PositiveInfinity;
        var right = Double.NegativeInfinity;

        var top = Math.Max(0, (Int32)Math.Floor(box.Y));
        var bottom = Math.Min(bitmap.Height - 1, (Int32)Math.Ceiling(box.Bottom));

        for (var y = top; y <= bottom; y++)
        {
            for (var x = 0; x < bitmap.Width; x++)
            {
                if (bitmap.GetPixel(x, y) != SKColors.White)
                {
                    left = Math.Min(left, x);
                    right = Math.Max(right, x);
                }
            }
        }

        return (left, right);
    }

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

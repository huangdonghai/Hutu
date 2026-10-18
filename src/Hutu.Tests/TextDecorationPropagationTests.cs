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
/// Covers what a decoration reaches and in what colour: the line an element asks for is drawn across the
/// text of the elements inside it, a descendant that asks for none takes none of it away, an atomic box
/// stops it on either side, and the line keeps the colour the decorating box settled on rather than the
/// colour of the glyphs it runs over (CSS Text Decoration 4 §2.1, §2.3, §2.5).
/// </summary>
/// <remarks>
/// The answers are counted out of the pixels the engine really drew, under the baseline of each run and
/// across its own width - the one place where a decoration line is the only ink there is, the letters
/// being written without descenders.
/// </remarks>
[TestFixture]
public sealed class TextDecorationPropagationTests
{
    private const String Family = "Segoe UI";

    [Test]
    public async Task ADecorationReachesTheTextOfTheElementsInsideIt()
    {
        var bands = await Read("aa<span>bb</span>cc", "underline");

        Assert.That(bands["aa"].Rows, Is.GreaterThan(0), "the run the element itself holds is lined");
        Assert.That(bands["bb"].Rows, Is.EqualTo(bands["aa"].Rows), "and so is the one inside the span");
        Assert.That(bands["cc"].Rows, Is.EqualTo(bands["aa"].Rows), "and the one after it");
    }

    [Test]
    public async Task AChildThatAsksForNothingKeepsTheLineItRanInto()
    {
        // The property asks a box to decorate its own text; it has nothing to say about a line an
        // ancestor already asked for, which runs on through the child as if nothing had been said here.
        var bands = await Read("aa<span style='text-decoration:none'>bb</span>cc", "underline");

        Assert.That(bands["bb"].Rows, Is.GreaterThan(0), "the parent's line is not taken away");
    }

    [Test]
    public async Task AnAtomicBoxStopsTheLineOnEitherSideOfIt()
    {
        // An inline block lays its own content out and is placed as one unit, so the decoration the
        // container asked for neither reaches into it nor is drawn across it: the line stops at its
        // edges and resumes beyond them.
        var bands = await Read("aa<span style='display:inline-block'>bb</span>cc", "underline");

        Assert.That(bands["aa"].Rows, Is.GreaterThan(0), "the line runs under the text before it");
        Assert.That(bands["bb"].Rows, Is.EqualTo(0), "and not across the box set as a unit");
        Assert.That(bands["cc"].Rows, Is.GreaterThan(0), "resuming after it");
    }

    [Test]
    public async Task TheLineKeepsTheColourTheDecoratingBoxSettledOn()
    {
        // The line belongs to the box that asked for it, in the colour that box settled on - the text it
        // runs across is blue and the line over it is still red (CSS Text Decoration 4 §2.3).
        var bands = await Read("aa<span style='color:blue'>bb</span>cc", "underline; text-decoration-color:red");

        Assert.That(bands["bb"].Rows, Is.GreaterThan(0), "the descendant's text is lined too");
        Assert.That(bands["bb"].Colour, Is.EqualTo(0xFFFF0000), "in the colour of the box that decorates");
    }

    [Test]
    public async Task ADecorationRunsBesideAColumnItReaches()
    {
        // The same propagation down a column, where the line runs beside the text instead of under it.
        // The letters are drawn the same either way, so whatever ink the decoration adds to a run's own
        // band is the line reaching that run - and it has to reach the descendant's as well.
        var decorated = await Column(true);
        var plain = await Column(false);

        Assert.That(decorated.Keys, Is.EquivalentTo(plain.Keys), "the same runs are laid out either way");

        foreach (var text in decorated.Keys)
        {
            Assert.That(
                decorated[text],
                Is.GreaterThan(plain[text]),
                $"the line runs beside the text of '{text}'");
        }
    }

    /// <summary>Draws the page and reports how much ink stands under each run's baseline.</summary>
    private static async Task<Dictionary<String, Band>> Read(String content, String decoration)
    {
        using var view = await Loaded(
            $"<p style='margin:0;font-family:{Family};font-size:40px;line-height:2;" +
            $"text-decoration:{decoration}'>{content}</p>");

        var bands = new Dictionary<String, Band>();

        foreach (var run in Walk(view.Layout!.Root).Where(node => node.Kind == LayoutKind.Text))
        {
            bands[run.Text!] = Band.Of(view, run);
        }

        return bands;
    }

    /// <summary>Draws the underlined column, or the same column with nothing on it, and counts the ink
    /// each run's own band carries.</summary>
    private static async Task<Dictionary<String, Int32>> Column(Boolean decorated)
    {
        using var view = await Loaded(
            "<div style='writing-mode:vertical-rl;text-decoration:" + (decorated ? "underline" : "none") +
            "'>aa<span>bb</span></div>");

        var counts = new Dictionary<String, Int32>();

        foreach (var run in Walk(view.Layout!.Root).Where(node => node.Kind == LayoutKind.Text))
        {
            counts[run.Text!] = InkInRunBand(view, run);
        }

        return counts;
    }

    private static async Task<HutuViewInternal> Loaded(String html)
    {
        using var checking = new SkiaTextMeasurer();

        Assume.That(
            checking.GetMetrics(new FontStyle(Family, 40)).Ascent,
            Is.GreaterThan(0),
            $"no {Family} on this machine");

        var view = new HutuViewInternal(new SkiaTextMeasurer());
        view.SetViewportSize(400, 240);
        await view.SetHtml("<html><body style='margin:0'>" + html + "</body></html>");

        return view;
    }

    /// <summary>Paints the document into a bitmap the caller owns, beside the paint it was drawn from.</summary>
    /// <summary>Counts the inked pixels that stand inside the band a run's own box covers.</summary>
    private static Int32 InkInRunBand(HutuViewInternal view, LayoutBox run)
    {
        var (paint, bitmap) = Drawn(view);

        try
        {
            var left = (Int32)Math.Round(run.BorderBox.X - paint.Bounds.X);
            var right = (Int32)Math.Round(run.BorderBox.Right - paint.Bounds.X);
            var top = (Int32)Math.Round(run.BorderBox.Y - paint.Bounds.Y);
            var bottom = (Int32)Math.Round(run.BorderBox.Bottom - paint.Bounds.Y);
            var pixels = 0;

            for (var y = Math.Max(0, top); y < Math.Min(bottom, bitmap.Height); y++)
            {
                for (var x = Math.Max(0, left); x < Math.Min(right, bitmap.Width); x++)
                {
                    if (bitmap.GetPixel(x, y) != SKColors.White)
                    {
                        pixels++;
                    }
                }
            }

            return pixels;
        }
        finally
        {
            bitmap.Dispose();
        }
    }

    private static (PaintList Paint, SKBitmap Bitmap) Drawn(HutuViewInternal view)
    {
        var paint = view.Paint!;
        using var measurer = new SkiaTextMeasurer();

        var width = Math.Max(1, (Int32)Math.Ceiling(paint.Bounds.Width));
        var height = Math.Max(1, (Int32)Math.Ceiling(paint.Bounds.Height));

        using var surface = SKSurface.Create(new SKImageInfo(width, height));
        surface.Canvas.Clear(SKColors.White);
        new SkiaPaintExecutor(measurer).Execute(paint, surface.Canvas);

        using var image = surface.Snapshot();

        return (paint, SKBitmap.FromImage(image));
    }

    /// <summary>The ink one run of text is drawn under: how many rows of it, and the colour of the first.</summary>
    private readonly struct Band
    {
        public Band(Int32 rows, UInt32 colour)
        {
            Rows = rows;
            Colour = colour;
        }

        public Int32 Rows { get; }

        public UInt32 Colour { get; }

        public static Band Of(HutuViewInternal view, LayoutBox run)
        {
            var (paint, bitmap) = Drawn(view);

            try
            {
                var baseline = (Int32)Math.Round(run.BorderBox.Y + run.Baseline - paint.Bounds.Y);
                var left = (Int32)Math.Round(run.BorderBox.X - paint.Bounds.X);
                var right = (Int32)Math.Round(run.BorderBox.Right - paint.Bounds.X);

                var rows = 0;
                var colour = 0u;
                var depth = 0;

                for (var y = baseline + 2; y < Math.Min(baseline + 24, bitmap.Height); y++)
                {
                    var inked = false;

                    for (var x = left; x < right - 2 && x < bitmap.Width; x++)
                    {
                        var pixel = bitmap.GetPixel(x, y);

                        if (pixel != SKColors.White)
                        {
                            var strength = 255 - Math.Min(pixel.Red, Math.Min(pixel.Green, pixel.Blue));

                            // The rows of an antialiased line fade at their edges; the colour worth
                            // reporting is the most solid pixel the band holds, not the first one met.
                            if (strength >= depth)
                            {
                                depth = strength;
                                colour = (UInt32)(((UInt32)pixel.Alpha << 24) | ((UInt32)pixel.Red << 16) |
                                    ((UInt32)pixel.Green << 8) | pixel.Blue);
                            }

                            inked = true;
                        }
                    }

                    if (inked)
                    {
                        rows++;
                    }
                }

                return new Band(rows, colour);
            }
            finally
            {
                bitmap.Dispose();
            }
        }
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

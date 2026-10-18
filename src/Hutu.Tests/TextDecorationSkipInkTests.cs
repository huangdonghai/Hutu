using Hutu.Rendering.Layout;
using Hutu.Skia;
using NUnit.Framework;
using SkiaSharp;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Hutu.Tests;

/// <summary>
/// Covers a line being broken where the text's ink stands in the way: that it is broken for a descender
/// under <c>auto</c> and drawn straight through under <c>none</c>, that a script whose glyphs are written
/// across the rule keeps it whole under <c>auto</c> and breaks it under <c>all</c>, and that the same
/// holds where the page runs down (CSS Text Decoration 4 §2.10.4).
/// </summary>
/// <remarks>
/// The answers are counted out of the ink the engine really drew: the number of page columns in the band
/// the line stands in that carry any ink at all. A rule drawn across the whole run counts as many columns
/// as the run is wide; one broken around the letters counts fewer.
/// </remarks>
[TestFixture]
public sealed class TextDecorationSkipInkTests
{
    private const String Family = "Segoe UI";

    [Test]
    public async Task ALetterThatDescendsBreaksTheLineUnderAuto()
    {
        // The line stops short of the ink of 'p' rather than running through it, and is drawn whole again
        // where the page says nothing is to be skipped.
        var round = await Columns("ooo");
        var broken = await Columns("ppp");
        var whole = await Columns("ppp", "text-decoration-skip-ink:none");

        Assert.That(round - broken, Is.GreaterThan(4), "the descenders took room out of the rule");
        Assert.That(whole - broken, Is.GreaterThan(4), "and none asks for the rule unbroken");
    }

    [Test]
    public async Task AnIdeographInTheTextKeepsTheLineWholeUnderAuto()
    {
        // Where the text is set in a script written across the rule, auto asks nothing of the line - even
        // for the letter in it that would break the line elsewhere. All breaks it whatever the script is
        // (CSS Text Decoration 4 §2.10.4).
        var auto = await Columns("p開", "font-family:Yu Gothic");
        var none = await Columns("p開", "font-family:Yu Gothic;text-decoration-skip-ink:none");
        var every = await Columns("p開", "font-family:Yu Gothic;text-decoration-skip-ink:all");

        Assert.That(none, Is.LessThan(120), "the row of letters is wide enough to show a break");
        Assert.That(auto, Is.EqualTo(none), "auto leaves it whole where the script stands across the rule");
        Assert.That(every, Is.LessThan(auto), "and all breaks it whatever the script");
    }

    [Test]
    public async Task AColumnBreaksItsLineAtTheInkToo()
    {
        // The same rule down a column, where the line runs beside turned text: the ink is asked of the
        // glyphs as they are drawn there, not of the row they would stand on.
        var round = await Columns("ooo", column: true);
        var broken = await Columns("ppp", column: true);

        Assert.That(broken, Is.LessThan(round), "the turned descenders take room out of the rule");
    }

    /// <summary>
    /// Counts the page columns in the band a run's line stands in that carry ink, over one underlined row
    /// of the text - or down one column of it, where the band is across the column instead.
    /// </summary>
    private static async Task<Int32> Columns(
        String content, String extra = "", Boolean column = false)
    {
        using var checking = new SkiaTextMeasurer();

        Assume.That(
            checking.GetMetrics(new FontStyle(Family, 40)).Ascent,
            Is.GreaterThan(0),
            $"no {Family} on this machine");

        using var view = new HutuViewInternal(new SkiaTextMeasurer());
        view.SetViewportSize(400, 300);

        await view.SetHtml(
            "<html><body style='margin:0'><div style='margin:0;font-family:" + Family + ";font-size:40px;" +
            "line-height:2;text-decoration:underline;text-decoration-color:red;" + extra +
            (column ? "writing-mode:vertical-rl;" : "") + "'>" + content + "</div></body></html>");

        var run = Walk(view.Layout!.Root).Single(node => node.Kind == LayoutKind.Text);
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
        var inked = 0;

        if (!column)
        {
            // The band the rule stands in: below the baseline as far as a thick underline can reach.
            var top = (Int32)Math.Round(box.Y + run.Baseline - paint.Bounds.Y);
            var bottom = top + (Int32)Math.Ceiling(Math.Max(metrics.UnderlineThickness, 4) + 12);
            var left = (Int32)Math.Round(box.X - paint.Bounds.X);
            var right = (Int32)Math.Round(box.Right - paint.Bounds.X);

            for (var at = left; at < right; at++)
            {
                for (var side = top; side < Math.Min(bottom, height); side++)
                {
                    if (Lined(bitmap.GetPixel(at, side)))
                    {
                        inked++;
                        break;
                    }
                }
            }

            return inked;
        }

        // Down a column the rule runs beside the text, so it is the same count taken across the column.
        var head = (Int32)Math.Round(box.Y - paint.Bounds.Y);
        var foot = (Int32)Math.Round(box.Bottom - paint.Bounds.Y);
        var near = (Int32)Math.Round(box.X - paint.Bounds.X);
        var far = near + (Int32)Math.Round(box.Width) + (Int32)metrics.Ascent;

        for (var side = head; side < foot; side++)
        {
            for (var at = near; at < Math.Min(far, width); at++)
            {
                if (Lined(bitmap.GetPixel(at, side)))
                {
                    inked++;
                    break;
                }
            }
        }

        return inked;
    }

    private static Boolean Lined(SKColor pixel) =>
        pixel.Red > 60 && pixel.Green < 120 && pixel.Blue < 120;

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
}

using Hutu.Rendering.Layout;
using Hutu.Skia;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Hutu.Tests;

/// <summary>
/// Covers the scripts a font has to shape rather than map one character to one glyph, and the
/// right-to-left direction those scripts read in: Arabic letters join into their contextual forms,
/// and a right-to-left line is laid out from its right edge.
/// </summary>
[TestFixture]
public sealed class ArabicTextTests
{
    /// <summary>A word whose letters join, and whose lam-alef pair is a ligature of its own.</summary>
    private const String Word = "مرحبا";
    private const String LamAlef = "لا";

    [Test]
    public void ArabicLettersAreShapedIntoTheirJoinedForms()
    {
        using var measurer = new SkiaTextMeasurer();
        var style = new FontStyle("Arial", 24);
        var font = measurer.GetFont(style);

        Assume.That(measurer.HasGlyph(style, 'ل'), Is.True, "no system font carries Arabic on this machine");

        // Three letters shaped as a word: the picture the font makes of them is not the three glyphs
        // the characters map to on their own, which is the whole point of shaping a joining script.
        var shaped = measurer.Shaper.Shape(font, Word);

        Assert.That(shaped.Succeeded, Is.True, "the shaper has to answer for Arabic");
        Assert.That(shaped.Glyphs, Has.Length.EqualTo(Word.Length), "one glyph per letter, joined in place");
        Assert.That(shaped.Points, Has.Length.EqualTo(shaped.Glyphs.Length));

        // Every glyph but the first advances the pen; a run of no width, or with its glyphs stacked on
        // the origin, would be a run that was never laid out along its baseline.
        Assert.That(shaped.Advance, Is.GreaterThan(0));
        Assert.That(shaped.Points[^1].X, Is.GreaterThan(0), "the last glyph sits along the run");
    }

    [Test]
    public void LamAndAlefBecomeASingleLigatureGlyph()
    {
        using var measurer = new SkiaTextMeasurer();
        var style = new FontStyle("Arial", 24);
        var font = measurer.GetFont(style);

        Assume.That(measurer.HasGlyph(style, 'ل'), Is.True, "no system font carries Arabic on this machine");

        // Two characters the font welds into one glyph. Shaping them apart would leave two glyphs, so
        // the count is what tells a shaper from a straight character-to-glyph mapping.
        Assert.That(measurer.Shaper.Shape(font, LamAlef).Glyphs, Has.Length.EqualTo(1));
        Assert.That(measurer.Shaper.Shape(font, "ل").Glyphs, Has.Length.EqualTo(1));
    }

    [Test]
    public void ArabicIsMeasuredByItsShapedWidth()
    {
        using var measurer = new SkiaTextMeasurer();
        var style = new FontStyle("Arial", 24);

        Assume.That(measurer.HasGlyph(style, 'ل'), Is.True, "no system font carries Arabic on this machine");

        var shaped = measurer.Shaper.Shape(measurer.GetFont(style), Word);

        Assert.That(
            measurer.MeasureWidth(Word, style),
            Is.EqualTo(shaped.Advance).Within(0.0001),
            "the layout has to advance by the glyphs that will be drawn, not by the characters");
    }

    [Test]
    public async Task ARightToLeftParagraphStartsAtItsRightEdge()
    {
        using var measurer = new SkiaTextMeasurer();
        using var view = new HutuViewInternal(measurer);
        view.SetViewportSize(200, 100);

        // The `dir` attribute is the presentational hint the engine reads when no rule states a
        // direction, which is how a page in the sample sets its Arabic.
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<p dir='rtl' style='margin:0;width:100px'>abc</p></body></html>");

        var run = Text(view, "abc");

        Assert.That(run.BorderBox.Right, Is.EqualTo(100).Within(0.001), "the line begins against the right edge");
        Assert.That(run.BorderBox.X, Is.GreaterThan(0), "so the run sits away from the left one");
    }

    [Test]
    public async Task TheDirectionPropertyAsksForTheSameThingAsTheAttribute()
    {
        using var measurer = new SkiaTextMeasurer();
        using var view = new HutuViewInternal(measurer);
        view.SetViewportSize(200, 100);

        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<p style='margin:0;width:100px;direction:rtl'>abc</p></body></html>");

        Assert.That(Text(view, "abc").BorderBox.Right, Is.EqualTo(100).Within(0.001));
    }

    [Test]
    public async Task ALeftToRightParagraphStillStartsAtItsLeftEdge()
    {
        using var measurer = new SkiaTextMeasurer();
        using var view = new HutuViewInternal(measurer);
        view.SetViewportSize(200, 100);

        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<p style='margin:0;width:100px'>abc</p></body></html>");

        var run = Text(view, "abc");

        Assert.That(run.BorderBox.X, Is.EqualTo(0).Within(0.001), "the default direction is unchanged");
    }

    [Test]
    public async Task TheArabicSamplePagePaintsItsJoinedText()
    {
        using var measurer = new SkiaTextMeasurer();
        using var view = new HutuViewInternal(measurer);

        Assume.That(measurer.HasGlyph(new FontStyle("Tahoma", 22), 'م'), Is.True, "no system font carries Arabic on this machine");

        view.SetViewportSize(700, 900);

        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<p dir='rtl' style='margin:0;font-family:Tahoma;font-size:22px'>مرحبا بالعالم</p>" +
            "</body></html>");

        var run = Text(view, "مرحبا بالعالم");

        using var bitmap = RenderToBitmap(view.Paint!, measurer);
        var ink = 0;

        for (var y = 0; y < bitmap.Height; y++)
        {
            for (var x = 0; x < bitmap.Width; x++)
            {
                if (bitmap.GetPixel(x, y) != SkiaSharp.SKColors.White)
                {
                    ink++;
                }
            }
        }

        Assert.That(ink, Is.GreaterThan(0), "the shaped Arabic has to reach the canvas");
        Assert.That(run.BorderBox.Right, Is.EqualTo(700).Within(0.001), "and it is set against the right edge");
    }

    private static LayoutBox Text(HutuViewInternal view, String text) =>
        Walk(view.Layout!.Root).Single(node => node.Kind == LayoutKind.Text && node.Text == text);

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

    private static SkiaSharp.SKBitmap RenderToBitmap(Hutu.Rendering.Paint.PaintList paint, SkiaTextMeasurer measurer)
    {
        var width = Math.Max(1, (Int32)Math.Ceiling(paint.Bounds.Width));
        var height = Math.Max(1, (Int32)Math.Ceiling(paint.Bounds.Height));

        using var surface = SkiaSharp.SKSurface.Create(new SkiaSharp.SKImageInfo(width, height));
        surface.Canvas.Clear(SkiaSharp.SKColors.White);
        new SkiaPaintExecutor(measurer).Execute(paint, surface.Canvas);

        using var image = surface.Snapshot();
        return SkiaSharp.SKBitmap.FromImage(image);
    }
}

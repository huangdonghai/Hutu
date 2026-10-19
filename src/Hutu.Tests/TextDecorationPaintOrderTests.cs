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
/// Covers which of a run's ink and its decoration lines lies on top: the lines stood off the baseline are
/// drawn under the text and the line that crosses the letters is drawn over them
/// (CSS Text Decoration 4 §5.1, bottommost first: shadows, underlines, overlines, text, emphasis marks,
/// line-through).
/// </summary>
/// <remarks>
/// <para>
/// The answer is read where a glyph and a line really do stand on one another, which is what makes the
/// order visible rather than merely implied: a lowercase p carries its stem down across the rule under it,
/// and a struck letter is crossed through the middle of its own ink. The same text is painted twice, with
/// the line and without it, so the letters say which pixels the line had to pass over - and the rows the
/// line stands in are read out of what it drew, rather than guessed from the font's metrics.
/// </para>
/// <para>
/// Set in Segoe UI with the fill blue and the line red, because which of the two a pixel carries says
/// which was laid down last; <c>text-decoration-skip-ink: none</c> keeps the rule unbroken, so that there
/// is an overlap to read at all.
/// </para>
/// </remarks>
[TestFixture]
public sealed class TextDecorationPaintOrderTests
{
    private const String Text = "pppppp";
    private const String Family = "Segoe UI";

    private const Int32 Letter = 0;
    private const Int32 Line = 1;

    [Test]
    public async Task AnUnderlineStandsUnderTheInkItRunsThrough()
    {
        var band = await Band("underline");

        Assert.Multiple(() =>
        {
            Assert.That(band.Rows, Is.GreaterThan(0), "the rule is drawn, on rows of its own");
            Assert.That(
                band.Ink,
                Is.GreaterThan(band.Covered),
                "where the descender's ink lies the letter keeps its colour, so the rule went down first");
        });
    }

    [Test]
    public async Task ALineThroughLiesOverTheInkItCrosses()
    {
        var band = await Band("line-through");

        Assert.Multiple(() =>
        {
            Assert.That(band.Rows, Is.GreaterThan(0), "the strike is drawn, on rows of its own");
            Assert.That(
                band.Covered,
                Is.GreaterThan(band.Ink),
                "the line that crosses the text is laid over it, which is where §5.1 puts it last");
        });
    }

    /// <summary>
    /// One line's band: the rows it stands in, how many pixels of the letters' own ink there keep the
    /// letter's colour, and how many carry the line's instead.
    /// </summary>
    private readonly record struct LineBand(Int32 Rows, Int32 Ink, Int32 Covered);

    /// <summary>
    /// Reads one line's band out of the two pages. The rows that carry the line's colour where the letters
    /// alone leave the page blank are the rows it stands in, and what those rows hold over the letters' own
    /// ink tells which of the two was laid down last.
    /// </summary>
    private static async Task<LineBand> Band(String decoration)
    {
        using var measurer = new SkiaTextMeasurer();

        Assume.That(
            measurer.GetMetrics(new FontStyle(Family, 40)).Ascent,
            Is.GreaterThan(0),
            $"no {Family} on this machine");

        var plain = await Page(measurer, "none");
        var lined = await Page(measurer, decoration);

        var rows = new List<Int32>();

        for (var y = 0; y < lined.Height; y++)
        {
            var gaps = 0;

            for (var x = 0; x < lined.Width; x++)
            {
                if (InkOf(plain.Bitmap, x, y) != Line && InkOf(lined.Bitmap, x, y) == Line)
                {
                    gaps++;
                }
            }

            // A row of the line's own carries its colour wherever the letters leave a gap; a row the
            // letters cover from edge to edge is not read as the line's.
            if (gaps > 10)
            {
                rows.Add(y);
            }
        }

        var ink = 0;
        var covered = 0;

        foreach (var y in rows)
        {
            for (var x = 0; x < lined.Width; x++)
            {
                // Only where the letters alone put ink - the line is not asked to cover blank page, and a
                // pixel that came out a mix of the two says nothing either way.
                if (InkOf(plain.Bitmap, x, y) != Letter)
                {
                    continue;
                }

                var over = InkOf(lined.Bitmap, x, y);

                if (over == Letter)
                {
                    ink++;
                }
                else if (over == Line)
                {
                    covered++;
                }
            }
        }

        return new LineBand(rows.Count, ink, covered);
    }

    /// <summary>
    /// Gets which colour a pixel carries - the blue of the letters, the red of the line - or neither, which
    /// covers white page and the mix along an antialiased edge alike.
    /// </summary>
    private static Int32 InkOf(SKBitmap bitmap, Int32 x, Int32 y)
    {
        var pixel = bitmap.GetPixel(x, y);

        if (pixel.Blue > pixel.Red + 40)
        {
            return Letter;
        }

        return pixel.Red > pixel.Blue + 40 ? Line : -1;
    }

    /// <summary>
    /// One page of the same text, painted with the line the page asks for, over an area big enough to hold
    /// it: the paint list's own bounds, so the two pages are read at the same pixels.
    /// </summary>
    private static async Task<(SKBitmap Bitmap, Int32 Width, Int32 Height)> Page(
        SkiaTextMeasurer measurer, String decoration)
    {
        using var view = new HutuViewInternal(measurer);
        view.SetViewportSize(400, 200);

        await view.SetHtml(
            "<html><body style='margin:0'>" +
            $"<p style='margin:0;font-family:{Family};font-size:40px;line-height:2;color:#0000ff;" +
            $"text-decoration:{decoration};text-decoration-color:#ff0000;text-decoration-skip-ink:none'>" +
            $"{Text}</p></body></html>");

        var run = Walk(view.Layout!.Root).Single(node => node.Kind == LayoutKind.Text);
        var paint = view.Paint!;

        Assert.That(
            paint.Commands.OfType<TextCommand>().Single(c => c.Text == Text).Decorations.Count,
            Is.EqualTo(decoration == "none" ? 0 : 1),
            "the page asks for one decorating box, or for none");

        var width = Math.Max(1, (Int32)Math.Ceiling(paint.Bounds.Width));
        var height = Math.Max(1, (Int32)Math.Ceiling(paint.Bounds.Height));

        using var surface = SKSurface.Create(new SKImageInfo(width, height));
        surface.Canvas.Clear(SKColors.White);
        new SkiaPaintExecutor(measurer).Execute(paint, surface.Canvas);
        using var image = surface.Snapshot();

        return (SKBitmap.FromImage(image), width, height);
    }

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

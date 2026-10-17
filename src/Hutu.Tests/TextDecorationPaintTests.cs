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
/// Covers the lines a decoration draws: that an overline is drawn over the text rather than under it,
/// that a double draws two of them, and that the broken shapes come broken rather than whole.
/// </summary>
/// <remarks>
/// Set in Segoe UI with nothing but lowercase letters, because the em box above them is empty room an
/// overline can be seen to stand in - which a square font like Ahem, filling its own em box, would hide.
/// The answers are counted out of the pixels the engine really drew.
/// </remarks>
[TestFixture]
public sealed class TextDecorationPaintTests
{
    private const String Text = "xxxxxxxxxx";
    private const String Family = "Segoe UI";

    [Test]
    public async Task AnOverlineStandsAboveTheTextItDecorates()
    {
        var plain = await Ink(Text);
        var over = await Ink(Text, "overline");

        Assert.That(
            over.Topmost - plain.Topmost,
            Is.LessThanOrEqualTo(-4),
            "the ink reaches at least four rows higher than the letters alone");
        Assert.That(
            over.Bottommost,
            Is.EqualTo(plain.Bottommost).Within(2),
            "and nothing was drawn below the letters that was not there before");
    }

    [Test]
    public async Task AnUnderlineLiesBelowTheTextItDecorates()
    {
        var plain = await Ink(Text);
        var under = await Ink(Text, "underline");

        Assert.That(
            under.Bottommost - plain.Bottommost,
            Is.GreaterThanOrEqualTo(3),
            "the line is drawn below the letters, where the font asks for it");
        Assert.That(
            under.Topmost,
            Is.EqualTo(plain.Topmost).Within(2),
            "and it is not drawn over them as well");
    }

    [Test]
    public async Task ADoubleDecorationDrawsTwoBandsOfInk()
    {
        var solid = await Ink(Text, "underline solid");
        var doubled = await Ink(Text, "underline double");

        Assert.That(
            solid.Rows,
            Is.EqualTo(1),
            "a solid line is one band of ink under the letters");
        Assert.That(
            doubled.Rows,
            Is.EqualTo(2),
            "and a double is two of them with clear between");
    }

    [Test]
    public async Task ADashedOrDottedDecorationComesBroken()
    {
        var solid = await Ink(Text, "underline solid");
        var dashed = await Ink(Text, "underline dashed");
        var dotted = await Ink(Text, "underline dotted");

        Assert.That(solid.Columns, Is.EqualTo(1), "a solid line runs unbroken across the text");

        Assert.That(
            dashed.Columns,
            Is.GreaterThanOrEqualTo(3),
            "a dashed line is a row of separate dashes");
        Assert.That(
            dotted.Columns,
            Is.GreaterThanOrEqualTo(3),
            "and a dotted one is a row of separate dots");
    }

    private static async Task<InkExtent> Ink(String content, String decoration = "none")
    {
        using var measurer = new SkiaTextMeasurer();
        using var view = new HutuViewInternal(measurer);
        view.SetViewportSize(400, 200);

        Assume.That(
            measurer.GetMetrics(new FontStyle(Family, 40)).Ascent,
            Is.GreaterThan(0),
            $"no {Family} on this machine");

        await view.SetHtml(
            "<html><body style='margin:0'>" +
            $"<p style='margin:0;font-family:{Family};font-size:40px;line-height:2;" +
            $"text-decoration:{decoration}'>{content}</p></body></html>");

        var run = Walk(view.Layout!.Root).Single(node => node.Kind == LayoutKind.Text);

        return InkExtent.Of(view.Paint!, measurer, run);
    }

    /// <summary>
    /// The shape of the ink drawn under one text box: how far up and down it reaches, how many bands of
    /// rows lie below the baseline, and how many runs of columns the lowest of them is broken into.
    /// </summary>
    private readonly struct InkExtent
    {
        public InkExtent(Int32 topmost, Int32 bottommost, Int32 rows, Int32 columns)
        {
            Topmost = topmost;
            Bottommost = bottommost;
            Rows = rows;
            Columns = columns;
        }

        public Int32 Topmost { get; }

        public Int32 Bottommost { get; }

        public Int32 Rows { get; }

        public Int32 Columns { get; }

        public static InkExtent Of(PaintList paint, SkiaTextMeasurer measurer, LayoutBox run)
        {
            var width = Math.Max(1, (Int32)Math.Ceiling(paint.Bounds.Width));
            var height = Math.Max(1, (Int32)Math.Ceiling(paint.Bounds.Height));

            using var surface = SKSurface.Create(new SKImageInfo(width, height));
            surface.Canvas.Clear(SKColors.White);
            new SkiaPaintExecutor(measurer).Execute(paint, surface.Canvas);

            using var image = surface.Snapshot();
            using var bitmap = SKBitmap.FromImage(image);

            // The bitmap is laid out from the paint list's own origin, so the page coordinates
            // the run carries have to come back to it.
            var baseline = (Int32)Math.Round(run.BorderBox.Y + run.Baseline - paint.Bounds.Y);

            // Rows of ink, and the runs inside them, are counted only under the letters: the descender
            // space a lowercase x leaves is where a decoration line is the only ink there is.
            var inked = new Boolean[height];

            var top = height;
            var bottom = -1;

            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    if (bitmap.GetPixel(x, y) != SKColors.White)
                    {
                        inked[y] = true;
                        top = Math.Min(top, y);
                        bottom = Math.Max(bottom, y);
                        break;
                    }
                }
            }

            var from = Math.Min(height - 1, baseline + 1);
            var rows = Runs(inked, from, height - 1);

            // The lowest row that carries ink is the decoration's own, and how many runs its columns
            // hold says whether the line was drawn whole or in pieces.
            var columns = 0;

            for (var y = height - 1; y >= from; y--)
            {
                if (!inked[y])
                {
                    continue;
                }

                var inside = false;

                for (var x = 0; x < width; x++)
                {
                    var inkedHere = bitmap.GetPixel(x, y) != SKColors.White;

                    if (inkedHere && !inside)
                    {
                        columns++;
                        inside = true;
                    }
                    else if (!inkedHere)
                    {
                        inside = false;
                    }
                }

                break;
            }

            return new InkExtent(top, bottom, rows, columns);
        }

        /// <summary>Gets how many separate runs of inked entries the range holds.</summary>
        private static Int32 Runs(Boolean[] inked, Int32 start, Int32 end)
        {
            var runs = 0;
            var inside = false;

            for (var index = start; index <= end && index < inked.Length; index++)
            {
                if (inked[index] && !inside)
                {
                    runs++;
                    inside = true;
                }
                else if (!inked[index])
                {
                    inside = false;
                }
            }

            return runs;
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

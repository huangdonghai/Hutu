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
    public async Task HorizontalCjkAutoUnderlineUsesTheFontPosition()
    {
        const String cjk = "font-family:Microsoft JhengHei;font-size:40px;" +
            "text-decoration-color:red;text-decoration-skip-ink:none;text-decoration-thickness:2px";
        var auto = await Ink("中", "underline", cjk + ";text-underline-position:auto");
        var fromFont = await Ink("中", "underline", cjk + ";text-underline-position:from-font");

        Assert.That(
            auto.Bottommost,
            Is.EqualTo(fromFont.Bottommost).Within(2),
            "auto must not move a horizontal CJK underline down to the font descent");
    }

    [Test]
    public async Task HorizontalCjkUnderUsesTheBottomOfTheEmBox()
    {
        const String cjk = "font-family:Microsoft JhengHei;font-size:40px;" +
            "text-decoration-color:red;text-decoration-skip-ink:none;text-decoration-thickness:8px";
        var auto = await Ink("中", "underline", cjk + ";text-underline-position:auto");
        var under = await Ink("中", "underline", cjk + ";text-underline-position:under");

        Assert.That(
            under.Bottommost,
            Is.EqualTo(auto.Bottommost).Within(2),
            "under must use the em-box bottom, not the font's full hhea descent");
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

    /// <summary>
    /// <c>text-decoration-skip-spaces: start end</c> keeps the white space a decorated text opens and
    /// closes with free of the rule: the line stops at the first letter it has a reason to stand under,
    /// however much space the mode kept on either side of the letters (CSS Text Decoration 4 §2.9.2).
    /// Without the ask the same text is ruled straight through those spaces, which is the pair the
    /// comparison below states.
    /// </summary>
    [Test]
    public async Task ARuleAskedToSkipItsEndSpacesStopsAtTheFirstLetterUnderIt()
    {
        // The eight spaces on either side of the letters are worth sixty-four pixels of rule at the size
        // this text is set at, and the walk that pulls the ends in is where the ask is carried out.
        const String Spaced = "        " + Text + "        ";

        var whole = await Ends(Spaced, "");
        var skipped = await Ends(Spaced, "text-decoration-skip-spaces:start end;");
        var before = await Ends(Spaced, "text-decoration-skip-spaces:start;");

        Assert.Multiple(() =>
        {
            Assert.That(whole.Start, Is.EqualTo(0).Within(0.5), "nothing was pulled in");
            Assert.That(whole.End, Is.EqualTo(0).Within(0.5));
            Assert.That(skipped.Start, Is.EqualTo(40).Within(0.5), "the spaces the text opens with are left out");
            Assert.That(skipped.End, Is.EqualTo(40).Within(0.5), "and the ones it closes with");
            Assert.That(before.Start, Is.EqualTo(40).Within(0.5), "naming one end pulls that one in");
            Assert.That(before.End, Is.EqualTo(0).Within(0.5), "and only that one");
        });
    }

    private static async Task<(Double Start, Double End)> Ends(String content, String extra)
    {
        using var view = new HutuViewInternal(new FakeTextMeasurer());
        view.SetViewportSize(400, 200);

        await view.SetHtml(
            "<html><body style='margin:0'>" +
            $"<p style='margin:0;font-size:40px;white-space:pre;text-decoration:underline;{extra}'>" +
            $"{content}</p></body></html>");

        var run = Walk(view.Layout!.Root).Single(node => node.Kind == LayoutKind.Text);

        Assert.That(run.DecorationPlacements, Is.Not.Null.And.Not.Empty, "the run carries the line it asks for");

        return (run.DecorationPlacements![0].TrimStart, run.DecorationPlacements![0].TrimEnd);
    }

    private static async Task<InkExtent> Ink(String content, String decoration = "none", String extra = "")
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
            $"text-decoration:{decoration};{extra}'>{content}</p></body></html>");

        var run = Walk(view.Layout!.Root).Single(node => node.Kind == LayoutKind.Text);

        return InkExtent.Of(view.Paint!, measurer, run);
    }

    [Test]
    public async Task AThicknessWrittenInTheShorthandIsTheThicknessOfTheLine()
    {
        // The shorthand takes the thickness as one of its words (CSS Text Decoration 4 §2.6). A grammar
        // with no slot for it leaves the length unconsumed, and an unconsumed value is no value at all:
        // the whole declaration goes, taking the line with it. Both words have to reach the line.
        var asked = await Ink(Text, "8px underline");
        var faced = await Ink(Text, "underline");

        Assert.That(asked.Rows, Is.GreaterThan(0), "the line the shorthand asked for is drawn");
        Assert.That(
            asked.Bottommost - faced.Bottommost,
            Is.GreaterThanOrEqualTo(6),
            "and it is eight pixels wide where the face would have been two");
    }

    [Test]
    public async Task AThickerLineIsTheOneTheBoxAskedFor()
    {
        // The face's own width is where the line stops short of the ask: eight pixels of thickness reach
        // eight rows below the baseline, where the font's own answer reaches only its underline metric
        // (CSS Text Decoration 4 §2.4).
        var asked = await Ink(Text, "underline", "text-decoration-thickness:8px");
        var faced = await Ink(Text, "underline");

        Assert.That(asked.Bottommost - faced.Bottommost, Is.GreaterThanOrEqualTo(6), "the band grew by the ask");
    }

    [Test]
    public async Task APercentageThicknessIsAShareOfTheTextItIsSetAt()
    {
        // Half of the forty pixels the text is set at, which is what a share of the used font size means
        // (CSS Text Decoration 4 §2.4), against the face's own width.
        var share = await Ink(Text, "underline", "text-decoration-thickness:50%");
        var stated = await Ink(Text, "underline", "text-decoration-thickness:20px");

        Assert.That(
            share.Bottommost,
            Is.EqualTo(stated.Bottommost).Within(1),
            "half of the forty pixels the text is set at, which is what a share of it means");
    }

    [Test]
    public async Task AnUnderlineOffsetStandsTheLineOffTheText()
    {
        // The offset is a distance outward from where the font puts the line, and it moves the whole band
        // - the letters stay where they were (CSS Text Decoration 4 §2.8).
        var stood = await Ink(Text, "underline", "text-underline-offset:12px");
        var near = await Ink(Text, "underline");

        Assert.That(stood.Bottommost - near.Bottommost, Is.EqualTo(12).Within(2), "twelve rows further out");
    }

    [Test]
    public async Task APercentageOffsetSharesTheFontSizeWithTheLineItOffsets()
    {
        // A quarter of the twenty pixels this text is set at, not a quarter of the page: the same place
        // the same distance states in pixels, which is what the borrowed percentage case asks for.
        using var measurer = new SkiaTextMeasurer();
        using var view = new HutuViewInternal(measurer);
        view.SetViewportSize(400, 260);

        await view.SetHtml(
            "<html><body style='margin:0'><p style='margin:0;font-family:Segoe UI;font-size:40px;" +
            "line-height:2;text-decoration:underline;text-underline-offset:25%'>" + Text + "</p></body></html>");

        var run = Walk(view.Layout!.Root).Single(node => node.Kind == LayoutKind.Text);
        var share = InkExtent.Of(view.Paint!, measurer, run);

        var stated = await Ink(Text, "underline", "text-underline-offset:10px");

        Assert.That(share.Bottommost, Is.EqualTo(stated.Bottommost).Within(1), "ten of the forty");
    }

    [Test]
    public async Task ANegativeOffsetDrawsTheLineBackInward()
    {
        // Negative is inward: the band comes up toward the letters by the same distance it would have
        // gone down.
        var inward = await Ink(Text, "underline", "text-underline-offset:-6px");
        var near = await Ink(Text, "underline");

        Assert.That(near.Bottommost - inward.Bottommost, Is.EqualTo(6).Within(2), "six rows inward");
    }

    [Test]
    public async Task TheOffsetTravelsDownToTheUnderliningInsideIt()
    {
        // The property is inherited, and the decoration is asked for below: the distance the container
        // settled on is what the line inside it stands at, even though the box that decorated is another.
        using var measurer = new SkiaTextMeasurer();
        using var view = new HutuViewInternal(measurer);
        view.SetViewportSize(400, 200);

        await view.SetHtml(
            "<html><body style='margin:0'><div style='margin:0;text-underline-offset:12px'>" +
            $"<p style='margin:0;font-family:{Family};font-size:40px;line-height:2;" +
            $"text-decoration:underline'>{Text}</p></div></body></html>");

        var run = Walk(view.Layout!.Root).Single(node => node.Kind == LayoutKind.Text);
        var stood = InkExtent.Of(view.Paint!, measurer, run);

        var near = await Ink(Text, "underline");

        Assert.That(stood.Bottommost - near.Bottommost, Is.EqualTo(12).Within(2), "the inherited distance");
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

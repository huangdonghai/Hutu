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
/// Covers <c>text-decoration-inset</c>: the ends of the lines a box asks for are pulled inward by the
/// distance the page said, run outward when it is negative, and counted along the whole of the decoration
/// a box draws - so a line that breaks is trimmed at its two outer ends and not at the break between the
/// fragments, unless the box was told to repeat its decoration on every line (CSS Text Decoration 4
/// §2.9.1).
/// </summary>
/// <remarks>
/// The line is drawn in red over black letters, so the ink that is measured belongs to the line alone:
/// what is counted for each run is where its red ink begins and ends, from that run's own box.
/// </remarks>
[TestFixture]
public sealed class TextDecorationInsetTests
{
    private const String Family = "Segoe UI";

    [Test]
    public async Task AnInsetPullsTheEndsOfTheLineBack()
    {
        var runs = await Lines("abcdef", "text-decoration-inset:10px");

        Assert.That(runs[0].Red.From, Is.EqualTo(10).Within(1), "the start stands in by the ask");
        Assert.That(runs[0].Red.To, Is.EqualTo(-10).Within(1), "and the end by the same distance");
    }

    [Test]
    public async Task ANegativeInsetRunsTheLinePastTheTextItDecorates()
    {
        // The line is given room to grow into by the text set before it, and its own colour tells its ink
        // from the box around it - which asked for a line of its own and was told nothing about growing.
        var plain = await Lines(Grown(""), "");
        var grown = await Lines(Grown("text-decoration-inset:-10px"), "");

        Assert.That(grown[1].Green.From, Is.EqualTo(plain[1].Green.From - 10).Within(1), "the line begins before its text");
        Assert.That(grown[1].Green.To, Is.EqualTo(plain[1].Green.To + 10).Within(1), "and ends beyond it");
    }

    [Test]
    public async Task OneValueSpeaksForBothEndsAndTwoForTheEndsInOrder()
    {
        var both = await Lines("abcdef", "text-decoration-inset:10px");
        var ends = await Lines("abcdef", "text-decoration-inset:10px 25px");

        Assert.That(ends[0].Red.From, Is.EqualTo(both[0].Red.From).Within(1), "the first value is the start either way");
        Assert.That(ends[0].Red.To, Is.EqualTo(-25).Within(1), "and the second speaks only for the end");
    }

    [Test]
    public async Task APercentageIsAShareOfTheTextTheDecorationRunsAcross()
    {
        var runs = await Lines("abcdef", "text-decoration-inset:10%");
        var share = runs[0].Width * 0.1;

        Assert.That(share, Is.GreaterThan(4), "the text is wide enough for the share to be seen");
        Assert.That(runs[0].Red.From, Is.EqualTo(share).Within(2), "a tenth of the inline size at the start");
        Assert.That(runs[0].Red.To, Is.EqualTo(-share).Within(2), "and a tenth of it at the end");
    }

    [Test]
    public async Task TheInsetBelongsToTheBoxThatAskedForTheLine()
    {
        // The inset controls the lines its own box draws and none of the lines its ancestors asked for
        // (§2.9.1): the red line of the paragraph runs on unmoved while the green line the span asked for
        // stands back from its ends.
        var runs = await Lines(
            "aa<span style='text-decoration:underline;text-decoration-color:green;" +
            "text-decoration-inset:10px'>bb</span>cc",
            "");

        var (red, green) = (runs[1].Red, runs[1].Green);

        Assert.That(green.From, Is.EqualTo(10).Within(2), "the line the element asked for stands back");
        Assert.That(green.To, Is.EqualTo(-10).Within(2), "at both of its ends");
        Assert.That(red.From, Is.LessThanOrEqualTo(0), "the line its box above asked for is not pulled in");
        Assert.That(red.To, Is.GreaterThanOrEqualTo(0), "and runs on to the ends it was given");
    }

    [Test]
    public async Task ADecorationThatBreaksIsTrimmedOnlyAtItsOuterEnds()
    {
        var runs = await Broken("text-decoration-inset:10px");

        Assert.That(runs[0].Red.From, Is.EqualTo(10).Within(1), "the first fragment is pulled in at its start");
        Assert.That(runs[0].Red.To, Is.EqualTo(0).Within(2), "and reaches the break it was cut at untouched");
        Assert.That(runs[^1].Red.To, Is.EqualTo(-10).Within(1), "while the last is pulled in at its end");
    }

    [Test]
    public async Task ADecorationRepeatedOnEveryLineIsTrimmedAtEachOfThem()
    {
        var runs = await Broken("text-decoration-inset:10px;box-decoration-break:clone");

        Assert.That(runs[0].Red.To, Is.EqualTo(-10).Within(1), "every fragment ends where the inset says");
    }

    [Test]
    public async Task AColumnIsTrimmedAtTheEndsOfTheLineToo()
    {
        // The same rule where the page runs down, where the line runs beside the text: the distance is
        // counted along the column, from the head the text begins reading at (§2.9.1).
        var runs = await Column("text-decoration-inset:10px");

        Assert.That(runs[0].Red.From, Is.EqualTo(10).Within(1), "the head of the column is pulled back");
        Assert.That(runs[0].Red.To, Is.EqualTo(-10).Within(1), "and its foot by the same distance");
    }

    /// <summary>A span that asks for a line of its own in green, with anything else said about it. The
    /// box around it draws a red line, so the two are told apart by their ink.</summary>
    private static String Grown(String extra) =>
        $"zz<span style='text-decoration:underline;text-decoration-color:green;{extra}'>abcdef</span>";

    [Test]
    public async Task AnAutoInsetKeepsTwoBoxesSetSideBySideFromReadingAsOneRule()
    {
        // The distance the engine chooses for <c>auto</c> is asked to keep two identical elements set side
        // by side from looking like one long rule (§2.9.1), so each stands back from the join by the width
        // of its own line where nothing was said they meet there.
        var joined = await Singles(Adjoining(""));
        var apart = await Singles(Adjoining("text-decoration-inset:auto"));

        Assert.That(joined[0].Close.To, Is.EqualTo(0).Within(1), "with nothing said the two lines meet");
        Assert.That(apart[0].Close.To, Is.LessThan(-1), "an auto inset stands back from the join");
        Assert.That(apart[1].Close.From, Is.GreaterThan(1), "on both sides of it");
    }

    [Test]
    public async Task TheEndsAreCountedFromWhereTheTextBeginsReading()
    {
        // The two values speak of the start and the end of the text, which for a row written
        // right-to-left is the far edge of the box and its near one (§2.9.1): the first value pulls the
        // right, and the second the left.
        var runs = await Lines("abcdef", "text-decoration-inset:10px 25px;direction:rtl");

        Assert.That(runs[0].Red.To, Is.EqualTo(-10).Within(1), "the start of the row is its right edge");
        Assert.That(runs[0].Red.From, Is.EqualTo(25).Within(1), "and the end is the left one");
    }

    /// <summary>Two elements side by side, each asking for a green line of its own.</summary>
    private static String Adjoining(String extra) =>
        $"<span style='text-decoration:underline;text-decoration-color:green;{extra}'>xxx</span>" +
        $"<span style='text-decoration:underline;text-decoration-color:green;{extra}'>yyy</span>";

    /// <summary>Draws a page where only what is inside the paragraph asks for lines.</summary>
    private static async Task<List<Run>> Singles(String content)
    {
        using var view = await Loaded(
            $"<p style='margin:0;font-family:{Family};font-size:40px;line-height:2'>{content}</p>");

        return Ends(view);
    }

    /// <summary>Draws one underlined line in red and reports where each run's line begins and ends.</summary>
    private static async Task<List<Run>> Lines(String content, String inset)
    {
        using var view = await Loaded(
            $"<p style='margin:0;font-family:{Family};font-size:40px;line-height:2;" +
            $"text-decoration:underline;text-decoration-color:red;{inset}'>{content}</p>");

        return Ends(view);
    }

    /// <summary>Draws a decorated element that is broken across two lines of a narrow box.</summary>
    private static async Task<List<Run>> Broken(String inset)
    {
        using var view = await Loaded(
            $"<div style='width:260px;margin:0;font-family:{Family};font-size:40px;line-height:2;" +
            $"text-decoration:underline;text-decoration-color:red;{inset}'>alpha beta gamma</div>");

        return Ends(view);
    }

    /// <summary>Draws a red line beside a column of turned text.</summary>
    private static async Task<List<Run>> Column(String inset)
    {
        using var view = await Loaded(
            $"<div style='writing-mode:vertical-rl;margin:0;font-family:{Family};font-size:40px;" +
            $"text-decoration:underline;text-decoration-color:red;{inset}'>abcdef</div>");

        return Ends(view);
    }

    private static List<Run> Ends(HutuViewInternal view) =>
        Walk(view.Layout!.Root)
            .Where(node => node.Kind == LayoutKind.Text)
            .Select(node => new Run(
                node.Text!,
                node.Vertical ? node.BorderBox.Height : node.BorderBox.Width,
                Where(view, node, Red),
                Where(view, node, Green),
                Where(view, node, Green, 0)))
            .ToList();

    /// <summary>
    /// Gets where the ink of one colour that a line was drawn in stands, measured from the edges of the
    /// run's own box along the axis the line runs on: a negative start is outside the box the text was
    /// given. The letters are black, so any ink of this colour belongs to a line.
    /// </summary>
    private static (Int32 From, Int32 To) Where(
        HutuViewInternal view, LayoutBox run, Func<SKColor, Boolean> inked, Int32 reach = 30)
    {
        var (paint, bitmap) = Drawn(view);

        try
        {
            var along = run.Vertical;
            var box = run.BorderBox;
            var from = Int32.MaxValue;
            var to = Int32.MinValue;

            // Look well past the box on either side: a line that was run outward stands outside it.
            var first = (Int32)Math.Floor((along ? box.Y : box.X) - paint.Bounds.X) - reach;
            var last = (Int32)Math.Ceiling((along ? box.Bottom : box.Right) - (along ? paint.Bounds.Y : paint.Bounds.X)) + reach;
            var across = (Int32)Math.Floor((along ? box.X : box.Y) - (along ? paint.Bounds.X : paint.Bounds.Y)) - 10;
            var over = (Int32)Math.Ceiling((along ? box.Right : box.Bottom) - (along ? paint.Bounds.X : paint.Bounds.Y)) + 30;

            for (var at = Math.Max(0, first); at < Math.Min(last, along ? bitmap.Height : bitmap.Width); at++)
            {
                for (var side = Math.Max(0, across); side < Math.Min(over, along ? bitmap.Width : bitmap.Height); side++)
                {
                    // `at` runs along the axis the line is drawn on and `side` across it: for a
                    // row that is x and then y, and for a column the other way round.
                    if (inked(along ? bitmap.GetPixel(side, at) : bitmap.GetPixel(at, side)))
                    {
                        from = Math.Min(from, at);
                        to = Math.Max(to, at);
                    }
                }
            }

            if (from > to)
            {
                return (-1, -1);
            }

            var edge = (Int32)Math.Round(along ? box.Y - paint.Bounds.Y : box.X - paint.Bounds.X);
            var beyond = (Int32)Math.Round(along ? box.Bottom - paint.Bounds.Y : box.Right - paint.Bounds.X);

            return (from - edge, to - beyond);
        }
        finally
        {
            bitmap.Dispose();
        }
    }

    private static Boolean Red(SKColor pixel) =>
        pixel.Red > 100 && pixel.Green < 100 && pixel.Blue < 100;

    private static Boolean Green(SKColor pixel) =>
        pixel.Green > 100 && pixel.Red < 100 && pixel.Blue < 100;

    /// <summary>
    /// Where the lines drawn under or beside one run of text stand, from that run's own box: the red line
    /// the box above asked for, and the green one the run's own element asked for.
    /// </summary>
    /// <param name="Close">
    /// The green line again, measured only within the box the text was given - which is how a line that
    /// stands back from its end is told from one that runs on into the neighbour's ink.
    /// </param>
    private readonly record struct Run(
        String Text,
        Double Width,
        (Int32 From, Int32 To) Red,
        (Int32 From, Int32 To) Green,
        (Int32 From, Int32 To) Close);

    private static async Task<HutuViewInternal> Loaded(String html)
    {
        using var checking = new SkiaTextMeasurer();

        Assume.That(
            checking.GetMetrics(new FontStyle(Family, 40)).Ascent,
            Is.GreaterThan(0),
            $"no {Family} on this machine");

        var view = new HutuViewInternal(new SkiaTextMeasurer());
        view.SetViewportSize(400, 300);
        await view.SetHtml("<html><body style='margin:0'>" + html + "</body></html>");

        return view;
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

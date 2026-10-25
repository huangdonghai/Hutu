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
    public async Task AnInheritedColourIsTheOneTheParentComputed()
    {
        // `text-decoration-color` is not inherited, but a child may ask for the parent's answer by name.
        // The parent need not decorate anything itself: the first paragraph computed no colour of its
        // own, so the child's line falls to the text's colour, and the second computed green, which is
        // what its child takes. The two children are written alike, so only the parents tell them apart
        // (CSS Text Decoration 4 §2.3).
        using var view = await Loaded(
            $"<p style='margin:0;font-family:{Family};font-size:40px;line-height:2;color:red'>" +
            "<span style='text-decoration:underline;text-decoration-color:inherit'>aa</span></p>" +
            $"<p style='margin:0;font-family:{Family};font-size:40px;line-height:2;color:red;" +
            "text-decoration-color:green'>" +
            "<span style='text-decoration:underline;text-decoration-color:inherit'>bb</span></p>");

        var bands = Walk(view.Layout!.Root)
            .Where(node => node.Kind == LayoutKind.Text)
            .ToDictionary(node => node.Text!, node => Band.Of(view, node));

        Assert.That(bands["aa"].Colour, Is.EqualTo(0xFFFF0000), "with nothing said above, the line takes the red the text is set in");
        Assert.That(bands["bb"].Colour, Is.EqualTo(0xFF008000), "and the parent's green is what the second child asks for");
    }

    [Test]
    public async Task EveryWordOfTheShorthandReachesTheLineItDraws()
    {
        // Line, colour and thickness are all words the shorthand takes (CSS Text Decoration 4 §2.6). A
        // grammar with no slot for the length leaves it unconsumed, and an unconsumed value is no value:
        // the declaration goes whole, and the text under it is left unlined as well.
        var bands = await Read("aa<span>bb</span>cc", "3px green underline");

        Assert.That(bands["bb"].Rows, Is.GreaterThan(0), "the line the shorthand asked for reaches the descendant");
        Assert.That(bands["bb"].Colour, Is.EqualTo(0xFF008000), "in the colour the shorthand said");
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

    [Test]
    public async Task ARubyBaseTakesTheLineAndItsAnnotationAboveItDoesNot()
    {
        // A ruby base and its annotation are laid out as runs of text rather than as boxes of their own,
        // so a decoration has to reach them where they actually stand: the line the paragraph asked for
        // runs under its base and leaves the annotation above it alone, and a line asked for by the ruby
        // itself still lands on the base even though no box of the ruby's own was ever laid out.
        var fromTheParagraph = await Ruby("underline", "none");
        var fromTheRuby = await Ruby("none", "underline");
        var plain = await Ruby("none", "none");

        Assert.That(fromTheParagraph["base"].Rows, Is.GreaterThan(plain["base"].Rows), "the base is lined");
        Assert.That(fromTheParagraph["text"].Rows, Is.EqualTo(plain["text"].Rows), "the annotation is not");
        Assert.That(fromTheRuby["base"].Rows, Is.GreaterThan(plain["base"].Rows), "the ruby's own line");
    }

    [Test]
    public async Task OneBoxDrawsOneLineAcrossARaisedRun()
    {
        // A decorating box uses one position and one thickness along each line, whatever the sizes and the
        // alignments of the text it runs across (CSS Text Decoration 4 §2.9): the line a container asked
        // for stays where the rest of the line has it, and the raised run does not get a line of its own
        // as well. Measured as the rows of solid ink that run the whole width of a run.
        var rows = await Rows(Raised);

        Assert.That(rows["oo"], Has.Length.EqualTo(1), "one line across the line box");
        Assert.That(rows["o"], Is.EqualTo(rows["oo"]), "and the raised run is drawn under on that one");
    }

    [Test]
    public async Task ARaisedRunLeavesTheLineWhereItsParentHasIt()
    {
        // The position is averaged over the text the box decorates on the line, and a run lifted out of
        // its line's row counts as its parent was set: the superscript has no effect at all on where the
        // line stands, or on how thick it is (CSS Text Decoration 4 §2.5).
        var raised = await Rows(Raised);
        var plain = await Rows(Plain);

        Assert.That(plain["oooooo"], Has.Length.EqualTo(1), "the plain line is drawn under");
        Assert.That(
            raised["oo"],
            Is.EqualTo(plain["oooooo"]),
            "the line stands where the parent alone puts it");
    }

    [Test]
    public async Task ABoxAskingForTheSameLinesAsItsContainerLaysItsOwn()
    {
        // Two boxes asking for identical lines are still two decorating boxes, each laying its own
        // (§2.1, §2.9): the line the raised element asked for goes up with it, while the one its container
        // asked for runs on at the row of the line.
        var rows = await Rows(Own);

        Assert.That(rows["oo"], Has.Length.EqualTo(1), "the container lays one line");
        Assert.That(rows["o"], Has.Length.EqualTo(2), "and the raised run is drawn under twice");
        Assert.That(rows["o"][1], Is.EqualTo(rows["oo"][0]), "one of them where the line stands");
    }

    /// <summary>A line with a superscript in it, the same line without one, and a superscript that asks
    /// for the same lines its container did.</summary>
    private const String Raised = "oo<span style='vertical-align:super;font-size:20px'>o</span>oo";

    private const String Plain = "oooooo";

    private const String Own =
        "oo<span style='vertical-align:super;font-size:20px;text-decoration:underline'>o</span>oo";

    /// <summary>A column with a run of another size in it.</summary>
    private const String Small = "oo<span style='font-size:20px'>o</span>oo";

    [Test]
    public async Task OneBoxDrawsOneLineBesideARunOfAnotherSize()
    {
        // The same rule where the page runs down: one decorating box keeps one position and one thickness
        // for the line it draws beside a column, however many sizes the text of that column is set in
        // (CSS Text Decoration 4 §2.9). Measured as the page column the ink stands in.
        var stands = await Columns(Small);
        var bare = await Columns(Small, "none");

        Assert.That(stands["oo"], Is.GreaterThan(0), "the line is drawn");
        Assert.That(bare["oo"], Is.EqualTo(-1), "and only the line runs the whole way down");
        Assert.That(stands["o"], Is.EqualTo(stands["oo"]), "one line for the whole of the column");
    }

    /// <summary>Draws one underlined line and reports the rows each run of its text is lined on.</summary>
    private static async Task<Dictionary<String, Int32[]>> Rows(String content)
    {
        using var view = await Loaded(
            "<div style='font-family:" + Family + ";font-size:40px;text-decoration:underline'>" +
            content + "</div>");

        var rows = new Dictionary<String, Int32[]>();

        foreach (var run in Walk(view.Layout!.Root).Where(node => node.Kind == LayoutKind.Text))
        {
            rows[run.Text!] = Lined(view, run);
        }

        return rows;
    }

    /// <summary>
    /// Gets the first page row of every band of ink that runs across nearly the whole width of a run - one
    /// band for each line drawn under it. The letters are round ones, so nothing but the lines themselves
    /// reaches from edge to edge in solid ink: where a glyph is drawn up to the width, its edges are too
    /// lightly covered to count as ink.
    /// </summary>
    private static Int32[] Lined(HutuViewInternal view, LayoutBox run)
    {
        var (paint, bitmap) = Drawn(view);

        try
        {
            var left = (Int32)Math.Round(run.BorderBox.X - paint.Bounds.X);
            var right = (Int32)Math.Round(run.BorderBox.Right - paint.Bounds.X);
            var bottom = (Int32)Math.Round(run.BorderBox.Bottom - paint.Bounds.Y) + 20;
            var rows = new List<Int32>();
            var lined = false;

            for (var y = 0; y < Math.Min(bottom, bitmap.Height); y++)
            {
                var inked = 0;

                for (var x = left; x < right && x < bitmap.Width; x++)
                {
                    if (Inked(bitmap.GetPixel(x, y)))
                    {
                        inked++;
                    }
                }

                if (right > left && inked >= (right - left) - 2)
                {
                    if (!lined)
                    {
                        rows.Add(y);
                    }

                    lined = true;
                }
                else
                {
                    lined = false;
                }
            }

            return [.. rows];
        }
        finally
        {
            bitmap.Dispose();
        }
    }

    /// <summary>Draws one column of text and reports the page column each run of it is lined on.</summary>
    private static async Task<Dictionary<String, Double>> Columns(String content, String decoration)
    {
        using var view = await Loaded(
            "<div style='writing-mode:vertical-rl;font-family:" + Family + ";font-size:40px;" +
            "text-decoration:" + decoration + "'>" + content + "</div>");

        var stands = new Dictionary<String, Double>();

        foreach (var run in Walk(view.Layout!.Root).Where(node => node.Kind == LayoutKind.Text))
        {
            stands[run.Text!] = Lied(view, run);
        }

        return stands;
    }

    /// <summary>Draws one column of text and reports the page column its runs are lined on.</summary>
    private static Task<Dictionary<String, Double>> Columns(String content) => Columns(content, "underline");

    /// <summary>
    /// Gets the leftmost page column whose ink runs down nearly the whole height of a run: the line drawn
    /// beside a column, where there is one.
    /// </summary>
    private static Double Lied(HutuViewInternal view, LayoutBox run)
    {
        var (paint, bitmap) = Drawn(view);

        try
        {
            var top = (Int32)Math.Round(run.BorderBox.Y - paint.Bounds.Y);
            var bottom = (Int32)Math.Round(run.BorderBox.Bottom - paint.Bounds.Y);
            var left = (Int32)Math.Round(run.BorderBox.X - paint.Bounds.X);
            var right = (Int32)Math.Round(run.BorderBox.Right - paint.Bounds.X);
            var best = -1d;

            for (var x = right; x >= left && x >= 0; x--)
            {
                var inked = 0;

                for (var y = top; y < bottom && y < bitmap.Height; y++)
                {
                    if (Inked(bitmap.GetPixel(x, y)))
                    {
                        inked++;
                    }
                }

                if (bottom > top && inked >= (bottom - top) - 2)
                {
                    best = x;
                }
            }

            return best;
        }
        finally
        {
            bitmap.Dispose();
        }
    }

    /// <summary>Gets whether a pixel carries the ink of a line drawn, rather than the edge of a glyph.</summary>
    private static Boolean Inked(SKColor pixel) =>
        pixel.Red < 128 && pixel.Green < 128 && pixel.Blue < 128;
    /// <summary>Draws one ruby pair - a base and its annotation - with the decorations asked for.</summary>
    private static async Task<Dictionary<String, Band>> Ruby(String onParagraph, String onRuby)
    {
        using var measurer = new SkiaTextMeasurer();
        using var view = new HutuViewInternal(measurer);
        view.SetViewportSize(400, 240);

        await view.SetHtml(
            "<html><body style='margin:0'><p style='margin:0;line-height:4;font-family:" + Family +
            ";font-size:40px;text-decoration:" + onParagraph + "'>" +
            "<ruby style='text-decoration:" + onRuby + "'><rb>base</rb><rt>text</rt></ruby></p></body></html>");

        var bands = new Dictionary<String, Band>();

        foreach (var run in Walk(view.Layout!.Root).Where(node => node.Kind == LayoutKind.Text))
        {
            bands[run.Text!] = Band.Of(view, run);
        }

        return bands;
    }

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

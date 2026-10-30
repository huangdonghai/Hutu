using SkiaSharp;
using System.Runtime.ExceptionServices;

namespace Hutu.Conformance;

/// <summary>
/// What two pages came to when they were drawn.
/// </summary>
/// <param name="Matched">Whether every pixel of the two pictures agreed, or agreed as far as the pair allows.</param>
/// <param name="Differing">How many pixels disagreed, which is the size of the lie.</param>
/// <param name="Widest">The largest difference in one colour channel of one of those pixels.</param>
/// <param name="Detail">Where the first disagreement was, and what each side drew there.</param>
/// <param name="DiffPath">A picture of the difference, when there was one to picture.</param>
internal sealed record ReftestComparison(
    Boolean Matched,
    Int32 Differing,
    Int32 Widest,
    String? Detail,
    String? DiffPath);

/// <summary>
/// Draws a page and compares the drawing with another one - the reftest method, in which the oracle
/// is not a browser but a second document that is supposed to come to the same picture.
/// </summary>
/// <remarks>
/// This is the cheapest strong oracle a renderer can have. A WPT reftest says "the page that uses the
/// feature" and "a page built out of primitives that happen to look like it" must be indistinguishable,
/// and both halves are plain HTML: nothing has to be captured, nothing has to be stored from another
/// machine, and the pair is reviewable because the reference is written by hand and says what it
/// expects in CSS rather than in a number.
/// <para>
/// The comparison is exact. A reftest whose two pages differ by one pixel is a reftest that failed, and
/// the tolerance that belongs here belongs in the pages - which is why the corpus's own tests carry no
/// text: two machines with different fonts would disagree about the width of a word, and that is a
/// question about the harness, not about the layout.
/// </para>
/// </remarks>
internal static class ReftestRunner
{
    /// <summary>
    /// The window both halves of a pair are drawn in, which is the one the suite's own runner gives a
    /// test: 800 by 600. A narrower window is not a neutral choice - a page that puts a mark at
    /// <c>left: 599px</c> to prove two references differ is invisible at 400 wide, so the pair the suite
    /// says must look different comes out the same, and any page that scrolls because its content is
    /// wider than the window grows a scrollbar the suite never showed.
    /// </summary>
    public const Int32 Width = 800;

    public const Int32 Height = 600;

    /// <summary>Where a picture of a difference is written, which is not beside the tests.</summary>
    private static String DiffFolder => Path.Combine(Corpus.Root, "build", "reftest-diff");

    /// <summary>How long a page is given before the drawing is called a failure rather than waited on.</summary>
    /// <remarks>
    /// Five seconds, for a window of 400 by 300. A page of plain layout comes back in about a tenth of a
    /// second, so this is fifty times the ordinary case and still short enough that a run over thousands
    /// of pages is not held up by the few that never finish. A page that sends the engine into a loop has
    /// to fail its own test rather than stop the run - a corpus of borrowed pages is exactly where such a
    /// page turns up, and finding it is part of the point.
    /// </remarks>
    public static readonly TimeSpan Limit = TimeSpan.FromSeconds(5);

    public static async Task<SKBitmap> DrawAsync(String page)
    {
        using var engine = EngineReader.WithTypefaces(Width, Height);

        await engine.LoadAsync(await File.ReadAllTextAsync(page), new Uri(page).AbsoluteUri);

        return engine.Render(Width, Height);
    }

    /// <summary>
    /// Draws a page as a printing device draws it: every page of the document, each in its own page
    /// box. This is the other half of <see cref="DrawAsync"/> - a printed pair is compared page by
    /// page, not as the one screen picture a window would show.
    /// </summary>
    public static async Task<IReadOnlyList<SKBitmap>> DrawPagesAsync(String page)
    {
        using var engine = EngineReader.WithTypefaces(Width, Height);

        engine.UsePrintMedia();
        await engine.LoadAsync(await File.ReadAllTextAsync(page), new Uri(page).AbsoluteUri);

        return engine.RenderPages();
    }

    /// <summary>
    /// Draws a page's printed pages, but not for longer than <see cref="Limit"/>: the same pages as
    /// <see cref="DrawPagesAsync"/> with the guard <see cref="Draw"/> puts round one screen drawing, so
    /// a page that never comes back costs a thread rather than the run.
    /// </summary>
    public static IReadOnlyList<SKBitmap> DrawPages(String page)
    {
        IReadOnlyList<SKBitmap>? drawn = null;
        Exception? failure = null;

        var drawing = new Thread(() =>
        {
            try
            {
                drawn = DrawPagesAsync(page).GetAwaiter().GetResult();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });

        drawing.IsBackground = true;
        drawing.Start();

        if (!drawing.Join(Limit))
        {
            throw new TimeoutException($"drawing {Path.GetFileName(page)} as pages was still going after {Limit.TotalSeconds} seconds");
        }

        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }

        return drawn ?? Array.Empty<SKBitmap>();
    }

    /// <summary>
    /// Draws a page, but not for longer than <see cref="Limit"/>: the same drawing as
    /// <see cref="DrawAsync"/>, with a guard round it.
    /// </summary>
    public static SKBitmap Draw(String page)
    {
        SKBitmap? drawn = null;
        Exception? failure = null;

        // A thread of this drawing's own, and not one taken from the pool. A page that does not come back
        // is abandoned where it stands, and an abandoned pool thread is a thread nothing else can have:
        // the pool is what the parallel runs are made of, so spending one on a page that will never
        // finish is how a run with three looping pages in it ends up with nothing left to run on.
        var drawing = new Thread(() =>
        {
            try
            {
                drawn = DrawAsync(page).GetAwaiter().GetResult();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        })
        {
            IsBackground = true,
        };

        drawing.Start();

        if (!drawing.Join(Limit))
        {
            throw new TimeoutException(
                $"drawing {Path.GetFileName(page)} was still going after {Limit.TotalSeconds:0} seconds, " +
                "which means the engine did not come back from it");
        }

        // Carried out and thrown here rather than on the thread, so the caller sees the engine's own
        // failure with its stack still on it, instead of something rewrapped on the way out of a thread.
        if (failure is not null)
        {
            ExceptionDispatchInfo.Throw(failure);
        }

        return drawn!;
    }

    /// <param name="writeDiff">
    /// Whether to picture the difference when there is one. A batch that is comparing thousands of
    /// candidates only needs the count and the coordinate, and writing a picture for each would cost more
    /// than the run it is part of.
    /// </param>
    /// <param name="allowance">
    /// The difference the pair allows itself, when it states one: a page that draws a gradient is drawn
    /// a little differently by every engine that draws it, and a pair may say how much of a difference
    /// it will accept. A pair that states none has to match outright.
    /// </param>
    public static ReftestComparison Compare(
        SKBitmap test,
        SKBitmap reference,
        String label,
        Boolean writeDiff = true,
        FuzzyAllowance? allowance = null)
    {
        if (test.Width != reference.Width || test.Height != reference.Height)
        {
            return new ReftestComparison(false, 0, 0,
                $"the two pages came to different sizes: {test.Width}x{test.Height} against " +
                $"{reference.Width}x{reference.Height}", null);
        }

        var differing = 0;
        var widest = 0;
        String? first = null;
        var marks = new bool[test.Width, test.Height];

        for (var y = 0; y < test.Height; y++)
        {
            for (var x = 0; x < test.Width; x++)
            {
                var drawn = test.GetPixel(x, y);
                var against = reference.GetPixel(x, y);

                if (drawn == against)
                {
                    continue;
                }

                marks[x, y] = true;
                differing++;
                widest = Math.Max(widest, Widest(drawn, against));

                first ??= $"first at {x},{y}: the test drew {Hex(drawn)} where the " +
                          $"reference drew {Hex(against)}";
            }
        }

        if (differing == 0)
        {
            return new ReftestComparison(true, 0, 0, null, null);
        }

        // The allowance is the pair's own: a difference the page says it accepts is not a difference it
        // failed on, and a difference it did not accept is not one it forgives.
        if (allowance is { } allowed && allowed.Allows(differing, widest))
        {
            return new ReftestComparison(true, differing, widest, null, null);
        }

        return new ReftestComparison(false, differing, widest, first, writeDiff ? Write(test, marks, label) : null);
    }

    /// <summary>
    /// Compares two printed documents page by page. A pair whose halves come to different page counts
    /// is a difference whatever the pages themselves look like - one page is not another page's
    /// picture, and a blank page at the end is a page the other half did not print.
    /// </summary>
    public static ReftestComparison ComparePages(
        IReadOnlyList<SKBitmap> test,
        IReadOnlyList<SKBitmap> reference,
        String label,
        Boolean writeDiff = true,
        FuzzyAllowance? allowance = null)
    {
        if (test.Count != reference.Count)
        {
            return new ReftestComparison(false, 0, 0,
                $"the test came to {test.Count} pages where the reference came to {reference.Count}", null);
        }

        var matched = true;
        var differing = 0;
        var widest = 0;
        String? first = null;
        String? diff = null;

        for (var index = 0; index < test.Count; index++)
        {
            var page = Compare(test[index], reference[index], label + "-page-" + (index + 1), writeDiff, allowance);

            matched &= page.Matched;
            differing += page.Differing;
            widest = Math.Max(widest, page.Widest);
            first ??= page.Detail;
            diff ??= page.DiffPath;
        }

        return new ReftestComparison(matched, differing, widest, first, diff);
    }

    /// <summary>Gets the largest difference in one colour channel between two pixels.</summary>
    private static Int32 Widest(SKColor one, SKColor other) =>
        Math.Max(Math.Abs(one.Red - other.Red),
            Math.Max(Math.Abs(one.Green - other.Green), Math.Abs(one.Blue - other.Blue)));

    /// <summary>
    /// A picture of the difference: the test's own drawing faded back, with every disagreeing pixel in
    /// full red on top of it. Read as "where the two pages part", which is a faster question to answer
    /// than a list of coordinates.
    /// </summary>
    private static String? Write(SKBitmap test, bool[,] marks, String label)
    {
        try
        {
            Directory.CreateDirectory(DiffFolder);

            using var diff = new SKBitmap(test.Width, test.Height);

            for (var y = 0; y < test.Height; y++)
            {
                for (var x = 0; x < test.Width; x++)
                {
                    diff.SetPixel(x, y, marks[x, y]
                        ? new SKColor(255, 0, 0)
                        : Fade(test.GetPixel(x, y)));
                }
            }

            var path = Path.Combine(DiffFolder, label.Replace('/', '_') + ".png");

            using var image = SKImage.FromBitmap(diff);
            using var stream = File.Create(path);

            image.Encode(SKEncodedImageFormat.Png, 100).SaveTo(stream);

            return path;
        }
        catch (IOException)
        {
            // A difference that cannot be pictured is still a difference; the count and the first
            // coordinate are the part a fix needs anyway.
            return null;
        }
    }

    private static SKColor Fade(SKColor color) =>
        new((Byte)(color.Red / 4), (Byte)(color.Green / 4), (Byte)(color.Blue / 4), 255);

    private static String Hex(SKColor color) =>
        $"#{color.Red:x2}{color.Green:x2}{color.Blue:x2}";
}

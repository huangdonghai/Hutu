using NUnit.Framework;

namespace Hutu.Conformance;

/// <summary>
/// Dumps the layout tree and the paint list of one page, so a picture that is wrong can be read as
/// boxes with coordinates instead of as a field of pixels.
/// </summary>
/// <remarks>
/// A reftest says two pages came to different pictures and gives a pixel count and a coordinate. That
/// is enough to know something is wrong and not enough to know what: the same 790 pixels could be a box
/// in the wrong place, a box that was never drawn, or a box drawn twice. Reading the tree answers which,
/// and it answers it in the terms the layout code is written in - so a finding can be taken to the line
/// that produced it instead of being guessed at.
/// <para>
/// Nothing here checks anything. It is a way of looking at one page, which is why it is explicit and
/// driven entirely by the environment: with no page set it ignores itself.
/// </para>
/// <list type="bullet">
/// <item><c>HUTU_PROBE_PAGE</c> - the html file to read.</item>
/// <item><c>HUTU_PROBE_OUT</c> - where to write the dump; defaults to <c>build/probe-&lt;name&gt;.txt</c>.</item>
/// </list>
/// </remarks>
[TestFixture]
[Explicit("it reads one page for a person, and checks nothing")]
public sealed class LayoutProbe
{
    [Test]
    public void DumpsOnePage()
    {
        var page = Environment.GetEnvironmentVariable("HUTU_PROBE_PAGE");

        if (String.IsNullOrWhiteSpace(page) || !File.Exists(page))
        {
            Assert.Ignore("set HUTU_PROBE_PAGE to the html file to read");
            return;
        }

        // The same window and the same measurer a reftest is drawn with, so the tree is the one the
        // comparison was made from rather than the one a golden would use. A taller page is looked at
        // with `HUTU_PROBE_WIDTH`/`HUTU_PROBE_HEIGHT`, which is how a picture cut off at the bottom of
        // a 300-pixel window is told from a box that never got there.
        var width = Size("HUTU_PROBE_WIDTH", (Int32)ReftestRunner.Width);
        var height = Size("HUTU_PROBE_HEIGHT", (Int32)ReftestRunner.Height);

        using var engine = EngineReader.WithTypefaces(width, height);

        engine.LoadAsync(File.ReadAllText(page), new Uri(page).AbsoluteUri).GetAwaiter().GetResult();

        var dump = engine.Layout is null || engine.Paint is null
            ? "the page produced no layout tree and no paint list to read"
            : LayoutDumper.Dump(engine.Layout, engine.Paint);

        var target = Environment.GetEnvironmentVariable("HUTU_PROBE_OUT")
                     ?? Path.Combine(Corpus.Root, "build", "probe-" + Path.GetFileNameWithoutExtension(page) + ".txt");

        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.WriteAllText(target, dump);

        // The picture as well, when asked for: a dump says what the engine thought the page was, and
        // the picture says what it drew, and a page that is wrong in one is usually wrong in the other.
        var picture = Environment.GetEnvironmentVariable("HUTU_PROBE_PNG");

        if (!String.IsNullOrWhiteSpace(picture))
        {
            using var drawn = engine.Render(width, height);
            using var image = SkiaSharp.SKImage.FromBitmap(drawn);
            using var stream = File.Create(picture);

            image.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100).SaveTo(stream);
        }

        TestContext.Out.WriteLine(dump);
    }

    /// <summary>Reads one side of the window the page is looked at in, or the reftest's own.</summary>
    private static Int32 Size(String name, Int32 fallback) =>
    Int32.TryParse(Environment.GetEnvironmentVariable(name), out var stated) && stated > 0
        ? stated
        : fallback;
}

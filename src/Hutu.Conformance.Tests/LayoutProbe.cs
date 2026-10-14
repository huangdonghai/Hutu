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
        // comparison was made from rather than the one a golden would use.
        using var engine = EngineReader.WithTypefaces(ReftestRunner.Width, ReftestRunner.Height);

        engine.LoadAsync(File.ReadAllText(page), new Uri(page).AbsoluteUri).GetAwaiter().GetResult();

        var dump = engine.Layout is null || engine.Paint is null
            ? "the page produced no layout tree and no paint list to read"
            : LayoutDumper.Dump(engine.Layout, engine.Paint);

        var target = Environment.GetEnvironmentVariable("HUTU_PROBE_OUT")
                     ?? Path.Combine(Corpus.Root, "build", "probe-" + Path.GetFileNameWithoutExtension(page) + ".txt");

        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.WriteAllText(target, dump);

        TestContext.Out.WriteLine(dump);
    }
}

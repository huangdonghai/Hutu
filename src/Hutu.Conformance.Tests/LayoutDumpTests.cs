using NUnit.Framework;
using System.Text;

namespace Hutu.Conformance;

/// <summary>
/// Compares each case's layout tree and paint list with the answer the engine gave the last time a
/// human read it.
/// </summary>
/// <remarks>
/// This is the cheap half of the harness, and the reason it is worth having next to the browser
/// comparison is that it needs nothing: no capture, no network, no installed typefaces - the page is
/// measured with fixed metrics, so what changes is the layout's arithmetic and nothing else. A
/// regression in how a flex row shrinks, in the order two boxes are painted, or in whether a clip is
/// pushed before its children, shows up here as one line of a text file.
/// <para>
/// A golden is a claim about the current answer, not about the specification, so it can be wrong
/// together with the engine. That is what the reference cases are for; this one is for the change
/// nobody intended after the behaviour was settled. Reading the golden in review is part of the cost,
/// which is why the dump is one box per line and says only what is out of the ordinary.
/// </para>
/// <para>
/// <c>HUTU_CONFORMANCE_UPDATE=1</c> rewrites the goldens from the engine instead of checking them,
/// and reports the run as skipped so a rewritten baseline cannot pass unnoticed - and so it cannot
/// happen in CI at all, which has no reason to have that variable set.
/// </para>
/// </remarks>
[TestFixture]
public sealed class LayoutDumpTests
{
    private static Boolean Update =>
        Environment.GetEnvironmentVariable("HUTU_CONFORMANCE_UPDATE") == "1";

    [TestCaseSource(nameof(Cases))]
    public async Task MatchesTheGolden(String directory)
    {
        var request = Corpus.Case(directory);

        using var engine = await Corpus.RenderAsync(request, directory, withTypefaces: false);

        Assert.That(engine.Layout, Is.Not.Null, "the page laid out nothing, so there is no tree to compare");

        var actual = LayoutDumper.Dump(engine.Layout!, engine.Paint!);
        var golden = Path.Combine(directory, Corpus.Golden);

        if (Update)
        {
            File.WriteAllText(golden, actual);
            Assert.Ignore($"{Corpus.Golden} was rewritten from the engine's current answer; read it, " +
                          "then run again without the update flag");
        }

        var stored = (await File.ReadAllTextAsync(golden)).Replace("\r\n", "\n");

        if (stored == actual)
        {
            return;
        }

        Assert.Fail(Describe(stored, actual, golden));
    }

    /// <summary>
    /// What moved, said as lines rather than as a boolean.
    /// </summary>
    /// <remarks>
    /// The comparison is positional, so a box added early in the tree shifts everything below it and
    /// the listing goes quiet after the first difference - which is why the two lengths are reported
    /// apart from it. Reading a golden's diff is a matter of finding where they part, not of counting
    /// how many lines disagree afterwards.
    /// </remarks>
    private static String Describe(String stored, String actual, String golden)
    {
        var were = stored.Split('\n');
        var are = actual.Split('\n');
        var report = new StringBuilder();

        report.AppendLine($"{Path.GetFileName(golden)} is not what the engine produces: " +
                          $"{were.Length} lines stored, {are.Length} produced.");

        var shown = 0;

        for (var line = 0; line < Math.Min(were.Length, are.Length) && shown < 10; line++)
        {
            if (were[line] == are[line])
            {
                continue;
            }

            report.AppendLine($"  line {line + 1}")
                .AppendLine($"    was {were[line]}")
                .AppendLine($"    now {are[line]}");
            shown++;
        }

        if (shown == 0)
        {
            report.AppendLine("  the lines agree and the file does not: check for a trailing newline " +
                              "or a changed line ending");
        }
        else if (were.Length != are.Length)
        {
            report.AppendLine("  (the two are different lengths, so a box was added or dropped; lines " +
                              "after the first difference may only be shifted)");
        }

        return report.ToString();
    }

    /// <summary>
    /// The cases to run. An update pass takes every case, including the one whose golden has never
    /// been written - which is the only way a baseline can be created without hand-making the file.
    /// </summary>
    private static IEnumerable<String> Cases() => Update ? Corpus.All() : Corpus.With(Corpus.Golden);
}

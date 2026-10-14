using NUnit.Framework;
using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;

namespace Hutu.Conformance;

/// <summary>
/// The score: how much of each borrowed corpus the engine already draws the way the corpus says.
/// </summary>
/// <remarks>
/// A corpus copied in from upstream is hundreds or thousands of pairs, most of them about features the
/// engine has not been built for yet. Holding the run to all of them would mean a suite that is red on
/// the day it is written and only gets redder as more is imported, and a suite nobody can get green is a
/// suite nobody reads. So the borrowed pairs are drawn, counted and listed, and nothing here is asserted.
/// The number is the point: it is the only measure of the engine that nobody chose the cases for, so it
/// moves when the engine moves rather than when the corpus does.
/// <para>
/// A pair that agrees is worth moving into <c>conformance/reftests</c> beside the hand-written corpus,
/// where <see cref="ReftestTests"/> takes it over and guards it from then on. A pair that does not is a
/// finding for whoever is looking for the next thing to fix. Either way the decision is a person's, which
/// is why this is a score and not an expectation.
/// </para>
/// <para>
/// Two ways in. <see cref="StopsAtTheTenthFinding"/> is the one to use while a fix is being made: it draws
/// until it has ten findings and then stops, which is a minute of work rather than half an hour, and each
/// of the ten names a place to open. <see cref="TheEngineDrawsSomeOfTheBorrowedPagesTheWayTheySay"/> is the
/// whole corpus, and is the one to run at the end of a day to watch the number move.
/// </para>
/// <para>
/// Both runs keep a log while they go - <c>build/score-&lt;corpus&gt;.log</c> and <c>build/quick.log</c> -
/// so a run started in the background can be watched rather than waited on: the last line says how far it
/// has got, and a line that stops moving says it has stopped. That is also why a page is given only a few
/// seconds to be drawn (see <see cref="ReftestRunner.Limit"/>), and why a run that collects enough pages
/// which never came back gives up on the rest instead of spending the rest of the machine waiting.
/// </para>
/// <para>
/// Because both draw the corpus, this fixture is explicit: run it by name rather than as part of an
/// ordinary run. <c>HUTU_SCORE_LIMIT</c> caps the pages drawn, for a run that is only asking whether the
/// fixture works; <c>HUTU_SCORE_FINDINGS</c> changes how many the quick run stops at. The report is written
/// to <c>build/reftest-score.md</c> as well as to the console.
/// </para>
/// </remarks>
[TestFixture]
[Explicit("it scores the borrowed corpora rather than guarding anything, and draws every page in them")]
public sealed class ImportedCorpusTests
{
    private static readonly Regex Link = new(
        @"<link\b(?<attrs>[^>]*)>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>How many findings the report names before it stops, since the counts are the point.</summary>
    private const Int32 Listed = 60;

    /// <summary>
    /// Half the machine, and no more. Both runs here draw thousands of pages, and a run that takes every
    /// core is a run nobody else can work through while it goes.
    /// </summary>
    private static Int32 Cores => Math.Max(1, Environment.ProcessorCount / 2);

    /// <summary>How many findings the quick run stops at, and why it is worth stopping at all.</summary>
    private static Int32 Findings =>
        Int32.TryParse(Environment.GetEnvironmentVariable("HUTU_SCORE_FINDINGS"), out var cap) ? cap : 10;

    /// <summary>
    /// How many pages may fail to come back before the run gives up on the rest of the corpus. A page
    /// that does not come back is not one gap among many - it is the engine failing to finish at all -
    /// and a corpus with several of those in it is reporting something a score cannot express. Spending
    /// the rest of the machine to count them is not worth the answer.
    /// </summary>
    private const Int32 TimeoutBudget = 8;

    private static Int32 Limit =>
        Int32.TryParse(Environment.GetEnvironmentVariable("HUTU_SCORE_LIMIT"), out var cap) ? cap : 0;

    [Test]
    public void TheEngineDrawsSomeOfTheBorrowedPagesTheWayTheySay()
    {
        var corpora = Corpora();
        var report = new StringBuilder();

        if (corpora.Count == 0)
        {
            TestContext.Out.WriteLine(
                "nothing has been imported: no folder under conformance/reftests carries a PROVENANCE.md");
            return;
        }

        foreach (var corpus in corpora)
        {
            report.Append(Score(corpus));
            report.AppendLine();
        }

        TestContext.Out.WriteLine(report.ToString());

        var path = Path.Combine(Corpus.Root, "build", "reftest-score.md");

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, report.ToString());
    }

    /// <summary>
    /// The quick way in: draw until enough pairs have gone wrong, then stop.
    /// </summary>
    /// <remarks>
    /// The whole corpus is the number worth watching over a day. This is the thing worth watching while a
    /// fix is being made: ten findings is enough to be worth stopping for and few enough that the run
    /// answers before the patience does, and each of them names a pair where the engine's layout parts
    /// from what the page asked for. Which one to open first is a question with an answer, and this is
    /// the fastest way to get it.
    /// </remarks>
    [Test]
    public void StopsAtTheTenthFinding()
    {
        var pairs = Pairs();
        var findings = new ConcurrentBag<(String Test, String Finding)>();
        var enough = 0;
        var drawn = 0;

        Progress.Begin("quick", pairs.Count);

        Parallel.ForEach(pairs,
            new ParallelOptions { MaxDegreeOfParallelism = Cores },
            (pair, state) =>
            {
                if (Volatile.Read(ref enough) == 1)
                {
                    // Enough is enough: the rest of the corpus is not this run's business.
                    state.Stop();
                    return;
                }

                Interlocked.Increment(ref drawn);
                Progress.Tick("quick", drawn);

                var (how, finding, _) = DrawOne(pair.Path);

                if (how == Drawn.Declared)
                {
                    return;
                }

                findings.Add((pair.Name, how == Drawn.Differs ? finding! : $"could not be drawn: {finding}"));

                if (findings.Count >= Findings)
                {
                    Volatile.Write(ref enough, 1);
                }
            });

        Progress.Done("quick", drawn);

        var report = QuickReport(findings, drawn);

        TestContext.Out.WriteLine(report);

        // To a file as well, because this is the run meant to be started and left: a report that only
        // exists in a console that has already scrolled away is a report nobody reads.
        var path = Path.Combine(Corpus.Root, "build", "reftest-quick.md");

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, report);
    }

    /// <summary>Every borrowed pair, named as the report names it, across every borrowed corpus.</summary>
    private static List<(String Name, String Path)> Pairs()
    {
        var folder = Path.Combine(ReftestExpectations.Folder, "reftests");
        var pairs = new List<(String, String)>();

        foreach (var corpus in Corpora())
        {
            foreach (var test in Directory.EnumerateFiles(Path.Combine(folder, corpus), "*.html", SearchOption.AllDirectories))
            {
                if (test.EndsWith("-ref.html", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                pairs.Add((Relative(corpus, test), test));
            }
        }

        return pairs.OrderBy(pair => pair.Item1, StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// What the quick run found, and how far it got before it stopped. The count of pages drawn is part of
    /// it: a run that stopped after thirty pages has looked at thirty pages, and its ten findings are ten
    /// out of thirty rather than ten out of the corpus.
    /// </summary>
    private static String QuickReport(ConcurrentBag<(String Test, String Finding)> findings, Int32 drawn)
    {
        var text = new StringBuilder();

        text.AppendLine($"stopped after drawing {drawn} pages, with {findings.Count} to look at:");
        text.AppendLine();

        foreach (var (test, finding) in findings.OrderBy(pair => pair.Test, StringComparer.Ordinal))
        {
            text.AppendLine($"- `{test}` - {finding}");
        }

        return text.ToString();
    }

    /// <summary>
    /// The borrowed corpora: the folders carrying a provenance file, which is what says the pages were
    /// taken from elsewhere and are kept as they were written there.
    /// </summary>
    private static IReadOnlyList<String> Corpora()
    {
        var folder = Path.Combine(ReftestExpectations.Folder, "reftests");

        if (!Directory.Exists(folder))
        {
            return [];
        }

        return Directory.EnumerateDirectories(folder, "*", SearchOption.AllDirectories)
            .Where(d => File.Exists(Path.Combine(d, "PROVENANCE.md")))
            .Select(d => Path.GetRelativePath(folder, d).Replace('\\', '/'))
            .OrderBy(d => d, StringComparer.Ordinal)
            .ToList();
    }

    private static String Score(String corpus)
    {
        var folder = Path.Combine(ReftestExpectations.Folder, "reftests", corpus);
        var pairs = Directory.EnumerateFiles(folder, "*.html", SearchOption.AllDirectories)
            .Where(f => !f.EndsWith("-ref.html", StringComparison.OrdinalIgnoreCase))
            .Select(f => f.Replace('\\', '/'))
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();

        if (Limit > 0 && pairs.Count > Limit)
        {
            pairs = pairs.Take(Limit).ToList();
        }

        var declared = new ConcurrentBag<String>();
        var differing = new ConcurrentBag<(String Test, String Finding)>();
        var undrawable = new ConcurrentBag<(String Test, String Finding)>();
        var run = "score-" + corpus;
        var drawn = 0;
        var gaveUp = 0;

        Progress.Begin(run, pairs.Count);

        Parallel.ForEach(pairs,
            new ParallelOptions { MaxDegreeOfParallelism = Cores },
            (test, state, _) =>
            {
                if (Volatile.Read(ref gaveUp) >= TimeoutBudget)
                {
                    // The engine has stopped finishing pages rather than started getting them wrong. The
                    // rest of the corpus would only tell us how many more, and the machine has other work.
                    state.Stop();
                    return;
                }

                var (how, finding, timedOut) = DrawOne(test);

                if (timedOut)
                {
                    Interlocked.Increment(ref gaveUp);
                }

                var name = Relative(corpus, test);

                Interlocked.Increment(ref drawn);
                Progress.Tick(run, drawn);

                switch (how)
                {
                    case Drawn.Declared:
                        declared.Add(name);
                        break;

                    case Drawn.Differs:
                        differing.Add((name, finding!));
                        break;

                    default:
                        undrawable.Add((name, finding!));
                        break;
                }
            });

        Progress.Done(run, drawn);

        return Report(corpus, pairs.Count, drawn, gaveUp, declared, differing, undrawable);
    }

    /// <summary>What became of one pair when it was drawn.</summary>
    private enum Drawn
    {
        /// <summary>It came to what the pair says it should.</summary>
        Declared,

        /// <summary>It came to something else.</summary>
        Differs,

        /// <summary>It could not be drawn at all.</summary>
        Undrawable,
    }

    /// <summary>
    /// Draws one pair and says what became of it. Shared by the score and the quick run, because the two
    /// ask the same question of a page and differ only in how many they ask it of.
    /// </summary>
    private static (Drawn How, String? Finding, Boolean TimedOut) DrawOne(String test)
    {
        try
        {
            var (reference, relation, complaint) = Resolve(test);

            if (reference is null)
            {
                return (Drawn.Undrawable, complaint, false);
            }

            using var drawn = ReftestRunner.Draw(test);
            using var against = ReftestRunner.Draw(reference);

            // No picture of the difference: this draws thousands of pairs, and a thousand png files is
            // not what anybody asked for.
            var comparison = ReftestRunner.Compare(drawn, against, String.Empty, writeDiff: false);

            // What the pair declares is the question, not whether the two pictures happened to match: an
            // upstream pair written as "these two must differ" is satisfied by a difference.
            return comparison.Matched == (relation == "match")
                ? (Drawn.Declared, null, false)
                : (Drawn.Differs, comparison.Matched
                    ? "it came to the same picture, and the pair says the two must differ"
                    : $"{comparison.Differing} pixels differ, {comparison.Detail}", false);
        }
        catch (Exception exception)
        {
            return (Drawn.Undrawable, $"{exception.GetType().Name}: {Line(exception.Message)}",
                exception is TimeoutException);
        }
    }

    private static String Report(
        String corpus,
        Int32 offered,
        Int32 drawn,
        Int32 gaveUp,
        IReadOnlyCollection<String> declared,
        IReadOnlyCollection<(String Test, String Finding)> differing,
        IReadOnlyCollection<(String Test, String Finding)> undrawable)
    {
        var text = new StringBuilder();

        text.AppendLine($"## {corpus}: {drawn} of {offered} pairs drawn");
        text.AppendLine();
        text.AppendLine($"- drew what the pair declares: {declared.Count} ({Share(declared.Count, drawn)})");
        text.AppendLine($"- did not: {differing.Count}");
        text.AppendLine($"- could not be drawn: {undrawable.Count}");

        if (gaveUp > 0)
        {
            text.AppendLine();
            text.AppendLine($"{gaveUp} pages never came back, which is the budget this run allows; the rest of " +
                            "the corpus was left undrawn rather than spent waiting for it. An engine that " +
                            "does not finish is a finding of its own, and not one a score can carry.");
        }

        text.AppendLine();

        // Named, because this is the list the work comes off: each of the first is a pair the engine
        // already gets right and that nobody has moved into the guarded corpus yet. Sorted, because the
        // pairs are drawn by half the machine at once now, and a report whose order moves between runs is
        // a report nobody can diff against yesterday's.
        Named(text, "What the engine already gets right",
            declared.OrderBy(name => name, StringComparer.Ordinal).Select(name => (name, String.Empty)).ToList());
        Named(text, "Findings", differing.OrderBy(pair => pair.Test, StringComparer.Ordinal).ToList());
        Named(text, "Could not be drawn", undrawable.OrderBy(pair => pair.Test, StringComparer.Ordinal).ToList());

        return text.ToString();
    }

    private static void Named(StringBuilder text, String title, IReadOnlyList<(String Test, String Finding)> entries)
    {
        if (entries.Count == 0)
        {
            return;
        }

        text.AppendLine($"### {title}");
        text.AppendLine();

        foreach (var (test, finding) in entries.Take(Listed))
        {
            text.AppendLine(finding.Length == 0 ? $"- `{test}`" : $"- `{test}` - {finding}");
        }

        if (entries.Count > Listed)
        {
            text.AppendLine($"- ... and {entries.Count - Listed} more");
        }

        text.AppendLine();
    }

    /// <summary>
    /// Which page a pair compares with, and whether it is to come to the same picture.
    /// </summary>
    /// <remarks>
    /// This reads the declaration the way <see cref="ReftestTests"/> does, and deliberately does not
    /// assert on it: a page taken from another suite may be malformed in a way this run has no business
    /// failing over. Such a pair is counted as one that could not be drawn, with the reason beside it.
    /// </remarks>
    private static (String? Reference, String Relation, String? Complaint) Resolve(String test)
    {
        foreach (Match link in Link.Matches(File.ReadAllText(test)))
        {
            var attrs = link.Groups["attrs"].Value;
            var relation = Value(attrs, "rel")?.ToLowerInvariant();

            if (relation is not ("match" or "mismatch"))
            {
                continue;
            }

            var href = Value(attrs, "href");

            return String.IsNullOrWhiteSpace(href)
                ? (null, relation, $"declares rel=\"{relation}\" with no href")
                : (Path.GetFullPath(Path.Combine(Path.GetDirectoryName(test)!, href)), relation, null);
        }

        // The naming convention the importer leaves behind, for a pair whose link did not survive.
        var conventional = Path.ChangeExtension(test, null) + "-ref.html";

        return File.Exists(conventional)
            ? (conventional, "match", null)
            : (null, "match", "declares no reference and there is none beside it");
    }

    private static String? Value(String attrs, String name)
    {
        var match = Regex.Match(attrs, $@"\b{name}\s*=\s*(?:""(?<v>[^""]*)""|'(?<v>[^']*)'|(?<v>[^\s>]+))");

        return match.Success ? match.Groups["v"].Value.Trim() : null;
    }

    private static String Relative(String corpus, String test) =>
        corpus + "/" + Path
            .GetRelativePath(Path.Combine(ReftestExpectations.Folder, "reftests", corpus), test)
            .Replace('\\', '/');

    private static String Share(Int32 part, Int32 whole) =>
        whole == 0 ? "none" : $"{100 * part / whole}%";

    private static String Line(String message)
    {
        var newline = message.IndexOf('\n');

        return newline < 0 ? message : message[..newline];
    }
}

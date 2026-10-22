using NUnit.Framework;
using System.Text.RegularExpressions;

namespace Hutu.Conformance;

/// <summary>
/// The reftest suite: every pair under <c>conformance/reftests</c>, drawn twice and compared.
/// </summary>
/// <remarks>
/// The corpus here is written in the shape WPT uses - a test page that declares its reference with
/// <c>&lt;link rel="match"&gt;</c>, and a reference page that reaches the same picture by other means -
/// so that dropping a directory of upstream tests beside it needs no change to this file. The pairs in
/// the repository are hand-written for the features the engine already claims, and they are deliberately
/// without text: a pixel comparison between two machines with different fonts measures the fonts.
/// <para>
/// A pair that disagrees is not automatically a failure - it is a fact to be explained. The expectations
/// file next to the folder says which disagreements are known, and why, and the suite holds the engine
/// to that claim in both directions: a registered gap has to stay open, and an unregistered one has to
/// be answered by a fix or by a sentence.
/// </para>
/// <para>
/// What is held to that is the hand-written corpus. A folder that carries a <c>PROVENANCE.md</c> was
/// copied in from another suite as it was written there, and those pairs are scored rather than asserted
/// - <see cref="ImportedCorpusTests"/> says why - so they are left out of this suite.
/// </para>
/// </remarks>
[TestFixture]
public sealed class ReftestTests
{
    private static readonly Regex Link = new(
        @"<link\b(?<attrs>[^>]*)>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    [TestCaseSource(nameof(Tests))]
    public void DrawsTheSamePictureAsItsReference(String test)
    {
        var relative = Relative(test);
        var (relation, reference) = ReferenceOf(test);

        var (state, reason) = Load().For(relative);

        using var drawn = ReftestRunner.Draw(test);
        using var against = ReftestRunner.Draw(reference);

        // A pair may state how much of a difference it accepts: the pages here are hand-written, but a
        // page copied from another suite may still come with the allowance it was written under.
        var comparison = ReftestRunner.Compare(
            drawn,
            against,
            relative,
            allowance: FuzzyAllowance.Of(File.ReadAllText(test)));

        // The claim on record flips what the run should have shown: a pair registered as failing is a
        // pair the engine is expected to draw differently, and a mismatch pair is expected to differ
        // whatever the registry says about other tests.
        var shouldMatch = (relation == "match") ^ (state == "fail");

        Assert.That(comparison.Matched, Is.EqualTo(shouldMatch), Describe(relative, comparison, state, reason));
    }

    [Test]
    public void TheExpectationsAreStillAboutTestsThatExist()
    {
        var expectations = Load();
        var tests = Tests().Select(Relative).ToHashSet(StringComparer.Ordinal);
        var stale = new List<String>();

        foreach (var key in expectations.Tests.Keys)
        {
            var prefix = key.EndsWith("/*", StringComparison.Ordinal) ? key[..^1] : key;

            if (tests.All(test => !test.StartsWith(prefix, StringComparison.Ordinal)))
            {
                stale.Add(key);
            }
        }

        Assert.That(stale, Is.Empty,
            "these expectations name tests that are no longer in the folder, and a registry nobody " +
            $"prunes stops meaning anything: {string.Join(", ", stale)}");
    }

    /// <summary>
    /// The guard under the whole suite. A comparison that always answered "the same picture" would pass
    /// every reftest above, pass the invariants, and show nothing but green - which is the one failure
    /// mode a test harness must not be able to have.
    /// </summary>
    [Test]
    public async Task TheComparisonNoticesADifference()
    {
        using var one = await ReftestRunner.DrawAsync(Page("box-model/padding-and-border.html"));
        using var two = await ReftestRunner.DrawAsync(Page("layout/primitives.html"));

        var comparison = ReftestRunner.Compare(one, two, "self-check");

        Assert.Multiple(() =>
        {
            Assert.That(comparison.Matched, Is.False,
                "two plainly different pages came out identical, so nothing this suite reports can be trusted");
            Assert.That(comparison.Differing, Is.GreaterThan(1000),
                $"the difference between two different pages was measured at {comparison.Differing} pixels, " +
                "which is smaller than either one of them is worth");
        });
    }

    private static String Page(String relative) =>
        Path.Combine(ReftestExpectations.Folder, "reftests", relative);

    private static String Describe(String relative, ReftestComparison comparison, String state, String reason)
    {
        if (comparison.Matched)
        {
            return $"this pair is registered as '{state}'{((reason.Length > 0) ? $": {reason}" : String.Empty)} " +
                   "and the engine now draws both pages the same; take the entry out of " +
                   $"{ReftestExpectations.Artifact} so it starts guarding the behaviour";
        }

        return $"{comparison.Differing} pixels differ, {comparison.Detail}" +
               (comparison.DiffPath is null ? String.Empty : $"; the difference is pictured at {comparison.DiffPath}") +
               $"\n  if this is a gap you mean to keep, register '{relative}' in " +
               $"{ReftestExpectations.Artifact} as \"fail: what is missing\"";
    }

    /// <summary>
    /// Which page this test is to be compared with, and whether it should come to the same picture or
    /// a different one.
    /// </summary>
    private static (String Relation, String Reference) ReferenceOf(String test)
    {
        var html = File.ReadAllText(test);

        foreach (Match link in Link.Matches(html))
        {
            var attrs = link.Groups["attrs"].Value;
            var relation = Attribute(attrs, "rel", lower: true);

            if (relation is not ("match" or "mismatch"))
            {
                continue;
            }

            var href = Attribute(attrs, "href", lower: false);

            if (href.Length == 0)
            {
                Assert.Fail($"{Relative(test)} declares rel=\"{relation}\" with no href");
            }

            return (relation, Path.GetFullPath(Path.Combine(Path.GetDirectoryName(test)!, href)));
        }

        // The naming convention WPT's own harness also accepts, for a pair written before links did.
        var conventional = Path.ChangeExtension(test, null) + "-ref.html";

        Assert.That(File.Exists(conventional), Is.True,
            $"{Relative(test)} declares no <link rel=\"match\"> and there is no {Path.GetFileName(conventional)} " +
            "beside it, so there is nothing to compare it with");

        return ("match", conventional);
    }

    /// <param name="lower">
    /// Whether the value is a keyword to be read case-insensitively. An <c>href</c> is not: a file name
    /// that differs only in its case is a different file on the machines the suite has to run on.
    /// </param>
    private static String Attribute(String attrs, String name, Boolean lower)
    {
        // The value may be quoted or bare: an upstream page is free to write href=/css/x.html, and a
        // declaration this suite cannot read is a pair it would quietly stop checking.
        var match = Regex.Match(attrs, $@"\b{name}\s*=\s*(?:""(?<v>[^""]*)""|'(?<v>[^']*)'|(?<v>[^\s>]+))");

        if (!match.Success)
        {
            return String.Empty;
        }

        var value = match.Groups["v"].Value.Trim();

        return lower ? value.ToLowerInvariant() : value;
    }

    private static ReftestExpectations Load() => ReftestExpectations.Load(ReftestExpectations.PathOf);

    /// <summary>
    /// The folders holding a corpus that was copied in from elsewhere: a <c>PROVENANCE.md</c> beside the
    /// pages is what says so. Those are scored by <see cref="ImportedCorpusTests"/> instead, because a
    /// borrowed pair records what an upstream suite expects of a browser rather than what this engine
    /// promises, and a run is not the place to keep a queue of a thousand of them.
    /// </summary>
    /// <returns>Each folder as a path prefix, forward-slashed and ending in a slash.</returns>
    private static IReadOnlyList<String> Borrowed()
    {
        var folder = Path.Combine(ReftestExpectations.Folder, "reftests");

        if (!Directory.Exists(folder))
        {
            return [];
        }

        return Directory.EnumerateDirectories(folder, "*", SearchOption.AllDirectories)
            .Where(d => File.Exists(Path.Combine(d, "PROVENANCE.md")))
            .Select(d => d.Replace('\\', '/') + "/")
            .ToList();
    }

    private static IEnumerable<String> Tests()
    {
        var folder = Path.Combine(ReftestExpectations.Folder, "reftests");

        if (!Directory.Exists(folder))
        {
            return [];
        }

        var borrowed = Borrowed();

        return Directory.EnumerateFiles(folder, "*.html", SearchOption.AllDirectories)
            .Where(f => !f.EndsWith("-ref.html", StringComparison.OrdinalIgnoreCase))
            .Select(f => f.Replace('\\', '/'))
            .Where(f => !borrowed.Any(root => f.StartsWith(root, StringComparison.Ordinal)))
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();
    }

    private static String Relative(String test) =>
        Path.GetRelativePath(Path.Combine(ReftestExpectations.Folder, "reftests"), test)
            .Replace('\\', '/');
}

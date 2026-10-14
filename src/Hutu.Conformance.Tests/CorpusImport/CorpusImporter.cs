using System.Text;
using System.Text.RegularExpressions;
using NUnit.Framework;

namespace Hutu.Conformance;

/// <summary>
/// Brings a subset of an upstream suite in: the pairs that are static and independent of the window,
/// which is to say the ones that mean the same thing here as they do upstream. Nothing is drawn
/// and nothing is judged here - the pages are copied in, and the suite's own run is what decides what the
/// engine makes of them.
/// </summary>
/// <remarks>
/// It is driven entirely by environment, so it never touches a normal test run: with no source set it
/// ignores itself.
/// <list type="bullet">
/// <item><c>HUTU_IMPORT_SOURCE</c> - the folder to read (a WPT <c>css</c> or a Gecko <c>layout/reftests</c>).</item>
/// <item><c>HUTU_IMPORT_KIND</c> - <c>wpt</c> or <c>gecko</c>; the suite's own discovery is used.</item>
/// <item><c>HUTU_IMPORT_PHASE</c> - <c>filter</c> to only count candidates, <c>run</c> to import.</item>
/// <item><c>HUTU_IMPORT_LIMIT</c> - an optional cap, for a smoke run.</item>
/// <item><c>HUTU_IMPORT_COMMIT</c> - the source revision, recorded in the provenance file.</item>
/// </list>
/// </remarks>
[TestFixture]
public sealed class CorpusImporter
{
    private static readonly RegexOptions Options =
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.Singleline;

    private static readonly Regex Link = new(@"<link\b(?<attrs>[^>]*)>", Options);

    // The value may be quoted or bare - upstream pages write href=/css/x.html often enough that a rule
    // which only reads quotes would leave those pairs pointing at a file that is not there.
    private static readonly Regex Rel = new(
        @"\brel\s*=\s*(?:""(?<v>[^""]*)""|'(?<v>[^']*)'|(?<v>[^\s>]+))", Options);

    private static readonly Regex Href = new(
        @"\bhref\s*=\s*(?:""(?<v>[^""]*)""|'(?<v>[^']*)'|(?<v>[^\s>]+))", Options);

    // A page reaches a picture in two ways: a CSS url() and an element's src. Both names are read here so
    // the file beside the source page is brought along too, and a reference that draws one is not left
    // pointing at a file the import never copied.
    private static readonly Regex UrlValue = new(
        @"url\s*\(\s*(?:""(?<v>[^""]*)""|'(?<v>[^']*)'|(?<v>[^)]*))\)", Options);

    private static readonly Regex Src = new(
        @"\bsrc\s*=\s*(?:""(?<v>[^""]*)""|'(?<v>[^']*)'|(?<v>[^\s>]+))", Options);

    [Test]
    public void ImportsTheStaticSubset()
    {
        var source = Environment.GetEnvironmentVariable("HUTU_IMPORT_SOURCE");

        if (String.IsNullOrWhiteSpace(source) || !Directory.Exists(source))
        {
            Assert.Ignore("set HUTU_IMPORT_SOURCE to a WPT css or Gecko layout/reftests folder");
            return;
        }

        var kind = Environment.GetEnvironmentVariable("HUTU_IMPORT_KIND") ?? "wpt";
        var phase = Environment.GetEnvironmentVariable("HUTU_IMPORT_PHASE") ?? "filter";
        var limit = Int32.TryParse(Environment.GetEnvironmentVariable("HUTU_IMPORT_LIMIT"), out var cap) ? cap : 0;

        var candidates = kind.Equals("gecko", StringComparison.OrdinalIgnoreCase)
            ? ImportDiscovery.DiscoverGecko(source)
            : ImportDiscovery.DiscoverWpt(source);

        // Half the machine, and no more: this reads every page the suite offers, which is tens of
        // thousands of files, and a run that takes every core is a run nobody else can work through.
        var workers = new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount / 2) };

        // Each page is judged into a slot of its own, so the counting below is a walk over an array
        // rather than a dictionary every worker has to take a lock to touch.
        var verdicts = new ImportVerdict[candidates.Count];
        var reasons = new String?[candidates.Count];
        var judging = "import-" + kind;

        Progress.Begin(judging, candidates.Count);

        Parallel.For(0, candidates.Count, workers, index =>
        {
            verdicts[index] = ImportFilters.Judge(File.ReadAllText(candidates[index].Test), out reasons[index]);

            Progress.Tick(judging, index + 1);
        });

        Progress.Done(judging, candidates.Count);

        var counts = new Dictionary<ImportVerdict, Int32>();
        var notLayout = new Dictionary<String, Int32>(StringComparer.Ordinal);
        var kept = new List<ReftestCandidate>();

        for (var index = 0; index < candidates.Count; index++)
        {
            var verdict = verdicts[index];

            counts[verdict] = counts.GetValueOrDefault(verdict) + 1;

            if (verdict == ImportVerdict.Keep)
            {
                kept.Add(candidates[index]);
            }
            else if (verdict == ImportVerdict.Scripted)
            {
                var what = reasons[index]!;

                notLayout[what] = notLayout.GetValueOrDefault(what) + 1;
            }
        }

        var report = new StringBuilder();

        report.AppendLine($"# {kind} import");
        report.AppendLine();
        report.AppendLine($"Source: `{source}`");
        report.AppendLine($"Revision: `{Environment.GetEnvironmentVariable("HUTU_IMPORT_COMMIT") ?? "unknown"}`");
        report.AppendLine();
        report.AppendLine("## Candidates declared by the suite");
        report.AppendLine();
        report.AppendLine($"- pairs declared: {candidates.Count}");
        report.AppendLine($"- kept (static, window-independent): {kept.Count}");
        report.AppendLine($"- turned away as not plain layout: {counts.GetValueOrDefault(ImportVerdict.Scripted)}");
        report.AppendLine($"- turned away because of the window: {counts.GetValueOrDefault(ImportVerdict.Viewport)}");

        foreach (var (what, turned) in notLayout
                     .OrderByDescending(pair => pair.Value)
                     .ThenBy(pair => pair.Key, StringComparer.Ordinal))
        {
            report.AppendLine($"  - {what}: {turned}");
        }

        var folder = Path.Combine(Corpus.Root, "build");

        Directory.CreateDirectory(folder);

        if (phase != "run")
        {
            Write(Path.Combine(folder, $"import-filter-{kind}.md"), report.ToString());

            TestContext.Out.WriteLine(report.ToString());
            return;
        }

        if (limit > 0 && kept.Count > limit)
        {
            kept = kept.Take(limit).ToList();
        }

        var destination = Path.Combine(Corpus.Root, "conformance", "reftests", kind);

        // A manifest may name the same page in more than one place; the page is copied once, under the
        // name it is first met by, or the second copy would quietly overwrite the first.
        var distinct = new Dictionary<String, ReftestCandidate>(StringComparer.Ordinal);

        foreach (var candidate in kept)
        {
            distinct.TryAdd(candidate.Relative, candidate);
        }

        // Every path is distinct - that is what the dictionary above is for - so the copies cannot
        // collide with each other and do not have to be serialised.
        var copies = distinct.Values.ToArray();
        var copying = "copy-" + kind;

        Progress.Begin(copying, copies.Length);

        Parallel.For(0, copies.Length, workers, index =>
        {
            Import(destination, copies[index]);

            Progress.Tick(copying, index + 1);
        });

        Progress.Done(copying, copies.Length);

        WriteProvenance(Path.Combine(destination, "PROVENANCE.md"), kind, source);

        report.AppendLine();
        report.AppendLine("## Imported");
        report.AppendLine();
        report.AppendLine($"- candidates kept: {kept.Count}");
        report.AppendLine($"- distinct pages: {distinct.Count}");
        report.AppendLine($"- the same page named more than once: {kept.Count - distinct.Count}");
        report.AppendLine($"- copied into `conformance/reftests/{kind}`: {copies.Length}");

        foreach (var repeat in kept
                     .GroupBy(c => c.Relative, StringComparer.Ordinal)
                     .Where(group => group.Count() > 1)
                     .OrderByDescending(group => group.Count())
                     .ThenBy(group => group.Key, StringComparer.Ordinal)
                     .Take(15))
        {
            report.AppendLine($"  - `{repeat.Key}` x{repeat.Count()}");
        }

        Write(Path.Combine(folder, $"import-report-{kind}.md"), report.ToString());

        TestContext.Out.WriteLine(report.ToString());
    }

    /// <summary>
    /// Lays the pair out under the corpus: the test keeps its path, the reference is copied next to it
    /// under a name derived from the test's, and the test is made to declare that reference - which is
    /// what the runner reads. A WPT test already says it in a link and only the href has to be pointed at
    /// the new name; a Gecko test says it in a manifest, which is not carried over, so the link is written
    /// in. Both matter: without the link a WPT pair whose reference lived in a shared folder would not be
    /// found, and a Gecko pair declared <c>!=</c> would be read as one that has to look the same.
    /// </summary>
    private static void Import(String destination, ReftestCandidate candidate)
    {
        var relative = Path.ChangeExtension(candidate.Relative, ".html");
        var test = Path.Combine(destination, relative.Replace('/', Path.DirectorySeparatorChar));
        var directory = Path.GetDirectoryName(test)!;

        Directory.CreateDirectory(directory);

        var reference = Path.Combine(directory, Path.GetFileNameWithoutExtension(relative) + "-ref.html");
        var name = Path.GetFileName(reference);
        var declared = false;

        var html = Link.Replace(File.ReadAllText(candidate.Test), match =>
        {
            var attrs = match.Groups["attrs"].Value;
            var relation = Value(Rel.Match(attrs))?.Trim().ToLowerInvariant();

            if (relation is not ("match" or "mismatch"))
            {
                return match.Value;
            }

            declared = true;

            return "<link" + Href.Replace(attrs, _ => $"href=\"{name}\"") + ">";
        });

        if (!declared)
        {
            html = $"<link rel=\"{candidate.Relation}\" href=\"{name}\">\n" + html;
        }

        File.WriteAllText(test, html);
        File.Copy(candidate.Reference, reference, overwrite: true);

        // Both halves are left where their source left them, so a picture each reaches for is copied in
        // beside them. The two source pages may sit in different folders yet land in one here, so each is
        // read against its own folder and its own relative path is what is kept under this one.
        CopyReferencedResources(Path.GetDirectoryName(candidate.Test)!, directory, File.ReadAllText(candidate.Test));
        CopyReferencedResources(Path.GetDirectoryName(candidate.Reference)!, directory, File.ReadAllText(candidate.Reference));
    }

    /// <summary>
    /// Copies, beside the laid-out pair, every picture the page reaches for by a relative path.
    /// </summary>
    /// <remarks>
    /// A url() or src that names a data URL, a fragment, or something over the network is left alone - the
    /// first two carry no file to copy and the last is deliberately not fetched, since a drawing has to
    /// reach the same picture on every machine. Only raster pictures are brought along: a font or a
    /// stylesheet pulled in the same way would move the width of a word, and a word's width is a question
    /// about the machine's fonts rather than about the layout the pair is meant to check.
    /// </remarks>
    private static void CopyReferencedResources(String sourceDirectory, String destinationDirectory, String html)
    {
        foreach (var target in ReferencedFiles(html))
        {
            var name = Strip(target);

            if (name.Length == 0 ||
                name.StartsWith("#", StringComparison.Ordinal) ||
                name.StartsWith("data:", StringComparison.OrdinalIgnoreCase) ||
                name.Contains(":", StringComparison.Ordinal) ||
                name.StartsWith("//", StringComparison.Ordinal))
            {
                continue;
            }

            var source = Path.GetFullPath(Path.Combine(sourceDirectory, name));

            if (!IsPicture(source))
            {
                continue;
            }

            var relative = Path.GetRelativePath(sourceDirectory, source);
            var destination = Path.Combine(destinationDirectory, relative);

            if (String.Equals(source, destination, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(source, destination, overwrite: true);
        }
    }

    /// <summary>Whether a path names a picture file that is actually there.</summary>
    private static Boolean IsPicture(String path) =>
        Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".png" or ".gif" or ".jpg" or ".jpeg" or ".bmp" or ".webp" or ".ico" => File.Exists(path),
            _ => false,
        };

    /// <summary>The distinct paths a page reaches for, from its url() and src values.</summary>
    private static IEnumerable<String> ReferencedFiles(String html)
    {
        var seen = new HashSet<String>(StringComparer.OrdinalIgnoreCase);

        foreach (var regex in new[] { UrlValue, Src })
        {
            foreach (Match match in regex.Matches(html))
            {
                if (match.Groups["v"].Success && seen.Add(match.Groups["v"].Value))
                {
                    yield return match.Groups["v"].Value;
                }
            }
        }
    }

    /// <summary>Drops the query and fragment a browser would not name a file by.</summary>
    private static String Strip(String value) =>
        Uri.UnescapeDataString(value.Split('?', '#')[0]).Trim();

    private static void WriteProvenance(String path, String kind, String source)
    {
        var origin = kind.Equals("gecko", StringComparison.OrdinalIgnoreCase)
            ? "https://github.com/mozilla/gecko-dev (github.com/mozilla/gecko-dev)"
            : "https://github.com/web-platform-tests/wpt (github.com/web-platform-tests/wpt)";

        var text =
            $"# Imported {kind} reftests\n\n" +
            "These reftest pairs were taken, unmodified apart from their reference link, from an upstream\n" +
            "test suite. They are under the upstream licence, not this project's.\n\n" +
            $"- Source: {origin}\n" +
            $"- Revision: {Environment.GetEnvironmentVariable("HUTU_IMPORT_COMMIT") ?? "unknown"}\n" +
            $"- Local checkout: {source}\n\n" +
            "A test that uses the feature and a reference that reaches the same picture by other means are\n" +
            "copied beside each other; the reference is renamed after the test so the pair is self-contained.\n";

        Write(path, text);
    }

    private static void Write(String path, String text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    private static String? Value(Match match) =>
        match.Success && match.Groups["v"].Success ? match.Groups["v"].Value : null;
}

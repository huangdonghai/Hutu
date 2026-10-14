using System.Text.RegularExpressions;

namespace Hutu.Conformance;

/// <summary>
/// One pair the source offered: the page under test, the page it is to look like, and how they are
/// required to relate.
/// </summary>
/// <param name="Test">The page that uses the feature, by absolute path.</param>
/// <param name="Reference">The page it is compared with, by absolute path.</param>
/// <param name="Relation">Either <c>match</c> or <c>mismatch</c>.</param>
/// <param name="Relative">The test's path under the source's own root, which is how it is laid out here.</param>
internal sealed record ReftestCandidate(String Test, String Reference, String Relation, String Relative);

/// <summary>
/// Finds the pairs two upstream suites declare, in each suite's own way: WPT states the relation in a
/// link inside the test, and Gecko states it as a line of a manifest beside the tests.
/// </summary>
internal static class ImportDiscovery
{
    private static readonly RegexOptions Options =
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.Singleline;

    private static readonly Regex Link = new(@"<link\b(?<attrs>[^>]*)>", Options);
    private static readonly Regex Rel = new(@"\brel\s*=\s*(?:""(?<v>[^""]*)""|'(?<v>[^']*)')", Options);
    private static readonly Regex Href = new(@"\bhref\s*=\s*(?:""(?<v>[^""]*)""|'(?<v>[^']*)')", Options);
    private static readonly Regex ReferenceName = new(@"-ref\.[a-z0-9]+$", Options);

    /// <summary>The relation words both suites use, and the only two this harness understands.</summary>
    private static readonly String[] Relations = ["match", "mismatch"];

    /// <summary>
    /// The pairs under a WPT <c>css</c> folder. A test is any page that declares exactly one relation and
    /// is not itself the target of another's, which is also the only shape the suite itself can read.
    /// </summary>
    public static List<ReftestCandidate> DiscoverWpt(String css)
    {
        var files = Directory.EnumerateFiles(css, "*.html", SearchOption.AllDirectories).ToList();

        var declared = new Dictionary<String, List<(String Relation, String Target)>>(StringComparer.OrdinalIgnoreCase);
        var targets = new HashSet<String>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in files)
        {
            var links = ReadLinks(File.ReadAllText(file), Path.GetDirectoryName(file)!);

            if (links.Count > 0)
            {
                declared[file] = links;

                foreach (var (_, target) in links)
                {
                    targets.Add(target);
                }
            }
        }

        var candidates = new List<ReftestCandidate>();

        foreach (var file in files)
        {
            if (targets.Contains(file) || ReferenceName.IsMatch(file))
            {
                continue;
            }

            if (declared.TryGetValue(file, out var links))
            {
                // More than one relation and the harness cannot say which reference it means, so the pair
                // would not mean the same thing to the importer as it does to the runner.
                if (links.Count == 1 && File.Exists(links[0].Target))
                {
                    candidates.Add(new ReftestCandidate(file, links[0].Target, links[0].Relation, Relative(css, file)));
                }

                continue;
            }

            // The naming convention, for a pair written before links did.
            var conventional = Path.ChangeExtension(file, null) + "-ref.html";

            if (File.Exists(conventional))
            {
                candidates.Add(new ReftestCandidate(file, conventional, "match", Relative(css, file)));
            }
        }

        return candidates;
    }

    /// <summary>
    /// The pairs under a Gecko <c>layout/reftests</c> tree, read from the <c>reftest.list</c> manifests
    /// that stand beside them. A line whose options carry a condition, a preference or a tolerance is
    /// left out: this harness has no way to honour any of the three.
    /// </summary>
    public static List<ReftestCandidate> DiscoverGecko(String reftests)
    {
        var candidates = new List<ReftestCandidate>();

        foreach (var manifest in Directory.EnumerateFiles(reftests, "reftest.list", SearchOption.AllDirectories))
        {
            ReadManifest(manifest, reftests, candidates, new HashSet<String>(StringComparer.OrdinalIgnoreCase));
        }

        return candidates;
    }

    private static void ReadManifest(
        String manifest,
        String reftests,
        List<ReftestCandidate> candidates,
        HashSet<String> seen)
    {
        if (!seen.Add(manifest))
        {
            return;
        }

        var directory = Path.GetDirectoryName(manifest)!;

        foreach (var raw in File.ReadAllLines(manifest))
        {
            var line = raw;

            var comment = line.IndexOf('#');

            if (comment >= 0)
            {
                line = line[..comment];
            }

            var tokens = line.Split((Char[]?)null, StringSplitOptions.RemoveEmptyEntries);

            if (tokens.Length == 0)
            {
                continue;
            }

            if (tokens[0].Equals("include", StringComparison.OrdinalIgnoreCase) && tokens.Length >= 2)
            {
                var included = Path.GetFullPath(Path.Combine(directory, tokens[1]));

                if (File.Exists(included))
                {
                    ReadManifest(included, reftests, candidates, seen);
                }

                continue;
            }

            var at = Array.IndexOf(tokens, "==");

            if (at < 0)
            {
                at = Array.IndexOf(tokens, "!=");
            }

            if (at < 0 || at + 2 >= tokens.Length)
            {
                continue;
            }

            var options = tokens[..at];

            if (options.Any(Unsupported))
            {
                continue;
            }

            var test = Full(directory, tokens[at + 1]);
            var reference = Full(directory, tokens[at + 2]);

            if (!File.Exists(test) || !File.Exists(reference) || !Supported(test))
            {
                continue;
            }

            candidates.Add(new ReftestCandidate(
                test,
                reference,
                tokens[at] == "!=" ? "mismatch" : "match",
                Relative(reftests, test)));
        }
    }

    /// <summary>
    /// An option this harness cannot reproduce: a condition about the platform, a preference that changes
    /// what is drawn, a tolerance it does not apply, or a promise the test will be skipped anyway.
    /// </summary>
    private static Boolean Unsupported(String option)
    {
        if (option.Equals("skip", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var head = option.ToLowerInvariant();

        return head.StartsWith("skip-if", StringComparison.Ordinal)
            || head.StartsWith("fuzzy", StringComparison.Ordinal)
            || head.StartsWith("pref", StringComparison.Ordinal)
            || head.StartsWith("test-pref", StringComparison.Ordinal)
            || head.StartsWith("require", StringComparison.Ordinal)
            || head.StartsWith("run-if", StringComparison.Ordinal)
            || head.StartsWith("asserts", StringComparison.Ordinal)
            || head.StartsWith("needs-focus", StringComparison.Ordinal)
            || head.StartsWith("random", StringComparison.Ordinal)
            || head.StartsWith("silent", StringComparison.Ordinal)
            || head.StartsWith("default-preferences", StringComparison.Ordinal);
    }

    private static List<(String Relation, String Target)> ReadLinks(String html, String directory)
    {
        var links = new List<(String, String)>();

        foreach (Match link in Link.Matches(html))
        {
            var attrs = link.Groups["attrs"].Value;
            var rel = Value(Rel.Match(attrs))?.Trim().ToLowerInvariant();

            if (rel is null || !Relations.Contains(rel))
            {
                continue;
            }

            var href = Value(Href.Match(attrs));

            if (String.IsNullOrWhiteSpace(href))
            {
                continue;
            }

            links.Add((rel, Path.GetFullPath(Path.Combine(directory, href))));
        }

        return links;
    }

    private static String? Value(Match match) =>
        !match.Success ? null : match.Groups["v"].Success ? match.Groups["v"].Value : null;

    private static Boolean Supported(String path) =>
        path.EndsWith(".html", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".xhtml", StringComparison.OrdinalIgnoreCase);

    private static String Full(String directory, String name) =>
        Path.GetFullPath(Path.Combine(directory, name));

    private static String Relative(String root, String path) =>
        Path.GetRelativePath(root, path).Replace('\\', '/');
}

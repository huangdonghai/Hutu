using AngleSharp.Css.Dom;
using AngleSharp.Dom;
using NUnit.Framework;
using System.Text;

namespace Hutu.Conformance;

/// <summary>
/// The census: every property the corpus declares, against what the engine has been said to do with
/// it.
/// </summary>
/// <remarks>
/// This is the test that turns "we do not support multi-column" from a fact somebody remembers into a
/// fact the build enforces. It runs over every case, reads the rules the page actually carries -
/// including the ones inside a media or supports block, because a rule that only applies on a narrow
/// screen is still a feature the page uses - and asks one question of each property name: has anyone
/// decided what the engine does with it?
/// <para>
/// A page that starts using something unregistered fails the run, and the message is the list of
/// names, which is the whole work of triage: each one gets looked at and given a state, and the
/// corpus then knows what it covers. The point is not that unsupported properties are failures -
/// plenty are perfectly fine to lack - it is that none of them are unknown.
/// </para>
/// </remarks>
[TestFixture]
public sealed class CssPropertyCensusTests
{
    [Test]
    public async Task EveryPropertyTheCorpusDeclaresHasBeenLookedAt()
    {
        var registry = Load();
        var declared = new Dictionary<String, Int32>(StringComparer.OrdinalIgnoreCase);
        var unknown = new SortedSet<String>(StringComparer.OrdinalIgnoreCase);

        foreach (var directory in Corpus.All())
        {
            foreach (var name in await DeclaredInAsync(directory))
            {
                declared[name] = declared.TryGetValue(name, out var seen) ? seen + 1 : 1;

                if (registry.State(name) is null)
                {
                    unknown.Add(name);
                }
            }
        }

        TestContext.Out.WriteLine(Report(registry, declared));

        Assert.That(unknown, Is.Empty,
            unknown.Count == 0 ? String.Empty
                : $"{unknown.Count} properties are declared by the corpus and registered nowhere: " +
                  $"{string.Join(", ", unknown)} - give each one a state in {CssSupport.Artifact}");
    }

    [Test]
    public void TheRegistryItselfIsHonest()
    {
        var registry = Load();
        var malformed = new List<String>();

        foreach (var (name, entry) in registry.Properties)
        {
            var state = CssSupport.Status(entry);

            if (!CssSupport.States.Contains(state, StringComparer.Ordinal))
            {
                malformed.Add($"{name}: '{state}' is not one of {string.Join('/', CssSupport.States)}");
            }
            else if (state != "modelled" && CssSupport.Reason(entry).Length == 0)
            {
                // A gap without a sentence about it is a gap nobody can size, and the next reader has
                // to work out from the code whether it is a missing feature or a half-finished one.
                malformed.Add($"{name}: a {state} property has to say what is missing");
            }
        }

        Assert.That(malformed, Is.Empty, String.Join(Environment.NewLine, malformed));
    }

    private static CssSupport Load() =>
        CssSupport.Load(Path.Combine(Corpus.Root, "conformance", CssSupport.Artifact));

    /// <summary>
    /// The properties one case declares, taken from the rules that survived their own media and
    /// supports conditions at the width the case is read at.
    /// </summary>
    private static async Task<IReadOnlyList<String>> DeclaredInAsync(String directory)
    {
        var request = Corpus.Case(directory);

        using var engine = await Corpus.RenderAsync(request, directory, withTypefaces: false);

        var names = new List<String>();

        if (engine.Document is not { } document)
        {
            return names;
        }

        foreach (var sheet in document.StyleSheets.OfType<ICssStyleSheet>())
        {
            Collect(sheet.Rules, names);
        }

        return names;
    }

    private static void Collect(ICssRuleList rules, List<String> names)
    {
        foreach (var rule in rules)
        {
            switch (rule)
            {
                case ICssStyleRule style:
                    foreach (var property in style.Style)
                    {
                        // A custom property is the page's own vocabulary, not a feature the engine
                        // could support or fail to; what matters about one is the property it reaches.
                        if (property.Name.StartsWith("--", StringComparison.Ordinal))
                        {
                            continue;
                        }

                        names.Add(property.Name);
                    }

                    break;

                // A conditional group rule whose condition held has its rules in the list already;
                // walking them is what catches a property that only appears on a narrow screen or
                // behind a @supports - which is exactly the kind of property a corpus stops covering
                // without anyone noticing.
                case ICssMediaRule media:
                    Collect(media.Rules, names);
                    break;

                case ICssSupportsRule supports:
                    Collect(supports.Rules, names);
                    break;
            }
        }
    }

    /// <summary>
    /// The matrix, counted. Written out on every run because the number that moves is the thing to
    /// notice, and a report nobody reads is a report nobody checks.
    /// </summary>
    private static String Report(CssSupport registry, Dictionary<String, Int32> declared)
    {
        var text = new StringBuilder();
        var byState = declared.Keys
            .GroupBy(name => registry.State(name) ?? "unknown")
            .OrderBy(group => group.Key, StringComparer.Ordinal);

        text.AppendLine($"{declared.Count} properties declared by {Corpus.All().Count()} cases, " +
                        $"{registry.Properties.Count} registered:");

        foreach (var group in byState)
        {
            text.AppendLine($"  {group.Key,-12} {group.Count()}");
        }

        var gaps = declared.Keys
            .Select(name => (Name: name, Entry: registry.Properties.TryGetValue(name, out var entry) ? entry : null))
            .Where(pair => pair.Entry is not null && CssSupport.Status(pair.Entry) != "modelled")
            .OrderBy(pair => pair.Name, StringComparer.Ordinal);

        foreach (var (name, entry) in gaps)
        {
            text.AppendLine($"  {name,-24} {entry}");
        }

        return text.ToString();
    }
}

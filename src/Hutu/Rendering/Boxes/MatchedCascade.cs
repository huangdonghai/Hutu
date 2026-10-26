using AngleSharp.Css;
using AngleSharp.Css.Dom;
using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using AngleSharp.Svg.Dom;
using System;
using System.Collections.Generic;

namespace Hutu.Rendering.Boxes;

/// <summary>
/// The rules matching one element, in cascade application order, plus its inline style:
/// what <c>ComputeExplicitStyle</c> accumulates, without the intermediate declaration it
/// accumulates into.
/// </summary>
/// <remarks>
/// Application order is specificity ascending with document order breaking ties, inline
/// style last - the order <c>SetDeclarations</c> is applied in. A lookup mirrors that
/// accumulation exactly: the last important declaration wins, and only when nothing
/// important is declared does the last normal one.
/// </remarks>
/// <summary>Where a matched declaration came from: the cascade's most significant sort key.</summary>
/// <remarks>
/// A declaration's origin decides more than its specificity does. An author (document) declaration
/// beats a user-agent one no matter how specific the latter's selector is - which is why an author
/// <c>a { text-decoration: none }</c> overrides the user-agent <c>:link { text-decoration: underline }</c>.
/// User stylesheets are not supplied by the reader, so only user-agent and author occur in practice.
/// </remarks>
internal enum RuleOrigin : Byte
{
    UserAgent = 0,
    User = 1,
    Author = 2,
}

/// <summary>One rule an element matched, together with the three things that decide whether its
/// declaration of a property wins: its origin, its specificity and its place in the document.</summary>
internal readonly struct MatchedRule
{
    public MatchedRule(ICssStyleRule rule, RuleOrigin origin, Priority specificity, Int32 docOrder)
    {
        Rule = rule;
        Origin = origin;
        Specificity = specificity;
        DocOrder = docOrder;
    }

    public ICssStyleRule Rule { get; }
    public RuleOrigin Origin { get; }
    public Priority Specificity { get; }
    public Int32 DocOrder { get; }
}

internal sealed class MatchedCascade
{
    private readonly List<MatchedRule> _rules;
    private readonly ICssStyleDeclaration? _inline;

    /// <summary>
    /// What this cascade has already answered.
    /// </summary>
    /// <remarks>
    /// A cascade the index shares between elements is asked for the same names by each of them and
    /// answers the same every time, so the second element's read is a lookup rather than a walk over
    /// the rules. One that serves a single element still pays for it - and still comes out ahead,
    /// because a name is asked for more than once even within one element: the box model reads each
    /// margin and padding longhand twice, and the writing mode three times.
    /// </remarks>
    private readonly Dictionary<String, ICssProperty?> _found = new(StringComparer.Ordinal);

    /// <summary>
    /// What one walk over this cascade's declarations answers: the custom properties it declares, and
    /// whether any of its values asks for an environment variable. Null until asked - most cascades
    /// are never asked, because a page that declares no custom property and names no environment
    /// variable needs neither answer.
    /// </summary>
    private CascadeScan? _scan;

    internal MatchedCascade(List<MatchedRule> rules, ICssStyleDeclaration? inline)
    {
        _rules = rules;
        _inline = inline;
    }

    /// <summary>
    /// Gets this cascade with one more rule standing before every rule it already holds. The user
    /// agent uses it to give a generated pseudo-element the defaults the page did not state: a rule
    /// from a lower origin loses to every declaration the page wrote, and answers only for the
    /// properties the page left alone.
    /// </summary>
    internal MatchedCascade WithPreceding(ICssStyleRule rule, RuleOrigin origin, Priority specificity)
    {
        var rules = new List<MatchedRule>(_rules.Count + 1)
        {
            new(rule, origin, specificity, -1),
        };

        rules.AddRange(_rules);
        return new MatchedCascade(rules, _inline);
    }

    /// <summary>
    /// Gets this cascade followed by every rule of another. The arm a page writes for one scroll
    /// button (`::scroll-button(inline-end)`) is read after the arm it writes for all of them
    /// (`::scroll-button(*)`), so the narrower arm is the one that wins.
    /// </summary>
    internal MatchedCascade WithFollowing(MatchedCascade other)
    {
        var rules = new List<MatchedRule>(_rules.Count + other._rules.Count);
        rules.AddRange(_rules);
        rules.AddRange(other._rules);
        return new MatchedCascade(rules, _inline);
    }

    /// <summary>Gets the rules that matched, in the order the cascade applies them.</summary>
    internal IReadOnlyList<ICssStyleRule> Matched
    {
        get
        {
            var rules = new ICssStyleRule[_rules.Count];
            for (var i = 0; i < _rules.Count; i++)
            {
                rules[i] = _rules[i].Rule;
            }

            return rules;
        }
    }

    /// <summary>Gets the element's own inline style, which the cascade applies last of all.</summary>
    internal ICssStyleDeclaration? Inline => _inline;

    /// <summary>
    /// Gets the cascade an element matches, which the index may already have worked out for an
    /// element this one cannot differ from.
    /// </summary>
    /// <param name="marks">
    /// The marks the engine has placed on the element, packed the way
    /// <see cref="StyleRecord.MarksOf"/> packs them. The index needs them to know whether a match it
    /// already holds can still be the answer.
    /// </param>
    public static MatchedCascade ForElement(RuleBucketIndex index, IElement element, Byte marks) =>
        index.Cascade(element, marks);

    /// <summary>
    /// Matches an element against the index: the rules that could match it, ordered the way the
    /// cascade applies them. The inline style is left out of this list on purpose - it is folded in
    /// by the cascade that wraps the list - because the list alone is what keys the cache, and
    /// building the wrapper before the lookup would throw away the one a hit returns.
    /// </summary>
    /// <param name="candidates">
    /// The rules that could match it, collected by the caller - the index needs them to key the
    /// result, and collecting them twice would pay for the bucketing twice.
    /// </param>
    /// <param name="pseudoOnly">
    /// For each rule, whether every arm of its selector is written for a pseudo-element. Such a rule
    /// names no element, so it is passed over here: the box it styles is the one the pseudo-element
    /// index makes, and the parser's own match would otherwise hand its declarations to the element.
    /// </param>
    internal static List<MatchedRule> MatchRules(
        IElement element,
        List<ICssStyleRule> rules,
        List<Int32> candidates,
        RuleOrigin[] origins,
        Boolean[] pseudoOnly)
    {
        var scope = element.Owner?.DocumentElement;

        // Candidates arrive in ascending document order already. A stable insertion sort by
        // specificity keeps equal-specificity rules in that order, without the allocations a
        // LINQ sort would cost on every element. The order is kept for the Matched view; the
        // winner of each property is decided by origin, importance, specificity and document
        // order together (see Scan), where origin outranks specificity.
        var matched = new List<MatchedRule>(candidates.Count);
        var priorities = new List<Priority>(candidates.Count);

        for (var i = 0; i < candidates.Count; i++)
        {
            var index = candidates[i];

            if (pseudoOnly[index])
            {
                continue;
            }

            var rule = rules[index];

            if (rule.TryMatch(element, scope, out var specificity))
            {
                var at = matched.Count;

                while (at > 0 && priorities[at - 1] > specificity)
                {
                    at--;
                }

                matched.Insert(at, new MatchedRule(rule, origins[index], specificity, index));
                priorities.Insert(at, specificity);
            }
        }

        return matched;
    }

    /// <summary>
    /// Finds the cascaded property: the last important declaration for the name, or when
    /// none is important, the last normal one. Null when nothing declares it.
    /// </summary>
    public ICssProperty? Find(String name)
    {
        // A name that was never asked for is walked once and kept, null included: most names are
        // declared by nothing at all, and "nothing declared it" is the answer worth remembering.
        if (_found.TryGetValue(name, out var known))
        {
            return known;
        }

        var found = Scan(name);
        _found.Add(name, found);
        return found;
    }

    /// <summary>The walk itself, over the matched rules in the order the cascade applies them.</summary>
    /// <remarks>
    /// The winner of a property is the declaration with the highest cascade precedence: origin first
    /// (user-agent below author, user-agent !important above all but inline), then importance, then
    /// specificity, then document order. The list order is not relied on here - each declaration is
    /// weighed directly, because a single rule can mix normal and important declarations and so needs
    /// a different effective origin for each.
    /// </remarks>
    private ICssProperty? Scan(String name)
    {
        ICssProperty? best = null;
        var bestTier = -1;
        var bestSpecificity = default(Priority);
        var bestOrder = -1;

        for (var i = 0; i < _rules.Count; i++)
        {
            var matched = _rules[i];
            var property = matched.Rule.Style.TryGetProperty(name);

            if (property is null)
            {
                continue;
            }

            var tier = Tier(matched.Origin, property.IsImportant, false);

            if (Beats(tier, matched.Specificity, matched.DocOrder, bestTier, bestSpecificity, bestOrder))
            {
                best = property;
                bestTier = tier;
                bestSpecificity = matched.Specificity;
                bestOrder = matched.DocOrder;
            }
        }

        if (_inline is not null)
        {
            var property = _inline.TryGetProperty(name);

            if (property is not null)
            {
                // Inline style is applied last, so it outranks every normal declaration and, when
                // important, every other declaration. A normal inline declaration still loses to any
                // important one - matching the behaviour before origin was tracked.
                var tier = Tier(RuleOrigin.Author, property.IsImportant, true);

                if (Beats(tier, default, Int32.MaxValue, bestTier, bestSpecificity, bestOrder))
                {
                    best = property;
                }
            }
        }

        return best;
    }

    /// <summary>Gets the cascade tier of a declaration: the most significant part of its precedence.</summary>
    /// <remarks>
    /// Low to high: user-agent normal (1), user normal (2), author normal (3), inline normal (4),
    /// author !important (5), user !important (6), user-agent !important (7), inline !important (8).
    /// So an author declaration beats a user-agent one regardless of specificity, and any important
    /// declaration beats any normal one.
    /// </remarks>
    private static Int32 Tier(RuleOrigin origin, Boolean important, Boolean inline) => (inline, origin, important) switch
    {
        (true, _, true) => 8,   // inline !important: above everything
        (true, _, false) => 4,  // inline normal: above author normal, below any important
        (false, RuleOrigin.UserAgent, false) => 1,
        (false, RuleOrigin.User, false) => 2,
        (false, RuleOrigin.Author, false) => 3,
        (false, RuleOrigin.Author, true) => 5,
        (false, RuleOrigin.User, true) => 6,
        (false, RuleOrigin.UserAgent, true) => 7,
        _ => 3,
    };

    /// <summary>Gets whether the candidate declaration outranks the best held so far.</summary>
    private static Boolean Beats(
        Int32 tier,
        Priority specificity,
        Int32 order,
        Int32 bestTier,
        Priority bestSpecificity,
        Int32 bestOrder)
    {
        if (tier != bestTier)
        {
            return tier > bestTier;
        }

        if (specificity > bestSpecificity)
        {
            return true;
        }

        if (bestSpecificity > specificity)
        {
            return false;
        }

        return order > bestOrder;
    }

    /// <summary>
    /// Gets what one walk over the declarations answers: the names starting with <c>--</c> that any
    /// matched rule or the inline style declares - which decides whether a variable scope has to open
    /// for this element at all - and whether any value names an environment variable, which nothing in
    /// the engine can answer and so is the one substitution the fused path still gives the whole
    /// document up over. A <c>var()</c> in itself is no reason to give up: it is read through the
    /// scope the element's variables are resolved in.
    /// </summary>
    /// <remarks>
    /// Asked once per cascade, and the cascade is shared between the elements that matched alike, so
    /// the walk is paid once for each distinct set of declarations rather than once per element - the
    /// same shape as the answers <see cref="Find"/> remembers.
    /// </remarks>
    internal CascadeScan Scan()
    {
        _scan ??= Walk();
        return _scan.Value;
    }

    private static readonly IReadOnlyList<String> NoNames = Array.Empty<String>();

    private CascadeScan Walk()
    {
        List<String>? names = null;
        var environment = false;
        var variable = false;

        for (var i = 0; i < _rules.Count; i++)
        {
            Read(_rules[i].Rule.Style, ref names, ref environment, ref variable);
        }

        if (_inline is not null)
        {
            Read(_inline, ref names, ref environment, ref variable);
        }

        return new CascadeScan(names is null ? NoNames : names, variable, environment);
    }

    private static void Read(
        ICssStyleDeclaration declaration,
        ref List<String>? names,
        ref Boolean environment,
        ref Boolean variable)
    {
        foreach (var property in declaration)
        {
            var name = property.Name;

            if (name.StartsWith("--", StringComparison.Ordinal))
            {
                // The last declaration of a name is the one a lookup finds, and a name met twice costs
                // one wasted entry in a list walked once per variable, so the duplicate is left in
                // rather than paying for a set to keep it out.
                (names ??= new List<String>()).Add(name);
                continue;
            }

            var text = property.Value;

            if (String.IsNullOrEmpty(text))
            {
                continue;
            }

            if (!variable && text.Contains("var(", StringComparison.OrdinalIgnoreCase))
            {
                variable = true;
            }

            if (!environment && text.Contains("env(", StringComparison.OrdinalIgnoreCase))
            {
                environment = true;
            }
        }
    }

    /// <summary>What a walk over a cascade's declarations came back with.</summary>
    /// <param name="CustomProperties">The custom property names declared, empty when there are none.</param>
    /// <param name="UsesVariable">Whether a value names a variable - which needs a scope to read even
    /// when this cascade declares nothing, because the answer may be the fallback written after the name.</param>
    /// <param name="UsesEnvironment">Whether a value names an environment variable.</param>
    internal readonly record struct CascadeScan(
        IReadOnlyList<String> CustomProperties,
        Boolean UsesVariable,
        Boolean UsesEnvironment);
}

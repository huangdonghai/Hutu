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
public sealed class MatchedCascade
{
    private readonly List<ICssStyleRule> _rules;
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

    internal MatchedCascade(List<ICssStyleRule> rules, ICssStyleDeclaration? inline)
    {
        _rules = rules;
        _inline = inline;
    }

    /// <summary>Gets the rules that matched, in the order the cascade applies them.</summary>
    internal IReadOnlyList<ICssStyleRule> Matched => _rules;

    /// <summary>Gets the element's own inline style, which the cascade applies last of all.</summary>
    internal ICssStyleDeclaration? Inline => _inline;

    /// <summary>
    /// Gets the cascade an element matches, which the index may already have worked out for an
    /// element this one cannot differ from.
    /// </summary>
    public static MatchedCascade ForElement(RuleBucketIndex index, IElement element) =>
        index.Cascade(element);

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
    internal static List<ICssStyleRule> MatchRules(
        IElement element,
        List<ICssStyleRule> rules,
        List<Int32> candidates)
    {
        var scope = element.Owner?.DocumentElement;

        // Candidates arrive in ascending document order already. A stable insertion sort by
        // specificity keeps equal-specificity rules in that order, without the allocations a
        // LINQ sort would cost on every element.
        var matched = new List<ICssStyleRule>(candidates.Count);
        var priorities = new List<Priority>(candidates.Count);

        for (var i = 0; i < candidates.Count; i++)
        {
            var rule = rules[candidates[i]];

            if (rule.TryMatch(element, scope, out var specificity))
            {
                var at = matched.Count;

                while (at > 0 && priorities[at - 1] > specificity)
                {
                    at--;
                }

                matched.Insert(at, rule);
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
    private ICssProperty? Scan(String name)
    {
        ICssProperty? important = null;
        ICssProperty? normal = null;

        for (var i = 0; i < _rules.Count; i++)
        {
            Accumulate(_rules[i].Style.TryGetProperty(name), ref important, ref normal);
        }

        if (_inline is not null)
        {
            Accumulate(_inline.TryGetProperty(name), ref important, ref normal);
        }

        return important ?? normal;
    }

    private static void Accumulate(ICssProperty? property, ref ICssProperty? important, ref ICssProperty? normal)
    {
        if (property is null)
        {
            return;
        }

        if (property.IsImportant)
        {
            important = property;
        }
        else
        {
            normal = property;
        }
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
            Read(_rules[i].Style, ref names, ref environment, ref variable);
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

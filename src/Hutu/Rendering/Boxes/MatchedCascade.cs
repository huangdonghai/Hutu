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

    private MatchedCascade(List<ICssStyleRule> rules, ICssStyleDeclaration? inline)
    {
        _rules = rules;
        _inline = inline;
    }

    /// <summary>
    /// Matches the element against the index: bucket candidates plus matching, ordered the
    /// way the cascade applies them, with the element's inline style last.
    /// </summary>
    public static MatchedCascade ForElement(RuleBucketIndex index, IElement element)
    {
        var scope = element.Owner?.DocumentElement;
        var rules = index.Rules;
        var candidates = index.CollectCandidates(element);

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

        ICssStyleDeclaration? inline = null;

        if (element is IHtmlElement || element is ISvgElement)
        {
            inline = element.GetStyle();
        }

        return new MatchedCascade(matched, inline);
    }

    /// <summary>
    /// Finds the cascaded property: the last important declaration for the name, or when
    /// none is important, the last normal one. Null when nothing declares it.
    /// </summary>
    public ICssProperty? Find(String name)
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
    /// Gets whether any matched declaration's text mentions a substitution function. The
    /// fused resolvers read cascaded values as written and never resolve <c>var()</c> or
    /// <c>env()</c> the way computing a style does, so an element that uses either falls
    /// back to the render-tree path.
    /// </summary>
    public Boolean UsesSubstitution()
    {
        for (var i = 0; i < _rules.Count; i++)
        {
            if (MentionsSubstitution(_rules[i].Style))
            {
                return true;
            }
        }

        return _inline is not null && MentionsSubstitution(_inline);
    }

    private static Boolean MentionsSubstitution(ICssStyleDeclaration declaration)
    {
        foreach (var property in declaration)
        {
            var text = property.Value;

            if (!String.IsNullOrEmpty(text) &&
                (text.Contains("var(", StringComparison.OrdinalIgnoreCase) ||
                 text.Contains("env(", StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }

        return false;
    }
}

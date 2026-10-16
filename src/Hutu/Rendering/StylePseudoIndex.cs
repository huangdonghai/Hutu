using AngleSharp.Css;
using AngleSharp.Css.Dom;
using AngleSharp.Dom;
using System;
using System.Linq;

namespace Hutu.Rendering;

/// <summary>
/// Remembers which dynamic pseudo-classes the stylesheets feeding the current picture mention. A
/// mark whose pseudo appears in no selector cannot change any match, so the pipeline may skip the
/// rebuild its change would otherwise ask for.
/// </summary>
/// <remarks>
/// Active is tracked like the rest, but its short-circuit has a second condition in the view: the
/// engine's own link colour answers the active mark, so a press over a link always rebuilds
/// whatever the sheets say.
/// <para>
/// Fail-open throughout: any doubt resolves to "may affect", which is today's behaviour of
/// rebuilding. A short-circuit happens only on proven absence.
/// </para>
/// </remarks>
internal sealed class StylePseudoIndex
{
    [Flags]
    private enum Found : byte
    {
        None = 0,
        Hover = 1,
        Visited = 2,
        Target = 4,
        Active = 8,
        All = Hover | Visited | Target | Active,
    }

    private static readonly string[] Names = { "hover", "visited", "target", "active" };

    /// <summary>
    /// Gets whether a selector answers to one of the marks a reading gesture places - the four this
    /// index tracks - and so can change what matches without the document changing.
    /// </summary>
    /// <remarks>
    /// A selector that cannot be read as text answers yes, because nothing was learned from it: the
    /// same fail-open the index itself promises. Used by the style resolution to decide whether a
    /// rebuild under an unchanged viewport may keep the layout it has.
    /// </remarks>
    internal static Boolean NamesATrackedPseudo(string? selector)
    {
        if (selector is null)
        {
            return true;
        }

        for (var name = 0; name < Names.Length; name++)
        {
            if (ContainsPseudo(selector, Names[name]))
            {
                return true;
            }
        }

        return false;
    }

    // Everything set until the first scan: asking before anything was scanned rebuilds, which is
    // what not having the index at all would do.
    private Found _found = Found.All;

    /// <summary>Drops what was remembered, so asking rebuilds until the next scan.</summary>
    public void Clear() => _found = Found.All;

    /// <summary>
    /// Gets whether a change to the mark a pseudo-class answers to can influence matching. Unknown
    /// names answer yes: that is the rebuild, never the skip.
    /// </summary>
    public bool MayAffect(string pseudo)
    {
        if (pseudo.Equals(PseudoClassNames.Hover, StringComparison.OrdinalIgnoreCase))
        {
            return (_found & Found.Hover) != 0;
        }

        if (pseudo.Equals(PseudoClassNames.Visited, StringComparison.OrdinalIgnoreCase))
        {
            return (_found & Found.Visited) != 0;
        }

        if (pseudo.Equals(PseudoClassNames.Target, StringComparison.OrdinalIgnoreCase))
        {
            return (_found & Found.Target) != 0;
        }

        if (pseudo.Equals(PseudoClassNames.Active, StringComparison.OrdinalIgnoreCase))
        {
            return (_found & Found.Active) != 0;
        }

        return true;
    }

    /// <summary>
    /// Scans the sheets a build reads - the defaults plus the document's own - for the tracked
    /// pseudo-classes. Meant to run wherever a full build runs, so what is remembered never grows
    /// older than the picture on screen: between builds the sheets cannot have taken effect yet.
    /// A missing default sheet scans nothing and leaves everything set, which rebuilds.
    /// </summary>
    public void Refresh(IDocument? document, ICssStyleSheet? defaultSheet)
    {
        if (defaultSheet is null)
        {
            _found = Found.All;
            return;
        }

        var found = Found.None;
        ScanSheet(defaultSheet, ref found);

        if (document is not null)
        {
            foreach (var sheet in document.GetStyleSheets().OfType<ICssStyleSheet>())
            {
                ScanSheet(sheet, ref found);

                if (found == Found.All)
                {
                    break;
                }
            }
        }

        _found = found;
    }

    private static void ScanSheet(ICssStyleSheet sheet, ref Found found)
    {
        var rules = sheet.Rules;

        for (var i = 0; i < rules.Length; i++)
        {
            ScanRule(rules[i], ref found);

            if (found == Found.All)
            {
                return;
            }
        }
    }

    private static void ScanRule(ICssRule rule, ref Found found)
    {
        if (rule is ICssStyleRule style)
        {
            ScanText(style.SelectorText, ref found);

            // Rules nested in the style rule's own block (CSS nesting).
            var nested = style.Rules;

            for (var i = 0; i < nested.Length; i++)
            {
                ScanRule(nested[i], ref found);
            }
        }
        else if (rule is ICssGroupingRule group)
        {
            // @media, @supports and the like, scanned whether they match or not: a non-matching
            // group only ever rebuilds needlessly, while a missed one would skip a needed build.
            var inner = group.Rules;

            for (var i = 0; i < inner.Length; i++)
            {
                ScanRule(inner[i], ref found);
            }
        }
        // @font-face, @keyframes and @property carry no element selectors and need no walk.
    }

    /// <summary>
    /// Reads one rule's selector for the tracked pseudo-classes.
    /// </summary>
    /// <remarks>
    /// A selector the parser will not hand back as text is read as a rule that may mention anything,
    /// which is every one of the rules written for a pseudo-element the parser does not model - and
    /// <c>::marker</c> is one of those. Nothing was learned from such a rule, so nothing may be skipped
    /// because of it: the page <c>li:hover::marker</c> states a pseudo-class in the very selector that
    /// comes back null, and a scan that read it as nothing would let a hover rebuild be talked out of.
    /// This is the fail-open the class promises, bought at the price of the short-circuit on pages that
    /// mark their items - which is the trade a reader would make.
    /// </remarks>
    private static void ScanText(string? text, ref Found found)
    {
        if (text is null)
        {
            found = Found.All;

            return;
        }

        for (var name = 0; name < Names.Length; name++)
        {
            if (((byte)found & (1 << name)) == 0 && ContainsPseudo(text, Names[name]))
            {
                found |= (Found)(1 << name);

                if (found == Found.All)
                {
                    return;
                }
            }
        }
    }

    /// <summary>
    /// Finds a pseudo-class in selector text, colon included: a class called hover carries no colon
    /// and never matches, while the colon of :hover is the pseudo-class's own. Functional contents
    /// are plain text here - :not(:hover) names hover - which is exactly right, because a hovered
    /// element stops matching :not(:hover).
    /// </summary>
    private static bool ContainsPseudo(string text, string name)
    {
        var index = 0;

        while ((index = text.IndexOf(':', index)) >= 0)
        {
            var start = index + 1;

            if (start + name.Length <= text.Length &&
                string.Compare(text, start, name, 0, name.Length, StringComparison.OrdinalIgnoreCase) == 0)
            {
                var before = index == 0 ? '\0' : text[index - 1];
                var end = start + name.Length;
                var after = end >= text.Length ? '\0' : text[end];

                // Before the colon: a second colon (::before) or a backslash (an escaped .\:hover
                // class) means this is not a pseudo-class; anything else - an element name, an
                // opening bracket, a quote - either is the pseudo-class or leans towards rebuilding.
                // After the name: only a name character disqualifies (:hoverx); an opening bracket
                // is the functional form (:not(), :has()) and counts.
                if (before != ':' && before != '\\' && !IsNameChar(after))
                {
                    return true;
                }
            }

            index++;
        }

        return false;
    }

    private static bool IsNameChar(char c) =>
        c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '-' or '_';
}

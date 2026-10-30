using AngleSharp.Css;
using AngleSharp.Css.Dom;
using AngleSharp.Css.Parser;
using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using AngleSharp.Svg.Dom;
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace Hutu.Rendering.Boxes;

/// <summary>
/// The kind of an essential selector key: a name the rightmost compound selector requires
/// every matched element to carry.
/// </summary>
internal enum RuleKeyKind : Byte
{
    /// <summary>The key is an id, compared ordinally.</summary>
    Id = 0,

    /// <summary>The key is a class name, compared ordinally.</summary>
    Class = 1,

    /// <summary>The key is a tag name, compared case-insensitively.</summary>
    Tag = 2,
}

/// <summary>
/// An essential property of a selector: the id, class or tag name the rightmost compound
/// selector requires of every element it matches.
/// </summary>
internal readonly struct RuleKey
{
    public RuleKey(RuleKeyKind kind, String value)
    {
        Kind = kind;
        Value = value;
    }

    public RuleKeyKind Kind { get; }

    public String Value { get; }
}

/// <summary>
/// Derives the essential keys of a selector: the id, class or tag names the rightmost
/// compound selector requires of every element it matches.
/// </summary>
/// <remarks>
/// A key is only reported when it is a necessary condition of a match, so an element that
/// does not carry it can never be matched. Whenever a selector cannot be reduced to such a
/// condition the whole selector yields no keys at all, and its rule is consulted for every
/// element. This mirrors the proven extraction in AngleSharp.Css, using only its public
/// selector visitor contract.
/// </remarks>
internal sealed class SelectorKeyExtractor : ISelectorVisitor
{
    private const String AnyName = "*";
    private const String NestingName = "&";

    private readonly List<RuleKey> _keys = new();
    private readonly List<String> _classes = new();

    private String? _id;
    private String? _tag;
    private Boolean _hasKeylessPosition;
    private Boolean _collecting;

    /// <summary>
    /// Gets how many compounds of the selector asked for a key, counted whether or not the key was
    /// new. A position that asked for one is a position the index can prune with.
    /// </summary>
    private Int32 _flushed;

    private SelectorKeyExtractor()
    {
    }

    public static RuleKey[]? Extract(ISelector? selector)
    {
        if (selector is null)
        {
            return null;
        }

        var extractor = new SelectorKeyExtractor();
        extractor.VisitPosition(selector);
        return extractor._hasKeylessPosition || extractor._keys.Count == 0
            ? null
            : extractor._keys.ToArray();
    }

    private void VisitPosition(ISelector selector)
    {
        ResetKey();

        var collecting = _collecting;
        var counted = _flushed;
        _collecting = true;
        selector.Accept(this);
        _collecting = collecting;

        // Whether this position produced a key is asked of the counter rather than of the key list,
        // because the list deduplicates: a selector that names the same class twice - and every
        // selector list of the form `.a .b, .c .b`, which is how a sheet states one rule for several
        // places of a page - adds nothing the second time. Reading the flat list length then says the
        // second half had no key, the rule joins the bucket every element is tested against, and a
        // page that writes its selectors this way pays for the whole sheet per element.
        if (_flushed == counted && !FlushKey())
        {
            _hasKeylessPosition = true;
        }
    }

    private Boolean FlushKey()
    {
        var found = false;

        if (_id is not null)
        {
            Append(RuleKeyKind.Id, _id);
            found = true;
        }
        else if (_classes.Count > 0)
        {
            Append(RuleKeyKind.Class, _classes[0]);
            found = true;
        }
        else if (_tag is not null)
        {
            Append(RuleKeyKind.Tag, _tag);
            found = true;
        }

        if (found)
        {
            _flushed++;
        }

        ResetKey();
        return found;
    }

    private void ResetKey()
    {
        _id = null;
        _tag = null;
        _classes.Clear();
    }

    private void Append(RuleKeyKind kind, String value)
    {
        var comparison = kind == RuleKeyKind.Tag
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        for (var i = 0; i < _keys.Count; i++)
        {
            if (_keys[i].Kind == kind && String.Equals(_keys[i].Value, value, comparison))
            {
                return;
            }
        }

        _keys.Add(new RuleKey(kind, value));
    }

    void ISelectorVisitor.List(IEnumerable<ISelector> selectors)
    {
        foreach (var selector in selectors)
        {
            VisitPosition(selector);
        }
    }

    void ISelectorVisitor.Combinator(IEnumerable<ISelector> selectors, IEnumerable<String> symbols)
    {
        var last = default(ISelector);

        foreach (var selector in selectors)
        {
            last = selector;
        }

        if (last is not null)
        {
            VisitPosition(last);
        }
    }

    void ISelectorVisitor.Many(IEnumerable<ISelector> selectors)
    {
        foreach (var selector in selectors)
        {
            selector.Accept(this);
        }
    }

    void ISelectorVisitor.Id(String value)
    {
        if (_collecting && _id is null)
        {
            _id = value;
        }
    }

    void ISelectorVisitor.Class(String name)
    {
        if (_collecting && !_classes.Contains(name))
        {
            _classes.Add(name);
        }
    }

    void ISelectorVisitor.Type(String name)
    {
        if (_collecting && _tag is null &&
            !String.Equals(name, AnyName, StringComparison.Ordinal) &&
            !String.Equals(name, NestingName, StringComparison.Ordinal))
        {
            _tag = name;
        }
    }

    void ISelectorVisitor.Attribute(String name, String op, String? value)
    {
    }

    void ISelectorVisitor.Child(String name, Int32 step, Int32 offset, ISelector selector)
    {
    }

    void ISelectorVisitor.PseudoClass(String name)
    {
    }

    void ISelectorVisitor.PseudoElement(String name)
    {
    }
}

/// <summary>
/// Groups style rules by the essential key of their rightmost compound selector, so an
/// element is only tested against the rules that could match it.
/// </summary>
/// <remarks>
/// The index never decides a match: it only prunes rules that provably cannot match. Every
/// rule whose selector yields no key lands in the unkeyed bucket every element consults.
/// Built from public CSSOM surface only: sheet enumeration, media validation, flattened
/// matching styles, and per-rule matching.
/// </remarks>
internal sealed class RuleBucketIndex
{
    private readonly List<ICssStyleRule> _rules;
    private readonly Dictionary<String, List<Int32>> _idBuckets;
    private readonly Dictionary<String, List<Int32>> _classBuckets;
    private readonly Dictionary<String, List<Int32>> _tagBuckets;
    private readonly List<Int32> _unkeyed;

    /// <summary>
    /// The cascades already matched for this document, keyed on the match itself.
    /// </summary>
    /// <remarks>
    /// The key is the rules an element matched, in the order the cascade applied them, together
    /// with its inline style - which is everything a lookup through the cascade depends on, so two
    /// elements that share a key cannot disagree about a single answer. It is not a prediction of
    /// the match: matching is what produces it. That costs the match, which is a fraction of a
    /// percent of a build, and in exchange no selector can make the key wrong - a descendant
    /// combinator, an attribute selector, a hover mark and a state the engine reads off the
    /// element all show up in what matched rather than having to be reasoned about here.
    /// <para>
    /// The cache lives as long as the index does, which is one build. Nothing can go stale inside
    /// that: the rules are fixed for the whole of it.
    /// </para>
    /// </remarks>
    private readonly Dictionary<CascadeKey, MatchedCascade> _cascades = new();

    /// <summary>
    /// What each element of this document was last matched against: the cascade it came to, the
    /// marks it was matched under, the inline style text it was matched with, and whether any rule
    /// that could match it reaches outside the element.
    /// </summary>
    /// <remarks>
    /// A match is a function of the element's own name, attributes and marks, and of the marks on
    /// its ancestors and siblings - the last two only because a selector with a combinator in it
    /// reads them. So for an element no such rule could reach, the match is settled by the element
    /// alone, and the only thing that can move it between two builds is a mark on the element
    /// itself. That is the whole of what has to be checked to know the answer in hand is still the
    /// answer, and it is checked instead of matching, which walks every candidate selector.
    /// <para>
    /// The inline style text is held because it is the other input the match is read with: it is
    /// the attribute, compared by identity, which is enough because AngleSharp rewrites the
    /// attribute whenever the declaration changes.
    /// </para>
    /// </remarks>
    private readonly Dictionary<IElement, (MatchedCascade Cascade, Byte Marks, String? Inline, Boolean ReachesOutside)> _matches = new();

    /// <summary>
    /// Whether each rule's selector reads a mark off an element other than the one it would match: a
    /// pseudo-class answering to a mark, written on a compound that stands in a relationship with
    /// the subject, or inside <c>:has()</c>. Such a rule's answer depends on the element's ancestors
    /// and siblings, so a mark placed on any of them can move it.
    /// </summary>
    private readonly Boolean[] _reaches;

    /// <summary>
    /// The origin of each flattened rule - user-agent or author - in the same order as
    /// <see cref="_rules"/>. It is what lets an author declaration win over a user-agent one
    /// regardless of specificity (see <see cref="MatchedCascade.Tier"/>).
    /// </summary>
    private readonly RuleOrigin[] _origins;

    /// <summary>
    /// The arms of the sheet that are written for <c>::before</c> and <c>::after</c>, which match no
    /// element and so are held apart from the rules they came from. What they select is a box this
    /// engine has to make itself.
    /// </summary>
    private readonly PseudoRuleIndex _pseudo;

    /// <summary>
    /// For each flattened rule, whether every arm of its selector is written for a pseudo-element.
    /// Such a rule names no element and is answered only through <see cref="_pseudo"/>; the parser
    /// keeps a pseudo-element step matching the element it originates, so the element cascade has to
    /// leave these rules out by itself.
    /// </summary>
    private readonly Boolean[] _pseudoOnly;

    /// <summary>
    /// How many elements were answered from the match the last build left, and how many had to be
    /// matched again, since <see cref="ResetMatchCounters"/>. Counted always - a pair of increments
    /// is nothing next to a match - and read only by the probes.
    /// </summary>
    private Int32 _heldMatches;
    private Int32 _freshMatches;
    private readonly CounterStyleTable _counters;
    private readonly PageStyleTable _pages;

    private RuleBucketIndex(
        List<ICssStyleRule> rules,
        Dictionary<String, List<Int32>> idBuckets,
        Dictionary<String, List<Int32>> classBuckets,
        Dictionary<String, List<Int32>> tagBuckets,
        List<Int32> unkeyed,
        Boolean[] reaches,
        RuleOrigin[] origins,
        PseudoRuleIndex pseudo,
        Boolean[] pseudoOnly,
        CounterStyleTable counters,
        PageStyleTable pages)
    {
        _rules = rules;
        _idBuckets = idBuckets;
        _classBuckets = classBuckets;
        _tagBuckets = tagBuckets;
        _unkeyed = unkeyed;
        _reaches = reaches;
        _origins = origins;
        _pseudo = pseudo;
        _pseudoOnly = pseudoOnly;
        _counters = counters;
        _pages = pages;
    }

    /// <summary>Gets the flattened rules in the order they are matched in.</summary>
    public List<ICssStyleRule> Rules => _rules;

    /// <summary>Gets the origin of each flattened rule, in the same order as <see cref="Rules"/>.</summary>
    internal RuleOrigin[] Origins => _origins;

    /// <summary>Gets the arms written for the generated-content pseudo-elements.</summary>
    internal PseudoRuleIndex Pseudo => _pseudo;

    /// <summary>
    /// Gets the counter styles the sheets define with <c>@counter-style</c>, which is what a marker or a
    /// <c>counter()</c> is spelled with when the name it carries is one the page wrote.
    /// </summary>
    internal CounterStyleTable Counters => _counters;

    /// <summary>
    /// Gets the page rules the sheets carry for this device. The page box reads them from here rather
    /// than from the element cascade: <c>@page</c> names no element, and its declarations answer a
    /// page, not a box.
    /// </summary>
    internal PageStyleTable Pages => _pages;

    /// <summary>Gets how many elements stood on the match of the last build since the counters were reset.</summary>
    internal Int32 HeldMatches => _heldMatches;

    /// <summary>Gets how many elements had to be matched again since the counters were reset.</summary>
    internal Int32 FreshMatches => _freshMatches;

    /// <summary>Starts the match counters over, for a caller that means to read them after one build.</summary>
    internal void ResetMatchCounters()
    {
        _heldMatches = 0;
        _freshMatches = 0;
    }

    /// <summary>
    /// Collects the rules of the given sheets for the device and indexes them: sheets in
    /// order, skipping disabled sheets and sheets whose media does not validate, with each
    /// sheet's matching styles (media and supports blocks resolved) in place.
    /// </summary>
    public static RuleBucketIndex Build(IEnumerable<ICssStyleSheet> sheets, IRenderDevice device)
    {
        var collected = new List<ICssStyleRule>();
        var origins = new List<RuleOrigin>();
        var styles = new List<ICssCounterStyleRule>();
        var pages = new List<ICssPageRule>();

        foreach (var sheet in sheets)
        {
            if (!sheet.IsDisabled && SheetApplies(sheet, device))
            {
                // A sheet the document authored carries an owner node; the reader's default
                // (user-agent) sheet is parsed without one. Origin is the cascade's most
                // significant sort key: an author declaration beats a user-agent one whatever
                // the latter's specificity (see MatchedCascade.Tier).
                var origin = sheet.OwnerNode is null ? RuleOrigin.UserAgent : RuleOrigin.Author;

                for (var i = 0; i < sheet.Rules.Length; i++)
                {
                    // The counter styles are collected from the sheet's own list rather than from the
                    // matching styles the cascade is built from: `@counter-style` names no element, so
                    // the walk that hands out rules to match drops it - and a style defined in a media
                    // block that does not apply is dropped here the same way, by the group's own check.
                    CollectCounterStyles(sheet.Rules[i], styles, device);

                    // `@page` names no element either: the page box reads its declarations from a
                    // table of its own, built here where the sheets and the device are both in hand.
                    CollectPageRules(sheet.Rules[i], pages, device);
                }

                foreach (var rule in sheet.Rules.GetMatchingStyles(device))
                {
                    Flatten(rule, collected, origins, origin);
                }

                // A selector naming a pseudo-element the CSS parser does not know is dropped whole,
                // declarations and all. The scroll markers are the case that matters here: the
                // parser knows ::before and ::marker, but not ::scroll-marker. Read those rules from
                // the <style> text the parser was given and hand the pseudo-element index a rule that
                // keeps the original selector while carrying the declarations parsed without it.
                if (sheet.OwnerNode is IElement style && style.LocalName == "style")
                {
                    var parser = style.Owner?.Context.GetService<ICssParser>();

                    if (parser is not null)
                    {
                        CollectScrollMarkerRules(style.TextContent, parser, collected, origins, origin);
                    }
                }
            }
        }

        var flattened = collected;

        var idBuckets = new Dictionary<String, List<Int32>>(StringComparer.Ordinal);
        var classBuckets = new Dictionary<String, List<Int32>>(StringComparer.Ordinal);
        var tagBuckets = new Dictionary<String, List<Int32>>(StringComparer.OrdinalIgnoreCase);
        var unkeyed = new List<Int32>();
        var reaches = new Boolean[flattened.Count];
        var pseudoOnly = new Boolean[flattened.Count];

        for (var i = 0; i < flattened.Count; i++)
        {
            var keys = SelectorKeyExtractor.Extract(flattened[i].Selector);
            pseudoOnly[i] = PseudoRuleIndex.EveryArmIsWrittenForAPseudoElement(flattened[i].SelectorText);

            if (keys is null || keys.Length == 0)
            {
                unkeyed.Add(i);
            }
            else
            {
                for (var j = 0; j < keys.Length; j++)
                {
                    var key = keys[j];
                    var buckets = key.Kind switch
                    {
                        RuleKeyKind.Id => idBuckets,
                        RuleKeyKind.Class => classBuckets,
                        _ => tagBuckets,
                    };

                    if (!buckets.TryGetValue(key.Value, out var indices))
                    {
                        indices = new List<Int32>();
                        buckets.Add(key.Value, indices);
                    }

                    indices.Add(i);
                }
            }

            // Asked of every rule, keyed or not: an unkeyed rule is one no key could be taken from -
            // a bare `*`, or a selector list one of whose arms is - and that says nothing about
            // whether it reads a mark off somebody else. It matters more here than anywhere, because
            // an unkeyed rule is consulted for every element of the document.
            reaches[i] = ReachesOutside(flattened[i]);
        }

        var pageDefaults = device as HutuRenderDevice;

        return new RuleBucketIndex(
            flattened, idBuckets, classBuckets, tagBuckets, unkeyed, reaches, origins.ToArray(), PseudoRuleIndex.Build(flattened),
            pseudoOnly, CounterStyleTable.Build(styles),
            new PageStyleTable(
                pages,
                device,
                pageDefaults?.DefaultPageSize ?? PageSize.Default,
                pageDefaults?.DefaultPageMargins ?? default));
    }

    /// <summary>
    /// Reads the style rules that name a scroll-marker pseudo-element out of a <c>&lt;style&gt;</c>
    /// element's text. The CSS parser drops such a rule because it does not know the pseudo-element,
    /// so the text is walked here and each arm is parsed again without the pseudo-element, keeping
    /// the declarations the page wrote.
    /// </summary>
    private static void CollectScrollMarkerRules(
        String css,
        ICssParser parser,
        List<ICssStyleRule> collected,
        List<RuleOrigin> origins,
        RuleOrigin origin)
    {
        var i = 0;

        while (i < css.Length)
        {
            while (i < css.Length && Char.IsWhiteSpace(css[i]))
            {
                i++;
            }

            if (i >= css.Length)
            {
                return;
            }

            if (css[i] == '/' && i + 1 < css.Length && css[i + 1] == '*')
            {
                i = SkipComment(css, i);
                continue;
            }

            // An at-rule either ends at a semicolon (an @import, say) or holds a block. Only the
            // grouping rules can hold style rules, so only those are walked into.
            if (css[i] == '@')
            {
                var atOpen = css.IndexOf('{', i);
                var atSemi = css.IndexOf(';', i);

                if (atSemi >= 0 && (atOpen < 0 || atSemi < atOpen))
                {
                    i = atSemi + 1;
                    continue;
                }

                if (atOpen < 0)
                {
                    return;
                }

                var atClose = MatchingBrace(css, atOpen);

                if (atClose < 0)
                {
                    return;
                }

                var header = css[i..atOpen].Trim();

                if (header.StartsWith("@media", StringComparison.OrdinalIgnoreCase) ||
                    header.StartsWith("@supports", StringComparison.OrdinalIgnoreCase) ||
                    header.StartsWith("@layer", StringComparison.OrdinalIgnoreCase))
                {
                    CollectScrollMarkerRules(css[(atOpen + 1)..atClose], parser, collected, origins, origin);
                }

                i = atClose + 1;
                continue;
            }

            var open = css.IndexOf('{', i);

            if (open < 0)
            {
                return;
            }

            var selector = css[i..open].Trim();
            var close = MatchingBrace(css, open);

            if (close < 0)
            {
                return;
            }

            var scrollMarker = selector.Contains("::scroll-marker", StringComparison.OrdinalIgnoreCase);
            var scrollButton = selector.Contains("::scroll-button(", StringComparison.OrdinalIgnoreCase);

            if (scrollMarker || scrollButton)
            {
                var body = css[(open + 1)..close];

                foreach (var rawArm in SplitSelectors(selector))
                {
                    var arm = rawArm.Trim();

                    if (!PseudoRange(arm, scrollMarker, out var pseudo, out var length))
                    {
                        continue;
                    }

                    // The selector text keeps the pseudo-element, which is what the pseudo-element
                    // index reads; the selector it parses for matching is the arm without it. A
                    // pseudo-class written after the pseudo-element - `:enabled`, say - belongs to
                    // the box the pseudo-element makes, so it is taken off with it and the rule is
                    // read as one for the button.
                    var selectorText = arm[..(pseudo + length)];
                    var stripped = arm[..pseudo].Trim();

                    if (stripped.Length == 0)
                    {
                        stripped = "*";
                    }

                    var sheet = parser.ParseStyleSheet(stripped + "{" + body + "}");

                    if (sheet.Rules.Length > 0 && sheet.Rules[0] is ICssStyleRule inner)
                    {
                        collected.Add(new SyntheticPseudoRule(inner, selectorText));
                        origins.Add(origin);
                    }
                }
            }

            i = close + 1;
        }
    }

    /// <summary>Gets where the scroll-marker pseudo-element begins in a selector arm, or -1.</summary>
    private static Int32 ScrollMarkerPseudo(String arm)
    {
        var group = arm.LastIndexOf("::scroll-marker-group", StringComparison.OrdinalIgnoreCase);
        var marker = arm.LastIndexOf("::scroll-marker", StringComparison.OrdinalIgnoreCase);
        return Math.Max(group, marker);
    }

    /// <summary>
    /// Gets where the scroll pseudo-element the arm names begins and how long its name is. A scroll
    /// marker is named without arguments; a scroll button carries the edge it stands on in parentheses.
    /// </summary>
    private static Boolean PseudoRange(String arm, Boolean marker, out Int32 start, out Int32 length)
    {
        start = -1;
        length = 0;

        if (marker)
        {
            start = ScrollMarkerPseudo(arm);

            if (start < 0)
            {
                return false;
            }

            length = arm.AsSpan(start).StartsWith("::scroll-marker-group", StringComparison.OrdinalIgnoreCase)
                ? "::scroll-marker-group".Length
                : "::scroll-marker".Length;
            return true;
        }

        start = arm.LastIndexOf("::scroll-button(", StringComparison.OrdinalIgnoreCase);

        if (start < 0)
        {
            return false;
        }

        var close = arm.IndexOf(')', start + "::scroll-button(".Length);

        if (close < 0)
        {
            start = -1;
            return false;
        }

        length = close - start + 1;
        return true;
    }

    /// <summary>Finds the brace that closes the block opened at <paramref name="open"/>.</summary>
    private static Int32 MatchingBrace(String css, Int32 open)
    {
        var depth = 0;
        var quote = '\0';

        for (var i = open; i < css.Length; i++)
        {
            var c = css[i];

            if (quote != '\0')
            {
                if (c == '\\')
                {
                    i++;
                }
                else if (c == quote)
                {
                    quote = '\0';
                }

                continue;
            }

            if (c is '"' or '\'')
            {
                quote = c;
            }
            else if (c == '/' && i + 1 < css.Length && css[i + 1] == '*')
            {
                i = SkipComment(css, i) - 1;
            }
            else if (c == '{')
            {
                depth++;
            }
            else if (c == '}')
            {
                depth--;

                if (depth == 0)
                {
                    return i;
                }
            }
        }

        return -1;
    }

    /// <summary>Gets the position just after the comment that starts at <paramref name="at"/>.</summary>
    private static Int32 SkipComment(String css, Int32 at)
    {
        var end = css.IndexOf("*/", at + 2, StringComparison.Ordinal);
        return end < 0 ? css.Length : end + 2;
    }

    /// <summary>Splits a selector list at the commas that are not inside brackets or quotes.</summary>
    private static List<String> SplitSelectors(String text)
    {
        var parts = new List<String>();
        var start = 0;
        var depth = 0;
        var quote = '\0';

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];

            if (quote != '\0')
            {
                if (c == '\\')
                {
                    i++;
                }
                else if (c == quote)
                {
                    quote = '\0';
                }

                continue;
            }

            if (c is '"' or '\'')
            {
                quote = c;
            }
            else if (c is '(' or '[')
            {
                depth++;
            }
            else if (c is ')' or ']')
            {
                depth = Math.Max(0, depth - 1);
            }
            else if (c == ',' && depth == 0)
            {
                parts.Add(text[start..i]);
                start = i + 1;
            }
        }

        parts.Add(text[start..]);
        return parts;
    }

    /// <summary>
    /// Gets whether a rule's answer depends on the marks of any element but the one it would match.
    /// </summary>
    /// <remarks>
    /// A match is read off three things: the element's own name, attributes and marks, which are
    /// either fixed for the life of the document or are marks the engine places on that element; the
    /// shape of what is above and beside it, which is fixed too; and - only through a selector that
    /// has both a combinator and a pseudo-class answering to a mark - the marks on those ancestors
    /// and siblings. <c>div p</c> reads the shape and can never move; <c>.note:hover</c> reads the
    /// element's own mark, which the stamp already carries; <c>div:hover p</c> and
    /// <c>.a:hover + .b</c> read somebody else's, and those are what this is for.
    /// <para>
    /// The arms of a selector list are asked one at a time. The space that follows a comma is not a
    /// combinator, and a sheet that states one rule for several places is nothing but spaces:
    /// <c>:link, :visited</c>, <c>td, th</c>, and every stylesheet that opens with a list of the
    /// elements it is about. Asking the whole text would find a descendant combinator in each of
    /// them, and a rule that reaches outside is a rule that stands no element's match down - and a
    /// rule with no key is consulted for every element of the document, so one such rule costs the
    /// whole document a match on every pointer move.
    /// </para>
    /// <para>
    /// What decides within an arm is which compound the pseudo-class is written on. A combinator is
    /// "a condition of relationship between two elements represented by the compound selectors on
    /// either side" (Selectors 4 §3.1), so a pseudo-class on the rightmost compound - the one the
    /// arm matches - relates the element to nothing and is read off the element alone. That is the
    /// compound a real sheet puts it on: <c>.nav .item:hover</c> asks for a shape above the element,
    /// which is fixed, and for the element's own mark, which the stamp carries, while
    /// <c>.nav:hover .item</c> asks for the mark of whatever is above it, which nothing in the
    /// element's own stamp can speak for. Reading the arm for any combinator at all instead made
    /// every one of the first kind reach outside, and a page whose rules mark their items - which is
    /// every page with a hover rule on a list - then stood no element's match down at all.
    /// </para>
    /// <para>
    /// Only a selector nested in the subject compound can reach past it. <c>:is()</c>, <c>:where()</c>
    /// and <c>:not()</c> take a selector list of their own, and §4 of Selectors 4 lets a combinator
    /// stand inside one, so <c>:is(.a:hover .b)</c> asks for an ancestor's mark exactly as the
    /// written-out selector does; each argument is asked the same question in turn. A functional
    /// pseudo-class whose shape is not known here - <c>:nth-child(2n of …)</c> is the one that takes
    /// a selector list - answers yes when its argument names a tracked pseudo-class, which costs a
    /// match that was not needed, the direction this errs in.
    /// </para>
    /// </remarks>
    private static Boolean ReachesOutside(ICssStyleRule rule)
    {
        var selector = rule.SelectorText;

        // A rule the parser never gave a selector to matches no element at all: TryMatch asks the
        // selector list and then the selector, and a rule whose selector failed to parse has
        // neither, so it answers false every time. Nor can it move an element's answer, and a rule
        // that reaches outside is one that stands no element's match down - so failing open here
        // costs the whole document its hold. `::-webkit-scrollbar` and its track and thumb are what
        // a real sheet writes once each and AngleSharp will not take, and since no key can be taken
        // from a selector that is not there either, every one of them is consulted for every
        // element of the page.
        if (selector is null)
        {
            return rule.Selector is not null;
        }

        // No mark can move this rule, so nothing about it can have changed either.
        if (!StylePseudoIndex.NamesATrackedPseudo(selector))
        {
            return false;
        }

        return ListReachesOutside(selector, 0, selector.Length);
    }

    /// <summary>
    /// Gets whether any arm of a selector list reads a mark off an element other than the one it
    /// would match.
    /// </summary>
    /// <remarks>
    /// The text is walked once, splitting at the commas that stand outside brackets and quotes, so
    /// that the arms of a list are asked one at a time and a list written inside <c>:is()</c> is not
    /// read as arms of the list it sits in.
    /// </remarks>
    private static Boolean ListReachesOutside(String text, Int32 start, Int32 end)
    {
        var depth = 0;
        var quote = '\0';
        var arm = start;

        for (var i = start; i < end; i++)
        {
            var c = text[i];

            if (quote != '\0')
            {
                if (c == quote)
                {
                    quote = '\0';
                }

                continue;
            }

            switch (c)
            {
                case '"' or '\'':
                    quote = c;
                    break;
                case '(' or '[' or '{':
                    depth++;
                    break;
                case ')' or ']' or '}':
                    if (depth > 0)
                    {
                        depth--;
                    }

                    break;
                case ',' when depth == 0:
                    if (ArmReachesOutside(text, arm, i))
                    {
                        return true;
                    }

                    arm = i + 1;
                    break;
            }
        }

        return ArmReachesOutside(text, arm, end);
    }

    /// <summary>
    /// Gets whether one arm of a selector reads a mark off an element other than the one it would
    /// match.
    /// </summary>
    /// <remarks>
    /// The arm is asked in two halves: everything left of the subject, which is read through a
    /// combinator, and the subject compound, which is the element itself.
    /// </remarks>
    private static Boolean ArmReachesOutside(String text, Int32 start, Int32 end)
    {
        // The space that followed the comma is now at the head of the arm, and the one before the
        // next comma is at its tail. Neither is a combinator: a selector cannot begin or end with
        // one. Trimming them is what makes the arms of `a , b` come out as `a` and `b`.
        while (start < end && text[start] is ' ' or '\t' or '\n' or '\r')
        {
            start++;
        }

        while (end > start && text[end - 1] is ' ' or '\t' or '\n' or '\r')
        {
            end--;
        }

        // Everything left of the subject stands in a relationship with it, so a pseudo-class in
        // there answers to a mark on whatever is at the far end of that relationship, and so does a
        // `:has()` of its own, which Selectors 4 §4.5 makes a relative selector list.
        var subject = SubjectStart(text, start, end);

        if (StylePseudoIndex.NamesATrackedPseudo(text[start..subject]))
        {
            return true;
        }

        return SubjectReachesOutside(text, subject, end);
    }

    /// <summary>
    /// Gets the offset the subject compound of an arm starts at: just past the last combinator that
    /// stands outside every bracket and quote.
    /// </summary>
    /// <remarks>
    /// Every character of a run of space is a descendant combinator, so the last of them is the one
    /// that counts; the space a sheet writes around a <c>&gt;</c> is not a combinator of its own,
    /// which the trim at the end of the walk is what leaves out. A <c>+</c> inside
    /// <c>:nth-child()</c> and a <c>~</c> inside an attribute value stand inside a bracket and are
    /// not combinators at all.
    /// </remarks>
    private static Int32 SubjectStart(String text, Int32 start, Int32 end)
    {
        var subject = start;
        var depth = 0;
        var quote = '\0';

        for (var i = start; i < end; i++)
        {
            var c = text[i];

            if (quote != '\0')
            {
                if (c == quote)
                {
                    quote = '\0';
                }

                continue;
            }

            switch (c)
            {
                case '"' or '\'':
                    quote = c;
                    break;
                case '(' or '[' or '{':
                    depth++;
                    break;
                case ')' or ']' or '}':
                    if (depth > 0)
                    {
                        depth--;
                    }

                    break;
                case ' ' or '\t' or '\n' or '\r' or '>' or '+' or '~' when depth == 0:
                    subject = i + 1;
                    break;
            }
        }

        while (subject < end && text[subject] is ' ' or '\t' or '\n' or '\r')
        {
            subject++;
        }

        return subject;
    }

    /// <summary>
    /// Gets whether the subject compound of an arm reads a mark off something other than the element
    /// it would match, which only a selector nested inside it can.
    /// </summary>
    /// <remarks>
    /// A pseudo-class on the compound is the element's own, and a colon written inside an attribute
    /// value is read the same way: no mark can move either. What is not the element's own is the
    /// argument of <c>:is()</c>, <c>:where()</c> or <c>:not()</c>, which is a selector list that may
    /// hold a combinator of its own and is asked the same question in turn; and the argument of
    /// anything else that takes one - <c>:has()</c> above all - which answers yes as soon as a
    /// tracked pseudo-class is named anywhere in it.
    /// </remarks>
    private static Boolean SubjectReachesOutside(String text, Int32 start, Int32 end)
    {
        var i = start;

        while (i < end)
        {
            if (text[i] != ':')
            {
                i++;
                continue;
            }

            var name = i + 1;
            var nameEnd = name;

            while (nameEnd < end && StylePseudoIndex.IsNameChar(text[nameEnd]))
            {
                nameEnd++;
            }

            if (nameEnd >= end || text[nameEnd] != '(')
            {
                // A pseudo-class of the compound, or the empty name a second colon leaves, which
                // the next turn of the walk reads as the pseudo-element it is.
                i = nameEnd > name ? nameEnd : i + 1;
                continue;
            }

            var close = GroupEnd(text, nameEnd, end);
            var function = text.AsSpan(name, nameEnd - name);

            if (function.Equals("is", StringComparison.OrdinalIgnoreCase) ||
                function.Equals("not", StringComparison.OrdinalIgnoreCase) ||
                function.Equals("where", StringComparison.OrdinalIgnoreCase))
            {
                if (ListReachesOutside(text, nameEnd + 1, close))
                {
                    return true;
                }
            }
            else if (StylePseudoIndex.NamesATrackedPseudo(text[(nameEnd + 1)..close]))
            {
                return true;
            }

            i = close + 1;
        }

        return false;
    }

    /// <summary>
    /// Gets the offset of the bracket closing the one at <paramref name="open"/>, or the end of the
    /// text when nothing closes it - which leaves whatever was written inside it unanswered rather
    /// than reading the rest of the selector as part of the argument.
    /// </summary>
    private static Int32 GroupEnd(String text, Int32 open, Int32 end)
    {
        var depth = 0;
        var quote = '\0';

        for (var i = open; i < end; i++)
        {
            var c = text[i];

            if (quote != '\0')
            {
                if (c == quote)
                {
                    quote = '\0';
                }

                continue;
            }

            switch (c)
            {
                case '"' or '\'':
                    quote = c;
                    break;
                case '(':
                    depth++;
                    break;
                case ')':
                    if (--depth == 0)
                    {
                        return i;
                    }

                    break;
            }
        }

        return end;
    }

    /// <summary>
    /// Gets the cascade of an element: the one already matched for an element that matched the
    /// same rules with the same inline style, or a match of its own.
    /// </summary>
    /// <param name="marks">
    /// The marks the engine has placed on the element, packed the way
    /// <see cref="StyleRecord.MarksOf"/> packs them. They are the only input to the match that can
    /// have moved since the last build.
    /// </param>
    internal MatchedCascade Cascade(IElement element, Byte marks)
    {
        // What this element was matched against last time, when nothing about it can have moved.
        // A rule that reaches outside the element is the one thing that rules this out: its answer
        // reads the marks on the ancestors and the siblings, so a mark placed on any of them can
        // move it, and there is no saying which without matching.
        if (_matches.TryGetValue(element, out var held) &&
            held.Marks == marks &&
            !held.ReachesOutside &&
            ReferenceEquals(held.Inline, InlineTextOf(element)))
        {
            _heldMatches++;
            return held.Cascade;
        }

        _freshMatches++;

        var candidates = CollectCandidates(element);
        var inline = InlineOf(element);
        var matched = MatchedCascade.MatchRules(element, _rules, candidates, _origins, _pseudoOnly);
        var key = new CascadeKey(matched, inline?.CssText);

        // The match is what the key is built from, so it must run before the lookup; what the lookup
        // spares is the wrapper around it, not the matching. A hit returns the cascade already held
        // and the freshly matched list is dropped - the wrapper is only built on a miss.
        if (_cascades.TryGetValue(key, out var known))
        {
            return Remember(element, known, marks, candidates);
        }

        var cascade = new MatchedCascade(matched, inline);
        _cascades.Add(key, cascade);
        return Remember(element, cascade, marks, candidates);
    }

    /// <summary>
    /// Records what an element was matched against, so the next build can tell whether the answer in
    /// hand still stands - and answers whether it does.
    /// </summary>
    private MatchedCascade Remember(
        IElement element,
        MatchedCascade cascade,
        Byte marks,
        List<Int32> candidates)
    {
        var reachesOutside = false;

        for (var i = 0; i < candidates.Count; i++)
        {
            if (_reaches[candidates[i]])
            {
                reachesOutside = true;
                break;
            }
        }

        _matches[element] = (cascade, marks, InlineTextOf(element), reachesOutside);
        return cascade;
    }

    /// <summary>
    /// Gets the element's inline style as the text the attribute holds, which is what the declaration
    /// is built from and what AngleSharp rewrites whenever it changes. Null for an element that
    /// cannot carry one, which is the same answer the declaration gives.
    /// </summary>
    private static String? InlineTextOf(IElement element) =>
        element is IHtmlElement || element is ISvgElement ? element.GetAttribute("style") : null;

    /// <summary>
    /// Gets the element's inline style. Only the two element kinds that can carry a style attribute
    /// have one, which is the same test the cascade made before it was cached.
    /// </summary>
    private static ICssStyleDeclaration? InlineOf(IElement element) =>
        element is IHtmlElement || element is ISvgElement ? element.GetStyle() : null;

    /// <summary>
    /// What one match is worth to another: the rules it matched, in the order the cascade applied
    /// them, and the inline style it was matched with.
    /// </summary>
    /// <remarks>
    /// Every answer a cascade gives is decided by those two things, so there is nothing else for a
    /// key to carry - and nothing a selector can read that would not already have changed which
    /// rules matched. The hash is only a bucket: equality is decided by comparing the rules one by
    /// one by identity, so a collision costs a comparison and never a wrong answer.
    /// </remarks>
    private readonly struct CascadeKey : IEquatable<CascadeKey>
    {
        private readonly List<MatchedRule> _rules;
        private readonly String? _inline;

        public CascadeKey(List<MatchedRule> rules, String? inline)
        {
            _rules = rules;
            _inline = inline;
        }

        public Boolean Equals(CascadeKey other)
        {
            if (_rules.Count != other._rules.Count ||
                !String.Equals(_inline, other._inline, StringComparison.Ordinal))
            {
                return false;
            }

            for (var i = 0; i < _rules.Count; i++)
            {
                if (!ReferenceEquals(_rules[i].Rule, other._rules[i].Rule))
                {
                    return false;
                }
            }

            return true;
        }

        public override Boolean Equals(Object? value) => value is CascadeKey other && Equals(other);

        public override Int32 GetHashCode()
        {
            var hash = new HashCode();
            hash.Add(_inline);

            for (var i = 0; i < _rules.Count; i++)
            {
                hash.Add(RuntimeHelpers.GetHashCode(_rules[i].Rule));
            }

            return hash.ToHashCode();
        }
    }

    /// <summary>
    /// Collects the indices of all rules that could match the element, in ascending order
    /// and without duplicates.
    /// </summary>
    public List<Int32> CollectCandidates(IElement element)
    {
        var candidates = new List<Int32>();
        var id = element.Id;

        if (id is { Length: > 0 })
        {
            Append(_idBuckets, id, candidates);
        }

        var classes = element.ClassList;

        if (classes is not null)
        {
            for (var i = 0; i < classes.Length; i++)
            {
                var name = classes[i];

                if (name is not null)
                {
                    Append(_classBuckets, name, candidates);
                }
            }
        }

        var tag = element.LocalName;

        if (tag is not null)
        {
            Append(_tagBuckets, tag, candidates);
        }

        for (var i = 0; i < _unkeyed.Count; i++)
        {
            candidates.Add(_unkeyed[i]);
        }

        if (candidates.Count > 1)
        {
            candidates.Sort();
            Deduplicate(candidates);
        }

        return candidates;
    }

    private static Boolean SheetApplies(ICssStyleSheet sheet, IRenderDevice device)
    {
        // An empty sheet-level media list applies everywhere. A non-empty one needs evaluation
        // against the device, whose entry point (media validation) is internal to AngleSharp.Css:
        // exposing it there is the follow-up. Until then such sheets are included, which can only
        // over-approximate the candidate set - matching itself still decides, and the differential
        // test pins the behaviour on sheets without media.
        _ = device;
        return String.IsNullOrWhiteSpace(sheet.Media.MediaText);
    }

    /// <summary>
    /// Gathers the <c>@counter-style</c> rules of a sheet, including the ones a conditional group holds:
    /// a style defined inside a <c>@media</c> that applies is as much a style of the document as one
    /// written at the top (CSS Lists 3 §2.1), and the chapter lets a later rule replace an earlier one of
    /// the same name, so the order the sheets came in is kept.
    /// </summary>
    private static void CollectCounterStyles(ICssRule rule, List<ICssCounterStyleRule> styles, IRenderDevice device)
    {
        if (rule is ICssCounterStyleRule defined)
        {
            styles.Add(defined);
            return;
        }

        if (rule is not ICssGroupingRule group || !GroupApplies(rule, device))
        {
            return;
        }

        var subRules = group.Rules;

        for (var i = 0; i < subRules.Length; i++)
        {
            CollectCounterStyles(subRules[i], styles, device);
        }
    }

    /// <summary>
    /// Gathers the <c>@page</c> rules of a sheet, including the ones a conditional group holds: a page
    /// declared inside an <c>@media print</c> that this device answers to is as much a page of the
    /// document as one written at the top, while one in a block that does not apply never arrives.
    /// </summary>
    private static void CollectPageRules(ICssRule rule, List<ICssPageRule> pages, IRenderDevice device)
    {
        if (rule is ICssPageRule page)
        {
            pages.Add(page);
            return;
        }

        if (rule is not ICssGroupingRule group || !GroupApplies(rule, device))
        {
            return;
        }

        var subRules = group.Rules;

        for (var i = 0; i < subRules.Length; i++)
        {
            CollectPageRules(subRules[i], pages, device);
        }
    }

    /// <summary>
    /// Gets whether a group's contents stand for this device. A media group is read when its media text
    /// is empty - the same over-approximation <see cref="SheetApplies"/> makes for a whole sheet, since
    /// a medium this window answers to is not asked of the rule here.
    /// </summary>
    private static Boolean GroupApplies(ICssRule rule, IRenderDevice device) => rule switch
    {
        ICssMediaRule media => media.IsValid(device),
        ICssSupportsRule supports => supports.IsValid(device),
        // A group of another kind - a layer, a container - is walked into rather than skipped: the
        // chapter lets a style be defined in any of them, and nothing here decides a container query.
        _ => true,
    };

    private static void Flatten(ICssStyleRule rule, List<ICssStyleRule> flattened, List<RuleOrigin> origins, RuleOrigin origin)
    {
        flattened.Add(rule);
        origins.Add(origin);

        var subRules = rule.Rules;

        for (var i = 0; i < subRules.Length; i++)
        {
            if (subRules[i] is ICssStyleRule subRule)
            {
                Flatten(subRule, flattened, origins, origin);
            }
        }
    }

    private static void Append(Dictionary<String, List<Int32>> buckets, String key, List<Int32> candidates)
    {
        if (buckets.TryGetValue(key, out var indices))
        {
            candidates.AddRange(indices);
        }
    }

    private static void Deduplicate(List<Int32> indices)
    {
        var write = 1;

        for (var read = 1; read < indices.Count; read++)
        {
            if (indices[read] != indices[write - 1])
            {
                indices[write++] = indices[read];
            }
        }

        if (write < indices.Count)
        {
            indices.RemoveRange(write, indices.Count - write);
        }
    }
}

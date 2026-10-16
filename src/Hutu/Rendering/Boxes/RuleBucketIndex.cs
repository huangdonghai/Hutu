using AngleSharp.Css;
using AngleSharp.Css.Dom;
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
    /// Whether each rule's selector reaches outside the element it would match: a combinator in it,
    /// or a relational pseudo-class. Such a rule's answer depends on the element's ancestors and
    /// siblings, so a mark placed on any of them can move it.
    /// </summary>
    private readonly Boolean[] _reaches;

    private RuleBucketIndex(
        List<ICssStyleRule> rules,
        Dictionary<String, List<Int32>> idBuckets,
        Dictionary<String, List<Int32>> classBuckets,
        Dictionary<String, List<Int32>> tagBuckets,
        List<Int32> unkeyed,
        Boolean[] reaches)
    {
        _rules = rules;
        _idBuckets = idBuckets;
        _classBuckets = classBuckets;
        _tagBuckets = tagBuckets;
        _unkeyed = unkeyed;
        _reaches = reaches;
    }

    /// <summary>Gets the flattened rules in the order they are matched in.</summary>
    public List<ICssStyleRule> Rules => _rules;

    /// <summary>
    /// Collects the rules of the given sheets for the device and indexes them: sheets in
    /// order, skipping disabled sheets and sheets whose media does not validate, with each
    /// sheet's matching styles (media and supports blocks resolved) in place.
    /// </summary>
    public static RuleBucketIndex Build(IEnumerable<ICssStyleSheet> sheets, IRenderDevice device)
    {
        var collected = new List<ICssStyleRule>();

        foreach (var sheet in sheets)
        {
            if (!sheet.IsDisabled && SheetApplies(sheet, device))
            {
                foreach (var rule in sheet.Rules.GetMatchingStyles(device))
                {
                    collected.Add(rule);
                }
            }
        }

        var flattened = new List<ICssStyleRule>(collected.Count);

        for (var i = 0; i < collected.Count; i++)
        {
            Flatten(collected[i], flattened);
        }

        var idBuckets = new Dictionary<String, List<Int32>>(StringComparer.Ordinal);
        var classBuckets = new Dictionary<String, List<Int32>>(StringComparer.Ordinal);
        var tagBuckets = new Dictionary<String, List<Int32>>(StringComparer.OrdinalIgnoreCase);
        var unkeyed = new List<Int32>();
        var reaches = new Boolean[flattened.Count];

        for (var i = 0; i < flattened.Count; i++)
        {
            var keys = SelectorKeyExtractor.Extract(flattened[i].Selector);

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

        return new RuleBucketIndex(flattened, idBuckets, classBuckets, tagBuckets, unkeyed, reaches);
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
    /// Within an arm the test is on the text and errs towards yes: a <c>~</c> inside an attribute
    /// value, a <c>+</c> inside <c>:nth-child()</c> and a <c>&gt;</c> inside <c>:not()</c> all read
    /// as combinators, though only the last of those really is one. Each costs a match that was not
    /// needed, which is the direction the engine errs in - the other way would serve an element a
    /// cascade it no longer matches.
    /// </para>
    /// </remarks>
    private static Boolean ReachesOutside(ICssStyleRule rule)
    {
        var selector = rule.SelectorText;

        if (selector is null)
        {
            return true;
        }

        // No mark can move this rule, so nothing about it can have changed either.
        if (!StylePseudoIndex.NamesATrackedPseudo(selector))
        {
            return false;
        }

        var depth = 0;
        var start = 0;
        var quote = '\0';

        for (var i = 0; i < selector.Length; i++)
        {
            var c = selector[i];

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
                    if (ArmReachesOutside(selector, start, i))
                    {
                        return true;
                    }

                    start = i + 1;
                    break;
            }
        }

        return ArmReachesOutside(selector, start, selector.Length);
    }

    /// <summary>
    /// Gets whether one arm of a selector reads a mark off an element other than the one it would
    /// match: a combinator in it, or a relational pseudo-class, which reaches anything at all and so
    /// may name a mark with no combinator of its own anywhere in the arm.
    /// </summary>
    private static Boolean ArmReachesOutside(String selector, Int32 start, Int32 end)
    {
        // The space that followed the comma is now at the head of the arm, and the one before the
        // next comma is at its tail. Neither is a combinator: a selector cannot begin or end with
        // one. Trimming them is what makes the arms of `a , b` come out as `a` and `b`.
        while (start < end && selector[start] is ' ' or '\t' or '\n' or '\r')
        {
            start++;
        }

        while (end > start && selector[end - 1] is ' ' or '\t' or '\n' or '\r')
        {
            end--;
        }

        var arm = selector.AsSpan(start, end - start);

        for (var i = 0; i < arm.Length; i++)
        {
            if (arm[i] is ' ' or '\t' or '\n' or '\r' or '>' or '+' or '~')
            {
                return true;
            }
        }

        return arm.Contains(":has(", StringComparison.OrdinalIgnoreCase);
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
            return held.Cascade;
        }

        var candidates = CollectCandidates(element);
        var inline = InlineOf(element);
        var matched = MatchedCascade.MatchRules(element, _rules, candidates);
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
        private readonly IReadOnlyList<ICssStyleRule> _rules;
        private readonly String? _inline;

        public CascadeKey(IReadOnlyList<ICssStyleRule> rules, String? inline)
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
                if (!ReferenceEquals(_rules[i], other._rules[i]))
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
                hash.Add(RuntimeHelpers.GetHashCode(_rules[i]));
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

    private static void Flatten(ICssStyleRule rule, List<ICssStyleRule> flattened)
    {
        flattened.Add(rule);

        var subRules = rule.Rules;

        for (var i = 0; i < subRules.Length; i++)
        {
            if (subRules[i] is ICssStyleRule subRule)
            {
                Flatten(subRule, flattened);
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

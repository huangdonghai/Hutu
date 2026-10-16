using AngleSharp.Css;
using AngleSharp.Dom;
using System;
using System.Collections.Generic;

namespace Hutu.Rendering.Boxes;

/// <summary>
/// What one build computed for one element: the match its style was resolved from, the scope its
/// <c>var()</c> references were read through, the marks the engine read off it, and the style that
/// came out.
/// </summary>
/// <remarks>
/// These four are the whole of what a style is a function of once the document, the device and the
/// element's own place in the tree are held still - which is why holding all of them by reference,
/// or by the small number the marks pack into, is enough to say whether the answer is still the one
/// in hand.
/// </remarks>
internal readonly record struct StyleRecord(
    MatchedCascade Cascade,
    VariableScope? Variables,
    ComputedStyle Style,
    Byte Marks)
{
    /// <summary>
    /// The four marks the engine answers to off the element itself: the link colour is resolved from
    /// two of them and the rules a reading gesture places from all four, so a style - and a match -
    /// resolved under one of them is not the one the element has now. A mark that was never forced
    /// and one forced off are different answers, so each is read as it stands rather than as a
    /// boolean.
    /// </summary>
    /// <remarks>
    /// Two bits each, four of them, one byte. The packing is arbitrary but fixed: it is compared, not
    /// decoded, so all that matters is that the same element under the same marks packs the same
    /// number every time.
    /// </remarks>
    internal static Byte MarksOf(IElement? element)
    {
        var active = MarkOf(element, PseudoClassNames.Active);
        var visited = MarkOf(element, PseudoClassNames.Visited);
        var hover = MarkOf(element, PseudoClassNames.Hover);
        var target = MarkOf(element, PseudoClassNames.Target);

        return (Byte)(active | (visited << 2) | (hover << 4) | (target << 6));
    }

    private static Byte MarkOf(IElement? element, String name) => element?.GetPseudoClass(name) switch
    {
        true => 1,
        false => 2,
        _ => 0,
    };
}

/// <summary>
/// The style resolution state that outlives one build: the rule index, the two caches the fused
/// builder fills while it walks the document, and what it computed for each element.
/// </summary>
/// <remarks>
/// A build is a function of the document, the device and the marks the interaction state has
/// placed, so nothing here has to be thrown away between two builds that agree on all three - and
/// the marks are the common case: a pointer moving across a link rebuilds the whole document
/// while nothing about the sheets or the viewport has changed.
/// <para>
/// Two things do force a fresh start. A different document brings different rules and different
/// declarations, and an index is built against a device, because a media query is evaluated
/// against the viewport that device describes. Both are settled by <see cref="Reset"/>.
/// </para>
/// <para>
/// What is held is the index together with the cascades it has already matched - the expensive
/// half, since a cascade remembers every property name it was asked for - the two tables that
/// keep a document from resolving the same area twice, the style each element came to, and the
/// head links of the variable scope chain, which have no parent to be filed under and so can be
/// held nowhere else. The intern table holds the strings the declarations handed over, which is
/// why it is dropped with the index rather than kept across documents.
/// </para>
/// </remarks>
internal sealed class StyleResolution
{
    private RuleBucketIndex? _index;
    private StyleIntern _pool = new();
    private FusedAreaCache _cache = new();
    private Boolean _restylesWithoutRelayout;
    private readonly Dictionary<IElement, StyleRecord> _records = new();

    /// <summary>
    /// The head links of the scope chain, by the cascade they were opened for. They are kept here
    /// because a head link has no parent object to be filed under, and because a head rebuilt is a
    /// head whose own children are empty - every link below it is built again with it.
    /// </summary>
    private readonly Dictionary<MatchedCascade, VariableScope> _heads = new();

    /// <summary>Gets the index of the last build, or null when the sheets have to be scanned again.</summary>
    internal RuleBucketIndex? Index => _index;

    /// <summary>Gets the intern table the last build filled.</summary>
    internal StyleIntern Pool => _pool;

    /// <summary>Gets the area cache the last build filled.</summary>
    internal FusedAreaCache Cache => _cache;

    /// <summary>Gets the head links of the scope chain, which have no parent to be filed under.</summary>
    internal Dictionary<MatchedCascade, VariableScope> Heads => _heads;

    /// <summary>Gets the one string this build stands a tag name by, lowering it the first time.</summary>
    internal String Tag(String name) => _cache.Tag(name);

    /// <summary>Gets what the last build computed for the element, or null when it computed nothing.</summary>
    internal StyleRecord? RecordFor(IElement element) =>
        _records.TryGetValue(element, out var record) ? record : null;

    /// <summary>
    /// Remembers what a build computed for one element, so the next one can tell whether it still
    /// stands. Written only on a miss: a hit has just proved the record in hand is the right one.
    /// </summary>
    internal void Remember(IElement element, StyleRecord record) => _records[element] = record;

    /// <summary>
    /// Gets whether every rule that answers to one of the marks a reading gesture places only
    /// changes how a box is painted, never how it is laid out.
    /// </summary>
    /// <remarks>
    /// Such a rule is the one thing that can make a rebuild under an unchanged viewport produce a
    /// different picture, so what it declares is what decides whether the layout may be kept. A
    /// property that is not on the paint-only list closes the gate, and so does anything the list
    /// cannot vouch for: a custom property, which feeds a <c>var()</c> anywhere in the document, and
    /// every wide keyword, which stands for a whole property group.
    /// <para>
    /// Fail-open, the way the pseudo index is: a closed gate costs a relayout that was not needed,
    /// while an open one that should not have been serves a box laid out from a style it no longer
    /// has. The properties on the list are there because nothing in the layout pass reads them and
    /// nothing materialises them onto a layout box - a corner radius, the background clip and the
    /// overflow all reach <c>LayoutEngine.ResolveDecorations</c>, and the positioning and the
    /// transform reach <c>ArrangeCore</c>, which is why none of them is here.
    /// </para>
    /// </remarks>
    internal Boolean RestylesWithoutRelayout => _restylesWithoutRelayout;

    /// <summary>Remembers the state a build produced, so the next one reads the same sheets.</summary>
    internal void Keep(RuleBucketIndex index)
    {
        // The gate is a fact about the rules, so it is answered once per index rather than once per
        // build: a build that read the index the last one built has already had it answered.
        if (!ReferenceEquals(_index, index))
        {
            _restylesWithoutRelayout = GateFor(index);
        }

        _index = index;
    }

    /// <summary>
    /// Drops everything remembered, so the next build scans the sheets and interns from scratch.
    /// </summary>
    internal void Reset()
    {
        _index = null;
        _pool = new StyleIntern();
        _cache = new FusedAreaCache();
        _restylesWithoutRelayout = false;
        _records.Clear();
        _heads.Clear();
    }

    /// <summary>
    /// The properties a rule may declare and still leave the layout alone: everything here is read by
    /// the painter and by nothing else. A property that is not named closes the gate.
    /// </summary>
    private static readonly HashSet<String> PaintOnly = new(StringComparer.OrdinalIgnoreCase)
    {
        "color",
        "background",
        "background-color",
        "background-image",
        "background-position",
        "background-position-x",
        "background-position-y",
        "background-size",
        "background-repeat",
        "background-repeat-x",
        "background-repeat-y",
        "background-attachment",
        "background-origin",
        "border-color",
        "border-top-color",
        "border-right-color",
        "border-bottom-color",
        "border-left-color",
        "outline",
        "outline-color",
        "outline-style",
        "outline-width",
        "outline-offset",
        "text-decoration",
        "text-decoration-line",
        "text-decoration-color",
        "text-decoration-style",
        "cursor",
        "caret-color",
        "-webkit-text-fill-color",
        "-webkit-text-stroke-color",
        "-webkit-text-stroke-width",
    };

    /// <summary>Reads the gate off the rules of an index, which is where the sheets have already been
    /// flattened and their media blocks resolved.</summary>
    private static Boolean GateFor(RuleBucketIndex index)
    {
        foreach (var rule in index.Rules)
        {
            if (!StylePseudoIndex.NamesATrackedPseudo(rule.SelectorText))
            {
                continue;
            }

            foreach (var property in rule.Style)
            {
                if (!PaintOnly.Contains(property.Name))
                {
                    return false;
                }
            }
        }

        return true;
    }

    /// <summary>
    /// Gets whether the index already held still describes the document's sheets for this device:
    /// the same rules, in the same order, so a viewport that moved has moved no media query.
    /// </summary>
    /// <remarks>
    /// The index is what evaluates the media queries, so the honest answer is to build the one the
    /// new viewport asks for and read it against the one in hand. The rules are compared by
    /// identity: a sheet hands back the same rule instances every time it is read, so two indexes
    /// that name the same rules in the same order are the same index - and one that names different
    /// ones is another answer, whatever the count says.
    /// <para>
    /// A false answer costs a build that was not needed; a true one that was wrong would serve
    /// styles the sheets no longer state. The comparison therefore errs towards false, and a
    /// document that has no index yet cannot be answered at all.
    /// </para>
    /// </remarks>
    internal Boolean StillDescribes(IDocument document, HutuRenderDevice device)
    {
        if (_index is null)
        {
            return false;
        }

        var fresh = RuleBucketIndex.Build(BoxBuilder.SheetsOf(document), device);
        var held = _index.Rules;
        var next = fresh.Rules;

        if (held.Count != next.Count)
        {
            return false;
        }

        for (var i = 0; i < held.Count; i++)
        {
            if (!ReferenceEquals(held[i], next[i]))
            {
                return false;
            }
        }

        return true;
    }
}

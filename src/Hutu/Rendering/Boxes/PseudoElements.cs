using AngleSharp.Css;
using AngleSharp.Css.Dom;
using AngleSharp.Css.Parser;
using AngleSharp.Dom;
using Hutu.Rendering.Layout;
using Hutu.Rendering.Media;
using System;
using System.Collections.Generic;
using System.Text;

namespace Hutu.Rendering.Boxes;

/// <summary>
/// Which of the pseudo-elements the index holds arms for a rule is written for.
/// </summary>
internal enum PseudoArm
{
    Before,
    After,
    Marker,
}

/// <summary>
/// The arms of the sheet that are written for <c>::before</c>, <c>::after</c> and <c>::marker</c>, held
/// apart from the rules they came from and matched against the element that originates the pseudo-element.
/// </summary>
/// <remarks>
/// <para>
/// An arm that names a pseudo-element matches no element of the document, which is why the index
/// hands it to nobody: AngleSharp builds such an arm as a selector whose last step is
/// <c>element is IPseudoElement</c>, and this engine's tree holds no such node. What the arm does
/// select is a box the engine has to make itself - one that is, in the words of Pseudo-Elements 4
/// §4.1, "as if [it] were immediate children of their originating element". So what is kept here is
/// not the arm but the arm with the pseudo-element taken off, which is a selector for the element
/// that owns the box.
/// </para>
/// <para>
/// The arms are found by reading the selector's text rather than by walking the parsed selector:
/// the parser will not hand a pseudo-element back as anything this engine can ask, and the text is
/// what the sheet wrote. A rule is still indexed in full by <see cref="RuleBucketIndex"/>, so an arm
/// that is written for an element and one written for a pseudo-element, listed in the same rule,
/// each reach the cascade they belong to.
/// </para>
/// </remarks>
internal sealed class PseudoRuleIndex
{
    private readonly ArmSet _before = new();
    private readonly ArmSet _after = new();
    private readonly ArmSet _marker = new();

    /// <summary>
    /// Gets whether no arm anywhere in the sheets is written for either generated-content pseudo-element.
    /// A page that generates no content is every page this engine has rendered until now, and it must not
    /// pay for the question - so this is the gate the box builder asks first, and it is answered from
    /// the count of arms alone, with nothing parsed.
    /// </summary>
    internal Boolean IsEmpty => _before.Count == 0 && _after.Count == 0;

    /// <summary>Gets whether any arm anywhere in the sheets is written for a list item's marker.</summary>
    internal Boolean HasMarkers => _marker.Count > 0;

    /// <summary>Reads the pseudo-element arms out of the flattened rules, in the order they cascade in.</summary>
    internal static PseudoRuleIndex Build(List<ICssStyleRule> rules)
    {
        var index = new PseudoRuleIndex();

        for (var i = 0; i < rules.Count; i++)
        {
            index.Add(rules[i], i);
        }

        return index;
    }

    private void Add(ICssStyleRule rule, Int32 order)
    {
        var text = rule.SelectorText;

        if (text is null)
        {
            return;
        }

        // The arms of a selector list are read one at a time. `p::before, div` is two arms and only
        // the first of them is written for a pseudo-element; the declarations belong to both.
        var arm = 0;

        for (var i = 0; i <= text.Length; i++)
        {
            if (i == text.Length)
            {
                Take(text, arm, i, order);
                break;
            }

            var c = text[i];

            if (c is '"' or '\'')
            {
                i = SkipString(text, i);
            }
            else if (c is '(' or '[')
            {
                i = SkipGroup(text, i);
            }
            else if (c == ',')
            {
                Take(text, arm, i, order);
                arm = i + 1;
            }
        }
    }

    private void Take(String text, Int32 start, Int32 end, Int32 order)
    {
        if (!EndsWithPseudoElement(text, start, end, out var arm, out var colon))
        {
            return;
        }

        // A marker hangs on a generated box as readily as on an element, and the two are different boxes:
        // `li::before::marker` is the marker of the box the li's `::before` generates, which Pseudo-
        // Elements 4 §4.2 lists as valid, while the plain `::marker` - which Selectors 4 expands to
        // `*::marker` - does not select it, as Lists 3 §3.1.1 repeats. So the generated box's own name is
        // taken off with the marker's, and the arm is kept against the box it belongs to rather than
        // against every marker of the element.
        PseudoArm? origin = null;

        if (arm == PseudoArm.Marker &&
            EndsWithPseudoElement(text, start, colon, out var inner, out var innerColon))
        {
            // A marker has no marker of its own, and an arm that asks for one selects nothing at all.
            if (inner == PseudoArm.Marker)
            {
                return;
            }

            origin = inner;
            colon = innerColon;
        }

        // What is left when the pseudo-elements are taken off selects the originating element. An arm
        // written `::before` alone is written for every element, which is what `*` says.
        var rest = text[Math.Min(colon, start)..colon].Trim();
        ArmsOf(arm).Add(rest.Length == 0 ? "*" : rest, order, origin);
    }

    /// <summary>Gets the arms of one pseudo-element out of the three the index keeps.</summary>
    private ArmSet ArmsOf(PseudoArm arm) => arm switch
    {
        PseudoArm.Before => _before,
        PseudoArm.After => _after,
        _ => _marker,
    };

    /// <summary>
    /// Gets whether an arm is written for one of the pseudo-elements the index keeps, which one it is, and
    /// where the colon that begins it stands.
    /// </summary>
    /// <remarks>
    /// A pseudo-element is the last thing in a selector - Selectors 4 lets nothing follow it - so the
    /// test is on the tail of the arm. Both spellings are taken: §8 of Pseudo-Elements 4 requires the
    /// one-colon form CSS 2 wrote to keep working. The colon has to stand outside every bracket and
    /// quote, or <c>[title="a:before"]</c> would read as a pseudo-element.
    /// </remarks>
    private static Boolean EndsWithPseudoElement(String text, Int32 start, Int32 end, out PseudoArm arm, out Int32 colon)
    {
        arm = PseudoArm.Before;
        colon = -1;

        var at = -1;

        for (var i = start; i < end; i++)
        {
            var c = text[i];

            if (c is '"' or '\'')
            {
                i = SkipString(text, i);
            }
            else if (c is '(' or '[')
            {
                i = SkipGroup(text, i);
            }
            else if (c == ':')
            {
                at = i;
            }
        }

        if (at < 0)
        {
            return false;
        }

        while (at - 1 >= start && text[at - 1] == ':')
        {
            at--;
        }

        var name = text[(at + (text[at] == ':' ? 1 : 0))..end];
        name = name.Trim();

        // `::before` leaves one colon in the name once the first is stepped over; `:before` leaves
        // none. Either way what has to follow is the name and nothing else.
        while (name.Length > 0 && name[0] == ':')
        {
            name = name[1..];
        }

        if (name.Equals("before", StringComparison.OrdinalIgnoreCase))
        {
            arm = PseudoArm.Before;
            colon = at;
            return true;
        }

        if (name.Equals("after", StringComparison.OrdinalIgnoreCase))
        {
            arm = PseudoArm.After;
            colon = at;
            return true;
        }

        // A marker is held the same way although it is not a generated-content box: the arm names a box
        // the engine makes for itself, and no element of the document is what it selects. Lists 3 §3.1
        // calls it the marker box of a list item, and Pseudo-Elements 4 §4.2 gives it the same
        // tree-abiding place as the two above.
        if (name.Equals("marker", StringComparison.OrdinalIgnoreCase))
        {
            arm = PseudoArm.Marker;
            colon = at;
            return true;
        }

        return false;
    }

    private static Int32 SkipString(String text, Int32 at)
    {
        var quote = text[at];

        for (var i = at + 1; i < text.Length; i++)
        {
            if (text[i] == '\\')
            {
                i++;
            }
            else if (text[i] == quote)
            {
                return i;
            }
        }

        return text.Length - 1;
    }

    private static Int32 SkipGroup(String text, Int32 at)
    {
        var depth = 0;
        var quote = '\0';

        for (var i = at; i < text.Length; i++)
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
                if (--depth == 0)
                {
                    return i;
                }
            }
        }

        return text.Length - 1;
    }

    /// <summary>
    /// Gets the cascade the pseudo-element of <paramref name="arm"/> matches on
    /// <paramref name="element"/>, or null when no arm of the sheet is written for it.
    /// </summary>
    /// <param name="origin">
    /// The generated box the marker of <paramref name="arm"/> hangs on, where one is asked for:
    /// <see cref="PseudoArm.Before"/> for the marker of the element's <c>::before</c>, and null for the
    /// marker of the element itself. An arm is kept against one or the other, never both, which is what
    /// keeps <c>*::marker</c> out of the markers generated boxes make.
    /// </param>
    /// <remarks>
    /// The pseudo-element itself is the last step of the arm and was taken off before the arm was
    /// matched, so the specificity the cascade sorts by is the arm's own plus the one a
    /// pseudo-element counts for - which Selectors 4 §17 puts in the same place as a type selector, and
    /// which AngleSharp's own <c>PseudoElementSelector</c> also answers with. A marker of a generated box
    /// is two pseudo-elements, so it is two type selectors.
    /// </remarks>
    internal MatchedCascade? Cascade(
        PseudoArm arm,
        IElement element,
        List<ICssStyleRule> rules,
        RuleOrigin[] origins,
        PseudoArm? origin = null)
    {
        var arms = ArmsOf(arm);

        if (arms.Count == 0)
        {
            return null;
        }

        // Every pseudo-element the arm named counts as a type selector of its own.
        var pseudo = origin is null ? Priority.OneTag : Priority.OneTag + Priority.OneTag;
        var candidates = arms.Candidates(element);
        var matched = new List<MatchedRule>(candidates.Count);
        var priorities = new List<Priority>(candidates.Count);
        var scope = element.Owner?.DocumentElement;

        for (var i = 0; i < candidates.Count; i++)
        {
            var entry = arms.Arms[candidates[i]];

            if (entry.Origin != origin ||
                entry.Selector is null || !entry.Selector.Match(element, scope))
            {
                continue;
            }

            var specificity = entry.Selector.Specificity + pseudo;
            var at = matched.Count;

            while (at > 0 && priorities[at - 1] > specificity)
            {
                at--;
            }

            matched.Insert(at, new MatchedRule(rules[entry.Rule], origins[entry.Rule], specificity, entry.Rule));
            priorities.Insert(at, specificity);
        }

        return matched.Count == 0 ? null : new MatchedCascade(matched, null);
    }

    /// <summary>
    /// One arm, with the selector it was left as, the rule its declarations came from and the generated
    /// box it was written for, where a marker was.
    /// </summary>
    private sealed class Arm
    {
        internal Arm(String text, Int32 rule, PseudoArm? origin)
        {
            Text = text;
            Rule = rule;
            Origin = origin;
        }

        internal String Text { get; }

        internal Int32 Rule { get; }

        /// <summary>The generated box a marker of this arm belongs to, or null for the element itself.</summary>
        internal PseudoArm? Origin { get; }

        /// <summary>The parsed selector, or null when the parser would not take the text.</summary>
        internal ISelector? Selector;
    }

    private sealed class ArmSet
    {
        internal readonly List<Arm> Arms = new();

        private readonly Dictionary<String, List<Int32>> _ids = new(StringComparer.Ordinal);
        private readonly Dictionary<String, List<Int32>> _classes = new(StringComparer.Ordinal);
        private readonly Dictionary<String, List<Int32>> _tags = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<Int32> _unkeyed = new();
        private Boolean _keyed;

        internal Int32 Count => Arms.Count;

        internal void Add(String selectorText, Int32 rule, PseudoArm? origin) =>
            Arms.Add(new Arm(selectorText, rule, origin));

        /// <summary>
        /// Parses the arms and files them by key. Nothing of this happens until an element asks, and
        /// it happens once: a page that generates content is asked for every element it has, and a
        /// page that does not never gets here at all.
        /// </summary>
        private void Key(IElement element)
        {
            if (_keyed)
            {
                return;
            }

            _keyed = true;

            var parser = element.Owner?.Context.GetService<ICssSelectorParser>();

            if (parser is null)
            {
                return;
            }

            for (var i = 0; i < Arms.Count; i++)
            {
                var arm = Arms[i];
                arm.Selector = parser.ParseSelector(arm.Text);

                var keys = arm.Selector is null ? null : SelectorKeyExtractor.Extract(arm.Selector);

                if (keys is null || keys.Length == 0)
                {
                    _unkeyed.Add(i);
                    continue;
                }

                for (var j = 0; j < keys.Length; j++)
                {
                    var key = keys[j];
                    var buckets = key.Kind switch
                    {
                        RuleKeyKind.Id => _ids,
                        RuleKeyKind.Class => _classes,
                        _ => _tags,
                    };

                    if (!buckets.TryGetValue(key.Value, out var indices))
                    {
                        indices = new List<Int32>();
                        buckets.Add(key.Value, indices);
                    }

                    indices.Add(i);
                }
            }
        }

        internal List<Int32> Candidates(IElement element)
        {
            Key(element);

            var candidates = new List<Int32>();
            var id = element.Id;

            if (id is { Length: > 0 })
            {
                Append(_ids, id, candidates);
            }

            var classes = element.ClassList;

            if (classes is not null)
            {
                for (var i = 0; i < classes.Length; i++)
                {
                    var name = classes[i];

                    if (name is not null)
                    {
                        Append(_classes, name, candidates);
                    }
                }
            }

            var tag = element.LocalName;

            if (tag is not null)
            {
                Append(_tags, tag, candidates);
            }

            for (var i = 0; i < _unkeyed.Count; i++)
            {
                candidates.Add(_unkeyed[i]);
            }

            if (candidates.Count > 1)
            {
                candidates.Sort();

                for (var i = candidates.Count - 1; i > 0; i--)
                {
                    if (candidates[i] == candidates[i - 1])
                    {
                        candidates.RemoveAt(i);
                    }
                }
            }

            return candidates;
        }

        private static void Append(Dictionary<String, List<Int32>> buckets, String key, List<Int32> candidates)
        {
            if (buckets.TryGetValue(key, out var indices))
            {
                candidates.AddRange(indices);
            }
        }
    }
}

/// <summary>
/// Makes the boxes <c>::before</c> and <c>::after</c> generate, and puts them where the spec puts
/// them: first and last among the originating element's children.
/// </summary>
internal static class GeneratedContent
{
    /// <summary>
    /// Puts the two generated boxes into <paramref name="box"/>, which has all of its other children
    /// already.
    /// </summary>
    internal static void Place(
        Box box,
        IElement element,
        RuleBucketIndex index,
        ComputedStyle style,
        MatchedCascade cascade,
        VariableScope? variables,
        FusedParent parent,
        HutuRenderDevice device,
        FontFaceTable? faces,
        StyleResolution state,
        IImageProvider? images,
        ICssParser? parser,
        WritingModeInfo flow)
    {
        if (index.Pseudo.IsEmpty)
        {
            return;
        }

        // §4.1: the two are "suppressed when their parent, the originating element, is replaced". A
        // replaced element's contents are the thing it was replaced by, and there is no room in it
        // for content this engine would have to draw.
        if (IsReplaced(element) || box.Kind is BoxKind.LineBreak or BoxKind.HorizontalRule)
        {
            return;
        }

        Put(box, element, index, PseudoArm.Before, style, cascade, variables, parent, device, faces, state, images, parser, flow);
        Put(box, element, index, PseudoArm.After, style, cascade, variables, parent, device, faces, state, images, parser, flow);
    }

    private static void Put(
        Box box,
        IElement element,
        RuleBucketIndex index,
        PseudoArm arm,
        ComputedStyle style,
        MatchedCascade cascade,
        VariableScope? variables,
        FusedParent parent,
        HutuRenderDevice device,
        FontFaceTable? faces,
        StyleResolution state,
        IImageProvider? images,
        ICssParser? parser,
        WritingModeInfo flow)
    {
        var pseudo = index.Pseudo.Cascade(arm, element, index.Rules, index.Origins);

        if (pseudo is null)
        {
            return;
        }

        var items = Items(pseudo, element);

        if (items is null)
        {
            return;
        }

        // §4: a tree-abiding pseudo-element inherits what its originating element inherited, and
        // takes the initial value of everything else - which is what `Next` hands over, since it is
        // the same hand an element's own children are dealt by.
        var pseudoStyle = FusedStyleFactory.Build(
            element,
            pseudo,
            parent.Next(style, pseudo, variables, state.Tag(element.LocalName), false),
            device,
            faces,
            state.Pool,
            state.Cache,
            variables);

        // `content: none`, and the `normal` it computes to when the sheet says nothing, inhibit the
        // pseudo-element "as if it had display: none" (Content 3 §1); `display: none` says the same
        // thing in so many words.
        if (pseudoStyle.IsDisplayNone)
        {
            return;
        }

        // `display` is not inherited, so what the pseudo-element gets is the initial value unless a
        // rule gives it one - and the initial value is `inline`, which is what §4.1 means by "since
        // the initial value of display is inline, this will generate an inline box".
        var declared = pseudo.Find("display")?.Value;
        var kind = String.IsNullOrWhiteSpace(declared) ? BoxKind.Inline : KindOf(declared!.Trim());

        // One image and nothing else replaces the contents rather than joining them: Content 3 §1
        // reads a lone <image> as a <content-replacement>, and §2 makes that "an anonymous inline
        // replaced element" - so the pseudo-element is the replaced box, not a box around one.
        if (items.Count == 1 && items[0].Url is { } only)
        {
            Insert(arm, box, new List<Box> { Replaced(only, element, kind, pseudoStyle, flow, images) });
            return;
        }

        if (kind != BoxKind.Inline)
        {
            var container = new Box(kind, pseudoStyle)
            {
                Element = element,
                Flow = flow,
                IsGenerated = true,
                IsInlineOuter = BoxBuilder.HasInlineOuter(kind, pseudoStyle),
            };

            foreach (var item in items)
            {
                if (item.Text is { Length: > 0 } inside)
                {
                    container.Children.Add(Run(inside, element, pseudoStyle, flow));
                }
            }

            // A generated box shown `list-item` makes a marker of its own, and the arms written for that
            // marker - `li::before::marker` - are read over the box rather than over the element it came
            // from. Those arms name a box, not a node of the tree, so nothing else in the cascade sees them.
            if (index.Pseudo.HasMarkers && kind is BoxKind.Block or BoxKind.InlineBlock)
            {
                ListMarkers.ApplyMarker(
                    container, element, pseudoStyle, pseudo, variables, parent,
                    index, device, faces, state, parser, arm);
            }

            Insert(arm, box, new List<Box> { container });
            return;
        }

        var runs = new List<Box>(items.Count);

        foreach (var item in items)
        {
            if (item.Text is { Length: > 0 } text)
            {
                runs.Add(Run(text, element, pseudoStyle, flow));
            }
        }

        Insert(arm, box, runs);
    }

    private static Box Run(String text, IElement element, ComputedStyle style, WritingModeInfo flow) =>
        new Box(BoxKind.Text, style) { Element = element, Text = text, Flow = flow, IsGenerated = true };

    /// <summary>
    /// Gets the box a <c>content: url(...)</c> draws: an inline replaced element sized by the picture
    /// it names.
    /// </summary>
    private static Box Replaced(
        String url,
        IElement element,
        BoxKind kind,
        ComputedStyle style,
        WritingModeInfo flow,
        IImageProvider? images)
    {
        var resolved = ImageUrlResolver.Resolve(element, url);
        var intrinsic = resolved is null ? null : images?.GetSize(resolved);

        // Nothing decoded to size from, so the specification's default object size answers for the
        // picture - the same standing-in an <img> whose file never arrived gets.
        return new Box(kind, style)
        {
            Element = element,
            IsReplaced = true,
            IsGenerated = true,
            SourceUrl = resolved,
            IntrinsicSize = intrinsic ?? new ImageSize(300, 150),
            Flow = flow,
        };
    }

    /// <summary>
    /// Gets the kind of box a <c>display</c> the pseudo-element declares asks for.
    /// </summary>
    /// <remarks>
    /// Both pseudo-elements are fully styleable (Pseudo-Elements 4 §4.1), so <c>display</c> is read
    /// the way it is read for an element - the same table <see cref="BoxBuilder"/> walks, minus the
    /// kinds only a tag can ask for. The outer role the engine does not model is dropped the same way
    /// it is for an element: <c>inline-flex</c> is a flex container, <c>inline-table</c> a table.
    /// </remarks>
    private static BoxKind KindOf(String display)
    {
        if (display.Equals("inline-block", StringComparison.OrdinalIgnoreCase))
        {
            return BoxKind.InlineBlock;
        }

        if (display.StartsWith("flex", StringComparison.OrdinalIgnoreCase) ||
            display.StartsWith("inline-flex", StringComparison.OrdinalIgnoreCase))
        {
            return BoxKind.Flex;
        }

        if (display.StartsWith("grid", StringComparison.OrdinalIgnoreCase) ||
            display.StartsWith("inline-grid", StringComparison.OrdinalIgnoreCase))
        {
            return BoxKind.Grid;
        }

        if (display.StartsWith("table", StringComparison.OrdinalIgnoreCase) ||
            display.StartsWith("inline-table", StringComparison.OrdinalIgnoreCase))
        {
            return BoxKind.Table;
        }

        return display.StartsWith("inline", StringComparison.OrdinalIgnoreCase) ? BoxKind.Inline : BoxKind.Block;
    }

    private static void Insert(PseudoArm arm, Box host, List<Box> generated)
    {
        if (generated.Count == 0)
        {
            return;
        }

        if (arm == PseudoArm.Before)
        {
            InsertFirst(host, generated);
        }
        else
        {
            InsertLast(host, generated);
        }

        GroupInlineRuns(host);
    }

    /// <summary>
    /// Wraps the inline-level runs of a block container that has come to hold block-level children too.
    /// </summary>
    /// <remarks>
    /// §9.2.1.1 asks for this whenever inline content stands beside a block in a block container, and a
    /// generated box is exactly how content comes to stand there after the fact: the grouping the builder
    /// did over the element's own children declined to wrap an inline run when nothing was beside it, and
    /// the block-level generated box inserted here is beside it now. Left unwrapped, the run is content no
    /// block layout looks at - a block container lays its block-level children out and hands the lines to
    /// nobody - and the element's own words vanish from the page.
    /// </remarks>
    private static void GroupInlineRuns(Box host)
    {
        if (host.Kind is not (BoxKind.Block or BoxKind.InlineBlock))
        {
            return;
        }

        var inline = false;
        var block = false;

        foreach (var child in host.Children)
        {
            // A box that left the flow takes no part in what stands beside what.
            if (PositionedLayout.IsOutOfFlow(child))
            {
                continue;
            }

            if (child.IsInlineLevel)
            {
                inline = true;
            }
            else
            {
                block = true;
            }
        }

        if (!inline || !block)
        {
            return;
        }

        var children = new List<Box>(host.Children);
        host.Children.Clear();
        BoxBuilder.GroupChildren(host, children);
    }

    /// <summary>
    /// Puts generated boxes at the head of the element's children, wrapping them when the element is a
    /// block container whose children are blocks - the same fixup
    /// <see cref="ListMarkers"/> makes for a marker it places inside its item.
    /// </summary>
    /// <remarks>
    /// Only a run of inline generated content is ever wrapped, and only to stand among block-level
    /// siblings: §9.2.1.1 gives a block container that holds a block-level box anonymous block boxes
    /// around the inline runs beside it, and that is the whole of what the wrap is for. Anywhere else
    /// the run is already the right content for its parent. An inline box holds inline-level content
    /// and nothing else (§9.2.1), and a block container whose content is inline-level - including the
    /// empty one a generated box is about to fill - establishes an inline formatting context that the
    /// run simply joins. Wrapping <c>::before</c> for an empty inline host used to put a block-level
    /// box inside an inline one, which cost the host element its own box and with it the glyph.
    /// </remarks>
    private static void InsertFirst(Box host, List<Box> generated)
    {
        // A generated box that is itself block-level sits among the blocks; only a run of inline
        // content has to be wrapped to stand in a block container.
        if (!generated[0].IsInlineLevel || host.Children.Count == 0 || host.Children[0].IsInlineLevel)
        {
            host.Children.InsertRange(0, generated);
            return;
        }

        var line = new Box(BoxKind.Block, host.Style.AsAnonymous()) { IsAnonymous = true, Flow = host.Flow };
        line.Children.AddRange(generated);
        host.Children.Insert(0, line);
    }

    /// <summary>Puts generated boxes at the tail of the element's children, on the same terms as <see cref="InsertFirst"/>.</summary>
    private static void InsertLast(Box host, List<Box> generated)
    {
        var last = host.Children.Count - 1;

        if (!generated[0].IsInlineLevel || last < 0 || host.Children[last].IsInlineLevel)
        {
            host.Children.AddRange(generated);
            return;
        }

        var line = new Box(BoxKind.Block, host.Style.AsAnonymous()) { IsAnonymous = true, Flow = host.Flow };
        line.Children.AddRange(generated);
        host.Children.Add(line);
    }

    /// <summary>One thing a <c>content</c> value asks for: a run of text, or the picture a URL names.</summary>
    private readonly struct Item
    {
        internal Item(String? text, String? url)
        {
            Text = text;
            Url = url;
        }

        internal String? Text { get; }

        internal String? Url { get; }
    }

    /// <summary>
    /// Gets what a pseudo-element's <c>content</c> asks for, or null when it asks for nothing - whether
    /// because the sheet named no content, or because it named content this engine does not draw yet.
    /// </summary>
    /// <remarks>
    /// Content 3 §1 lets <c>content</c> hold strings, images, attribute values, counters and quotation
    /// marks. Strings, images and attributes are drawn - an attribute as the text the element carries, with
    /// the fallback the page gave for the ones that carry nothing. Counters and quotation marks are left out
    /// rather than guessed at, since a <c>counter()</c> read as zero or a quote read as some default would
    /// put words on the page the sheet never wrote. A value that holds something this engine cannot draw is
    /// left out whole, because a half-read <c>content</c> would put up part of what the page asked for.
    /// </remarks>
    private static List<Item>? Items(MatchedCascade pseudo, IElement element)
    {
        var property = pseudo.Find("content");

        return property is null ? null : Read(property.Value, element);
    }

    /// <summary>
    /// Reads a <c>content</c> value item by item, in CSS's own escaping. Null when it holds anything
    /// this engine does not draw.
    /// </summary>
    private static List<Item>? Read(String? value, IElement element)
    {
        if (String.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var items = new List<Item>();
        var text = value!;
        var i = 0;

        while (i < text.Length)
        {
            if (Char.IsWhiteSpace(text[i]))
            {
                i++;
                continue;
            }

            // A slash opens the alternative text, which is what is read aloud instead of what is
            // drawn - it is not part of the content (Content 3 §1.2).
            if (text[i] == '/')
            {
                break;
            }

            if (text[i] is '"' or '\'')
            {
                var run = new StringBuilder();
                ReadRun(text, ref i, run);
                items.Add(new Item(run.ToString(), null));
                continue;
            }

            if (i + 4 < text.Length && text.AsSpan(i, 4).Equals("url(", StringComparison.OrdinalIgnoreCase))
            {
                var close = text.IndexOf(')', i + 4);

                if (close < 0)
                {
                    return null;
                }

                items.Add(new Item(null, text[(i + 4)..close].Trim().Trim('"', '\'')));
                i = close + 1;
                continue;
            }

            if (i + 5 <= text.Length && text.AsSpan(i, 5).Equals("attr(", StringComparison.OrdinalIgnoreCase))
            {
                var close = text.IndexOf(')', i + 5);

                if (close < 0)
                {
                    return null;
                }

                // An attribute is read as the text the element carries it in, and Content 3 §10.5 lets the
                // page state the text standing where an attribute the element has not got would have been.
                // A typed reading is a different feature from drawing its text, so it is left out whole -
                // and a name in quotes is not a name at all.
                var inner = text[(i + 5)..close];
                var comma = inner.IndexOf(',');
                var name = (comma < 0 ? inner : inner[..comma]).Trim();

                if (name.Length == 0 || name[0] is '"' or '\'' || name.Contains('('))
                {
                    return null;
                }

                var fallback = String.Empty;

                if (comma >= 0)
                {
                    var term = inner[(comma + 1)..].Trim();

                    if (term.Length == 0 || term[0] is not ('"' or '\''))
                    {
                        return null;
                    }

                    var run = new StringBuilder();
                    var at = 0;
                    ReadRun(term, ref at, run);
                    fallback = run.ToString();
                }

                items.Add(new Item(element.GetAttribute(name) ?? fallback, null));
                i = close + 1;
                continue;
            }

            return null;
        }

        return items;
    }

    /// <summary>
    /// Reads one quoted string into <paramref name="run"/>, leaving <paramref name="at"/> just past its
    /// closing quote. A marker's <c>content</c> is read with this too - Lists 3 §3.2 has it spelled
    /// "exactly as for <c>::before</c>".
    /// </summary>
    internal static void ReadRun(String text, ref Int32 at, StringBuilder run)
    {
        var quote = text[at++];

        while (at < text.Length && text[at] != quote)
        {
            if (text[at] != '\\')
            {
                run.Append(text[at++]);
                continue;
            }

            at++;

            if (at >= text.Length)
            {
                break;
            }

            // `\A` is the escape CSS 2.1 §12.2 gives a generated line break. Every other escape names
            // a character by its code point, in hex, with one space allowed after it.
            if (text[at] == 'A' && (at + 1 >= text.Length || !Uri.IsHexDigit(text[at + 1])))
            {
                run.Append('\n');
                at++;
                continue;
            }

            var digits = 0;
            var code = 0;

            while (at < text.Length && digits < 6 && Uri.IsHexDigit(text[at]))
            {
                code = code * 16 + Convert.ToInt32(text[at].ToString(), 16);
                digits++;
                at++;
            }

            if (digits == 0)
            {
                run.Append(text[at++]);
                continue;
            }

            run.Append((Char)code);

            if (at < text.Length && text[at] == ' ')
            {
                at++;
            }
        }

        if (at < text.Length)
        {
            at++;
        }
    }

    private static Boolean IsReplaced(IElement element) =>
        element.LocalName is "img" or "input" or "textarea" or "select" or "iframe" or
            "video" or "audio" or "canvas" or "object" or "embed";
}

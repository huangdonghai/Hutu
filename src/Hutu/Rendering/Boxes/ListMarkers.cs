using AngleSharp.Css.Dom;
using AngleSharp.Css.Parser;
using AngleSharp.Dom;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Hutu.Rendering.Boxes;

/// <summary>
/// One thing a marker's own <c>content</c> asks to be drawn: text as the author wrote it, the list's
/// counting spelled in a counter style, or the attribute the originating element carries.
/// </summary>
internal readonly record struct MarkerPiece(String? Text, String? Style, String? Join, String? Attribute);

/// <summary>
/// Which item a box is among its siblings, and the marker it therefore wears.
/// </summary>
/// <remarks>
/// The counting is done after a container has assembled its children, and from the children themselves:
/// an item dropped for <c>display: none</c> is not in the tree at all, and CSS does not count it either,
/// so counting the boxes is the count that agrees with a browser rather than one that has to subtract
/// what was thrown away. It runs in the builder, not the layout, for the same reason the style does -
/// a marker is a property of the document, and the reader's place in it has nothing to do with it.
/// <para>
/// Spelling the count out is <see cref="CounterStyles"/>'s work; what is decided here is the shape the
/// drawing wants around it. A count wears a period, and every marker but a string wears the space that
/// gaps it from its text - a browser leaves about a third of the font there, and does so because the
/// marker's own content carries a space rather than because its box is set further off. A marker written
/// as a string is left exactly as its author wrote it, since such a marker has no automatic gap at all
/// and the room it wants is inside the string.
/// </para>
/// <para>
/// Where the marker goes is the item's <c>list-style-position</c>, and the two answers are made by
/// different machinery: an outside marker is carried on the box and placed by the layout once the lines
/// are down, while an inside marker becomes the item's first inline box here, so that the line breaks
/// around it like any other text.
/// </para>
/// </remarks>
internal static class ListMarkers
{
    /// <summary>
    /// Marks the items of one container, counting them in the order they stand in it.
    /// </summary>
    public static void Mark(Box container)
    {
        // Every child that either is a list item or moves the list-item counter becomes a step, in tree
        // order. A step that is not an item wears no marker but still advances the count - which is how a
        // plain <div> given `counter-increment: list-item` between two items shifts the second's number.
        var steps = new List<Step>();

        foreach (var child in container.Children)
        {
            var isItem = IsItem(child);
            var style = child.Style;
            var anchor = style.ListItemSet ?? style.ListItemReset ?? (isItem ? Value(child) : null);
            var increment = style.ListItemIncrement;

            if (!isItem && anchor is null && increment is null)
            {
                continue;
            }

            steps.Add(new Step(child, isItem, anchor, increment));
        }

        if (steps.Count == 0)
        {
            return;
        }

        var numbers = Number(steps, container);

        foreach (var (step, index) in steps.Select((s, i) => (s, i)))
        {
            if (!step.IsItem)
            {
                continue;
            }

            var child = step.Child;

            // What the marker says is read once the whole tree is counted - see <see cref="Finish"/> - and
            // all that is settled here is which item this one is and where its mark stands.
            child.MarkerPosition = child.Style.ListStylePosition;
            child.ListItemNumber = numbers[index];
        }
    }

    /// <summary>
    /// Gives every item of a finished tree the marker its counting lands on, walking the lists from the
    /// outer in so that an item nested inside another knows the numbers of the items around it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The walk cannot happen while a container is being built: an item's marker may be written
    /// <c>counters(list-item, ".")</c>, which spells the count of every list the item stands in from the
    /// outermost in (Lists 3 §4.7), and the outer list has not counted yet when its nested one is put
    /// together. So <see cref="Mark"/> records the number each item lands on and this reads the texts out
    /// once the whole document has been counted.
    /// </para>
    /// <para>
    /// A marker stated <c>inside</c> its item becomes a box here, which is why the run is a pass over the
    /// tree rather than an answer given while the item is painted: the mark is the item's first inline box,
    /// and the line has to be laid out around it.
    /// </para>
    /// </remarks>
    public static void Finish(Box box, Int32[]? reaching = null)
    {
        foreach (var child in box.Children)
        {
            if (!IsItem(child))
            {
                Finish(child, reaching);
                continue;
            }

            // The chain this item's own marker is measured against: the numbers of the items it stands in,
            // outermost first, with its own at the end.
            var chain = reaching is null
                ? new[] { child.ListItemNumber ?? 1 }
                : [.. reaching, child.ListItemNumber ?? 1];

            SetMarker(child, chain);
            Finish(child, chain);
        }
    }

    /// <summary>Puts one item's marker where its position asks, spelled out as its list and its content do.</summary>
    private static void SetMarker(Box item, Int32[] chain)
    {
        var text = MarkerText(item, chain);

        if (text is not null && item.MarkerPosition == MarkerPosition.Inside)
        {
            PlaceInside(item, text);
            return;
        }

        // Beside an item the marker is no part of its text: the layout is given a place for it once the
        // lines are down, and nothing in the line knows it exists.
        item.Marker = text;
    }

    /// <summary>
    /// Gets what one item's marker is drawn with, or null when it has nothing to draw.
    /// </summary>
    private static String? MarkerText(Box item, Int32[] chain)
    {
        // Lists 3 §3.2 puts the marker's own `content` first: unless it says `normal`, it decides what the
        // marker holds and the item's list style is left aside.
        if (item.MarkerContent is not { } pieces)
        {
            return For(item.Style.ListStyleType, item.Style.ListStyleImage, chain[^1]);
        }

        // `content: none` asks for no marker at all, and so does a content this engine has no way of
        // drawing - a picture, or a counter the page keeps under a name the list does not count. The whole
        // value is left out rather than half-read, which is how a generated content list is dealt with too.
        if (pieces.Count == 0)
        {
            return null;
        }

        var text = new StringBuilder();

        foreach (var piece in pieces)
        {
            if (piece.Text is { } written)
            {
                text.Append(written);
                continue;
            }

            if (piece.Attribute is { } name)
            {
                text.Append(item.Element?.GetAttribute(name));
                continue;
            }

            var style = piece.Style ?? "decimal";

            if (piece.Join is { } join)
            {
                for (var i = 0; i < chain.Length; i++)
                {
                    if (i > 0)
                    {
                        text.Append(join);
                    }

                    text.Append(CounterStyles.Spell(style, chain[i]));
                }

                continue;
            }

            // The innermost count is what a lone counter() spells, and the item's own number is the inner
            // one of its list.
            text.Append(CounterStyles.Spell(style, chain[^1]));
        }

        return text.ToString();
    }

    /// <summary>
    /// Resolves what one item's marker is drawn with - the style the arms written for it came to, and the
    /// contents its own <c>content</c> asks for - and puts both on the box.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A marker is a box of its own, and Pseudo-Elements 4 §4.2 gives it the same tree-abiding place as
    /// <c>::before</c>: what the item came to is what the marker inherits, and the arms written
    /// <c>li::marker</c> are read over that. Those arms reach the marker and nowhere else, because an arm
    /// that names a pseudo-element matches no element of the document - so the item's own style, resolved
    /// from the rules that do match it, has no notice of them.
    /// </para>
    /// <para>
    /// The parent the style is read against is the channel the item hands its own children rather than the
    /// marker's declarations, and that is measured rather than assumed: Chrome re-resolves a numeric
    /// <c>line-height</c> at the marker's size, so an item at 16px with <c>line-height: 3</c> and a marker
    /// at 20px draws its marker in a row 60px tall. A declaration of the marker's own still wins, since
    /// the marker's cascade is what every resolver reads.
    /// </para>
    /// </remarks>
    public static void ApplyMarker(
        Box item,
        IElement element,
        ComputedStyle style,
        MatchedCascade itemCascade,
        VariableScope? itemVariables,
        FusedParent parent,
        RuleBucketIndex index,
        HutuRenderDevice device,
        FontFaceTable? faces,
        StyleResolution state,
        ICssParser? parser)
    {
        if (!style.Display.Equals("list-item", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var marker = index.Pseudo.Cascade(PseudoArm.Marker, element, index.Rules, index.Origins);

        if (marker is null)
        {
            return;
        }

        // A custom property written on the marker is the marker's own, so the scope it is read through is
        // opened from the marker's arms - with the item's scope for a parent, which is what hands a
        // property the marker inherited from above down the values it was given higher up.
        var variables = VariableScope.Open(marker, itemVariables, parser, state.Heads);

        item.MarkerStyle = FusedStyleFactory.Build(
            element,
            marker,
            parent.Next(style, itemCascade, itemVariables, state.Tag(element.LocalName), false),
            device,
            faces,
            state.Pool,
            state.Cache,
            variables,
            AppliesToMarker);

        item.MarkerContent = MarkerPieces(
            variables is null ? marker.Find("content") : variables.ReadProperty(marker, "content"));
    }

    /// <summary>
    /// Reads the pieces one marker's <c>content</c> asks to be drawn, or null where the contents are left
    /// to the item's own list style - which is what <c>normal</c> says, and what an arm that never
    /// mentions content says. An empty list is a marker asked to hold nothing.
    /// </summary>
    /// <remarks>
    /// Lists 3 §3.2 has the marker's contents read "exactly as for <c>::before</c>", so a string is the
    /// text and a counter is the list's counting spelled in the style the page named. Only the
    /// <c>list-item</c> counter is spelled: it is the one a list keeps, and a value that names a counter
    /// of the page's own - or a picture, or an attribute - is left out whole rather than guessed at, since
    /// a counter read as zero or an attribute read as its own name would put words on the page the sheet
    /// never wrote.
    /// </remarks>
    private static List<MarkerPiece>? MarkerPieces(ICssProperty? declared)
    {
        var value = declared?.Value;

        if (declared is null || String.IsNullOrWhiteSpace(value) || ComputedStyle.IsInherit(declared))
        {
            return null;
        }

        var text = value!.Trim();

        if (text.Equals("normal", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (text.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            return [];
        }

        var pieces = new List<MarkerPiece>();
        var at = 0;

        while (at < text.Length)
        {
            if (Char.IsWhiteSpace(text[at]))
            {
                at++;
                continue;
            }

            // A slash opens the alternative text, which is spoken rather than drawn (Content 3 §1.2).
            if (text[at] == '/')
            {
                break;
            }

            if (text[at] is '"' or '\'')
            {
                var run = new StringBuilder();
                GeneratedContent.ReadRun(text, ref at, run);
                pieces.Add(new MarkerPiece(run.ToString(), null, null, null));
                continue;
            }

            if (ReadCounter(text, ref at, out var piece))
            {
                if (piece is null)
                {
                    return [];
                }

                pieces.Add(piece.Value);
                continue;
            }

            if (ReadAttribute(text, ref at, out var term))
            {
                if (term is null)
                {
                    return [];
                }

                pieces.Add(term.Value);
                continue;
            }

            // Anything else - a picture, a quotation mark, a value this grammar has no way of spelling -
            // is content this engine does not draw, and the value that holds it is left out whole.
            return [];
        }

        return pieces;
    }

    /// <summary>
    /// Reads one <c>counter()</c> or <c>counters()</c> out of the marker's content, leaving
    /// <paramref name="at"/> past its closing parenthesis. False when the text there is no such call, and a
    /// null piece when it is one this engine cannot spell.
    /// </summary>
    /// <remarks>
    /// Only the list's own counting is spelled: it is the counter a list keeps, and a name the page declared
    /// for itself is a counter this engine has no register of. The style a call names defaults to decimal,
    /// which is what the page's list style has nothing to do with - a marker that says
    /// <c>counter(list-item)</c> spells digits even on a list of letters, measured against Chrome.
    /// <para>
    /// The call is read out of what the parser hands back rather than out of the text the page wrote, and
    /// the two do not agree on a separator: <c>counters(list-item, ".")</c> comes back as
    /// <c>counters(list-item .)</c>, its quotes resolved and the comma between its arguments gone, and a
    /// style is left out altogether when it is the decimal one nothing says. Both spellings are taken -
    /// a <c>var()</c> that was substituted reaches this reader as the words the page wrote.
    /// </para>
    /// </remarks>
    private static Boolean ReadCounter(String text, ref Int32 at, out MarkerPiece? piece)
    {
        piece = null;

        var end = Identifier(text, at);
        var function = text[at..end];
        var counts = function.Equals("counters", StringComparison.OrdinalIgnoreCase);

        if (!counts && !function.Equals("counter", StringComparison.OrdinalIgnoreCase) ||
            end >= text.Length || text[end] != '(')
        {
            return false;
        }

        var terms = Terms(text, end + 1, out var after);
        at = after;

        if (terms is null)
        {
            return true;
        }

        var head = terms[0].TrimStart();
        var space = head.IndexOf(' ');
        var counter = space < 0 ? head : head[..space];
        var joined = space < 0 ? null : head[(space + 1)..];

        // An unbalanced call, or one whose counter is not the list's own counting, is content this engine
        // has nothing to spell; the piece stays null and the caller leaves the value out whole.
        if (!counter.Equals("list-item", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        String? style = null;

        if (terms.Count > 1)
        {
            if (joined is not null)
            {
                // The serializer hangs a style on the end of the separators it was given.
                style = Style(terms[1]);
            }
            else if (counts)
            {
                joined = Joined(terms[1]);
                style = terms.Count > 2 ? Style(terms[2]) : null;
            }
            else
            {
                style = Style(terms[1]);
            }
        }

        if (counts && joined is null)
        {
            return true;
        }

        piece = new MarkerPiece(null, style, joined, null);
        return true;
    }

    /// <summary>Gets how far an identifier reaches from <paramref name="at"/>.</summary>
    private static Int32 Identifier(String text, Int32 at)
    {
        var end = at;

        while (end < text.Length && (Char.IsLetterOrDigit(text[end]) || text[end] is '-' or '_'))
        {
            end++;
        }

        return end;
    }

    /// <summary>
    /// Reads one <c>attr()</c> out of the marker's content, leaving <paramref name="at"/> past its closing
    /// parenthesis. False when the text there is no such call, and a null term when it is one this engine
    /// cannot spell.
    /// </summary>
    /// <remarks>
    /// Content 3 §1 lets an attribute be read as the text it holds, and CSS Values 4 lets the page say what
    /// to hold for an element that carries no such attribute - a fallback. AngleSharp's grammar for content
    /// takes only the name, so a call that states a fallback never reaches this reader at all; the name is
    /// read here as it comes, and a typed reading (<c>attr(x, integer)</c>) is left out whole, since
    /// turning an attribute into a number or a colour is a different feature from drawing its text.
    /// </summary>
    private static Boolean ReadAttribute(String text, ref Int32 at, out MarkerPiece? term)
    {
        term = null;

        var end = Identifier(text, at);

        if (!text[at..end].Equals("attr", StringComparison.OrdinalIgnoreCase) ||
            end >= text.Length || text[end] != '(')
        {
            return false;
        }

        var terms = Terms(text, end + 1, out var after);
        at = after;

        var name = terms is { Count: 1 } ? terms[0].Trim() : null;

        if (name is not { Length: > 0 } || name[0] is '"' or '\'')
        {
            return true;
        }

        term = new MarkerPiece(null, null, null, name);
        return true;
    }

    /// <summary>
    /// Gets the arguments of a function call, split at the commas that stand outside its quotes, and
    /// <paramref name="after"/> the index past its closing parenthesis. Null when none closes.
    /// </summary>
    private static List<String>? Terms(String text, Int32 start, out Int32 after)
    {
        var terms = new List<String>();
        var from = start;
        var quote = '\0';

        for (var i = start; i < text.Length; i++)
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
            else if (c == ',')
            {
                terms.Add(text[from..i]);
                from = i + 1;
            }
            else if (c == ')')
            {
                terms.Add(text[from..i]);
                after = i + 1;
                return terms.Count == 0 ? null : terms;
            }
        }

        after = text.Length;
        return null;
    }

    /// <summary>
    /// Gets the counter style an argument names, or null when it is not a style this engine can read - a
    /// bare name, since the quoted spelling CSS 2.1 allowed is not the one counter styles take.
    /// </summary>
    private static String? Style(String term)
    {
        var name = term.Trim();

        return name.Length > 0 && name[0] is not ('"' or '\'') &&
            !name.Equals("none", StringComparison.OrdinalIgnoreCase)
            ? name
            : null;
    }

    /// <summary>
    /// Gets the text a <c>counters()</c> joins its values with, read in CSS's own escaping, or null when
    /// the argument is not the string the function asks for.
    /// </summary>
    private static String? Joined(String term)
    {
        var text = term.Trim();

        if (text.Length == 0 || text[0] is not ('"' or '\''))
        {
            return null;
        }

        var run = new StringBuilder();
        var at = 0;
        GeneratedContent.ReadRun(text, ref at, run);

        return run.ToString();
    }

    /// <summary>
    /// Gets whether a property written on <c>::marker</c> has any effect on it.
    /// </summary>
    /// <remarks>
    /// Lists 3 §3.1.1 lets only a handful of properties apply to the marker box, and allows the rest to be
    /// read either as not applying or as enforced from the originating element. Chrome enforces them:
    /// measured against it, a decoration, a background, a visibility, a text fill or stroke, an opacity or
    /// a <c>display</c> written on the marker comes back as the value the item came to - or as the initial
    /// one where the property is not inherited - while the colour, the whole font, its spacing, its
    /// line-height and <c>text-decoration-skip-ink</c> all take the declaration. What is below is the
    /// first of those two groups, in the names this engine reads. A marker's <c>display</c> is in it
    /// because the answer is a box this engine places, not one the page lays out.
    /// </remarks>
    private static Boolean AppliesToMarker(String name) => !_markerIgnores.Contains(name);

    private static readonly HashSet<String> _markerIgnores = new(StringComparer.OrdinalIgnoreCase)
    {
        "display",
        "visibility",
        "opacity",
        "text-rendering",
        "text-decoration",
        "text-decoration-line",
        "text-decoration-style",
        "text-decoration-color",
        "text-decoration-thickness",
        "-webkit-text-fill-color",
        "-webkit-text-stroke",
        "-webkit-text-stroke-width",
        "-webkit-text-stroke-color",
        "background",
        "background-color",
        "background-image",
        "background-repeat",
        "background-position",
        "background-size",
        "background-origin",
        "background-clip",
        "background-blend-mode",
        "-webkit-background-clip",
    };
    /// <summary>
    /// Works out the number each step lands on, walking the list the way CSS counts its <c>list-item</c>
    /// counter. A step that gives the counter an absolute value (an item's <c>value</c>, or a
    /// <c>counter-set</c>/<c>counter-reset</c> on any child) anchors it; every other step moves the
    /// running count by its own <c>counter-increment</c>, or by the list's step when it is an item that
    /// says nothing. A reversed list steps down, and with no anchor its starting value is settled at
    /// layout time so the last item lands on 1 - which is how an item, or a plain element, that steps by
    /// more than one shifts every number after it. The items before the first anchor count back down from
    /// it, so a value stated mid-list lifts the ones above it.
    /// </summary>
    private static Int32[] Number(List<Step> steps, Box container)
    {
        var count = steps.Count;
        var reversed = container.Element?.HasAttribute("reversed") == true;
        var step = reversed ? -1 : 1;
        var items = steps.Count(s => s.IsItem);
        var start = Start(container, items, reversed);

        var value = new Int32[count];
        var firstAnchor = -1;

        for (var i = 0; i < count; i++)
        {
            if (steps[i].Anchor is not null)
            {
                firstAnchor = i;
                break;
            }
        }

        // How far a step moves the count when it is not an anchor: its own increment, else the list's step
        // for an item (a non-item step only exists because it named an increment or an anchor).
        Int32 Eff(Int32 i) => steps[i].Increment ?? (steps[i].IsItem ? step : 0);

        if (firstAnchor < 0)
        {
            // No absolute anchor. The count runs from the list's start, but a reversed list with no start
            // stated settles its start at layout time so the last item reaches 1 - which is what lets an
            // item, or a plain element, that steps by more than one shift every number after it. A list
            // that did state a start (either direction), or a forward one, begins at that start; a
            // forward list's first item is the start itself, so the run begins one step behind it.
            var cum = new Int32[count];
            var scan = 0;

            for (var i = 0; i < count; i++)
            {
                scan = Advance(scan, Eff(i));
                cum[i] = scan;
            }

            var hasStart = Int32.TryParse(container.Element?.GetAttribute("start"),
                NumberStyles.Integer, CultureInfo.InvariantCulture, out var stated);
            var startValue = hasStart ? stated : 1;
            var baseValue = reversed && !hasStart
                ? 1 - LastItem(cum, steps, count)
                : reversed ? startValue - cum[FirstItem(steps)]
                : startValue - 1;

            var running = baseValue;

            for (var i = 0; i < count; i++)
            {
                running = Advance(running, Eff(i));
                value[i] = running;
            }

            return value;
        }

        // Forward from the first anchor: it sets the count, and each later step either re-anchors or moves
        // the running count along.
        var current = steps[firstAnchor].Anchor!.Value;
        value[firstAnchor] = current;

        for (var i = firstAnchor + 1; i < count; i++)
        {
            current = steps[i].Anchor is { } a ? a : Advance(current, Eff(i));
            value[i] = current;
        }

        // The steps above the first anchor. A reversed list counts them back down toward the anchor, so a
        // value stated mid-list lifts the ones above it; a forward list runs them from its own start.
        if (reversed)
        {
            for (var i = firstAnchor - 1; i >= 0; i--)
            {
                value[i] = value[i + 1] - (steps[i + 1].IsItem || steps[i + 1].Increment is not null ? Eff(i + 1) : step);
            }
        }
        else
        {
            var forward = start - step;

            for (var i = 0; i < firstAnchor; i++)
            {
                forward = Advance(forward, Eff(i));
                value[i] = forward;
            }
        }

        return value;
    }

    private static Int32 FirstItem(List<Step> steps)
    {
        for (var i = 0; i < steps.Count; i++)
        {
            if (steps[i].IsItem)
            {
                return i;
            }
        }

        return 0;
    }

    private static Int32 LastItem(Int32[] cum, List<Step> steps, Int32 count)
    {
        var last = 0;

        for (var i = 0; i < count; i++)
        {
            if (steps[i].IsItem)
            {
                last = cum[i];
            }
        }

        return last;
    }

    /// <summary>One child that takes part in the list's counting: an item, or a step that moves it.</summary>
    private readonly record struct Step(Box Child, Boolean IsItem, Int32? Anchor, Int32? Increment);

    /// <summary>
    /// Moves a counter by one step without wrapping over the edge of what it holds: a count that reaches
    /// the last number the range carries stays there, which is what a browser draws for items past it.
    /// </summary>
    private static Int32 Advance(Int32 from, Int32 by)
    {
        if (by > 0 && from > Int32.MaxValue - by)
        {
            return Int32.MaxValue;
        }

        if (by < 0 && from < Int32.MinValue - by)
        {
            return Int32.MinValue;
        }

        return from + by;
    }

    /// <summary>
    /// Puts a marker stated <c>inside</c> its item at the head of that item's content, where it is laid
    /// out with the text and takes room on the line: an inside marker is the first inline box of the
    /// principal box, which is a different thing from the marker that hangs outside it.
    /// </summary>
    private static void PlaceInside(Box item, String text)
    {
        // The marker is a box of its own, so it wears the style the sheet wrote for it rather than the one
        // its item came to - and where the sheets say nothing about markers, the two are the one style.
        var marker = new Box(
            BoxKind.Text, item.MarkerStyle ?? item.Style) { Text = text, Flow = item.Flow, IsMarker = true };

        if (item.Children.Count > 0 && item.Children[0].IsInlineLevel)
        {
            // The item opens with inline content, so the marker joins that content and the two share the
            // first line: the mark, then its text beside it.
            item.Children.Insert(0, marker);

            return;
        }

        // The item opens with a block, or has nothing a line could hold. The marker is still the first
        // inline box in it, and the first thing in a block whose rest is blocks is a line of its own -
        // which in this tree is an anonymous block, the same shape the builder wraps inline runs in.
        var line = new Box(BoxKind.Block, item.Style.AsAnonymous()) { IsAnonymous = true, Flow = item.Flow };
        line.Children.Add(marker);
        item.Children.Insert(0, line);
    }

    /// <summary>
    /// Gets the marker one item is drawn with, or null when the style has no marker to draw.
    /// </summary>
    public static String? For(String type, String? image, Int32 number)
    {
        // An image marker needs the image loader, which the style has no part of; nothing is drawn for
        // one until the painting can ask for it.
        if (image is { Length: > 0 } || CounterStyles.IsNone(type))
        {
            return null;
        }

        var spelled = CounterStyles.Spell(type, number);

        if (spelled is null)
        {
            return null;
        }

        // A string marker is left as its author wrote it. The room after such a marker belongs to the
        // string - and a period would be writing on their text rather than marking the list.
        if (CounterStyles.IsString(type))
        {
            return spelled;
        }

        return CounterStyles.IsCounted(type) ? spelled + ". " : spelled + " ";
    }

    /// <summary>
    /// Gets the number the first item takes: the <c>start</c> the list was given, or one - except a
    /// reversed list that never said where to start, which counts down from how many items it holds.
    /// </summary>
    private static Int32 Start(Box container, Int32 items, Boolean reversed)
    {
        if (container.Element?.GetAttribute("start") is { Length: > 0 } stated &&
            Int32.TryParse(stated, NumberStyles.Integer, CultureInfo.InvariantCulture, out var asked))
        {
            return asked;
        }

        return reversed ? items : 1;
    }

    /// <summary>Gets the number an item was told to take, which restarts the count from there.</summary>
    private static Int32? Value(Box item) =>
        item.Element?.GetAttribute("value") is { Length: > 0 } stated &&
        Int32.TryParse(stated, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number)
            ? number
            : null;

    /// <summary>
    /// Gets whether the box is an item of the list it stands in: an element the cascade made
    /// <c>display: list-item</c>. An anonymous box is never one, however it borrowed its parent's
    /// style - and the wrapper that gathers a list item's own inline content does borrow it.
    /// </summary>
    private static Boolean IsItem(Box box) =>
        box.Element is not null &&
        box.Kind is BoxKind.Block or BoxKind.InlineBlock &&
        box.Style.Display.Equals("list-item", StringComparison.OrdinalIgnoreCase);
}

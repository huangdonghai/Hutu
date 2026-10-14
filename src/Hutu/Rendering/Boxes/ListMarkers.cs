using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Hutu.Rendering.Boxes;

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
            var text = For(child.Style.ListStyleType, child.Style.ListStyleImage, numbers[index]);

            child.MarkerPosition = child.Style.ListStylePosition;

            if (text is not null && child.Style.ListStylePosition == MarkerPosition.Inside)
            {
                PlaceInside(child, text);
            }
            else
            {
                // Beside an item the marker is no part of its text: the layout is given a place for it
                // once the lines are down, and nothing in the line knows it exists.
                child.Marker = text;
            }
        }
    }

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
        var marker = new Box(BoxKind.Text, item.Style) { Text = text, Flow = item.Flow, IsMarker = true };

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

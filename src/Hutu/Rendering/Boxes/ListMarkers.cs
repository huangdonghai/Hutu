using System;
using System.Globalization;

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
        var reversed = container.Element?.HasAttribute("reversed") == true;
        var running = Start(container, Count(container), reversed);

        foreach (var child in container.Children)
        {
            if (!IsItem(child))
            {
                continue;
            }

            var number = Value(child) ?? running;
            var text = For(child.Style.ListStyleType, child.Style.ListStyleImage, number);

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

            running = reversed ? number - 1 : number + 1;
        }
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
        var line = new Box(BoxKind.Block, item.Style) { IsAnonymous = true, Flow = item.Flow };
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

    /// <summary>Gets how many items the container has, which is where a reversed list counts from.</summary>
    private static Int32 Count(Box container)
    {
        var items = 0;

        foreach (var child in container.Children)
        {
            if (IsItem(child))
            {
                items++;
            }
        }

        return items;
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

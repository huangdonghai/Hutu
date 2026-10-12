using AngleSharp.Css.Dom;
using AngleSharp.Dom;
using Hutu.Rendering.Boxes;
using Hutu.Rendering.Layout;
using System;
using System.Collections.Generic;

namespace Hutu.Rendering.Interaction;

/// <summary>
/// Finds what a point in document coordinates lands on: the deepest box that contains it, and the
/// elements that box belongs to.
/// </summary>
/// <remarks>
/// A selection asks which character a point is on; a pointer asks the broader question of which
/// element it is over, because that is what <c>:hover</c>, <c>cursor</c>, <c>title</c> and a link
/// click are all about. Three rules keep the answer one a reader would agree with:
/// <list type="bullet">
/// <item>A subtree pinned to the viewport is left out, exactly as the selector leaves it out: its
/// geometry is viewport-relative, so a point in document coordinates cannot be mapped onto it.</item>
/// <item>A box that clips hides whatever its descendants put outside it, so a point beyond the clip
/// belongs to whatever lies underneath instead.</item>
/// <item>A box only answers for itself if the point is inside it, which is why an element with no
/// extent of its own - an empty <c>div</c> - cannot be pointed at.</item>
/// </list>
/// The last box that contains the point wins, because document order is the closest thing the tree
/// has to a stacking order: a descendant is seen after its ancestor and covers it, and a box placed
/// over another is seen later still.
/// <para>
/// The elements are then read from the document rather than from the boxes that were walked. A run of
/// text stands for no element of its own, so a link around a word exists only as the text: the
/// document knows which element that text is in, and knows the elements it is in in turn. An inline
/// element is boxed around the runs it holds, so pointing at its padding resolves to the element
/// itself rather than to the word it wraps.
/// </para>
/// </remarks>
public sealed class ElementHitTester
{
    private readonly LayoutResult _layout;

    private Double _x;
    private Double _y;
    private LayoutBox? _box;

    public ElementHitTester(LayoutResult layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        _layout = layout;
    }

    /// <summary>
    /// Gets what a point in document coordinates lands on.
    /// </summary>
    /// <remarks>
    /// The point is already in document coordinates, which is the host's business: it holds the
    /// scroll offset, and a host that scrolls adds it before asking.
    /// </remarks>
    public HitResult Hit(Double x, Double y)
    {
        _x = x;
        _y = y;
        _box = null;

        Visit(_layout.Root);

        return _box is null ? HitResult.None : new HitResult(_box, Chain(_box));
    }

    /// <summary>
    /// Gets the element a box came from and the elements it sits in, nearest first - the chain a state
    /// is set on, a hint is looked up along and a link is searched up.
    /// </summary>
    private static IReadOnlyList<IElement> Chain(LayoutBox box)
    {
        var chain = new List<IElement>();

        for (var element = Element(box.Source); element is not null; element = element.ParentElement)
        {
            chain.Add(element);
        }

        return chain;
    }

    /// <summary>
    /// Gets the element nearest a box: the one the box stands for, or - for a run of text, which
    /// stands for nothing itself - the element holding that text.
    /// </summary>
    private static IElement? Element(Box box) => box.Element ?? box.Source?.ParentElement;

    /// <summary>
    /// Walks a box and its descendants, remembering the last one that contains the point.
    /// </summary>
    private void Visit(LayoutBox box)
    {
        if (box.Position is PositionMode.Fixed or PositionMode.Sticky)
        {
            return;
        }

        // A box that is not seen draws nothing, and a reader cannot be pointing at what is not there -
        // the same rule the painter follows, and for the same reason that what is inside it may still be
        // seen, so the walk goes on. A line box borrows the style of the text it carries and so does
        // not answer for an element of its own.
        if (box.Kind != LayoutKind.Line && box.Source.Style.IsHidden)
        {
            foreach (var child in box.Children)
            {
                Visit(child);
            }

            return;
        }

        if (box.ClipsOverflow && !Contains(box.PaddingBox, _x, _y))
        {
            return;
        }

        if (Contains(box.BorderBox, _x, _y))
        {
            _box = box;
        }

        foreach (var child in box.Children)
        {
            Visit(child);
        }
    }

    /// <summary>Gets whether a point is inside a rectangle, with the bottom and right edges outside.</summary>
    private static Boolean Contains(Rect bounds, Double x, Double y) =>
        x >= bounds.X && x < bounds.Right && y >= bounds.Y && y < bounds.Bottom;
}

using AngleSharp.Dom;
using System;

namespace Hutu.Rendering.Layout;

/// <summary>Where a rectangle ends up in the viewport when the document is scrolled to it.</summary>
public enum ScrollAlignment
{
    /// <summary>Move the least amount that brings the whole rectangle into view, which leaves a
    /// rectangle that is already visible exactly where it is.</summary>
    Nearest,

    /// <summary>Align the top edge with the top of the viewport, the way a fragment link does.</summary>
    Start,

    /// <summary>Centre the rectangle in the viewport.</summary>
    Center,

    /// <summary>Align the bottom edge with the bottom of the viewport.</summary>
    End,
}

/// <summary>The outcome of a layout pass: the tree plus the size the document occupies.</summary>
/// <remarks>
/// The size is what the viewport scrolls over, which is the whole of what the document occupies -
/// more than the root's box wherever a box reaches past it. The document occupies exactly
/// <c>0, 0</c> to <c>Width, Height</c>: the offsets a scroll is measured in start where the document
/// does on the outside, whichever corner the page itself begins reading at.
/// </remarks>
public sealed class LayoutResult
{
    public LayoutResult(LayoutBox root, Double width, Double height)
    {
        Root = root;
        Width = width;
        Height = height;
    }

    public LayoutBox Root { get; }

    /// <summary>Gets the width of the laid-out document, i.e. the scrollable content width.</summary>
    public Double Width { get; }

    /// <summary>Gets the height of the laid-out document, i.e. the scrollable content height.</summary>
    public Double Height { get; }

    /// <summary>
    /// Gets whether the document begins at its right edge: the columns of a vertical page march
    /// leftward from there, and a right-to-left page reads that way along its lines.
    /// </summary>
    /// <remarks>
    /// A host that opens a document at the origin opens at the end of one of these rather than at its
    /// beginning. Showing the beginning means scrolling to the far end of the range, which is the
    /// document's width less the viewport's, and no rectangle of the layout says so on its own:
    /// bringing one into view is a question about an element, and this one is about the page.
    /// </remarks>
    public Boolean StartsAtRight => Root.Flow.BlockSign < 0 || Root.Flow.IsRightToLeft;

    /// <summary>
    /// Gets the first box an element generated, in document order. An element whose content spans
    /// several lines generates several boxes; the first is the one its top edge belongs to.
    /// </summary>
    public LayoutBox? FindBox(IElement element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return FindBox(Root, element);
    }

    /// <summary>
    /// Gets the vertical offset that brings a rectangle into view, clamped to the range the document
    /// can actually scroll through, so the result can be assigned without checking the bounds first.
    /// </summary>
    /// <param name="target">The rectangle to bring into view, in document coordinates.</param>
    /// <param name="viewportHeight">The height of the viewport it is viewed through.</param>
    /// <param name="alignment">Where the rectangle should end up in the viewport.</param>
    /// <param name="currentOffset">The offset the viewport is at now, which is what lets
    /// <see cref="ScrollAlignment.Nearest"/> leave an already visible rectangle alone.</param>
    public Double VerticalScrollOffsetFor(
        Rect target,
        Double viewportHeight,
        ScrollAlignment alignment,
        Double currentOffset = 0)
    {
        var offset = alignment switch
        {
            ScrollAlignment.Start => target.Y,
            ScrollAlignment.Center => target.Y - (viewportHeight - target.Height) / 2,
            ScrollAlignment.End => target.Bottom - viewportHeight,
            _ => NearestOffset(target, viewportHeight, currentOffset),
        };

        return Math.Clamp(offset, 0, Math.Max(0, Height - viewportHeight));
    }

    private static LayoutBox? FindBox(LayoutBox box, IElement element)
    {
        if (box.Source.Element == element)
        {
            return box;
        }

        foreach (var child in box.Children)
        {
            var found = FindBox(child, element);

            if (found is not null)
            {
                return found;
            }
        }

        return null;
    }

    /// <summary>
    /// The smallest move that brings the rectangle fully into view. A rectangle taller than the
    /// viewport is aligned to its top, because its bottom edge can never be reached anyway.
    /// </summary>
    private static Double NearestOffset(Rect target, Double viewportHeight, Double currentOffset)
    {
        if (target.Height >= viewportHeight || target.Y < currentOffset)
        {
            return target.Y;
        }

        return target.Bottom > currentOffset + viewportHeight
            ? target.Bottom - viewportHeight
            : currentOffset;
    }
}

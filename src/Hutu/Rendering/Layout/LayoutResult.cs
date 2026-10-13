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
/// <c>0, 0</c> to <c>Width, Height</c>: the boxes are in that frame, and a scroll offset names the
/// point of it the viewport's top left corner stands at.
/// <para>
/// That frame is not the one a reader scrolls in. A reader's offset is counted from the corner the
/// document begins at, and on an axis the page reads against - the horizontal one of a vertical page,
/// whose columns march leftward from the right edge - those are opposite ends of the same rectangle.
/// <see cref="FrameOffset"/> and <see cref="ScrollPosition"/> are the two views of one offset.
/// </para>
/// </remarks>
public sealed class LayoutResult
{
    internal LayoutResult(LayoutBox root, Double width, Double height)
    {
        Root = root;
        Width = width;
        Height = height;
    }

    /// <summary>
    /// Gets the root of the layout tree. It is the engine's own structure rather than anything a host
    /// draws, so it stays inside: a host scrolls with <see cref="Width"/> and <see cref="Height"/> and
    /// paints from the paint list.
    /// </summary>
    internal LayoutBox Root { get; }

    /// <summary>Gets the width of the laid-out document, i.e. the scrollable content width.</summary>
    public Double Width { get; }

    /// <summary>Gets the height of the laid-out document, i.e. the scrollable content height.</summary>
    public Double Height { get; }

    /// <summary>
    /// Gets whether the document begins at its right edge: the columns of a vertical page march
    /// leftward from there, and a right-to-left page reads that way along its lines.
    /// </summary>
    /// <remarks>
    /// A host needs it for anything it lays out beside the document - a scrollbar, an edge a page is
    /// turned from - and to know which way moving forward goes. Where the viewport stands when the
    /// reader is at offset zero is <see cref="FrameOffset"/>'s answer instead.
    /// </remarks>
    public Boolean StartsAtRight => Root.Flow.StartsAtTheRight;

    /// <summary>
    /// Gets the offset the layout is drawn at when the reader is at the given scroll position: the
    /// point of the document the viewport's top left corner stands at, which is what a painter is
    /// handed and what a pointer is measured against.
    /// </summary>
    /// <remarks>
    /// The two are the same point of the same rectangle, counted from opposite ends on any axis the
    /// page reads against: a vertical page is scrolled leftward to go forward, so the beginning is at
    /// the far end of the frame's x. On every other axis the two agree.
    /// </remarks>
    public Offset FrameOffset(Offset scroll, Double viewportWidth, Double viewportHeight) =>
        Mirror(scroll, viewportWidth, viewportHeight);

    /// <summary>
    /// Gets the position a reader is at when the layout is drawn at the given frame offset, which is
    /// the offset a host's scrollbar moves in.
    /// </summary>
    public Offset ScrollPosition(Offset frame, Double viewportWidth, Double viewportHeight) =>
        Mirror(frame, viewportWidth, viewportHeight);

    /// <summary>
    /// Turns an offset from either frame into the other. Each frame is the other's image, so one
    /// subtraction serves both directions.
    /// </summary>
    private Offset Mirror(Offset offset, Double viewportWidth, Double viewportHeight)
    {
        var flow = Root.Flow;

        return new Offset(
            flow.StartsAtTheRight ? Math.Max(0, Width - viewportWidth) - offset.X : offset.X,
            flow.StartsAtTheBottom ? Math.Max(0, Height - viewportHeight) - offset.Y : offset.Y);
    }

    /// <summary>
    /// Gets the first box an element generated, in document order. An element whose content spans
    /// several lines generates several boxes; the first is the one its top edge belongs to.
    /// </summary>
    internal LayoutBox? FindBox(IElement element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return FindBox(Root, element);
    }

    /// <summary>
    /// Gets the vertical offset of the frame that brings a rectangle into view, clamped to the range
    /// the document can actually scroll through, so the result can be assigned without checking the
    /// bounds first.
    /// </summary>
    /// <remarks>
    /// Both the argument and the answer are frame offsets, because the rectangle is a box of the
    /// layout; a host that scrolls in the reader's frame turns the answer round with
    /// <see cref="ScrollPosition"/>. The vertical axis is the one both frames agree on in every mode
    /// but a vertical one that reads up the page, which is why this one is asked about elements.
    /// </remarks>
    /// <param name="target">The rectangle to bring into view, in document coordinates.</param>
    /// <param name="viewportHeight">The height of the viewport it is viewed through.</param>
    /// <param name="alignment">Where the rectangle should end up in the viewport.</param>
    /// <param name="currentOffset">The frame offset the viewport is at now, which is what lets
    /// <see cref="ScrollAlignment.Nearest"/> leave an already visible rectangle alone.</param>
    public Double VerticalFrameOffsetFor(
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

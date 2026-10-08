using AngleSharp.Css.Dom;
using System;

namespace Hutu.Rendering.Layout;

/// <summary>
/// The rectangle a viewport scrolls over: everything the document occupies and where it begins.
/// </summary>
/// <remarks>
/// A document is not the root's box. Text that runs past the root's edge is still read by scrolling
/// to it, and that is what a vertical page is made of: there the root's block axis is the horizontal
/// one, so a chapter longer than the viewport is wider than the box holding it. The rectangle is the
/// union of the root's box with every descendant's, less the sides the page reads from - nothing
/// scrolls back past where the document begins, so a box hanging off that edge is cut off rather
/// than made reachable.
/// <para>
/// The frame it is measured in has the root's box at the origin, as the layout produced it. A
/// scroller wants the rectangle itself at the origin instead, which is what
/// <see cref="MoveToOrigin"/> settles.
/// </para>
/// </remarks>
internal static class ScrollableOverflow
{
    /// <summary>
    /// Measures the rectangle the document can be scrolled over, in the coordinates the layout was
    /// produced in.
    /// </summary>
    public static Rect Of(LayoutBox root)
    {
        var region = Union(root, root.BorderBox, anchored: false);

        // The root's own box is as far as either side the page reads from goes. The other two grow
        // with whatever reaches past them: below a row and beside a column is where a document
        // continues, and it cannot continue behind itself.
        var border = root.BorderBox;
        var flow = root.Flow;

        var left = flow.BlockSign >= 0 ? Math.Max(region.X, border.X) : region.X;
        var right = flow.BlockSign < 0 ? Math.Min(region.Right, border.Right) : region.Right;
        var top = flow.InlineSign >= 0 ? Math.Max(region.Y, border.Y) : region.Y;
        var bottom = flow.InlineSign < 0 ? Math.Min(region.Bottom, border.Bottom) : region.Bottom;

        return new Rect(left, top, Math.Max(0, right - left), Math.Max(0, bottom - top));
    }

    /// <summary>
    /// Moves every box of the document so that its region begins at the origin.
    /// </summary>
    /// <remarks>
    /// What a scroller measures is a distance from where the document begins, so the frame a host
    /// works in is the region's rather than the root's box's. The two differ only where a box
    /// reaches past the block or inline end of the root - a column with a definite block size, or
    /// absolutely positioned content placed off the edge - and there the region's own edges are the
    /// only ones a scroll offset can be measured from.
    /// </remarks>
    public static void MoveToOrigin(LayoutBox root, Rect region)
    {
        if (region.X != 0 || region.Y != 0)
        {
            Shift(root, -region.X, -region.Y, anchored: false);
        }
    }

    /// <summary>
    /// Adds the extents of a box's descendants to the rectangle, starting from the box itself.
    /// </summary>
    /// <param name="anchored">
    /// Whether the box already sits in a fixed frame, where the viewport rather than the page is the
    /// frame of reference. Its contents neither widen the page nor move with it.
    /// </param>
    private static Rect Union(LayoutBox box, Rect region, Boolean anchored)
    {
        foreach (var child in box.Children)
        {
            var fixedFrame = anchored || child.Position == PositionMode.Fixed;

            if (!fixedFrame)
            {
                region = Merge(region, child.BorderBox);
            }

            // A box that clips what it holds keeps the overflow to itself - that is what makes it a
            // scroll container - so nothing inside it reaches the page around it.
            if (!child.ClipsOverflow)
            {
                region = Union(child, region, fixedFrame);
            }
        }

        return region;
    }

    /// <summary>Moves every box by the same distance, leaving the fixed frame where it is.</summary>
    private static void Shift(LayoutBox box, Double dx, Double dy, Boolean anchored)
    {
        if (!anchored)
        {
            box.BorderBox = new Rect(box.BorderBox.X + dx, box.BorderBox.Y + dy, box.BorderBox.Width, box.BorderBox.Height);
        }

        // A sticky box is pinned by where it sits in the page and by where the viewport stands over
        // it. Both parts move together here, so the shift between them is what it was - and the
        // offset it is worked out at is given in the same frame the page now occupies.
        if (box.Sticky is { } sticky && !anchored)
        {
            sticky.Box = Shifted(sticky.Box, dx, dy);
            sticky.ContainingBlock = Shifted(sticky.ContainingBlock, dx, dy);
        }

        foreach (var child in box.Children)
        {
            Shift(child, dx, dy, anchored || child.Position == PositionMode.Fixed);
        }
    }

    private static Rect Merge(Rect region, Rect box)
    {
        var left = Math.Min(region.X, box.X);
        var top = Math.Min(region.Y, box.Y);

        return new Rect(
            left,
            top,
            Math.Max(region.Right, box.Right) - left,
            Math.Max(region.Bottom, box.Bottom) - top);
    }

    private static Rect Shifted(Rect rect, Double dx, Double dy) =>
        new(rect.X + dx, rect.Y + dy, rect.Width, rect.Height);
}

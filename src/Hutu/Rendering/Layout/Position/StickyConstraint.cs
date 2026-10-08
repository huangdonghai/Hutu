using System;

namespace Hutu.Rendering.Layout;

/// <summary>
/// How a sticky box stays pinned inside the scrollport: the offset that has to be added to its static
/// position for a given scroll position.
/// </summary>
/// <remarks>
/// The constraint is what lets <c>sticky</c> be answered without a relayout. Layout records the box's
/// static rectangle and the rule; the painter applies <see cref="Shift"/> with the scroll offset it
/// is handed, so scrolling never reruns the layout pass. An <c>auto</c> edge is carried as
/// <see cref="Double.NaN"/>, which no comparison or clamp will satisfy, so it needs no special case.
/// </remarks>
public sealed class StickyConstraint
{
    /// <param name="viewport">
    /// The viewport at the origin. Its size is the scrollport; the live scroll offset is supplied at
    /// paint time, not baked in here.
    /// </param>
    /// <param name="containingBlock">
    /// The box's containing block, which bounds how far it may travel - a sticky header cannot leave
    /// its section.
    /// </param>
    /// <param name="box">The box's static rectangle, i.e. where it sits with no scroll at all.</param>
    public StickyConstraint(Rect viewport, Rect containingBlock, Rect box, Double top, Double right, Double bottom, Double left)
    {
        Viewport = viewport;
        ContainingBlock = containingBlock;
        Box = box;
        Top = top;
        Right = right;
        Bottom = bottom;
        Left = left;
    }

    /// <summary>Gets the scrollport, i.e. the viewport rectangle at the origin.</summary>
    public Rect Viewport { get; }

    /// <summary>
    /// Gets the containing block the box may not leave. Settable because the constraint travels with
    /// the box: a layout whose document is moved to the frame a scroller measures in moves both the
    /// box and the block it is bound to, and the shift it works out is the same either way.
    /// </summary>
    public Rect ContainingBlock { get; internal set; }

    /// <summary>Gets the static rectangle the shift is measured from.</summary>
    public Rect Box { get; internal set; }

    public Double Top { get; }

    public Double Right { get; }

    public Double Bottom { get; }

    public Double Left { get; }

    public Boolean HasTop => !Double.IsNaN(Top);

    public Boolean HasRight => !Double.IsNaN(Right);

    public Boolean HasBottom => !Double.IsNaN(Bottom);

    public Boolean HasLeft => !Double.IsNaN(Left);

    /// <summary>
    /// Gets how far the box moves from its static position at the given scroll offset. The box is
    /// pinned to the declared edges and then clamped so it never leaves its containing block.
    /// </summary>
    public Offset Shift(Offset scroll)
    {
        var window = new Rect(scroll.X, scroll.Y, Viewport.Width, Viewport.Height);
        var dx = 0.0;
        var dy = 0.0;

        if (HasLeft && Box.X < window.X + Left)
        {
            dx = window.X + Left - Box.X;
        }
        else if (HasRight && Box.Right > window.Right - Right)
        {
            dx = window.Right - Right - Box.Right;
        }

        if (HasTop && Box.Y < window.Y + Top)
        {
            dy = window.Y + Top - Box.Y;
        }
        else if (HasBottom && Box.Bottom > window.Bottom - Bottom)
        {
            dy = window.Bottom - Bottom - Box.Bottom;
        }

        // The box may not leave its containing block - but only when that block has a definite size
        // on the axis. A zero dimension means "indefinite" here, the same convention percentage
        // heights use, because a content-sized ancestor's height is not known while it arranges its
        // children. Clamping against it would instead shove the box off the top of the page.
        if (ContainingBlock.Width > 0)
        {
            dx = Clamp(dx, ContainingBlock.X - Box.X, ContainingBlock.Right - Box.Right);
        }

        if (ContainingBlock.Height > 0)
        {
            dy = Clamp(dy, ContainingBlock.Y - Box.Y, ContainingBlock.Bottom - Box.Bottom);
        }

        return new Offset(dx, dy);
    }

    /// <summary>
    /// Clamps a shift, letting the lower bound win when the box is larger than what contains it -
    /// a plain <see cref="Math.Clamp"/> throws in exactly that case.
    /// </summary>
    private static Double Clamp(Double value, Double minimum, Double maximum) =>
        minimum > maximum ? minimum : Math.Clamp(value, minimum, maximum);
}

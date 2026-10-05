using System;

namespace Hutu.Rendering.Layout;

/// <summary>
/// The state that flows through a layout pass: the box a percentage resolves against, the viewport,
/// the scroll offset and the text measurer.
/// </summary>
/// <remarks>
/// This struct replaces the render device as the source of the percentage basis. The device's width
/// is the viewport - a global - while a percentage is relative to the containing block, which is a
/// property of where a box sits in the tree. Threading it explicitly is what lets
/// <c>width: 100%</c> mean "one container" and a <c>figure img { width: 100% }</c> follow its figure.
/// <para>
/// The viewport and the scroll offset stay global; only the containing block changes as the pass
/// descends, which <see cref="ForContainingBlock(Size)"/> produces. Two rectangles ride along with
/// the sizes because positioning needs coordinates rather than dimensions: the containing block's
/// own rectangle (a sticky box is confined to it) and the rectangle an <c>absolute</c> descendant
/// resolves against (its nearest positioned ancestor's padding box, or the viewport).
/// </para>
/// </remarks>
public readonly struct LayoutContext
{
    public LayoutContext(Size containingBlock, Size viewport, Offset scroll, ITextMeasurer measurer)
        : this(
            containingBlock,
            new Rect(0, 0, containingBlock.Width, containingBlock.Height),
            viewport,
            new Rect(0, 0, viewport.Width, viewport.Height),
            scroll,
            new Rect(0, 0, viewport.Width, viewport.Height),
            measurer)
    {
    }

    public LayoutContext(
        Size containingBlock,
        Rect containingBlockRect,
        Size viewport,
        Rect viewportRect,
        Offset scroll,
        Rect absoluteContainingBlock,
        ITextMeasurer measurer)
    {
        ContainingBlock = containingBlock;
        ContainingBlockRect = containingBlockRect;
        Viewport = viewport;
        ViewportRect = viewportRect;
        Scroll = scroll;
        AbsoluteContainingBlock = absoluteContainingBlock;
        Measurer = measurer;
    }

    /// <summary>Gets the box percentages, auto widths and relative positioning resolve against.</summary>
    public Size ContainingBlock { get; }

    /// <summary>Gets the containing block's rectangle, in document coordinates.</summary>
    public Rect ContainingBlockRect { get; }

    public Size Viewport { get; }

    /// <summary>Gets the viewport's rectangle at the origin, which <c>fixed</c> resolves against.</summary>
    public Rect ViewportRect { get; }

    /// <summary>Gets how far the content is scrolled, which only <c>fixed</c> and <c>sticky</c> use.</summary>
    public Offset Scroll { get; }

    /// <summary>
    /// Gets the rectangle an <c>absolute</c> descendant resolves its offsets against: the padding box
    /// of the nearest positioned ancestor, or the viewport when there is none.
    /// </summary>
    public Rect AbsoluteContainingBlock { get; }

    public ITextMeasurer Measurer { get; }

    /// <summary>Gets the context a box's children see, with its content box as their containing block.</summary>
    public LayoutContext ForContainingBlock(Size contentBox) => new(
        contentBox,
        new Rect(ContainingBlockRect.X, ContainingBlockRect.Y, contentBox.Width, contentBox.Height),
        Viewport,
        ViewportRect,
        Scroll,
        AbsoluteContainingBlock,
        Measurer);

    /// <summary>
    /// Gets the context a box's children see when the content box's position is known, which is what
    /// positioning needs and a bare size cannot express.
    /// </summary>
    public LayoutContext ForContainingBlock(Rect contentBox) => new(
        new Size(contentBox.Width, contentBox.Height),
        contentBox,
        Viewport,
        ViewportRect,
        Scroll,
        AbsoluteContainingBlock,
        Measurer);

    /// <summary>
    /// Gets the context a positioned box's children see: the absolute containing block becomes this
    /// box's padding box, so an <c>absolute</c> descendant anchors to it rather than to an ancestor
    /// further up.
    /// </summary>
    public LayoutContext WithAbsoluteContainingBlock(Rect paddingBox) => new(
        ContainingBlock,
        ContainingBlockRect,
        Viewport,
        ViewportRect,
        Scroll,
        paddingBox,
        Measurer);
}

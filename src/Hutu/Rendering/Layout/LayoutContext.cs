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
internal readonly struct LayoutContext
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
        : this(containingBlock, containingBlockRect, viewport, viewportRect, scroll, absoluteContainingBlock, measurer, null, false)
    {
    }

    private LayoutContext(
        Size containingBlock,
        Rect containingBlockRect,
        Size viewport,
        Rect viewportRect,
        Offset scroll,
        Rect absoluteContainingBlock,
        ITextMeasurer measurer,
        FloatBand? floats,
        Boolean hypothetical)
    {
        ContainingBlock = containingBlock;
        ContainingBlockRect = containingBlockRect;
        Viewport = viewport;
        ViewportRect = viewportRect;
        Scroll = scroll;
        AbsoluteContainingBlock = absoluteContainingBlock;
        Measurer = measurer;
        Floats = floats;
        BasisIsHypothetical = hypothetical;
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

    /// <summary>
    /// Gets the floats this box's line boxes stand aside for, and where the box begins inside them -
    /// null for a box with nothing floated anywhere above it, which is most of a page.
    /// </summary>
    internal FloatBand? Floats { get; }

    /// <summary>
    /// Gets whether the containing block handed down is a hypothesis rather than a fact.
    /// </summary>
    /// <remarks>
    /// An intrinsic measurement asks what a box would need at whatever width it ends up with, so while
    /// one is running the block a percentage speaks of has no size to be a fraction of yet - and a
    /// percentage answered with the width being asked about is the answer fed back into the question.
    /// That is how a menu bar whose entries each declare <c>width: 100%</c> comes to ask every one of
    /// them for the whole row, and the row overflows the page. The specification's word for it is that a
    /// percentage against an indefinite containing block behaves as <c>auto</c>.
    /// </remarks>
    public Boolean BasisIsHypothetical { get; }

    /// <summary>
    /// Gets the same context seen from inside an intrinsic measurement, where the containing block is
    /// the thing being worked out rather than something settled beforehand.
    /// </summary>
    internal LayoutContext WithHypotheticalBasis() => new(
        ContainingBlock,
        ContainingBlockRect,
        Viewport,
        ViewportRect,
        Scroll,
        AbsoluteContainingBlock,
        Measurer,
        Floats,
        true);

    /// <summary>
    /// Gets the context a child is handed when the floats around it and its own place among them are
    /// known, which is a per-child answer rather than one shared by the whole container: two siblings
    /// stand at different heights, and a float reaching down between them narrows only the lower one.
    /// </summary>
    internal LayoutContext WithFloats(FloatBand? floats) => new(
        ContainingBlock,
        ContainingBlockRect,
        Viewport,
        ViewportRect,
        Scroll,
        AbsoluteContainingBlock,
        Measurer,
        floats,
        BasisIsHypothetical);

    /// <summary>Gets the context a box's children see, with its content box as their containing block.</summary>
    /// <remarks>
    /// The band rides along rather than being dropped: a box that holds no floats of its own leaves the
    /// ones above it in force for everything inside it, and a context that lost the band on the way down
    /// would shorten only the lines of the container the floats sit in - which is the difference between
    /// a title beside its picture and the same title one <c>div</c> deeper.
    /// </remarks>
    public LayoutContext ForContainingBlock(Size contentBox) => new(
        contentBox,
        new Rect(ContainingBlockRect.X, ContainingBlockRect.Y, contentBox.Width, contentBox.Height),
        Viewport,
        ViewportRect,
        Scroll,
        AbsoluteContainingBlock,
        Measurer,
        Floats,
        BasisIsHypothetical);

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
        Measurer,
        Floats,
        BasisIsHypothetical);

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
        Measurer,
        Floats,
        BasisIsHypothetical);
}

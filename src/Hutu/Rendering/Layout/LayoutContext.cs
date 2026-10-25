using System;

namespace Hutu.Rendering.Layout;

/// <summary>
/// A viewer standing <see cref="Distance"/> pixels in front of the plane, looking through the point
/// <see cref="OriginX"/>, <see cref="OriginY"/> in document coordinates: what the <c>perspective</c>
/// property puts between a box and its children (CSS Transforms 2 §perspective-property).
/// </summary>
internal readonly record struct PerspectiveView(Double Distance, Double OriginX, Double OriginY);

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
/// <para>
/// <see cref="CapturedContainingBlock"/> is a second, narrower channel: the padding box of the nearest
/// ancestor that captures <c>fixed</c> descendants as well as <c>absolute</c> ones - a transform or
/// offset path, a filter or a backdrop filter, layout or paint containment, or a <c>will-change</c>
/// hint at one of those. CSS makes such an ancestor the containing block for <c>fixed</c> descendants
/// too (not just <c>absolute</c>), which is why fixed cannot simply reuse
/// <see cref="AbsoluteContainingBlock"/>.
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
        : this(containingBlock, containingBlockRect, viewport, viewportRect, scroll, absoluteContainingBlock, measurer, null, false, null)
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
        Boolean hypothetical,
        SubgridTracks? subgrid = null,
        Rect? capturedContainingBlock = null,
        Rect? referenceBorderBox = null,
        Rect? referencePaddingBox = null,
        CornerRadii? referenceRadii = null,
        PerspectiveView? perspective = null)
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
        Subgrid = subgrid;
        CapturedContainingBlock = capturedContainingBlock;
        ReferenceBorderBox = referenceBorderBox;
        ReferencePaddingBox = referencePaddingBox;
        ReferenceRadii = referenceRadii;
        Perspective = perspective;
    }

    /// <summary>
    /// Gets the perspective a box in this context is seen with, when the element establishing the
    /// context declares one: a viewer <see cref="PerspectiveView.Distance"/> pixels away, looking
    /// through the point <see cref="PerspectiveView.OriginX"/>, <see cref="PerspectiveView.OriginY"/>
    /// in document coordinates. Null when the context's children are drawn flat.
    /// </summary>
    public PerspectiveView? Perspective { get; }

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

    /// <summary>
    /// Gets the padding box of the nearest ancestor that captures <c>fixed</c> descendants as well as
    /// <c>absolute</c> ones - a transform, an offset path, a filter or containment - or null when there is
    /// none. A fixed descendant resolves against this in preference to the viewport.
    /// </summary>
    public Rect? CapturedContainingBlock { get; }

    /// <summary>
    /// Gets the border box of the element that establishes this containing block, which is the
    /// reference box an <c>offset-path</c> shape sizes into when it names no <c>&lt;coord-box&gt;</c>.
    /// Null when nothing recorded it, in which case the content box stands in.
    /// </summary>
    public Rect? ReferenceBorderBox { get; }

    /// <summary>Gets the padding box of the element that establishes this containing block.</summary>
    public Rect? ReferencePaddingBox { get; }

    /// <summary>Gets the corner radii of that element's border box.</summary>
    public CornerRadii? ReferenceRadii { get; }

    public ITextMeasurer Measurer { get; }

    /// <summary>
    /// Gets the floats this box's line boxes stand aside for, and where the box begins inside them -
    /// null for a box with nothing floated anywhere above it, which is most of a page.
    /// </summary>
    internal FloatBand? Floats { get; }

    /// <summary>
    /// Gets the tracks the grid around this box gave the area it spans, where this box is a subgrid:
    /// its own track list is the parent's rather than anything it wrote (CSS Grid 2 §8). Null for a
    /// box that stands anywhere else, which is every box whose grid is its own.
    /// </summary>
    internal SubgridTracks? Subgrid { get; }

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
        true,
        Subgrid,
        CapturedContainingBlock,
        ReferenceBorderBox,
        ReferencePaddingBox,
        ReferenceRadii,
        Perspective);

    /// <summary>
    /// Gets the context a child is handed when the floats around it and its own place among them are
    /// known, which is a per-child answer rather than one shared by the whole container: two siblings
    /// stand at different heights, and a float reaching down between them narrows only the lower one.
    /// </summary>
    /// <summary>
    /// Gets the context a subgrid is laid out in: the tracks and gutters the parent grid gave the area
    /// it spans, so its own children stand on the parent's lines (CSS Grid 2 §8).
    /// </summary>
    internal LayoutContext WithSubgrid(SubgridTracks subgrid) => new(
        ContainingBlock,
        ContainingBlockRect,
        Viewport,
        ViewportRect,
        Scroll,
        AbsoluteContainingBlock,
        Measurer,
        Floats,
        BasisIsHypothetical,
        subgrid,
        CapturedContainingBlock,
        ReferenceBorderBox,
        ReferencePaddingBox,
        ReferenceRadii,
        Perspective);

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
        BasisIsHypothetical,
        Subgrid,
        CapturedContainingBlock,
        ReferenceBorderBox,
        ReferencePaddingBox,
        ReferenceRadii,
        Perspective);

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
        BasisIsHypothetical,
        Subgrid,
        CapturedContainingBlock,
        ReferenceBorderBox,
        ReferencePaddingBox,
        ReferenceRadii,
        Perspective);

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
        BasisIsHypothetical,
        Subgrid,
        CapturedContainingBlock,
        ReferenceBorderBox,
        ReferencePaddingBox,
        ReferenceRadii,
        Perspective);

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
        BasisIsHypothetical,
        Subgrid,
        CapturedContainingBlock,
        ReferenceBorderBox,
        ReferencePaddingBox,
        ReferenceRadii,
        Perspective);

    /// <summary>
    /// Gets the context a box that captures its descendants hands them: the padding box becomes the frame
    /// for <c>absolute</c> descendants as with a positioned ancestor, and <c>fixed</c> ones are pulled in
    /// behind it - which is what <see cref="CapturedContainingBlock"/> carries. A transform or offset path
    /// (CSS Transforms 1 §transform-rendering), a filter or backdrop filter
    /// (CSS Filter Effects 1 §2.4) and layout or paint containment (CSS Contain 2 §2.2) each do this.
    /// </summary>
    public LayoutContext WithCapturedContainingBlock(Rect paddingBox) => new(
        ContainingBlock,
        ContainingBlockRect,
        Viewport,
        ViewportRect,
        Scroll,
        paddingBox,
        Measurer,
        Floats,
        BasisIsHypothetical,
        Subgrid,
        paddingBox,
        ReferenceBorderBox,
        ReferencePaddingBox,
        ReferenceRadii,
        Perspective);

    /// <summary>
    /// Gets the context with the boxes of the element that establishes it, so a shape an
    /// <c>offset-path</c> draws can size into the border box, padding box or content box it names -
    /// the element the containing block belongs to, not the element carrying the path.
    /// </summary>
    internal LayoutContext WithReferenceBox(Rect borderBox, Rect paddingBox, CornerRadii radii) => new(
        ContainingBlock,
        ContainingBlockRect,
        Viewport,
        ViewportRect,
        Scroll,
        AbsoluteContainingBlock,
        Measurer,
        Floats,
        BasisIsHypothetical,
        Subgrid,
        CapturedContainingBlock,
        borderBox,
        paddingBox,
        radii,
        Perspective);

    /// <summary>
    /// Gets the context a box's children are seen through: the viewer the box declares, or none when
    /// it declares no perspective - a perspective reaches only the children of the box that states it,
    /// so a context that descends past that box loses it again (CSS Transforms 2 §perspective-property).
    /// </summary>
    internal LayoutContext WithPerspective(PerspectiveView? perspective) => new(
        ContainingBlock,
        ContainingBlockRect,
        Viewport,
        ViewportRect,
        Scroll,
        AbsoluteContainingBlock,
        Measurer,
        Floats,
        BasisIsHypothetical,
        Subgrid,
        CapturedContainingBlock,
        ReferenceBorderBox,
        ReferencePaddingBox,
        ReferenceRadii,
        perspective);
}

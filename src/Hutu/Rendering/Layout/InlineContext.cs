using System;

namespace Hutu.Rendering.Layout;

/// <summary>
/// What an inline formatting context needs in order to place an atomic inline-level box: the engine
/// that measures and arranges it, and the containing block standing around the line it sits on.
/// </summary>
/// <remarks>
/// An inline-block is the one thing a line lays out that is not built from runs, so the line has to ask
/// the engine for it. The engine owns the inline context and constructs it, so passing itself in at
/// construction time would tie the two together in both directions; it is handed along per call
/// instead. The struct holds two references, so a call costs no allocation.
/// </remarks>
internal readonly struct InlineContext
{
    public InlineContext(LayoutEngine engine, LayoutContext containing, LayoutContext? absolute = null)
        : this(engine, containing, absolute, containing)
    {
    }

    public InlineContext(LayoutEngine engine, LayoutContext containing, LayoutContext? absolute, LayoutContext atomic)
    {
        Engine = engine ?? throw new ArgumentNullException(nameof(engine));
        Containing = containing;
        Absolute = absolute;
        Atomic = atomic;
    }

    /// <summary>Gets the engine that measures and arranges an atomic box.</summary>
    public LayoutEngine Engine { get; }

    /// <summary>Gets the context the atomic box is measured and arranged against.</summary>
    public LayoutContext Containing { get; }

    /// <summary>
    /// Gets the context a box that left the flow is placed against, when it is not the one the line's
    /// own boxes are arranged against.
    /// </summary>
    /// <remarks>
    /// An absolutely positioned box anchors to the nearest positioned ancestor, which is a question about
    /// the subtree rather than about the line it was written in - so it is answered by a context the
    /// inline layout is handed rather than one it can work out for itself. Left null, the line's own
    /// context stands in, which is right wherever nothing between them is positioned.
    /// </remarks>
    public LayoutContext? Absolute { get; }

    /// <summary>
    /// Gets the context an atomic inline-level box standing on the line is measured and arranged
    /// against: the content box of the block container the line sits in, which is the containing block
    /// CSS 2.1 §9.1.1 gives an inline-level box - the very box whose lines these are, and not the one
    /// its own lines resolve their lengths against.
    /// </summary>
    /// <remarks>
    /// The two rectangles differ whenever the container does not fill its own containing block: a
    /// floated or absolutely positioned box, a table, an inline-block. A percentage height on the
    /// atomic box speaks of the container it stands in (§10.5), while a percentage on the line itself
    /// - <c>text-indent</c>, say (§16.1) - speaks of the containing block of the box that declares it,
    /// which is the container's own containing block. Keeping the two apart is what lets both come out
    /// right at once.
    /// </remarks>
    public LayoutContext Atomic { get; }
}

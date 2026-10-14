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
    {
        Engine = engine ?? throw new ArgumentNullException(nameof(engine));
        Containing = containing;
        Absolute = absolute;
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
}

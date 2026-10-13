using Hutu.Rendering.Layout;
using System.Collections.Generic;

namespace Hutu.Rendering.Paint;

/// <summary>
/// The ordered drawing instructions for a document, in paint order: backgrounds and borders before
/// the content they contain, and children after their parent.
/// </summary>
/// <remarks>
/// <para>
/// Most of the document is in <see cref="Commands"/>, in document coordinates and already ordered by
/// stacking level. The two viewport-anchored kinds live apart because a later scroll must move them
/// differently: a fixed box stays put and a sticky box is pinned to an edge, and neither can be
/// expressed as a document-coordinate command.
/// </para>
/// <para>
/// A list is what the painter hands back, never what a host assembles, so the constructor - like the
/// commands in it - belongs to the engine.
/// </para>
/// </remarks>
public sealed class PaintList
{
    internal PaintList()
    {
    }

    public List<PaintCommand> Commands { get; } = new();

    /// <summary>
    /// Gets the commands of the <c>fixed</c> boxes. Their geometry is in viewport coordinates, so a
    /// painter draws them without the document's scroll translation.
    /// </summary>
    public List<PaintCommand> FixedCommands { get; } = new();

    /// <summary>
    /// Gets the commands of the <c>sticky</c> boxes, each with the constraint that decides how far it
    /// moves at a given scroll offset.
    /// </summary>
    public List<StickyLayer> Sticky { get; } = new();

    /// <summary>Gets the size of the painted document, which is the scrollable content size.</summary>
    public Rect Bounds { get; internal set; }
}

/// <summary>
/// The commands of one sticky box together with the rule that pins it. The painter puts a layer
/// together as it walks the tree; a backend only asks the rule how far the box has moved.
/// </summary>
public sealed class StickyLayer
{
    internal StickyLayer(StickyConstraint constraint) => Constraint = constraint;

    public StickyConstraint Constraint { get; }

    public List<PaintCommand> Commands { get; } = new();
}

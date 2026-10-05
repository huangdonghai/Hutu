using Hutu.Rendering.Layout;
using System.Collections.Generic;

namespace Hutu.Rendering.Paint;

/// <summary>
/// The ordered drawing instructions for a document, in paint order: backgrounds and borders before
/// the content they contain, and children after their parent.
/// </summary>
/// <remarks>
/// Most of the document is in <see cref="Commands"/>, in document coordinates and already ordered by
/// stacking level. The two viewport-anchored kinds live apart because a later scroll must move them
/// differently: a fixed box stays put and a sticky box is pinned to an edge, and neither can be
/// expressed as a document-coordinate command.
/// </remarks>
public sealed class PaintList
{
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
    public Rect Bounds { get; set; }
}

/// <summary>The commands of one sticky box together with the rule that pins it.</summary>
public sealed class StickyLayer
{
    public StickyLayer(StickyConstraint constraint) => Constraint = constraint;

    public StickyConstraint Constraint { get; }

    public List<PaintCommand> Commands { get; } = new();
}

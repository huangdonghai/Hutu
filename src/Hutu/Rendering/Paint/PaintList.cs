using Hutu.Rendering.Layout;
using System.Collections.Generic;

namespace Hutu.Rendering.Paint;

/// <summary>
/// The ordered drawing instructions for a document, in paint order: backgrounds and borders before
/// the content they contain, and children after their parent.
/// </summary>
public sealed class PaintList
{
    public List<PaintCommand> Commands { get; } = new();

    /// <summary>Gets the size of the painted document, which is the scrollable content size.</summary>
    public Rect Bounds { get; set; }
}

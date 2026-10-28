namespace Hutu.Rendering.Layout;

/// <summary>
/// The line an inline-level out-of-flow box would have joined, and the point on it the box hangs from.
/// </summary>
/// <remarks>
/// An absolutely positioned box whose specified display was inline-level is not a block with a
/// static position at its container's start corner: the place it would have had in flow is an
/// insertion point in the line box the container was laying out, so the floats standing at that
/// block offset shorten the line before the point is chosen, and <c>text-align</c> shifts an empty
/// line's insertion point across what is left. The box then grows from that point along the
/// direction the line reads - rightwards in a left-to-right row, leftwards in a right-to-left one.
/// Measured in Chrome: a 20px box in a 100px container with <c>text-align: center</c> sits at 50
/// (not centered in the line as a 20px box would be at 40), and a right-to-left line puts it at 80.
/// </remarks>
internal readonly struct StaticLine
{
    public StaticLine(Double start, Double end, Double alignment, Int32 sign)
    {
        Start = start;
        End = end;
        Alignment = alignment;
        Sign = sign;
    }

    /// <summary>Gets the line's start edge, on the physical axis the line runs along.</summary>
    public Double Start { get; }

    /// <summary>Gets the line's end edge on that axis.</summary>
    public Double End { get; }

    /// <summary>Gets how far the insertion point stands from the edge the line begins at.</summary>
    public Double Alignment { get; }

    /// <summary>Gets the sign the line reads along: positive when it grows towards the end coordinate.</summary>
    public Int32 Sign { get; }

    /// <summary>Gets the border box's start coordinate for a box of the given width and margins.</summary>
    public Double Anchor(Double width, Thickness margin) => Sign > 0
        ? Start + Alignment + margin.Left
        : End - Alignment - width - margin.Right;
}

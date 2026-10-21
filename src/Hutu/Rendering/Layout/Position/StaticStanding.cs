namespace Hutu.Rendering.Layout;

/// <summary>
/// The area an out-of-flow box stands in, and how its own alignment moves it along that area.
/// </summary>
/// <remarks>
/// A container that lends an absolutely positioned child an area of its own - a grid names one from the
/// child's own <c>grid-row</c> and <c>grid-column</c> - gives it a room to be aligned in as well as an
/// edge to count offsets from, and CSS Align 3 §5.2 says the self-alignment applies to that position:
/// where both edges are <c>auto</c>, the box is put where its <c>justify-self</c> and <c>align-self</c>
/// ask rather than at the area's start corner.
/// </remarks>
internal readonly struct StaticStanding
{
    public StaticStanding(Rect area, FlexAlignment justify, FlexAlignment align)
    {
        Area = area;
        Justify = justify;
        Align = align;
    }

    /// <summary>Gets the area the box is aligned within, in the coordinates its offsets are resolved in.</summary>
    public Rect Area { get; }

    /// <summary>Gets the alignment along the inline axis, once resolved.</summary>
    public FlexAlignment Justify { get; }

    /// <summary>Gets the alignment along the block axis, once resolved.</summary>
    public FlexAlignment Align { get; }

    /// <summary>
    /// Resolves the alignment one axis of an out-of-flow box asks for. <c>auto</c> takes what the
    /// container says for that axis; every keyword that would otherwise stretch the box or set it on a
    /// line of text stands at the area's start edge instead, because an out-of-flow box has no line to
    /// join and no room it is expected to fill. <c>last baseline</c> is not read apart from a plain
    /// <c>baseline</c> yet, and comes to the same start edge.
    /// </summary>
    public static FlexAlignment Resolve(FlexAlignment self, FlexAlignment container) =>
        (self == FlexAlignment.Auto ? container : self) switch
        {
            FlexAlignment.Center => FlexAlignment.Center,
            FlexAlignment.End => FlexAlignment.End,
            _ => FlexAlignment.Start,
        };
}

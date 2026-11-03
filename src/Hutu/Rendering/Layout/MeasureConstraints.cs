using System;

namespace Hutu.Rendering.Layout;

/// <summary>
/// The space a formatting context makes available to a box while it is being measured.
/// </summary>
/// <remarks>
/// A null dimension means "unconstrained": the box is free to be as large as its content requires,
/// which is what an intrinsic measurement asks for. A number is a definite available size, which
/// flex and grid hand down once their own algorithm has decided how much each item gets.
/// </remarks>
internal readonly struct MeasureConstraints
{
    private MeasureConstraints(Double? availableWidth, Double? availableHeight, Double? definiteWidth)
    {
        AvailableWidth = availableWidth;
        AvailableHeight = availableHeight;
        DefiniteWidth = definiteWidth;
    }

    public Double? AvailableWidth { get; }

    public Double? AvailableHeight { get; }

    /// <summary>
    /// Gets the border-box width that has already been settled by the caller, or null where the
    /// box's own width still decides it. A table cell is the case this exists for: its column has
    /// settled the cell's width, so the cell's percentage width is not a second constraint.
    /// </summary>
    public Double? DefiniteWidth { get; }

    /// <summary>Gets constraints that leave both dimensions open, as an intrinsic measurement does.</summary>
    public static MeasureConstraints Unconstrained => default;

    /// <summary>Gets constraints that fix the available width and leave the height to the content.</summary>
    public static MeasureConstraints FitWidth(Double width) => new(Math.Max(0, width), null, null);

    /// <summary>Gets constraints that fix both dimensions.</summary>
    public static MeasureConstraints Fixed(Double width, Double height) =>
        new(Math.Max(0, width), Math.Max(0, height), null);

    /// <summary>
    /// Gets constraints that take the given border-box width as already resolved and leave the
    /// height to the content.
    /// </summary>
    public static MeasureConstraints Definite(Double width) =>
        new(Math.Max(0, width), null, Math.Max(0, width));

    /// <summary>
    /// Gets constraints that fix the available height and leave the width to the content. A table that
    /// reads down the page settles a cell's inline extent along the physical height, so it is the
    /// height its column has already settled rather than the width.
    /// </summary>
    public static MeasureConstraints FitHeight(Double height) =>
        new(null, Math.Max(0, height), null);

    public MeasureConstraints WithHeight(Double height) =>
        new(AvailableWidth, Math.Max(0, height), DefiniteWidth);
}

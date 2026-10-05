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
public readonly struct MeasureConstraints
{
    private MeasureConstraints(Double? availableWidth, Double? availableHeight)
    {
        AvailableWidth = availableWidth;
        AvailableHeight = availableHeight;
    }

    public Double? AvailableWidth { get; }

    public Double? AvailableHeight { get; }

    /// <summary>Gets constraints that leave both dimensions open, as an intrinsic measurement does.</summary>
    public static MeasureConstraints Unconstrained => default;

    /// <summary>Gets constraints that fix the available width and leave the height to the content.</summary>
    public static MeasureConstraints FitWidth(Double width) => new(Math.Max(0, width), null);

    /// <summary>Gets constraints that fix both dimensions.</summary>
    public static MeasureConstraints Fixed(Double width, Double height) => new(Math.Max(0, width), Math.Max(0, height));

    public MeasureConstraints WithHeight(Double height) => new(AvailableWidth, Math.Max(0, height));
}

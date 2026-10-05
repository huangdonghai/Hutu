using System;

namespace Hutu.Rendering.Layout;

/// <summary>
/// The four insets of a box - margin or padding - with their units still attached.
/// </summary>
/// <remarks>
/// Every side of a margin or a padding resolves its percentage against the containing block's
/// <em>width</em>, including the top and bottom ones. That is why this resolves from a single basis
/// rather than one per axis.
/// </remarks>
public readonly struct StyleThickness
{
    public StyleThickness(StyleLength top, StyleLength right, StyleLength bottom, StyleLength left)
    {
        Top = top;
        Right = right;
        Bottom = bottom;
        Left = left;
    }

    public static StyleThickness Zero { get; } = new(StyleLength.Zero, StyleLength.Zero, StyleLength.Zero, StyleLength.Zero);

    public StyleLength Top { get; }

    public StyleLength Right { get; }

    public StyleLength Bottom { get; }

    public StyleLength Left { get; }

    /// <summary>Gets whether any side is <c>auto</c>, which only margins allow.</summary>
    public Boolean HasAuto => Top.IsAuto || Right.IsAuto || Bottom.IsAuto || Left.IsAuto;

    /// <param name="basis">The containing block's width.</param>
    public Thickness Resolve(Double basis) => new(
        Top.Resolve(basis),
        Right.Resolve(basis),
        Bottom.Resolve(basis),
        Left.Resolve(basis));
}

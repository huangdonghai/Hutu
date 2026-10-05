using System;

namespace Hutu.Rendering.Layout;

/// <summary>An inset on all four sides, used for margins, borders and padding.</summary>
public readonly struct Thickness
{
    public Thickness(Double top, Double right, Double bottom, Double left)
    {
        Top = top;
        Right = right;
        Bottom = bottom;
        Left = left;
    }

    public Double Top { get; }

    public Double Right { get; }

    public Double Bottom { get; }

    public Double Left { get; }

    public Double Horizontal => Left + Right;

    public Double Vertical => Top + Bottom;

    public static Thickness Zero => default;
}

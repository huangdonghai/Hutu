using System;

namespace Hutu.Rendering.Layout;

/// <summary>A horizontal and a vertical displacement, used for scroll positions and translations.</summary>
public readonly struct Offset
{
    public Offset(Double x, Double y)
    {
        X = x;
        Y = y;
    }

    public Double X { get; }

    public Double Y { get; }

    public static Offset Zero => default;
}

using System;

namespace Hutu.Rendering.Layout;

/// <summary>An axis-aligned rectangle in document coordinates.</summary>
public readonly struct Rect
{
    public Rect(Double x, Double y, Double width, Double height)
    {
        X = x;
        Y = y;
        Width = width;
        Height = height;
    }

    public Double X { get; }

    public Double Y { get; }

    public Double Width { get; }

    public Double Height { get; }

    public Double Right => X + Width;

    public Double Bottom => Y + Height;

    public static Rect Empty => default;
}

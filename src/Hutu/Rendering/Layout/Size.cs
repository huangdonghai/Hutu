using System;

namespace Hutu.Rendering.Layout;

/// <summary>A width and a height, the size a box or a containing block occupies.</summary>
public readonly struct Size
{
    public Size(Double width, Double height)
    {
        Width = width;
        Height = height;
    }

    public Double Width { get; }

    public Double Height { get; }

    public static Size Empty => default;

    public override String ToString() =>
        Width.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) + "x" +
        Height.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
}

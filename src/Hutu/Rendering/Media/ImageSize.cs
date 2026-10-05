using System;

namespace Hutu.Rendering.Media;

/// <summary>
/// The intrinsic size of an image, in CSS pixels, as decoded from its bytes.
/// </summary>
public readonly struct ImageSize
{
    public ImageSize(Double width, Double height)
    {
        Width = width;
        Height = height;
    }

    public Double Width { get; }

    public Double Height { get; }

    /// <summary>Gets whether both dimensions are usable; a zero-sized image cannot be laid out.</summary>
    public Boolean IsValid => Width > 0 && Height > 0;

    /// <summary>Gets the width divided by the height, or 1 when the height is unknown.</summary>
    public Double AspectRatio => Height > 0 ? Width / Height : 1;
}

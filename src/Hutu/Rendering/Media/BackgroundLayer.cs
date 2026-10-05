using System;
using System.Collections.Generic;

namespace Hutu.Rendering.Media;

/// <summary>How a background layer's image is sized.</summary>
public enum BackgroundLayerSizeKind
{
    /// <summary>The image keeps its intrinsic size.</summary>
    Auto,

    /// <summary>The image is scaled to cover the whole positioning area.</summary>
    Cover,

    /// <summary>The image is scaled to fit inside the positioning area.</summary>
    Contain,

    /// <summary>The image is sized by <see cref="BackgroundLayer.SizeWidth"/> and <see cref="BackgroundLayer.SizeHeight"/>.</summary>
    Explicit,
}

/// <summary>
/// One component of a background position or size: a length, or a percentage of the free space.
/// </summary>
/// <remarks>
/// The distinction matters because a percentage is relative to the positioning area, not to the
/// viewport, so it cannot be resolved when the value is read - only when the box is known.
/// </remarks>
public readonly struct BackgroundOffset
{
    public BackgroundOffset(Double value, Boolean isPercentage)
    {
        Value = value;
        IsPercentage = isPercentage;
    }

    public Double Value { get; }

    public Boolean IsPercentage { get; }

    /// <summary>Resolves the offset against the space left over by the content.</summary>
    public Double Resolve(Double freeSpace) => IsPercentage ? Value * freeSpace : Value;
}

/// <summary>A colour stop of a gradient, with its position from 0 to 1.</summary>
public readonly struct GradientStop
{
    public GradientStop(Double offset, UInt32 color)
    {
        Offset = offset;
        Color = color;
    }

    public Double Offset { get; }

    /// <summary>Gets the colour as packed ARGB.</summary>
    public UInt32 Color { get; }
}

/// <summary>A linear gradient as declared in CSS.</summary>
public sealed class LinearGradientSpec
{
    public LinearGradientSpec(Double angleDegrees, IReadOnlyList<GradientStop> stops)
    {
        AngleDegrees = angleDegrees;
        Stops = stops;
    }

    /// <summary>Gets the direction, in degrees clockwise from "to top".</summary>
    public Double AngleDegrees { get; }

    public IReadOnlyList<GradientStop> Stops { get; }
}

/// <summary>
/// A single background layer, as resolved from the <c>background-image</c> and its companion
/// longhands. Exactly one of <see cref="Url"/> and <see cref="Gradient"/> is set.
/// </summary>
public sealed class BackgroundLayer
{
    /// <summary>Gets the image to draw, or null for a gradient layer.</summary>
    public String? Url { get; init; }

    /// <summary>Gets the gradient to draw, or null for an image layer.</summary>
    public LinearGradientSpec? Gradient { get; init; }

    public BackgroundLayerSizeKind SizeKind { get; init; }

    public BackgroundOffset SizeWidth { get; init; }

    public BackgroundOffset SizeHeight { get; init; }

    public BackgroundOffset PositionX { get; init; }

    public BackgroundOffset PositionY { get; init; }

    /// <summary>Gets whether the layer tiles. The initial value is <c>repeat</c>.</summary>
    public Boolean Repeat { get; init; }
}

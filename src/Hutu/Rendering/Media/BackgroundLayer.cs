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
/// <para>
/// It is built while the declaration is parsed, so a backend reads one rather than making it; that is
/// also why only <see cref="Resolve"/> is a host's to call.
/// </para>
/// </remarks>
public readonly struct BackgroundOffset
{
    internal BackgroundOffset(Double value, Boolean isPercentage)
    {
        Value = value;
        IsPercentage = isPercentage;
    }

    public Double Value { get; }

    public Boolean IsPercentage { get; }

    /// <summary>Resolves the offset against the space left over by the content.</summary>
    public Double Resolve(Double freeSpace) => IsPercentage ? Value * freeSpace : Value;
}

/// <summary>
/// A colour stop of a gradient, with its position from 0 to 1. The parser lays the stops down; a
/// backend only reads them back.
/// </summary>
public readonly struct GradientStop
{
    internal GradientStop(Double offset, UInt32 color)
    {
        Offset = offset;
        Color = color;
    }

    public Double Offset { get; }

    /// <summary>Gets the colour as packed ARGB.</summary>
    public UInt32 Color { get; }
}

/// <summary>
/// A linear gradient as declared in CSS. Like the stops it is made of, it is what the parser produces
/// and what a backend draws, not something a host assembles.
/// </summary>
public sealed class LinearGradientSpec
{
    internal LinearGradientSpec(Double angleDegrees, IReadOnlyList<GradientStop> stops)
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
/// <remarks>
/// The layer is filled in by the parser and read by whoever draws it, so the writing side of every
/// property stays inside the engine.
/// </remarks>
public sealed class BackgroundLayer
{
    /// <summary>Gets the image to draw, or null for a gradient layer.</summary>
    public String? Url { get; internal init; }

    /// <summary>Gets the gradient to draw, or null for an image layer.</summary>
    public LinearGradientSpec? Gradient { get; internal init; }

    public BackgroundLayerSizeKind SizeKind { get; internal init; }

    public BackgroundOffset SizeWidth { get; internal init; }

    public BackgroundOffset SizeHeight { get; internal init; }

    public BackgroundOffset PositionX { get; internal init; }

    public BackgroundOffset PositionY { get; internal init; }

    /// <summary>
    /// Gets whether the layer tiles along the inline axis. The initial value of
    /// <c>background-repeat</c> is <c>repeat</c>, so that is the default for both axes.
    /// </summary>
    /// <remarks>
    /// The two axes are separate because CSS Backgrounds 3 §2.6 makes them so: <c>repeat-x</c> is
    /// "repeat" in one and "no-repeat" in the other, and a single flag cannot say it. A one-pixel
    /// column that is repeated across a banner is the usual way a page draws a rule or a gradient
    /// band, and read as "does not repeat" it drew one pixel of it and nothing else.
    /// </remarks>
    public Boolean RepeatX { get; internal init; } = true;

    /// <summary>Gets whether the layer tiles along the block axis. See <see cref="RepeatX"/>.</summary>
    public Boolean RepeatY { get; internal init; } = true;
}

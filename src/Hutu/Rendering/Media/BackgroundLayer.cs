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
/// Where a colour stop stands along the gradient line. The two ways of saying it are kept apart rather
/// than folded together, because <c>calc(30% + 30px)</c> mixes them and the share has nothing to be a
/// share of until the box has settled the line's length.
/// </summary>
public readonly struct StopOffset
{
    internal StopOffset(Double pixels, Double percent)
    {
        Pixels = pixels;
        Percent = percent;
    }

    /// <summary>Gets the distance from the starting point of the line, measured along it.</summary>
    public Double Pixels { get; }

    /// <summary>Gets the share of the line's length the stop stands from its start, as a fraction.</summary>
    public Double Percent { get; }

    internal StopOffset Scaled(Double by) => new(Pixels * by, Percent * by);

    /// <summary>Resolves the offset against a gradient line of <paramref name="length"/> pixels.</summary>
    public Double Resolve(Double length) => Pixels + Percent * length;
}

/// <summary>
/// One colour of a gradient as its author wrote it, and where they stood it along the line. The
/// position is kept in the unit it was given in, because a length is measured along the gradient line
/// and a percentage is a share of it, and neither is known until the box is.
/// </summary>
public readonly struct ColorStop
{
    internal ColorStop(UInt32 color, StopOffset position, Boolean isStated)
    {
        Color = color;
        Position = position;
        IsStated = isStated;
    }

    /// <summary>Gets the colour as packed ARGB.</summary>
    public UInt32 Color { get; }

    /// <summary>Gets the position as written: a distance along the line, a share of it, or both.</summary>
    public StopOffset Position { get; }

    /// <summary>Gets whether a position was written at all, which the fixup of the list settles.</summary>
    public Boolean IsStated { get; }
}

/// <summary>
/// A linear gradient as declared in CSS. Like the stops it is made of, it is what the parser produces
/// and what a backend draws, not something a host assembles.
/// </summary>
public sealed class LinearGradientSpec
{
    internal LinearGradientSpec(Double angleDegrees, IReadOnlyList<ColorStop> stops)
    {
        AngleDegrees = angleDegrees;
        Stops = stops;
    }

    /// <summary>Gets the direction, in degrees clockwise from "to top".</summary>
    public Double AngleDegrees { get; }

    public IReadOnlyList<ColorStop> Stops { get; }

    /// <summary>
    /// Gets the length of the gradient line a box of these sides runs along: the projection of the box
    /// onto the direction the angle points, which is how the starting point and the ending point come
    /// to sit just far enough out that a stop at 0% and one at 100% land on two corners (CSS Images 3
    /// §3.1.1).
    /// </summary>
    public static Double LineLength(Double width, Double height, Double angleDegrees)
    {
        var radians = angleDegrees * Math.PI / 180.0;

        return Math.Abs(width * Math.Sin(radians)) + Math.Abs(height * Math.Cos(radians));
    }

    /// <summary>
    /// Gets the stops as fractions of a gradient line <paramref name="length"/> long, in the order a
    /// painter needs them, and with the two colours the line has come to at each end of the box beside
    /// them.
    /// </summary>
    /// <remarks>
    /// The fixup CSS Images 3 §3.4.3 asks for runs in its own order: the ends of the list take the ends
    /// of the line where the author left them, no stop stands behind the largest position before it,
    /// and a run of stops written without a position spaces itself equally between its neighbours.
    /// <para>
    /// The stops that come out are the ones for the window the box shows, which is not the whole line:
    /// a stop at -5% or 150% is on the line but off the box, and the colour the box starts and ends on
    /// is whatever the line has come to at those two points. Handing the painter the raw positions
    /// instead would lose the part of the blend that runs before the box, and clamping them to the ends
    /// would stop it dead.
    /// </para>
    /// </remarks>
    public IReadOnlyList<GradientStop> StopsAt(Double length)
    {
        var count = Stops.Count;

        if (count == 0)
        {
            return Array.Empty<GradientStop>();
        }

        if (length <= 0)
        {
            // A line with no length has no room to place anything, so every colour lands on it at once
            // and the box is the last one written.
            return new[] { new GradientStop(0, Stops[count - 1].Color), new GradientStop(1, Stops[count - 1].Color) };
        }

        var at = new Double[count];

        for (var index = 0; index < count; index++)
        {
            at[index] = Stops[index].IsStated ? Stops[index].Position.Resolve(length) : Double.NaN;
        }

        if (Double.IsNaN(at[0]))
        {
            at[0] = 0;
        }

        if (Double.IsNaN(at[count - 1]))
        {
            at[count - 1] = length;
        }

        var largest = at[0];

        for (var index = 1; index < count; index++)
        {
            if (Double.IsNaN(at[index]))
            {
                continue;
            }

            if (at[index] < largest)
            {
                at[index] = largest;
            }
            else
            {
                largest = at[index];
            }
        }

        // A run of stops left without a position divides the distance between the positioned stop
        // before it and the one after it equally, the way the first of them would sit one gap in.
        for (var from = 1; from < count; from++)
        {
            if (!Double.IsNaN(at[from]))
            {
                continue;
            }

            var to = from;

            while (to < count && Double.IsNaN(at[to]))
            {
                to++;
            }

            var before = at[from - 1];
            var after = to < count ? at[to] : length;
            var gaps = to - from + 1;

            for (var index = from; index < to; index++)
            {
                at[index] = before + (after - before) * (index - from + 1) / gaps;
            }

            from = to;
        }

        var visible = new List<GradientStop>(count + 2) { new(0, ColorAt(at, 0)) };

        for (var index = 0; index < count; index++)
        {
            var fraction = at[index] / length;

            // A stop standing on either edge of the box is the colour that edge already carries, so
            // listing it again would hand the painter the same position twice.
            if (fraction > 1e-9 && fraction < 1 - 1e-9)
            {
                visible.Add(new GradientStop(fraction, Stops[index].Color));
            }
        }

        visible.Add(new GradientStop(1, ColorAt(at, length)));

        return visible;
    }

    /// <summary>
    /// Gets the colour the line has come to at <paramref name="distance"/>: the first or the last
    /// stop's own colour outside the two ends of the list, and between two stops the blend of them in
    /// premultiplied space (CSS Images 3 §3.4.2). Two stops written at the same distance change over
    /// at that point rather than fading, so the later one is what stands there.
    /// </summary>
    private UInt32 ColorAt(Double[] at, Double distance)
    {
        var count = at.Length;

        if (distance >= at[count - 1])
        {
            return Stops[count - 1].Color;
        }

        if (distance < at[0])
        {
            return Stops[0].Color;
        }

        var index = 0;

        while (index < count - 1 && at[index + 1] <= distance)
        {
            index++;
        }

        var span = at[index + 1] - at[index];

        return span <= 0
            ? Stops[index + 1].Color
            : Blend(Stops[index].Color, Stops[index + 1].Color, (distance - at[index]) / span);
    }

    /// <summary>Blends two packed ARGB colours, with the alpha folded into the channels it rides on.</summary>
    private static UInt32 Blend(UInt32 from, UInt32 to, Double at)
    {
        static Double AlphaOf(UInt32 color) => ((color >> 24) & 0xFF) / 255.0;
        static Double Folded(UInt32 color, Int32 shift) => (((color >> shift) & 0xFF) / 255.0) * AlphaOf(color);

        var alpha = AlphaOf(from) + (AlphaOf(to) - AlphaOf(from)) * at;

        if (alpha <= 0)
        {
            return 0;
        }

        Byte Channel(Int32 shift)
        {
            var start = Folded(from, shift);
            var end = Folded(to, shift);

            return (Byte)Math.Clamp((Int32)Math.Round(Math.Clamp((start + (end - start) * at) / alpha, 0.0, 1.0) * 255), 0, 255);
        }

        var coverage = (Byte)Math.Clamp((Int32)Math.Round(alpha * 255), 0, 255);

        return (UInt32)((coverage << 24) | (Channel(16) << 16) | (Channel(8) << 8) | Channel(0));
    }
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

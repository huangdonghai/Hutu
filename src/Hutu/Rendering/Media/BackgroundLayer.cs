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
/// One component of a background position or size: the distance it adds and the share of the box it
/// stands on, which a <c>calc()</c> can mix.
/// </summary>
/// <remarks>
/// Neither part has a number until the box is known: a percentage is a share of the positioning area
/// rather than of the viewport, and the distance beside it is measured in (CSS Values 4 §10.8). The two
/// are kept apart for the same reason a colour stop's position is - see <see cref="StopOffset"/>.
/// <para>
/// It is built while the declaration is parsed, so a backend reads one rather than making it; that is
/// also why only <see cref="Resolve"/> is a host's to call.
/// </para>
/// </remarks>
public readonly struct BackgroundOffset
{
    internal BackgroundOffset(Double pixels, Double percent)
    {
        Pixels = pixels;
        Percent = percent;
    }

    /// <summary>Gets the distance the component adds, in pixels.</summary>
    public Double Pixels { get; }

    /// <summary>Gets the share of the axis the component is measured along, as a fraction.</summary>
    public Double Percent { get; }

    /// <summary>
    /// Gets whether the component states a distance at all. An axis of a stated size that the image is
    /// left to settle - <c>auto 40px</c> - is kept as zero, which is how the painter knows to take it from
    /// the intrinsic size instead.
    /// </summary>
    public Boolean IsStated => Pixels > 0 || Percent > 0;

    /// <summary>Resolves the component against the space it is measured in.</summary>
    public Double Resolve(Double space) => Pixels + Percent * space;
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
    internal ColorStop(UInt32 color, StopOffset position, Boolean isStated, Boolean isHint = false)
    {
        Color = color;
        Position = position;
        IsStated = isStated;
        IsHint = isHint;
    }

    /// <summary>Gets the colour as packed ARGB.</summary>
    public UInt32 Color { get; }

    /// <summary>Gets the position as written: a distance along the line, a share of it, or both.</summary>
    public StopOffset Position { get; }

    /// <summary>Gets whether a position was written at all, which the fixup of the list settles.</summary>
    public Boolean IsStated { get; }

    /// <summary>
    /// Gets whether this entry is an interpolation hint rather than a colour: a place where the ramp
    /// between the two colours on either side of it is pulled to their midpoint, which states no colour of
    /// its own (CSS Images 3 §3.4).
    /// </summary>
    public Boolean IsHint { get; }
}

/// <summary>How the colours of a gradient are laid out across the box it fills.</summary>
public enum GradientShape
{
    /// <summary>A line across the box, pointed by the angle the page wrote.</summary>
    Linear,

    /// <summary>
    /// A turn about a centre, the colours standing at the angle they were given (CSS Images 3 §3.4).
    /// </summary>
    Conic,

    /// <summary>
    /// A shape grown out from a centre, the colours standing at the radius they were given. The shape is
    /// a circle when the page says so or writes one radius, and an ellipse otherwise, and how far it
    /// reaches is one of the four keywords or the radius the page measured (CSS Images 3 §3.4).
    /// </summary>
    Radial,
}

/// <summary>How far out a radial gradient's ending shape reaches (CSS Images 3 §3.4).</summary>
public enum RadialEnding
{
    /// <summary>The radius the page wrote, which is the shape and needs no room to grow into.</summary>
    Explicit,

    /// <summary>The shape that just touches the nearest side of the box.</summary>
    ClosestSide,

    /// <summary>The shape that reaches the side farthest from its centre.</summary>
    FarthestSide,

    /// <summary>The shape that just touches the nearest corner.</summary>
    ClosestCorner,

    /// <summary>The shape that reaches the farthest corner, which is what a size left unwritten means.</summary>
    FarthestCorner,
}

/// <summary>
/// A gradient as declared in CSS. Like the stops it is made of, it is what the parser produces and what
/// a backend draws, not something a host assembles.
/// </summary>
/// <remarks>
/// The two shapes share everything but the geometry: the stop list, the fixup that spaces the ones left
/// without a position, and the repetition <c>repeating-*</c> asks for are the same reading of the same
/// grammar. What differs is what the stops are resolved <em>along</em> - a line projected onto the box
/// for a linear one, and one full turn for a conic one - which is why <see cref="LineLength"/> answers
/// for both and the painter only has to ask it once.
/// </remarks>
public sealed class GradientSpec
{
    internal GradientSpec(Double angleDegrees, IReadOnlyList<ColorStop> stops, Boolean repeats)
        : this(GradientShape.Linear, angleDegrees, stops, repeats, default, default)
    {
    }

    internal GradientSpec(
        GradientShape shape,
        Double angleDegrees,
        IReadOnlyList<ColorStop> stops,
        Boolean repeats,
        BackgroundOffset centerX,
        BackgroundOffset centerY)
    {
        Shape = shape;
        AngleDegrees = angleDegrees;
        Stops = stops;
        Repeats = repeats;
        CenterX = centerX;
        CenterY = centerY;
    }

    /// <summary>Gets whether the colours run along a line or around a centre.</summary>
    public GradientShape Shape { get; }

    /// <summary>
    /// Gets the angle the gradient is written at, clockwise from up: for a linear one the direction the
    /// line points, and for a conic one where its first colour starts.
    /// </summary>
    public Double AngleDegrees { get; }

    public IReadOnlyList<ColorStop> Stops { get; }

    /// <summary>
    /// Gets whether the list of stops repeats itself along the gradient, which is all
    /// <c>repeating-linear-gradient()</c> and <c>repeating-conic-gradient()</c> add to their
    /// non-repeating siblings (CSS Images 3 §3.3).
    /// </summary>
    public Boolean Repeats { get; }

    /// <summary>Gets where the sweep of a conic gradient turns around, along the box.</summary>
    public BackgroundOffset CenterX { get; }

    /// <summary>Gets where the sweep of a conic gradient turns around, down the box.</summary>
    public BackgroundOffset CenterY { get; }

    /// <summary>
    /// Gets whether a radial gradient's ending shape is a circle. The page says so, or writes one radius
    /// and no second; otherwise the shape is an ellipse whose two radii stand on their own axes.
    /// </summary>
    public Boolean IsCircle { get; internal init; }

    /// <summary>Gets how far out a radial gradient's ending shape reaches.</summary>
    public RadialEnding Ending { get; internal init; } = RadialEnding.FarthestCorner;

    /// <summary>Gets the radius the page wrote along the box's inline axis, for an explicit size.</summary>
    public BackgroundOffset RadiusX { get; internal init; }

    /// <summary>Gets the radius the page wrote down the box, for an explicit size on an ellipse.</summary>
    public BackgroundOffset RadiusY { get; internal init; }

    /// <summary>
    /// Gets the space the colours between the stops are mixed in, which the head of the gradient may name
    /// with <c>in</c> (CSS Color 4 §13.2). The one the painter blends in is what a head that says nothing
    /// asks for.
    /// </summary>
    internal ColorSpace InterpolationSpace { get; init; } = ColorSpace.Srgb;

    /// <summary>
    /// Gets which way round the circle a hue of this ramp is taken, beside the space that has one
    /// (CSS Color 4 §13.5).
    /// </summary>
    internal HueMethod InterpolationHue { get; init; } = HueMethod.Shorter;

    /// <summary>
    /// Gets the ending shape of a radial gradient in a box of these sides: the two radii the colour stops
    /// are measured against, and the shape the 100% stop is drawn on (CSS Images 3 §3.4).
    /// </summary>
    /// <remarks>
    /// The centre the page placed may stand outside the box, which is why the distances to the sides are
    /// absolute rather than the plain gaps. A corner keyword keeps the ratio of the farthest-side ellipse
    /// and scales it until it just reaches the corner the keyword names - the shape is an ellipse of the
    /// same proportions either way, which is the whole difference between the four keywords and two
    /// numbers.
    /// </remarks>
    public (Double RadiusX, Double RadiusY) EndingShape(Double width, Double height)
    {
        var cx = CenterX.Resolve(width);
        var cy = CenterY.Resolve(height);

        if (Ending == RadialEnding.Explicit)
        {
            var stated = Math.Max(0, RadiusX.Resolve(width));

            return (stated, IsCircle ? stated : Math.Max(0, RadiusY.Resolve(height)));
        }

        var left = Math.Abs(cx);
        var right = Math.Abs(width - cx);
        var top = Math.Abs(cy);
        var bottom = Math.Abs(height - cy);

        if (IsCircle)
        {
            var nearestSide = Math.Min(Math.Min(left, right), Math.Min(top, bottom));
            var furthestSide = Math.Max(Math.Max(left, right), Math.Max(top, bottom));

            var radius = Ending switch
            {
                RadialEnding.ClosestSide => nearestSide,
                RadialEnding.FarthestSide => furthestSide,
                RadialEnding.ClosestCorner => Corner(left, right, top, bottom, Math.Min),
                _ => Corner(left, right, top, bottom, Math.Max),
            };

            return (radius, radius);
        }

        var x = Ending == RadialEnding.ClosestSide ? Math.Min(left, right) : Math.Max(left, right);
        var y = Ending == RadialEnding.ClosestSide ? Math.Min(top, bottom) : Math.Max(top, bottom);

        if (Ending is RadialEnding.ClosestSide or RadialEnding.FarthestSide)
        {
            return (x, y);
        }

        var farX = Math.Max(left, right);
        var farY = Math.Max(top, bottom);

        if (farX <= 0 || farY <= 0)
        {
            // The centre stands on one of the ellipse's own axes, so no scaling of the degenerate shape
            // reaches a corner: the radius that is left is the one the page can mean.
            return (farX, farY);
        }

        var reach = Double.NaN;

        foreach (var (cornerX, cornerY) in new[]
        {
            (0.0, 0.0), (width, 0.0), (0.0, height), (width, height),
        })
        {
            var along = (cornerX - cx) / farX;
            var across = (cornerY - cy) / farY;
            var at = Math.Sqrt(along * along + across * across);

            reach = Double.IsNaN(reach)
                ? at
                : Ending == RadialEnding.ClosestCorner
                    ? Math.Min(reach, at)
                    : Math.Max(reach, at);
        }

        return (farX * reach, farY * reach);
    }

    /// <summary>
    /// Gets the ending shape of a radial gradient in a box of these sides, or nothing for the shapes that
    /// have no radius - so a painter builds one command for every shape and asks the question once.
    /// </summary>
    public (Double RadiusX, Double RadiusY) EndingRadii(Double width, Double height) =>
        Shape == GradientShape.Radial ? EndingShape(width, height) : (0, 0);

    /// <summary>Gets the distance from the centre to the nearest or the farthest corner of the box.</summary>
    private static Double Corner(
        Double left,
        Double right,
        Double top,
        Double bottom,
        Func<Double, Double, Double> which)
    {
        var across = Math.Sqrt(left * left + top * top);
        var other = Math.Sqrt(right * right + top * top);
        var below = Math.Sqrt(left * left + bottom * bottom);
        var opposite = Math.Sqrt(right * right + bottom * bottom);

        return which(which(across, other), which(below, opposite));
    }

    /// <summary>
    /// Gets the length the stops of this gradient are stood along: the projection of a box of these
    /// sides onto the line a linear gradient runs along, the radius a radial one ends on, or the one turn
    /// a conic one sweeps.
    /// </summary>
    /// <remarks>
    /// A radial gradient's stops are measured along the horizontal radius, which is the one axis a length
    /// has a plain meaning on: a percentage is a fraction of the ending shape and so needs no axis, and a
    /// circle - the case where a length is written at all - has the two radii equal anyway.
    /// </remarks>
    public Double LineLength(Double width, Double height) => Shape switch
    {
        GradientShape.Conic => 360,
        GradientShape.Radial => EndingShape(width, height).RadiusX,
        _ => LineLength(width, height, AngleDegrees),
    };

    /// <summary>
    /// Gets the length of the gradient line a box of these sides runs along: the projection of the box
    /// onto the direction the angle points, which is how the starting point and the ending point come
    /// to sit just far enough out that a stop at 0% and one at 100% land on two corners (CSS Images 3
    /// §3.1.1).
    /// </summary>
    public static Double LineLength(Double width, Double height, Double angleDegrees)
    {
        var radians = angleDegrees * Math.PI / 180.0;
        var along = Math.Sin(radians);
        var across = Math.Cos(radians);

        // The trigonometry of a quarter turn lands on nothing rather than on 1.2e-16, and a line
        // pointed along an axis is exactly the side it runs along: a stop at 100% of it has to sit on
        // the edge of the box, not a hair beyond it.
        if (Math.Abs(along) < 1e-12)
        {
            along = 0;
        }

        if (Math.Abs(across) < 1e-12)
        {
            across = 0;
        }

        return Math.Abs(width * along) + Math.Abs(height * across);
    }

    /// <summary>
    /// Gets the stops as fractions of a gradient line <paramref name="length"/> long, in the order a
    /// painter needs them: the list first placed by the fixup it is written for, then repeated if the
    /// notation asks, then kept to what the box of that line shows, and then sampled - only when the ramp
    /// is mixed in a space other than the one the painter blends in - at the places the box draws it.
    /// </summary>
    /// <param name="length">
    /// The distance the stops are measured along, which is what a position of theirs is a fraction of.
    /// </param>
    /// <param name="pixels">
    /// How far the line runs in what the box is drawn with, which decides how finely a ramp has to be
    /// told before its steps show.
    /// </param>
    public IReadOnlyList<GradientStop> StopsAt(Double length, Double pixels)
    {
        if (Stops.Count == 0)
        {
            return Array.Empty<GradientStop>();
        }

        if (length <= 0)
        {
            // A line with no length has no room to place anything, so every colour lands on it at once
            // and the box is the last one written.
            return new[] { new GradientStop(0, Stops[Stops.Count - 1].Color), new GradientStop(1, Stops[Stops.Count - 1].Color) };
        }

        var placed = Placed(length);

        if (placed.Count == 0)
        {
            // Nothing in the list carried a colour - every argument was a hint - so there is no ramp to
            // draw at all.
            return Array.Empty<GradientStop>();
        }

        return Sampled(Windowed(Repeats ? Repeated(placed, length) : placed, length), pixels);
    }

    /// <summary>
    /// Gets the ramp the painter blends, told at every point it is drawn across: the shader only knows how
    /// to mix the eight bits it is handed, so a ramp that is mixed in another space has to say what it
    /// comes to between its own stops (CSS Color 4 §13.2). A step every other pixel of the box is too fine
    /// for the eye to pick out of the blend, and the count stays a power of two so that a stop the page
    /// stood on a plain fraction of the line is still stood on one.
    /// </summary>
    private List<GradientStop> Sampled(List<GradientStop> windowed, Double pixels)
    {
        if (InterpolationSpace == ColorSpace.Srgb || windowed.Count < 2)
        {
            return windowed;
        }

        var mixed = new List<GradientStop>(windowed.Count * 2);

        for (var index = 0; index < windowed.Count; index++)
        {
            var stop = windowed[index];
            var next = index + 1 < windowed.Count ? windowed[index + 1] : default;
            var span = next.Offset - stop.Offset;

            mixed.Add(stop);

            if (span <= 0)
            {
                continue;
            }

            var steps = 1;

            while (steps < 256 && pixels * span / steps > 2)
            {
                steps *= 2;
            }

            for (var at = 1; at < steps; at++)
            {
                var of = (Double)at / steps;

                mixed.Add(new GradientStop(
                    stop.Offset + span * of,
                    ColorMixing.Mix(stop.Color, next.Color, of, InterpolationSpace, InterpolationHue)));
            }
        }

        return mixed;
    }

    /// <summary>
    /// Gets where each colour of the list stands on a line <paramref name="length"/> long, after the
    /// fixup CSS Images 3 §3.4.3 asks for: the ends of the list take the ends of the line where the
    /// author left them, no stop stands behind the largest position before it, and a run written
    /// without a position spaces itself equally between its neighbours.
    /// </summary>
    private List<(Double At, UInt32 Color)> Placed(Double length)
    {
        // A hint is no part of this fixup: it stands between two colours and says nothing about where
        // either of them is. It is taken out first, the colours are laid down without it, and it comes
        // back as the colour the ramp reaches at the place it wrote (CSS Images 3 §3.4).
        var hints = new List<(Int32 Between, Double At)>();
        var stops = new List<ColorStop>(Stops.Count);

        for (var index = 0; index < Stops.Count; index++)
        {
            if (Stops[index].IsHint)
            {
                hints.Add((stops.Count, Stops[index].Position.Resolve(length)));
            }
            else
            {
                stops.Add(Stops[index]);
            }
        }

        var count = stops.Count;
        var at = new Double[count];

        for (var index = 0; index < count; index++)
        {
            at[index] = stops[index].IsStated ? stops[index].Position.Resolve(length) : Double.NaN;
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

        var placed = new List<(Double At, UInt32 Color)>(count + hints.Count);

        for (var index = 0; index < count; index++)
        {
            placed.Add((at[index], stops[index].Color));
        }

        // A hint is clamped into the stretch of the ramp it was written between - the only place its
        // midpoint colour can stand - and is inserted there, so the order of the list stays the order the
        // page wrote and two colours on one place keep the second of them on top.
        foreach (var (between, where) in hints)
        {
            if (between < 1 || between >= count)
            {
                continue;
            }

            var left = placed[between - 1];
            var right = placed[between];

            placed.Insert(
                between,
                (Math.Clamp(where, left.At, right.At),
                    ColorMixing.Mix(left.Color, right.Color, 0.5, InterpolationSpace, InterpolationHue)));
        }

        return placed;
    }

    /// <summary>
    /// Lays the list out again over the whole line, shifted by multiples of the distance between its
    /// first and last colour stop in both directions (CSS Images 3 §3.3). The last stop of one group and
    /// the first of the next always coincide, which is what makes the pattern change sharply at each
    /// boundary when the two are not the same colour.
    /// </summary>
    private List<(Double At, UInt32 Color)> Repeated(List<(Double At, UInt32 Color)> placed, Double length)
    {
        var period = placed[placed.Count - 1].At - placed[0].At;

        if (period <= 0)
        {
            // The group has no width to repeat, so the line never gets past the last colour written.
            return new List<(Double, UInt32)> { (0, placed[placed.Count - 1].Color), (length, placed[placed.Count - 1].Color) };
        }

        if (period < 1)
        {
            // A group narrower than a device pixel cannot be drawn as a pattern; the spec's answer is
            // the gradient's own average colour, and one group is all of it that counts.
            var flat = Average(placed);

            return new List<(Double, UInt32)> { (0, flat), (length, flat) };
        }

        var repeated = new List<(Double At, UInt32 Color)>(placed.Count * (Int32)Math.Ceiling(length / period) + placed.Count);

        // The groups wanted are the ones with a stop of their own on the line: the last group whose
        // first stop can still reach it, and the first whose last stop has not gone past it.
        var opening = (Int32)Math.Floor(-placed[placed.Count - 1].At / period);
        var closing = (Int32)Math.Ceiling((length - placed[0].At) / period);

        for (var group = opening; group <= closing; group++)
        {
            foreach (var (at, color) in placed)
            {
                var stood = at + group * period;

                if (stood >= 0 && stood <= length)
                {
                    repeated.Add((stood, color));
                }
            }
        }

        // The groups are laid down in order and each one's last stop stands where the next one's first
        // does, so the list comes out ascending without a sort - which is as well, since the order of
        // two stops at one distance is the sharp change the pattern is made of.
        return repeated;
    }

    /// <summary>
    /// Gets the colour a line painted with these stops averages to: each stretch of the blend counted
    /// as long as it runs, which for a linear one is the mid-point of its two ends (CSS Images 3 §3.3,
    /// reading §4.1's average colour of an image).
    /// </summary>
    private static UInt32 Average(List<(Double At, UInt32 Color)> placed)
    {
        var red = 0.0;
        var green = 0.0;
        var blue = 0.0;
        var coverage = 0.0;
        var runs = 0.0;

        for (var index = 0; index < placed.Count - 1; index++)
        {
            var span = placed[index + 1].At - placed[index].At;

            if (span <= 0)
            {
                continue;
            }

            var middle = Blend(placed[index].Color, placed[index + 1].Color, 0.5);
            var alpha = ((middle >> 24) & 0xFF) / 255.0;

            red += (((middle >> 16) & 0xFF) / 255.0) * alpha * span;
            green += (((middle >> 8) & 0xFF) / 255.0) * alpha * span;
            blue += ((middle & 0xFF) / 255.0) * alpha * span;
            coverage += alpha * span;
            runs += span;
        }

        if (runs <= 0)
        {
            return placed[placed.Count - 1].Color;
        }

        if (coverage <= 0)
        {
            return 0;
        }

        Byte Channel(Double folded) =>
            (Byte)Math.Clamp((Int32)Math.Round(Math.Clamp(folded / coverage, 0.0, 1.0) * 255), 0, 255);

        return (UInt32)(((Byte)Math.Clamp((Int32)Math.Round(coverage / runs * 255), 0, 255) << 24) |
            (Channel(red) << 16) | (Channel(green) << 8) | Channel(blue));
    }

    /// <summary>
    /// Keeps the part of the list the box shows: every stop that stands on it, with the colour the line
    /// has come to at each edge where the stops do not reach one. A stop at -5% or 150% is on the line
    /// but off the box, and the blend that runs through it is what the box opens or closes on -
    /// clamping the position instead would stop the blend dead at the edge.
    /// </summary>
    private static List<GradientStop> Windowed(List<(Double At, UInt32 Color)> placed, Double length)
    {
        var last = placed.Count - 1;
        var visible = new List<GradientStop>(placed.Count + 2);

        // Two stops of one colour at one distance say one thing, which is what the boundary between two
        // groups of a repeating gradient comes to when the pattern ends and begins alike.
        void Add(Double fraction, UInt32 color)
        {
            if (visible.Count == 0 || visible[^1].Offset != fraction || visible[^1].Color != color)
            {
                visible.Add(new GradientStop(fraction, color));
            }
        }

        if (placed[0].At > 0)
        {
            Add(0, placed[0].Color);
        }
        else if (placed[0].At < 0)
        {
            Add(0, ColorAt(placed, 0));
        }

        for (var index = 0; index <= last; index++)
        {
            var (at, color) = placed[index];

            if (at >= 0 && at <= length)
            {
                Add(at / length, color);
            }
        }

        if (placed[last].At < length)
        {
            Add(1, placed[last].Color);
        }
        else if (placed[last].At > length)
        {
            Add(1, ColorAt(placed, length));
        }

        return visible;
    }

    /// <summary>
    /// Gets the colour the line has come to at <paramref name="distance"/>: the first or the last
    /// stop's own colour outside the two ends of the list, and between two stops the blend of them in
    /// premultiplied space (CSS Images 3 §3.4.2). Two stops written at the same distance change over
    /// at that point rather than fading, so the later one is what stands there.
    /// </summary>
    private static UInt32 ColorAt(List<(Double At, UInt32 Color)> placed, Double distance)
    {
        var count = placed.Count;

        if (distance >= placed[count - 1].At)
        {
            return placed[count - 1].Color;
        }

        if (distance < placed[0].At)
        {
            return placed[0].Color;
        }

        var index = 0;

        while (index < count - 1 && placed[index + 1].At <= distance)
        {
            index++;
        }

        var span = placed[index + 1].At - placed[index].At;

        return span <= 0
            ? placed[index + 1].Color
            : Blend(placed[index].Color, placed[index + 1].Color, (distance - placed[index].At) / span);
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
    public GradientSpec? Gradient { get; internal init; }

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

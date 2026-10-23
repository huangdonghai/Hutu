using AngleSharp.Css;
using AngleSharp.Css.Values;
using Hutu.Rendering.Layout;
using System;
using System.Collections.Generic;
using System.Globalization;

namespace Hutu.Rendering.Media;

/// <summary>How one axis of a border image's edges fills the room it is given (CSS Backgrounds 3 §6.4).</summary>
public enum BorderImageRepeat
{
    /// <summary>One copy of the edge, drawn to the room it has, whatever its own size.</summary>
    Stretch,

    /// <summary>Copies at their own size, the last one cut off by the end of the room.</summary>
    Repeat,

    /// <summary>Copies resized so that a whole number of them fits exactly.</summary>
    Round,

    /// <summary>Copies at their own size, with equal space between them.</summary>
    Space,
}

/// <summary>
/// One side of the source a border image is cut from: a distance into the picture, in the picture's own
/// pixels or as a share of it (CSS Backgrounds 3 §6.2).
/// </summary>
public readonly struct BorderImageSlice
{
    internal BorderImageSlice(Double value, Boolean percent)
    {
        Value = value;
        IsPercent = percent;
    }

    /// <summary>Gets the stated number.</summary>
    public Double Value { get; }

    /// <summary>Gets whether the number is a share of the picture's side rather than a length in it.</summary>
    public Boolean IsPercent { get; }

    /// <summary>Gets the distance into a side <paramref name="length"/> long of the picture.</summary>
    public Double Resolve(Double length) => IsPercent ? Value / 100.0 * length : Value;
}

/// <summary>
/// One side of the room a border image's edges are drawn into: the border-image-width, which may be a
/// length, a share of the area, a multiple of the border's own width, or left to that width.
/// </summary>
public readonly struct BorderImageExtent
{
    /// <summary>The keyword <c>auto</c>: the border's own width on that side.</summary>
    public static readonly BorderImageExtent Auto = new(0, BorderImageExtentKind.Auto, false);

    internal BorderImageExtent(Double value, BorderImageExtentKind kind, Boolean percent)
    {
        Value = value;
        Kind = kind;
        IsPercent = percent;
    }

    /// <summary>Gets the stated number.</summary>
    public Double Value { get; }

    /// <summary>Gets what the number means.</summary>
    public BorderImageExtentKind Kind { get; }

    /// <summary>Gets whether the number is a share of the area's side.</summary>
    public Boolean IsPercent { get; }

    /// <summary>
    /// Gets the room this side takes in a border image area of <paramref name="basis"/> on this axis, in a
    /// box whose border on this side is <paramref name="border"/> wide.
    /// </summary>
    public Double Resolve(Double basis, Double border) => Kind switch
    {
        BorderImageExtentKind.Auto => border,
        BorderImageExtentKind.Number => Value * border,
        BorderImageExtentKind.Percent => Value / 100.0 * basis,
        _ => Value,
    };
}

/// <summary>What the number of a <see cref="BorderImageExtent"/> means.</summary>
public enum BorderImageExtentKind
{
    /// <summary>The border's own width on that side.</summary>
    Auto,

    /// <summary>A multiple of the border's width on that side.</summary>
    Number,

    /// <summary>A length.</summary>
    Length,

    /// <summary>A share of the border image area's side.</summary>
    Percent,
}

/// <summary>
/// A border image: the picture a box paints its border area with, cut into nine regions and tiled into
/// them (CSS Backgrounds 3 §6). The picture is a file or a gradient - a gradient is as good an image as
/// any other, and its size is the border image area it is drawn into.
/// </summary>
public sealed class BorderImageSpec
{
    internal BorderImageSpec(
        String? url,
        GradientSpec? gradient,
        BorderImageSlice[] slices,
        Boolean fill,
        BorderImageExtent[] widths,
        BorderImageExtent[] outsets,
        BorderImageRepeat repeatX,
        BorderImageRepeat repeatY)
    {
        Url = url;
        Gradient = gradient;
        Slices = slices;
        Fill = fill;
        Widths = widths;
        Outsets = outsets;
        RepeatX = repeatX;
        RepeatY = repeatY;
    }

    /// <summary>Gets the file the picture is read from, or null when the source is a gradient.</summary>
    public String? Url { get; }

    /// <summary>Gets the gradient the picture is drawn as, or null when the source is a file.</summary>
    public GradientSpec? Gradient { get; }

    /// <summary>Gets how far into the picture each side is cut, in the order top, right, bottom, left.</summary>
    public IReadOnlyList<BorderImageSlice> Slices { get; }

    /// <summary>Gets whether the middle of the picture is painted too, which is what <c>fill</c> asks.</summary>
    public Boolean Fill { get; }

    /// <summary>Gets how wide each edge is drawn, in the order top, right, bottom, left.</summary>
    public IReadOnlyList<BorderImageExtent> Widths { get; }

    /// <summary>Gets how far the area reaches past the border box, top, right, bottom, left.</summary>
    public IReadOnlyList<BorderImageExtent> Outsets { get; }

    /// <summary>Gets how the edges that run across the box fill their room.</summary>
    public BorderImageRepeat RepeatX { get; }

    /// <summary>Gets how the edges that run down the box fill their room.</summary>
    public BorderImageRepeat RepeatY { get; }

    /// <summary>
    /// Gets the border image area of a box: its border box grown by the outsets (CSS Backgrounds 3 §6.1).
    /// The area is where the picture is painted; it takes no room in the layout, so a box that outgrows
    /// its border box paints over what stands around it.
    /// </summary>
    public Rect Area(Rect borderBox, Thickness border) => new(
        borderBox.X - Outsets[3].Resolve(borderBox.Width, border.Left),
        borderBox.Y - Outsets[0].Resolve(borderBox.Height, border.Top),
        borderBox.Width + Outsets[3].Resolve(borderBox.Width, border.Left) + Outsets[1].Resolve(borderBox.Width, border.Right),
        borderBox.Height + Outsets[0].Resolve(borderBox.Height, border.Top) + Outsets[2].Resolve(borderBox.Height, border.Bottom));

    /// <summary>Gets how wide each edge is drawn inside an area of this size, top, right, bottom, left.</summary>
    public Double[] DrawnWidths(Rect area, Thickness border) => new[]
    {
        Widths[0].Resolve(area.Height, border.Top),
        Widths[1].Resolve(area.Width, border.Right),
        Widths[2].Resolve(area.Height, border.Bottom),
        Widths[3].Resolve(area.Width, border.Left),
    };
}

/// <summary>Reads the border image a box was given (CSS Backgrounds 3 §6.2 to §6.4).</summary>
internal static class BorderImageParser
{
    /// <summary>
    /// Reads the four longhands that make a border image, or null where the box has none: a source that
    /// is <c>none</c>, a file the engine cannot read, or a declaration it does not understand.
    /// </summary>
    public static BorderImageSpec? Parse(
        String? source,
        String? slices,
        String? widths,
        String? outsets,
        String? repeat,
        Thickness border,
        IRenderDimensions dimensions,
        Double fontSize,
        Func<String, String?>? resolveUrl)
    {
        var image = Image(source, dimensions, fontSize, resolveUrl);

        if (image is not { } picture)
        {
            return null;
        }

        var cut = Slices(slices, dimensions, fontSize);
        var room = Extents(widths, dimensions, fontSize, auto: true);
        var outset = Extents(outsets, dimensions, fontSize, auto: false);
        var (repeatX, repeatY) = Repeats(repeat);

        return new BorderImageSpec(
            picture.Url,
            picture.Gradient,
            cut.Slices,
            cut.Fill,
            room,
            outset,
            repeatX,
            repeatY);
    }

    /// <summary>Reads the source: a gradient the engine can draw, or a file it can fetch.</summary>
    private static (String? Url, GradientSpec? Gradient)? Image(
        String? source,
        IRenderDimensions dimensions,
        Double fontSize,
        Func<String, String?>? resolveUrl)
    {
        if (String.IsNullOrWhiteSpace(source) || source.Trim().Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var text = source.Trim();

        if (BackgroundLayerParser.ParseGradientImage(text, dimensions, fontSize) is { } gradient)
        {
            return (null, gradient);
        }

        var urls = CssUrlReferences.Extract(text);

        if (urls.Count == 0)
        {
            return null;
        }

        return (resolveUrl?.Invoke(urls[0]) ?? urls[0], null);
    }

    /// <summary>Reads the slices, in the order top, right, bottom, left, and whether the middle is filled.</summary>
    private static (BorderImageSlice[] Slices, Boolean Fill) Slices(
        String? value,
        IRenderDimensions dimensions,
        Double fontSize)
    {
        var slices = new List<BorderImageSlice>();
        var fill = false;

        foreach (var word in CssValueList.SplitWhitespace(value))
        {
            if (word.Equals("fill", StringComparison.OrdinalIgnoreCase))
            {
                fill = true;
                continue;
            }

            if (Number(word, out var number, out var percent))
            {
                slices.Add(new BorderImageSlice(number, percent));
            }
            else if (Length(word, dimensions, fontSize) is { } length)
            {
                slices.Add(new BorderImageSlice(length, false));
            }
        }

        return (Edges(slices, new BorderImageSlice(0, false)), fill);
    }

    /// <summary>Reads the widths or the outsets, in the order top, right, bottom, left.</summary>
    private static BorderImageExtent[] Extents(
        String? value,
        IRenderDimensions dimensions,
        Double fontSize,
        Boolean auto)
    {
        var stated = new List<BorderImageExtent>();
        var none = auto ? BorderImageExtent.Auto : new BorderImageExtent(0, BorderImageExtentKind.Length, false);

        foreach (var word in CssValueList.SplitWhitespace(value))
        {
            if (auto && word.Equals("auto", StringComparison.OrdinalIgnoreCase))
            {
                stated.Add(BorderImageExtent.Auto);
            }
            else if (Number(word, out var number, out var percent))
            {
                stated.Add(new BorderImageExtent(
                    number,
                    percent ? BorderImageExtentKind.Percent : BorderImageExtentKind.Number,
                    percent));
            }
            else if (Length(word, dimensions, fontSize) is { } length)
            {
                stated.Add(new BorderImageExtent(length, BorderImageExtentKind.Length, false));
            }
        }

        return Edges(stated, none);
    }

    /// <summary>Reads the one or two repeat keywords: across the box first, then down it.</summary>
    private static (BorderImageRepeat X, BorderImageRepeat Y) Repeats(String? value)
    {
        var stated = new List<BorderImageRepeat>();

        foreach (var word in CssValueList.SplitWhitespace(value))
        {
            var repeat = word.ToLowerInvariant() switch
            {
                "stretch" => BorderImageRepeat.Stretch,
                "repeat" => BorderImageRepeat.Repeat,
                "round" => BorderImageRepeat.Round,
                "space" => BorderImageRepeat.Space,
                _ => (BorderImageRepeat?)null,
            };

            if (repeat is { } found)
            {
                stated.Add(found);
            }
        }

        return stated.Count switch
        {
            0 => (BorderImageRepeat.Stretch, BorderImageRepeat.Stretch),
            1 => (stated[0], stated[0]),
            _ => (stated[0], stated[1]),
        };
    }

    /// <summary>
    /// Lays one to four values out as four sides: one stands for all of them, two for the axes, three for
    /// top, the axis and bottom, and four for top, right, bottom, left in that order (CSS Backgrounds 3 §6.2).
    /// </summary>
    private static T[] Edges<T>(List<T> stated, T none)
    {
        var edges = new[] { none, none, none, none };

        if (stated.Count == 0)
        {
            return edges;
        }

        if (stated.Count > 4)
        {
            stated.RemoveRange(4, stated.Count - 4);
        }

        return stated.Count switch
        {
            1 => new[] { stated[0], stated[0], stated[0], stated[0] },
            2 => new[] { stated[0], stated[1], stated[0], stated[1] },
            3 => new[] { stated[0], stated[1], stated[2], stated[1] },
            _ => new[] { stated[0], stated[1], stated[2], stated[3] },
        };
    }

    /// <summary>Reads a plain number or a percentage, which a slice is written as.</summary>
    private static Boolean Number(String word, out Double value, out Boolean percent)
    {
        value = 0;
        percent = word.EndsWith('%');

        var digits = percent ? word[..^1] : word;

        return Double.TryParse(digits, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    /// <summary>Reads a length - <c>12pt</c> and the like - as pixels, or null where the word is none.</summary>
    private static Double? Length(String word, IRenderDimensions dimensions, Double fontSize)
    {
        if (!CssLengthValue.TryParse(word, out var length) || length.Type == CssLengthValue.Unit.Percent)
        {
            return null;
        }

        try
        {
            return LengthReader.ToPixel(length, RenderMode.Horizontal, dimensions, fontSize);
        }
        catch (Exception)
        {
            return null;
        }
    }
}

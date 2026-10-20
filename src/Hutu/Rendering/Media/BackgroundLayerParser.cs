using AngleSharp.Css;
using AngleSharp.Css.Values;
using System;
using System.Collections.Generic;
using System.Globalization;

namespace Hutu.Rendering.Media;

/// <summary>
/// Parses the background longhands into layers.
/// </summary>
/// <remarks>
/// The values are parsed from text because AngleSharp.Css models them with internal types that
/// cannot be read from the outside. The four longhands are zipped by index, with the last value
/// repeated for the remaining layers, which is how the shorthand expands them.
/// <para>
/// Anything that is not understood is skipped rather than thrown on: an unsupported gradient means
/// one layer is not painted, never that the page fails. <c>radial-gradient</c> and
/// <c>conic-gradient</c> are deliberately not supported.
/// </para>
/// </remarks>
internal static class BackgroundLayerParser
{
    /// <param name="resolveUrl">
    /// Resolves a reference to an absolute URL. Background images are declared relative to the
    /// element that carries them, and the image cache is keyed by absolute URL, so an unresolved
    /// reference would never be found again at paint time.
    /// </param>
    public static IReadOnlyList<BackgroundLayer> Parse(
        String? images,
        String? positions,
        String? sizes,
        String? repeats,
        IRenderDimensions dimensions,
        Double fontSize,
        Func<String, String?>? resolveUrl = null)
    {
        var imageList = CssValueList.SplitTopLevel(images);

        if (imageList.Count == 0)
        {
            return Array.Empty<BackgroundLayer>();
        }

        var positionList = CssValueList.SplitTopLevel(positions);
        var sizeList = CssValueList.SplitTopLevel(sizes);
        var repeatList = CssValueList.SplitTopLevel(repeats);

        var layers = new List<BackgroundLayer>(imageList.Count);

        for (var index = 0; index < imageList.Count; index++)
        {
            var image = imageList[index];

            if (image.Equals("none", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var gradient = ParseGradient(image);
            var urls = gradient is null ? CssUrlReferences.Extract(image) : new List<String>();

            if (gradient is null && urls.Count == 0)
            {
                continue;
            }

            var position = ParsePosition(At(positionList, index), dimensions, fontSize);
            var size = ParseSize(At(sizeList, index), dimensions, fontSize);

            var repeat = RepeatOf(At(repeatList, index));

            layers.Add(new BackgroundLayer
            {
                Url = urls.Count > 0 ? resolveUrl?.Invoke(urls[0]) ?? urls[0] : null,
                Gradient = gradient,
                SizeKind = size.Kind,
                SizeWidth = size.Width,
                SizeHeight = size.Height,
                PositionX = position.X,
                PositionY = position.Y,
                RepeatX = repeat.X,
                RepeatY = repeat.Y,
            });
        }

        return layers;
    }

    /// <summary>
    /// Reads one layer's <c>background-repeat</c> as the two axes it states.
    /// </summary>
    /// <remarks>
    /// CSS Backgrounds 3 §2.6: the value names the inline axis first and the block axis second, a
    /// single name stands for both (<c>repeat-x</c> and <c>repeat-y</c> being the two shorthands for
    /// "repeat in one, no-repeat in the other"), and <c>space</c> and <c>round</c> are read as
    /// <c>repeat</c> here - a tile that is spread out or scaled to fit a whole number of times is a
    /// refinement of the same tiling, and reading them as <c>no-repeat</c> would drop the picture.
    /// </remarks>
    private static (Boolean X, Boolean Y) RepeatOf(String value)
    {
        var parts = CssValueList.SplitWhitespace(value);

        if (parts.Count == 0)
        {
            return (true, true);
        }

        Boolean Tiles(String part) => !part.Equals("no-repeat", StringComparison.OrdinalIgnoreCase);

        return parts.Count == 1
            ? parts[0].Trim().ToLowerInvariant() switch
            {
                "repeat-x" => (true, false),
                "repeat-y" => (false, true),
                "no-repeat" => (false, false),
                _ => (true, true),
            }
            : (Tiles(parts[0]), Tiles(parts[1]));
    }

    private static String At(List<String> values, Int32 index) =>
        values.Count == 0 ? String.Empty : values[Math.Min(index, values.Count - 1)];

    /// <summary>
    /// The per-layer value of an axis-pairing longhand such as <c>background-repeat</c> or
    /// <c>background-position</c>. AngleSharp expands a <c>background</c> shorthand into the
    /// <c>-x</c>/<c>-y</c> sub-longhands but leaves the aggregate empty, so when the aggregate is blank
    /// the pair is rebuilt from its axes ("x y" per layer, zipped by index) - otherwise a
    /// <c>… no-repeat</c> background tiles and one with a position lands at the top left.
    /// </summary>
    public static String? CombineAxes(String? aggregate, String? x, String? y)
    {
        if (!String.IsNullOrWhiteSpace(aggregate))
        {
            return aggregate;
        }

        var xs = CssValueList.SplitTopLevel(x);
        var ys = CssValueList.SplitTopLevel(y);

        if (xs.Count == 0 && ys.Count == 0)
        {
            return aggregate;
        }

        var count = Math.Max(xs.Count, ys.Count);
        var layers = new List<String>(count);

        for (var i = 0; i < count; i++)
        {
            layers.Add(($"{At(xs, i)} {At(ys, i)}").Trim());
        }

        return String.Join(", ", layers);
    }

    // --- position -------------------------------------------------------

    private static (BackgroundOffset X, BackgroundOffset Y) ParsePosition(String value, IRenderDimensions dimensions, Double fontSize)
    {
        var parts = CssValueList.SplitWhitespace(value);

        if (parts.Count == 0)
        {
            return (default, default);
        }

        if (parts.Count == 1)
        {
            var single = Component(parts[0], dimensions, fontSize);

            // A lone vertical keyword leaves the horizontal axis centred, and vice versa.
            return IsVertical(parts[0])
                ? (new BackgroundOffset(0.5, true), single)
                : (single, new BackgroundOffset(0.5, true));
        }

        var first = Component(parts[0], dimensions, fontSize);
        var second = Component(parts[1], dimensions, fontSize);

        // `top left` is as valid as `left top`; normalise so the horizontal component comes first.
        return IsVertical(parts[0]) ? (second, first) : (first, second);
    }

    private static BackgroundOffset Component(String part, IRenderDimensions dimensions, Double fontSize)
    {
        if (part.Equals("left", StringComparison.OrdinalIgnoreCase) ||
            part.Equals("top", StringComparison.OrdinalIgnoreCase))
        {
            return new BackgroundOffset(0, true);
        }

        if (part.Equals("right", StringComparison.OrdinalIgnoreCase) ||
            part.Equals("bottom", StringComparison.OrdinalIgnoreCase))
        {
            return new BackgroundOffset(1, true);
        }

        if (part.Equals("center", StringComparison.OrdinalIgnoreCase))
        {
            return new BackgroundOffset(0.5, true);
        }

        return ParseOffset(part, dimensions, fontSize) ?? default;
    }

    private static Boolean IsVertical(String part) =>
        part.Equals("top", StringComparison.OrdinalIgnoreCase) ||
        part.Equals("bottom", StringComparison.OrdinalIgnoreCase);

    // --- size -----------------------------------------------------------

    private static (BackgroundLayerSizeKind Kind, BackgroundOffset Width, BackgroundOffset Height) ParseSize(
        String value,
        IRenderDimensions dimensions,
        Double fontSize)
    {
        if (String.IsNullOrWhiteSpace(value))
        {
            return (BackgroundLayerSizeKind.Auto, default, default);
        }

        var keyword = value.Trim();

        if (keyword.Equals("cover", StringComparison.OrdinalIgnoreCase))
        {
            return (BackgroundLayerSizeKind.Cover, default, default);
        }

        if (keyword.Equals("contain", StringComparison.OrdinalIgnoreCase))
        {
            return (BackgroundLayerSizeKind.Contain, default, default);
        }

        var parts = CssValueList.SplitWhitespace(keyword);

        if (parts.Count == 0)
        {
            return (BackgroundLayerSizeKind.Auto, default, default);
        }

        var first = parts[0];
        var firstIsAuto = first.Equals("auto", StringComparison.OrdinalIgnoreCase);

        // An axis left to the image is written as `auto` beside the one that is stated, so the second
        // value still has to reach the reader: read as nothing at all, `auto 40px` sized the layer from
        // the intrinsic alone. An unset component of a stated size is kept as zero, which is how both
        // readers know an axis they have to settle themselves.
        if (firstIsAuto && (parts.Count < 2 || parts[1].Equals("auto", StringComparison.OrdinalIgnoreCase)))
        {
            return (BackgroundLayerSizeKind.Auto, default, default);
        }

        var width = firstIsAuto ? null : ParseOffset(first, dimensions, fontSize);
        var height = parts.Count > 1 && !parts[1].Equals("auto", StringComparison.OrdinalIgnoreCase)
            ? ParseOffset(parts[1], dimensions, fontSize)
            : null;

        if (width is null && height is null)
        {
            return (BackgroundLayerSizeKind.Auto, default, default);
        }

        return (BackgroundLayerSizeKind.Explicit, width ?? default, height ?? default);
    }

    /// <summary>
    /// Converts one component to an offset. A percentage is kept as a fraction because it is
    /// relative to the positioning area, which is only known when the box is painted.
    /// </summary>
    private static BackgroundOffset? ParseOffset(String text, IRenderDimensions dimensions, Double fontSize)
    {
        if (!CssLengthValue.TryParse(text, out var length))
        {
            return null;
        }

        if (length.Type == CssLengthValue.Unit.Percent)
        {
            return new BackgroundOffset(length.Value / 100.0, true);
        }

        try
        {
            return new BackgroundOffset(LengthReader.ToPixel(length, RenderMode.Horizontal, dimensions, fontSize), false);
        }
        catch (Exception)
        {
            return null;
        }
    }

    // --- gradients ------------------------------------------------------

    private static LinearGradientSpec? ParseGradient(String value)
    {
        var open = value.IndexOf('(');

        if (open <= 0 || !value.EndsWith(")", StringComparison.Ordinal))
        {
            return null;
        }

        if (!value[..open].Trim().Equals("linear-gradient", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var arguments = CssValueList.SplitTopLevel(value[(open + 1)..^1]);
        var start = 0;
        var angle = 180.0;

        if (arguments.Count > 0 && TryParseDirection(arguments[0], out var parsed))
        {
            angle = parsed;
            start = 1;
        }

        var count = arguments.Count - start;

        if (count < 2)
        {
            return null;
        }

        var stops = new List<GradientStop>(count);

        for (var index = start; index < arguments.Count; index++)
        {
            var stop = ParseStop(arguments[index], index - start, count);

            if (stop is null)
            {
                return null;
            }

            stops.Add(stop.Value);
        }

        return new LinearGradientSpec(angle, stops);
    }

    private static GradientStop? ParseStop(String text, Int32 index, Int32 count)
    {
        var parts = CssValueList.SplitWhitespace(text);

        if (parts.Count == 0)
        {
            return null;
        }

        var color = ParseColor(parts[0]);

        if (color is null)
        {
            return null;
        }

        var offset = count > 1 ? (Double)index / (count - 1) : 0.0;

        if (parts.Count > 1 && CssLengthValue.TryParse(parts[1], out var position))
        {
            offset = position.Type == CssLengthValue.Unit.Percent ? position.Value / 100.0 : offset;
        }

        return new GradientStop(offset, color.Value);
    }

    private static Boolean TryParseDirection(String text, out Double angle)
    {
        angle = 0;

        if (text.EndsWith("deg", StringComparison.OrdinalIgnoreCase) &&
            Double.TryParse(text[..^3].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var degrees))
        {
            angle = degrees;
            return true;
        }

        if (!text.StartsWith("to ", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        angle = text[3..].Trim().ToLowerInvariant() switch
        {
            "top" => 0,
            "right" => 90,
            "bottom" => 180,
            "left" => 270,
            "top right" or "right top" => 45,
            "bottom right" or "right bottom" => 135,
            "bottom left" or "left bottom" => 225,
            "top left" or "left top" => 315,
            _ => -1,
        };

        return angle >= 0;
    }

    // --- colours --------------------------------------------------------

    /// <summary>
    /// Reads a colour written as text - a hex triple, a named colour or an <c>rgb()</c>/<c>rgba()</c> -
    /// as packed ARGB, or null when the text names no colour. The background layers use it, and so does
    /// a reader of a vendor value AngleSharp never parsed into a colour of its own.
    /// </summary>
    internal static UInt32? ParseColor(String text)
    {
        if (text.StartsWith('#'))
        {
            return ParseHex(text[1..]);
        }

        var open = text.IndexOf('(');

        if (open <= 0 || !text.EndsWith(")", StringComparison.Ordinal))
        {
            var named = CssColorValue.FromName(text);
            return named is null ? null : ToArgb(named.Value);
        }

        var name = text[..open].Trim();

        if (!name.Equals("rgb", StringComparison.OrdinalIgnoreCase) &&
            !name.Equals("rgba", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var arguments = CssValueList.SplitTopLevel(text[(open + 1)..^1]);

        if (arguments.Count < 3)
        {
            return null;
        }

        var red = Number(arguments[0]);
        var green = Number(arguments[1]);
        var blue = Number(arguments[2]);
        var alpha = arguments.Count > 3 ? Number(arguments[3]) : 1.0;

        if (red is null || green is null || blue is null || alpha is null)
        {
            return null;
        }

        return ToArgb(CssColorValue.FromRgba(
            (Byte)Math.Clamp(red.Value, 0, 255),
            (Byte)Math.Clamp(green.Value, 0, 255),
            (Byte)Math.Clamp(blue.Value, 0, 255),
            Math.Clamp(alpha.Value, 0, 1)));
    }

    private static Double? Number(String text) =>
        Double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;

    /// <summary>Reads a hex colour written as three, four, six or eight digits, alpha last.</summary>
    private static UInt32? ParseHex(String digits)
    {
        if (digits.Length is not (3 or 4 or 6 or 8) ||
            !digits.All(Uri.IsHexDigit))
        {
            return null;
        }

        if (digits.Length is 3 or 4)
        {
            digits = String.Concat(digits.Select(c => new String(c, 2)));
        }

        Byte Channel(Int32 at) =>
            Byte.Parse(digits.Substring(at, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);

        var alpha = digits.Length == 8 ? Channel(6) : (Byte)255;

        return ToArgb(CssColorValue.FromRgba(Channel(0), Channel(2), Channel(4), alpha / 255.0));
    }

    private static UInt32 ToArgb(CssColorValue color) =>
        (UInt32)((color.A << 24) | (color.R << 16) | (color.G << 8) | color.B);
}

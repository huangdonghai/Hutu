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
/// one layer is not painted, never that the page fails.
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

            if (Drawn(image, dimensions, fontSize) is not { } drawn)
            {
                continue;
            }

            var gradient = ParseGradient(drawn, dimensions, fontSize);
            var urls = gradient is null ? CssUrlReferences.Extract(drawn) : new List<String>();

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
                ? (new BackgroundOffset(0, 0.5), single)
                : (single, new BackgroundOffset(0, 0.5));
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
            return new BackgroundOffset(0, 0);
        }

        if (part.Equals("right", StringComparison.OrdinalIgnoreCase) ||
            part.Equals("bottom", StringComparison.OrdinalIgnoreCase))
        {
            return new BackgroundOffset(0, 1);
        }

        if (part.Equals("center", StringComparison.OrdinalIgnoreCase))
        {
            return new BackgroundOffset(0, 0.5);
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
        // A position or a size written as arithmetic keeps its two parts apart: `calc(100% / 4)` is a
        // share of a box the style has not been told the size of yet (CSS Values 4 §10.8), so the sum is
        // read as words and added up where the box is known.
        if (CalcText.Read(text, atom => Length(atom, dimensions, fontSize, conic: false)) is { } sum)
        {
            return new BackgroundOffset(sum.Distance, sum.Share);
        }

        if (!CssLengthValue.TryParse(text, out var length))
        {
            return null;
        }

        if (length.Type == CssLengthValue.Unit.Percent)
        {
            return new BackgroundOffset(0, length.Value / 100.0);
        }

        try
        {
            return new BackgroundOffset(LengthReader.ToPixel(length, RenderMode.Horizontal, dimensions, fontSize), 0);
        }
        catch (Exception)
        {
            return null;
        }
    }

    // --- gradients ------------------------------------------------------

    /// <summary>
    /// Gets the picture a layer draws when the page wrote an <c>image-set()</c>: the first of its
    /// candidates this engine can draw, as the text it would have been written as on its own (CSS Images
    /// 4 §4). An image that is no set is handed back as it stands, and a set whose every candidate names
    /// something the engine cannot draw - a resolution on its own, an <c>element()</c> - comes back as
    /// nothing, which the layer treats the way it treats any image it does not understand.
    /// </summary>
    /// <remarks>
    /// The candidates are tried in the order the page wrote them rather than by the resolution beside
    /// them: a box is drawn at one resolution here, so the choice between them has nothing to weigh.
    /// </remarks>
    private static String? Drawn(String image, IRenderDimensions dimensions, Double fontSize)
    {
        var open = image.IndexOf('(');

        if (open <= 0 ||
            !image.EndsWith(")", StringComparison.Ordinal) ||
            !image[..open].Trim().Equals("image-set", StringComparison.OrdinalIgnoreCase))
        {
            return image;
        }

        foreach (var option in CssValueList.SplitTopLevel(image[(open + 1)..^1]))
        {
            var words = CssValueList.SplitWhitespace(option);

            if (words.Count == 0)
            {
                continue;
            }

            var candidate = words[0];

            // A candidate written as a bare string names the file a url() would name it by; the
            // resolution and the type() that may follow belong to the choice between candidates.
            if (candidate.Length > 1 && candidate[0] is '"' or '\'')
            {
                return "url(" + candidate + ")";
            }

            if (ParseGradient(candidate, dimensions, fontSize) is not null ||
                CssUrlReferences.Extract(candidate).Count > 0)
            {
                return candidate;
            }
        }

        return null;
    }

    private static GradientSpec? ParseGradient(
        String value,
        IRenderDimensions dimensions,
        Double fontSize)
    {
        var open = value.IndexOf('(');

        if (open <= 0 || !value.EndsWith(")", StringComparison.Ordinal))
        {
            return null;
        }

        var name = value[..open].Trim();
        var repeats = name.StartsWith("repeating-", StringComparison.OrdinalIgnoreCase);
        var kind = (repeats ? name["repeating-".Length..] : name).Trim().ToLowerInvariant();
        var conic = kind == "conic-gradient";
        var radial = kind == "radial-gradient";

        if (kind is not ("linear-gradient" or "conic-gradient" or "radial-gradient"))
        {
            return null;
        }

        var arguments = CssValueList.SplitTopLevel(value[(open + 1)..^1]);

        // The space the colours are mixed in stands at the front of the head, glued to whatever else the
        // head says or alone where the head says nothing. It is taken off first, and what is left of that
        // argument is the direction, the shape or the first colour it was written beside.
        if (!TakeInterpolation(arguments, out var space, out var hue))
        {
            return null;
        }

        var start = 0;

        // A line points down unless the page turns it; a sweep starts at the top and goes round.
        var angle = conic ? 0.0 : 180.0;
        var centerX = new BackgroundOffset(0, 0.5);
        var centerY = new BackgroundOffset(0, 0.5);
        var radialHead = default(RadialHead);
        var hasHead = false;

        if (arguments.Count > 0 && radial)
        {
            if (ParseRadialHead(arguments[0], dimensions, fontSize) is { } head)
            {
                if (!head.Ok)
                {
                    return null;
                }

                centerX = head.X;
                centerY = head.Y;
                radialHead = head;
                hasHead = true;
                start = 1;
            }
        }
        else if (arguments.Count > 0 && conic)
        {
            if (ParseSweepHead(arguments[0], dimensions, fontSize) is not { } head)
            {
                // No head was written, so the first argument is the first colour of the sweep.
            }
            else if (!head.Ok)
            {
                return null;
            }
            else
            {
                angle = head.Angle;
                centerX = head.X;
                centerY = head.Y;
                start = 1;
            }
        }
        else if (arguments.Count > 0 && TryParseDirection(arguments[0], out var parsed))
        {
            angle = parsed;
            start = 1;
        }

        var count = arguments.Count - start;

        // One colour is a gradient the whole line is that colour of: the fixup leaves the stop where it
        // stands, and everything before it and after it is that colour anyway.
        if (count < 1)
        {
            return null;
        }

        var stops = new List<ColorStop>(count);

        for (var index = start; index < arguments.Count; index++)
        {
            var stop = ParseStop(arguments[index], dimensions, fontSize, conic);

            if (stop is null)
            {
                return null;
            }

            stops.Add(stop.Value);
        }

        var shape = conic ? GradientShape.Conic : radial ? GradientShape.Radial : GradientShape.Linear;

        return new GradientSpec(shape, angle, stops, repeats, centerX, centerY)
        {
            IsCircle = hasHead && radialHead.IsCircle,
            Ending = hasHead ? radialHead.Ending : RadialEnding.FarthestCorner,
            RadiusX = radialHead.RadiusX,
            RadiusY = radialHead.RadiusY,
            InterpolationSpace = space,
            InterpolationHue = hue,
        };
    }

    /// <summary>
    /// Reads the <c>in &lt;color-space&gt; [hue method]</c> a gradient head may open with off the front of
    /// its first argument, and takes it out of the list so the rest of the reader never sees it.
    /// </summary>
    /// <returns>
    /// False when the prefix is written but comes to nothing - a space the chapter does not define, or a
    /// hue method beside a space that has no hue - which makes the whole function invalid rather than one
    /// of its parts. True with the sRGB defaults where no prefix was written at all.
    /// </returns>
    private static Boolean TakeInterpolation(List<String> arguments, out ColorSpace space, out HueMethod hue)
    {
        space = ColorSpace.Srgb;
        hue = HueMethod.Shorter;

        if (arguments.Count == 0)
        {
            return true;
        }

        var words = CssValueList.SplitWhitespace(arguments[0]);

        if (words.Count == 0 || !Is(words[0], "in"))
        {
            return true;
        }

        if (words.Count < 2 || Space(words[1]) is not { } named)
        {
            return false;
        }

        space = named;

        var read = 2;

        if (words.Count > read && Hue(words[read]) is { } method)
        {
            // A hue method is two words, and belongs to a space whose third axis is a hue angle at all.
            // Anything else that stands beside the space is the head, not the method, and is left for the
            // reader of the head to take.
            if (!IsPolar(named) || read + 1 >= words.Count || !Is(words[read + 1], "hue"))
            {
                return false;
            }

            hue = method;
            read += 2;
        }

        if (read >= words.Count)
        {
            arguments.RemoveAt(0);
        }
        else
        {
            arguments[0] = String.Join(" ", words.GetRange(read, words.Count - read));
        }

        return true;
    }

    /// <summary>Gets the space a word of an <c>in</c> prefix names, or nothing where it names none.</summary>
    private static ColorSpace? Space(String word) => word.ToLowerInvariant() switch
    {
        "srgb" => ColorSpace.Srgb,
        "srgb-linear" => ColorSpace.SrgbLinear,
        "display-p3" => ColorSpace.DisplayP3,
        "display-p3-linear" => ColorSpace.DisplayP3Linear,
        "a98-rgb" => ColorSpace.A98Rgb,
        "prophoto-rgb" => ColorSpace.ProPhotoRgb,
        "rec2020" => ColorSpace.Rec2020,
        "xyz" or "xyz-d65" => ColorSpace.XyzD65,
        "xyz-d50" => ColorSpace.XyzD50,
        "lab" => ColorSpace.Lab,
        "lch" => ColorSpace.Lch,
        "oklab" => ColorSpace.Oklab,
        "oklch" => ColorSpace.Oklch,
        "hsl" => ColorSpace.Hsl,
        "hwb" => ColorSpace.Hwb,
        _ => null,
    };

    /// <summary>Gets the way round the circle a hue method names, or nothing where the word names none.</summary>
    private static HueMethod? Hue(String word) => word.ToLowerInvariant() switch
    {
        "shorter" => HueMethod.Shorter,
        "longer" => HueMethod.Longer,
        "increasing" => HueMethod.Increasing,
        "decreasing" => HueMethod.Decreasing,
        _ => null,
    };

    /// <summary>Whether the space has a hue for a method to speak about (CSS Color 4 §13.2).</summary>
    private static Boolean IsPolar(ColorSpace space) =>
        space is ColorSpace.Hsl or ColorSpace.Hwb or ColorSpace.Lch or ColorSpace.Oklch;

    /// <summary>What the first argument of a radial gradient came to.</summary>
    private readonly record struct RadialHead(
        Boolean Ok,
        Boolean IsCircle,
        RadialEnding Ending,
        BackgroundOffset RadiusX,
        BackgroundOffset RadiusY,
        BackgroundOffset X,
        BackgroundOffset Y);

    /// <summary>
    /// Reads the head of a radial gradient: the shape, the size and the centre, in any order and any
    /// subset of the three (CSS Images 3 §3.4).
    /// </summary>
    /// <returns>
    /// Null when the argument is no head at all - which is how a shape that opens on its first colour is
    /// told from one whose head was written broken - and the head otherwise, with
    /// <see cref="RadialHead.Ok"/> saying whether it stood up.
    /// </returns>
    private static RadialHead? ParseRadialHead(String text, IRenderDimensions dimensions, Double fontSize)
    {
        var words = CssValueList.SplitWhitespace(text);

        var first = words.Count == 0 ? String.Empty : words[0];

        // A colour is none of these: not a shape, not a size, not an `at`, and not a number a radius could
        // be. So a first argument that opens on anything else is no head at all, and the first colour of
        // the shape is where the argument list begins.
        if (first.Length == 0 ||
            (Ending(first) is null && !Is(first, "circle") && !Is(first, "ellipse") && !Is(first, "at") &&
             Radius(first, dimensions, fontSize) is null))
        {
            return null;
        }

        var broken = new RadialHead(false, false, RadialEnding.FarthestCorner, default, default, default, default);
        var circle = false;
        var shapeWritten = false;
        var ending = RadialEnding.FarthestCorner;
        var radiusX = default(BackgroundOffset);
        var radiusY = default(BackgroundOffset);
        var radii = 0;
        var x = new BackgroundOffset(0, 0.5);
        var y = new BackgroundOffset(0, 0.5);
        var index = 0;

        while (index < words.Count)
        {
            var word = words[index];

            if (Is(word, "circle") || Is(word, "ellipse"))
            {
                if (shapeWritten || radii > 0)
                {
                    return broken;
                }

                circle = Is(word, "circle");
                shapeWritten = true;
            }
            else if (Ending(word) is { } size)
            {
                if (radii > 0 || ending != RadialEnding.FarthestCorner)
                {
                    return broken;
                }

                ending = size;
            }
            else if (Is(word, "at"))
            {
                var place = new List<String>();

                // Everything that follows an `at` is the centre until a word that can only belong to the
                // shape or the size turns up - the numbers of a position are the position.
                while (index + 1 < words.Count &&
                       !Is(words[index + 1], "at") &&
                       !Is(words[index + 1], "circle") &&
                       !Is(words[index + 1], "ellipse") &&
                       Ending(words[index + 1]) is null)
                {
                    place.Add(words[++index]);
                }

                if (!TryParsePlace(place, dimensions, fontSize, out x, out y))
                {
                    return broken;
                }
            }
            else if (Radius(word, dimensions, fontSize) is { } radius)
            {
                if (radii > 0 && ending != RadialEnding.Explicit)
                {
                    // The second number of a size stands beside the first; after a keyword that already
                    // said how far the shape reaches it names nothing.
                    return broken;
                }

                if (radii == 0)
                {
                    radiusX = radius;
                }
                else if (radii > 1 || circle)
                {
                    // A circle has one radius, and an ellipse has two: a third number is no size at all.
                    return broken;
                }
                else
                {
                    radiusY = radius;
                }

                radii++;
                ending = RadialEnding.Explicit;
            }
            else
            {
                return broken;
            }

            index++;
        }

        if (radii == 1)
        {
            // One number names a shape of equal radii, and where the page did not say which shape it is a
            // circle - the only one of the two a single number can draw (CSS Images 3 §3.4).
            radiusY = radiusX;
            circle = circle || !shapeWritten;
        }

        return new RadialHead(true, circle, ending, radiusX, radiusY, x, y);
    }

    /// <summary>
    /// Gets the radius a token of a radial head is, or null where it is no number. The length reader
    /// answers zero to any word it does not know - <c>red</c> comes back as <c>0</c> - so the shape of the
    /// token is asked first, or the first colour of a gradient is read as its size.
    /// </summary>
    private static BackgroundOffset? Radius(String text, IRenderDimensions dimensions, Double fontSize) =>
        text.Length > 0 && (Char.IsDigit(text[0]) || text[0] is '.' or '+' or '-')
            ? Offset(text, dimensions, fontSize, conic: false)
            : null;

    /// <summary>
    /// Gets which ending a size keyword stands for, or null where the word is no size. The two legacy
    /// words are read as the sides they meant: <c>contain</c> keeps to the nearest side and <c>cover</c>
    /// reaches the farthest.
    /// </summary>
    private static RadialEnding? Ending(String word) => word.ToLowerInvariant() switch
    {
        "closest-side" or "contain" => RadialEnding.ClosestSide,
        "farthest-side" or "cover" => RadialEnding.FarthestSide,
        "closest-corner" => RadialEnding.ClosestCorner,
        "farthest-corner" => RadialEnding.FarthestCorner,
        _ => null,
    };

    /// <summary>What the first argument of a conic gradient came to.</summary>
    private readonly record struct SweepHead(Boolean Ok, Double Angle, BackgroundOffset X, BackgroundOffset Y);

    /// <summary>
    /// Reads the head of a conic gradient, <c>from &lt;angle&gt;</c> and <c>at &lt;position&gt;</c> in
    /// either order (CSS Images 3 §3.4).
    /// </summary>
    /// <returns>
    /// Null when the argument is no head at all - which is how a sweep that opens on its first colour is
    /// told from one whose head was written broken - and the head, with <see cref="SweepHead.Ok"/> saying
    /// whether it stood up, otherwise.
    /// </returns>
    private static SweepHead? ParseSweepHead(String text, IRenderDimensions dimensions, Double fontSize)
    {
        var words = CssValueList.SplitWhitespace(text);

        if (words.Count == 0 || (!Is(words[0], "from") && !Is(words[0], "at")))
        {
            return null;
        }

        var broken = new SweepHead(false, 0, default, default);
        var angle = 0.0;
        var x = new BackgroundOffset(0, 0.5);
        var y = new BackgroundOffset(0, 0.5);
        var index = 0;

        while (index < words.Count)
        {
            if (Is(words[index], "from"))
            {
                if (index + 1 >= words.Count || Angle(words[++index]) is not { } degrees)
                {
                    return broken;
                }

                angle = degrees;
            }
            else if (Is(words[index], "at"))
            {
                var place = new List<String>();

                while (index + 1 < words.Count && !Is(words[index + 1], "from") && !Is(words[index + 1], "at"))
                {
                    place.Add(words[++index]);
                }

                if (!TryParsePlace(place, dimensions, fontSize, out x, out y))
                {
                    return broken;
                }
            }
            else
            {
                return broken;
            }

            index++;
        }

        return new SweepHead(true, angle, x, y);
    }

    /// <summary>Turns the one, two or four tokens of a `<position>` into the two offsets it stands for.</summary>
    private static Boolean TryParsePlace(
        List<String> words,
        IRenderDimensions dimensions,
        Double fontSize,
        out BackgroundOffset centerX,
        out BackgroundOffset centerY)
    {
        centerX = new BackgroundOffset(0, 0.5);
        centerY = new BackgroundOffset(0, 0.5);

        if (words.Count == 0 || words.Count > 4)
        {
            return false;
        }

        var at = 0;
        var horizontal = false;
        var vertical = false;

        while (at < words.Count)
        {
            var word = words[at];

            // A keyword of one axis names that axis and nothing else; `center` is the middle of both,
            // and stands for the axis a keyword beside it left out.
            if (Is(word, "left") || Is(word, "right"))
            {
                centerX = new BackgroundOffset(0, Is(word, "left") ? 0 : 1);
                horizontal = true;
            }
            else if (Is(word, "top") || Is(word, "bottom"))
            {
                centerY = new BackgroundOffset(0, Is(word, "top") ? 0 : 1);
                vertical = true;
            }
            else if (Is(word, "center"))
            {
                if (!horizontal)
                {
                    centerX = new BackgroundOffset(0, 0.5);
                    horizontal = true;
                }
                else
                {
                    centerY = new BackgroundOffset(0, 0.5);
                    vertical = true;
                }
            }
            else if (Offset(word, dimensions, fontSize, conic: false) is { } offset)
            {
                if (!horizontal)
                {
                    centerX = offset;
                    horizontal = true;
                }
                else if (!vertical)
                {
                    centerY = offset;
                    vertical = true;
                }
                else
                {
                    return false;
                }
            }
            else
            {
                return false;
            }

            at++;
        }

        return true;
    }

    /// <summary>Reads an angle as the degrees it turns (CSS Values 4 §8.7).</summary>
    private static Double? Angle(String text)
    {
        var trimmed = text.Trim();

        foreach (var (suffix, per) in Turns)
        {
            if (trimmed.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) &&
                Double.TryParse(
                    trimmed[..^suffix.Length].Trim(),
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out var value))
            {
                return value * per;
            }
        }

        return null;
    }

    /// <summary>How far each angle unit turns, in degrees.</summary>
    private static readonly (String Suffix, Double Degrees)[] Turns =
    [
        ("turn", 360.0),
        ("grad", 0.9),
        ("rad", 180.0 / Math.PI),
        ("deg", 1.0),
    ];

    private static Boolean Is(String word, String name) => word.Equals(name, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Reads one stop: the colour it is, and the length or percentage beside it where the author stood
    /// it on the line.
    /// </summary>
    /// <remarks>
    /// The position is the last token, because that is where the grammar puts it, and the colour is
    /// whatever stands before it - spaces and all, since a <c>rgb(0 0 255 / .5)</c> is written with the
    /// same separators a stop list uses. A token that is no length leaves the whole argument to the
    /// colour, which is how a stop written without a position is left for the line to place.
    /// <para>
    /// A sweep's stops are stood at angles rather than at distances, and the two are read apart because
    /// the one has a unit the other has not: <c>red 90deg</c> and <c>red 90px</c> are both a position,
    /// and only one of them is a quarter of the way round.
    /// </para>
    /// </remarks>
    private static ColorStop? ParseStop(String text, IRenderDimensions dimensions, Double fontSize, Boolean conic)
    {
        var parts = CssValueList.SplitWhitespace(text);

        if (parts.Count == 0)
        {
            return null;
        }

        var last = parts.Count - 1;
        var position = last > 0 ? ParseStopPosition(parts[last], dimensions, fontSize, conic) : null;
        var color = ParseColor(position is null ? text.Trim() : String.Join(' ', parts.Take(last)));

        if (color is null)
        {
            // A position with no colour beside it is an interpolation hint: it says where the ramp
            // between the two colours on either side reaches their midpoint, and is neither of them
            // (CSS Images 3 §3.4).
            return last == 0 && ParseStopPosition(parts[0], dimensions, fontSize, conic) is { } hint
                ? new ColorStop(0, hint, true, isHint: true)
                : null;
        }

        return new ColorStop(color.Value, position ?? default, position is not null);
    }

    /// <summary>
    /// Reads one stop's position: a length, a percentage, or a <c>calc()</c> of the two - and for a
    /// sweep, an angle instead of a length, with the percentage a share of the turn.
    /// </summary>
    private static StopOffset? ParseStopPosition(
        String text,
        IRenderDimensions dimensions,
        Double fontSize,
        Boolean conic) =>
        CalcText.Read(text, atom => Length(atom, dimensions, fontSize, conic)) is { } amount
            ? new StopOffset(amount.Distance, amount.Share)
            : Of(Offset(text.Trim(), dimensions, fontSize, conic));

    private static BackgroundOffset? Offset(
        String text,
        IRenderDimensions dimensions,
        Double fontSize,
        Boolean conic) =>
        conic && Angle(text) is { } degrees
            ? new BackgroundOffset(degrees, 0)
            : ParseOffset(text, dimensions, fontSize);

    private static StopOffset? Of(BackgroundOffset? offset) => offset is not { } stated
        ? null
        : new StopOffset(stated.Pixels, stated.Percent);

    /// <summary>Reads one operand of a <c>calc()</c> as the distance or the share of the line it comes to.</summary>
    private static StyleLength? Length(
        String atom,
        IRenderDimensions dimensions,
        Double fontSize,
        Boolean conic) =>
        Offset(atom, dimensions, fontSize, conic) is not { } stated
            ? null
            : stated.Percent != 0
                ? StyleLength.FromPercent(stated.Percent)
                : StyleLength.FromPixels(stated.Pixels);

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

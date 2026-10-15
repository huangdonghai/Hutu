using AngleSharp.Css;
using System;

namespace Hutu.Rendering.Layout;

/// <summary>
/// Turns the CSS Motion Path longhands into the <em>offset transform</em>: a translation that aligns the
/// element's anchor point with its offset position on the path, then a rotation from
/// <c>offset-rotate</c> (Motion Path §calculating-the-offset-transform). The result is folded into the
/// box's transform, which the painter already applies about the transform-origin.
/// </summary>
/// <remarks>
/// Covers <c>offset-path: path('…')</c> with <c>offset-distance</c>, <c>offset-rotate</c>
/// (auto/reverse/angle) and <c>offset-anchor</c> (auto = the transform-origin). The path's coordinates
/// are read in the containing block's coordinate system (the offset starting position when
/// <c>offset-position</c> is <c>normal</c>).
/// </remarks>
internal static class MotionTransform
{
    /// <summary>
    /// Computes the offset transform for a box, or null when it has no offset path.
    /// </summary>
    /// <param name="style">The box's computed style, for the offset-* longhands.</param>
    /// <param name="frame">The box's border box, in document coordinates.</param>
    /// <param name="containingBlock">The box's containing block rectangle, in document coordinates.</param>
    /// <param name="dimensions">The render dimensions, for font-relative lengths.</param>
    /// <param name="fontSize">The box's font size, for <c>em</c>.</param>
    /// <param name="originX">The transform-origin's x within the box, in pixels.</param>
    /// <param name="originY">The transform-origin's y within the box, in pixels.</param>
    public static Affine? Compute(
        ComputedStyle style,
        Rect frame,
        Rect containingBlock,
        IRenderDimensions dimensions,
        Double fontSize,
        Double originX,
        Double originY)
    {
        var pathText = style.OffsetValue("offset-path");

        if (string.IsNullOrWhiteSpace(pathText))
        {
            return null;
        }

        var path = ParsePath(pathText, frame, containingBlock, style, dimensions, fontSize, out var pathOriginX, out var pathOriginY);

        if (path is null || path.Total <= 0)
        {
            return null;
        }

        var distance = ResolveDistance(style.OffsetValue("offset-distance"), path.Total, dimensions, fontSize);
        var (px, py, tangent) = path.PointAt(distance);

        // The offset position in document coordinates: the path point added to the origin the path was
        // drawn from (the element's own box for path(), the containing block for a shape or ray).
        var positionX = pathOriginX + px;
        var positionY = pathOriginY + py;

        // The anchor point defaults to the transform-origin; the element is translated so the anchor
        // lands on the offset position.
        var anchorX = frame.X + originX;
        var anchorY = frame.Y + originY;

        if (style.OffsetValue("offset-anchor") is { } anchor && !anchor.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            var (ax, ay) = ResolvePosition(anchor, frame.Width, frame.Height, dimensions, fontSize);
            anchorX = frame.X + ax;
            anchorY = frame.Y + ay;
        }

        var angle = ResolveRotate(style.OffsetValue("offset-rotate"), tangent);

        var translate = new Affine(1, 0, 0, 1, positionX - anchorX, positionY - anchorY);
        var cos = Math.Cos(angle);
        var sin = Math.Sin(angle);
        var rotate = new Affine(cos, sin, -sin, cos, 0, 0);

        // The offset transform is a rotation plus the translation that lands the anchor on the offset
        // position. The painter already applies the box's matrix about its transform-origin (which is
        // the anchor when offset-anchor is auto), so the anchor round-trip must NOT be baked in here -
        // doing both shifts the box twice. Only the linear rotation and the (position - anchor) slide.
        return translate.Then(rotate);
    }

    /// <summary>
    /// Builds the offset path from an <c>offset-path</c> value, and reports the document point its own
    /// (0,0) sits at. <c>path()</c> is drawn from the element's own box; the basic shapes and <c>ray()</c>
    /// are laid out in the containing block (the reference box). Returns null for <c>none</c> or a form
    /// not yet handled.
    /// </summary>
    private static MotionPath? ParsePath(
        String text,
        Rect frame,
        Rect containingBlock,
        ComputedStyle style,
        IRenderDimensions dimensions,
        Double fontSize,
        out Double originX,
        out Double originY)
    {
        originX = 0;
        originY = 0;

        var lower = text.Trim();

        if (lower.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (lower.StartsWith("path(", StringComparison.OrdinalIgnoreCase))
        {
            var data = ExtractQuoted(text, "path(".Length);

            if (data is null)
            {
                return null;
            }

            originX = frame.X;
            originY = frame.Y;
            return MotionPath.FromPathData(data);
        }

        // A shape or ray is drawn in the containing block's coordinate system.
        originX = containingBlock.X;
        originY = containingBlock.Y;
        var box = containingBlock;
        var args = Inner(text);

        if (lower.StartsWith("circle(", StringComparison.OrdinalIgnoreCase) ||
            lower.StartsWith("ellipse(", StringComparison.OrdinalIgnoreCase))
        {
            return ParseEllipse(args, box, dimensions, fontSize);
        }

        if (lower.StartsWith("inset(", StringComparison.OrdinalIgnoreCase))
        {
            return ParseInset(args, box, dimensions, fontSize);
        }

        if (lower.StartsWith("polygon(", StringComparison.OrdinalIgnoreCase))
        {
            return ParsePolygon(args, box, dimensions, fontSize);
        }

        if (lower.StartsWith("ray(", StringComparison.OrdinalIgnoreCase))
        {
            return ParseRay(args, box, frame, style, dimensions, fontSize);
        }

        return null;
    }

    /// <summary>The text inside the outermost parentheses.</summary>
    private static String Inner(String text)
    {
        var open = text.IndexOf('(');
        var close = text.LastIndexOf(')');

        return open < 0 || close <= open ? String.Empty : text[(open + 1)..close];
    }

    private static MotionPath? ParseEllipse(String args, Rect box, IRenderDimensions dimensions, Double fontSize)
    {
        var (centerX, centerY) = CenterOf(args, box, dimensions, fontSize);
        var rx = Radius(args, "closest-side", centerX, centerY, box, true, dimensions, fontSize);
        var ry = Radius(args, "closest-side", centerX, centerY, box, false, dimensions, fontSize);

        // circle() gives one radius to both axes; ellipse() may give two before "at".
        var numbers = NumbersBeforeAt(args);

        if (numbers.Count == 1)
        {
            ry = rx;
        }

        return MotionPath.FromEllipse(centerX, centerY, rx, ry);
    }

    private static MotionPath? ParseInset(String args, Rect box, IRenderDimensions dimensions, Double fontSize)
    {
        var round = 0.0;
        var rAt = args.IndexOf("round", StringComparison.OrdinalIgnoreCase);
        var head = args;

        if (rAt >= 0)
        {
            head = args[..rAt];
            round = Length(args[(rAt + 5)..].Trim(), Math.Min(box.Width, box.Height), dimensions, fontSize);
        }

        var sides = Lengths(head, dimensions, fontSize, box);
        var top = sides.Count > 0 ? sides[0] : 0;
        var right = sides.Count > 1 ? sides[1] : top;
        var bottom = sides.Count > 2 ? sides[2] : top;
        var left = sides.Count > 3 ? sides[3] : right;

        return MotionPath.FromRect(left, top, Math.Max(0, box.Width - left - right), Math.Max(0, box.Height - top - bottom), round);
    }

    private static MotionPath? ParsePolygon(String args, Rect box, IRenderDimensions dimensions, Double fontSize)
    {
        var points = new List<(Double X, Double Y)>();
        var pairs = args.Split(',');

        foreach (var pair in pairs)
        {
            var xy = Lengths(pair, dimensions, fontSize, box);

            if (xy.Count >= 2)
            {
                points.Add((xy[0], xy[1]));
            }
        }

        return points.Count >= 2 ? MotionPath.FromPolygon(points) : null;
    }

    private static MotionPath? ParseRay(String args, Rect box, Rect frame, ComputedStyle style, IRenderDimensions dimensions, Double fontSize)
    {
        var tokens = args.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var angle = 0.0;
        var size = "closest-side";
        var contain = false;
        (Double X, Double Y)? at = null;

        for (var i = 0; i < tokens.Length; i++)
        {
            var token = tokens[i];

            if (token.Equals("contain", StringComparison.OrdinalIgnoreCase))
            {
                contain = true;
            }
            else if (token.Equals("at", StringComparison.OrdinalIgnoreCase) && i + 1 < tokens.Length)
            {
                var pos = tokens[++i];
                var xy = ResolvePosition(pos, box.Width, box.Height, dimensions, fontSize);
                at = (xy.X, xy.Y);
            }
            else if (token is "closest-side" or "closest-corner" or "farthest-side" or "farthest-corner" or "sides")
            {
                size = token.ToLowerInvariant();
            }
            else if (IsAngle(token))
            {
                angle = ToRadians(token);
            }
        }

        var origin = at ?? RayDefaultOrigin(style, box, frame, dimensions, fontSize);
        var length = RayLength(size, origin.X, origin.Y, angle, box);

        if (contain)
        {
            length = Math.Max(0, length - Math.Max(frame.Width, frame.Height) / 2);
        }

        return MotionPath.FromRay(origin.X, origin.Y, angle, length);
    }

    private static (Double X, Double Y) RayDefaultOrigin(ComputedStyle style, Rect box, Rect frame, IRenderDimensions dimensions, Double fontSize)
    {
        var position = style.OffsetValue("offset-position");

        if (string.IsNullOrWhiteSpace(position) || position.Trim().Equals("normal", StringComparison.OrdinalIgnoreCase))
        {
            return (box.Width / 2, box.Height / 2);
        }

        if (position.Trim().Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            return (frame.X - box.X, frame.Y - box.Y);
        }

        var xy = ResolvePosition(position, box.Width, box.Height, dimensions, fontSize);
        return (xy.X, xy.Y);
    }

    private static Double RayLength(String size, Double ox, Double oy, Double angle, Rect box)
    {
        var w = box.Width;
        var h = box.Height;
        var left = ox;
        var right = w - ox;
        var top = oy;
        var bottom = h - oy;

        switch (size)
        {
            case "closest-side":
                return Math.Min(Math.Min(left, right), Math.Min(top, bottom));
            case "farthest-side":
                return Math.Max(Math.Max(left, right), Math.Max(top, bottom));
            case "closest-corner":
                return Math.Min(Math.Sqrt(Sq(left) + Sq(top)), Math.Min(Math.Sqrt(Sq(right) + Sq(top)),
                    Math.Min(Math.Sqrt(Sq(left) + Sq(bottom)), Math.Sqrt(Sq(right) + Sq(bottom)))));
            case "farthest-corner":
                return Math.Max(Math.Max(Math.Sqrt(Sq(left) + Sq(top)), Math.Sqrt(Sq(right) + Sq(top))),
                    Math.Max(Math.Sqrt(Sq(left) + Sq(bottom)), Math.Sqrt(Sq(right) + Sq(bottom))));
            default:
                return RayToSides(ox, oy, angle, w, h);
        }
    }

    /// <summary>Distance along the ray's direction from the origin to the containing block's boundary.</summary>
    private static Double RayToSides(Double ox, Double oy, Double angle, Double w, Double h)
    {
        var dx = Math.Sin(angle);
        var dy = -Math.Cos(angle);
        var best = Double.PositiveInfinity;

        void Edge(Double nx, Double ny, Double d)
        {
            var denom = nx * dx + ny * dy;

            if (denom < -1e-9)
            {
                best = Math.Min(best, d / -denom);
            }
        }

        Edge(1, 0, ox);        // left   (points +x inward)
        Edge(-1, 0, w - ox);   // right
        Edge(0, 1, oy);        // top
        Edge(0, -1, h - oy);   // bottom

        return Double.IsPositiveInfinity(best) ? 0 : best;
    }

    private static (Double X, Double Y) CenterOf(String args, Rect box, IRenderDimensions dimensions, Double fontSize)
    {
        var at = args.IndexOf(" at ", StringComparison.OrdinalIgnoreCase);

        if (at < 0)
        {
            return (box.Width / 2, box.Height / 2);
        }

        var pos = args[(at + 4)..].Trim();
        var xy = ResolvePosition(pos, box.Width, box.Height, dimensions, fontSize);
        return (xy.X, xy.Y);
    }

    private static Double Radius(String args, String fallback, Double cx, Double cy, Rect box, Boolean horizontal, IRenderDimensions dimensions, Double fontSize)
    {
        var head = args;
        var at = head.IndexOf(" at ", StringComparison.OrdinalIgnoreCase);

        if (at >= 0)
        {
            head = head[..at];
        }

        var tokens = head.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        foreach (var token in tokens)
        {
            switch (token.ToLowerInvariant())
            {
                case "closest-side":
                    return horizontal ? Math.Min(cx, box.Width - cx) : Math.Min(cy, box.Height - cy);
                case "farthest-side":
                    return horizontal ? Math.Max(cx, box.Width - cx) : Math.Max(cy, box.Height - cy);
                case "closest-corner":
                    return CornerDistance(cx, cy, box, true);
                case "farthest-corner":
                    return CornerDistance(cx, cy, box, false);
            }
        }

        var numbers = NumbersBeforeAt(args);

        if (numbers.Count > 0)
        {
            var first = Length(numbers[horizontal ? 0 : Math.Min(1, numbers.Count - 1)],
                Math.Sqrt(Sq(box.Width) + Sq(box.Height)) / Math.Sqrt(2), dimensions, fontSize);
            return first;
        }

        return horizontal ? Math.Min(cx, box.Width - cx) : Math.Min(cy, box.Height - cy);
    }

    private static Double CornerDistance(Double cx, Double cy, Rect box, Boolean closest)
    {
        var distances = new[]
        {
            Math.Sqrt(Sq(cx) + Sq(cy)),
            Math.Sqrt(Sq(box.Width - cx) + Sq(cy)),
            Math.Sqrt(Sq(cx) + Sq(box.Height - cy)),
            Math.Sqrt(Sq(box.Width - cx) + Sq(box.Height - cy)),
        };
        Array.Sort(distances);
        return closest ? distances[0] : distances[^1];
    }

    private static List<String> NumbersBeforeAt(String args)
    {
        var at = args.IndexOf(" at ", StringComparison.OrdinalIgnoreCase);
        var head = at >= 0 ? args[..at] : args;
        var list = new List<String>();

        foreach (var token in head.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (token.Length > 0 && (Char.IsDigit(token[0]) || token[0] is '-' or '+' or '.'))
            {
                list.Add(token);
            }
        }

        return list;
    }

    private static List<Double> Lengths(String text, IRenderDimensions dimensions, Double fontSize, Rect box)
    {
        var list = new List<Double>();

        foreach (var token in text.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (token.Length > 0 && (Char.IsDigit(token[0]) || token[0] is '-' or '+' or '.'))
            {
                list.Add(Length(token, Math.Min(box.Width, box.Height), dimensions, fontSize));
            }
        }

        return list;
    }

    private static Double Length(String token, Double basis, IRenderDimensions dimensions, Double fontSize)
    {
        if (token.EndsWith('%'))
        {
            return Parse(token[..^1]) / 100 * basis;
        }

        if (CssLengthValue(token) is { } length)
        {
            return LengthReader.ToPixel(length, RenderMode.Horizontal, dimensions, fontSize);
        }

        return Parse(token);
    }

    private static Boolean IsAngle(String token) =>
        token.EndsWith("deg", StringComparison.OrdinalIgnoreCase) ||
        token.EndsWith("rad", StringComparison.OrdinalIgnoreCase) ||
        token.EndsWith("turn", StringComparison.OrdinalIgnoreCase) ||
        token.EndsWith("grad", StringComparison.OrdinalIgnoreCase);

    private static Double Sq(Double v) => v * v;

    private static String? ExtractQuoted(String text, Int32 from)
    {
        var quote = -1;

        for (var i = from; i < text.Length; i++)
        {
            if (text[i] is '\'' or '"')
            {
                quote = i;
                break;
            }
        }

        if (quote < 0)
        {
            return null;
        }

        var close = text.IndexOf(text[quote], quote + 1);

        return close < 0 ? null : text[(quote + 1)..close];
    }

    private static Double ResolveDistance(String? text, Double total, IRenderDimensions dimensions, Double fontSize)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return 0;
        }

        var token = text.Trim();

        if (token.EndsWith('%'))
        {
            return Parse(token[..^1]) / 100 * total;
        }

        if (CssLengthValue(token) is { } length)
        {
            return LengthReader.ToPixel(length, RenderMode.Horizontal, dimensions, fontSize);
        }

        return Parse(token);
    }

    private static Double ResolveRotate(String? text, Double tangent)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return tangent; // auto
        }

        var lower = text.Trim().ToLowerInvariant();
        var extra = 0.0;
        var baseAngle = tangent;

        // An angle may ride alongside auto/reverse; pull the keywords out and add whatever angle remains.
        if (lower.Contains("reverse"))
        {
            baseAngle = tangent + Math.PI;
            lower = lower.Replace("reverse", String.Empty);
        }
        else if (lower.Contains("auto"))
        {
            lower = lower.Replace("auto", String.Empty);
        }
        else
        {
            baseAngle = 0; // a bare angle is an absolute rotation
        }

        foreach (var token in lower.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            extra += ToRadians(token);
        }

        return baseAngle + extra;
    }

    private static (Double X, Double Y) ResolvePosition(String text, Double width, Double height, IRenderDimensions dimensions, Double fontSize)
    {
        var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var x = width / 2;
        var y = height / 2;

        if (parts.Length >= 1)
        {
            x = Axis(parts[0], width, dimensions, fontSize, new[] { "left", "center", "right" });
        }

        if (parts.Length >= 2)
        {
            y = Axis(parts[1], height, dimensions, fontSize, new[] { "top", "center", "bottom" });
        }

        return (x, y);
    }

    private static Double Axis(String token, Double basis, IRenderDimensions dimensions, Double fontSize, String[] keywords)
    {
        switch (token.ToLowerInvariant())
        {
            case "left":
            case "top":
                return 0;
            case "center":
                return basis / 2;
            case "right":
            case "bottom":
                return basis;
        }

        if (token.EndsWith('%'))
        {
            return Parse(token[..^1]) / 100 * basis;
        }

        if (CssLengthValue(token) is { } length)
        {
            return LengthReader.ToPixel(length, RenderMode.Horizontal, dimensions, fontSize);
        }

        return basis / 2;
    }

    private static AngleSharp.Css.Values.CssLengthValue? CssLengthValue(String token) =>
        AngleSharp.Css.Values.CssLengthValue.TryParse(token, out var length) ? length : null;

    private static Double ToRadians(String token)
    {
        var text = token.Trim();
        var i = 0;

        while (i < text.Length && (Char.IsDigit(text[i]) || text[i] is '.' or '-' or '+'))
        {
            i++;
        }

        var value = Parse(text[..i]);
        var unit = text[i..].Trim().ToLowerInvariant();

        return unit switch
        {
            "rad" => value,
            "grad" => value * Math.PI / 200,
            "turn" => value * 2 * Math.PI,
            _ => value * Math.PI / 180,
        };
    }

    private static Double Parse(String text) =>
        Double.TryParse(text.Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 0;
}

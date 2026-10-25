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
/// Covers <c>offset-path: path('…)</c> with <c>offset-distance</c>, <c>offset-rotate</c>
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
        LayoutContext context,
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

        var path = ParsePath(pathText, frame, context, style, dimensions, fontSize, out var pathOriginX, out var pathOriginY);

        // A shape with no length at all still has a point to stand on - a circle whose radius came out
        // zero is a point, and an element on it is still translated onto that point.
        if (path is null)
        {
            return null;
        }

        var distance = path.Total > 0 ? ResolveDistance(style.OffsetValue("offset-distance"), path.Total, dimensions, fontSize) : 0;

        if (path.Total > 0)
        {
            distance = path.Closed
                ? ((distance % path.Total) + path.Total) % path.Total
                : Math.Clamp(distance, 0, path.Total);
        }

        var (px, py, tangent) = path.PointAt(distance);


        // The offset position in document coordinates: the path point added to the origin the path was
        // drawn from (the element's own box for path(), the containing block for a shape or ray).
        var positionX = pathOriginX + px;
        var positionY = pathOriginY + py;

        // The anchor point defaults to the transform-origin, both measured within the box. The offset
        // transform is written `translate(offset position) rotate(angle) translate(-anchor)`: the
        // rotation turns the box about the transform-origin the painter already applies the matrix
        // about, so the translate before it slides the anchor into place and the one after it takes the
        // anchor back out again. Reading the two apart is what lets an explicit offset-anchor land the
        // named corner on the path rather than the middle of the box.
        var anchorX = originX;
        var anchorY = originY;

        if (style.OffsetValue("offset-anchor") is { } anchor && !anchor.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            var (ax, ay) = ResolvePosition(anchor, frame.Width, frame.Height, dimensions, fontSize);
            anchorX = ax;
            anchorY = ay;
        }

        var angle = ResolveRotate(style.OffsetValue("offset-rotate"), tangent);

        var translate = new Affine(1, 0, 0, 1, positionX - (frame.X + originX), positionY - (frame.Y + originY));
        var cos = Math.Cos(angle);
        var sin = Math.Sin(angle);
        var rotate = new Affine(cos, sin, -sin, cos, 0, 0);
        var recenter = new Affine(1, 0, 0, 1, originX - anchorX, originY - anchorY);

        return translate.Then(rotate).Then(recenter);
    }

    /// <summary>
    /// Builds the offset path from an <c>offset-path</c> value, and reports the document point its own
    /// (0,0) sits at. <c>path()</c> is drawn from the element's own box; the basic shapes and <c>ray()</c>
    /// are laid out in the reference box the value's <c>&lt;coord-box&gt;</c> names, which defaults to the
    /// border box of the element that establishes the containing block. Returns null for <c>none</c> or a
    /// form not yet handled.
    /// </summary>
    private static MotionPath? ParsePath(
        String text,
        Rect frame,
        LayoutContext context,
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

        var (box, radius) = ReferenceBox(lower, context);

        // A shape or ray is drawn in the reference box's coordinate system.
        originX = box.X;
        originY = box.Y;
        var args = Inner(text);

        if (lower.StartsWith("circle(", StringComparison.OrdinalIgnoreCase) ||
            lower.StartsWith("ellipse(", StringComparison.OrdinalIgnoreCase))
        {
            return ParseEllipse(args, lower.StartsWith("circle(", StringComparison.OrdinalIgnoreCase), box, frame, style, dimensions, fontSize);
        }

        if (lower.StartsWith("inset(", StringComparison.OrdinalIgnoreCase))
        {
            return ParseInset(args, box, dimensions, fontSize);
        }

        if (lower.StartsWith("rect(", StringComparison.OrdinalIgnoreCase))
        {
            return ParseRect(args, box, dimensions, fontSize);
        }

        if (lower.StartsWith("xywh(", StringComparison.OrdinalIgnoreCase))
        {
            return ParseXywh(args, box, dimensions, fontSize);
        }

        if (lower.StartsWith("polygon(", StringComparison.OrdinalIgnoreCase))
        {
            return ParsePolygon(args, box, dimensions, fontSize);
        }

        if (lower.StartsWith("shape(", StringComparison.OrdinalIgnoreCase))
        {
            return ParseShape(args, box, dimensions, fontSize);
        }

        if (lower.StartsWith("ray(", StringComparison.OrdinalIgnoreCase))
        {
            return ParseRay(args, box, frame, style, dimensions, fontSize);
        }

        // A box with no path function: the path is inset(0 round <the box's radius>), the outline of
        // the box itself (Motion Path §offset-path, "<coord-box>" alone). Its coordinates are written
        // from the box's own corner, which the caller adds to the path origin.
        if (CoordBox(lower) is not null)
        {
            return MotionPath.FromRect(0, 0, box.Width, box.Height, radius);
        }

        return null;
    }

    /// <summary>
    /// The box a shape sizes into and the radius its outline carries: the <c>&lt;coord-box&gt;</c> named at
    /// the end of the value, or the border box of the element establishing the containing block.
    /// </summary>
    private static (Rect Box, Double Radius) ReferenceBox(String text, LayoutContext context)
    {
        var border = context.ReferenceBorderBox ?? context.ContainingBlockRect;
        var padding = context.ReferencePaddingBox ?? context.ContainingBlockRect;
        var content = context.ContainingBlockRect;
        var radii = context.ReferenceRadii;
        var coord = CoordBox(text);

        var box = coord switch
        {
            "padding-box" => padding,
            "content-box" => content,
            _ => border,
        };

        // Only the border box's own radius is known here; a padding or content box would need the
        // border widths and padding of the element that owns it, which the path only reads for the
        // rounded outline of a bare <coord-box>.
        var radius = coord is null or "border-box" && radii is { } corners
            ? Math.Max(corners.TopLeft.Horizontal, corners.TopLeft.Vertical)
            : 0;

        return (box, radius);
    }

    /// <summary>The <c>&lt;coord-box&gt;</c> keyword trailing a path value, if one is written.</summary>
    private static String? CoordBox(String text)
    {
        var close = text.LastIndexOf(')');
        var tail = close >= 0 ? text[(close + 1)..].Trim() : text.Trim();

        return tail.ToLowerInvariant() switch
        {
            "border-box" => "border-box",
            "padding-box" => "padding-box",
            "content-box" => "content-box",
            "margin-box" => "margin-box",
            "stroke-box" => "stroke-box",
            "fill-box" => "fill-box",
            "view-box" => "view-box",
            _ => null,
        };
    }

    /// <summary>The text inside the outermost parentheses.</summary>
    private static String Inner(String text)
    {
        var open = text.IndexOf('(');
        var close = text.LastIndexOf(')');

        return open < 0 || close <= open ? String.Empty : text[(open + 1)..close];
    }

    private static MotionPath? ParseEllipse(String args, Boolean circle, Rect box, Rect frame, ComputedStyle style, IRenderDimensions dimensions, Double fontSize)
    {
        var (centerX, centerY) = CenterOf(args, box, frame, style, dimensions, fontSize);

        if (circle)
        {
            // A circle has one radius, so the closest or farthest side is measured over all four sides
            // rather than per axis the way an ellipse reads them.
            var radius = CircleRadius(args, centerX, centerY, box, dimensions, fontSize);
            return MotionPath.FromEllipse(centerX, centerY, radius, radius);
        }

        var rx = Radius(args, "closest-side", centerX, centerY, box, true, dimensions, fontSize);
        var ry = Radius(args, "closest-side", centerX, centerY, box, false, dimensions, fontSize);

        return MotionPath.FromEllipse(centerX, centerY, rx, ry);
    }

    /// <summary>The one radius of a <c>circle()</c>: an explicit length, or a keyword read over the
    /// whole box rather than one axis of it.</summary>
    private static Double CircleRadius(String args, Double cx, Double cy, Rect box, IRenderDimensions dimensions, Double fontSize)
    {
        var head = args;
        var at = head.IndexOf(" at ", StringComparison.OrdinalIgnoreCase);

        if (at >= 0)
        {
            head = head[..at];
        }

        foreach (var token in head.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            switch (token.ToLowerInvariant())
            {
                case "closest-side":
                    return Math.Min(Math.Min(cx, box.Width - cx), Math.Min(cy, box.Height - cy));
                case "farthest-side":
                    return Math.Max(Math.Max(cx, box.Width - cx), Math.Max(cy, box.Height - cy));
                case "closest-corner":
                    return CornerDistance(cx, cy, box, true);
                case "farthest-corner":
                    return CornerDistance(cx, cy, box, false);
            }
        }

        var numbers = NumbersBeforeAt(args);

        return numbers.Count > 0
            ? Length(numbers[0], Math.Sqrt(Sq(box.Width) + Sq(box.Height)) / Math.Sqrt(2), dimensions, fontSize)
            : Math.Min(Math.Min(cx, box.Width - cx), Math.Min(cy, box.Height - cy));
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

        var (top, right, bottom, left) = Edges(head, box, dimensions, fontSize, 0, box.Width, box.Height, 0);

        return MotionPath.FromRect(left, top, Math.Max(0, box.Width - left - right), Math.Max(0, box.Height - top - bottom), round);
    }

    /// <summary>
    /// The rectangle of <c>rect()</c>: the four edges of the reference box's outline, where <c>auto</c>
    /// stands for the edge itself (the box's top and left, or its right and bottom), and the optional
    /// <c>round</c> carries the corner radius.
    /// </summary>
    private static MotionPath? ParseRect(String args, Rect box, IRenderDimensions dimensions, Double fontSize)
    {
        var round = 0.0;
        var rAt = args.IndexOf("round", StringComparison.OrdinalIgnoreCase);
        var head = args;

        if (rAt >= 0)
        {
            head = args[..rAt];
            round = Length(args[(rAt + 5)..].Trim(), Math.Min(box.Width, box.Height), dimensions, fontSize);
        }

        var (top, right, bottom, left) = Edges(head, box, dimensions, fontSize, 0, box.Width, box.Height, 0);

        return MotionPath.FromRect(left, top, Math.Max(0, right - left), Math.Max(0, bottom - top), round);
    }

    /// <summary>The rectangle of <c>xywh()</c>: its origin and size, with each percentage taken against
    /// the axis its number measures.</summary>
    private static MotionPath? ParseXywh(String args, Rect box, IRenderDimensions dimensions, Double fontSize)
    {
        var round = 0.0;
        var rAt = args.IndexOf("round", StringComparison.OrdinalIgnoreCase);
        var head = args;

        if (rAt >= 0)
        {
            head = args[..rAt];
            round = Length(args[(rAt + 5)..].Trim(), Math.Min(box.Width, box.Height), dimensions, fontSize);
        }

        var tokens = head.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries);

        Double V(Int32 index, Double basis) =>
            index < tokens.Length ? Length(tokens[index], basis, dimensions, fontSize) : 0;

        var x = V(0, box.Width);
        var y = V(1, box.Height);
        var width = V(2, box.Width);
        var height = V(3, box.Height);

        return MotionPath.FromRect(x, y, Math.Max(0, width), Math.Max(0, height), round);
    }

    /// <summary>
    /// Reads the four edges of an inset-style rectangle - <c>inset()</c>, <c>rect()</c> and <c>xywh()</c>
    /// share the shape of their arguments - with each percentage resolved against the axis it measures and
    /// <c>auto</c> answered by the fallback for that edge.
    /// </summary>
    private static (Double Top, Double Right, Double Bottom, Double Left) Edges(
        String head,
        Rect box,
        IRenderDimensions dimensions,
        Double fontSize,
        Double autoTop,
        Double autoRight,
        Double autoBottom,
        Double autoLeft)
    {
        var tokens = head.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries);
        String At(Int32 index) => tokens.Length == 0 ? "0" : tokens[Math.Min(index, tokens.Length - 1)];

        Double Edge(String token, Double basis, Double auto)
        {
            if (token.Equals("auto", StringComparison.OrdinalIgnoreCase))
            {
                return auto;
            }

            return token.EndsWith('%')
                ? Parse(token[..^1]) / 100 * basis
                : Length(token, basis, dimensions, fontSize);
        }

        return (
            Edge(At(0), box.Height, autoTop),
            Edge(At(1), box.Width, autoRight),
            Edge(At(2), box.Height, autoBottom),
            Edge(At(3), box.Width, autoLeft));
    }

    private static MotionPath? ParsePolygon(String args, Rect box, IRenderDimensions dimensions, Double fontSize)
    {
        var points = new List<(Double X, Double Y)>();

        foreach (var pair in SplitTopLevel(args, ','))
        {
            var numbers = Words(pair);

            if (numbers.Count >= 2)
            {
                points.Add((
                    Number(numbers[0], box.Width, dimensions, fontSize),
                    Number(numbers[1], box.Height, dimensions, fontSize)));
            }
        }

        return points.Count >= 2 ? MotionPath.FromPolygon(points) : null;
    }

    /// <summary>
    /// The path of <c>shape()</c>: a run of drawing commands in the reference box's coordinates, which is
    /// turned into path data so the same reader that handles <c>path()</c> measures it.
    /// </summary>
    private static MotionPath? ParseShape(String args, Rect box, IRenderDimensions dimensions, Double fontSize)
    {
        var data = new System.Text.StringBuilder();
        var started = false;

        foreach (var command in SplitTopLevel(args, ','))
        {
            var words = Words(command);

            if (words.Count == 0)
            {
                continue;
            }

            var name = words[0].ToLowerInvariant();

            if (name == "close")
            {
                data.Append(" Z");
                continue;
            }

            switch (name)
            {
                case "from" when words.Count >= 3:
                    data.Append($" M {X(words[1])} {Y(words[2])}");
                    started = true;
                    break;

                case "line" when words.Count >= 4:
                    data.Append(words[1].Equals("by", StringComparison.OrdinalIgnoreCase)
                        ? $" l {X(words[2])} {Y(words[3])}"
                        : $" L {X(words[2])} {Y(words[3])}");
                    break;

                case "hline" when words.Count >= 3:
                    data.Append(words[1].Equals("by", StringComparison.OrdinalIgnoreCase)
                        ? $" h {X(words[2])}"
                        : $" H {X(words[2])}");
                    break;

                case "vline" when words.Count >= 3:
                    data.Append(words[1].Equals("by", StringComparison.OrdinalIgnoreCase)
                        ? $" v {Y(words[2])}"
                        : $" V {Y(words[2])}");
                    break;

                case "curve" when words.Count >= 8:
                    data.Append($" C {X(words[2])} {Y(words[3])} {X(words[4])} {Y(words[5])} {X(words[6])} {Y(words[7])}");
                    break;
            }

            String X(String token) => Number(token, box.Width, dimensions, fontSize).ToString(System.Globalization.CultureInfo.InvariantCulture);
            String Y(String token) => Number(token, box.Height, dimensions, fontSize).ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        return started ? MotionPath.FromPathData(data.ToString()) : null;
    }

    /// <summary>Splits a value at its top-level commas, leaving the ones inside a function alone.</summary>
    private static List<String> SplitTopLevel(String text, Char separator)
    {
        var parts = new List<String>();
        var depth = 0;
        var start = 0;

        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '(')
            {
                depth++;
            }
            else if (text[i] == ')')
            {
                depth--;
            }
            else if (text[i] == separator && depth == 0)
            {
                parts.Add(text[start..i]);
                start = i + 1;
            }
        }

        parts.Add(text[start..]);
        return parts;
    }

    /// <summary>Splits a command into words, keeping a space inside <c>calc()</c> with its expression.</summary>
    private static List<String> Words(String text)
    {
        var words = new List<String>();
        var depth = 0;
        var start = -1;

        for (var i = 0; i <= text.Length; i++)
        {
            var end = i == text.Length;
            var c = end ? ' ' : text[i];

            if (!end)
            {
                if (c == '(')
                {
                    depth++;
                }
                else if (c == ')')
                {
                    depth--;
                }
            }

            var blank = Char.IsWhiteSpace(c) && depth == 0;

            if (blank || end)
            {
                if (start >= 0)
                {
                    words.Add(text[start..i]);
                    start = -1;
                }
            }
            else if (start < 0)
            {
                start = i;
            }
        }

        return words;
    }

    /// <summary>Reads one coordinate: a length, a percentage of the axis, or the arithmetic of a
    /// <c>calc()</c> over both.</summary>
    private static Double Number(String token, Double basis, IRenderDimensions dimensions, Double fontSize)
    {
        var text = token.Trim();

        if (CalcText.Read(text, Atom) is { } calc)
        {
            return CalcText.ToLength(calc).Resolve(basis);
        }

        return Atom(text)?.Resolve(basis) ?? 0;

        StyleLength? Atom(String atom)
        {
            var trimmed = atom.Trim();

            if (trimmed.EndsWith('%') &&
                Double.TryParse(trimmed[..^1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var percent))
            {
                return StyleLength.FromPercent(percent / 100);
            }

            return CssLengthValue(trimmed) is { } value
                ? StyleLength.FromPixels(LengthReader.ToPixel(value, RenderMode.Horizontal, dimensions, fontSize))
                : null;
        }
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
                // The position runs to the end of the arguments: `at 100px 30%` is one position, not
                // just the first word of one.
                var pos = String.Join(" ", tokens[(i + 1)..]);
                var xy = ResolvePosition(pos, box.Width, box.Height, dimensions, fontSize);
                at = (xy.X, xy.Y);
                break;
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

        // The sides are read as lines that reach to infinity, so a start outside the box still has a
        // closest side - and the distance is how far the line is, not which way the point stands.
        var left = Math.Abs(ox);
        var right = Math.Abs(w - ox);
        var top = Math.Abs(oy);
        var bottom = Math.Abs(h - oy);

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
        // A ray that starts outside the box entirely has nowhere to go: the distance to the boundary
        // is zero. A start that merely lies on an edge still runs across the box to the far side.
        if (ox < 0 || oy < 0 || ox > w || oy > h)
        {
            return 0;
        }

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

    private static (Double X, Double Y) CenterOf(String args, Rect box, Rect frame, ComputedStyle style, IRenderDimensions dimensions, Double fontSize)
    {
        var at = args.IndexOf(" at ", StringComparison.OrdinalIgnoreCase);

        if (at >= 0)
        {
            var pos = args[(at + 4)..].Trim();
            var xy = ResolvePosition(pos, box.Width, box.Height, dimensions, fontSize);
            return (xy.X, xy.Y);
        }

        // With no "at", a declared offset-position is the shape's starting position: `auto` is the
        // element's own box origin (in the containing block's coordinates), and a position lands in
        // the containing block. Only `normal` leaves the function's own default, the middle.
        var position = style.OffsetValue("offset-position");

        if (position is { Length: > 0 })
        {
            var trimmed = position.Trim();

            if (trimmed.Equals("auto", StringComparison.OrdinalIgnoreCase))
            {
                return (frame.X - box.X, frame.Y - box.Y);
            }

            if (!trimmed.Equals("normal", StringComparison.OrdinalIgnoreCase))
            {
                var xy = ResolvePosition(trimmed, box.Width, box.Height, dimensions, fontSize);
                return (xy.X, xy.Y);
            }
        }

        return (box.Width / 2, box.Height / 2);
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
            // An explicit ellipse radius takes its percentage from the axis it measures: the box's width
            // for rx, its height for ry.
            var first = Length(numbers[horizontal ? 0 : Math.Min(1, numbers.Count - 1)],
                horizontal ? box.Width : box.Height, dimensions, fontSize);
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
        var parts = Words(text);

        // A position of three or four parts names an edge and the distance in from it:
        // `bottom 25% right 25%` is a quarter in from the bottom and a quarter in from the right.
        if (parts.Count >= 3)
        {
            Double? edgeX = null;
            Double? edgeY = null;

            for (var i = 0; i < parts.Count; i++)
            {
                var keyword = parts[i].ToLowerInvariant();
                var hasOffset = IsEdge(keyword) && i + 1 < parts.Count && !IsEdge(parts[i + 1].ToLowerInvariant()) && !parts[i + 1].Equals("center", StringComparison.OrdinalIgnoreCase);
                var offset = hasOffset ? Number(parts[i + 1], keyword is "left" or "right" ? width : height, dimensions, fontSize) : 0;

                switch (keyword)
                {
                    case "left":
                        edgeX ??= offset;
                        break;
                    case "right":
                        edgeX ??= width - offset;
                        break;
                    case "top":
                        edgeY ??= offset;
                        break;
                    case "bottom":
                        edgeY ??= height - offset;
                        break;
                    case "center":
                        if (edgeX is null)
                        {
                            edgeX = width / 2;
                        }
                        else
                        {
                            edgeY ??= height / 2;
                        }

                        break;
                }

                if (hasOffset)
                {
                    i++;
                }
            }

            return (edgeX ?? width / 2, edgeY ?? height / 2);
        }

        // One or two parts read by the axis each names, in either order.
        return TransformValue.ResolveOrigin(text, width, height, dimensions, fontSize);
    }

    private static Boolean IsEdge(String token) =>
        token is "left" or "right" or "top" or "bottom";

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

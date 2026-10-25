using AngleSharp.Css;
using AngleSharp.Css.Values;
using System;
using System.Collections.Generic;
using System.Globalization;

namespace Hutu.Rendering.Layout;

/// <summary>
/// A 2D transform, stored the way CSS's <c>matrix(a, b, c, d, e, f)</c> reads: a point maps as
/// <c>x' = A·x + C·y + E</c>, <c>y' = B·x + D·y + F</c>. It is kept here rather than as a backend
/// matrix so the layout and paint sides stay platform-neutral; the Skia backend folds it into an
/// <c>SKMatrix</c> when it draws.
/// </summary>
/// <remarks>
/// The three <c>P</c> terms are the bottom row of the same 3×3 matrix, which is where a perspective
/// divide lives: when they are not <c>0, 0, 1</c> the point is read as
/// <c>(A·x + C·y + E) / (P0·x + P1·y + P2)</c>, and a flat box turned into a plane is drawn as the
/// quadrilateral that divide makes of its rectangle (CSS Transforms 2 §3).
/// </remarks>
public readonly struct Affine
{
    public Affine(Double a, Double b, Double c, Double d, Double e, Double f, Double p0 = 0, Double p1 = 0, Double p2 = 1)
    {
        A = a;
        B = b;
        C = c;
        D = d;
        E = e;
        F = f;
        P0 = p0;
        P1 = p1;
        P2 = p2;
    }

    public Double A { get; }

    public Double B { get; }

    public Double C { get; }

    public Double D { get; }

    public Double E { get; }

    public Double F { get; }

    /// <summary>Gets the first term of the perspective row, which carries x into the divide.</summary>
    public Double P0 { get; }

    /// <summary>Gets the second term of the perspective row, which carries y into the divide.</summary>
    public Double P1 { get; }

    /// <summary>Gets the constant of the perspective row.</summary>
    public Double P2 { get; }

    public static Affine Identity { get; } = new(1, 0, 0, 1, 0, 0);

    public Boolean IsIdentity =>
        A == 1 && B == 0 && C == 0 && D == 1 && E == 0 && F == 0 && P0 == 0 && P1 == 0 && P2 == 1;

    /// <summary>Gets whether the transform divides by anything other than one, which is what makes a
    /// rectangle come out as a quadrilateral rather than a parallelogram.</summary>
    public Boolean IsPerspective => P0 != 0 || P1 != 0 || P2 != 1;

    /// <summary>Composes two transforms so that a point is carried by <c>other</c> first, then this.</summary>
    public Affine Then(Affine other) => new(
        A * other.A + C * other.B + E * other.P0,
        B * other.A + D * other.B + F * other.P0,
        A * other.C + C * other.D + E * other.P1,
        B * other.C + D * other.D + F * other.P1,
        A * other.E + C * other.F + E * other.P2,
        B * other.E + D * other.F + F * other.P2,
        P0 * other.A + P1 * other.B + P2 * other.P0,
        P0 * other.C + P1 * other.D + P2 * other.P1,
        P0 * other.E + P1 * other.F + P2 * other.P2);

    /// <summary>Maps a point through the transform.</summary>
    public (Double X, Double Y) Map(Double x, Double y)
    {
        var w = P0 * x + P1 * y + P2;

        if (w == 0)
        {
            return (0, 0);
        }

        return ((A * x + C * y + E) / w, (B * x + D * y + F) / w);
    }

    /// <summary>
    /// Gets the transform that undoes this one, for mapping a pointer from where a transformed box is
    /// drawn back to where its layout put it. A singular matrix (zero determinant) has no inverse and
    /// answers as itself, which leaves the point where it was rather than throwing.
    /// </summary>
    public Affine Inverse()
    {
        // The adjugate of the 3×3 matrix, divided by its determinant.
        var c11 = D * P2 - F * P1;
        var c12 = F * P0 - B * P2;
        var c13 = B * P1 - D * P0;

        var det = A * c11 + C * c12 + E * c13;

        if (det == 0)
        {
            return Identity;
        }

        return new Affine(
            c11 / det,
            c12 / det,
            (E * P1 - C * P2) / det,
            (A * P2 - E * P0) / det,
            (C * F - E * D) / det,
            (E * B - A * F) / det,
            c13 / det,
            (C * P0 - A * P1) / det,
            (A * D - B * C) / det);
    }
}

/// <summary>
/// The <c>transform</c> and <c>transform-origin</c> longhands of a box, parsed from their cascaded
/// text and resolved to a single affine matrix once the box's own size is known.
/// </summary>
/// <remarks>
/// CSS Transforms Level 1, 2D: the functions are <c>matrix</c>, <c>translate</c>/<c>translateX/Y</c>,
/// <c>scale</c>/<c>scaleX/Y</c>, <c>rotate</c> and <c>skew</c>/<c>skewX/Y</c>. A transform list is
/// composed left to right - <c>M = M1·M2·…·Mn</c> - and applied to a point as <c>M·p</c>, so the
/// leftmost function is the outermost. <c>translate</c> percentages are relative to the border box
/// (its width on x, its height on y) and <c>transform-origin</c> to the same, which is why resolution
/// waits for the box's size rather than happening when the style was computed.
/// </remarks>
internal sealed class TransformValue
{
    private readonly List<(String Name, String[] Args)> _ops = new();

    private TransformValue()
    {
    }

    /// <summary>Gets whether the box carries any transform at all (<c>none</c> or empty is none).</summary>
    public Boolean IsEmpty => _ops.Count == 0;

    /// <summary>
    /// Parses a <c>transform</c> value into its function list. Returns null for <c>none</c> or empty;
    /// an unrecognised function is skipped rather than failing the whole list, so one bad token never
    /// drops the transforms beside it.
    /// </summary>
    public static TransformValue? Parse(String? text)
    {
        if (String.IsNullOrWhiteSpace(text) || text.Trim().Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var value = new TransformValue();

        foreach ((var name, var args) in SplitFunctions(text))
        {
            value._ops.Add((name.ToLowerInvariant(), args));
        }

        return value._ops.Count == 0 ? null : value;
    }

    /// <summary>
    /// Resolves the whole list to one matrix against a box of the given size. The zoom is the
    /// scale the box is drawn at: the two distances of a move - and the two of a <c>matrix()</c>, which
    /// are the same thing written as numbers - are lengths and grow with it, while a ratio, an angle and
    /// a share of the box are not and do not.
    /// </summary>
    public Affine Resolve(Double width, Double height, IRenderDimensions dimensions, Double fontSize, Double zoom = 1) =>
        Resolve4(width, height, dimensions, fontSize, zoom).Project();

    /// <summary>
    /// Resolves the list to the full 4×4 matrix, which is where a function that leaves the plane keeps
    /// its third dimension.
    /// </summary>
    public Matrix4 Resolve4(Double width, Double height, IRenderDimensions dimensions, Double fontSize, Double zoom = 1)
    {
        var total = Matrix4.Identity;

        foreach (var (name, args) in _ops)
        {
            total = total.Then(Function(name, args, width, height, dimensions, fontSize, zoom));
        }

        return total;
    }

    private Matrix4 Function(String name, String[] args, Double width, Double height, IRenderDimensions dimensions, Double fontSize, Double zoom)
    {
        Double Length(Int32 index, Double basis, Double fallback) =>
            index < args.Length ? ToPixels(args[index], basis, dimensions, fontSize, zoom) : fallback;

        Double Number(Int32 index, Double fallback) =>
            index < args.Length && Double.TryParse(args[index], NumberStyles.Float, CultureInfo.InvariantCulture, out var n)
                ? n
                : fallback;

        Double Distance(Int32 index) => Number(index, 0) * zoom;

        Double Angle(Int32 index) => index < args.Length ? ToRadians(args[index]) : 0;

        switch (name)
        {
            case "translate":
                return Matrix4.Translation(Length(0, width, 0), args.Length > 1 ? Length(1, height, 0) : 0, 0);

            case "translatex":
                return Matrix4.Translation(Length(0, width, 0), 0, 0);

            case "translatey":
                return Matrix4.Translation(0, Length(0, height, 0), 0);

            case "translatez":
                return Matrix4.Translation(0, 0, Length(0, 0, 0));

            case "translate3d":
                return Matrix4.Translation(Length(0, width, 0), Length(1, height, 0), Length(2, 0, 0));

            case "scale":
                var sx = Number(0, 1);
                return Matrix4.Scaling(sx, args.Length > 1 ? Number(1, 1) : sx, 1);

            case "scalex":
                return Matrix4.Scaling(Number(0, 1), 1, 1);

            case "scaley":
                return Matrix4.Scaling(1, Number(0, 1), 1);

            case "scalez":
                return Matrix4.Scaling(1, 1, Number(0, 1));

            case "scale3d":
                return Matrix4.Scaling(Number(0, 1), Number(1, 1), Number(2, 1));

            case "rotate":
                return Matrix4.Rotation(0, 0, 1, Angle(0));

            case "rotatex":
                return Matrix4.Rotation(1, 0, 0, Angle(0));

            case "rotatey":
                return Matrix4.Rotation(0, 1, 0, Angle(0));

            case "rotatez":
                return Matrix4.Rotation(0, 0, 1, Angle(0));

            case "rotate3d":
                return Matrix4.Rotation(Number(0, 0), Number(1, 0), Number(2, 1), Angle(3));

            case "skewx":
                return Matrix4.SkewX(Angle(0));

            case "skewy":
                return Matrix4.SkewY(Angle(0));

            case "skew":
                return new Matrix4(
                    1, Math.Tan(Angle(0)), 0, 0,
                    Math.Tan(Angle(1)), 1, 0, 0,
                    0, 0, 1, 0,
                    0, 0, 0, 1);

            case "matrix":
                return new Matrix4(
                    Number(0, 1), Number(2, 0), 0, Distance(4),
                    Number(1, 0), Number(3, 1), 0, Distance(5),
                    0, 0, 1, 0,
                    0, 0, 0, 1);

            case "matrix3d":
                return Matrix3d(args, Distance);

            case "perspective":
                return Matrix4.Perspective(Length(0, 0, 0));

            default:
                return Matrix4.Identity;
        }
    }

    /// <summary>
    /// Reads <c>matrix3d()</c>'s sixteen numbers. CSS writes them column by column, so the cell each
    /// one names is the other way round from the argument's place in the list; the last four are the
    /// fourth column, which is where the move lives.
    /// </summary>
    private static Matrix4 Matrix3d(String[] args, Func<Int32, Double> distance)
    {
        if (args.Length != 16)
        {
            return Matrix4.Identity;
        }

        Double Cell(Int32 row, Int32 column)
        {
            var index = (column - 1) * 4 + (row - 1);

            return index is 12 or 13 or 14
                ? distance(index)
                : Double.TryParse(args[index], NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ? n : 0;
        }

        return new Matrix4(
            Cell(1, 1), Cell(1, 2), Cell(1, 3), Cell(1, 4),
            Cell(2, 1), Cell(2, 2), Cell(2, 3), Cell(2, 4),
            Cell(3, 1), Cell(3, 2), Cell(3, 3), Cell(3, 4),
            Cell(4, 1), Cell(4, 2), Cell(4, 3), Cell(4, 4));
    }

    /// <summary>
    /// Resolves <c>transform-origin</c> to a point in pixels from the box's top-left.
    /// </summary>
    /// <remarks>
    /// The value is a <c>&lt;position&gt;</c>, and a position is read by the axis each part names rather
    /// than by the place it stands in: left and right speak for the horizontal, top and bottom for the
    /// vertical, and the two may come in either order, so <c>top right</c> and <c>right top</c> are the
    /// same point. A length or percentage has no axis of its own and fills the horizontal first and the
    /// vertical after it, which is why <c>left 20px</c> puts the 20 on the vertical. An axis nobody names
    /// stands at half the box, and a third value - the offset along z - is nobody's business in a 2D
    /// transform.
    /// </remarks>
    public static (Double X, Double Y) ResolveOrigin(
        String? text,
        Double width,
        Double height,
        IRenderDimensions dimensions,
        Double fontSize,
        Double zoom = 1)
    {
        Double? x = null;
        Double? y = null;

        foreach (var part in SplitTop(text))
        {
            switch (part.ToLowerInvariant())
            {
                case "left":
                    x ??= 0;
                    break;

                case "right":
                    x ??= width;
                    break;

                case "top":
                    y ??= 0;
                    break;

                case "bottom":
                    y ??= height;
                    break;

                case "center":
                case "centre":
                    if (x is null)
                    {
                        x = width / 2;
                    }
                    else
                    {
                        y ??= height / 2;
                    }

                    break;

                default:
                    if (x is null)
                    {
                        x = ToPixels(part, width, dimensions, fontSize, zoom);
                    }
                    else if (y is null)
                    {
                        y = ToPixels(part, height, dimensions, fontSize, zoom);
                    }

                    break;
            }
        }

        return (x ?? width / 2, y ?? height / 2);
    }

    /// <summary>
    /// Reads a <c>perspective</c> distance in pixels, or zero for <c>none</c> and for a distance that
    /// puts the viewer at or behind the plane (which is the flat view).
    /// </summary>
    public static Double ResolvePerspective(String? text, IRenderDimensions dimensions, Double fontSize, Double zoom = 1)
    {
        if (text is not { Length: > 0 })
        {
            return 0;
        }

        var stated = text.Trim();

        if (stated.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        return ToPixels(stated, 0, dimensions, fontSize, zoom);
    }

    /// <summary>
    /// Reads the individual <c>translate</c>, <c>rotate</c> and <c>scale</c> properties and the
    /// <c>transform</c> list into the one matrix CSS Transforms 2 asks for: a move, then a turn, then a
    /// resize, then whatever <c>transform</c> says, in that order.
    /// </summary>
    public static Matrix4 Compose(
        String? translate,
        String? rotate,
        String? scale,
        TransformValue? transform,
        Double width,
        Double height,
        IRenderDimensions dimensions,
        Double fontSize,
        Double zoom = 1)
    {
        var matrix = Matrix4.Identity;

        if (Stated(translate) is { } move)
        {
            matrix = matrix.Then(IndividualTranslate(move, width, height, dimensions, fontSize, zoom));
        }

        if (Stated(rotate) is { } turn)
        {
            matrix = matrix.Then(IndividualRotate(turn));
        }

        if (Stated(scale) is { } size)
        {
            matrix = matrix.Then(IndividualScale(size));
        }

        if (transform is { } list)
        {
            matrix = matrix.Then(list.Resolve4(width, height, dimensions, fontSize, zoom));
        }

        return matrix;
    }

    private static String? Stated(String? text) =>
        text is { Length: > 0 } value && !value.Equals("none", StringComparison.OrdinalIgnoreCase) ? value : null;

    private static Matrix4 IndividualTranslate(String text, Double width, Double height, IRenderDimensions dimensions, Double fontSize, Double zoom)
    {
        var parts = SplitTop(text);

        Double At(Int32 index, Double basis) =>
            index < parts.Count ? ToPixels(parts[index], basis, dimensions, fontSize, zoom) : 0;

        return Matrix4.Translation(At(0, width), At(1, height), At(2, 0));
    }

    private static Matrix4 IndividualScale(String text)
    {
        var parts = SplitTop(text);

        Double At(Int32 index, Double fallback)
        {
            if (index >= parts.Count)
            {
                return fallback;
            }

            var token = parts[index].Trim();

            if (token.EndsWith('%') &&
                Double.TryParse(token[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var percent))
            {
                return percent / 100;
            }

            return Double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ? number : fallback;
        }

        var x = At(0, 1);

        return Matrix4.Scaling(x, At(1, x), At(2, 1));
    }

    private static Matrix4 IndividualRotate(String text)
    {
        var parts = SplitTop(text);

        if (parts.Count == 1)
        {
            return Matrix4.Rotation(0, 0, 1, ToRadians(parts[0]));
        }

        if (parts.Count == 2)
        {
            return parts[0].ToLowerInvariant() switch
            {
                "x" => Matrix4.Rotation(1, 0, 0, ToRadians(parts[1])),
                "y" => Matrix4.Rotation(0, 1, 0, ToRadians(parts[1])),
                "z" => Matrix4.Rotation(0, 0, 1, ToRadians(parts[1])),
                _ => Matrix4.Rotation(0, 0, 1, ToRadians(parts[1])),
            };
        }

        Double Axis(Int32 index) =>
            Double.TryParse(parts[index], NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ? n : 0;

        return Matrix4.Rotation(Axis(0), Axis(1), Axis(2), ToRadians(parts[3]));
    }

    /// <summary>
    /// Reads the z of a <c>transform-origin</c>, which is the last value when the origin gives one and
    /// is a length rather than a keyword or a share. It is where the plane a turned box is drawn on is
    /// cut through the third dimension.
    /// </summary>
    public static Double ResolveOriginZ(
        String? text,
        Double width,
        Double height,
        IRenderDimensions dimensions,
        Double fontSize,
        Double zoom = 1)
    {
        var parts = SplitTop(text);

        if (parts.Count < 3)
        {
            return 0;
        }

        var last = parts[^1].Trim();

        if (last.EndsWith('%') || last.Equals("left", StringComparison.OrdinalIgnoreCase) ||
            last.Equals("right", StringComparison.OrdinalIgnoreCase) ||
            last.Equals("top", StringComparison.OrdinalIgnoreCase) ||
            last.Equals("bottom", StringComparison.OrdinalIgnoreCase) ||
            last.Equals("center", StringComparison.OrdinalIgnoreCase) ||
            last.Equals("centre", StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        return ToPixels(last, width, dimensions, fontSize, zoom);
    }

    /// <summary>
    /// Turns one length token into pixels, resolving a percentage against the given basis. The basis is
    /// the box's own size and is already drawn at its zoom, so a share of it is grown by that zoom once,
    /// where a length the page stated is grown here. The font size folded against is the computed one,
    /// before the scale: a relative length is computed once and only then zoomed.
    /// </summary>
    private static Double ToPixels(String token, Double basis, IRenderDimensions dimensions, Double fontSize, Double zoom = 1)
    {
        if (!CssLengthValue.TryParse(token, out var length))
        {
            return 0;
        }

        if (length.Type == CssLengthValue.Unit.Percent)
        {
            return length.Value / 100 * basis;
        }

        try
        {
            return LengthReader.ToPixel(length, RenderMode.Horizontal, dimensions, fontSize) * zoom;
        }
        catch (Exception)
        {
            return 0;
        }
    }

    /// <summary>Reads an angle token - <c>deg</c>, <c>rad</c>, <c>grad</c>, <c>turn</c> - in radians.</summary>
    private static Double ToRadians(String token)
    {
        var text = token.Trim();
        var (number, unit) = SplitUnit(text);

        if (!Double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
        {
            return 0;
        }

        return unit switch
        {
            "rad" => value,
            "grad" => value * Math.PI / 200,
            "turn" => value * 2 * Math.PI,
            _ => value * Math.PI / 180,
        };
    }

    private static (String Number, String Unit) SplitUnit(String text)
    {
        var i = 0;

        while (i < text.Length && (Char.IsDigit(text[i]) || text[i] is '.' or '-' or '+'))
        {
            i++;
        }

        return (text[..i], text[i..].Trim().ToLowerInvariant());
    }

    /// <summary>Splits a transform list into its <c>name(args)</c> pieces, respecting nested parentheses.</summary>
    private static IEnumerable<(String Name, String[] Args)> SplitFunctions(String text)
    {
        var i = 0;

        while (i < text.Length)
        {
            var open = text.IndexOf('(', i);

            if (open < 0)
            {
                yield break;
            }

            var name = text[i..open].Trim();
            var close = Matching(text, open);

            if (close < 0)
            {
                yield break;
            }

            var inner = text[(open + 1)..close];

            if (name.Length > 0)
            {
                yield return (name, SplitTop(inner).ToArray());
            }

            i = close + 1;
        }
    }

    private static Int32 Matching(String text, Int32 open)
    {
        var depth = 0;

        for (var i = open; i < text.Length; i++)
        {
            if (text[i] == '(')
            {
                depth++;
            }
            else if (text[i] == ')')
            {
                if (--depth == 0)
                {
                    return i;
                }
            }
        }

        return -1;
    }

    private static List<String> SplitTop(String? text)
    {
        var parts = new List<String>();

        if (String.IsNullOrWhiteSpace(text))
        {
            return parts;
        }

        var depth = 0;
        var start = 0;

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];

            if (c == '(')
            {
                depth++;
            }
            else if (c == ')')
            {
                depth--;
            }
            else if ((c == ',' || c == ' ' || c == '\t') && depth == 0)
            {
                Add(parts, text, start, i);
                start = i + 1;
            }
        }

        Add(parts, text, start, text.Length);
        return parts;
    }

    private static void Add(List<String> parts, String text, Int32 from, Int32 to)
    {
        var piece = text[from..to].Trim();

        if (piece.Length > 0)
        {
            parts.Add(piece);
        }
    }
}

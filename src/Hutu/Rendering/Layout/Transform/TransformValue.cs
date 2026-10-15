using AngleSharp.Css;
using AngleSharp.Css.Values;
using System;
using System.Collections.Generic;
using System.Globalization;

namespace Hutu.Rendering.Layout;

/// <summary>
/// A 2D affine transform, stored the way CSS's <c>matrix(a, b, c, d, e, f)</c> reads: a point maps as
/// <c>x' = A·x + C·y + E</c>, <c>y' = B·x + D·y + F</c>. It is kept here rather than as a backend
/// matrix so the layout and paint sides stay platform-neutral; the Skia backend folds it into an
/// <c>SKMatrix</c> when it draws.
/// </summary>
public readonly struct Affine
{
    public Affine(Double a, Double b, Double c, Double d, Double e, Double f)
    {
        A = a;
        B = b;
        C = c;
        D = d;
        E = e;
        F = f;
    }

    public Double A { get; }

    public Double B { get; }

    public Double C { get; }

    public Double D { get; }

    public Double E { get; }

    public Double F { get; }

    public static Affine Identity { get; } = new(1, 0, 0, 1, 0, 0);

    public Boolean IsIdentity =>
        A == 1 && B == 0 && C == 0 && D == 1 && E == 0 && F == 0;

    /// <summary>Composes two transforms so that a point is carried by <c>other</c> first, then this.</summary>
    public Affine Then(Affine other) => new(
        A * other.A + C * other.B,
        B * other.A + D * other.B,
        A * other.C + C * other.D,
        B * other.C + D * other.D,
        A * other.E + C * other.F + E,
        B * other.E + D * other.F + F);

    /// <summary>
    /// Gets the transform that undoes this one, for mapping a pointer from where a transformed box is
    /// drawn back to where its layout put it. A singular matrix (zero determinant) has no inverse and
    /// answers as itself, which leaves the point where it was rather than throwing.
    /// </summary>
    public Affine Inverse()
    {
        var det = A * D - B * C;

        if (det == 0)
        {
            return Identity;
        }

        return new Affine(
            D / det, -B / det,
            -C / det, A / det,
            (C * F - D * E) / det, (B * E - A * F) / det);
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

    /// <summary>Resolves the whole list to one affine matrix against a box of the given size.</summary>
    public Affine Resolve(Double width, Double height, IRenderDimensions dimensions, Double fontSize)
    {
        var total = Affine.Identity;

        foreach (var (name, args) in _ops)
        {
            total = total.Then(Function(name, args, width, height, dimensions, fontSize));
        }

        return total;
    }

    private Affine Function(String name, String[] args, Double width, Double height, IRenderDimensions dimensions, Double fontSize)
    {
        Double Length(Int32 index, Double basis, Double fallback) =>
            index < args.Length ? ToPixels(args[index], basis, dimensions, fontSize) : fallback;

        Double Number(Int32 index, Double fallback) =>
            index < args.Length && Double.TryParse(args[index], NumberStyles.Float, CultureInfo.InvariantCulture, out var n)
                ? n
                : fallback;

        Double Angle(Int32 index) => index < args.Length ? ToRadians(args[index]) : 0;

        switch (name)
        {
            case "translate":
                return new Affine(1, 0, 0, 1, Length(0, width, 0), args.Length > 1 ? Length(1, height, 0) : 0);

            case "translatex":
                return new Affine(1, 0, 0, 1, Length(0, width, 0), 0);

            case "translatey":
                return new Affine(1, 0, 0, 1, 0, Length(0, height, 0));

            case "scale":
                var sx = Number(0, 1);
                return new Affine(sx, 0, 0, args.Length > 1 ? Number(1, 1) : sx, 0, 0);

            case "scalex":
                return new Affine(Number(0, 1), 0, 0, 1, 0, 0);

            case "scaley":
                return new Affine(1, 0, 0, Number(0, 1), 0, 0);

            case "rotate":
                var r = Angle(0);
                var cos = Math.Cos(r);
                var sin = Math.Sin(r);
                return new Affine(cos, sin, -sin, cos, 0, 0);

            case "skewx":
                return new Affine(1, 0, Math.Tan(Angle(0)), 1, 0, 0);

            case "skewy":
                return new Affine(1, Math.Tan(Angle(0)), 0, 1, 0, 0);

            case "skew":
                return new Affine(1, Math.Tan(Angle(1)), Math.Tan(Angle(0)), 1, 0, 0);

            case "matrix":
                return new Affine(
                    Number(0, 1), Number(1, 0), Number(2, 0), Number(3, 1), Number(4, 0), Number(5, 0));

            default:
                return Affine.Identity;
        }
    }

    /// <summary>
    /// Resolves <c>transform-origin</c> to a point in pixels from the box's top-left. One value sets
    /// the horizontal axis and leaves the vertical centred; two set both; a third (the z offset) is
    /// ignored by a 2D transform. Keywords stand for 0%, 50% or 100% on their axis.
    /// </summary>
    public static (Double X, Double Y) ResolveOrigin(
        String? text,
        Double width,
        Double height,
        IRenderDimensions dimensions,
        Double fontSize)
    {
        var parts = SplitTop(text);

        if (parts.Count == 0)
        {
            return (width / 2, height / 2);
        }

        var x = Axis(parts[0], width, dimensions, fontSize, horizontal: true, centre: width / 2);
        var y = parts.Count > 1
            ? Axis(parts[1], height, dimensions, fontSize, horizontal: false, centre: height / 2)
            : height / 2;

        return (x, y);
    }

    private static Double Axis(String token, Double basis, IRenderDimensions dimensions, Double fontSize, Boolean horizontal, Double centre)
    {
        switch (token.ToLowerInvariant())
        {
            case "left" when horizontal:
            case "top" when !horizontal:
                return 0;

            case "right" when horizontal:
            case "bottom" when !horizontal:
                return basis;

            case "center":
            case "centre":
                return centre;
        }

        return ToPixels(token, basis, dimensions, fontSize);
    }

    /// <summary>Turns one length token into pixels, resolving a percentage against the given basis.</summary>
    private static Double ToPixels(String token, Double basis, IRenderDimensions dimensions, Double fontSize)
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
            return LengthReader.ToPixel(length, RenderMode.Horizontal, dimensions, fontSize);
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

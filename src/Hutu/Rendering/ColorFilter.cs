using AngleSharp.Css.Values;
using System;
using System.Collections.Generic;
using System.Globalization;

namespace Hutu.Rendering;

/// <summary>
/// The half of <c>filter</c> that changes a colour without looking at its neighbours: the eight
/// functions of Filter Effects 1 §6 - <c>brightness</c>, <c>contrast</c>, <c>grayscale</c>,
/// <c>invert</c>, <c>opacity</c>, <c>saturate</c>, <c>sepia</c> and <c>hue-rotate</c>.
/// </summary>
/// <remarks>
/// Every one of them is a matrix over the non-premultiplied sRGB components (§6.1), which is why the
/// colour a box paints can be put through it where the colour is read, with no layer of its own. A
/// list that holds anything else - a <c>blur()</c>, an <c>url()</c> - is not read here at all: a filter
/// applied only in part would darken where the page asked for a darkening and a blur, which is neither
/// what the page asked for nor what it would have got.
/// </remarks>
internal readonly struct ColorFilter
{
    /// <summary>The four rows of the matrix, each with the five coefficients of the chapter's form.</summary>
    private readonly Double[][] _rows;

    private ColorFilter(Double[][] rows) => _rows = rows;

    /// <summary>Gets the filter a page that wrote none comes to: nothing is changed.</summary>
    internal static ColorFilter None => default;

    /// <summary>Gets whether the filter changes any colour at all.</summary>
    internal Boolean Applies => _rows is not null;

    /// <summary>
    /// Reads the filter list, or answers nothing where any function in it is not one of the eight the
    /// chapter gives a matrix for.
    /// </summary>
    internal static ColorFilter Parse(String? text)
    {
        var trimmed = text?.Trim();

        if (String.IsNullOrEmpty(trimmed) || trimmed.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            return None;
        }

        var total = Identity();

        foreach (var function in SplitFunctions(trimmed))
        {
            if (Matrix(function) is not { } matrix)
            {
                return None;
            }

            total = Multiply(matrix, total);
        }

        return new ColorFilter(total);
    }

    /// <summary>
    /// Gets the filter that applies this one and then the one it is handed - what an ancestor's filter
    /// makes of a subtree that carries a filter of its own (the inner one paints first).
    /// </summary>
    internal ColorFilter Then(ColorFilter inner) =>
        _rows is null ? inner
        : inner._rows is null ? this
        : new ColorFilter(Multiply(_rows, inner._rows));

    /// <summary>Gets whether two filters are the same answer, by the matrix each was read into.</summary>
    internal Boolean SameAs(ColorFilter other) => ReferenceEquals(_rows, other._rows);

    /// <summary>
    /// Gets the colour the filter makes of this one. Alpha is filtered too - <c>opacity()</c> is a row
    /// of the same matrix - so a transparent colour stays as transparent as the chapter says.
    /// </summary>
    internal CssColorValue Apply(CssColorValue colour)
    {
        if (_rows is null)
        {
            return colour;
        }

        var rows = _rows;
        var r = colour.R / 255.0;
        var g = colour.G / 255.0;
        var b = colour.B / 255.0;
        var a = colour.A;

        Double Channel(Int32 row) =>
            rows[row][0] * r + rows[row][1] * g + rows[row][2] * b + rows[row][3] * a + rows[row][4];

        return CssColorValue.FromRgba(
            ToByte(Channel(0)),
            ToByte(Channel(1)),
            ToByte(Channel(2)),
            Math.Clamp(Channel(3), 0.0, 1.0));
    }

    private static Byte ToByte(Double value) => (Byte)Math.Round(Math.Clamp(value, 0.0, 1.0) * 255.0);

    private static Double[][] Identity() =>
    [
        [1, 0, 0, 0, 0],
        [0, 1, 0, 0, 0],
        [0, 0, 1, 0, 0],
        [0, 0, 0, 1, 0],
    ];

    /// <summary>Gets the matrix one function stands for, or null where it is not a colour function.</summary>
    private static Double[][]? Matrix(String function)
    {
        var open = function.IndexOf('(');

        if (open <= 0 || !function.EndsWith(")", StringComparison.Ordinal))
        {
            return null;
        }

        var name = function.Substring(0, open).Trim().ToLowerInvariant();
        var argument = function.Substring(open + 1, function.Length - open - 2).Trim();

        // A function written without an argument stands for one, and an angle for hue-rotate is in
        // degrees unless it says otherwise.
        var value = name switch
        {
            "brightness" or "contrast" or "grayscale" or "invert" or "opacity" or "saturate" or "sepia" =>
                argument.Length == 0 ? 1.0 : Amount(argument),
            _ => 0.0,
        };

        if (name is not ("hue-rotate") && Double.IsNaN(value))
        {
            return null;
        }

        switch (name)
        {
            case "brightness":
                return Scale(value, value, value);

            case "contrast":
            {
                var offset = 0.5 - 0.5 * value;
                return
                [
                    [value, 0, 0, 0, offset],
                    [0, value, 0, 0, offset],
                    [0, 0, value, 0, offset],
                    [0, 0, 0, 1, 0],
                ];
            }

            case "grayscale":
                return Lerp(1 - value, Luminance());

            case "sepia":
                return Lerp(value,
                [
                    [0.393, 0.769, 0.189, 0, 0],
                    [0.349, 0.686, 0.168, 0, 0],
                    [0.272, 0.534, 0.131, 0, 0],
                    [0, 0, 0, 1, 0],
                ]);

            case "invert":
            {
                var scale = 1 - 2 * value;
                return
                [
                    [scale, 0, 0, 0, value],
                    [0, scale, 0, 0, value],
                    [0, 0, scale, 0, value],
                    [0, 0, 0, 1, 0],
                ];
            }

            case "opacity":
                return
                [
                    [1, 0, 0, 0, 0],
                    [0, 1, 0, 0, 0],
                    [0, 0, 1, 0, 0],
                    [0, 0, 0, value, 0],
                ];

            case "saturate":
                return
                [
                    [0.213 + 0.787 * value, 0.715 - 0.715 * value, 0.072 - 0.072 * value, 0, 0],
                    [0.213 - 0.213 * value, 0.715 + 0.285 * value, 0.072 - 0.072 * value, 0, 0],
                    [0.213 - 0.213 * value, 0.715 - 0.715 * value, 0.072 + 0.928 * value, 0, 0],
                    [0, 0, 0, 1, 0],
                ];

            case "hue-rotate":
            {
                var radians = Angle(argument);
                var cos = Math.Cos(radians);
                var sin = Math.Sin(radians);

                return
                [
                    [0.213 + cos * 0.787 - sin * 0.213, 0.715 - cos * 0.715 - sin * 0.715, 0.072 - cos * 0.072 + sin * 0.928, 0, 0],
                    [0.213 - cos * 0.213 + sin * 0.143, 0.715 + cos * 0.285 + sin * 0.140, 0.072 - cos * 0.072 - sin * 0.283, 0, 0],
                    [0.213 - cos * 0.213 - sin * 0.787, 0.715 - cos * 0.715 + sin * 0.715, 0.072 + cos * 0.928 + sin * 0.072, 0, 0],
                    [0, 0, 0, 1, 0],
                ];
            }

            default:
                return null;
        }
    }

    /// <summary>Gets the number a function's argument states, a percentage being a share of one.</summary>
    private static Double Amount(String argument)
    {
        var text = argument.Trim();

        if (text.EndsWith("%", StringComparison.Ordinal) &&
            Double.TryParse(text[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var percent))
        {
            return percent / 100.0;
        }

        return Double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
            ? number
            : Double.NaN;
    }

    /// <summary>Gets the angle <c>hue-rotate</c> asks for, in radians.</summary>
    private static Double Angle(String argument)
    {
        var text = argument.Trim();

        if (Double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
        {
            return 0;
        }

        foreach (var (suffix, perDegree) in new[] { ("deg", Math.PI / 180), ("grad", Math.PI / 200), ("rad", 1.0), ("turn", 2 * Math.PI) })
        {
            if (text.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) &&
                Double.TryParse(text[..^suffix.Length], NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            {
                return value * perDegree;
            }
        }

        return 0;
    }

    private static Double[][] Scale(Double r, Double g, Double b) =>
    [
        [r, 0, 0, 0, 0],
        [0, g, 0, 0, 0],
        [0, 0, b, 0, 0],
        [0, 0, 0, 1, 0],
    ];

    /// <summary>Gets the matrix that takes a third of the colour and leaves two thirds of it.</summary>
    private static Double[][] Luminance() =>
    [
        [0.2126, 0.7152, 0.0722, 0, 0],
        [0.2126, 0.7152, 0.0722, 0, 0],
        [0.2126, 0.7152, 0.0722, 0, 0],
        [0, 0, 0, 1, 0],
    ];

    /// <summary>Gets the matrix partway between leaving a colour alone and taking it to another.</summary>
    private static Double[][] Lerp(Double amount, Double[][] target)
    {
        var identity = Identity();
        var rows = new Double[4][];

        for (var row = 0; row < 4; row++)
        {
            rows[row] = new Double[5];

            for (var column = 0; column < 5; column++)
            {
                rows[row][column] = identity[row][column] * (1 - amount) + target[row][column] * amount;
            }
        }

        return rows;
    }

    /// <summary>Gets the matrix that applies the second filter and then the first.</summary>
    private static Double[][] Multiply(Double[][] first, Double[][] second)
    {
        var rows = new Double[4][];

        for (var row = 0; row < 4; row++)
        {
            rows[row] = new Double[5];

            for (var column = 0; column < 4; column++)
            {
                var sum = 0.0;

                for (var middle = 0; middle < 4; middle++)
                {
                    sum += first[row][middle] * second[middle][column];
                }

                rows[row][column] = sum;
            }

            var offset = first[row][4];

            for (var middle = 0; middle < 4; middle++)
            {
                offset += first[row][middle] * second[middle][4];
            }

            rows[row][4] = offset;
        }

        return rows;
    }

    /// <summary>Takes a filter list apart at the commas that stand outside every bracket.</summary>
    private static IEnumerable<String> SplitFunctions(String text)
    {
        var start = 0;
        var depth = 0;

        for (var index = 0; index < text.Length; index++)
        {
            var c = text[index];

            if (c == '(')
            {
                depth++;
            }
            else if (c == ')')
            {
                depth--;
            }
            else if (c == ',' && depth == 0)
            {
                yield return text[start..index].Trim();
                start = index + 1;
            }
        }

        yield return text[start..].Trim();
    }
}

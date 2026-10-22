using System;
using System.Globalization;

namespace Hutu.Rendering;

/// <summary>
/// Reads the arithmetic of a <c>calc()</c> written as words, keeping the share of a box and the distance
/// measured in from it apart.
/// </summary>
/// <remarks>
/// <c>calc(100% * (1 - 0.3))</c> is a share of the box it stands on, and <c>calc(100% - 20px)</c> is a share
/// and a distance together; neither has its number until the box is known (CSS Values 4 §10.8), so the sum is
/// added up in its two parts and carried to the layout as one.
/// <para>
/// The words are read here rather than left to AngleSharp's own math tree because that tree folds every
/// percentage against the viewport the moment it computes, and a shape's point is a share of the box it is
/// cut against - which the style has not been told yet.
/// </para>
/// </remarks>
internal static class CalcText
{
    /// <summary>What an expression comes to: the distance it adds and the share of the box it stands on.</summary>
    internal readonly record struct Amount(Double Distance, Double Share)
    {
        internal Amount Added(Amount other) => this with
        {
            Distance = Distance + other.Distance,
            Share = Share + other.Share,
        };

        internal Amount Scaled(Double by) => this with
        {
            Distance = Distance * by,
            Share = Share * by,
        };
    }

    /// <summary>
    /// Gets the sum a whole <c>calc()</c> comes to, or null where the text is not one, or where it does not
    /// add up to a length.
    /// </summary>
    /// <param name="text">The value as the page wrote it.</param>
    /// <param name="atom">Reads one operand - a length, a percentage, a font-relative unit - into the
    /// distance or the share it is.</param>
    internal static Amount? Read(String? text, Func<String, StyleLength?> atom)
    {
        var trimmed = text?.Trim();

        if (trimmed is null ||
            !trimmed.StartsWith("calc(", StringComparison.OrdinalIgnoreCase) ||
            !trimmed.EndsWith(")", StringComparison.Ordinal))
        {
            return null;
        }

        // Read at the top of a length, where a sum of nothing but numbers has already been taken for the
        // length whose unit was dropped - see <see cref="Sum"/>.
        return Sum(trimmed[5..^1], atom, numberIsLength: true) is { } came ? came.Length : null;
    }

    /// <summary>Turns the sum into the length a box is measured from later.</summary>
    internal static StyleLength ToLength(Amount amount)
    {
        var hasShare = amount.Share != 0;
        var hasDistance = amount.Distance != 0;

        if (!hasShare)
        {
            return StyleLength.FromPixels(amount.Distance);
        }

        if (!hasDistance)
        {
            return StyleLength.FromPercent(amount.Share);
        }

        return StyleLength.Sum(new[]
        {
            StyleLength.FromPercent(amount.Share),
            StyleLength.FromPixels(amount.Distance),
        });
    }

    /// <summary>One operand of a sum: either a number, or the two parts of a length.</summary>
    private readonly struct Value
    {
        private Value(Boolean isNumber, Double number, Amount length)
        {
            IsNumber = isNumber;
            Number = number;
            Length = length;
        }

        internal Boolean IsNumber { get; }

        internal Double Number { get; }

        internal Amount Length { get; }

        internal static Value Of(Double number) => new(true, number, default);

        internal static Value Of(Amount length) => new(false, 0, length);
    }

    /// <summary>
    /// Adds a sum up, term by term, holding the sign each term was written with.
    /// </summary>
    /// <param name="numberIsLength">
    /// Whether a term of nothing but numbers is the length whose unit was dropped. The fork serialises
    /// <c>calc(0px)</c> as <c>calc(0)</c>, so at the top of a length's own expression a bare number is the
    /// zero it came from; inside a bracket the same number scales the term it stands beside instead.
    /// </param>
    private static Value? Sum(String body, Func<String, StyleLength?> atom, Boolean numberIsLength)
    {
        var terms = CssValueList.SplitOperators(body, '+', '-');

        if (terms.Count == 0)
        {
            return null;
        }

        var started = false;
        var isNumber = false;
        var number = 0.0;
        var length = default(Amount);

        foreach (var term in terms)
        {
            if (Product(term, atom, numberIsLength) is not { } value)
            {
                return null;
            }

            var part = numberIsLength && value.IsNumber
                ? Value.Of(new Amount(value.Number, 0))
                : value;

            if (!started)
            {
                started = true;
                isNumber = part.IsNumber;
            }
            else if (isNumber != part.IsNumber)
            {
                // A number and a length do not add, and a length divided by a length is not a length;
                // either way what the page wrote is not a length at all.
                return null;
            }

            if (isNumber)
            {
                number += part.Number;
            }
            else
            {
                length = length.Added(part.Length);
            }
        }

        return isNumber ? Value.Of(number) : Value.Of(length);
    }

    /// <summary>
    /// Reads one term of a sum: a length or a percentage scaled by the numbers it is multiplied or divided
    /// by, or the plain number - possibly a bracketed sum of numbers - that scales it.
    /// </summary>
    private static Value? Product(String term, Func<String, StyleLength?> atom, Boolean numberIsLength)
    {
        var text = term.Trim();
        var sign = 1.0;

        if (text.StartsWith('+') || text.StartsWith('-'))
        {
            sign = text[0] == '-' ? -1 : 1;
            text = text[1..].Trim();
        }

        var factors = CssValueList.SplitOperators(text, '*', '/');

        if (factors.Count == 0)
        {
            return null;
        }

        var scale = 1.0;
        Value? held = null;

        foreach (var factor in factors)
        {
            var body = factor.Trim();
            var divides = body.StartsWith('/');
            var operand = divides || body.StartsWith('*') ? body[1..].Trim() : body;

            if (Number(operand) is { } scalar)
            {
                if (divides && scalar == 0)
                {
                    return null;
                }

                scale = divides ? scale / scalar : scale * scalar;
                continue;
            }

            if (Operand(operand, atom, numberIsLength) is not { } value)
            {
                return null;
            }

            // A number scales the term however it was written, and a bracketed sum of them scales it too.
            if (value.IsNumber)
            {
                if (divides && value.Number == 0)
                {
                    return null;
                }

                scale = divides ? scale / value.Number : scale * value.Number;
                continue;
            }

            // Two lengths of one product is not a length, and a length under a division is not one either.
            if (held is not null || divides)
            {
                return null;
            }

            held = value;
        }

        return held is not { } length
            ? Value.Of(sign * scale)
            : Value.Of(length.Length.Scaled(sign * scale));
    }

    /// <summary>Reads one factor that is no plain number: a bracketed sum, a length, or a percentage.</summary>
    private static Value? Operand(String operand, Func<String, StyleLength?> atom, Boolean numberIsLength)
    {
        if (operand.Contains('(') && operand.EndsWith(")", StringComparison.Ordinal))
        {
            var open = operand.IndexOf('(');

            // `calc(100% * (1 - 0.3))` - the bracket is the whole of the factor, so what is inside it is
            // the factor's own value, whatever kind of number that comes to.
            return open == 0
                ? Sum(operand[1..^1], atom, numberIsLength: false)
                : Function(operand, open, atom);
        }

        return atom(operand) is not { } length
            ? null
            : length.Kind switch
            {
                StyleLengthKind.Percent => Value.Of(new Amount(0, length.Value)),
                StyleLengthKind.Pixels => Value.Of(new Amount(length.Value, 0)),
                _ => null,
            };
    }

    /// <summary>
    /// Reads a function standing as a factor. Only <c>calc()</c> nests, which is the same expression one
    /// level down; the min, max and clamp of the grammar scale nothing, so they leave the term to a reader
    /// that can hold them.
    /// </summary>
    private static Value? Function(String operand, Int32 open, Func<String, StyleLength?> atom)
    {
        var name = operand[..open].Trim();

        if (!name.Equals("calc", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return Sum(operand[(open + 1)..^1], atom, numberIsLength: false);
    }

    private static Double? Number(String text) =>
        Double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
}

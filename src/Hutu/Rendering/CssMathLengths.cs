using System;
using System.Collections.Generic;
using System.Globalization;

namespace Hutu.Rendering;

/// <summary>
/// Evaluates the <c>min()</c>, <c>max()</c> and <c>clamp()</c> length functions.
/// </summary>
/// <remarks>
/// These have to be handled here rather than left to AngleSharp.Css, because it models them as a
/// math expression rather than as a length - reading such a value as pixels yields zero, so
/// <c>width: min(100%, 640px)</c> would silently collapse instead of picking one of its arguments.
/// Only length and percentage arguments are supported; anything else declines the value so the
/// caller can fall back.
/// </remarks>
internal static class CssMathLengths
{
    public static Double? Evaluate(String? text, Func<String, Double?> resolve)
    {
        var call = Parse(text);

        if (call is not { } parsed)
        {
            return null;
        }

        var values = new List<Double>(parsed.Arguments.Count);

        foreach (var argument in parsed.Arguments)
        {
            var value = ResolveArgument(argument, resolve);

            if (value is null)
            {
                return null;
            }

            values.Add(value.Value);
        }

        return Combine(parsed.Function, values);
    }

    /// <summary>
    /// Same as <see cref="Evaluate"/>, but the arguments keep their units, so a percentage among
    /// them is still a percentage when the result is used. The box model needs this: collapsing
    /// <c>min(100%, 640px)</c> to pixels here would resolve its percentage against the viewport
    /// rather than against the containing block.
    /// </summary>
    public static StyleLength? Evaluate(String? text, Func<String, StyleLength?> resolve)
    {
        var call = Parse(text);

        if (call is not { } parsed)
        {
            return null;
        }

        var values = new List<StyleLength>(parsed.Arguments.Count);

        foreach (var argument in parsed.Arguments)
        {
            var value = ResolveArgument(argument, resolve);

            if (value is null)
            {
                return null;
            }

            values.Add(value.Value);
        }

        if (parsed.Function == StyleMathFunction.Clamp)
        {
            return values.Count == 3 ? StyleLength.Clamp(values) : null;
        }

        return parsed.Function == StyleMathFunction.Min ? StyleLength.Min(values) : StyleLength.Max(values);
    }

    private static Double? Combine(StyleMathFunction function, List<Double> values)
    {
        if (values.Count == 0)
        {
            return null;
        }

        if (function == StyleMathFunction.Clamp)
        {
            return values.Count == 3 ? Math.Clamp(values[1], values[0], values[2]) : null;
        }

        var result = values[0];

        foreach (var value in values)
        {
            result = function == StyleMathFunction.Min ? Math.Min(result, value) : Math.Max(result, value);
        }

        return result;
    }

    /// <summary>Takes <c>min(...)</c>, <c>max(...)</c> or <c>clamp(...)</c> apart.</summary>
    /// <summary>
    /// Evaluates a <c>calc()</c> written as a sum of length terms, which is the one shape the math
    /// functions above do not cover. Terms keep the caller's resolver, so a percentage is read against
    /// the basis the property takes; a term that is not a plain length or percentage declines the sum.
    /// </summary>
    public static Double? EvaluateSum(String? text, Func<String, Double?> resolve)
    {
        var trimmed = text?.Trim();

        if (trimmed is null || !trimmed.StartsWith("calc(", StringComparison.OrdinalIgnoreCase) ||
            trimmed[^1] != ')')
        {
            return null;
        }

        var open = trimmed.IndexOf('(');
        var body = trimmed[(open + 1)..^1];
        var total = 0.0;
        var sign = 1.0;
        var start = 0;
        var depth = 0;
        var any = false;

        for (var at = 0; at <= body.Length; at++)
        {
            var character = at < body.Length ? body[at] : '+';

            if (character == '(')
            {
                depth++;
            }
            else if (character == ')')
            {
                depth--;
            }

            var minus = character == '-' && at > start && body[at - 1] is not ('e' or 'E');

            if (depth != 0 || (character != '+' && !minus))
            {
                continue;
            }

            var term = body[start..at].Trim();

            if (term.Length > 0)
            {
                if (EvaluateTerm(term, resolve) is not { } value)
                {
                    return null;
                }

                total += sign * value;
                any = true;
            }

            sign = minus ? -1.0 : 1.0;
            start = at + 1;
        }

        return any ? total : null;
    }

    /// <summary>
    /// Evaluates one term of a sum: a length, a plain number, or a product or quotient of them, as
    /// <c>calc(0.5 * 2ch)</c> writes the two factors. A factor that is neither a number nor a length
    /// the caller can resolve declines the term.
    /// </summary>
    private static Double? EvaluateTerm(String term, Func<String, Double?> resolve)
    {
        var value = 1.0;
        var any = false;
        var operation = '*';
        var start = 0;
        var depth = 0;

        for (var at = 0; at <= term.Length; at++)
        {
            var character = at < term.Length ? term[at] : '*';

            if (character == '(')
            {
                depth++;
            }
            else if (character == ')')
            {
                depth--;
            }

            if (depth != 0 || (character != '*' && character != '/'))
            {
                continue;
            }

            var factor = term[start..at].Trim();

            if (factor.Length == 0)
            {
                return null;
            }

            Double? number = Double.TryParse(factor, NumberStyles.Float, CultureInfo.InvariantCulture, out var plain)
                ? plain
                : resolve(factor);

            if (number is null)
            {
                return null;
            }

            value = operation == '*' ? value * number.Value : value / number.Value;
            any = true;
            operation = character;
            start = at + 1;
        }

        return any ? value : null;
    }

    private static (StyleMathFunction Function, List<String> Arguments)? Parse(String? text)
    {
        var trimmed = text?.Trim();

        if (String.IsNullOrEmpty(trimmed) || trimmed[^1] != ')')
        {
            return null;
        }

        var open = trimmed.IndexOf('(');

        if (open <= 0)
        {
            return null;
        }

        var name = trimmed[..open].Trim();
        StyleMathFunction function;

        if (name.Equals("min", StringComparison.OrdinalIgnoreCase))
        {
            function = StyleMathFunction.Min;
        }
        else if (name.Equals("max", StringComparison.OrdinalIgnoreCase))
        {
            function = StyleMathFunction.Max;
        }
        else if (name.Equals("clamp", StringComparison.OrdinalIgnoreCase))
        {
            function = StyleMathFunction.Clamp;
        }
        else
        {
            return null;
        }

        var arguments = CssValueList.SplitTopLevel(trimmed[(open + 1)..^1]);

        return arguments.Count == 0 ? null : (function, arguments);
    }

    /// <summary>
    /// Resolves one argument of a math function. Inside <c>min()</c> the parentheses around a
    /// <c>calc()</c> are left out, so an argument that is not a single length may be a sum written
    /// bare - <c>min(300px, 25% + 100px)</c> - and its terms keep the units they were written in.
    /// </summary>
    private static StyleLength? ResolveArgument(String argument, Func<String, StyleLength?> resolve)
    {
        var terms = SplitSum(Unwrap(argument));

        if (terms.Count < 2)
        {
            return resolve(Unwrap(argument));
        }

        var lengths = new List<StyleLength>(terms.Count);

        foreach (var (negative, term) in terms)
        {
            if (resolve(term) is not { } value)
            {
                return null;
            }

            if (!negative)
            {
                lengths.Add(value);
                continue;
            }

            if (Negate(value) is not { } minus)
            {
                return null;
            }

            lengths.Add(minus);
        }

        return StyleLength.Sum(lengths);
    }

    /// <summary>The same for a caller that wants the answer in pixels rather than the length.</summary>
    private static Double? ResolveArgument(String argument, Func<String, Double?> resolve)
    {
        var terms = SplitSum(Unwrap(argument));

        if (terms.Count < 2)
        {
            return resolve(Unwrap(argument));
        }

        var total = 0.0;

        foreach (var (negative, term) in terms)
        {
            if (resolve(term) is not { } value)
            {
                return null;
            }

            total += negative ? -value : value;
        }

        return total;
    }

    /// <summary>
    /// A length taken away rather than added: a share stays a share of the same box, so only the
    /// number in front of the unit turns round. A length that is itself a math function has no
    /// negated form the box model carries, and declines.
    /// </summary>
    private static StyleLength? Negate(StyleLength value) => value.Kind switch
    {
        StyleLengthKind.Pixels => StyleLength.FromPixels(-value.Value),
        StyleLengthKind.Percent => StyleLength.FromPercent(-value.Value),
        _ => null,
    };

    /// <summary>
    /// The body of an argument: a <c>calc()</c> written inside a math function is allowed and its
    /// parentheses carry no meaning of their own, so the terms inside are the argument.
    /// </summary>
    private static String Unwrap(String argument)
    {
        var trimmed = argument.Trim();

        if (trimmed.Length > 6 && trimmed.StartsWith("calc(", StringComparison.OrdinalIgnoreCase) &&
            trimmed[^1] == ')')
        {
            return trimmed[5..^1];
        }

        // A group is the same arithmetic with the function's name left off, and that is how a
        // bracketed expression inside a math function reaches us. Only a group that holds the whole
        // argument is taken apart: `(1px) + (2px)` keeps its own terms for the splitter.
        if (trimmed.Length > 2 && trimmed[0] == '(' && OpensOnce(trimmed))
        {
            return trimmed[1..^1];
        }

        return trimmed;
    }

    /// <summary>
    /// Gets whether the parentheses of an argument close only at its last character.
    /// </summary>
    private static Boolean OpensOnce(String text)
    {
        var depth = 0;

        for (var at = 0; at < text.Length; at++)
        {
            if (text[at] == '(')
            {
                depth++;
            }
            else if (text[at] == ')')
            {
                depth--;

                if (depth == 0 && at != text.Length - 1)
                {
                    return false;
                }
            }
        }

        return depth == 0;
    }

    /// <summary>
    /// Splits a sum into its terms at the signs that stand outside every group. A sign that opens a
    /// term belongs to that term, and one written after an exponent letter belongs to the number.
    /// </summary>
    private static List<(Boolean Negative, String Term)> SplitSum(String text)
    {
        var terms = new List<(Boolean, String)>();
        var start = 0;
        var depth = 0;
        var negative = false;
        var quote = '\0';

        for (var at = 0; at <= text.Length; at++)
        {
            var character = at < text.Length ? text[at] : '\0';

            if (quote != '\0')
            {
                if (character == quote)
                {
                    quote = '\0';
                }

                continue;
            }

            if (character is '\'' or '"')
            {
                quote = character;
                continue;
            }

            if (character == '(')
            {
                depth++;
                continue;
            }

            if (character == ')')
            {
                depth--;
                continue;
            }

            var isSign = depth == 0 && at > 0 &&
                (character == '+' || (character == '-' && text[at - 1] is not ('e' or 'E')));

            if (!isSign && at < text.Length)
            {
                continue;
            }

            var term = text[start..at].Trim();

            if (term.Length > 0)
            {
                terms.Add((negative, term));
            }

            negative = character == '-';
            start = at + 1;
        }

        return terms;
    }
}

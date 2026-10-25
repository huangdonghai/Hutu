using System;
using System.Collections.Generic;

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
            var value = resolve(argument);

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
            var value = resolve(argument);

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
                if (resolve(term) is not { } value)
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
}

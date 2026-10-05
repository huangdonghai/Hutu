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
    public static Double? Evaluate(String text, Func<String, Double?> resolve)
    {
        var trimmed = text.Trim();

        if (trimmed.Length == 0 || trimmed[^1] != ')')
        {
            return null;
        }

        var open = trimmed.IndexOf('(');

        if (open <= 0)
        {
            return null;
        }

        var name = trimmed[..open].Trim();
        var isMinimum = name.Equals("min", StringComparison.OrdinalIgnoreCase);
        var isMaximum = name.Equals("max", StringComparison.OrdinalIgnoreCase);
        var isClamp = name.Equals("clamp", StringComparison.OrdinalIgnoreCase);

        if (!isMinimum && !isMaximum && !isClamp)
        {
            return null;
        }

        var arguments = CssValueList.SplitTopLevel(trimmed[(open + 1)..^1]);

        if (arguments.Count == 0)
        {
            return null;
        }

        var values = new List<Double>(arguments.Count);

        foreach (var argument in arguments)
        {
            var value = resolve(argument);

            if (value is null)
            {
                return null;
            }

            values.Add(value.Value);
        }

        if (isClamp)
        {
            return values.Count == 3 ? Math.Clamp(values[1], values[0], values[2]) : null;
        }

        var result = values[0];

        foreach (var value in values)
        {
            result = isMinimum ? Math.Min(result, value) : Math.Max(result, value);
        }

        return result;
    }
}

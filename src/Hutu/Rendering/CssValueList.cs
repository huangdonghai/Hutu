using System;
using System.Collections.Generic;

namespace Hutu.Rendering;

/// <summary>
/// Splits a CSS value into its components without being fooled by nested functions.
/// </summary>
/// <remarks>
/// Background values are only available as text - the types AngleSharp.Css models them with are
/// internal - so they have to be taken apart here. A comma inside <c>rgba(15, 23, 42, .7)</c> must
/// not be mistaken for a layer separator, which is what this guards against.
/// </remarks>
internal static class CssValueList
{
    public static List<String> SplitTopLevel(String? value) => Split(value, ',', false);

    public static List<String> SplitWhitespace(String? value) => Split(value, ' ', true);

    private static List<String> Split(String? value, Char separator, Boolean isWhitespace)
    {
        var parts = new List<String>();

        if (String.IsNullOrWhiteSpace(value))
        {
            return parts;
        }

        var depth = 0;
        var start = 0;

        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];

            if (character == '(')
            {
                depth++;
            }
            else if (character == ')')
            {
                depth--;
            }
            else if (depth == 0 && (isWhitespace ? Char.IsWhiteSpace(character) : character == separator))
            {
                AddPart(parts, value[start..index]);

                if (isWhitespace)
                {
                    // Collapse a run of white space into a single separator.
                    while (index + 1 < value.Length && Char.IsWhiteSpace(value[index + 1]))
                    {
                        index++;
                    }
                }

                start = index + 1;
            }
        }

        AddPart(parts, value[start..]);
        return parts;
    }

    private static void AddPart(List<String> parts, String part)
    {
        var trimmed = part.Trim();

        if (trimmed.Length > 0)
        {
            parts.Add(trimmed);
        }
    }
}

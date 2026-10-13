using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Hutu.Rendering.Media;

/// <summary>
/// Pulls the <c>url(...)</c> references out of a CSS value.
/// </summary>
/// <remarks>
/// Background values can only be read as text: the value types AngleSharp.Css models them with are
/// internal, so the layering and the individual functions have to be parsed here. This type covers
/// the one part that is needed before parsing a value's structure - knowing which resources a
/// declaration refers to.
/// </remarks>
internal static class CssUrlReferences
{
    private static readonly Regex Pattern = new(
        @"url\(\s*(?<url>[^)]*?)\s*\)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// Extracts the referenced URLs in order, with surrounding quotes removed. Never throws.
    /// </summary>
    public static IReadOnlyList<String> Extract(String? cssValue)
    {
        var urls = new List<String>();

        if (String.IsNullOrWhiteSpace(cssValue))
        {
            return urls;
        }

        foreach (Match match in Pattern.Matches(cssValue))
        {
            var url = match.Groups["url"].Value.Trim().Trim('"', '\'');

            if (url.Length > 0)
            {
                urls.Add(url);
            }
        }

        return urls;
    }
}

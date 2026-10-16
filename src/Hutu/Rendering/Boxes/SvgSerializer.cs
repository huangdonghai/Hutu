using AngleSharp.Dom;
using System;
using System.Text;

namespace Hutu.Rendering.Boxes;

/// <summary>
/// Turns an inline <c>&lt;svg&gt;</c> element into a standalone SVG document the backend can render on
/// its own - the element's own markup with any <c>&lt;use&gt;</c>-referenced symbol or group inlined,
/// because an icon sprite keeps its <c>&lt;symbol&gt;</c> definitions in a different <c>&lt;svg&gt;</c>
/// elsewhere in the page and a lone <c>&lt;use href="#id"&gt;</c> would otherwise find nothing to draw.
/// </summary>
internal static class SvgSerializer
{
    private const String SvgNamespace = "http://www.w3.org/2000/svg";
    private const String XLinkNamespace = "http://www.w3.org/1999/xlink";

    /// <summary>Serializes one <c>&lt;svg&gt;</c> root to standalone markup, or null when it has nothing.</summary>
    public static String? Serialize(IElement svg)
    {
        var markup = svg.OuterHtml;

        if (String.IsNullOrWhiteSpace(markup))
        {
            return null;
        }

        var defs = CollectUsedDefinitions(svg);
        var close = markup.IndexOf('>');

        if (close < 0)
        {
            return markup;
        }

        var open = markup[..close];

        if (!open.Contains("xmlns=", StringComparison.Ordinal))
        {
            open += " xmlns=\"" + SvgNamespace + "\"";
        }

        if (defs.Length > 0 && !open.Contains("xmlns:xlink=", StringComparison.Ordinal))
        {
            open += " xmlns:xlink=\"" + XLinkNamespace + "\"";
        }

        var head = defs.Length > 0 ? open + "><defs>" + defs + "</defs>" : open + ">";

        return head + markup[(close + 1)..];
    }

    /// <summary>
    /// The markup of every element the <c>&lt;use&gt;</c> references by fragment id, gathered so the
    /// references resolve inside the standalone document. A name that nothing defines is skipped.
    /// </summary>
    private static StringBuilder CollectUsedDefinitions(IElement svg)
    {
        var defs = new StringBuilder();
        var seen = new System.Collections.Generic.HashSet<String>(StringComparer.Ordinal);

        foreach (var node in svg.Descendants())
        {
            if (node is not IElement descendant ||
                !String.Equals(descendant.LocalName, "use", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var href = descendant.GetAttribute("href") ?? descendant.GetAttribute("xlink:href");

            if (String.IsNullOrEmpty(href) || href![0] != '#')
            {
                continue;
            }

            var id = href[1..];

            if (!seen.Add(id))
            {
                continue;
            }

            var target = svg.Owner?.GetElementById(id);

            if (target is not null)
            {
                defs.Append(target.OuterHtml);
            }
        }

        return defs;
    }
}

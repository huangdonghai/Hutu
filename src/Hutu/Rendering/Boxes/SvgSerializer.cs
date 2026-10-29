using AngleSharp.Dom;
using System;
using System.Text;
using System.Text.RegularExpressions;

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

    /// <summary>A <c>width</c> or <c>height</c> attribute together with its value, quoted or not.</summary>
    private static readonly Regex SizeAttribute = new(
        "(?<![\\w-])(?:width|height)\\s*=\\s*(?:\"[^\"]*\"|'[^']*'|[^\\s>]+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

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

        var open = DropFontRelativeSize(markup[..close]);

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
    /// Drops the root <c>width</c>/<c>height</c> attributes whose value is a font-relative length, which
    /// a standalone document has no font to resolve.
    /// </summary>
    /// <remarks>
    /// <c>em</c>, <c>ex</c>, <c>ch</c> and <c>rem</c> are multiples of a font size, and the document
    /// handed to the picture renderer carries no computed font size for its root - that is a fact of the
    /// page's cascade, not of the markup - so the renderer reads the number and lets the unit fall:
    /// <c>width="1em" height="1em"</c> comes out as a one-unit viewport. Such a viewport does not merely
    /// make a small picture, it clips every shape in the picture away, which is how an icon whose box
    /// and <c>viewBox</c> were both right still drew nothing. Omitting the attribute leaves the
    /// <c>viewBox</c> to establish the coordinate space, which is the space the box was sized from
    /// anyway, and the picture is scaled onto the box regardless of the size it reports.
    /// </remarks>
    private static String DropFontRelativeSize(String open) =>
        SizeAttribute.Replace(open, match => IsFontRelative(AttributeValue(match.Value)) ? String.Empty : match.Value);

    /// <summary>Gets the value of an attribute <c>name="value"</c> spelling, without its quotes.</summary>
    private static String AttributeValue(String attribute)
    {
        var text = attribute[(attribute.IndexOf('=') + 1)..].Trim();

        return text.Length > 1 && (text[0] == '"' || text[0] == '\'') && text[^1] == text[0]
            ? text[1..^1].Trim()
            : text;
    }

    /// <summary>Gets whether a length is a multiple of a font size rather than a length in user units.</summary>
    private static Boolean IsFontRelative(String length)
    {
        var text = length.Trim();
        var end = 0;

        while (end < text.Length && (Char.IsDigit(text[end]) || text[end] is '.' or '+' or '-'))
        {
            end++;
        }

        var unit = text[end..].Trim();

        return unit.Equals("em", StringComparison.OrdinalIgnoreCase) ||
               unit.Equals("ex", StringComparison.OrdinalIgnoreCase) ||
               unit.Equals("ch", StringComparison.OrdinalIgnoreCase) ||
               unit.Equals("ic", StringComparison.OrdinalIgnoreCase) ||
               unit.Equals("rem", StringComparison.OrdinalIgnoreCase);
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

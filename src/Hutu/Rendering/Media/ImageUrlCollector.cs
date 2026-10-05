using AngleSharp.Css.RenderTree;
using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using System;
using System.Collections.Generic;

namespace Hutu.Rendering.Media;

/// <summary>
/// Collects the image URLs a document needs, so they can be fetched before layout runs.
/// </summary>
/// <remarks>
/// It works from the render tree rather than from the DOM, because the render tree is where the
/// computed styles are - that is what makes background images declared in any stylesheet, including
/// a linked one, discoverable. URLs are resolved against the owning element's base URL, so
/// <c>res:</c> / <c>epub:</c> references resolve exactly like the document's own resources do.
/// </remarks>
public static class ImageUrlCollector
{
    public static IReadOnlyList<String> Collect(IRenderNode? renderTree)
    {
        var urls = new List<String>();
        var seen = new HashSet<String>(StringComparer.Ordinal);

        if (renderTree is not null)
        {
            Walk(renderTree, urls, seen);
        }

        return urls;
    }

    private static void Walk(IRenderNode node, List<String> urls, HashSet<String> seen)
    {
        if (node is ElementRenderNode element)
        {
            var reference = element.Ref;

            if (reference is IHtmlImageElement image && image.Source is { Length: > 0 } source)
            {
                Add(ImageUrlResolver.Resolve(reference, source), urls, seen);
            }

            var background = element.ComputedStyle.GetPropertyValue("background-image");

            foreach (var candidate in CssUrlReferences.Extract(background))
            {
                Add(ImageUrlResolver.Resolve(reference, candidate), urls, seen);
            }
        }

        foreach (var child in node.Children)
        {
            Walk(child, urls, seen);
        }
    }

    private static void Add(String? url, List<String> urls, HashSet<String> seen)
    {
        if (!String.IsNullOrEmpty(url) && seen.Add(url))
        {
            urls.Add(url);
        }
    }
}

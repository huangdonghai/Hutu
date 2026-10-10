using AngleSharp.Css;
using AngleSharp.Css.Dom;
using AngleSharp.Css.RenderTree;
using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using System;
using System.Collections.Generic;
using System.Linq;

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

    /// <summary>
    /// Collects from the document's own DOM, matching each element's background declaration
    /// directly - the fused path has no render tree to read. URLs resolve the same way.
    /// </summary>
    public static IReadOnlyList<String> Collect(IDocument document, IRenderDevice device)
    {
        var urls = new List<String>();
        var seen = new HashSet<String>(StringComparer.Ordinal);

        if (document.DocumentElement is not { } root)
        {
            return urls;
        }

        var sheets = new List<ICssStyleSheet>();

        foreach (var provider in document.Context.GetServices<ICssDefaultStyleSheetProvider>())
        {
            sheets.Add(provider.Default);
        }

        sheets.AddRange(document.GetStyleSheets().OfType<ICssStyleSheet>());

        var index = Boxes.RuleBucketIndex.Build(sheets, device);
        WalkDom(root, index, urls, seen);
        return urls;
    }

    private static void WalkDom(IElement element, Boxes.RuleBucketIndex index, List<String> urls, HashSet<String> seen)
    {
        if (element is IHtmlImageElement image && image.Source is { Length: > 0 } source)
        {
            Add(ImageUrlResolver.Resolve(element, source), urls, seen);
        }

        var background = Boxes.MatchedCascade.ForElement(index, element).Find("background-image")?.Value;

        foreach (var candidate in CssUrlReferences.Extract(background))
        {
            Add(ImageUrlResolver.Resolve(element, candidate), urls, seen);
        }

        foreach (var child in element.Children)
        {
            WalkDom(child, index, urls, seen);
        }
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

using AngleSharp.Dom;
using System;

namespace Hutu.Rendering.Media;

/// <summary>
/// Resolves an image reference against the base URL of the element that declared it.
/// </summary>
/// <remarks>
/// Resolution has to go through the element's base rather than the document's, because <c>res:</c>
/// and <c>epub:</c> references - and any <c>&lt;base&gt;</c> element - are relative to it. Returns
/// null instead of throwing when there is no usable base, so a malformed reference simply yields no
/// image.
/// </remarks>
public static class ImageUrlResolver
{
    public static String? Resolve(IElement element, String? url)
    {
        if (String.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        var baseUrl = element.BaseUrl;

        if (baseUrl is null)
        {
            return null;
        }

        var resolved = new Url(baseUrl, url);
        return resolved.IsInvalid ? null : resolved.Href;
    }
}

using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using System;

namespace Hutu.Rendering.Interaction;

/// <summary>
/// Reads an anchor element as the link it declares: where it points, where it asks to be opened, and
/// whether it points into the document it is in.
/// </summary>
/// <remarks>
/// The address is resolved against the element's own base, which is what makes a relative href - the
/// usual kind - mean the same thing here as it does in the page. An anchor with nothing to follow is not
/// a link at all: one with no href is a placeholder, and an empty one or a bare "#" goes nowhere.
/// </remarks>
public static class LinkResolver
{
    /// <summary>Gets the link an element declares, or null when it declares none.</summary>
    public static LinkTarget? Resolve(IElement element)
    {
        ArgumentNullException.ThrowIfNull(element);

        if (element is not IHtmlAnchorElement anchor)
        {
            return null;
        }

        if (anchor.GetAttribute("href") is not { } attribute || String.IsNullOrWhiteSpace(attribute))
        {
            return null;
        }

        var href = attribute.Trim();

        if (href.Equals("#", StringComparison.Ordinal))
        {
            return null;
        }

        var baseUrl = element.BaseUrl;
        var target = anchor.Target is { Length: > 0 } ? anchor.Target : null;
        var download = element.HasAttribute("download");

        // A href that is nothing but a fragment is the plain case of a link into the document it was
        // written on, and it is answered without resolving anything: the address type resolves a
        // relative reference against the path of a hierarchical scheme, and an opaque one like res: or
        // epub: keeps its path where that resolution cannot see it - so resolving "#name" would produce
        // an address of the document's scheme and nothing else.
        if (href[0] == '#')
        {
            return new LinkTarget(WithFragment(baseUrl, href[1..]), target, download, true, href[1..]);
        }

        var address = baseUrl is null ? new Url(href) : new Url(baseUrl, href);

        if (address.IsInvalid)
        {
            return null;
        }

        var fragment = String.IsNullOrEmpty(address.Fragment) ? null : address.Fragment;
        var same = fragment is not null && IsSameDocument(baseUrl, address);

        return new LinkTarget(address.Href, target, download, same, same ? fragment : null);
    }

    /// <summary>
    /// Gets whether an address names the document the link is in: with the fragment set aside, the two
    /// are the same address.
    /// </summary>
    private static Boolean IsSameDocument(Url? document, Url address)
    {
        if (document is null)
        {
            return false;
        }

        return String.Equals(Address(address), Address(document), StringComparison.Ordinal);
    }

    /// <summary>Gets the address as the document it names, i.e. with anything after the hash dropped.</summary>
    private static String Address(Url url) => new Url(url) { Fragment = null }.Href;

    /// <summary>Gets the address of a document with a fragment on the end of it.</summary>
    private static String WithFragment(Url? document, String fragment) =>
        document is null ? $"#{fragment}" : new Url(document) { Fragment = fragment }.Href;
}

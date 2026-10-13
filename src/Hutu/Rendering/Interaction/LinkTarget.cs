using System;

namespace Hutu.Rendering.Interaction;

/// <summary>
/// Where a link points and what it asks for: the address, the context it wants to open in, and whether
/// it asks to be saved rather than shown.
/// </summary>
/// <remarks>
/// A link into the document it is in is kept apart from one that leaves it, because the two are the
/// work of different layers: an anchor is a scroll and a mark the document itself can do, while loading
/// another document is the host's.
/// <para>
/// What a link asks for is read out of the document, so a host is told where one points rather than
/// assembling the answer itself: the constructor belongs to the engine.
/// </para>
/// </remarks>
public readonly struct LinkTarget
{
    internal LinkTarget(String url, String? target, Boolean isDownload, Boolean isFragment, String? fragment)
    {
        Url = url;
        Target = target;
        IsDownload = isDownload;
        IsFragment = isFragment;
        Fragment = fragment;
    }

    /// <summary>Gets the absolute address the link points at.</summary>
    public String Url { get; }

    /// <summary>
    /// Gets the browsing context the link asks to be opened in, or null when it asks for the one it is
    /// already in.
    /// </summary>
    public String? Target { get; }

    /// <summary>Gets whether the link asks to be saved rather than shown.</summary>
    public Boolean IsDownload { get; }

    /// <summary>Gets whether the link points into the document it is in, i.e. at one of its anchors.</summary>
    public Boolean IsFragment { get; }

    /// <summary>Gets the name of the anchor a link into this document points at, without its hash.</summary>
    public String? Fragment { get; }
}

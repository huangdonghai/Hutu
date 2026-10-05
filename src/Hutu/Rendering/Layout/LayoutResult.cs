using System;

namespace Hutu.Rendering.Layout;

/// <summary>The outcome of a layout pass: the tree plus the size the document occupies.</summary>
public sealed class LayoutResult
{
    public LayoutResult(LayoutBox root, Double width, Double height)
    {
        Root = root;
        Width = width;
        Height = height;
    }

    public LayoutBox Root { get; }

    /// <summary>Gets the width the layout was computed against.</summary>
    public Double Width { get; }

    /// <summary>Gets the height of the laid-out document, i.e. the scrollable content height.</summary>
    public Double Height { get; }
}

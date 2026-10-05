using AngleSharp.Dom;
using Hutu.Rendering.Media;
using System;
using System.Collections.Generic;

namespace Hutu.Rendering.Boxes;

/// <summary>The formatting role of a box in the tree built from the styled render tree.</summary>
public enum BoxKind
{
    /// <summary>A block-level box: it stacks vertically and may contain block or inline content.</summary>
    Block,

    /// <summary>An inline-level box: it participates in a line box.</summary>
    Inline,

    /// <summary>A run of text belonging to its containing inline formatting context.</summary>
    Text,

    /// <summary>A forced line break (<c>&lt;br&gt;</c>).</summary>
    LineBreak,

    /// <summary>A horizontal rule (<c>&lt;hr&gt;</c>), which is block-level.</summary>
    HorizontalRule,
}

/// <summary>
/// A node of the box tree - the intermediate structure between the styled render tree and layout.
/// </summary>
/// <remarks>
/// Boxes mirror render nodes, with two differences that exist because the render tree is not a
/// formatting structure: <c>display: none</c> subtrees are dropped here, and runs of inline-level
/// content inside a block that also has block-level children are wrapped in anonymous blocks.
/// Geometry is not stored here; it is produced by the layout engine.
/// </remarks>
public sealed class Box
{
    public Box(BoxKind kind, ComputedStyle style)
    {
        Kind = kind;
        Style = style;
    }

    public BoxKind Kind { get; }

    /// <summary>Gets the resolved style. For a text box this is the containing element's style.</summary>
    public ComputedStyle Style { get; }

    /// <summary>Gets the source element, or null for a text or anonymous box.</summary>
    public IElement? Element { get; init; }

    /// <summary>Gets the source text node, or null for an element box.</summary>
    public IText? Source { get; init; }

    /// <summary>Gets the processed text of a text box (white space already collapsed where applicable).</summary>
    public String? Text { get; init; }

    /// <summary>Gets whether this box was generated to hold inline content, not by an element.</summary>
    public Boolean IsAnonymous { get; init; }

    /// <summary>
    /// Gets whether the element's content is replaced - an image - instead of being its children.
    /// Such a box never has children: its geometry comes from the intrinsic size and the CSS
    /// constraints. Whether it is block-level or inline-level is still decided by <c>display</c>,
    /// which is why this is a flag rather than a kind of its own.
    /// </summary>
    public Boolean IsReplaced { get; init; }

    /// <summary>Gets the absolute URL of the replaced content.</summary>
    public String? SourceUrl { get; init; }

    /// <summary>
    /// Gets the intrinsic size of the replaced content. Always set for a replaced box: the declared
    /// width/height attributes stand in for a missing intrinsic size and the specification's default
    /// object size is the last resort, so layout never depends on an image having loaded.
    /// </summary>
    public ImageSize? IntrinsicSize { get; init; }

    public List<Box> Children { get; } = new();

    public Boolean IsBlockLevel => Kind is BoxKind.Block or BoxKind.HorizontalRule;

    public Boolean IsInlineLevel => !IsBlockLevel;
}

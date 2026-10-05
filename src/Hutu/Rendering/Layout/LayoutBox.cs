using Hutu.Rendering.Boxes;
using System;
using System.Collections.Generic;

namespace Hutu.Rendering.Layout;

/// <summary>The role of a node in the layout tree.</summary>
public enum LayoutKind
{
    /// <summary>A block box with a margin, border and padding.</summary>
    Block,

    /// <summary>A line box inside an inline formatting context.</summary>
    Line,

    /// <summary>A single run of text placed in a line box.</summary>
    Text,

    /// <summary>A horizontal rule.</summary>
    HorizontalRule,

    /// <summary>A replaced box - an image - whose content is drawn rather than laid out.</summary>
    Image,
}

/// <summary>
/// A node of the layout tree: a box tree node with resolved geometry, in document coordinates.
/// </summary>
/// <remarks>
/// Geometry is stored as the border box; the content box is derived. A text node additionally
/// carries the resolved font and the distance from the top of its box to the baseline, so the
/// painter does not have to redo any of the measurement.
/// </remarks>
public sealed class LayoutBox
{
    public LayoutBox(Box source, LayoutKind kind)
    {
        Source = source;
        Kind = kind;
    }

    public Box Source { get; }

    public LayoutKind Kind { get; }

    public Rect BorderBox { get; set; }

    public Thickness Margin { get; set; }

    public Thickness Border { get; set; }

    public Thickness Padding { get; set; }

    public List<LayoutBox> Children { get; } = new();

    /// <summary>Gets the text of a text node, or null for anything else.</summary>
    public String? Text { get; set; }

    /// <summary>Gets the resolved font of a text node.</summary>
    public FontStyle Font { get; set; }

    /// <summary>Gets the distance from the top of the border box to the baseline.</summary>
    public Double Baseline { get; set; }

    /// <summary>Gets the absolute URL of the image drawn in an <see cref="LayoutKind.Image"/> box.</summary>
    public String? ImageUrl { get; set; }

    /// <summary>Gets how the image content is fitted into its box.</summary>
    public ObjectFit ImageFit { get; set; }

    /// <summary>Gets the radius painted content is clipped to.</summary>
    public Double CornerRadius { get; set; }

    public Rect ContentBox => new(
        BorderBox.X + Border.Left + Padding.Left,
        BorderBox.Y + Border.Top + Padding.Top,
        Math.Max(0, BorderBox.Width - Border.Horizontal - Padding.Horizontal),
        Math.Max(0, BorderBox.Height - Border.Vertical - Padding.Vertical));

    /// <summary>Gets the padding box, i.e. the border box minus its borders.</summary>
    public Rect PaddingBox => new(
        BorderBox.X + Border.Left,
        BorderBox.Y + Border.Top,
        Math.Max(0, BorderBox.Width - Border.Horizontal),
        Math.Max(0, BorderBox.Height - Border.Vertical));
}

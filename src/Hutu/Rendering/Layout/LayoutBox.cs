using AngleSharp.Css.Dom;
using Hutu.Rendering.Boxes;
using System;
using System.Collections.Generic;

namespace Hutu.Rendering.Layout;

/// <summary>The role of a node in the layout tree.</summary>
internal enum LayoutKind
{
    /// <summary>A block box with a margin, border and padding.</summary>
    Block,

    /// <summary>
    /// One line's fragment of an inline element's box. An inline element that is broken over several
    /// lines leaves one of these per line, which is what lets its background, border and padding be
    /// painted per fragment the way <c>box-decoration-break: slice</c> asks for.
    /// </summary>
    Inline,

    /// <summary>A line box inside an inline formatting context.</summary>
    Line,

    /// <summary>A single run of text placed in a line box.</summary>
    Text,

    /// <summary>A horizontal rule.</summary>
    HorizontalRule,

    /// <summary>A replaced box - an image - whose content is drawn rather than laid out.</summary>
    Image,

    /// <summary>A table wrapper box.</summary>
    Table,

    /// <summary>A row group, whose box spans its rows so its background can be painted.</summary>
    TableRowGroup,

    /// <summary>A table row, whose box spans its cells so its background can be painted.</summary>
    TableRow,

    /// <summary>A table cell.</summary>
    TableCell,
}

/// <summary>The four border colours of a box, in ARGB order.</summary>
/// <remarks>
/// A box normally takes its border colours from its own style, but a collapsed table edge is shared
/// with a neighbour and the colour that wins may belong to the neighbour, so the resolution has to be
/// carried on the box instead.
/// </remarks>
internal readonly struct BorderColors
{
    public BorderColors(UInt32 top, UInt32 right, UInt32 bottom, UInt32 left)
    {
        Top = top;
        Right = right;
        Bottom = bottom;
        Left = left;
    }

    public UInt32 Top { get; }

    public UInt32 Right { get; }

    public UInt32 Bottom { get; }

    public UInt32 Left { get; }
}

/// <summary>
/// A node of the layout tree: a box tree node with resolved geometry, in document coordinates.
/// </summary>
/// <remarks>
/// Geometry is stored as the border box; the content box is derived. A text node additionally
/// carries the resolved font and the distance from the top of its box to the baseline, so the
/// painter does not have to redo any of the measurement.
/// </remarks>
internal sealed class LayoutBox
{
    public LayoutBox(Box source, LayoutKind kind)
    {
        Source = source;
        Kind = kind;
    }

    public Box Source { get; }

    public LayoutKind Kind { get; }

    public Rect BorderBox { get; set; }

    /// <summary>Gets or sets how the box is positioned, which decides whether it joins the stacking
    /// order and whether it took part in normal flow at all.</summary>
    public PositionMode Position { get; set; }

    /// <summary>Gets or sets the declared stacking level, or zero for an automatic one.</summary>
    public Int32 ZIndex { get; set; }

    /// <summary>Gets or sets the rule that pins a sticky box to the viewport as the page scrolls.</summary>
    public StickyConstraint? Sticky { get; set; }

    /// <summary>Gets whether the box was taken out of normal flow by <c>absolute</c> or <c>fixed</c>.</summary>
    public Boolean IsOutOfFlow => Position is PositionMode.Absolute or PositionMode.Fixed;

    public Thickness Margin { get; set; }

    public Thickness Border { get; set; }

    public Thickness Padding { get; set; }

    public List<LayoutBox> Children { get; } = new();

    /// <summary>Gets the text of a text node, or null for anything else.</summary>
    public String? Text { get; set; }

    /// <summary>Gets the resolved font of a text node.</summary>
    public FontStyle Font { get; set; }

    /// <summary>
    /// Gets the distance from the box's inline start to the baseline, measured on the block axis: the
    /// distance down from the top of the border box for horizontal text, and the distance right from
    /// its left edge for vertical text.
    /// </summary>
    /// <remarks>
    /// One field covers both because the two never apply at once - a box is either horizontal or not -
    /// and the painter resolves which of the two it is holding from <see cref="Flow"/>, which it has
    /// anyway when it decides how to draw the glyphs.
    /// </remarks>
    public Double Baseline { get; set; }

    /// <summary>
    /// Gets or sets where a list item's marker is drawn, or null for a box that carries none.
    /// </summary>
    /// <remarks>
    /// Worked out once the tree is arranged, by <see cref="LayoutEngine"/>, and read only by the
    /// painting: the marker is text, and text is drawn where its box and its baseline say. It is kept
    /// apart from the box's own <see cref="Text"/> and <see cref="Baseline"/> because a marker belongs to
    /// no line - it hangs beside the item, in the space the list was given for it, and takes no part in
    /// where the item's own text stands.
    /// </remarks>
    public Rect? MarkerBounds { get; set; }

    /// <summary>
    /// Gets the distance from the top edge of <see cref="MarkerBounds"/> to the baseline the marker
    /// stands on, stated the way <see cref="Baseline"/> is - as a distance inside the box rather than a
    /// coordinate of the page.
    /// </summary>
    public Double MarkerBaseline { get; set; }

    /// <summary>
    /// Gets or sets the writing mode, text orientation and upright combining the box was laid out
    /// with. The whole subtree inherits it, so a vertical container makes its text vertical unless an
    /// inner box asks for horizontal again.
    /// </summary>
    public WritingModeInfo Flow { get; set; } = WritingModeInfo.Horizontal;

    /// <summary>Gets whether the box's text runs down a column rather than along a line.</summary>
    public Boolean Vertical => Flow.IsVertical;

    /// <summary>Gets the absolute URL of the image drawn in an <see cref="LayoutKind.Image"/> box.</summary>
    public String? ImageUrl { get; set; }

    /// <summary>Gets how the image content is fitted into its box.</summary>
    public ObjectFit ImageFit { get; set; }

    /// <summary>
    /// Gets or sets the resolved corner radii of the border box. They are filled in once, after the
    /// box has been sized, because a percentage radius is relative to that size.
    /// </summary>
    public CornerRadii Radii { get; set; }

    /// <summary>
    /// Gets or sets the box's resolved 2D transform, or null when it carries none. It is filled in once
    /// the box is sized, because a <c>translate</c> percentage and the transform origin are relative to
    /// the border box. It changes only how the box is drawn, never where the layout put it.
    /// </summary>
    public Affine? Transform { get; set; }

    /// <summary>
    /// Gets or sets the point the <see cref="Transform"/> is taken about, in document coordinates: the
    /// border box's origin plus the resolved <c>transform-origin</c>.
    /// </summary>
    public Offset TransformOrigin { get; set; }

    /// <summary>
    /// Gets the corner radii of the padding box, which is where the background stops once the border
    /// has eaten into the curve and where descendants are clipped.
    /// </summary>
    public CornerRadii PaddingRadii => Radii.Shrink(Border);

    /// <summary>Gets or sets the box the background is painted into.</summary>
    public BackgroundClip BackgroundClip { get; set; }

    /// <summary>
    /// Gets or sets whether this fragment was cut from the rest of its inline element at the start
    /// edge, i.e. the element began on an earlier line. A cut edge carries no padding, no border and
    /// no corner curve, because everything on it was drawn on the line the element started on.
    /// </summary>
    /// <remarks>Only meaningful for an <see cref="LayoutKind.Inline"/> box.</remarks>
    public Boolean SlicesInlineStart { get; set; }

    /// <summary>
    /// Gets or sets whether this fragment was cut at the end edge, i.e. the element carries on onto a
    /// later line. The mirror of <see cref="SlicesInlineStart"/>.
    /// </summary>
    /// <remarks>Only meaningful for an <see cref="LayoutKind.Inline"/> box.</remarks>
    public Boolean SlicesInlineEnd { get; set; }

    /// <summary>
    /// Gets or sets whether the descendants are clipped to the padding box, which is what an
    /// <c>overflow</c> other than <c>visible</c> asks for.
    /// </summary>
    public Boolean ClipsOverflow { get; set; }

    /// <summary>Gets or sets whether the background is skipped, as on a cell under
    /// <c>empty-cells: hide</c>.</summary>
    public Boolean SuppressBackground { get; set; }

    /// <summary>Gets or sets whether the border is skipped, which <c>empty-cells: hide</c> also does
    /// in the separated border model.</summary>
    public Boolean SuppressBorder { get; set; }

    /// <summary>Gets or sets the border colours to paint instead of the box's own, set when the
    /// borders come from a shared table grid line.</summary>
    public BorderColors? BorderColors { get; set; }

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

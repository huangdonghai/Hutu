using AngleSharp.Dom;
using Hutu.Rendering.Layout;
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

    /// <summary>
    /// A <c>display: inline-block</c> box. Inline-level in its parent's context, so it takes part in a
    /// line box, but it lays its own content out as a block container and is placed as one unit.
    /// </summary>
    InlineBlock,

    /// <summary>A run of text belonging to its containing inline formatting context.</summary>
    Text,

    /// <summary>A forced line break (<c>&lt;br&gt;</c>).</summary>
    LineBreak,

    /// <summary>A horizontal rule (<c>&lt;hr&gt;</c>), which is block-level.</summary>
    HorizontalRule,

    /// <summary>A flex container, which is block-level and lays its children out along an axis.</summary>
    Flex,

    /// <summary>A grid container, which is block-level and lays its children out into rows and
    /// columns rather than a single axis.</summary>
    Grid,

    /// <summary>A table wrapper box. Block-level in its parent's context, but lays its children out
    /// with the table algorithms rather than in normal flow.</summary>
    Table,

    /// <summary>A <c>thead</c>/<c>tbody</c>/<c>tfoot</c> row group.</summary>
    TableRowGroup,

    /// <summary>A table row.</summary>
    TableRow,

    /// <summary>A table cell.</summary>
    TableCell,

    /// <summary>A table caption, positioned above or below the rows.</summary>
    TableCaption,

    /// <summary>A table column, which contributes a width hint but generates no geometry.</summary>
    TableColumn,

    /// <summary>A table column group, which is a container for column hints only.</summary>
    TableColumnGroup,

    /// <summary>
    /// A <c>ruby</c> element, which carries its base text and the annotation that belongs over it.
    /// Inline-level: the whole annotation behaves as one unit within the line.
    /// </summary>
    Ruby,

    /// <summary>The base text of a <c>ruby</c> element, which the annotation is set over.</summary>
    RubyBase,

    /// <summary>An <c>rt</c> annotation, set in a smaller font over the base text it belongs to.</summary>
    RubyText,
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

    /// <summary>
    /// Gets the resolved style. For a text box this is the containing element's style. The setter
    /// lets a rebuild that only changes how the document is painted hand the previous tree the new
    /// styles, so its geometry - which nothing in the paints touches - can be kept.
    /// </summary>
    public ComputedStyle Style { get; internal set; }

    /// <summary>
    /// Gets the writing mode, text orientation, direction and upright combining the box lays its
    /// content out in.
    /// </summary>
    /// <remarks>
    /// <c>writing-mode</c> and <c>direction</c> reach a box through the cascade, but
    /// <c>text-orientation</c> and <c>text-combine-upright</c> are not registered with the parser and
    /// so are not inherited there. The builder therefore resolves them once, while it walks the tree,
    /// and every descendant takes what its ancestor ran in unless it declares otherwise.
    /// <para>
    /// The root is the one box the builder comes back to: what the whole page runs in is settled from
    /// the <c>body</c> once the tree is built, so this is written after construction rather than
    /// during it.
    /// </para>
    /// </remarks>
    public WritingModeInfo Flow { get; internal set; } = WritingModeInfo.Horizontal;

    /// <summary>
    /// Gets the marker this item is drawn with - the glyph, or the counted text with its suffix - or
    /// null when the box is not a list item, or its list asks for no marker.
    /// </summary>
    /// <remarks>
    /// Filled in by <see cref="ListMarkers"/> once a container has assembled its children, because an
    /// item's marker says which item it is among its siblings, and that is unknown while any one of
    /// them is still being built. Layout takes no notice of it: a marker beside an item hangs in the
    /// space the list was given and does not move the item's own box. It is the painting that reads it.
    /// </remarks>
    public String? Marker { get; internal set; }

    /// <summary>
    /// Gets where the marker stands: beside the item's text, or at the head of its first line.
    /// </summary>
    public MarkerPosition MarkerPosition { get; internal set; } = MarkerPosition.Outside;

    /// <summary>
    /// Gets whether the box wants the same intrinsic size at every containing block: nothing in the
    /// subtree it holds reads a fraction of one.
    /// </summary>
    /// <remarks>
    /// Only a percentage makes an intrinsic size depend on the basis it was measured against. Every
    /// other length has already been folded into pixels when the style was read, so a box whose
    /// subtree carries none answers the same however wide the containing block is - and can be
    /// measured once per pass instead of once per basis the layout happens to ask it about, which is
    /// what <see cref="Layout.LayoutEngine"/> keys its intrinsic cache on.
    /// <para>
    /// Settled by the builder, once, the way <see cref="Flow"/> is: it is a fact about the whole
    /// subtree, so it can only be answered on the way back up, and the tree does not change after
    /// that. A rebuild that changes which lengths a box resolves gets a new tree, so this cannot go
    /// stale.
    /// </para>
    /// </remarks>
    public Boolean IntrinsicIgnoresBasis { get; internal set; }

    /// <summary>Gets the source element, or null for a text or anonymous box.</summary>
    public IElement? Element { get; init; }

    /// <summary>Gets the source text node, or null for an element box.</summary>
    public IText? Source { get; init; }

    /// <summary>Gets the processed text of a text box (white space already collapsed where applicable).</summary>
    public String? Text { get; init; }

    /// <summary>Gets whether this box was generated to hold inline content, not by an element.</summary>
    public Boolean IsAnonymous { get; init; }

    /// <summary>
    /// Gets whether this text box is a list item's marker rather than something the author wrote.
    /// </summary>
    /// <remarks>
    /// A marker stated <c>inside</c> its item is the first inline box of that item, so unlike the marker
    /// that hangs beside an item it has to be laid out with the text - and that means a box of its own.
    /// It is a box the reader never chose: selecting across it skips it and copying a line does not bring
    /// it back, because a marker is not part of what the document says.
    /// </remarks>
    public Boolean IsMarker { get; init; }

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

    /// <summary>
    /// Gets whether the box takes part in normal block flow. Every table part counts as block-level:
    /// the table algorithms arrange them, but if one ever leaks into normal flow - a stray <c>td</c>
    /// inside a <c>div</c>, say - it degrades to an empty block instead of joining a line box.
    /// </summary>
    public Boolean IsBlockLevel => Kind is BoxKind.Block or BoxKind.HorizontalRule or BoxKind.Flex or
        BoxKind.Grid or BoxKind.Table or BoxKind.TableRowGroup or BoxKind.TableRow or BoxKind.TableCell or
        BoxKind.TableCaption or BoxKind.TableColumn or BoxKind.TableColumnGroup;

    public Boolean IsInlineLevel => !IsBlockLevel;

    /// <summary>Gets whether the box generates no geometry of its own and only carries a width hint.</summary>
    public Boolean IsTableColumnHint => Kind is BoxKind.TableColumn or BoxKind.TableColumnGroup;
}

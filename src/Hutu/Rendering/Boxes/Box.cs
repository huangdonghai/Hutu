using AngleSharp.Dom;
using Hutu.Rendering.Layout;
using Hutu.Rendering.Media;
using System;
using System.Collections.Generic;

namespace Hutu.Rendering.Boxes;

/// <summary>The formatting role of a box in the tree built from the styled render tree.</summary>
internal enum BoxKind
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

    /// <summary>
    /// A grid-lanes (masonry) container: block-level, and lays its children along one axis into the
    /// shortest lane of the other, so tracks on the lane axis are sized by content rather than gridded.
    /// </summary>
    Masonry,

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
internal sealed class Box
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
    /// Gets the style the item's marker is drawn with, or null when nothing in the sheets is written for
    /// it - which is the common case, and the one where the marker wears its item's own style.
    /// </summary>
    /// <remarks>
    /// A marker is a box of its own (Pseudo-Elements 4 §4.2), and Lists 3 §3.1.1 has it inherit the
    /// inheritable properties of text from its originating element unless a rule written for the marker
    /// says otherwise. The builder resolves it beside the item's own style because the two are read from
    /// different arms of the sheet: <see cref="Style"/> comes from the rules that match the element, and
    /// this from the ones that end in <c>::marker</c>, which match no element at all.
    /// </remarks>
    public ComputedStyle? MarkerStyle { get; internal set; }

    /// <summary>
    /// Gets what the arm ending in <c>::first-line</c> came to for this block, or null where the sheets write
    /// none. What it covers is not the block but the first line the block reaches, which only the layout
    /// knows, so this travels to the fragments of that line as they are marked.
    /// </summary>
    public Hutu.Rendering.Boxes.FirstLine.Paint? FirstLinePaint { get; internal set; }

    /// <summary>
    /// Gets whether the element wrote a colour of its own, which is what the first line's anonymous box
    /// leaves alone: that box reaches the text it covers by inheritance, so the text of an inline element
    /// stating its own colour keeps it (display-contents-first-line-002), while a colour stated only by a
    /// block above the line steps aside (first-line-on-ancestor-block).
    /// </summary>
    public Boolean DeclaresColor { get; internal set; }

    /// <summary>
    /// Gets the pieces one item's marker is asked to hold by the <c>content</c> written for it, or null
    /// when its contents are left to the item's own list style. An empty list is a marker the page asked to
    /// hold nothing - <c>content: none</c>, or a content this engine has no way of drawing.
    /// </summary>
    /// <remarks>
    /// Read while the marker's style is, because it comes from the same arms of the sheet, and turned into
    /// text only once the lists have counted - <see cref="ListMarkers.Finish"/>.
    /// </remarks>
    internal IReadOnlyList<MarkerPiece>? MarkerContent { get; set; }

    /// <summary>
    /// Gets which item the box was counted as among its siblings, once its list has counted them, or null
    /// while it stands in no list. The marker says that number out.
    /// </summary>
    internal Int32? ListItemNumber { get; set; }

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

    /// <summary>
    /// Gets whether a flex, grid or grid-lanes container lays this box out as an item. An item
    /// establishes an independent formatting context of its own (CSS Flexbox 1 §4, CSS Grid 1 §6.1),
    /// which is what its margins collapse against: a child's margin never escapes through the item,
    /// and the item's own margins never fold into its children's.
    /// </summary>
    /// <remarks>
    /// Only the container's own display settles this, so it is marked on the way in rather than
    /// derived from the style. A page that hands its layout to a grid keeps the room the boxes in it
    /// ask for: Wikipedia's sitenotice holds a `margin: 24px 0` inside the item that carries it, and
    /// without this the margin folded out of the page's header grid and took everything below it up
    /// with it.
    /// </remarks>
    public Boolean IsItem { get; internal set; }

    /// <summary>
    /// Gets the line count a <c>-webkit-line-clamp</c> on an ancestor asks this box to keep, or null
    /// where no ancestor clamps. A clamp on a block whose content is another block belongs to the first
    /// line the descendant formats, not to the outer block itself (CSS Overflow 3 §5.1).
    /// </summary>
    public Int32? LineClampOverride { get; internal set; }

    /// <summary>Gets the source text node, or null for an element box.</summary>
    public IText? Source { get; init; }

    /// <summary>Gets the processed text of a text box (white space already collapsed where applicable).</summary>
    public String? Text { get; set; }

    /// <summary>
    /// Gets the <c>text-transform</c> the element inherited, or null where it asked for none. It is
    /// applied once the whole tree stands, because capitalizing a word needs the letters written in
    /// the boxes around it - and out-of-flow text is not a letter of the word before it.
    /// </summary>
    public String? TextTransform { get; init; }

    /// <summary>
    /// Gets the quotation pairs the element hands down, or null where it asked for none. A generated
    /// box takes the value from the element it came from, which is why only element boxes set it.
    /// </summary>
    public QuoteSet? Quotes { get; init; } = QuoteSet.Default;

    /// <summary>Gets whether this box was generated to hold inline content, not by an element.</summary>
    public Boolean IsAnonymous { get; init; }

    /// <summary>
    /// Gets whether the box begins the content of the block it was made from, which is where that block's
    /// indentation stands. A run of inline content that a block child broke away from the head of its box
    /// resumes on a line of its own, and that line is not the block's first (CSS Text 3 §5.2).
    /// </summary>
    public Boolean StartsParentContent { get; init; } = true;

    /// <summary>
    /// Gets whether the element asked for <c>display: contents</c>, which generates no box of its own:
    /// the box here exists only to hold the children the walk found, and paints none of the element's
    /// background, border or margin (CSS Display 3 §2.5).
    /// </summary>
    public Boolean IsContents { get; init; }

    /// <summary>
    /// Gets whether this box is the letter an <c>initial-letter</c> made into a float. It stands on the
    /// block's first line even though it has left it, so <c>::first-line</c> still reaches it.
    /// </summary>
    public Boolean IsInitialLetter { get; init; }

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
    /// Gets whether this text box is the content a <c>::before</c> or <c>::after</c> generated rather
    /// than something the document says. It is laid out and drawn with the rest of the element's
    /// inline content, but it is not part of the document, so selecting across it skips it.
    /// </summary>
    public Boolean IsGenerated { get; init; }

    /// <summary>
    /// Gets whether the element's content is replaced - an image - instead of being its children.
    /// Such a box never has children: its geometry comes from the intrinsic size and the CSS
    /// constraints. Whether it is block-level or inline-level is still decided by <c>display</c>,
    /// which is why this is a flag rather than a kind of its own.
    /// </summary>
    public Boolean IsReplaced { get; init; }

    /// <summary>
    /// Gets whether a replaced box is an inline <c>&lt;svg&gt;</c> rather than an image. It is laid out
    /// the same way - a box sized by its own geometry, not by children - but painted from its own
    /// vector content instead of a fetched resource, so it becomes a distinct kind in the layout tree.
    /// </summary>
    public Boolean IsInlineSvg { get; init; }

    /// <summary>Gets the absolute URL of the replaced content.</summary>
    public String? SourceUrl { get; init; }

    /// <summary>
    /// Gets the standalone SVG markup for an <see cref="IsInlineSvg"/> box - the element's own subtree
    /// with any <c>&lt;use&gt;</c>-referenced symbols inlined - so the backend can render it without the
    /// document. Null for anything but an inline SVG.
    /// </summary>
    public String? SvgMarkup { get; init; }

    /// <summary>
    /// Gets the intrinsic size of the replaced content. Always set for a replaced box: the declared
    /// width/height attributes stand in for a missing intrinsic size and the specification's default
    /// object size is the last resort, so layout never depends on an image having loaded.
    /// </summary>
    public ImageSize? IntrinsicSize { get; init; }

    /// <summary>
    /// Gets the size the element declares through its <c>width</c>/<c>height</c> attributes, or null
    /// where it declares neither. An axis left out is zero, which the sizing reads as nothing declared
    /// there.
    /// </summary>
    /// <remarks>
    /// The specification maps those attributes to the CSS <c>width</c> and <c>height</c> properties at
    /// the presentational-hint level: under anything the author wrote, above the picture's own size.
    /// A thumbnail is the whole reason that matters - a page that asks for a 197-pixel display width
    /// says so in the attribute and ships a 250-pixel bitmap, so a box built from the bitmap is a
    /// quarter wider than the page asked for. It is kept on the box rather than folded into the style
    /// because the style a box carries is shared by everything the same rules matched, and an attribute
    /// belongs to one element.
    /// </remarks>
    public ImageSize? AttributeSize { get; init; }

    /// <summary>
    /// Gets the width a legacy HTML attribute asks of this cell, which is not a CSS size: the cell's own
    /// content is laid out at the width its column settles on, and the attribute only tells the table how
    /// wide that column should be. It lives on the box for the same reason the image's attribute size
    /// does - the style a box carries is shared by everything the same rules matched, and an attribute
    /// belongs to one element.
    /// </summary>
    public StyleLength? HintedWidth { get; init; }

    /// <summary>
    /// Gets the height a legacy HTML attribute asks of this cell or row, held the same way: the table
    /// takes it as a floor under the row, and the box itself is laid out as tall as the row comes to.
    /// </summary>
    public StyleLength? HintedHeight { get; init; }

    public List<Box> Children { get; } = new();

    /// <summary>
    /// Gets the role a table part has inside a table, or null where the element is not one.
    /// </summary>
    /// <remarks>
    /// The role is what the element is, and it does not change with the display the page writes for it:
    /// a <c>tbody</c> is a row group and a <c>tr</c> a row whatever the page says (CSS Display 3
    /// §2.7). What the page may change is the outer role - whether the box is a block, a flex container
    /// and the like - and a part that is no longer in a table keeps its role for what its children are
    /// while it is laid out as what the page asked for.
    /// </remarks>
    public BoxKind? TableRole { get; init; }

    /// <summary>
    /// Gets whether the box stands in a table, which is what the table's algorithms read to lay it out.
    /// </summary>
    /// <remarks>
    /// A cell that is not in a table is a block: it is a cell only for what the cells around it are
    /// (CSS Display 3 §2.7), and the anonymous table they make is what gives it a place to stand.
    /// </remarks>
    public Boolean IsInTable { get; init; }

    /// <summary>
    /// Gets whether the box has an inline outer role: <c>inline-flex</c>, <c>inline-grid</c>,
    /// <c>inline-table</c> and <c>inline</c> masonry keep the container they are named after inside,
    /// but take part in the surrounding line box as one atomic piece rather than stacking as a block.
    /// </summary>
    /// <remarks>
    /// The builder settles this, not the layout, because blockification has to happen first: a box
    /// whose outer role the specification blockifies - an absolutely positioned or floated one, or an
    /// item of a flex or grid container (Display 3 §2.7) - reaches here already carrying a block kind
    /// and this flag unset, so the two never disagree.
    /// </remarks>
    public Boolean IsInlineOuter { get; init; }

    /// <summary>
    /// Gets whether the box takes part in normal block flow. Every table part counts as block-level:
    /// the table algorithms arrange them, but if one ever leaks into normal flow - a stray <c>td</c>
    /// inside a <c>div</c>, say - it degrades to an empty block instead of joining a line box.
    /// </summary>
    public Boolean IsBlockLevel => !IsInlineOuter && Kind is BoxKind.Block or BoxKind.HorizontalRule or BoxKind.Flex or
        BoxKind.Grid or BoxKind.Masonry or BoxKind.Table or BoxKind.TableRowGroup or BoxKind.TableRow or BoxKind.TableCell or
        BoxKind.TableCaption or BoxKind.TableColumn or BoxKind.TableColumnGroup;

    public Boolean IsInlineLevel => !IsBlockLevel;

    /// <summary>Gets whether the box generates no geometry of its own and only carries a width hint.</summary>
    public Boolean IsTableColumnHint => Kind is BoxKind.TableColumn or BoxKind.TableColumnGroup;

    /// <summary>
    /// Gets whether this box and its whole subtree would be laid out exactly the way
    /// <paramref name="fresh"/> does: the same shape, the same replaced content, and the same style as
    /// far as the layout pass reads it.
    /// </summary>
    /// <remarks>
    /// This is the question a restyle-in-place asks. A box that answers yes keeps the geometry the
    /// layout already gave it and takes the styles the fresh build computed, which is what keeps a
    /// pointer moving across a link from laying the document out again.
    /// <para>
    /// The style half is <see cref="SameLayoutAreas"/>, which compares the areas by reference - they
    /// are interned, so two styles that set the same box model hold the same object. What that cannot
    /// answer is what is not an area: the positioning, the flex, grid and table values, the transform,
    /// the background clip and the overflow. Those are settled once for the whole document by
    /// <see cref="StyleResolution.RestylesWithoutRelayout"/>, and a box is only ever asked this
    /// question when that gate is open.
    /// </para>
    /// <para>
    /// Everything else here is compared by value, because it is what the two trees are built from and
    /// some of it moves without the document moving: a picture that has arrived since the last build
    /// gives its box another intrinsic size, and a box laid out from the old one is a box the page
    /// never asked for. The children are walked rather than counted, because a tree that agrees at the
    /// root and disagrees underneath is still a different document.
    /// </para>
    /// </remarks>
    internal Boolean SameLayout(Box fresh)
    {
        if (Kind != fresh.Kind ||
            IsAnonymous != fresh.IsAnonymous ||
            IsMarker != fresh.IsMarker ||
            IsItem != fresh.IsItem ||
            IsReplaced != fresh.IsReplaced ||
            IsInlineSvg != fresh.IsInlineSvg ||
            !String.Equals(Text, fresh.Text, StringComparison.Ordinal) ||
            !String.Equals(SourceUrl, fresh.SourceUrl, StringComparison.Ordinal) ||
            !String.Equals(SvgMarkup, fresh.SvgMarkup, StringComparison.Ordinal) ||
            !ReferenceEquals(Element, fresh.Element) ||
            !IntrinsicSize.Equals(fresh.IntrinsicSize) ||
            !AttributeSize.Equals(fresh.AttributeSize) ||
            !Equals(HintedWidth, fresh.HintedWidth) ||
            !Equals(HintedHeight, fresh.HintedHeight) ||
            !SameFlow(fresh) ||
            !String.Equals(Marker, fresh.Marker, StringComparison.Ordinal) ||
            MarkerPosition != fresh.MarkerPosition ||
            !SameMarkerStyle(fresh) ||
            IntrinsicIgnoresBasis != fresh.IntrinsicIgnoresBasis ||
            !Style.SameLayoutAreas(fresh.Style))
        {
            return false;
        }

        var mine = Children;
        var theirs = fresh.Children;

        if (mine.Count != theirs.Count)
        {
            return false;
        }

        for (var i = 0; i < mine.Count; i++)
        {
            if (!mine[i].SameLayout(theirs[i]))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Gets whether the box lays its content out in the same writing mode and direction.</summary>
    private Boolean SameFlow(Box fresh) =>
        Flow.Mode == fresh.Flow.Mode &&
        Flow.Orientation == fresh.Flow.Orientation &&
        Flow.CombinesUpright == fresh.Flow.CombinesUpright &&
        Flow.Direction == fresh.Flow.Direction;

    /// <summary>
    /// Gets whether the marker this box wears would be laid out the same way as <paramref name="fresh"/>'s.
    /// The two styles are compared by their areas, as <see cref="Style"/> is: a marker that came to the
    /// same box model, font and text at a different colour needs no layout, only a new paint.
    /// </summary>
    private Boolean SameMarkerStyle(Box fresh)
    {
        var mine = MarkerStyle;
        var theirs = fresh.MarkerStyle;

        if (!mine.HasValue || !theirs.HasValue)
        {
            return !mine.HasValue && !theirs.HasValue;
        }

        return mine.Value.SameLayoutAreas(theirs.Value);
    }
}

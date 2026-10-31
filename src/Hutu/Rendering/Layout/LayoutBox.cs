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

    /// <summary>
    /// A replaced box holding an inline <c>&lt;svg&gt;</c>: sized like an image, but painted from its
    /// own vector content (serialized to markup) rather than a fetched resource.
    /// </summary>
    Svg,

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

    /// <summary>
    /// Gets or sets how far the box stands off the place the flow gave it, which is the displacement its
    /// own <c>position: relative</c> offsets add: what is drawn moves, the room it holds does not
    /// (CSS 2.1 §9.4.3). A box that aligns or stacks its children asks where they stood to work out how
    /// much room is left, and reading that off the displaced boxes would charge the move twice.
    /// </summary>
    public Offset RelativeDisplacement { get; set; }

    /// <summary>Gets or sets how the box is positioned, which decides whether it joins the stacking
    /// order and whether it took part in normal flow at all.</summary>
    public PositionMode Position { get; set; }

    /// <summary>Gets or sets the declared stacking level, or zero for an automatic one.</summary>
    public Int32 ZIndex { get; set; }

    /// <summary>
    /// Gets or sets whether the box joins the stacking order even though it stays in the flow, which is
    /// what a <c>z-index</c> written on a flex or grid item does: the item is a stacking context of its
    /// own and paints at its stated level among the positioned boxes of its context, rather than with
    /// the in-flow content around it (CSS Flexbox 1 §5.4, CSS Grid 1 §9).
    /// </summary>
    public Boolean HasStackingLevel { get; set; }

    /// <summary>Gets or sets the rule that pins a sticky box to the viewport as the page scrolls.</summary>
    public StickyConstraint? Sticky { get; set; }

    /// <summary>
    /// Gets or sets whether the box forms a stacking context: the levels its descendants name are its own
    /// to answer, and the walk that reached it carries nothing past it (CSS 2.1 §Appendix E).
    /// </summary>
    public Boolean EstablishesStackingContext { get; set; }

    /// <summary>
    /// Gets or sets how strongly the box is laid down. Below one, the box and everything drawn inside it go
    /// through one layer at that strength, which is what <c>opacity</c> asks of them (CSS Compositing 1
    /// §9.1); one, the default, draws them at full strength and asks for no layer at all.
    /// </summary>
    public Double Opacity { get; set; } = 1;

    /// <summary>
    /// Gets or sets how the box's picture is mixed with the backdrop it is drawn over. A mode other
    /// than normal also stands the box as a stacking context of its own (CSS Compositing 1 §5.1).
    /// </summary>
    public Paint.BlendMode MixBlendMode { get; set; } = Paint.BlendMode.Normal;

    /// <summary>Gets whether the box was taken out of normal flow by <c>absolute</c> or <c>fixed</c>.</summary>
    public Boolean IsOutOfFlow => Position is PositionMode.Absolute or PositionMode.Fixed;

    /// <summary>
    /// Gets what the <c>::first-line</c> of a block this fragment stands in came to, where the fragment
    /// stands on that block's first line. Only the properties the arm wrote are drawn from it: the
    /// pseudo-element is a box around the line, so anything it says nothing about is left as the text came
    /// to it - which is what lets a colour declared on the text inside stand beside a line that is red.
    /// </summary>
    public Hutu.Rendering.Boxes.FirstLine.Paint? FirstLinePaint { get; set; }

    public Thickness Margin { get; set; }

    public Thickness Border { get; set; }

    public Thickness Padding { get; set; }

    public List<LayoutBox> Children { get; } = new();

    /// <summary>Gets the text of a text node, or null for anything else.</summary>
    public String? Text { get; set; }

    /// <summary>
    /// Gets or sets which characters of <see cref="Text"/> the line set half-width, one code per
    /// UTF-16 unit; null when the run has nothing to trim (CSS Text 4 §8.5).
    /// </summary>
    internal String? Trims { get; set; }

    /// <summary>
    /// Gets or sets which boundaries of <see cref="Text"/> the property widened, one code per UTF-16
    /// unit; null when the run spaces nothing (CSS Text 4 §8.4).
    /// </summary>
    internal String? AutoSpaces { get; set; }

    /// <summary>Gets or sets the room one spaced boundary of <see cref="Text"/> adds, in pixels.</summary>
    internal Double AutoSpaceRoom { get; set; }

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
    /// Gets or sets the room a justified line owed after each place between this run's words, and how
    /// many of those places the run holds. The width the box was laid out at already counts them; the
    /// painter needs the two apart because it has to put the room between the glyphs rather than behind
    /// the whole run.
    /// </summary>
    internal Double Justification { get; set; }

    internal Int32 Justifies { get; set; }

    /// <summary>
    /// Gets or sets whether the run's glyphs are drawn from the far end of its room. The shaper reads a
    /// run's direction off its characters, and this turns the glyphs round where the level the bidi
    /// algorithm gave the run says otherwise. Null leaves the answer to the flow.
    /// </summary>
    internal Boolean? Reversed { get; set; }

    /// <summary>
    /// Gets or sets the lines to draw across this box's text: the decorating boxes whose decoration
    /// reaches here, from the outermost in - which is also the order they are painted in, so an inner
    /// line lies over an outer one (CSS Text Decoration 4 §5.1). Resolved once the tree is placed, where
    /// the ancestry of a box is known and the painter need only read what it was given.
    /// </summary>
    internal List<DecorationSource>? Decorations { get; set; }

    /// <summary>
    /// Gets where a fieldset's rendered legend stands, in document coordinates, so that the border it
    /// stands in can leave a gap for it (HTML rendering §15.3.12).
    /// </summary>
    internal Rect? LegendBox { get; set; }

    /// <summary>
    /// Gets or sets whether this run is a ruby annotation, set above its base rather than text the line
    /// under the base decorates. The annotation is laid out as a box of its own and never as an element
    /// box, so the pass that hands the decorations down cannot tell it apart from the base's text by
    /// looking at the tree alone.
    /// </summary>
    internal Boolean IsRubyAnnotation { get; set; }

    /// <summary>
    /// Gets or sets where each line this box's text is drawn under stands on the line of text it is part
    /// of, in the order of <see cref="Decorations"/>, or null for a box that draws none. A decorating box
    /// uses one position and one thickness along each line for its underlines and overlines, so the answer
    /// is settled once for the line from all the text the box decorates there rather than once per run of
    /// it (CSS Text Decoration 4 §2.5, §2.9).
    /// </summary>
    internal List<DecorationPlacement>? DecorationPlacements { get; set; }

    /// <summary>
    /// Gets or sets which of <see cref="Decorations"/> this box's own element asked for, counting from
    /// the outermost, or -1 where none of them is its own. A run lifting its text out of the row its line
    /// stands on is answered from its parent for the lines an ancestor asked for, but keeps its own face
    /// and its own row for the line its own element asked for - which is why a superscript with a
    /// decoration of its own draws its line with itself, while the one its container asked for runs
    /// straight on past it (CSS Text Decoration 4 §2.1, §2.5).
    /// </summary>
    internal Int32 OwnDecoration { get; set; } = -1;

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
    /// Gets or sets the standalone SVG markup drawn in a <see cref="LayoutKind.Svg"/> box: the element's
    /// own subtree with any <c>&lt;use&gt;</c>-referenced symbols inlined, ready for the backend to parse.
    /// </summary>
    public String? SvgMarkup { get; set; }

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
    /// Gets or sets the point the <see cref="Transform"/> is taken about, within the border box: the
    /// resolved <c>transform-origin</c>. It is kept relative to the box rather than in document
    /// coordinates, so that moving the box later - a relative offset on an ancestor, a scroll, a table
    /// alignment - carries the point along without patching it wherever the box is moved.
    /// </summary>
    public Offset TransformOrigin { get; set; }

    /// <summary>
    /// Gets or sets whether the box's plane is turned away from the viewer, which is when a box with
    /// <c>backface-visibility: hidden</c> shows nothing (CSS Transforms 2 §backface-visibility).
    /// </summary>
    public Boolean FacesAway { get; set; }

    /// <summary>
    /// Gets or sets whether the box is drawn through a matrix that cannot be undone - a zero scale, say -
    /// which CSS Transforms 2 §mathematical-description answers with nothing at all, the box and
    /// everything inside it.
    /// </summary>
    public Boolean SingularTransform { get; set; }

    /// <summary>
    /// Gets or sets the box's own matrix as a 4×4 of the page: the transform (and its origin) without any
    /// perspective a parent states, which is what a 3D rendering context composes.
    /// </summary>
    internal Matrix4 OwnTransform4 { get; set; } = Matrix4.Identity;

    /// <summary>
    /// Gets or sets the perspective the parent states, as a 4×4 of the page. It is carried apart from
    /// <see cref="OwnTransform4"/> because it applies to what is inside the box rather than to the box.
    /// </summary>
    internal Matrix4 ParentView4 { get; set; } = Matrix4.Identity;

    /// <summary>
    /// Gets or sets whether <see cref="Transform"/> already carries every ancestor's transform, which is
    /// what a box inside a 3D rendering context is drawn with: it is placed in the page outright rather
    /// than through the matrices the boxes around it were drawn with.
    /// </summary>
    public Boolean AbsoluteTransform { get; set; }

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
    /// Gets or sets whether this fragment of a block was cut from the rest of it at its own start edge,
    /// i.e. the block began in an earlier column or page. A cut edge carries no border, no padding and no
    /// corner curve, because everything on it was drawn where the block started.
    /// </summary>
    /// <remarks>Only meaningful for a block broken across columns or pages, and only under
    /// <c>box-decoration-break: slice</c> (CSS Break 3 §4.7).</remarks>
    public Boolean SlicesBlockStart { get; set; }

    /// <summary>
    /// Gets or sets whether this fragment was cut at its own end edge, i.e. the block carries on into a
    /// later column or page. The mirror of <see cref="SlicesBlockStart"/>.
    /// </summary>
    public Boolean SlicesBlockEnd { get; set; }

    /// <summary>Gets or sets the fragment this piece of a broken box shows, where the content was laid
    /// out in the whole box and only the stretch inside the fragment may be drawn.</summary>
    public Rect? FragmentClip { get; set; }

    /// <summary>Gets or sets the table box - the grid and the border around it, without the captions,
    /// which stand in the wrapper outside it. The table's background and border are painted there and
    /// not over them.</summary>
    public Rect? TableBox { get; set; }

    /// <summary>
    /// Gets or sets the border box the block would have had before it was broken, where this fragment is
    /// one piece of it: a background is painted over that whole box and then cut with the box, which is
    /// what keeps one gradient running down both pieces of a sliced block.
    /// </summary>
    public Rect? BackgroundOrigin { get; set; }

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

    /// <summary>Gets or sets the strokes to paint instead of the box's own, set with
    /// <see cref="BorderColors"/>: a collapsed line is drawn in the stroke of whichever border won it,
    /// which may be a row's or a column's rather than this box's.</summary>
    public BorderStyles? BorderStyles { get; set; }

    /// <summary>
    /// Gets or sets the decorations the container laid out its gaps with. Only a context that makes gaps
    /// - and knows how many of them there are and where each one stands - can say which of the container's
    /// rule list a gap takes, so it hands the finished bands to the painter here.
    /// </summary>
    public List<GapRuleSegment>? GapDecorations { get; set; }

    /// <summary>
    /// Gets or sets the marks this run of text is drawn with, one per character the page asked to have
    /// marked, placed against the line the run stands on. They are paint alone: the line was laid out as
    /// though the text had none.
    /// </summary>
    public List<EmphasisMark>? Marks { get; set; }

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

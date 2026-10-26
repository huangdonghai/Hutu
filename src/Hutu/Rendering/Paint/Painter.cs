using AngleSharp.Css.Dom;
using AngleSharp.Css.Values;
using Hutu.Rendering.Boxes;
using Hutu.Rendering.Layout;
using Hutu.Rendering.Media;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Hutu.Rendering.Paint;

/// <summary>
/// Walks the layout tree and produces the platform-neutral drawing instructions.
/// </summary>
/// <remarks>
/// Borders and text are painted on the border box, and so is a background, which reaches under a
/// transparent border by default unless <c>background-clip</c> moves it inwards. Everything a rounded
/// box paints follows its corner radii. The walk order is the paint order: a block's own decorations
/// come before its children, and among the children the stacking levels are honoured - the negative
/// <c>z-index</c> layer, the in-flow content, the positioned boxes with an automatic level, then the
/// positive layer. Boxes fixed to the viewport are diverted to their own layer instead, and a box
/// that hides its overflow wraps its children in a clipping pair.
/// </remarks>
internal sealed class Painter
{
    /// <summary>The colour a selection is highlighted with when the host chooses none.</summary>
    /// <remarks>
    /// Translucent, so the glyphs stay readable through it: a highlight marks the text rather than
    /// replacing it.
    /// </remarks>
    public const UInt32 DefaultHighlightColor = 0x5599CCFF;

    /// <summary>
    /// Gets or sets the highlight rectangle of each selected run, keyed by the run. A run that is
    /// absent is painted as if nothing were selected.
    /// </summary>
    /// <remarks>
    /// The rectangles name boxes of the layout they were computed from, so a highlight left over
    /// from an earlier layout is simply never matched and degrades to no highlight at all.
    /// </remarks>
    public IReadOnlyDictionary<LayoutBox, Rect>? Highlight { get; set; }

    /// <summary>Gets or sets the colour a selection is highlighted with, as packed ARGB.</summary>
    public UInt32 HighlightColor { get; set; } = DefaultHighlightColor;

    public PaintList Paint(LayoutResult layout)
    {
        var list = new PaintList
        {
            Bounds = new Rect(0, 0, layout.Width, layout.Height),
        };

        // The canvas is the surface the document is drawn on and no element owns it, so CSS Backgrounds 3
        // §2.11 gives its background to the root element - and to the body under it where the root says
        // nothing of a background at all. The box that hands its background over then paints none of its
        // own, and what is handed over covers the whole canvas while its layers are still placed against the
        // root's box (§2.11.1).
        var donor = CanvasDonor(layout.Root);

        _canvas = donor?.Source;

        // The strength of the box that hands its background to the canvas is what the whole page is laid
        // down at: the backdrop and everything drawn over it come to one picture, which is what CSS
        // Compositing 1 §9.1 asks of a group - and what Chrome draws for a root that fades, where the
        // background reaches the canvas and the opacity still reaches the background. The layer is opened
        // around both halves here, so the box's own pass does not open a second one inside.
        _faded = donor is { } handing && handing.Opacity < 1 ? handing : null;

        // The shape the document element was cut to cuts the backdrop as well as what the element holds:
        // the background it handed to the canvas is still its own picture, and a shape is a cut through the
        // whole of what the element paints (CSS Masking 1 §5.1, and what the clip-path-document-element pair
        // measures). It is opened around both halves here, so the box's own pass keeps from cutting twice.
        var page = ShapeOf(layout.Root);

        _page = page is null ? null : layout.Root;

        if (page is { } framed)
        {
            AddCut(list.Commands, framed);
        }

        if (_faded is { } faded)
        {
            list.Commands.Add(new PushLayerCommand(list.Bounds, faded.Opacity));
        }

        if (donor is { } box)
        {
            PaintCanvas(box, layout.Root, list.Bounds, list.Commands);
        }

        PaintBox(layout.Root, list, list.Commands);

        if (_faded is not null)
        {
            list.Commands.Add(new PopLayerCommand(list.Bounds));
        }

        if (page is { } cut)
        {
            AddPop(list.Commands, cut.Bounds);
        }

        _faded = null;
        _page = null;
        _canvas = null;
        return list;
    }

    /// <summary>The box whose background the canvas wears, and which therefore keeps none of its own.</summary>
    private Box? _canvas;

    /// <summary>
    /// The box whose <c>clip-path</c> the whole page is cut to, when the document element wears a shape -
    /// the backdrop it handed over is part of the picture the shape cuts.
    /// </summary>
    private LayoutBox? _page;

    /// <summary>
    /// The box whose <c>opacity</c> the whole page picture is laid down through, when the background it
    /// hands to the canvas means that strength belongs to the canvas rather than to the box's own pass.
    /// </summary>
    private LayoutBox? _faded;

    /// <summary>
    /// The legacy <c>clip</c> of every box the walk currently stands inside, outermost first. An overflow
    /// clip cuts only the descendants whose containing block is inside the box that hides its overflow, and
    /// a box lifted to another layer escapes it; a <c>clip</c> cuts the whole subtree it was written on,
    /// and a descendant painted from the viewport layer is still inside that subtree, so the cut has to
    /// travel with it (CSS Masking 1 §1.3, which is what `clip-fixed-pos-transform-descendant-001` asks).
    /// </summary>
    private List<Rect> _cuts = [];

    /// <summary>
    /// How many of <see cref="_cuts"/> are already laid around the list being painted. A clip is a
    /// rectangle of the page rather than of the box it was written on, so a cut that stands around a box
    /// needs no second pair around what is painted inside it - but the moment the painting moves to another
    /// list, or to another pass of this one, the standing cuts have to be put down again from the first.
    /// </summary>
    private Int32 _cutDepth;

    /// <summary>No box above this one was written to be cut.</summary>
    private static readonly List<Rect> NoCuts = [];

    /// <summary>
    /// Gets the box whose background the canvas is painted with: the root's own when the root states a
    /// background, and otherwise the body's, which §2.11.2 hands to the canvas and takes away from the body's
    /// own box.
    /// </summary>
    /// <remarks>
    /// A body with containment hands nothing over - the note to that chapter says containment disables the
    /// special handling of the body element, and a root that is contained gives the same answer. A body that
    /// generated no box at all, being hidden or <c>display: contents</c>, has no background to hand over
    /// either, and a root that generated none leaves the document with no canvas at all (§2.11).
    /// </remarks>
    private static LayoutBox? CanvasDonor(LayoutBox root)
    {
        var style = root.Source.Style;

        if (StatesBackground(style))
        {
            return root;
        }

        if (style.Contain.Stated)
        {
            return null;
        }

        var body = BodyUnder(root);

        return body is not null &&
               !body.Source.Style.Contain.Stated &&
               StatesBackground(body.Source.Style)
            ? body
            : null;
    }

    /// <summary>
    /// Gets the root's body box - the first child of the root that is an HTML body - or null where the
    /// document has none. The body is found by the element rather than by the shape of the box because that
    /// is what the chapter names: it is the first <c>BODY</c> child of the root element. A body that left no
    /// box behind - being hidden, or set out of the box tree altogether - has no background to hand over,
    /// which is the same answer as having written none (§2.11).
    /// </summary>
    private static LayoutBox? BodyUnder(LayoutBox root)
    {
        foreach (var child in root.Children)
        {
            if (child.Source.Element?.LocalName.Equals("body", StringComparison.OrdinalIgnoreCase) == true)
            {
                return child;
            }
        }

        return null;
    }

    /// <summary>
    /// Gets whether a style states a background the canvas could be painted with: a colour with any ink in
    /// it, or a layer naming an image or a gradient. §2.11.2 asks exactly this of the root - the hand over
    /// happens where its <c>background-image</c> is <c>none</c> and its <c>background-color</c> is
    /// transparent - and a colour with no ink in it is the same nothing as the absence of one.
    /// </summary>
    private static Boolean StatesBackground(ComputedStyle style) =>
        style.BackgroundColor is { } colour && colour.A != 0 ||
        style.Backgrounds.Any(layer => layer.Url is { Length: > 0 } || layer.Gradient is not null);

    /// <summary>
    /// Paints the canvas with the background a box handed over. The area is the whole canvas and the corners
    /// are square, since the surface has no box of its own to round; the layers inside it are placed and
    /// sized against the root's border box as if the root were the only thing painted, which is how a margin
    /// on the root moves where a background begins without narrowing what it covers (§2.11.1).
    /// </summary>
    private static void PaintCanvas(LayoutBox donor, LayoutBox root, Rect canvas, List<PaintCommand> target)
    {
        var style = donor.Source.Style;
        var placement = root.BorderBox;

        // Gathered last-first for the same reason a box gathers its own: the first layer the page wrote is
        // the one on top, and the canvas is painted before anything else on it.
        var layers = new List<PaintCommand>();

        if (style.BackgroundColor is { } colour && colour.A != 0)
        {
            layers.Add(new FillCommand(canvas, ToArgb(colour), CornerRadii.None));
        }

        var declared = style.Backgrounds;

        for (var index = declared.Count - 1; index >= 0; index--)
        {
            var layer = declared[index];

            if (layer.Gradient is { } gradient)
            {
                var (tile, repeatX, repeatY) = GradientTile(layer, placement);
                var length = gradient.LineLength(tile.Width, tile.Height);
                var (radiusX, radiusY) = gradient.EndingRadii(tile.Width, tile.Height);

                layers.Add(new GradientCommand(
                    canvas,
                    tile,
                    repeatX,
                    repeatY,
                    gradient.Shape,
                    gradient.AngleDegrees,
                    tile.X + gradient.CenterX.Resolve(tile.Width),
                    tile.Y + gradient.CenterY.Resolve(tile.Height),
                    gradient.StopsAt(length, RampPixels(gradient, tile, length)),
                    CornerRadii.None,
                    radiusX,
                    radiusY));
            }
            else if (layer.Url is { Length: > 0 })
            {
                layers.Add(new BackgroundImageCommand(canvas, placement, layer, CornerRadii.None));
            }
        }

        target.AddRange(layers);
    }

    /// <summary>One overflow clip standing around the place a box was found, kept so it can be put
    /// back when that box is painted in a stacking context's layer.</summary>
    private readonly record struct ClipEdge(Rect Bounds, CornerRadii Radii);

    /// <summary>A positioned box left for an ancestor's stacking context to paint, with the overflow
    /// clips that still reach it - those of the boxes it stands in that lie between it and the
    /// layer, which the layer must not lift it out of - and the legacy cuts it was found inside.</summary>
    private sealed class DeferredBox(LayoutBox box, List<ClipEdge> clips, List<Rect> cuts)
    {
        public LayoutBox Box { get; } = box;

        public List<ClipEdge> Clips { get; } = clips;

        /// <summary>Gets the legacy <c>clip</c> of the boxes this one was found inside, outermost first.</summary>
        public List<Rect> Cuts { get; } = cuts;
    }

    /// <summary>Collects the positioned boxes a stacking context paints in its layer, and the overflow
    /// clips standing along the walk that reaches each of them.</summary>
    private sealed class DeferredStack
    {
        /// <summary>Gets the boxes naming a negative level, in the order they were gathered. They reach
        /// under the whole of the content that stayed in the flow, however deep they stand.</summary>
        public List<DeferredBox> Below { get; } = [];

        /// <summary>Gets the boxes waiting for the layer, in the order they were gathered.</summary>
        public List<DeferredBox> Pending { get; } = [];

        /// <summary>
        /// Gets the boxes naming a level above the layer, in the order they were gathered. A level is
        /// answered by the context the box belongs to, and a box that stands inside one which forms no
        /// context of its own belongs to the context above - painting it where it happens to stand puts it
        /// under content that should have gone beneath it.
        /// </summary>
        public List<DeferredBox> Above { get; } = [];

        /// <summary>Gets the clips of the boxes climbed through on the way down that hide their
        /// overflow, outermost first.</summary>
        public List<ClipEdge> ActiveClips { get; } = [];

        /// <summary>
        /// Gets the clips that reach a box taken out of the flow found at this depth, outermost first. A
        /// box like that is clipped along its containing block chain and not along the boxes it merely
        /// stands in (CSS 2.1 §11.1.1, CSS Overflow 1 §3.2), and its containing block is the nearest
        /// positioned ancestor - so only a box that is itself a containing block puts its own clip on this
        /// list, and the static ones between it and the descendant stay off it.
        /// </summary>
        public List<ClipEdge> ChainClips { get; } = [];

        /// <summary>Gets whether the walk found no level for this context to answer, which is the common
        /// case and the one that leaves the document order to be the paint order.</summary>
        public Boolean IsEmpty => Below.Count == 0 && Pending.Count == 0 && Above.Count == 0;
    }

    private static readonly List<ClipEdge> NoClips = [];

    /// <param name="deferred">
    /// Where a positioned descendant that belongs to a stacking context above this box is left, or null
    /// when this box is painted into the layer it stands in.
    /// </param>
    private void PaintBox(LayoutBox box, PaintList list, List<PaintCommand> target, DeferredStack? deferred = null)
    {
        // A box that carries a transform is painted - together with everything inside it - through the
        // matrix, taken about its origin. The pair of commands is also what makes the box the layer its
        // out-of-flow descendants belong to: the subtree is painted self-contained (it is not handed to
        // an ancestor's deferred stack) so it lands under the same matrix, which is what CSS Transforms
        // asks of an element whose transform is not none.
        var cut = CutOf(box);

        if (box.Transform is { } transform)
        {
            var origin = new Offset(box.BorderBox.X + box.TransformOrigin.X, box.BorderBox.Y + box.TransformOrigin.Y);
            target.Add(new PushTransformCommand(box.BorderBox, transform, origin));

            // The cut is measured in the page, the matrix in the box: inside the transform the rectangle
            // the box was cut to travels with it, which is what the pair of commands below lays down.
            if (cut is { } moved)
            {
                _cuts.Add(moved);
            }

            PaintClipped(box, list, target, deferred: null);

            if (cut is not null)
            {
                _cuts.RemoveAt(_cuts.Count - 1);
            }

            target.Add(new PopTransformCommand(box.BorderBox));
            return;
        }

        if (cut is { } standing)
        {
            _cuts.Add(standing);
        }

        PaintClipped(box, list, target, deferred);

        if (cut is not null)
        {
            _cuts.RemoveAt(_cuts.Count - 1);
        }
    }

    /// <summary>
    /// Paints a box inside the shape its <c>clip-path</c> cuts it to, if it states one.
    /// </summary>
    /// <remarks>
    /// The cut covers the whole of the box's picture - its background, borders, outline and shadow, its
    /// content and everything inside them - because a shape is what the element was cut to rather than a
    /// limit put on what it contains (CSS Masking 1 §5.1). The box that carries one stands a stacking
    /// context of its own, so nothing it paints is answered a level above the cut: the pair below is the
    /// whole of where the box's ink may reach.
    /// </remarks>
    private void PaintClipped(LayoutBox box, PaintList list, List<PaintCommand> target, DeferredStack? deferred)
    {
        if (ReferenceEquals(box, _page))
        {
            // The page was opened on this very cut, and the document's whole picture - the backdrop
            // included - is what sits inside it.
            PaintFaded(box, list, target, deferred);
            return;
        }

        if (ShapeOf(box) is not { } cut)
        {
            PaintFaded(box, list, target, deferred);
            return;
        }

        AddCut(target, cut);

        PaintFaded(box, list, target, deferred);

        AddPop(target, cut.Bounds);
    }

    /// <summary>
    /// Gets the area a <c>clip-path</c> came to on the box it was written on, or null where the box states
    /// no shape. The cut covers the box's own background, borders, outline and shadow, its content and
    /// everything inside them - which is why it is asked of the box rather than of what it contains.
    /// </summary>
    private static ClipCut? ShapeOf(LayoutBox box) =>
        box.Source.Style.ClipPath is { } shape ? shape.Cut(BoxesOf(box)) : null;

    private static void AddCut(List<PaintCommand> target, ClipCut cut) =>
        target.Add(
            cut.Outline is { } outline
                ? new PushClipCommand(cut.Bounds, cut.Radii, outline, cut.EvenOdd)
                : new PushClipCommand(cut.Bounds, cut.Radii));

    private static void AddPop(List<PaintCommand> target, Rect bounds) =>
        target.Add(new PopClipCommand(bounds));

    /// <summary>
    /// Gets the four boxes a <c>clip-path</c> may be told to measure itself against. The margin box is not
    /// one the layout keeps: it is the border box with the margins the box was given put back around it,
    /// and a shape written against it reaches past the box whatever the box was cut to.
    /// </summary>
    private static ClipBoxes BoxesOf(LayoutBox box)
    {
        var border = box.BorderBox;
        var margin = box.Margin;

        return new ClipBoxes(
            border,
            box.PaddingBox,
            box.ContentBox,
            new Rect(
                border.X - margin.Left,
                border.Y - margin.Top,
                border.Width + margin.Left + margin.Right,
                border.Height + margin.Top + margin.Bottom));
    }

    /// <summary>
    /// Paints a box through the one layer its <c>opacity</c> asks for, if it asks for one.
    /// </summary>
    /// <remarks>
    /// The pair covers the whole of the box's picture - its own background, borders and content and
    /// everything inside them - which is what the group strength means (CSS Compositing 1 §9.1). Nothing
    /// that belongs to the box is painted outside it: a box below full strength stands a stacking context of
    /// its own, so the walk that gathers levels stops here and keeps what is named inside to be answered
    /// inside. What leaves by another rule does not ride along: an ancestor's opacity gives no box a frame
    /// to resolve against, so a <c>fixed</c> descendant is still drawn in the viewport's own pass and keeps
    /// full strength.
    /// </remarks>
    private void PaintFaded(LayoutBox box, PaintList list, List<PaintCommand> target, DeferredStack? deferred)
    {
        if ((box.Opacity >= 1 && box.MixBlendMode == BlendMode.Normal) || ReferenceEquals(box, _faded))
        {
            PaintBoxContent(box, list, target, deferred);
            return;
        }

        target.Add(new PushLayerCommand(box.BorderBox, box.Opacity, box.MixBlendMode));
        PaintBoxContent(box, list, target, deferred);
        target.Add(new PopLayerCommand(box.BorderBox));
    }

    /// <summary>
    /// Gets the rectangle the box cuts its subtree to, or null where it states none. Only a box taken out of
    /// the flow answers to the legacy <c>clip</c>, and the area is the border box the offsets were read
    /// against, which the layout has to have settled first (§11.1.1, CSS Masking 1 §1.3). The gather walk
    /// and the painting both answer from here, so the cut a box is painted with is the one its descendants
    /// were gathered under.
    /// </summary>
    private static Rect? CutOf(LayoutBox box) =>
        box.Position is PositionMode.Absolute or PositionMode.Fixed
            ? box.Source.Style.Clip?.ClipOf(box.BorderBox)
            : null;

    /// <summary>
    /// Paints a box inside the rectangle its <c>clip</c> cuts it to, if it states one.
    /// </summary>
    /// <remarks>
    /// The legacy <c>clip</c> reaches further than an overflow does: it cuts the box's own background,
    /// borders, text decoration and outline as well as its content and descendants, and it does that from
    /// the border box rather than the padding one (CSS 2.1 §11.1.1, CSS Masking 1 §1.3). Only an
    /// absolutely positioned box answers to it - a box that stayed in the flow is clipped by its
    /// ancestors, not by itself.
    /// </remarks>
    private void PaintBoxContent(LayoutBox box, PaintList list, List<PaintCommand> target, DeferredStack? deferred)
    {
        // The box's own cut is already on the standing chain; what is laid here is every cut of that chain
        // the list has not been given yet.
        var from = _cutDepth;

        for (var i = from; i < _cuts.Count; i++)
        {
            target.Add(new PushClipCommand(_cuts[i], CornerRadii.None));
        }

        var outside = _cutDepth;
        _cutDepth = _cuts.Count;

        PaintBoxPresentation(box, list, target, deferred);

        _cutDepth = outside;

        for (var i = _cuts.Count - 1; i >= from; i--)
        {
            target.Add(new PopClipCommand(_cuts[i]));
        }
    }

    private void PaintBoxPresentation(LayoutBox box, PaintList list, List<PaintCommand> target, DeferredStack? deferred)
    {
        // A box that is not seen draws nothing of itself - no background, border, image, text, marker
        // or outline - and still lets what is inside it draw, because a descendant can ask to be seen
        // again and a hidden menu is a hundred boxes hidden by the one around them. The geometry is
        // untouched either way, which is why this is a painting rule and not a layout one.
        //
        // A line box is not an element and borrows the style of the text it carries, so it never
        // decides for itself: its runs answer for their own elements, and a visible span in a hidden
        // paragraph keeps its line to be drawn on.
        if (box.Kind != LayoutKind.Line && box.Source.Style.IsHidden)
        {
            PaintChildren(box, list, target, deferred);
            return;
        }

        // A box seen from behind shows nothing - and neither does what stands on its plane, which is
        // the box's own content rather than a plane of its own. A matrix that cannot be undone has no
        // plane to show either.
        if (box.Kind != LayoutKind.Line && (box.SingularTransform || (box.FacesAway && box.Source.Style.HidesBackface)))
        {
            return;
        }

        switch (box.Kind)
        {
            case LayoutKind.Text:
                PaintText(box, target);
                break;

            case LayoutKind.HorizontalRule:
                AddShadows(box, box.Source.Style, target, inset: false);
                AddShadows(box, box.Source.Style, target, inset: true);
                AddBorder(box, box.Source.Style, target);
                AddBorderImage(box, box.Source.Style, target);
                break;

            case LayoutKind.Image:
                // An image box can carry a background and a border of its own, which paint under the
                // image itself.
                AddShadows(box, box.Source.Style, target, inset: false);
                PaintBackground(box, box.Source.Style, target);
                AddShadows(box, box.Source.Style, target, inset: true);
                AddBorder(box, box.Source.Style, target);
                AddBorderImage(box, box.Source.Style, target);
                PaintImage(box, target);
                break;

            case LayoutKind.Svg:
                // An inline SVG is a replaced box like an image: its own background and border paint
                // under the vector content, which is drawn from the markup the box carries.
                AddShadows(box, box.Source.Style, target, inset: false);
                PaintBackground(box, box.Source.Style, target);
                AddShadows(box, box.Source.Style, target, inset: true);
                AddBorder(box, box.Source.Style, target);
                AddBorderImage(box, box.Source.Style, target);
                PaintSvg(box, target);
                break;

            case LayoutKind.Inline:
                // An inline element draws what any block draws - background, border, then children -
                // over the band its own runs take rather than over a whole line. It is given a box
                // whether or not it has anything to draw, and most of them have nothing: the guards
                // inside PaintBackground and AddBorder are what turn those away.
                PaintBlock(box, list, target, deferred);
                break;

            case LayoutKind.Line:
                // A line box has no decorations of its own; only its runs are painted. A box taken out of
                // the flow that stands on the line - the place it would have had, which is where its static
                // position comes from - is left for the stacking context that gathered it, exactly as one
                // standing among blocks is: the line is here only to say where it belongs. Routed through
                // PaintChild rather than PaintBox, so that a fixed child lands in the viewport layer instead
                // of the line's own commands: a fixed box is clipped only by an ancestor that is its
                // containing block, never by a plain overflow:hidden wrapper that merely holds its static
                // position (CSS 2.1 11.1.1). Painting it inline here is what cut a fixed sidebar down to
                // the height of such a wrapper.
                PaintInFlow(box, list, target, deferred);
                break;

            default:
                PaintBlock(box, list, target, deferred);
                break;
        }

        // The outline is emitted last because it is drawn around the box rather than in it, which also
        // puts it after the clip the children were wrapped in: the box's own overflow must not cut it
        // off, while the clip of the nearest ancestor that hides its overflow still holds it in.
        // A line box and a run of text are not elements of their own - both borrow the style of what
        // they sit in - so an outline must not be emitted for them, or a block would grow one per line
        // and a paragraph one per word. An inline element is an element, and draws its outline around
        // each of the fragments it was broken into, one per line.
        if (box.Kind is not (LayoutKind.Line or LayoutKind.Text))
        {
            AddOutline(box, box.Source.Style, target);
        }
    }

    /// <summary>
    /// Emits the shadows of one kind a box is drawn with - the outer ones, or the inner - in the order a
    /// backend has to lay them down.
    /// </summary>
    /// <remarks>
    /// The list a page wrote is ordered front to back, "the first shadow is on top" (CSS Backgrounds 3
    /// §6.1.3), so it is emitted last-first: the shadow standing furthest back is the first ink on the page
    /// and the one the author wrote first is the last laid, over the rest.
    /// <para>
    /// Each shadow is drawn from the box's own area - the border box for an outer one, the padding box for
    /// an inner one, which is what §6.1.1 says each is a copy of - moved by the offsets, and moved again by
    /// the spread, which outsets the outer shape and insetting the inner one. The size the spread leaves is
    /// floored at nothing: a spread wider than the box it was written on leaves a line or a point rather
    /// than a rectangle of negative size, and it is the corners that carry the rest of the shape, grown or
    /// shrunk along with the edges.
    /// </para>
    /// </remarks>
    private static void AddShadows(LayoutBox box, ComputedStyle style, List<PaintCommand> target, Boolean inset)
    {
        var shadows = style.BoxShadows;

        if (shadows.Count == 0)
        {
            return;
        }

        // An outer shadow has no effect on an internal table element of a table whose borders collapse
        // (§6.1.3) - and the table itself, which is not internal, still casts one.
        if (!inset && box.Kind is LayoutKind.TableCell or LayoutKind.TableRow or LayoutKind.TableRowGroup &&
            style.Table.Collapse)
        {
            return;
        }

        var area = inset ? box.PaddingBox : box.BorderBox;
        var radii = inset ? box.PaddingRadii : box.Radii;

        for (var index = shadows.Count - 1; index >= 0; index--)
        {
            var shadow = shadows[index];

            // A shadow with nothing to draw is not a shadow: the box it was cast from is transparent, and the
            // layer, the blur and the cut made for it would come to the same picture as not making them.
            if (shadow.Inset != inset || (shadow.Color & 0xFF00_0000) == 0)
            {
                continue;
            }

            // An inner shadow's spread works the other way: expanding the shadow is the same as contracting
            // the shape it is drawn from (§6.1).
            var distance = inset ? -shadow.Spread : shadow.Spread;
            var width = Math.Max(0, area.Width + distance * 2);
            var height = Math.Max(0, area.Height + distance * 2);
            var shape = new Rect(
                area.X - distance + shadow.OffsetX,
                area.Y - distance + shadow.OffsetY,
                width,
                height);

            // The area the command covers is the shape with as much blur around it as the blur can reach -
            // which is the answer a backend culls against, and one taken from the shape alone would cut a
            // soft edge short. An inner shadow never leaves the box it is clipped to, whatever its blur.
            var reach = shadow.Blur * 2;
            var bounds = inset
                ? area
                : new Rect(shape.X - reach, shape.Y - reach, width + reach * 2, height + reach * 2);

            target.Add(new BoxShadowCommand(
                bounds,
                area,
                radii,
                shape,
                radii.Spread(distance, width, height),
                shadow.Blur,
                shadow.Color,
                inset));
        }
    }

    /// <summary>
    /// Emits the outline of a box, a stroke drawn outside the border box that takes no part in the
    /// layout. Nothing is emitted for a box that was never given an outline to draw.
    /// </summary>
    private static void AddOutline(LayoutBox box, ComputedStyle style, List<PaintCommand> target)
    {
        var stroke = style.OutlineStyle;

        // Asked before the width, which is the more expensive of the two and which no box that was
        // never given an outline has anything to say about.
        if (stroke == BorderLineStyle.None)
        {
            return;
        }

        var width = style.OutlineWidth;

        if (width <= 0)
        {
            return;
        }

        target.Add(new OutlineCommand(
            box.BorderBox,
            box.Radii,
            stroke,
            width,
            style.OutlineOffset,
            ToArgb(style.OutlineColor)));
    }

    private void PaintBlock(LayoutBox box, PaintList list, List<PaintCommand> target, DeferredStack? deferred = null)
    {
        PaintPresentation(box, target);
        PaintMarker(box, target);
        PaintChildren(box, list, target, deferred);
    }

    /// <summary>
    /// Emits the ground a box paints for itself: its shadows, background, borders and gap rules, in the
    /// order a backend has to lay them down.
    /// </summary>
    /// <remarks>
    /// This is the part CSS 2.1 §Appendix E puts in a pass of its own, before the line the box's text
    /// stands on - which is why it is separated from what the box says rather than painted with it.
    /// </remarks>
    private void PaintPresentation(LayoutBox box, List<PaintCommand> target)
    {
        var style = box.Source.Style;

        // A generated anonymous box - the block stood between an inline run and a block sibling, or
        // wrapping the whole inline content of a block - is not an element and owns no presentation. It
        // borrows the container's computed values to lay its children out, but it must not paint them:
        // the container already painted its own background behind the whole content box. It draws only
        // what is inside it.
        if (box.Source.IsAnonymous || box.Source.IsContents)
        {
            // An anonymous table cell is the exception: the border it carries is not the container's
            // presentation borrowed but the half of a collapsed grid line the table settled on, and the
            // row around it has nothing to paint it with.
            if (box.Kind == LayoutKind.TableCell && !box.SuppressBorder)
            {
                AddBorder(box, style, target);
            }

            return;
        }

        // An empty cell under `empty-cells: hide` paints neither its background nor, in the separated
        // border model, its border. Every other box paints both.
        //
        // A shadow is presentation of the box as much as its background is, so it goes where the
        // specification puts it: the outer ones immediately below the background and the inner ones
        // immediately above it, still below the border (CSS Backgrounds 3 §6.1.3).
        AddShadows(box, style, target, inset: false);

        if (!box.SuppressBackground)
        {
            PaintBackground(box, style, target);
        }

        AddShadows(box, style, target, inset: true);

        if (!box.SuppressBorder)
        {
            AddBorder(box, style, target);
            AddBorderImage(box, style, target);
        }

        // A rule belongs to the gap rather than to the columns: it stands in the middle of it, reaches
        // from one content edge of the container to the other, and takes no room of its own (CSS Multicol
        // 3 §4). It is painted just above the container's border and below what the columns hold.
        // The rules a container's gaps ask for, where its own layout worked them out: a flex, grid or
        // grid-lanes container's gaps from its context, a multi-column one's from the columns it made.
        //
        // A box that hides its overflow hides the decorations too. Unlike the box's own border and
        // background, which stay where they are, a decoration belongs to the scrolled content: it travels
        // with the items and is cut at the padding edge, which is what a grid wider than its scrollport
        // asks for (grid-gap-decorations-035).
        var cutsRules = box.ClipsOverflow && box.GapDecorations is { Count: > 0 };

        if (cutsRules)
        {
            var edge = OverflowClipEdge(box);
            target.Add(new PushClipCommand(edge.Bounds, edge.Radii));
        }

        AddGapDecorations(box, style, target);

        if (cutsRules)
        {
            target.Add(new PopClipCommand(OverflowClipEdge(box).Bounds));
        }
    }

    /// <summary>
    /// Emits the mark a list item is drawn with, which is text of the item rather than ground: it stands
    /// with the content, so a line pulled over it by a negative indent reaches above it.
    /// </summary>
    private void PaintMarker(LayoutBox box, List<PaintCommand> target)
    {
        // A marker is text, drawn where the layout put it and in the font and colour the rules written for
        // it settled - the item's own where the sheet says nothing about markers, which is the usual page.
        // It stands beside the item rather than in it, so nothing about the box decides where it goes.
        // What it is painted under is the item's own content: the space it hangs in is outside the item's
        // border box, but content reaches it - a first line pulled back over it by a negative
        // <c>text-indent</c>, a block shifted out there by <c>position: relative</c> - and each of those
        // lies over the mark, which is what both outside-marker-paint-order and
        // outside-marker-covered-by-relpos-block-link are drawn to say.
        if (box.MarkerBounds is not { } marker || box.Source.Marker is not { Length: > 0 } marks)
        {
            return;
        }

        var marked = box.Source.MarkerStyle ?? box.Source.Style;

        target.Add(new TextCommand(
            marker,
            marks,
            box.MarkerBaseline,
            marked.GetFont(),
            marked.TextFillColor ?? ToArgb(marked.Color),
            box.Decorations ?? NoDecorations,
            box.Flow,
            marked.LetterSpacing,
            wordSpacing: marked.WordSpacing,
            skip: marked.SkipInk,
            shadows: marked.TextShadows));
    }

    /// <summary>
    /// Paints a box's children, clipped to the box when it hides its overflow, in the passes CSS 2.1
    /// §Appendix E gives a stacking context: the negative levels, the content that stayed in the flow, the
    /// automatic level in the order the document wrote it, then the positive levels lowest first.
    /// </summary>
    /// <remarks>
    /// The positioned descendants are gathered by a walk of their own before any of them is painted, because
    /// a level is answered by the context the box belongs to rather than by the box it happens to stand in -
    /// and a negative one belongs under content the painting walk would otherwise have reached already.
    /// Nothing gathered is a level to answer, and the plain walk paints in document order, which is then the
    /// paint order.
    /// </remarks>
    private void PaintChildren(LayoutBox box, PaintList list, List<PaintCommand> target, DeferredStack? deferred = null)
    {
        // The clip covers the children only: the box's own background and border were painted before
        // it, which is what keeps a border from being cut off by the radius it draws.
        var clipped = box.ClipsOverflow && box.Children.Count > 0;
        var edge = OverflowClipEdge(box);

        if (clipped)
        {
            target.Add(new PushClipCommand(edge.Bounds, edge.Radii));
        }

        if (deferred is not null)
        {
            // Walking the in-flow content on behalf of the context above it: every box taken out of the
            // flow was gathered by that context already and is painted from its own level there.
            PaintInFlow(box, list, target, deferred);
        }
        else
        {
            var stack = new DeferredStack();

            if (clipped)
            {
                stack.ActiveClips.Add(edge);

                if (box.Position != PositionMode.Static)
                {
                    stack.ChainClips.Add(edge);
                }
            }

            Collect(box, stack);

            if (clipped)
            {
                stack.ActiveClips.RemoveAt(stack.ActiveClips.Count - 1);

                if (box.Position != PositionMode.Static)
                {
                    stack.ChainClips.RemoveAt(stack.ChainClips.Count - 1);
                }
            }

            if (stack.IsEmpty)
            {
                PaintInFlow(box, list, target, null);
            }
            else
            {
                PaintStackedChildren(box, list, target, stack);
            }
        }

        if (clipped)
        {
            target.Add(new PopClipCommand(edge.Bounds));
        }
    }

    /// <summary>
    /// Gathers the positioned descendants whose level this context answers, in the order the document
    /// writes them, with the overflow clips and the legacy cuts that stood around each where it was found.
    /// </summary>
    /// <remarks>
    /// The walk goes down through any box that forms no context of its own - including a positioned box
    /// whose level is <c>auto</c>, which paints at level zero but is no barrier, so the levels written
    /// inside it belong to the context above (§Appendix E). It stops at a box that does form one, which
    /// answers its own descendants when it is painted.
    /// </remarks>
    private void Collect(LayoutBox box, DeferredStack stack)
    {
        foreach (var child in box.Children)
        {
            if (child.Position != PositionMode.Static || child.HasStackingLevel)
            {
                // A relatively positioned box is not taken out of the flow - its containing block is the
                // place it already stands - so every clip between it and the layer reaches it. One taken
                // right out is clipped along its containing block chain only: the boxes it merely stands in,
                // being static, hide nothing from it. A box fixed to the viewport is out of the document's
                // chain altogether. A flex or grid item that states a level stays in the flow too, so the
                // clips between it and its context reach it just as they reach a relatively placed box.
                // What a box carries is what stood around it where it was found - its own clip and its own
                // overflow belong to the painting of it, not to the list put back around it.
                var reached = child.Position switch
                {
                    PositionMode.Relative => CopiesOf(stack.ActiveClips),
                    PositionMode.Absolute => CopiesOf(stack.ChainClips),
                    _ => child.HasStackingLevel ? CopiesOf(stack.ActiveClips) : NoClips,
                };

                var waits = child.ZIndex < 0 ? stack.Below : child.ZIndex == 0 ? stack.Pending : stack.Above;

                waits.Add(new DeferredBox(child, reached, CutsHere()));
            }

            if (!child.EstablishesStackingContext)
            {
                var cut = CutOf(child);
                ClipEdge? hiding = child.ClipsOverflow ? OverflowClipEdge(child) : null;

                if (cut is { } standing)
                {
                    _cuts.Add(standing);
                }

                if (hiding is { } edge)
                {
                    stack.ActiveClips.Add(edge);

                    if (child.Position != PositionMode.Static)
                    {
                        stack.ChainClips.Add(edge);
                    }
                }

                Collect(child, stack);

                if (hiding is not null)
                {
                    stack.ActiveClips.RemoveAt(stack.ActiveClips.Count - 1);

                    if (child.Position != PositionMode.Static)
                    {
                        stack.ChainClips.RemoveAt(stack.ChainClips.Count - 1);
                    }
                }

                if (cut is not null)
                {
                    _cuts.RemoveAt(_cuts.Count - 1);
                }
            }
        }
    }

    /// <summary>
    /// Gets the clips standing around a box that stayed in the flow, or no clips where none do - the list
    /// a gathered box carries is only ever read, so an empty one needs no copy.
    /// </summary>
    private static List<ClipEdge> CopiesOf(List<ClipEdge> clips) => clips.Count > 0 ? [.. clips] : NoClips;

    /// <summary>
    /// Gets the edge a box with <c>overflow: clip</c> cuts its content to: the visual box
    /// <c>overflow-clip-margin</c> names, moved out by the distance it states, with the corners grown
    /// along with the edges (CSS Overflow 3 §3.3). Without the property this is the padding edge, which
    /// is what the clip has always been.
    /// </summary>
    private static ClipEdge OverflowClipEdge(LayoutBox box)
    {
        var style = box.Source.Style;
        var margin = style.ClipsWithMargin ? style.OverflowClipMargin : ClipMargin.None;

        var (area, radii) = margin.Box switch
        {
            ClipMarginBox.Content => (box.ContentBox, box.PaddingRadii.Shrink(box.Padding)),
            ClipMarginBox.Border => (box.BorderBox, box.Radii),
            _ => (box.PaddingBox, box.PaddingRadii),
        };

        if (margin.Length == 0)
        {
            return new ClipEdge(area, radii);
        }

        var width = Math.Max(0, area.Width + margin.Length * 2);
        var height = Math.Max(0, area.Height + margin.Length * 2);
        var grown = new Rect(area.X - margin.Length, area.Y - margin.Length, width, height);

        return new ClipEdge(grown, radii.Grow(margin.Length, width, height));
    }

    /// <summary>
    /// Paints the children that stay in the flow, in the three passes CSS 2.1 §Appendix E gives a
    /// stacking context: first the ground of every block-level box below this one, then the floats with
    /// everything they carry, then the lines the text stands on.
    /// </summary>
    /// <remarks>
    /// A box taken out of the flow is left out wherever a context has gathered it; with no gathering - the
    /// plain walk, and each box that forms a context of its own - every child is painted where it stands.
    /// <para>
    /// The passes are made over the boxes below this one rather than box by box because that is what makes
    /// a line stand over ground that was written after it: a page pulls a later block up with a negative
    /// margin, and the earlier box's text is still the ink on top of it. Painting each box completely as
    /// the walk reaches it buries that text instead.
    /// </para>
    /// </remarks>
    private void PaintInFlow(LayoutBox box, PaintList list, List<PaintCommand> target, DeferredStack? owner)
    {
        var children = Standing(box, owner);

        if (box.Kind == LayoutKind.Line)
        {
            // The line's own runs and the replaced boxes standing on it are the inline-level content, and
            // each is drawn complete where it stands: an atomic inline is a context of its own for what
            // is inside it.
            foreach (var child in children)
            {
                PaintChild(child, list, target, owner);
            }

            return;
        }

        var floated = new List<LayoutBox>();

        foreach (var child in children)
        {
            if (Floats(child))
            {
                floated.Add(child);
            }
        }

        // 1. The ground: every block's shadows, background, borders and gap rules, each box before the
        //    boxes inside it, and no line among them. A replaced box is drawn complete here, since its
        //    picture is its ground; what a context of its own holds is left to its own passes.
        foreach (var child in children)
        {
            if (Floats(child) || IsInlineLevel(child))
            {
                continue;
            }

            if (IsReplaced(child))
            {
                PaintChild(child, list, target, owner);
                continue;
            }

            if (PaintsAsAUnit(child))
            {
                // A box that forms a context of its own is drawn as one unit where it stands: what it
                // holds is laid down with its ground, under the layers of the boxes around it. A flex
                // or grid item is drawn that way too, even without a level of its own: it paints as an
                // inline-block would, so a sibling that comes after it covers the content it overflows
                // (CSS Flexbox 1 §5.4, CSS Grid 1 §9).
                PaintChild(child, list, target, owner);
                continue;
            }

            PaintGround(child, list, target, owner);
        }

        // 2. The floats, in the order the document wrote them, each with the whole of what it holds.
        foreach (var child in floated)
        {
            PaintChild(child, list, target, owner);
        }

        // 3. The ink: the lines this box's text stands on, and the same pass over the blocks below it, so
        //    a run deep in an early box still lies over the ground of a later one.
        foreach (var child in children)
        {
            if (Floats(child) || IsReplaced(child) || PaintsAsAUnit(child))
            {
                // A float came with the pass before, drawn with all that hangs on it; a replaced box and a
                // box that forms a context of its own came with the ground, each as one unit.
                continue;
            }

            if (IsInlineLevel(child))
            {
                PaintChild(child, list, target, owner);
                continue;
            }

            PaintInk(child, list, target, owner);
        }
    }

    /// <summary>
    /// Paints the ground of one block and of every block below it, clipped as its own overflow hides.
    /// </summary>
    private void PaintGround(LayoutBox box, PaintList list, List<PaintCommand> target, DeferredStack? owner)
    {
        var hidden = box.Kind != LayoutKind.Line && box.Source.Style.IsHidden;

        if (!hidden)
        {
            PaintPresentation(box, target);
        }

        // The clip covers what the box holds and never the box itself, which is what keeps a border from
        // being cut off by the radius it draws.
        var clipped = box.ClipsOverflow && box.Children.Count > 0;
        var edge = clipped ? OverflowClipEdge(box) : default;

        if (clipped)
        {
            target.Add(new PushClipCommand(edge.Bounds, edge.Radii));
        }

        foreach (var child in Standing(box, owner))
        {
            if (Floats(child) || IsInlineLevel(child))
            {
                continue;
            }

            if (IsReplaced(child))
            {
                PaintChild(child, list, target, owner);
                continue;
            }

            if (PaintsAsAUnit(child))
            {
                PaintChild(child, list, target, owner);
                continue;
            }

            PaintGround(child, list, target, owner);
        }

        if (clipped)
        {
            target.Add(new PopClipCommand(edge.Bounds));
        }
    }

    /// <summary>
    /// Paints what a block says - its mark, its lines and whatever stands on them - over the ground the
    /// pass before has already laid, and clipped by the same overflow that held its ground.
    /// </summary>
    private void PaintInk(LayoutBox box, PaintList list, List<PaintCommand> target, DeferredStack? owner)
    {
        var hidden = box.Kind != LayoutKind.Line && box.Source.Style.IsHidden;

        if (!hidden)
        {
            PaintMarker(box, target);
        }

        // The clip is only worth opening when this pass has ink to put inside it, and it covers that ink
        // alone: a mark hung outside the box is not cut by what the box hides.
        var clipped = box.ClipsOverflow && HasInk(box, owner);
        var edge = clipped ? OverflowClipEdge(box) : default;

        if (clipped)
        {
            target.Add(new PushClipCommand(edge.Bounds, edge.Radii));
        }

        foreach (var child in Standing(box, owner))
        {
            if (IsInlineLevel(child))
            {
                PaintChild(child, list, target, owner);
                continue;
            }

            if (Floats(child))
            {
                // A float this deep had no pass of its own above, since the passes reach only the children
                // of the box they belong to: it is drawn with all that hangs on it, here.
                PaintChild(child, list, target, owner);
                continue;
            }

            if (IsReplaced(child))
            {
                // Its picture came with its ground.
                continue;
            }

            if (PaintsAsAUnit(child))
            {
                // It came with the ground as one unit, layer and all.
                continue;
            }

            PaintInk(child, list, target, owner);
        }

        if (clipped)
        {
            target.Add(new PopClipCommand(edge.Bounds));
        }

        // The outline is drawn around the box rather than in it, so it comes after the clip that held the
        // box's own overflow: that clip must not cut it off, while the clip of the nearest ancestor that
        // hides its overflow still holds it in. A line box and a run of text are not elements of their
        // own, so neither grows one.
        if (box.Kind is not (LayoutKind.Line or LayoutKind.Text))
        {
            AddOutline(box, box.Source.Style, target);
        }
    }

    /// <summary>
    /// Gets the children of a box that the flow paints in place: one taken right out is left to the level
    /// that gathered it, and is nothing to any of the three passes.
    /// </summary>
    private static List<LayoutBox> Standing(LayoutBox box, DeferredStack? owner)
    {
        if (owner is null)
        {
            return box.Children;
        }

        var stays = new List<LayoutBox>(box.Children.Count);

        foreach (var child in box.Children)
        {
            // An item that states a level was gathered by the context above even though it stays in the
            // flow, so it is painted from that layer rather than by any of the flow's three passes.
            if (child.Position == PositionMode.Static && !child.HasStackingLevel)
            {
                stays.Add(child);
            }
        }

        return stays;
    }

    /// <summary>Gets whether a box stands on a line rather than as a block of its own.</summary>
    private static Boolean IsInlineLevel(LayoutBox box) =>
        box.Kind is LayoutKind.Line or LayoutKind.Inline or LayoutKind.Text;

    /// <summary>
    /// Gets whether a box is drawn complete where it stands, because what it holds belongs to a context of
    /// its own: a replaced box draws its own picture, and a box that forms a stacking context answers the
    /// levels inside itself rather than lending its ground to the pass above.
    /// </summary>
    private static Boolean IsAtomic(LayoutBox box) =>
        box.Kind is LayoutKind.Image or LayoutKind.Svg or LayoutKind.HorizontalRule
        || box.EstablishesStackingContext;

    private static Boolean Floats(LayoutBox box) =>
        !box.Source.IsItem && box.Source.Style.Float != FloatSide.None;

    /// <summary>
    /// Gets whether a box paints as one unit where it stands rather than lending its ground and its ink
    /// to the two passes around it: a box that forms a stacking context, and a flex or grid item, which
    /// paints the way an inline-block does even without a level of its own (CSS Flexbox 1 §5.4).
    /// </summary>
    private static Boolean PaintsAsAUnit(LayoutBox box) =>
        box.EstablishesStackingContext || (box.Source.IsItem && !IsInlineLevel(box));

    /// <summary>
    /// Gets whether a box is drawn by being replaced rather than by saying what is inside it - an image, a
    /// vector, a rule - which is why its picture comes with its ground rather than with the lines. A box
    /// that forms a stacking context of its own is atomic in the same way but is not replaced: what it
    /// holds is painted together with it wherever it stands, so neither pass reaches inside it on the
    /// terms of the context above.
    /// </summary>
    private static Boolean IsReplaced(LayoutBox box) =>
        box.Kind is LayoutKind.Image or LayoutKind.Svg or LayoutKind.HorizontalRule;

    /// <summary>
    /// Gets whether the ink pass has anything to lay inside a box: a mark of its own, a line, a replaced
    /// or atomic box, a float nested below, or the same answer further down.
    /// </summary>
    private static Boolean HasInk(LayoutBox box, DeferredStack? owner)
    {
        if (box.MarkerBounds is not null && box.Source.Marker is { Length: > 0 })
        {
            return true;
        }

        foreach (var child in Standing(box, owner))
        {
            if (IsInlineLevel(child) || Floats(child))
            {
                return true;
            }

            if (HasInk(child, owner))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Paints one context's four passes over the levels its gather walk found.
    /// </summary>
    private void PaintStackedChildren(LayoutBox box, PaintList list, List<PaintCommand> target, DeferredStack stack)
    {
        // 1. The negative levels, in the order the document wrote them. These reach under the content of
        //    the boxes they stand in, however deep, which is why they are gathered before any painting.
        foreach (var entry in stack.Below)
        {
            PaintDeferred(entry, list, target, stack);
        }

        // 2. The content that stayed in the flow, the box's own background and border having been painted
        //    by the caller before it asked for any of this.
        PaintInFlow(box, list, target, stack);

        // 3. The automatic level - `auto` and `0` paint as one - in document order. Depth is not that
        //    order, so the layer is emitted in the order the gather walk met the boxes, and a relatively
        //    positioned box met deeper carries the overflow clips of the boxes it stands in, which are put
        //    back around it here: the layer does not lift it out of a clip that reaches it.
        foreach (var entry in stack.Pending)
        {
            PaintDeferred(entry, list, target, stack);
        }

        // 4. The positive levels, lowest first. `OrderBy` is stable and the list came out of one walk in
        //    document order, so equal levels keep that order.
        foreach (var entry in stack.Above.OrderBy(entry => entry.Box.ZIndex))
        {
            PaintDeferred(entry, list, target, stack);
        }
    }

    /// <summary>
    /// Paints one box its context gathered, with the overflow clips that still reach it put back around it,
    /// the cuts it was found inside put back around what it lifts into a layer, and the context handed down
    /// so that the boxes it holds are left to their own levels rather than painted twice.
    /// </summary>
    private void PaintDeferred(DeferredBox entry, PaintList list, List<PaintCommand> target, DeferredStack stack)
    {
        foreach (var clip in entry.Clips)
        {
            target.Add(new PushClipCommand(clip.Bounds, clip.Radii));
        }

        // The walk has moved on since this box was found: the cuts of that place travel with it, and this
        // pass is painted outside the pairs they were laid in, so they are laid again here.
        var where = _cuts;
        var depth = _cutDepth;
        _cuts = entry.Cuts;
        _cutDepth = 0;

        PaintChild(entry.Box, list, target, entry.Box.EstablishesStackingContext ? null : stack);

        _cuts = where;
        _cutDepth = depth;

        for (var i = entry.Clips.Count - 1; i >= 0; i--)
        {
            target.Add(new PopClipCommand(entry.Clips[i].Bounds));
        }
    }

    /// <summary>
    /// Keeps the cuts standing around the place a box was found, for the layer that is to paint it after
    /// the walk has climbed out of them.
    /// </summary>
    private List<Rect> CutsHere() => _cuts.Count > 0 ? [.. _cuts] : NoCuts;

    /// <summary>
    /// Paints a box that is lifted into a layer of its own - the viewport's, or a pinned one - inside the
    /// cuts standing around it. Those are commands of the layer rather than of the walk, because the list
    /// they belong to is not the one the walk is filling.
    /// </summary>
    private void PaintLifted(LayoutBox box, PaintList list, List<PaintCommand> target)
    {
        // The layer starts with nothing laid down, so every cut standing around the box goes into it.
        var outside = _cutDepth;
        _cutDepth = 0;

        PaintBox(box, list, target);

        _cutDepth = outside;
    }

    /// <summary>
    /// Paints one child into the layer its position asks for: a fixed box goes to the viewport layer,
    /// a sticky box to a pinned layer of its own, and everything else into the current one.
    /// </summary>
    private void PaintChild(LayoutBox child, PaintList list, List<PaintCommand> target, DeferredStack? deferred = null)
    {
        switch (child.Position)
        {
            case PositionMode.Fixed:
                PaintLifted(child, list, list.FixedCommands);
                break;

            case PositionMode.Sticky when child.Sticky is { } constraint:
                var layer = new StickyLayer(constraint);
                PaintLifted(child, list, layer.Commands);
                list.Sticky.Add(layer);
                break;

            default:
                // A box that forms a stacking context answers the levels inside itself, so the walk that
                // carries boxes up to a layer stops here; a box that forms none - a positioned box whose
                // level is `auto` - carries on, because its descendants belong to the context above.
                PaintBox(child, list, target, child.EstablishesStackingContext ? null : deferred);
                break;
        }
    }

    /// <summary>
    /// Paints the background colour and then the layers, both across the box the background is clipped
    /// to - the border box by default, so that the background reaches under a transparent border. CSS
    /// paints the first declared layer on top, so the layers are emitted last-first and the colour
    /// always ends up underneath all of them.
    /// </summary>
    private void PaintBackground(LayoutBox box, ComputedStyle style, List<PaintCommand> target)
    {
        // The box whose background the canvas wears paints none of its own: §2.11 hands that background to
        // the surface, and the chapter says so twice - the root "does not paint this background again", and
        // the used values of a body that handed its over are the initial ones.
        if (ReferenceEquals(box.Source, _canvas))
        {
            return;
        }

        // The radii follow the area: the border box rounds off along the outer curve, the padding box
        // along the one the border leaves behind, and the content box along the one the padding adds.
        var area = box.BorderBox;
        var radii = box.Radii;

        switch (box.BackgroundClip)
        {
            case BackgroundClip.PaddingBox:
                area = box.PaddingBox;
                radii = box.PaddingRadii;
                break;

            case BackgroundClip.ContentBox:
                area = box.ContentBox;
                radii = box.PaddingRadii.Shrink(box.Padding);
                break;
        }

        // A piece of a box broken across columns paints the background the whole box would have had, cut
        // with the piece: the layers are placed against the box it came from, not against the fragment,
        // which is what keeps one gradient running down both pieces (CSS Break 3 §4.7).
        var placement = box.BackgroundClip == BackgroundClip.BorderBox && box.BackgroundOrigin is { } whole
            ? whole
            : area;

        // The colour first and the layers last-first, so the first declared layer ends on top, as CSS
        // paints them. They are gathered rather than sent straight out because a text clip wraps the
        // whole set in one masked command.
        var layers = new List<PaintCommand>();

        if (style.BackgroundColor is { } background && background.A != 0)
        {
            layers.Add(new FillCommand(area, ToArgb(background), radii));
        }

        var declared = style.Backgrounds;

        for (var index = declared.Count - 1; index >= 0; index--)
        {
            var layer = declared[index];

            if (layer.Gradient is { } gradient)
            {
                var (tile, repeatX, repeatY) = GradientTile(layer, placement);
                var length = gradient.LineLength(tile.Width, tile.Height);
                var (radiusX, radiusY) = gradient.EndingRadii(tile.Width, tile.Height);

                layers.Add(new GradientCommand(
                    area,
                    tile,
                    repeatX,
                    repeatY,
                    gradient.Shape,
                    gradient.AngleDegrees,
                    tile.X + gradient.CenterX.Resolve(tile.Width),
                    tile.Y + gradient.CenterY.Resolve(tile.Height),
                    gradient.StopsAt(length, RampPixels(gradient, tile, length)),
                    radii,
                    radiusX,
                    radiusY));
            }
            else if (layer.Url is { Length: > 0 })
            {
                layers.Add(new BackgroundImageCommand(area, placement, layer, radii));
            }
        }

        if (layers.Count == 0)
        {
            return;
        }

        // Clipped to the text, the background survives only where a glyph covers it - and where none does
        // there is nothing to paint, so a text-clipped box with no words in it is simply blank.
        if (box.BackgroundClip == BackgroundClip.Text)
        {
            var mask = CollectTextRuns(box);

            if (mask.Count > 0)
            {
                target.Add(new TextClippedBackgroundCommand(area, layers, mask));
            }

            return;
        }

        target.AddRange(layers);
    }

    /// <summary>
    /// Settles the rectangle a gradient layer fills: the size it states, or the area where an axis is
    /// left to itself, and placed where its position puts it (CSS Backgrounds 3 §2.4 and §2.6).
    /// </summary>
    /// <remarks>
    /// A gradient has no intrinsic size and no intrinsic ratio, so the default object size every
    /// <c>auto</c> falls back to is the positioning area itself, and <c>cover</c> and <c>contain</c> -
    /// which are the two ways of scaling an image to the area - leave it exactly that size too (CSS
    /// Images 3 §4.1.3). A component of zero is how the reader marks an axis it never resolved, which
    /// is the same convention the image layers keep between them.
    /// </remarks>
    private static (Rect Tile, Boolean RepeatX, Boolean RepeatY) GradientTile(BackgroundLayer layer, Rect area)
    {
        var stated = layer.SizeKind == BackgroundLayerSizeKind.Explicit;
        var width = stated && layer.SizeWidth.IsStated ? layer.SizeWidth.Resolve(area.Width) : area.Width;
        var height = stated && layer.SizeHeight.IsStated ? layer.SizeHeight.Resolve(area.Height) : area.Height;

        return (
            new Rect(
                area.X + layer.PositionX.Resolve(area.Width - width),
                area.Y + layer.PositionY.Resolve(area.Height - height),
                width,
                height),
            layer.RepeatX,
            layer.RepeatY);
    }

    /// <summary>
    /// Gets how far the ramp of a gradient is stretched in what the box is drawn with, which is how finely
    /// a mix in another colour space has to be told before its steps show. A line and a radius are measured
    /// along themselves; a turn is measured by the widest circle the box holds, whose radius the diagonal
    /// stands for wherever the centre was put.
    /// </summary>
    private static Double RampPixels(GradientSpec gradient, Rect tile, Double length) =>
        gradient.Shape != GradientShape.Conic
            ? length
            : 2 * Math.PI * Math.Sqrt(tile.Width * tile.Width + tile.Height * tile.Height);

    /// <summary>
    /// Gathers the runs of text under a box, in the flow each was set, as the shapes a text-clipped
    /// background is kept to. Only the glyph coverage matters to the mask, so the decorations and colour
    /// are left off; the width each run is stroked with is kept, for a stroke widens the ink it is clipped
    /// to as surely as it would paint one.
    /// </summary>
    private static IReadOnlyList<TextCommand> CollectTextRuns(LayoutBox box)
    {
        var runs = new List<TextCommand>();
        GatherTextRuns(box, runs);
        return runs;
    }

    private static void GatherTextRuns(LayoutBox node, List<TextCommand> runs)
    {
        if (!String.IsNullOrEmpty(node.Text))
        {
            var style = node.Source.Style;

            runs.Add(new TextCommand(
                node.BorderBox,
                node.Text,
                node.Baseline,
                node.Font,
                0xFF00_0000,
                node.Decorations ?? NoDecorations,
                node.Flow,
                style.LetterSpacing,
                style.TextStrokeWidth,
                0,
                reversed: node.Reversed));
        }

        foreach (var child in node.Children)
        {
            GatherTextRuns(child, runs);
        }
    }

    /// <summary>
    /// Emits the image, clipped to its content box. The box is the content box because that is where
    /// a replaced element's content goes; for the common case of an image with no padding or border
    /// it is the same rectangle as the border box, and so are the radii.
    /// </summary>
    private static void PaintImage(LayoutBox box, List<PaintCommand> target)
    {
        if (String.IsNullOrEmpty(box.ImageUrl))
        {
            return;
        }

        target.Add(new ImageCommand(
            box.ContentBox,
            box.ImageUrl,
            box.ImageFit,
            box.PaddingRadii.Shrink(box.Padding)));
    }

    /// <summary>
    /// Emits an inline SVG's markup into its content box, the way an image is placed there - clipped to
    /// the same rounded rectangle so a bordered or padded SVG keeps its shape.
    /// </summary>
    private static void PaintSvg(LayoutBox box, List<PaintCommand> target)
    {
        if (String.IsNullOrEmpty(box.SvgMarkup))
        {
            return;
        }

        target.Add(new SvgCommand(
            box.ContentBox,
            box.SvgMarkup!,
            box.PaddingRadii.Shrink(box.Padding)));
    }

    private void PaintText(LayoutBox box, List<PaintCommand> target)
    {
        if (String.IsNullOrEmpty(box.Text))
        {
            return;
        }

        // The highlight is added before the run it covers, which is what puts it over the background
        // and under the glyphs - the only order that leaves the text readable.
        if (Highlight?.TryGetValue(box, out var highlight) == true)
        {
            target.Add(new FillCommand(highlight, HighlightColor, CornerRadii.None));
        }

        var style = box.Source.Style;
        var line = box.FirstLinePaint;

        if (line is { Background: true } band && band.Style.BackgroundColor is { } background &&
            background.A != 0)
        {
            // The band is the fragment's own box, which is what makes a first line's background the union
            // of the pieces standing on it - and why the mark an item wears inside is covered with its text.
            // A background the run's own style carries is not painted here: that belongs to the inline
            // element box, which paints its fragments where they stand.
            target.Add(new FillCommand(box.BorderBox, ToArgb(background), CornerRadii.None));
        }

        // The first line is asked for what it wrote, and for nothing else: it is a box around the line, so
        // a property it never mentions leaves the text with the value it came to. A spacing or a font size
        // written on the line is not asked for at all, because the breaks were settled with the block's own
        // font and the layout cannot yet be asked to break a line twice.
        var painted = line is { Color: true } colour ? colour.Style : style;
        var stroke = line is { Stroke: true } ring ? ring.Style : style;

        // The fill colour stands in for the text colour where the page set one; the stroke travels so the
        // back end rings each glyph with it, and a background clipped to the text can widen to it.
        var fill = (line is { Fill: true } ? line.Value.Style.TextFillColor : style.TextFillColor)
            ?? ToArgb(painted.Color);

        target.Add(new TextCommand(
            box.BorderBox,
            box.Text,
            box.Baseline,
            box.Font,
            fill,
            box.Decorations ?? NoDecorations,
            box.Flow,
            style.LetterSpacing,
            stroke.TextStrokeWidth,
            stroke.TextStrokeColor,
            box.Justification,
            box.Justifies,
            style.TextJustify,
            style.WordSpacing,
            box.DecorationPlacements,
            style.SkipInk,
            (line is { Shadow: true } cast ? cast.Style : style).TextShadows,
            box.Reversed));

        // The marks the run asked for stand over the text they mark. They carry no lines of their own - a
        // mark is not underlined because the word under it is - and they follow the run, which is where the
        // ink of the marks falls in the order the text is drawn in.
        if (box.Marks is { Count: > 0 } marks)
        {
            foreach (var mark in marks)
            {
                target.Add(new TextCommand(
                    mark.Band,
                    mark.Text,
                    mark.Baseline,
                    mark.Font,
                    mark.Color,
                    NoDecorations,
                    box.Flow,
                    style.LetterSpacing,
                    stroke.TextStrokeWidth,
                    stroke.TextStrokeColor,
                    0,
                    0,
                    style.TextJustify,
                    0,
                    null,
                    style.SkipInk,
                    (line is { Shadow: true } shadow ? shadow.Style : style).TextShadows));
            }
        }
    }

    /// <summary>
    /// Draws the rule each column gap is asked to carry: a band of the rule's width centred in the gap,
    /// reaching from one content edge of the container to the other, and only between two columns that
    /// both have content (CSS Multicol 3 §4).
    /// </summary>
    /// <remarks>
    /// The band is drawn as one side of a box that is nothing but that side, which is what gives a rule
    /// the same strokes a border takes - a dashed one dashes along its length, a double one is two lines
    /// within its width - without the painter needing a second set of them. Which columns hold something
    /// is read from where the fragments were put, so a column the content never reached leaves no rule
    /// beside it, and a rule is only drawn where two occupied columns are neighbours in the first place.
    /// </remarks>
    /// <summary>
    /// Paints the decorations a container's layout worked out for its gaps (CSS Gaps 1 §3).
    /// </summary>
    private static void AddGapDecorations(LayoutBox box, ComputedStyle style, List<PaintCommand> target)
    {
        if (box.GapDecorations is not { Count: > 0 } segments)
        {
            return;
        }

        var vertical = box.Flow.IsVertical;
        var columnOverRow = style.Gap.ColumnOverRow;

        // Two passes, so the axis the page asked to have on top is laid down last and its decorations
        // cover the others where a row gap and a column gap cross.
        for (var pass = 0; pass < 2; pass++)
        {
            var wantsRow = pass == 0 ? columnOverRow : !columnOverRow;

            foreach (var segment in segments)
            {
                if (segment.IsRow == wantsRow)
                {
                    // A row gap separates along the block axis, so its rule is thin along that axis:
                    // across the page in a horizontal flow, down it in a vertical one.
                    AddRuleBand(
                        target,
                        segment.Band,
                        segment.Rule,
                        thinAlongY: segment.IsRow != vertical,
                        segment.GapWidth);
                }
            }
        }
    }

    /// <summary>
    /// Draws a rule as the side of a box that is nothing but that side: the bottom edge for a rule that
    /// runs across the page, the left edge for one that runs down it. Turning the band into a border side
    /// is what gives a dashed rule its dashes and a double rule its two lines.
    /// </summary>
    private static void AddRuleBand(
        List<PaintCommand> target,
        Rect band,
        GapRule rule,
        Boolean thinAlongY,
        Double gapWidth)
    {
        var width = rule.Width;

        // A decoration sits in the gap, seen from the reader's side of it: an `inset` rule reads as a
        // ridge and an `outset` one as a groove, the shade on the near half being the light one either
        // way (CSS Gaps 1 §3.3).
        var stroke = rule.Style switch
        {
            BorderLineStyle.Inset => BorderLineStyle.Ridge,
            BorderLineStyle.Outset => BorderLineStyle.Groove,
            _ => rule.Style,
        };

        // A column rule that fills its gap is drawn as the far edge of the band, so the dashes of a
        // dotted or dashed stripe start where the gap ends; one narrower than its gap is drawn from
        // the near edge, which is the side the stripe is seen from. A row rule runs across the page
        // and keeps the bottom edge either way.
        var fillsGap = !thinAlongY && gapWidth > 0 && width >= gapWidth;

        target.Add(new BorderCommand(
            band,
            thinAlongY
                ? new Thickness(0, 0, width, 0)
                : fillsGap
                    ? new Thickness(0, width, 0, 0)
                    : new Thickness(0, 0, 0, width),
            thinAlongY
                ? new BorderStyles(BorderLineStyle.None, BorderLineStyle.None, stroke, BorderLineStyle.None)
                : fillsGap
                    ? new BorderStyles(BorderLineStyle.None, stroke, BorderLineStyle.None, BorderLineStyle.None)
                    : new BorderStyles(BorderLineStyle.None, BorderLineStyle.None, BorderLineStyle.None, stroke),
            default,
            rule.Color,
            rule.Color,
            rule.Color,
            rule.Color));
    }

    /// <summary>
    /// Draws the picture a box paints its border area with. A border image stands where the border stands
    /// and replaces it (CSS Backgrounds 3 §6.1): the area is the border box grown by the outsets, so the
    /// picture may reach past what the box took in the layout, and the corners of the box's own radii cut
    /// it as they cut everything else the box paints.
    /// </summary>
    private static void AddBorderImage(LayoutBox box, ComputedStyle style, List<PaintCommand> target)
    {
        if (style.BorderImage is not { } image)
        {
            return;
        }

        var area = image.Area(box.BorderBox, box.Border);
        var sides = image.DrawnWidths(area, box.Border);
        var gradient = image.Gradient is { } picture ? GradientOf(picture, area) : null;

        target.Add(new BorderImageCommand(
            area,
            image,
            new Thickness(sides[0], sides[1], sides[2], sides[3]),
            gradient));
    }

    /// <summary>
    /// Settles a gradient picture over the border image area it is drawn into, on the same terms a
    /// background gradient is settled over its tile: the area is the image's whole size, so its line and
    /// its shape are asked of the area (CSS Images 3 §3.4).
    /// </summary>
    private static GradientCommand GradientOf(GradientSpec gradient, Rect area)
    {
        var length = gradient.LineLength(area.Width, area.Height);
        var (radiusX, radiusY) = gradient.EndingRadii(area.Width, area.Height);

        return new GradientCommand(
            area,
            area,
            false,
            false,
            gradient.Shape,
            gradient.AngleDegrees,
            area.X + gradient.CenterX.Resolve(area.Width),
            area.Y + gradient.CenterY.Resolve(area.Height),
            gradient.StopsAt(length, RampPixels(gradient, area, length)),
            CornerRadii.None,
            radiusX,
            radiusY);
    }

    private static void AddBorder(LayoutBox box, ComputedStyle style, List<PaintCommand> target)
    {        var border = box.Border;

        if (border.Horizontal <= 0 && border.Vertical <= 0)
        {
            return;
        }

        // A collapsed table edge takes its colour from whichever neighbouring border won, which is not
        // necessarily the box's own, so the resolution is carried on the box when it applies.
        var colors = box.BorderColors;

        target.Add(new BorderCommand(
            box.BorderBox,
            border,
            box.BorderStyles ?? style.BorderStyles,
            box.Radii,
            colors?.Top ?? ToArgb(style.BorderTopColor ?? style.Color),
            colors?.Right ?? ToArgb(style.BorderRightColor ?? style.Color),
            colors?.Bottom ?? ToArgb(style.BorderBottomColor ?? style.Color),
            colors?.Left ?? ToArgb(style.BorderLeftColor ?? style.Color)));
    }

    /// <summary>The lines a run draws when nothing around it asked for any.</summary>
    private static readonly IReadOnlyList<DecorationSource> NoDecorations = Array.Empty<DecorationSource>();

    private static UInt32 ToArgb(CssColorValue color) =>
        (UInt32)((color.A << 24) | (color.R << 16) | (color.G << 8) | color.B);
}

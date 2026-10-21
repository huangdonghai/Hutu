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

        if (donor is { } box)
        {
            PaintCanvas(box, layout.Root, list.Bounds, list.Commands);
        }

        PaintBox(layout.Root, list, list.Commands);

        _canvas = null;
        return list;
    }

    /// <summary>The box whose background the canvas wears, and which therefore keeps none of its own.</summary>
    private Box? _canvas;

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
                var length = LinearGradientSpec.LineLength(tile.Width, tile.Height, gradient.AngleDegrees);

                layers.Add(new LinearGradientCommand(
                    canvas, tile, repeatX, repeatY, gradient.AngleDegrees, gradient.StopsAt(length), CornerRadii.None));
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
    /// layer, which the layer must not lift it out of.</summary>
    private sealed class DeferredBox(LayoutBox box, List<ClipEdge> clips)
    {
        public LayoutBox Box { get; } = box;

        public List<ClipEdge> Clips { get; } = clips;
    }

    /// <summary>Collects the positioned boxes a stacking context paints in its layer, and the overflow
    /// clips standing along the walk that reaches each of them.</summary>
    private sealed class DeferredStack
    {
        /// <summary>Gets the boxes waiting for the layer, in the order they were written.</summary>
        public List<DeferredBox> Pending { get; } = [];

        /// <summary>Gets the clips of the boxes climbed through on the way down that hide their
        /// overflow, outermost first.</summary>
        public List<ClipEdge> ActiveClips { get; } = [];
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
        if (box.Transform is { } transform)
        {
            target.Add(new PushTransformCommand(box.BorderBox, transform, box.TransformOrigin));
            PaintBoxContent(box, list, target, deferred: null);
            target.Add(new PopTransformCommand(box.BorderBox));
            return;
        }

        PaintBoxContent(box, list, target, deferred);
    }

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
        var area = box.Position is PositionMode.Absolute or PositionMode.Fixed
            ? box.Source.Style.Clip?.ClipOf(box.BorderBox)
            : null;

        if (area is { } clip)
        {
            target.Add(new PushClipCommand(clip, CornerRadii.None));

            // A descendant lifted into a stacking context above is painted from that layer, so it carries
            // this clip with it the same way it carries an overflow clip - and no more, once this box has
            // been cut, which is what the removal below gives back.
            deferred?.ActiveClips.Add(new ClipEdge(clip, CornerRadii.None));
        }

        PaintBoxPresentation(box, list, target, deferred);

        if (area is { } cut)
        {
            target.Add(new PopClipCommand(cut));
            deferred?.ActiveClips.RemoveAt(deferred.ActiveClips.Count - 1);
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

        switch (box.Kind)
        {
            case LayoutKind.Text:
                PaintText(box, target);
                break;

            case LayoutKind.HorizontalRule:
                AddShadows(box, box.Source.Style, target, inset: false);
                AddShadows(box, box.Source.Style, target, inset: true);
                AddBorder(box, box.Source.Style, target);
                break;

            case LayoutKind.Image:
                // An image box can carry a background and a border of its own, which paint under the
                // image itself.
                AddShadows(box, box.Source.Style, target, inset: false);
                PaintBackground(box, box.Source.Style, target);
                AddShadows(box, box.Source.Style, target, inset: true);
                AddBorder(box, box.Source.Style, target);
                PaintImage(box, target);
                break;

            case LayoutKind.Svg:
                // An inline SVG is a replaced box like an image: its own background and border paint
                // under the vector content, which is drawn from the markup the box carries.
                AddShadows(box, box.Source.Style, target, inset: false);
                PaintBackground(box, box.Source.Style, target);
                AddShadows(box, box.Source.Style, target, inset: true);
                AddBorder(box, box.Source.Style, target);
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
                // A line box has no decorations of its own; only its runs are painted. A box taken out
                // of the flow that stands on the line - the place it would have had, which is where its
                // static position comes from - is left for the stacking context above, exactly as one
                // standing among blocks is: it is here only to say where it belongs.
                foreach (var child in box.Children)
                {
                    if (deferred is not null && child.Position == PositionMode.Absolute && child.ZIndex == 0)
                    {
                        deferred.Pending.Add(new DeferredBox(child, NoClips));
                        continue;
                    }

                    // Routed through PaintChild, not PaintBox, so a fixed child lands in the viewport
                    // layer rather than the line's own commands: a fixed box is clipped only by an
                    // ancestor that is its containing block, never by a plain overflow:hidden wrapper
                    // that merely holds its static position (CSS 2.1 11.1.1). Painting it inline here
                    // is what cut a fixed sidebar down to the height of such a wrapper.
                    PaintChild(child, list, target, deferred);
                }

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
        var style = box.Source.Style;

        // A generated anonymous box - the block stood between an inline run and a block sibling, or
        // wrapping the whole inline content of a block - is not an element and owns no presentation. It
        // borrows the container's computed values to lay its children out, but it must not paint them:
        // the container already painted its own background behind the whole content box, so the
        // anonymous block painting that same background again only risks covering a float that stands
        // beside the run it wraps (children paint in document order, so the later anonymous block would
        // bury the earlier float). It draws only what is inside it.
        if (box.Source.IsAnonymous)
        {
            // An anonymous table cell is the exception: the border it carries is not the container's
            // presentation borrowed but the half of a collapsed grid line the table settled on, and the
            // row around it has nothing to paint it with.
            if (box.Kind == LayoutKind.TableCell && !box.SuppressBorder)
            {
                AddBorder(box, style, target);
            }

            PaintChildren(box, list, target, deferred);
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
        }

        // A rule belongs to the gap rather than to the columns: it stands in the middle of it, reaches
        // from one content edge of the container to the other, and takes no room of its own (CSS Multicol
        // 3 §4). It is painted just above the container's border and below what the columns hold.
        // The rules a container's gaps ask for, where its own layout worked them out: a flex, grid or
        // grid-lanes container's gaps from its context, a multi-column one's from the columns it made.
        AddGapDecorations(box, style, target);

        // A marker is text, drawn where the layout put it and in the font and colour the rules written for
        // it settled - the item's own where the sheet says nothing about markers, which is the usual page.
        // It stands beside the item rather than in it, so nothing about the box decides where it goes.
        // What it is painted under is the item's own content: the space it hangs in is outside the item's
        // border box, but content reaches it - a first line pulled back over it by a negative
        // <c>text-indent</c>, a block shifted out there by <c>position: relative</c> - and each of those
        // lies over the mark, which is what both outside-marker-paint-order and
        // outside-marker-covered-by-relpos-block-link are drawn to say.
        if (box.MarkerBounds is { } marker && box.Source.Marker is { Length: > 0 } marks)
        {
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

        PaintChildren(box, list, target, deferred);
    }

    /// <summary>
    /// Paints a box's children in stacking order, clipped to the box when it hides its overflow. With
    /// no positioned child the document order is already the paint order, so the fast path avoids the
    /// four passes the layering needs.
    /// </summary>
    private void PaintChildren(LayoutBox box, PaintList list, List<PaintCommand> target, DeferredStack? deferred = null)
    {
        // The clip covers the children only: the box's own background and border were painted before
        // it, which is what keeps a border from being cut off by the radius it draws.
        var clipped = box.ClipsOverflow && box.Children.Count > 0;

        if (clipped)
        {
            target.Add(new PushClipCommand(box.PaddingBox, box.PaddingRadii));

            // While climbing on behalf of a stacking context above, the children are about to be lifted
            // off this clip into that context's layer. The clip is noted, so that a descendant it still
            // reaches - one whose containing block sits inside this box - gets it back when it is painted.
            deferred?.ActiveClips.Add(new ClipEdge(box.PaddingBox, box.PaddingRadii));
        }

        if (deferred is not null)
        {
            // Walking the in-flow content of a box on behalf of the stacking context above it. A box
            // taken out of the flow (absolute or fixed) belongs to that context's positioned layer,
            // however deep it stands in here, so it is left for the caller rather than painted where it
            // happens to sit - which is how a box that should be covered ends up on top. Its containing
            // block may sit outside this box, so it is left outside the clip too: a clip reaches only
            // the descendants whose containing block is inside the box that clips.
            //
            // A relatively positioned box is not taken out of the flow - its containing block is the
            // place it already stands - so every clip between it and the layer reaches it. It is left
            // carrying those clips, which are put back around it when the layer is painted.
            foreach (var child in box.Children)
            {
                if (child.Position == PositionMode.Static)
                {
                    PaintChild(child, list, target, deferred);
                    continue;
                }

                var clips = child.Position == PositionMode.Relative
                    ? new List<ClipEdge>(deferred.ActiveClips)
                    : NoClips;

                deferred.Pending.Add(new DeferredBox(child, clips));
            }
        }
        else if (HasPositionedChild(box))
        {
            PaintStackedChildren(box, list, target);
        }
        else
        {
            foreach (var child in box.Children)
            {
                PaintChild(child, list, target);
            }
        }

        if (clipped)
        {
            target.Add(new PopClipCommand(box.PaddingBox));
            deferred?.ActiveClips.RemoveAt(deferred.ActiveClips.Count - 1);
        }
    }

    private void PaintStackedChildren(LayoutBox box, PaintList list, List<PaintCommand> target)
    {
        // 1. A negative z-index paints below the in-flow content.
        foreach (var child in box.Children)
        {
            if (child.Position != PositionMode.Static && child.ZIndex < 0)
            {
                PaintChild(child, list, target);
            }
        }

        // 2. The in-flow, non-positioned content. A box taken out of the flow with an automatic level is
        //    not part of it, however deep it stands: it belongs to this stacking context's positioned
        //    layer, and painting it here would put it beneath a positioned sibling that comes later in
        //    the document - which is how a box that should be covered ends up on top.
        var deferred = new DeferredStack();

        foreach (var child in box.Children)
        {
            if (child.Position != PositionMode.Static)
            {
                // Its own layer is one of the other three passes; only an automatic level waits here.
                if (child.ZIndex == 0)
                {
                    deferred.Pending.Add(new DeferredBox(child, NoClips));
                }

                continue;
            }

            PaintChild(child, list, target, deferred);
        }

        // 3. Positioned boxes with `z-index: auto` or `0`, in document order: a box this box holds
        //    itself, then the ones the walk above met deeper in the tree. Depth is not the order - the
        //    document is - so the layer is emitted in the order the boxes were written. A relatively
        //    positioned box met deeper carries the overflow clips of the boxes it stands in, which are
        //    put back around it here: the layer does not lift it out of a clip that reaches it.
        foreach (var entry in deferred.Pending)
        {
            foreach (var clip in entry.Clips)
            {
                target.Add(new PushClipCommand(clip.Bounds, clip.Radii));
            }

            PaintChild(entry.Box, list, target);

            for (var i = entry.Clips.Count - 1; i >= 0; i--)
            {
                target.Add(new PopClipCommand(entry.Clips[i].Bounds));
            }
        }

        // 4. The positive z-index layer, lowest first. `OrderBy` is stable, so equal levels keep
        // their document order.
        foreach (var child in box.Children.Where(child => child.Position != PositionMode.Static && child.ZIndex > 0).OrderBy(child => child.ZIndex))
        {
            PaintChild(child, list, target);
        }
    }

    private static Boolean HasPositionedChild(LayoutBox box)
    {
        foreach (var child in box.Children)
        {
            if (child.Position != PositionMode.Static)
            {
                return true;
            }
        }

        return false;
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
                PaintBox(child, list, list.FixedCommands);
                break;

            case PositionMode.Sticky when child.Sticky is { } constraint:
                var layer = new StickyLayer(constraint);
                PaintBox(child, list, layer.Commands);
                list.Sticky.Add(layer);
                break;

            default:
                PaintBox(child, list, target, deferred);
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
                var (tile, repeatX, repeatY) = GradientTile(layer, area);
                var length = LinearGradientSpec.LineLength(tile.Width, tile.Height, gradient.AngleDegrees);

                layers.Add(new LinearGradientCommand(
                    area, tile, repeatX, repeatY, gradient.AngleDegrees, gradient.StopsAt(length), radii));
            }
            else if (layer.Url is { Length: > 0 })
            {
                layers.Add(new BackgroundImageCommand(area, area, layer, radii));
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
        var width = stated && layer.SizeWidth.Value > 0 ? layer.SizeWidth.Resolve(area.Width) : area.Width;
        var height = stated && layer.SizeHeight.Value > 0 ? layer.SizeHeight.Resolve(area.Height) : area.Height;

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
                0));
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
            (line is { Shadow: true } cast ? cast.Style : style).TextShadows));
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
                    AddRuleBand(target, segment.Band, segment.Rule, thinAlongY: segment.IsRow != vertical);
                }
            }
        }
    }

    /// <summary>
    /// Draws a rule as the side of a box that is nothing but that side: the top edge for a rule that runs
    /// across the page, the left edge for one that runs down it. Turning the band into a border side is
    /// what gives a dashed rule its dashes and a double rule its two lines.
    /// </summary>
    private static void AddRuleBand(List<PaintCommand> target, Rect band, GapRule rule, Boolean thinAlongY)
    {
        var width = rule.Width;

        target.Add(new BorderCommand(
            band,
            thinAlongY
                ? new Thickness(width, 0, 0, 0)
                : new Thickness(0, 0, 0, width),
            thinAlongY
                ? new BorderStyles(rule.Style, BorderLineStyle.None, BorderLineStyle.None, BorderLineStyle.None)
                : new BorderStyles(BorderLineStyle.None, BorderLineStyle.None, BorderLineStyle.None, rule.Style),
            default,
            rule.Color,
            rule.Color,
            rule.Color,
            rule.Color));
    }

    private static void AddBorder(LayoutBox box, ComputedStyle style, List<PaintCommand> target)
    {
        var border = box.Border;

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

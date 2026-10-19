using AngleSharp.Css.Dom;
using AngleSharp.Css.Values;
using Hutu.Rendering.Layout;
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

        PaintBox(layout.Root, list, list.Commands);
        return list;
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

    private void PaintBoxContent(LayoutBox box, PaintList list, List<PaintCommand> target, DeferredStack? deferred)
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
                AddBorder(box, box.Source.Style, target);
                break;

            case LayoutKind.Image:
                // An image box can carry a background and a border of its own, which paint under the
                // image itself.
                PaintBackground(box, box.Source.Style, target);
                AddBorder(box, box.Source.Style, target);
                PaintImage(box, target);
                break;

            case LayoutKind.Svg:
                // An inline SVG is a replaced box like an image: its own background and border paint
                // under the vector content, which is drawn from the markup the box carries.
                PaintBackground(box, box.Source.Style, target);
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
            PaintChildren(box, list, target, deferred);
            return;
        }

        // An empty cell under `empty-cells: hide` paints neither its background nor, in the separated
        // border model, its border. Every other box paints both.
        if (!box.SuppressBackground)
        {
            PaintBackground(box, style, target);
        }

        if (!box.SuppressBorder)
        {
            AddBorder(box, style, target);
        }

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
                skip: marked.SkipInk));
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
    private static void PaintBackground(LayoutBox box, ComputedStyle style, List<PaintCommand> target)
    {
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
                layers.Add(new LinearGradientCommand(area, gradient.AngleDegrees, gradient.Stops, radii));
            }
            else if (layer.Url is { Length: > 0 })
            {
                layers.Add(new BackgroundImageCommand(area, layer, radii));
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

        // The fill colour stands in for the text colour where the page set one; the stroke travels so the
        // back end rings each glyph with it, and a background clipped to the text can widen to it.
        var fill = style.TextFillColor ?? ToArgb(style.Color);

        target.Add(new TextCommand(
            box.BorderBox,
            box.Text,
            box.Baseline,
            box.Font,
            fill,
            box.Decorations ?? NoDecorations,
            box.Flow,
            style.LetterSpacing,
            style.TextStrokeWidth,
            style.TextStrokeColor,
            box.Justification,
            box.Justifies,
            style.TextJustify,
            style.WordSpacing,
            box.DecorationPlacements,
            style.SkipInk));
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
            style.BorderStyles,
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

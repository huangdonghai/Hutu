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
public sealed class Painter
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

    private void PaintBox(LayoutBox box, PaintList list, List<PaintCommand> target)
    {
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

            case LayoutKind.Line:
                // A line box has no decorations of its own; only its runs are painted.
                foreach (var child in box.Children)
                {
                    PaintBox(child, list, target);
                }

                break;

            default:
                PaintBlock(box, list, target);
                break;
        }

        // The outline is emitted last because it is drawn around the box rather than in it, which also
        // puts it after the clip the children were wrapped in: the box's own overflow must not cut it
        // off, while the clip of the nearest ancestor that hides its overflow still holds it in.
        // A line box and a run of text are not elements of their own - both borrow the style of what
        // they sit in - so an outline must not be emitted for them, or a block would grow one per line.
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

    private void PaintBlock(LayoutBox box, PaintList list, List<PaintCommand> target)
    {
        var style = box.Source.Style;

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

        PaintChildren(box, list, target);
    }

    /// <summary>
    /// Paints a box's children in stacking order, clipped to the box when it hides its overflow. With
    /// no positioned child the document order is already the paint order, so the fast path avoids the
    /// four passes the layering needs.
    /// </summary>
    private void PaintChildren(LayoutBox box, PaintList list, List<PaintCommand> target)
    {
        // The clip covers the children only: the box's own background and border were painted before
        // it, which is what keeps a border from being cut off by the radius it draws.
        var clipped = box.ClipsOverflow && box.Children.Count > 0;

        if (clipped)
        {
            target.Add(new PushClipCommand(box.PaddingBox, box.PaddingRadii));
        }

        if (HasPositionedChild(box))
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

        // 2. The in-flow, non-positioned content.
        foreach (var child in box.Children)
        {
            if (child.Position == PositionMode.Static)
            {
                PaintChild(child, list, target);
            }
        }

        // 3. Positioned boxes with `z-index: auto` or `0`, in document order.
        foreach (var child in box.Children)
        {
            if (child.Position != PositionMode.Static && child.ZIndex == 0)
            {
                PaintChild(child, list, target);
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
    private void PaintChild(LayoutBox child, PaintList list, List<PaintCommand> target)
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
                PaintBox(child, list, target);
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

        if (style.BackgroundColor is { } background && background.A != 0)
        {
            target.Add(new FillCommand(area, ToArgb(background), radii));
        }

        var layers = style.Backgrounds;

        if (layers.Count == 0)
        {
            return;
        }

        for (var index = layers.Count - 1; index >= 0; index--)
        {
            var layer = layers[index];

            if (layer.Gradient is { } gradient)
            {
                target.Add(new LinearGradientCommand(area, gradient.AngleDegrees, gradient.Stops, radii));
            }
            else if (layer.Url is { Length: > 0 })
            {
                target.Add(new BackgroundImageCommand(area, layer, radii));
            }
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

        target.Add(new TextCommand(
            box.BorderBox,
            box.Text,
            box.Baseline,
            box.Font,
            ToArgb(style.Color),
            style.HasUnderline,
            style.HasLineThrough,
            box.Flow));
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

    private static UInt32 ToArgb(CssColorValue color) =>
        (UInt32)((color.A << 24) | (color.R << 16) | (color.G << 8) | color.B);
}

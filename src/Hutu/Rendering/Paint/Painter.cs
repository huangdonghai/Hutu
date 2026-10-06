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
/// Borders and text are painted on the border box, backgrounds on the padding box, which is where a
/// background is painted by default. The walk order is the paint order: a block's own decorations
/// come before its children, and among the children the stacking levels are honoured - the negative
/// <c>z-index</c> layer, the in-flow content, the positioned boxes with an automatic level, then the
/// positive layer. Boxes fixed to the viewport are diverted to their own layer instead.
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
    /// Paints a box's children in stacking order. With no positioned child the document order is
    /// already the paint order, so the fast path avoids the four passes the layering needs.
    /// </summary>
    private void PaintChildren(LayoutBox box, PaintList list, List<PaintCommand> target)
    {
        if (!HasPositionedChild(box))
        {
            foreach (var child in box.Children)
            {
                PaintChild(child, list, target);
            }

            return;
        }

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
    /// Paints the background colour and then the layers, both across the padding box - which is
    /// where they are positioned by default. CSS paints the first declared layer on top, so the
    /// layers are emitted last-first and the colour always ends up underneath all of them.
    /// </summary>
    private static void PaintBackground(LayoutBox box, ComputedStyle style, List<PaintCommand> target)
    {
        var area = box.PaddingBox;

        if (style.BackgroundColor is { } background && background.A != 0)
        {
            target.Add(new FillCommand(area, ToArgb(background)));
        }

        var layers = style.Backgrounds;

        if (layers.Count == 0)
        {
            return;
        }

        var radius = style.CornerRadius;

        for (var index = layers.Count - 1; index >= 0; index--)
        {
            var layer = layers[index];

            if (layer.Gradient is { } gradient)
            {
                target.Add(new LinearGradientCommand(area, gradient.AngleDegrees, gradient.Stops, radius));
            }
            else if (layer.Url is { Length: > 0 })
            {
                target.Add(new BackgroundImageCommand(area, layer, radius));
            }
        }
    }

    /// <summary>
    /// Emits the image, clipped to its content box. The box is the content box because that is where
    /// a replaced element's content goes; for the common case of an image with no padding or border
    /// it is the same rectangle as the border box.
    /// </summary>
    private static void PaintImage(LayoutBox box, List<PaintCommand> target)
    {
        if (String.IsNullOrEmpty(box.ImageUrl))
        {
            return;
        }

        target.Add(new ImageCommand(box.ContentBox, box.ImageUrl, box.ImageFit, box.CornerRadius));
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
            target.Add(new FillCommand(highlight, HighlightColor));
        }

        var style = box.Source.Style;

        target.Add(new TextCommand(
            box.BorderBox,
            box.Text,
            box.Baseline,
            box.Font,
            ToArgb(style.Color),
            style.HasUnderline,
            style.HasLineThrough));
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
            colors?.Top ?? ToArgb(style.BorderTopColor ?? style.Color),
            colors?.Right ?? ToArgb(style.BorderRightColor ?? style.Color),
            colors?.Bottom ?? ToArgb(style.BorderBottomColor ?? style.Color),
            colors?.Left ?? ToArgb(style.BorderLeftColor ?? style.Color)));
    }

    private static UInt32 ToArgb(CssColorValue color) =>
        (UInt32)((color.A << 24) | (color.R << 16) | (color.G << 8) | color.B);
}

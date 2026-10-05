using AngleSharp.Css.Values;
using Hutu.Rendering.Layout;
using System;

namespace Hutu.Rendering.Paint;

/// <summary>
/// Walks the layout tree and produces the platform-neutral drawing instructions.
/// </summary>
/// <remarks>
/// Borders and text are painted on the border box, backgrounds on the padding box, which is where a
/// background is painted by default. The walk order is the paint order: a block's own decorations
/// come before its children.
/// </remarks>
public sealed class Painter
{
    public PaintList Paint(LayoutResult layout)
    {
        var list = new PaintList
        {
            Bounds = new Rect(0, 0, layout.Width, layout.Height),
        };

        PaintBox(layout.Root, list);
        return list;
    }

    private static void PaintBox(LayoutBox box, PaintList list)
    {
        switch (box.Kind)
        {
            case LayoutKind.Text:
                PaintText(box, list);
                break;

            case LayoutKind.HorizontalRule:
                AddBorder(box, box.Source.Style, list);
                break;

            case LayoutKind.Image:
                // An image box can carry a background and a border of its own, which paint under the
                // image itself.
                PaintBackground(box, box.Source.Style, list);
                AddBorder(box, box.Source.Style, list);
                PaintImage(box, list);
                break;

            case LayoutKind.Line:
                // A line box has no decorations of its own; only its runs are painted.
                foreach (var child in box.Children)
                {
                    PaintBox(child, list);
                }

                break;

            default:
                PaintBlock(box, list);
                break;
        }
    }

    private static void PaintBlock(LayoutBox box, PaintList list)
    {
        var style = box.Source.Style;

        PaintBackground(box, style, list);
        AddBorder(box, style, list);

        foreach (var child in box.Children)
        {
            PaintBox(child, list);
        }
    }

    /// <summary>
    /// Paints the background colour and then the layers, both across the padding box - which is
    /// where they are positioned by default. CSS paints the first declared layer on top, so the
    /// layers are emitted last-first and the colour always ends up underneath all of them.
    /// </summary>
    private static void PaintBackground(LayoutBox box, ComputedStyle style, PaintList list)
    {
        var area = box.PaddingBox;

        if (style.BackgroundColor is { } background && background.A != 0)
        {
            list.Commands.Add(new FillCommand(area, ToArgb(background)));
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
                list.Commands.Add(new LinearGradientCommand(area, gradient.AngleDegrees, gradient.Stops, radius));
            }
            else if (layer.Url is { Length: > 0 })
            {
                list.Commands.Add(new BackgroundImageCommand(area, layer, radius));
            }
        }
    }

    /// <summary>
    /// Emits the image, clipped to its content box. The box is the content box because that is where
    /// a replaced element's content goes; for the common case of an image with no padding or border
    /// it is the same rectangle as the border box.
    /// </summary>
    private static void PaintImage(LayoutBox box, PaintList list)
    {
        if (String.IsNullOrEmpty(box.ImageUrl))
        {
            return;
        }

        list.Commands.Add(new ImageCommand(box.ContentBox, box.ImageUrl, box.ImageFit, box.CornerRadius));
    }

    private static void PaintText(LayoutBox box, PaintList list)
    {
        if (String.IsNullOrEmpty(box.Text))
        {
            return;
        }

        var style = box.Source.Style;

        list.Commands.Add(new TextCommand(
            box.BorderBox,
            box.Text,
            box.Baseline,
            box.Font,
            ToArgb(style.Color),
            style.HasUnderline,
            style.HasLineThrough));
    }

    private static void AddBorder(LayoutBox box, ComputedStyle style, PaintList list)
    {
        var border = box.Border;

        if (border.Horizontal <= 0 && border.Vertical <= 0)
        {
            return;
        }

        list.Commands.Add(new BorderCommand(
            box.BorderBox,
            border,
            ToArgb(style.BorderTopColor ?? style.Color),
            ToArgb(style.BorderRightColor ?? style.Color),
            ToArgb(style.BorderBottomColor ?? style.Color),
            ToArgb(style.BorderLeftColor ?? style.Color)));
    }

    private static UInt32 ToArgb(CssColorValue color) =>
        (UInt32)((color.A << 24) | (color.R << 16) | (color.G << 8) | color.B);
}

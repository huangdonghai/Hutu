using Hutu.Rendering.Layout;
using Hutu.Rendering.Media;
using System;
using System.Collections.Generic;

namespace Hutu.Rendering.Paint;

/// <summary>
/// A platform-neutral drawing instruction. The layout side produces these; a backend executes them
/// against its own canvas, which is what keeps painting out of the core.
/// </summary>
public abstract class PaintCommand
{
    protected PaintCommand(Rect bounds) => Bounds = bounds;

    /// <summary>Gets the area the command covers, in document coordinates.</summary>
    public Rect Bounds { get; }
}

/// <summary>Fills a rectangle with a solid colour, given as packed ARGB.</summary>
public sealed class FillCommand : PaintCommand
{
    public FillCommand(Rect bounds, UInt32 color)
        : base(bounds) => Color = color;

    public UInt32 Color { get; }
}

/// <summary>
/// Draws the four borders of a box. Each side carries its own colour and width, so a side with a
/// zero width is simply not drawn.
/// </summary>
public sealed class BorderCommand : PaintCommand
{
    public BorderCommand(Rect bounds, Thickness widths, UInt32 top, UInt32 right, UInt32 bottom, UInt32 left)
        : base(bounds)
    {
        Widths = widths;
        TopColor = top;
        RightColor = right;
        BottomColor = bottom;
        LeftColor = left;
    }

    public Thickness Widths { get; }

    public UInt32 TopColor { get; }

    public UInt32 RightColor { get; }

    public UInt32 BottomColor { get; }

    public UInt32 LeftColor { get; }
}

/// <summary>
/// Draws an image into its box. <c>object-fit</c> decides how the content is fitted and the radius
/// how it is clipped; both are drawing concerns, which is why the box geometry is already resolved
/// by the time this command is produced.
/// </summary>
public sealed class ImageCommand : PaintCommand
{
    public ImageCommand(Rect bounds, String url, ObjectFit fit, Double cornerRadius)
        : base(bounds)
    {
        Url = url;
        Fit = fit;
        CornerRadius = cornerRadius;
    }

    /// <summary>Gets the absolute URL of the image; the backend resolves it from its own cache.</summary>
    public String Url { get; }

    public ObjectFit Fit { get; }

    public Double CornerRadius { get; }
}

/// <summary>
/// Draws one background image layer. A background brings its own size, position and repeat rules,
/// which is a different placement model from <c>object-fit</c>, so it carries the parsed layer.
/// </summary>
public sealed class BackgroundImageCommand : PaintCommand
{
    public BackgroundImageCommand(Rect bounds, BackgroundLayer layer, Double cornerRadius)
        : base(bounds)
    {
        Layer = layer;
        CornerRadius = cornerRadius;
    }

    public BackgroundLayer Layer { get; }

    public Double CornerRadius { get; }
}

/// <summary>Draws one linear gradient layer across the background positioning area.</summary>
public sealed class LinearGradientCommand : PaintCommand
{
    public LinearGradientCommand(Rect bounds, Double angleDegrees, IReadOnlyList<GradientStop> stops, Double cornerRadius)
        : base(bounds)
    {
        AngleDegrees = angleDegrees;
        Stops = stops;
        CornerRadius = cornerRadius;
    }

    /// <summary>Gets the direction, in degrees clockwise from "to top".</summary>
    public Double AngleDegrees { get; }

    public IReadOnlyList<GradientStop> Stops { get; }

    public Double CornerRadius { get; }
}

/// <summary>A run of text drawn on a single baseline.</summary>
public sealed class TextCommand : PaintCommand
{
    public TextCommand(
        Rect bounds,
        String text,
        Double baseline,
        FontStyle font,
        UInt32 color,
        Boolean underline,
        Boolean lineThrough)
        : base(bounds)
    {
        Text = text;
        Baseline = baseline;
        Font = font;
        Color = color;
        Underline = underline;
        LineThrough = lineThrough;
    }

    public String Text { get; }

    /// <summary>Gets the distance from the top of <see cref="PaintCommand.Bounds"/> to the baseline.</summary>
    public Double Baseline { get; }

    public FontStyle Font { get; }

    public UInt32 Color { get; }

    public Boolean Underline { get; }

    public Boolean LineThrough { get; }
}

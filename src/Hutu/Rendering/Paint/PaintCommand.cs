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
    public FillCommand(Rect bounds, UInt32 color, CornerRadii radii)
        : base(bounds)
    {
        Color = color;
        Radii = radii;
    }

    public UInt32 Color { get; }

    /// <summary>Gets the corner radii of the filled area, which are square when there are none.</summary>
    public CornerRadii Radii { get; }
}

/// <summary>
/// Draws the four borders of a box. Each side carries its own colour, width and stroke, so a side
/// with a zero width is simply not drawn.
/// </summary>
public sealed class BorderCommand : PaintCommand
{
    public BorderCommand(
        Rect bounds,
        Thickness widths,
        BorderStyles styles,
        CornerRadii radii,
        UInt32 top,
        UInt32 right,
        UInt32 bottom,
        UInt32 left)
        : base(bounds)
    {
        Widths = widths;
        Styles = styles;
        Radii = radii;
        TopColor = top;
        RightColor = right;
        BottomColor = bottom;
        LeftColor = left;
    }

    public Thickness Widths { get; }

    /// <summary>Gets the stroke each side is drawn with.</summary>
    public BorderStyles Styles { get; }

    /// <summary>Gets the corner radii of the border box, which the border follows.</summary>
    public CornerRadii Radii { get; }

    public UInt32 TopColor { get; }

    public UInt32 RightColor { get; }

    public UInt32 BottomColor { get; }

    public UInt32 LeftColor { get; }
}

/// <summary>
/// Draws an image into its box. <c>object-fit</c> decides how the content is fitted and the radii
/// how it is clipped; both are drawing concerns, which is why the box geometry is already resolved
/// by the time this command is produced.
/// </summary>
public sealed class ImageCommand : PaintCommand
{
    public ImageCommand(Rect bounds, String url, ObjectFit fit, CornerRadii radii)
        : base(bounds)
    {
        Url = url;
        Fit = fit;
        Radii = radii;
    }

    /// <summary>Gets the absolute URL of the image; the backend resolves it from its own cache.</summary>
    public String Url { get; }

    public ObjectFit Fit { get; }

    /// <summary>Gets the corner radii the image is clipped to.</summary>
    public CornerRadii Radii { get; }
}

/// <summary>
/// Draws one background image layer. A background brings its own size, position and repeat rules,
/// which is a different placement model from <c>object-fit</c>, so it carries the parsed layer.
/// </summary>
public sealed class BackgroundImageCommand : PaintCommand
{
    public BackgroundImageCommand(Rect bounds, BackgroundLayer layer, CornerRadii radii)
        : base(bounds)
    {
        Layer = layer;
        Radii = radii;
    }

    public BackgroundLayer Layer { get; }

    /// <summary>Gets the corner radii of the painted area.</summary>
    public CornerRadii Radii { get; }
}

/// <summary>Draws one linear gradient layer across the background positioning area.</summary>
public sealed class LinearGradientCommand : PaintCommand
{
    public LinearGradientCommand(Rect bounds, Double angleDegrees, IReadOnlyList<GradientStop> stops, CornerRadii radii)
        : base(bounds)
    {
        AngleDegrees = angleDegrees;
        Stops = stops;
        Radii = radii;
    }

    /// <summary>Gets the direction, in degrees clockwise from "to top".</summary>
    public Double AngleDegrees { get; }

    public IReadOnlyList<GradientStop> Stops { get; }

    /// <summary>Gets the corner radii of the painted area.</summary>
    public CornerRadii Radii { get; }
}

/// <summary>
/// Clips everything that follows to a rounded rectangle, until the matching
/// <see cref="PopClipCommand"/>: what a box with an <c>overflow</c> other than <c>visible</c> asks for.
/// </summary>
/// <remarks>
/// The two commands are always a pair inside one list, which is what lets a backend map them straight
/// onto its own save and restore without having to balance them at the end of a walk. Clipping a
/// descendant that is diverted to another layer - a fixed box, say - is deliberately not part of this:
/// a fixed box is positioned against the viewport rather than the clipping box.
/// </remarks>
public sealed class PushClipCommand : PaintCommand
{
    public PushClipCommand(Rect bounds, CornerRadii radii)
        : base(bounds) => Radii = radii;

    /// <summary>Gets the corner radii of the clip, relative to <see cref="PaintCommand.Bounds"/>.</summary>
    public CornerRadii Radii { get; }
}

/// <summary>Ends the clip the nearest preceding <see cref="PushClipCommand"/> opened.</summary>
public sealed class PopClipCommand : PaintCommand
{
    /// <summary>
    /// Creates the command that closes a clip. The area is carried along so that the command still
    /// describes where it applies, even though undoing a clip needs no geometry.
    /// </summary>
    public PopClipCommand(Rect bounds)
        : base(bounds)
    {
    }
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

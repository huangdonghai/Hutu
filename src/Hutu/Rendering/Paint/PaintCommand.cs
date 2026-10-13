using Hutu.Rendering.Layout;
using Hutu.Rendering.Media;
using System;
using System.Collections.Generic;

namespace Hutu.Rendering.Paint;

/// <summary>
/// A platform-neutral drawing instruction. The layout side produces these; a backend executes them
/// against its own canvas, which is what keeps painting out of the core.
/// </summary>
/// <remarks>
/// The constructors belong to the painter alone: a backend is told what to draw and never assembles
/// a command of its own, so what a host can do with a paint list is read it.
/// </remarks>
public abstract class PaintCommand
{
    protected PaintCommand(Rect bounds) => Bounds = bounds;

    /// <summary>Gets the area the command covers, in document coordinates.</summary>
    public Rect Bounds { get; }
}

/// <summary>Fills a rectangle with a solid colour, given as packed ARGB.</summary>
public sealed class FillCommand : PaintCommand
{
    internal FillCommand(Rect bounds, UInt32 color, CornerRadii radii)
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
    internal BorderCommand(
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
/// Draws the outline of a box: one unbroken stroke drawn just outside the border box, which is what
/// makes it useful for marking a box the reader is pointing at without disturbing the layout.
/// </summary>
/// <remarks>
/// <see cref="PaintCommand.Bounds"/> is the border box, not the outline's own rectangle: the stroke
/// runs from <c>outline-offset</c> outside that rectangle outwards for <c>outline-width</c>, so it is
/// the backend that inflates the rectangle it draws. A rounded box's outline follows its corner radii,
/// grown to match the distance it stands away, so that it runs parallel to the border it surrounds.
/// </remarks>
public sealed class OutlineCommand : PaintCommand
{
    internal OutlineCommand(
        Rect bounds,
        CornerRadii radii,
        BorderLineStyle style,
        Double width,
        Double offset,
        UInt32 color)
        : base(bounds)
    {
        Radii = radii;
        Style = style;
        Width = width;
        Offset = offset;
        Color = color;
    }

    /// <summary>Gets the corner radii of the border box, which the outline follows.</summary>
    public CornerRadii Radii { get; }

    public BorderLineStyle Style { get; }

    /// <summary>Gets how thick the stroke is.</summary>
    public Double Width { get; }

    /// <summary>Gets how far outside the border box the stroke begins.</summary>
    public Double Offset { get; }

    public UInt32 Color { get; }
}

/// <summary>
/// Draws an image into its box. <c>object-fit</c> decides how the content is fitted and the radii
/// how it is clipped; both are drawing concerns, which is why the box geometry is already resolved
/// by the time this command is produced.
/// </summary>
public sealed class ImageCommand : PaintCommand
{
    internal ImageCommand(Rect bounds, String url, ObjectFit fit, CornerRadii radii)
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
    internal BackgroundImageCommand(Rect bounds, BackgroundLayer layer, CornerRadii radii)
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
    internal LinearGradientCommand(Rect bounds, Double angleDegrees, IReadOnlyList<GradientStop> stops, CornerRadii radii)
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
    internal PushClipCommand(Rect bounds, CornerRadii radii)
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
    internal PopClipCommand(Rect bounds)
        : base(bounds)
    {
    }
}

/// <summary>A run of text drawn on a single baseline.</summary>
public sealed class TextCommand : PaintCommand
{
    /// <summary>
    /// Creates the command for a run of horizontal text, which is what a page that never asks for a
    /// writing mode is made of.
    /// </summary>
    internal TextCommand(
        Rect bounds,
        String text,
        Double baseline,
        FontStyle font,
        UInt32 color,
        Boolean underline,
        Boolean lineThrough)
        : this(bounds, text, baseline, font, color, underline, lineThrough, WritingModeInfo.Horizontal, 0)
    {
    }

    /// <param name="flow">
    /// How the run is laid out. A vertical run advances down its bounds rather than across them, and
    /// which way its glyphs are turned decides whether the backend rotates the whole run or stacks the
    /// characters upright one under the other.
    /// </param>
    internal TextCommand(
        Rect bounds,
        String text,
        Double baseline,
        FontStyle font,
        UInt32 color,
        Boolean underline,
        Boolean lineThrough,
        WritingModeInfo flow)
        : this(bounds, text, baseline, font, color, underline, lineThrough, flow, 0)
    {
    }

    /// <param name="letterSpacing">
    /// The extra space the run carries after every character, which the backend adds to its advances
    /// so that what it draws lands where the layout measured it. It is zero for text that asks for
    /// none.
    /// </param>
    internal TextCommand(
        Rect bounds,
        String text,
        Double baseline,
        FontStyle font,
        UInt32 color,
        Boolean underline,
        Boolean lineThrough,
        WritingModeInfo flow,
        Double letterSpacing)
        : base(bounds)
    {
        Text = text;
        Baseline = baseline;
        Font = font;
        Color = color;
        Underline = underline;
        LineThrough = lineThrough;
        Flow = flow;
        LetterSpacing = letterSpacing;
    }

    public String Text { get; }

    /// <summary>
    /// Gets the distance from the block-start edge of <see cref="PaintCommand.Bounds"/> to the
    /// baseline: down from the top edge for horizontal text, across from the left edge for a column.
    /// </summary>
    public Double Baseline { get; }

    /// <summary>Gets how the run is laid out, which is horizontal for every run that does not ask otherwise.</summary>
    public WritingModeInfo Flow { get; }

    /// <summary>Gets the extra space added after every character, in pixels; zero when there is none.</summary>
    public Double LetterSpacing { get; }

    public FontStyle Font { get; }

    public UInt32 Color { get; }

    public Boolean Underline { get; }

    public Boolean LineThrough { get; }
}

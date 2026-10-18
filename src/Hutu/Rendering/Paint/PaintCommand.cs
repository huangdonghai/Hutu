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
/// Draws an inline <c>&lt;svg&gt;</c> into its box. The backend parses the standalone markup and paints
/// the resulting picture scaled to the box, so the vector stays crisp at any size.
/// </summary>
public sealed class SvgCommand : PaintCommand
{
    internal SvgCommand(Rect bounds, String markup, CornerRadii radii)
        : base(bounds)
    {
        Markup = markup;
        Radii = radii;
    }

    /// <summary>Gets the standalone SVG markup to render.</summary>
    public String Markup { get; }

    /// <summary>Gets the corner radii the graphic is clipped to.</summary>
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
/// Draws a box's background - the colour and any layers - but keeps it only where the box's text is:
/// <c>background-clip: text</c>. The layers are painted, then masked to the union of the glyph shapes
/// listed in <see cref="Mask"/>, each widened by its own text stroke, so a transparent stroke still
/// lets more of the background through than no stroke would.
/// </summary>
/// <remarks>
/// Grouped into one command rather than a clip pair because the mask is not a hard clip a canvas applies
/// to what follows - it is the alpha of the glyphs multiplied out of a layer - and a backend that drew
/// the layers, then the mask, then composed the result, is exactly the sequence a save-restore around
/// them expresses in one instruction.
/// </remarks>
public sealed class TextClippedBackgroundCommand : PaintCommand
{
    internal TextClippedBackgroundCommand(Rect bounds, IReadOnlyList<PaintCommand> layers, IReadOnlyList<TextCommand> mask)
        : base(bounds)
    {
        Layers = layers;
        Mask = mask;
    }

    /// <summary>Gets the background commands painted before the mask is applied.</summary>
    public IReadOnlyList<PaintCommand> Layers { get; }

    /// <summary>Gets the runs whose glyph coverage the background is kept to.</summary>
    public IReadOnlyList<TextCommand> Mask { get; }
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

/// <summary>
/// Applies a 2D transform to everything that follows, until the matching <see cref="PopTransformCommand"/>:
/// what a box with a <c>transform</c> asks of its own paint and its whole subtree. The matrix is taken
/// about <see cref="Origin"/>, a point in document coordinates, so a rotate or scale pivots where the
/// author set <c>transform-origin</c> rather than at the page corner.
/// </summary>
/// <remarks>
/// Like a clip, the two commands are always a balanced pair inside one list, and the box is painted as
/// a unit between them - which is also what makes a transformed box the layer its out-of-flow
/// descendants belong to, since nothing from inside escapes to a list beyond the pair.
/// </remarks>
public sealed class PushTransformCommand : PaintCommand
{
    internal PushTransformCommand(Rect bounds, Affine matrix, Offset origin)
        : base(bounds)
    {
        Matrix = matrix;
        Origin = origin;
    }

    /// <summary>Gets the affine matrix, in CSS's <c>matrix(a, b, c, d, e, f)</c> terms.</summary>
    public Affine Matrix { get; }

    /// <summary>Gets the point the transform is taken about, in document coordinates.</summary>
    public Offset Origin { get; }
}

/// <summary>Ends the transform the nearest preceding <see cref="PushTransformCommand"/> opened.</summary>
public sealed class PopTransformCommand : PaintCommand
{
    internal PopTransformCommand(Rect bounds)
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
        Boolean overline,
        Boolean lineThrough)
        : this(
            bounds, text, baseline, font, color, underline, overline, lineThrough,
            WritingModeInfo.Horizontal, 0)
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
        Boolean overline,
        Boolean lineThrough,
        WritingModeInfo flow)
        : this(bounds, text, baseline, font, color, underline, overline, lineThrough, flow, 0)
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
        Boolean overline,
        Boolean lineThrough,
        WritingModeInfo flow,
        Double letterSpacing)
        : this(
            bounds, text, baseline, font, color, underline, overline, lineThrough, flow,
            letterSpacing, 0, 0, DecorationShape.Solid)
    {
    }

    /// <param name="strokeWidth">
    /// The width the glyphs are ringed with, in pixels, and <paramref name="strokeColor"/> its colour.
    /// Both come from <c>-webkit-text-stroke</c>; a zero width is no stroke. The ring is drawn under the
    /// fill, and its width also widens the ink a background clipped to the text is kept to.
    /// </param>
    internal TextCommand(
        Rect bounds,
        String text,
        Double baseline,
        FontStyle font,
        UInt32 color,
        Boolean underline,
        Boolean overline,
        Boolean lineThrough,
        WritingModeInfo flow,
        Double letterSpacing,
        Double strokeWidth,
        UInt32 strokeColor,
        DecorationShape decoration = DecorationShape.Solid,
        Double justification = 0,
        Int32 justifies = 0,
        String expand = "auto")
        : base(bounds)
    {
        Text = text;
        Baseline = baseline;
        Font = font;
        Color = color;
        Underline = underline;
        Overline = overline;
        LineThrough = lineThrough;
        Decoration = decoration;
        Flow = flow;
        LetterSpacing = letterSpacing;
        StrokeWidth = strokeWidth;
        StrokeColor = strokeColor;
        Justification = justification;
        Justifies = justifies;
        Expand = expand;
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

    /// <summary>Gets the width the glyphs are ringed with, in pixels; zero is no stroke.</summary>
    public Double StrokeWidth { get; }

    /// <summary>Gets the packed ARGB of that ring; transparent when none was given.</summary>
    public UInt32 StrokeColor { get; }

    public FontStyle Font { get; }

    public UInt32 Color { get; }

    public Boolean Underline { get; }

    public Boolean Overline { get; }

    public Boolean LineThrough { get; }

    /// <summary>Gets the shape every one of the lines above is drawn in.</summary>
    public DecorationShape Decoration { get; }

    /// <summary>
    /// Gets the room a justified line owes after each place between this run's words, and how many of
    /// those places the run holds: the width the layout gave the run already counts them, and the glyphs
    /// have to be set out to match. Zero asks for nothing.
    /// </summary>
    public Double Justification { get; }

    public Int32 Justifies { get; }

    /// <summary>
    /// Gets which boundaries of the run the justified room goes after - the value of
    /// <c>text-justify</c>, which says whether a line expands only between its words or also between
    /// the characters of a script that writes no spaces. The runs of white space the layout counted
    /// are found again from the text, and the seam between two ideographs split across runs is not,
    /// so the answer travels with the command rather than being guessed at here.
    /// </summary>
    public String Expand { get; }
}

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

    /// <summary>
    /// Gets the place a fieldset's legend stands in the border, where the border is not painted
    /// (HTML rendering §15.3.12). Null for every box whose border is whole.
    /// </summary>
    public Rect? Notch { get; init; }
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
/// Draws one shadow of a box - the shape the box casts, or the shape it is cast by.
/// </summary>
/// <remarks>
/// Both halves of the geometry travel with it, because the two kinds of shadow are the same operation read
/// from opposite ends: an outer one is <see cref="Shape"/> drawn and <see cref="Area"/> cut out of it, so
/// that nothing shows where the box itself stands; an inner one is <see cref="Area"/> filled and
/// <see cref="Shape"/> cut out of that, so that nothing shows beyond the edge the shadow was thrown from.
/// The area is the border box for the outer kind and the padding box for the inner one, which is what
/// §6.1.1 says each is drawn from, and the shape is that area moved by the offsets and grown or shrunk by
/// the spread, with its corners grown or shrunk to match (CSS Backgrounds 3 §6.1.1).
/// <para>
/// <see cref="PaintCommand.Bounds"/> is neither of the two but the whole of what the command can put ink
/// on: the shape, with the blur added on every side it reaches - which is why it is the rectangle a
/// backend is culled against and the smaller one would cut a soft edge short.
/// </para>
/// </remarks>
public sealed class BoxShadowCommand : PaintCommand
{
    internal BoxShadowCommand(
        Rect bounds,
        Rect area,
        CornerRadii areaRadii,
        Rect shape,
        CornerRadii shapeRadii,
        Double blur,
        UInt32 color,
        Boolean inset)
        : base(bounds)
    {
        Area = area;
        AreaRadii = areaRadii;
        Shape = shape;
        ShapeRadii = shapeRadii;
        Blur = blur;
        Color = color;
        Inset = inset;
    }

    /// <summary>Gets the box's own area: the border box of an outer shadow, the padding box of an inner one.</summary>
    public Rect Area { get; }

    /// <summary>Gets the corner radii of <see cref="Area"/>.</summary>
    public CornerRadii AreaRadii { get; }

    /// <summary>Gets the shape the shadow is drawn from, moved by the offsets and moved again by the spread.</summary>
    public Rect Shape { get; }

    /// <summary>Gets the corner radii of <see cref="Shape"/>.</summary>
    public CornerRadii ShapeRadii { get; }

    /// <summary>
    /// Gets the blur radius in pixels, of which the backend makes a Gaussian whose standard deviation is
    /// half it (CSS Backgrounds 3 §6.1.2). Zero draws a sharp edge.
    /// </summary>
    public Double Blur { get; }

    public UInt32 Color { get; }

    /// <summary>Gets whether the shadow lies inside the box rather than under it.</summary>
    public Boolean Inset { get; }
}

/// <summary>
/// Draws one background image layer. A background brings its own size, position and repeat rules,
/// which is a different placement model from <c>object-fit</c>, so it carries the parsed layer.
/// </summary>
/// <remarks>
/// The two areas are apart because CSS keeps them apart: <see cref="PaintCommand.Bounds"/> is the area
/// the layer is painted over and clipped to, while <see cref="Placement"/> is the box the image is sized
/// and stood against. For a box they are the same rectangle, and only the background the canvas wears
/// makes them differ - where the picture covers the whole surface while the root's own box still decides
/// where the first tile begins (CSS Backgrounds 3 §2.4, §2.11.1).
/// </remarks>
public sealed class BackgroundImageCommand : PaintCommand
{
    internal BackgroundImageCommand(Rect bounds, Rect placement, BackgroundLayer layer, CornerRadii radii)
        : base(bounds)
    {
        Placement = placement;
        Layer = layer;
        Radii = radii;
    }

    public BackgroundLayer Layer { get; }

    /// <summary>Gets the area the image is sized and placed against.</summary>
    public Rect Placement { get; }

    /// <summary>Gets the corner radii of the painted area.</summary>
    public CornerRadii Radii { get; }
}

/// <summary>
/// Draws a box's border image: the picture in <see cref="BorderImageSpec"/> cut into nine regions and
/// tiled into the border image area after the corners, which are never scaled (CSS Backgrounds 3 §6.3).
/// </summary>
public sealed class BorderImageCommand : PaintCommand
{
    internal BorderImageCommand(
        Rect area,
        BorderImageSpec image,
        Thickness widths,
        GradientCommand? gradient)
        : base(area)
    {
        Area = area;
        Image = image;
        Widths = widths;
        Gradient = gradient;
    }

    /// <summary>
    /// Gets the border image area the picture is drawn over: the border box grown by the outsets, which
    /// is larger than what the box took in the layout.
    /// </summary>
    public Rect Area { get; }

    /// <summary>Gets the picture, the cut and the way its edges fill their room.</summary>
    public BorderImageSpec Image { get; }

    /// <summary>Gets how wide each edge is drawn inside the area, settled against its size.</summary>
    public Thickness Widths { get; }

    /// <summary>
    /// Gets the gradient the picture is, drawn over the whole area before it is cut - or null where the
    /// source is a file. A gradient has no size of its own, so it is the area that gives it one
    /// (CSS Backgrounds 3 §6.2).
    /// </summary>
    public GradientCommand? Gradient { get; }
}

/// <summary>
/// Draws one linear gradient layer. <see cref="PaintCommand.Bounds"/> is the background positioning
/// area the layer is clipped to, and <see cref="Tile"/> is the piece of it the gradient fills and
/// repeats from.
/// </summary>
/// <remarks>
/// <see cref="PaintCommand.Bounds"/> is the area the layer is painted over and clipped to - the box's
/// background positioning area as a rule, and the whole canvas for the background the canvas wears - while
/// <see cref="Tile"/> is where the position stood the first copy of it, which is always settled against the
/// box the declaration was written on.
/// <para>
/// A gradient carries no intrinsic size, so the tile is settled entirely by the declaration and is
/// therefore the core's to work out (CSS Images 3 §4.1.3); the repeating of it across the area is left
/// to the backend, which is the one that draws, because a hairline band tiled across a wide box is
/// thousands of tiles and not thousands of commands.
/// </para>
/// </remarks>
public sealed class GradientCommand : PaintCommand
{
    internal GradientCommand(
        Rect bounds,
        Rect tile,
        Boolean repeatX,
        Boolean repeatY,
        GradientShape shape,
        Double angleDegrees,
        Double centerX,
        Double centerY,
        IReadOnlyList<GradientStop> stops,
        CornerRadii radii,
        Double radiusX = 0,
        Double radiusY = 0)
        : base(bounds)
    {
        Tile = tile;
        RepeatX = repeatX;
        RepeatY = repeatY;
        Shape = shape;
        AngleDegrees = angleDegrees;
        CenterX = centerX;
        CenterY = centerY;
        Stops = stops;
        Radii = radii;
        RadiusX = radiusX;
        RadiusY = radiusY;
    }

    /// <summary>Gets whether the colours run along a line or around the centre.</summary>
    public GradientShape Shape { get; }

    /// <summary>
    /// Gets the angle, in degrees clockwise from "to top": the direction of a line, and the start of a
    /// sweep.
    /// </summary>
    public Double AngleDegrees { get; }

    /// <summary>Gets where a sweep turns around, in page coordinates.</summary>
    public Double CenterX { get; }

    /// <summary>Gets where a sweep turns around, in page coordinates.</summary>
    public Double CenterY { get; }

    /// <summary>
    /// Gets how far a radial gradient's ending shape reaches along the inline axis, in page coordinates.
    /// The two are the box the shape was settled against already, so the backend has only to draw it.
    /// </summary>
    public Double RadiusX { get; }

    /// <summary>Gets how far a radial gradient's ending shape reaches down the box.</summary>
    public Double RadiusY { get; }

    public IReadOnlyList<GradientStop> Stops { get; }

    /// <summary>Gets the corner radii of the painted area.</summary>
    public CornerRadii Radii { get; }

    /// <summary>Gets the rectangle the gradient is drawn into, where the position puts its first tile.</summary>
    public Rect Tile { get; }

    /// <summary>Gets whether the tile repeats along the inline axis.</summary>
    public Boolean RepeatX { get; }

    /// <summary>Gets whether the tile repeats along the block axis.</summary>
    public Boolean RepeatY { get; }
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
/// How one piece of an outlined clip continues from the point the last piece left off: a corner of the
/// shape, a straight edge to the next one, a curve through the two controls it was drawn with, or the
/// closing of the sub-shape the piece began.
/// </summary>
public enum ClipEdge
{
    /// <summary>Starts a sub-shape at this point.</summary>
    Begin,

    /// <summary>Runs straight to this point.</summary>
    Line,

    /// <summary>Runs to this point along the curve whose controls are the first two points.</summary>
    Curve,

    /// <summary>Closes the sub-shape, joining its last point back to the first.</summary>
    Close,
}

/// <summary>A point of an outlined clip, in document coordinates, with the edge it carries into.</summary>
public readonly record struct ClipSegment(
    ClipEdge Edge,
    Double X,
    Double Y,
    Double Control1X,
    Double Control1Y,
    Double Control2X,
    Double Control2Y)
{
    /// <summary>Gets the corner of a shape that a straight edge is drawn to.</summary>
    public static ClipSegment To(ClipEdge edge, Double x, Double y) => new(edge, x, y, 0, 0, 0, 0);

    /// <summary>Gets the corner a curve is drawn to, through the two controls it was written with.</summary>
    public static ClipSegment Curving(Double x, Double y, Double control1X, Double control1Y, Double control2X, Double control2Y) =>
        new(ClipEdge.Curve, x, y, control1X, control1Y, control2X, control2Y);

    /// <summary>Gets the sub-shape-closing piece, which needs no point of its own.</summary>
    public static ClipSegment Closed => To(ClipEdge.Close, 0, 0);

    /// <summary>Gets the same piece of edge, moved by the given distance.</summary>
    public ClipSegment Shifted(Double x, Double y) =>
        new(Edge, X + x, Y + y, Control1X + x, Control1Y + y, Control2X + x, Control2Y + y);
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
/// <para>
/// Where the cut is an outline rather than a box, <see cref="Outline"/> says what the edge is and the
/// rectangle is only the area it covers: that is how a <c>clip-path</c> that named a
/// <c>polygon()</c> or a <c>path()</c> reaches the canvas. An oval needs no outline of its own, because a
/// rectangle whose four corners are each turned a quarter of the way round is the oval.
/// </para>
/// </remarks>
public sealed class PushClipCommand : PaintCommand
{
    internal PushClipCommand(Rect bounds, CornerRadii radii)
        : this(bounds, radii, null, false)
    {
    }

    internal PushClipCommand(Rect bounds, CornerRadii radii, IReadOnlyList<ClipSegment>? outline, Boolean evenOdd)
        : base(bounds)
    {
        Radii = radii;
        Outline = outline;
        EvenOdd = evenOdd;
    }

    /// <summary>Gets the corner radii of the clip, relative to <see cref="PaintCommand.Bounds"/>.</summary>
    public CornerRadii Radii { get; }

    /// <summary>Gets the edge the cut follows, where no rectangle is one.</summary>
    public IReadOnlyList<ClipSegment>? Outline { get; }

    /// <summary>Gets whether the inside of an outline is decided by the even-odd rule rather than by winding.</summary>
    public Boolean EvenOdd { get; }
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

/// <summary>
/// Draws everything that follows through one layer laid down at the given strength, until the matching
/// <see cref="PopLayerCommand"/>: what a box whose <c>opacity</c> is below one asks of its own picture and
/// of everything inside it (CSS Compositing 1 §9.1).
/// </summary>
/// <remarks>
/// The group is the meaning rather than an optimisation: the subtree is drawn into the layer at full
/// strength and the layer is then laid down as one, so two translucent children that overlap come to the
/// darker of the two and not to the product of both. Like the clip and the transform, the two commands are
/// always a balanced pair inside one list, which is also why nothing from inside escapes to a level above -
/// a box below full strength stands its subtree on a layer of its own.
/// </remarks>
public sealed class PushLayerCommand : PaintCommand
{
    internal PushLayerCommand(Rect bounds, Double alpha, BlendMode blend = BlendMode.Normal)
        : base(bounds)
    {
        Alpha = alpha;
        Blend = blend;
    }

    /// <summary>Gets how strongly the layer is laid down, from nought to one.</summary>
    public Double Alpha { get; }

    /// <summary>Gets how the layer is mixed with what is already drawn beneath it.</summary>
    public BlendMode Blend { get; }
}

/// <summary>
/// The separable and non-separable blend modes <c>mix-blend-mode</c> names (CSS Compositing 1 §5).
/// </summary>
public enum BlendMode : Byte
{
    Normal,
    Multiply,
    Screen,
    Overlay,
    Darken,
    Lighten,
    ColorDodge,
    ColorBurn,
    HardLight,
    SoftLight,
    Difference,
    Exclusion,
    Hue,
    Saturation,
    Color,
    Luminosity,
    PlusLighter,
}

/// <summary>Ends the layer the nearest preceding <see cref="PushLayerCommand"/> opened.</summary>
public sealed class PopLayerCommand : PaintCommand
{
    /// <summary>
    /// Creates the command that closes a layer. The area is carried along so that the command still
    /// describes where it applies, even though undoing a layer needs no geometry.
    /// </summary>
    internal PopLayerCommand(Rect bounds)
        : base(bounds)
    {
    }
}

/// <summary>A run of text drawn on a single baseline.</summary>
public sealed class TextCommand : PaintCommand
{
    /// <summary>
    /// Creates the command a run of text is drawn from, in whichever flow it is set: a vertical run
    /// advances down its bounds rather than across them, and which way its glyphs are turned decides
    /// whether the backend rotates the whole run or stacks the characters upright one under the other.
    /// </summary>
    /// <param name="decorations">
    /// The lines to draw across the run, with the colour and shape of the box that asked for each of them.
    /// </param>
    /// <param name="letterSpacing">
    /// The extra space the run carries after every character, which the backend adds to its advances
    /// so that what it draws lands where the layout measured it. It is zero for text that asks for
    /// none.
    /// </param>
    /// <param name="strokeWidth">
    /// The width the glyphs are ringed with, in pixels, and <paramref name="strokeColor"/> its colour.
    /// Both come from <c>-webkit-text-stroke</c>; a zero width is no stroke. The ring is drawn under the
    /// fill, and its width also widens the ink a background clipped to the text is kept to.
    /// </param>
    /// <param name="shadows">
    /// The shadows the run's text is drawn with, in the order the page wrote them. Each is the whole of the
    /// run again, moved by its own offsets and blurred by its own radius, laid under the one before it and
    /// under the glyphs last of all (CSS Text Decoration 4 §4.1).
    /// </param>
    internal TextCommand(
        Rect bounds,
        String text,
        Double baseline,
        FontStyle font,
        UInt32 color,
        IReadOnlyList<DecorationSource> decorations,
        WritingModeInfo flow,
        Double letterSpacing,
        Double strokeWidth = 0,
        UInt32 strokeColor = 0,
        Double justification = 0,
        Int32 justifies = 0,
        String expand = "auto",
        Double wordSpacing = 0,
        IReadOnlyList<DecorationPlacement>? placements = null,
        DecorationSkip skip = DecorationSkip.Auto,
        IReadOnlyList<Shadow>? shadows = null,
        Boolean? reversed = null,
        String? trims = null,
        String? autoSpaces = null,
        Double autoSpace = 0)
        : base(bounds)
    {
        Text = text;
        Trims = trims;
        AutoSpaces = autoSpaces;
        AutoSpaceRoom = autoSpace;
        Baseline = baseline;
        Font = font;
        Color = color;
        Decorations = decorations;
        Flow = flow;
        LetterSpacing = letterSpacing;
        StrokeWidth = strokeWidth;
        StrokeColor = strokeColor;
        Justification = justification;
        Justifies = justifies;
        Expand = expand;
        WordSpacing = wordSpacing;
        Placements = placements ?? Array.Empty<DecorationPlacement>();
        Skip = skip;
        Shadows = shadows ?? Array.Empty<Shadow>();
        Reversed = reversed;
    }

    /// <summary>
    /// Creates the command a page margin box's text is drawn from: one horizontal run, undecorated,
    /// which is all a page number standing in a margin asks for (CSS Page 3 §5.3).
    /// </summary>
    public static TextCommand PageMargin(Rect bounds, String text, Double baseline, FontStyle font, UInt32 color) =>
        new(bounds, text, baseline, font, color, Array.Empty<DecorationSource>(), WritingModeInfo.Horizontal, 0);

    public String Text { get; }

    /// <summary>
    /// Gets which characters of <see cref="Text"/> were set half-width, one code per UTF-16 unit:
    /// <c>'\0'</c> untouched, <c>'\1'</c> opening, <c>'\2'</c> closing, <c>'\3'</c> middle dot.
    /// Null when the run has nothing to trim (CSS Text 4 §8.5).
    /// </summary>
    public String? Trims { get; }

    /// <summary>
    /// Gets which boundaries of <see cref="Text"/> the property widened, one code per UTF-16 unit:
    /// <c>''</c> when a space follows the unit, <c>''</c> when the unit is a space the boundary
    /// takes the place of. Null when the run spaces nothing (CSS Text 4 §8.4).
    /// </summary>
    public String? AutoSpaces { get; }

    /// <summary>Gets the room one widened boundary adds, in pixels.</summary>
    public Double AutoSpaceRoom { get; }

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

    /// <summary>
    /// Gets the lines to draw across this run, in the order they are painted: the decorating box nearest
    /// the run's text is last, so its line lies over the ones the boxes around it asked for. Each carries
    /// its own kinds, shape and colour, because a decoration an ancestor asked for is drawn in the
    /// ancestor's colour and not in the colour of the glyphs it runs across
    /// (CSS Text Decoration 4 §2.3, §5.1).
    /// </summary>
    public IReadOnlyList<DecorationSource> Decorations { get; }

    /// <summary>
    /// Gets how this run's own ink is treated by the lines it is drawn under: <c>auto</c>, <c>none</c> or
    /// <c>all</c>, as the box that holds the text was set. It is the text's answer rather than the
    /// decorating box's (CSS Text Decoration 4 §2.10.4) - what is skipped is where this run's glyphs stand,
    /// and a marker, whose ink is its own, is measured against its own value.
    /// </summary>
    public DecorationSkip Skip { get; }

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

    /// <summary>
    /// Gets the room <c>word-spacing</c> adds after each word separator the run holds: the layout
    /// counted the separators when it gave the run its width, and the glyphs have to be set out where
    /// that width says they stand (CSS Text 3 §7.1).
    /// </summary>
    public Double WordSpacing { get; }

    /// <summary>
    /// Gets where each of <see cref="Decorations"/> stands on the line this run is part of, in the same
    /// order and as many of them: one decorating box draws its underlines and overlines at one position
    /// and one thickness along the whole of a line, so the answer is taken from all the text the box
    /// decorates there rather than from this run alone (CSS Text Decoration 4 §2.5, §2.9). A run with
    /// nothing to say draws each line where its own face puts it.
    /// </summary>
    public IReadOnlyList<DecorationPlacement> Placements { get; }

    /// <summary>
    /// Gets the shadows the run's text is drawn with, in the order the page wrote them: the first of them
    /// is the one on top, so a backend lays them last-first, each under the glyphs it shadows. Empty, which
    /// is what a box with no <c>text-shadow</c> of its own or above it comes to, draws the text plain.
    /// </summary>
    public IReadOnlyList<Shadow> Shadows { get; }

    /// <summary>
    /// Gets whether the run's glyphs are drawn from the far end of its bounds, or null to let the flow
    /// decide. The shaper reads a run's direction off its characters, so a run the bidi algorithm put at
    /// a level they do not agree with is turned round by the backend instead.
    /// </summary>
    public Boolean? Reversed { get; }
}

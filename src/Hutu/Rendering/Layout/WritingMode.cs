using System;

namespace Hutu.Rendering.Layout;

/// <summary>
/// The direction the inline and the block axis run, as the <c>writing-mode</c> property asks.
/// </summary>
/// <remarks>
/// Layout is written in terms of the two axes - the inline axis a line runs along, and the block axis
/// the lines advance along - so this is the single place that knows how they land on the physical
/// axes. Horizontal writing is the default and puts the inline axis on x and the block axis on y; the
/// vertical modes swap the two, which is what makes text run down a column and the columns advance
/// sideways.
/// </remarks>
internal enum WritingMode
{
    /// <summary>The inline axis runs along x, the block axis down the page. The default.</summary>
    HorizontalTb,

    /// <summary>The inline axis runs down a column, the block axis from right to left.</summary>
    VerticalRl,

    /// <summary>The inline axis runs down a column, the block axis from left to right.</summary>
    VerticalLr,

    /// <summary>Like <see cref="VerticalRl"/>, with every glyph turned a quarter turn clockwise.</summary>
    SidewaysRl,

    /// <summary>Like <see cref="VerticalLr"/>, with every glyph turned a quarter turn counter-clockwise.</summary>
    SidewaysLr,
}

/// <summary>
/// The direction a line reads in, as the <c>direction</c> property and the <c>dir</c> attribute ask.
/// </summary>
/// <remarks>
/// It is separate from <see cref="WritingMode"/> on purpose: the writing mode decides which physical
/// axis the line runs along, and the direction decides which way along that axis it reads. A row of
/// Arabic and a row of English share a writing mode and differ only here.
/// </remarks>
internal enum TextDirection
{
    /// <summary>The line reads from the start of the inline axis to its end.</summary>
    LeftToRight,

    /// <summary>The line reads from the end of the inline axis back to its start.</summary>
    RightToLeft,
}

/// <summary>How the glyphs of vertical text are turned, as <c>text-orientation</c> asks.</summary>
public enum TextOrientation
{
    /// <summary>CJK and other wide characters stay upright, the rest is turned a quarter turn.</summary>
    Mixed,

    /// <summary>Every character stays upright, one under the other, however many a column holds.</summary>
    Upright,

    /// <summary>Every character is turned a quarter turn, so the run reads sideways.</summary>
    Sideways,
}

/// <summary>
/// A writing mode together with the text orientation and the direction, and the mapping between the
/// logical axes and the physical ones. It is a value type built and discarded freely, so nothing in
/// the layout path pays an allocation for asking where a box goes.
/// </summary>
/// <remarks>
/// The logical frame is measured from the containing block's content box: an <em>inline</em> offset and
/// size along the axis the text runs, and a <em>block</em> offset and size along the axis the lines
/// advance. Both are signed, so an offset always grows in the reading direction and the caller never
/// has to know whether that is rightward, leftward, downward or upward. A row's sign comes from the
/// direction the text reads in, and a column's from the writing mode.
/// <para>
/// What leaves the engine with a text run is four questions: whether the run is vertical, how its
/// glyphs are turned, whether <c>text-combine-upright</c> wants one box, and which way along the line
/// it advances. The axis arithmetic that places a box is asked before the run is drawn, so the answers
/// are already physical rectangles by the time a backend sees them.
/// </para>
/// </remarks>
public readonly struct WritingModeInfo : IEquatable<WritingModeInfo>
{
    internal WritingModeInfo(
        WritingMode mode,
        TextOrientation orientation = TextOrientation.Mixed,
        Boolean combinesUpright = false,
        TextDirection direction = TextDirection.LeftToRight)
    {
        Mode = mode;
        Orientation = orientation;
        CombinesUpright = combinesUpright;
        Direction = direction;
    }

    /// <summary>
    /// Gets whether two requests set text the same way, which is the four decisions the struct carries.
    /// </summary>
    /// <remarks>
    /// Typed rather than left to the struct's own <c>Equals(Object)</c>: the line asks it of every run it
    /// puts on it, and the inherited form boxes both sides and compares the fields through reflection.
    /// Measured on a page of vertical CJK, where it was a twentieth of the layout's own CPU.
    /// </remarks>
    public Boolean Equals(WritingModeInfo other) =>
        Mode == other.Mode &&
        Orientation == other.Orientation &&
        CombinesUpright == other.CombinesUpright &&
        Direction == other.Direction;

    public override Boolean Equals(Object? obj) => obj is WritingModeInfo other && Equals(other);

    /// <summary>The writing mode plain horizontal text uses, which is what every existing box gets.</summary>
    internal static WritingModeInfo Horizontal { get; } = new(WritingMode.HorizontalTb);

    internal WritingMode Mode { get; }

    /// <summary>Gets how the glyphs are turned once the mode is vertical.</summary>
    public TextOrientation Orientation { get; }

    /// <summary>Gets whether <c>text-combine-upright: all</c> asks short runs to be set as one upright box.</summary>
    public Boolean CombinesUpright { get; }

    /// <summary>
    /// Gets the direction the line reads in. It is the <c>direction</c> property, which the
    /// <c>dir</c> attribute stands in for when the page wrote no rule.
    /// </summary>
    internal TextDirection Direction { get; }

    /// <summary>Gets whether the line reads from the end of the inline axis back to its start.</summary>
    internal Boolean IsRightToLeft => Direction == TextDirection.RightToLeft;

    /// <summary>
    /// Gets whether the writing mode itself turns every glyph a quarter turn, which is what the
    /// <c>sideways-*</c> modes do. Their text is read along the page rather than down it, so a rule
    /// beside it follows the row rules rather than the column ones.
    /// </summary>
    public Boolean IsSideways => Mode is WritingMode.SidewaysRl or WritingMode.SidewaysLr;

    /// <summary>
    /// Gets whether the text on this line stands against the way the line reads. A row does it by its
    /// nature: its glyphs go left to right while the line runs back from the right edge, and the drawing
    /// follows the glyphs. A column turns its glyphs with the line, so the direction has to be told to
    /// the drawing as well - which is what a right-to-left row of direction asks of it.
    /// </summary>
    public Boolean SetsTextBackwards => IsVertical && IsRightToLeft;

    /// <summary>Gets whether the block axis runs from the right edge back to the left one, which is
    /// what a vertical-rl box does.</summary>
    /// <summary>
    /// Gets whether the block axis runs from the right edge back to the left one, which decides the side
    /// a rule stood under the text falls on: a line asked to sit on the text's under side stands at the
    /// left edge of a right-to-left column and at the right edge of a left-to-right one (CSS Text
    /// Decoration 4 §2.7). Public because the drawing asks it across the backend boundary.
    /// </summary>
    public Boolean BlockIsRightToLeft => Mode is WritingMode.VerticalRl or WritingMode.SidewaysRl;

    /// <summary>Gets whether the inline axis runs down the page rather than along it.</summary>
    public Boolean IsVertical => Mode != WritingMode.HorizontalTb;

    /// <summary>
    /// Gets whether a quarter turn of the glyphs goes counter-clockwise. Only <c>sideways-lr</c> turns
    /// them that way: every other vertical mode turns the characters mixed text sets sideways a quarter
    /// turn clockwise, whichever way the line reads, so a right-to-left column keeps the same turn a
    /// left-to-right one does and only its character order changes (CSS Writing Modes 3 §5.1, measured
    /// in Chrome on the vertical `dir=rtl` pages).
    /// </summary>
    public Boolean TurnsCounterClockwise => Mode == WritingMode.SidewaysLr;

    /// <summary>
    /// Gets whether the mode turns every glyph by itself. <c>sideways-rl</c> and <c>sideways-lr</c> do,
    /// whatever <c>text-orientation</c> says.
    /// </summary>
    internal Boolean IsSidewaysMode => Mode is WritingMode.SidewaysRl or WritingMode.SidewaysLr;

    /// <summary>Gets the sign an inline offset grows by. A row reads leftward under
    /// <c>direction: rtl</c> and a column upward, and the direction turns each of them back: a column
    /// of a right-to-left row of direction runs from the foot of the page to its head, which is the
    /// same reversal a row gets (CSS Writing Modes 3 §4.3).</summary>
    public Int32 InlineSign
    {
        get
        {
            // The mode decides which way up the line lies - only `sideways-lr` writes it from the foot
            // to the head - and the direction the text reads in turns it back on either axis.
            var forward = IsVertical && Mode == WritingMode.SidewaysLr ? -1 : 1;

            return IsRightToLeft ? -forward : forward;
        }
    }

    /// <summary>Gets the sign a block offset grows by: only the two right-to-left modes grow leftward.</summary>
    internal Int32 BlockSign => Mode is WritingMode.VerticalRl or WritingMode.SidewaysRl ? -1 : 1;

    /// <summary>
    /// Gets whether the page begins at the right edge of the document, so that how far along a reader
    /// is counts from there rather than from the left.
    /// </summary>
    /// <remarks>
    /// Two quite different things put the beginning on the right: a vertical page's columns march
    /// leftward from it, and a line of a right-to-left row reads from it. What they share is the only
    /// thing a scroller asks about - which edge x is measured from.
    /// </remarks>
    internal Boolean StartsAtTheRight => IsVertical ? BlockSign < 0 : IsRightToLeft;

    /// <summary>
    /// Gets whether the page begins at the bottom edge of the document, which is what a vertical mode
    /// that reads up the page asks for. Every other mode begins at the top.
    /// </summary>
    internal Boolean StartsAtTheBottom => IsVertical && InlineSign < 0;

    /// <summary>Gets the size a physical box has along the axis the text runs.</summary>
    internal Double InlineExtent(Double width, Double height) => IsVertical ? height : width;

    /// <summary>Gets the size a physical box has along the axis the lines advance.</summary>
    internal Double BlockExtent(Double width, Double height) => IsVertical ? width : height;

    /// <summary>Gets the physical size a logical pair of extents adds up to.</summary>
    internal Size SizeOf(Double inlineExtent, Double blockExtent) =>
        IsVertical ? new Size(blockExtent, inlineExtent) : new Size(inlineExtent, blockExtent);

    /// <summary>
    /// Gets the coordinate the block axis starts at on the physical axis - the right edge of a
    /// right-to-left content box, its left edge otherwise, and the top edge in horizontal writing.
    /// </summary>
    internal Double BlockStart(Rect contentBox) => IsVertical
        ? BlockSign < 0 ? contentBox.Right : contentBox.X
        : contentBox.Y;

    /// <summary>Gets the coordinate the inline axis starts at on the physical axis.</summary>
    internal Double InlineStart(Rect contentBox) => IsVertical
        ? InlineSign < 0 ? contentBox.Bottom : contentBox.Y
        : InlineSign < 0 ? contentBox.Right : contentBox.X;

    /// <summary>
    /// Maps a logical frame - an offset and a size along each axis, measured from the containing
    /// block's content box - onto the physical rectangle it occupies.
    /// </summary>
    internal Rect Map(Rect contentBox, Double inlineOffset, Double inlineSize, Double blockOffset, Double blockSize)
    {
        if (!IsVertical)
        {
            // A right-to-left row measures its inline offset from the right edge, so the first run of
            // the line lands against that edge and the runs after it march back toward the start.
            var x = InlineSign < 0
                ? contentBox.Right - inlineOffset - inlineSize
                : contentBox.X + inlineOffset;

            return new Rect(x, contentBox.Y + blockOffset, inlineSize, blockSize);
        }

        var columnX = BlockSign < 0
            ? contentBox.Right - blockOffset - blockSize
            : contentBox.X + blockOffset;

        var y = InlineSign < 0
            ? contentBox.Bottom - inlineOffset - inlineSize
            : contentBox.Y + inlineOffset;

        return new Rect(columnX, y, blockSize, inlineSize);
    }

    /// <summary>
    /// Turns a distance measured from the block-start edge into one measured from the box's left edge,
    /// which is the edge the painter draws a column's baseline from.
    /// </summary>
    /// <remarks>
    /// A row's baseline stands below the top edge by the ascent, and its glyphs are not turned. A
    /// column's glyphs are turned about the baseline: the quarter turn every vertical mode but
    /// `sideways-lr` makes is clockwise, so the ascent reaches towards the right and the baseline
    /// stands the descent in from the left; `sideways-lr` turns them the other way and the ascent
    /// reaches left. The side the ascent reaches towards is the side the glyphs turn away from, so it
    /// follows the turn rather than the direction the line reads in.
    /// </remarks>
    internal Double BlockOffsetFromLeft(Double blockSize, Double fromBlockStart) =>
        IsVertical
            ? TurnsCounterClockwise ? fromBlockStart : blockSize - fromBlockStart
            : fromBlockStart;

    /// <summary>
    /// Splits a box's margins into the pair that opens and closes it along the line it sits on.
    /// </summary>
    /// <remarks>
    /// The inline axis is the one a line runs along, so a box in a row is opened by its left margin
    /// and one down a column by its top - or by the other of each pair where the line reads the other
    /// way. It is the same mapping that decides which of the two pairs a parent's cursor walks past:
    /// the margins on this axis belong to the box, and the ones on the
    /// <see cref="BlockMargins(Thickness)">block axis</see> to whoever stacks it.
    /// </remarks>
    internal (Double Start, Double End) InlineMargins(Thickness margin) =>
        IsVertical
            ? InlineSign > 0 ? (margin.Top, margin.Bottom) : (margin.Bottom, margin.Top)
            : InlineSign > 0 ? (margin.Left, margin.Right) : (margin.Right, margin.Left);

    /// <summary>
    /// Splits a box's margins into the pair that opens and closes it across the lines, which is the
    /// axis the next line advances along.
    /// </summary>
    internal (Double Start, Double End) BlockMargins(Thickness margin) =>
        IsVertical
            ? BlockSign > 0 ? (margin.Left, margin.Right) : (margin.Right, margin.Left)
            : (margin.Top, margin.Bottom);

    /// <summary>
    /// Gets the physical edge a stated float side names: the logical spellings follow the line, so the
    /// side the line begins at is the left one in a row that reads rightwards and the top one in a
    /// column that reads downwards.
    /// </summary>
    internal FloatSide FloatSideOf(FloatSide side) => side switch
    {
        FloatSide.InlineStart => InlineSign > 0 ? FloatSide.Left : FloatSide.Right,
        FloatSide.InlineEnd => InlineSign > 0 ? FloatSide.Right : FloatSide.Left,
        _ => side,
    };

    /// <summary>
    /// Gets whether a stated float side names the end of the line the text begins at, which is where a
    /// column pins the floats of that side.
    /// </summary>
    /// <remarks>
    /// The two pairs name the ends of different things. A logical side is an end of the line, so a
    /// direction that turns the line turns it with it; a physical one is line-left or line-right, which
    /// in a column is the head or the foot of the page whichever way the text runs - measured in
    /// Chrome, where a <c>vertical-rl</c> and a <c>vertical-lr</c> column both keep their
    /// <c>float: left</c> box at the top and move neither with the direction. The two therefore agree
    /// only while the line reads in the direction the physical name points.
    /// </remarks>
    internal Boolean FloatsAtTheInlineStart(FloatSide side) => side switch
    {
        FloatSide.InlineStart => true,
        FloatSide.InlineEnd => false,
        FloatSide.Left => InlineSign > 0,
        FloatSide.Right => InlineSign < 0,
        _ => false,
    };

    /// <summary>
    /// Gets whether a character stays upright. <c>mixed</c> keeps the wide characters - CJK, kana and
    /// the fullwidth forms - upright and turns the rest, which is what makes Latin read sideways in a
    /// column while the text around it does not.
    /// </summary>
    internal Boolean IsUpright(Int32 codePoint)
    {
        if (IsSidewaysMode || Orientation == TextOrientation.Sideways)
        {
            return false;
        }

        return Orientation == TextOrientation.Upright || TextChunker.IsUpright(codePoint);
    }

    internal Boolean IsUpright(Char character) => IsUpright((Int32)character);
}

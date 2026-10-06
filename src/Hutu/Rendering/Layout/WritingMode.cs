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
public enum WritingMode
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
/// A writing mode together with the text orientation, and the mapping between the logical axes and the
/// physical ones. It is a value type built and discarded freely, so nothing in the layout path pays an
/// allocation for asking where a box goes.
/// </summary>
/// <remarks>
/// The logical frame is measured from the containing block's content box: an <em>inline</em> offset and
/// size along the axis the text runs, and a <em>block</em> offset and size along the axis the lines
/// advance. Both are signed by the mode, so an offset always grows in the reading direction and the
/// caller never has to know whether that is rightward, leftward, downward or upward.
/// </remarks>
public readonly struct WritingModeInfo
{
    public WritingModeInfo(WritingMode mode, TextOrientation orientation = TextOrientation.Mixed, Boolean combinesUpright = false)
    {
        Mode = mode;
        Orientation = orientation;
        CombinesUpright = combinesUpright;
    }

    /// <summary>The writing mode plain horizontal text uses, which is what every existing box gets.</summary>
    public static WritingModeInfo Horizontal { get; } = new(WritingMode.HorizontalTb);

    public WritingMode Mode { get; }

    /// <summary>Gets how the glyphs are turned once the mode is vertical.</summary>
    public TextOrientation Orientation { get; }

    /// <summary>Gets whether <c>text-combine-upright: all</c> asks short runs to be set as one upright box.</summary>
    public Boolean CombinesUpright { get; }

    /// <summary>Gets whether the inline axis runs down the page rather than along it.</summary>
    public Boolean IsVertical => Mode != WritingMode.HorizontalTb;

    /// <summary>
    /// Gets whether the mode turns every glyph by itself. <c>sideways-rl</c> and <c>sideways-lr</c> do,
    /// whatever <c>text-orientation</c> says.
    /// </summary>
    public Boolean IsSidewaysMode => Mode is WritingMode.SidewaysRl or WritingMode.SidewaysLr;

    /// <summary>Gets the sign an inline offset grows by: text always reads downward, but <c>sideways-lr</c> reads upward.</summary>
    public Int32 InlineSign => Mode == WritingMode.SidewaysLr ? -1 : 1;

    /// <summary>Gets the sign a block offset grows by: only the two right-to-left modes grow leftward.</summary>
    public Int32 BlockSign => Mode is WritingMode.VerticalRl or WritingMode.SidewaysRl ? -1 : 1;

    /// <summary>Gets the size a physical box has along the axis the text runs.</summary>
    public Double InlineExtent(Double width, Double height) => IsVertical ? height : width;

    /// <summary>Gets the size a physical box has along the axis the lines advance.</summary>
    public Double BlockExtent(Double width, Double height) => IsVertical ? width : height;

    /// <summary>Gets the physical size a logical pair of extents adds up to.</summary>
    public Size SizeOf(Double inlineExtent, Double blockExtent) =>
        IsVertical ? new Size(blockExtent, inlineExtent) : new Size(inlineExtent, blockExtent);

    /// <summary>
    /// Gets the coordinate the block axis starts at on the physical axis - the right edge of a
    /// right-to-left content box, its left edge otherwise, and the top edge in horizontal writing.
    /// </summary>
    public Double BlockStart(Rect contentBox) => IsVertical
        ? BlockSign < 0 ? contentBox.Right : contentBox.X
        : contentBox.Y;

    /// <summary>Gets the coordinate the inline axis starts at on the physical axis.</summary>
    public Double InlineStart(Rect contentBox) => IsVertical
        ? InlineSign < 0 ? contentBox.Bottom : contentBox.Y
        : contentBox.X;

    /// <summary>
    /// Maps a logical frame - an offset and a size along each axis, measured from the containing
    /// block's content box - onto the physical rectangle it occupies.
    /// </summary>
    public Rect Map(Rect contentBox, Double inlineOffset, Double inlineSize, Double blockOffset, Double blockSize)
    {
        if (!IsVertical)
        {
            return new Rect(
                contentBox.X + inlineOffset,
                contentBox.Y + blockOffset,
                inlineSize,
                blockSize);
        }

        var x = BlockSign < 0
            ? contentBox.Right - blockOffset - blockSize
            : contentBox.X + blockOffset;

        var y = InlineSign < 0
            ? contentBox.Bottom - inlineOffset - inlineSize
            : contentBox.Y + inlineOffset;

        return new Rect(x, y, blockSize, inlineSize);
    }

    /// <summary>
    /// Turns a distance measured from the block-start edge into one measured from the box's left edge,
    /// which is the edge the painter draws a column's baseline from.
    /// </summary>
    /// <remarks>
    /// A right-to-left mode starts at the right edge, so everything measured from there has to be
    /// mirrored to be expressed from the left. Horizontal text is unaffected: its block-start edge
    /// already is the top one, and the painter measures the baseline down from it.
    /// </remarks>
    public Double BlockOffsetFromLeft(Double blockSize, Double fromBlockStart) =>
        BlockSign < 0 ? blockSize - fromBlockStart : fromBlockStart;

    /// <summary>
    /// Gets whether a character stays upright. <c>mixed</c> keeps the wide characters - CJK, kana and
    /// the fullwidth forms - upright and turns the rest, which is what makes Latin read sideways in a
    /// column while the text around it does not.
    /// </summary>
    public Boolean IsUpright(Char character)
    {
        if (IsSidewaysMode || Orientation == TextOrientation.Sideways)
        {
            return false;
        }

        return Orientation == TextOrientation.Upright || TextChunker.IsUpright(character);
    }
}

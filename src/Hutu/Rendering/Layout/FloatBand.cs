using System;

namespace Hutu.Rendering.Layout;

/// <summary>
/// The floats a block's line boxes have to stand aside for, together with where that block's content
/// box begins inside them.
/// </summary>
/// <remarks>
/// The tracks speak the frame of the container that holds the floats, because that is the box they
/// were pinned to the edges of; a line's room is asked at the height of the line, which is written in
/// the frame of the block the line belongs to. Those two frames are the same box's content corner
/// plus a distance down and in, so carrying the two offsets is enough to translate between them -
/// and a block whose container holds no floats of its own inherits the band of the nearest ancestor
/// that does, with its own offset added, which is what makes a paragraph inside a floated column read
/// beside a picture it never mentions.
/// <para>
/// A line's room is asked at a point rather than over its height: the lines of a block stack downward
/// from the top, so a line whose top is below a float can never overlap it, and one whose top is
/// inside it always does. That makes the top edge alone enough to decide how far the line reaches.
/// </para>
/// <para>
/// When a block has floats of its own, its children are told about those and not about the ancestors'
/// as well. Both sets would have to be kept in one frame, which means carrying the ancestor's offsets
/// through the child's tracks as well - and a container that holds a float is already the box a reader
/// would call a layout of its own.
/// </para>
/// </remarks>
internal sealed class FloatBand
{
    public FloatBand(FloatTracks tracks, Double originY, Double left)
    {
        Tracks = tracks;
        OriginY = originY;
        Left = left;
    }

    /// <summary>Gets the floats the lines of this block stand aside for.</summary>
    public FloatTracks Tracks { get; }

    /// <summary>Gets where the block's content box begins, along the axis the floats stack down.</summary>
    public Double OriginY { get; }

    /// <summary>Gets where the block's content box begins, along the axis the floats are pinned to.</summary>
    public Double Left { get; }

    /// <summary>
    /// Gets how far a line standing <paramref name="top"/> down from the block's content box may reach,
    /// and where it has to begin: the inset is given back in the block's own frame, which is where the
    /// line box and every run on it are placed.
    /// </summary>
    public Double Room(Double top, Double maxInline, out Double inset)
    {
        var (left, right) = Tracks.FreeSpanAt(OriginY + top, 0.001);

        inset = Math.Max(0, left - Left);

        var room = Math.Min(maxInline, right - Left) - inset;

        return room > 0 ? room : 0;
    }

    /// <summary>
    /// Gets the span the band's floats leave at the block's own content edge, which is where a box that
    /// keeps its own formatting context has to stand clear of.
    /// </summary>
    public (Double Left, Double Right) FreeSpan() => Tracks.FreeSpanAt(OriginY, 0.001);

    /// <summary>
    /// Gets the height a line with no room left to it in drops to: below every float standing in the
    /// band, which is the rule the specification gives for a line box too narrow to hold anything.
    /// </summary>
    public Double Below(Double top) =>
        Math.Max(top, Tracks.ClearBelow(ClearSide.Both, OriginY + top) - OriginY);
}

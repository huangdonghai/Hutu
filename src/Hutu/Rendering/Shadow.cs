using System;

namespace Hutu.Rendering;

/// <summary>
/// One shadow a box or a run of text is drawn with, as the declaration came to: the two offsets and the
/// two radii in pixels, the colour it is drawn in, and whether it lies inside the box rather than under
/// it.
/// </summary>
/// <remarks>
/// Nothing here is geometry a backend has to work out - the shadow's own rectangle is settled by the
/// painter, which is the one that knows the box's size and its corner radii. What the style keeps is the
/// declaration's answer, resolved once: a length in <c>em</c> is the element's own font size and a colour
/// left out is the element's own ink (CSS Backgrounds 3 §6.1).
/// <para>
/// The list a property holds is kept in the order the author wrote it, and that order is front-to-back:
/// the first shadow of a <c>box-shadow</c> ends up on top of the others (§6.1.3), so whoever paints them
/// paints them last-first.
/// </para>
/// </remarks>
public sealed record Shadow
{
    /// <summary>Gets how far to the right of the box the shadow stands, in pixels; negative is to the left.</summary>
    public Double OffsetX { get; internal init; }

    /// <summary>Gets how far below the box the shadow stands, in pixels; negative is above.</summary>
    /// <remarks>
    /// The two offsets are physical, as CSS makes them: a positive one moves the shadow down and right on
    /// the page whatever direction the text it shadows runs in.
    /// </remarks>
    public Double OffsetY { get; internal init; }

    /// <summary>
    /// Gets the blur radius in pixels, of which the backend makes a Gaussian whose standard deviation is
    /// half it (CSS Backgrounds 3 §6.1.2). Zero, the value a declaration that names no blur comes to, is a
    /// sharp edge.
    /// </summary>
    public Double Blur { get; internal init; }

    /// <summary>
    /// Gets the spread distance in pixels: positive grows the shape the shadow is drawn from, negative
    /// shrinks it. Only <c>box-shadow</c> has one - the grammar of a text shadow leaves it out, along with
    /// <see cref="Inset"/>.
    /// </summary>
    public Double Spread { get; internal init; }

    /// <summary>Gets the colour the shadow is drawn in, packed as ARGB.</summary>
    /// <remarks>
    /// Where <see cref="FollowsInk"/> says the declaration left the colour to the element, this is the answer
    /// the ink of the box that was read - which is the box the declaration was written on, and not
    /// necessarily the box the shadow is drawn with.
    /// </remarks>
    public UInt32 Color { get; internal init; }

    /// <summary>
    /// Gets whether the shadow asks for the ink of the box it is drawn with, either because the declaration
    /// named no colour or because it wrote the keyword that asks for it (CSS Backgrounds 3 §6.1: "omitted
    /// colors default to currentColor").
    /// </summary>
    /// <remarks>
    /// The asking is what travels down a tree that inherits the shadow, and the answer is each box's own: a
    /// shadow written on a red element and inherited by a green one is drawn green. Settling the colour
    /// where the declaration was read would bring the red down with it.
    /// </remarks>
    internal Boolean FollowsInk { get; init; }

    /// <summary>
    /// Gets whether the shadow lies inside the box - the canvas cast onto the box as if it were cut out of
    /// it and stood behind - rather than the box cast onto the canvas (CSS Backgrounds 3 §6.1).
    /// </summary>
    public Boolean Inset { get; internal init; }

    /// <summary>
    /// Gets the shadow grown by the box's zoom: the two offsets, the blur and the spread are lengths the
    /// page wrote, and a scale reaches every length of a box it is put on. The list is held as the
    /// declaration came to and grown on the way out, so the same shadow travels to a box at two and to a
    /// box at one.
    /// </summary>
    internal Shadow Scaled(Double factor) =>
        factor == 1
            ? this
            : this with
            {
                OffsetX = OffsetX * factor,
                OffsetY = OffsetY * factor,
                Blur = Blur * factor,
                Spread = Spread * factor,
            };
}

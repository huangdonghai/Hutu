using Hutu.Rendering.Layout;
using System;

namespace Hutu.Rendering;

/// <summary>
/// The rectangle an absolutely positioned box cuts itself to, as its <c>clip</c> declaration states it:
/// four offsets from the top-left corner of the border box, any of which may be left to <c>auto</c> -
/// the box's own edge on that side.
/// </summary>
/// <remarks>
/// The rectangle itself is the painter's to work out, since it needs the box as laid out; what the style
/// keeps is the four distances, folded to pixels in the font of the element that wrote them.
/// <para>
/// The legacy grammar is peculiar in one way that has to survive the reading: all four offsets run from
/// the <em>top-left</em> corner, so <c>right</c> and <c>bottom</c> are the far edges' distances from that
/// corner rather than distances inward from the edges they name (CSS 2.1 §11.1.1, kept by CSS Masking 1
/// §1.3). Measured in Chrome on 2026-10-22: <c>rect(10px, 180px, 190px, 20px)</c> on a box whose border
/// box is 200x200 leaves the band x 20 to 180, y 10 to 190.
/// </para>
/// </remarks>
internal readonly struct ClipShape
{
    /// <summary>
    /// Creates the shape a declaration came to, in pixels. A distance left to <c>auto</c> is the edge of
    /// the box on that side, which is the absence of a number rather than zero.
    /// </summary>
    internal ClipShape(Double? top, Double? right, Double? bottom, Double? left)
    {
        Top = top;
        Right = right;
        Bottom = bottom;
        Left = left;
    }

    /// <summary>Gets how far below the top of the border box the clip begins, or null for the near edge.</summary>
    internal Double? Top { get; }

    /// <summary>Gets how far right of the top-left corner the clip ends, or null for the far edge.</summary>
    internal Double? Right { get; }

    /// <summary>Gets how far below the top-left corner the clip ends, or null for the far edge.</summary>
    internal Double? Bottom { get; }

    /// <summary>Gets how far right of the top-left corner the clip begins, or null for the near edge.</summary>
    internal Double? Left { get; }

    /// <summary>
    /// Gets the rectangle the box and everything it paints is cut to. Edges left to <c>auto</c> are the
    /// box's own, and a shape whose far edge falls before its near one leaves nothing to show: the offsets
    /// run along the two axes from the same corner, so a <c>right</c> smaller than the <c>left</c> is a
    /// rectangle of negative width rather than a wider clip.
    /// </summary>
    internal Rect ClipOf(Rect borderBox)
    {
        var left = borderBox.X + (Left ?? 0);
        var top = borderBox.Y + (Top ?? 0);
        var width = borderBox.X + (Right ?? borderBox.Width) - left;
        var height = borderBox.Y + (Bottom ?? borderBox.Height) - top;

        return width <= 0 || height <= 0
            ? Rect.Empty
            : new Rect(left, top, width, height);
    }
}

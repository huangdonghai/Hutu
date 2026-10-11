using System;

namespace Hutu.Rendering;

/// <summary>One corner of <c>border-radius</c>, as written and with its units intact.</summary>
/// <remarks>
/// The two axes are separate values because a corner may be elliptical: the radius before the slash is
/// the horizontal one and the radius after it the vertical one. Both survive as
/// <see cref="StyleLength"/>, percentage included, because a percentage radius is relative to the size
/// of the box itself and only the layout pass knows that size.
/// </remarks>
public readonly struct StyleCornerRadius : IEquatable<StyleCornerRadius>
{
    public StyleCornerRadius(StyleLength horizontal, StyleLength vertical)
    {
        Horizontal = horizontal;
        Vertical = vertical;
    }

    /// <summary>Gets a square corner, which is what an unset radius is.</summary>
    public static StyleCornerRadius Zero => default;

    public StyleLength Horizontal { get; }

    public StyleLength Vertical { get; }

    /// <summary>
    /// Gets whether two corners are the same ellipse: the same radius on each of its two axes.
    /// </summary>
    public Boolean Equals(StyleCornerRadius other) =>
        Horizontal.Equals(other.Horizontal) && Vertical.Equals(other.Vertical);

    public override Boolean Equals(Object? obj) => obj is StyleCornerRadius other && Equals(other);

    public override Int32 GetHashCode() => HashCode.Combine(Horizontal, Vertical);
}

/// <summary>The four corners of <c>border-radius</c>, in visual order around the box.</summary>
public readonly struct StyleCornerRadii : IEquatable<StyleCornerRadii>
{
    public StyleCornerRadii(
        StyleCornerRadius topLeft,
        StyleCornerRadius topRight,
        StyleCornerRadius bottomRight,
        StyleCornerRadius bottomLeft)
    {
        TopLeft = topLeft;
        TopRight = topRight;
        BottomRight = bottomRight;
        BottomLeft = bottomLeft;
    }

    /// <summary>Gets four square corners, which is what an unset <c>border-radius</c> is.</summary>
    public static StyleCornerRadii None => default;

    public StyleCornerRadius TopLeft { get; }

    public StyleCornerRadius TopRight { get; }

    public StyleCornerRadius BottomRight { get; }

    public StyleCornerRadius BottomLeft { get; }

    /// <summary>
    /// Gets whether two sets of corners are the same four corners, in the same visual order.
    /// </summary>
    public Boolean Equals(StyleCornerRadii other) =>
        TopLeft.Equals(other.TopLeft) &&
        TopRight.Equals(other.TopRight) &&
        BottomRight.Equals(other.BottomRight) &&
        BottomLeft.Equals(other.BottomLeft);

    public override Boolean Equals(Object? obj) => obj is StyleCornerRadii other && Equals(other);

    public override Int32 GetHashCode() => HashCode.Combine(TopLeft, TopRight, BottomRight, BottomLeft);
}

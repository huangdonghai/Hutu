using System;

namespace Hutu.Rendering.Layout;

/// <summary>One corner of a rounded box: a radius per axis, in pixels.</summary>
/// <remarks>
/// The two axes are kept apart because a radius is an ellipse, which is how <c>border-radius: 40px / 10px</c>
/// produces a flattened corner. A corner with a zero on either axis is square - that is what the spec
/// asks for, and normalising it here keeps every consumer from having to test for it.
/// <para>
/// A corner is the layout's answer to a declared radius; a backend is told the four of them and draws
/// what it is told.
/// </para>
/// </remarks>
public readonly struct CornerRadius
{
    internal CornerRadius(Double horizontal, Double vertical)
    {
        Horizontal = horizontal;
        Vertical = vertical;
    }

    /// <summary>Gets a square corner.</summary>
    public static CornerRadius Zero => default;

    /// <summary>Gets the radius along the x axis.</summary>
    public Double Horizontal { get; }

    /// <summary>Gets the radius along the y axis.</summary>
    public Double Vertical { get; }

    /// <summary>Gets whether the corner is square, so there is nothing to round off.</summary>
    public Boolean IsZero => Horizontal <= 0 || Vertical <= 0;
}

/// <summary>The four corner radii of a box, in pixels and relative to that box.</summary>
/// <remarks>
/// Nothing here reads the style: the radii are resolved once, at the end of layout, because a
/// percentage radius is relative to the size of the box and shrinking the radii that would overlap
/// needs that size as well. Painting then only does arithmetic.
/// </remarks>
public readonly struct CornerRadii
{
    internal CornerRadii(
        CornerRadius topLeft,
        CornerRadius topRight,
        CornerRadius bottomRight,
        CornerRadius bottomLeft)
    {
        TopLeft = topLeft;
        TopRight = topRight;
        BottomRight = bottomRight;
        BottomLeft = bottomLeft;
    }

    /// <summary>Gets four square corners, which is what a box with no radius has.</summary>
    public static CornerRadii None => default;

    public CornerRadius TopLeft { get; }

    public CornerRadius TopRight { get; }

    public CornerRadius BottomRight { get; }

    public CornerRadius BottomLeft { get; }

    /// <summary>Gets whether every corner is square.</summary>
    public Boolean IsZero => TopLeft.IsZero && TopRight.IsZero && BottomRight.IsZero && BottomLeft.IsZero;

    /// <summary>
    /// Gets whether the four corners are one and the same circle, which is the case a uniform
    /// <c>border-radius</c> produces and the one a backend can draw in a single call.
    /// </summary>
    public Boolean IsUniform =>
        TopLeft.Horizontal == TopRight.Horizontal && TopLeft.Horizontal == BottomRight.Horizontal &&
        TopLeft.Horizontal == BottomLeft.Horizontal && TopLeft.Horizontal == TopLeft.Vertical &&
        TopLeft.Vertical == TopRight.Vertical && TopLeft.Vertical == BottomRight.Vertical &&
        TopLeft.Vertical == BottomLeft.Vertical;

    /// <summary>
    /// Resolves the written radii against the size of the box, which is the border box: that is what
    /// a percentage radius is relative to, and what the overlap rule is measured against.
    /// </summary>
    /// <remarks>
    /// It is the engine's own step, not a backend's: the specified radii are a style, and a style
    /// never reaches a drawing surface.
    /// </remarks>
    internal static CornerRadii Resolve(StyleCornerRadii specified, Double width, Double height)
    {
        var radii = new CornerRadii(
            Resolve(specified.TopLeft, width, height),
            Resolve(specified.TopRight, width, height),
            Resolve(specified.BottomRight, width, height),
            Resolve(specified.BottomLeft, width, height));

        return Fit(radii, width, height);
    }

    /// <summary>
    /// Gets the radii of the shape a shadow is drawn from: every corner moved out by the distance its edges
    /// were moved, or back toward the corner where that distance is negative, and the four then fitted to the
    /// size that leaves. Moving them together is what keeps a spread shadow the shape of the box it was cast
    /// from, sharp corners and all (CSS Backgrounds 3 §6.1.1).
    /// </summary>
    internal CornerRadii Spread(Double distance, Double width, Double height) =>
        Fit(new CornerRadii(
            Spread(TopLeft, distance),
            Spread(TopRight, distance),
            Spread(BottomRight, distance),
            Spread(BottomLeft, distance)),
            width,
            height);

    private static CornerRadius Spread(CornerRadius radius, Double distance) =>
        Normalize(radius.Horizontal + distance, radius.Vertical + distance);

    /// <summary>
    /// Gets the radii of a box that has them, or four square corners where it has none to speak of.
    /// </summary>
    internal static CornerRadii Fit(CornerRadii radii, Double width, Double height)
    {
        if (radii.IsZero)
        {
            return None;
        }

        // CSS Backgrounds and Borders 3, §5.5: when the radii on one side add up to more than that
        // side, every radius shrinks by the same factor, so the corners keep their shape instead of
        // eating into each other. The horizontal radii are measured against the width and the
        // vertical ones against the height, because each is an ellipse of its own.
        var factor = Math.Min(
            Math.Min(
                Ratio(width, radii.TopLeft.Horizontal + radii.TopRight.Horizontal),
                Ratio(width, radii.BottomLeft.Horizontal + radii.BottomRight.Horizontal)),
            Math.Min(
                Ratio(height, radii.TopLeft.Vertical + radii.BottomLeft.Vertical),
                Ratio(height, radii.TopRight.Vertical + radii.BottomRight.Vertical)));

        return factor < 1 ? radii.Scale(factor) : radii;
    }

    /// <summary>
    /// Gets the radii of the padding box, i.e. the ones a border eats into: a 6px border around a
    /// 14px corner leaves an 8px corner behind.
    /// </summary>
    public CornerRadii Shrink(Thickness insets) => new(
        Shrink(TopLeft, insets.Top, insets.Left),
        Shrink(TopRight, insets.Top, insets.Right),
        Shrink(BottomRight, insets.Bottom, insets.Right),
        Shrink(BottomLeft, insets.Bottom, insets.Left));

    private static CornerRadius Shrink(CornerRadius radius, Double vertical, Double horizontal) =>
        Normalize(radius.Horizontal - horizontal, radius.Vertical - vertical);

    internal static CornerRadius Resolve(StyleCornerRadius specified, Double width, Double height) =>
        Normalize(specified.Horizontal.Resolve(width), specified.Vertical.Resolve(height));

    private static CornerRadius Normalize(Double horizontal, Double vertical) =>
        horizontal > 0 && vertical > 0 ? new CornerRadius(horizontal, vertical) : CornerRadius.Zero;

    /// <summary>
    /// Gets how much of a side a radius takes. A side with no radii on it imposes no limit, so the
    /// factor it contributes is infinite and the minimum ignores it.
    /// </summary>
    private static Double Ratio(Double side, Double total) => total > 0 ? side / total : Double.PositiveInfinity;

    private CornerRadii Scale(Double factor) => new(
        Scale(TopLeft, factor),
        Scale(TopRight, factor),
        Scale(BottomRight, factor),
        Scale(BottomLeft, factor));

    private static CornerRadius Scale(CornerRadius radius, Double factor) =>
        new(radius.Horizontal * factor, radius.Vertical * factor);
}

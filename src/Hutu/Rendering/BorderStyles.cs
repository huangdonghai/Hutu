using System;

namespace Hutu.Rendering;

/// <summary>The stroke one side of a border is drawn with, i.e. what <c>border-style</c> selects.</summary>
public enum BorderLineStyle : byte
{
    /// <summary>Nothing is drawn, which is what <c>none</c> and <c>hidden</c> ask for.</summary>
    None,

    /// <summary>One unbroken stroke.</summary>
    Solid,

    /// <summary>A stroke broken into dashes.</summary>
    Dashed,

    /// <summary>A stroke made of dots.</summary>
    Dotted,
}

/// <summary>The stroke each side of a border is drawn with.</summary>
/// <remarks>
/// The strokes are carried per side rather than as a single flag because a box may mix them, and a
/// rounded border has to be drawn side by side along one continuous outline.
/// <para>
/// A backend reads which stroke each side asks for; the four are put together from the computed style,
/// which is no part of a host's work.
/// </para>
/// </remarks>
public readonly struct BorderStyles : IEquatable<BorderStyles>
{
    internal BorderStyles(BorderLineStyle top, BorderLineStyle right, BorderLineStyle bottom, BorderLineStyle left)
    {
        Top = top;
        Right = right;
        Bottom = bottom;
        Left = left;
    }

    /// <summary>Gets four sides that draw nothing.</summary>
    public static BorderStyles None => default;

    public BorderLineStyle Top { get; }

    public BorderLineStyle Right { get; }

    public BorderLineStyle Bottom { get; }

    public BorderLineStyle Left { get; }

    /// <summary>Gets whether every side is drawn with one unbroken stroke, so no pattern is needed.</summary>
    public Boolean IsSolid =>
        Top == BorderLineStyle.Solid &&
        Right == BorderLineStyle.Solid &&
        Bottom == BorderLineStyle.Solid &&
        Left == BorderLineStyle.Solid;

    /// <summary>
    /// Gets whether the four sides share one stroke, so that they can be drawn along a single outline
    /// instead of being cut apart at the corners.
    /// </summary>
    public Boolean IsUniform => Top == Right && Top == Bottom && Top == Left;

    /// <summary>
    /// Gets whether the four sides of two borders are drawn with the same four strokes.
    /// </summary>
    public Boolean Equals(BorderStyles other) =>
        Top == other.Top && Right == other.Right && Bottom == other.Bottom && Left == other.Left;

    public override Boolean Equals(Object? obj) => obj is BorderStyles other && Equals(other);

    public override Int32 GetHashCode() => HashCode.Combine(Top, Right, Bottom, Left);
}

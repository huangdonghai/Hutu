using Hutu.Rendering;
using Hutu.Rendering.Layout;
using Hutu.Rendering.Paint;
using System;

namespace Hutu.Skia;

/// <summary>One side of a border, so that the four of them can be walked in a loop.</summary>
internal enum BorderSide
{
    Top,
    Right,
    Bottom,
    Left,
}

/// <summary>Reads the per-side values of a border, which a command carries as four separate ones.</summary>
internal static class BorderSides
{
    /// <summary>The four sides, in the order the collapsing border model resolves ties.</summary>
    public static readonly BorderSide[] All = { BorderSide.Top, BorderSide.Right, BorderSide.Bottom, BorderSide.Left };

    public static Double Side(this Thickness widths, BorderSide side) => side switch
    {
        BorderSide.Top => widths.Top,
        BorderSide.Right => widths.Right,
        BorderSide.Bottom => widths.Bottom,
        _ => widths.Left,
    };

    public static BorderLineStyle Side(this BorderStyles styles, BorderSide side) => side switch
    {
        BorderSide.Top => styles.Top,
        BorderSide.Right => styles.Right,
        BorderSide.Bottom => styles.Bottom,
        _ => styles.Left,
    };

    public static UInt32 ColorOf(this BorderCommand command, BorderSide side) => side switch
    {
        BorderSide.Top => command.TopColor,
        BorderSide.Right => command.RightColor,
        BorderSide.Bottom => command.BottomColor,
        _ => command.LeftColor,
    };

    /// <summary>Gets whether the four sides share one colour, so that they can be drawn in one pass.</summary>
    public static Boolean HasUniformColor(this BorderCommand command) =>
        command.TopColor == command.RightColor &&
        command.TopColor == command.BottomColor &&
        command.TopColor == command.LeftColor;

    /// <summary>Gets whether the four sides share one width, so that one stroke can stand for them all.</summary>
    public static Boolean IsUniform(this Thickness widths) =>
        widths.Top == widths.Right && widths.Top == widths.Bottom && widths.Top == widths.Left;
}

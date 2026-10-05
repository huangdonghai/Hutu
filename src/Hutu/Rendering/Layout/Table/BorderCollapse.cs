using AngleSharp.Css.Values;
using System;

namespace Hutu.Rendering.Layout;

/// <summary>One side of a box, named the way the border properties spell it.</summary>
internal enum BorderSide : byte
{
    Top,
    Right,
    Bottom,
    Left,
}

/// <summary>
/// A border as it takes part in the collapsing model. Unlike a plain width, it carries the style and
/// the origin the conflict rules need: when two borders meet on a grid line, the wider one wins, then
/// the one with the more emphatic style, then the one declared by the box closer to the cell.
/// </summary>
internal readonly struct CollapsedBorder
{
    /// <summary>Where a border sits in the origin ladder. Higher wins.</summary>
    public const Int32 OriginTable = 0;
    public const Int32 OriginColumnGroup = 1;
    public const Int32 OriginColumn = 2;
    public const Int32 OriginRowGroup = 3;
    public const Int32 OriginRow = 4;
    public const Int32 OriginCell = 5;

    private CollapsedBorder(Double width, UInt32 color, Boolean hidden, Int32 styleRank, Int32 origin)
    {
        Width = width;
        Color = color;
        IsHidden = hidden;
        StyleRank = styleRank;
        Origin = origin;
    }

    public Double Width { get; }

    public UInt32 Color { get; }

    /// <summary>Gets whether the border is <c>border-style: hidden</c>, which suppresses the line.</summary>
    public Boolean IsHidden { get; }

    public Int32 StyleRank { get; }

    public Int32 Origin { get; }

    /// <summary>Gets whether anything is painted for this border.</summary>
    public Boolean IsVisible => !IsHidden && Width > 0;

    /// <summary>A border that paints nothing and loses every conflict.</summary>
    public static CollapsedBorder None => new(0, 0, false, 0, -1);

    /// <summary>Reads one side of a box as a collapsing candidate.</summary>
    public static CollapsedBorder Of(ComputedStyle style, BorderSide side, Int32 origin)
    {
        var (width, keyword, color) = side switch
        {
            BorderSide.Top => (style.BorderTopWidth, style.BorderTopStyle, style.BorderTopColor),
            BorderSide.Right => (style.BorderRightWidth, style.BorderRightStyle, style.BorderRightColor),
            BorderSide.Bottom => (style.BorderBottomWidth, style.BorderBottomStyle, style.BorderBottomColor),
            _ => (style.BorderLeftWidth, style.BorderLeftStyle, style.BorderLeftColor),
        };

        var rank = StyleRankOf(keyword);

        if (rank == 0 || width <= 0)
        {
            // `none` and `hidden` both mean "no line here", but they differ in what they do to a
            // neighbour: `hidden` wins the conflict, `none` loses it.
            return keyword == "hidden"
                ? new CollapsedBorder(0, 0, true, rank, origin)
                : None;
        }

        return new CollapsedBorder(width, ToArgb(color), false, rank, origin);
    }

    /// <summary>Picks the border that wins the conflict between two candidates on the same line.</summary>
    public static CollapsedBorder Resolve(CollapsedBorder left, CollapsedBorder right)
    {
        if (left.IsHidden || right.IsHidden)
        {
            return (left.IsHidden ? left : right).Width == 0
                ? new CollapsedBorder(0, 0, true, 9, Math.Max(left.Origin, right.Origin))
                : (left.IsHidden ? left : right);
        }

        if (right.Width > left.Width + 0.0001)
        {
            return right;
        }

        if (left.Width > right.Width + 0.0001)
        {
            return left;
        }

        if (right.StyleRank != left.StyleRank)
        {
            return right.StyleRank > left.StyleRank ? right : left;
        }

        return right.Origin > left.Origin ? right : left;
    }

    /// <summary>Gets the widest of the candidates, which is what a table's own edge reports.</summary>
    public static CollapsedBorder Widest(CollapsedBorder current, CollapsedBorder candidate) =>
        candidate.Width > current.Width + 0.0001 ? candidate : current;

    /// <summary>
    /// Ranks a border style for the tie-break. The order is the one CSS 2.1 gives, from the least to
    /// the most emphatic, with <c>hidden</c> ranked above everything because it suppresses the line.
    /// </summary>
    private static Int32 StyleRankOf(String keyword) => keyword switch
    {
        "hidden" => 9,
        "double" => 8,
        "solid" => 7,
        "dashed" => 6,
        "dotted" => 5,
        "ridge" => 4,
        "outset" => 3,
        "groove" => 2,
        "inset" => 1,
        _ => 0,
    };

    private static UInt32 ToArgb(CssColorValue? color) =>
        color is null ? 0xFF000000u : (UInt32)(((UInt32)color.Value.A << 24) | ((UInt32)color.Value.R << 16) | ((UInt32)color.Value.G << 8) | color.Value.B);
}

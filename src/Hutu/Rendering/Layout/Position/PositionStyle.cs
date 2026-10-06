using AngleSharp.Css;
using AngleSharp.Css.Dom;
using AngleSharp.Css.Values;
using System;

namespace Hutu.Rendering.Layout;

/// <summary>
/// The positioning longhands of a box: how it is placed and, when it is out of flow, which offsets
/// decide where it lands.
/// </summary>
/// <remarks>
/// The offsets stay as <see cref="StyleLength"/> so a percentage can be resolved against the box's
/// containing block at layout time rather than the viewport when the style was computed. Vertical
/// offsets resolve against the containing block's height, which is why the two axes are kept apart.
/// <para>
/// Two upstream quirks are contained here. <c>position</c>'s initial value is keyed <c>inline</c>
/// while the value itself is <c>static</c>, so the keyword is taken from the typed value and never
/// from the serialised string; and <c>z-index</c>'s initial value is a length rather than an integer,
/// so both shapes are accepted.
/// </para>
/// </remarks>
public readonly struct PositionStyle
{
    private PositionStyle(
        PositionMode position,
        StyleLength top,
        StyleLength right,
        StyleLength bottom,
        StyleLength left,
        Int32 zIndex,
        Boolean hasZIndex)
    {
        Position = position;
        Top = top;
        Right = right;
        Bottom = bottom;
        Left = left;
        ZIndex = zIndex;
        HasZIndex = hasZIndex;
    }

    public PositionMode Position { get; }

    public StyleLength Top { get; }

    public StyleLength Right { get; }

    public StyleLength Bottom { get; }

    public StyleLength Left { get; }

    /// <summary>Gets the stacking level. Only meaningful when <see cref="HasZIndex"/> is true.</summary>
    public Int32 ZIndex { get; }

    /// <summary>Gets whether the author declared a numeric <c>z-index</c> rather than <c>auto</c>.</summary>
    public Boolean HasZIndex { get; }

    /// <summary>Gets whether the box takes part in the stacking order, i.e. is anything but static.</summary>
    public Boolean IsPositioned => Position != PositionMode.Static;

    /// <summary>Gets whether the box is removed from normal flow (<c>absolute</c> or <c>fixed</c>).</summary>
    public Boolean IsOutOfFlow => Position is PositionMode.Absolute or PositionMode.Fixed;

    internal static PositionStyle Read(ComputedStyle style) => new(
        ReadPosition(style),
        Inset(style, "top"),
        Inset(style, "right"),
        Inset(style, "bottom"),
        Inset(style, "left"),
        ReadZIndex(style, out var hasZIndex),
        hasZIndex);

    /// <summary>
    /// Reads the <c>position</c> keyword from its typed value. The serialised string is only a
    /// fallback for the cascaded declaration, because the initial value's key is wrong.
    /// </summary>
    private static PositionMode ReadPosition(ComputedStyle style)
    {
        if (style.Declaration.GetProperty("position")?.RawValue is CssConstantValue<PositionMode> constant)
        {
            return constant.Value;
        }

        var text = style.Specified?.GetPropertyValue("position");

        if (String.IsNullOrWhiteSpace(text))
        {
            text = style.Declaration.GetPropertyValue("position");
        }

        return text?.Trim().ToLowerInvariant() switch
        {
            "relative" => PositionMode.Relative,
            "absolute" => PositionMode.Absolute,
            "fixed" => PositionMode.Fixed,
            "sticky" => PositionMode.Sticky,
            _ => PositionMode.Static,
        };
    }

    private static StyleLength Inset(ComputedStyle style, String name) =>
        LengthReader.TryRead(style.Specified, style.Declaration, name, RenderMode.Horizontal, style.Dimensions, style.FontSize, out var length) &&
        length.HasValue
            ? length
            : StyleLength.Auto;

    private static Int32 ReadZIndex(ComputedStyle style, out Boolean hasValue)
    {
        switch (style.Declaration.GetProperty("z-index")?.RawValue)
        {
            case CssIntegerValue integer:
                hasValue = true;
                return (Int32)integer.Value;
        }

        hasValue = false;
        return 0;
    }
}

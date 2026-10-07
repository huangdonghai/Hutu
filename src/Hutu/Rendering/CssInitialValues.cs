using AngleSharp.Css.Dom;
using Hutu.Rendering.Layout;

namespace Hutu.Rendering;

/// <summary>
/// The initial value of every CSS property the read layer falls back to, in the form its reader wants
/// it. One store rather than a literal at each call site.
/// </summary>
/// <remarks>
/// This is not a convenience. AngleSharp answers an undeclared name with an <em>invented, empty</em>
/// property rather than with null - a placeholder the declaration factory builds for the name that was
/// asked about - so a reader cannot tell "the page declared nothing" from "the page declared an empty
/// value", and always takes its fallback. That makes these constants the engine's real initial values,
/// with nothing checking them against the specification.
/// <para>
/// The hazard is drift, and it is the one PeachPDF records having been bitten by: their
/// <c>border-color</c> defaulted to the literal <c>black</c> in one store and to the specification's
/// <c>currentcolor</c> in another, so which value a box got depended on which store it was read from.
/// A page only shows it when a property is reached without ever having been declared.
/// </para>
/// <para>
/// Numeric values are the <em>computed</em> form of the keyword: <c>font-weight: normal</c> is 400,
/// <c>letter-spacing: normal</c> is 0, <c>line-height: normal</c> is null (which is why it is not
/// here - the reader takes no fallback for it and answers null, and the font metrics decide).
/// </para>
/// </remarks>
internal static class CssInitialValues
{
    // --- Display and box model ---------------------------------------------------------------
    internal const String Display = "inline";

    internal const String VerticalAlign = "baseline";

    internal const String ObjectFit = "fill";

    // --- Text --------------------------------------------------------------------------------
    internal const String WhiteSpace = "normal";

    /// <remarks>
    /// The specification leaves this one implementation-defined, so it is a choice rather than a
    /// transcription - and the only entry here that is not read straight off the specification. It is
    /// rarely reached: a computed declaration nearly always carries a font family, which is why the
    /// measured pages never exercise it.
    /// </remarks>
    internal const String FontFamily = "sans-serif";

    internal const String FontWeight = "400";

    /// <remarks>
    /// <c>start</c> rather than the historical <c>left</c>: the line's own edge decides which side that
    /// is, and <c>AlignmentOffset</c> treats the two logical keywords as the default anyway.
    /// </remarks>
    internal const String TextAlign = "start";

    internal const Double LetterSpacing = 0;

    // --- Border and outline ------------------------------------------------------------------
    //
    // One entry for all four sides: the specification gives every one of them `none`, and a reader
    // that takes the style by name - HasBorder does - needs a single value rather than four that
    // could only ever disagree with each other.
    internal const String BorderStyle = "none";

    internal const String OutlineStyle = "none";

    internal const Double OutlineOffset = 0;

    // --- Flex, and grid where the two differ ---------------------------------------------------
    //
    // Six of these are per-module rather than per-property: both modules start their alignment from
    // `normal`, and what `normal` behaves as is not the same in each. `justify-content` is flex's
    // `flex-start` but grid's `stretch`; `align-items` is flex's `stretch` but grid's `auto`, which
    // grid then reads as stretch for the axes it applies to.
    internal const FlexAlignment JustifyContent = FlexAlignment.Start;

    internal const FlexAlignment JustifyContentGrid = FlexAlignment.Stretch;

    internal const FlexAlignment AlignItems = FlexAlignment.Stretch;

    internal const FlexAlignment AlignItemsGrid = FlexAlignment.Auto;

    internal const FlexAlignment JustifyItemsGrid = FlexAlignment.Auto;

    internal const FlexAlignment JustifySelfGrid = FlexAlignment.Auto;

    internal const FlexAlignment AlignContent = FlexAlignment.Stretch;

    internal const FlexAlignment AlignSelf = FlexAlignment.Auto;

    // Qualified because the constant shares its name with AngleSharp's enum, which would otherwise
    // shadow the type inside this class.
    internal const FlexDirection FlexDirection = AngleSharp.Css.Dom.FlexDirection.Row;

    internal const FlexWrapMode FlexWrap = FlexWrapMode.NoWrap;

    /// <remarks>
    /// Not an initial value but the value a direction or alignment keyword Hutu cannot map falls back
    /// to. It sits here because it is the same kind of thing: a default the reader answers with when
    /// the declaration gave it nothing usable.
    /// </remarks>
    internal const FlexAlignment UnmappableAlignment = FlexAlignment.Auto;

    // --- Positioning -------------------------------------------------------------------------
    internal const PositionMode Position = PositionMode.Static;
}

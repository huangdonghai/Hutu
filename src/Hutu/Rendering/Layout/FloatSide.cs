namespace Hutu.Rendering.Layout;

/// <summary>
/// Which edge of its block formatting context a box floats to.
/// </summary>
/// <remarks>
/// The physical sides, and the logical spellings the page may write instead. A logical side is the
/// edge the line reads from or to, which only the box's writing mode can name - <see
/// cref="WritingModeInfo.FloatSideOf"/> turns it into the physical edge when the float is placed.
/// The name is <c>FloatSide</c> rather than <c>FloatMode</c> because
/// AngleSharp, whose enums the engine otherwise borrows, already spells <c>clear</c>'s values a
/// <c>ClearMode</c> - and a pair of float keywords read as one set and the other as a second would
/// only invite a mix-up at the call site.
/// </remarks>
internal enum FloatSide
{
    /// <summary>The box takes its normal place in flow.</summary>
    None,

    /// <summary>The box is pulled out of flow against the container's left edge.</summary>
    Left,

    /// <summary>The box is pulled out of flow against the container's right edge.</summary>
    Right,

    /// <summary>The box floats to the edge the line begins at, whichever edge that is.</summary>
    InlineStart,

    /// <summary>The box floats to the edge the line ends at, whichever edge that is.</summary>
    InlineEnd,
}

/// <summary>
/// Which side's floats a box refuses to stand beside, moving down until it is clear of them.
/// </summary>
internal enum ClearSide
{
    /// <summary>The box may sit beside a float.</summary>
    None,

    /// <summary>The box moves below every float on the left.</summary>
    Left,

    /// <summary>The box moves below every float on the right.</summary>
    Right,

    /// <summary>The box moves below every float of the context it belongs to.</summary>
    Both,
}

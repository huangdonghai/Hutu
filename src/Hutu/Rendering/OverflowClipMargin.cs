using System;

namespace Hutu.Rendering;

/// <summary>
/// The box <c>overflow-clip-margin</c> measures its distance from.
/// </summary>
internal enum ClipMarginBox : Byte
{
    /// <summary>The content edge.</summary>
    Content,

    /// <summary>The padding edge, which is the initial answer.</summary>
    Padding,

    /// <summary>The border edge.</summary>
    Border,
}

/// <summary>
/// How far past its own edge a box that clips its overflow still shows what is inside it
/// (CSS Overflow 3 §3.3).
/// </summary>
internal readonly record struct ClipMargin(ClipMarginBox Box, Double Length)
{
    /// <summary>Gets the initial answer: the padding edge and no room past it.</summary>
    public static ClipMargin None => new(ClipMarginBox.Padding, 0);
}

namespace Hutu.Rendering;

/// <summary>
/// Which box a declared <c>width</c> and <c>height</c> describe.
/// </summary>
/// <remarks>
/// Both the sample stylesheets open with <c>* { box-sizing: border-box }</c>, so a width that
/// ignores the padding and the border overstates the box. The min and max widths and heights follow
/// the same setting, as the specification requires.
/// </remarks>
internal enum BoxSizing : byte
{
    /// <summary>A declared size is the size of the content box, the initial value.</summary>
    ContentBox,

    /// <summary>A declared size already includes the padding and the border.</summary>
    BorderBox,
}

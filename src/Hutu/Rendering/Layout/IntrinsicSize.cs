using System;

namespace Hutu.Rendering.Layout;

/// <summary>
/// The intrinsic inline sizes of a box: the narrowest it can be without its content overflowing,
/// and the widest its content ever wants to be.
/// </summary>
/// <remarks>
/// <c>min-content</c> is the width of the widest indivisible part - the longest word - and
/// <c>max-content</c> the width of the whole content laid out on a single line. The auto sizing of
/// grid tracks, the automatic table layout and a <c>flex-basis: content</c> all ask for one or the
/// other before a definite size has been handed down.
/// </remarks>
internal readonly struct IntrinsicSize
{
    public IntrinsicSize(Double minContent, Double maxContent)
    {
        MinContent = minContent;
        MaxContent = maxContent;
    }

    public Double MinContent { get; }

    public Double MaxContent { get; }

    public static IntrinsicSize Empty => default;
}

namespace Hutu.Rendering;

/// <summary>Which box a background is painted into, which is what <c>background-clip</c> selects.</summary>
/// <remarks>
/// The members run from the outermost box inwards, and <see cref="BorderBox"/> is the initial value of
/// the property - it is also the zero of this type, so a box whose style never mentioned the property
/// is already painted the right way.
/// </remarks>
internal enum BackgroundClip : byte
{
    /// <summary>The border box, so the background reaches under a transparent border.</summary>
    BorderBox,

    /// <summary>The padding box, so the background stops at the inner edge of the border.</summary>
    PaddingBox,

    /// <summary>The content box.</summary>
    ContentBox,

    /// <summary>
    /// The shape of the text the element paints, so the background shows through the glyphs and nowhere
    /// else. Where the other members pick a box, this picks the ink, and a text stroke widens the glyphs
    /// it clips to.
    /// </summary>
    Text,
}

using Hutu.Rendering.Layout;
using System;

namespace Hutu.Rendering;

/// <summary>
/// The measures a font is made of, for the two length units that are read off the face rather than off
/// the size: <c>ex</c> is the font's x-height and <c>ch</c> the width of its zero.
/// </summary>
internal interface IFontMetricSource
{
    /// <summary>Gets the font's x-height in pixels, or null when no face was there to state one.</summary>
    Double? XHeight(FontStyle font);

    /// <summary>Gets how far the font's zero advances, in pixels, or null when nothing was there to measure.</summary>
    Double? ZeroAdvance(FontStyle font);
}

using Hutu.Rendering.Layout;
using System;

namespace Hutu.Tests;

/// <summary>
/// A fixed-width stand-in so layout geometry is independent of the fonts installed on the machine
/// running the tests.
/// </summary>
internal sealed class FakeTextMeasurer : ITextMeasurer
{
    private const Double CharacterWidth = 5;
    private const Double Ascent = 8;
    private const Double Descent = 2;

    public FontMetrics GetMetrics(FontStyle style) => new(Ascent, Descent, 0, 0, 0, 0, 0);

    public Double MeasureWidth(String text, FontStyle style) => text.Length * CharacterWidth;

    /// <summary>
    /// Reads a Hebrew or Arabic letter as one a shaper would set right to left by itself, which is
    /// what tells a run the layout turned round by hand from one the shaper turns round.
    /// </summary>
    public Boolean ReadsRightToLeft(String text)
    {
        foreach (var character in text)
        {
            if (character is >= '\u0590' and <= '\u08FF')
            {
                return true;
            }
        }

        return false;
    }
}

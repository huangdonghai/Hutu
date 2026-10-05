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
}

using Hutu.Rendering.Layout;

namespace Hutu.Conformance;

/// <summary>
/// A measurer that knows nothing about fonts, so a layout golden says what the layout engine did
/// rather than what this machine's typefaces happen to measure.
/// </summary>
/// <remarks>
/// Every character is the same width and every font the same height, which makes a line break, a
/// shrink or an intrinsic size depend on the arithmetic alone. That is a different question from
/// "does the page come out the size a browser makes it" - which is what the reference cases ask
/// against a real measurer - and it is the question a golden can ask without the answer changing when
/// the build machine gets a font update.
/// </remarks>
internal sealed class FixedTextMeasurer : ITextMeasurer
{
    private const Double CharacterWidth = 5;
    private const Double Ascent = 8;
    private const Double Descent = 2;

    public FontMetrics GetMetrics(FontStyle style) => new(Ascent, Descent, 0, 0, 0, 0, 0);

    public Double MeasureWidth(String text, FontStyle style) => text.Length * CharacterWidth;
}

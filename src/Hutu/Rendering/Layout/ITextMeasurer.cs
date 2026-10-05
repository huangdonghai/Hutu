using System;

namespace Hutu.Rendering.Layout;

/// <summary>
/// The text services the layout engine needs: font metrics and advance widths.
/// </summary>
/// <remarks>
/// Line breaking deliberately lives in the layout engine, not here: where a line may break is
/// script logic and must see the mixed styles of a whole line, while the backend only knows how to
/// turn a run of characters into a width. Keeping measurement behind this interface is also what
/// makes the layout unit-testable with a deterministic fake.
/// </remarks>
public interface ITextMeasurer
{
    /// <summary>Gets the vertical metrics of the font the style resolves to.</summary>
    FontMetrics GetMetrics(FontStyle style);

    /// <summary>Gets the advance width of the text, in pixels, without breaking it.</summary>
    Double MeasureWidth(String text, FontStyle style);
}

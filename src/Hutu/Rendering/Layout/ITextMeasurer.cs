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

    /// <summary>
    /// Gets how far the text advances along the axis it runs on, which is what decides where the next
    /// character goes.
    /// </summary>
    /// <remarks>
    /// A run advances by the same amount in its own frame however its glyphs are turned: an upright
    /// CJK character by its em box, and a Latin word rotated into a column by the very width it would
    /// have had along a line. The advance along the inline axis of a vertical column is therefore the
    /// horizontal width everywhere it matters, which is why the default hands every mode back to
    /// <see cref="MeasureWidth"/> - and why a backend only needs this when that stops being true, as
    /// it does for a script whose vertical advance the shaping engine reports differently.
    /// </remarks>
    Double MeasureAdvance(String text, FontStyle style, WritingModeInfo flow) => MeasureWidth(text, style);

    /// <summary>
    /// Gets how far one character advances along the axis it runs on.
    /// </summary>
    /// <remarks>
    /// The single-character form of <see cref="MeasureAdvance(String,FontStyle,WritingModeInfo)"/>, and
    /// worth having because a column of upright text asks it once per character: a backend that keeps the
    /// answer by character and font then answers a whole page from a table, where the string form would
    /// write out a one-character string and hash it per character.
    /// <para>
    /// A backend that overrides the flow-dependent behaviour of the string form should override this
    /// with it, or the two will disagree about the same character.
    /// </para>
    /// </remarks>
    Double MeasureAdvance(Char character, FontStyle style, WritingModeInfo flow) =>
        MeasureAdvance(character.ToString(), style, flow);

    /// <summary>
    /// Gets the extent the text occupies along the block axis: the height of a horizontal line, or the
    /// width of a vertical column.
    /// </summary>
    /// <remarks>
    /// The default is the font's own line height, which is also what an upright CJK em box measures
    /// across a column. It is only a fallback: a declared <c>line-height</c> is what the layout engine
    /// uses when there is one, so this decides nothing until the font is left to speak for itself.
    /// </remarks>
    Double MeasureBlockExtent(String text, FontStyle style, WritingModeInfo flow) =>
        GetMetrics(style).LineHeight;
}

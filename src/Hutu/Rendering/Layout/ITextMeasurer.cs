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

    /// <summary>
    /// Gets the vertical metrics a stretch of text is set with: the tallest ascent and descent among
    /// the fonts its glyphs resolve to, which is the font the style names where that font carries them.
    /// </summary>
    /// <remarks>
    /// CSS 2.2 §10.8.1 gives every glyph the metrics of the font it comes from - "glyphs in a single
    /// element may come from different fonts and thus need not all have the same A and D" - and a run
    /// is as tall as the tallest of them. A face that carries no CJK glyph sends those characters to a
    /// fallback, and it is the fallback's metrics a line of Chinese follows: measured from the
    /// requested font alone, a line of 16px Chinese comes out 21.3px tall where a browser gives 24,
    /// and every line of such a page is short by that much.
    /// <para>
    /// The default answers with the requested font's metrics, which is right for a backend whose fonts
    /// carry everything they are asked to set, and is what the line rules want for the strut - an
    /// invisible glyph of zero width, which the specification gives the first available font's metrics.
    /// </para>
    /// </remarks>
    FontMetrics GetRunMetrics(String text, FontStyle style) => GetMetrics(style);

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
        MeasureAdvance((Int32)character, style, flow);

    /// <summary>
    /// Gets how far one code point advances along the axis it runs on - which for a column is the height
    /// of the one cell it stands in.
    /// </summary>
    /// <remarks>
    /// The character form above cannot be asked for anything written above the BMP, and a column stepped
    /// by characters therefore takes two cells for the ideograph one code point stands for. This is the
    /// form a column walks by.
    /// </remarks>
    Double MeasureAdvance(Int32 codePoint, FontStyle style, WritingModeInfo flow) =>
        MeasureAdvance(Char.ConvertFromUtf32(codePoint), style, flow);

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

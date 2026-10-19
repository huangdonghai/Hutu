using System;

namespace Hutu.Rendering.Layout;

/// <summary>
/// Vertical metrics of a resolved font, in pixels, with the baseline as the origin.
/// </summary>
/// <remarks>
/// <see cref="Ascent"/> and <see cref="Descent"/> are normalised to positive distances above and
/// below the baseline, which is the opposite sign of the raw Skia metrics for the ascent.
/// </remarks>
public readonly struct FontMetrics
{
    public FontMetrics(
        Double ascent,
        Double descent,
        Double lineGap,
        Double underlineOffset,
        Double underlineThickness,
        Double strikeoutOffset,
        Double strikeoutThickness,
        Double overlineOffset = 0,
        Double overlineThickness = 0,
        Double xHeight = 0,
        Double capHeight = 0)
    {
        Ascent = ascent;
        Descent = descent;
        LineGap = lineGap;
        UnderlineOffset = underlineOffset;
        UnderlineThickness = underlineThickness;
        StrikeoutOffset = strikeoutOffset;
        StrikeoutThickness = strikeoutThickness;
        OverlineOffset = overlineOffset;
        OverlineThickness = overlineThickness;
        XHeight = xHeight;
        CapHeight = capHeight;
    }

    /// <summary>Gets the distance from the baseline to the top of the em box, as a positive value.</summary>
    public Double Ascent { get; }

    /// <summary>Gets the distance from the baseline to the bottom of the em box, as a positive value.</summary>
    public Double Descent { get; }

    /// <summary>Gets the recommended extra space between lines.</summary>
    public Double LineGap { get; }

    /// <summary>Gets the default line height for the font.</summary>
    public Double LineHeight => Ascent + Descent + LineGap;

    /// <summary>Gets the distance below the baseline at which an underline is drawn.</summary>
    public Double UnderlineOffset { get; }

    public Double UnderlineThickness { get; }

    /// <summary>Gets the distance above the baseline at which a strike-through is drawn.</summary>
    public Double StrikeoutOffset { get; }

    public Double StrikeoutThickness { get; }

    /// <summary>
    /// Gets the distance above the baseline at which an overline is drawn, or zero when the face does
    /// not say. A face that does not ask gets the top of its own em box, which is the only other height
    /// the line could sensibly be taken from.
    /// </summary>
    public Double OverlineOffset { get; }

    /// <summary>Gets the thickness an overline is drawn at, or zero to use the underline's own.</summary>
    public Double OverlineThickness { get; }

    /// <summary>
    /// Gets the distance from the baseline to the top of a lowercase letter, or zero when the face does
    /// not say. It is what <c>vertical-align: middle</c> is measured from, and the one height Skia's own
    /// metrics do not carry.
    /// </summary>
    public Double XHeight { get; }

    /// <summary>Gets the distance from the baseline to the top of a capital letter, or zero where the face does not say.</summary>
    public Double CapHeight { get; }
}

using System;

namespace Hutu.Rendering;

/// <summary>Which way a block's lines are scaled to fill their line boxes (CSS Text 5 §1.1).</summary>
internal enum TextFitMode : Byte
{
    /// <summary>Nothing is scaled - the initial value, and the answer for anything unreadable.</summary>
    None,

    /// <summary>Contents are scaled up to fill the line box.</summary>
    Grow,

    /// <summary>Contents are scaled down to fit the line box.</summary>
    Shrink,
}

/// <summary>Which lines carry the scaling (CSS Text 5 §1.1).</summary>
internal enum TextFitScaling : Byte
{
    /// <summary>Every line of the block is scaled by the smallest of the line factors.</summary>
    Consistent,

    /// <summary>Each line is scaled by its own factor, except the block's last line and any line a
    /// forced break ended.</summary>
    PerLine,

    /// <summary>Each line is scaled by its own factor, those two included.</summary>
    PerLineAll,
}

/// <summary>
/// The resolution of <c>text-fit</c>: which way the block's lines are scaled, which of them are, and the
/// limit the factor is clamped to.
/// </summary>
/// <remarks>
/// The property scales the font size the line is drawn with so that its contents exactly fill the line
/// box, which is a different thing from justification - that spreads the room between characters, this
/// makes the characters themselves bigger or smaller (CSS Text 5 §1.1). What reaches the layout is the
/// three parts of the declaration, kept apart because the direction and the limit answer different
/// questions: the direction says which way a line that does not need scaling is left alone, and the limit
/// is a floor or a ceiling on the factor rather than a size.
/// </remarks>
internal readonly struct TextFit
{
    public TextFit(TextFitMode mode, TextFitScaling scaling, Double? limit)
    {
        Mode = mode;
        Scaling = scaling;
        Limit = limit;
    }

    /// <summary>Gets the block whose lines stand at the size the page wrote.</summary>
    public static readonly TextFit None = new(TextFitMode.None, TextFitScaling.Consistent, null);

    /// <summary>Gets which way the contents are scaled.</summary>
    public TextFitMode Mode { get; }

    /// <summary>Gets which of the block's lines are scaled.</summary>
    public TextFitScaling Scaling { get; }

    /// <summary>
    /// Gets the limit on the factor as a multiple - <c>75%</c> is 0.75 - or null where the page stated
    /// none. It is a ceiling for <c>grow</c> and a floor for <c>shrink</c>, and nothing otherwise.
    /// </summary>
    public Double? Limit { get; }

    /// <summary>Gets whether the block's lines are left at the size they were written at.</summary>
    public Boolean IsNone => Mode == TextFitMode.None;

    /// <summary>Gets whether the whole block shares one factor, as it does unless the page said otherwise.</summary>
    public Boolean SharesOne => Scaling == TextFitScaling.Consistent;

    /// <summary>
    /// Gets the factor a line computed to <paramref name="factor"/> is kept within: a ceiling for a block
    /// that grows, a floor for one that shrinks, and no bound where the limit was written for the other
    /// direction or left out (CSS Text 5 §1.1).
    /// </summary>
    public Double Clamp(Double factor)
    {
        if (Limit is not { } limit)
        {
            return factor;
        }

        return Mode switch
        {
            TextFitMode.Grow when limit >= 1 => Math.Min(factor, limit),
            TextFitMode.Shrink when limit <= 1 => Math.Max(factor, limit),
            _ => factor,
        };
    }
}

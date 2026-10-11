using Hutu.Rendering.Boxes;
using System;

namespace Hutu.Rendering.Layout;

/// <summary>
/// A flex item while the algorithm is running: its own flex longhands, its box model along the two
/// axes, and the sizes the algorithm resolves - the flex base size, the hypothetical main size, the
/// used main and cross sizes and the resulting border box.
/// </summary>
/// <remarks>
/// Everything is kept in the item's <em>logical</em> main and cross axes, so the algorithm does not
/// branch on the direction. The margins at the two ends of each axis are named <c>Start</c> and
/// <c>End</c> rather than <c>Left</c> and <c>Right</c> for the same reason; the direction is applied
/// once, when the border box is placed.
/// </remarks>
internal sealed class FlexItem
{
    public Box Box = null!;

    // Flex longhands.
    public Double Grow;
    public Double Shrink;
    public StyleLength Basis;

    /// <summary>Gets whether the base size is <c>content</c>: measured, and deaf to the main-size property.</summary>
    public Boolean BasisIsContent;

    public Int32 Order;
    public FlexAlignment AlignSelf;

    // Box model along the main axis, in the direction the flex container runs.
    public Double MarginMainStart;
    public Double MarginMainEnd;
    public Double MarginCrossStart;
    public Double MarginCrossEnd;
    public Boolean AutoMarginMainStart;
    public Boolean AutoMarginMainEnd;
    public Boolean AutoMarginCrossStart;
    public Boolean AutoMarginCrossEnd;

    /// <summary>Gets the padding and border along the main axis, which the sizes here exclude.</summary>
    public Double InsetMain;

    /// <summary>Gets the padding and border along the cross axis.</summary>
    public Double InsetCross;

    public Double? MinMain;
    public Double? MaxMain;
    public Double? MinCross;
    public Double? MaxCross;

    /// <summary>Gets whether the item's cross size comes from its content rather than a declaration.</summary>
    public Boolean AutoCross;

    /// <summary>Gets the flex base size, a content-box size.</summary>
    public Double BaseMain;

    /// <summary>Gets the flex base size after the min and max constraints, a content-box size.</summary>
    public Double HypotheticalMain;

    /// <summary>Gets the used content-box main size.</summary>
    public Double UsedMain;

    /// <summary>Gets the used content-box cross size.</summary>
    public Double UsedCross;

    /// <summary>
    /// Gets the distance from the item's border-box top to its first baseline, which the items a
    /// line aligns by baseline are raised against.
    /// </summary>
    public Double FirstBaseline;

    /// <summary>Gets the border box's main size.</summary>
    public Double BorderBoxMain => UsedMain + InsetMain;

    /// <summary>Gets the border box's cross size.</summary>
    public Double BorderBoxCross => UsedCross + InsetCross;

    /// <summary>Gets the item's outer main size: the border box plus both main margins.</summary>
    public Double OuterMain => BorderBoxMain + MarginMainStart + MarginMainEnd;

    /// <summary>Gets the outer main size the item would have at its hypothetical main size.</summary>
    public Double OuterHypothetical => HypotheticalMain + InsetMain + MarginMainStart + MarginMainEnd;

    /// <summary>Gets the item's outer cross size: the border box plus both cross margins.</summary>
    public Double OuterCross => BorderBoxCross + MarginCrossStart + MarginCrossEnd;

    // Resolved placement: the position of the border box inside the container's content box, in the
    // container's own coordinate system, and the size of the border box. The logical main and cross
    // axes are mapped onto X and Y once, when the item is placed, so nothing downstream has to know
    // which way the container runs.
    public Double OffsetX;
    public Double OffsetY;
    public Double BorderBoxWidth;
    public Double BorderBoxHeight;

    public Double ClampMain(Double value) => Clamp(value, MinMain, MaxMain);

    public Double ClampCross(Double value) => Clamp(value, MinCross, MaxCross);

    private static Double Clamp(Double value, Double? min, Double? max)
    {
        if (min is { } lower && value < lower)
        {
            value = lower;
        }

        if (max is { } upper && value > upper)
        {
            value = upper;
        }

        return value;
    }
}

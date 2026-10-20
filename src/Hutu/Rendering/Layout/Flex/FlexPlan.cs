using System;
using System.Collections.Generic;

namespace Hutu.Rendering.Layout;

/// <summary>The result of running the flex algorithm: the container's used sizes and its items.</summary>
internal sealed class FlexPlan
{
    /// <summary>Gets the used content-box main size of the container.</summary>
    public Double ContentMain { get; set; }

    /// <summary>Gets the used content-box cross size of the container.</summary>
    public Double ContentCross { get; set; }

    /// <summary>Gets the items, in order, with their placement resolved.</summary>
    public List<FlexItem> Items { get; } = new();

    /// <summary>
    /// Gets the lines the items came to, in the order the container placed them. The gaps between lines
    /// are what a container's row or column decorations stand in, so the lines are what a caller that
    /// wants those decorations has to have.
    /// </summary>
    public List<FlexLine> Lines { get; } = new();
}

/// <summary>A single flex line: the items that share a main-axis run, and the line's cross size.</summary>
internal sealed class FlexLine
{
    public List<FlexItem> Items { get; } = new();

    /// <summary>Gets the outer main size the line's items and gaps occupy.</summary>
    public Double MainSize { get; set; }

    /// <summary>Gets the line's cross size.</summary>
    public Double CrossSize { get; set; }

    /// <summary>Gets the line's cross start, relative to the container's content box.</summary>
    public Double CrossStart { get; set; }

    /// <summary>
    /// Gets the distance from the line's cross start to the baseline its baseline-aligned items
    /// share, which is the first baseline of whichever of them sits lowest.
    /// </summary>
    public Double Baseline { get; set; }
}

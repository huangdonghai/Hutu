using AngleSharp.Dom;
using Hutu.Rendering.Layout;
using System;
using System.Collections.Generic;

namespace Hutu.Rendering.Interaction;

/// <summary>
/// What lies under a point in document coordinates: the box the point landed in, and the elements
/// that box sits in, nearest first.
/// </summary>
/// <remarks>
/// One hit answers everything a pointer asks - what to hover, which pointer to show, whose hint to
/// raise and which link a click would follow - so a pointer move measures the tree once.
/// <para>
/// A point that lands in no element is <see cref="None"/>, whose chain is empty. The margin beside
/// a paragraph belongs to whatever is behind it, which is nothing, and saying so is what keeps the
/// pointer the default one there.
/// </para>
/// </remarks>
public readonly struct HitResult
{
    private readonly IReadOnlyList<IElement>? _chain;

    public HitResult(LayoutBox? box, IReadOnlyList<IElement> chain)
    {
        Box = box;
        _chain = chain;
    }

    /// <summary>Gets a hit that found nothing.</summary>
    public static HitResult None => new(null, Array.Empty<IElement>());

    /// <summary>Gets the deepest box the point landed in, or null when it landed in none.</summary>
    public LayoutBox? Box { get; }

    /// <summary>
    /// Gets the element the point is over and its ancestors, nearest first. A run of text belongs
    /// to an element rather than being one, so the first entry is the element that set it.
    /// </summary>
    public IReadOnlyList<IElement> Chain => _chain ?? Array.Empty<IElement>();

    /// <summary>Gets the element the point is over, or null when it is over none.</summary>
    public IElement? Element => Chain.Count > 0 ? Chain[0] : null;

    /// <summary>Gets whether the point landed on a run of text a reader could select.</summary>
    public Boolean IsText => Box is { Kind: LayoutKind.Text };

    /// <summary>
    /// Gets whether the point landed on text set down a column. The run's own flow answers it, which
    /// is the same flow the characters were placed and drawn by.
    /// </summary>
    public Boolean IsVerticalText => Box is { Kind: LayoutKind.Text } box && box.Flow.IsVertical;

    /// <summary>
    /// Gets the link the point is on, or null when it is on none.
    /// </summary>
    /// <remarks>
    /// The nearest anchor that names somewhere to go wins, so a click on anything a link wraps follows
    /// the link rather than being lost on the wrapper.
    /// </remarks>
    public LinkTarget? Link
    {
        get
        {
            foreach (var element in Chain)
            {
                if (LinkResolver.Resolve(element) is { } link)
                {
                    return link;
                }
            }

            return null;
        }
    }
}

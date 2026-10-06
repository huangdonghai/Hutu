using AngleSharp.Css;
using AngleSharp.Dom;
using System;
using System.Collections.Generic;

namespace Hutu.Rendering.Interaction;

/// <summary>
/// Holds the states a reading gesture gives the document: what the pointer is over, and what it is
/// pressing.
/// </summary>
/// <remarks>
/// The states are forced onto the elements themselves rather than only tracked here, because it is
/// the style engine that reads them while it matches selectors; what this keeps is the list of
/// elements it marked, so the marks can be taken back when the gesture moves on. Forcing does not
/// travel to ancestors or descendants - the hit already reports the whole chain - so the list is
/// exactly what has to be undone.
/// <para>
/// Every method reports whether it changed anything. That is what lets the view leave the document
/// alone while the pointer moves about within one element: an unchanged set of states means the
/// styles, and so the whole picture, are unchanged too.
/// </para>
/// </remarks>
internal sealed class InteractionState
{
    private readonly List<IElement> _hovered = new();
    private readonly List<IElement> _pressed = new();
    private readonly List<IElement> _visited = new();
    private IElement? _target;

    /// <summary>Marks the elements the pointer is over, unmarking the ones it has left.</summary>
    public Boolean Hover(IReadOnlyList<IElement> elements) =>
        Apply(PseudoClassNames.Hover, _hovered, elements);

    /// <summary>Marks the elements a pointer went down on, which is what <c>:active</c> asks for.</summary>
    public Boolean Press(IReadOnlyList<IElement> elements) =>
        Apply(PseudoClassNames.Active, _pressed, elements);

    /// <summary>Releases whatever was being pressed, as letting the button go does.</summary>
    public Boolean Release() => Apply(PseudoClassNames.Active, _pressed, Array.Empty<IElement>());

    /// <summary>
    /// Marks the anchor a link was followed to, which is what <c>:target</c> answers to. Following
    /// another link moves the mark, so the one before it stops being the target.
    /// </summary>
    public Boolean Target(IElement? element)
    {
        if (ReferenceEquals(_target, element))
        {
            return false;
        }

        _target?.RemovePseudoClass(PseudoClassNames.Target);
        _target = element;
        _target?.SetPseudoClass(PseudoClassNames.Target, true);

        return true;
    }

    /// <summary>
    /// Marks the links whose address the host has been to before, and unmarks the ones it has not.
    /// </summary>
    /// <remarks>
    /// Where a reader has been is the host's to know - it is browsing history rather than anything the
    /// page says - so the answer is asked for per link and only the marks are kept here. A link that
    /// resolves to no address at all cannot carry the mark: an anchor with nothing to follow is not a
    /// link, and marking it would paint a placeholder as if it had been read.
    /// </remarks>
    public Boolean Visited(IEnumerable<IElement> links, Func<String, Boolean> isVisited)
    {
        var visited = new List<IElement>();

        foreach (var link in links)
        {
            if (LinkResolver.Resolve(link) is { } target && isVisited(target.Url))
            {
                visited.Add(link);
            }
        }

        return Apply(PseudoClassNames.Visited, _visited, visited);
    }

    /// <summary>
    /// Takes every mark back. A replaced document leaves nothing to hover, press or have followed a
    /// link to, and the marks belong to elements that are about to be unreachable.
    /// </summary>
    public void Clear()
    {
        Unmark(PseudoClassNames.Hover, _hovered);
        Unmark(PseudoClassNames.Active, _pressed);
        Unmark(PseudoClassNames.Visited, _visited);
        Target(null);
    }

    /// <summary>
    /// Moves a state from whatever carried it to the elements that carry it now. An unchanged set is
    /// reported as no change, because the styles it would produce are the ones already computed.
    /// </summary>
    private static Boolean Apply(String pseudoClass, List<IElement> marked, IReadOnlyList<IElement> elements)
    {
        if (IsSame(marked, elements))
        {
            return false;
        }

        Unmark(pseudoClass, marked);

        foreach (var element in elements)
        {
            element.SetPseudoClass(pseudoClass, true);
            marked.Add(element);
        }

        return true;
    }

    private static void Unmark(String pseudoClass, List<IElement> marked)
    {
        foreach (var element in marked)
        {
            // Removed rather than forced off, so the pseudo-class goes back to being answered the way
            // the engine answers it rather than by a standing override.
            element.RemovePseudoClass(pseudoClass);
        }

        marked.Clear();
    }

    private static Boolean IsSame(List<IElement> marked, IReadOnlyList<IElement> elements)
    {
        if (marked.Count != elements.Count)
        {
            return false;
        }

        for (var index = 0; index < marked.Count; index++)
        {
            if (!ReferenceEquals(marked[index], elements[index]))
            {
                return false;
            }
        }

        return true;
    }
}

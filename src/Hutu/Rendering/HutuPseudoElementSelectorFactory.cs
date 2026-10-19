using AngleSharp.Css;
using AngleSharp.Css.Dom;
using AngleSharp.Dom;
using System;

namespace Hutu.Rendering;

/// <summary>
/// The pseudo-element names the selector parser is told about: the ones AngleSharp already knows, plus
/// <c>::marker</c>.
/// </summary>
/// <remarks>
/// <para>
/// A name the parser does not know is not kept as an unknown pseudo-element but taken as a broken
/// selector, and an invalid selector takes the whole rule with it: <c>li::marker { color: red }</c>
/// arrives with no selector at all and with its own text already cut down to the declarations. Nothing
/// downstream can then tell which element the arm was written for, so the sheet's marker rules are
/// unreadable however the engine is arranged. The name is registered here rather than in the vendored
/// parser because the factory is a configuration service - <c>IPseudoElementSelectorFactory</c> - and a
/// consumer is allowed to hold the one its documents are parsed with.
/// </para>
/// <para>
/// Only the name is added, not a match: the marker box is a box this engine makes for itself, and an arm
/// written for it selects no element of the document, which is how <c>::before</c> and <c>::first-line</c>
/// are already held. See <see cref="Boxes.PseudoRuleIndex"/>, which reads the arms out of the rule text.
/// </para>
/// </remarks>
internal sealed class HutuPseudoElementSelectorFactory : DefaultPseudoElementSelectorFactory
{
    /// <summary>The name Pseudo-Elements 4 §4.2 gives the marker box of a list item.</summary>
    internal const String Marker = "marker";

    public HutuPseudoElementSelectorFactory()
    {
        Register(Marker, new MarkerSelector());
    }

    /// <summary>
    /// The step a selector ends with when it is written for a marker box. It matches nothing, and says
    /// so to a visitor as the pseudo-element its name states.
    /// </summary>
    /// <remarks>
    /// AngleSharp's own <c>PseudoElementSelector</c> is internal to the library, so the consumer that
    /// registers a name has to bring the selector with it. What that selector has to answer is fixed by
    /// the contract: the specificity a pseudo-element counts for, which Selectors 4 §17 puts where a
    /// type selector stands, and the text the sheet wrote.
    /// </remarks>
    private sealed class MarkerSelector : ISelector
    {
        public Boolean Match(IElement element, IElement? scope) => false;

        public String Text => PseudoElementNames.Separator + Marker;

        public Priority Specificity => Priority.OneTag;

        public void Accept(ISelectorVisitor visitor) => visitor.PseudoElement(Marker);
    }
}

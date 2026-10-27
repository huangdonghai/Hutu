using AngleSharp.Css;
using AngleSharp.Css.Dom;
using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using System;

namespace Hutu.Rendering;

/// <summary>
/// The pseudo-class names the selector parser is told about, so far the <c>:link</c> family.
/// </summary>
/// <remarks>
/// <para>
/// AngleSharp reads a link as an anchor whose <c>href</c> is a non-empty string, and it answers the same
/// question for <c>:link</c>, <c>:visited</c> and <c>:any-link</c>. The chapter says otherwise: Selectors 4
/// §8.2 makes an <c>&lt;a&gt;</c> with an <c>href</c> attribute a link whatever that attribute holds, and
/// <c>href=""</c> - which resolves to the document the anchor sits in - is a link the reader can have been
/// to. A page that writes <c>a:link { color: … }</c> over an <c>href=""</c> therefore has its rule dropped
/// by the borrowed matcher, and what is left standing is the user agent's own colour for a link: the borrowed
/// pages that ask about a link to the page itself came out blue on every one of them.
/// </para>
/// <para>
/// The names are registered here rather than in the vendored matcher because a factory is a configuration
/// service - <c>IPseudoClassSelectorFactory</c> - and a consumer is allowed to hold the one its documents are
/// parsed with. It is registered before the CSS services are added, which is what lets the vendored wrapper
/// that carries the forced states sit on top of this one rather than under it; see
/// <see cref="HutuPseudoElementSelectorFactory"/>, which is placed on the same terms.
/// </para>
/// </remarks>
internal sealed class HutuLinkPseudoClassSelectorFactory : DefaultPseudoClassSelectorFactory
{
    public HutuLinkPseudoClassSelectorFactory()
    {
        // The three names the borrowed factory already holds are taken back before being put down again:
        // its register refuses a name it has, and the link family is exactly what it has.
        Replace(PseudoClassNames.Link, new LinkSelector(read: false));
        Replace(PseudoClassNames.Visited, new LinkSelector(read: true));
        Replace(PseudoClassNames.AnyLink, new LinkSelector(read: null));
    }

    /// <summary>Puts one selector down under a name the base factory already answers for.</summary>
    private void Replace(String name, ISelector selector)
    {
        Unregister(name);
        Register(name, selector);
    }

    /// <summary>
    /// One step of the link family, asked of the three elements the chapter gives a link state to. Which
    /// of the answers is wanted is settled when the step is written, not per match: <c>:link</c> wants the
    /// unread link, <c>:visited</c> the read one, and <c>:any-link</c> either.
    /// </summary>
    private sealed class LinkSelector : ISelector
    {
        /// <summary>
        /// Whether the link has to have been read, has to have not, or is asked only to be a link - which
        /// is the <c>null</c> the step <c>:any-link</c> states.
        /// </summary>
        private readonly Boolean? _read;

        internal LinkSelector(Boolean? read) => _read = read;

        public Boolean Match(IElement element, IElement? scope) =>
            // The attribute is read rather than the resolved address: an <c>href</c> the page wrote is what
            // makes the anchor a link, and where it points is the reader's history rather than the page's.
            States(element) is { } link &&
            link.HasAttribute(AttributeNames.Href) &&
            (_read is null || IsRead(link) == _read.Value);

        /// <summary>Gets the element when it is one that carries a link state, and nothing otherwise.</summary>
        private static IElement? States(IElement element) =>
            element is IHtmlAnchorElement or IHtmlAreaElement or IHtmlLinkElement ? element : null;

        /// <summary>
        /// Gets whether the reader has been to where that link points. The document says so through the
        /// mark the host sets - <c>:visited</c> forced onto the element - because a page parsed on its own
        /// has no history to ask.
        /// </summary>
        private static Boolean IsRead(IElement element) =>
            element.GetPseudoClass(PseudoClassNames.Visited) == true;

        public String Text => PseudoClassNames.Separator + (_read switch
        {
            null => PseudoClassNames.AnyLink,
            false => PseudoClassNames.Link,
            true => PseudoClassNames.Visited,
        });

        public Priority Specificity => Priority.OneClass;

        public void Accept(ISelectorVisitor visitor) =>
            visitor.PseudoClass(_read switch
            {
                null => PseudoClassNames.AnyLink,
                false => PseudoClassNames.Link,
                true => PseudoClassNames.Visited,
            });
    }
}

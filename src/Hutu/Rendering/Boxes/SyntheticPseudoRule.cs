using AngleSharp;
using AngleSharp.Css;
using AngleSharp.Css.Dom;
using AngleSharp.Dom;
using System;
using System.IO;

namespace Hutu.Rendering.Boxes;

/// <summary>
/// A rule the CSS parser would not keep because it names a pseudo-element the parser does not
/// know, such as <c>::scroll-marker</c>. The declarations are those of a rule parsed without the
/// pseudo-element; this wrapper puts the original selector back so the pseudo-element index can
/// read it, while everything the cascade asks about the rule itself is answered by the inner rule.
/// </summary>
internal sealed class SyntheticPseudoRule : ICssStyleRule
{
    private readonly ICssStyleRule _inner;
    private readonly String _selectorText;

    internal SyntheticPseudoRule(ICssStyleRule inner, String selectorText)
    {
        _inner = inner;
        _selectorText = selectorText;
    }

    public String SelectorText
    {
        get => _selectorText;
        set => _inner.SelectorText = value;
    }

    public ICssStyleDeclaration Style => _inner.Style;

    public ISelector Selector => _inner.Selector;

    public Boolean TryMatch(IElement element, IElement? scope, out Priority specificity) =>
        _inner.TryMatch(element, scope, out specificity);

    public ICssRuleList Rules => _inner.Rules;

    public CssRuleType Type => _inner.Type;

    public String CssText
    {
        get => _inner.CssText;
        set => _inner.CssText = value;
    }

    public ICssRule Parent => _inner.Parent;

    public ICssStyleSheet Owner => _inner.Owner;

    public void SetParent(ICssRule rule) => _inner.SetParent(rule);

    public void SetOwner(ICssStyleSheet sheet) => _inner.SetOwner(sheet);

    public String ToCss() => _inner.ToCss();

    public void ToCss(TextWriter writer, IStyleFormatter formatter) => _inner.ToCss(writer, formatter);
}

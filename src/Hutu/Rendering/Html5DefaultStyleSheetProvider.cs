using AngleSharp.Css;
using AngleSharp.Css.Dom;
using AngleSharp.Css.Parser;
using System;

namespace Hutu.Rendering;

/// <summary>
/// Supplies the HTML5 user-agent defaults that AngleSharp.Css does not ship.
/// </summary>
/// <remarks>
/// The default stylesheet AngleSharp.Css works from is the CSS 2.2 sample, which predates HTML5:
/// elements such as <c>main</c>, <c>section</c>, <c>article</c>, <c>nav</c>, <c>aside</c>,
/// <c>header</c>, <c>footer</c> and <c>figure</c> are never mentioned, so they compute to the
/// initial <c>display</c> value - <c>inline</c> - and their block content would be lost. The same
/// sheet only knows <c>head</c> as hidden, so an element HTML never renders - a <c>script</c> or a
/// <c>style</c> in the body, where the spec allows both - would otherwise lay its source text out.
/// <para>
/// This is registered as an additional provider rather than a replacement: AngleSharp concatenates
/// every provider's sheet ahead of the author sheets, so the defaults stay at user-agent level and
/// author rules still win.
/// </para>
/// </remarks>
public sealed class Html5DefaultStyleSheetProvider : ICssDefaultStyleSheetProvider
{
    private const String Source = @"
main, article, aside, figure, figcaption, footer, header, hgroup, nav, section, details, dialog, search { display: block }
summary { display: list-item }
mark { background-color: yellow; color: black }
ruby, rb, rt, rtc { display: inline }
rt { font-size: 50%; line-height: 1 }
area, base, basefont, datalist, head, link, meta, noembed, noframes, param, rp, script, style, template, title { display: none }
";

    private ICssStyleSheet? _default;

    public ICssStyleSheet Default => _default ??= Parse(Source);

    public void SetDefault(ICssStyleSheet? sheet) => _default = sheet;

    public void SetDefault(String source) => _default = Parse(source);

    public void AppendDefault(String source) => SetDefault($"{Source}\r\n{source}");

    private static ICssStyleSheet Parse(String source) => new CssParser().ParseStyleSheet(source);
}

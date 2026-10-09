using AngleSharp.Css;
using AngleSharp.Css.Dom;
using AngleSharp.Css.Parser;
using System;

namespace Hutu.Rendering;

/// <summary>
/// Supplies the reader's own user-agent stylesheet: what every document is read against when it says
/// nothing itself.
/// </summary>
/// <remarks>
/// The sheet AngleSharp.Css works from is the CSS 2.2 sample, which two things make unusable as it
/// stands. It predates HTML5, so elements such as <c>main</c>, <c>section</c>, <c>article</c>,
/// <c>nav</c>, <c>aside</c>, <c>header</c>, <c>footer</c> and <c>figure</c> are never mentioned and
/// compute to the initial <c>display</c> - <c>inline</c> - which loses their block content; the same
/// sheet knows only <c>head</c> as hidden, so a <c>script</c> or a <c>style</c> in the body would lay
/// its source text out.
/// <para>
/// What is here is the sample's other work: which elements are blocks, what a table's parts are, the
/// sizes a heading starts at, the marks of a list. The space HTML leaves around a heading or a
/// paragraph is <em>not</em> here, because the sample states it to the page - <c>h1 { margin: .67em
/// 0 }</c> - and a column reads the vertical pair down itself, so every heading and paragraph would
/// be pushed away from the head of its column by an amount that differs per element. A browser states
/// it as <c>margin-block</c>, which lands beside a column instead; this reader supplies it in the one
/// place that knows which way the flow runs, which is
/// <see cref="ComputedStyle"/>'s reading of a thickness.
/// </para>
/// <para>
/// It replaces the sheet AngleSharp.Css ships rather than adding to it, and is registered ahead of
/// the CSS services so that theirs is never supplied: two sheets at the user-agent level would be two
/// sources to reconcile, and a property both state would be decided by registration order.
/// </para>
/// </remarks>
public sealed class UserAgentStyleSheetProvider : ICssDefaultStyleSheetProvider
{
    private const String Source = @"
html, address,
blockquote,
body, dd, div,
dl, dt, fieldset, form,
frame, frameset,
h1, h2, h3, h4,
h5, h6, noframes,
ol, p, ul, center,
dir, hr, menu, pre   { display: block; unicode-bidi: embed }
li              { display: list-item }
head            { display: none }
table           { display: table }
tr              { display: table-row }
thead           { display: table-header-group }
tbody           { display: table-row-group }
tfoot           { display: table-footer-group }
col             { display: table-column }
colgroup        { display: table-column-group }
td, th          { display: table-cell }
caption         { display: table-caption }
th              { font-weight: bolder; text-align: center }
caption         { text-align: center }
body            { margin: 8px }
h1              { font-size: 2em }
h2              { font-size: 1.5em }
h3              { font-size: 1.17em }
h5              { font-size: .83em }
h6              { font-size: .75em }
h1, h2, h3, h4,
h5, h6, b,
strong          { font-weight: bolder }
i, cite, em,
var, address    { font-style: italic }
pre, tt, code,
kbd, samp       { font-family: monospace }
pre             { white-space: pre }
button, textarea,
input, select   { display: inline-block }
big             { font-size: 1.17em }
small, sub, sup { font-size: .83em }
sub             { vertical-align: sub }
sup             { vertical-align: super }
table           { border-spacing: 2px; }
thead, tbody,
tfoot           { vertical-align: middle }
td, th, tr      { vertical-align: inherit }
s, strike, del  { text-decoration: line-through }
hr              { border: 1px inset }
ol, ul, dir,
menu            { list-style-type: disc; list-style-position: outside }
ol              { list-style-type: decimal }
u, ins          { text-decoration: underline }
br::before      { content: '\A'; white-space: pre-line }

/* this will slow down css matching, so I disable it first, by HDH
*::before, *::after { display: inline }
*/

center          { text-align: center }
:link, :visited { text-decoration: underline }
:focus          { outline: thin dotted invert }

/* The elements HTML5 added, which the CSS 2.2 sheet this one is built from never knew. */
main, article, aside, figure, figcaption, footer, header, hgroup, nav, section, details, dialog, search { display: block }
summary         { display: list-item }
mark            { background-color: yellow; color: black }
ruby, rb, rt, rtc { display: inline }
rt              { font-size: 50%; line-height: 1 }
area, base, basefont, datalist, link, meta, noembed, noframes, param, rp, script, style, template, title { display: none }

/* Begin bidirectionality settings (do not change) */
BDO[DIR='ltr']  { direction: ltr; unicode-bidi: bidi-override }
BDO[DIR='rtl']  { direction: rtl; unicode-bidi: bidi-override }

*[DIR='ltr']    { direction: ltr; unicode-bidi: embed }
*[DIR='rtl']    { direction: rtl; unicode-bidi: embed }
";

    private ICssStyleSheet? _default;

    public ICssStyleSheet Default => _default ??= Parse(Source);

    public void SetDefault(ICssStyleSheet? sheet) => _default = sheet;

    public void SetDefault(String source) => _default = Parse(source);

    public void AppendDefault(String source) => SetDefault($"{Source}\r\n{source}");

    private static ICssStyleSheet Parse(String source) => new CssParser().ParseStyleSheet(source);
}

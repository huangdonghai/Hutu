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
/// The room a list keeps at the edge its items begin at is the one spacing that is <em>not</em> stated
/// here, and what decides that is who has to yield to whom. Given as a margin, a page that resets its
/// list margins - which nearly every page does - would take away the space its markers stand in and they
/// would be drawn off the edge of the list. Given as this sheet states a spacing, it comes back to the
/// reader as a declaration of the page, because nothing in the cascade here tells the two sheets apart:
/// a page that says <c>padding: 0</c> and means it - a wiki's lists do, indenting by a margin of their
/// own instead - would be overruled by the reader, and its items would begin forty pixels further along
/// the line than the page asked. It is therefore supplied where a reader's own value belongs, beside the
/// space around a heading, which a page's declaration does beat: see <c>ComputedStyle</c>'s reading of a
/// thickness. Being a padding it still belongs to the list's own box, so the list's background reaches
/// under its markers as it does in a browser, and being on the inline axis it lands at the head of a
/// column rather than to the left of a row.
/// </para>
/// <para>
/// It replaces the sheet AngleSharp.Css ships rather than adding to it, and is registered ahead of
/// the CSS services so that theirs is never supplied: two sheets at the user-agent level would be two
/// sources to reconcile, and a property both state would be decided by registration order.
/// </para>
/// </remarks>
internal sealed class UserAgentStyleSheetProvider : ICssDefaultStyleSheetProvider
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
th              { font-weight: bolder }
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

/* The legacy scrolling element is still a box the page can size and clip, even though this reader
 * does not scroll it: an inline-block that hides what leaves it, which is how a browser states it. */
marquee         { display: inline-block; overflow: hidden }

/* HTML Standard §15.3.10. A form control is not aligned by what surrounds it: a field states its
 * text at the head of the line, and the three pressable inputs and a button state their label at
 * the middle of it. Without the pair, a page that centers a column - a header's search box, its
 * sign-in button - would have the text inside them slide to the middle along with it.
 *
 * The same section states the label at the middle of the control's own box, with align-content, so
 * that it keeps to the middle of whatever height a page gives the control: a search field that a
 * flex header stretches to 44px holds one line of about 14, and the two lines left over are split
 * above and below it. A control that sizes itself to its content has no room over and is left
 * where it stands, which is what the property asks for. The four types the section leaves out -
 * file, range, checkbox and radio - are left out here too, by naming the ones it keeps, as the
 * selectors a user agent sheet is written in are the plain kind.
 */
input, select,
textarea        { text-align: initial }

input[type=reset],
input[type=button],
input[type=submit],
button          { text-align: center }
input[type=button],
input[type=color],
input[type=date],
input[type=datetime-local],
input[type=email],
input[type=month],
input[type=number],
input[type=password],
input[type=reset],
input[type=search],
input[type=submit],
input[type=tel],
input[type=text],
input[type=time],
input[type=url],
input[type=week],
button,
select          { align-content: center }
big             { font-size: 1.17em }
small, sub, sup { font-size: .83em }
sub             { vertical-align: sub }
sup             { vertical-align: super }
/* The distance between the cells of a table is not here: it is one of the readings HTML makes of a
 * table's own attributes, and the two belong together - see PresentationalHints.Spacing. The same goes
 * for the room a cell keeps its content at, which is a pixel that only stands where the table said no
 * cellpadding of its own, and for the bevel a table with a border attribute is drawn in. */
thead, tbody,
tfoot           { vertical-align: middle }
td, th, tr      { vertical-align: inherit }
s, strike, del  { text-decoration: line-through }
hr              { border: 1px inset }
ol, ul, dir,
menu            { list-style-type: disc; list-style-position: outside }
ol              { list-style-type: decimal }
u, ins          { text-decoration: underline }

/* CSS 2.1 §12.1 suggests a line break be drawn by giving br a generated newline; it is left out
 * here on purpose, because this engine gives br a box of its own (BoxKind.LineBreak) and a
 * generated newline beside it would break the line twice. The display: inline the same section
 * gives the two pseudo-elements is left out for the same reason - it is their initial value.
 */

center          { text-align: center }
:link, :visited { text-decoration: underline }
:focus          { outline: thin dotted invert }

/* The elements HTML5 added, which the CSS 2.2 sheet this one is built from never knew. */
main, article, aside, figure, figcaption, footer, header, hgroup, nav, section, details, dialog, search { display: block }
summary         { display: block }

/* HTML's rendering sheet gives the disclosure marker to the first summary of a details and to no other:
 * a second summary, and one that stands outside a details, is a plain block. The marker is the list
 * item's own, so the list-style the sheet states - a closed triangle inside the line - is what turns
 * when the details opens. */
details > summary:first-of-type { display: list-item; list-style: disclosure-closed inside }
details[open] > summary:first-of-type { list-style-type: disclosure-open }
mark            { background-color: yellow; color: black }
ruby, rb, rt, rtc { display: inline }
rt              { font-size: 50%; line-height: 1 }
area, base, basefont, datalist, link, meta, noembed, noframes, param, rp, script, style, template, title { display: none }

/* Begin bidirectionality settings (do not change) */
BDO[DIR='ltr']  { direction: ltr; unicode-bidi: isolate-override }
BDO[DIR='rtl']  { direction: rtl; unicode-bidi: isolate-override }

*[DIR='ltr']    { direction: ltr; unicode-bidi: isolate }
*[DIR='rtl']    { direction: rtl; unicode-bidi: isolate }
BDI             { unicode-bidi: isolate }
";

    private ICssStyleSheet? _default;

    public ICssStyleSheet Default => _default ??= Parse(Source);

    public void SetDefault(ICssStyleSheet? sheet) => _default = sheet;

    public void SetDefault(String source) => _default = Parse(source);

    public void AppendDefault(String source) => SetDefault($"{Source}\r\n{source}");

    private static ICssStyleSheet Parse(String source) => new CssParser().ParseStyleSheet(source);
}

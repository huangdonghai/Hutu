using Hutu.Rendering.Layout;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Hutu.Tests;

/// <summary>
/// Covers the fragmentation properties (CSS Fragmentation 3): where a box lets a fragmentainer break
/// around it and inside it, and the lines it keeps together at a fragment's ends.
/// </summary>
[TestFixture]
public sealed class BreakStyleTests
{
    [Test]
    public async Task ABoxThatSaysNothingLetsTheContentBreakWhereItMay()
    {
        var breaks = await Read("<div id='d'>x</div>");

        Assert.That(breaks.Before, Is.EqualTo(BreakValue.Auto));
        Assert.That(breaks.After, Is.EqualTo(BreakValue.Auto));
        Assert.That(breaks.Inside, Is.EqualTo(BreakValue.Auto));
        Assert.That(breaks.Widows, Is.EqualTo(2), "the fewest lines every fragment keeps at its end");
        Assert.That(breaks.Orphans, Is.EqualTo(2), "and at its start");
    }

    [Test]
    public async Task TheModernLonghandsAreRead()
    {
        var breaks = await ReadWith(
            "break-before: page; break-after: avoid; break-inside: avoid");

        Assert.That(breaks.Before, Is.EqualTo(BreakValue.Page));
        Assert.That(breaks.After, Is.EqualTo(BreakValue.Avoid));
        Assert.That(breaks.Inside, Is.EqualTo(BreakValue.Avoid));
    }

    [Test]
    public async Task TheLegacyPageBreakLonghandsMeanTheSame()
    {
        var breaks = await ReadWith(
            "page-break-before: always; page-break-after: avoid; page-break-inside: avoid");

        Assert.That(breaks.Before, Is.EqualTo(BreakValue.Page), "always is a forced page break under the older name");
        Assert.That(breaks.After, Is.EqualTo(BreakValue.Avoid));
        Assert.That(breaks.Inside, Is.EqualTo(BreakValue.Avoid));
    }

    [Test]
    public async Task TheModernNameIsReadAheadOfTheOlderOne()
    {
        var breaks = await ReadWith(
            "break-before: avoid; page-break-before: always");

        Assert.That(breaks.Before, Is.EqualTo(BreakValue.Avoid), "the page wrote the modern longhand too");
    }

    [Test]
    public async Task ARectoBreakForcesARightPage()
    {
        var breaks = await ReadWith("break-before: recto");

        Assert.That(breaks.Before, Is.EqualTo(BreakValue.Right));
    }

    [Test]
    public async Task TheLineCountsAreReadAndNeverFewerThanOne()
    {
        var breaks = await ReadWith("widows: 3; orphans: 4");
        Assert.That(breaks.Widows, Is.EqualTo(3));
        Assert.That(breaks.Orphans, Is.EqualTo(4));

        var invalid = await ReadWith("widows: 0; orphans: abc");
        Assert.That(invalid.Widows, Is.EqualTo(2), "a count under one says nothing");
        Assert.That(invalid.Orphans, Is.EqualTo(2));
    }

    private static async Task<BreakStyle> Read(String content) =>
        Find(await Render(content)).Source.Style.Breaks;

    private static Task<BreakStyle> ReadWith(String declarations) =>
        Read("<style>div { " + declarations + " }</style><div id='d'>x</div>");

    private static async Task<HutuViewInternal> Render(String content)
    {
        var view = new HutuViewInternal(new FakeTextMeasurer());
        view.SetViewportSize(400, 600);
        await view.SetHtml(
            "<html><head><style>div { margin: 0 }</style></head>" +
            "<body style='margin:0;font-size:10px;line-height:10px'>" + content + "</body></html>");
        return view;
    }

    private static LayoutBox Find(HutuViewInternal view) =>
        Walk(view.Layout!.Root).First(box => box.Source.Element?.GetAttribute("id") == "d");

    private static IEnumerable<LayoutBox> Walk(LayoutBox node)
    {
        yield return node;

        foreach (var child in node.Children)
        {
            foreach (var descendant in Walk(child))
            {
                yield return descendant;
            }
        }
    }
}

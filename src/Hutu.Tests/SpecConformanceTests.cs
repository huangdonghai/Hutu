using Hutu.Rendering.Layout;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Hutu.Tests;

/// <summary>
/// The sizing and line rules that were found wrong by comparing the engine against a browser on a
/// real page at 3840 wide, each asserted here as a document so that the cascade, the box tree and the
/// formatting context are all exercised. Every case names the clause it follows.
/// </summary>
[TestFixture]
public sealed class SpecConformanceTests
{
    [Test]
    public async Task AnAbsoluteBoxIsClampedByMaxWidthAndCentredByItsAutoMargins()
    {
        using var view = CreateView(400);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div id='a' style='position:absolute;left:0;right:0;max-width:200px;margin:0 auto;height:10px'></div>" +
            "</body></html>");

        // CSS 2.1 §10.4: a tentative width outside the constraints is worked out again with the bound
        // standing in for the computed width, and §10.3.7 then finds all three of left, width and right
        // definite - the case in which a pair of auto margins shares the slack and centres the box.
        Assert.That(Find(view, "a").BorderBox.Width, Is.EqualTo(200).Within(0.5));
        Assert.That(Find(view, "a").BorderBox.X, Is.EqualTo(100).Within(0.5), "(400 - 200) / 2");
    }

    [Test]
    public async Task AFixedBoxIsClampedByMinWidthAndCentredByItsAutoMargins()
    {
        using var view = CreateView(400);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div id='a' style='position:fixed;left:0;right:0;min-width:500px;margin:0 auto;height:10px'></div>" +
            "</body></html>");

        Assert.That(Find(view, "a").BorderBox.Width, Is.EqualTo(500).Within(0.5), "min-width wins over the space between the edges");

        // The box is wider than the block it is centred in, and §10.3.7 will not centre it into
        // negative margins: with nothing left to share, margin-left is set to zero and the box keeps
        // the block's start edge, overflowing the end.
        Assert.That(Find(view, "a").BorderBox.X, Is.EqualTo(0).Within(0.5));
    }

    [Test]
    public async Task AnAbsoluteBoxIsClampedByMaxHeightAndCentredByItsAutoMargins()
    {
        using var view = CreateView(400);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div style='position:relative;height:400px'>" +
            "<div id='a' style='position:absolute;top:0;bottom:0;max-height:100px;margin:auto 0;width:10px'></div>" +
            "</div></body></html>");

        // CSS 2.1 §10.7 with §10.6.4: the height the bound settled on is definite, so a pair of auto
        // vertical margins shares what the containing block leaves below it.
        Assert.That(Find(view, "a").BorderBox.Height, Is.EqualTo(100).Within(0.5));
        Assert.That(Find(view, "a").BorderBox.Y, Is.EqualTo(150).Within(0.5), "(400 - 100) / 2");
    }

    [Test]
    public async Task AFlexContainerContributesTheWidthItDeclares()
    {
        using var view = CreateView(400);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div id='row' style='display:inline-flex'>" +
            "<span id='icon' style='display:inline-flex;align-items:center;width:30px;height:10px'></span>" +
            "<span style='display:block;width:10px'></span>" +
            "</div></body></html>");

        // A box's max-content contribution is the content size of a hypothetical auto-sized float that
        // holds it (CSS Sizing 3 §5.2), and a box states how wide it is there - which is also Flexbox's
        // rule for an item, "the larger of its outer max-content size and outer preferred size if that
        // is not auto, clamped by its min/max main size" (§9.9.3).
        Assert.That(Find(view, "icon").BorderBox.Width, Is.EqualTo(30).Within(0.5), "its own width, not its empty content's");
        Assert.That(Find(view, "row").BorderBox.Width, Is.EqualTo(40).Within(0.5), "30 + 10");
    }

    [Test]
    public async Task AUnitlessLineHeightIsResolvedAgainstEachElementsOwnSize()
    {
        using var view = CreateView(400);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div style='font-size:16px;line-height:1.5'>" +
            "<span id='c' style='display:inline-block;font-size:12px'>x</span>" +
            "</div></body></html>");

        // CSS 2.2 §10.8.1: a <number> computes to the number, and its used value is that number times
        // the font size of whichever element it lands on - so a 12px child of a 1.5 line is 18 tall.
        Assert.That(Find(view, "c").BorderBox.Height, Is.EqualTo(18).Within(0.5));
    }

    [Test]
    public async Task APercentageLineHeightIsResolvedWhereItIsDeclared()
    {
        using var view = CreateView(400);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div style='font-size:16px;line-height:150%'>" +
            "<span id='c' style='display:inline-block;font-size:12px'>x</span>" +
            "</div></body></html>");

        // The other half of §10.8.1: a percentage's computed value is the percentage times the font size
        // of the element that declared it, so 150% of 16 is 24 and that is what reaches the 12px child.
        Assert.That(Find(view, "c").BorderBox.Height, Is.EqualTo(24).Within(0.5));
    }

    [Test]
    public async Task LineHeightInheritKeepsTheNumberItInherited()
    {
        using var view = CreateView(400);
        await view.SetHtml(
            "<html style='line-height:1.5'><body style='margin:0;line-height:inherit;font-size:16px'>" +
            "<span id='c' style='display:inline-block;font-size:12px'>x</span>" +
            "</body></html>");

        // `inherit` asks for the value the parent computed, and a number's computed value is the number:
        // the chain below the wrapper goes on re-resolving it against each element's own size.
        Assert.That(Find(view, "c").BorderBox.Height, Is.EqualTo(18).Within(0.5));
    }

    [Test]
    public async Task NowrapTextOverflowsItsLineInsteadOfWrapping()
    {
        using var view = CreateView(400);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div id='box' style='width:20px'>" +
            "<span id='s' style='white-space:nowrap'>abcdefgh</span>" +
            "</div></body></html>");

        // CSS 2.1 §16.6 / CSS Text 3 §5.1: `nowrap` collapses white space as `normal` does but
        // suppresses the breaks, so the text stays on one line and overflows the box it was put in.
        Assert.That(Find(view, "box").BorderBox.Height, Is.EqualTo(10).Within(0.5), "one line, not two");
        Assert.That(Find(view, "s").BorderBox.Width, Is.EqualTo(40).Within(0.5), "eight characters at 5px");
    }

    [Test]
    public async Task ACentredFlexLineOverflowsBothWays()
    {
        using var view = CreateView(400);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div style='display:flex;justify-content:center;width:100px'>" +
            "<div id='a' style='flex:0 0 auto;width:40px;height:10px'></div>" +
            "<div style='flex:0 0 auto;width:40px;height:10px'></div>" +
            "<div style='flex:0 0 auto;width:40px;height:10px'></div>" +
            "</div></body></html>");

        // CSS Flexbox 1 §8.2, `center`: with a negative leftover free space "the flex items will
        // overflow equally in both directions" - the items do not fall back to the start.
        Assert.That(Find(view, "a").BorderBox.X, Is.EqualTo(-10).Within(0.5), "(100 - 120) / 2");
    }

    [Test]
    public async Task AnInlineElementsEndMarginTakesItsRoomOnTheLine()
    {
        using var view = CreateView(400);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div>" +
            "<span style='margin-right:20px'>ab</span><span id='b'>cd</span>" +
            "</div></body></html>");

        // CSS 2.1 §8.1: the horizontal margins of an inline box take their place on the line - only the
        // vertical ones are the ones that never grow the line box.
        Assert.That(Find(view, "b").BorderBox.X, Is.EqualTo(30).Within(0.5), "10px of text + the 20px margin");
    }

    [Test]
    public async Task AFlexRowOfNowrapLabelsKeepsEveryLabelWhole()
    {
        using var view = CreateView(400);
        await view.SetHtml(
            "<html><head><style>" +
            "ul{display:flex;justify-content:center;width:30px;height:18px;line-height:18px;margin:0;padding:0;list-style:none}" +
            "li{padding:0 4px}li:first-child{padding-left:0}" +
            ".name{font-size:12px;white-space:nowrap;overflow:hidden}" +
            "</style></head><body>" +
            "<ul><li><a href='#'><span class='name' id='n1'>abcd</span></a></li>" +
            "<li><a href='#'><span class='name' id='n2'>efgh</span></a></li></ul></body></html>");

        // A `nowrap` run offers no soft break inside itself, so its min-content size is its max-content
        // size (CSS Text 3 §5.1) - and a flex item's automatic minimum size is built from that, which is
        // what keeps two labels of 20px in a 30px row whole and overflowing the row rather than each
        // being squeezed to a single character.
        Assert.That(Find(view, "n1").BorderBox.Width, Is.EqualTo(20).Within(0.5));
        Assert.That(Find(view, "n2").BorderBox.Width, Is.EqualTo(20).Within(0.5));
    }

    [Test]
    public async Task ACellThatClipsItsOverflowDoesNotPushItsRowOpen()
    {
        using var view = CreateView(400);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div id='grid' style='display:grid;width:100px;grid-template-columns:50px 50px;gap:0;height:20px'>" +
            "<div id='cell' style='overflow:hidden'><span style='display:block;height:60px'>x</span></div>" +
            "</div></body></html>");

        // CSS Grid 6.6: the automatic minimum size of an item whose block overflow is a scroll axis is
        // zero, so the row takes the grid's own definite height and the taller content overflows and is
        // clipped - rather than the row growing to the content and the page showing a line the grid
        // was sized to hide.
        Assert.That(Find(view, "grid").BorderBox.Height, Is.EqualTo(20).Within(0.5));
        Assert.That(Find(view, "cell").BorderBox.Height, Is.EqualTo(20).Within(0.5), "the cell is the row, not its content");
    }

    [Test]
    public async Task ACellThatShowsItsOverflowStillPushesItsRowOpen()
    {
        using var view = CreateView(400);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div id='grid' style='display:grid;width:100px;grid-template-columns:50px 50px;gap:0;height:20px'>" +
            "<div id='cell'><span style='display:block;height:60px'>x</span></div>" +
            "</div></body></html>");

        // The other side of the same rule: `visible` is not a scroll axis, so the item keeps its
        // content-based floor and the row grows to what the cell holds.
        Assert.That(Find(view, "cell").BorderBox.Height, Is.EqualTo(60).Within(0.5));
    }

    private static HutuViewInternal CreateView(Int32 viewportWidth)
    {
        var view = new HutuViewInternal(new FakeTextMeasurer());
        view.SetViewportSize(viewportWidth, 600);
        return view;
    }

    private static LayoutBox Find(HutuViewInternal view, String id)
    {
        var root = view.Layout?.Root;

        Assert.That(root, Is.Not.Null, "the document produced no layout");

        foreach (var node in Walk(root!))
        {
            if (node.Source.Element?.GetAttribute("id") == id)
            {
                return node;
            }
        }

        Assert.Fail($"no box with id '{id}' was laid out");
        return null!;
    }

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

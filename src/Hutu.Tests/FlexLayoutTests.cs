using Hutu.Rendering.Layout;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Hutu.Tests;

/// <summary>
/// The flex formatting context: the main axis both ways, wrapping, the flexible lengths, the
/// alignment keywords and the gaps.
/// </summary>
/// <remarks>
/// A document is the input so that <c>display: flex</c>, the flex longhands and the alignment
/// keywords all come from a real cascade - the reading layer, not the algorithm, is where the
/// upstream value shapes are most likely to be misread. The assertions are about the geometry the
/// items end up with, which is the only thing the algorithm is for.
/// </remarks>
[TestFixture]
public sealed class FlexLayoutTests
{
    [Test]
    public async Task ItemsSitSideBySideAlongTheRow()
    {
        using var view = CreateView(400);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div style='display:flex;width:300px;height:50px'>" +
            "<div id='a' style='width:100px'></div>" +
            "<div id='b' style='width:100px'></div>" +
            "</div>" +
            "</body></html>");

        Assert.That(Find(view, "a").BorderBox.X, Is.EqualTo(0).Within(0.5));
        Assert.That(Find(view, "b").BorderBox.X, Is.EqualTo(100).Within(0.5));
    }

    [Test]
    public async Task GrowSharesTheFreeSpaceBetweenTheItems()
    {
        using var view = CreateView(400);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div style='display:flex;width:300px'>" +
            "<div id='a' style='width:100px;flex-grow:1'></div>" +
            "<div id='b' style='width:100px;flex-grow:1'></div>" +
            "</div>" +
            "</body></html>");

        Assert.That(Find(view, "a").BorderBox.Width, Is.EqualTo(150).Within(0.5));
        Assert.That(Find(view, "b").BorderBox.Width, Is.EqualTo(150).Within(0.5));
    }

    [Test]
    public async Task ShrinkTakesTheDeficitFromTheBaseSize()
    {
        using var view = CreateView(400);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div style='display:flex;width:200px'>" +
            "<div id='a' style='width:200px'></div>" +
            "<div id='b' style='width:200px'></div>" +
            "</div>" +
            "</body></html>");

        Assert.That(Find(view, "a").BorderBox.Width, Is.EqualTo(100).Within(0.5));
        Assert.That(Find(view, "b").BorderBox.Width, Is.EqualTo(100).Within(0.5));
    }

    [Test]
    public async Task PercentageBasisResolvesAgainstTheContainer()
    {
        using var view = CreateView(400);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div style='display:flex;width:200px'>" +
            "<div id='a' style='flex-basis:50%'></div>" +
            "</div>" +
            "</body></html>");

        Assert.That(Find(view, "a").BorderBox.Width, Is.EqualTo(100).Within(0.5));
    }

    [Test]
    public async Task JustifyContentCentresTheRow()
    {
        using var view = CreateView(400);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div style='display:flex;width:300px;justify-content:center'>" +
            "<div id='a' style='width:100px'></div>" +
            "</div>" +
            "</body></html>");

        Assert.That(Find(view, "a").BorderBox.X, Is.EqualTo(100).Within(0.5), "(300 - 100) / 2");
    }

    [Test]
    public async Task JustifyContentSpaceBetweenPushesTheEndsApart()
    {
        using var view = CreateView(400);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div style='display:flex;width:400px;justify-content:space-between'>" +
            "<div id='a' style='width:100px'></div>" +
            "<div id='b' style='width:100px'></div>" +
            "<div id='c' style='width:100px'></div>" +
            "</div>" +
            "</body></html>");

        Assert.That(Find(view, "a").BorderBox.X, Is.EqualTo(0).Within(0.5));
        Assert.That(Find(view, "b").BorderBox.X, Is.EqualTo(150).Within(0.5));
        Assert.That(Find(view, "c").BorderBox.X, Is.EqualTo(300).Within(0.5));
    }

    [Test]
    public async Task RowReversePlacesTheFirstItemAtTheEnd()
    {
        using var view = CreateView(400);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div style='display:flex;flex-direction:row-reverse;width:300px'>" +
            "<div id='a' style='width:100px'></div>" +
            "<div id='b' style='width:100px'></div>" +
            "</div>" +
            "</body></html>");

        Assert.That(Find(view, "a").BorderBox.X, Is.EqualTo(200).Within(0.5));
        Assert.That(Find(view, "b").BorderBox.X, Is.EqualTo(100).Within(0.5));
    }

    [Test]
    public async Task AColumnStacksTheItemsVertically()
    {
        using var view = CreateView(400);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div style='display:flex;flex-direction:column;width:100px'>" +
            "<div id='a' style='height:30px'></div>" +
            "<div id='b' style='height:30px'></div>" +
            "</div>" +
            "</body></html>");

        Assert.That(Find(view, "a").BorderBox.Y, Is.EqualTo(0).Within(0.5));
        Assert.That(Find(view, "b").BorderBox.Y, Is.EqualTo(30).Within(0.5));
    }

    [Test]
    public async Task AlignmentStretchesTheItemAcrossTheLine()
    {
        using var view = CreateView(400);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div style='display:flex;width:300px;height:80px'>" +
            "<div id='a' style='width:100px'></div>" +
            "</div>" +
            "</body></html>");

        Assert.That(Find(view, "a").BorderBox.Height, Is.EqualTo(80).Within(0.5), "align-items defaults to stretch");
    }

    [Test]
    public async Task AlignItemsCentresOnTheCrossAxis()
    {
        using var view = CreateView(400);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div style='display:flex;width:300px;height:100px;align-items:center'>" +
            "<div id='a' style='width:100px;height:20px'></div>" +
            "</div>" +
            "</body></html>");

        Assert.That(Find(view, "a").BorderBox.Y, Is.EqualTo(40).Within(0.5), "(100 - 20) / 2");
    }

    [Test]
    public async Task WrapBreaksTheItemsOntoASecondLine()
    {
        using var view = CreateView(400);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div style='display:flex;flex-wrap:wrap;width:250px'>" +
            "<div id='a' style='width:100px;height:50px'></div>" +
            "<div id='b' style='width:100px;height:50px'></div>" +
            "<div id='c' style='width:100px;height:50px'></div>" +
            "</div>" +
            "</body></html>");

        Assert.That(Find(view, "a").BorderBox.X, Is.EqualTo(0).Within(0.5));
        Assert.That(Find(view, "b").BorderBox.X, Is.EqualTo(100).Within(0.5));
        Assert.That(Find(view, "c").BorderBox.Y, Is.EqualTo(50).Within(0.5), "the third item starts the second line");
    }

    [Test]
    public async Task GapSeparatesTheItemsWithoutMargins()
    {
        using var view = CreateView(400);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div style='display:flex;width:300px;gap:20px'>" +
            "<div id='a' style='width:100px'></div>" +
            "<div id='b' style='width:100px'></div>" +
            "</div>" +
            "</body></html>");

        Assert.That(Find(view, "b").BorderBox.X, Is.EqualTo(120).Within(0.5));
    }

    [Test]
    public async Task OrderMovesAnItemToTheFront()
    {
        using var view = CreateView(400);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div style='display:flex;width:300px'>" +
            "<div id='a' style='width:100px'></div>" +
            "<div id='b' style='width:100px;order:-1'></div>" +
            "</div>" +
            "</body></html>");

        Assert.That(Find(view, "b").BorderBox.X, Is.EqualTo(0).Within(0.5));
        Assert.That(Find(view, "a").BorderBox.X, Is.EqualTo(100).Within(0.5));
    }

    [Test]
    public async Task AutoMarginsCentreALoneItem()
    {
        using var view = CreateView(400);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div style='display:flex;width:300px'>" +
            "<div id='a' style='width:100px;margin:auto'></div>" +
            "</div>" +
            "</body></html>");

        Assert.That(Find(view, "a").BorderBox.X, Is.EqualTo(100).Within(0.5), "(300 - 100) / 2");
    }

    [Test]
    public async Task AnItemWithAnAutomaticSizeIsMeasuredByItsContent()
    {
        using var view = CreateView(400);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div style='display:flex;width:300px'>" +
            "<div id='a'>aaaaaaaaaaaa</div>" +
            "<div id='b'>bbbb</div>" +
            "</div>" +
            "</body></html>");

        // The fake measurer gives five pixels a character, so the content asks for 60 and 20 of the
        // 300 - and an item that took the container's width instead would push its neighbour off the
        // page, which is what a header made of two such items comes to.
        Assert.That(Find(view, "a").BorderBox.Width, Is.EqualTo(60).Within(0.5), "flex-basis auto is the content size");
        Assert.That(Find(view, "b").BorderBox.X, Is.EqualTo(60).Within(0.5));
        Assert.That(Find(view, "b").BorderBox.Width, Is.EqualTo(20).Within(0.5));
    }

    [Test]
    public async Task AnItemHoldingAPercentageWidthIsStillMeasuredByItsContent()
    {
        using var view = CreateView(400);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div style='display:flex;justify-content:space-between;width:300px'>" +
            "<div id='a' style='display:flex'><div style='width:100%'>aaaaaaaa</div></div>" +
            "<div id='b' style='display:flex'><div style='width:100%'>bbbb</div></div>" +
            "</div>" +
            "</body></html>");

        // A site's menu bar is made of exactly this: an item whose every box inside declares
        // `width: 100%`. While the item is being asked what it needs, the block that percentage speaks
        // of is the very answer, so it has to fall back to the content - otherwise each item asks for
        // the whole row, the row cannot shrink them below what they asked for, and the second is placed
        // off the edge of the page.
        Assert.That(Find(view, "a").BorderBox.Width, Is.EqualTo(40).Within(0.5), "eight characters, not the row");
        Assert.That(Find(view, "b").BorderBox.Width, Is.EqualTo(20).Within(0.5));
        Assert.That(Find(view, "b").BorderBox.Right, Is.EqualTo(300).Within(0.5), "and space-between still puts the second at the end");
    }

    [Test]
    public async Task PaddingAndBorderAreExcludedFromTheContentSize()
    {
        using var view = CreateView(400);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div style='display:flex;width:300px;height:60px;padding:10px;border:5px solid #000'>" +
            "<div id='a' style='width:100px'></div>" +
            "</div>" +
            "</body></html>");

        var item = Find(view, "a");

        Assert.That(item.BorderBox.X, Is.EqualTo(15).Within(0.5), "the container's border and padding come first");
        Assert.That(item.BorderBox.Height, Is.EqualTo(60).Within(0.5), "the line is the container's content height");
    }

    private static HutuViewInternal CreateView(int viewportWidth)
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

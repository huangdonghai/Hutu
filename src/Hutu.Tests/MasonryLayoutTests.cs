using Hutu.Rendering.Layout;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Hutu.Tests;

/// <summary>
/// The grid-lanes (masonry) formatting context: the cross (column) axis is sized by the grid itself,
/// while the lane axis packs each column independently - a column's next item sits right below the last
/// item in that same column, not below the tallest item of the whole row.
/// </summary>
/// <remarks>
/// The documents go through the real cascade, so <c>display:grid-lanes</c> is kept (rather than dropped)
/// exactly as a page presents it. The font is a fixed-width stand-in - a character is five pixels wide and
/// a line ten tall - and the items carry explicit heights, so the expected numbers are exact.
/// </remarks>
[TestFixture]
public sealed class MasonryLayoutTests
{
    [Test]
    public async Task TheCrossAxisIsSizedTheWayAGridSizesIt()
    {
        using var view = CreateView(400);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div style='display:grid-lanes;grid-template-columns:repeat(3,minmax(0,1fr));width:300px'>" +
            "<div id='a'>A</div><div id='b'>B</div><div id='c'>C</div>" +
            "</div>" +
            "</body></html>");

        var a = Find(view, "a");
        var b = Find(view, "b");
        var c = Find(view, "c");

        // The three fractions share the width exactly as they would in a plain grid.
        Assert.That(a.BorderBox.X, Is.EqualTo(0).Within(0.5));
        Assert.That(a.BorderBox.Width, Is.EqualTo(100).Within(0.5));
        Assert.That(b.BorderBox.X, Is.EqualTo(100).Within(0.5));
        Assert.That(c.BorderBox.X, Is.EqualTo(200).Within(0.5));

        // One item per column, so the first rung of every lane starts at the top.
        Assert.That(a.BorderBox.Y, Is.EqualTo(0).Within(0.5));
        Assert.That(b.BorderBox.Y, Is.EqualTo(0).Within(0.5));
        Assert.That(c.BorderBox.Y, Is.EqualTo(0).Within(0.5));
    }

    [Test]
    public async Task EachLaneCollapsesIndependentlyOfItsTallerNeighbours()
    {
        using var view = CreateView(400);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div style='display:grid-lanes;grid-template-columns:repeat(3,100px);width:300px'>" +
            "<div id='a' style='height:20px'>A</div>" +
            "<div id='b' style='height:40px'>B</div>" +
            "<div id='c' style='height:20px'>C</div>" +
            "<div id='d' style='height:10px'>D</div>" +
            "</div>" +
            "</body></html>");

        var d = Find(view, "d");

        // A plain grid would push D to the second row, under the tallest first-row item (height 40).
        // A lane collapses on its own column: D wraps back to the first column, whose floor is only the
        // twenty pixels A left behind - the whole point that makes this masonry and not a grid.
        Assert.That(d.BorderBox.X, Is.EqualTo(0).Within(0.5), "D returns to the first column");
        Assert.That(d.BorderBox.Y, Is.EqualTo(20).Within(0.5), "D sits on A's floor, not the row's tallest");
    }

    [Test]
    public async Task AnExplicitGridColumnPinsAnItemToThatLane()
    {
        using var view = CreateView(400);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div style='display:grid-lanes;grid-template-columns:repeat(3,100px);width:300px'>" +
            "<div id='a' style='grid-column:3;height:20px'>A</div>" +
            "<div id='b' style='height:10px'>B</div>" +
            "</div>" +
            "</body></html>");

        var a = Find(view, "a");
        var b = Find(view, "b");

        // A is pinned to the third column; B is auto-placed from the cursor, which sits in the first lane.
        Assert.That(a.BorderBox.X, Is.EqualTo(200).Within(0.5));
        Assert.That(b.BorderBox.X, Is.EqualTo(0).Within(0.5));
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

        foreach (var node in Descend(root!))
        {
            if (node.Source.Element?.GetAttribute("id") == id)
            {
                return node;
            }
        }

        Assert.Fail($"no box with id '{id}' was laid out");
        return null!;
    }

    private static IEnumerable<LayoutBox> Descend(LayoutBox node)
    {
        yield return node;

        foreach (var child in node.Children)
        {
            foreach (var inner in Descend(child))
            {
                yield return inner;
            }
        }
    }
}

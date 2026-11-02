using Hutu.Rendering.Layout;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Hutu.Tests;

/// <summary>
/// Covers the column a full-width float takes when the column the flow reached has no room left for
/// it: the float opens the next column, that column is its own, and what was written after the float
/// fills the column the float left (CSS 2.1 9.5.1 with CSS Fragmentation 3 4.1, measured:
/// wpt/css-break/float-017).
/// </summary>
[TestFixture]
public sealed class FullWidthFloatTests
{
    [Test]
    public async Task AFullWidthFloatWithNoRoomLeftOpensTheNextColumn()
    {
        using var view = await Columned(200);

        Assert.Multiple(() =>
        {
            Assert.That(
                Pieces(view, "f").Select(box => (box.BorderBox.X, box.BorderBox.Y)).ToList(),
                Is.EqualTo(new[] { (100.0, 0.0) }),
                "the float takes the second column's head and stands alone in it");

            // Four lines fit in the first column under the box that opened it and the rest carry on in
            // the third: none of the flow is pushed into the column the float took.
            Assert.That(
                Columns(view),
                Is.EqualTo(new[] { 0.0, 0.0, 0.0, 0.0, 200.0, 200.0, 200.0, 200.0, 200.0 }));
        });
    }

    [Test]
    public async Task AFullWidthFloatTheColumnStillHoldsStandsWhereTheFlowReachedIt()
    {
        using var view = await Columned(60);

        // The column had the room, so the float is not moved on: it stands at the place the flow had
        // reached (the room a float of this shape leaves the lines after it is widows-orphans-020's).
        Assert.That(
            Pieces(view, "f").Select(box => (box.BorderBox.X, box.BorderBox.Y)).ToList(),
            Is.EqualTo(new[] { (0.0, 20.0) }));
    }

    private static async Task<HutuViewInternal> Columned(Double floatHeight) =>
        await Shown(
            "<div style='columns:3; column-gap:0; column-fill:auto; width:300px; height:100px;" +
            "line-height:20px; orphans:1; widows:1; font-size:10px'>" +
            "<div id='a' style='height:20px'></div>" +
            "<div id='f' style='float:left; width:100%; height:" + floatHeight + "px'></div>" +
            "<div id='b'>1<br>2<br>3<br>4<br>5<br>6<br>7<br>8<br>9</div></div>");

    private static async Task<HutuViewInternal> Shown(String content)
    {
        var view = new HutuViewInternal(new FakeTextMeasurer());
        view.SetViewportSize(400, 600);
        await view.SetHtml("<html><body style='margin:0'>" + content + "</body></html>");
        return view;
    }

    // Which column each line of the run written after the float came to stand in.
    private static List<Double> Columns(HutuViewInternal view)
    {
        var lines = new List<LayoutBox>();
        Visit(view.Layout!.Root, null, "b", lines);

        return lines.Select(box => box.BorderBox.X).ToList();
    }

    private static void Visit(LayoutBox node, String? owner, String id, List<LayoutBox> found)
    {
        var named = node.Source.Element?.GetAttribute("id");
        var mine = String.IsNullOrEmpty(named) ? owner : named;

        if (mine == id && node.Kind == LayoutKind.Line)
        {
            found.Add(node);
        }

        foreach (var child in node.Children)
        {
            Visit(child, mine, id, found);
        }
    }

    private static List<LayoutBox> Pieces(HutuViewInternal view, String id) =>
        Walk(view).Where(box => String.Equals(
            box.Source.Element?.GetAttribute("id"), id, StringComparison.OrdinalIgnoreCase)).ToList();

    private static IEnumerable<LayoutBox> Walk(HutuViewInternal view)
    {
        foreach (var box in DepthFirst(view.Layout!.Root))
        {
            yield return box;
        }
    }

    private static IEnumerable<LayoutBox> DepthFirst(LayoutBox node)
    {
        yield return node;

        foreach (var child in node.Children)
        {
            foreach (var below in DepthFirst(child))
            {
                yield return below;
            }
        }
    }
}

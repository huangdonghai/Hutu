using Hutu.Rendering.Layout;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Hutu.Tests;

/// <summary>
/// Covers what an inline element asks a line for along the inline axis: its own inset and margin edges,
/// charged once and on the line they belong to, whether the element ends on that line, is followed by a
/// forced break, or is still open when the block ends (CSS Inline 3 §4.1, CSS Break 3 §4.6).
/// </summary>
[TestFixture]
public sealed class InlineIntrinsicEdgesTests
{
    [Test]
    public async Task TheSameInlineAsksTheSameRoomHoweverTheLinesAreWritten()
    {
        using var view = await Shown(
            "<table><tr>" +
            "<td id='one'><span>a</span></td>" +
            "<td id='two'><span>b</span><br>c</td>" +
            "<td id='three'><span>d</span><br><span>e</span></td>" +
            "</tr></table>");

        var widths = new[] { "one", "two", "three" }.Select(id => Cell(view, id)).ToList();

        // Each cell holds one decorated inline of the same size: the first has it alone, the second has
        // it before a break, the third has a second one after the break. All three ask for the same box
        // plus both of its margin edges, and none of them may charge an edge twice or drop one.
        Assert.That(widths[1], Is.EqualTo(widths[0]).Within(0.01), "the break does not lose the closing edge");
        Assert.That(widths[2], Is.EqualTo(widths[0]).Within(0.01), "nor does a second inline inherit it");
    }

    [Test]
    public async Task ACloneAsksForItsEdgesOnEveryLineItIsBrokenAcross()
    {
        using var view = await Shown(
            "<table><tr>" +
            "<td id='one'><span class='s'>b<br>c</span></td>" +
            "<td id='two'><span class='c'>b<br>c</span></td>" +
            "</tr></table>");

        // A sliced box pays its start and end edges once, on the fragments that really have them; a
        // clone keeps those edges on every fragment and must ask for them on every line the element is
        // broken across (CSS Break 3 4.6). The two cells hold the same markup on the same two lines.
        var sliced = Cell(view, "one");
        var cloned = Cell(view, "two");

        Assert.That(cloned, Is.GreaterThan(sliced), "each line of a clone carries its own edges");
    }

    private static async Task<HutuViewInternal> Shown(String content)
    {
        var view = new HutuViewInternal(new FakeTextMeasurer());
        view.SetViewportSize(400, 600);
        await view.SetHtml(
            "<html><head><style>" +
            "table { border-collapse: separate } td { padding: 0 }" +
            "span { padding: 3px 5px; border: 2px solid;" +
            "  margin: 13px 17px 19px 23px; font-size: 10px; line-height: 10px }" +
            ".c { box-decoration-break: clone } .s { box-decoration-break: slice }" +
            "</style></head><body style='margin:0'>" + content + "</body></html>");
        return view;
    }

    private static Double Cell(HutuViewInternal view, String id) =>
        Walk(view).First(box => String.Equals(
            box.Source.Element?.GetAttribute("id"), id, StringComparison.OrdinalIgnoreCase)).BorderBox.Width;

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

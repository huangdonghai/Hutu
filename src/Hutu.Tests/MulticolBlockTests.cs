using Hutu.Rendering.Layout;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Hutu.Tests;

/// <summary>
/// Covers a multi-column container whose content is block-level boxes: the children are shared over
/// the columns whole, a forced column break ends a column early, and the box's intrinsic width is the
/// columns' width side by side.
/// </summary>
[TestFixture]
public sealed class MulticolBlockTests
{
    [Test]
    public async Task TheChildrenShareTheColumnsAndTheBoxIsAsTallAsTheyBalanceTo()
    {
        using var view = await Render(
            "<style>#c { width: 300px; columns: 2; column-gap: 20px }" +
            " .b { height: 40px }</style>" +
            "<div id='c'><div class='b'></div><div class='b'></div></div>");

        var children = Blocks(view);

        Assert.That(children.Count, Is.EqualTo(2), "both children are laid out");
        Assert.That(children[0].BorderBox.X, Is.EqualTo(0), "the first stands in the first column");
        Assert.That(children[1].BorderBox.X, Is.EqualTo(160), "and the second in the column after the gap");
        Assert.That(children.Select(child => child.BorderBox.Y), Is.All.EqualTo(0), "each column begins at the top");
        Assert.That(Find(view, "c").BorderBox.Height, Is.EqualTo(40), "the box is as tall as the columns were balanced to");
    }

    [Test]
    public async Task AForcedColumnBreakEndsTheColumnEvenWhereMoreWouldFit()
    {
        using var view = await Render(
            "<style>#c { width: 300px; columns: 2; column-gap: 20px; column-fill: auto; height: 100px }" +
            " .b { height: 20px } .next { break-before: column }</style>" +
            "<div id='c'><div class='b'></div><div class='b next'></div><div class='b'></div></div>");

        var children = Blocks(view);

        Assert.That(children[0].BorderBox.X, Is.EqualTo(0), "the first child stays in the first column");
        Assert.That(children[1].BorderBox.X, Is.EqualTo(160), "the forced break carries the second to the next column");
        Assert.That(children[2].BorderBox.X, Is.EqualTo(160), "and the third follows it there");
        Assert.That(children[1].BorderBox.Y, Is.EqualTo(0), "which begins at its top");
    }

    [Test]
    public async Task TheIntrinsicWidthIsTheColumnsSideBySide()
    {
        using var view = await Render(
            "<style>body { font-size: 10px; line-height: 10px }" +
            " #c { display: inline-block; columns: 2; column-gap: 10px }</style>" +
            "<div id='c'>XXXXXXXX<br>XX</div>");

        Assert.That(
            Find(view, "c").BorderBox.Width,
            Is.EqualTo(90),
            "two columns of the widest line, forty, and the gap between them");
    }

    [Test]
    public async Task ASpannerLeavesTheBoxInItsOrdinaryStream()
    {
        using var view = await Render(
            "<style>#c { width: 300px; columns: 2; column-gap: 20px }" +
            " .b { height: 40px } .all { column-span: all }</style>" +
            "<div id='c'><div class='b'></div><div class='b all'></div></div>");

        var children = Blocks(view);

        Assert.That(
            children.Select(child => child.BorderBox.X),
            Is.All.EqualTo(0),
            "a container holding a spanner keeps the ordinary stack until it can lay the spanner across");
    }

    private static async Task<HutuViewInternal> Render(String content)
    {
        var view = new HutuViewInternal(new FakeTextMeasurer());
        view.SetViewportSize(400, 600);
        await view.SetHtml(
            "<html><head><style>div { margin: 0 }</style></head>" +
            "<body style='margin:0;font-size:10px;line-height:10px'>" + content + "</body></html>");
        return view;
    }

    private static LayoutBox Find(HutuViewInternal view, String id) =>
        Walk(view.Layout!.Root).First(box => box.Source.Element?.GetAttribute("id") == id);

    private static List<LayoutBox> Blocks(HutuViewInternal view) =>
        Walk(Find(view, "c"))
            .Where(box => box != Find(view, "c") && box.Kind == LayoutKind.Block)
            .ToList();

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

using Hutu.Rendering.Layout;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Hutu.Tests;

/// <summary>
/// Covers the logical insets: an offset written as <c>inset-inline-start</c> or
/// <c>inset-block-start</c> names an edge of the box's own axes, so where it lands depends on the
/// writing mode and the line direction (CSS Logical 1 §12.2).
/// </summary>
[TestFixture]
public sealed class LogicalInsetTests
{
    [Test]
    public async Task TheInlineStartOfALeftToRightBoxIsItsLeftEdge()
    {
        using var view = await Render("<div id='b' style='inset-inline-start: 10px'></div>");

        Assert.That(Of(view).BorderBox.X, Is.EqualTo(10), "the line starts at the left, so the inline start is the left edge");
    }

    [Test]
    public async Task TheInlineStartOfARightToLeftBoxIsItsRightEdge()
    {
        using var view = await Render("<div id='b' style='inset-inline-start: 10px'></div>", "direction: rtl");

        Assert.That(Of(view).BorderBox.X, Is.EqualTo(140), "the line starts at the right, so the offset is taken off that edge");
    }

    [Test]
    public async Task TheBlockStartOfAVerticalBoxIsItsRightEdge()
    {
        using var view = await Render("<div id='b' style='inset-block-start: 10px'></div>", "writing-mode: vertical-rl");

        Assert.That(Of(view).BorderBox.X, Is.EqualTo(140), "a vertical-rl box runs its block axis right to left, so the start is its right edge");
    }

    [Test]
    public async Task TheInlineStartOfAVerticalBoxIsItsTopEdge()
    {
        using var view = await Render("<div id='b' style='inset-inline-start: 10px'></div>", "writing-mode: vertical-rl");

        Assert.That(Of(view).BorderBox.Y, Is.EqualTo(10), "a vertical box runs its line down the page, so the inline start is the top edge");
    }

    [Test]
    public async Task TheInlineStartOfAVerticalBoxRunsDownwardsWhenTheDirectionIsRightToLeft()
    {
        using var view = await Render(
            "<div id='b' style='inset-inline-start: 10px'></div>",
            "writing-mode: vertical-rl; direction: rtl");

        Assert.That(Of(view).BorderBox.Y, Is.EqualTo(70), "a right-to-left vertical line starts at the bottom, ten above its edge");
    }

    [Test]
    public async Task AnEdgeThePageWroteItselfKeepsTheOffsetItWasGiven()
    {
        using var view = await Render("<div id='b' style='top: 5px; inset-block-start: 20px'></div>");

        Assert.That(Of(view).BorderBox.Y, Is.EqualTo(5), "the logical offset names the edge the page had already written for");
    }

    private static LayoutBox Of(HutuViewInternal view) =>
        Walk(view.Layout!.Root).First(box => box.Source.Element?.GetAttribute("id") == "b");

    private static async Task<HutuViewInternal> Render(String content, String blockStyle = "")
    {
        var view = new HutuViewInternal(new FakeTextMeasurer());
        view.SetViewportSize(400, 600);
        await view.SetHtml(
            "<html><head><style>div { margin: 0 }</style></head>" +
            "<body style='margin:0;font-size:10px;line-height:10px'>" +
            "<div id='p' style='position:relative;width:200px;height:100px;" + blockStyle + "'>" +
            "<style>#b { position: absolute; width: 50px; height: 20px }</style>" + content +
            "</div></body></html>");
        return view;
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

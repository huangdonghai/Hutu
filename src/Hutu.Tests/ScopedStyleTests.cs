using Hutu.Rendering.Layout;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Hutu.Tests;

/// <summary>
/// Covers the subtree a <c>@scope</c> block confines its rules to, and the blocks this engine leaves
/// unread (CSS Cascade 6 §3.5).
/// </summary>
[TestFixture]
public sealed class ScopedStyleTests
{
    [Test]
    public async Task ABlockWithNothingBeforeItReachesOnlyTheElementWrittenAroundIt()
    {
        using var view = await Shown(
            "<div id='one'><style>@scope { .a { height: 40px } }</style><div id='a1' class='a'></div></div>" +
            "<div id='two'><div id='a2' class='a'></div></div>");

        Assert.Multiple(() =>
        {
            // The style element stands inside #one, so #one is the root: what hangs beneath it is what
            // the rule was written for.
            Assert.That(Tall(view, "a1"), Is.EqualTo(40).Within(0.01), "the block reaches its own subtree");
            Assert.That(Tall(view, "a2"), Is.EqualTo(0).Within(0.01), "and no further");
        });
    }

    [Test]
    public async Task ABlockThatNamesItsRootWithASelectorIsLeftUnread()
    {
        using var view = await Shown(
            "<style>@scope (#here) { .b { height: 30px } }</style><div id='here'><div id='b' class='b'></div></div>");

        // The root would have to be found by matching a selector, which the walk that hands out rules
        // does not do: the rule is kept out rather than let loose over every element of the page.
        Assert.That(Tall(view, "b"), Is.EqualTo(0).Within(0.01));
    }

    private static async Task<HutuViewInternal> Shown(String content)
    {
        var view = new HutuViewInternal(new FakeTextMeasurer());
        view.SetViewportSize(400, 600);
        await view.SetHtml(
            "<html><head><style>div { margin: 0 }</style></head>" +
            "<body style='margin:0;font-size:10px;line-height:10px'>" + content + "</body></html>");
        return view;
    }

    private static Double Tall(HutuViewInternal view, String id) =>
        Walk(view).First(box => box.Source.Element?.GetAttribute("id") == id).BorderBox.Height;

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

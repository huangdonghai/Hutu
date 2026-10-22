using Hutu.Rendering.Layout;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Hutu.Tests;

/// <summary>
/// The size a flex container takes from its aspect ratio, and the size a percentage inside a
/// <c>calc()</c> takes when the container it is measured against has no definite size.
/// </summary>
[TestFixture]
public sealed class FlexRatioTests
{
    [Test]
    public async Task AColumnContainerTakesItsHeightFromItsRatio()
    {
        using var view = CreateView(400);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div style='width:400px;aspect-ratio:5/2;display:flex;flex-direction:column'>" +
            "<div style='flex:1 1 0'>a</div><div style='flex:1 1 0'>b</div></div>" +
            "</body></html>");

        var container = Find(view, "div").First();

        Assert.That(container.BorderBox.Height, Is.EqualTo(160).Within(0.5),
            "the height of a column container is the width over the ratio, and the flex layout does not " +
            "decide it (CSS Sizing 4 §4)");
    }

    [Test]
    public async Task ARowContainerTakesItsHeightFromItsRatio()
    {
        using var view = CreateView(400);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div style='width:400px;aspect-ratio:5/2;display:flex'>x</div>" +
            "</body></html>");

        var container = Find(view, "div").First();

        Assert.That(container.BorderBox.Height, Is.EqualTo(160).Within(0.5),
            "the cross size of a row container is the width over the ratio");
    }

    [Test]
    public async Task APercentageInACalcFallsBackToTheContentWhenTheHeightIsIndefinite()
    {
        using var view = CreateView(400);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div id='a' style='width:100px;height:calc(100% * 0.5)'>x</div>" +
            "<div id='b' style='width:100px;height:auto'>x</div>" +
            "</body></html>");

        var boxes = Find(view, "div").ToList();

        Assert.Multiple(() =>
        {
            Assert.That(boxes[0].BorderBox.Height, Is.EqualTo(boxes[1].BorderBox.Height).Within(0.5),
                "a percentage against a height that is not definite falls back to the content, and the " +
                "content of the first is the same as the second's");
            Assert.That(boxes[0].BorderBox.Height, Is.GreaterThan(0),
                "and it is not nothing at all");
        });
    }

    private static HutuViewInternal CreateView(Int32 viewportWidth)
    {
        var view = new HutuViewInternal(new FakeTextMeasurer());
        view.SetViewportSize(viewportWidth, 600);
        return view;
    }

    private static IEnumerable<LayoutBox> Find(HutuViewInternal view, String tag)
    {
        var root = view.Layout?.Root;

        if (root is null)
        {
            yield break;
        }

        foreach (var node in Walk(root))
        {
            if (node.Kind is LayoutKind.Block or LayoutKind.HorizontalRule &&
                node.Source.Element is { } element &&
                String.Equals(element.LocalName, tag, StringComparison.OrdinalIgnoreCase))
            {
                yield return node;
            }
        }
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

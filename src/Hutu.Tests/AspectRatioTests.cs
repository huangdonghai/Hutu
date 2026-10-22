using Hutu.Rendering.Layout;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Hutu.Tests;

/// <summary>
/// The ratio a box's width and height keep to, as CSS Sizing 4 §4 states it.
/// </summary>
/// <remarks>
/// A box with a ratio and one definite axis takes the other from it. The ratio is of the box the
/// sizing applies to - the content box, or the border box where <c>box-sizing</c> says so - and
/// where the page fixed both axes the ratio says nothing at all.
/// </remarks>
[TestFixture]
public sealed class AspectRatioTests
{
    [Test]
    public async Task ADefiniteWidthTakesItsHeightFromTheRatio()
    {
        using var view = CreateView(400);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div style='width:200px;aspect-ratio:21/9'></div>" +
            "</body></html>");

        var box = FindAll(view, "div").Single();

        Assert.That(box.BorderBox.Width, Is.EqualTo(200).Within(0.5));
        Assert.That(box.BorderBox.Height, Is.EqualTo(200d / (21d / 9d)).Within(0.5),
            "the height is the width over the ratio");
    }

    [Test]
    public async Task ADefiniteHeightTakesItsWidthFromTheRatio()
    {
        using var view = CreateView(400);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div style='height:100px;aspect-ratio:16/9'></div>" +
            "</body></html>");

        var box = FindAll(view, "div").Single();

        Assert.That(box.BorderBox.Height, Is.EqualTo(100).Within(0.5));
        Assert.That(box.BorderBox.Width, Is.EqualTo(100d * (16d / 9d)).Within(0.5),
            "the width is the height times the ratio, and not the room the box was offered");
    }

    [Test]
    public async Task ARatioSaysNothingWhereBothAxesAreFixed()
    {
        using var view = CreateView(400);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div style='width:200px;height:50px;aspect-ratio:16/9'></div>" +
            "</body></html>");

        var box = FindAll(view, "div").Single();

        Assert.That(box.BorderBox.Width, Is.EqualTo(200).Within(0.5));
        Assert.That(box.BorderBox.Height, Is.EqualTo(50).Within(0.5));
    }

    [Test]
    public async Task ARatioOfNothingIsNoRatio()
    {
        using var view = CreateView(400);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div style='width:200px;aspect-ratio:1/0'></div>" +
            "<div style='width:200px;aspect-ratio:0/1'></div>" +
            "<div style='width:200px;aspect-ratio:auto'></div>" +
            "</body></html>");

        foreach (var box in FindAll(view, "div"))
        {
            Assert.That(box.BorderBox.Height, Is.EqualTo(0).Within(0.5),
                "a ratio that cannot be kept to is no ratio at all");
        }
    }

    [Test]
    public async Task TheRatioIsOfTheBorderBoxWhereTheSizingSaysSo()
    {
        using var view = CreateView(400);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div style='box-sizing:border-box;width:200px;padding:10px;aspect-ratio:1/1'></div>" +
            "</body></html>");

        var box = FindAll(view, "div").Single();

        Assert.That(box.BorderBox.Width, Is.EqualTo(200).Within(0.5));
        Assert.That(box.BorderBox.Height, Is.EqualTo(200).Within(0.5),
            "the ratio is of the border box, so the padding is inside it");
    }

    [Test]
    public async Task TheMinimumHeightHasTheLastWord()
    {
        using var view = CreateView(400);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div style='width:200px;aspect-ratio:2/1;min-height:300px'></div>" +
            "</body></html>");

        var box = FindAll(view, "div").Single();

        Assert.That(box.BorderBox.Height, Is.EqualTo(300).Within(0.5),
            "the constraint is applied to the height the ratio settled");
    }

    [Test]
    public async Task ARatioWrittenThroughAVariableIsRead()
    {
        using var view = CreateView(400);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div style='width:200px;aspect-ratio:var(--ratio,21/9)'></div>" +
            "<div style='width:200px;--ratio:16/9;aspect-ratio:var(--ratio,21/9)'></div>" +
            "</body></html>");

        var boxes = FindAll(view, "div").ToList();

        Assert.That(boxes[0].BorderBox.Height, Is.EqualTo(200d / (21d / 9d)).Within(0.5),
            "the fallback is the ratio where the page named none");
        Assert.That(boxes[1].BorderBox.Height, Is.EqualTo(200d / (16d / 9d)).Within(0.5),
            "the variable's own value is the ratio where the page named one");
    }

    [Test]
    public async Task TheContentRaisesTheHeightTheRatioSuggested()
    {
        using var view = CreateView(400);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div style='width:200px;aspect-ratio:1/1'><div style='height:300px'></div></div>" +
            "</body></html>");

        var boxes = FindAll(view, "div").ToList();

        Assert.That(boxes[0].BorderBox.Height, Is.EqualTo(300).Within(0.5),
            "the height a ratio suggests is a preferred one, and the content is taller than it");
    }

    [Test]
    public async Task ABoxThatClipsItsOverflowKeepsTheHeightTheRatioGave()
    {
        using var view = CreateView(400);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div style='width:200px;aspect-ratio:1/1;overflow:auto'><div style='height:300px'></div></div>" +
            "</body></html>");

        var boxes = FindAll(view, "div").ToList();

        Assert.That(boxes[0].BorderBox.Height, Is.EqualTo(200).Within(0.5),
            "a box that clips its overflow has no automatic minimum size, so the ratio's height stands");
    }

    private static HutuViewInternal CreateView(Int32 viewportWidth)
    {
        var view = new HutuViewInternal(new FakeTextMeasurer());
        view.SetViewportSize(viewportWidth, 600);
        return view;
    }

    private static IEnumerable<LayoutBox> FindAll(HutuViewInternal view, String tag)
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

using Hutu.Rendering.Layout;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Hutu.Tests;

/// <summary>
/// The box model corrections the other formatting contexts rest on: <c>box-sizing</c>, automatic
/// margins and percentages resolved against the containing block rather than the viewport.
/// </summary>
/// <remarks>
/// Documents are the input for the same reason as in <see cref="LayoutEngineTests"/>: these values
/// come from the cascade, and how AngleSharp stores them is part of what is being asserted. A
/// percentage that was folded into pixels at style computation time, for instance, would still look
/// right here as long as the viewport happened to match the container.
/// </remarks>
[TestFixture]
public sealed class BoxModelTests
{
    [Test]
    public async Task BorderBoxMakesTheDeclaredWidthCoverTheInsets()
    {
        using var view = CreateView(400);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div style='box-sizing:border-box;width:100px;padding:10px;border:5px solid #000'></div>" +
            "</body></html>");

        var box = FindAll(view, "div").Single();

        Assert.That(box.BorderBox.Width, Is.EqualTo(100).Within(0.5), "the declared width is the border box");
    }

    [Test]
    public async Task ContentBoxRemainsTheDefault()
    {
        using var view = CreateView(400);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div style='width:100px;padding:10px;border:5px solid #000'></div>" +
            "</body></html>");

        var box = FindAll(view, "div").Single();

        Assert.That(box.BorderBox.Width, Is.EqualTo(130).Within(0.5), "the padding and the border come on top");
    }

    [Test]
    public async Task PercentWidthResolvesAgainstTheContainingBlockAndNotTheViewport()
    {
        using var view = CreateView(400);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div style='width:200px'>" +
            "<div id='child' style='width:100%'></div>" +
            "</div>" +
            "</body></html>");

        var child = FindAll(view, "div").Single(box => box.Source.Element?.GetAttribute("id") == "child");

        Assert.That(child.BorderBox.Width, Is.EqualTo(200).Within(0.5), "100% of the parent, not of the viewport");
    }

    [Test]
    public async Task PercentPaddingResolvesAgainstTheWidthOnEverySide()
    {
        using var view = CreateView(400);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div style='width:200px'>" +
            "<div id='child' style='width:100px;padding:10%'></div>" +
            "</div>" +
            "</body></html>");

        var child = FindAll(view, "div").Single(box => box.Source.Element?.GetAttribute("id") == "child");

        Assert.That(child.BorderBox.Width, Is.EqualTo(140).Within(0.5), "100 + 20 + 20, both taken from the width");
    }

    [Test]
    public async Task AutoMarginsCentreADeclaredWidth()
    {
        using var view = CreateView(400);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div style='width:100px;margin:0 auto'></div>" +
            "</body></html>");

        var box = FindAll(view, "div").Single();

        Assert.That(box.BorderBox.X, Is.EqualTo(150).Within(0.5), "(400 - 100) / 2");
        Assert.That(box.BorderBox.Width, Is.EqualTo(100).Within(0.5));
    }

    [Test]
    public async Task AutoMarginsStillCentreOnceAMaxWidthHasApplied()
    {
        // The shape `.demo { max-width: 1000px; margin: 0 auto }` has: an auto width that the
        // constraint turns into a definite one, and the leftover shared by the two auto margins.
        using var view = CreateView(400);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div style='max-width:200px;margin:0 auto'></div>" +
            "</body></html>");

        var box = FindAll(view, "div").Single();

        Assert.That(box.BorderBox.Width, Is.EqualTo(200).Within(0.5));
        Assert.That(box.BorderBox.X, Is.EqualTo(100).Within(0.5), "(400 - 200) / 2");
    }

    [Test]
    public async Task ASingleAutoMarginTakesAllTheSpaceLeftOver()
    {
        using var view = CreateView(400);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div style='width:100px;margin-left:auto;margin-right:20px'></div>" +
            "</body></html>");

        var box = FindAll(view, "div").Single();

        Assert.That(box.BorderBox.X, Is.EqualTo(280).Within(0.5), "400 - 20 - 100, pushed to the right edge");
    }

    [Test]
    public async Task MaxWidthSuppressesADeclaredWidth()
    {
        using var view = CreateView(400);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div style='width:500px;max-width:120px'></div>" +
            "</body></html>");

        var box = FindAll(view, "div").Single();

        Assert.That(box.BorderBox.Width, Is.EqualTo(120).Within(0.5));
    }

    [Test]
    public async Task MinWidthOverridesADeclaredWidth()
    {
        using var view = CreateView(400);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div style='width:50px;min-width:200px'></div>" +
            "</body></html>");

        var box = FindAll(view, "div").Single();

        Assert.That(box.BorderBox.Width, Is.EqualTo(200).Within(0.5));
    }

    [Test]
    public async Task MinAndMaxWidthBoundTheBorderBoxUnderBorderSizing()
    {
        using var view = CreateView(400);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div style='box-sizing:border-box;width:300px;max-width:140px;padding:10px;border:5px solid #000'></div>" +
            "</body></html>");

        var box = FindAll(view, "div").Single();

        Assert.That(box.BorderBox.Width, Is.EqualTo(140).Within(0.5), "max-width bounds the border box, not the content");
    }

    [Test]
    public async Task PercentHeightResolvesAgainstADefiniteContainingBlock()
    {
        using var view = CreateView(400);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div style='height:200px'>" +
            "<div id='child' style='height:50%'></div>" +
            "</div>" +
            "</body></html>");

        var child = FindAll(view, "div").Single(box => box.Source.Element?.GetAttribute("id") == "child");

        Assert.That(child.BorderBox.Height, Is.EqualTo(100).Within(0.5));
    }

    [Test]
    public async Task PercentHeightBehavesAsAutoWithoutADefiniteContainingBlock()
    {
        using var view = CreateView(400);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div>" +
            "<div id='child' style='height:50%'></div>" +
            "</div>" +
            "</body></html>");

        var child = FindAll(view, "div").Single(box => box.Source.Element?.GetAttribute("id") == "child");

        Assert.That(child.BorderBox.Height, Is.EqualTo(0).Within(0.5), "the parent's height comes from its content");
    }

    private static HutuViewInternal CreateView(int viewportWidth)
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

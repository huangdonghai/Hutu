using Hutu.Rendering.Layout;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Hutu.Tests;

/// <summary>
/// Covers the rectangle a document is scrolled over: it is what the page occupies rather than the
/// root's box, it stops at the edges the page reads from, and the frame it is measured in begins
/// where the document does.
/// </summary>
/// <remarks>
/// The measurer is the fixed-width one, so the geometry here is arithmetic rather than the fonts of
/// the machine running it.
/// </remarks>
[TestFixture]
public sealed class ScrollableOverflowTests
{
    [Test]
    public async Task AColumnWiderThanItsPageIsAsWideAsTheDocument()
    {
        using var view = CreateView();

        await view.SetHtml(
            "<html><body style='margin:0;writing-mode:vertical-rl;width:50px'>" +
            "<div style='width:200px;height:10px'></div>" +
            "</body></html>");

        var layout = view.Layout!;
        var column = Box(view, "div");

        Assert.That(layout.Width, Is.EqualTo(200).Within(0.5), "the document reaches as far as the column that leaves the page");
        Assert.That(
            column.BorderBox.X,
            Is.EqualTo(0).Within(0.5),
            "the frame begins where the document does, so the column that is the document starts at the origin");
        Assert.That(
            Box(view, "body").BorderBox.X,
            Is.EqualTo(150).Within(0.5),
            "the page's own box moved with the frame: it sits at the end of the column it could not hold");
    }

    [Test]
    public async Task ABoxAnchoredToTheViewportIsNotPartOfTheDocument()
    {
        using var view = CreateView();

        await view.SetHtml(
            "<html><body style='margin:0;writing-mode:vertical-rl;width:50px'>" +
            "<div style='width:200px;height:10px'></div>" +
            "<div id='pin' style='position:fixed;left:5000px;top:0;width:10px;height:10px'></div>" +
            "</body></html>");

        var layout = view.Layout!;
        var fixedBox = Box(view, "div", "pin");

        Assert.That(layout.Width, Is.EqualTo(200).Within(0.5), "a box held by the viewport is not something the page can be scrolled to");
        Assert.That(fixedBox.BorderBox.X, Is.EqualTo(5000).Within(0.5), "and it is not moved when the page's frame is settled");
    }

    [Test]
    public async Task ContentHangingOffTheSideThePageReadsFromIsNotReachable()
    {
        using var view = CreateView();

        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div style='position:absolute;left:-100px;top:0;width:50px;height:10px'></div>" +
            "</body></html>");

        var layout = view.Layout!;

        Assert.That(Box(view, "div").BorderBox.X, Is.EqualTo(-100).Within(0.5), "the box is where it was put");
        Assert.That(
            layout.Width,
            Is.EqualTo(600).Within(0.5),
            "nothing scrolls back past the edge a left-to-right page reads from, so its box is left out");
    }

    [Test]
    public async Task ARowThatOutgrowsItsPageMakesTheDocumentTaller()
    {
        using var view = CreateView();

        await view.SetHtml(
            "<html><body style='margin:0'><div style='height:2000px'></div></body></html>");

        Assert.That(view.Layout!.Height, Is.EqualTo(2000).Within(0.5));
    }

    private static HutuViewInternal CreateView()
    {
        var view = new HutuViewInternal(new FakeTextMeasurer());
        view.SetViewportSize(600, 400);
        return view;
    }

    /// <summary>Finds the block box an element generated, the last one when it is asked for by id.</summary>
    private static LayoutBox Box(HutuViewInternal view, String tag, String id = "")
    {
        var root = view.Layout?.Root ?? throw new AssertionException("The document was not laid out.");

        return Walk(root).First(node =>
            node.Kind == LayoutKind.Block &&
            node.Source.Element is { } element &&
            String.Equals(element.LocalName, tag, StringComparison.OrdinalIgnoreCase) &&
            (id.Length == 0 || element.Id == id));
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

using Hutu.Rendering.Layout;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Hutu.Tests;

/// <summary>
/// Covers the defaults the reader supplies: what HTML's elements are given when a document says
/// nothing about them, and in particular that the space around a block is between blocks rather than
/// along the line it sets - which is what keeps two elements of different kinds level at the top of a
/// column.
/// </summary>
/// <remarks>
/// The measurer is the fixed-width one and the root font size is sixteen, so the em values the sheet
/// states come out as arithmetic.
/// </remarks>
[TestFixture]
public sealed class UserAgentStyleSheetTests
{
    [Test]
    public async Task TheSpaceAroundABlockIsBetweenBlocksInARow()
    {
        using var view = CreateView();

        await view.SetHtml("<html><body style='margin:0'><h1>h</h1><p>text</p></body></html>");

        var h1 = Block(view, "h1");
        var p = Block(view, "p");

        // An em is the element's own size, and a heading is twice the root's.
        Assert.That(h1.Margin.Top, Is.EqualTo(32 * 0.67).Within(0.01), "a heading takes .67em above it");
        Assert.That(h1.Margin.Bottom, Is.EqualTo(32 * 0.67).Within(0.01));
        Assert.That(p.Margin.Top, Is.EqualTo(16 * 1.12).Within(0.01), "and a paragraph 1.12em");
        Assert.That(h1.Margin.Left, Is.EqualTo(0).Within(0.01), "neither of them is set in from the sides");
        Assert.That(p.Margin.Left, Is.EqualTo(0).Within(0.01));
    }

    [Test]
    public async Task TheSpaceAroundABlockIsBetweenColumnsInAColumn()
    {
        using var view = CreateView();

        await view.SetHtml(
            "<html><body style='margin:0;writing-mode:vertical-rl'><h1>h</h1><p>text</p></body></html>");

        var h1 = Block(view, "h1");
        var p = Block(view, "p");

        Assert.That(
            h1.Margin.Top,
            Is.EqualTo(0).Within(0.01),
            "the top of a column is where its text begins, so nothing is set in from it");
        Assert.That(p.Margin.Top, Is.EqualTo(0).Within(0.01), "however long the text on it is");
        Assert.That(
            p.Margin.Bottom,
            Is.EqualTo(0).Within(0.01),
            "which is what puts the two of them level at the head of the column");
        Assert.That(h1.Margin.Right, Is.EqualTo(32 * 0.67).Within(0.01), "the space is beside the column instead");
        Assert.That(p.Margin.Right, Is.EqualTo(16 * 1.12).Within(0.01));
    }

    [Test]
    public async Task TheHeadingsOfTheSheetKeepTheirSizes()
    {
        using var view = CreateView();

        await view.SetHtml("<html><body style='margin:0'><h1>h</h1><h3>h</h3><h6>h</h6></body></html>");

        Assert.That(Block(view, "h1").Source.Style.FontSize, Is.EqualTo(32).Within(0.01), "twice the root size");
        Assert.That(Block(view, "h3").Source.Style.FontSize, Is.EqualTo(16 * 1.17).Within(0.01));
        Assert.That(Block(view, "h6").Source.Style.FontSize, Is.EqualTo(16 * 0.75).Within(0.01));
    }

    [Test]
    public async Task ASideThePageStatesItselfIsTheOneItMeans()
    {
        using var view = CreateView();

        await view.SetHtml(
            "<html><body style='margin:0;writing-mode:vertical-rl'>" +
            "<p style='margin-left:30px'>text</p></body></html>");

        var p = Block(view, "p");

        Assert.That(p.Margin.Left, Is.EqualTo(30).Within(0.01), "the page's own side stands, against the sheet's");
        Assert.That(p.Margin.Top, Is.EqualTo(0).Within(0.01), "and the side it said nothing about keeps the sheet's answer");
    }

    private static HutuViewInternal CreateView()
    {
        var view = new HutuViewInternal(new FakeTextMeasurer());
        view.SetViewportSize(600, 400);
        return view;
    }

    private static LayoutBox Block(HutuViewInternal view, String tag) =>
        Walk(view.Layout!.Root).First(node =>
            node.Kind == LayoutKind.Block && node.Source.Element?.LocalName == tag);

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

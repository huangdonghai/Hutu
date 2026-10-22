using Hutu.Rendering.Layout;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Hutu.Tests;

/// <summary>
/// Covers the insets that are named by the axis they lie on rather than by the side of the page:
/// <c>margin-block-*</c> and <c>margin-inline-*</c>, and the same pair of names for padding. Each is
/// put on the physical side the element's own writing mode and direction send it to.
/// </summary>
/// <remarks>
/// The measurer is the fixed-width one, so the geometry here is arithmetic rather than the fonts of
/// the machine running it.
/// </remarks>
[TestFixture]
public sealed class FlowRelativeEdgesTests
{
    [Test]
    public async Task AColumnReadsThemAlongAndAcrossItself()
    {
        using var view = CreateView();

        await view.SetHtml(
            "<html><body style='margin:0'><div style='writing-mode:vertical-rl;width:100px;height:200px;margin:0'>" +
            "<div id='kid' style='margin-inline-start:10px;margin-block-start:20px;width:20px;height:20px'></div>" +
            "</div></body></html>");

        var kid = Box(view, "kid");

        Assert.That(
            kid.Margin.Top,
            Is.EqualTo(10).Within(0.01),
            "the line runs down the column, so the margin that opens the inline axis is the one at the top");
        Assert.That(
            kid.Margin.Right,
            Is.EqualTo(20).Within(0.01),
            "and the columns advance leftward, so the one that opens the block axis is the one at the right");
    }

    [Test]
    public async Task ARowReadsThemFromTheDirectionItReadsIn()
    {
        using var view = CreateView();

        await view.SetHtml(
            "<html><body style='margin:0'><div dir='rtl'>" +
            "<div id='kid' style='margin-inline-start:10px;margin-block-start:20px;width:20px;height:20px'></div>" +
            "</div></body></html>");

        var kid = Box(view, "kid");

        Assert.That(kid.Margin.Right, Is.EqualTo(10).Within(0.01), "a right-to-left row begins at its right edge");
        Assert.That(kid.Margin.Top, Is.EqualTo(20).Within(0.01), "and its block axis still starts at the top");
    }

    [Test]
    public async Task TheShorthandNamesBothEndsOfOneAxis()
    {
        using var view = CreateView();

        await view.SetHtml(
            "<html><body style='margin:0'><div style='writing-mode:vertical-rl;width:100px;height:200px;margin:0'>" +
            "<div id='kid' style='margin-inline:10px 20px;width:20px;height:20px'></div>" +
            "</div></body></html>");

        var kid = Box(view, "kid");

        Assert.That(kid.Margin.Top, Is.EqualTo(10).Within(0.01), "the first value is the start of the axis");
        Assert.That(kid.Margin.Bottom, Is.EqualTo(20).Within(0.01), "and the second its end");
    }

    [Test]
    public async Task PaddingIsNamedByTheAxisToo()
    {
        using var view = CreateView();

        await view.SetHtml(
            "<html><body style='margin:0'><div style='writing-mode:vertical-rl;width:100px;height:200px;margin:0'>" +
            "<div id='kid' style='padding-inline-start:10px;padding-block-end:20px;width:20px;height:20px'></div>" +
            "</div></body></html>");

        var kid = Box(view, "kid");

        Assert.That(kid.Padding.Top, Is.EqualTo(10).Within(0.01), "the inline axis runs down the column");
        Assert.That(kid.Padding.Left, Is.EqualTo(20).Within(0.01), "and the block axis runs leftward, so its end is the left edge");
    }

    [Test]
    public async Task ASideNamedBothWaysTakesTheOneNamedByAxis()
    {
        using var view = CreateView();

        // The shape a book written for a reader that speaks the flow-relative names has: a reset to
        // clear whatever the reader brought with it, and then the side it actually wants.
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div id='kid' style='margin:0;margin-inline-start:30px;width:20px;height:20px'></div>" +
            "</body></html>");

        var kid = Box(view, "kid");

        Assert.That(
            kid.Margin.Left,
            Is.EqualTo(30).Within(0.01),
            "the side the page named by axis is the one it meant, though the reset named it too");
        Assert.That(kid.Margin.Top, Is.EqualTo(0).Within(0.01), "and the reset is what the other three are");
    }

    [Test]
    public async Task ANameByAxisFillsInWhatThePageLeftUnsaid()
    {
        using var view = CreateView();

        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div id='kid' style='margin-left:10px;margin-top:5px;margin-inline-end:30px;width:20px;height:20px'></div>" +
            "</body></html>");

        var kid = Box(view, "kid");

        Assert.That(kid.Margin.Left, Is.EqualTo(10).Within(0.01), "the side the page stated stands");
        Assert.That(
            kid.Margin.Right,
            Is.EqualTo(30).Within(0.01),
            "and the side it named by axis is the one the axis name is for");
    }

    /// <summary>
    /// A size named for the axis the text runs along measures the side that axis lies on: the inline
    /// axis is the width of a row and the height of a column, and the block axis the other way round
    /// (CSS Logical 1 §4). Where the page names both the side and the axis it lies on, the axis is the
    /// statement it meant, as it is for the margins.
    /// </summary>
    [Test]
    public async Task ARowSizesTheAxisItRunsAlongAsItsOwnWidth()
    {
        using var view = CreateView();

        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div id='kid' style='inline-size:100px;block-size:50px'></div></body></html>");

        var kid = Box(view, "kid");

        Assert.That(kid.BorderBox.Width, Is.EqualTo(100).Within(0.01), "the inline axis of a row is its width");
        Assert.That(kid.BorderBox.Height, Is.EqualTo(50).Within(0.01), "and its block axis is its height");
    }

    [Test]
    public async Task AColumnTurnsTheAxisSizesOntoTheSidesTheyLieOn()
    {
        using var view = CreateView();

        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div id='kid' style='writing-mode:vertical-rl;inline-size:100px;block-size:50px'></div></body></html>");

        var kid = Box(view, "kid");

        Assert.That(kid.BorderBox.Height, Is.EqualTo(100).Within(0.01), "the line runs down the page, so the inline axis is the height");
        Assert.That(kid.BorderBox.Width, Is.EqualTo(50).Within(0.01), "and the columns advance across the block axis");
    }

    [Test]
    public async Task AnAxisNamedSizeOutranksTheSideItLiesOn()
    {
        using var view = CreateView();

        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div id='kid' style='width:30px;height:30px;inline-size:100px;block-size:50px'></div></body></html>");

        var kid = Box(view, "kid");

        Assert.That(kid.BorderBox.Width, Is.EqualTo(100).Within(0.01), "the axis is the statement the page meant");
        Assert.That(kid.BorderBox.Height, Is.EqualTo(50).Within(0.01), "on both of its sides");
    }

    [Test]
    public async Task AnAxisNamedLimitHoldsTheSideItLiesOn()
    {
        using var view = CreateView();

        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div id='kid' style='width:100px;height:10px;max-inline-size:40px;min-block-size:60px'></div></body></html>");

        var kid = Box(view, "kid");

        Assert.That(kid.BorderBox.Width, Is.EqualTo(40).Within(0.01), "the inline axis caps the width of a row");
        Assert.That(kid.BorderBox.Height, Is.EqualTo(60).Within(0.01), "and the block axis floors its height");
    }

    /// <summary>
    /// A border side named by the axis it lies on. <c>border-block-end</c> is the foot of a row and the
    /// head of a column, so the stroke has to land where the element's own flow sends the name (CSS
    /// Logical 1 §4) - which is how a chart draws its axis, the engine reading only the four physical
    /// longhands leaving the line unwritten.
    /// </summary>
    [Test]
    public async Task AnAxisNamedBorderStandsOnTheSideItLiesOn()
    {
        using var view = CreateView();

        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div id='row' style='width:100px;height:10px;border-block-end:2px solid black'></div>" +
            "<div id='column' style='writing-mode:vertical-rl;width:100px;height:80px;border-block-end:2px solid black'></div>" +
            "<div id='axis' style='width:100px;height:10px;border-inline-end:3px solid black'></div>" +
            "<div id='both' style='width:100px;height:10px;border-bottom:1px solid red;border-block-end:4px solid black'></div>" +
            "</body></html>");

        Assert.Multiple(() =>
        {
            Assert.That(Box(view, "row").Border.Bottom, Is.EqualTo(2), "the block axis is the foot of a row");
            Assert.That(Box(view, "row").Border.Top, Is.Zero, "and nothing else of the border");
            Assert.That(Box(view, "column").Border.Left, Is.EqualTo(2), "the same name heads a column, whose block axis runs right to left");
            Assert.That(Box(view, "axis").Border.Right, Is.EqualTo(3), "the inline axis ends at the right of a line that reads leftward");
            Assert.That(Box(view, "both").Border.Bottom, Is.EqualTo(4), "a side named both ways is the axis the page meant");
        });
    }

    /// <summary>
    /// The style and the ink of an axis-named side travel with it. A chart states all three channels of
    /// its axis as custom properties, so a reader that folded only the width would draw a border of the
    /// right thickness in the wrong colour, or none at all: a side whose style is <c>none</c> is zero.
    /// </summary>
    [Test]
    public async Task AnAxisNamedBorderCarriesItsOwnStyleAndInk()
    {
        using var view = CreateView();

        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div id='row' style='--w:2px;--s:solid;--c:green;width:100px;height:10px;" +
            "border-block-end:var(--w) var(--s) var(--c)'></div>" +
            "<div id='foot' style='--w:2px;--s:solid;--c:green;width:100px;height:10px;" +
            "border-bottom:var(--w) var(--s) var(--c)'></div>" +
            "</body></html>");

        var laid = Box(view, "row");

        Assert.Multiple(() =>
        {
            Assert.That(laid.Border.Bottom, Is.EqualTo(2), "the width the property carries");
            Assert.That(laid.Source.Style.BorderBottomStyle, Is.EqualTo("solid"), "the style it names");
            Assert.That(
                laid.Source.Style.BorderBottomColor,
                Is.EqualTo(Box(view, "foot").Source.Style.BorderBottomColor),
                "and the ink, the same one the side's own name gives it");
        });
    }

    private static HutuViewInternal CreateView()
    {
        var view = new HutuViewInternal(new FakeTextMeasurer());
        view.SetViewportSize(600, 400);
        return view;
    }

    private static LayoutBox Box(HutuViewInternal view, String id)
    {
        var root = view.Layout?.Root ?? throw new AssertionException("The document was not laid out.");

        return Walk(root).First(node =>
            node.Kind == LayoutKind.Block && node.Source.Element?.Id == id);
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

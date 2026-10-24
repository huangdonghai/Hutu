using Hutu.Rendering.Layout;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Hutu.Tests;

/// <summary>
/// The scale a page puts on a box and everything under it: the <c>zoom</c> property of CSS Viewport,
/// which multiplies the lengths a box states rather than the numbers its sheets hold - a computed value
/// stays what was written and only the used one grows.
/// </summary>
/// <remarks>
/// Every number here was measured in headless Chrome on 2026-10-24 from the same markup (.tmp/probe-
/// zoom.html), which is the only way to tell a zoom that scales the box from one that scales the page.
/// The measurer is the fixed-width one, so a box stated in pixels is the width it says.
/// </remarks>
[TestFixture]
public sealed class ZoomTests
{
    [Test]
    public async Task AZoomedBoxTakesTheRoomItsLengthesStateTwiceOver()
    {
        using var view = CreateView(400);
        await view.SetHtml(
            "<html><body style='margin:8px'>" +
            "<div id='a' style='width:100px;height:50px;padding:5px;border:2px solid;" +
            "margin:8px;background:#444;zoom:2'>text</div>" +
            "</body></html>");

        var a = Find(view, "a");

        // Chrome: 228x128 with its top left at (24,16) - the border box of 114x64 grown by two, its
        // margin of 8 grown by two, and the body's own eight unzoomed beside it on the inline axis. On
        // the block axis the two margins collapse into the larger of them, so the height reads sixteen.
        Assert.Multiple(() =>
        {
            Assert.That(a.BorderBox.Width, Is.EqualTo(228).Within(0.5), "the border box doubled");
            Assert.That(a.BorderBox.Height, Is.EqualTo(128).Within(0.5));
            Assert.That(a.BorderBox.X, Is.EqualTo(24).Within(0.5), "the margin doubled with it");
            Assert.That(a.BorderBox.Y, Is.EqualTo(16).Within(0.5), "and the two collapse to the larger");
        });
    }

    [Test]
    public async Task AZoomReachesTheBoxesUnderIt()
    {
        using var view = CreateView(400);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div style='zoom:3'><div id='a' style='width:100px;height:50px'></div></div>" +
            "</body></html>");

        var a = Find(view, "a");

        Assert.That(a.BorderBox.Width, Is.EqualTo(300).Within(0.5), "the child carries its parent's scale");
        Assert.That(a.BorderBox.Height, Is.EqualTo(150).Within(0.5));
    }

    [Test]
    public async Task TwoZoomsMultiplyIntoOne()
    {
        using var view = CreateView(400);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div style='zoom:2'><div style='zoom:3'>" +
            "<div id='a' style='width:100px;height:50px'></div></div></div>" +
            "</body></html>");

        var a = Find(view, "a");

        Assert.That(a.BorderBox.Width, Is.EqualTo(600).Within(0.5), "each zoom is a step on the last");
    }

    [Test]
    public async Task AZoomWrittenAsAPercentShrinksWhatItTouches()
    {
        using var view = CreateView(400);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div id='a' style='width:100px;height:20px;background:#f00;zoom:50%'></div>" +
            "</body></html>");

        var a = Find(view, "a");

        Assert.Multiple(() =>
        {
            Assert.That(a.BorderBox.Width, Is.EqualTo(50).Within(0.5), "half, as Chrome lays it");
            Assert.That(a.BorderBox.Height, Is.EqualTo(10).Within(0.5));
        });
    }

    [Test]
    public async Task TextUnderAZoomIsLaidOutAtTheSizeItIsDrawnAt()
    {
        using var view = CreateView(400);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div style='zoom:2'><p id='a' style='font-size:10px;line-height:20px;margin:0'>word</p></div>" +
            "</body></html>");

        var a = Find(view, "a");
        var line = a.Children.First(node => node.Kind == LayoutKind.Line);

        Assert.Multiple(() =>
        {
            Assert.That(line.BorderBox.Height, Is.EqualTo(40).Within(0.5), "the leading doubles with the text");
            Assert.That(
                line.Children.First().Source.Style.FontSize,
                Is.EqualTo(20).Within(0.5),
                "and the run is set in the size it is drawn at");
        });
    }

    [Test]
    public async Task ALengthWrittenInEmIsFoldedBeforeTheZoomIsApplied()
    {
        using var view = CreateView(400);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div id='a' style='font-size:10px;width:100px;margin-top:1em;zoom:2;height:20px'></div>" +
            "</body></html>");

        var a = Find(view, "a");

        Assert.Multiple(() =>
        {
            // One em is the element's own computed ten, and the scale turns it into twenty - which is
            // what Chrome offsets the box by (measured on the same page as the tests above).
            Assert.That(a.BorderBox.Y, Is.EqualTo(20).Within(0.5), "the margin followed the zoom");
            Assert.That(a.BorderBox.Width, Is.EqualTo(200).Within(0.5), "and the width did too");
        });
    }

    [Test]
    public async Task ABoxThatSaysNoZoomKeepsItsOwnNumbers()
    {
        using var view = CreateView(400);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div id='a' style='width:100px;height:50px;padding:5px;border:2px solid;margin:8px'></div>" +
            "</body></html>");

        var a = Find(view, "a");

        Assert.Multiple(() =>
        {
            Assert.That(a.BorderBox.Width, Is.EqualTo(114).Within(0.5), "one is what no zoom means");
            Assert.That(a.BorderBox.Height, Is.EqualTo(64).Within(0.5));
            Assert.That(a.BorderBox.X, Is.EqualTo(8).Within(0.5));
        });
    }

    [Test]
    public async Task AZoomReachesTheGapsAndTheSpacingABoxStates()
    {
        using var view = CreateView(400);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div id='a' style='column-count:2;column-gap:10px;row-gap:4px;zoom:2;width:100px'>word word</div>" +
            "<div id='b' style='display:table;border-collapse:separate;border-spacing:10px 6px;zoom:2'>" +
            "<div style='display:table-row'>" +
            "<div id='c' style='display:table-cell;width:50px'>x</div>" +
            "<div style='display:table-cell;width:50px'>y</div></div></div>" +
            "<div id='f' style='display:flex;flex-wrap:wrap;column-gap:10px;row-gap:3px;zoom:2'>" +
            "<div style='width:20px;height:10px'></div><div style='width:20px;height:10px'></div></div>" +
            "</body></html>");

        var columns = Find(view, "a").Source.Style;
        var table = Find(view, "b").Source.Style;

        Assert.Multiple(() =>
        {
            Assert.That(columns.Multi.Gap, Is.EqualTo(20).Within(0.5), "the gutter between columns doubles");
            Assert.That(columns.Multi.RowGap, Is.EqualTo(8).Within(0.5));
            Assert.That(table.Table.HorizontalSpacing, Is.EqualTo(20).Within(0.5), "and so does a table's spacing");
            Assert.That(table.Table.VerticalSpacing, Is.EqualTo(12).Within(0.5));

            var flex = Find(view, "f").Source.Style.Flex;
            Assert.That(flex.ColumnGap, Is.EqualTo(20).Within(0.5), "and the room a flex row leaves between items");
            Assert.That(flex.RowGap, Is.EqualTo(6).Within(0.5));
        });
    }

    [Test]
    public async Task AZoomReachesTheDistanceATransformMovesBy()
    {
        using var view = CreateView(400);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div id='a' style='width:50px;height:20px;transform:translate(30px,10px);zoom:2'></div>" +
            "<div id='b' style='width:50px;height:20px;transform:matrix(1,0,0,-1,30,10);zoom:2'></div>" +
            "</body></html>");

        var moved = Find(view, "a").Transform!.Value;
        var numbered = Find(view, "b").Transform!.Value;

        Assert.Multiple(() =>
        {
            Assert.That(moved.E, Is.EqualTo(60).Within(0.5), "the move doubles with the box");
            Assert.That(moved.F, Is.EqualTo(20).Within(0.5));
            Assert.That(numbered.E, Is.EqualTo(60).Within(0.5), "and so does the same move as numbers");
            Assert.That(numbered.F, Is.EqualTo(20).Within(0.5));
            Assert.That(numbered.D, Is.EqualTo(-1).Within(0.001), "a ratio is no length and stays itself");
        });
    }

    private static HutuViewInternal CreateView(Int32 viewportWidth)
    {
        var view = new HutuViewInternal(new FakeTextMeasurer());
        view.SetViewportSize(viewportWidth, 600);
        return view;
    }

    private static LayoutBox Find(HutuViewInternal view, String id)
    {
        foreach (var node in Walk(view.Layout!.Root))
        {
            if (node.Source.Element?.GetAttribute("id") == id)
            {
                return node;
            }
        }

        Assert.Fail($"no box with id '{id}' was laid out");
        return null!;
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

using Hutu.Rendering;
using Hutu.Rendering.Boxes;
using Hutu.Rendering.Layout;
using NUnit.Framework;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace Hutu.Tests;

/// <summary>
/// Covers <c>float</c> and <c>clear</c>: a box pulled out of the stack and pinned to one edge of its
/// container, the container growing to hold what it holds, and the boxes that refuse to stand beside.
/// </summary>
/// <remarks>
/// The portal layout this was written against is two columns - <c>float: left</c> at 860 pixels and
/// <c>float: right</c> at 300 - inside a container that has to reach as far down as the taller of
/// them. Asserted on explicit widths and heights wherever that is possible, so the numbers are the
/// layout's arithmetic rather than the measurer's.
/// </remarks>
[TestFixture]
public sealed class FloatLayoutTests
{
    [Test]
    public async Task TwoFloatsStandSideBySideInOneContainer()
    {
        using var view = CreateView();

        await view.SetHtml(Page(
            "<div id='area' style='width:1200px'>" +
            "<div id='lm' style='float:left;width:860px;height:400px'></div>" +
            "<div id='r' style='float:right;width:300px;height:500px'></div>" +
            "</div>"));

        Assert.That(Bounds(view, "lm"), Is.EqualTo(new Rect(0, 0, 860, 400)));
        Assert.That(
            Bounds(view, "r"),
            Is.EqualTo(new Rect(900, 0, 300, 500)),
            "the right float takes the container's right edge and stands at the same height");
    }

    [Test]
    public async Task TheContainerReachesAsFarAsTheDeepestFloat()
    {
        using var view = CreateView();

        await view.SetHtml(Page(
            "<div id='area' style='width:300px'>" +
            "<div style='float:left;width:100px;height:70px'></div>" +
            "<div style='float:right;width:80px;height:40px'></div>" +
            "</div>" +
            "<p id='after' style='margin:0'>after</p>"));

        Assert.That(
            Bounds(view, "area").Height,
            Is.EqualTo(70),
            "a container of floats is as tall as the float that reaches furthest down");
        Assert.That(
            Bounds(view, "after").Y,
            Is.EqualTo(70),
            "and what follows it starts below the floats rather than beside them");
    }

    [Test]
    public async Task AClearedBoxMovesBelowTheFloats()
    {
        using var view = CreateView();

        await view.SetHtml(Page(
            "<div id='area' style='width:300px'>" +
            "<div style='float:left;width:100px;height:70px'></div>" +
            "<div style='float:right;width:80px;height:40px'></div>" +
            "<p id='both' style='margin:0;clear:both;height:20px'>both</p>" +
            "</div>"));

        Assert.That(Bounds(view, "both").Y, Is.EqualTo(70), "clear:both goes below the taller of the two");
    }

    [Test]
    public async Task AClearedBoxStaysBesideTheSideItDoesNotClear()
    {
        using var view = CreateView();

        await view.SetHtml(Page(
            "<div id='area' style='width:300px'>" +
            "<div id='lm' style='float:left;width:100px;height:70px'></div>" +
            "<div id='r' style='float:right;width:80px;height:40px'></div>" +
            "<p id='right' style='margin:0;clear:right;height:20px'>right</p>" +
            "</div>"));

        // The cleared side's float is the one it moves under; the left float keeps its column to
        // itself at the top, so the box lands at the height the right float's bottom reaches.
        Assert.That(Bounds(view, "right").Y, Is.EqualTo(40));
        Assert.That(Bounds(view, "lm").Y, Is.EqualTo(0), "and the float it did not clear is left where it was");
    }

    [Test]
    public async Task TwoFloatsOfOneSideShareARowUntilTheRoomRunsOut()
    {
        using var view = CreateView();

        await view.SetHtml(Page(
            "<div id='area' style='width:300px'>" +
            "<div id='a' style='float:left;width:120px;height:50px'></div>" +
            "<div id='b' style='float:left;width:120px;height:30px'></div>" +
            "<div id='c' style='float:left;width:80px;height:20px'></div>" +
            "</div>"));

        Assert.That(Bounds(view, "a"), Is.EqualTo(new Rect(0, 0, 120, 50)));
        Assert.That(Bounds(view, "b"), Is.EqualTo(new Rect(120, 0, 120, 30)));

        // The third does not fit beside the pair - two hundred and forty of the three hundred are
        // taken - so it steps down to the next height where its own edge is free: b has ended thirty
        // pixels down, so it lands there, beside a, which still reaches further than that.
        Assert.That(Bounds(view, "c"), Is.EqualTo(new Rect(120, 30, 80, 20)));

        // And the container is as tall as the float that reaches furthest down, which is the first
        // one - not as tall as the stack, which took nothing from it.
        Assert.That(Bounds(view, "area").Height, Is.EqualTo(50));
    }

    [Test]
    public async Task AFloatsMarginKeepsOthersOffTheGroundItOccupies()
    {
        using var view = CreateView();

        await view.SetHtml(Page(
            "<div id='area' style='width:300px'>" +
            "<div style='float:left;width:100px;height:50px;margin-right:40px'></div>" +
            "<div id='r' style='float:left;width:100px;height:20px'></div>" +
            "</div>"));

        Assert.That(Bounds(view, "r").X, Is.EqualTo(140), "the margin box, not the border box, is what is reserved");
    }

    [Test]
    public async Task AnAutoWidthFloatShrinksToItsContent()
    {
        using var view = CreateView(600);

        await view.SetHtml(Page(
            "<div id='area' style='width:600px'>" +
            "<div id='f' style='float:left'>abcd</div>" +
            "</div>"));

        // The fake measurer gives five pixels a character, so the content the float would rather have
        // is twenty across - and the 600 it was given room for is not what it takes.
        Assert.That(
            Bounds(view, "f").Width,
            Is.EqualTo(20),
            "a float whose width is auto takes its preferred width, not the container's");
    }

    [Test]
    public async Task AFloatOnAnInlineElementTakesItOffTheLine()
    {
        using var view = CreateView();

        await view.SetHtml(Page(
            "<div id='area' style='width:200px'>" +
            "<a id='x' href='#' style='float:left;width:80px;height:30px'>x</a>" +
            "<a id='y' href='#' style='float:right;width:80px;height:30px'>y</a>" +
            "</div>"));

        Assert.That(Bounds(view, "x").X, Is.EqualTo(0));
        Assert.That(
            Bounds(view, "y").X,
            Is.EqualTo(120),
            "the anchor's own display is inline, and a float blockifies it before it is placed");
    }

    [Test]
    public async Task AnAbsolutePositionedBoxIsNotFloated()
    {
        using var view = CreateView();

        await view.SetHtml(Page(
            "<div id='area' style='width:300px;position:relative'>" +
            "<div id='a' style='position:absolute;float:left;left:10px;top:20px;width:50px;height:10px'></div>" +
            "</div>"));

        Assert.That(Bounds(view, "a"), Is.EqualTo(new Rect(10, 20, 50, 10)), "the offsets decide where it stands");
        Assert.That(
            Bounds(view, "area").Height,
            Is.EqualTo(0),
            "and it neither floats into the container's tracks nor grows the container");
    }

    [Test]
    public async Task AColumnLeavesItsFloatsInTheColumnStack()
    {
        using var view = CreateView();

        var body = "<body style='margin:0;writing-mode:vertical-rl'>";

        using var floated = CreateView();
        using var plain = CreateView();

        await floated.SetHtml(
            $"<html>{body}<div id='area' style='width:300px'>" +
            "<div id='a' style='float:left;width:100px;height:70px'></div>" +
            "<div id='b' style='float:right;width:80px;height:40px'></div>" +
            "</div></body></html>");

        await plain.SetHtml(
            $"<html>{body}<div id='area' style='width:300px'>" +
            "<div id='a' style='width:100px;height:70px'></div>" +
            "<div id='b' style='width:80px;height:40px'></div>" +
            "</div></body></html>");

        // A page that reads down a column has its block axis running sideways, and the tracks here
        // only know how to stack down. The gap is stated rather than guessed at: a floated box in a
        // column lands exactly where the same box with no float would.
        Assert.That(Bounds(floated, "a"), Is.EqualTo(Bounds(plain, "a")));
        Assert.That(Bounds(floated, "b"), Is.EqualTo(Bounds(plain, "b")));
    }

    /// <summary>
    /// The next gap, written down rather than approved: a block whose container holds a float still
    /// makes every one of its line boxes the full width of that container, so its text is drawn under
    /// the floating box. The block's own box already overlaps the float the way a browser's does - that
    /// part is right - and what is missing is the one rule CSS adds on top of it, that a line box next
    /// to a float starts at the float's edge and ends before it. It is not a one-line change because the
    /// width a line breaks at is decided by the cached line plan, which a float would have to make a
    /// function of where the line sits.
    /// </summary>
    [Test]
    public async Task ALineStillRunsUnderAFloat()
    {
        using var view = CreateView();

        await view.SetHtml(Page(
            "<div id='h' style='width:200px'>" +
            "<img id='f' src='x.png' style='width:120px;height:60px;float:left'>" +
            "<p id='p' style='margin:0'>word word word word word word word word word word</p></div>"));

        var paragraph = view.Layout!.FindBox(view.Document!.GetElementById("p")!)!;
        var line = paragraph.Children.FirstOrDefault(box => box.Kind == LayoutKind.Line);

        Assert.That(line, Is.Not.Null, "the paragraph has a line box to look at");
        Assert.That(line!.BorderBox.Y, Is.EqualTo(0).Within(0.5), "the block does overlap the float, as it should");

        // A browser shortens the lines standing beside the image to the 80 pixels left of it and
        // returns to the full width below it: the first line box there is 80 wide in a 200 pixel block.
        Assert.That(line.BorderBox.X, Is.EqualTo(0), "the line does not step past the float");
        Assert.That(line.BorderBox.Width, Is.EqualTo(200).Within(0.5), "nor is it shortened by it");
    }

    private static Rect Bounds(HutuViewInternal view, String id)
    {
        var element = view.Document!.GetElementById(id);

        Assert.That(element, Is.Not.Null, $"no element with id '{id}' in the document");

        var bounds = view.GetElementBounds(element!);

        Assert.That(bounds, Is.Not.Null, $"'{id}' generated no box to measure");
        return bounds!.Value;
    }

    private static String Page(String content) =>
        "<html><body style='margin:0'>" + content + "</body></html>";

    private static HutuViewInternal CreateView(Int32 viewportWidth = 600)
    {
        var view = new HutuViewInternal(new FakeTextMeasurer());
        view.SetViewportSize(viewportWidth, 600);
        return view;
    }
}

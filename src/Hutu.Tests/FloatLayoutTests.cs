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
    public async Task AFixedWidthFormattingContextDropsBesideANarrowerFloat()
    {
        using var view = CreateView();

        await view.SetHtml(Page(
            "<div id='area' style='width:100px'>" +
            "<div id='f1' style='float:left;width:100px;height:50px'></div>" +
            "<div id='f2' style='float:left;width:50px;height:50px'></div>" +
            "<div id='bfc' style='overflow:hidden;width:50px;height:50px'></div>" +
            "</div>"));

        // f1 fills the row, so a fixed-width formatting context cannot squeeze beside it and steps
        // down to f2's row, taking the fifty pixels f2 leaves on the right - not shoved past the
        // container's edge beside f1.
        Assert.That(Bounds(view, "f1"), Is.EqualTo(new Rect(0, 0, 100, 50)));
        Assert.That(Bounds(view, "f2"), Is.EqualTo(new Rect(0, 50, 50, 50)));
        Assert.That(Bounds(view, "bfc"), Is.EqualTo(new Rect(50, 50, 50, 50)));
    }

    [Test]
    public async Task AFixedWidthFormattingContextStillStandsBesideAFloatItFits()
    {
        using var view = CreateView();

        await view.SetHtml(Page(
            "<div id='area' style='width:400px'>" +
            "<div id='f' style='float:left;width:200px;height:20px'></div>" +
            "<div id='bfc' style='overflow:hidden;width:200px;height:15px;margin-right:100px'></div>" +
            "</div>"));

        // The box fits in the room the float leaves, so it stays beside it: an ignored trailing
        // margin does not force a descent. Only a box wider than the room drops below.
        Assert.That(Bounds(view, "bfc"), Is.EqualTo(new Rect(200, 0, 200, 15)));
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
    /// A line box standing beside a float begins at the float's far edge and ends before it, and the
    /// lines below the float take the block's full width again. This is the rule that makes text read
    /// around a picture rather than under it, and the block's own box overlapping the float - which it
    /// does in a browser too - is what leaves the shortening as the only thing to do.
    /// </summary>
    [Test]
    public async Task ALineStopsAtTheFloatAndResumesBelowIt()
    {
        using var view = CreateView();

        await view.SetHtml(Page(
            "<div id='h' style='width:200px'>" +
            "<img id='f' src='x.png' style='width:120px;height:20px;float:left'>" +
            "<p id='p' style='margin:0'>word word word word word word word word word word</p></div>"));

        var paragraph = view.Layout!.FindBox(view.Document!.GetElementById("p")!)!;
        var lines = paragraph.Children.Where(box => box.Kind == LayoutKind.Line).ToList();

        Assert.That(lines[0].BorderBox.X, Is.EqualTo(120).Within(0.5), "the line starts at the float's far edge");
        Assert.That(lines[0].BorderBox.Width, Is.EqualTo(80).Within(0.5), "and reaches only what it leaves");
        Assert.That(lines.Count, Is.GreaterThan(2), "narrower lines break the same words into more of them");
        Assert.That(
            lines.Any(line => line.BorderBox.Width > 199),
            "and a line below the float takes the block's whole width again");
    }

    /// <summary>
    /// A float belongs to the container that holds it and shortens the lines of the blocks inside that
    /// container as well, however deep - which is the difference between text beside a picture and text
    /// that only steps around the picture sitting in its own box.
    /// </summary>
    [Test]
    public async Task ADescendantsLinesStandAsideForTheContainersFloat()
    {
        using var view = CreateView();

        await view.SetHtml(Page(
            "<div id='h' style='width:200px'>" +
            "<img id='f' src='x.png' style='width:120px;height:20px;float:left'>" +
            "<div id='w' style='margin:0'><p id='p' style='margin:0'>word word word word word word</p></div></div>"));

        var paragraph = view.Layout!.FindBox(view.Document!.GetElementById("p")!)!;
        var line = paragraph.Children.First(box => box.Kind == LayoutKind.Line);

        Assert.That(line.BorderBox.X, Is.EqualTo(120).Within(0.5), "the float is three boxes above it");
        Assert.That(line.BorderBox.Width, Is.EqualTo(80).Within(0.5));
    }

    /// <summary>
    /// A float pinned to the far edge shortens the line's reach rather than its start, so the words keep
    /// beginning at the content edge and stop short of the picture.
    /// </summary>
    [Test]
    public async Task AFloatOnTheFarEdgeEndsTheLineShortOfIt()
    {
        using var view = CreateView();

        await view.SetHtml(Page(
            "<div id='h' style='width:200px'>" +
            "<img id='f' src='x.png' style='width:100px;height:20px;float:right'>" +
            "<p id='p' style='margin:0'>word word word word word word</p></div>"));

        var paragraph = view.Layout!.FindBox(view.Document!.GetElementById("p")!)!;
        var line = paragraph.Children.First(box => box.Kind == LayoutKind.Line);

        Assert.That(line.BorderBox.X, Is.EqualTo(0).Within(0.5), "the line keeps its own start");
        Assert.That(line.BorderBox.Width, Is.EqualTo(100).Within(0.5), "and reaches only to the float");
    }

    /// <summary>
    /// A float that leaves a line no room at all is a line that has to stand below it, and the height it
    /// dropped through is room the block's own stack has to count - which is why both passes ask the
    /// same question of the same plan.
    /// </summary>
    [Test]
    public async Task ALineWithNoRoomLeftItDropsBelowTheFloat()
    {
        using var view = CreateView();

        await view.SetHtml(Page(
            "<div id='h' style='width:200px'>" +
            "<img id='f' src='x.png' style='width:200px;height:30px;float:left'>" +
            "<p id='p' style='margin:0'>word word word word word word</p></div>"));

        var paragraph = view.Layout!.FindBox(view.Document!.GetElementById("p")!)!;
        var lines = paragraph.Children.Where(box => box.Kind == LayoutKind.Line).ToList();

        Assert.That(lines[0].BorderBox.Y, Is.EqualTo(30).Within(0.5), "below the float that left it nowhere");
        Assert.That(lines[0].BorderBox.Width, Is.EqualTo(200).Within(0.5), "where the block is its own width again");
        Assert.That(paragraph.BorderBox.Height, Is.GreaterThanOrEqualTo(40), "and the drop is part of the height");
    }

    /// <summary>
    /// The border edge of a box that keeps its own formatting context may not touch a float: it steps
    /// to the room the float leaves and takes that room as its width, which is the whole reason
    /// <c>overflow: hidden</c> beside a floated picture has been a two column layout for twenty years.
    /// A box that clips is a container rather than a run of text, so it moves instead of only its lines
    /// shortening.
    /// </summary>
    [Test]
    public async Task AFormattingRootStepsClearOfTheFloatAndTakesTheRoomLeft()
    {
        using var view = CreateView();

        await view.SetHtml(Page(
            "<div id='h' style='width:200px'>" +
            "<img id='f' src='x.png' style='width:120px;height:60px;float:left'>" +
            "<div id='b' style='overflow:hidden;height:20px'>aside</div></div>"));

        var aside = Bounds(view, "b");

        Assert.That(aside.X, Is.EqualTo(120).Within(0.5), "clear of the float's margin edge");
        Assert.That(aside.Width, Is.EqualTo(80).Within(0.5), "and as wide as what that leaves");
    }

    /// <summary>
    /// Nothing outside a formatting root narrows what is inside it: once the box has stepped clear of
    /// the floats, its own content is the container's whole width and its lines read as if no float
    /// existed above it - which is why both halves of the last test had to happen together.
    /// </summary>
    [Test]
    public async Task TheLinesInsideAFormattingRootAnswerOnlyToItsOwnFloats()
    {
        using var view = CreateView();

        await view.SetHtml(Page(
            "<div id='h' style='width:200px'>" +
            "<img id='f' src='x.png' style='width:120px;height:60px;float:left'>" +
            "<div id='b' style='overflow:hidden'><p id='p' style='margin:0'>word word word word word word</p></div></div>"));

        var box = view.Layout!.FindBox(view.Document!.GetElementById("b")!)!;
        var paragraph = view.Layout.FindBox(view.Document!.GetElementById("p")!)!;
        var line = paragraph.Children.First(node => node.Kind == LayoutKind.Line);

        var content = box.BorderBox.X + box.Border.Left + box.Padding.Left;

        Assert.That(line.BorderBox.X, Is.EqualTo(content).Within(0.5), "the box already stood clear");
        Assert.That(line.BorderBox.Right, Is.EqualTo(box.BorderBox.Right).Within(0.5), "so its lines fill it");
        Assert.That(paragraph.BorderBox.Width, Is.EqualTo(80).Within(0.5));
    }

    [Test]
    public async Task AFloatIsAsWideAsTheRowItsOwnFloatsMake()
    {
        using var view = CreateView(400);

        await view.SetHtml(Page(
            "<div id='f' style='float:right'>" +
            "<div id='a' style='float:left;width:60px;height:20px'></div>" +
            "<div id='b' style='float:left;width:60px;height:20px'></div>" +
            "</div>"));

        // The two inside stand beside each other, so the row they make is 120 wide and the float holding
        // them is 120 wide too. Asked for the widest of its children - which is what a stack of blocks
        // wants - the float is made half as wide, the second box drops below the first, and a pair of
        // badges in a footer comes out as a column of them.
        var outer = Bounds(view, "f");

        Assert.Multiple(() =>
        {
            Assert.That(outer.Width, Is.EqualTo(120).Within(0.5), "the whole row rather than the widest of it");
            Assert.That(outer.Height, Is.EqualTo(20).Within(0.5), "and one row tall");
            Assert.That(Bounds(view, "b").X, Is.EqualTo(340).Within(0.5), "beside its sibling at the edge");
        });
    }

    [Test]
    public async Task AFlowRootContainsItsChildMarginsWhereAPlainBlockLetsThemEscape()
    {
        using var view = CreateView();

        await view.SetHtml(Page(
            "<div style='width:200px'>" +
            "<div id='fr' style='display:flow-root'><div style='height:10px;margin-top:20px;margin-bottom:30px'></div></div>" +
            "<div id='after' style='height:10px'></div>" +
            "</div>"));

        // A flow-root establishes a block formatting context, so the child's margins are contained
        // inside it rather than escaping through its edges: the box is the whole 20+10+30 tall and the
        // box after it starts below that, not drawn up into the escaped margin as a plain block would.
        Assert.That(Bounds(view, "fr").Height, Is.EqualTo(60).Within(0.5), "contains the child margins");
        Assert.That(Bounds(view, "after").Y, Is.EqualTo(60).Within(0.5));
    }

    [Test]
    public async Task AFloatWithAPercentageMarginShrinksToItsContent()
    {
        using var view = CreateView();

        await view.SetHtml(Page(
            "<div id='c' style='width:200px'>" +
            "<span id='f' style='float:right; margin-right:50%'>ab</span>" +
            "</div>"));

        // A floated box shrinks to its content. Its percentage margin is measured against the
        // containing block - half of 200 is 100 - which leaves room for the two letters (10px), so the
        // box is 10px wide. The old bug resolved that margin against the smaller shrink-to-fit room
        // it handed the box, which fed back into the width and grew the box instead of shrinking it.
        Assert.That(Bounds(view, "f").Width, Is.EqualTo(10).Within(0.5), "shrinks to its content, margin measured against the containing block");
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

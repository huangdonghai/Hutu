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
    public async Task AFloatStepsBelowAnotherFloatsMarginBox()
    {
        using var view = CreateView();

        // A hundred-pixel container: the second float's seventy-five-pixel margin box cannot stand
        // beside the first, so it begins below that box rather than at its border edge, and its own top
        // margin stands in between. Chrome puts the second border box at (25, 100).
        await view.SetHtml(Page(
            "<div style='width:100px'>" +
            "<div id='a' style='float:left;width:25px;height:25px;margin:25px'></div>" +
            "<div id='b' style='float:left;width:25px;height:25px;margin:25px'></div>" +
            "</div>"));

        Assert.Multiple(() =>
        {
            Assert.That(Bounds(view, "a"), Is.EqualTo(new Rect(25, 25, 25, 25)), "the first float stands inside its own margin box");
            Assert.That(
                Bounds(view, "b"),
                Is.EqualTo(new Rect(25, 100, 25, 25)),
                "the second begins below the first float's margin box and then adds its own top margin");
        });
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
    public async Task AClearedFormattingRootTakesTheRoomLeftWhereItLands()
    {
        using var view = CreateView(1000);

        await view.SetHtml(Page(
            "<div id='area' style='width:1000px'>" +
            "<div id='left' style='float:left;width:400px;height:500px'></div>" +
            "<div id='right' style='float:right;width:400px;height:700px'></div>" +
            "<div id='links' style='clear:left;overflow:hidden;height:20px'></div>" +
            "</div>"));

        // clear:left moves the box below the left float, but the right float still reaches past
        // that height. The formatting root therefore begins at the container's left edge and takes
        // the 600 pixels left of the right float, rather than measuring the zero-width gap between
        // both floats at the height it started from.
        Assert.That(
            Bounds(view, "links"),
            Is.EqualTo(new Rect(0, 500, 600, 20)),
            "clearance moves the box before its width is read off the floats");
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
    public async Task AColumnPutsItsFloatsAgainstTheBlockStartAndTheLinesSide()
    {
        using var view = CreateView();

        await view.SetHtml(
            "<html><body style='margin:0;writing-mode:vertical-rl'>" +
            "<div id='area' style='width:300px;height:200px'>" +
            "<div id='a' style='float:left;width:100px;height:70px'></div>" +
            "<div id='b' style='float:right;width:80px;height:40px'></div>" +
            "</div></body></html>");

        // The axes are exchanged, so a float of a column is pinned to the right edge - the block start
        // of a right-to-left column - and to the side of the line it names: the top for `left`, the
        // bottom for `right`. Chrome measures exactly this (CSS 2.1 §9.5, Writing Modes 3 §7).
        var a = Bounds(view, "a");
        var b = Bounds(view, "b");


        Assert.That(a.X, Is.EqualTo(500).Within(0.01), "the block start, the area's own right edge");
        Assert.That(a.Y, Is.EqualTo(0).Within(0.01), "the line's left");
        Assert.That(a.Width, Is.EqualTo(100).Within(0.01));

        Assert.That(b.X, Is.EqualTo(520).Within(0.01), "the same band, its own width taken off");
        Assert.That(b.Y, Is.EqualTo(160).Within(0.01), "the line's right");
    }

    /// <summary>
    /// A column whose line reads upward keeps a physical float where a downward one puts it - the
    /// keywords name line-left and line-right, which are the head and the foot of the page - and moves
    /// only a logical one, which names the ends of the line and so follows the direction. Measured in
    /// Chrome in a 200px column.
    /// </summary>
    [Test]
    public async Task ARightToLeftColumnKeepsAPhysicalFloatAndTurnsALogicalOne()
    {
        using var view = CreateView();

        await view.SetHtml(
            "<html><body style='margin:0;writing-mode:vertical-rl;direction:rtl'>" +
            "<div id='area' style='width:300px;height:200px'>" +
            "<div id='head' style='float:left;width:100px;height:70px'></div>" +
            "<div id='start' style='float:inline-start;width:80px;height:40px'></div>" +
            "</div></body></html>");

        var area = Bounds(view, "area");
        var head = Bounds(view, "head");
        var start = Bounds(view, "start");

        Assert.That(head.X, Is.EqualTo(area.Right - 100).Within(0.01), "both stand at the block start");
        Assert.That(head.Y, Is.EqualTo(area.Y).Within(0.01), "line-left is still the head of the page");
        Assert.That(start.X, Is.EqualTo(area.Right - 80).Within(0.01));
        Assert.That(start.Y, Is.EqualTo(area.Bottom - 40).Within(0.01), "the edge the line begins at is its foot");
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
    public async Task AFloatStandsBelowTheMarginTheBoxBeforeItLeft()
    {
        using var view = CreateView();

        await view.SetHtml(Page(
            "<div id='area' style='width:300px'>" +
            "<p id='before' style='margin:0 0 20px;height:10px'>before</p>" +
            "<div id='f' style='float:left;width:50px;height:50px'></div>" +
            "</div>"));

        // The paragraph's bottom margin does not collapse with the float: CSS 2.1 §8.3.1 keeps a
        // float's margins from collapsing with any other box, so the strip the run above has built
        // up stands between the two and the float's outer top goes below it - the place the next
        // box in flow would have taken, which is where the box it stands beside would put it.
        Assert.That(
            Bounds(view, "f"),
            Is.EqualTo(new Rect(0, 30, 50, 50)),
            "the float starts below the margin the box before it left, not on top of it");
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

    [Test]
    public async Task AFloatHeldByAPlainContainerReachesTheContainerItBelongsTo()
    {
        using var view = CreateView();

        // The float sits in a plain block that establishes no formatting context of its own, so
        // CSS 2.1 §9.4.1 puts it in the block formatting context of the nearest ancestor that
        // does - the page's own - and it reaches out of the ten-pixel box that holds it, standing
        // twenty pixels tall over the boxes below. The block under it stands aside for the float,
        // which is the difference between a float beside a picture and a float that only
        // shortens the lines of the box it was written in.
        await view.SetHtml(Page(
            "<div id='hold' style='height:10px'>" +
            "<div id='f' style='float:left;width:100px;height:20px'></div>" +
            "</div>" +
            "<div id='after' style='margin-left:40px;width:70px'>" +
            "<span id='wide' style='display:inline-block;width:60px;height:10px'></span>" +
            "</div>"));

        // The block below starts beside the float's lower half, where the float leaves it ten
        // pixels of the seventy it asked for - too little for the sixty the inline-block needs,
        // so CSS 2.1 §9.5 shifts the line holding it below the float rather than overflowing
        // the room the float left.
        Assert.That(
            Bounds(view, "wide"),
            Is.EqualTo(new Rect(40, 20, 60, 10)),
            "the line holding the inline-block drops below the float the plain container let out");
    }

    [Test]
    public async Task AFloatHeldByAPlainContainerIsNotMovedTwiceOnTheWayUp()
    {
        using var view = CreateView();

        // Three plain blocks in a row, each holding a float as wide as itself. The arranging
        // pass works in document coordinates from end to end, so the floats a container's walk
        // hands up are already written in the frame of the container above and owe it no
        // distance on the way in. Asking for that distance anyway - the measuring pass's own
        // arithmetic, where the walk counts from its content top - moved every float a second
        // time and left the last container reading a float standing below its own content,
        // which pushed the float's own line out of the float.
        await view.SetHtml(Page(
            "<div id='row1' style='width:200px;height:20px'>" +
            "<div id='f1' style='float:left;width:200px;height:20px'>a</div></div>" +
            "<div id='row2' style='width:200px;height:20px'>" +
            "<div id='f2' style='float:left;width:200px;height:20px'>b</div></div>" +
            "<div id='row3' style='width:200px;height:20px'>" +
            "<div id='f3' style='float:left;width:200px;height:20px'>c</div></div>"));

        var line = Walk(view.Layout!.Root!)
            .Where(node => node.Kind == LayoutKind.Line)
            .Last();

        Assert.That(
            line.BorderBox.Y,
            Is.EqualTo(Bounds(view, "f3").Y),
            "the last float's own line sits at the float's top, not below the floats above it");
    }

    [Test]
    public async Task ALineThatHoldsNothingBesideAFloatDropsBelowIt()
    {
        using var view = CreateView();

        // The other half of the rule the test above holds: a line whose first piece is wider
        // than the room the float leaves holds nothing at all, and CSS 2.1 §9.5 shifts it down
        // until some content fits - the wide picture goes below the floated one rather than
        // overflowing the strip beside it. A line that already holds something broke the
        // ordinary way and keeps its place beside the float.
        await view.SetHtml(Page(
            "<div id='area' style='width:200px'>" +
            "<div id='f' style='float:left;width:120px;height:20px'></div>" +
            "<span id='wide' style='display:inline-block;width:150px;height:10px'></span>" +
            "</div>"));

        // The line drops below the float and takes the block's whole width again, so the
        // inline-block stands at the container's left edge under the float - not squeezed into
        // the strip beside it, and not hanging off the float's far edge.
        Assert.That(
            Bounds(view, "wide"),
            Is.EqualTo(new Rect(0, 20, 150, 10)),
            "the inline-block too wide for the room beside the float stands below it");
    }

    /// <summary>
    /// A float steps clear of the floats of its context, not only of the ones its own box holds: CSS 2.1
    /// §9.5 puts every float of one formatting context in the same run, and a container that holds a float
    /// is still in the context above it. Measured in Chrome against <c>gecko/bugs/428810-2a-ltr</c>, where
    /// a hundred-pixel float stands in the row above and a fifteen-pixel one is held by the box below it:
    /// the lower float is put <em>under</em> the upper, at the height the upper one leaves, and the text
    /// beside it then has the room the lower float spares - which is where the bullet of that item hangs.
    /// </summary>
    [Test]
    public async Task AFloatStepsClearOfAFloatItOnlyStandsBeside()
    {
        using var view = CreateView();

        await view.SetHtml(Page(
            "<div style='height:10px'>" +
            "<div style='float:left;width:100px;height:20px'></div>" +
            "</div>" +
            "<div style='margin-left:40px;width:70px'>" +
            "<div id='under' style='float:left;width:15px;height:20px'></div>" +
            "</div>"));

        // The upper float reaches twenty down and spans the whole left of the row; standing at the container's
        // own left edge would lay the lower float on top of it, so it begins where the upper one ends.
        Assert.That(
            Bounds(view, "under"),
            Is.EqualTo(new Rect(40, 20, 15, 20)),
            "a float of the context blocks a float that is not its own neighbour");
    }

    /// <summary>
    /// A float is the root of a block formatting context of its own (CSS 2.1 §9.5), so the room it leaves
    /// for others is not a room taken out of its own lines - and the track it just reserved in its parent
    /// is not a float standing beside it. Chrome was measured on the reference of
    /// <c>wpt/css-grid/grid-model/grid-margins-no-collapse-002</c>: a left float with a vertical margin, then
    /// a floated paragraph clearing it, puts its single line at the top of its own box rather than at the
    /// bottom of a band cut by itself.
    /// </summary>
    [Test]
    public async Task AFloatingBoxTakesNoRoomOutOfItsOwnLines()
    {
        using var view = CreateView();

        await view.SetHtml(Page(
            "<div id='root' style='float:left;width:200px'>" +
            "<div style='float:left;width:60px;height:20px;margin:20px 0'></div>" +
            "<div><p id='cleared' style='float:left;clear:both;margin:0'>AB</p></div>" +
            "</div>"));

        var paragraph = Bounds(view, "cleared");

        Assert.Multiple(() =>
        {
            // The clear carries it past the float above - sixty down: twenty of margin, twenty of the float,
            // twenty of margin again - and its own line then stands at the head of its own box.
            Assert.That(paragraph.Y, Is.EqualTo(60), "the cleared float stands below the one it refuses");
            Assert.That(paragraph.Height, Is.EqualTo(10), "and holds one line of its own, not a band");
        });
    }

    /// <summary>
    /// What pushes a float down is another float it would really stand on, and not the edge of the block it
    /// was put in: CSS 2.1 §9.5.1 pins its outer edge against the containing block and a float wider than
    /// that block simply overflows, the way a browser lets one overflow. Measured in Chrome on
    /// <c>gecko/floats/other-float-outside-rule-3-left</c>, where a four-hundred-and-twenty-five pixel float
    /// is held by a four-hundred pixel block beside a fifty pixel float on the far side that it never
    /// touches: it stays at the top of the block.
    /// </summary>
    [Test]
    public async Task AFloatWiderThanItsBlockOverflowsRatherThanDropping()
    {
        using var view = CreateView(560);

        await view.SetHtml(Page(
            "<div style='float:left;width:500px;height:500px'>" +
            "<div style='float:right;width:50px;height:300px'></div>" +
            "<div style='margin-right:100px'>" +
            "<div id='wide' style='float:left;width:425px;height:10px'></div>" +
            "</div>" +
            "</div>"));

        Assert.That(
            Bounds(view, "wide"),
            Is.EqualTo(new Rect(0, 0, 425, 10)),
            "the float is pinned to the block's left edge and overflows it, because the far float is beyond it");
    }

    /// <summary>
    /// A row that reads right to left begins at the block's <em>end</em> edge, so a float standing on that
    /// side shortens its lines from there - and the edge has to be the one that is known before the row is
    /// measured, since the inline size is what the constraint equation has still to settle (§10.3.3).
    /// Chrome on <c>gecko/bugs/428810-1a-rtl</c> puts a line that cannot fit beside a right float under it,
    /// exactly as the left-to-right mirror does.
    /// </summary>
    [Test]
    public async Task ARightToLeftLineStandsClearOfAFloatAtItsEndEdge()
    {
        using var view = CreateView(800);

        // The direction belongs to the row the boxes stand in, not to the box that reads that way: a block is
        // placed by its containing block's row, which is what the borrowed page sets on its root.
        await view.SetHtml(Page(
            "<div dir='rtl'>" +
            "<div style='height:10px'>" +
            "<div style='float:right;width:100px;height:20px'></div>" +
            "</div>" +
            "<div id='row' style='margin-right:40px;width:70px'>ABCDEFGH</div>" +
            "</div>"));

        // Forty of the row's seventy pixels are taken by the float, and forty letters of text are twenty, so
        // the line has nowhere to stand beside it and goes under: the row begins ten down and its first line
        // lands below the float that ends at twenty.
        var row = Bounds(view, "row");
        var line = Walk(view.Layout!.Root).Single(box => box.Kind == LayoutKind.Line);

        Assert.Multiple(() =>
        {
            Assert.That(
                $"{row.X},{row.Y} {row.Width}x{row.Height}",
                Is.EqualTo("690,10 70x20"),
                "the row stands against the end edge of a page that reads right to left");
            Assert.That(
                $"{line.BorderBox.X},{line.BorderBox.Y} {line.BorderBox.Width}x{line.BorderBox.Height}",
                Is.EqualTo("690,20 70x10"),
                "and its line drops below the float at its end edge rather than spilling over it");
        });
    }

    /// <summary>
    /// White space the line rules may collapse away leaves no trace between two blocks, but a no-break space
    /// is not white space to those rules (CSS Text 3 §4): it is measured, never folded into a neighbour and
    /// never trimmed from either end of a line. A run of them beside a floated child is content and holds a
    /// line of its own, which is what the borrowed <c>428810</c> family rests on.
    /// </summary>
    [Test]
    public async Task ANoBreakSpaceRunBesideAFloatedChildHoldsALine()
    {
        using var view = CreateView();

        await view.SetHtml(Page(
            "<div style='width:200px'>" +
            "<div id='held' style='float:left;width:50px;height:20px'></div>" +
            "&nbsp;&nbsp;&nbsp;&nbsp;&nbsp;" +
            "</div>"));

        // Five no-break spaces are five characters of five pixels each, on one line, standing beside the
        // float; the document's own list of spaces would have folded them away with the run.
        var lines = Walk(view.Layout!.Root).Where(box => box.Kind == LayoutKind.Line).ToList();
        var text = Walk(view.Layout!.Root).Single(box => box.Kind == LayoutKind.Text);

        Assert.Multiple(() =>
        {
            Assert.That(lines, Has.Count.EqualTo(1), "the run of no-break spaces is content, not a blank");
            Assert.That(
                $"{lines[0].BorderBox.X},{lines[0].BorderBox.Y} {lines[0].BorderBox.Width}x{lines[0].BorderBox.Height}",
                Is.EqualTo("50,0 150x10"),
                "the line stands beside the float and takes the room the float leaves");
            Assert.That(
                $"{text.BorderBox.X},{text.BorderBox.Y} {text.BorderBox.Width}x{text.BorderBox.Height}",
                Is.EqualTo("50,0 25x10"),
                "five of them are five characters, measured and kept");
        });
    }

    /// <summary>
    /// A float shortens the lines of the block it stands in and stops at the edge of any formatting
    /// context it meets: an inline-block roots one of its own, so the floats outside it are not the ones
    /// its lines stand aside for - it is the box as a whole that steps around them, which the line the box
    /// sits on has already settled (CSS 2.1 §9.4.1, §9.5). Letting the band through dropped the nested
    /// first line below the float and made the box taller than anything written in it.
    /// </summary>
    [Test]
    public async Task AFloatStopsAtTheFormattingContextAnInlineBlockRoots()
    {
        using var view = CreateView(400);

        await view.SetHtml(Page(
            "<div style='float:left;width:50px;height:50px'></div>" +
            "<div id='box' style='display:inline-block'>aaaaaaaaaa<br>aaaaaaaaaa</div>"));

        var box = Bounds(view, "box");
        var nested = Walk(view.Layout!.Root)
            .Where(node => node.Kind == LayoutKind.Line &&
                node.Source.Element?.GetAttribute("id") == "box")
            .OrderBy(node => node.BorderBox.Y)
            .ToList();

        Assert.Multiple(() =>
        {
            Assert.That(nested, Has.Count.GreaterThanOrEqualTo(2), "the box holds the two lines its text makes");
            Assert.That(
                box.Height,
                Is.EqualTo(20).Within(0.5),
                "two lines of ten, which is all the box holds - not the float's fifty added to them");
            Assert.That(
                nested[0].BorderBox.Y,
                Is.EqualTo(box.Y).Within(0.5),
                "and its first line stands at the top of the box, not below the float");
        });
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

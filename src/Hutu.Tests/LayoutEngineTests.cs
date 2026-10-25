using Hutu.Rendering.Layout;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Hutu.Tests;

/// <summary>
/// Layout behaviour asserted through real AngleSharp documents, with a deterministic text measurer
/// so geometry can be pinned to exact numbers. Documents are the input because the style layer is
/// where the box model actually comes from; faking the declaration block would test the fake.
/// </summary>
[TestFixture]
public sealed class LayoutEngineTests
{
    [Test]
    public async Task ThePageIsPlacedInsideTheMarginsItIsGiven()
    {
        using var view = CreateView(400);

        await view.SetHtml(
            "<html style='margin:20px 0 0 30px'><body style='margin:0'><div style='height:10px'></div></body></html>");

        var root = view.Layout!.Root;

        Assert.That(root.BorderBox.X, Is.EqualTo(30).Within(0.5), "the page's left margin sets it in from the left");
        Assert.That(root.BorderBox.Y, Is.EqualTo(20).Within(0.5), "and its top margin from the top, as the initial containing block's first box");
    }

    [Test]
    public async Task BlockBoxModelResolvesPaddingBorderAndMargin()
    {
        using var view = CreateView(400);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div style='width:100px;height:50px;padding:10px;border:5px solid #000;margin:20px'></div>" +
            "</body></html>");

        var box = Find(view, "div");

        Assert.That(box, Is.Not.Null);
        Assert.That(box!.BorderBox.Width, Is.EqualTo(130).Within(0.5), "content 100 + padding 20 + border 10");
        Assert.That(box.BorderBox.Height, Is.EqualTo(80).Within(0.5), "content 50 + padding 20 + border 10");
        Assert.That(box.BorderBox.X, Is.EqualTo(20).Within(0.5), "margin-left");
        Assert.That(box.BorderBox.Y, Is.EqualTo(20).Within(0.5), "margin-top");
    }

    [Test]
    public async Task AutoWidthFillsTheContainingBlock()
    {
        using var view = CreateView(300);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div style='padding:10px'></div>" +
            "</body></html>");

        var box = Find(view, "div");

        Assert.That(box!.BorderBox.Width, Is.EqualTo(300).Within(0.5), "auto width spans the body content box");
    }

    [Test]
    public async Task SiblingMarginsCollapseToTheLargest()
    {
        using var view = CreateView(400);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div style='height:10px;margin-bottom:20px'></div>" +
            "<div style='height:10px;margin-top:30px'></div>" +
            "</body></html>");

        var boxes = FindAll(view, "div").ToList();

        Assert.That(boxes, Has.Count.EqualTo(2));
        Assert.That(boxes[1].BorderBox.Y, Is.EqualTo(40).Within(0.5), "10 + max(20, 30)");
    }

    [Test]
    public async Task AGridItemHoldsTheMarginsOfTheBoxesInIt()
    {
        // CSS Grid 1 §6.1: a grid item establishes an independent formatting context for its
        // contents, so a child's margin is the item's to hold - the same rule as a box with
        // `overflow: hidden`. Folding it out of the item instead loses the room the page asked for
        // and carries everything after it up: Wikipedia's sitenotice is a `margin: 24px 0` box inside
        // a grid item, and the page's whole header grid followed it.
        using var view = CreateView(400);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div style='display:grid'>" +
            "<div id='item'><div id='note' style='margin:24px 0'></div></div>" +
            "<div id='after' style='height:10px'></div>" +
            "</div></body></html>");

        var item = ById(view, "item");
        var note = ById(view, "note");

        Assert.That(item.BorderBox.Height, Is.EqualTo(24).Within(0.5), "the item is as tall as the margin inside it");
        Assert.That(note.BorderBox.Y - item.BorderBox.Y, Is.EqualTo(24).Within(0.5), "the note stands after its own top margin (CSS 2.1 §8.3.1)");
        Assert.That(ById(view, "after").BorderBox.Y - item.BorderBox.Y, Is.EqualTo(24).Within(0.5), "nothing below is pulled up");
    }

    [Test]
    public async Task AFlexItemHoldsTheMarginsOfTheBoxesInIt()
    {
        // The same clause for a flex item (CSS Flexbox 1 §4).
        using var view = CreateView(400);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div style='display:flex'>" +
            "<div id='item'><div id='note' style='margin:24px 0'></div></div>" +
            "</div></body></html>");

        var item = ById(view, "item");

        Assert.That(item.BorderBox.Height, Is.EqualTo(24).Within(0.5));
        Assert.That(ById(view, "note").BorderBox.Y - item.BorderBox.Y, Is.EqualTo(24).Within(0.5));
    }

    [Test]
    public async Task APlainBlockStillLetsAMarginEscapeThroughIt()
    {
        // The control for the two above: a box that establishes no formatting context of its own
        // takes no part in the collapse, so the margin inside it passes straight through and the
        // box itself is nothing tall.
        using var view = CreateView(400);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div id='item'><div id='note' style='margin:24px 0'></div></div>" +
            "</body></html>");

        Assert.That(ById(view, "item").BorderBox.Height, Is.EqualTo(0).Within(0.5));
    }

    [Test]
    public async Task AFixedHeightParentAbsorbsItsLastChildsBottomMargin()
    {
        using var view = CreateView(400);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div style='height:100px'><div style='height:40px;margin-bottom:20px'></div></div>" +
            "<div style='height:20px'></div>" +
            "</body></html>");

        var boxes = FindAll(view, "div").ToList();

        Assert.That(boxes, Has.Count.EqualTo(3));
        // The parent's height is definite, so the child's 20px bottom margin collapses into that fixed
        // region rather than escaping below the parent: the separator sits at the parent's bottom edge.
        Assert.That(boxes[0].BorderBox.Height, Is.EqualTo(100).Within(0.5), "fixed height");
        Assert.That(boxes[2].BorderBox.Y, Is.EqualTo(100).Within(0.5), "no escaped margin");
    }

    [Test]
    public async Task DisplayNoneSubtreeIsDropped()
    {
        using var view = CreateView(400);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div style='display:none'>hidden</div>" +
            "<div>visible</div>" +
            "</body></html>");

        Assert.That(FindAll(view, "div").ToList(), Has.Count.EqualTo(1));
    }

    [Test]
    public async Task ScriptAndStyleContentIsNotLaidOut()
    {
        // Neither element has a box, so their source - which the HTML allows in the body, and where
        // Wikipedia puts its scripts - must not reach a line.
        using var view = CreateView(400);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<script>var answer = 42;</script>" +
            "<style>.note { color: red }</style>" +
            "<div>visible</div>" +
            "</body></html>");

        var text = String.Concat(Lines(view).Select(LineText));

        Assert.That(text, Is.EqualTo("visible"));
    }

    [Test]
    public async Task TextWrapsAtWhitespaceWithoutSplittingWords()
    {
        // Each character is 5px wide, so "aaaa bbbb" cannot fit into 25px on one line.
        using var view = CreateView(25);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<p style='margin:0'>aaaa bbbb</p>" +
            "</body></html>");

        var lines = Lines(view).Select(LineText).ToList();

        Assert.That(lines, Is.EqualTo(new[] { "aaaa", "bbbb" }));
    }

    [Test]
    public async Task ExplicitBreakStartsANewLine()
    {
        using var view = CreateView(400);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<p style='margin:0'>one<br>two</p>" +
            "</body></html>");

        var lines = Lines(view).Select(LineText).ToList();

        Assert.That(lines, Is.EqualTo(new[] { "one", "two" }));
    }

    [Test]
    public async Task WhitespaceRunsCollapseToASingleSpace()
    {
        using var view = CreateView(400);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<p style='margin:0'>a    b</p>" +
            "</body></html>");

        var lines = Lines(view).Select(LineText).ToList();

        Assert.That(lines, Is.EqualTo(new[] { "a b" }));
    }

    [Test]
    public async Task SpaceAfterAnInlineElementSurvivesCollapsing()
    {
        // The space stands at the head of the text node that follows </b>, and a node's own
        // collapse used to take it for a line's leading white space and drop it.
        using var view = CreateView(400);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<p style='margin:0'><b>slice</b> (the initial value)</p>" +
            "</body></html>");

        var lines = Lines(view).Select(LineText).ToList();

        Assert.That(lines, Is.EqualTo(new[] { "slice (the initial value)" }));
    }

    /// <summary>
    /// <c>overflow: initial</c> states no keyword of its own: it is the property's first value, which for
    /// this non-inherited one is <c>visible</c> (CSS Values 4 §4.1, CSS Overflow 3 §3.2). Reading the word
    /// as though it named a clip is what cut the bottom off a round chart body - the sheet resets the
    /// table with <c>overflow: initial</c>, the circle is a caption taller than the table, and the table
    /// then clipped a descendant it was never asked to hold.
    /// </summary>
    [Test]
    public async Task AResetOverflowHoldsNothingIn()
    {
        using var view = CreateView(400);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div id='first' style='overflow: initial;height:20px'><div style='height:80px'></div></div>" +
            "<div id='unset' style='overflow: unset;height:20px'><div style='height:80px'></div></div>" +
            "<div id='hidden' style='overflow: hidden;height:20px'><div style='height:80px'></div></div>" +
            "</body></html>");

        Assert.Multiple(() =>
        {
            Assert.That(ById(view, "first").ClipsOverflow, Is.False, "`initial` is the first value, `visible`");
            Assert.That(ById(view, "unset").ClipsOverflow, Is.False, "and so is `unset` on a property nothing inherits");
            Assert.That(ById(view, "hidden").ClipsOverflow, Is.True, "the keyword that does clip still clips");
        });
    }

    [Test]
    public async Task AColumnLendsItsOwnWidthToAHorizontalChild()
    {
        // The arranging pass has to hand a column's child the same block size the measuring pass did.
        // It used to hand it the column's own containing block instead, so a paragraph turned back to
        // horizontal filled the viewport rather than the 200px body it stands in.
        using var view = CreateView(400);
        await view.SetHtml(
            "<html><body style='margin:0;width:200px;height:200px;writing-mode:vertical-rl'>" +
            "<p style='margin:0;writing-mode:horizontal-tb'>x</p></body></html>");

        Assert.That(Find(view, "p")!.BorderBox.Width, Is.EqualTo(200).Within(0.5), "the body's declared width");
    }

    [Test]
    public async Task ANoBreakSpaceIsALineOnThePage()
    {
        // U+00A0 is white space to the framework but not the collapsible kind CSS removes at a line's
        // edge, so a box holding one has a line of its own and its margins do not fold through it.
        using var view = CreateView(400);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div id='space' style='font-size:15px;line-height:1;margin:10px 0 20px'>&nbsp;</div>" +
            "</body></html>");

        Assert.That(ById(view, "space").BorderBox.Height, Is.EqualTo(15).Within(0.5), "one line, not nothing");
    }

    [Test]
    public async Task EmptyInlineBoxesLeaveNoLineToSeparateTheMargins()
    {
        // CSS 2.1 §9.4.2: a line box holding nothing but collapsible white space and inline elements
        // without margins, padding or borders is zero-height and does not exist for any other purpose.
        // The anonymous block the run makes must therefore collapse through, letting the paragraph's
        // top margin fold with the body's.
        using var view = CreateView(400);
        await view.SetHtml(
            "<html><body style='margin:8px'><span></span> <span></span>" +
            "<p style='margin:16px 0'>x</p></body></html>");

        Assert.That(Find(view, "p")!.BorderBox.Y, Is.EqualTo(16).Within(0.5), "max(body 8, paragraph 16)");
    }

    [Test]
    public async Task ASizeContainedFlexFillsTheWidthAndAsksItsContentForNothing()
    {
        // Size containment answers the block size the content would have decided, but a block-level
        // flex container's auto width is the containing block's, not the content's (CSS Contain 1
        // §3.1.1; CSS Flexbox 1 §4).
        using var view = CreateView(400);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div id='c' style='display:flex;contain:size;border:10px solid'>" +
            "<div style='width:100px;height:100px'></div></div>" +
            "</body></html>");

        var box = ById(view, "c");

        Assert.Multiple(() =>
        {
            Assert.That(box.BorderBox.Width, Is.EqualTo(400).Within(0.5), "auto width fills the body");
            Assert.That(box.BorderBox.Height, Is.EqualTo(20).Within(0.5), "the content lends no height");
        });
    }

    [Test]
    public async Task AContainedMulticolAsksForTheGapsBetweenItsColumns()
    {
        // Size containment leaves the content with no size, but the columns and the gaps between them
        // still stand: a floated three-column box is as wide as its two 1em gaps (Chromium measured,
        // contain-size-multicol-002).
        using var view = CreateView(400);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div id='c' style='float:left;contain:size;column-count:3;border:10px solid'></div>" +
            "</body></html>");

        Assert.That(ById(view, "c").BorderBox.Width, Is.EqualTo(52).Within(0.5), "20 of border and two 16px gaps");
    }

    [Test]
    public async Task ACanvasIsAReplacedBoxAtTheSizeThePageStates()
    {
        // A canvas is replaced content the page draws itself: its CSS width and height size the box,
        // and the default object size answers an axis the page leaves out (CSS 2.1 §10.3.2, §10.6.2).
        using var view = CreateView(400);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<canvas id='c' style='display:block;width:100px;height:200px;background:orange'></canvas>" +
            "</body></html>");

        var canvas = Walk(view.Layout!.Root).FirstOrDefault(node => node.Source.Element?.LocalName == "canvas");

        Assert.That(canvas, Is.Not.Null, "a canvas generates a box");
        Assert.Multiple(() =>
        {
            Assert.That(canvas!.BorderBox.Width, Is.EqualTo(100).Within(0.5), "the stated width");
            Assert.That(canvas.BorderBox.Height, Is.EqualTo(200).Within(0.5), "the stated height");
        });
    }

    [Test]
    public async Task SizeContainmentAppliesToATableCaption()
    {
        // CSS Contain 2 §3.1.1 excludes table boxes and internal table boxes from size containment but
        // says a caption is not one of them: a caption can hold a size of its own, so `contain: size`
        // leaves it zero-sized and its content is hidden by the clip.
        using var view = CreateView(400);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div id='caption' style='display:table-caption;contain:size;overflow:hidden'>FAIL</div>" +
            "</body></html>");

        var caption = Walk(view.Layout!.Root)
            .FirstOrDefault(node => node.Source.Element?.GetAttribute("id") == "caption");

        Assert.That(caption, Is.Not.Null, "the caption generates a box");
        Assert.That(caption!.BorderBox.Height, Is.EqualTo(0).Within(0.5), "nothing from its content");
    }

    [Test]
    public async Task ALayoutContainedBoxStandsClearOfAFloat()
    {
        // Layout containment makes the box an independent formatting context (CSS Contain 2 §3.2), so
        // its border box may not overlap a float, exactly as an `overflow: hidden` box may not: it
        // steps into the room the float leaves.
        using var view = CreateView(400);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div style='float:left;width:100px;height:20px'></div>" +
            "<div id='c' style='contain:layout;width:50px;height:10px'></div>" +
            "</body></html>");

        Assert.That(ById(view, "c").BorderBox.X, Is.EqualTo(100).Within(0.5), "the float's width");
    }

    [Test]
    public async Task HiddenContentVisibilityKeepsTheBoxAndSkipsWhatItHolds()
    {
        // CSS Contain 4 §2: `content-visibility: hidden` skips the element's contents - it paints
        // neither them nor its pseudo-elements - while the element's own box, background and size
        // remain. Nothing under it is laid out.
        using var view = CreateView(400);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div id='h' style='content-visibility:hidden;width:100px;height:100px;background:green'>" +
            "<div id='k' style='width:50px;height:50px'></div>text</div>" +
            "<div id='v'>visible</div></body></html>");

        var hidden = ById(view, "h");

        Assert.Multiple(() =>
        {
            Assert.That(hidden.BorderBox.Width, Is.EqualTo(100).Within(0.5), "the element's own box stays");
            Assert.That(hidden.BorderBox.Height, Is.EqualTo(100).Within(0.5), "and its stated height");
            Assert.That(
                Walk(view.Layout!.Root).Any(node => node.Source.Element?.GetAttribute("id") == "k"),
                Is.False,
                "nothing below it is laid out");
            Assert.That(String.Concat(Lines(view).Select(LineText)), Is.EqualTo("visible"), "the sibling is untouched");
        });
    }

    [Test]
    public async Task ALineDoesNotBreakBetweenLettersAcrossAnInlineElement()
    {
        // UAX #14 LB28: two letters are one word, whichever element wrote them. A run from another
        // box is chunked on its own, so the break its start offers has to be refused across the edge.
        using var view = CreateView(25);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<p style='margin:0'>PASS<span>FAIL</span></p></body></html>");

        Assert.That(Lines(view).Select(LineText), Is.EqualTo(new[] { "PASSFAIL" }));
    }

    [Test]
    public async Task ADisplayContentsElementLeavesItsChildrenInTheParentsFlow()
    {
        // CSS Display 3 §2.5: the element generates no box, so its block child stacks in the parent's
        // flow and its text joins the parent's inline content.
        using var view = CreateView(400);
        await view.SetHtml(
            "<html><body style='display:contents'>" +
            "<p style='margin:0'>first</p>second</body></html>");

        Assert.Multiple(() =>
        {
            Assert.That(Find(view, "body"), Is.Null, "the body generates no box");
            Assert.That(Find(view, "p"), Is.Not.Null, "its paragraph is a child of the root");
            Assert.That(Lines(view).Select(LineText), Is.EquivalentTo(new[] { "first", "second" }));
        });
    }

    [Test]
    public async Task AContainedBodyDoesNotHandItsWritingModeToTheRoot()
    {
        // CSS Contain 1 §2: containment on the root or the body disables the propagation of the body's
        // writing-mode, overflow and background to the page. Only the writing mode is asserted here; the
        // canvas already answers the background, and no box in this document can overflow.
        using var plain = CreateView(400);
        await plain.SetHtml(
            "<html><body style='margin:0;writing-mode:vertical-rl'><p style='margin:0'>x</p></body></html>");

        using var onBody = CreateView(400);
        await onBody.SetHtml(
            "<html><body style='margin:0;writing-mode:vertical-rl;contain:layout'>" +
            "<p style='margin:0'>x</p></body></html>");

        using var onRoot = CreateView(400);
        await onRoot.SetHtml(
            "<html style='contain:layout'><body style='margin:0;writing-mode:vertical-rl'>" +
            "<p style='margin:0'>x</p></body></html>");

        Assert.Multiple(() =>
        {
            Assert.That(plain.Layout!.Root.Flow.IsVertical, Is.True, "the body's mode reaches a page that contains nothing");
            Assert.That(onBody.Layout!.Root.Flow.IsVertical, Is.False, "a contained body keeps its mode");
            Assert.That(onRoot.Layout!.Root.Flow.IsVertical, Is.False, "and so does a body under a contained root");
        });
    }

    private static HutuViewInternal CreateView(int viewportWidth)
    {
        var view = new HutuViewInternal(new FakeTextMeasurer());
        view.SetViewportSize(viewportWidth, 600);
        return view;
    }

    private static LayoutBox? Find(HutuViewInternal view, String tag) => FindAll(view, tag).FirstOrDefault();

    private static LayoutBox ById(HutuViewInternal view, String id) =>
        FindAll(view, "div").First(box => box.Source.Element?.GetAttribute("id") == id);

    private static IEnumerable<LayoutBox> FindAll(HutuViewInternal view, String tag)
    {
        var root = view.Layout?.Root;

        if (root is null)
        {
            yield break;
        }

        foreach (var node in Walk(root))
        {
            // Line and text nodes report the container/fragment they belong to as their source, so
            // only the block-level boxes actually stand for an element.
            if (node.Kind is LayoutKind.Block or LayoutKind.HorizontalRule &&
                node.Source.Element is { } element &&
                String.Equals(element.LocalName, tag, StringComparison.OrdinalIgnoreCase))
            {
                yield return node;
            }
        }
    }

    private static IEnumerable<LayoutBox> Lines(HutuViewInternal view)
    {
        var root = view.Layout?.Root;

        return root is null
            ? Enumerable.Empty<LayoutBox>()
            : Walk(root).Where(node => node.Kind == LayoutKind.Line);
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

    /// <summary>
    /// Gets the text a line box was set from. The runs are looked for through the whole subtree rather
    /// than among the line's own children, because an inline element stands between the two: its runs
    /// hang off its own node, one node per line the element reaches.
    /// </summary>
    private static String LineText(LayoutBox line) =>
        String.Concat(Walk(line).Where(node => node.Kind == LayoutKind.Text).Select(node => node.Text));
}

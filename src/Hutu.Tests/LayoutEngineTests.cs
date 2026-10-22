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

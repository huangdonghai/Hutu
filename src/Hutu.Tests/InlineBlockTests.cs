using Hutu.Rendering.Layout;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Hutu.Tests;

/// <summary>
/// Covers <c>display: inline-block</c>: a box the line places whole, which is as large as its own
/// content asks for and stands on its own baseline rather than on the line's.
/// </summary>
/// <remarks>
/// Asserted against the fake measurer's fixed numbers - five pixels a character, an ascent of eight
/// and a descent of two, whatever the font size - so the geometry is the layout's own arithmetic and
/// not the machine's fonts. The paragraph the content is set in has a line height of two, which gives
/// every line of text twenty pixels of its own.
/// </remarks>
[TestFixture]
public sealed class InlineBlockTests
{
    [Test]
    public async Task ABoxIsAsLargeAsItsOwnContent()
    {
        using var view = CreateView();

        await view.SetHtml(Document("<span style='display:inline-block'>abcd</span>"));

        var block = Box(view, "span");

        Assert.That(
            block.BorderBox.Width,
            Is.EqualTo(20),
            "an auto width shrinks to the content instead of filling what the line has left");
        Assert.That(block.BorderBox.Height, Is.EqualTo(20), "the content is one line of the paragraph's own height");
        Assert.That(block.Children.Single().Kind, Is.EqualTo(LayoutKind.Line), "the box lays its content out as lines");
    }

    [Test]
    public async Task PaddingAndBorderMakeAPill()
    {
        using var view = CreateView();

        await view.SetHtml(Document(
            "<span style='display:inline-block;padding:3px 9px;border:1px solid #000000'>abcd</span>"));

        var block = Box(view, "span");

        Assert.That(block.BorderBox.Width, Is.EqualTo(40), "1 + 9 + 20 + 9 + 1");
        Assert.That(block.BorderBox.Height, Is.EqualTo(28), "1 + 3 + 20 + 3 + 1");
    }

    [Test]
    public async Task TheBoxStandsOnItsOwnBaseline()
    {
        using var view = CreateView();

        await view.SetHtml(Document("ab<span style='display:inline-block;padding:3px 9px'>abcd</span>"));

        var run = Text(view, "ab");
        var block = Box(view, "span");
        var inner = Walk(block).First(node => node.Kind == LayoutKind.Line);

        // The text inside the box has to sit on the line's baseline - not the box's bottom edge, and
        // not the top of the room the box takes, which is what makes it read with the text beside it.
        Assert.That(
            inner.BorderBox.Y + inner.Baseline,
            Is.EqualTo(run.BorderBox.Y + run.Baseline).Within(0.001));

        Assert.That(inner.BorderBox.Y - block.BorderBox.Y, Is.EqualTo(3), "and the box reaches its own padding above it");
    }

    [Test]
    public async Task ABoxTallerThanItsTextGrowsTheLine()
    {
        using var view = CreateView();

        await view.SetHtml(Document("ab<span style='display:inline-block;padding:20px 0'>cd</span>"));

        var block = Box(view, "span");

        Assert.That(block.BorderBox.Height, Is.EqualTo(60));
        Assert.That(
            Lines(view).Single().BorderBox.Height,
            Is.EqualTo(60),
            "unlike an inline element, the box pushes the line out to hold it");
    }

    [Test]
    public async Task ABoxThatDoesNotFitMovesWholeToTheNextLine()
    {
        using var view = CreateView(40);

        await view.SetHtml(Document("aaaa<span style='display:inline-block'>bbbbbb</span>", width: 40));

        var lines = Lines(view).ToList();
        var block = Box(view, "span");

        Assert.That(lines.Count, Is.EqualTo(2), "twenty pixels of text and thirty of box do not fit on one line");
        Assert.That(block.BorderBox.Width, Is.EqualTo(30), "the box is not broken across the two");
        Assert.That(block.BorderBox.Y, Is.EqualTo(20), "the box was carried down to the second line");
        Assert.That(Walk(block).Count(node => node.Kind == LayoutKind.Line), Is.EqualTo(1));
    }

    [Test]
    public async Task ABoxNeverShrinksBelowWhatItsContentCannotGoUnder()
    {
        using var view = CreateView();

        await view.SetHtml(Document("<span style='display:inline-block'>あいう</span>", width: 2));

        var block = Box(view, "span");

        // A CJK character may be broken away from its neighbours, so the narrowest the content can go
        // is one character, which is what the box keeps however little room the line has left.
        Assert.That(block.BorderBox.Width, Is.EqualTo(5), "one character, not the two pixels of room");
    }

    [Test]
    public async Task TwoBoxesOnALineEachTakeTheirOwnWidth()
    {
        using var view = CreateView();

        await view.SetHtml(Document(
            "<span style='display:inline-block'>ab</span><span style='display:inline-block'>cdef</span>"));

        var blocks = Boxes(view, "span").ToList();

        Assert.That(blocks[0].BorderBox.Width, Is.EqualTo(10));
        Assert.That(blocks[0].BorderBox.X, Is.EqualTo(0));
        Assert.That(blocks[1].BorderBox.Width, Is.EqualTo(20));
        Assert.That(blocks[1].BorderBox.X, Is.EqualTo(10), "the second box starts where the first ends");
    }

    [Test]
    public async Task TheMarginsBesideABoxTakeRoomOnTheLine()
    {
        using var view = CreateView();

        await view.SetHtml(Document(
            "<span style='display:inline-block;margin:0 7px'>ab</span>cd"));

        var block = Box(view, "span");

        Assert.That(block.BorderBox.X, Is.EqualTo(7), "the margin stands before the box");
        Assert.That(block.BorderBox.Width, Is.EqualTo(10), "and is not part of it");
        Assert.That(Text(view, "cd").BorderBox.X, Is.EqualTo(24), "the far margin stands after it");
    }

    [Test]
    public async Task ABoxMayHoldBlockContentOfItsOwn()
    {
        using var view = CreateView();

        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div style='font-size:10px;line-height:2'><span style='display:inline-block'><div>ab</div></span></div>" +
            "</body></html>");

        var block = Box(view, "span");

        Assert.That(block.Children.Single().Kind, Is.EqualTo(LayoutKind.Block), "the inner div is a block container");
        Assert.That(block.BorderBox.Width, Is.EqualTo(10), "the box is still as wide as what it holds");
        Assert.That(block.BorderBox.Height, Is.EqualTo(20));
    }

    [Test]
    public async Task ABoxIsNestedInsideTheElementAroundIt()
    {
        using var view = CreateView();

        await view.SetHtml(Document(
            "<mark style='padding:0 5px'><span style='display:inline-block'>ab</span></mark>"));

        var mark = Inline(view, "mark");
        var block = Box(view, "span");

        Assert.That(mark.Children.Single(), Is.SameAs(block), "the box hangs off the element it sits inside");
        Assert.That(mark.BorderBox.Width, Is.EqualTo(20), "5 + 10 + 5: the element wraps the box and its own padding");
    }

    /// <summary>
    /// A column is where the gap is: an inline-block's inline axis is its height there, and the room
    /// the line has left is measured along that axis, so the shrinking this fixture otherwise covers
    /// has nothing to bound. The box is placed unconstrained until that is modelled, which is what the
    /// ignored assertion records.
    /// </summary>
    [Test]
    [Ignore("shrink-to-fit is not modelled along a column's inline axis")]
    public async Task ABoxInAColumnShrinksToFitTheColumn()
    {
        using var view = CreateView();

        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<p style='margin:0;font-size:10px;line-height:1;writing-mode:vertical-rl;height:40px'>" +
            "<span style='display:inline-block'>ab</span></p></body></html>");

        Assert.That(Box(view, "span").BorderBox.Height, Is.EqualTo(10));
    }

    /// <summary>
    /// Gets the document the content is set in: a paragraph with no margin of its own, set in a font
    /// the fake measurer gives a ten-pixel content area whatever the size.
    /// </summary>
    private static String Document(String content, Int32 width = 0) =>
        "<html><body style='margin:0'><p style='margin:0;font-size:10px;line-height:2" +
        (width > 0 ? ";width:" + width + "px" : String.Empty) +
        "'>" + content + "</p></body></html>";

    private static HutuViewInternal CreateView(Int32 viewportWidth = 600)
    {
        var view = new HutuViewInternal(new FakeTextMeasurer());
        view.SetViewportSize(viewportWidth, 600);
        return view;
    }

    /// <summary>Finds the one box a <c>display: inline-block</c> element was given.</summary>
    private static LayoutBox Box(HutuViewInternal view, String tag) => Boxes(view, tag).Single();

    private static IEnumerable<LayoutBox> Boxes(HutuViewInternal view, String tag) =>
        Walk(view).Where(node => node.Kind == LayoutKind.Block && Name(node) == tag);

    /// <summary>Finds the one inline element of the given name, which is boxed around its own runs.</summary>
    private static LayoutBox Inline(HutuViewInternal view, String tag) =>
        Walk(view).Single(node => node.Kind == LayoutKind.Inline && Name(node) == tag);

    /// <summary>Finds the one text box drawn from the given characters, which is how a run is picked out.</summary>
    private static LayoutBox Text(HutuViewInternal view, String text) =>
        Walk(view).SingleOrDefault(node => node.Kind == LayoutKind.Text && node.Text == text)
            ?? throw new AssertionException($"The layout holds no text box for '{text}'.");

    /// <summary>
    /// Gets the line boxes of the paragraph itself, which is what the content forms lines in. The line
    /// boxes an inline-block formed inside itself are its own and are reached through it.
    /// </summary>
    private static IEnumerable<LayoutBox> Lines(HutuViewInternal view) =>
        Walk(view).Single(node => node.Kind == LayoutKind.Block && Name(node) == "p").Children
            .Where(node => node.Kind == LayoutKind.Line);

    private static String? Name(LayoutBox box) => box.Source.Element?.LocalName;

    private static IEnumerable<LayoutBox> Walk(HutuViewInternal view)
    {
        var root = view.Layout?.Root;

        return root is null ? Enumerable.Empty<LayoutBox>() : Walk(root);
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

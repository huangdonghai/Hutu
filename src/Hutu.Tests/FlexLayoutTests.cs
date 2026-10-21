using Hutu.Rendering.Layout;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Hutu.Tests;

/// <summary>
/// The flex formatting context: the main axis both ways, wrapping, the flexible lengths, the
/// alignment keywords and the gaps.
/// </summary>
/// <remarks>
/// A document is the input so that <c>display: flex</c>, the flex longhands and the alignment
/// keywords all come from a real cascade - the reading layer, not the algorithm, is where the
/// upstream value shapes are most likely to be misread. The assertions are about the geometry the
/// items end up with, which is the only thing the algorithm is for.
/// </remarks>
[TestFixture]
public sealed class FlexLayoutTests
{
    [Test]
    public async Task ItemsSitSideBySideAlongTheRow()
    {
        using var view = CreateView(400);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div style='display:flex;width:300px;height:50px'>" +
            "<div id='a' style='width:100px'></div>" +
            "<div id='b' style='width:100px'></div>" +
            "</div>" +
            "</body></html>");

        Assert.That(Find(view, "a").BorderBox.X, Is.EqualTo(0).Within(0.5));
        Assert.That(Find(view, "b").BorderBox.X, Is.EqualTo(100).Within(0.5));
    }

    /// <summary>
    /// Margins come off the free space before it is shared: an item's flexible length is its base size
    /// plus its share of what the container has left once every item's outer hypothetical main size -
    /// base, borders, padding and margins together - has been taken out of it, and a shortage is
    /// likewise weighed against the outer sizes (CSS Flexbox 1 §9.2.1 and §9.7.3). Sharing the container
    /// first and paying each item its own margins afterwards leaves one item wider than its neighbours
    /// for no reason the page gave.
    /// </summary>
    [Test]
    public async Task MarginsComeOffTheFreeSpaceBeforeItIsShared()
    {
        using var view = CreateView(400);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div style='display:flex;width:384px'>" +
            "<div id='a' style='flex:1 1 auto;height:20px'></div>" +
            "<div id='b' style='flex:1 1 auto;height:20px;margin-left:20px'></div>" +
            "<div id='c' style='flex:1 1 auto;height:20px;margin-left:20px'></div>" +
            "</div></body></html>");

        Assert.That(Find(view, "a").BorderBox.Width, Is.EqualTo(344.0 / 3).Within(0.5),
            "the three items share what the two margins leave, the first no wider than the rest");
        Assert.That(Find(view, "b").BorderBox.Width, Is.EqualTo(344.0 / 3).Within(0.5));
        Assert.That(Find(view, "c").BorderBox.Width, Is.EqualTo(344.0 / 3).Within(0.5));
    }

    [Test]
    public async Task AShortageIsWeighedAgainstTheOuterSizesToo()
    {
        using var view = CreateView(400);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div style='display:flex;width:200px'>" +
            "<div id='a' style='flex:1 1 auto;width:100px;height:20px'></div>" +
            "<div id='b' style='flex:1 1 auto;width:100px;height:20px;margin-left:20px'></div>" +
            "<div id='c' style='flex:1 1 auto;width:100px;height:20px;margin-left:20px'></div>" +
            "</div></body></html>");

        Assert.That(Find(view, "a").BorderBox.Width, Is.EqualTo(100 - 140.0 / 3).Within(0.5),
            "the deficit is the container short of the three bases and the two margins");
        Assert.That(Find(view, "b").BorderBox.Width, Is.EqualTo(100 - 140.0 / 3).Within(0.5));
        Assert.That(Find(view, "c").BorderBox.Width, Is.EqualTo(100 - 140.0 / 3).Within(0.5));
    }

    /// <summary>
    /// A <c>flex</c> shorthand written as numbers and left without its third value sets the basis to
    /// zero, not to <c>auto</c> (CSS Flexbox 1 §7.1.1) - so two items share the container equally even
    /// when one of them holds content and the other does not, which is what
    /// <c>flex: 1 1 auto</c> is for.
    /// </summary>
    [Test]
    public async Task AOmittedBasisInTheShorthandStandsAtZero()
    {
        using var view = CreateView(400);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div style='display:flex;width:200px'>" +
            "<div id='a' style='flex:1 0'><div style='width:10px;height:100px'></div></div>" +
            "<div id='b' style='flex:1 0;height:100px'></div>" +
            "</div></body></html>");

        Assert.That(Find(view, "a").BorderBox.Width, Is.EqualTo(100).Within(0.5),
            "the item with content takes no more than its neighbour, because its basis is zero");
        Assert.That(Find(view, "b").BorderBox.Width, Is.EqualTo(100).Within(0.5));
    }

    [Test]
    public async Task GrowSharesTheFreeSpaceBetweenTheItems()
    {
        using var view = CreateView(400);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div style='display:flex;width:300px'>" +
            "<div id='a' style='width:100px;flex-grow:1'></div>" +
            "<div id='b' style='width:100px;flex-grow:1'></div>" +
            "</div>" +
            "</body></html>");

        Assert.That(Find(view, "a").BorderBox.Width, Is.EqualTo(150).Within(0.5));
        Assert.That(Find(view, "b").BorderBox.Width, Is.EqualTo(150).Within(0.5));
    }

    [Test]
    public async Task ShrinkTakesTheDeficitFromTheBaseSize()
    {
        using var view = CreateView(400);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div style='display:flex;width:200px'>" +
            "<div id='a' style='width:200px'></div>" +
            "<div id='b' style='width:200px'></div>" +
            "</div>" +
            "</body></html>");

        Assert.That(Find(view, "a").BorderBox.Width, Is.EqualTo(100).Within(0.5));
        Assert.That(Find(view, "b").BorderBox.Width, Is.EqualTo(100).Within(0.5));
    }

    [Test]
    public async Task AnOverflowHiddenFlexItemShrinksBelowItsWideContent()
    {
        // CSS Flexbox 4.5: a scroll-axis item's automatic minimum size is zero, so it gives up its
        // 400-pixel child and shares the line. An identical `overflow: visible` item would keep
        // min-width:auto at its content and refuse to shrink - the whole reason a `flex-grow` column
        // with a wide (scrolling) child can otherwise balloon the page.
        using var view = CreateView(400);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div style='display:flex;width:200px'>" +
            "<div id='a' style='flex:1;overflow:hidden'><div style='width:400px;height:10px'></div></div>" +
            "<div id='b' style='flex:1;overflow:hidden'><div style='width:400px;height:10px'></div></div>" +
            "</div></body></html>");

        Assert.That(Find(view, "a").BorderBox.Width, Is.EqualTo(100).Within(0.5));
        Assert.That(Find(view, "b").BorderBox.Width, Is.EqualTo(100).Within(0.5));
    }

    [Test]
    public async Task AnOverflowClipFlexItemKeepsItsContentMinimum()
    {
        // `clip` is not a scroll container: for min-size it behaves like `visible`, so the item keeps
        // its content floor and the two 400-pixel items do not shrink to the 200-pixel line.
        using var view = CreateView(400);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div style='display:flex;width:200px'>" +
            "<div id='a' style='flex:1;overflow:clip'><div style='width:400px;height:10px'></div></div>" +
            "<div id='b' style='flex:1;overflow:clip'><div style='width:400px;height:10px'></div></div>" +
            "</div></body></html>");

        Assert.That(Find(view, "a").BorderBox.Width, Is.EqualTo(400).Within(0.5));
        Assert.That(Find(view, "b").BorderBox.Width, Is.EqualTo(400).Within(0.5));
    }

    [Test]
    public async Task PercentageBasisResolvesAgainstTheContainer()
    {
        using var view = CreateView(400);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div style='display:flex;width:200px'>" +
            "<div id='a' style='flex-basis:50%'></div>" +
            "</div>" +
            "</body></html>");

        Assert.That(Find(view, "a").BorderBox.Width, Is.EqualTo(100).Within(0.5));
    }

    [Test]
    public async Task JustifyContentCentresTheRow()
    {
        using var view = CreateView(400);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div style='display:flex;width:300px;justify-content:center'>" +
            "<div id='a' style='width:100px'></div>" +
            "</div>" +
            "</body></html>");

        Assert.That(Find(view, "a").BorderBox.X, Is.EqualTo(100).Within(0.5), "(300 - 100) / 2");
    }

    [Test]
    public async Task JustifyContentSpaceBetweenPushesTheEndsApart()
    {
        using var view = CreateView(400);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div style='display:flex;width:400px;justify-content:space-between'>" +
            "<div id='a' style='width:100px'></div>" +
            "<div id='b' style='width:100px'></div>" +
            "<div id='c' style='width:100px'></div>" +
            "</div>" +
            "</body></html>");

        Assert.That(Find(view, "a").BorderBox.X, Is.EqualTo(0).Within(0.5));
        Assert.That(Find(view, "b").BorderBox.X, Is.EqualTo(150).Within(0.5));
        Assert.That(Find(view, "c").BorderBox.X, Is.EqualTo(300).Within(0.5));
    }

    [Test]
    public async Task RowReversePlacesTheFirstItemAtTheEnd()
    {
        using var view = CreateView(400);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div style='display:flex;flex-direction:row-reverse;width:300px'>" +
            "<div id='a' style='width:100px'></div>" +
            "<div id='b' style='width:100px'></div>" +
            "</div>" +
            "</body></html>");

        Assert.That(Find(view, "a").BorderBox.X, Is.EqualTo(200).Within(0.5));
        Assert.That(Find(view, "b").BorderBox.X, Is.EqualTo(100).Within(0.5));
    }

    [Test]
    public async Task AColumnStacksTheItemsVertically()
    {
        using var view = CreateView(400);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div style='display:flex;flex-direction:column;width:100px'>" +
            "<div id='a' style='height:30px'></div>" +
            "<div id='b' style='height:30px'></div>" +
            "</div>" +
            "</body></html>");

        Assert.That(Find(view, "a").BorderBox.Y, Is.EqualTo(0).Within(0.5));
        Assert.That(Find(view, "b").BorderBox.Y, Is.EqualTo(30).Within(0.5));
    }

    [Test]
    public async Task AlignmentStretchesTheItemAcrossTheLine()
    {
        using var view = CreateView(400);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div style='display:flex;width:300px;height:80px'>" +
            "<div id='a' style='width:100px'></div>" +
            "</div>" +
            "</body></html>");

        Assert.That(Find(view, "a").BorderBox.Height, Is.EqualTo(80).Within(0.5), "align-items defaults to stretch");
    }

    [Test]
    public async Task AlignItemsCentresOnTheCrossAxis()
    {
        using var view = CreateView(400);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div style='display:flex;width:300px;height:100px;align-items:center'>" +
            "<div id='a' style='width:100px;height:20px'></div>" +
            "</div>" +
            "</body></html>");

        Assert.That(Find(view, "a").BorderBox.Y, Is.EqualTo(40).Within(0.5), "(100 - 20) / 2");
    }

    [Test]
    public async Task WrapBreaksTheItemsOntoASecondLine()
    {
        using var view = CreateView(400);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div style='display:flex;flex-wrap:wrap;width:250px'>" +
            "<div id='a' style='width:100px;height:50px'></div>" +
            "<div id='b' style='width:100px;height:50px'></div>" +
            "<div id='c' style='width:100px;height:50px'></div>" +
            "</div>" +
            "</body></html>");

        Assert.That(Find(view, "a").BorderBox.X, Is.EqualTo(0).Within(0.5));
        Assert.That(Find(view, "b").BorderBox.X, Is.EqualTo(100).Within(0.5));
        Assert.That(Find(view, "c").BorderBox.Y, Is.EqualTo(50).Within(0.5), "the third item starts the second line");
    }

    [Test]
    public async Task GapSeparatesTheItemsWithoutMargins()
    {
        using var view = CreateView(400);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div style='display:flex;width:300px;gap:20px'>" +
            "<div id='a' style='width:100px'></div>" +
            "<div id='b' style='width:100px'></div>" +
            "</div>" +
            "</body></html>");

        Assert.That(Find(view, "b").BorderBox.X, Is.EqualTo(120).Within(0.5));
    }

    [Test]
    public async Task OrderMovesAnItemToTheFront()
    {
        using var view = CreateView(400);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div style='display:flex;width:300px'>" +
            "<div id='a' style='width:100px'></div>" +
            "<div id='b' style='width:100px;order:-1'></div>" +
            "</div>" +
            "</body></html>");

        Assert.That(Find(view, "b").BorderBox.X, Is.EqualTo(0).Within(0.5));
        Assert.That(Find(view, "a").BorderBox.X, Is.EqualTo(100).Within(0.5));
    }

    [Test]
    public async Task AutoMarginsCentreALoneItem()
    {
        using var view = CreateView(400);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div style='display:flex;width:300px'>" +
            "<div id='a' style='width:100px;margin:auto'></div>" +
            "</div>" +
            "</body></html>");

        Assert.That(Find(view, "a").BorderBox.X, Is.EqualTo(100).Within(0.5), "(300 - 100) / 2");
    }

    [Test]
    public async Task AnItemWithAnAutomaticSizeIsMeasuredByItsContent()
    {
        using var view = CreateView(400);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div style='display:flex;width:300px'>" +
            "<div id='a'>aaaaaaaaaaaa</div>" +
            "<div id='b'>bbbb</div>" +
            "</div>" +
            "</body></html>");

        // The fake measurer gives five pixels a character, so the content asks for 60 and 20 of the
        // 300 - and an item that took the container's width instead would push its neighbour off the
        // page, which is what a header made of two such items comes to.
        Assert.That(Find(view, "a").BorderBox.Width, Is.EqualTo(60).Within(0.5), "flex-basis auto is the content size");
        Assert.That(Find(view, "b").BorderBox.X, Is.EqualTo(60).Within(0.5));
        Assert.That(Find(view, "b").BorderBox.Width, Is.EqualTo(20).Within(0.5));
    }

    [Test]
    public async Task AnItemHoldingAPercentageWidthIsStillMeasuredByItsContent()
    {
        using var view = CreateView(400);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div style='display:flex;justify-content:space-between;width:300px'>" +
            "<div id='a' style='display:flex'><div style='width:100%'>aaaaaaaa</div></div>" +
            "<div id='b' style='display:flex'><div style='width:100%'>bbbb</div></div>" +
            "</div>" +
            "</body></html>");

        // A site's menu bar is made of exactly this: an item whose every box inside declares
        // `width: 100%`. While the item is being asked what it needs, the block that percentage speaks
        // of is the very answer, so it has to fall back to the content - otherwise each item asks for
        // the whole row, the row cannot shrink them below what they asked for, and the second is placed
        // off the edge of the page.
        Assert.That(Find(view, "a").BorderBox.Width, Is.EqualTo(40).Within(0.5), "eight characters, not the row");
        Assert.That(Find(view, "b").BorderBox.Width, Is.EqualTo(20).Within(0.5));
        Assert.That(Find(view, "b").BorderBox.Right, Is.EqualTo(300).Within(0.5), "and space-between still puts the second at the end");
    }

    [Test]
    public async Task AnInlineChildOfAFlexContainerIsAnItemOfItsOwn()
    {
        using var view = CreateView(400);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div style='display:flex;width:300px'>" +
            "<span id='a' style='width:100px;height:20px'>one</span>" +
            "<a id='b' href='#' style='width:80px;height:20px'>two</a>" +
            "</div>" +
            "</body></html>");

        // Gathered into one anonymous block instead, they would be runs of that block: its style rather
        // than theirs, and an inline box takes no width - which is how a card's title or a nav row of
        // links loses every size the page gave it. As items they are measured, placed and sized.
        Assert.Multiple(() =>
        {
            Assert.That(Find(view, "a").BorderBox.Width, Is.EqualTo(100).Within(0.5), "the span's own width");
            Assert.That(Find(view, "b").BorderBox.X, Is.EqualTo(100).Within(0.5), "beside it, not inside it");
            Assert.That(Find(view, "b").BorderBox.Width, Is.EqualTo(80).Within(0.5));
        });
    }

    [Test]
    public async Task ATwoValueFlexShorthandStillGrowsTheItem()
    {
        // `flex: 1 1` names two of the three longhands and leaves the basis to default to `auto`, and a
        // shorthand is the place where a rebuild of longhands back into a value can go lossy - which
        // would leave the item asking for nothing and the row holding room it never handed over.
        using var view = CreateView(400);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div style='display:flex;width:300px'>" +
            "<div id='a' style='flex:1 1'>aaaaaaaaaaaa</div>" +
            "<div id='b' style='width:60px'></div>" +
            "</div>" +
            "</body></html>");

        Assert.Multiple(() =>
        {
            Assert.That(
                Find(view, "a").BorderBox.Width,
                Is.EqualTo(240).Within(0.5),
                "its own sixty of content and the hundred and eighty the row had left");
            Assert.That(Find(view, "b").BorderBox.X, Is.EqualTo(240).Within(0.5));
        });
    }

    [Test]
    public async Task ItemsThatNameNoOrderKeepTheOrderTheDocumentDid()
    {
        // Twenty items, each one character wider than the last, none declaring `order`: a sort that is
        // not stable permutes them, and the row comes out in an order nobody wrote. The count matters -
        // the sort switches from its insertion path to its partitioning one above sixteen, which is
        // where equal keys start trading places.
        var made = String.Concat(Enumerable.Range(0, 20).Select(index =>
            $"<div id='i{index}' style='width:{10 + index}px'></div>"));

        using var view = CreateView(600);
        await view.SetHtml(
            $"<html><body style='margin:0'><div style='display:flex;width:600px'>{made}</div></body></html>");

        var x = 0.0;

        for (var index = 0; index < 20; index++)
        {
            var box = Find(view, $"i{index}").BorderBox;

            Assert.That(box.X, Is.EqualTo(x).Within(0.5), $"item {index} follows item {index - 1}");
            Assert.That(box.Width, Is.EqualTo(10 + index).Within(0.5));
            x += 10 + index;
        }
    }

    [Test]
    public async Task PaddingAndBorderAreExcludedFromTheContentSize()
    {
        using var view = CreateView(400);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div style='display:flex;width:300px;height:60px;padding:10px;border:5px solid #000'>" +
            "<div id='a' style='width:100px'></div>" +
            "</div>" +
            "</body></html>");

        var item = Find(view, "a");

        Assert.That(item.BorderBox.X, Is.EqualTo(15).Within(0.5), "the container's border and padding come first");
        Assert.That(item.BorderBox.Height, Is.EqualTo(60).Within(0.5), "the line is the container's content height");
    }

    /// <summary>
    /// A size-contained item is not measured from its (empty) content: its base comes from
    /// `contain-intrinsic-size`, in a row from the width and in a column from the height.
    /// </summary>
    [Test]
    public async Task AContainedItemTakesItsBaseFromTheIntrinsicSize()
    {
        var view = CreateView(600);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div id='row' style='display:flex;width:200px;height:50px'>" +
            "<div id='a' style='contain:size;contain-intrinsic-size:55px 11px'></div>" +
            "<div id='b' style='contain:size;contain-intrinsic-size:66px 22px'></div></div>" +
            "<div id='column' style='display:flex;flex-direction:column;width:50px;height:200px'>" +
            "<div id='c' style='contain:size;contain-intrinsic-size:55px 11px'></div>" +
            "<div id='d' style='contain:size;contain-intrinsic-size:66px 22px'></div></div>" +
            "</body></html>");

        Assert.Multiple(() =>
        {
            Assert.That(Find(view, "a").BorderBox.Width, Is.EqualTo(55).Within(0.5), "the row's base");
            Assert.That(Find(view, "b").BorderBox.Width, Is.EqualTo(66).Within(0.5));
            Assert.That(Find(view, "c").BorderBox.Height, Is.EqualTo(11).Within(0.5), "the column's base");
            Assert.That(Find(view, "d").BorderBox.Height, Is.EqualTo(22).Within(0.5));
        });
    }

    private static HutuViewInternal CreateView(int viewportWidth)
    {
        var view = new HutuViewInternal(new FakeTextMeasurer());
        view.SetViewportSize(viewportWidth, 600);
        return view;
    }

    private static LayoutBox Find(HutuViewInternal view, String id)
    {
        var root = view.Layout?.Root;

        Assert.That(root, Is.Not.Null, "the document produced no layout");

        foreach (var node in Walk(root!))
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

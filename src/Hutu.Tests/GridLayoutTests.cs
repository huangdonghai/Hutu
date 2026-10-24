using AngleSharp.Css.Dom;
using AngleSharp.Css.Parser;
using Hutu.Rendering.Layout;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Hutu.Tests;

/// <summary>
/// The grid formatting context: track sizing, the automatic placement, spans, implicit tracks, gaps
/// and both levels of alignment.
/// </summary>
/// <remarks>
/// The documents go through the real cascade, so <c>grid-template-columns</c> and friends are read the
/// way a page would present them - including the structured <c>repeat()</c> and <c>minmax()</c> forms.
/// The font is a fixed-width stand-in, so a character is five pixels wide and a line is ten pixels
/// tall, which is what makes the expected numbers exact.
/// </remarks>
[TestFixture]
public sealed class GridLayoutTests
{
    [Test]
    public async Task EqualFractionsSplitTheWidthEvenly()
    {
        using var view = CreateView(400);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div style='display:grid;grid-template-columns:repeat(3,minmax(0,1fr));width:300px'>" +
            "<div id='a'>A</div><div id='b'>B</div><div id='c'>C</div>" +
            "</div>" +
            "</body></html>");

        var a = Find(view, "a");
        var b = Find(view, "b");
        var c = Find(view, "c");

        Assert.That(a.BorderBox.X, Is.EqualTo(0).Within(0.5));
        Assert.That(a.BorderBox.Width, Is.EqualTo(100).Within(0.5));
        Assert.That(b.BorderBox.X, Is.EqualTo(100).Within(0.5));
        Assert.That(c.BorderBox.X, Is.EqualTo(200).Within(0.5));
        Assert.That(c.BorderBox.Right, Is.EqualTo(300).Within(0.5), "the tracks fill the container");
    }

    [Test]
    public async Task AGridOfFlexibleTracksShrinksToItsContainerInsideAFlexItem()
    {
        // A grid's min-content is the sum of its tracks' minima, and a `minmax(0, 1fr)` track's minimum
        // is zero - so such a grid has essentially no min-content, and a `flex-grow` item holding it (the
        // item's automatic minimum size is that min-content) shrinks to the container rather than being
        // held open by the words inside. A Tailwind `grid-cols-*` bar over a `flex-grow` box leans on
        // exactly this; reporting max-content as min-content made the item refuse to shrink and let a
        // real page balloon past the viewport.
        using var view = CreateView(200);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div style='display:flex;width:200px'>" +
            "<div id='item' style='flex-grow:1'>" +
            "<div id='g' style='display:grid;grid-template-columns:repeat(2,minmax(0,1fr))'>" +
            "<div>aaaaaaaaaaaaaaaaaaaaaaaaaaaaaa</div>" +
            "<div>bbbbbbbbbbbbbbbbbbbbbbbbbbbbbb</div>" +
            "</div>" +
            "</div>" +
            "</div>" +
            "</body></html>");

        // Thirty characters are 150 pixels each, so the grid's max-content is 300 - wider than the 200
        // container - yet the flexible tracks let it come down to the container all the same.
        Assert.That(
            Find(view, "g").BorderBox.Width,
            Is.EqualTo(200).Within(0.5),
            "the grid shrinks to the container, not to what it holds");
    }

    [Test]
    public async Task ASpanningItemDoesNotBlowOutFlexibleTracks()
    {
        // The same rule for an item that spans several tracks: the extra its min-content needs is shared
        // only among tracks with an intrinsic minimum (CSS Grid 12.6). A `minmax(0, 1fr)` track has a
        // definite minimum of zero, so a wide spanning item must not raise it - otherwise the spanning
        // item's 300-pixel word blows the two flexible tracks out to 300 and the page overflows.
        using var view = CreateView(200);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div style='display:flex;width:200px'>" +
            "<div style='flex-grow:1'>" +
            "<div id='g' style='display:grid;grid-template-columns:repeat(2,minmax(0,1fr))'>" +
            "<div style='grid-column:1/span 2'>aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa</div>" +
            "</div></div></div></body></html>");

        Assert.That(
            Find(view, "g").BorderBox.Width,
            Is.EqualTo(200).Within(0.5),
            "a spanning item's min-content does not blow out definite-minimum tracks");
    }

    [Test]
    public async Task ASpanAcrossAFlexibleRowLeavesTheContentRowsAtNothing()
    {
        // CSS Grid 1 §11.5 considers "the items with a span of 2 that do not span a track with a
        // flexible sizing function" for the intrinsic track sizes, and leaves the rest to the step
        // after: "distributing space only to flexible tracks (i.e. treating all other tracks as having
        // a fixed sizing function of their current base size)". Sharing the spanning item's demand
        // with the content-sized rows it also covers stands those rows open on a definite-height grid
        // - and a band of white space with them. This is the shape of the Wikipedia page body grid,
        // where that band stood above the page's tabs.
        using var view = CreateView(400);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div id='g' style='display:grid;height:300px;grid-template-columns:1fr 100px;" +
            "grid-template-rows:min-content min-content min-content 1fr'>" +
            "<div id='a' style='grid-area:1/1'></div>" +
            "<div id='b' style='grid-area:2/1;height:32px'></div>" +
            "<div id='tall' style='grid-area:2/2/5/3;height:61.8px'></div>" +
            "<div id='content' style='grid-area:4/1;height:200px'></div>" +
            "</div></body></html>");

        Assert.That(Find(view, "a").BorderBox.Height, Is.EqualTo(0).Within(0.5), "a min-content row holding nothing");
        Assert.That(Find(view, "a").BorderBox.Y, Is.EqualTo(0).Within(0.5), "no room opened above it");
        Assert.That(Find(view, "b").BorderBox.Height, Is.EqualTo(32).Within(0.5), "a min-content row holding its own item");
        Assert.That(Find(view, "g").BorderBox.Height, Is.EqualTo(300).Within(0.5));
    }

    [Test]
    public async Task ASpanAcrossAFlexibleRowGivesItsDemandToTheFlexibleRow()
    {
        // The same clause from the other side: where the spanning item's demand has somewhere to go,
        // it is the flexible track that takes it, "according to the ratios of their flexible sizing
        // functions", and the content-sized track it also covers is left where it is.
        using var view = CreateView(400);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div id='g' style='display:grid;height:200px;grid-template-columns:1fr 1fr;" +
            "grid-template-rows:auto 1fr'>" +
            "<div id='tall' style='grid-area:1/1/3/2;height:120px'></div>" +
            "<div id='first' style='grid-area:1/2'></div>" +
            "<div id='second' style='grid-area:2/2'></div>" +
            "</div></body></html>");

        Assert.That(Find(view, "first").BorderBox.Height, Is.EqualTo(0).Within(0.5), "the content-sized row is not opened");
        Assert.That(Find(view, "second").BorderBox.Height, Is.EqualTo(200).Within(0.5), "the flexible row holds the demand and the rest");
    }

    [Test]
    public async Task ASpanAcrossAFlexibleColumnLeavesTheContentColumnsAtNothing()
    {
        // The same rule on the inline axis: the content-sized columns beside a 1fr column keep the
        // size of what stands in them.
        using var view = CreateView(400);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div id='g' style='display:grid;grid-template-columns:min-content min-content 1fr'>" +
            "<div id='long' style='grid-area:1/1/2/4'>" + new String('a', 40) + "</div>" +
            "<div id='a' style='grid-area:2/1'>A</div>" +
            "</div></body></html>");

        Assert.That(Find(view, "a").BorderBox.Width, Is.EqualTo(5).Within(0.5), "the column keeps its own item's width");
    }

    [Test]
    public async Task GapIsLeftBetweenTheTracks()
    {
        using var view = CreateView(400);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div style='display:grid;grid-template-columns:repeat(3,minmax(0,1fr));gap:10px;width:320px'>" +
            "<div id='a'>A</div><div id='b'>B</div><div id='c'>C</div>" +
            "</div>" +
            "</body></html>");

        var a = Find(view, "a");
        var b = Find(view, "b");

        // Two gaps come off the width before the three tracks share it.
        Assert.That(a.BorderBox.Width, Is.EqualTo(100).Within(0.5));
        Assert.That(b.BorderBox.X - a.BorderBox.Right, Is.EqualTo(10).Within(0.5));
    }

    [Test]
    public async Task AFixedTrackKeepsItsWidthAndTheFractionTakesTheRest()
    {
        using var view = CreateView(400);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div style='display:grid;grid-template-columns:240px minmax(0,1fr);width:800px'>" +
            "<div id='a'>A</div><div id='b'>B</div>" +
            "</div>" +
            "</body></html>");

        Assert.That(Find(view, "a").BorderBox.Width, Is.EqualTo(240).Within(0.5));
        Assert.That(Find(view, "b").BorderBox.X, Is.EqualTo(240).Within(0.5));
        Assert.That(Find(view, "b").BorderBox.Width, Is.EqualTo(560).Within(0.5));
    }

    [Test]
    public async Task PercentTracksResolveAgainstTheContainer()
    {
        using var view = CreateView(400);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div style='display:grid;grid-template-columns:25% 75%;width:400px'>" +
            "<div id='a'>A</div><div id='b'>B</div>" +
            "</div>" +
            "</body></html>");

        Assert.That(Find(view, "a").BorderBox.Width, Is.EqualTo(100).Within(0.5));
        Assert.That(Find(view, "b").BorderBox.Width, Is.EqualTo(300).Within(0.5));
    }

    [Test]
    public async Task ContentTracksSizeToTheirItems()
    {
        using var view = CreateView(400);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div style='display:grid;grid-template-columns:auto auto;justify-content:flex-start'>" +
            "<div id='a'>A</div><div id='b'>A longer cell</div>" +
            "</div>" +
            "</body></html>");

        Assert.That(Find(view, "a").BorderBox.Width, Is.EqualTo(5).Within(0.5), "one character");
        Assert.That(Find(view, "b").BorderBox.Width, Is.EqualTo(65).Within(0.5), "thirteen characters");
        Assert.That(Find(view, "b").BorderBox.X, Is.EqualTo(5).Within(0.5));
    }

    [Test]
    public async Task MinMaxRaisesATrackToItsMinimum()
    {
        using var view = CreateView(400);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div style='display:grid;grid-template-columns:minmax(120px,1fr) minmax(120px,1fr);width:400px'>" +
            "<div id='a'>A</div><div id='b'>B</div>" +
            "</div>" +
            "</body></html>");

        Assert.That(Find(view, "a").BorderBox.Width, Is.EqualTo(200).Within(0.5), "the minimum is a floor, not a cap");
    }

    [Test]
    public async Task AutoFitCollapsesTheTracksNoItemReaches()
    {
        using var view = CreateView(400);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div style='display:grid;grid-template-columns:repeat(auto-fit,minmax(125px,1fr));gap:12px;width:400px'>" +
            "<div id='a'>A</div><div id='b'>B</div>" +
            "</div>" +
            "</body></html>");

        // Three tracks of 125 would fit, but only two have an item, so the two that remain share the
        // whole width - which is the difference between auto-fit and auto-fill.
        Assert.That(Find(view, "a").BorderBox.Width, Is.EqualTo(194).Within(0.5));
        Assert.That(Find(view, "b").BorderBox.X, Is.EqualTo(206).Within(0.5));
    }

    [Test]
    public async Task AutoFillKeepsTheTracksNoItemReaches()
    {
        using var view = CreateView(400);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div style='display:grid;grid-template-columns:repeat(auto-fill,minmax(125px,1fr));gap:12px;width:400px'>" +
            "<div id='a'>A</div><div id='b'>B</div>" +
            "</div>" +
            "</body></html>");

        Assert.That(Find(view, "a").BorderBox.Width, Is.EqualTo(125.33).Within(0.5));
        Assert.That(Find(view, "b").BorderBox.Right, Is.LessThan(400), "the third track stays empty");
    }

    [Test]
    public async Task ASpanningItemPushesTheFollowingItemsToTheNextRow()
    {
        using var view = CreateView(400);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div style='display:grid;grid-template-columns:repeat(3,minmax(0,1fr));width:300px'>" +
            "<div id='a' style='grid-column:span 2'>A</div>" +
            "<div id='b'>B</div><div id='c'>C</div><div id='d'>D</div><div id='e'>E</div>" +
            "</div>" +
            "</body></html>");

        Assert.That(Find(view, "a").BorderBox.Width, Is.EqualTo(200).Within(0.5), "two tracks");
        Assert.That(Find(view, "b").BorderBox.X, Is.EqualTo(200).Within(0.5), "the next item takes the third track");
        Assert.That(Find(view, "b").BorderBox.Y, Is.EqualTo(Find(view, "a").BorderBox.Y).Within(0.5));
        Assert.That(Find(view, "c").BorderBox.Y, Is.GreaterThan(Find(view, "a").BorderBox.Y), "the row wrapped");
        Assert.That(Find(view, "c").BorderBox.X, Is.EqualTo(0).Within(0.5));
        Assert.That(Find(view, "e").BorderBox.X, Is.EqualTo(200).Within(0.5));
    }

    [Test]
    public async Task AnExplicitPlacementPutsAnItemWhereItSays()
    {
        using var view = CreateView(400);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div style='display:grid;grid-template-columns:repeat(3,minmax(0,1fr));width:300px'>" +
            "<div id='a' style='grid-column:2'>A</div>" +
            "<div id='b'>B</div>" +
            "</div>" +
            "</body></html>");

        var a = Find(view, "a");
        var b = Find(view, "b");

        Assert.That(a.BorderBox.X, Is.EqualTo(100).Within(0.5), "the second column");
        Assert.That(a.BorderBox.Y, Is.EqualTo(0).Within(0.5), "the cursor was still on the first row");
        // The cursor advances past the explicitly placed item, so the automatic one continues the row
        // instead of back-filling the hole the explicit placement left behind.
        Assert.That(b.BorderBox.X, Is.EqualTo(200).Within(0.5));
        Assert.That(b.BorderBox.Y, Is.EqualTo(a.BorderBox.Y).Within(0.5));
    }

    [Test]
    public async Task AnItemPastTheExplicitGridCreatesAnImplicitTrack()
    {
        using var view = CreateView(400);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div style='display:grid;grid-template-columns:100px;grid-auto-columns:50px;width:400px;justify-content:flex-start'>" +
            "<div id='a'>A</div><div id='b'>B</div><div id='c' style='grid-column:3'>C</div>" +
            "</div>" +
            "</body></html>");

        Assert.That(Find(view, "a").BorderBox.Width, Is.EqualTo(100).Within(0.5));
        Assert.That(Find(view, "c").BorderBox.X, Is.EqualTo(150).Within(0.5), "an implicit track follows the explicit one");
        Assert.That(Find(view, "c").BorderBox.Width, Is.EqualTo(50).Within(0.5));
    }

    [Test]
    public async Task ARowIsAsTallAsItsTallestItem()
    {
        using var view = CreateView(400);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div style='display:grid;grid-template-columns:repeat(2,minmax(0,1fr));width:200px'>" +
            "<div id='a' style='height:40px'>A</div><div id='b' style='height:20px'>B</div>" +
            "<div id='c'>C</div>" +
            "</div>" +
            "</body></html>");

        Assert.That(Find(view, "a").BorderBox.Height, Is.EqualTo(40).Within(0.5));
        Assert.That(Find(view, "b").BorderBox.Height, Is.EqualTo(20).Within(0.5), "a shorter item keeps its own height");
        Assert.That(Find(view, "c").BorderBox.Y, Is.EqualTo(40).Within(0.5), "the second row starts below the first");
    }

    [Test]
    public async Task AStretchedItemFillsItsArea()
    {
        using var view = CreateView(400);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div style='display:grid;grid-template-columns:repeat(2,minmax(0,1fr));width:200px'>" +
            "<div id='a' style='height:40px'>A</div><div id='b'>B</div>" +
            "</div>" +
            "</body></html>");

        Assert.That(Find(view, "b").BorderBox.Height, Is.EqualTo(40).Within(0.5), "stretch is the default");
    }

    [Test]
    public async Task PlaceItemsCentresAnItemInItsArea()
    {
        using var view = CreateView(400);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div style='display:grid;place-items:center;width:100px'>" +
            "<div id='a' style='width:20px;height:10px'>A</div>" +
            "</div>" +
            "</body></html>");

        Assert.That(Find(view, "a").BorderBox.X, Is.EqualTo(40).Within(0.5), "(100 - 20) / 2");
        Assert.That(Find(view, "a").BorderBox.Y, Is.EqualTo(0).Within(0.5), "the row is only as tall as the item");
    }

    [Test]
    public async Task JustifyContentMovesTheWholeSetOfTracks()
    {
        using var view = CreateView(400);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div style='display:grid;grid-template-columns:50px 50px;justify-content:center;width:200px'>" +
            "<div id='a'>A</div><div id='b'>B</div>" +
            "</div>" +
            "</body></html>");

        Assert.That(Find(view, "a").BorderBox.X, Is.EqualTo(50).Within(0.5), "(200 - 100) / 2");
    }

    [Test]
    public async Task JustifySelfOverridesTheContainerAlignment()
    {
        using var view = CreateView(400);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div style='display:grid;grid-template-columns:100px;justify-items:center;width:100px'>" +
            "<div id='a' style='width:20px'>A</div>" +
            "<div id='b' style='width:20px;justify-self:flex-end'>B</div>" +
            "<div id='c' style='width:20px;justify-self:flex-start'>C</div>" +
            "</div>" +
            "</body></html>");

        Assert.That(Find(view, "a").BorderBox.X, Is.EqualTo(40).Within(0.5), "centred by the container");
        Assert.That(Find(view, "b").BorderBox.X, Is.EqualTo(80).Within(0.5), "pushed to the end");
        Assert.That(Find(view, "c").BorderBox.X, Is.EqualTo(0).Within(0.5), "back to the start");
    }

    [Test]
    public async Task AHeightOfAutoRowsIsPaidForByTheFractionRow()
    {
        using var view = CreateView(400);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div style='display:grid;grid-template-columns:100px;grid-template-rows:auto minmax(0,1fr);height:200px'>" +
            "<div id='a'>A</div><div id='b'>B</div>" +
            "</div>" +
            "</body></html>");

        Assert.That(Find(view, "a").BorderBox.Height, Is.EqualTo(10).Within(0.5), "one line of text");
        Assert.That(Find(view, "b").BorderBox.Y, Is.EqualTo(10).Within(0.5));
        Assert.That(Find(view, "b").BorderBox.Height, Is.EqualTo(190).Within(0.5), "the rest of the height");
    }

    [Test]
    public async Task AColumnOfGridsNestsWithoutLosingItsWidth()
    {
        using var view = CreateView(400);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div style='display:grid;grid-template-columns:repeat(2,minmax(0,1fr));gap:10px;width:210px'>" +
            "<div id='outer'>" +
            "<div style='display:grid;grid-template-columns:repeat(2,minmax(0,1fr));gap:10px'>" +
            "<div id='a'>A</div><div id='b'>B</div>" +
            "</div>" +
            "</div>" +
            "</div>" +
            "</body></html>");

        // The outer grid gives the nested one 100 pixels, and the nested grid shares them out again.
        Assert.That(Find(view, "outer").BorderBox.Width, Is.EqualTo(100).Within(0.5));
        Assert.That(Find(view, "a").BorderBox.Width, Is.EqualTo(45).Within(0.5));
        Assert.That(Find(view, "b").BorderBox.X, Is.EqualTo(55).Within(0.5));
    }

    [Test]
    public async Task AnInlineOnlyContainerHoldsOneItem()
    {
        using var view = CreateView(400);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div style='display:grid;place-items:center;width:100px;height:40px'>H</div>" +
            "</body></html>");

        // The bare word is wrapped into a single anonymous item, and the container's own declared size
        // must not leak into it - otherwise the text would be stretched across the whole grid.
        var line = FindLine(view, "H");

        Assert.That(line.BorderBox.X, Is.EqualTo(47.5).Within(0.5), "(100 - 5) / 2");
        Assert.That(line.BorderBox.Y, Is.EqualTo(15).Within(0.5), "(40 - 10) / 2");
    }

    /// <summary>
    /// A span written past any practical grid is clamped to the user-agent ceiling rather than built
    /// track by track - which is what keeps <c>grid-row-end: span 99999999</c> from spending the layout
    /// marking a hundred million cells.
    /// </summary>
    [Test]
    public async Task APathologicalSpanIsClampedNotMaterialized()
    {
        using var view = CreateView(400);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div style='display:grid;grid-auto-rows:1px'>" +
            "<div id='a' style='grid-row-end:span 99999999'>x</div>" +
            "</div></body></html>");

        Assert.That(view.Layout!.Height, Is.LessThan(1_000_000).Within(1),
            "the grid reaches its clamped ceiling of rows, not the literal hundred million");
    }

    /// <summary>
    /// A grid item's percentage width is resolved against the area the grid gives it, and never the other
    /// way round (CSS Grid 1 §6.6, §12.4): for the purpose of sizing a track an item whose size is a
    /// percentage contributes as `auto`, because a percentage of a track cannot be what decides the
    /// track's own width. Measuring it against the container instead is what made the reference pages of
    /// the gap-decoration corpus - which hand-draw their rules with `.item { width: 100% }` inside a
    /// `repeat(2, 1fr)` grid - lay two 110px columns into a 110px container.
    /// </summary>
    [Test]
    public async Task APercentWidthOnAnItemDoesNotSizeItsTrack()
    {
        using var view = CreateView(400);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div style='display:grid;grid-template-columns:repeat(2,1fr);column-gap:10px;row-gap:10px;" +
            "width:110px;height:110px'>" +
            "<div id='a' style='width:100%;height:50px'></div>" +
            "<div id='b' style='width:100%;height:50px'></div>" +
            "<div id='c' style='width:100%;height:50px'></div>" +
            "<div id='d' style='width:100%;height:50px'></div>" +
            "</div>" +
            "</body></html>");

        var a = Find(view, "a");
        var b = Find(view, "b");

        Assert.Multiple(() =>
        {
            Assert.That(a.BorderBox.Width, Is.EqualTo(50).Within(0.5), "half of what the gap leaves");
            Assert.That(b.BorderBox.X, Is.EqualTo(60).Within(0.5), "the second track starts past the gap");
            Assert.That(b.BorderBox.Width, Is.EqualTo(50).Within(0.5));
        });
    }

    /// <summary>
    /// The same rule down the block axis: a row's height is not what a `height: 100%` item inside it asks
    /// for, because the percentage is a share of that row (CSS Grid 1 §12.4).
    /// </summary>
    [Test]
    public async Task APercentHeightOnAnItemDoesNotSizeItsRow()
    {
        using var view = CreateView(400);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div style='display:grid;grid-template-rows:repeat(2,1fr);column-gap:10px;row-gap:10px;" +
            "width:110px;height:110px'>" +
            "<div id='a' style='height:100%'></div>" +
            "<div id='b' style='height:100%'></div>" +
            "</div>" +
            "</body></html>");

        var a = Find(view, "a");
        var b = Find(view, "b");

        Assert.Multiple(() =>
        {
            Assert.That(a.BorderBox.Height, Is.EqualTo(50).Within(0.5), "half of what the gap leaves");
            Assert.That(b.BorderBox.Y, Is.EqualTo(60).Within(0.5), "the second row starts past the gap");
            Assert.That(b.BorderBox.Height, Is.EqualTo(50).Within(0.5));
        });
    }

    [Test]
    public async Task AnAutoPlacedItemIsPackedInTheOrderItAsksFor()
    {
        using var view = CreateView(400);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div style='display:grid;grid-template-columns:50px 50px;grid-auto-rows:40px'>" +
            "<div id='a' style='order:2'></div>" +
            "<div id='b' style='order:1'></div>" +
            "</div></body></html>");

        // The second item asks to come first, so it takes the first cell and the other follows it - the
        // walk is in order-modified document order (CSS Grid 1 §8.5).
        Assert.Multiple(() =>
        {
            Assert.That(Find(view, "b").BorderBox.X, Is.EqualTo(0).Within(0.5), "the item that asked for order one");
            Assert.That(Find(view, "b").BorderBox.Y, Is.EqualTo(0).Within(0.5));
            Assert.That(Find(view, "a").BorderBox.X, Is.EqualTo(50).Within(0.5), "and the item after it");
            Assert.That(Find(view, "a").BorderBox.Y, Is.EqualTo(0).Within(0.5));
        });
    }

    [Test]
    public async Task AnItemWithARatioAndADefiniteRowSizesItsColumn()
    {
        using var view = CreateView(400);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div id='g' style='display:inline-grid;grid-template-rows:100px;background:green'>" +
            "<div id='a' style='aspect-ratio:1/1;height:100%'></div></div>" +
            "<div id='h' style='display:inline-grid;grid-template-rows:100px'>" +
            "<div id='b' style='aspect-ratio:2/1;height:50%'></div></div>" +
            "</body></html>");

        Assert.Multiple(() =>
        {
            // The row is settled at a hundred, the item is as tall as all of it, and the ratio gives the
            // column the same hundred (CSS Grid 1 §6.6, CSS Sizing 4 §4).
            Assert.That(Find(view, "g").BorderBox.Width, Is.EqualTo(100).Within(0.5));

            // Half the row, doubled: the height the item states is what the ratio is asked of.
            Assert.That(Find(view, "h").BorderBox.Width, Is.EqualTo(100).Within(0.5));
        });
    }

    private static HutuViewInternal CreateView(Int32 viewportWidth)
    {
        var view = new HutuViewInternal(new FakeTextMeasurer());
        view.SetViewportSize(viewportWidth, 600);
        return view;
    }

    /// <summary>
    /// A template area placed by name: the item says which cell it belongs to and the grid says where
    /// that cell is, on both axes and across the rows the area spans. This is the shape a real page's
    /// frame is written in, and the numbers are what a browser makes of the same document.
    /// </summary>
    [Test]
    public async Task ANamedAreaPlacesTheItemWhereTheTemplateSays()
    {
        using var view = CreateView(400);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div style='display:grid;width:300px;grid-template-columns:100px 100px;" +
            "grid-template-rows:50px 50px;grid-template-areas:&#39;head side&#39; &#39;body side&#39;'>" +
            "<div id='a' style='grid-area:side'>a</div>" +
            "<div id='b' style='grid-area:head'>b</div>" +
            "<div id='c' style='grid-area:body'>c</div>" +
            "</div>" +
            "</body></html>");

        var a = Find(view, "a");
        var b = Find(view, "b");
        var c = Find(view, "c");

        Assert.That(a.BorderBox.X, Is.EqualTo(100).Within(0.5), "the side cell is the second column");
        Assert.That(a.BorderBox.Y, Is.EqualTo(0).Within(0.5));
        Assert.That(a.BorderBox.Height, Is.EqualTo(100).Within(0.5), "and it reaches down both rows");
        Assert.That(b.BorderBox.X, Is.EqualTo(0).Within(0.5));
        Assert.That(b.BorderBox.Y, Is.EqualTo(0).Within(0.5));
        Assert.That(c.BorderBox.Y, Is.EqualTo(50).Within(0.5), "the body cell is below the head one");
    }

    /// <summary>
    /// A line named in the track list, which is the other thing an item's placement can point at: the
    /// names in front of the sizes are not tracks, so they are read from the text of the list.
    /// </summary>
    [Test]
    public async Task ANamedLinePlacesTheItemWhereTheTracksSay()
    {
        using var view = CreateView(400);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div style='display:grid;width:300px;" +
            "grid-template-columns:[first] 100px [second] 100px;grid-template-rows:50px 50px'>" +
            "<div id='a' style='grid-column:second;grid-row:2'>a</div>" +
            "<div id='b' style='grid-column:first/span 2'>b</div>" +
            "</div>" +
            "</body></html>");

        var a = Find(view, "a");
        var b = Find(view, "b");

        Assert.That(a.BorderBox.X, Is.EqualTo(100).Within(0.5), "the second named line");
        Assert.That(a.BorderBox.Y, Is.EqualTo(50).Within(0.5));
        Assert.That(b.BorderBox.X, Is.EqualTo(0).Within(0.5));
        Assert.That(b.BorderBox.Width, Is.EqualTo(200).Within(0.5), "and a span across both");
    }

    /// <summary>
    /// A template that names its cells without sizing them still says how many tracks there are: the
    /// grid gets one column per cell of a row and one row per string, each sized as <c>auto</c>.
    /// </summary>
    [Test]
    public async Task ATemplateOfAreasAloneSetsTheTrackCount()
    {
        using var view = CreateView(400);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div style='display:grid;width:300px;grid-template-areas:&#39;left right&#39; &#39;left right&#39;'>" +
            "<div id='a' style='grid-area:left'>a</div>" +
            "<div id='b' style='grid-area:right'>b</div>" +
            "</div>" +
            "</body></html>");

        var a = Find(view, "a");
        var b = Find(view, "b");

        Assert.That(a.BorderBox.X, Is.EqualTo(0).Within(0.5));
        Assert.That(b.BorderBox.X, Is.GreaterThan(a.BorderBox.Right - 0.5), "the second column starts after the first");
        Assert.That(b.BorderBox.Right, Is.EqualTo(300).Within(1), "and the two together fill the grid");

        // Both areas cover both rows, so both items reach the same depth however those rows were sized;
        // a grid that had not read the template would have laid them one under the other instead.
        Assert.That(a.BorderBox.Bottom, Is.EqualTo(b.BorderBox.Bottom).Within(0.5));
        Assert.That(a.BorderBox.Height, Is.GreaterThan(0));
    }

    /// <summary>
    /// A cell of the template can be left unnamed with a <c>.</c>, which claims the track without
    /// giving it a name anything could be placed by. The real pages that need this are the ones whose
    /// frame leaves a corner of the grid to whatever falls there.
    /// </summary>
    [Test]
    public async Task ACellOfATemplateCanBeLeftUnnamed()
    {
        using var view = CreateView(400);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div style='display:grid;width:300px;grid-template-areas:&#39;a .&#39; &#39;a .&#39;'>" +
            "<div id='a' style='grid-area:a'>a</div>" +
            "<div id='b'>b</div>" +
            "</div>" +
            "</body></html>");

        var a = Find(view, "a");

        Assert.That(a.BorderBox.X, Is.EqualTo(0).Within(0.5));
        Assert.That(a.BorderBox.Right, Is.LessThan(300).Within(1), "the unnamed cell still holds a column");
        Assert.That(a.BorderBox.Width, Is.GreaterThan(100), "and the named one keeps the rest it was sized to");
    }

    /// <summary>
    /// An area's <c>-end</c> line is named by cutting its four-character suffix, not the six that
    /// <c>-start</c> takes. Cut the wrong length and a short area name asks for a substring of negative
    /// length, while a longer one reads a stem that matches no area at all.
    /// </summary>
    [Test]
    public async Task AnAreasEndLineCutsItsOwnSuffixLength()
    {
        using var view = CreateView(400);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div style='display:grid;width:300px;grid-template-columns:100px 100px 100px;grid-template-rows:50px;" +
            "grid-template-areas:&#39;. a a&#39;'>" +
            "<div id='a' style='grid-column:a-start / a-end;grid-row:1'>a</div>" +
            "</div>" +
            "</body></html>");

        var a = Find(view, "a");

        // The area covers the second and third columns, so its start line is the second and its end the fourth.
        Assert.That(a.BorderBox.X, Is.EqualTo(100).Within(0.5), "the -start line begins the area");
        Assert.That(a.BorderBox.Right, Is.EqualTo(300).Within(0.5), "and the -end line closes it");
    }

    [Test]
    public async Task AnAbsolutePathChildStandsWhereItsPlacementNamesIt()
    {
        using var view = CreateView(400);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div style='display:grid;position:relative;width:300px;height:200px;padding:10px;" +
            "grid-template-columns:100px 100px;grid-template-rows:50px 50px'>" +
            "<div id='a' style='position:absolute;top:0;left:0;width:20px;height:20px;" +
            "grid-column:2;grid-row:2'>a</div>" +
            "</div>" +
            "</body></html>");

        var a = Find(view, "a");

        // The area the child names is what its offsets count from (CSS Grid 2 §10.1), so top and left of
        // zero put it on the second track's corners rather than on the container's own.
        Assert.That(a.BorderBox.X, Is.EqualTo(10 + 100).Within(0.5), "the column line the placement names");
        Assert.That(a.BorderBox.Y, Is.EqualTo(10 + 50).Within(0.5), "and the row line");
    }

    [Test]
    public async Task AnUnplacedAbsolutePathChildStandsInItsContainersContentBox()
    {
        using var view = CreateView(400);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div style='display:grid;position:relative;width:300px;height:200px;padding:10px;" +
            "grid-template-columns:100px 100px;grid-template-rows:50px 50px'>" +
            "<div id='a' style='position:absolute;width:20px;height:20px'>a</div>" +
            "</div>" +
            "</body></html>");

        var a = Find(view, "a");

        // Nothing placed it, so the area it stands in is the content box (CSS Grid 2 §10.2): the padding
        // edge is where its offsets would count from, and it has none.
        Assert.That(a.BorderBox.X, Is.EqualTo(10).Within(0.5), "the content edge is where it stands");
        Assert.That(a.BorderBox.Y, Is.EqualTo(10).Within(0.5), "on both axes");
    }

    [Test]
    public async Task AFixedChildOfAGridIsNotOffsetFromTheAreaItNames()
    {
        using var view = CreateView(400);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div style='display:grid;position:relative;width:300px;height:200px;padding:10px;" +
            "grid-template-columns:100px 100px;grid-template-rows:50px 50px'>" +
            "<div id='a' style='position:fixed;top:0;left:0;width:20px;height:20px;" +
            "grid-column:2;grid-row:2'>a</div>" +
            "</div>" +
            "</body></html>");

        var a = Find(view, "a");

        // A `fixed` box is offset from the viewport, so the grid lends it no containing block; where it
        // stands is the content box, which is the place a box with no area of its own would have.
        Assert.That(a.BorderBox.X, Is.EqualTo(0).Within(0.5), "the viewport is what top and left count from");
        Assert.That(a.BorderBox.Y, Is.EqualTo(0).Within(0.5), "on both axes");
    }

    [Test]
    public async Task AnAbsolutePathChildIsAlignedWithinTheAreaItStandsIn()
    {
        using var view = CreateView(400);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div style='display:grid;position:relative;width:300px;height:200px;padding:10px;" +
            "grid-template-columns:100px 100px;grid-template-rows:50px 50px'>" +
            "<div id='a' style='position:absolute;width:20px;height:20px;" +
            "grid-column:2/3;grid-row:2/3;justify-self:center;align-self:end'>a</div>" +
            "</div>" +
            "</body></html>");

        var a = Find(view, "a");

        // The area runs from 110 to 210 and from 60 to 110, and the child has no offsets to count from,
        // so its own alignment is what moves it along it (CSS Align 3 §5.2).
        Assert.That(a.BorderBox.X, Is.EqualTo(110 + (100 - 20) / 2).Within(0.5), "centred along the column");
        Assert.That(a.BorderBox.Y, Is.EqualTo(60 + (50 - 20)).Within(0.5), "and pushed to the row's end edge");
    }

    [Test]
    public async Task AnAbsolutePathChildTakesTheAlignmentItsContainerIsGiven()
    {
        using var view = CreateView(400);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div style='display:grid;position:relative;width:300px;height:200px;padding:10px;" +
            "align-items:center;grid-template-columns:100px 100px;grid-template-rows:50px 50px'>" +
            "<div id='a' style='position:absolute;width:20px;height:20px;grid-column:2/3;grid-row:2/3'>a</div>" +
            "</div>" +
            "</body></html>");

        var a = Find(view, "a");

        // `auto` is not the box's own answer: it takes what the container says for that axis, and the
        // normal that a grid item would stretch to stands an out-of-flow box on the area's start edge.
        Assert.That(a.BorderBox.Y, Is.EqualTo(60 + (50 - 20) / 2).Within(0.5), "centred by the container");
        Assert.That(a.BorderBox.X, Is.EqualTo(110).Within(0.5), "and left where the column line begins");
    }

    [TestCase("self-start", 110)]
    [TestCase("left", 110)]
    [TestCase("self-end", 190)]
    [TestCase("right", 190)]
    public async Task AnAbsolutePathChildStandsOnTheEdgeItsKeywordNames(String keyword, Double expected)
    {
        using var view = CreateView(400);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div style='display:grid;position:relative;width:300px;height:200px;padding:10px;" +
            "grid-template-columns:100px 100px;grid-template-rows:50px 50px'>" +
            $"<div id='a' style='position:absolute;width:20px;height:20px;" +
            $"grid-column:2/3;grid-row:2/3;justify-self:{keyword}'>a</div>" +
            "</div>" +
            "</body></html>");

        var a = Find(view, "a");

        // The area runs from 110 to 210 and the box is 20 wide, so the edge the keyword names is the edge
        // it stands against - and the two physical spellings name the same edges here because the axis
        // they run along is the one the page is written in.
        Assert.That(a.BorderBox.X, Is.EqualTo(expected).Within(0.5), $"'{keyword}' stands on that edge");
    }

    [Test]
    public async Task ANormalAlignmentOnAnAbsolutePathChildStandsAtTheAreaStartEdge()
    {
        using var view = CreateView(400);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div style='display:grid;position:relative;width:300px;height:200px;padding:10px;" +
            "align-items:center;grid-template-columns:100px 100px;grid-template-rows:50px 50px'>" +
            "<div id='a' style='position:absolute;width:20px;height:20px;" +
            "grid-column:2/3;grid-row:2/3;align-self:normal'>a</div>" +
            "</div>" +
            "</body></html>");

        var a = Find(view, "a");

        // `normal` is the box's own answer and not the container's: an out-of-flow box written with it
        // stands at the area's start edge even where the container centres its items (CSS Align 3 §5.1).
        Assert.That(a.BorderBox.Y, Is.EqualTo(60).Within(0.5), "normal is not auto, which would centre");
    }

    [Test]
    public async Task ANormalAlignmentOnAGridItemFillsTheAreaItWasGiven()
    {
        using var view = CreateView(400);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div style='display:grid;width:300px;height:200px;align-items:center;" +
            "grid-template-columns:100px 100px;grid-template-rows:50px 50px'>" +
            "<div id='a' style='align-self:normal'>a</div>" +
            "</div>" +
            "</body></html>");

        var a = Find(view, "a");

        // For an item that is in flow, `normal` is the one that stretches: it fills the row the item was
        // placed in, whatever the container's items value says.
        Assert.That(a.BorderBox.Height, Is.EqualTo(50).Within(0.5), "normal stretches an item to its area");
    }

    [Test]
    public async Task ASubgridLaysItsItemsOnTheTracksOfTheAreaItSpans()
    {
        using var view = CreateView(400);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div style='display:grid;grid-template-columns:repeat(3,100px);" +
            "grid-template-rows:repeat(2,50px);gap:10px;width:320px'>" +
            "<div id='s' style='display:grid;grid-column:1 / -1;grid-row:1 / -1;" +
            "grid-template-columns:subgrid;grid-template-rows:subgrid'>" +
            "<div id='a'></div><div id='b'></div><div id='c'></div>" +
            "</div></div>" +
            "</body></html>");

        var a = Find(view, "a");
        var b = Find(view, "b");
        var c = Find(view, "c");

        Assert.Multiple(() =>
        {
            Assert.That(a.BorderBox.X, Is.EqualTo(0).Within(0.5), "the first child stands on the parent's first column");
            Assert.That(a.BorderBox.Width, Is.EqualTo(100).Within(0.5));
            Assert.That(b.BorderBox.X, Is.EqualTo(110).Within(0.5), "and the next past the parent's gutter");
            Assert.That(c.BorderBox.X, Is.EqualTo(220).Within(0.5));
            Assert.That(a.BorderBox.Height, Is.EqualTo(50).Within(0.5), "the rows are the parent's as well");
        });
    }

    [Test]
    public async Task AnAbsolutePathChildOfAnUnpositionedGridStandsInItsContentBox()
    {
        using var view = CreateView(400);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div style='display:grid;width:300px;height:200px;padding:10px;" +
            "grid-template-columns:100px 100px;grid-template-rows:50px 50px'>" +
            "<div id='a' style='position:absolute;width:20px;height:20px;" +
            "grid-column:2/3;grid-row:2/3;justify-self:end;align-self:center'>a</div>" +
            "</div>" +
            "</body></html>");

        var a = Find(view, "a");

        // The container is static, so it generates no containing block for the child and lends it no area
        // either: the child stands as the sole grid item in the content box would, aligned within it
        // (CSS Grid 2 §10.2), while its offsets - none here - would count from wherever an ancestor puts
        // them.
        Assert.That(a.BorderBox.X, Is.EqualTo(10 + 300 - 20).Within(0.5), "at the content box's inline end");
        Assert.That(a.BorderBox.Y, Is.EqualTo(10 + (200 - 20) / 2).Within(0.5), "and centred along the block axis");
    }

    [Test]
    public async Task ASubgridStandsItsItemsInTheGuttersOfTheTracksItBorrows()
    {
        // Every line a subgrid borrows is the middle of the room the parent left at that line, and the
        // gutter the subgrid writes for the axis stands centred on it, so the tracks it borrows are cut
        // to what is left between the two gutters about them (CSS Grid 2 §8.2, as Chrome lays it).
        using var view = CreateView(400);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div style='display:grid;grid-template-columns:100px 100px;gap:20px;width:220px'>" +
            "<div style='display:grid;grid-column:span 2;grid-template-columns:subgrid;gap:60px'>" +
            "<div id='a' style='grid-column:2;width:50px;height:20px'>A</div>" +
            "</div></div></body></html>");

        var a = Find(view, "a");

        Assert.That(a.BorderBox.X, Is.EqualTo(140).Within(0.5), "past the half of each gutter the line carries");
    }

    [Test]
    public async Task ASubgridKeepsTheGapItStatedBeforeItsOwnTemplateShorthand()
    {
        // `grid: subgrid` borrows the tracks but not the gutters: the gap the page wrote before it is
        // still the subgrid's own, because the shorthand resets only what controls the implicit grid
        // (CSS Grid 2 §7.1) - which is how wpt's grid-gap-larger-001 states its 20px.
        using var view = CreateView(400);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div style='display:grid;grid-template-columns:100px 100px;gap:20px;width:220px'>" +
            "<div style='display:grid;grid-column:span 2;gap:60px;grid: subgrid / subgrid'>" +
            "<div id='a' style='grid-column:2;width:50px;height:20px'>A</div>" +
            "</div></div></body></html>");

        var a = Find(view, "a");

        Assert.That(a.BorderBox.X, Is.EqualTo(140).Within(0.5), "the stated gutter still cuts the tracks");
    }

    [Test]
    public async Task ASubgridWiderThanTheGutterItBorrowsCutsItsOuterTracks()
    {
        // The gap a subgrid states is charged to the tracks it borrows rather than added to them: the
        // outer line of the area stands at the subgrid's own edge, so its padding and the wider gutter
        // both come off the first and last track (CSS Grid 2 §8.2, and wpt's grid-gap-larger-001).
        using var view = CreateView(600);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div style='display:grid;grid-template-columns:150px 100px 150px 100px;width:500px'>" +
            "<div style='display:grid;grid-column:2/5;grid-template-columns:subgrid;" +
            "gap:20px;padding:20px'>" +
            "<div id='a'></div><div id='b'></div><div id='c'></div>" +
            "</div></div></body></html>");

        var a = Find(view, "a");
        var b = Find(view, "b");
        var c = Find(view, "c");

        Assert.Multiple(() =>
        {
            Assert.That(a.BorderBox.X, Is.EqualTo(170).Within(0.5), "at the subgrid's own content edge");
            Assert.That(a.BorderBox.Width, Is.EqualTo(70).Within(0.5), "the first track cut to what is left");
            Assert.That(b.BorderBox.X, Is.EqualTo(260).Within(0.5));
            Assert.That(b.BorderBox.Width, Is.EqualTo(130).Within(0.5), "the middle loses the whole gutter");
            Assert.That(c.BorderBox.X, Is.EqualTo(410).Within(0.5));
            Assert.That(c.BorderBox.Width, Is.EqualTo(70).Within(0.5));
        });
    }

    [Test]
    public async Task ASubgridWithoutAGutterOfItsOwnTakesTheRoomTheParentLeft()
    {
        // The other half of the same rule: a gutter of nothing stands at the middle of the parent's, so
        // the tracks it borrows grow over the gutters it did not ask for (wpt's grid-gap-smaller-001).
        using var view = CreateView(600);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div style='display:grid;grid-template-columns:150px 100px 150px 100px;" +
            "gap:20px;width:560px'>" +
            "<div style='display:grid;grid-column:2/5;grid-template-columns:subgrid;" +
            "gap:0;padding:20px'>" +
            "<div id='a'></div><div id='b'></div><div id='c'></div>" +
            "</div></div></body></html>");

        var a = Find(view, "a");
        var b = Find(view, "b");
        var c = Find(view, "c");

        Assert.Multiple(() =>
        {
            Assert.That(a.BorderBox.Width, Is.EqualTo(90).Within(0.5), "half the parent's gutter added to it");
            Assert.That(b.BorderBox.X, Is.EqualTo(280).Within(0.5));
            Assert.That(b.BorderBox.Width, Is.EqualTo(170).Within(0.5), "both of them, for the middle track");
            Assert.That(c.BorderBox.X, Is.EqualTo(450).Within(0.5));
            Assert.That(c.BorderBox.Width, Is.EqualTo(90).Within(0.5));
        });
    }

    [Test]
    public async Task ASubgridItemNamesALineTheOuterGridWrote()
    {
        // The lines a subgrid borrows carry the names the parent wrote on them, so an item of the
        // subgrid asks for the same word and stands where the outer grid put it (CSS Grid 2 §8.1).
        using var view = CreateView(400);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div style='display:grid;grid-template-columns:100px [a] 100px 100px;width:300px'>" +
            "<div style='display:grid;grid-column:span 3;grid-template-columns:subgrid'>" +
            "<div id='x' style='grid-column:a;width:50px;height:20px'>X</div>" +
            "</div></div></body></html>");

        var x = Find(view, "x");

        Assert.That(x.BorderBox.X, Is.EqualTo(100).Within(0.5), "where the outer grid named the second line");
    }

    [Test]
    public async Task ASubgridNamesTheLinesItBorrowsWithItsOwn()
    {
        // The groups written after `subgrid` name the borrowed lines in order, standing beside the
        // names the outer grid wrote on those lines rather than replacing them (CSS Grid 2 §8.1).
        using var view = CreateView(400);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div style='display:grid;grid-template-columns:100px [a] 100px 100px;width:300px'>" +
            "<div style='display:grid;grid-column:span 3;grid-template-columns:subgrid [b] [c]'>" +
            "<div id='p' style='grid-column:b;grid-row:1;width:20px;height:20px'>P</div>" +
            "<div id='q' style='grid-column:c;grid-row:2;width:20px;height:20px'>Q</div>" +
            "<div id='r' style='grid-column:a;grid-row:3;width:20px;height:20px'>R</div>" +
            "</div></div></body></html>");

        var p = Find(view, "p");
        var q = Find(view, "q");
        var r = Find(view, "r");

        Assert.Multiple(() =>
        {
            Assert.That(p.BorderBox.X, Is.EqualTo(0).Within(0.5), "the first borrowed line");
            Assert.That(q.BorderBox.X, Is.EqualTo(100).Within(0.5), "the second, which the outer grid also named");
            Assert.That(r.BorderBox.X, Is.EqualTo(100).Within(0.5), "and the outer name still stands there");
        });
    }

    [Test]
    public async Task ANestedSubgridItemNamesALineTheOutermostGridWrote()
    {
        // A subgrid hands on the lines it borrowed, so a subgrid two levels down is still named from
        // the grid that wrote the names rather than from the one it stands in (CSS Grid 2 §8.1).
        using var view = CreateView(400);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div style='display:grid;grid-template-columns:100px [a] 100px 100px;width:300px'>" +
            "<div style='display:grid;grid-column:span 3;grid-template-columns:subgrid'>" +
            "<div style='display:grid;grid-column:span 3;grid-template-columns:subgrid'>" +
            "<div id='x' style='grid-column:a;width:50px;height:20px'>X</div>" +
            "</div></div></div></body></html>");

        var x = Find(view, "x");

        Assert.That(x.BorderBox.X, Is.EqualTo(100).Within(0.5), "the line the outermost grid named");
    }

    [Test]
    public async Task ANameWrittenWithItsNumberCountsThatManyLinesOver()
    {
        // `a 3` is the third line named `a`, and a number past the last one of that name is held to it
        // rather than left unplaced (CSS Grid 1 §8.5).
        using var view = CreateView(400);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div style='display:grid;grid-template-columns:10px [a] repeat(3,10px [a]);width:40px'>" +
            "<div id='x' style='grid-column:a 3 / span 1;height:20px'>X</div>" +
            "<div id='y' style='grid-column:a 99 / span 1;height:20px;grid-row:2'>Y</div>" +
            "</div></body></html>");

        var x = Find(view, "x");
        var y = Find(view, "y");

        Assert.Multiple(() =>
        {
            Assert.That(x.BorderBox.X, Is.EqualTo(30).Within(0.5), "the third line named a");
            Assert.That(y.BorderBox.X, Is.EqualTo(40).Within(0.5), "and the last of them for a count past it");
        });
    }

    [Test]
    public async Task AGapWrittenAsACalcKeepsItsShareAndItsDistance()
    {
        // A calc() that adds a share to a distance keeps both until the box is known (CSS Values 4 §10.9):
        // the row gutter of `calc(15% + 7px)` is 15% of the height *and* 7px, not the window's 15% folded
        // on the way in - wpt's grid-gutters-011.
        using var view = CreateView(400);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div style='display:grid;width:200px;height:220px;" +
            "gap:calc(15% + 7px) calc(10px + 5%);" +
            "grid-template-columns:90px 90px;grid-template-rows:90px 90px'>" +
            "<div id='a'></div><div id='b'></div><div id='c'></div><div id='d'></div>" +
            "</div></body></html>");

        var b = Find(view, "b");
        var c = Find(view, "c");

        Assert.Multiple(() =>
        {
            Assert.That(b.BorderBox.X, Is.EqualTo(110).Within(0.5), "10px plus a twentieth of the width");
            Assert.That(c.BorderBox.Y, Is.EqualTo(130).Within(0.5), "7px plus 15% of the height");
        });
    }

    [Test]
    public async Task AGapWrittenAsAPercentFillsTheRoomTheTracksStandIn()
    {
        // A gap written as a share is a share of the box the tracks are laid in, not of the length it was
        // read with (CSS Box Alignment 3 §8) - 10% of a 300px content box is a 30px gutter, and Chrome
        // lays the second column at 80px for it.
        using var view = CreateView(400);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div style='display:grid;width:300px;height:100px;gap:10%;" +
            "grid-template-columns:50px 50px;grid-template-rows:20px 20px'>" +
            "<div id='a'></div><div id='b'></div><div id='c'></div><div id='d'></div>" +
            "</div></body></html>");

        var b = Find(view, "b");
        var c = Find(view, "c");

        Assert.Multiple(() =>
        {
            Assert.That(b.BorderBox.X, Is.EqualTo(80).Within(0.5), "past a tenth of the width");
            Assert.That(c.BorderBox.Y, Is.EqualTo(30).Within(0.5), "and a tenth of the height between the rows");
        });
    }

    [Test]
    public async Task ASubgridTakesItsPercentageGutterFromTheAreaItBorrows()
    {
        // The share is the subgrid's own, taken from the room the borrowed tracks give it: two 50px
        // columns make a 100px area, so a 10% gap is 10px and each track keeps 45.
        using var view = CreateView(400);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div style='display:grid;width:300px;" +
            "grid-template-columns:50px 50px;grid-template-rows:20px 20px'>" +
            "<div style='display:grid;grid-column:1 / -1;grid-row:1 / -1;" +
            "grid-template-columns:subgrid;column-gap:10%'>" +
            "<div id='a'></div><div id='b'></div>" +
            "</div></div></body></html>");

        var a = Find(view, "a");
        var b = Find(view, "b");

        Assert.Multiple(() =>
        {
            Assert.That(a.BorderBox.Width, Is.EqualTo(45).Within(0.5), "the borrowed track cut by the gutter");
            Assert.That(b.BorderBox.X, Is.EqualTo(55).Within(0.5), "centred on the line it borrows");
        });
    }

    [Test]
    public async Task ASubgridOpensTheRowsItBorrowsToWhatItsOwnItemsNeed()
    {
        // What a subgrid asks of the parent's auto tracks is its own items' sizes plus the room its own
        // gutter and padding stand in the way of: an item of 56 with a 20px gutter and 20px of padding
        // opens the borrowed row to 86, and the row it lays its item in is back to 56 (CSS Grid 2,
        // sizing subgrids - and what Chrome measures for the same page).
        using var view = CreateView(400);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div id='p' style='display:grid;grid-template-columns:100px 100px;" +
            "grid-template-rows:auto auto;width:200px'>" +
            "<div id='s' style='display:grid;grid-column:span 2;grid-row:span 2;" +
            "grid-template-columns:subgrid;grid-template-rows:subgrid;gap:20px;padding:20px'>" +
            "<div id='a' style='height:56px'></div><div id='b' style='height:56px'></div>" +
            "<div id='c' style='height:56px'></div><div id='d' style='height:56px'></div>" +
            "</div></div></body></html>");

        var s = Find(view, "s");
        var a = Find(view, "a");
        var b = Find(view, "b");
        var c = Find(view, "c");
        var d = Find(view, "d");

        Assert.Multiple(() =>
        {
            Assert.That(s.BorderBox.Height, Is.EqualTo(172).Within(0.5), "two rows of 86 with the parent's room");
            Assert.That(a.BorderBox.Width, Is.EqualTo(70).Within(0.5), "the column cut to what the gutter leaves");
            Assert.That(a.BorderBox.Height, Is.EqualTo(56).Within(0.5));
            Assert.That(b.BorderBox.X, Is.EqualTo(110).Within(0.5));
            Assert.That(c.BorderBox.Y, Is.EqualTo(96).Within(0.5), "the second row past the subgrid's gutter");
            Assert.That(d.BorderBox.X, Is.EqualTo(110).Within(0.5));
            Assert.That(d.BorderBox.Y, Is.EqualTo(96).Within(0.5));
        });
    }

    [Test]
    public void ASubgridWrittenInATemplateShorthandIsATemplate()
    {
        // The template shorthand covers the three explicit-track properties - and the `grid` shorthand
        // adds only the ones that control the implicit grid, not the gutters between the tracks, which
        // keep whatever the page said for them (CSS Grid 2 §7.1, as Chrome computes it).
        var stated = Declared(".x { row-gap: 100px; column-gap: 10px; grid: subgrid / subgrid }");

        Assert.Multiple(() =>
        {
            Assert.That(stated["grid-template-rows"], Is.EqualTo("subgrid"));
            Assert.That(stated["grid-template-columns"], Is.EqualTo("subgrid"));
            Assert.That(stated["row-gap"], Is.EqualTo("100px"), "the gap the page stated before stands");
            Assert.That(stated["column-gap"], Is.EqualTo("10px"));
        });
    }

    [Test]
    public void ATrackListOnOneSideOfATemplateShorthandKeepsASubgridOnTheOther()
    {
        var stated = Declared(".x { grid: 50px / subgrid }");

        Assert.Multiple(() =>
        {
            Assert.That(stated["grid-template-rows"], Is.EqualTo("50px"));
            Assert.That(stated["grid-template-columns"], Is.EqualTo("subgrid"));
        });
    }

    /// <summary>Gets the declarations a rule leaves behind, by the name each was filed under.</summary>
    private static Dictionary<String, String> Declared(String css)
    {
        var sheet = (ICssStyleSheet)new CssParser().ParseStyleSheet(css);
        var rule = sheet.Rules.OfType<ICssStyleRule>().Single();
        var stated = new Dictionary<String, String>(StringComparer.Ordinal);

        foreach (var property in rule.Style)
        {
            stated[property.Name] = property.Value;
        }

        return stated;
    }

    private static LayoutBox Find(HutuViewInternal view, String id)
    {
        foreach (var node in Walk(view))
        {
            if (node.Source.Element?.GetAttribute("id") == id)
            {
                return node;
            }
        }

        Assert.Fail($"no box with id '{id}' was laid out");
        return null!;
    }

    /// <summary>Finds the line box that holds a given piece of text.</summary>
    private static LayoutBox FindLine(HutuViewInternal view, String text)
    {
        foreach (var node in Walk(view))
        {
            if (node.Kind != LayoutKind.Line)
            {
                continue;
            }

            // Through the whole subtree: text wrapped in an inline element hangs off that element's own
            // node rather than off the line, one node per line the element reaches.
            if (Descend(node).Any(child => child.Kind == LayoutKind.Text && child.Text == text))
            {
                return node;
            }
        }

        Assert.Fail($"no line box holding '{text}' was laid out");
        return null!;
    }

    private static IEnumerable<LayoutBox> Walk(HutuViewInternal view)
    {
        var root = view.Layout?.Root;

        Assert.That(root, Is.Not.Null, "the document produced no layout");

        return Descend(root!);
    }

    private static IEnumerable<LayoutBox> Descend(LayoutBox node)
    {
        yield return node;

        foreach (var child in node.Children)
        {
            foreach (var descendant in Descend(child))
            {
                yield return descendant;
            }
        }
    }
}

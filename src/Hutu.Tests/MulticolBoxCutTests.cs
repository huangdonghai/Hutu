using Hutu.Rendering.Layout;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Hutu.Tests;

/// <summary>
/// Covers a box that has nothing to break between standing in a column shorter than it: the column is
/// cut through the box rather than left to overflow the container whole, the fragments keep the edges
/// that fall inside them, and a wrapper that paints nothing and is shorter than what it holds is opened
/// into the columns so that its content is what gets cut.
/// </summary>
[TestFixture]
public sealed class MulticolBoxCutTests
{
    [Test]
    public async Task ABoxTallerThanTheColumnsIsCutAcrossThem()
    {
        using var view = await Render(
            "<style>#c { width: 300px; columns: 3; column-gap: 0; column-fill: auto; height: 100px }" +
            " .tall { height: 250px }</style>" +
            "<div id='c'><div class='tall' id='t'></div></div>");

        var pieces = Of(view, "t");

        Assert.That(pieces.Count, Is.EqualTo(3), "the box stands in every column");
        Assert.That(
            pieces.Select(piece => piece.BorderBox.Height).ToList(),
            Is.EqualTo(new List<Double> { 100, 100, 50 }),
            "each column holds what stands in it");
        Assert.That(
            pieces.Select(piece => piece.BorderBox.X).ToList(),
            Is.EqualTo(new List<Double> { 0, 100, 200 }),
            "and the pieces follow the columns");
        Assert.That(Find(view, "c").BorderBox.Height, Is.EqualTo(100), "the container keeps the height it stated");
    }

    [Test]
    public async Task TheEdgesOfACutBoxBelongToThePiecesThatHoldThem()
    {
        using var view = await Render(
            "<style>#c { width: 300px; columns: 3; column-gap: 0; column-fill: auto; height: 100px }" +
            " .lead { height: 95px }" +
            " .box { border: 10px solid; height: 100px }</style>" +
            "<div id='c'><div class='lead'></div><div class='box' id='b'></div></div>");

        var pieces = Of(view, "b");

        Assert.That(pieces.Count, Is.EqualTo(2), "the box is cut, and the column after holds its rest");
        Assert.That((pieces[0].BorderBox.X, pieces[0].BorderBox.Y), Is.EqualTo((100.0, 0.0)),
            "a box that does not fit the room left begins the next column");
        Assert.That(pieces[0].BorderBox.Height, Is.EqualTo(100), "the first piece is as tall as the column");
        Assert.That(pieces[1].BorderBox.Height, Is.EqualTo(20), "and the rest of the box stands in the column after it");
        Assert.That(pieces[0].Border.Bottom, Is.EqualTo(0), "no bottom edge is drawn where the box was cut");
        Assert.That(pieces[1].Border.Top, Is.EqualTo(0), "nor is the top edge drawn again");
        Assert.That(pieces[1].Border.Bottom, Is.EqualTo(10), "which edge the last piece owns");
    }

    [Test]
    public async Task AWrapperShorterThanWhatItHoldsIsOpenedIntoTheColumns()
    {
        using var view = await Render(
            "<style>#c { width: 200px; columns: 2; column-gap: 0; column-fill: auto; height: 100px }" +
            " .clip { height: 2px } .tall { height: 150px }</style>" +
            "<div id='c'><div class='clip'><div class='tall' id='t'></div></div></div>");

        var pieces = Of(view, "t");

        Assert.That(pieces.Count, Is.EqualTo(2), "the wrapper is no place for a column to end, so its content is cut");
        Assert.That(
            pieces.Select(piece => piece.BorderBox.Height).ToList(),
            Is.EqualTo(new List<Double> { 100, 50 }),
            "at the height the column has");
        Assert.That(pieces[1].BorderBox.X, Is.EqualTo(100), "and the rest is carried to the next column");
    }

    [Test]
    public async Task AMaximumOnTheContainerHoldsItsColumnsToThatHeight()
    {
        using var view = await Render(
            "<style>#c { width: 200px; columns: 2; column-gap: 0; column-fill: auto; max-height: 100px }" +
            " .tall { height: 200px }</style>" +
            "<div id='c'><div class='tall' id='t'></div></div>");

        var pieces = Of(view, "t");

        Assert.That(pieces.Count, Is.EqualTo(2), "a column is as tall as the maximum the container comes to");
        Assert.That(
            pieces.Select(piece => piece.BorderBox.Height).ToList(),
            Is.EqualTo(new List<Double> { 100, 100 }),
            "and the box is shared between the columns at that height");
        Assert.That(Find(view, "c").BorderBox.Height, Is.EqualTo(100), "which is the height the container holds");
    }

    [Test]
    public async Task ABoxThatRepeatsItsEdgesBreaksTheContentAndAddsThemToEveryPiece()
    {
        using var view = await Render(
            "<style>#c { width: 300px; columns: 3; column-gap: 0; column-fill: auto; height: 100px }" +
            " .clone { box-decoration-break: clone; border: 10px solid; height: 240px }</style>" +
            "<div id='c'><div class='clone' id='b'></div></div>");

        var pieces = Of(view, "b");

        Assert.That(pieces.Count, Is.EqualTo(3), "eighty of content stands in each column");
        Assert.That(
            pieces.Select(piece => piece.BorderBox.Height).ToList(),
            Is.EqualTo(new List<Double> { 100, 100, 100 }),
            "and each piece brings its own edges, ten above and ten below");
        Assert.That(
            pieces.Select(piece => piece.Border.Top + piece.Border.Bottom).Distinct().ToList(),
            Is.EqualTo(new List<Double> { 20 }),
            "so the edges are drawn on every piece, not only the first and the last");
    }

    [Test]
    public async Task AWrapperCappedByAMaximumLetsTheColumnCutWhatOverflowsIt()
    {
        using var view = await Render(
            "<style>#c { width: 200px; columns: 2; column-gap: 0; column-fill: auto; height: 100px }" +
            " .cap { max-height: 100px } .tall { height: 200px }</style>" +
            "<div id='c'><div class='cap'><div class='tall' id='t'></div></div></div>");

        var pieces = Of(view, "t");

        Assert.That(pieces.Count, Is.EqualTo(2), "a wrapper that caps itself at the column's height is no place to stop");
        Assert.That(
            pieces.Select(piece => piece.BorderBox.Height).ToList(),
            Is.EqualTo(new List<Double> { 100, 100 }),
            "so the content is shared between the columns");
    }

    [Test]
    public async Task ABoxThatNeedsMorePiecesThanThereAreColumnsIsLeftWhole()
    {
        using var view = await Render(
            "<style>#c { width: 200px; columns: 2; column-gap: 0; column-fill: auto; height: 100px }" +
            " .tall { height: 250px }</style>" +
            "<div id='c'><div class='tall' id='t'></div></div>");

        var pieces = Of(view, "t");

        Assert.That(pieces.Count, Is.EqualTo(1), "the columns it would need are not drawn, so the box is not cut");
        Assert.That(pieces[0].BorderBox.Height, Is.EqualTo(250), "and stands whole past the bottom of them");
    }

    [Test]
    public async Task ABoxThatContainsItsBlockSizeIsNotCutEvenWhereItOverflows()
    {
        using var view = await Render(
            "<style>#c { width: 200px; columns: 2; column-gap: 0; column-fill: auto; height: 100px }" +
            " .solid { contain: size; height: 250px }</style>" +
            "<div id='c'><div class='solid' id='t'></div></div>");

        var pieces = Of(view, "t");

        Assert.Multiple(() =>
        {
            // A box that contains its size is monolithic - its own declared extent, with nothing of what
            // it holds reaching it - so it has no break point for a column to fall on, however empty it
            // is and however far it reaches past the column (CSS Contain 1 §3.1.1, CSS Break 3 §4.1).
            Assert.That(pieces, Has.Count.EqualTo(1), "no column is cut through it");
            Assert.That(
                (pieces[0].BorderBox.X, pieces[0].BorderBox.Y, pieces[0].BorderBox.Height),
                Is.EqualTo((0.0, 0.0, 250.0)),
                "it stands whole in the column it reached and overflows from there");
        });
    }

    [Test]
    public async Task AFloatNoColumnCanHoldIsPushedWholeAndStandsThereOverflowing()
    {
        using var view = await Render(
            "<style>#c { width: 200px; columns: 2; column-gap: 0; column-fill: auto; height: 100px }</style>" +
            "<div id='c'><div style='height:80px'></div>" +
            "<div id='f' style='float:right; contain:size; width:50px; height:200px'></div></div>");

        var pieces = Of(view, "f");

        // The room the box above left was 20 of a column, so the float begins the next one; it is
        // monolithic, so that column cuts nothing through it either - it is measured as one 200-tall
        // band there, and the flow it stands beside is pushed below the whole of it (CSS 2.1 §9.5.1,
        // CSS Contain 1 §3.1.1, measured: wpt/css-break/float-017 against the same float with
        // contain:paint, which Chrome does cut down the columns).
        Assert.That(pieces, Has.Count.EqualTo(1), "the pushed float is not continued down the columns");
        Assert.That(
            (pieces[0].BorderBox.X, pieces[0].BorderBox.Y, pieces[0].BorderBox.Height),
            Is.EqualTo((150.0, 0.0, 200.0)),
            "it stands at the top of the column it was pushed to, at its own side");
    }

    [Test]
    public async Task EachPieceOfATallFlowBoxTakesTheRoomItsOwnColumnLeaves()
    {
        using var view = await Render(
            "<style>#c { width: 300px; columns: 3; column-gap: 0; column-fill: auto; height: 100px }</style>" +
            "<div id='c'><div style='float:right; width:80px; height:20px'></div>" +
            "<div style='float:right; clear:both; break-inside:avoid; width:60px; height:90px'></div>" +
            "<div id='f' style='display:flow-root; height:300px'></div></div>");

        var pieces = Of(view, "f");

        // The first float fits the column it is written in; the second has no room left under it, so it
        // begins the next column and leaves the flow where it stood - and a root of a formatting context
        // may not reach over a float, so each piece is as wide as its own column leaves (CSS 2.1 §9.4.1,
        // §9.5.1, measured: wpt/css-break/fragmented-autowidth-fc-root-beside-floats).
        Assert.That(pieces, Has.Count.EqualTo(3), "the columns share the box one piece each");
        Assert.That(
            pieces.Select(piece => (piece.BorderBox.X, piece.BorderBox.Width)).ToList(),
            Is.EqualTo(new List<(Double, Double)> { (0, 20), (100, 40), (200, 100) }),
            "each piece takes the width its column has left beside the float standing there");
        Assert.That(
            pieces.Select(piece => piece.FragmentClip?.Height ?? 0.0).ToList(),
            Is.EqualTo(new List<Double> { 100, 100, 100 }),
            "and each shows one column's height of the box");
    }

    [Test]
    public async Task ANestedContainerOfOneFilledColumnIsOpenedIntoTheColumnsAroundIt()
    {
        using var view = await Render(
            "<style>#c { width: 200px; columns: 2; column-gap: 0; column-fill: auto; height: 100px }" +
            " .shell { columns: 1; column-fill: auto; height: 250px } .tall { height: 200px }</style>" +
            "<div id='c'><div class='shell' id='s'><div class='tall' id='t'></div></div></div>");

        var pieces = Of(view, "t");

        // A container of a single filled column that paints nothing brings no boundary the cut could
        // stop at: what the columns around it divide is its content, at their own width - which is
        // what Chrome does with each piece of a nested fragmentainer (CSS Multicol 3 §7.4, CSS Break 3
        // §4.1, measured: repeated-section/variable-fragmentainer-size-002).
        Assert.That(Of(view, "s"), Has.Count.EqualTo(0), "the shell itself stands in no column");
        Assert.That(pieces, Has.Count.EqualTo(2), "the box it holds is shared between the outer columns");
        Assert.That(
            pieces.Select(piece => (piece.BorderBox.X, piece.BorderBox.Width, piece.BorderBox.Height)).ToList(),
            Is.EqualTo(new List<(Double, Double, Double)> { (0, 100, 100), (100, 100, 100) }),
            "each piece takes one column's width and height");
    }

    [Test]
    public async Task ANestedContainerOfTwoColumnsIsStillLeftWhole()
    {
        using var view = await Render(
            "<style>#c { width: 200px; columns: 2; column-gap: 0; column-fill: auto; height: 100px }" +
            " .shell { columns: 2; column-gap: 0; column-fill: auto; height: 250px } .tall { height: 200px }</style>" +
            "<div id='c'><div class='shell' id='s'><div class='tall' id='t'></div></div></div>");

        // Where the nested container has columns of its own to balance, the columns around it still step
        // around it: each piece of the shell would need its own column layout again, which the outer
        // columns do not do yet (measured edge of the rule: balanced-inner-multicol stays whole).
        Assert.That(Of(view, "s"), Has.Count.EqualTo(1), "the two-column shell stands as one box");
        Assert.That(Of(view, "t"), Has.Count.EqualTo(1), "and what it holds is not shared out");
    }

    [Test]
    public async Task ABoxWithItsOwnBlockStartMarginDividesAtTheRoomLeftAfterIt()
    {
        using var view = await Render(
            "<style>#c { width: 200px; columns: 2; column-gap: 0; column-fill: auto; height: 100px }" +
            " #g { margin-top: 60px; height: 120px } .unit { height: 200px }</style>" +
            "<div id='c'><div id='g'><div class='unit'></div></div></div>");

        var pieces = Of(view, "g");

        // The margin stands in the column that holds the box, so that column only gets what is left of
        // it; the box carries on at the top of the next. The margin is spent once, where the box begins
        // - not again on every piece of it (CSS 2.1 §8.3.1, CSS Break 3 §4.3).
        Assert.That(pieces, Has.Count.EqualTo(3), "the box and what it lets overflow span three columns");
        Assert.That(
            pieces.Select(piece => (piece.FragmentClip?.X, piece.FragmentClip?.Y, piece.FragmentClip?.Height))
                .ToList(),
            Is.EqualTo(new List<(Double?, Double?, Double?)> { (0, 60, 40), (100, 60, 100), (200, 60, 100) }),
            "the first column holds only the room its margin left");
    }

    [Test]
    public async Task EachPieceOfADividedTransformedBoxTurnsAboutItsOwnRectangle()
    {
        using var view = await Render(
            "<style>#c { width: 200px; columns: 2; column-gap: 0; column-fill: auto; height: 100px }" +
            " #g { margin-top: 60px; height: 120px; transform: rotate(25deg) } .unit { height: 200px }</style>" +
            "<div id='c'><div id='g'><div class='unit'></div></div></div>");

        var pieces = Of(view, "g");

        // The transform reads the fragment, not the whole: each column's origin stands in the middle of
        // the stretch it holds, where the whole box would give every piece the same 60 pixels down
        // (CSS Transforms 1 §transform-origin read against CSS Fragmentation 3 §4.1, measured:
        // transform-006, whose fragments each stand rotated about a corner of their own stretch).
        Assert.That(pieces, Has.Count.EqualTo(3), "the box divides");
        Assert.That(
            pieces.Select(piece => (piece.TransformOrigin.X, piece.TransformOrigin.Y)).ToList(),
            Is.EqualTo(new List<(Double, Double)> { (50, 20), (50, 90), (50, 190) }),
            "each origin is the middle of the stretch that column holds, in the box's own coordinates");
        Assert.That(
            pieces[0].TransformOrigin.Y,
            Is.Not.EqualTo(60),
            "not the middle of the whole box, which every piece would then share");
    }

    [Test]
    public async Task AHeaderGroupFreeToBreakStandsInNoColumnButItsOwn()
    {
        using var view = await Render(Table(inside: "auto"));

        // A fragment copies the table's header only when the header itself asks not to be broken; a
        // header free to split is left where its rows fall, so a table over two columns shows it once
        // (CSS Tables 3 §6.2, measured: sections-and-captions-mixed-order, whose header of one line
        // shows in none of the three columns after the first).
        Assert.That(HeadOfSecondColumn(view), Is.EqualTo(0), "the second column takes no copy of the header");
    }

    [Test]
    public async Task AHeaderGroupThatMustNotBreakIsCopiedIntoTheColumnAfter()
    {
        using var view = await Render(Table(inside: "avoid"));

        // The same table with `break-inside: avoid` on the group: the second column carries a copy of
        // the header at its own top, and gives up the room for it (CSS Tables 3 §6.2, measured:
        // variable-fragmentainer-size-001, whose header shows in every column the table spans).
        Assert.That(HeadOfSecondColumn(view), Is.EqualTo(1), "the header stands again at the head of the column");
    }

    /// <summary>Counts the copies of the header group standing at the top of the second column.</summary>
    private static Int32 HeadOfSecondColumn(HutuViewInternal view) =>
        Of(view, "h").Count(head => head.BorderBox.X == 100 && head.BorderBox.Y == 0);

    [Test]
    public async Task ASectionTallerThanAQuarterOfTheColumnIsCopiedNowhere()
    {
        using var view = await Render(Table(inside: "avoid", head: 30));

        // The clause that asks for the copy caps it: a repeated header or footer is only wanted where it
        // reaches no further than a quarter of the fragmentainer's block size, so a header of 30 in a
        // column of 100 is left where its rows stand (CSS Tables 3 §6.2, measured: build/_repthresh.html,
        // where a header of 20 or 25 shows in every column and one of 26 shows in the first only).
        Assert.That(HeadOfSecondColumn(view), Is.EqualTo(0), "the column keeps no copy of a header that tall");
    }

    private static String Table(String inside, Double head = 20) =>
        "<style>#c { width: 200px; columns: 2; column-gap: 0; column-fill: auto; height: 100px }" +
        " table { border-spacing: 0; width: 100px }</style>" +
        "<div id='c'><table><thead id='h' style='break-inside:" + inside + "'>" +
        "<tr><td><div style='height:" + head.ToString(System.Globalization.CultureInfo.InvariantCulture) + "px'></div></td></tr></thead>" +
        "<tbody><tr><td><div style='height:130px'></div></td></tr></tbody></table></div>";

    /// <summary>
    /// A float the column holds directly spends none of that column's room: the boxes the flow carries
    /// after it go on filling the same column, and the float overflows the column it was written in
    /// (CSS 2.1 §9.5.1, measured: wpt/css-break/float-017, whose ten lines stand in the columns around a
    /// 100%-wide, 200-tall float rather than one per column after it). The flow here is a box of lines
    /// taller than a column, which is the shape the columns share out the ordinary way rather than
    /// through the float-and-flow packing - and the ordinary way is where the float's room was spent.
    /// </summary>
    [Test]
    public async Task AFloatTheColumnHoldsSpendsNoneOfItsFlowRoom()
    {
        using var view = await Render(
            "<style>#c { columns:2; width:200px; height:100px; column-fill:auto; column-gap:0 }" +
            " #f { float:right; width:100%; height:200px }</style>" +
            "<div id='c'><div id='f'></div>" +
            "<div id='t'>a<br>a<br>a<br>a<br>a<br>a<br>a<br>a<br>a<br>a<br>a<br>a</div></div>");

        var flowed = Find(view, "t");
        var floated = Find(view, "f");

        Assert.Multiple(() =>
        {
            Assert.That(flowed.BorderBox.X, Is.EqualTo(0), "the lines begin in the first column...");
            Assert.That(flowed.BorderBox.Y, Is.EqualTo(0), "...at its top, under nothing the float spent");
            Assert.That(floated.BorderBox.X, Is.EqualTo(0), "a float as wide as the column fills it");
            Assert.That(floated.BorderBox.Height, Is.EqualTo(200), "and overflows the column it stands in");
        });
    }

    /// <summary>
    /// A vertical column that a divided box left part-empty is filled by the box that follows it: the
    /// cut runs along the block axis, so the room the column still has belongs to the flow, the same way
    /// a horizontal column's leftover does (CSS Fragmentation 3 §4.3, measured: the reference page of
    /// wpt/css-break/table/table-grid-paint-vrl-rtl, whose three boxes share each column they reach).
    /// </summary>
    [Test]
    public async Task AVerticalColumnFillsWhatTheBoxBeforeLeftInIt()
    {
        using var view = await Render(
            "<style>#c { writing-mode: vertical-rl; inline-size: 400px; block-size: 100px;" +
            " columns: 4; column-gap: 0; column-fill: auto }" +
            " .short { block-size: 140px } .tall { block-size: 150px }</style>" +
            "<div id='c'><div class='short' id='s'></div><div class='tall' id='t'></div></div>");

        var band = Find(view, "c").BorderBox;
        var head = Of(view, "s");
        var tail = Of(view, "t");

        Assert.Multiple(() =>
        {
            Assert.That(
                head.Select(piece => piece.BorderBox.Width).ToList(),
                Is.EqualTo(new List<Double> { 100, 40 }),
                "the first column is filled and what is left of the box carries on in the next");
            Assert.That(
                tail.Select(piece => piece.BorderBox.Width).ToList(),
                Is.EqualTo(new List<Double> { 60, 90 }),
                "the box after takes the room its column still has rather than a fresh one");
            Assert.That(
                tail[0].BorderBox.X + tail[0].BorderBox.Width,
                Is.EqualTo(band.X + band.Width - 40),
                "standing against the end of the piece the column already holds");
            Assert.That(tail[0].BorderBox.Y, Is.EqualTo(band.Y + 100), "in the column the tail of the box before");
            Assert.That(tail[1].BorderBox.Y, Is.EqualTo(band.Y + 200), "and the column after for the rest");
        });
    }

    [Test]
    public async Task EveryPieceOfABoxThatAvoidsBreakingTakesTheHeadOfAColumn()
    {
        using var view = await Render(
            "<style>#c { writing-mode: vertical-rl; inline-size: 400px; block-size: 30px;" +
            " columns: 4; column-gap: 0; column-fill: auto }" +
            " .b { break-inside: avoid; min-block-size: 40px }</style>" +
            "<div id='c'><div class='b' id='one'></div><div class='b' id='two'></div></div>");

        var band = Find(view, "c").BorderBox;
        var first = Of(view, "one");
        var second = Of(view, "two");

        Assert.Multiple(() =>
        {
            // A box longer than any column is broken after all, since no fragmentainer could hold it
            // whole (CSS Break 3 §3.1). What the ask still buys is where the break falls: every piece
            // of that box stands at the head of a column, so the box behind it is never started in the
            // room its tail left (measured: break-inside-avoid-min-block-size-2, whose two boxes of 40
            // in columns of 30 come out 30 + 10 at the heads of four columns, not three).
            Assert.That(
                first.Select(piece => piece.BorderBox.Width).ToList(),
                Is.EqualTo(new List<Double> { 30, 10 }),
                "the box is cut where the column ends");
            Assert.That(
                second.Select(piece => piece.BorderBox.Width).ToList(),
                Is.EqualTo(new List<Double> { 30, 10 }),
                "and its twin is cut the same way, from a column of its own");
            Assert.That(second[0].BorderBox.Y, Is.EqualTo(band.Y + 200),
                "the box behind begins at the head of the third column, not in the second's tail");
            Assert.That(
                first[0].BorderBox.X + first[0].BorderBox.Width,
                Is.EqualTo(band.X + band.Width),
                "and each piece stands at the edge vertical-rl begins its block axis at");
        });
    }

    private static async Task<HutuViewInternal> Render(String content)
    {
        var view = new HutuViewInternal(new FakeTextMeasurer());
        view.SetViewportSize(400, 600);
        await view.SetHtml(
            "<html><head><style>div { margin: 0 }</style></head>" +
            "<body style='margin:0;font-size:10px;line-height:10px'>" + content + "</body></html>");
        return view;
    }

    private static LayoutBox Find(HutuViewInternal view, String id) => Of(view, id).First();

    /// <summary>Gets every fragment of the box the page was given the id on, in the order they stand.</summary>
    private static List<LayoutBox> Of(HutuViewInternal view, String id) =>
        Walk(view.Layout!.Root)
            .Where(box => box.Source.Element?.GetAttribute("id") == id)
            .ToList();

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

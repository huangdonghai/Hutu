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

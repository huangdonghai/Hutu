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

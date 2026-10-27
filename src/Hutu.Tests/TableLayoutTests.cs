using Hutu.Rendering;
using Hutu.Rendering.Layout;
using Hutu.Rendering.Paint;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Hutu.Tests;

/// <summary>
/// The table formatting context: the two border models, the column and row algorithms, the anonymous
/// boxes the structure needs and the cell alignment.
/// </summary>
/// <remarks>
/// The documents go through the real cascade so that <c>display: table-cell</c>, <c>border-collapse</c>
/// and friends are read the way a page would present them. The font is a fixed-width stand-in, so a
/// character is five pixels wide and a line is ten pixels tall - which is what makes the expected
/// numbers exact rather than approximate.
/// </remarks>
[TestFixture]
public sealed class TableLayoutTests
{
    [Test]
    public async Task CellsInARowArePlacedSideBySide()
    {
        using var view = CreateView(400);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<table style='border-collapse:separate;border-spacing:0'>" +
            "<tr><td id='a'>A</td><td id='b'>BB</td></tr>" +
            "</table>" +
            "</body></html>");

        var a = Find(view, "a");
        var b = Find(view, "b");

        Assert.That(a.BorderBox.X, Is.EqualTo(0).Within(0.5));
        Assert.That(a.BorderBox.Width, Is.EqualTo(7).Within(0.5), "one character and the room a cell keeps at each side of it");
        Assert.That(b.BorderBox.X, Is.EqualTo(a.BorderBox.Right).Within(0.5));
        Assert.That(b.BorderBox.Width, Is.EqualTo(12).Within(0.5), "two characters and the same room");
        Assert.That(b.BorderBox.Y, Is.EqualTo(a.BorderBox.Y).Within(0.5));
        Assert.That(b.BorderBox.Height, Is.EqualTo(a.BorderBox.Height).Within(0.5), "one row, one height");
    }

    [Test]
    public async Task AStatedTableHeightMakesACellsPercentageResolve()
    {
        using var view = CreateView(400);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<table style='border-collapse:separate;border-spacing:0;height:100px'>" +
            "<tr><td id='cell'><div id='inner' style='height:100%'>x</div></td></tr>" +
            "</table>" +
            "</body></html>");

        var cell = Find(view, "cell");
        var inner = Find(view, "inner");

        Assert.That(
            inner.BorderBox.Height,
            Is.EqualTo(cell.BorderBox.Height - 2).Within(0.5),
            "the cell's height came from the table's, so it is definite and the percentage fills it");
    }

    [Test]
    public async Task ASiblingCellsHeightDoesNotMakeAPercentageResolve()
    {
        using var view = CreateView(400);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<table style='border-collapse:separate;border-spacing:0'>" +
            "<tr><td><div id='inner' style='height:100%'>x</div></td><td height='80'>tall</td></tr>" +
            "</table>" +
            "</body></html>");

        Assert.That(
            Find(view, "inner").BorderBox.Height,
            Is.EqualTo(10).Within(0.5),
            "the row is tall because of a neighbour, which Chrome does not treat as definite for the percentage");
    }

    [Test]
    public async Task TheTableShrinksToFitItsContent()
    {
        using var view = CreateView(400);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<table id='t' style='border-collapse:separate;border-spacing:0'>" +
            "<tr><td>A</td></tr>" +
            "</table>" +
            "</body></html>");

        Assert.That(Find(view, "t").BorderBox.Width, Is.EqualTo(7).Within(0.5), "a table without a width shrinks to the cell and the room it keeps");
    }

    [Test]
    public async Task APercentageWidthFillsTheParent()
    {
        using var view = CreateView(400);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div style='width:200px'>" +
            "<table id='t' style='width:100%;border-collapse:separate;border-spacing:0'><tr><td id='a'>A</td></tr></table>" +
            "</div>" +
            "</body></html>");

        Assert.That(Find(view, "t").BorderBox.Width, Is.EqualTo(200).Within(0.5));
        Assert.That(Find(view, "a").BorderBox.Width, Is.EqualTo(200).Within(0.5), "the single column takes it all");
    }

    [Test]
    public async Task CollapsedBordersShareASingleLine()
    {
        using var view = CreateView(400);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<table style='border-collapse:collapse'>" +
            "<tr>" +
            "<td id='a' style='border:1px solid #000'>A</td>" +
            "<td id='b' style='border:1px solid #000'>B</td>" +
            "</tr>" +
            "</table>" +
            "</body></html>");

        var a = Find(view, "a");
        var b = Find(view, "b");

        // The two cells abut; each paints half of the line they share, so together they draw one
        // one-pixel border rather than two.
        Assert.That(a.BorderBox.Right, Is.EqualTo(b.BorderBox.X).Within(0.05), "the cells abut");
        Assert.That(a.Border.Right, Is.EqualTo(0.5).Within(0.01));
        Assert.That(b.Border.Left, Is.EqualTo(0.5).Within(0.01));
        Assert.That(a.Border.Right + b.Border.Left, Is.EqualTo(1).Within(0.01), "one shared line");

        // On the table's outer edge a cell draws the whole line instead of half of it.
        Assert.That(a.Border.Left, Is.EqualTo(1).Within(0.01));
        Assert.That(b.Border.Right, Is.EqualTo(1).Within(0.01));
    }

    [Test]
    public async Task SeparatedBordersLeaveSpacingBetweenTheCells()
    {
        using var view = CreateView(400);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<table style='border-collapse:separate;border-spacing:10px'>" +
            "<tr><td id='a'>A</td><td id='b'>B</td></tr>" +
            "</table>" +
            "</body></html>");

        var a = Find(view, "a");
        var b = Find(view, "b");

        Assert.That(a.BorderBox.X, Is.EqualTo(10).Within(0.5), "the spacing also separates the cells from the edge");
        Assert.That(b.BorderBox.X - a.BorderBox.Right, Is.EqualTo(10).Within(0.5));
    }

    /// <summary>
    /// A cell that spans over the room between two columns or two rows covers exactly that room and no
    /// other: it reaches from the edge of the first track it stands in to the edge of the last, so it
    /// grows by the distance between them on its own axis only (CSS 2.1 §17.5.0.1). Read on both axes,
    /// the single number <c>border-spacing</c> is written with gives a cell across two columns a width no
    /// row of the table occupies - which is what a page stating <c>0 2px</c> exposes, since its columns
    /// have no room between them while its rows have two pixels.
    /// </summary>
    [Test]
    public async Task ASpanningCellReachesFromFirstTrackToLast()
    {
        // Two cells side by side give the width of both columns together with the room between them, and
        // two cells above each other give the height of both rows with theirs - the rulers the spanning
        // cells are measured against on their own axes.
        foreach (var spacing in new[] { "0 2px", "2px 0" })
        {
            using var view = CreateView(400);
            await view.SetHtml(
                "<html><body style='margin:0'>" +
                $"<table style='border-collapse:separate;border-spacing:{spacing}'>" +
                "<tr><td id='a'>A</td><td id='b'>B</td></tr>" +
                "<tr><td id='across' colspan='2'>C</td></tr>" +
                "<tr><td id='down' rowspan='2'>D</td><td id='e'>E</td></tr>" +
                "<tr><td id='f'>F</td></tr></table>" +
                "</body></html>");

            var a = Find(view, "a");
            var b = Find(view, "b");
            var across = Find(view, "across");
            var down = Find(view, "down");
            var e = Find(view, "e");
            var f = Find(view, "f");

            Assert.Multiple(() =>
            {
                Assert.That(
                    across.BorderBox.Width,
                    Is.EqualTo(b.BorderBox.Right - a.BorderBox.X).Within(0.5),
                    $"{spacing}: the cell across both columns reaches the far edge of the second");
                Assert.That(
                    down.BorderBox.Height,
                    Is.EqualTo(f.BorderBox.Bottom - e.BorderBox.Y).Within(0.5),
                    $"{spacing}: and the cell down both rows reaches the bottom of the second");
            });
        }
    }

    /// <summary>
    /// The two numbers <c>border-spacing</c> is written with belong to different axes: the room stated for
    /// the rows separates rows and nothing else, so two columns written with no room between them still
    /// stand side by side while the second row sits two pixels below the first.
    /// </summary>
    [Test]
    public async Task EachSpacingNumberSeparatesItsOwnAxis()
    {
        using var view = CreateView(400);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<table style='border-collapse:separate;border-spacing:0 2px'>" +
            "<tr><td id='a'>A</td><td id='b'>B</td></tr>" +
            "<tr><td id='c'>C</td><td id='d'>D</td></tr>" +
            "</table></body></html>");

        var a = Find(view, "a");
        var b = Find(view, "b");
        var c = Find(view, "c");

        Assert.Multiple(() =>
        {
            Assert.That(b.BorderBox.X - a.BorderBox.Right, Is.EqualTo(0).Within(0.5), "no room between columns");
            Assert.That(c.BorderBox.Y - a.BorderBox.Bottom, Is.EqualTo(2).Within(0.5), "two pixels between rows");
        });
    }

    [Test]
    public async Task ACellWithoutARowGetsAnAnonymousRow()
    {
        using var view = CreateView(400);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<div style='display:table'>" +
            "<div id='a' style='display:table-cell'>A</div>" +
            "</div>" +
            "</body></html>");

        var a = Find(view, "a");

        Assert.That(a.Kind, Is.EqualTo(LayoutKind.TableCell));
        Assert.That(a.BorderBox.Width, Is.EqualTo(5).Within(0.5), "the anonymous row still sizes the cell");
        Assert.That(a.BorderBox.Height, Is.EqualTo(10).Within(0.5));
    }

    /// <summary>
    /// A table part that stands `relative` is displaced the way a block is, and carries its rows and
    /// their content with it (CSS Positioned 3 §6.2 treats a table part as the block box the property
    /// applies to). The borrowed suite checks this by leaving a marker where the part would otherwise
    /// cover it: unmoved, the marker shows through. The measure here is the same page written twice,
    /// the part standing and not standing, so the ruler is the place the grid gave the part itself
    /// rather than a number read off where the table happens to land.
    /// </summary>
    [Test]
    public async Task ARowGroupStandsOffThePlaceItsRowsWereLaidIn()
    {
        var still = await Head(Offset: "");
        var moved = await Head(Offset: "top:100px");

        Assert.Multiple(() =>
        {
            Assert.That(moved.Part - still.Part, Is.EqualTo(100).Within(0.5),
                "the group is drawn a hundred below the place its rows were laid in");
            Assert.That(moved.Cell - moved.Part, Is.EqualTo(still.Cell - still.Part).Within(0.5),
                "and the cell came along, rather than being left in the grid's place");
            Assert.That(moved.Next - still.Next, Is.EqualTo(0).Within(0.5),
                "while the group beside it, which states nothing, stays where the grid put it");
        });
    }

    /// <summary>
    /// The same two-group table, with what the row group, its cell and the group after it landed on.
    /// </summary>
    private static async Task<(Double Part, Double Cell, Double Next)> Head(String Offset)
    {
        using var view = CreateView(400);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<table style='border-collapse:collapse'>" +
            $"<thead id='head' style='position:relative;{Offset}'><tr><td id='a'>A</td></tr></thead>" +
            "<tbody id='body'><tr><td id='b'>B</td></tr></tbody>" +
            "</table>" +
            "</body></html>");

        return (Find(view, "head").BorderBox.Y, Find(view, "a").BorderBox.Y, Find(view, "body").BorderBox.Y);
    }

    /// <summary>
    /// A part's own height is what a percentage offset on it is a share of, so one written `100%` says
    /// nothing until the table states its height (CSS 2.1 §10.5, read the way the suite's
    /// `top: 100%` rows do): a table whose height the rows settled has no whole for the share to be a
    /// share <em>of</em>, and reaching all the way down the grid it grew to would move the row off the
    /// page.
    /// </summary>
    [Test]
    public async Task APartOffsetSaysNothingUntilTheTableStatesAHeight()
    {
        using var unstated = CreateView(400);
        await unstated.SetHtml(
            "<html><body style='margin:0'>" +
            "<table style='border-spacing:0'><tbody id='part' style='position:relative;top:100%'>" +
            "<tr><td id='a'>A</td></tr></tbody></table></body></html>");

        using var stated = CreateView(400);
        await stated.SetHtml(
            "<html><body style='margin:0'>" +
            "<table style='border-spacing:0;height:200px'>" +
            "<tbody id='part' style='position:relative;top:100%'><tr><td id='a'>A</td></tr></tbody>" +
            "</table></body></html>");

        var settled = Find(unstated, "part");
        var named = Find(stated, "part");

        Assert.Multiple(() =>
        {
            Assert.That(settled.BorderBox.Y, Is.EqualTo(0).Within(0.5),
                "the table that named no height leaves the share unresolved, so the part is not moved");
            Assert.That(named.BorderBox.Y, Is.GreaterThan(0),
                "the same offset resolves against the height the table stated");
        });
    }

    [Test]
    public async Task AColumnSpanningCellStretchesItsColumns()
    {
        using var view = CreateView(400);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<table style='border-collapse:separate;border-spacing:0'>" +
            "<tr><td id='a'>A</td><td id='b'>B</td></tr>" +
            "<tr><td id='c' colspan='2'>A wide cell</td></tr>" +
            "</table>" +
            "</body></html>");

        // The spanning cell wants eleven characters across two columns, so each column has to grow far
        // past the single character the cell above it asked for.
        var a = Find(view, "a");
        var b = Find(view, "b");
        var c = Find(view, "c");

        Assert.That(a.BorderBox.Width, Is.GreaterThan(20), "the span pushed the first column wider");
        Assert.That(c.BorderBox.Width, Is.AtLeast(55).Within(1), "the spanning cell gets the width it wants");
        Assert.That(c.BorderBox.Width, Is.EqualTo(a.BorderBox.Width + b.BorderBox.Width).Within(1));
        Assert.That(c.BorderBox.Y, Is.GreaterThan(a.BorderBox.Y), "the third cell is in the second row");
    }

    [Test]
    public async Task AColumnWithMoreContentIsWider()
    {
        using var view = CreateView(400);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<table style='border-collapse:separate;border-spacing:0'>" +
            "<tr><td id='a'>A</td><td id='b'>A longer cell</td></tr>" +
            "</table>" +
            "</body></html>");

        Assert.That(Find(view, "b").BorderBox.Width, Is.GreaterThan(Find(view, "a").BorderBox.Width));
    }

    [Test]
    public async Task FixedLayoutKeepsTheDeclaredColumnWidths()
    {
        using var view = CreateView(400);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<table id='t' style='table-layout:fixed;width:200px;border-collapse:separate;border-spacing:0'>" +
            "<col style='width:50px'><col>" +
            "<tr><td id='a'>A</td><td id='b'>B</td></tr>" +
            "</table>" +
            "</body></html>");

        Assert.That(Find(view, "a").BorderBox.Width, Is.EqualTo(50).Within(0.5), "the column declaration is kept");
        Assert.That(Find(view, "b").BorderBox.Width, Is.EqualTo(150).Within(0.5), "the rest is shared out");
    }

    [Test]
    public async Task ABottomCaptionIsPlacedAfterTheRows()
    {
        using var view = CreateView(400);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<table style='border-collapse:separate;border-spacing:0'>" +
            "<caption id='cap' style='caption-side:bottom'>The caption</caption>" +
            "<tr><td id='a'>A</td></tr>" +
            "</table>" +
            "</body></html>");

        var caption = Find(view, "cap");
        var cell = Find(view, "a");

        Assert.That(caption.BorderBox.Y, Is.GreaterThanOrEqualTo(cell.BorderBox.Bottom - 0.5), "the caption sits below the rows");
    }

    [Test]
    public async Task EmptyCellsHideTheirBordersWhenTheTableAsksForIt()
    {
        using var view = CreateView(400);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<table style='border-collapse:separate;border-spacing:0;empty-cells:hide'>" +
            "<tr><td id='a' style='border:2px solid #000'></td><td id='b'>B</td></tr>" +
            "</table>" +
            "</body></html>");

        var empty = Find(view, "a");

        Assert.That(empty.SuppressBorder, Is.True);
        Assert.That(empty.SuppressBackground, Is.True);
        Assert.That(Find(view, "b").SuppressBorder, Is.False, "a cell with content is not suppressed");
    }

    [Test]
    public async Task VerticalAlignMiddleCentresTheCellContent()
    {
        using var view = CreateView(400);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<table style='border-collapse:separate;border-spacing:0'>" +
            "<tr>" +
            "<td id='tall' style='height:60px'></td>" +
            "<td id='a' style='vertical-align:middle'>A</td>" +
            "</tr>" +
            "</table>" +
            "</body></html>");

        var cell = Find(view, "a");
        var line = cell.Children[0];

        Assert.That(cell.BorderBox.Height, Is.EqualTo(62).Within(1), "as tall as the tallest cell, whose 60 pixels sit inside a pixel of room");
        Assert.That(line.BorderBox.Y - cell.BorderBox.Y, Is.EqualTo(26).Within(0.5), "(60 - 10) / 2, plus the room above the content");
    }

    /// <summary>
    /// A cell centres its content on the place that content stood in the flow, not on the place a
    /// relative offset draws it: such an offset moves what is painted and leaves the room held where it
    /// was (CSS 2.1 §9.4.3), so a cell that took the moved place to work out the slack would shift the
    /// content away from the middle it was asked for and then move it again - charging the same
    /// displacement twice, and leaving the content half a move off where the alignment put it.
    /// </summary>
    [Test]
    public async Task ACellAlignsItsContentByThePlaceItStoodInTheFlow()
    {
        // The same cell written three times, the only difference being its own offset, in a row made a
        // hundred tall by the cell beside it. Measured first: the plain content stands twenty down - the
        // middle of the forty pixels the row leaves over its sixty - and each offset moves it exactly
        // what it states.
        var standing = await Content("");
        var up = await Content("top:-10px;");
        var down = await Content("top:10px;");

        Assert.Multiple(() =>
        {
            Assert.That(up - standing, Is.EqualTo(-10).Within(0.5),
                "a box moved ten up stands ten above where the alignment put it, not twenty");
            Assert.That(down - standing, Is.EqualTo(10).Within(0.5),
                "and the same holds for a box moved down");
            Assert.That(standing, Is.EqualTo(20).Within(0.5),
                "which is the middle of the room the row leaves, where the alignment alone puts it");
        });
    }

    /// <summary>
    /// Gets where the sixty-pixel box of a middle-aligned cell lands, given what the box itself writes
    /// for an offset. The room the row leaves does not depend on that: a relative box keeps the place it
    /// had, so the three readings differ by the offsets alone.
    /// </summary>
    private static async Task<Double> Content(String offset)
    {
        using var view = CreateView(400);

        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<table style='border-collapse:separate;border-spacing:0'><tr>" +
            "<td id='cell' style='padding:0;vertical-align:middle'>" +
            $"<div id='moved' style='position:relative;{offset}width:100px;height:60px'></div></td>" +
            "<td style='padding:0'><div style='width:20px;height:100px'></div></td>" +
            "</tr></table></body></html>");

        return Find(view, "moved").BorderBox.Y;
    }

    [Test]
    public async Task ARowsBorderMeetsTheLinesAboveAndBelowIt()
    {
        using var view = CreateView(400);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<table style='border-collapse:collapse'>" +
            "<tr style='border-top:2px solid;border-bottom:1px solid'><td id='a'>A</td></tr>" +
            "<tr style='border-bottom:3px solid'><td id='b'>B</td></tr>" +
            "</table></body></html>");

        Assert.That(Painted(view, "a").Widths.Top, Is.EqualTo(2).Within(0.01), "the top line is the table's edge, so the cell paints all of it");
        Assert.That(Painted(view, "a").Widths.Bottom, Is.EqualTo(0.5).Within(0.01), "the line between the rows is shared, so each cell paints half of it");
        Assert.That(Painted(view, "b").Widths.Top, Is.EqualTo(0.5).Within(0.01), "and the row below takes the other half");
        Assert.That(Painted(view, "b").Widths.Bottom, Is.EqualTo(3).Within(0.01), "the last line is the table's other edge");
    }

    [Test]
    public async Task AColumnsBorderMeetsTheLinesBesideItsCells()
    {
        using var view = CreateView(400);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<table style='border-collapse:collapse'>" +
            "<colgroup><col style='border-left:4px solid;border-right:2px solid'><col></colgroup>" +
            "<tr><td id='a'>A</td><td id='b'>B</td></tr>" +
            "</table></body></html>");

        Assert.That(Painted(view, "a").Widths.Left, Is.EqualTo(4).Within(0.01), "the column's border at the table's edge");
        Assert.That(Painted(view, "a").Widths.Right, Is.EqualTo(1).Within(0.01), "and half of the line it shares with the next column");
        Assert.That(Painted(view, "b").Widths.Left, Is.EqualTo(1).Within(0.01), "which the next column takes the other half of");
        Assert.That(Painted(view, "b").Widths.Right, Is.EqualTo(0).Within(0.01), "the last column declared nothing");
    }

    [Test]
    public async Task ACellWinsTheLineItSharesWithItsRow()
    {
        using var view = CreateView(400);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<table style='border-collapse:collapse'>" +
            "<tr style='border-top:2px solid'><td id='a' style='border-top:2px double'>A</td></tr>" +
            "</table></body></html>");

        Assert.That(Painted(view, "a").Styles.Top, Is.EqualTo(BorderLineStyle.Double), "the same width, but the cell's style is the more emphatic");
        Assert.That(Painted(view, "a").Widths.Top, Is.EqualTo(2).Within(0.01));
    }

    [Test]
    public async Task AHiddenTableBorderHidesTheTablesOwnEdge()
    {
        using var view = CreateView(400);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<table style='border-collapse:collapse;border-style:hidden'>" +
            "<tr style='border-bottom:2px solid'><td id='a'>A</td></tr>" +
            "<tr style='border-top:2px solid'><td id='b'>B</td></tr>" +
            "</table></body></html>");

        Assert.That(Painted(view, "a").Widths.Top, Is.EqualTo(0).Within(0.01), "hidden wins the table's own edge");
        Assert.That(Painted(view, "a").Widths.Bottom, Is.EqualTo(1).Within(0.01), "but the line between the rows is still drawn");
        Assert.That(Painted(view, "b").Widths.Bottom, Is.EqualTo(0).Within(0.01));
    }

    [Test]
    public async Task AShortRowIsFilledToTheGrid()
    {
        using var view = CreateView(400);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<table id='t'><tr><td>A</td><td>B</td><td>C</td></tr><tr id='r'><td>D</td></tr></table>" +
            "</body></html>");

        var cells = Walk(Find(view, "t")).Count(box => box.Kind == LayoutKind.TableCell);

        Assert.That(cells, Is.EqualTo(6), "the short row is filled to the three columns the grid has");
    }

    [Test]
    public async Task TheFirstHeaderIsRenderedFirstAndTheFirstFooterLast()
    {
        using var view = CreateView(400);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<table><tbody><tr id='body'><td>B</td></tr></tbody>" +
            "<thead><tr id='head'><td>H</td></tr></thead>" +
            "<tfoot><tr id='foot'><td>F</td></tr></tfoot></table>" +
            "</body></html>");

        Assert.That(Find(view, "head").BorderBox.Y, Is.LessThan(Find(view, "body").BorderBox.Y), "the header is drawn above the rows");
        Assert.That(Find(view, "foot").BorderBox.Y, Is.GreaterThan(Find(view, "body").BorderBox.Y), "and the footer below them");
    }

    [Test]
    public async Task AColumnGroupDrawsTheLinesAtItsEdges()
    {
        using var view = CreateView(400);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<style>.g { border-left: 2px solid; border-right: 2px solid }</style>" +
            "<table style='border-collapse:collapse'>" +
            "<colgroup class='g'><col></colgroup><colgroup><col></colgroup>" +
            "<tr><td id='a'>A</td><td id='b'>B</td></tr></table>" +
            "</body></html>");

        Assert.That(Painted(view, "a").Widths.Left, Is.EqualTo(2).Within(0.01), "the group's left edge is the table's own edge");
        Assert.That(Painted(view, "a").Widths.Right, Is.EqualTo(1).Within(0.01), "and its right edge is the line the columns share");
        Assert.That(Painted(view, "b").Widths.Left, Is.EqualTo(1).Within(0.01), "which the next column takes the other half of");
        Assert.That(Painted(view, "b").Widths.Right, Is.EqualTo(0).Within(0.01), "the second group declared nothing");
    }

    /// <summary>
    /// A table laid out fixed keeps the width it asked for even when its words are wider: the columns it
    /// declares divide that width, the rest are given what is left of it, and content that does not fit
    /// overflows the column it stands in rather than pushing the table out (CSS 2.1 §17.5.2.1). The
    /// automatic algorithm answers the other way, and the two must not be confused - the floor the auto
    /// one reads from its columns' minimums is what once made a fixed table grow to its words.
    /// </summary>
    [Test]
    public async Task AFixedTableKeepsTheWidthItDeclared()
    {
        using var fixedView = CreateView(400);
        await fixedView.SetHtml(
            "<html><body style='margin:0'>" +
            "<table id='t' style='table-layout:fixed;border-spacing:0;width:25px'>" +
            "<tr><td id='a'>PASSFAIL</td></tr></table>" +
            "</body></html>");

        using var autoView = CreateView(400);
        await autoView.SetHtml(
            "<html><body style='margin:0'>" +
            "<table id='t' style='table-layout:auto;border-spacing:0;width:25px'>" +
            "<tr><td id='a'>PASSFAIL</td></tr></table>" +
            "</body></html>");

        Assert.Multiple(() =>
        {
            Assert.That(
                Find(fixedView, "t").BorderBox.Width,
                Is.EqualTo(25).Within(0.5),
                "eight letters of five pixels each do not widen a fixed table past what it was given");
            Assert.That(
                Find(fixedView, "a").BorderBox.Width,
                Is.EqualTo(25).Within(0.5),
                "and its one column takes the whole of it");
            Assert.That(
                Find(autoView, "t").BorderBox.Width,
                Is.GreaterThan(25),
                "the same table laid out automatic is as wide as its column minimum, words included");
        });
    }

    private static BorderCommand Painted(HutuViewInternal view, String id)
    {
        var box = Find(view, id);

        return view.Paint!.Commands.OfType<BorderCommand>()
            .First(command => Math.Abs(command.Bounds.X - box.BorderBox.X) < 0.01 &&
                              Math.Abs(command.Bounds.Y - box.BorderBox.Y) < 0.01);
    }

    private static HutuViewInternal CreateView(Int32 viewportWidth)
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

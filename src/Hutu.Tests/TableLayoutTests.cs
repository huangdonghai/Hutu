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

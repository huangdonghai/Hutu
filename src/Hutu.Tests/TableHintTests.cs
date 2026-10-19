using AngleSharp.Css.Values;
using Hutu.Rendering.Layout;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Hutu.Tests;

/// <summary>
/// Covers the sizes HTML's obsolete attributes hint at: the room a cell keeps around its content, the
/// distance between a table's cells, the border a table wears, and the width and height of a table, a row
/// or a cell.
/// </summary>
/// <remarks>
/// Every number here was measured in Chrome first. The font is the fixed-width stand-in the table tests
/// use, so a character is five pixels wide and a line ten tall, and a cell of one character asks for
/// seven pixels across once its padding is counted.
/// </remarks>
[TestFixture]
public sealed class TableHintTests
{
    [Test]
    public async Task ACellKeepsAPixelOfRoomWhereItsTableSaysNothing()
    {
        using var view = await Render("<table><tr><td id='c'>A</td></tr></table>");

        Assert.That(Padding(view, "c").Left.Resolve(0), Is.EqualTo(1), "the room every browser's sheet gives a cell");
        Assert.That(Find(view, "c").BorderBox.Width, Is.EqualTo(7), "one character and the room at each side of it");
        Assert.That(Padding(view, "c").Top.Resolve(0), Is.EqualTo(1), "above the line as well as beside it");
    }

    [TestCase("4", 13.0)]
    [TestCase("0", 5.0)]
    [TestCase("abc", 5.0)]
    [TestCase("-3", 5.0)]
    [TestCase(" 6 ", 17.0)]
    [TestCase("10%", 25.0)]
    [TestCase("12.9", 29.0)]
    [TestCase("4.5", 13.0)]
    [TestCase("", 7.0)]
    public async Task TheRoomACellKeepsIsTheNumberItsTableNames(String value, Double width)
    {
        using var view = await Render("<table cellpadding='" + value + "'><tr><td id='c'>A</td></tr></table>");

        Assert.That(Find(view, "c").BorderBox.Width, Is.EqualTo(width), "the value read the way HTML reads one");
    }

    [Test]
    public async Task TheRoomIsTheCellsAndNotTheTables()
    {
        using var view = await Render(
            "<table id='t' cellpadding='4'><tr><td id='c'><div id='d'>A</div></td></tr></table>");

        Assert.That(Padding(view, "t").Left.Resolve(0), Is.EqualTo(0), "a table is not a cell");
        Assert.That(Padding(view, "d").Left.Resolve(0), Is.EqualTo(0), "and neither is a box inside one");
        Assert.That(Padding(view, "c").Left.Resolve(0), Is.EqualTo(4), "only the cell takes the room");
    }

    [Test]
    public async Task ANestedTableKeepsItsOwnRoom()
    {
        using var view = await Render(
            "<table cellpadding='0'><tr><td><table><tr><td id='c'>A</td></tr></table></td></tr></table>");

        Assert.That(Padding(view, "c").Left.Resolve(0), Is.EqualTo(1), "the inner table says nothing, so its cell keeps the sheet's pixel");
    }

    [Test]
    public async Task ARuleThePageWritesTakesTheRoomBack()
    {
        using var view = await Render(
            "<style>td { padding: 2px }</style><table cellpadding='4'><tr><td id='c'>A</td></tr></table>");

        Assert.That(Padding(view, "c").Left.Resolve(0), Is.EqualTo(2), "a declaration is the page's own word against the attribute's");
    }

    [Test]
    public async Task AStarRuleAlsoTakesTheRoomBack()
    {
        using var view = await Render(
            "<style>* { padding: 2px }</style><table cellpadding='4'><tr><td id='c'>A</td></tr></table>");

        Assert.That(Padding(view, "c").Left.Resolve(0), Is.EqualTo(2), "a hint is a declaration of no specificity, and any rule beats it");
    }

    [Test]
    public async Task TheRoomThePageWritesForOneSideLeavesTheOthers()
    {
        using var view = await Render(
            "<style>td { padding-left: 2px }</style><table cellpadding='4'><tr><td id='c'>A</td></tr></table>");

        Assert.That(Padding(view, "c").Left.Resolve(0), Is.EqualTo(2), "the page named this side");
        Assert.That(Padding(view, "c").Top.Resolve(0), Is.EqualTo(4), "and the hint the ones it did not");
    }

    [TestCase("5", 17.0)]
    [TestCase("0", 7.0)]
    [TestCase("x", 11.0)]
    [TestCase("10%", 11.0)]
    [TestCase("-2", 11.0)]
    [TestCase("", 11.0)]
    [TestCase("1e3", 9.0)]
    [TestCase("12.9", 32.0)]
    [TestCase("0.9", 8.0)]
    public async Task TheDistanceBetweenCellsIsTheNumberTheTableNames(String value, Double width)
    {
        using var view = await Render(
            "<table id='t' cellspacing='" + value + "'><tr><td id='c'>A</td></tr></table>");

        Assert.That(Find(view, "t").BorderBox.Width, Is.EqualTo(width), "a table of one cell, its sides as far apart as the attribute says");
    }

    [Test]
    public async Task ATablesOwnRuleTakesTheDistanceBack()
    {
        using var view = await Render(
            "<style>table { border-spacing: 1px }</style><table id='t' cellspacing='5'><tr><td id='c'>A</td></tr></table>");

        Assert.That(Table(view, "t").HorizontalSpacing, Is.EqualTo(1), "the page's declaration stands above the attribute");
    }

    [Test]
    public async Task ANestedTableKeepsItsOwnDistance()
    {
        using var view = await Render(
            "<table cellspacing='0'><tr><td><table id='t'><tr><td>A</td></tr></table></td></tr></table>");

        Assert.That(Table(view, "t").HorizontalSpacing, Is.EqualTo(2), "the inner table says nothing and keeps the sheet's distance");
    }

    [Test]
    public async Task ABorderRingsTheTableAndItsCells()
    {
        using var view = await Render("<table id='t' border='3'><tr><td id='c'>A</td></tr></table>");

        Assert.That(Find(view, "t").Source.Style.BorderTopWidth, Is.EqualTo(3), "the table wears the number the attribute holds");
        Assert.That(Find(view, "t").Source.Style.BorderTopStyle, Is.EqualTo("outset"), "raised on the page");
        Assert.That(Find(view, "c").Source.Style.BorderTopWidth, Is.EqualTo(1), "and every cell is ringed with one pixel whatever the table's thickness");
        Assert.That(Find(view, "c").Source.Style.BorderTopStyle, Is.EqualTo("inset"), "sunk into it");
        Assert.That(Find(view, "c").BorderBox.Width, Is.EqualTo(9), "one character, its room and its ring");
        Assert.That(Find(view, "t").BorderBox.Width, Is.EqualTo(19), "the cell, the distance around it and the table's own ring");
    }

    [TestCase("", 1.0)]
    [TestCase("abc", 1.0)]
    [TestCase("-2", 1.0)]
    [TestCase("0", 0.0)]
    [TestCase("00", 0.0)]
    [TestCase("0.5", 0.0)]
    [TestCase(" 0 ", 0.0)]
    [TestCase("3px", 3.0)]
    [TestCase("3em", 3.0)]
    [TestCase("3.7", 3.0)]
    [TestCase("1e3", 1.0)]
    [TestCase("+3", 3.0)]
    [TestCase("20", 20.0)]
    public async Task TheTablesRingIsAsThickAsTheValueBegins(String value, Double width)
    {
        using var view = await Render("<table id='t' border='" + value + "'><tr><td>A</td></tr></table>");

        Assert.That(Find(view, "t").Source.Style.BorderTopWidth, Is.EqualTo(width), "read from the front of the value");
        Assert.That(Find(view, "t").Source.Style.BorderTopStyle,
            Is.EqualTo(width > 0 ? "outset" : "none"), "and a ring of nothing is no ring");
    }

    [Test]
    public async Task APageRuleTakesTheTablesRingBack()
    {
        using var view = await Render(
            "<style>table { border: 1px solid rgb(255, 0, 0) }</style><table id='t' border='3'><tr><td id='c'>A</td></tr></table>");

        Assert.That(Find(view, "t").Source.Style.BorderTopWidth, Is.EqualTo(1), "the page's border");
        Assert.That(Find(view, "t").Source.Style.BorderTopStyle, Is.EqualTo("solid"), "in the page's style");
        Assert.That(Find(view, "c").Source.Style.BorderTopStyle, Is.EqualTo("inset"), "the cells keep the ring the attribute gives them");
    }

    [Test]
    public async Task TheFrameNamesTheTablesOwnSides()
    {
        using var view = await Render(
            "<table id='t' border='3' frame='above'><tr><td id='c'>A</td></tr></table>");

        Assert.That(Sides(view, "t"), Is.EqualTo("3px,outset;0px,hidden;0px,hidden;0px,hidden"), "the top side alone, raised in the border's own width");
        Assert.That(Sides(view, "c"), Is.EqualTo("1px,inset;1px,inset;1px,inset;1px,inset"), "and the cells keep the ring the border attribute gives them");
    }

    [Test]
    public async Task TheFrameDrawsEvenWhereTheBorderIsNoWidth()
    {
        using var view = await Render("<table id='t' frame='box'><tr><td id='c'>A</td></tr></table>");

        Assert.That(Sides(view, "t"), Is.EqualTo("3px,outset;3px,outset;3px,outset;3px,outset"), "a frame of no border draws the width a border begins with");
        Assert.That(Sides(view, "c"), Is.EqualTo("0px,none;0px,none;0px,none;0px,none"), "and it draws no ring around the cells");
    }

    [TestCase("void", "0px,hidden;0px,hidden;0px,hidden;0px,hidden")]
    [TestCase("below", "0px,hidden;0px,hidden;1px,outset;0px,hidden")]
    [TestCase("hsides", "1px,outset;0px,hidden;1px,outset;0px,hidden")]
    [TestCase("vsides", "0px,hidden;1px,outset;0px,hidden;1px,outset")]
    [TestCase("lhs", "0px,hidden;0px,hidden;0px,hidden;1px,outset")]
    [TestCase("rhs", "0px,hidden;1px,outset;0px,hidden;0px,hidden")]
    [TestCase("border", "1px,outset;1px,outset;1px,outset;1px,outset")]
    [TestCase("x", "1px,outset;1px,outset;1px,outset;1px,outset")]
    public async Task TheFrameReadsTheValuesHtmlGivesIt(String value, String sides)
    {
        using var view = await Render("<table id='t' border='1' frame='" + value + "'><tr><td>A</td></tr></table>");

        Assert.That(Sides(view, "t"), Is.EqualTo(sides), "a value that is no frame leaves the border attribute to answer");
    }

    [Test]
    public async Task TheRulesNameTheCellsOwnSides()
    {
        using var view = await Render(
            "<table id='t' border='1' rules='cols'><tr><td id='c'>A</td></tr></table>");

        Assert.That(Sides(view, "c"), Is.EqualTo("0px,none;1px,solid;0px,none;1px,solid"), "the lines between columns, and no lines above or below");
        Assert.That(Sides(view, "t"), Is.EqualTo("1px,outset;1px,outset;1px,outset;1px,outset"), "the table keeps its own ring");
    }

    [Test]
    public async Task TheRulesDrawEvenWhereTheBorderIsNoWidth()
    {
        using var view = await Render("<table id='t' rules='all'><tr><td id='c'>A</td></tr></table>");

        Assert.That(Sides(view, "c"), Is.EqualTo("1px,solid;1px,solid;1px,solid;1px,solid"), "the rules are borders of their own");
        Assert.That(Sides(view, "t"), Is.EqualTo("0px,hidden;0px,hidden;0px,hidden;0px,hidden"), "and the rules hide the table's own edges");
    }

    [TestCase("none", "0px,none;0px,none;0px,none;0px,none")]
    [TestCase("groups", "0px,none;0px,none;0px,none;0px,none")]
    [TestCase("rows", "0px,none;0px,none;0px,none;0px,none")]
    [TestCase("x", "1px,inset;1px,inset;1px,inset;1px,inset")]
    public async Task TheRulesReadTheValuesHtmlGivesThem(String value, String sides)
    {
        using var view = await Render("<table border='1' rules='" + value + "'><tr><td id='c'>A</td></tr></table>");

        Assert.That(Sides(view, "c"), Is.EqualTo(sides), "groups draws nothing, measured, and a value that is no rule leaves the border attribute to answer");
    }

    [Test]
    public async Task TheRulesDrawTheirTableCollapsed()
    {
        using var view = await Render("<table id='t' rules='none'><tr><td>A</td></tr></table>");

        Assert.That(Table(view, "t").Collapse, Is.True, "the standard's sheet collapses every rules table");

        using var separated = await Render("<table id='t'><tr><td>A</td></tr></table>");

        Assert.That(Table(separated, "t").Collapse, Is.False, "a table without rules keeps its own model");
    }

    [Test]
    public async Task ARowDrawsTheLinesOfRulesRows()
    {
        using var view = await Render(
            "<table rules='rows'><tr id='r'><td id='c'>A</td></tr><tr><td>B</td></tr></table>");

        Assert.That(Sides(view, "r"), Is.EqualTo("1px,solid;0px,none;1px,solid;0px,none"), "the rows carry the horizontal lines");
        Assert.That(Sides(view, "c"), Is.EqualTo("0px,none;0px,none;0px,none;0px,none"), "and the cells none of them");
    }

    [Test]
    public async Task ARowGroupDrawsTheLinesOfRulesGroups()
    {
        using var view = await Render(
            "<table rules='groups'><thead id='g'><tr id='r'><td>A</td></tr></thead>" +
            "<tbody><tr><td>B</td></tr></tbody></table>");

        Assert.That(Sides(view, "r"), Is.EqualTo("0px,none;0px,none;0px,none;0px,none"), "a row draws nothing for groups");
        Assert.That(Sides(view, "g"), Is.EqualTo("1px,solid;0px,none;1px,solid;0px,none"), "the group draws the lines at its own edges");
    }

    [Test]
    public async Task ABordercolorIsTheColourOfTheTablesRingAndItsCells()
    {
        using var view = await Render(
            "<table id='t' border='1' bordercolor='green'><tr><td id='c'>A</td></tr></table>");

        Assert.That(Colour(Find(view, "t").Source.Style.BorderTopColor), Is.EqualTo((0x00, 0x80, 0x00)), "the table's ring");
        Assert.That(Colour(Find(view, "c").Source.Style.BorderTopColor), Is.EqualTo((0x00, 0x80, 0x00)), "and the ring of every cell in it");
    }

    [Test]
    public async Task APageRuleTakesTheCellsRingBack()
    {
        using var view = await Render(
            "<style>td { border: 2px solid rgb(255, 0, 255) }</style><table id='t' border='3'><tr><td id='c'>A</td></tr></table>");

        Assert.That(Find(view, "c").Source.Style.BorderTopWidth, Is.EqualTo(2), "the page named the cell's ring");
        Assert.That(Find(view, "c").Source.Style.BorderTopStyle, Is.EqualTo("solid"), "in the page's style");
    }

    [Test]
    public async Task ATablesWidthAttributeSizesTheTable()
    {
        using var view = await Render("<table id='t' width='300' cellpadding='0'><tr><td id='c'>A</td></tr></table>");

        Assert.That(Find(view, "t").BorderBox.Width, Is.EqualTo(300), "the box the table takes");
        Assert.That(Find(view, "c").BorderBox.Width, Is.EqualTo(296), "and the column fills it, the distance at each side taken out");
    }

    [Test]
    public async Task ACellsWidthIsALeastForItsColumn()
    {
        using var view = await Render("<table cellpadding='0'><tr><td id='c' width='50'>A</td></tr></table>");

        Assert.That(Find(view, "c").BorderBox.Width, Is.EqualTo(50), "the column is the width the cell asked for");

        using var narrow = await Render("<table cellpadding='0'><tr><td id='c' width='5'>wwwwwwwwww</td></tr></table>");

        Assert.That(Find(narrow, "c").BorderBox.Width, Is.EqualTo(50), "and wider where its content wants more");
    }

    [Test]
    public async Task ACellsWidthLeavesTheOtherColumnTheRest()
    {
        using var view = await Render(
            "<table width='400' cellpadding='0'><tr><td id='a' width='300'>A</td><td id='b'>B</td></tr></table>");

        Assert.That(Find(view, "a").BorderBox.Width, Is.EqualTo(300), "the column that asked for a width keeps it");
        Assert.That(Find(view, "b").BorderBox.Width, Is.EqualTo(94), "and the other one takes the room the table has left, the distance at each column edge counted");
    }

    [Test]
    public async Task AWidthOnARowSaysNothing()
    {
        using var view = await Render("<table id='t' cellpadding='0'><tr width='100'><td id='c'>A</td></tr></table>");

        Assert.That(Find(view, "t").BorderBox.Width, Is.EqualTo(9), "a row takes its width from its columns, not the other way about");
    }

    [Test]
    public async Task AHeightOnATableIsALeast()
    {
        using var view = await Render("<table id='t' height='100' cellpadding='0'><tr><td id='c'></td></tr></table>");

        Assert.That(Find(view, "t").BorderBox.Height, Is.EqualTo(100), "the table is as tall as it was asked to be");
        Assert.That(Find(view, "c").BorderBox.Height, Is.EqualTo(96), "and its row stretches to fill it");

        using var tall = await Render(
            "<table id='t' height='1' cellpadding='0'><tr><td id='c'>wwwwwwwwwwwwwwwwwwwwwwwwwwwwwwwwwwwwwwww</td></tr></table>");

        Assert.That(Find(tall, "t").BorderBox.Height, Is.GreaterThan(1), "but content taller than the asking wins");
    }

    [Test]
    public async Task ACellsWidthSizesItsColumnAndNotItsOwnContent()
    {
        using var view = await Render(
            "<table cellpadding='0'><tr><td id='a' width='10'>aaaa</td></tr>" +
            "<tr><td width='60'></td></tr></table>");

        Assert.That(Find(view, "a").BorderBox.Width, Is.EqualTo(60), "the column takes the widest width its cells ask for");
        Assert.That(Find(view, "a").Children[0].BorderBox.Height, Is.EqualTo(11), "and the words are laid out at the column's width, so they fit on one line");
    }

    [Test]
    public async Task APercentageCellWidthIsAShareOfTheTable()
    {
        using var view = await Render(
            "<table width='200' cellpadding='0' cellspacing='0'><tr><td id='a' width='25%'>A</td><td id='b'>B</td></tr></table>");

        Assert.That(Find(view, "a").BorderBox.Width, Is.EqualTo(50), "a quarter of the room the columns share");
        Assert.That(Find(view, "b").BorderBox.Width, Is.EqualTo(150), "and the rest to the column that asked for nothing");
    }

    [Test]
    public async Task ACellHeightIsAFloorUnderItsRow()
    {
        using var view = await Render(
            "<table cellpadding='0'><tr><td id='a' height='15'></td></tr><tr><td id='b'>x</td></tr></table>");

        Assert.That(Find(view, "a").BorderBox.Height, Is.EqualTo(15), "the row is as tall as the cell asked to be");
        Assert.That(Find(view, "b").BorderBox.Height, Is.EqualTo(11), "and the row below keeps its own height");
    }

    [Test]
    public async Task ASurplusHeightGoesToTheRowsThatAskedForNone()
    {
        using var view = await Render(
            "<table id='t' height='100' cellpadding='0' cellspacing='0'>" +
            "<tr><td id='a' bgcolor='green'></td></tr>" +
            "<tr><td id='b' height='10' bgcolor='pink'></td></tr>" +
            "<tr><td id='c' bgcolor='blue'></td></tr></table>");

        Assert.That(Find(view, "a").BorderBox.Height, Is.EqualTo(45), "half of what is left over");
        Assert.That(Find(view, "b").BorderBox.Height, Is.EqualTo(10), "the row that asked for a height keeps it");
        Assert.That(Find(view, "c").BorderBox.Height, Is.EqualTo(45), "and the other row that asked for none takes the other half");
    }

    [Test]
    public async Task ASurplusHeightIsSharedOverEveryRowWhenEveryRowAsked()
    {
        using var view = await Render(
            "<table id='t' height='100' cellpadding='0' cellspacing='0'>" +
            "<tr><td height='10'></td></tr><tr><td height='10'></td></tr><tr><td height='10'></td></tr></table>");

        var rows = Walk(Find(view, "t")).Where(box => box.Kind == LayoutKind.TableRow).ToList();

        Assert.That(rows[0].BorderBox.Height, Is.EqualTo(100.0 / 3).Within(0.01), "the surplus is shared equally");
        Assert.That(rows[1].BorderBox.Height, Is.EqualTo(rows[0].BorderBox.Height).Within(0.01));
        Assert.That(rows[2].BorderBox.Height, Is.EqualTo(rows[0].BorderBox.Height).Within(0.01));
        Assert.That(rows[0].BorderBox.Y, Is.EqualTo(0).Within(0.01));
    }

    [Test]
    public async Task ASpanDoesNotMakeColumnsOfItsOwn()
    {
        using var view = await Render(
            "<table id='t' width='300' cellspacing='10'><tr><td id='a'>A</td></tr>" +
            "<tr><td id='b' colspan='2'>B</td></tr></table>");

        Assert.That(Find(view, "a").BorderBox.Width, Is.EqualTo(280), "a row of one cell settles one column, however far the other row's cell spans");
        Assert.That(Find(view, "b").BorderBox.Width, Is.EqualTo(280), "and the spanning cell takes it whole");
    }

    [Test]
    public async Task ASpanReachesAcrossTheColumnsItsRowMakes()
    {
        using var view = await Render(
            "<table id='t' width='300' cellspacing='10'><tr><td id='a' colspan='2'>A</td></tr>" +
            "<tr><td id='b'>B</td><td id='c'>C</td></tr></table>");

        Assert.That(Find(view, "a").BorderBox.Width, Is.EqualTo(280), "the span covers both columns");
        Assert.That(Find(view, "b").BorderBox.Width, Is.EqualTo(135), "and the row of two cells settles them equal");
    }

    [Test]
    public async Task AHeightOnARowIsALeast()
    {
        using var view = await Render("<table cellpadding='0'><tr height='40'><td id='c'>A</td></tr></table>");

        Assert.That(Find(view, "c").BorderBox.Height, Is.EqualTo(40), "the row is as tall as it was asked to be");
    }

    [Test]
    public async Task AWidthOnADivIsNotASize()
    {
        using var view = await Render("<div id='d' width='300' height='200'>A</div>");

        Assert.That(Find(view, "d").BorderBox.Width, Is.EqualTo(400), "the attributes size tables and their parts, not every box");
        Assert.That(Find(view, "d").BorderBox.Height, Is.EqualTo(10), "so the box is as tall as its line");
    }

    [Test]
    public async Task ARowsAlignmentIsHandedDownToItsCells()
    {
        using var view = await Render("<table><tr align='left'><th id='h'>Text</th></tr></table>");

        Assert.That(Find(view, "h").Source.Style.TextAlign, Is.EqualTo("left"), "the header takes the row's alignment");
    }

    [Test]
    public async Task AHeaderIsCentredWhereNothingSaysOtherwise()
    {
        using var view = await Render("<table><tr><th id='h'>Text</th></tr><tr><td id='c'>A</td></tr></table>");

        Assert.That(Find(view, "h").Source.Style.TextAlign, Is.EqualTo("center"), "the value a header begins with");
        Assert.That(Find(view, "c").Source.Style.TextAlign, Is.Not.EqualTo("center"), "and a cell begins at the start of its line");
    }

    [Test]
    public async Task AnAlignmentTheAttributeNamesLosesToThePagesOwnRule()
    {
        using var view = await Render(
            "<style>th { text-align: right }</style><table><tr align='left'><th id='h'>Text</th></tr></table>");

        Assert.That(Find(view, "h").Source.Style.TextAlign, Is.EqualTo("right"), "the page's declaration beats the hint");
    }

    [TestCase("center", "center")]
    [TestCase("absmiddle", "center")]
    [TestCase("justify", "justify")]
    [TestCase("bogus", "start")]
    public async Task ACellsAlignmentReadsTheValuesHtmlGives(String value, String expected)
    {
        using var view = await Render(
            "<table><tr><td id='c' align='" + value + "'>Text</td></tr></table>");

        Assert.That(Find(view, "c").Source.Style.TextAlign, Is.EqualTo(expected), "the value read as the standard reads it");
    }

    [TestCase("middle", "Middle")]
    [TestCase("bottom", "Bottom")]
    [TestCase("top", "Top")]
    public async Task ACellsVerticalAlignmentReadsValign(String value, String expected)
    {
        using var view = await Render(
            "<table height='100'><tr><td id='c' valign='" + value + "'>Text</td></tr></table>");

        Assert.That(Table(view, "c").VerticalAlign.ToString(), Is.EqualTo(expected), "the cell sits where the attribute says");
    }

    [Test]
    public async Task ATableAlignedLeftFloats()
    {
        using var view = await Render(
            "<table id='t' align='left' width='100'><tr><td>A</td></tr></table><p>words</p>");

        Assert.That(Find(view, "t").Source.Style.Float, Is.EqualTo(FloatSide.Left), "the standard's sheet floats a left-aligned table");
        Assert.That(Find(view, "t").BorderBox.X, Is.EqualTo(0), "and it stands at the start of the line");
    }

    [Test]
    public async Task ACentredTableTakesAutoMargins()
    {
        using var view = await Render(
            "<table id='t' align='center' width='100'><tr><td>A</td></tr></table>");

        Assert.That(Find(view, "t").BorderBox.X, Is.EqualTo(150), "a hundred pixels centred in the four hundred the body has");
        Assert.That(Find(view, "t").Source.Style.Margin.Left.IsAuto, Is.True, "with the share left to the margins");
    }

    [Test]
    public async Task ABodysBgcolorIsThePagesBackground()
    {
        using var view = await Render("<body id='b' bgcolor='green' style='margin:0'>A</body>");

        Assert.That(Colour(Background(view, "b")), Is.EqualTo((0x00, 0x80, 0x00)), "the colour the attribute names");
    }

    [Test]
    public async Task ATablesBgcolorIsTheTablesBackground()
    {
        using var view = await Render("<table id='t' bgcolor='blue'><tr><td id='c'>A</td></tr></table>");

        Assert.That(Colour(Background(view, "t")), Is.EqualTo((0x00, 0x00, 0xFF)), "the table is painted in it");
        Assert.That(Background(view, "c"), Is.Null, "and the cell is not: a colour is written on the element, not handed down");
    }

    [Test]
    public async Task ACellsBgcolorIsItsOwn()
    {
        using var view = await Render(
            "<table><tr id='r' bgcolor='yellow'><td id='c' bgcolor='blue'>A</td></tr></table>");

        Assert.That(Colour(Background(view, "r")), Is.EqualTo((0xFF, 0xFF, 0x00)), "the row is painted");
        Assert.That(Colour(Background(view, "c")), Is.EqualTo((0x00, 0x00, 0xFF)), "and the cell in the colour of its own attribute");
    }

    [Test]
    public async Task ABodysTextAttributeIsTheColourOfItsWords()
    {
        using var view = await Render("<body id='b' text='red' style='margin:0'><p id='p'>words</p></body>");

        Assert.That(Ink(view, "words"), Is.EqualTo(0xFFFF0000u), "the body's colour is inherited the ordinary way");
    }

    [Test]
    public async Task TheBodysTextColourLeavesALinkDrawnAsALink()
    {
        using var view = await Render(
            "<body style='margin:0' text='red'><a id='a' href='next.htm'>link</a></body>");

        Assert.That(Ink(view, "link"), Is.EqualTo(0xFF0000EEu), "a link the page gave no colour of its own keeps the engine's");
    }

    [Test]
    public async Task ARuleThePageWritesTakesTheColourBack()
    {
        using var view = await Render(
            "<style>* { background-color: #0000ff }</style><table id='t' bgcolor='green'><tr><td>A</td></tr></table>");

        Assert.That(Colour(Background(view, "t")), Is.EqualTo((0x00, 0x00, 0xFF)), "a rule of any specificity stands above the hint");
    }

    [Test]
    public async Task AColourAttributeOnAnElementItDoesNotBelongToSaysNothing()
    {
        using var view = await Render("<div id='d' bgcolor='green'>A</div>");

        Assert.That(Background(view, "d"), Is.Null, "bgcolor belongs to the body and the table family, and nowhere else");
    }

    private static async Task<HutuViewInternal> Render(String body)
    {
        var view = new HutuViewInternal(new FakeTextMeasurer());
        view.SetViewportSize(400, 600);
        await view.SetHtml("<html style='margin:0'><body style='margin:0'>" + body + "</body></html>");
        return view;
    }

    private static String Side(Double width, String style) => width + "px," + style;

    private static StyleThickness Padding(HutuViewInternal view, String id) => Find(view, id).Source.Style.Padding;

    private static TableStyle Table(HutuViewInternal view, String id) => Find(view, id).Source.Style.Table;

    private static String Sides(HutuViewInternal view, String id)
    {
        var style = Find(view, id).Source.Style;

        return Side(style.BorderTopWidth, style.BorderTopStyle) + ";" +
            Side(style.BorderRightWidth, style.BorderRightStyle) + ";" +
            Side(style.BorderBottomWidth, style.BorderBottomStyle) + ";" +
            Side(style.BorderLeftWidth, style.BorderLeftStyle);
    }

    internal static CssColorValue? Background(HutuViewInternal view, String id) =>
        Find(view, id).Source.Style.BackgroundColor;

    private static (Byte R, Byte G, Byte B) Colour(CssColorValue? colour) =>
        colour is { } value ? (value.R, value.G, value.B) : default;

    private static UInt32 Ink(HutuViewInternal view, String text) =>
        view.Paint!.Commands.OfType<Hutu.Rendering.Paint.TextCommand>().First(command => command.Text == text).Color;

    internal static LayoutBox Find(HutuViewInternal view, String id)
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

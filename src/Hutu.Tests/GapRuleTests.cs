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
/// Covers the decorations a container's gaps carry: the lists the three properties take and the way they
/// are given out to the gaps, and where the rules of a flex container's gaps stand (CSS Gaps 1 §3, §4).
/// </summary>
[TestFixture]
public sealed class GapRuleTests
{
    [Test]
    public void AListIsGivenOutFromItsStartAsManyTimesAsTheGapsNeed()
    {
        var widths = new GapRuleTerms<Double>(3.0, new[] { 2.0, 4.0 });

        Assert.Multiple(() =>
        {
            Assert.That(widths.At(0, 3), Is.EqualTo(2), "the first gap takes the first value");
            Assert.That(widths.At(1, 3), Is.EqualTo(4), "the second the second");
            Assert.That(widths.At(2, 3), Is.EqualTo(2), "and the third starts the list again (§4.6)");
        });
    }

    [Test]
    public void APropertyThePageWroteKeepsItsInitialValueForEveryGap()
    {
        var widths = new GapRuleTerms<Double>(3.0, null);

        Assert.That(widths.At(1, 4), Is.EqualTo(3), "medium, which is what the property starts at");
        Assert.That(widths.IsStated, Is.False);
    }

    [Test]
    public async Task AnIntegerRepeaterStandsForItsValuesWrittenOut()
    {
        using var view = await Render(
            "<style>#d { display:flex; flex-direction:column; row-rule-color: red, repeat(2, green, blue), gold;" +
            " row-rule-style: solid }</style><div id='d'>x</div>");

        var colors = Find(view, "d").Source.Style.Gap.Row;

        Assert.Multiple(() =>
        {
            Assert.That(colors.At(0, 6).Color, Is.EqualTo(0xffff0000u), "the value before the repeat");
            Assert.That(colors.At(1, 6).Color, Is.EqualTo(0xff008000u), "the first turn of it");
            Assert.That(colors.At(2, 6).Color, Is.EqualTo(0xff0000ffu));
            Assert.That(colors.At(3, 6).Color, Is.EqualTo(0xff008000u), "the second turn");
            Assert.That(colors.At(4, 6).Color, Is.EqualTo(0xff0000ffu));
            Assert.That(colors.At(5, 6).Color, Is.EqualTo(0xffffd700u), "and the value after it");
        });
    }

    /// <summary>
    /// An <c>auto</c> repeater takes the gaps no other value reaches - the first and last values keep the
    /// gaps they were written beside, however many gaps there turn out to be (CSS Gaps 1 §4.6).
    /// </summary>
    [Test]
    public async Task AnAutoRepeaterTakesTheGapsTheOthersDoNotReach()
    {
        using var view = await Render(
            "<style>#d { display:flex; flex-direction:column; row-rule-color: red, repeat(auto, green, blue), gold;" +
            " row-rule-style: solid }</style><div id='d'>x</div>");

        var colors = Find(view, "d").Source.Style.Gap.Row;

        Assert.Multiple(() =>
        {
            Assert.That(colors.At(0, 5).Color, Is.EqualTo(0xffff0000u), "the leading value");
            Assert.That(colors.At(1, 5).Color, Is.EqualTo(0xff008000u), "the repeater starts");
            Assert.That(colors.At(2, 5).Color, Is.EqualTo(0xff0000ffu));
            Assert.That(colors.At(3, 5).Color, Is.EqualTo(0xff008000u), "and turns again");
            Assert.That(colors.At(4, 5).Color, Is.EqualTo(0xffffd700u), "the trailing value keeps the last gap");
        });
    }

    [Test]
    public async Task AnAutoRepeaterAloneColoursEveryGap()
    {
        using var view = await Render(
            "<style>#d { display:flex; gap:20px; width:340px; column-rule-color: repeat(auto, red);" +
            " column-rule-style: solid; column-rule-width: 10px }" +
            ".i { width:100px; height:100px }</style>" +
            "<div id='d'><div class='i'></div><div class='i'></div><div class='i'></div></div>");

        var rules = Rules(view);

        Assert.That(rules, Has.Count.EqualTo(2), "one rule in each of the two gaps");
        Assert.That(rules.Select(rule => rule.LeftColor), Is.All.EqualTo(0xffff0000u));
    }

    /// <summary>
    /// The shorthand is expanded into its three longhands by the cascade, so a rule written whole reaches
    /// the axis it names - and the axis it does not name keeps the initials it was never told about.
    /// </summary>
    [Test]
    public async Task AShorthandReachesTheAxisItNames()
    {
        using var view = await Render(
            "<style>#d { display:flex; flex-direction:column; row-rule: 5px solid gold }</style>" +
            "<div id='d'><p>a</p></div>");

        var gap = Find(view, "d").Source.Style.Gap;
        var row = gap.Row.At(0, 1);

        Assert.Multiple(() =>
        {
            Assert.That(row.Width, Is.EqualTo(5), "the width the shorthand stated");
            Assert.That(row.Style, Is.EqualTo(BorderLineStyle.Solid), "its style");
            Assert.That(row.Color, Is.EqualTo(0xffffd700u), "and its colour");
            Assert.That(gap.Column.At(0, 1).Appears, Is.False, "the other axis was said nothing about");
        });
    }

    [Test]
    public async Task AListColoursTheGapsOfItsAxisInOrder()
    {
        using var view = await Render(
            "<style>#d { display:flex; gap:10px; column-rule-color: red, blue; column-rule-style: solid;" +
            " width:170px } .i { width:50px; height:50px }</style>" +
            "<div id='d'><div class='i'></div><div class='i'></div><div class='i'></div></div>");

        var column = Find(view, "d").Source.Style.Gap.Column;

        Assert.Multiple(() =>
        {
            Assert.That(column.At(0, 2).Color, Is.EqualTo(0xffff0000u), "the first gap");
            Assert.That(column.At(1, 2).Color, Is.EqualTo(0xff0000ffu), "the second");
        });
    }

    /// <summary>A rule with no colour stated is drawn in the text's own colour (§4.1).</summary>
    [Test]
    public async Task ARuleWithoutAColourIsDrawnInTheTextsOwnColour()
    {
        using var view = await Render(
            "<style>#d { display:flex; gap:10px; color: green; column-rule-style: solid; width:110px }" +
            ".i { width:50px; height:50px }</style>" +
            "<div id='d'><div class='i'></div><div class='i'></div></div>");

        var rule = Find(view, "d").Source.Style.Gap.Column.At(0, 1);

        Assert.Multiple(() =>
        {
            Assert.That(rule.Color, Is.EqualTo(0xff008000u), "currentcolor");
            Assert.That(rule.Width, Is.EqualTo(3), "and the medium width the page left standing");
        });
    }

    [Test]
    public async Task TheOverlapOrderThePageAsksForIsTheOnePainted()
    {
        using var view = await Render(
            "<style>#d { display:flex; rule-overlap: column-over-row; row-rule: 2px solid red }</style><div id='d'>x</div>");

        Assert.That(Find(view, "d").Source.Style.Gap.ColumnOverRow, Is.True);
    }

    /// <summary>
    /// A gap's rule stands in the middle of the gap and runs the line's whole cross stretch, while a gap
    /// between two lines runs the container's whole main stretch (CSS Gaps 1 §3.1, and the shapes of
    /// flex-gap-decorations-002 and -039).
    /// </summary>
    [Test]
    public async Task AGapRuleFillsTheGapItBelongsTo()
    {
        using var view = await Render(
            "<style>#d { display:flex; flex-wrap:wrap; gap:10px; width:110px; height:110px;" +
            " column-rule:10px solid blue; row-rule:6px solid red }" +
            ".i { width:50px; height:50px }</style>" +
            "<div id='d'><div class='i'></div><div class='i'></div>" +
            "<div class='i'></div><div class='i'></div></div>");

        var rules = Rules(view);

        Assert.That(rules, Has.Count.EqualTo(3), "one rule in each of the three gaps");
        Assert.Multiple(() =>
        {
            // Two lines of two items: the room between the items of each line is x 50..60, and each line
            // is fifty deep. The room between the lines is y 50..60, six of it taken by the rule.
            Assert.That(rules[0].Bounds.X, Is.EqualTo(50).Within(0.01), "the first line's column rule");
            Assert.That(rules[0].Bounds.Y, Is.EqualTo(0).Within(0.01), "stands across the line");
            Assert.That(rules[0].Bounds.Height, Is.EqualTo(50).Within(0.01));
            Assert.That(rules[1].Bounds.Y, Is.EqualTo(60).Within(0.01), "the second line's own");
            Assert.That(rules[2].Bounds.Y, Is.EqualTo(52).Within(0.01), "the row rule centred on its gap");
            Assert.That(rules[2].Bounds.Width, Is.EqualTo(110).Within(0.01), "and running the whole line");
            Assert.That(rules[2].Widths.Top, Is.EqualTo(6), "drawn as the side of the band it fills");
            Assert.That(rules[2].TopColor, Is.EqualTo(0xffff0000u));
        });
    }

    /// <summary>
    /// The space two boxes leave between them is a gap whoever made it, so a distribution rules the rooms
    /// it opened; the room left outside the first and last box is between nothing and stays plain (§3).
    /// </summary>
    [Test]
    public async Task AGapMadeByDistributingTheItemsIsRuled()
    {
        using var view = await Render(
            "<style>#d { display:flex; justify-content:space-evenly; width:110px;" +
            " column-rule:2px solid blue }.i { width:50px; height:50px }</style>" +
            "<div id='d'><div class='i'></div><div class='i'></div></div>");

        var rules = Rules(view);

        Assert.That(rules, Has.Count.EqualTo(1), "the space between the two items, not the two outside it");
        Assert.That(rules[0].Bounds.X, Is.EqualTo(54).Within(0.01), "centred on the gap it fills");
    }

    [Test]
    public async Task ARuleWiderThanItsGapRunsUnderTheBoxesBesideIt()
    {
        using var view = await Render(
            "<style>#d { display:flex; width:110px; column-rule:10px solid blue }" +
            ".i { width:50px; height:50px }</style>" +
            "<div id='d'><div class='i'></div><div class='i'></div></div>");

        var rules = Rules(view);

        Assert.That(rules, Has.Count.EqualTo(1));
        Assert.That(rules[0].Bounds.X, Is.EqualTo(45).Within(0.01), "ten across the edge the two share");
    }

    /// <summary>
    /// The decorations are given out from the side placement begins at, which a reversed direction puts at
    /// the other end of the container (§4.6).
    /// </summary>
    [Test]
    public async Task TheListIsGivenOutFromTheSidePlacementBeginsAt()
    {
        using var view = await Render(
            "<style>#d { display:flex; flex-direction:row-reverse; gap:10px; width:170px;" +
            " column-rule-color:red, blue; column-rule-style:solid }" +
            ".i { width:50px; height:50px }</style>" +
            "<div id='d'><div class='i'></div><div class='i'></div><div class='i'></div></div>");

        var rules = Rules(view);

        Assert.That(rules, Has.Count.EqualTo(2));
        Assert.Multiple(() =>
        {
            // The first gap placed is the one nearest the right edge, and it takes the first colour.
            Assert.That(rules[0].Bounds.X, Is.EqualTo(113.5).Within(0.5), "the first gap of the line");
            Assert.That(rules[0].LeftColor, Is.EqualTo(0xffff0000u), "in the first colour");
            Assert.That(rules[1].LeftColor, Is.EqualTo(0xff0000ffu), "and the next gap the next");
        });
    }

    /// <summary>
    /// A column container separates its items along the block axis, so the rules between them are row
    /// rules and run the line's cross stretch (flex-gap-decorations-058).
    /// </summary>
    [Test]
    public async Task TheRulesOfAColumnContainerLieBetweenItsItems()
    {
        using var view = await Render(
            "<style>#d { display:flex; flex-direction:column; gap:10px; width:50px; height:110px;" +
            " row-rule:10px solid gold }.i { width:50px; height:50px }</style>" +
            "<div id='d'><div class='i'></div><div class='i'></div></div>");

        var rules = Rules(view);

        Assert.That(rules, Has.Count.EqualTo(1));
        Assert.Multiple(() =>
        {
            Assert.That(rules[0].Bounds.Y, Is.EqualTo(50).Within(0.01));
            Assert.That(rules[0].Bounds.Width, Is.EqualTo(50).Within(0.01), "across the line");
            Assert.That(rules[0].Widths.Top, Is.EqualTo(10), "as a rule lying across the page");
        });
    }

    /// <summary>
    /// Where a row gap and a column gap cross, the row decoration stands above the column one unless the
    /// page turns them over (§3.5).
    /// </summary>
    [Test]
    public async Task RowDecorationsPaintAboveColumnOnes()
    {
        using var view = await Render(
            "<style>#d { display:flex; flex-wrap:wrap; gap:10px; width:110px; height:110px;" +
            " column-rule:10px solid blue; row-rule:6px solid red }" +
            ".i { width:50px; height:50px }</style>" +
            "<div id='d'><div class='i'></div><div class='i'></div>" +
            "<div class='i'></div><div class='i'></div></div>");

        var rules = Rules(view);

        Assert.That(rules[^1].Widths.Top, Is.GreaterThan(0), "laid down last, so on top");

        using var swapped = await Render(
            "<style>#d { display:flex; flex-wrap:wrap; gap:10px; width:110px; height:110px; rule-overlap:column-over-row;" +
            " column-rule:10px solid blue; row-rule:6px solid red }" +
            ".i { width:50px; height:50px }</style>" +
            "<div id='d'><div class='i'></div><div class='i'></div>" +
            "<div class='i'></div><div class='i'></div></div>");

        Assert.That(Rules(swapped)[0].Widths.Top, Is.GreaterThan(0), "and first when the page asks for the reverse");
    }

    [Test]
    public async Task ARuleIsNotDrawnForAGapThatHasNoStretch()
    {
        using var view = await Render(
            "<style>#d { display:flex; flex-wrap:wrap; gap:10px; width:110px; column-rule:10px solid blue }" +
            ".i { width:50px; height:0 }</style>" +
            "<div id='d'><div class='i'></div><div class='i'></div></div>");

        Assert.That(Rules(view), Is.Empty, "a line of no depth leaves the gap nothing to run along");
    }

    /// <summary>
    /// A grid's column gap is ruled the whole way across the tracks - not just where the items stand -
    /// and a grid whose items stand in one row has no crossing to break it at (CSS Gaps 1 §3.1).
    /// </summary>
    [Test]
    public async Task AGridColumnGapIsRuledAcrossTheTracks()
    {
        using var view = await Render(
            "<style>#d { display:grid; grid-template-columns:50px 50px; column-gap:10px; width:110px;" +
            " column-rule:4px solid blue } .i { height:40px }</style>" +
            "<div id='d'><div class='i'></div><div class='i'></div></div>");

        var rules = Rules(view);

        Assert.That(rules, Has.Count.EqualTo(1));
        Assert.Multiple(() =>
        {
            Assert.That(rules[0].Bounds.X, Is.EqualTo(53).Within(0.01), "centred in the ten-pixel gap");
            Assert.That(rules[0].Bounds.Y, Is.EqualTo(0).Within(0.01));
            Assert.That(rules[0].Bounds.Height, Is.EqualTo(40).Within(0.01), "as deep as the tracks reach");
            Assert.That(rules[0].Widths.Left, Is.EqualTo(4));
        });
    }

    [Test]
    public async Task AGridIsRuledAlongBothOfItsAxes()
    {
        using var view = await Render(
            "<style>#d { display:grid; grid-template-columns:50px 50px; grid-template-rows:40px 40px;" +
            " gap:10px; width:110px; height:90px; column-rule:4px solid blue; row-rule:4px solid red }</style>" +
            "<div id='d'><div></div><div></div><div></div><div></div></div>");

        var rules = Rules(view);

        Assert.That(rules, Has.Count.EqualTo(2), "one gap on each axis");
        Assert.Multiple(() =>
        {
            Assert.That(rules[0].Bounds.X, Is.EqualTo(53).Within(0.01), "the column rule");
            Assert.That(rules[0].Bounds.Height, Is.EqualTo(90).Within(0.01), "across both rows of tracks");
            Assert.That(rules[1].Bounds.Y, Is.EqualTo(43).Within(0.01), "the row rule");
            Assert.That(rules[1].Bounds.Width, Is.EqualTo(110).Within(0.01), "across both columns of tracks");
        });
    }

    /// <summary>
    /// An item that spans the gap stands in the room the decoration would cross, so under the grid's own
    /// <c>normal</c> the decoration starts again on the far side of it - and a page that asks for no
    /// breaks gets one rule the whole way, painted under the item (§3.1.2).
    /// </summary>
    [Test]
    public async Task AnItemAcrossTheGapEndsTheRuleUnderTheNormalBreak()
    {
        using var view = await Render(
            "<style>#d { display:grid; grid-template-columns:50px 50px; grid-template-rows:40px 40px; gap:10px;" +
            " width:110px; height:90px; column-rule:4px solid blue }" +
            ".a { grid-column:1 / 3 }</style>" +
            "<div id='d'><div class='a'></div><div></div><div></div></div>");

        var rules = Rules(view);

        Assert.That(rules, Has.Count.EqualTo(1));
        Assert.Multiple(() =>
        {
            Assert.That(rules[0].Bounds.Y, Is.EqualTo(50).Within(0.01), "only the room below the item is ruled");
            Assert.That(rules[0].Bounds.Height, Is.EqualTo(40).Within(0.01));
        });

        using var unbroken = await Render(
            "<style>#d { display:grid; grid-template-columns:50px 50px; grid-template-rows:40px 40px; gap:10px;" +
            " width:110px; height:90px; column-rule:4px solid blue; column-rule-break:none }" +
            ".a { grid-column:1 / 3 }</style>" +
            "<div id='d'><div class='a'></div><div></div><div></div></div>");

        var whole = Rules(unbroken);

        Assert.That(whole, Has.Count.EqualTo(1));
        Assert.That(whole[0].Bounds.Height, Is.EqualTo(90).Within(0.01), "one rule from end to end");
    }

    /// <summary>
    /// An <c>intersection</c> decoration starts and ends at every crossing, and the crossing only lets it
    /// through when items on either side span it - which a grid of separate cells never has (§3.2).
    /// </summary>
    [Test]
    public async Task AnIntersectionBreakSplitsTheRuleAtTheCrossingGap()
    {
        using var view = await Render(
            "<style>#d { display:grid; grid-template-columns:50px 50px; grid-template-rows:40px 40px; gap:10px;" +
            " width:110px; height:90px; column-rule:4px solid blue; column-rule-break:intersection }</style>" +
            "<div id='d'><div></div><div></div><div></div><div></div></div>");

        var rules = Rules(view);

        Assert.That(rules, Has.Count.EqualTo(2), "one stretch above the crossing and one below");
        Assert.Multiple(() =>
        {
            Assert.That(rules[0].Bounds.Y, Is.EqualTo(0).Within(0.01));
            Assert.That(rules[0].Bounds.Height, Is.EqualTo(40).Within(0.01));
            Assert.That(rules[1].Bounds.Y, Is.EqualTo(50).Within(0.01));
        });
    }

    /// <summary>
    /// A crossing only lets an <c>intersection</c> decoration through where items on both sides span it:
    /// a row the items leave open splits the rule at that crossing even when the row opposite spans it
    /// (grid-gap-decorations-007).
    /// </summary>
    [Test]
    public async Task AnIntersectionRowRuleStopsAtACrossingOnlyOneRowSpans()
    {
        using var view = await Render(
            "<style>#d { display:grid; grid-template-columns:50px 50px; grid-template-rows:40px 40px 40px; gap:10px;" +
            " width:110px; height:140px; row-rule:4px solid red; row-rule-break:intersection }" +
            " .wide { grid-column:1 / 3 }</style>" +
            "<div id='d'><div class='wide'></div><div></div><div></div><div class='wide'></div></div>");

        var rules = Rules(view);

        Assert.That(rules, Has.Count.EqualTo(4), "each crossing splits its row rule in two, whatever spans further down");
        Assert.Multiple(() =>
        {
            Assert.That(rules[0].Bounds.X, Is.EqualTo(0).Within(0.01));
            Assert.That(rules[0].Bounds.Width, Is.EqualTo(50).Within(0.01), "up to the column gap the row below leaves open");
            Assert.That(rules[1].Bounds.X, Is.EqualTo(60).Within(0.01), "and it resumes past the crossing");
        });
    }

    /// <summary>
    /// A row gap in a flex container is crossed by the column gaps of the two lines it sits between, and
    /// an <c>intersection</c> decoration is drawn in the stretches those crossings leave - the shape of
    /// flex-gap-decorations-050.
    /// </summary>
    [Test]
    public async Task AnIntersectingFlexRowRuleStopsAtTheColumnGapsOfBothLines()
    {
        using var view = await Render(
            "<style>#d { display:flex; flex-wrap:wrap; justify-content:space-between; width:400px;" +
            " row-gap:10px; row-rule:5px solid red; row-rule-break:intersection }" +
            ".i { width:70px; height:50px }</style>" +
            "<div id='d'>" + String.Concat(Enumerable.Repeat("<div class='i'></div>", 9)) + "</div>");

        var rules = Rules(view).Where(rule => rule.Bounds.Height <= 5.01).ToList();

        Assert.That(rules, Has.Count.EqualTo(6), "the five crossings leave six stretches of the row gap");
        Assert.Multiple(() =>
        {
            Assert.That(rules[0].Bounds.X, Is.EqualTo(0).Within(0.01));
            Assert.That(rules[0].Bounds.Width, Is.EqualTo(70).Within(0.01), "up to the first line's column gap");
            Assert.That(rules[1].Bounds.X, Is.EqualTo(110).Within(0.01), "past the second line's wider gap");
            Assert.That(rules[1].Bounds.Width, Is.EqualTo(42.5).Within(0.01));
            Assert.That(rules[^1].Bounds.Right, Is.EqualTo(400).Within(0.01), "and the last runs to the edge");
        });
    }

    /// <summary>
    /// An inset draws a decoration's ends back from where they would stand: a length from the cap, and a
    /// percentage of the crossing gap from a junction (CSS Gaps 1 §3.3).
    /// </summary>
    [Test]
    public async Task AnInsetDrawsTheEndsOfARuleBack()
    {
        using var view = await Render(
            "<style>#d { display:grid; grid-template-columns:50px 50px; grid-template-rows:40px 40px; gap:10px;" +
            " width:110px; height:90px; column-rule:4px solid blue; row-rule-inset-start:10px }</style>" +
            "<div id='d'><div></div><div></div><div></div><div></div></div>");

        Assert.That(Rules(view).Any(rule => rule.Bounds.Height <= 5.01), Is.False, "no row rule was asked for");

        using var inset = await Render(
            "<style>#d { display:grid; grid-template-columns:50px 50px; grid-template-rows:40px 40px; gap:10px;" +
            " width:110px; height:90px; column-rule:4px solid blue; column-rule-inset:10px }</style>" +
            "<div id='d'><div></div><div></div><div></div><div></div></div>");

        var rules = Rules(inset);

        Assert.That(rules, Has.Count.EqualTo(1));
        Assert.Multiple(() =>
        {
            Assert.That(rules[0].Bounds.Y, Is.EqualTo(10).Within(0.01), "ten in from the top edge");
            Assert.That(rules[0].Bounds.Bottom, Is.EqualTo(80).Within(0.01), "and ten in from the bottom");
        });
    }

    /// <summary>
    /// Where an end stands at a crossing, its percentage is a share of the crossing gap; where it stands at
    /// a bound there is no crossing, so the same percentage comes to nothing.
    /// </summary>
    [Test]
    public async Task AnInsetPercentageIsAShareOfTheCrossingGap()
    {
        using var view = await Render(
            "<style>#d { display:grid; grid-template-columns:50px 50px; grid-template-rows:40px 40px; gap:10px;" +
            " width:110px; height:90px; column-rule:4px solid blue; column-rule-break:intersection;" +
            " column-rule-inset-junction-start:50% }</style>" +
            "<div id='d'><div></div><div></div><div></div><div></div></div>");

        var rules = Rules(view);

        Assert.That(rules, Has.Count.EqualTo(2));
        Assert.Multiple(() =>
        {
            Assert.That(rules[0].Bounds.Y, Is.EqualTo(0).Within(0.01), "the cap at the top takes nothing");
            Assert.That(rules[1].Bounds.Y, Is.EqualTo(55).Within(0.01), "the junction starts half the gap in");
        });
    }

    /// <summary>
    /// <c>overlap-join</c> runs a junction end into the crossing by half the crossing gap plus half the
    /// crossing rule, so the two decorations that meet there join.
    /// </summary>
    [Test]
    public async Task OverlapJoinMakesTheDecorationsMeetAtACrossing()
    {
        using var view = await Render(
            "<style>#d { display:grid; grid-template-columns:50px 50px; grid-template-rows:40px 40px; gap:10px;" +
            " width:110px; height:90px; column-rule:4px solid blue; row-rule:4px solid red;" +
            " column-rule-break:intersection; rule-inset:overlap-join }</style>" +
            "<div id='d'><div></div><div></div><div></div><div></div></div>");

        var rules = Rules(view).Where(rule => rule.Widths.Left > 0).ToList();

        Assert.That(rules, Has.Count.EqualTo(2));
        Assert.Multiple(() =>
        {
            Assert.That(rules[0].Bounds.Bottom, Is.EqualTo(47).Within(0.01), "40 plus half the gap and half the rule");
            Assert.That(rules[1].Bounds.Y, Is.EqualTo(43).Within(0.01), "and the next piece runs back to meet it");
        });
    }

    /// <summary>
    /// An axis the page asked to show only between items draws nothing beside an empty column, while
    /// <c>all</c> and <c>around</c> still do (CSS Gaps 1 §3.4).
    /// </summary>
    [Test]
    public async Task AVisibilityThatAsksForBothSidesSkipsAnEmptyColumn()
    {
        const String grid =
            "<style>#d { display:grid; grid-template-columns:50px 50px 50px; gap:10px; width:170px;" +
            " column-rule:4px solid blue; rule-visibility-items:%s } .i { height:40px }</style>" +
            "<div id='d'><div class='i'></div><div class='i' style='grid-column:3'></div></div>";

        using var between = await Render(grid.Replace("%s", "between"));
        Assert.That(Rules(between), Is.Empty, "neither gap has an item on both sides");

        using var all = await Render(grid.Replace("%s", "all"));
        Assert.That(Rules(all), Has.Count.EqualTo(2), "every used gap is ruled");

        using var around = await Render(grid.Replace("%s", "around"));
        Assert.That(Rules(around), Has.Count.EqualTo(2), "each gap has an item on one side");
    }

    /// <summary>Gets the commands that draw one side of a band, which is every rule the engine paints.</summary>
    private static List<BorderCommand> Rules(HutuViewInternal view) =>
        view.Paint!.Commands.OfType<BorderCommand>()
            .Where(command => new[] { command.Widths.Top, command.Widths.Right, command.Widths.Bottom, command.Widths.Left }
                .Count(width => width > 0) == 1)
            .ToList();

    private static async Task<HutuViewInternal> Render(String content)
    {
        var view = new HutuViewInternal(new FakeTextMeasurer());
        view.SetViewportSize(400, 600);
        await view.SetHtml(
            "<html><head><style>div, p { margin: 0 }</style></head>" +
            "<body style='margin:0;font-size:10px;line-height:10px'>" + content + "</body></html>");
        return view;
    }

    private static LayoutBox Find(HutuViewInternal view, String id) =>
        Walk(view.Layout!.Root).First(box => box.Source.Element?.GetAttribute("id") == id);

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

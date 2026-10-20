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

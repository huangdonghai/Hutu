using Hutu.Rendering;
using Hutu.Rendering.Layout;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Hutu.Tests;

/// <summary>
/// Covers the multi-column longhands and the pseudo-algorithm that settles the used column count and
/// width (CSS Multicol 3 §3.4).
/// </summary>
[TestFixture]
public sealed class MulticolStyleTests
{
    [Test]
    public void OneColumnWhereNeitherIsDeclared()
    {
        var (count, width) = new MulticolStyle(null, null, 10, false).Used(300);

        Assert.That(count, Is.EqualTo(1), "a box that asks for no columns has one");
        Assert.That(width, Is.EqualTo(300), "and it is as wide as the room");
    }

    [Test]
    public void TheWidthSettlesTheCount()
    {
        var (count, width) = new MulticolStyle(null, 80, 10, false).Used(300);

        Assert.That(count, Is.EqualTo(3), "three eighty-pixel columns and two gaps fit in three hundred");
        Assert.That(width, Is.EqualTo(80), "and each stays the width it asked for");
    }

    [Test]
    public void TheCountSharesTheRoomOut()
    {
        var (count, width) = new MulticolStyle(4, null, 10, false).Used(300);

        Assert.That(count, Is.EqualTo(4));
        Assert.That(width, Is.EqualTo(67.5), "three gaps taken out of three hundred, shared over four");
    }

    [Test]
    public void BothAreTheNarrowerOfTheTwo()
    {
        var (count, width) = new MulticolStyle(4, 80, 10, false).Used(300);

        Assert.That(count, Is.EqualTo(3), "the room only fits three columns of the declared width");
        Assert.That(width, Is.EqualTo(280.0 / 3).Within(0.001), "and the three share the room rather than staying at eighty");
    }

    [Test]
    public void ADeclaredWidthTooWideForTwoKeepsOneColumn()
    {
        var (count, width) = new MulticolStyle(4, 80, 10, false).Used(100);

        Assert.That(count, Is.EqualTo(1), "eighty and its gap cannot be run twice in a hundred");
        Assert.That(width, Is.EqualTo(100), "so the one column takes the room");
    }

    [Test]
    public void ACountNarrowerThanTheDeclaredWidthIsKept()
    {
        var (count, width) = new MulticolStyle(6, 40, 0, false).Used(300);

        Assert.That(count, Is.EqualTo(6), "six columns of at least forty fit");
        Assert.That(width, Is.EqualTo(50), "and the room is shared over them");
    }

    [Test]
    public async Task TheLonghandsReachTheStyle()
    {
        using var view = await Render(
            "<style>div { columns: 3; column-gap: 20px; column-fill: auto }</style><div id='d'>x</div>");

        var style = Find(view, "d").Source.Style.Multi;

        Assert.That(style.IsMultiColumn, Is.True, "the columns shorthand is read");
        Assert.That(style.Count, Is.EqualTo(3));
        Assert.That(style.Gap, Is.EqualTo(20));
        Assert.That(style.Fills, Is.True);
    }

    [Test]
    public async Task AGapLeftAtNormalIsOneEm()
    {
        using var view = await Render(
            "<style>div { column-count: 2 }</style><div id='d'>x</div>");

        var style = Find(view, "d").Source.Style.Multi;

        Assert.That(style.Gap, Is.EqualTo(10), "normal is one em, and the fake face is set at ten pixels");
    }

    [Test]
    public async Task APageThatWritesNoGapAtAllStillHasOne()
    {
        using var view = await Render(
            "<style>div { column-count: 2; column-gap: 0 }</style><div id='d'>x</div>");

        Assert.That(Find(view, "d").Source.Style.Multi.Gap, Is.EqualTo(0), "a zero is a zero, not the one em of normal");
    }

    [Test]
    public async Task ABoxThatAsksForNoColumnsIsNotAContainer()
    {
        using var view = await Render("<div id='d'>x</div>");

        Assert.That(Find(view, "d").Source.Style.Multi.IsMultiColumn, Is.False);
    }

    /// <summary>
    /// A gap may carry a rule: drawn in the middle of the gap, from one content edge of the container to
    /// the other, taking no space of its own and appearing only between two columns that both have
    /// content (CSS Multicol 3 §4). Its stroke is the one a border side takes, so a dashed rule dashes
    /// down its length and a double one is two lines within its width. The three properties that say it
    /// belong to CSS Gaps 1 §4 now, so they are read for every container that has gaps and reached here
    /// through the same list the flex and grid gaps read.
    /// </summary>
    [Test]
    public async Task TheRuleShorthandReachesTheStyle()
    {
        using var view = await Render(
            "<style>div { columns: 3; column-gap: 20px; column-rule: 4px solid red }</style><div id='d'>x</div>");

        var rule = Find(view, "d").Source.Style.Gap.Column.At(0, 1);

        Assert.That(rule.Width, Is.EqualTo(4), "the shorthand's width");
        Assert.That(rule.Style, Is.EqualTo(BorderLineStyle.Solid), "and its style");
        Assert.That(rule.Color, Is.EqualTo(0xffff0000u), "and its colour");
        Assert.That(rule.Appears, Is.True);
    }

    [Test]
    public async Task ARuleWithoutAStyleDrawsNothing()
    {
        using var view = await Render(
            "<style>div { columns: 3; column-rule-width: 8px }</style><div id='d'>x</div>");

        var rule = Find(view, "d").Source.Style.Gap.Column.At(0, 1);

        Assert.That(rule.Width, Is.EqualTo(8), "the width stands on its own");
        Assert.That(rule.Style, Is.EqualTo(BorderLineStyle.None), "but the style is none until it is said");
        Assert.That(rule.Appears, Is.False);
    }

    [Test]
    public async Task ARuleStandsInTheMiddleOfEachColumnGap()
    {
        using var view = await Render(
            "<style>#d { columns: 3; column-gap: 20px; column-rule: 4px solid red; width:200px;height:100px;" +
            "border:0;padding:0;margin:0 }</style>" +
            "<div id='d'><p style='margin:0'>one</p><p style='margin:0'>two</p><p style='margin:0'>three</p></div>");

        var rules = Rules(view);

        Assert.That(rules, Has.Count.EqualTo(2), "one rule in each of the two gaps");
        Assert.Multiple(() =>
        {
            // Three columns of 53.33 with 20-pixel gaps: the gap centres are 10 into each gap, and the
            // four-pixel rule straddles its centre.
            Assert.That(rules[0].Bounds.X, Is.EqualTo(53.333 + 10 - 2).Within(0.5), "the first rule");
            Assert.That(rules[1].Bounds.X, Is.EqualTo(2 * 53.333 + 20 + 10 - 2).Within(0.5), "the second");
            Assert.That(rules[0].Bounds.Width, Is.EqualTo(4).Within(0.01), "as wide as it asked");
            Assert.That(rules[0].Bounds.Height, Is.EqualTo(100).Within(0.5),
                "and from one content edge of the container to the other");
        });
    }

    /// <summary>
    /// A spanner cuts a multi-column container into the stretches its columns run through, and a column
    /// rule belongs to the one its own gap stands in - the container's whole content box would carry the
    /// rule down across the spanner and the stretch beyond it (CSS Multicol 3 §3.2, CSS Gaps 1 §3.4).
    /// </summary>
    [Test]
    public async Task ASpannerEndsTheRuleAboveIt()
    {
        // Two boxes, a spanner, two more: the runs are the first pair of boxes and the last pair, twenty
        // tall each, so the rule in the gap between the columns stands twice - once per run.
        const String Page =
            "<style>#d { columns:2; column-gap:20px; width:200px; padding:0; margin:0; border:0;" +
            " column-rule:4px solid red }</style>" +
            "<div id='d'>" +
            "<div style='height:20px'></div><div style='height:20px'></div>" +
            "{SPANNER}" +
            "<div style='height:20px'></div><div style='height:20px'></div></div>";

        using var cut = await Render(Page.Replace(
            "{SPANNER}", "<div style='column-span:all;height:20px'></div>"));
        using var whole = await Render(Page.Replace("{SPANNER}", ""));

        var rules = Rules(cut);

        Assert.Multiple(() =>
        {
            Assert.That(rules, Has.Count.EqualTo(2), "one rule in the gap of each run");
            Assert.That(rules[0].Bounds.Y, Is.EqualTo(0).Within(0.5), "the first stands in the run above the spanner");
            Assert.That(rules[0].Bounds.Height, Is.EqualTo(20).Within(0.5), "as long as that run and no longer");
            Assert.That(rules[1].Bounds.Y, Is.EqualTo(40).Within(0.5), "the next begins past the spanner");
            Assert.That(rules[1].Bounds.Height, Is.EqualTo(20).Within(0.5), "and reaches the foot of its own run");

            // The same page with the spanner taken out is one run of forty, which the rule fills: the
            // rule is not simply its run's height wherever the container has room, since one unbroken
            // run reaches the content box the columns were laid across.
            Assert.That(Rules(whole), Has.Count.EqualTo(1), "one run, one rule");
            Assert.That(Rules(whole)[0].Bounds.Height, Is.EqualTo(40).Within(0.5), "the whole stretch");
        });
    }

    /// <summary>
    /// The lines of inline content run through the same grid: down a column box, on into the next
    /// column, and then into the next row (CSS Multicol 2 §3). The last box keeps two lines, because a
    /// single line left alone at the end of a run is what widows forbid.
    /// </summary>
    [Test]
    public async Task LinesOfTextRunThroughTheColumnBoxes()
    {
        var lines = String.Join("<br>", Enumerable.Range(0, 9).Select(index => "a"));

        using var view = await Render(
            "<style>#d { border:0; padding:0; margin:0; width:130px; height:90px; column-count:2;" +
            " column-width:60px; column-height:40px; column-gap:10px; row-gap:10px; column-wrap:wrap }</style>" +
            "<div id='d'>" + lines + "</div>");

        var boxes = Find(view, "d").Children;

        Assert.That(boxes, Has.Count.EqualTo(9), "one box per line");
        Assert.Multiple(() =>
        {
            Assert.That(boxes[4].BorderBox.X, Is.EqualTo(70).Within(0.01), "the fifth line starts the second column");
            Assert.That(boxes[4].BorderBox.Y, Is.EqualTo(0).Within(0.01));
            Assert.That(boxes[7].BorderBox.X, Is.EqualTo(0).Within(0.01), "the eighth starts the next row");
            Assert.That(boxes[7].BorderBox.Y, Is.EqualTo(50).Within(0.01), "a column height and a row gap down");
            Assert.That(boxes[8].BorderBox.Y, Is.EqualTo(60).Within(0.01), "with the widow beside it");
        });
    }

    /// <summary>
    /// Content that forces more column breaks than the container has columns comes to the tallest
    /// stretch between two of them, rather than stretching the columns to hold everything.
    /// </summary>
    [Test]
    public async Task ForcedBreaksBeyondTheColumnCountDoNotStretchTheColumns()
    {
        using var view = await Render(
            "<style>#d { columns:2; column-gap:0; width:100px }</style>" +
            "<div id='d'>" +
            "<div style='height:100px'></div>" +
            "<div style='break-before:column; height:90px'></div>" +
            "<div style='break-before:column; height:10px'></div></div>");

        Assert.That(Find(view, "d").BorderBox.Height, Is.EqualTo(100).Within(0.01));
    }

    /// <summary>
    /// A container that states a column height and wraps its columns makes a grid of column boxes: the
    /// content runs down a box, then on into the next column, and a row of boxes further down the page
    /// (CSS Multicol 2 §3).
    /// </summary>
    [Test]
    public async Task ColumnsThatStateAHeightAndWrapFillARowOfBoxes()
    {
        using var view = await Render(
            "<style>#d { border:0; padding:0; margin:0; width:200px; height:130px; column-count:3;" +
            " column-width:60px; column-height:60px; column-gap:10px; row-gap:10px; column-wrap:wrap }" +
            "p { margin:0; height:60px }</style>" +
            "<div id='d'><p></p><p></p><p></p><p></p><p></p><p></p></div>");

        var boxes = Find(view, "d").Children;

        Assert.That(boxes, Has.Count.EqualTo(6));
        Assert.Multiple(() =>
        {
            Assert.That(boxes[0].BorderBox.X, Is.EqualTo(0).Within(0.01));
            Assert.That(boxes[2].BorderBox.X, Is.EqualTo(140).Within(0.01), "the third column of the first row");
            Assert.That(boxes[3].BorderBox.Y, Is.EqualTo(70).Within(0.01), "the fourth box starts the next row");
            Assert.That(boxes[3].BorderBox.X, Is.EqualTo(0).Within(0.01), "back at the first column");
            Assert.That(boxes[5].BorderBox.Y, Is.EqualTo(70).Within(0.01), "and the last box the same row");
        });
    }

    [Test]
    public async Task ARuleAppearsOnlyBetweenColumnsThatHaveContent()
    {
        using var view = await Render(
            "<style>#d { columns: 3; column-gap: 20px; column-fill: auto; column-rule: 4px solid red;" +
            " width:200px;height:100px;border:0;padding:0;margin:0 }" +
            "p { margin:0; height:60px }</style>" +
            "<div id='d'><p>one</p></div>");

        Assert.That(Rules(view), Is.Empty, "one filled column has no neighbour to draw between");

        // Each paragraph is taller than half the column, so the second one the first cannot hold goes
        // over into the next column - and the two of them share exactly one gap.
        using var two = await Render(
            "<style>#d { columns: 3; column-gap: 20px; column-fill: auto; column-rule: 4px solid red;" +
            " width:200px;height:100px;border:0;padding:0;margin:0 }" +
            "p { margin:0; height:60px }</style>" +
            "<div id='d'><p>one</p><p>two</p></div>");

        Assert.That(Rules(two), Has.Count.EqualTo(1), "two filled columns share one gap");
    }

    private static List<Hutu.Rendering.Paint.BorderCommand> Rules(HutuViewInternal view) =>
        view.Paint!.Commands.OfType<Hutu.Rendering.Paint.BorderCommand>()
            .Where(command => command.Widths.Left > 0 && command.Widths.Right == 0 &&
                command.Widths.Top == 0 && command.Widths.Bottom == 0)
            .ToList();

    private static async Task<HutuViewInternal> Render(String content)
    {
        var view = new HutuViewInternal(new FakeTextMeasurer());
        view.SetViewportSize(400, 600);
        await view.SetHtml(
            "<html><head><style>div { margin: 0 }</style></head>" +
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

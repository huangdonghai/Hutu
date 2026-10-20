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
    /// down its length and a double one is two lines within its width.
    /// </summary>
    [Test]
    public async Task TheRuleShorthandReachesTheStyle()
    {
        using var view = await Render(
            "<style>div { columns: 3; column-gap: 20px; column-rule: 4px solid red }</style><div id='d'>x</div>");

        var rule = Find(view, "d").Source.Style.Multi.Rule;

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

        var rule = Find(view, "d").Source.Style.Multi.Rule;

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

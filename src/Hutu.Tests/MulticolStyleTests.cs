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

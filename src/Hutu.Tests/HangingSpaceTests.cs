using Hutu.Rendering.Layout;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Hutu.Tests;

/// <summary>
/// Covers the white space a line trails into: <c>pre-wrap</c> keeps it but hangs it past the line's
/// end edge, so it is neither room the line was given nor ink the line draws, and
/// <c>break-spaces</c> keeps it taking room instead (CSS Text 3 §4.1.3).
/// </summary>
/// <remarks>
/// The fake measurer gives every character five pixels, so a trailing space is five pixels of the
/// twenty a four-character run measures to.
/// </remarks>
[TestFixture]
public sealed class HangingSpaceTests
{
    [Test]
    public async Task APreWrapLineDrawsTheWordButNotTheSpaceItTrailsInto()
    {
        using var view = await Render(
            "<style>div { white-space: pre-wrap; width: 100px }</style><div>ABCD </div>");

        var run = Text(view, "ABCD");

        Assert.That(run.BorderBox.Width, Is.EqualTo(20), "the four kept characters, not the trailing space");
    }

    [Test]
    public async Task TheHangingSpaceDoesNotMoveTheLineInEitherDirection()
    {
        using var view = await Render(
            "<style>div { white-space: pre-wrap; width: 100px }</style>" +
            "<div>AB <span id='a'>CD</span> EF</div>" +
            "<div>AB <span id='b'>CD</span> EF </div>");

        var kept = Find(view, "a").BorderBox;
        var trailed = Find(view, "b").BorderBox;

        Assert.That(trailed.X, Is.EqualTo(kept.X), "the line ends where it would have without the space");
        Assert.That(Text(view, " EF").BorderBox.Width, Is.EqualTo(15), "and the run that trails is drawn without it");
    }

    [Test]
    public async Task ARightToLeftLineHangsItsTrailingSpaceOffItsEndEdge()
    {
        // In a right-to-left line the end edge is the left one, so a trailing space that was counted
        // as room shifts every glyph on the line sideways by its own width. The two lines below are
        // the same text with and without the space, and Chrome puts their words in the same places.
        using var view = await Render(
            "<style>div { direction: rtl; white-space: pre-wrap; width: 100px }</style>" +
            "<div>AB <span id='a'>CD</span> EF</div>" +
            "<div>AB <span id='b'>CD</span> EF </div>");

        var kept = Find(view, "a").BorderBox;
        var trailed = Find(view, "b").BorderBox;

        Assert.That(trailed.X, Is.EqualTo(kept.X), "the words stand where the shorter line puts them");
        Assert.That(
            Texts(view).Where(box => box.Text == " EF").Select(box => Math.Round(box.BorderBox.X, 3)).Distinct().Count(),
            Is.EqualTo(1),
            "and the run that trails into the space begins at the same edge");
    }

    [Test]
    public async Task ABreakSpacesLineKeepsTheRoomItsTrailingSpaceTakes()
    {
        using var view = await Render(
            "<style>div { white-space: break-spaces; width: 100px }</style><div>ABCD </div>");

        Assert.That(
            Text(view, "ABCD ").BorderBox.Width,
            Is.EqualTo(25),
            "the space is drawn for and takes room to the line's end");
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

    private static LayoutBox Text(HutuViewInternal view, String text) =>
        Texts(view).First(box => box.Text == text);

    private static IEnumerable<LayoutBox> Texts(HutuViewInternal view) =>
        Walk(view.Layout!.Root).Where(box => box.Kind == LayoutKind.Text);

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

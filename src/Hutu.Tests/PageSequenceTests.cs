using AngleSharp.Css;
using Hutu.Rendering;
using Hutu.Rendering.Layout;
using NUnit.Framework;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace Hutu.Tests;

/// <summary>
/// Covers the cut of the tall layout into pages: where a page ends, which break a flow allows there,
/// and how nested blocks and line runs take part in it (CSS Break 3 §3, §4).
/// </summary>
[TestFixture]
public sealed class PageSequenceTests
{
    [Test]
    public async Task BlockChildrenAreSharedOverThePages()
    {
        using var view = await Print(
            "<style>@page { size: 100px; margin: 0 } body { margin: 0 } div { height: 40px }</style>" +
            "<div></div><div></div><div></div>");

        var pages = view.Pages!;

        Assert.That(pages.Count, Is.EqualTo(2));
        Assert.Multiple(() =>
        {
            Assert.That(pages.Pages[0].Content.Y, Is.EqualTo(0).Within(0.01));
            Assert.That(pages.Pages[0].Content.Height, Is.EqualTo(80).Within(0.01), "two boxes fit and the third does not");
            Assert.That(pages.Pages[1].Content.Y, Is.EqualTo(80).Within(0.01));
            Assert.That(pages.Pages[1].Content.Height, Is.EqualTo(40).Within(0.01));
        });
    }

    [Test]
    public async Task NestedBlockChildrenAreSharedToo()
    {
        using var view = await Print(
            "<style>@page { size: 100px; margin: 0 } body, section, div { margin: 0 }" +
            " section { padding: 0 } div { height: 40px }</style>" +
            "<section><div></div><div></div><div></div></section>");

        var pages = view.Pages!;

        Assert.That(pages.Count, Is.EqualTo(2), "a wrapper between body and the boxes is not a reason to keep it whole");
        Assert.That(pages.Pages[0].Content.Height, Is.EqualTo(80).Within(0.01));
    }

    [Test]
    public async Task ALineRunBreaksBetweenItsLines()
    {
        using var view = await Print(
            "<style>@page { size: 100px; margin: 0 } body, div { margin: 0 }" +
            " div { font-size: 10px; line-height: 20px; widows: 1 }</style>" +
            "<div>a<br>b<br>c<br>d<br>e<br>f</div>");

        var pages = view.Pages!;

        Assert.That(pages.Count, Is.EqualTo(2));
        Assert.Multiple(() =>
        {
            Assert.That(pages.Pages[0].Content.Height, Is.EqualTo(100).Within(0.01), "five lines fit");
            Assert.That(pages.Pages[1].Content.Y, Is.EqualTo(100).Within(0.01));
            Assert.That(pages.Pages[1].Content.Height, Is.EqualTo(20).Within(0.01), "the sixth line");
        });
    }

    [Test]
    public async Task WidowsMoveTheBreakBackToKeepTheirLinesTogether()
    {
        using var view = await Print(
            "<style>@page { size: 100px; margin: 0 } body, div { margin: 0 }" +
            " div { font-size: 10px; line-height: 20px; widows: 3 }</style>" +
            "<div>a<br>b<br>c<br>d<br>e<br>f<br>g</div>");

        var pages = view.Pages!;

        Assert.That(pages.Count, Is.EqualTo(2));
        Assert.Multiple(() =>
        {
            Assert.That(pages.Pages[0].Content.Height, Is.EqualTo(80).Within(0.01), "four lines, because two would strand the widows");
            Assert.That(pages.Pages[1].Content.Height, Is.EqualTo(60).Within(0.01), "three lines follow");
        });
    }

    [Test]
    public async Task AForcedBreakEndsThePageEvenWhereMoreWouldFit()
    {
        using var view = await Print(
            "<style>@page { size: 100px; margin: 0 } body, div { margin: 0 }" +
            " div { height: 40px } .break { break-before: page }</style>" +
            "<div></div><div class='break'></div><div></div>");

        var pages = view.Pages!;

        Assert.That(pages.Count, Is.EqualTo(2), "the break ends the first page and the rest still fits the second");
        Assert.Multiple(() =>
        {
            Assert.That(pages.Pages[0].Content.Height, Is.EqualTo(40).Within(0.01));
            Assert.That(pages.Pages[1].Content.Y, Is.EqualTo(40).Within(0.01));
            Assert.That(pages.Pages[1].Content.Height, Is.EqualTo(80).Within(0.01));
        });
    }

    [Test]
    public async Task ABreakAfterTheLastBoxMakesNoBlankPage()
    {
        using var view = await Print(
            "<style>@page { size: 100px; margin: 0 } body, div { margin: 0 }" +
            " div { height: 40px; break-after: page }</style><div></div>");

        Assert.That(view.Pages!.Count, Is.EqualTo(1));
    }

    [Test]
    public async Task AnAvoidedBoxMovesWholeToTheNextPage()
    {
        using var view = await Print(
            "<style>@page { size: 100px; margin: 0 } body, div { margin: 0 }" +
            " .a { height: 60px } .b { height: 60px; break-inside: avoid }</style>" +
            "<div class='a'></div><div class='b'></div>");

        var pages = view.Pages!;

        Assert.That(pages.Count, Is.EqualTo(2));
        Assert.That(pages.Pages[0].Content.Height, Is.EqualTo(60).Within(0.01), "the avoid box is not split over the first page");
        Assert.That(pages.Pages[1].Content.Y, Is.EqualTo(60).Within(0.01));
    }

    [Test]
    public async Task ABoxTallerThanThePageIsCutAtThePageEdge()
    {
        using var view = await Print(
            "<style>@page { size: 100px; margin: 0 } body, div { margin: 0 }" +
            " div { height: 250px; overflow: hidden }</style><div></div>");

        var pages = view.Pages!;

        Assert.That(pages.Count, Is.EqualTo(3), "the flow must advance even through a monolithic box");
        Assert.That(pages.Pages[0].Content.Height, Is.EqualTo(100).Within(0.01));
        Assert.That(pages.Pages[2].Content.Height, Is.EqualTo(50).Within(0.01));
    }

    [Test]
    public async Task ThePageMarginsMoveTheSliceIntoThePageArea()
    {
        using var view = await Print(
            "<style>@page { size: 120px; margin: 10px } body, div { margin: 0 }" +
            " div { height: 100px }</style><div></div>");

        var page = view.Pages!.Pages[0];

        Assert.Multiple(() =>
        {
            Assert.That(page.OriginX, Is.EqualTo(10).Within(0.01));
            Assert.That(page.OriginY, Is.EqualTo(10).Within(0.01));
            Assert.That(page.Geometry.ContentWidth, Is.EqualTo(100).Within(0.01));
            Assert.That(page.Geometry.ContentHeight, Is.EqualTo(100).Within(0.01));
        });
    }

    [Test]
    public async Task ScreenModeHasNoPages()
    {
        using var view = new HutuViewInternal(new FakeTextMeasurer());
        view.SetViewportSize(200, 100);
        await view.SetHtml("<style>@page { size: 50px }</style><div style='height:10px'></div>");

        Assert.That(view.Pages, Is.Null, "a screen is a scroll, not a page sequence");
    }

    [Test]
    public async Task ARootThatGeneratesNothingStillPrintsOnePage()
    {
        using var view = await Print(
            "<style>@page { size: 100px; margin: 0 }</style><html style='display:none'><body>never</body></html>");

        Assert.That(view.Paint, Is.Null, "there is no box to paint");
        Assert.That(view.Pages, Is.Not.Null, "but the sheet is still printed");
        Assert.That(view.Pages!.Count, Is.EqualTo(1));
        Assert.That(view.Pages.Pages[0].Geometry.Width, Is.EqualTo(100).Within(0.01));
    }

    [Test]
    public async Task TableRowsAreSharedOverThePages()
    {
        using var view = await Print(
            "<style>@page { size: 100px; margin: 0 } body { margin: 0 }" +
            " table { border-collapse: collapse; margin: 0 } td { height: 40px; padding: 0; border: 0 }</style>" +
            "<table><tbody><tr><td></td></tr><tr><td></td></tr><tr><td></td></tr></tbody></table>");

        var pages = view.Pages!;

        Assert.That(pages.Count, Is.EqualTo(2), "a page may end between two rows");
        Assert.That(pages.Pages[0].Content.Height, Is.EqualTo(80).Within(0.01));
        Assert.That(pages.Pages[1].Content.Height, Is.EqualTo(40).Within(0.01));
    }

    [Test]
    public async Task ATableCaptionStaysWithItsFirstRow()
    {
        using var view = await Print(
            "<style>@page { size: 100px; margin: 0 } body { margin: 0 }" +
            " table { border-collapse: collapse; margin: 0 } caption { height: 40px; margin: 0 }" +
            " td { height: 40px; padding: 0; border: 0 }</style>" +
            "<table><caption></caption><tbody><tr><td></td></tr><tr><td></td></tr>" +
            "<tr><td></td></tr></tbody></table>");

        var pages = view.Pages!;

        Assert.That(pages.Pages[0].Content.Height, Is.EqualTo(80).Within(0.01), "the caption and the first row stay together");
    }

    [Test]
    public async Task AChangeOfPageNameStartsANewPage()
    {
        using var view = await Print(
            "<style>@page { size: 100px; margin: 0 } @page foo { size: 100px; margin: 10px }" +
            " body, div { margin: 0 } div { height: 40px }</style>" +
            "<div></div><div style='page: foo'></div>");

        var pages = view.Pages!;

        Assert.That(pages.Count, Is.EqualTo(2), "the name change forces a page break");
        Assert.Multiple(() =>
        {
            Assert.That(pages.Pages[0].PageName, Is.Null);
            Assert.That(pages.Pages[0].Geometry.Margins.Top, Is.EqualTo(0).Within(0.01));
            Assert.That(pages.Pages[1].PageName, Is.EqualTo("foo"));
            Assert.That(pages.Pages[1].Content.Y, Is.EqualTo(40).Within(0.01));
            Assert.That(pages.Pages[1].Geometry.Margins.Top, Is.EqualTo(10).Within(0.01), "the named rule answers the page");
        });
    }

    [Test]
    public async Task APageNameTravelsDownToTheBoxesThatStateNone()
    {
        using var view = await Print(
            "<style>@page { size: 100px; margin: 0 } @page foo { size: 100px; margin: 10px }" +
            " body, section, div { margin: 0 } div { height: 40px }</style>" +
            "<section style='page: foo'><div></div><div></div></section>");

        var pages = view.Pages!;

        Assert.That(pages.Count, Is.EqualTo(1), "both boxes belong to the same named page");
        Assert.That(pages.Pages[0].PageName, Is.EqualTo("foo"));
        Assert.That(pages.Pages[0].Geometry.Margins.Top, Is.EqualTo(10).Within(0.01));
    }

    [Test]
    public async Task AColumnFlexBreaksBetweenItsItems()
    {
        using var view = await Print(
            "<style>@page { size: 100px; margin: 0 } body, div { margin: 0 }" +
            " .f { display: flex; flex-direction: column } .i { height: 40px }</style>" +
            "<div class='f'><div class='i'></div><div class='i'></div><div class='i'></div></div>");

        var pages = view.Pages!;

        Assert.That(pages.Count, Is.EqualTo(2), "a column flex breaks between items");
        Assert.That(pages.Pages[0].Content.Height, Is.EqualTo(80).Within(0.01));
        Assert.That(pages.Pages[1].Content.Height, Is.EqualTo(40).Within(0.01));
    }

    [Test]
    public async Task ARowFlexBreaksBetweenItsLines()
    {
        using var view = await Print(
            "<style>@page { size: 60px; margin: 0 } body, div { margin: 0 }" +
            " .f { display: flex; flex-wrap: wrap; width: 100px } .i { width: 100px; height: 40px }</style>" +
            "<div class='f'><div class='i'></div><div class='i'></div></div>");

        var pages = view.Pages!;

        Assert.That(pages.Count, Is.EqualTo(2), "a row flex breaks between its lines");
        Assert.That(pages.Pages[0].Content.Height, Is.EqualTo(40).Within(0.01));
        Assert.That(pages.Pages[1].Content.Y, Is.EqualTo(40).Within(0.01));
    }

    [Test]
    public async Task AZeroHeightBoxAfterAForcedBreakStillHasItsPage()
    {
        using var view = await Print(
            "<style>@page { size: 100px; margin: 0 } body, div { margin: 0 }</style>" +
            "<div style='height: 50px; break-after: page'></div><div></div>");

        Assert.That(view.Pages!.Count, Is.EqualTo(2), "the zero-height box is on the page after the break");
        Assert.That(view.Pages.Pages[1].Content.Height, Is.EqualTo(0).Within(0.01));
    }

    [Test]
    public async Task TheFirstPageSelectorAnswersTheFirstPageOnly()
    {
        using var view = await Print(
            "<style>@page { size: 200px; margin: 0 } @page :first { margin-top: 5px }" +
            " body, div { margin: 0 } div { height: 100px }</style>" +
            "<div></div><div></div><div></div>");

        var pages = view.Pages!;

        Assert.That(pages.Pages[0].Geometry.Margins.Top, Is.EqualTo(5).Within(0.01));
        Assert.That(pages.Pages[1].Geometry.Margins.Top, Is.EqualTo(0).Within(0.01));
    }

    [Test]
    public async Task TheLeftAndRightPageSelectorsFollowThePageSide()
    {
        using var view = await Print(
            "<style>@page { size: 200px; margin: 0 } @page :right { margin-top: 3px }" +
            " @page :left { margin-top: 7px } body, div { margin: 0 } div { height: 100px }</style>" +
            "<div></div><div></div><div></div>");

        var pages = view.Pages!;

        Assert.That(pages.Count, Is.EqualTo(3));
        Assert.Multiple(() =>
        {
            Assert.That(pages.Pages[0].Geometry.Margins.Top, Is.EqualTo(3).Within(0.01), "the first page is a right page");
            Assert.That(pages.Pages[1].Geometry.Margins.Top, Is.EqualTo(7).Within(0.01), "the second is a left one");
            Assert.That(pages.Pages[2].Geometry.Margins.Top, Is.EqualTo(3).Within(0.01), "and the third a right one again");
        });
    }

    private static async Task<HutuViewInternal> Print(String html)
    {
        var view = new HutuViewInternal(new FakeTextMeasurer());
        view.SetViewportSize(800, 600);
        view.SetMediaType(DeviceCategory.Printer);
        await view.SetHtml(html);
        return view;
    }
}

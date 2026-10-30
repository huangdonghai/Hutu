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
    public async Task AnAvoidedBoxThatNoLongerFitsThePageMovesWholeToTheNextOne()
    {
        using var view = await Print(
            "<style>@page { size: 100px; margin: 0 } body, div { margin: 0 }" +
            " .t { font-size: 10px; line-height: 20px }" +
            " .b { break-inside: avoid } .b div { height: 45px }</style>" +
            "<div class='t'>x</div><div class='b'><div></div><div></div></div>");

        var pages = view.Pages!;

        Assert.That(pages.Count, Is.EqualTo(2));
        Assert.Multiple(() =>
        {
            Assert.That(pages.Pages[0].Content.Height, Is.EqualTo(20).Within(0.01), "the box does not start where it would not fit whole");
            Assert.That(pages.Pages[1].Content.Y, Is.EqualTo(20).Within(0.01));
            Assert.That(pages.Pages[1].Content.Height, Is.EqualTo(90).Within(0.01));
        });
    }

    [Test]
    public async Task AnAvoidedBoxTallerThanAPageIsCutOnlyWhereItsOwnAvoidsAllow()
    {
        using var view = await Print(
            "<style>@page { size: 100px; margin: 0 } body, div, p { margin: 0 }" +
            " .t { font-size: 10px; line-height: 10px }" +
            " .outer { break-inside: avoid } .outer > p { height: 50px }" +
            " .inner { break-inside: avoid } .inner > p { height: 50px }</style>" +
            "<div class='t'>x</div>" +
            "<div class='outer'><p></p><div class='inner'><p></p><p></p></div></div>");

        var pages = view.Pages!;

        Assert.That(pages.Count, Is.EqualTo(3), "the outer avoid cannot be kept, the inner one can");
        Assert.Multiple(() =>
        {
            Assert.That(pages.Pages[0].Content.Height, Is.EqualTo(10).Within(0.01), "the tall box starts a page of its own");
            Assert.That(pages.Pages[1].Content.Height, Is.EqualTo(50).Within(0.01), "the first child stands alone, the inner box whole after it");
            Assert.That(pages.Pages[2].Content.Y, Is.EqualTo(60).Within(0.01));
            Assert.That(pages.Pages[2].Content.Height, Is.EqualTo(100).Within(0.01));
        });
    }

    [Test]
    public async Task AHiddenOverflowBoxIsSharedOverThePagesLikeAnyBlock()
    {
        using var view = await Print(
            "<style>@page { size: 100px; margin: 0 } body, div { margin: 0 }" +
            " .clip { overflow: hidden } .clip div { height: 40px }</style>" +
            "<div class='clip'><div></div><div></div><div></div></div>");

        var pages = view.Pages!;

        Assert.That(pages.Count, Is.EqualTo(2), "hiding the overflow does not make the box one picture");
        Assert.That(pages.Pages[0].Content.Height, Is.EqualTo(80).Within(0.01), "two of the three boxes fit");
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

    [Test]
    public async Task ALeftBreakPrintsABlankPageWhenTheSideIsWrong()
    {
        using var view = await Print(
            "<style>@page { size: 200px; margin: 0 } @page :left { margin-top: 7px }" +
            " body, div { margin: 0 } div { height: 100px }</style>" +
            "<div></div><div></div><div></div><div style='break-before: left'></div>");

        var pages = view.Pages!;

        Assert.That(pages.Count, Is.EqualTo(4), "the third page is blank so the fourth is a left one");
        Assert.Multiple(() =>
        {
            Assert.That(pages.Pages[2].Blank, Is.True);
            Assert.That(pages.Pages[3].Blank, Is.False);
            Assert.That(pages.Pages[3].Geometry.Margins.Top, Is.EqualTo(7).Within(0.01), "the content page is a left page");
        });
    }

    [Test]
    public async Task APageRuleCanTurnTheSheet()
    {
        using var view = await Print(
            "<style>@page { size: 100px; margin: 0; page-orientation: rotate-left }</style>" +
            "body, div { margin: 0 } <div style='height: 40px'></div>");

        Assert.That(view.Pages!.Pages[0].Geometry.Orientation, Is.EqualTo(PageOrientation.RotateLeft));
    }

    [Test]
    public async Task AnAbsoluteBoxLeftAtItsPlaceCarriesItsPageNames()
    {
        using var view = await Print(
            "<style>@page { size: 200px; margin: 0 } body, div { margin: 0 }</style>" +
            "<div style='position:absolute'><div style='page:a'>a</div><div style='page:b'>b</div></div>");

        Assert.That(view.Pages!.Count, Is.EqualTo(2), "the name change inside the box still starts a page");
    }

    [Test]
    public async Task AnAbsoluteBoxPlacedByOffsetsDoesNotMoveThePages()
    {
        using var view = await Print(
            "<style>@page { size: 200px; margin: 0 } body, div { margin: 0 }</style>" +
            "<div style='position:absolute;left:0;top:0'><div style='page:a'>a</div><div style='page:b'>b</div></div>");

        Assert.That(view.Pages!.Count, Is.EqualTo(1), "a box with its own offsets is a picture, not a page break");
    }

    [Test]
    public async Task AColumnFlexTruncatesItsRowGapAtTheBreak()
    {
        using var view = await Print(
            "<style>@page { size: 100px; margin: 0 } body, div { margin: 0 }" +
            " .f { display: flex; flex-direction: column; row-gap: 20px } .i { height: 40px }</style>" +
            "<div class='f'><div class='i'></div><div class='i'></div><div class='i'></div></div>");

        var pages = view.Pages!;

        Assert.Multiple(() =>
        {
            Assert.That(pages.Pages[0].Content.Height, Is.EqualTo(100).Within(0.01), "the first item and the second fill it");
            Assert.That(pages.Pages[1].Content.Y, Is.EqualTo(120).Within(0.01), "the gap before the third is truncated");
            Assert.That(pages.Pages[1].Content.Height, Is.EqualTo(40).Within(0.01));
        });
    }

    [Test]
    public async Task AColumnFlexGapIsCutAtThePageEdgeWhenTheEdgeFallsInsideIt()
    {
        using var view = await Print(
            "<style>@page { size: 100px; margin: 0 } body, div { margin: 0 }" +
            " .f { display: flex; flex-direction: column; row-gap: 60px } .i { height: 60px }</style>" +
            "<div class='f'><div class='i'></div><div class='i'></div></div>");

        var pages = view.Pages!;

        Assert.That(pages.Count, Is.EqualTo(2));
        Assert.Multiple(() =>
        {
            Assert.That(pages.Pages[0].Content.Height, Is.EqualTo(100).Within(0.01), "the first item and part of the gap");
            Assert.That(pages.Pages[1].Content.Y, Is.EqualTo(100).Within(0.01), "the next page begins inside the gap");
            Assert.That(pages.Pages[1].Content.Height, Is.EqualTo(80).Within(0.01), "the rest of the gap and the second item");
        });
    }

    [Test]
    public async Task AGridBreaksBetweenItsRowsAndNotInsideOne()
    {
        using var view = await Print(
            "<style>@page { size: 100px; margin: 0 } body, div { margin: 0 }" +
            " .g { display: grid; grid-template-columns: 40px 40px; grid-auto-rows: 60px }</style>" +
            "<div class='g'><div></div><div></div><div></div><div></div></div>");

        var pages = view.Pages!;

        Assert.That(pages.Count, Is.EqualTo(2), "two rows of sixty in a hundred-pixel page");
        Assert.Multiple(() =>
        {
            Assert.That(pages.Pages[0].Content.Height, Is.EqualTo(60).Within(0.01), "the first row stands whole");
            Assert.That(pages.Pages[1].Content.Y, Is.EqualTo(60).Within(0.01));
            Assert.That(pages.Pages[1].Content.Height, Is.EqualTo(60).Within(0.01));
        });
    }

    [Test]
    public async Task AVerticalDocumentIsCutAlongItsColumns()
    {
        using var view = await Print(
            "<style>@page { size: 100px 50px; margin: 0 }" +
            " html { writing-mode: vertical-rl; font-size: 10px; line-height: 10px }" +
            " body, p { margin: 0 }</style>" +
            "<p>" + string.Join(" ", System.Linq.Enumerable.Repeat("abcd", 60)) + "</p>");

        var pages = view.Pages!;

        Assert.That(pages.Count, Is.GreaterThan(1), "the columns run past one page's width");
        Assert.Multiple(() =>
        {
            Assert.That(pages.Pages[0].Content.X, Is.GreaterThanOrEqualTo(0), "the first page is the rightmost band");
            Assert.That(pages.Pages[1].Content.X, Is.LessThan(pages.Pages[0].Content.X), "and the next stands to its left");
            Assert.That(pages.Pages[0].Content.Width, Is.LessThanOrEqualTo(100.01), "no band is wider than the page");
            Assert.That(pages.Pages[0].Content.Height, Is.EqualTo(50).Within(0.01), "a band is the page's whole inline extent");
        });
    }

    [Test]
    public async Task ABlankPageTakesTheRulesAndBoxesOfBlankPages()
    {
        using var view = await Print(
            "<style>@page { size: 100px; margin: 10px }" +
            " @page :blank { @top-center { content: 'blank' } }" +
            " @page { @bottom-right { content: counter(page) '/' counter(pages) } }" +
            " body, p { margin: 0 } .b { height: 50px; break-after: right }</style>" +
            "<p class='b'>a</p><p>b</p>");

        var pages = view.Pages!;

        Assert.That(pages.Count, Is.EqualTo(3), "a blank sheet puts the next page on the right");
        Assert.Multiple(() =>
        {
            Assert.That(pages.Pages[1].Blank, Is.True, "the second sheet is the blank one");
            Assert.That(pages.Pages[1].MarginBoxes![0].Text, Is.EqualTo("blank"), "and it carries what a blank page is given");
            Assert.That(
                pages.Pages[1].MarginBoxes![1].Text,
                Is.EqualTo("2/3"),
                "the blank sheet counts in the page number and in the count");
            Assert.That(pages.Pages[2].MarginBoxes![0].Text, Is.EqualTo("3/3"));
            Assert.That(pages.Pages[0].MarginBoxes![0].Text, Is.EqualTo("1/3"), "the pages that hold content count from the first");
        });
    }

    [Test]
    public async Task ATableCaptionStandsOnTheFirstFragmentOnly()
    {
        using var view = await Print(
            "<style>@page { size: 100px; margin: 0 } body, table, caption, td { margin: 0 }" +
            " table { border-collapse: collapse } caption { font-size: 10px; line-height: 10px }" +
            " td { height: 40px; padding: 0; border: 0 }</style>" +
            "<table><caption>cap</caption><tbody>" +
            "<tr><td></td></tr><tr><td></td></tr><tr><td></td></tr><tr><td></td></tr><tr><td></td></tr>" +
            "</tbody></table>");

        var pages = view.Pages!;
        var caption = Walk(view.Layout!.Root).First(box => box.Source.Kind == Hutu.Rendering.Boxes.BoxKind.TableCaption);

        Assert.That(pages.Count, Is.EqualTo(3), "a caption and five rows of forty in a hundred-pixel page");
        Assert.Multiple(() =>
        {
            Assert.That(caption.BorderBox.Y, Is.GreaterThanOrEqualTo(pages.Pages[0].Content.Y).Within(0.01));
            Assert.That(caption.BorderBox.Bottom, Is.LessThanOrEqualTo(pages.Pages[0].Content.Bottom + 0.01), "the caption stands on the first page");
            Assert.That(pages.Pages[1].Content.Y, Is.GreaterThanOrEqualTo(caption.BorderBox.Bottom).Within(0.01), "and no later fragment carries it");
        });
    }

    private static IEnumerable<LayoutBox> Walk(LayoutBox node)
    {
        yield return node;

        foreach (var child in node.Children)
        {
            foreach (var node2 in Walk(child))
            {
                yield return node2;
            }
        }
    }

    [Test]
    public async Task ATableHeaderStandsAgainAtTheTopOfEveryFragmentBelowTheFirst()
    {
        using var view = await Print(
            "<style>@page { size: 100px; margin: 0 } body, table, thead, tbody, tr, td, th { margin: 0 }" +
            " table { width: 100px; border-collapse: collapse } th, td { height: 40px; padding: 0; border: 0 }" +
            " thead th { height: 20px }</style>" +
            "<table><thead><tr><th></th></tr></thead><tbody>" +
            "<tr><td></td></tr><tr><td></td></tr><tr><td></td></tr><tr><td></td></tr><tr><td></td></tr>" +
            "</tbody></table>");

        var pages = view.Pages!;

        Assert.That(pages.Count, Is.EqualTo(3), "a header of twenty and five rows of forty in a hundred-pixel page");
        Assert.Multiple(() =>
        {
            Assert.That(pages.Pages[0].Lead, Is.EqualTo(0).Within(0.01), "the first fragment carries the header in its flow");
            Assert.That(pages.Pages[0].Repeats, Is.Null);
            Assert.That(pages.Pages[1].Lead, Is.EqualTo(20).Within(0.01), "the fragment below it makes room for the header");
            Assert.That(pages.Pages[1].Repeats![0].Place, Is.EqualTo(new Rect(0, 0, 100, 20)), "and draws the header in that room");
            Assert.That(pages.Pages[1].Content.Height, Is.EqualTo(80).Within(0.01));
            Assert.That(pages.Pages[2].Lead, Is.EqualTo(20).Within(0.01));
            Assert.That(pages.Pages[2].Content.Height, Is.EqualTo(40).Within(0.01));
        });
    }

    [Test]
    public async Task ATableFooterStandsAtTheBottomOfEveryFragmentTheTableGoesOnIn()
    {
        using var view = await Print(
            "<style>@page { size: 100px; margin: 0 } body, table, thead, tbody, tfoot, tr, td, th { margin: 0 }" +
            " table { width: 100px; border-collapse: collapse } th, td { padding: 0; border: 0 }" +
            " thead th { height: 20px } tbody td { height: 40px } tfoot td { height: 20px }</style>" +
            "<table><thead><tr><th></th></tr></thead><tbody>" +
            "<tr><td></td></tr><tr><td></td></tr><tr><td></td></tr><tr><td></td></tr><tr><td></td></tr>" +
            "</tbody><tfoot><tr><td></td></tr></tfoot></table>");

        var pages = view.Pages!;

        Assert.That(pages.Count, Is.EqualTo(4), "a header of twenty, five rows of forty, a footer of twenty");
        Assert.Multiple(() =>
        {
            Assert.That(pages.Pages[1].Lead, Is.EqualTo(20).Within(0.01), "the header takes the top of the fragment");
            Assert.That(pages.Pages[1].Trail, Is.EqualTo(20).Within(0.01), "and the footer the bottom");
            Assert.That(pages.Pages[1].Repeats!.Count, Is.EqualTo(2));
            Assert.That(pages.Pages[1].Repeats![1].Place, Is.EqualTo(new Rect(0, 80, 100, 20)), "the footer stands at the page's bottom");
            Assert.That(pages.Pages[1].Content.Height, Is.EqualTo(40).Within(0.01), "sixty pixels of room, of which one row");
            Assert.That(pages.Pages[3].Trail, Is.EqualTo(0).Within(0.01), "the fragment the table ends on keeps the footer in its flow");
            Assert.That(pages.Pages[3].Content.Height, Is.EqualTo(60).Within(0.01), "the last row and the footer");
        });
    }

    /// <summary>
    /// A box no page could hold whole, which the page has already begun, is cut where the page ends
    /// rather than carried to the next one: the page is filled to its own edge (CSS Fragmentation 3
    /// §4.3), and the lines that overflow a box of definite height do not change that.
    /// </summary>
    [Test]
    public async Task ABoxTallerThanAnyPageFillsThePageItStandsOn()
    {
        using var view = await Print(
            "<style>@page { size: 100px; margin: 0 } body, div { margin: 0 }" +
            " #over { height: 0; font-size: 10px; line-height: 20px }" +
            " #tall { contain: size; height: 250px }</style>" +
            "<div id=\"over\">a<br>b<br>c<br>d</div><div id=\"tall\"></div>");

        var pages = view.Pages!;

        Assert.That(pages.Count, Is.EqualTo(3), "two hundred and fifty in pages of a hundred");
        Assert.Multiple(() =>
        {
            Assert.That(pages.Pages[0].Content.Height, Is.EqualTo(100).Within(0.01),
                "the page is filled through the tall box, not ended at the last line before it");
            Assert.That(pages.Pages[1].Content.Y, Is.EqualTo(100).Within(0.01));
            Assert.That(pages.Pages[1].Content.Height, Is.EqualTo(100).Within(0.01));
            Assert.That(pages.Pages[2].Content.Height, Is.EqualTo(50).Within(0.01));
        });
    }

    /// <summary>
    /// A row that asks not to be broken inside is moved whole even when no page could hold it: the
    /// value says the row is one piece, and the page that has room for none of it ends before it.
    /// </summary>
    [Test]
    public async Task ARowTooTallForAnyPageIsMovedWholeWhenItAsksNotToBeBroken()
    {
        using var view = await Print(
            "<style>@page { size: 100px; margin: 0 } body, table, tbody, tr, td { margin: 0;" +
            " padding: 0; border: 0 } table { width: 100px; border-spacing: 0 }</style>" +
            "<table><tbody><tr><td style=\"height: 20px\"></td></tr>" +
            "<tr style=\"break-inside: avoid\"><td style=\"height: 110px\"></td></tr></tbody></table>");

        var pages = view.Pages!;

        Assert.That(pages.Count, Is.EqualTo(3), "the row is moved to a page of its own and cut there");
        Assert.Multiple(() =>
        {
            Assert.That(pages.Pages[0].Content.Height, Is.EqualTo(20).Within(0.01),
                "the first row is all the first page holds");
            Assert.That(pages.Pages[1].Content.Y, Is.EqualTo(20).Within(0.01));
            Assert.That(pages.Pages[1].Content.Height, Is.EqualTo(100).Within(0.01));
            Assert.That(pages.Pages[2].Content.Height, Is.EqualTo(10).Within(0.01));
        });
    }

    /// <summary>
    /// An inline table that asks not to be broken makes its whole line one piece: the piece is too
    /// tall for any page and the page before it ends where the line begins, because moving is what the
    /// value asks for and breaking is what the table refuses (CSS Break 3 §4.4).
    /// </summary>
    [Test]
    public async Task ALineHoldingAnUnbreakableInlineTableMovesWhole()
    {
        using var view = await Print(
            "<style>@page { size: 100px; margin: 0 } body, p, table, tbody, tr, td { margin: 0;" +
            " padding: 0; border: 0 } p { height: 30px } table { display: inline-table }</style>" +
            "<p></p>text<table style=\"break-inside: avoid\"><tbody><tr>" +
            "<td style=\"height: 110px\"></td></tr></tbody></table>");

        var pages = view.Pages!;

        Assert.That(pages.Count, Is.EqualTo(3), "the line is moved to a page of its own and cut there");
        Assert.Multiple(() =>
        {
            Assert.That(pages.Pages[0].Content.Height, Is.EqualTo(30).Within(0.01),
                "the page ends where the line begins rather than filling through the table");
            Assert.That(pages.Pages[1].Content.Y, Is.EqualTo(30).Within(0.01));
            Assert.That(pages.Pages[1].Content.Height, Is.EqualTo(100).Within(0.01));
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

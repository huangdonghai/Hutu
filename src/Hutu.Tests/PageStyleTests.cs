using AngleSharp.Css;
using Hutu.Rendering;
using Hutu.Rendering.Layout;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Hutu.Tests;

/// <summary>
/// Covers the page box the sheets describe: the medium the styles are computed for, the
/// <c>@page</c> rule collection, and the size and margins a default page comes to (CSS Page 3 §3,
/// §6).
/// </summary>
[TestFixture]
public sealed class PageStyleTests
{
    [Test]
    public async Task APageTheDocumentSaysNothingAboutIsLetterWithNoMargins()
    {
        using var view = await Render("<html><body></body></html>");

        var page = view.DefaultPageGeometry;

        Assert.Multiple(() =>
        {
            Assert.That(page.Width, Is.EqualTo(8.5 * 96).Within(0.01), "the user agent's own page");
            Assert.That(page.Height, Is.EqualTo(11 * 96).Within(0.01));
            Assert.That(page.Margins, Is.EqualTo(Thickness.Zero), "and no margin the document never wrote");
        });
    }

    [Test]
    public async Task APageSizeAndItsMarginsComeFromTheDefaultPageRule()
    {
        using var view = await Render(
            "<style>@page { size: 500px 300px; margin: 10% 5% }</style><body></body>");

        var page = view.DefaultPageGeometry;

        Assert.Multiple(() =>
        {
            Assert.That(page.Width, Is.EqualTo(500).Within(0.01));
            Assert.That(page.Height, Is.EqualTo(300).Within(0.01));
            Assert.That(page.Margins.Top, Is.EqualTo(30).Within(0.01), "ten per cent of the page height");
            Assert.That(page.Margins.Bottom, Is.EqualTo(30).Within(0.01));
            Assert.That(page.Margins.Left, Is.EqualTo(25).Within(0.01), "five per cent of the page width");
            Assert.That(page.Margins.Right, Is.EqualTo(25).Within(0.01));
            Assert.That(page.ContentWidth, Is.EqualTo(450).Within(0.01), "the page area the document is laid in");
            Assert.That(page.ContentHeight, Is.EqualTo(240).Within(0.01));
        });
    }

    [Test]
    public async Task APageSizeKeywordTurnsWithItsOrientation()
    {
        using var view = await Render(
            "<style>@page { size: a4 landscape }</style><body></body>");

        var page = view.DefaultPageGeometry;
        var portraitWidth = 210 * 96.0 / 25.4;
        var portraitHeight = 297 * 96.0 / 25.4;

        Assert.Multiple(() =>
        {
            Assert.That(page.Width, Is.EqualTo(portraitHeight).Within(0.01), "landscape uses the long side as the width");
            Assert.That(page.Height, Is.EqualTo(portraitWidth).Within(0.01));
        });
    }

    [Test]
    public async Task ALaterPageRuleOverridesAnEarlierOne()
    {
        using var view = await Render(
            "<style>@page { size: 100px; margin: 1px } @page { size: 200px 300px; margin: 10px }</style><body></body>");

        var page = view.DefaultPageGeometry;

        Assert.Multiple(() =>
        {
            Assert.That(page.Width, Is.EqualTo(200).Within(0.01));
            Assert.That(page.Height, Is.EqualTo(300).Within(0.01));
            Assert.That(page.Margins.Top, Is.EqualTo(10).Within(0.01), "the later rule's margin");
        });
    }

    [Test]
    public async Task ASizeOfAutoReturnsToTheUserAgentsPage()
    {
        using var view = await Render(
            "<style>@page { size: 300px } @page { size: auto }</style><body></body>");

        var page = view.DefaultPageGeometry;

        Assert.That(page.Width, Is.EqualTo(8.5 * 96).Within(0.01));
        Assert.That(page.Height, Is.EqualTo(11 * 96).Within(0.01));
    }

    [Test]
    public async Task ANamedPageRuleDoesNotChangeTheDefaultPage()
    {
        using var view = await Render(
            "<style>@page foo { size: 100px }</style><body></body>");

        var page = view.DefaultPageGeometry;

        Assert.That(page.Width, Is.EqualTo(8.5 * 96).Within(0.01), "a page name is answered only by the page it names");
    }

    [Test]
    public async Task APageRuleInsideAPrintBlockAppliesOnlyToPrint()
    {
        using var view = await Render(
            "<style>@page { size: 300px } @media print { @page { size: 400px 500px } }</style><body></body>");

        Assert.That(view.DefaultPageGeometry.Width, Is.EqualTo(300).Within(0.01), "the screen does not read the print block");

        view.SetMediaType(DeviceCategory.Printer);

        Assert.Multiple(() =>
        {
            Assert.That(view.DefaultPageGeometry.Width, Is.EqualTo(400).Within(0.01), "print reads it");
            Assert.That(view.DefaultPageGeometry.Height, Is.EqualTo(500).Within(0.01));
        });
    }

    [Test]
    public async Task PrintOnlyRulesAnswerAfterTheMediumChanges()
    {
        using var view = await Render(
            "<style>@media screen { #d { height: 10px } } @media print { #d { height: 50px } }</style>" +
            "<div id='d'></div>");

        Assert.That(ById(view, "d").BorderBox.Height, Is.EqualTo(10).Within(0.01), "the screen rule stands");

        view.SetMediaType(DeviceCategory.Printer);

        Assert.That(ById(view, "d").BorderBox.Height, Is.EqualTo(50).Within(0.01), "and print replaces it");
    }

    [Test]
    public async Task AMarginThePageInheritsTakesTheRootBoxesMargin()
    {
        using var view = await Render(
            "<style>@page { size: 6in; margin: inherit }</style><body style='margin:0'></body>");

        // The root carries no margin of its own here, so the inherited page margin is the initial zero.
        Assert.That(view.DefaultPageGeometry.Margins.Top, Is.EqualTo(0).Within(0.01));

        using var margined = await Render(
            "<html style='margin: 0.5in'><style>@page { size: 6in; margin: inherit }</style><body style='margin:0'></body></html>");

        var page = margined.DefaultPageGeometry;

        Assert.Multiple(() =>
        {
            Assert.That(page.Margins.Top, Is.EqualTo(48).Within(0.01), "the root's half inch");
            Assert.That(page.Margins.Right, Is.EqualTo(48).Within(0.01));
            Assert.That(page.Margins.Bottom, Is.EqualTo(48).Within(0.01));
            Assert.That(page.Margins.Left, Is.EqualTo(48).Within(0.01));
        });
    }

    [Test]
    public async Task APageSizeInEmsReadsTheRootFontSize()
    {
        using var view = await Render(
            "<html style='font-size: 20px'><style>@page { size: 10em }</style><body></body></html>");

        var page = view.DefaultPageGeometry;

        Assert.Multiple(() =>
        {
            Assert.That(page.Width, Is.EqualTo(200).Within(0.01));
            Assert.That(page.Height, Is.EqualTo(200).Within(0.01));
        });
    }

    [Test]
    public async Task AnImportantMarginIsNotOverriddenByALaterNormalOne()
    {
        using var view = await Render(
            "<style>@page { size: 100px; margin-top: 5px !important } @page { margin-top: 9px }</style><body></body>");

        Assert.That(view.DefaultPageGeometry.Margins.Top, Is.EqualTo(5).Within(0.01), "the important declaration wins");
    }

    [Test]
    public async Task AnInheritedPercentageMarginReadsThePageBox()
    {
        using var view = await Render(
            "<html style='margin: 10%'><style>@page { size: 400px 200px; margin: inherit }</style>" +
            "<body style='margin:0'></body></html>");

        var page = view.DefaultPageGeometry;

        Assert.Multiple(() =>
        {
            Assert.That(page.Margins.Top, Is.EqualTo(20).Within(0.01), "ten per cent of the page height");
            Assert.That(page.Margins.Bottom, Is.EqualTo(20).Within(0.01));
            Assert.That(page.Margins.Left, Is.EqualTo(40).Within(0.01), "ten per cent of the page width");
            Assert.That(page.Margins.Right, Is.EqualTo(40).Within(0.01));
        });
    }

    [Test]
    public async Task AnOrientationWithoutASizeTurnsTheUserAgentsPage()
    {
        using var view = await Render(
            "<style>@page { size: landscape }</style><body></body>");

        var page = view.DefaultPageGeometry;

        Assert.Multiple(() =>
        {
            Assert.That(page.Width, Is.EqualTo(11 * 96).Within(0.01));
            Assert.That(page.Height, Is.EqualTo(8.5 * 96).Within(0.01));
        });
    }

    [Test]
    public async Task APageAreaStatedByWidthAndHeightResizesThePageBox()
    {
        using var view = await Render(
            "<style>@page { size: 500px; margin: 50px; width: 200px; height: 300px }</style><body></body>");

        var page = view.DefaultPageGeometry;

        Assert.Multiple(() =>
        {
            Assert.That(page.ContentWidth, Is.EqualTo(200).Within(0.01), "the area the rule states");
            Assert.That(page.ContentHeight, Is.EqualTo(300).Within(0.01));
            Assert.That(page.Width, Is.EqualTo(300).Within(0.01), "the box grows to hold the area and its margins");
            Assert.That(page.Height, Is.EqualTo(400).Within(0.01));
        });
    }

    [Test]
    public async Task APercentagePageAreaReadsTheSizeBox()
    {
        using var view = await Render(
            "<style>@page { size: 500px; margin: 10%; width: 40%; height: 60% }</style><body></body>");

        var page = view.DefaultPageGeometry;

        Assert.Multiple(() =>
        {
            Assert.That(page.ContentWidth, Is.EqualTo(200).Within(0.01), "forty per cent of the size box");
            Assert.That(page.ContentHeight, Is.EqualTo(300).Within(0.01));
            Assert.That(page.Margins.Top, Is.EqualTo(50).Within(0.01), "the margin reads the same box");
            Assert.That(page.Width, Is.EqualTo(300).Within(0.01));
        });
    }

    private static async Task<HutuViewInternal> Render(String html)
    {
        var view = new HutuViewInternal(new FakeTextMeasurer());
        view.SetViewportSize(400, 300);
        await view.SetHtml(html);
        return view;
    }

    private static LayoutBox ById(HutuViewInternal view, String id) =>
        Walk(view.Layout!.Root).First(
            box => String.Equals(box.Source.Element?.GetAttribute("id"), id, StringComparison.Ordinal));

    private static IEnumerable<LayoutBox> Walk(LayoutBox box)
    {
        yield return box;

        foreach (var child in box.Children)
        {
            foreach (var node in Walk(child))
            {
                yield return node;
            }
        }
    }
}

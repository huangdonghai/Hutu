using Hutu.Rendering;
using Hutu.Rendering.Layout;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Hutu.Tests;

/// <summary>
/// The vendor text-decoration properties AngleSharp has no grammar for are read from the raw text the
/// declaration factory keeps: <c>background-clip: text</c> settles the clip, and the text stroke's width
/// and colour - settable together by a shorthand or apart by longhands, and inherited - come out as the
/// pixels and packed colour the painter will need.
/// </summary>
[TestFixture]
public sealed class TextStrokeAndClipTests
{
    [Test]
    public async Task TheVendorBackgroundClipTextSelectsTheTextClip()
    {
        using var view = CreateView();
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<p style='-webkit-background-clip: text'>hi</p></body></html>");

        Assert.That(BoxNamed(view, "p").Source.Style.BackgroundClip, Is.EqualTo(BackgroundClip.Text));
    }

    [Test]
    public async Task TheStandardBackgroundClipTextAlsoSelectsTheTextClip()
    {
        using var view = CreateView();
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<p style='background-clip: text'>hi</p></body></html>");

        Assert.That(BoxNamed(view, "p").Source.Style.BackgroundClip, Is.EqualTo(BackgroundClip.Text));
    }

    [Test]
    public async Task TheStrokeShorthandSetsItsWidthEvenWhenTheColourIsTransparent()
    {
        using var view = CreateView();
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<p style='-webkit-text-stroke: 2px transparent'>hi</p></body></html>");

        var style = BoxNamed(view, "p").Source.Style;
        Assert.That(style.TextStrokeWidth, Is.EqualTo(2).Within(0.01));
        Assert.That(style.TextStrokeColor, Is.EqualTo(0u), "a transparent stroke paints no colour of its own");
    }

    [Test]
    public async Task AStrokeInEmIsMeasuredAgainstTheElementsOwnSize()
    {
        using var view = CreateView();
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<p style='font-size:20px;-webkit-text-stroke: 0.1em red'>hi</p></body></html>");

        var style = BoxNamed(view, "p").Source.Style;
        Assert.That(style.TextStrokeWidth, Is.EqualTo(2).Within(0.01), "0.1em of 20px");
        Assert.That(style.TextStrokeColor, Is.EqualTo(0xFFFF0000u), "red");
    }

    [Test]
    public async Task AStrokeLonghandOverridesWhatTheShorthandLeft()
    {
        using var view = CreateView();
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<p style='-webkit-text-stroke: 2px; -webkit-text-stroke-width: 5px'>hi</p></body></html>");

        Assert.That(BoxNamed(view, "p").Source.Style.TextStrokeWidth, Is.EqualTo(5).Within(0.01));
    }

    [Test]
    public async Task TheStrokeIsInheritedDownToAChildThatSaysNothing()
    {
        using var view = CreateView();
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<p style='-webkit-text-stroke: 4px'>outer <b>inner</b></p></body></html>");

        Assert.That(BoxNamed(view, "b").Source.Style.TextStrokeWidth, Is.EqualTo(4).Within(0.01),
            "the bold child inherits the paragraph's stroke");
    }

    [Test]
    public async Task TheFillColorOverridesTheTextColourAndTravelsDown()
    {
        using var view = CreateView();
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<p style='-webkit-text-fill-color: #00ff00'>outer <b>inner</b></p></body></html>");

        Assert.That(BoxNamed(view, "p").Source.Style.TextFillColor, Is.EqualTo(0xFF00FF00u));
        Assert.That(BoxNamed(view, "b").Source.Style.TextFillColor, Is.EqualTo(0xFF00FF00u), "inherited");
    }

    private static HutuViewInternal CreateView()
    {
        var view = new HutuViewInternal(new FakeTextMeasurer());
        view.SetViewportSize(200, 200);
        return view;
    }

    private static LayoutBox BoxNamed(HutuViewInternal view, String name) =>
        Walk(view.Layout!.Root).First(box => box.Source.Element?.LocalName == name);

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

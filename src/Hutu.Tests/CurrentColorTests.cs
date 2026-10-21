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
/// Covers <c>currentcolor</c>, which is one keyword with two meanings: on a property that paints with
/// the element's own ink it stands for that ink, and on the colour property itself it cannot - it would
/// be the definition of itself.
/// </summary>
/// <remarks>
/// CSS Color 4 §4 gives the second case its answer: in the <c>color</c> property, the used value of
/// <c>currentcolor</c> is the resolved inherited value. The cases are set on a link because a link with
/// nothing of its own is drawn in the engine's own colour, which is the answer a page writing
/// <c>color: currentcolor</c> is asking to step past.
/// </remarks>
[TestFixture]
public sealed class CurrentColorTests
{
    private const UInt32 Green = 0xFF008000;
    private const UInt32 Red = 0xFFFF0000;
    private const UInt32 Black = 0xFF000000;
    private const UInt32 EngineBlue = 0xFF0000EE;

    [Test]
    public async Task CurrentcolorWrittenOnTheColourPropertyTakesTheColourFromAParent()
    {
        Assert.That(
            await Ink("<body style='margin:0'><style>div{color:#008000} a{color:currentcolor}</style>" +
                "<div><a href='next.htm'>link</a></div>"),
            Is.EqualTo(Green),
            "the colour the element is given is the colour it resolves to");

        Assert.That(
            await Ink("<body style='margin:0'><style>div{color:#ff0000} p{color:currentcolor}</style>" +
                "<div><p>link</p></div>"),
            Is.EqualTo(Red),
            "and nothing about that answer is particular to a link");
    }

    [Test]
    public async Task CurrentcolorWrittenOnTheColourPropertyOfTheRootTakesTheInitialColour()
    {
        Assert.That(
            await Ink("<body style='margin:0'><style>a{color:currentcolor}</style>" +
                "<a href='next.htm'>link</a>"),
            Is.EqualTo(Black),
            "with no colour above it there is nothing to resolve but the property's own first one");
    }

    [Test]
    public async Task ALinkWithoutAColourOfItsOwnIsStillDrawnInTheEnginesOwnColour()
    {
        Assert.That(
            await Ink("<body style='margin:0'><style>div{color:#008000}</style>" +
                "<div><a href='next.htm'>link</a></div>"),
            Is.EqualTo(EngineBlue),
            "the keyword is what asks for the parent; a link declaring nothing takes the colour a link is given");
    }

    [Test]
    public async Task CurrentcolorWrittenOnTheBackgroundIsTheInkOfTheBoxThatPaintsIt()
    {
        Assert.That(
            await Back("<style>#outer{width:60px;height:60px}#inner{width:30px;height:30px}" +
                "#outer{color:red;background-color:red}" +
                "#inner{color:green;background-color:currentColor}</style>" +
                "<div id='outer'><div id='inner'>x</div></div>"),
            Is.EqualTo(Green),
            "a box written with the keyword is painted in its own ink, not the ink above it (CSS Color 4 5)");
    }

    [Test]
    public async Task TheKeywordGoesDownATreeThatInheritsItAndIsReadAtEveryBox()
    {
        using var view = await Rendered("<style>#outer{width:60px;height:60px}" +
            "#middle{width:50px;height:50px}#inner{width:40px;height:40px}" +
            "#outer{color:red;background-color:currentColor}" +
            "#middle{background-color:inherit}" +
            "#inner{color:green;background-color:inherit}</style>" +
            "<div id='outer'><div id='middle'><div id='inner'>x</div></div></div>");

        Assert.Multiple(() =>
        {
            // What `background-color: inherit` hands down is the keyword rather than the colour the box
            // above it ended up with, so each of the three answers for itself.
            Assert.That(Paint(view, "middle"), Is.EqualTo(Red), "the middle box is red, as its own ink says");
            Assert.That(Paint(view, "inner"), Is.EqualTo(Green), "and the inner one is green");
        });
    }

    private static async Task<HutuViewInternal> Rendered(String body)
    {
        var view = new HutuViewInternal(new FakeTextMeasurer());
        view.SetViewportSize(400, 600);
        await view.SetHtml("<html>" + body + "</html>");
        return view;
    }

    private static async Task<UInt32> Back(String body) => Paint(await Rendered(body), "inner");

    private static UInt32 Paint(HutuViewInternal view, String id)
    {
        var box = Walk(view.Layout!.Root).First(node => node.Source.Element?.GetAttribute("id") == id);
        var area = box.BorderBox;

        return view.Paint!.Commands.OfType<FillCommand>()
            .First(command => command.Bounds.X == area.X && command.Bounds.Y == area.Y
                && command.Bounds.Width == area.Width && command.Bounds.Height == area.Height)
            .Color;
    }

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

    private static async Task<UInt32> Ink(String body)
    {
        using var view = new HutuViewInternal(new FakeTextMeasurer());
        view.SetViewportSize(400, 600);
        await view.SetHtml("<html>" + body + "</html>");

        return view.Paint!.Commands.OfType<TextCommand>().First(command => command.Text == "link").Color;
    }
}

using Hutu.Rendering;
using Hutu.Rendering.Paint;
using NUnit.Framework;
using System;
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

    private static async Task<UInt32> Ink(String body)
    {
        using var view = new HutuViewInternal(new FakeTextMeasurer());
        view.SetViewportSize(400, 600);
        await view.SetHtml("<html>" + body + "</html>");

        return view.Paint!.Commands.OfType<TextCommand>().First(command => command.Text == "link").Color;
    }
}

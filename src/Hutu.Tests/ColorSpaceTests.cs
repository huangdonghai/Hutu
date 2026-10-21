using AngleSharp.Css.Dom;
using AngleSharp.Css.Parser;
using AngleSharp.Css.Values;
using NUnit.Framework;
using System;

namespace Hutu.Tests;

/// <summary>
/// Covers <c>color()</c>, the function that names a colour in one of the spaces CSS Color 4 defines
/// besides sRGB: the space is written first, and the three components are then read on that space's own
/// scale before the colour is carried to the one the page is drawn in.
/// </summary>
/// <remarks>
/// Each expected colour is Chromium's own answer to the same string, read off a one-pixel canvas it is
/// painted into - the computed style keeps <c>color()</c> as it was written, so nothing can be read there.
/// The strings are the ones the borrowed pages of the same names write, and the greens and the pure green
/// are what their references show: each space's own spelling of the same colour, taken into sRGB.
/// </remarks>
[TestFixture]
public sealed class ColorSpaceTests
{
    private readonly CssParser _parser = new CssParser();

    [TestCase("color(srgb 0 0.6 0)", "#009900")]
    [TestCase("color(srgb 0% 60% 0%)", "#009900")]
    [TestCase("color(srgb none 0.6 none)", "#009900")]
    [TestCase("color(srgb-linear 0 0.21586 0)", "#008000")]
    [TestCase("color(display-p3 0.21604 0.49418 0.13151)", "#008000")]
    [TestCase("color(display-p3 0.26374 0.59085 0.16434)", "#009900")]
    [TestCase("color(display-p3-linear 0.0383 0.2087 0.0156)", "#008000")]
    [TestCase("color(display-p3 0 1 0)", "#00ff00")]
    [TestCase("color(a98-rgb 0.281363 0.498012 0.116746)", "#008000")]
    [TestCase("color(a98-rgb 0.33582 0.59441 0.13934)", "#009900")]
    [TestCase("color(a98-rgb 0 1 0)", "#00ff00")]
    [TestCase("color(rec2020 0 1 0)", "#00ff00")]
    [TestCase("color(prophoto-rgb 0.230479 0.395789 0.129968)", "#008000")]
    [TestCase("color(prophoto-rgb 0.2861 0.49131 0.16133)", "#009900")]
    [TestCase("color(prophoto-rgb 0 1 0)", "#00ff00")]
    [TestCase("color(xyz 0.07719 0.15438 0.02573)", "#008000")]
    [TestCase("color(xyz-d65 0.07719 0.15438 0.02573)", "#008000")]
    [TestCase("color(xyz-d50 0.08312 0.154746 0.020961)", "#008000")]
    [TestCase("color(xyz-d50 0.12266 0.22836 0.03093)", "#009900")]
    [TestCase("color(xyz 0 1 0)", "#00ff00")]
    public void TheSpaceNamesTheColourItsGreenSquareIs(String value, String expected)
    {
        var colour = Read(value);

        Assert.Multiple(() =>
        {
            Assert.That(colour.HasValue, Is.True, $"{value} is a colour");
            Assert.That(Hex(colour!.Value), Is.EqualTo(expected), $"and {value} is the channel it carries to sRGB");
        });
    }

    [Test]
    public void Rec2020IsCarriedToSrgbOnItsOwnTransferCurve()
    {
        var colour = Read("color(rec2020 0.235202 0.431704 0.085432)");

        // The specification's rec2020 is BT.1886: a pure gamma of 2.40 on the primaries the same table
        // lists. Chromium is brighter than that at every sample of the curve - the green of the page
        // written with these numbers is #008000 there and #006b00 here, and a whole scale of the same
        // channel measured side by side keeps the engine below the browser by between four and thirty.
        Assert.That(Hex(colour!.Value), Is.EqualTo("#006b00"), "the channel the gamma of 2.40 answers");
    }

    [TestCase("color(srgb 0 0.6 0 / 0.5)", 0x80)]
    [TestCase("color(srgb 0 0.6 0 / 50%)", 0x80)]
    public void TheAlphaRidesBehindTheSolidus(String value, Byte expected)
    {
        var colour = Read(value);

        Assert.That(colour.HasValue, Is.True, $"{value} is a colour");
        Assert.That(colour!.Value.A, Is.EqualTo(expected), "and the transparency it asked for");
    }

    [TestCase("color(foobar 0 1 0)")]
    [TestCase("color(display-p3 0 1)")]
    [TestCase("color(srgb)")]
    public void ANameOrAShapeTheGrammarDoesNotCarryIsNoColour(String value)
    {
        var colour = Read(value);

        Assert.Multiple(() =>
        {
            // Chromium was asked the same of each of these and answers false for all three, so a page
            // gating on @supports (color: ...) is told the same thing here as there.
            Assert.That(colour.HasValue, Is.False, $"{value} names nothing");
            Assert.That(Supports(value), Is.False, "and the page is told so");
        });
    }

    private Boolean Supports(String value)
    {
        var sheet = _parser.ParseStyleSheet($"div {{ color: {value} }}");

        return sheet.Rules.Length == 1
            && sheet.Rules[0] is ICssStyleRule style
            && !String.IsNullOrEmpty(style.Style.GetPropertyValue("color"));
    }

    private CssColorValue? Read(String value)
    {
        var sheet = _parser.ParseStyleSheet($"div {{ background-color: {value} }}");

        if (sheet.Rules.Length != 1 || sheet.Rules[0] is not ICssStyleRule style)
        {
            return null;
        }

        return style.Style.GetProperty("background-color")?.RawValue is CssColorValue colour ? colour : null;
    }

    private static String Hex(CssColorValue colour) =>
        $"#{colour.R:x2}{colour.G:x2}{colour.B:x2}";
}

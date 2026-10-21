using AngleSharp.Css.Dom;
using AngleSharp.Css.Parser;
using AngleSharp.Css.Values;
using NUnit.Framework;
using System;

namespace Hutu.Tests;

/// <summary>
/// Covers what a colour component written as a percentage stands for, and the components the chapter
/// says are taken back into their range before anything is converted: a percentage of lightness and a
/// percentage of chroma are not the same size of number in the two families of colour functions, and a
/// saturation below nothing is no colour at all.
/// </summary>
/// <remarks>
/// The reference ranges are CSS Color 4 10's own tables - for lab, 100% is 125 on the two axes; for lch,
/// 100% is 150 of chroma; for oklab and oklch, 100% is 0.4 - and each expected colour is what Chromium
/// answers the same string with, read from a canvas it is painted into or, for the Lab family, from the
/// components the computed style keeps.
/// </remarks>
[TestFixture]
public sealed class ColorScaleTests
{
    private readonly CssParser _parser = new CssParser();

    [TestCase("lab(50% 40% 0)", "#c14e79")]
    [TestCase("lch(50% 33.333% 0)", "#c14e79")]
    [TestCase("oklab(40.1% 25% 25%)", "#812200")]
    [TestCase("oklab(51.975% -35.075% 26.92%)", "#008000")]
    [TestCase("oklch(51.975% 44.215% 142.495)", "#008000")]
    [TestCase("oklch(50% 50% 0)", "#b4065f")]
    public void ThePercentageOfAComponentIsSoMuchOfItsOwnRange(String value, String expected)
    {
        Assert.That(Hex(Read(value)), Is.EqualTo(expected), $"and {value} is the colour that scale carries");
    }

    [TestCase("hsl(0, -50%, 40%)", "#666666")]
    [TestCase("hsl(30, -50%, 60%)", "#999999")]
    [TestCase("hsl(0, 150%, 40%)", "#cc0000")]
    [TestCase("hsl(0 50% -20%)", "#000000")]
    [TestCase("hwb(0 -20% 50%)", "#800000")]
    public void AComponentPastItsRangeIsTakenBackBeforeAnythingIsConverted(String value, String expected)
    {
        Assert.That(Hex(Read(value)), Is.EqualTo(expected), $"{value} is clamped at parsed-value time");
    }

    [TestCase("lab(150% 150 20)", "#ff00e3")]
    [TestCase("lab(100 150 20)", "#ff00e3")]
    [TestCase("lch(150% 150 20)", "#ff00a8")]
    [TestCase("lch(100 150 20)", "#ff00a8")]
    [TestCase("oklab(150% 0.5 0.2)", "#ff0000")]
    [TestCase("oklab(1 0.5 0.2)", "#ff0000")]
    [TestCase("oklch(150% 0.5 50)", "#ff0000")]
    [TestCase("oklch(1 0.5 50)", "#ff0000")]
    public void ALightnessAboveTheRangeIsTheLightnessAtTheTopOfIt(String value, String expected) =>
        Assert.That(Hex(Read(value)), Is.EqualTo(expected), $"{value} is the colour the top of the range answers");

    private CssColorValue Read(String value)
    {
        var sheet = _parser.ParseStyleSheet($"div {{ background-color: {value} }}");

        Assert.That(sheet.Rules.Length, Is.EqualTo(1), $"{value} is read");
        Assert.That(sheet.Rules[0], Is.InstanceOf<ICssStyleRule>(), "as a style rule");

        var style = (ICssStyleRule)sheet.Rules[0];
        var colour = style.Style.GetProperty("background-color")?.RawValue;

        Assert.That(colour, Is.InstanceOf<CssColorValue>(), $"{value} is a colour");

        return (CssColorValue)colour!;
    }

    private static String Hex(CssColorValue colour) =>
        $"#{colour.R:x2}{colour.G:x2}{colour.B:x2}";
}

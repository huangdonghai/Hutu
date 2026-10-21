using AngleSharp.Css.Dom;
using AngleSharp.Css.Parser;
using AngleSharp.Css.Values;
using NUnit.Framework;
using System;

namespace Hutu.Tests;

/// <summary>
/// Covers the channel a colour conversion ends up with, which is the last step before a colour is whole
/// again: the fraction the conversion answers is scaled to the eight bits it is drawn in.
/// </summary>
/// <remarks>
/// The expected colours are the ones the borrowed references of the same family write, each an
/// <c>rgb()</c> of four-decimal percentages a Chromium computed from the function on the page beside it -
/// so the two halves of every pair are asked of the same arithmetic. The engine scaled by 256 and cut the
/// remainder off, which is one below the number CSS Values 3 4.3 asks for wherever the fraction stands
/// between the two: 30.4487% of green is 77.644, and 78 is the channel.
/// </remarks>
[TestFixture]
public sealed class ColorConversionTests
{
    private readonly CssParser _parser = new CssParser();

    [TestCase("lab(50% 50 0)", "#c14e79")]
    [TestCase("lab(70% 0 70)", "#c3a90e")]
    [TestCase("lab(55% 0 -60)", "#2187ed")]
    [TestCase("lch(50% 50 0)", "#c14e79")]
    [TestCase("lch(70% 70 90)", "#c3a90e")]
    [TestCase("lch(55% 60 270)", "#2187ed")]
    [TestCase("oklab(70% -0.1 0)", "#4bb3a1")]
    [TestCase("oklch(50% 0.2 0)", "#b4065f")]
    [TestCase("oklch(80% 0.15 160)", "#52db9c")]
    [TestCase("hsl(0.3turn 100% 25%)", "#1a8000")]
    public void AConvertedChannelIsTheNearestWholeOne(String value, String expected)
    {
        var colour = Read(value);

        Assert.Multiple(() =>
        {
            Assert.That(colour.HasValue, Is.True, $"{value} is a colour");
            Assert.That(Hex(colour!.Value), Is.EqualTo(expected), $"and {value} is the channel it converts to");
        });
    }

    [TestCase("rgba(0 128 0 / 0.5)", "#00800080")]
    [TestCase("rgba(0 128 0 / 0.501)", "#00800080")]
    [TestCase("rgba(0 128 0 / 0.499)", "#0080007f")]
    public void TheAlphaIsScaledTheSameWay(String value, String expected)
    {
        var colour = Read(value);

        Assert.That(colour.HasValue, Is.True, $"{value} is a colour");
        Assert.That(Hex(colour!.Value), Is.EqualTo(expected), "and the transparency is the nearest whole one too");
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

    private static String Hex(CssColorValue colour)
    {
        var text = $"#{colour.R:x2}{colour.G:x2}{colour.B:x2}";

        return colour.A == 255 ? text : $"{text}{colour.A:x2}";
    }
}

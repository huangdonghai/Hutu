using AngleSharp.Css.Dom;
using AngleSharp.Css.Parser;
using AngleSharp.Css.Values;
using AngleSharp.Text;
using NUnit.Framework;
using System;

namespace Hutu.Tests;

/// <summary>
/// Covers the colour grammars CSS Color 4 writes without commas: the hue, the components and the alpha
/// standing a space apart and joined by a slash, and any of them free to be no value at all.
/// </summary>
/// <remarks>
/// Every expected colour is what the same string answers in Chromium, read from the computed
/// <c>background-color</c> of a page that writes it (measured 2026-10-21) - which is also how the two
/// roundings were settled: a channel standing exactly halfway between two whole numbers goes up, so
/// <c>rgb(0 128.5 0)</c> is 129 and the percentage that lands there, 70% of 255 being 178.5, is 179.
/// </remarks>
[TestFixture]
public sealed class ColorGrammarTests
{
    private readonly CssParser _parser = new CssParser();

    [TestCase("rgb(0 128.0 0)", "#008000")]
    [TestCase("rgb(0 128.0 0 / 1)", "#008000")]
    [TestCase("rgb(0 128.0 0 / 100%)", "#008000")]
    [TestCase("rgb(0, 128.0, 0, 100%)", "#008000")]
    [TestCase("rgb(0% 50% 0%)", "#008000")]
    [TestCase("rgb(0% 50% 0% / 1.0)", "#008000")]
    [TestCase("rgb(0%, 50%, 0%, 100%)", "#008000")]
    [TestCase("rgb(0 128.5 0)", "#008100")]
    [TestCase("rgb(0 127.5 0)", "#008000")]
    [TestCase("rgb(0 0.5 0)", "#000100")]
    [TestCase("rgb(0 0.4 0)", "#000000")]
    [TestCase("rgb(254.6 255.4 0)", "#ffff00")]
    [TestCase("rgb(30% 0 0)", "#4d0000")]
    [TestCase("rgb(0 0 70%)", "#0000b3")]
    [TestCase("rgb(none 128 none)", "#008000")]
    [TestCase("rgb(0 128 0 / none)", "#00800000")]
    [TestCase("rgba(0 128.0 0 / 0.5)", "#00800080")]
    [TestCase("rgba(0, 128.0, 0, 0.5)", "#00800080")]
    [TestCase("hsl(120 100% 25%)", "#008000")]
    [TestCase("hsl(120deg 100% 25%)", "#008000")]
    [TestCase("hsl(120 100% 25% / 1)", "#008000")]
    [TestCase("hsl(120 100% 25% / 100%)", "#008000")]
    [TestCase("hsl(120, 100%, 25%, 100%)", "#008000")]
    [TestCase("hsl(120 100% 25% / none)", "#00800000")]
    [TestCase("hsl(none 100% 25%)", "#800000")]
    [TestCase("hsl(120 none 25%)", "#404040")]
    [TestCase("hsl(432 100% 25%)", "#668000")]
    [TestCase("hsl(-240 100% 25%)", "#008000")]
    [TestCase("hwb(120 0% 49.8039%)", "#008000")]
    [TestCase("hwb(0 0% 100%)", "#000000")]
    [TestCase("hwb(120 0% 50%)", "#008000")]
    [TestCase("hwb(120 0% 0%)", "#00ff00")]
    [TestCase("hwb(120 40% 40%)", "#669966")]
    [TestCase("hwb(120 0% 49.8039% / 0.5)", "#00800080")]
    [TestCase("hwb(none 0% 50%)", "#800000")]
    public void TheValueIsReadAsTheColourItNames(String value, String expected)
    {
        var colour = Read(value);

        Assert.Multiple(() =>
        {
            Assert.That(colour.HasValue, Is.True, $"{value} is a colour");
            Assert.That(Hex(colour!.Value), Is.EqualTo(expected), $"and {value} is the colour Chromium answers");
        });
    }

    [TestCase("rgb(0 128 0")]
    [TestCase("hsl(120 100%)")]
    [TestCase("hsl(120 100% 25% /)")]
    [TestCase("hwb(120 0%)")]
    public void AnIncompleteValueNamesNoColour(String value) =>
        Assert.That(Read(value).HasValue, Is.False, $"{value} names nothing");

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

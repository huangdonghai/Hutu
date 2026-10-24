using Hutu.Rendering;
using Hutu.Rendering.Boxes;
using Hutu.Rendering.Media;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Hutu.Tests;

/// <summary>
/// The counter styles a page writes for itself with <c>@counter-style</c>: the systems it can ask for,
/// the pieces each of them counts with, and what answers when the descriptors do not hold together.
/// </summary>
/// <remarks>
/// CSS Lists 3 §2.1 lets a style be defined in a conditional group as readily as at the top of a sheet,
/// §2.3 gives six systems and the descriptors they read, and §2.4 has <c>extends</c> take the unstated
/// pieces from the style named. §3.1 sends a style whose descriptors do not hold together to its
/// fallback rather than leaving the list without marks, which is the answer a browser gives and the one
/// a test of a broken symbol list is written to pin.
/// </remarks>
[TestFixture]
public sealed class CounterStyleTests
{
    [Test]
    public async Task AStyleThatExtendsADecimalCountsTheSameWithItsOwnSuffix()
    {
        var markers = await Markers(
            "@counter-style a { system: extends decimal; suffix: \",\" } ol { list-style-type: a }",
            "<ol><li>one<li>two</ol>");

        Assert.That(markers, Is.EqualTo(new[] { "1,", "2," }),
            "§2.4: the pieces the page did not write come from the style it extends");
    }

    [Test]
    public async Task AStyleWithNoSystemWrittenCyclesThroughItsSymbols()
    {
        // §2.3: `cyclic` is the system a style gets when it says nothing, and a cycle hands out one
        // symbol per item, coming back to the first when the list runs out.
        var markers = await Markers(
            "@counter-style a { symbols: \"\\2020\" \"\\2021\" } ol { list-style-type: a }",
            "<ol><li>one<li>two<li>three<li>four</ol>");

        Assert.That(markers, Is.EqualTo(new[] { "\u2020. ", "\u2021. ", "\u2020. ", "\u2021. " }));
    }

    [Test]
    public async Task ASymbolicStyleKeepsTheLastMarkOverWhenItRunsOut()
    {
        var markers = await Markers(
            "@counter-style a { system: symbolic; symbols: \"\\2020\" \"\\2021\" } ol { list-style-type: a }",
            "<ol><li>one<li>two<li>three<li>four</ol>");

        Assert.That(markers, Is.EqualTo(new[] { "\u2020. ", "\u2021. ", "\u2020\u2020. ", "\u2021\u2021. " }),
            "§3.2.4: the chosen symbol is repeated for the overflow, which is not how an alphabet turns over");
    }

    [Test]
    public async Task AnAdditiveStyleAddsUpTheWorthItWasGiven()
    {
        var markers = await Markers(
            "@counter-style a { system: additive; additive-symbols: 3 C, 2 II, 1 I } ol { list-style-type: a }",
            "<ol start=\"4\"><li>four<li>five</ol>");

        Assert.That(markers, Is.EqualTo(new[] { "CI. ", "CII. " }));
    }

    [Test]
    public async Task ANumericStyleCountsInBaseTwoWithTheMarksItIsGiven()
    {
        var markers = await Markers(
            "@counter-style a { system: numeric 2; symbols: 0 1 } ol { list-style-type: a }",
            "<ol start=\"4\"><li>four<li>five</ol>");

        Assert.That(markers, Is.EqualTo(new[] { "100. ", "101. " }));
    }

    [Test]
    public async Task AFixedStyleSaysItsOwnNumberBeyondTheRunItHolds()
    {
        var markers = await Markers(
            "@counter-style a { system: fixed 3; symbols: x y } ol { list-style-type: a }",
            "<ol><li>one<li>two<li>three</ol>");

        Assert.That(markers, Is.EqualTo(new[] { "1. ", "2. ", "x. " }),
            "§3.2.5: the run starts at the number the style named, and a count below it is decimal");
    }

    [Test]
    public async Task APaddedStyleFillsTheRoomItAskedFor()
    {
        var markers = await Markers(
            "@counter-style a { system: extends decimal; pad: 3 \"0\" } ol { list-style-type: a }",
            "<ol><li>one</ol>");

        Assert.That(markers, Is.EqualTo(new[] { "001. " }));
    }

    [Test]
    public async Task AStyleWhoseSymbolsDoNotHoldTogetherCountsInDecimal()
    {
        // §3.1: `inherit` is not a symbol, so the whole descriptor is invalid, so the style is - and an
        // invalid style is spelled with its fallback, which is decimal unless the page named otherwise.
        var markers = await Markers(
            "@counter-style a { system: alphabetic; symbols: a inherit } ol { list-style-type: a }",
            "<ol><li>one</ol>");

        Assert.That(markers, Is.EqualTo(new[] { "1. " }));
    }

    [Test]
    public async Task AStyleThatExtendsADiscMarksWithTheBulletItInherits()
    {
        var markers = await Markers(
            "@counter-style a { system: extends disc } ol { list-style-type: a }",
            "<ol><li>one</ol>");

        Assert.That(markers, Is.EqualTo(new[] { "\u2022 " }),
            "a symbolic built-in has no numbers to count with, only its mark");
    }

    [Test]
    public async Task AStyleDefinedInsideAMediaThatAppliesIsStillDefined()
    {
        var markers = await Markers(
            "@media screen { @counter-style a { symbols: x } } ol { list-style-type: a }",
            "<ol><li>one</ol>");

        Assert.That(markers, Is.EqualTo(new[] { "x. " }), "§2.1: a conditional group holds styles too");
    }

    [Test]
    public async Task ASymbolsListCyclesThroughTheMarksItIsGiven()
    {
        var markers = await Markers(
            "ul { list-style-type: symbols(\"*\" \"†\" \"‡\") }",
            "<ul><li>one<li>two<li>three<li>four</ul>");

        Assert.That(markers, Is.EqualTo(new[] { "* ", "† ", "‡ ", "** " }),
            "§3.3: a bare symbols() list is a symbolic one, and a list the page wrote earns no period");
    }

    [Test]
    public async Task ASymbolsListThatNamesTheAlphabeticSystemTurnsOver()
    {
        var markers = await Markers(
            "ol { list-style-type: symbols(alphabetic \"a\" \"b\") }",
            "<ol><li>one<li>two<li>three<li>four</ol>");

        Assert.That(markers, Is.EqualTo(new[] { "a ", "b ", "aa ", "ab " }),
            "§3.2.3: an alphabet of two runs a, b, aa, ab - and a list the page wrote carries no period");
    }

    [Test]
    public async Task AStyleThatDoesNotHoldTogetherLosesItsOwnSuffixToo()
    {
        // §3.1: an invalid style is not used at all, so the `:` it asked for goes with it and the
        // fallback's period stands.
        var markers = await Markers(
            "@counter-style a { system: cyclic; suffix: ':' } ol { list-style-type: a }",
            "<ol start='4'><li>one<li>two</ol>");

        Assert.That(markers, Is.EqualTo(new[] { "4. ", "5. " }));
    }

    /// <summary>
    /// The markers of a document's list items, read off the box tree: the styles are what the sheets
    /// wrote, so the page has to be built the way the engine builds it.
    /// </summary>
    private static async Task<List<String?>> Markers(String css, String body)
    {
        using var view = new HutuViewInternal(new FakeTextMeasurer());
        view.SetViewportSize(900, 600);

        var document = await view.SetHtml(
            $"<html><head><style>{css}</style></head><body>{body}</body></html>");

        var tree = BoxBuilder.TryBuildFused(document, new HutuRenderDevice(900, 600), (IImageProvider?)null);

        Assert.That(tree, Is.Not.Null, "these documents have to stay on the fused path");

        var markers = new List<String?>();
        Collect(tree!, markers);

        return markers;
    }

    private static void Collect(Box box, List<String?> into)
    {
        if (box.Element is not null &&
            box.Style.Display.Equals("list-item", StringComparison.OrdinalIgnoreCase))
        {
            into.Add(box.Marker);
        }

        foreach (var child in box.Children)
        {
            Collect(child, into);
        }
    }
}

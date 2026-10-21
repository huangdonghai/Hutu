using AngleSharp.Css.Values;
using Hutu.Rendering;
using Hutu.Rendering.Layout;
using NUnit.Framework;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace Hutu.Tests;

/// <summary>
/// Covers the system colours: the names CSS Color 4 gives the user's own page and its parts, the older
/// names that stand for those, and the place the obsolete HTML colour attributes give to neither.
/// </summary>
/// <remarks>
/// The values are the palette a Chromium on this platform answers each name with, measured 2026-10-21
/// from the computed style of a page that sets one per element. The pairs are CSS Color 4 §6.2's own
/// table, which the borrowed <c>deprecated-sameas-*</c> pages each assert a row of - and which a reader
/// can only check by comparing the two names, since the colour of the user's page is not a constant a
/// test may write down.
/// <para>
/// Every case below asks that the name be read <em>as a colour</em> first: a declaration a parser throws
/// away leaves the element in the sentinel ink its parent was written in, and two names that both fail
/// that way answer the same colour as each other. That is the pass which means nothing, and it is the one
/// the borrowed pages fell into before these names existed.
/// </para>
/// </remarks>
[TestFixture]
public sealed class SystemColorTests
{
    private const UInt32 Magenta = 0xFFFF00FF;

    [TestCase("canvas", 0xFFFFFFFF)]
    [TestCase("canvastext", 0xFF000000)]
    [TestCase("buttonface", 0xFFF0F0F0)]
    [TestCase("buttonborder", 0xFF000000)]
    [TestCase("buttontext", 0xFF000000)]
    [TestCase("field", 0xFFFFFFFF)]
    [TestCase("fieldtext", 0xFF000000)]
    [TestCase("graytext", 0xFF6D6D6D)]
    [TestCase("highlight", 0xFF0078D7)]
    [TestCase("highlighttext", 0xFFFFFFFF)]
    [TestCase("linktext", 0xFF0066CC)]
    [TestCase("visitedtext", 0xFF0066CC)]
    [TestCase("selecteditem", 0xFF1967D2)]
    [TestCase("mark", 0xFFFFFF00)]
    [TestCase("marktext", 0xFF000000)]
    [TestCase("accentcolor", 0xFF0075FF)]
    [TestCase("accentcolortext", 0xFFFFFFFF)]
    public async Task ASystemColourNamesTheColourTheUsersPageIsDrawnWith(String name, UInt32 colour)
    {
        using var view = await Render($"<div style='color:#ff00ff'><span id='a' style='color:{name}'>x</span></div>");

        var ink = Packed(Find(view, "a").Source.Style.Color);

        Assert.Multiple(() =>
        {
            Assert.That(ink, Is.Not.EqualTo(Magenta), $"{name} is read as a colour and not thrown away");
            Assert.That(ink, Is.EqualTo(colour), $"and {name} is the colour the platform answers with");
        });
    }

    [TestCase("activeborder", "buttonborder")]
    [TestCase("activecaption", "canvas")]
    [TestCase("appworkspace", "canvas")]
    [TestCase("background", "canvas")]
    [TestCase("buttonhighlight", "buttonface")]
    [TestCase("buttonshadow", "buttonface")]
    [TestCase("captiontext", "canvastext")]
    [TestCase("inactiveborder", "buttonborder")]
    [TestCase("inactivecaption", "canvas")]
    [TestCase("inactivecaptiontext", "graytext")]
    [TestCase("infobackground", "canvas")]
    [TestCase("infotext", "canvastext")]
    [TestCase("menu", "canvas")]
    [TestCase("menutext", "canvastext")]
    [TestCase("scrollbar", "canvas")]
    [TestCase("threeddarkshadow", "buttonborder")]
    [TestCase("threedface", "buttonface")]
    [TestCase("threedhighlight", "buttonborder")]
    [TestCase("threedlightshadow", "buttonborder")]
    [TestCase("threedshadow", "buttonborder")]
    [TestCase("window", "canvas")]
    [TestCase("windowframe", "buttonborder")]
    [TestCase("windowtext", "canvastext")]
    public async Task ADeprecatedSystemColourStandsForTheNameItIsTheObsoleteSpellingOf(
        String legacy, String modern)
    {
        using var view = await Render(
            "<div style='color:#ff00ff'>" +
            $"<span id='a' style='color:{legacy}'>x</span><span id='b' style='color:{modern}'>y</span></div>");

        var obsolete = Packed(Find(view, "a").Source.Style.Color);
        var current = Packed(Find(view, "b").Source.Style.Color);

        Assert.Multiple(() =>
        {
            Assert.That(current, Is.Not.EqualTo(Magenta), $"{modern} is a colour");
            Assert.That(obsolete, Is.EqualTo(current),
                $"and {legacy} is the same one, as CSS Color 4 6.2 says");
        });
    }

    [Test]
    public async Task ABlockTakesTheBackgroundColourTheUsersCanvasIs()
    {
        using var view = await Render("<div id='a' style='background-color:Canvas'>x</div>");

        var back = Find(view, "a").Source.Style.BackgroundColor;

        Assert.That(Packed(back ?? new CssColorValue(255, 0, 255)), Is.EqualTo(0xFFFFFFFF),
            "the name is read as the canvas, and not thrown away for nothing");
    }

    [Test]
    public async Task APageMayAskWhetherTheEngineKnowsASystemColour()
    {
        using var view = await Render(
            "<style>@supports (color: CanvasText) { #a { color: #00ff00 } }</style>" +
            "<div style='color:#ff00ff'><span id='a'>x</span></div>");

        // The borrowed pages gate their test half on exactly this question, so a name that parses but
        // answers false here leaves the page in the colour it wrote for an engine that knows nothing.
        Assert.That(Packed(Find(view, "a").Source.Style.Color), Is.EqualTo(0xFF00FF00),
            "the block asks, and the name is one this engine knows");
    }

    [TestCase("canvas", 0xFFCA00A0)]
    [TestCase("highlight", 0xFF000000)]
    [TestCase("red", 0xFFFF0000)]
    public async Task ASystemColourNamesNoColourInAnObsoleteHtmlAttribute(String name, UInt32 colour)
    {
        using var view = new HutuViewInternal(new FakeTextMeasurer());
        view.SetViewportSize(400, 600);
        await view.SetHtml(
            $"<html><body style='margin:0' text='{name}'><p style='margin:0'>ink</p></body></html>");

        var ink = view.Paint!.Commands.OfType<Hutu.Rendering.Paint.TextCommand>()
            .First(command => command.Text == "ink").Color;

        // HTML 2.3.6 reads these attributes by the digits a name leaves when every character that is no
        // hexadecimal digit becomes a zero; the system colours are none of its names, the new ones no
        // less than the old, and Chrome answers "canvas" the same way as the word it is not (CSS Color 4
        // keeps the names for itself). "red" is a name HTML does know, and is the colour it says.
        Assert.That(ink, Is.EqualTo(colour), $"and {name} is read as the digits it is made of");
    }

    private static async Task<HutuViewInternal> Render(String content)
    {
        var view = new HutuViewInternal(new FakeTextMeasurer());
        view.SetViewportSize(400, 600);
        await view.SetHtml(
            "<html><head><style>div,span,p { margin: 0; font-size: 10px }</style></head>" +
            "<body style='margin:0'>" + content + "</body></html>");
        return view;
    }

    private static LayoutBox Find(HutuViewInternal view, String id) =>
        Walk(view.Layout!.Root).First(box => box.Source.Element?.GetAttribute("id") == id);

    private static UInt32 Packed(CssColorValue colour) =>
        ((UInt32)colour.A << 24) | ((UInt32)colour.R << 16) | ((UInt32)colour.G << 8) | colour.B;

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

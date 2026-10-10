using AngleSharp.Css;
using AngleSharp.Css.Dom;
using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using Hutu.Rendering;
using Hutu.Rendering.Boxes;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Hutu.Tests;

/// <summary>
/// The index shares one cascade between the elements that matched the same rules, and only between
/// those.
/// </summary>
/// <remarks>
/// The key is the match itself - the rules an element matched, in the order the cascade applied
/// them, with its inline style - so it is exact by construction: every test below takes one thing a
/// match can depend on and holds the sharing to it. Elements of the same shape share; anything that
/// could tell two of them apart - an attribute, a state, focus, an inline style, a position in the
/// tree - does not. That last group is the point of keying on the match rather than on what the
/// element looks like: a document full of descendant selectors cannot have its matches predicted
/// from an element alone, and does not need to be.
/// </remarks>
[TestFixture]
public sealed class FusedCascadeSharingTests
{
    /// <summary>The form AngleSharp serialises a declared colour in, which is what a reader gets.</summary>
    private const String Quiet = "rgba(17, 17, 17, 1)";
    private const String Loud = "rgba(255, 0, 0, 1)";
    private const String Green = "rgba(0, 255, 0, 1)";
    private const String Blue = "rgba(0, 0, 255, 1)";

    [Test]
    public async Task TheSameShapeSharesOneCascade()
    {
        using var view = CreateView();
        var document = await view.SetHtml(
            "<html><head><style>.note { color: #123456 }</style></head>" +
            "<body style='margin:0'><p class='note'>a</p><p class='note'>b</p><p>x</p></body></html>");

        var index = IndexOf(document);
        var all = document.QuerySelectorAll("p");
        var first = MatchedCascade.ForElement(index, all[0]);
        var second = MatchedCascade.ForElement(index, all[1]);
        var other = MatchedCascade.ForElement(index, all[2]);

        Assert.That(
            ReferenceEquals(first, second),
            Is.True,
            "two elements of the same shape match the same rules");
        Assert.That(
            second.Find("color")?.Value,
            Is.EqualTo(first.Find("color")?.Value),
            "and the shared cascade answers for both");
        Assert.That(
            ReferenceEquals(first, other),
            Is.False,
            "a different shape is a different match");
    }

    [Test]
    public async Task AnAttributeSelectorTellsTwoElementsApart()
    {
        using var view = CreateView();
        var document = await view.SetHtml(
            "<html><head><style>.tag { color: #111111 } .tag[data-kind='loud'] { color: #ff0000 }</style></head>" +
            "<body style='margin:0'>" +
            "<p class='tag' data-kind='loud'>a</p><p class='tag'>b</p></body></html>");

        var index = IndexOf(document);
        var all = document.QuerySelectorAll("p");
        var loud = MatchedCascade.ForElement(index, all[0]);
        var quiet = MatchedCascade.ForElement(index, all[1]);

        Assert.That(ReferenceEquals(loud, quiet), Is.False, "the attribute is part of what matched");
        Assert.That(loud.Find("color")?.Value, Is.EqualTo(Loud));
        Assert.That(quiet.Find("color")?.Value, Is.EqualTo(Quiet));
    }

    [Test]
    public async Task AForcedStateTellsTwoElementsApart()
    {
        using var view = CreateView();
        var document = await view.SetHtml(
            "<html><head><style>.tag { color: #111111 } .tag:hover { color: #ff0000 }</style></head>" +
            "<body style='margin:0'><p class='tag'>a</p><p class='tag'>b</p></body></html>");

        var index = IndexOf(document);
        var all = document.QuerySelectorAll("p");
        all[0].SetPseudoClass(PseudoClassNames.Hover);

        var hovered = MatchedCascade.ForElement(index, all[0]);
        var plain = MatchedCascade.ForElement(index, all[1]);

        Assert.That(ReferenceEquals(hovered, plain), Is.False, "the hover mark is part of what matched");
        Assert.That(hovered.Find("color")?.Value, Is.EqualTo(Loud));
        Assert.That(plain.Find("color")?.Value, Is.EqualTo(Quiet));
    }

    [Test]
    public async Task FocusTellsTwoElementsApart()
    {
        using var view = CreateView();
        var document = await view.SetHtml(
            "<html><head><style>.tag { color: #111111 } .tag:focus { color: #00ff00 }</style></head>" +
            "<body style='margin:0'><input class='tag'><input class='tag'></body></html>");

        var index = IndexOf(document);
        var all = document.QuerySelectorAll("input");
        ((IHtmlElement)all[0]).DoFocus();

        Assert.That(((IHtmlElement)all[0]).IsFocused, Is.True, "the first control took the focus");

        var focused = MatchedCascade.ForElement(index, all[0]);
        var plain = MatchedCascade.ForElement(index, all[1]);

        Assert.That(ReferenceEquals(focused, plain), Is.False, "focus is part of what matched");
        Assert.That(focused.Find("color")?.Value, Is.EqualTo(Green));
        Assert.That(plain.Find("color")?.Value, Is.EqualTo(Quiet));
    }

    [Test]
    public async Task AnInlineStyleTellsTwoElementsApart()
    {
        using var view = CreateView();
        var document = await view.SetHtml(
            "<html><head><style>.tag { color: #111111 }</style></head>" +
            "<body style='margin:0'>" +
            "<p class='tag'>a</p><p class='tag' style='color: #0000ff'>b</p></body></html>");

        var index = IndexOf(document);
        var all = document.QuerySelectorAll("p");
        var plain = MatchedCascade.ForElement(index, all[0]);
        var styled = MatchedCascade.ForElement(index, all[1]);

        Assert.That(ReferenceEquals(styled, plain), Is.False, "the inline style is part of what matched");
        Assert.That(styled.Find("color")?.Value, Is.EqualTo(Blue), "the inline declaration wins");
        Assert.That(plain.Find("color")?.Value, Is.EqualTo(Quiet));
    }

    [Test]
    public async Task AStateSelectorTheEngineAnswersFromTheElementIsNotShared()
    {
        using var view = CreateView();
        var document = await view.SetHtml(
            "<html><head><style>a { color: #111111 }</style></head>" +
            "<body style='margin:0'><a href='https://example.com'>a</a><a>b</a></body></html>");

        var index = IndexOf(document);
        var all = document.QuerySelectorAll("a");
        var link = MatchedCascade.ForElement(index, all[0]);
        var anchor = MatchedCascade.ForElement(index, all[1]);

        // `:link` is answered by the engine when nothing was forced, and what it reads is the
        // element's own attribute - which is why the two are not the same match, whatever the
        // document's own sheets say about the state.
        Assert.That(ReferenceEquals(link, anchor), Is.False, "the href is part of what matched");
    }

    [Test]
    public async Task ACombinatorStopsTheSharing()
    {
        using var view = CreateView();
        var document = await view.SetHtml(
            "<html><head><style>span { color: #111111 } div span { color: #ff0000 }</style></head>" +
            "<body style='margin:0'><div><span>a</span></div><span>b</span></body></html>");

        var index = IndexOf(document);
        var all = document.QuerySelectorAll("span");
        var inside = MatchedCascade.ForElement(index, all[0]);
        var outside = MatchedCascade.ForElement(index, all[1]);

        Assert.That(
            ReferenceEquals(inside, outside),
            Is.False,
            "a document whose selectors read the tree shares nothing");
        Assert.That(inside.Find("color")?.Value, Is.EqualTo(Loud));
        Assert.That(outside.Find("color")?.Value, Is.EqualTo(Quiet));
    }

    [Test]
    public async Task AStructuralPseudoClassStopsTheSharing()
    {
        using var view = CreateView();
        var document = await view.SetHtml(
            "<html><head><style>li { color: #111111 } li:first-child { color: #ff0000 }</style></head>" +
            "<body style='margin:0'><ul><li>a</li><li>b</li></ul></body></html>");

        var index = IndexOf(document);
        var all = document.QuerySelectorAll("li");
        var first = MatchedCascade.ForElement(index, all[0]);
        var second = MatchedCascade.ForElement(index, all[1]);

        Assert.That(ReferenceEquals(first, second), Is.False, "the position is part of what matched");
        Assert.That(first.Find("color")?.Value, Is.EqualTo(Loud));
        Assert.That(second.Find("color")?.Value, Is.EqualTo(Quiet));
    }

    private static HutuViewInternal CreateView()
    {
        var view = new HutuViewInternal(new FakeTextMeasurer());
        view.SetViewportSize(800, 600);
        return view;
    }

    private static RuleBucketIndex IndexOf(IDocument document)
    {
        var sheets = new List<ICssStyleSheet>();

        foreach (var provider in document.Context.GetServices<ICssDefaultStyleSheetProvider>())
        {
            sheets.Add(provider.Default);
        }

        sheets.AddRange(document.GetStyleSheets().OfType<ICssStyleSheet>());

        return RuleBucketIndex.Build(sheets, new HutuRenderDevice(800, 600));
    }
}

using Hutu.Rendering;
using Hutu.Rendering.Layout;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Hutu.Tests;

/// <summary>
/// Covers the language an element's text is written in: HTML's <c>lang</c> attribute reaches the text
/// below the element that writes it, and an empty value says the language is unknown instead.
/// </summary>
[TestFixture]
public sealed class LanguageInheritanceTests
{
    [Test]
    public async Task LangIsInheritedToTheTextBelowIt()
    {
        using var view = await Rendered(
            "<html lang='zh-Hans'><body style='margin:0'>" +
            "<div id='outer'><div id='inner'>x</div></div>" +
            "</body></html>");

        Assert.Multiple(() =>
        {
            Assert.That(Style(view, "outer").Language, Is.EqualTo("zh-Hans"));
            Assert.That(
                Style(view, "inner").Language,
                Is.EqualTo("zh-Hans"),
                "the child reads the language the ancestor wrote");
        });
    }

    [Test]
    public async Task AnElementsOwnLangReplacesTheOneAboveIt()
    {
        using var view = await Rendered(
            "<html lang='zh-Hans'><body style='margin:0'>" +
            "<div lang='ja' id='jp'><div id='under'>x</div></div>" +
            "</body></html>");

        Assert.Multiple(() =>
        {
            Assert.That(Style(view, "jp").Language, Is.EqualTo("ja"));
            Assert.That(Style(view, "under").Language, Is.EqualTo("ja"), "and everything under it inherits that");
        });
    }

    [Test]
    public async Task AnEmptyLangSaysTheLanguageIsUnknown()
    {
        using var view = await Rendered(
            "<html lang='zh-Hans'><body style='margin:0'>" +
            "<div lang='' id='unknown'><div id='under'>x</div></div>" +
            "</body></html>");

        Assert.Multiple(() =>
        {
            Assert.That(Style(view, "unknown").Language, Is.Null);
            Assert.That(
                Style(view, "under").Language,
                Is.Null,
                "an unknown language is inherited rather than skipped for the one above it");
        });
    }

    [Test]
    public async Task APageThatNamesNoLanguageLeavesItUnknown()
    {
        using var view = await Rendered("<html><body style='margin:0'><div id='plain'>x</div></body></html>");

        Assert.That(Style(view, "plain").Language, Is.Null);
    }

    private static async Task<HutuViewInternal> Rendered(String html)
    {
        var view = new HutuViewInternal(new FakeTextMeasurer());
        view.SetViewportSize(800, 600);
        await view.SetHtml(html);
        return view;
    }

    private static ComputedStyle Style(HutuViewInternal view, String id) =>
        Walk(view.Layout!.Root).First(box => box.Source.Element?.GetAttribute("id") == id).Source.Style;

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

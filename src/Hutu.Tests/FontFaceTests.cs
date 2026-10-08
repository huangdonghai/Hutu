using Hutu.Rendering.Layout;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Hutu.Tests;

/// <summary>
/// Covers the families a page renames with <c>@font-face</c>: a name the rule defines stands for the
/// local fonts its <c>src</c> asks for, in the order it asks for them.
/// </summary>
/// <remarks>
/// Nothing here needs a font to be installed: what is read is the name the layout asks the backend
/// for, which is the same on every machine.
/// </remarks>
[TestFixture]
public sealed class FontFaceTests
{
    [Test]
    public async Task ANameARuleDefinesStandsForTheLocalFontsBehindIt()
    {
        using var view = CreateView();

        await view.SetHtml(
            "<html><head><style>" +
            "@font-face { font-family: \"st\"; src: url(../Fonts/st.otf), local(\"DK-SONGTI\"), local(\"TsukuMinPr6-M\") }" +
            "p { font-family: st }" +
            "</style></head><body style='margin:0'><p>text</p></body></html>");

        Assert.That(
            Text(view).Font.Family,
            Is.EqualTo("DK-SONGTI, TsukuMinPr6-M"),
            "the file source is passed over and the local ones are what the name stands for");
    }

    [Test]
    public async Task AFaceWithNothingButAFileBehindItRenamesNothing()
    {
        using var view = CreateView();

        await view.SetHtml(
            "<html><head><style>" +
            "@font-face { font-family: \"mine\"; src: url(../Fonts/mine.woff2) format(\"woff2\") }" +
            "p { font-family: mine, serif }" +
            "</style></head><body style='margin:0'><p>text</p></body></html>");

        Assert.That(
            Text(view).Font.Family,
            Is.EqualTo("mine, serif"),
            "this reader has no font loader, so a face that ships a font of its own names nothing it can use");
    }

    [Test]
    public async Task TheRestOfTheFamilyListIsKept()
    {
        using var view = CreateView();

        await view.SetHtml(
            "<html><head><style>" +
            "@font-face { font-family: \"mine\"; src: local(\"Some Face\") }" +
            "p { font-family: 'mine', \"Other Face\", sans-serif }" +
            "</style></head><body style='margin:0'><p>text</p></body></html>");

        Assert.That(
            Text(view).Font.Family,
            Is.EqualTo("Some Face, Other Face, sans-serif"),
            "the names the page did not redefine are still asked for, after the ones it did");
    }

    [Test]
    public async Task TheRuleIsReadFromTheStyleSheetTheDocumentLinks()
    {
        using var view = CreateView();

        // The rule and the name it defines are inherited down the tree, so the element that asks for
        // the family may be nowhere near the rule that defined it.
        await view.SetHtml(
            "<html><head><style>" +
            "@font-face { font-family: \"mine\"; src: local(\"Some Face\") }" +
            "body { font-family: mine }" +
            "</style></head><body style='margin:0'><div><p>text</p></div></body></html>");

        Assert.That(Text(view).Font.Family, Is.EqualTo("Some Face"));
    }

    private static HutuViewInternal CreateView()
    {
        var view = new HutuViewInternal(new FakeTextMeasurer());
        view.SetViewportSize(600, 400);
        return view;
    }

    private static LayoutBox Text(HutuViewInternal view) =>
        Walk(view.Layout!.Root).First(node => node.Kind == LayoutKind.Text);

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

using Hutu.Rendering;
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

    [Test]
    public async Task AFormatThisReaderCannotDecodeIsSkippedForTheNextOne()
    {
        using var view = CreateView();

        await view.SetHtml(
            "<html><head><style>" +
            "@font-face { font-family: Icon; src: url(a.eot) format(\"embedded-opentype\"), " +
            "url(b.woff) format(\"woff\"), url(c.ttf) format(\"truetype\") }" +
            "</style></head><body></body></html>");

        var files = FontFaceTable.From(view.Document).Files["Icon"];

        Assert.That(
            files.Select(file => Name(file.Url)),
            Is.EqualTo(new[] { "b.woff", "c.ttf" }),
            "CSS Fonts 3 §4.3: a format that is not supported is not even fetched, and the first " +
            "reference that can be activated is the one used");
    }

    [Test]
    public async Task ALocalSourceIsReadInThePlaceItStands()
    {
        using var view = CreateView();

        await view.SetHtml(
            "<html><head><style>" +
            "@font-face { font-family: Mine; src: local(\"Some Face\"), url(d.ttf) format(\"truetype\") }" +
            "</style></head><body></body></html>");

        var table = FontFaceTable.From(view.Document);

        Assert.Multiple(() =>
        {
            Assert.That(table.Expand("Mine"), Is.EqualTo("Some Face"), "the local copy is what the name stands for");
            Assert.That(table.Files["Mine"].Select(file => Name(file.Url)), Is.EqualTo(new[] { "d.ttf" }),
                "and the file behind it is still there for when the machine has no such face");
        });
    }

    [Test]
    public async Task AnUnsupportedFormatAloneLeavesNothingToFetch()
    {
        using var view = CreateView();

        await view.SetHtml(
            "<html><head><style>" +
            "@font-face { font-family: Mine; src: url(e.eot) format(\"embedded-opentype\") }" +
            "</style></head><body></body></html>");

        Assert.That(
            FontFaceTable.From(view.Document).Files.ContainsKey("Mine"),
            Is.False,
            "a face whose only source is a format this reader cannot decode has no file behind it");
    }

    /// <summary>Gets the file part of a font URL, which is resolved against the document before it is
    /// handed to a fetch - and whose directory a test has no business depending on.</summary>
    private static String Name(String url) => url[(url.LastIndexOf('/') + 1)..];

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

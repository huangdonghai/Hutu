using AngleSharp.Css;
using AngleSharp.Css.Dom;
using AngleSharp.Dom;
using Hutu.Rendering;
using Hutu.Rendering.Boxes;
using Hutu.Rendering.Media;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Hutu.Tests;

/// <summary>
/// The fused box tree equals the render-tree box tree: the same boxes in the same order,
/// each with the same kind, text and style. One document-level build, compared whole.
/// </summary>
[TestFixture]
public sealed class FusedBoxTreeTests
{
    [Test]
    public async Task FusedBoxTreeMatchesRenderTreeBoxTree()
    {
        using var view = CreateView();
        var document = await view.SetHtml(
            "<html><head><style>" +
            "body { font-size: 1.1em; margin: 8px }" +
            "h1 { font-size: 2em; font-weight: bold; margin: 0.67em 0 }" +
            ".note { font-size: 0.8em; color: #146eb4; margin-left: 3.5em; text-indent: -1em }" +
            ".title2 { letter-spacing: 9px; font-weight: bold }" +
            ".box { width: 200px; padding: 0.5em 1em; border: 2px dashed red }" +
            "span.bk { text-decoration: underline }" +
            "ul { margin: 1em 0 } li { display: list-item }" +
            "pre { white-space: pre }" +
            "a { color: #00f }" +
            "</style></head>" +
            "<body>" +
            "<h1>Title</h1>" +
            "<p class='note'>note<span>1</span><span class='bk'>u</span></p>" +
            "<div class='title2'>t2</div>" +
            "<div class='box'>b<span>i</span></div>" +
            "<ul><li>one</li><li>two</li></ul>" +
            "<pre>  spaced   text  </pre>" +
            "<a href='https://example.com'>link</a>" +
            String.Concat(Enumerable.Repeat("<p class='note'>x<span class='bk'>y</span></p>", 10)) +
            "</body></html>");

        var device = new HutuRenderDevice(900, 600);
        var window = document.DefaultView!;
        var faces = FontFaceTable.From(document);

        var oldTree = BoxBuilder.Build(window.Render(device), device, (IImageProvider?)null, faces);
        var newTree = BoxBuilder.TryBuildFused(document, device, (IImageProvider?)null, faces);

        Assert.That(oldTree, Is.Not.Null, "the render-tree build yields a tree");
        Assert.That(newTree, Is.Not.Null, "the fused build does not abstain on this corpus");

        var old = Flatten(oldTree!);
        var @new = Flatten(newTree!);

        Assert.That(@new.Count, Is.EqualTo(old.Count), "same box count");

        for (var i = 0; i < old.Count; i++)
        {
            AssertBox(old[i], @new[i], i);
        }
    }

    private static void AssertBox(Box a, Box b, Int32 index)
    {
        var where = $"box {index} <{a.Element?.LocalName ?? "text"}>";

        Assert.That(b.Kind, Is.EqualTo(a.Kind), $"{where} kind");
        Assert.That(b.Text, Is.EqualTo(a.Text), $"{where} text");
        Assert.That(b.Element, Is.SameAs(a.Element), $"{where} element");
        Assert.That(b.IsAnonymous, Is.EqualTo(a.IsAnonymous), $"{where} anonymous");
        Assert.That(b.Flow.Mode, Is.EqualTo(a.Flow.Mode), $"{where} flow mode");

        // Style: the properties the box tree carries forward.
        Assert.That(b.Style.FontSize, Is.EqualTo(a.Style.FontSize), $"{where} font-size");
        Assert.That(b.Style.Display, Is.EqualTo(a.Style.Display), $"{where} display");
        Assert.That(b.Style.Color, Is.EqualTo(a.Style.Color), $"{where} color");
        Assert.That(b.Style.Margin, Is.EqualTo(a.Style.Margin), $"{where} margin");
        Assert.That(b.Style.Padding, Is.EqualTo(a.Style.Padding), $"{where} padding");
        Assert.That(b.Style.BorderStyles, Is.EqualTo(a.Style.BorderStyles), $"{where} border");
        Assert.That(b.Style.Width, Is.EqualTo(a.Style.Width), $"{where} width");
        Assert.That(b.Style.TextIndent, Is.EqualTo(a.Style.TextIndent), $"{where} text-indent");
        Assert.That(b.Style.WhiteSpace, Is.EqualTo(a.Style.WhiteSpace), $"{where} white-space");
        Assert.That(b.IsReplaced, Is.EqualTo(a.IsReplaced), $"{where} replaced");
    }

    private static List<Box> Flatten(Box root)
    {
        var list = new List<Box>();
        Walk(root, list);
        return list;
    }

    private static void Walk(Box box, List<Box> into)
    {
        into.Add(box);

        foreach (var child in box.Children)
        {
            Walk(child, into);
        }
    }

    private static HutuViewInternal CreateView(Int32 viewportWidth = 900)
    {
        var view = new HutuViewInternal(new FakeTextMeasurer());
        view.SetViewportSize(viewportWidth, 600);
        return view;
    }
}

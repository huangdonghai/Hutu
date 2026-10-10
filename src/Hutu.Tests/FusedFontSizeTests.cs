using AngleSharp.Css;
using AngleSharp.Css.Dom;
using AngleSharp.Dom;
using Hutu.Rendering;
using Hutu.Rendering.Boxes;
using Hutu.Rendering.Layout;
using Hutu.Rendering.Media;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Hutu.Tests;

/// <summary>
/// The fused font-size path resolves every element to the same size as the render-tree
/// path: matching plus cascade ordering plus inheritance, without the intermediate
/// declarations either of them allocates.
/// </summary>
[TestFixture]
public sealed class FusedFontSizeTests
{
    [Test]
    public async Task FusedFontSizeMatchesRenderTreePath()
    {
        using var view = CreateView();
        var document = await view.SetHtml(
            "<html><head><style>" +
            "div { font-size: 20px }" +
            ".big { font-size: 1.5em }" +
            "#main { font-size: 150% }" +
            "span { font-size: 0.8em }" +
            "p.small { font-size: 10px !important }" +
            "p { font-size: 30px }" +
            "h1 { font-size: 2em }" +
            ".shorthand { font: 1.2em serif }" +
            ".inheritor { font-size: inherit }" +
            "</style></head>" +
            "<body style='margin:0'>" +
            "<h1>title</h1>" +
            "<div id='main' class='big'>a<span>b<i>c</i></span></div>" +
            "<p class='small'>d</p>" +
            "<p class='shorthand'>e<span>f</span></p>" +
            "<div class='inheritor'>g</div>" +
            "<div style='font-size: 25px'>h</div>" +
            "</body></html>");

        AssertFusedMatchesOld(document, view);
    }

    [Test]
    public async Task FusedFontSizeMatchesOnNestedRelativeSizes()
    {
        using var view = CreateView(900);
        var document = await view.SetHtml(
            "<html><head><style>" +
            "body { font-size: 1.1em }" +
            ".note { font-size: 0.8em }" +
            "span.noteid { font-size: 1em }" +
            "</style></head>" +
            "<body style='margin:0'>" +
            String.Concat(Enumerable.Repeat(
                "<p class='note'>x<span class='noteid'>1</span><span>2</span></p>", 20)) +
            "</body></html>");

        AssertFusedMatchesOld(document, view);
    }

    private static void AssertFusedMatchesOld(IDocument document, HutuViewInternal view)
    {
        var device = new HutuRenderDevice(600, 600);
        var window = document.DefaultView!;
        var old = OldFontSizes(window, device);
        var @new = NewFontSizes(document, device);

        Assert.That(old.Count, Is.GreaterThan(0), "the box tree yields font sizes");

        foreach (var (element, size) in old)
        {
            Assert.That(
                @new.TryGetValue(element, out var fused),
                Is.True,
                $"the fused path resolves <{element.LocalName}> too");
            Assert.That(
                fused,
                Is.EqualTo(size).Within(0.001),
                $"fused size of <{Describe(element)}> is {size}");
        }
    }

    private static Dictionary<IElement, Double> OldFontSizes(IWindow window, HutuRenderDevice device)
    {
        var sizes = new Dictionary<IElement, Double>();
        var tree = BoxBuilder.Build(window.Render(device), device, (IImageProvider?)null);

        if (tree is not null)
        {
            WalkBoxes(tree, sizes);
        }

        return sizes;
    }

    private static void WalkBoxes(Box box, Dictionary<IElement, Double> sizes)
    {
        if (box.Element is { } element && !sizes.ContainsKey(element))
        {
            sizes.Add(element, box.Style.FontSize);
        }

        foreach (var child in box.Children)
        {
            WalkBoxes(child, sizes);
        }
    }

    private static Dictionary<IElement, Double> NewFontSizes(IDocument document, HutuRenderDevice device)
    {
        var sheets = SheetsOf(document).ToList();
        var index = RuleBucketIndex.Build(sheets, device);
        var sizes = new Dictionary<IElement, Double>();

        if (document.DocumentElement is { } root)
        {
            WalkElements(root, index, 0, device, sizes);
        }

        return sizes;
    }

    private static void WalkElements(
        IElement element,
        RuleBucketIndex index,
        Double parentSize,
        HutuRenderDevice device,
        Dictionary<IElement, Double> sizes)
    {
        var cascade = MatchedCascade.ForElement(index, element);
        var size = ComputedStyle.ResolveFontSizeFused(cascade.Find("font-size"), parentSize, device);
        sizes.Add(element, size);

        foreach (var child in element.Children.OfType<IElement>())
        {
            WalkElements(child, index, size, device, sizes);
        }
    }

    private static IEnumerable<ICssStyleSheet> SheetsOf(IDocument document)
    {
        var provider = document.Context.GetServices<ICssDefaultStyleSheetProvider>();

        foreach (var sheet in provider.SelectMany(p => new[] { p.Default }))
        {
            yield return sheet;
        }

        foreach (var sheet in document.GetStyleSheets().OfType<ICssStyleSheet>())
        {
            yield return sheet;
        }
    }

    private static String Describe(IElement element)
    {
        var id = String.IsNullOrEmpty(element.Id) ? String.Empty : "#" + element.Id;
        var classes = element.ClassList is { Length: > 0 }
            ? "." + String.Join(".", element.ClassList)
            : String.Empty;
        return element.LocalName + id + classes;
    }

    private static HutuViewInternal CreateView(Int32 viewportWidth = 600)
    {
        var view = new HutuViewInternal(new FakeTextMeasurer());
        view.SetViewportSize(viewportWidth, 600);
        return view;
    }
}

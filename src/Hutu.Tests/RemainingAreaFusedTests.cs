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
/// The fused path resolves font weight, style and line height to the same values as the
/// render-tree path: own cascade first, inheritance carried down the walk, relative
/// keywords and factors re-resolved per element.
/// </summary>
[TestFixture]
public sealed class RemainingAreaFusedTests
{
    [Test]
    public async Task FusedFontWeightStyleLineHeightMatchRenderTreePath()
    {
        using var view = CreateView();
        var document = await view.SetHtml(
            "<html><head><style>" +
            "div { font-weight: bold; font-style: italic; line-height: 1.5 }" +
            "span { font-weight: normal; font-style: normal }" +
            ".bolder { font-weight: bolder }" +
            ".lighter { font-weight: lighter }" +
            ".w600 { font-weight: 600 }" +
            ".oblique { font-style: oblique }" +
            "p { line-height: normal }" +
            "p.big { line-height: 2 }" +
            "p.share { line-height: 150% }" +
            "p.fixed { line-height: 24px }" +
            ".tight { line-height: 0.8em }" +
            "</style></head>" +
            "<body style='margin:0'>" +
            "<div>a<span>b</span></div>" +
            "<div class='bolder'>c<span>d</span></div>" +
            "<div class='lighter'>e</div>" +
            "<div class='w600'>f</div>" +
            "<div class='oblique'>g</div>" +
            "<p>h</p>" +
            "<p class='big'>i<span>j</span></p>" +
            "<p class='share'>k<span>l</span></p>" +
            "<p class='fixed'>m<span>n</span></p>" +
            "<div class='tight'>o<span>p</span></div>" +
            "</body></html>");

        AssertFusedMatchesOld(document);
    }

    private static void AssertFusedMatchesOld(IDocument document)
    {
        var device = new HutuRenderDevice(600, 600);
        var window = document.DefaultView!;
        var old = OldValues(window, device);
        var @new = NewValues(document, device);

        Assert.That(old.Count, Is.GreaterThan(0), "the box tree yields values");

        foreach (var (element, expected) in old)
        {
            Assert.That(
                @new.TryGetValue(element, out var fused),
                Is.True,
                $"the fused path resolves <{Describe(element)}> too");

            Assert.That(fused, Is.Not.Null, $"fused values exist for <{Describe(element)}>");

            Assert.That(
                fused!.Weight,
                Is.EqualTo(expected.Weight),
                $"fused font-weight of <{Describe(element)}> is {expected.Weight}");
            Assert.That(
                fused.Italic,
                Is.EqualTo(expected.Italic),
                $"fused font-style of <{Describe(element)}> is {expected.Italic}");
            Assert.That(
                fused.LineHeight ?? -1,
                Is.EqualTo(expected.LineHeight ?? -1).Within(0.001),
                $"fused line-height of <{Describe(element)}> is {expected.LineHeight}");
        }
    }

    private static Dictionary<IElement, FontValues> OldValues(IWindow window, HutuRenderDevice device)
    {
        var values = new Dictionary<IElement, FontValues>();
        var tree = BoxBuilder.Build(window.Render(device), device, (IImageProvider?)null);

        if (tree is not null)
        {
            WalkBoxes(tree, values);
        }

        return values;
    }

    private static void WalkBoxes(Box box, Dictionary<IElement, FontValues> values)
    {
        if (box.Element is { } element && !values.ContainsKey(element))
        {
            values.Add(element, new FontValues(box.Style.FontWeight, box.Style.IsItalic, box.Style.LineHeight));
        }

        foreach (var child in box.Children)
        {
            WalkBoxes(child, values);
        }
    }

    private static Dictionary<IElement, FontValues> NewValues(IDocument document, HutuRenderDevice device)
    {
        var sheets = SheetsOf(document).ToList();
        var index = RuleBucketIndex.Build(sheets, device);
        var values = new Dictionary<IElement, FontValues>();

        if (document.DocumentElement is { } root)
        {
            WalkElements(root, index, new Inherited(0, 400, false, null, null), device, values);
        }

        return values;
    }

    private static void WalkElements(
        IElement element,
        RuleBucketIndex index,
        Inherited parent,
        HutuRenderDevice device,
        Dictionary<IElement, FontValues> values)
    {
        var cascade = MatchedCascade.ForElement(index, element);
        var size = ComputedStyle.ResolveFontSizeFused(cascade.Find("font-size"), parent.Size, device);
        var weight = ComputedStyle.ResolveFontWeightFused(cascade.Find("font-weight"), parent.Weight);
        var italic = ComputedStyle.ResolveFontStyleFused(cascade.Find("font-style"), parent.Italic);
        var lineHeightHit = cascade.Find("line-height");
        var lineHeight = ComputedStyle.ResolveLineHeightFused(
            lineHeightHit, parent.LineHeightHit, size, parent.LineHeight, device);
        values.Add(element, new FontValues(weight, italic, lineHeight));

        var inherited = new Inherited(size, weight, italic, lineHeight, lineHeightHit);

        foreach (var child in element.Children.OfType<IElement>())
        {
            WalkElements(child, index, inherited, device, values);
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

    private sealed record FontValues(Int32 Weight, Boolean Italic, Double? LineHeight);

    private sealed record Inherited(
        Double Size,
        Int32 Weight,
        Boolean Italic,
        Double? LineHeight,
        ICssProperty? LineHeightHit);
}

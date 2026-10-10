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
/// The fused path resolves the text area to the same values as the render-tree path:
/// white space inherits down the walk, alignment and indent fall back to their initials,
/// lengths keep their units for the layout pass.
/// </summary>
[TestFixture]
public sealed class FusedTextTests
{
    [Test]
    public async Task FusedTextMatchesRenderTreePath()
    {
        using var view = CreateView();
        var document = await view.SetHtml(
            "<html><head><style>" +
            "div { white-space: pre; text-align: center; vertical-align: top; text-indent: 2em }" +
            "span { white-space: nowrap }" +
            ".plain { text-align: left }" +
            "p { white-space: normal; text-align: right; text-indent: 10px }" +
            "p.pct { text-indent: 5% }" +
            ".sub { vertical-align: sub }" +
            ".inh { white-space: inherit; text-indent: inherit }" +
            "</style></head>" +
            "<body style='margin:0'>" +
            "<div>a<span>b</span></div>" +
            "<div class='plain'>c</div>" +
            "<p>d<span>e</span></p>" +
            "<p class='pct'>f<span>ff</span></p>" +
            "<span class='sub'>g</span>" +
            "<div class='inh'>h</div>" +
            "</body></html>");

        AssertFusedMatchesOld(document);
    }

    private static void AssertFusedMatchesOld(IDocument document)
    {
        var device = new HutuRenderDevice(600, 600);
        var window = document.DefaultView!;
        var old = OldValues(window, device);
        var @new = NewValues(document, device, old);

        Assert.That(old.Count, Is.GreaterThan(0), "the box tree yields values");

        foreach (var (element, expected) in old)
        {
            Assert.That(
                @new.TryGetValue(element, out var fused),
                Is.True,
                $"the fused path resolves <{Describe(element)}> too");
            Assert.That(fused, Is.Not.Null);
            Assert.That(
                fused!.WhiteSpace,
                Is.EqualTo(expected.WhiteSpace),
                $"fused white-space of <{Describe(element)}> is {expected.WhiteSpace}");
            Assert.That(
                fused.Align,
                Is.EqualTo(expected.Align),
                $"fused text-align of <{Describe(element)}> is {expected.Align}");
            Assert.That(
                fused.VerticalAlign,
                Is.EqualTo(expected.VerticalAlign),
                $"fused vertical-align of <{Describe(element)}> is {expected.VerticalAlign}");
            Assert.That(
                fused.Indent,
                Is.EqualTo(expected.Indent),
                $"fused text-indent of <{Describe(element)}> is {expected.Indent}");
        }
    }

    private static Dictionary<IElement, TextValues> OldValues(IWindow window, HutuRenderDevice device)
    {
        var values = new Dictionary<IElement, TextValues>();
        var tree = BoxBuilder.Build(window.Render(device), device, (IImageProvider?)null);

        if (tree is not null)
        {
            WalkBoxes(tree, values);
        }

        return values;
    }

    private static void WalkBoxes(Box box, Dictionary<IElement, TextValues> values)
    {
        if (box.Element is { } element && !values.ContainsKey(element))
        {
            values.Add(element, new TextValues(
                box.Style.WhiteSpace, box.Style.TextAlign, box.Style.VerticalAlign, box.Style.TextIndent));
        }

        foreach (var child in box.Children)
        {
            WalkBoxes(child, values);
        }
    }

    private static Dictionary<IElement, TextValues> NewValues(
        IDocument document, HutuRenderDevice device, Dictionary<IElement, TextValues> old)
    {
        var sheets = SheetsOf(document).ToList();
        var index = RuleBucketIndex.Build(sheets, device);
        var values = new Dictionary<IElement, TextValues>();

        if (document.DocumentElement is { } root)
        {
            // The seeds stand for what flows in from above the document: the initial values,
            // taken as the ground truth the old path resolved for the root itself.
            var seedWhiteSpace = old.TryGetValue(root, out var rootValues) ? rootValues.WhiteSpace : "normal";
            var seedAlign = old.TryGetValue(root, out var rootAlign) ? rootAlign.Align : "start";
            var seedIndent = old.TryGetValue(root, out var rootIndent) ? rootIndent.Indent : StyleLength.Zero;
            WalkElements(root, index, new Inherited(0, seedWhiteSpace, seedAlign, seedIndent), device, values);
        }

        return values;
    }

    private static void WalkElements(
        IElement element,
        RuleBucketIndex index,
        Inherited parent,
        HutuRenderDevice device,
        Dictionary<IElement, TextValues> values)
    {
        var cascade = MatchedCascade.ForElement(index, element);
        var size = ComputedStyle.ResolveFontSizeFused(cascade.Find("font-size"), parent.Size, device);
        var whiteSpace = ComputedStyle.ResolveWhiteSpaceFused(cascade.Find("white-space"), parent.WhiteSpace);
        var align = ComputedStyle.ResolveTextAlignFused(cascade.Find("text-align"), parent.Align);
        var verticalAlign = ComputedStyle.ResolveVerticalAlignFused(cascade.Find("vertical-align"));
        var indent = ComputedStyle.ResolveTextIndentFused(cascade.Find("text-indent"), parent.Indent, size, device);
        values.Add(element, new TextValues(whiteSpace, align, verticalAlign, indent));

        var inherited = new Inherited(size, whiteSpace, align, indent);

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

    private sealed record TextValues(String WhiteSpace, String Align, String VerticalAlign, StyleLength Indent);

    private sealed record Inherited(Double Size, String WhiteSpace, String Align, StyleLength Indent);
}

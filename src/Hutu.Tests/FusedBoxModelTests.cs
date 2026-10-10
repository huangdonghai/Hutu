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
/// The fused path resolves the box model to the same values as the render-tree path:
/// sizing, lengths with units kept, thicknesses with logical axes mapped through the
/// flow, and the reader's own heading and paragraph margins.
/// </summary>
[TestFixture]
public sealed class FusedBoxModelTests
{
    [Test]
    public async Task FusedBoxModelMatchesRenderTreePath()
    {
        using var view = CreateView();
        var document = await view.SetHtml(
            "<html><head><style>" +
            "div { width: 200px; height: 50%; margin: 10px 20px; padding: 0.5em }" +
            ".sized { box-sizing: border-box; width: 100px; min-width: 50px; max-width: 300px }" +
            ".sides { margin-top: 1px; margin-right: 2px; margin-bottom: 3px; margin-left: 4px; padding: 5px }" +
            ".logical { margin-inline-start: 7px; margin-block-end: 9px }" +
            ".kw { margin-block-end: revert }" +
            ".auto { width: auto; margin: auto }" +
            "</style></head>" +
            "<body style='margin:0'>" +
            "<h1>t</h1>" +
            "<p>u</p>" +
            "<ul><li>v</li></ul>" +
            "<div>a</div>" +
            "<div class='sized'>b</div>" +
            "<div class='sides'>c</div>" +
            "<div class='logical'>d</div>" +
            "<div class='kw'>e</div>" +
            "<div class='auto'>f</div>" +
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

        // Every box on this page runs horizontal: the fused thickness takes the flow as a
        // parameter, and vertical mapping is covered when direction and writing mode fuse.
        foreach (var (element, expected) in old)
        {
            Assert.That(
                @new.TryGetValue(element, out var fused),
                Is.True,
                $"the fused path resolves <{Describe(element)}> too");
            Assert.That(fused, Is.Not.Null);
            Assert.That(
                fused!.Sizing,
                Is.EqualTo(expected.Sizing),
                $"fused box-sizing of <{Describe(element)}>");
            Assert.That(
                (fused.Width, fused.Height, fused.MinWidth, fused.MaxWidth, fused.MinHeight, fused.MaxHeight),
                Is.EqualTo((expected.Width, expected.Height, expected.MinWidth, expected.MaxWidth, expected.MinHeight, expected.MaxHeight)),
                $"fused lengths of <{Describe(element)}>");
            Assert.That(
                fused.Margin,
                Is.EqualTo(expected.Margin),
                $"fused margin of <{Describe(element)}>");
            Assert.That(
                fused.Padding,
                Is.EqualTo(expected.Padding),
                $"fused padding of <{Describe(element)}>");
        }
    }

    private static Dictionary<IElement, ModelValues> OldValues(IWindow window, HutuRenderDevice device)
    {
        var values = new Dictionary<IElement, ModelValues>();
        var tree = BoxBuilder.Build(window.Render(device), device, (IImageProvider?)null);

        if (tree is not null)
        {
            WalkBoxes(tree, values);
        }

        return values;
    }

    private static void WalkBoxes(Box box, Dictionary<IElement, ModelValues> values)
    {
        if (box.Element is { } element)
        {
            if (!values.ContainsKey(element))
            {
                Assert.That(
                    box.Style.WritingMode,
                    Is.EqualTo(Hutu.Rendering.Layout.WritingMode.HorizontalTb),
                    $"corpus stays horizontal for <{element.LocalName}>");

                values.Add(element, new ModelValues(
                    box.Style.BoxSizing,
                    box.Style.Width, box.Style.Height,
                    box.Style.MinWidth, box.Style.MaxWidth,
                    box.Style.MinHeight, box.Style.MaxHeight,
                    box.Style.Margin, box.Style.Padding));
            }
        }

        foreach (var child in box.Children)
        {
            WalkBoxes(child, values);
        }
    }

    private static Dictionary<IElement, ModelValues> NewValues(IDocument document, HutuRenderDevice device)
    {
        var sheets = SheetsOf(document).ToList();
        var index = RuleBucketIndex.Build(sheets, device);
        var values = new Dictionary<IElement, ModelValues>();

        if (document.DocumentElement is { } root)
        {
            WalkElements(root, index, 0, device, values);
        }

        return values;
    }

    private static void WalkElements(
        IElement element,
        RuleBucketIndex index,
        Double parentSize,
        HutuRenderDevice device,
        Dictionary<IElement, ModelValues> values)
    {
        var cascade = MatchedCascade.ForElement(index, element);
        var size = ComputedStyle.ResolveFontSizeFused(cascade.Find("font-size"), parentSize, device);
        Func<String, ICssProperty?> find = cascade.Find;
        var flow = WritingModeInfo.Horizontal;
        values.Add(element, new ModelValues(
            ComputedStyle.ResolveBoxSizingFused(find("box-sizing")),
            ComputedStyle.ResolveBoxLengthFused(find("width"), RenderMode.Horizontal, size, device, StyleLength.Auto),
            ComputedStyle.ResolveBoxLengthFused(find("height"), RenderMode.Vertical, size, device, StyleLength.Auto),
            ComputedStyle.ResolveBoxLengthFused(find("min-width"), RenderMode.Horizontal, size, device, StyleLength.None),
            ComputedStyle.ResolveBoxLengthFused(find("max-width"), RenderMode.Horizontal, size, device, StyleLength.None),
            ComputedStyle.ResolveBoxLengthFused(find("min-height"), RenderMode.Vertical, size, device, StyleLength.None),
            ComputedStyle.ResolveBoxLengthFused(find("max-height"), RenderMode.Vertical, size, device, StyleLength.None),
            ComputedStyle.ResolveThicknessFused(find, element, size, device, flow, "margin", StyleLength.Zero),
            ComputedStyle.ResolveThicknessFused(find, element, size, device, flow, "padding", StyleLength.Zero)));

        foreach (var child in element.Children.OfType<IElement>())
        {
            WalkElements(child, index, size, device, values);
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

    private sealed record ModelValues(
        BoxSizing Sizing,
        StyleLength Width, StyleLength Height,
        StyleLength MinWidth, StyleLength MaxWidth,
        StyleLength MinHeight, StyleLength MaxHeight,
        StyleThickness Margin, StyleThickness Padding);
}

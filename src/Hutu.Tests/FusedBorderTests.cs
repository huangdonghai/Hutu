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
/// The fused path resolves the border area to the same values as the render-tree path:
/// shorthand longhands, per-side styles and widths, the medium default width, and radii
/// with percentages left for layout.
/// </summary>
[TestFixture]
public sealed class FusedBorderTests
{
    [Test]
    public async Task FusedBorderMatchesRenderTreePath()
    {
        using var view = CreateView();
        var document = await view.SetHtml(
            "<html><head><style>" +
            "div { border: 2px solid red }" +
            ".sides { border-top-style: dashed; border-top-width: 1px; border-right-style: dotted; border-right-width: 0.5em }" +
            ".styleonly { border-style: solid }" +
            ".widthonly { border-width: 4px }" +
            ".hidden { border: 5px hidden blue }" +
            ".mix { border-style: solid dashed dotted none; border-width: 1px 2px 3px 4px }" +
            "p.round { border: 1px solid black; border-radius: 8px }" +
            "p.pct { border: 1px solid black; border-top-left-radius: 10% 20% }" +
            "p.em { border: 1px solid black; border-radius: 0.5em }" +
            "span { border-bottom: 3px double green }" +
            "</style></head>" +
            "<body style='margin:0'>" +
            "<div>a<span>b</span></div>" +
            "<div class='sides'>c</div>" +
            "<div class='styleonly'>d</div>" +
            "<div class='widthonly'>e</div>" +
            "<div class='hidden'>f</div>" +
            "<div class='mix'>g</div>" +
            "<p class='round'>h</p>" +
            "<p class='pct'>i</p>" +
            "<p class='em'>j</p>" +
            "<p>k</p>" +
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
            Assert.That(fused, Is.Not.Null);
            Assert.That(
                (fused!.TopStyle, fused.RightStyle, fused.BottomStyle, fused.LeftStyle),
                Is.EqualTo((expected.TopStyle, expected.RightStyle, expected.BottomStyle, expected.LeftStyle)),
                $"fused border styles of <{Describe(element)}>");
            Assert.That(
                (fused.TopWidth, fused.RightWidth, fused.BottomWidth, fused.LeftWidth),
                Is.EqualTo((expected.TopWidth, expected.RightWidth, expected.BottomWidth, expected.LeftWidth)).Within(0.001),
                $"fused border widths of <{Describe(element)}>");
            Assert.That(
                fused.Lines,
                Is.EqualTo(expected.Lines),
                $"fused border lines of <{Describe(element)}>");
            Assert.That(
                fused.Radius,
                Is.EqualTo(expected.Radius),
                $"fused border radius of <{Describe(element)}>");
        }
    }

    private static Dictionary<IElement, BorderValues> OldValues(IWindow window, HutuRenderDevice device)
    {
        var values = new Dictionary<IElement, BorderValues>();
        var tree = BoxBuilder.Build(window.Render(device), device, (IImageProvider?)null);

        if (tree is not null)
        {
            WalkBoxes(tree, values);
        }

        return values;
    }

    private static void WalkBoxes(Box box, Dictionary<IElement, BorderValues> values)
    {
        if (box.Element is { } element && !values.ContainsKey(element))
        {
            values.Add(element, new BorderValues(
                box.Style.BorderTopStyle, box.Style.BorderRightStyle,
                box.Style.BorderBottomStyle, box.Style.BorderLeftStyle,
                box.Style.BorderTopWidth, box.Style.BorderRightWidth,
                box.Style.BorderBottomWidth, box.Style.BorderLeftWidth,
                box.Style.BorderStyles, box.Style.BorderRadius));
        }

        foreach (var child in box.Children)
        {
            WalkBoxes(child, values);
        }
    }

    private static Dictionary<IElement, BorderValues> NewValues(IDocument document, HutuRenderDevice device)
    {
        var sheets = SheetsOf(document).ToList();
        var index = RuleBucketIndex.Build(sheets, device);
        var values = new Dictionary<IElement, BorderValues>();

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
        Dictionary<IElement, BorderValues> values)
    {
        var cascade = MatchedCascade.ForElement(index, element);
        var size = ComputedStyle.ResolveFontSizeFused(cascade.Find("font-size"), parentSize, device);
        Func<String, ICssProperty?> find = cascade.Find;
        var styles = ComputedStyle.ResolveBorderStylesFused(find);
        var widths = ComputedStyle.ResolveBorderWidthsFused(find, styles, size, device);
        var lines = ComputedStyle.ResolveBorderLinesFused(styles);
        var radius = ComputedStyle.ResolveBorderRadiusFused(find, size, device);
        values.Add(element, new BorderValues(
            styles.Top, styles.Right, styles.Bottom, styles.Left,
            widths.Top, widths.Right, widths.Bottom, widths.Left,
            lines, radius));

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

    private sealed record BorderValues(
        String TopStyle, String RightStyle, String BottomStyle, String LeftStyle,
        Double TopWidth, Double RightWidth, Double BottomWidth, Double LeftWidth,
        BorderStyles Lines, StyleCornerRadii Radius);
}

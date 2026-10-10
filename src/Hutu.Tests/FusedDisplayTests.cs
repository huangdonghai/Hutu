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
/// The fused path resolves display, direction, writing mode and outline to the same values
/// as the render-tree path: keywords verbatim, inheritance down the walk, the dir attribute
/// where no rule declares direction.
/// </summary>
[TestFixture]
public sealed class FusedDisplayTests
{
    [Test]
    public async Task FusedDisplayDirectionOutlineMatchRenderTreePath()
    {
        using var view = CreateView();
        var document = await view.SetHtml(
            "<html><head><style>" +
            "div { display: inline-block; overflow: hidden; direction: rtl }" +
            ".vis { overflow: visible }" +
            ".scroll { overflow-x: scroll; overflow-y: visible }" +
            ".cov { object-fit: cover }" +
            ".con { object-fit: contain }" +
            ".vert { writing-mode: vertical-rl; text-orientation: upright }" +
            ".side { writing-mode: sideways-lr }" +
            ".comb { text-combine-upright: all }" +
            ".ol { outline-style: solid }" +
            ".olw { outline-style: dashed; outline-width: 2px; outline-offset: 1px }" +
            "ul { outline: 3px dotted red }" +
            "</style></head>" +
            "<body style='margin:0'>" +
            "<div>a<span>b</span></div>" +
            "<div class='vis'>c</div>" +
            "<div class='scroll'>d</div>" +
            "<div class='cov'>e</div>" +
            "<div class='vert'>f<span>g</span></div>" +
            "<div class='side'>h</div>" +
            "<div class='comb'>i</div>" +
            "<div class='ol'>j</div>" +
            "<div class='olw'>k</div>" +
            "<ul><li>l</li></ul>" +
            "<div dir='rtl'>m</div>" +
            "<div dir='ltr'>n</div>" +
            "<table><tr><td>o</td></tr></table>" +
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
            Assert.That(fused!.Display, Is.EqualTo(expected.Display),
                $"fused display of <{Describe(element)}>");
            Assert.That(fused.Clips, Is.EqualTo(expected.Clips),
                $"fused overflow of <{Describe(element)}>");
            Assert.That(fused.Fit, Is.EqualTo(expected.Fit),
                $"fused object-fit of <{Describe(element)}>");
            Assert.That(fused.Direction, Is.EqualTo(expected.Direction),
                $"fused direction of <{Describe(element)}>");
            Assert.That(fused.Mode, Is.EqualTo(expected.Mode),
                $"fused writing-mode of <{Describe(element)}>");
            Assert.That(fused.Orientation, Is.EqualTo(expected.Orientation),
                $"fused text-orientation of <{Describe(element)}>");
            Assert.That(fused.Combine, Is.EqualTo(expected.Combine),
                $"fused text-combine of <{Describe(element)}>");
            Assert.That(fused.OutlineStyle, Is.EqualTo(expected.OutlineStyle),
                $"fused outline-style of <{Describe(element)}>");
            Assert.That(fused.OutlineWidth, Is.EqualTo(expected.OutlineWidth).Within(0.001),
                $"fused outline-width of <{Describe(element)}>");
            Assert.That(fused.OutlineOffset, Is.EqualTo(expected.OutlineOffset).Within(0.001),
                $"fused outline-offset of <{Describe(element)}>");
        }
    }

    private static Dictionary<IElement, DisplayValues> OldValues(IWindow window, HutuRenderDevice device)
    {
        var values = new Dictionary<IElement, DisplayValues>();
        var tree = BoxBuilder.Build(window.Render(device), device, (IImageProvider?)null);

        if (tree is not null)
        {
            WalkBoxes(tree, values);
        }

        return values;
    }

    private static void WalkBoxes(Box box, Dictionary<IElement, DisplayValues> values)
    {
        if (box.Element is { } element && !values.ContainsKey(element))
        {
            values.Add(element, new DisplayValues(
                box.Style.Display, box.Style.ClipsOverflow, box.Style.ObjectFit,
                box.Style.Direction, box.Style.WritingMode,
                box.Style.DeclaredTextOrientation, box.Style.DeclaredTextCombineUpright,
                box.Style.OutlineStyle, box.Style.OutlineWidth, box.Style.OutlineOffset));
        }

        foreach (var child in box.Children)
        {
            WalkBoxes(child, values);
        }
    }

    private static Dictionary<IElement, DisplayValues> NewValues(
        IDocument document, HutuRenderDevice device, Dictionary<IElement, DisplayValues> old)
    {
        var sheets = SheetsOf(document).ToList();
        var index = RuleBucketIndex.Build(sheets, device);
        var values = new Dictionary<IElement, DisplayValues>();

        if (document.DocumentElement is { } root)
        {
            var seed = old.TryGetValue(root, out var rootValues)
                ? new Inherited(0, rootValues.Direction, rootValues.Mode, rootValues.Orientation, rootValues.Combine)
                : new Inherited(0, TextDirection.LeftToRight, WritingMode.HorizontalTb, null, null);
            WalkElements(root, index, seed, device, values);
        }

        return values;
    }

    private static void WalkElements(
        IElement element,
        RuleBucketIndex index,
        Inherited parent,
        HutuRenderDevice device,
        Dictionary<IElement, DisplayValues> values)
    {
        var cascade = MatchedCascade.ForElement(index, element);
        var size = ComputedStyle.ResolveFontSizeFused(cascade.Find("font-size"), parent.Size, device);
        Func<String, ICssProperty?> find = cascade.Find;
        var direction = ComputedStyle.ResolveDirectionFused(element, find("direction"), parent.Direction);
        var mode = ComputedStyle.ResolveWritingModeFused(find("writing-mode"), parent.Mode);
        var orientation = ComputedStyle.ResolveTextOrientationFused(find("text-orientation"), parent.Orientation);
        var combine = ComputedStyle.ResolveTextCombineFused(find("text-combine-upright"), parent.Combine);
        var outlineStyle = ComputedStyle.ResolveOutlineStyleFused(find("outline-style"));
        values.Add(element, new DisplayValues(
            ComputedStyle.ResolveDisplayFused(find("display")),
            ComputedStyle.ResolveClipsOverflowFused(find("overflow-x"), find("overflow-y")),
            ComputedStyle.ResolveObjectFitFused(find("object-fit")),
            direction, mode,
            orientation, combine,
            outlineStyle,
            ComputedStyle.ResolveOutlineWidthFused(outlineStyle, find("outline-width"), size, device),
            ComputedStyle.ResolveOutlineOffsetFused(find("outline-offset"), size, device)));

        var inherited = new Inherited(size, direction, mode, orientation, combine);

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

    private sealed record DisplayValues(
        String Display, Boolean Clips, ObjectFit Fit,
        TextDirection Direction, WritingMode Mode,
        TextOrientation? Orientation, Boolean? Combine,
        BorderLineStyle OutlineStyle, Double OutlineWidth, Double OutlineOffset);

    private sealed record Inherited(
        Double Size, TextDirection Direction, WritingMode Mode, TextOrientation? Orientation, Boolean? Combine);
}

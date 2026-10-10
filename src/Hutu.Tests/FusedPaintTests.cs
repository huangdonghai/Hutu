using AngleSharp.Css;
using AngleSharp.Css.Dom;
using AngleSharp.Css.Values;
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
/// The fused path resolves paint-time values to the same as the render-tree path: text and
/// border colours with currentcolor, links, background colour, decoration flags, cursor,
/// ruby position, and positioning with offsets.
/// </summary>
[TestFixture]
public sealed class FusedPaintTests
{
    [Test]
    public async Task FusedPaintValuesMatchRenderTreePath()
    {
        using var view = CreateView();
        var document = await view.SetHtml(
            "<html><head><style>" +
            "div { color: #123456; border: 1px solid currentcolor }" +
            ".named { color: red; background-color: #abcdef }" +
            ".inh { color: inherit }" +
            "a { text-decoration: underline }" +
            "p.strike { text-decoration: line-through }" +
            ".cur { cursor: pointer }" +
            ".move { cursor: move }" +
            "rt { ruby-position: under }" +
            ".rel { position: relative; top: 2px; left: 1em; z-index: 5 }" +
            ".abs { position: absolute; bottom: 0; z-index: auto }" +
            "</style></head>" +
            "<body style='margin:0'>" +
            "<div>a<span>b</span></div>" +
            "<div class='named'>c</div>" +
            "<div class='inh'>d</div>" +
            "<a href='https://example.com'>e</a>" +
            "<a href='https://example.com' style='color:green'>f</a>" +
            "<p class='strike'>g</p>" +
            "<div class='cur'>h</div>" +
            "<div class='move'>i</div>" +
            "<ruby>j<rt>k</rt></ruby>" +
            "<div class='rel'>l</div>" +
            "<div class='abs'>m</div>" +
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
            Assert.That(fused!.Color, Is.EqualTo(expected.Color),
                $"fused color of <{Describe(element)}>");
            Assert.That(
                (fused.BorderTop, fused.BorderRight, fused.BorderBottom, fused.BorderLeft),
                Is.EqualTo((expected.BorderTop, expected.BorderRight, expected.BorderBottom, expected.BorderLeft)),
                $"fused border colors of <{Describe(element)}>");
            Assert.That(fused.Background, Is.EqualTo(expected.Background),
                $"fused background of <{Describe(element)}>");
            Assert.That((fused.Underline, fused.LineThrough), Is.EqualTo((expected.Underline, expected.LineThrough)),
                $"fused decoration of <{Describe(element)}>");
            Assert.That(fused.Cursor, Is.EqualTo(expected.Cursor),
                $"fused cursor of <{Describe(element)}>");
            Assert.That(fused.Ruby, Is.EqualTo(expected.Ruby),
                $"fused ruby of <{Describe(element)}>");
            Assert.That(fused.Position, Is.EqualTo(expected.Position),
                $"fused position of <{Describe(element)}>");
            Assert.That(
                (fused.Top, fused.Right, fused.Bottom, fused.Left),
                Is.EqualTo((expected.Top, expected.Right, expected.Bottom, expected.Left)),
                $"fused insets of <{Describe(element)}>");
            Assert.That((fused.ZIndex, fused.HasZIndex), Is.EqualTo((expected.ZIndex, expected.HasZIndex)),
                $"fused z-index of <{Describe(element)}>");
        }
    }

    private static Dictionary<IElement, PaintValues> OldValues(IWindow window, HutuRenderDevice device)
    {
        var values = new Dictionary<IElement, PaintValues>();
        var tree = BoxBuilder.Build(window.Render(device), device, (IImageProvider?)null);

        if (tree is not null)
        {
            WalkBoxes(tree, values);
        }

        return values;
    }

    private static void WalkBoxes(Box box, Dictionary<IElement, PaintValues> values)
    {
        if (box.Element is { } element && !values.ContainsKey(element))
        {
            var position = box.Style.Position;
            values.Add(element, new PaintValues(
                box.Style.Color,
                box.Style.BorderTopColor, box.Style.BorderRightColor,
                box.Style.BorderBottomColor, box.Style.BorderLeftColor,
                box.Style.BackgroundColor,
                box.Style.HasUnderline, box.Style.HasLineThrough,
                box.Style.Cursor, box.Style.RubyPosition,
                position.Position, position.Top, position.Right, position.Bottom, position.Left,
                position.ZIndex, position.HasZIndex));
        }

        foreach (var child in box.Children)
        {
            WalkBoxes(child, values);
        }
    }

    private static Dictionary<IElement, PaintValues> NewValues(IDocument document, HutuRenderDevice device)
    {
        var sheets = SheetsOf(document).ToList();
        var index = RuleBucketIndex.Build(sheets, device);
        var values = new Dictionary<IElement, PaintValues>();

        if (document.DocumentElement is { } root)
        {
            WalkElements(root, index, new Inherited(0, null, CursorKind.Auto, RubyPosition.Over), device, values);
        }

        return values;
    }

    private static void WalkElements(
        IElement element,
        RuleBucketIndex index,
        Inherited parent,
        HutuRenderDevice device,
        Dictionary<IElement, PaintValues> values)
    {
        var cascade = MatchedCascade.ForElement(index, element);
        var size = ComputedStyle.ResolveFontSizeFused(cascade.Find("font-size"), parent.Size, device);
        Func<String, ICssProperty?> find = cascade.Find;
        var color = ComputedStyle.ResolveColorFused(
            element, find("color"), parent.Color, ComputedStyle.ResolveLinkColorFused(element));
        var mode = ComputedStyle.ResolvePositionModeFused(find("position"));
        var cursor = ComputedStyle.ResolveCursorFused(element, find("cursor"), parent.Cursor);
        var ruby = ComputedStyle.ResolveRubyPositionFused(find("ruby-position"), parent.Ruby);
        values.Add(element, new PaintValues(
            color,
            ComputedStyle.ResolveBorderColorFused(find("border-top-color"), color),
            ComputedStyle.ResolveBorderColorFused(find("border-right-color"), color),
            ComputedStyle.ResolveBorderColorFused(find("border-bottom-color"), color),
            ComputedStyle.ResolveBorderColorFused(find("border-left-color"), color),
            ComputedStyle.ResolveBackgroundColorFused(find("background-color")),
            ComputedStyle.ResolveDecorationFused(find).Underline,
            ComputedStyle.ResolveDecorationFused(find).LineThrough,
            cursor,
            ruby,
            mode,
            ComputedStyle.ResolveInsetFused(find("top"), size, device),
            ComputedStyle.ResolveInsetFused(find("right"), size, device),
            ComputedStyle.ResolveInsetFused(find("bottom"), size, device),
            ComputedStyle.ResolveInsetFused(find("left"), size, device),
            ComputedStyle.ResolveZIndexFused(find("z-index")).ZIndex,
            ComputedStyle.ResolveZIndexFused(find("z-index")).HasZIndex));

        var inherited = new Inherited(size, color, cursor, ruby);

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

    private sealed record PaintValues(
        CssColorValue Color,
        CssColorValue? BorderTop, CssColorValue? BorderRight, CssColorValue? BorderBottom, CssColorValue? BorderLeft,
        CssColorValue? Background,
        Boolean Underline, Boolean LineThrough,
        CursorKind Cursor, RubyPosition Ruby,
        PositionMode Position, StyleLength Top, StyleLength Right, StyleLength Bottom, StyleLength Left,
        Int32 ZIndex, Boolean HasZIndex);

    private sealed record Inherited(Double Size, CssColorValue? Color, CursorKind Cursor, RubyPosition Ruby);
}

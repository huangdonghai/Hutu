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
/// Every public property of a fused style equals the render-tree style for the same element:
/// area by area, keyword by keyword, colour by colour - the whole resolver layer plus area
/// assembly, checked per element on a corpus that spans inheritance, shorthands and keywords.
/// </summary>
[TestFixture]
public sealed class FusedStyleTests
{
    [Test]
    public async Task FusedStyleMatchesRenderTreeStyleEverywhere()
    {
        using var view = CreateView();
        var document = await view.SetHtml(
            "<html><head><style>" +
            "body { font-size: 1.1em; color: #222; line-height: 1.4; text-align: justify }" +
            "h1 { font-size: 2em; margin: 0.67em 0; font-weight: bold }" +
            "h3 { font-size: 1.17em; border-bottom: 1px solid currentcolor }" +
            ".note { font-size: 0.8em; color: #146eb4; margin-left: 3.5em; text-indent: -1em; line-height: 1.2 }" +
            ".title2 { letter-spacing: 9px; font-weight: bold; text-indent: 0 }" +
            "span.noteid { font-size: 1em }" +
            "span.bk { text-decoration: underline }" +
            ".box { width: 200px; height: 50%; padding: 0.5em 1em; border: 2px dashed red; box-sizing: border-box }" +
            ".pos { position: relative; top: 3px; left: 1em; z-index: 4 }" +
            ".opaque { background-color: #abcdef; background-clip: content-box; overflow: hidden }" +
            ".inherit { color: inherit; text-indent: inherit }" +
            "a { color: #00f }" +
            "</style></head>" +
            "<body>" +
            "<h1>Title</h1>" +
            "<p class='note'>note<span class='noteid'>1</span><span class='bk'>u</span></p>" +
            "<div class='title2'>t2</div>" +
            "<div class='box'>b<span>i</span></div>" +
            "<div class='pos'>p</div>" +
            "<div class='opaque'>o</div>" +
            "<div class='inherit'>i</div>" +
            "<a href='https://example.com'>l</a>" +
            String.Concat(Enumerable.Repeat("<p class='note'>x<span class='noteid'>2</span></p>", 10)) +
            "</body></html>");

        AssertFusedStylesMatch(document);
    }

    private static void AssertFusedStylesMatch(IDocument document)
    {
        var device = new HutuRenderDevice(900, 600);
        var window = document.DefaultView!;
        var old = OldStyles(window, device);
        var @new = NewStyles(document, device);

        Assert.That(old.Count, Is.GreaterThan(0), "the box tree yields styles");

        foreach (var (element, expected) in old)
        {
            Assert.That(
                @new.TryGetValue(element, out var fused),
                Is.True,
                $"the fused path resolves <{Describe(element)}> too");

            AssertStyle(expected, fused, element);
        }
    }

    private static void AssertStyle(ComputedStyle a, ComputedStyle b, IElement element)
    {
        var where = Describe(element);

        // Font
        Eq(a.FontSize, b.FontSize, where, "font-size");
        Eq(a.FontFamily, b.FontFamily, where, "font-family");
        Eq(a.FontWeight, b.FontWeight, where, "font-weight");
        Eq(a.IsItalic, b.IsItalic, where, "font-style");
        Eq(a.LineHeight ?? -1, b.LineHeight ?? -1, where, "line-height");
        Eq(a.LetterSpacing, b.LetterSpacing, where, "letter-spacing");

        // Text
        Eq(a.WhiteSpace, b.WhiteSpace, where, "white-space");
        Eq(a.TextAlign, b.TextAlign, where, "text-align");
        Eq(a.VerticalAlign, b.VerticalAlign, where, "vertical-align");
        Eq(a.TextIndent, b.TextIndent, where, "text-indent");
        Eq(a.IsPreformatted, b.IsPreformatted, where, "preformatted");

        // Border
        Eq(a.BorderTopStyle, b.BorderTopStyle, where, "border-top-style");
        Eq(a.BorderRightStyle, b.BorderRightStyle, where, "border-right-style");
        Eq(a.BorderBottomStyle, b.BorderBottomStyle, where, "border-bottom-style");
        Eq(a.BorderLeftStyle, b.BorderLeftStyle, where, "border-left-style");
        Eq(a.BorderTopWidth, b.BorderTopWidth, where, "border-top-width");
        Eq(a.BorderRightWidth, b.BorderRightWidth, where, "border-right-width");
        Eq(a.BorderBottomWidth, b.BorderBottomWidth, where, "border-bottom-width");
        Eq(a.BorderLeftWidth, b.BorderLeftWidth, where, "border-left-width");
        Eq(a.BorderStyles, b.BorderStyles, where, "border-styles");
        Eq(a.BorderRadius, b.BorderRadius, where, "border-radius");
        Eq(a.BorderTopColor, b.BorderTopColor, where, "border-top-color");
        Eq(a.BorderLeftColor, b.BorderLeftColor, where, "border-left-color");

        // Box model
        Eq(a.BoxSizing, b.BoxSizing, where, "box-sizing");
        Eq(a.Width, b.Width, where, "width");
        Eq(a.Height, b.Height, where, "height");
        Eq(a.MinWidth, b.MinWidth, where, "min-width");
        Eq(a.MaxWidth, b.MaxWidth, where, "max-width");
        Eq(a.Margin, b.Margin, where, "margin");
        Eq(a.Padding, b.Padding, where, "padding");

        // Display
        Eq(a.Display, b.Display, where, "display");
        Eq(a.IsDisplayNone, b.IsDisplayNone, where, "display-none");
        Eq(a.Float, b.Float, where, "float");
        Eq(a.Clear, b.Clear, where, "clear");
        Eq(a.ObjectFit, b.ObjectFit, where, "object-fit");
        Eq(a.ClipsOverflow, b.ClipsOverflow, where, "overflow");

        // Outline
        Eq(a.OutlineStyle, b.OutlineStyle, where, "outline-style");
        Eq(a.OutlineWidth, b.OutlineWidth, where, "outline-width");
        Eq(a.OutlineOffset, b.OutlineOffset, where, "outline-offset");
        Eq(a.OutlineColor, b.OutlineColor, where, "outline-color");

        // Decoration / paint
        Eq(a.HasUnderline, b.HasUnderline, where, "underline");
        Eq(a.HasLineThrough, b.HasLineThrough, where, "line-through");
        Eq(a.Color, b.Color, where, "color");
        Eq(a.BackgroundColor, b.BackgroundColor, where, "background-color");
        Eq(a.BackgroundClip, b.BackgroundClip, where, "background-clip");
        Eq(a.Cursor, b.Cursor, where, "cursor");

        // Flow
        Eq(a.WritingMode, b.WritingMode, where, "writing-mode");
        Eq(a.Direction, b.Direction, where, "direction");
        Eq(a.DeclaredTextOrientation, b.DeclaredTextOrientation, where, "text-orientation");
        Eq(a.DeclaredTextCombineUpright, b.DeclaredTextCombineUpright, where, "text-combine-upright");

        // Position
        Eq(a.Position.Position, b.Position.Position, where, "position");
        Eq(a.Position.Top, b.Position.Top, where, "top");
        Eq(a.Position.Left, b.Position.Left, where, "left");
        Eq(a.Position.ZIndex, b.Position.ZIndex, where, "z-index");
        Eq(a.Position.HasZIndex, b.Position.HasZIndex, where, "has-z-index");
    }

    private static void Eq<T>(T expected, T actual, String where, String what)
    {
        if (!Equals(expected, actual))
        {
            Assert.Fail($"fused {what} of <{where}>: expected '{expected}', got '{actual}'");
        }
    }

    private static Dictionary<IElement, ComputedStyle> OldStyles(IWindow window, HutuRenderDevice device)
    {
        var styles = new Dictionary<IElement, ComputedStyle>();
        var tree = BoxBuilder.Build(window.Render(device), device, (IImageProvider?)null);

        if (tree is not null)
        {
            WalkBoxes(tree, styles);
        }

        return styles;
    }

    private static void WalkBoxes(Box box, Dictionary<IElement, ComputedStyle> styles)
    {
        if (box.Element is { } element && !styles.ContainsKey(element))
        {
            styles.Add(element, box.Style);
        }

        foreach (var child in box.Children)
        {
            WalkBoxes(child, styles);
        }
    }

    private static Dictionary<IElement, ComputedStyle> NewStyles(IDocument document, HutuRenderDevice device)
    {
        var sheets = SheetsOf(document).ToList();
        var index = RuleBucketIndex.Build(sheets, device);
        var faces = FontFaceTable.From(document);
        var pool = new StyleIntern();
        var styles = new Dictionary<IElement, ComputedStyle>();

        if (document.DocumentElement is { } root)
        {
            WalkElements(root, index, FusedParent.Root, device, faces, pool, styles);
        }

        return styles;
    }

    private static void WalkElements(
        IElement element,
        RuleBucketIndex index,
        FusedParent parent,
        HutuRenderDevice device,
        FontFaceTable? faces,
        StyleIntern pool,
        Dictionary<IElement, ComputedStyle> styles)
    {
        var cascade = MatchedCascade.ForElement(index, element);
        var style = FusedStyleFactory.Build(element, cascade, parent, device, faces, pool);
        styles.Add(element, style);

        var inherited = new FusedParent
        {
            FontSize = style.FontSize,
            Color = style.Color,
            Weight = style.FontWeight,
            Italic = style.IsItalic,
            LineHeight = style.LineHeight,
            LineHeightHit = cascade.Find("line-height"),
            WhiteSpace = style.WhiteSpace,
            Align = style.TextAlign,
            Indent = style.TextIndent,
            Direction = style.Direction,
            WritingMode = style.WritingMode,
            Orientation = style.DeclaredTextOrientation,
            CombinesUpright = style.DeclaredTextCombineUpright,
        };

        foreach (var child in element.Children.OfType<IElement>())
        {
            WalkElements(child, index, inherited, device, faces, pool, styles);
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

    private static HutuViewInternal CreateView(Int32 viewportWidth = 900)
    {
        var view = new HutuViewInternal(new FakeTextMeasurer());
        view.SetViewportSize(viewportWidth, 600);
        return view;
    }
}

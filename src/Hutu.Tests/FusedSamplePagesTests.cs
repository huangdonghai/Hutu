using AngleSharp.Dom;
using Hutu.Rendering;
using Hutu.Rendering.Boxes;
using Hutu.Rendering.Layout;
using Hutu.Rendering.Media;
using Hutu.Rendering.Paint;
using Hutu.Skia;
using NUnit.Framework;
using Sample.Common;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;

namespace Hutu.Tests;

/// <summary>
/// Every page in the sample corpus builds the same tree, lays out the same geometry and paints the
/// same commands whether the box builder is fed the render tree or reads the DOM itself.
/// </summary>
/// <remarks>
/// The fused path is pinned to the path it replaces on synthetic documents by the fixtures beside
/// this one, and those are small enough to hold in the head. What they cannot say is whether the
/// two agree on a real page: 2762 elements of vertical Chinese, a table, an image, a form, a
/// font-face - the shapes a corpus carries and a fixture written by hand does not.
/// <para>
/// So the whole corpus is built both ways and compared whole: the boxes in order, each with its
/// kind, text, element, flow and style; then the layout of those boxes, rect for rect; then the
/// paint list the two layouts make. A page the fused path abstains on is not a difference - the
/// render tree is built for it in production as well - but it is worth knowing which they are, so
/// those are printed rather than counted.
/// </para>
/// </remarks>
[TestFixture]
public sealed class FusedSamplePagesTests
{
    /// <summary>How many differences one page is allowed to report before the rest are left out.</summary>
    private const Int32 Shown = 8;

    [Test]
    public async Task EverySamplePageBuildsTheSameTwoWays()
    {
        var diffs = new StringBuilder();
        var notes = new StringBuilder();

        foreach (var page in Pages())
        {
            using var measurer = new SkiaTextMeasurer();
            using var view = new HutuViewInternal(measurer);
            view.AddEmbeddedResourceSource(typeof(SampleUtils).Assembly, "Samples");
            view.SetViewportSize(900, 600);
            await view.LoadUrlAsync(page);

            var document = view.Document!;
            var device = new HutuRenderDevice(900, 600);
            var faces = FontFaceTable.From(document);

            var tree = BoxBuilder.Build(document.DefaultView!.Render(device), device, (IImageProvider?)null, faces);
            var fused = BoxBuilder.TryBuildFused(document, device, (IImageProvider?)null, faces);

            if (fused is null)
            {
                notes.AppendLine($"{page}: the fused path abstains; the render tree is built for it");
                continue;
            }

            if (tree is null)
            {
                notes.AppendLine($"{page}: no render tree either");
                continue;
            }

            var before = diffs.Length;
            CompareBoxes(page, tree, fused, diffs);
            CompareLayout(page, tree, fused, measurer, device, diffs);

            if (diffs.Length == before)
            {
                notes.AppendLine($"{page}: same");
            }
        }

        TestContext.Out.WriteLine(notes.ToString());
        Assert.That(diffs.ToString(), Is.Empty, "the two builders agree on every page of the corpus");
    }

    /// <summary>
    /// The same comparison over documents the corpus has none of. Every page that uses flex also
    /// uses grid, and grid still sends the whole page to the render tree, so no page of the corpus
    /// reaches a flex container on the fused path at all - the one context that was fused last and
    /// has the least to say for itself in the sample. These are the shapes it has to answer for.
    /// </summary>
    [Test]
    public async Task FlexDocumentsBuildTheSameTwoWays()
    {
        var diffs = new StringBuilder();
        var notes = new StringBuilder();

        foreach (var (name, html) in FlexDocuments())
        {
            using var measurer = new SkiaTextMeasurer();
            using var view = new HutuViewInternal(measurer);
            view.SetViewportSize(900, 600);

            var document = await view.SetHtml(html);
            var device = new HutuRenderDevice(900, 600);
            var faces = FontFaceTable.From(document);

            var tree = BoxBuilder.Build(document.DefaultView!.Render(device), device, (IImageProvider?)null, faces);
            var fused = BoxBuilder.TryBuildFused(document, device, (IImageProvider?)null, faces);

            if (fused is null)
            {
                diffs.AppendLine($"{name}: the fused path abstains, and nothing here is beyond it");
                continue;
            }

            var before = diffs.Length;
            CompareBoxes(name, tree!, fused, diffs);
            CompareLayout(name, tree!, fused, measurer, device, diffs);

            if (diffs.Length == before)
            {
                notes.AppendLine($"{name}: same");
            }
        }

        TestContext.Out.WriteLine(notes.ToString());
        Assert.That(diffs.ToString(), Is.Empty, "the two builders agree on a flex container and its items");
    }

    /// <summary>
    /// A grid container for each of the shapes the reader has to tell apart: the track lists, with
    /// and without a <c>repeat()</c>, the gaps, the automatic flow and its tracks, and an item's
    /// placement - by line, by span and by area - with the two alignments it asks for itself.
    /// </summary>
    [Test]
    public async Task GridDocumentsBuildTheSameTwoWays()
    {
        var diffs = new StringBuilder();
        var notes = new StringBuilder();

        foreach (var (name, html) in GridDocuments())
        {
            using var measurer = new SkiaTextMeasurer();
            using var view = new HutuViewInternal(measurer);
            view.SetViewportSize(900, 600);

            var document = await view.SetHtml(html);
            var device = new HutuRenderDevice(900, 600);
            var faces = FontFaceTable.From(document);

            var tree = BoxBuilder.Build(document.DefaultView!.Render(device), device, (IImageProvider?)null, faces);
            var fused = BoxBuilder.TryBuildFused(document, device, (IImageProvider?)null, faces);

            if (fused is null)
            {
                diffs.AppendLine($"{name}: the fused path abstains, and nothing here is beyond it");
                continue;
            }

            var before = diffs.Length;
            CompareBoxes(name, tree!, fused, diffs);
            CompareLayout(name, tree!, fused, measurer, device, diffs);

            if (diffs.Length == before)
            {
                notes.AppendLine($"{name}: same");
            }
        }

        TestContext.Out.WriteLine(notes.ToString());
        Assert.That(diffs.ToString(), Is.Empty, "the two builders agree on a grid container and its items");
    }

    private static List<(String Name, String Html)> GridDocuments()
    {
        var documents = new List<(String, String)>
        {
            ("two columns",
                "<div style='display:grid;grid-template-columns:100px 100px;width:300px'>" +
                "<div id='a'>A</div><div id='b'>B</div></div>"),
            ("fractions",
                "<div style='display:grid;grid-template-columns:1fr 2fr;width:300px'>" +
                "<div id='a'>A</div><div id='b'>B</div></div>"),
            ("repeat fixed",
                "<div style='display:grid;grid-template-columns:repeat(3,50px);width:300px'>" +
                "<div id='a'>A</div><div id='b'>B</div><div id='c'>C</div></div>"),
            ("repeat auto-fill",
                "<div style='display:grid;grid-template-columns:repeat(auto-fill,minmax(60px,1fr));width:300px'>" +
                "<div id='a'>A</div><div id='b'>B</div><div id='c'>C</div></div>"),
            ("minmax",
                "<div style='display:grid;grid-template-columns:minmax(40px,120px) 1fr;width:300px'>" +
                "<div id='a'>A</div><div id='b'>B</div></div>"),
            ("rows and gaps",
                "<div style='display:grid;grid-template-columns:100px 100px;grid-template-rows:20px 30px;gap:10px 5px'>" +
                "<div id='a'>A</div><div id='b'>B</div><div id='c'>C</div><div id='d'>D</div></div>"),
            ("auto tracks",
                "<div style='display:grid;grid-auto-columns:80px;grid-auto-flow:column;width:300px'>" +
                "<div id='a'>A</div><div id='b'>B</div></div>"),
            ("auto flow dense",
                "<div style='display:grid;grid-template-columns:repeat(3,60px);grid-auto-flow:row dense;width:300px'>" +
                "<div id='a' style='grid-column:2'>A</div><div id='b'>B</div><div id='c'>C</div></div>"),
            ("placement by line",
                "<div style='display:grid;grid-template-columns:repeat(2,100px);width:300px'>" +
                "<div id='a' style='grid-column:2;grid-row:1'>A</div><div id='b'>B</div></div>"),
            ("placement by span",
                "<div style='display:grid;grid-template-columns:repeat(2,100px);width:300px'>" +
                "<div id='a' style='grid-column:span 2'>A</div><div id='b'>B</div></div>"),
            ("placement by area",
                "<div style='display:grid;grid-template-columns:repeat(2,100px);grid-template-rows:20px 20px'>" +
                "<div id='a' style='grid-column:1 / 3;grid-row:1 / 3'>A</div></div>"),
            ("place-items",
                "<div style='display:grid;place-items:center;width:100px'>" +
                "<div id='a' style='width:20px;height:10px'>A</div></div>"),
            ("justify and align content",
                "<div style='display:grid;grid-template-columns:50px;width:200px;height:100px;" +
                "justify-content:center;align-content:center'><div id='a'>A</div></div>"),
            ("justify and align items",
                "<div style='display:grid;grid-template-columns:100px;justify-items:end;align-items:end;" +
                "width:200px;height:100px'><div id='a' style='width:20px;height:10px'>A</div></div>"),
            ("self alignment",
                "<div style='display:grid;grid-template-columns:100px;width:200px;height:100px'>" +
                "<div id='a' style='width:20px;height:10px;justify-self:center;align-self:end'>A</div></div>"),
            ("bare text item",
                "<div style='display:grid;place-items:center;width:100px;height:40px'>H</div>"),
            ("nested",
                "<div style='display:grid;grid-template-columns:1fr 1fr;width:200px'>" +
                "<div id='a' style='display:grid;grid-template-columns:1fr 1fr'>" +
                "<div id='b'>B</div><div id='c'>C</div></div><div id='d'>D</div></div>"),
        };

        return documents
            .Select(pair => (pair.Item1, $"<html><body style='margin:0;font-size:16px'>{pair.Item2}</body></html>"))
            .ToList();
    }

    /// <summary>
    /// A flex container in each direction, with the longhands an item and a container carry: the
    /// wrap, the two gaps, the justification and alignment, and an item's grow, shrink, basis,
    /// order and <c>align-self</c>.
    /// </summary>
    private static List<(String Name, String Html)> FlexDocuments()
    {
        var documents = new List<(String, String)>
        {
            ("row",
                "<div style='display:flex;width:300px'><div id='a' style='width:100px'>A</div>" +
                "<div id='b' style='width:100px'>B</div></div>"),
            ("column",
                "<div style='display:flex;flex-direction:column;height:300px'>" +
                "<div id='a' style='height:50px'>A</div><div id='b' style='height:50px'>B</div></div>"),
            ("row-reverse",
                "<div style='display:flex;flex-direction:row-reverse;width:300px'>" +
                "<div id='a' style='width:100px'>A</div><div id='b' style='width:100px'>B</div></div>"),
            ("wrap",
                "<div style='display:flex;flex-wrap:wrap;width:250px'>" +
                "<div id='a' style='width:100px'>A</div><div id='b' style='width:100px'>B</div>" +
                "<div id='c' style='width:100px'>C</div></div>"),
            ("gap one value",
                "<div style='display:flex;width:300px;gap:20px'>" +
                "<div id='a' style='width:100px'>A</div><div id='b' style='width:100px'>B</div></div>"),
            ("gap two values",
                "<div style='display:flex;flex-wrap:wrap;width:300px;gap:10px 30px'>" +
                "<div id='a' style='width:100px'>A</div><div id='b' style='width:100px'>B</div>" +
                "<div id='c' style='width:100px'>C</div></div>"),
            ("row-gap alone",
                "<div style='display:flex;flex-wrap:wrap;width:300px;row-gap:20px'>" +
                "<div id='a' style='width:100px'>A</div><div id='b' style='width:100px'>B</div>" +
                "<div id='c' style='width:100px'>C</div></div>"),
            ("column-gap alone",
                "<div style='display:flex;flex-wrap:wrap;width:300px;column-gap:20px'>" +
                "<div id='a' style='width:100px'>A</div><div id='b' style='width:100px'>B</div>" +
                "<div id='c' style='width:100px'>C</div></div>"),
            ("justify and align",
                "<div style='display:flex;width:300px;height:100px;justify-content:space-between;" +
                "align-items:center'><div id='a' style='width:50px;height:20px'>A</div>" +
                "<div id='b' style='width:50px;height:40px'>B</div></div>"),
            ("grow and shrink",
                "<div style='display:flex;width:300px'><div id='a' style='flex-grow:1'>A</div>" +
                "<div id='b' style='flex-grow:2'>B</div></div>"),
            ("basis and order",
                "<div style='display:flex;width:300px'><div id='a' style='flex-basis:120px;order:2'>A</div>" +
                "<div id='b' style='flex:0 0 80px;order:1'>B</div></div>"),
            ("align-self",
                "<div style='display:flex;width:300px;height:100px;align-items:flex-start'>" +
                "<div id='a' style='width:50px;height:20px'>A</div>" +
                "<div id='b' style='width:50px;height:20px;align-self:flex-end'>B</div></div>"),
            ("inline-flex",
                "<div style='display:inline-flex;width:200px'><div id='a' style='width:80px'>A</div>" +
                "<div id='b' style='width:80px'>B</div></div>"),
            ("nested",
                "<div style='display:flex;width:300px'><div id='a' style='display:flex;flex-grow:1'>" +
                "<div id='b' style='flex-grow:1'>B</div><div id='c' style='width:40px'>C</div></div></div>"),
        };

        return documents
            .Select(pair => (pair.Item1, $"<html><body style='margin:0;font-size:16px'>{pair.Item2}</body></html>"))
            .ToList();
    }

    private static void CompareBoxes(String page, Box tree, Box fused, StringBuilder into)
    {
        var a = Flatten(tree);
        var b = Flatten(fused);

        if (a.Count != b.Count)
        {
            into.AppendLine($"{page}: {a.Count} boxes from the render tree, {b.Count} fused");
            return;
        }

        var shown = 0;

        for (var i = 0; i < a.Count && shown < Shown; i++)
        {
            foreach (var diff in BoxDiffs(a[i], b[i], i))
            {
                into.AppendLine($"{page} {diff}");
                shown++;
            }
        }
    }

    private static void CompareLayout(
        String page, Box tree, Box fused, ITextMeasurer measurer, HutuRenderDevice device, StringBuilder into)
    {
        var engine = new LayoutEngine(measurer);
        var painter = new Painter();

        var a = Flatten(engine.Layout(tree, device.RenderWidth, device.RenderHeight)!.Root);
        var b = Flatten(engine.Layout(fused, device.RenderWidth, device.RenderHeight)!.Root);

        if (a.Count != b.Count)
        {
            into.AppendLine($"{page}: {a.Count} boxes laid out from the render tree, {b.Count} fused");
            return;
        }

        var shown = 0;

        for (var i = 0; i < a.Count && shown < Shown; i++)
        {
            foreach (var diff in LayoutDiffs(a[i], b[i], i))
            {
                into.AppendLine($"{page} {diff}");
                shown++;
            }
        }

        var left = painter.Paint(engine.Layout(tree, device.RenderWidth, device.RenderHeight)!);
        var right = painter.Paint(engine.Layout(fused, device.RenderWidth, device.RenderHeight)!);

        if (left.Commands.Count != right.Commands.Count)
        {
            into.AppendLine($"{page}: {left.Commands.Count} paint commands from the render tree, {right.Commands.Count} fused");
        }
        else if (Math.Abs(left.Bounds.Width - right.Bounds.Width) > 0.5 ||
                 Math.Abs(left.Bounds.Height - right.Bounds.Height) > 0.5)
        {
            into.AppendLine(
                $"{page}: paint bounds {left.Bounds.Width:F1}x{left.Bounds.Height:F1} from the render tree, " +
                $"{right.Bounds.Width:F1}x{right.Bounds.Height:F1} fused");
        }
    }

    private static IEnumerable<String> BoxDiffs(Box a, Box b, Int32 index)
    {
        var where = $"box[{index}] {Where(a)}";

        if (b.Kind != a.Kind) yield return $"{where} kind {a.Kind} vs {b.Kind}";
        if (b.Text != a.Text) yield return $"{where} text '{a.Text}' vs '{b.Text}'";
        if (!ReferenceEquals(b.Element, a.Element)) yield return $"{where} element";
        if (b.IsAnonymous != a.IsAnonymous) yield return $"{where} anonymous {a.IsAnonymous} vs {b.IsAnonymous}";
        if (b.IsReplaced != a.IsReplaced) yield return $"{where} replaced {a.IsReplaced} vs {b.IsReplaced}";
        if (b.Flow.Mode != a.Flow.Mode) yield return $"{where} flow {a.Flow.Mode} vs {b.Flow.Mode}";
        if (b.Flow.IsVertical != a.Flow.IsVertical) yield return $"{where} vertical {a.Flow.IsVertical} vs {b.Flow.IsVertical}";
        if (b.IntrinsicIgnoresBasis != a.IntrinsicIgnoresBasis) yield return $"{where} basis-free {a.IntrinsicIgnoresBasis} vs {b.IntrinsicIgnoresBasis}";

        if (Math.Abs(b.Style.FontSize - a.Style.FontSize) > 0.001) yield return $"{where} font-size {a.Style.FontSize} vs {b.Style.FontSize}";
        if (b.Style.FontFamily != a.Style.FontFamily) yield return $"{where} font-family '{a.Style.FontFamily}' vs '{b.Style.FontFamily}'";
        if (b.Style.FontWeight != a.Style.FontWeight) yield return $"{where} font-weight {a.Style.FontWeight} vs {b.Style.FontWeight}";
        if (b.Style.IsItalic != a.Style.IsItalic) yield return $"{where} italic {a.Style.IsItalic} vs {b.Style.IsItalic}";
        if (b.Style.LineHeight != a.Style.LineHeight) yield return $"{where} line-height {a.Style.LineHeight} vs {b.Style.LineHeight}";
        if (Math.Abs(b.Style.LetterSpacing - a.Style.LetterSpacing) > 0.001) yield return $"{where} letter-spacing {a.Style.LetterSpacing} vs {b.Style.LetterSpacing}";
        if (b.Style.Display != a.Style.Display) yield return $"{where} display '{a.Style.Display}' vs '{b.Style.Display}'";
        if (!b.Style.Color.Equals(a.Style.Color)) yield return $"{where} color {a.Style.Color} vs {b.Style.Color}";
        if (b.Style.WhiteSpace != a.Style.WhiteSpace) yield return $"{where} white-space '{a.Style.WhiteSpace}' vs '{b.Style.WhiteSpace}'";
        if (b.Style.TextAlign != a.Style.TextAlign) yield return $"{where} text-align '{a.Style.TextAlign}' vs '{b.Style.TextAlign}'";
        if (b.Style.VerticalAlign != a.Style.VerticalAlign) yield return $"{where} vertical-align '{a.Style.VerticalAlign}' vs '{b.Style.VerticalAlign}'";
        if (!b.Style.TextIndent.Equals(a.Style.TextIndent)) yield return $"{where} text-indent {a.Style.TextIndent} vs {b.Style.TextIndent}";
        if (!b.Style.Margin.Equals(a.Style.Margin)) yield return $"{where} margin {a.Style.Margin} vs {b.Style.Margin}";
        if (!b.Style.Padding.Equals(a.Style.Padding)) yield return $"{where} padding {a.Style.Padding} vs {b.Style.Padding}";
        if (!b.Style.Width.Equals(a.Style.Width)) yield return $"{where} width {a.Style.Width} vs {b.Style.Width}";
        if (!b.Style.Height.Equals(a.Style.Height)) yield return $"{where} height {a.Style.Height} vs {b.Style.Height}";
        if (!b.Style.BorderStyles.Equals(a.Style.BorderStyles)) yield return $"{where} border-styles {a.Style.BorderStyles} vs {b.Style.BorderStyles}";
        if (b.Style.BorderTopWidth != a.Style.BorderTopWidth) yield return $"{where} border-top-width {a.Style.BorderTopWidth} vs {b.Style.BorderTopWidth}";
        if (b.Style.BorderRightWidth != a.Style.BorderRightWidth) yield return $"{where} border-right-width {a.Style.BorderRightWidth} vs {b.Style.BorderRightWidth}";
        if (b.Style.BorderBottomWidth != a.Style.BorderBottomWidth) yield return $"{where} border-bottom-width {a.Style.BorderBottomWidth} vs {b.Style.BorderBottomWidth}";
        if (b.Style.BorderLeftWidth != a.Style.BorderLeftWidth) yield return $"{where} border-left-width {a.Style.BorderLeftWidth} vs {b.Style.BorderLeftWidth}";
    }

    private static IEnumerable<String> LayoutDiffs(LayoutBox a, LayoutBox b, Int32 index)
    {
        var where = $"laid[{index}] {Where(a.Source)}";

        if (b.Kind != a.Kind) yield return $"{where} kind {a.Kind} vs {b.Kind}";
        if (b.Text != a.Text) yield return $"{where} text '{a.Text}' vs '{b.Text}'";
        if (!Near(a.BorderBox.X, b.BorderBox.X)) yield return $"{where} x {a.BorderBox.X:F2} vs {b.BorderBox.X:F2}";
        if (!Near(a.BorderBox.Y, b.BorderBox.Y)) yield return $"{where} y {a.BorderBox.Y:F2} vs {b.BorderBox.Y:F2}";
        if (!Near(a.BorderBox.Width, b.BorderBox.Width)) yield return $"{where} w {a.BorderBox.Width:F2} vs {b.BorderBox.Width:F2}";
        if (!Near(a.BorderBox.Height, b.BorderBox.Height)) yield return $"{where} h {a.BorderBox.Height:F2} vs {b.BorderBox.Height:F2}";
        if (!Near(a.Margin.Top, b.Margin.Top) || !Near(a.Margin.Right, b.Margin.Right) ||
            !Near(a.Margin.Bottom, b.Margin.Bottom) || !Near(a.Margin.Left, b.Margin.Left))
        {
            yield return $"{where} margin {a.Margin.Top}/{a.Margin.Right}/{a.Margin.Bottom}/{a.Margin.Left} " +
                         $"vs {b.Margin.Top}/{b.Margin.Right}/{b.Margin.Bottom}/{b.Margin.Left}";
        }

        if (!Near(a.Padding.Top, b.Padding.Top) || !Near(a.Padding.Right, b.Padding.Right) ||
            !Near(a.Padding.Bottom, b.Padding.Bottom) || !Near(a.Padding.Left, b.Padding.Left))
        {
            yield return $"{where} padding {a.Padding.Top}/{a.Padding.Right}/{a.Padding.Bottom}/{a.Padding.Left} " +
                         $"vs {b.Padding.Top}/{b.Padding.Right}/{b.Padding.Bottom}/{b.Padding.Left}";
        }

        if (!Near(a.Border.Top, b.Border.Top) || !Near(a.Border.Right, b.Border.Right) ||
            !Near(a.Border.Bottom, b.Border.Bottom) || !Near(a.Border.Left, b.Border.Left))
        {
            yield return $"{where} border {a.Border.Top}/{a.Border.Right}/{a.Border.Bottom}/{a.Border.Left} " +
                         $"vs {b.Border.Top}/{b.Border.Right}/{b.Border.Bottom}/{b.Border.Left}";
        }

        if (!Near(a.Baseline, b.Baseline)) yield return $"{where} baseline {a.Baseline:F2} vs {b.Baseline:F2}";
        if (b.Flow.Mode != a.Flow.Mode) yield return $"{where} flow {a.Flow.Mode} vs {b.Flow.Mode}";
        if (b.ImageUrl != a.ImageUrl) yield return $"{where} image '{a.ImageUrl}' vs '{b.ImageUrl}'";
    }

    private static Boolean Near(Double a, Double b) => Math.Abs(a - b) <= 0.5;

    /// <summary>
    /// The pages of the corpus, as the URLs they are loaded by: the embedded sample resources under
    /// <c>Samples/Pages</c>, named by the assembly rather than found on disk, so the corpus is the
    /// one the reader is given whatever machine the suite runs on.
    /// </summary>
    private static List<String> Pages()
    {
        const String prefix = "Samples.Pages.";

        return typeof(SampleUtils).Assembly
            .GetManifestResourceNames()
            .Where(name => name.Contains(prefix, StringComparison.Ordinal) &&
                           name.EndsWith(".htm", StringComparison.OrdinalIgnoreCase))
            .Select(name => name.Substring(name.IndexOf(prefix, StringComparison.Ordinal) + prefix.Length))
            .Select(tail => $"Pages/{tail}")
            .OrderBy(url => url, StringComparer.Ordinal)
            .ToList();
    }

    private static String Where(Box box)
    {
        if (box.Element is not { } element)
        {
            var text = box.Text ?? String.Empty;

            return $"<text> '{text.Substring(0, Math.Min(16, text.Length))}'";
        }

        return $"<{element.LocalName}" +
               $"{(element.Id is { Length: > 0 } id ? "#" + id : String.Empty)}" +
               $"{(element.ClassName is { Length: > 0 } name ? "." + name : String.Empty)}>";
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

    private static List<LayoutBox> Flatten(LayoutBox root)
    {
        var list = new List<LayoutBox>();
        Walk(root, list);
        return list;
    }

    private static void Walk(LayoutBox box, List<LayoutBox> into)
    {
        into.Add(box);

        foreach (var child in box.Children)
        {
            Walk(child, into);
        }
    }
}

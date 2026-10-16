using AngleSharp.Dom;
using Hutu.Rendering;
using Hutu.Rendering.Boxes;
using Hutu.Rendering.Layout;
using Hutu.Rendering.Media;
using Hutu.Rendering.Paint;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Hutu.Tests;

/// <summary>
/// A rebuild that only moved the colours keeps the geometry it has and takes the new styles.
/// </summary>
/// <remarks>
/// The pipeline is driven directly rather than through a view, because the answer worth asserting is
/// which tree and which layout survived: a picture that comes out the same either way would pass a
/// test that only looked at the paint list. Every case below states what the sheets declare, what
/// moved, and what the pipeline is therefore entitled to keep.
/// </remarks>
[TestFixture]
public sealed class RestyleInPlaceTests
{
    [Test]
    public async Task AColourChangeKeepsTheLayoutAndTheTree()
    {
        using var loader = new HutuViewInternal(new FakeTextMeasurer());
        var device = new HutuRenderDevice(800, 600);
        var pipeline = new RenderingPipeline(device, new FakeTextMeasurer());
        var document = await LoadAsync(loader, pipeline, Page(
            ".box { color: #111111 } .box:hover { color: #ff0000 }"));

        var tree = pipeline.BoxTree;
        var laid = pipeline.Layout;
        var stood = TextRun(pipeline, "text").BorderBox;

        MarkHover(document, "box");
        pipeline.Build(document);

        Assert.That(pipeline.BoxTree, Is.SameAs(tree), "the tree in hand is the one that stands");
        Assert.That(pipeline.Layout, Is.SameAs(laid), "and so is the layout it was measured against");

        Assert.That(PaintedColour(pipeline, "text"), Is.EqualTo(0xFFFF0000u), "the hover rule is what is drawn");
        Assert.That(TextRun(pipeline, "text").BorderBox, Is.EqualTo(stood), "and the run stands where it was put");
    }

    [Test]
    public async Task AnAnonymousBlockKeepsItsGeometry()
    {
        using var loader = new HutuViewInternal(new FakeTextMeasurer());
        var device = new HutuRenderDevice(800, 600);
        var pipeline = new RenderingPipeline(device, new FakeTextMeasurer());
        var document = await loader.SetHtmlDocumentAsync(
            "<html><head><style>.row:hover { color: #ff0000 }</style></head>" +
            "<body style='margin:0'><div class='row'><p>line</p><span>tail</span></div></body></html>");

        pipeline.Build(document);

        var tree = pipeline.BoxTree;
        var laid = pipeline.Layout;
        var stood = TextRun(pipeline, "tail").BorderBox;

        // The span beside the block is wrapped in a box the author never wrote, and that box borrows
        // its container's style - so a colour moving on the container moves the wrapper's style too.
        document.QuerySelectorAll(".row")[0].SetPseudoClass("hover", true);
        pipeline.Build(document);

        Assert.That(pipeline.BoxTree, Is.SameAs(tree), "the wrapper is shared, so the tree stands");
        Assert.That(pipeline.Layout, Is.SameAs(laid));
        Assert.That(TextRun(pipeline, "tail").BorderBox, Is.EqualTo(stood), "and the run has not moved");
        Assert.That(PaintedColour(pipeline, "tail"), Is.EqualTo(0xFFFF0000u), "while the colour has");
    }

    [Test]
    public async Task AWidthChangeLaysTheDocumentOutAgain()
    {
        using var loader = new HutuViewInternal(new FakeTextMeasurer());
        var device = new HutuRenderDevice(800, 600);
        var pipeline = new RenderingPipeline(device, new FakeTextMeasurer());
        var document = await LoadAsync(loader, pipeline, Page(
            ".box { width: 100px } .box:hover { width: 200px }"));

        var tree = pipeline.BoxTree;
        var laid = pipeline.Layout;

        Assert.That(WidthOf(pipeline), Is.EqualTo(100).Within(0.001));

        MarkHover(document, "box");
        pipeline.Build(document);

        Assert.That(pipeline.Layout, Is.Not.SameAs(laid), "a width is not a colour, so the layout goes");
        Assert.That(pipeline.BoxTree, Is.Not.SameAs(tree), "and the tree with it");
        Assert.That(WidthOf(pipeline), Is.EqualTo(200).Within(0.001));
    }

    [Test]
    public async Task AFontChangeLaysTheDocumentOutAgain()
    {
        using var loader = new HutuViewInternal(new FakeTextMeasurer());
        var device = new HutuRenderDevice(800, 600);
        var pipeline = new RenderingPipeline(device, new FakeTextMeasurer());
        var document = await LoadAsync(loader, pipeline, Page(
            ".box { font-size: 10px } .box:hover { font-size: 20px }"));

        var laid = pipeline.Layout;
        var size = TextRun(pipeline, "text").Font.Size;

        MarkHover(document, "box");
        pipeline.Build(document);

        Assert.That(pipeline.Layout, Is.Not.SameAs(laid), "a font size moves the line boxes");
        Assert.That(TextRun(pipeline, "text").Font.Size, Is.Not.EqualTo(size), "and the run is set in the new size");
    }

    [Test]
    public async Task APositionChangeLaysTheDocumentOutAgain()
    {
        using var loader = new HutuViewInternal(new FakeTextMeasurer());
        var device = new HutuRenderDevice(800, 600);
        var pipeline = new RenderingPipeline(device, new FakeTextMeasurer());
        var document = await LoadAsync(loader, pipeline, Page(
            ".box { width: 100px } .box:hover { position: absolute; top: 40px }"));

        var laid = pipeline.Layout;

        MarkHover(document, "box");
        pipeline.Build(document);

        // Positioning is not an area - it is built on demand and shares nothing - so the only thing
        // that keeps it honest is the gate: `position` is not a paint-only property.
        Assert.That(pipeline.Layout, Is.Not.SameAs(laid), "the box left the flow");
        Assert.That(TextRun(pipeline, "text").BorderBox.Y, Is.EqualTo(40).Within(0.001));
    }

    [Test]
    public async Task ATransformChangeLaysTheDocumentOutAgain()
    {
        using var loader = new HutuViewInternal(new FakeTextMeasurer());
        var device = new HutuRenderDevice(800, 600);
        var pipeline = new RenderingPipeline(device, new FakeTextMeasurer());
        var document = await LoadAsync(loader, pipeline, Page(
            ".box { width: 100px } .box:hover { transform: translate(10px, 20px) }"));

        var laid = pipeline.Layout;

        MarkHover(document, "box");
        pipeline.Build(document);

        // The matrix is resolved once the border box has its size, so it is materialised onto the
        // layout box: a style that carries a different one cannot keep the layout that has none.
        Assert.That(pipeline.Layout, Is.Not.SameAs(laid), "the transform is part of what is arranged");
        Assert.That(
            pipeline.Paint!.Commands.OfType<PushTransformCommand>().Any(),
            Is.True,
            "and the box is painted through it");
    }

    [Test]
    public async Task ABorderRadiusChangeLaysTheDocumentOutAgain()
    {
        using var loader = new HutuViewInternal(new FakeTextMeasurer());
        var device = new HutuRenderDevice(800, 600);
        var pipeline = new RenderingPipeline(device, new FakeTextMeasurer());
        var document = await LoadAsync(loader, pipeline, Page(
            ".box { width: 100px; border-radius: 0 } .box:hover { border-radius: 20px }"));

        var laid = pipeline.Layout;

        MarkHover(document, "box");
        pipeline.Build(document);

        // A radius is resolved against the box's own size once the tree has been placed, so it lives
        // on the layout box rather than only in the style.
        Assert.That(pipeline.Layout, Is.Not.SameAs(laid), "the radii are resolved from the placed box");
    }

    [Test]
    public async Task AnOverflowChangeLaysTheDocumentOutAgain()
    {
        using var loader = new HutuViewInternal(new FakeTextMeasurer());
        var device = new HutuRenderDevice(800, 600);
        var pipeline = new RenderingPipeline(device, new FakeTextMeasurer());
        var document = await LoadAsync(loader, pipeline, Page(
            ".box { width: 100px } .box:hover { overflow: hidden }"));

        var laid = pipeline.Layout;

        MarkHover(document, "box");
        pipeline.Build(document);

        Assert.That(pipeline.Layout, Is.Not.SameAs(laid), "the clip is materialised with the geometry");
    }

    [Test]
    public async Task AVisitedMarkRestylesInPlace()
    {
        using var loader = new HutuViewInternal(new FakeTextMeasurer());
        var device = new HutuRenderDevice(800, 600);
        var pipeline = new RenderingPipeline(device, new FakeTextMeasurer());
        var document = await loader.SetHtmlDocumentAsync(
            "<html><head><style>a { color: #0000ee } a:visited { color: #551a8b }</style></head>" +
            "<body style='margin:0'><a href='next.htm'>text</a></body></html>");

        pipeline.Build(document);

        var tree = pipeline.BoxTree;
        var laid = pipeline.Layout;

        Assert.That(PaintedColour(pipeline, "text"), Is.EqualTo(0xFF0000EEu));

        document.QuerySelectorAll("a")[0].SetPseudoClass("visited", true);
        pipeline.Build(document);

        Assert.That(pipeline.BoxTree, Is.SameAs(tree), "the visited mark only moves a colour");
        Assert.That(pipeline.Layout, Is.SameAs(laid));
        Assert.That(PaintedColour(pipeline, "text"), Is.EqualTo(0xFF551A8Bu));
    }

    [Test]
    public async Task ACustomPropertyClosesTheGate()
    {
        using var loader = new HutuViewInternal(new FakeTextMeasurer());
        var device = new HutuRenderDevice(800, 600);
        var pipeline = new RenderingPipeline(device, new FakeTextMeasurer());
        var document = await LoadAsync(loader, pipeline, Page(
            ".box { --tint: #111111; color: var(--tint) } .box:hover { --tint: #ff0000 }"));

        var laid = pipeline.Layout;

        MarkHover(document, "box");
        pipeline.Build(document);

        // A custom property feeds a var() anywhere in the document, so nothing can vouch for it
        // closing only the colour: the gate stays shut and the document is laid out again.
        Assert.That(pipeline.Layout, Is.Not.SameAs(laid), "a variable could be read by anything");
        Assert.That(PaintedColour(pipeline, "text"), Is.EqualTo(0xFFFF0000u));
    }

    [Test]
    public async Task APictureThatArrivesLaysTheDocumentOutAgain()
    {
        using var loader = new HutuViewInternal(new FakeTextMeasurer());
        var device = new HutuRenderDevice(800, 600);
        var images = new GrowingImages();
        var pipeline = new RenderingPipeline(device, new FakeTextMeasurer(), images);
        var document = await loader.SetHtmlDocumentAsync(
            "<html><body style='margin:0'>" +
            "<img src='http://example.com/picture.png'></body></html>");

        pipeline.Build(document);

        var laid = pipeline.Layout;
        var width = ImageWidth(pipeline);

        // Nothing about the marks moved, but a replaced box now answers another intrinsic size, and
        // a tree laid out from the old one is a tree the page never asked for.
        pipeline.Build(document);

        Assert.That(pipeline.Layout, Is.Not.SameAs(laid), "the picture changed the box it sits in");
        Assert.That(ImageWidth(pipeline), Is.Not.EqualTo(width), "and the box is the size of the picture");
    }

    [Test]
    public async Task AViewThatHoversAndLeavesAnswersAsAFreshOneDoes()
    {
        using var view = new HutuViewInternal(new FakeTextMeasurer());
        view.SetViewportSize(400, 600);
        await view.SetHtml(
            "<html><body style='margin:0'><style>" +
            ".box { color: #111111 } .box:hover { color: #ff0000 }" +
            "</style><div class='box'>text</div></body></html>");

        var box = Block(view, "div");

        Assert.That(PaintedColour(view, "text"), Is.EqualTo(0xFF111111u));

        view.PointerMoved(box.BorderBox.X + 1, box.BorderBox.Y + 1);
        Assert.That(PaintedColour(view, "text"), Is.EqualTo(0xFFFF0000u), "hovered");

        view.PointerMoved(1, box.BorderBox.Bottom + 30);
        Assert.That(PaintedColour(view, "text"), Is.EqualTo(0xFF111111u), "and back again");

        using var fresh = new HutuViewInternal(new FakeTextMeasurer());
        fresh.SetViewportSize(400, 600);
        await fresh.SetHtml(
            "<html><body style='margin:0'><style>" +
            ".box { color: #111111 } .box:hover { color: #ff0000 }" +
            "</style><div class='box'>text</div></body></html>");

        Assert.That(
            PaintedColour(view, "text"),
            Is.EqualTo(PaintedColour(fresh, "text")),
            "the reused tree answers as the freshly built one does");
        Assert.That(
            view.Layout!.Width,
            Is.EqualTo(fresh.Layout!.Width).Within(0.001),
            "and occupies the same frame");
        Assert.That(
            view.Layout!.Height,
            Is.EqualTo(fresh.Layout!.Height).Within(0.001),
            "to the same depth");
    }

    [Test]
    public async Task ASelectionSurvivesARestyleInPlace()
    {
        using var view = new HutuViewInternal(new FakeTextMeasurer());
        view.SetViewportSize(400, 600);
        await view.SetHtml(
            "<html><body style='margin:0'><style>" +
            ".box { color: #111111 } .box:hover { color: #ff0000 }" +
            "</style><p class='box' style='margin:0'>hello world</p></body></html>");

        var run = TextRun(view, "hello world");

        view.BeginSelection(run.BorderBox.X, run.BorderBox.Y + 1);
        view.ExtendSelection(run.BorderBox.Right - 1, run.BorderBox.Y + 1);

        Assert.That(view.SelectedText, Is.EqualTo("hello world"));

        view.PointerMoved(run.BorderBox.X + 1, run.BorderBox.Y + 1);

        // The layout was not replaced, so the runs the selection names are the ones still on screen
        // and the highlight is drawn against them without being rebuilt.
        Assert.That(view.SelectedText, Is.EqualTo("hello world"), "the selection is still there");
        Assert.That(
            view.Paint!.Commands.OfType<FillCommand>().Any(command => command.Color == Painter.DefaultHighlightColor),
            Is.True,
            "and still highlighted");
    }

    private static String Page(String css) =>
        "<html><head><style>" + css + "</style></head>" +
        "<body style='margin:0'><div class='box'>text</div></body></html>";

    private static async Task<IDocument> LoadAsync(HutuViewInternal loader, RenderingPipeline pipeline, String html)
    {
        var document = await loader.SetHtmlDocumentAsync(html);
        pipeline.Build(document);
        return document;
    }

    /// <summary>Places the hover mark the way a pointer moving onto the element would.</summary>
    private static void MarkHover(IDocument document, String cssClass)
    {
        foreach (var element in document.QuerySelectorAll("." + cssClass))
        {
            element.SetPseudoClass("hover", true);
        }
    }

    private static Double WidthOf(RenderingPipeline pipeline)
    {
        var box = Walk(pipeline.Layout!.Root).First(node =>
            node.Kind == LayoutKind.Block && node.Source.Element?.GetAttribute("class") == "box");

        return box.BorderBox.Width;
    }

    private static LayoutBox TextRun(RenderingPipeline pipeline, String text) =>
        Walk(pipeline.Layout!.Root).First(box => box.Kind == LayoutKind.Text && box.Text == text);

    private static LayoutBox TextRun(HutuViewInternal view, String text) =>
        Walk(view.Layout!.Root).First(box => box.Kind == LayoutKind.Text && box.Text == text);

    private static LayoutBox Block(HutuViewInternal view, String tag) =>
        Walk(view.Layout!.Root).First(box => box.Source.Element?.LocalName == tag);

    private static Double ImageWidth(RenderingPipeline pipeline) =>
        Walk(pipeline.Layout!.Root).First(box => box.Kind == LayoutKind.Image).BorderBox.Width;

    private static UInt32 PaintedColour(RenderingPipeline pipeline, String text) =>
        pipeline.Paint!.Commands.OfType<TextCommand>().First(command => command.Text == text).Color;

    private static UInt32 PaintedColour(HutuViewInternal view, String text) =>
        view.Paint!.Commands.OfType<TextCommand>().First(command => command.Text == text).Color;

    private static IEnumerable<LayoutBox> Walk(LayoutBox node)
    {
        yield return node;

        foreach (var child in node.Children)
        {
            foreach (var descendant in Walk(child))
            {
                yield return descendant;
            }
        }
    }

    /// <summary>
    /// An image provider that answers a size the first time it is asked and a bigger one after, which
    /// is what a picture arriving between two builds looks like to the pipeline.
    /// </summary>
    private sealed class GrowingImages : IImageProvider
    {
        private Boolean _asked;

        public ImageSize? GetSize(String url)
        {
            _asked = !_asked;
            return _asked ? new ImageSize(40, 20) : null;
        }
    }
}

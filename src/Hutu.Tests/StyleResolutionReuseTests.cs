using AngleSharp.Dom;
using Hutu.Rendering;
using Hutu.Rendering.Boxes;
using Hutu.Rendering.Layout;
using Hutu.Rendering.Paint;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Hutu.Tests;

/// <summary>
/// What a build reuses from the one before it, and what it must not.
/// </summary>
/// <remarks>
/// A build is a function of the document, the device and the marks the interaction state has
/// placed, so two builds that agree on those three may share the rule index and the caches behind
/// it - and a viewport that moved under an unchanged document may keep the box tree altogether.
/// Both are decided by reading the box tree and the index back out of the pipeline, which is the
/// only place the answer is honest: the same picture drawn twice would pass either way.
/// </remarks>
[TestFixture]
public sealed class StyleResolutionReuseTests
{
    [Test]
    public async Task AViewportTheStylesNeverReadKeepsTheBoxTree()
    {
        using var loader = new HutuViewInternal(new FakeTextMeasurer());
        var device = new HutuRenderDevice(800, 600);
        var pipeline = new RenderingPipeline(device, new FakeTextMeasurer());
        var document = await LoadAsync(loader, pipeline, Page(".box { width: 50% }"));

        var first = pipeline.BoxTree;

        Assert.That(WidthOf(pipeline), Is.EqualTo(400).Within(0.001));

        Assert.That(device.UpdateViewport(400, 600), Is.True);
        pipeline.Build(document);

        Assert.That(
            pipeline.BoxTree,
            Is.SameAs(first),
            "a percentage is left unresolved by the style resolution, so the tree read no viewport");
        Assert.That(pipeline.Layout!.Width, Is.EqualTo(400).Within(0.001), "and it is laid out at the new one");
        Assert.That(WidthOf(pipeline), Is.EqualTo(200).Within(0.001));
    }

    [Test]
    public async Task ACalcOfAShareAndADistanceKeepsTheBoxTree()
    {
        using var loader = new HutuViewInternal(new FakeTextMeasurer());
        var device = new HutuRenderDevice(800, 600);
        var pipeline = new RenderingPipeline(device, new FakeTextMeasurer());
        var document = await LoadAsync(loader, pipeline, Page(".box { width: calc(100% - 10px) }"));

        var first = pipeline.BoxTree;

        Assert.That(WidthOf(pipeline), Is.EqualTo(790).Within(0.001));

        Assert.That(device.UpdateViewport(400, 600), Is.True);
        pipeline.Build(document);

        // The share is left for the layout to take off the box, exactly as a plain percentage is, so the
        // style read no viewport and the tree it built stands.
        Assert.That(pipeline.BoxTree, Is.SameAs(first), "the calc keeps its share unresolved");
        Assert.That(WidthOf(pipeline), Is.EqualTo(390).Within(0.001), "and the new box answers the new share");
    }

    [Test]
    public async Task AViewportLengthBuildsTheTreeAgain()
    {
        using var loader = new HutuViewInternal(new FakeTextMeasurer());
        var device = new HutuRenderDevice(800, 600);
        var pipeline = new RenderingPipeline(device, new FakeTextMeasurer());
        var document = await LoadAsync(loader, pipeline, Page(".box { width: 5vw }"));

        var first = pipeline.BoxTree;

        Assert.That(WidthOf(pipeline), Is.EqualTo(40).Within(0.001));

        Assert.That(device.UpdateViewport(400, 600), Is.True);
        pipeline.Build(document);

        Assert.That(pipeline.BoxTree, Is.Not.SameAs(first), "a viewport unit is resolved while the style is read");
        Assert.That(WidthOf(pipeline), Is.EqualTo(20).Within(0.001));
    }

    [Test]
    public async Task AMediaQueryThatStoppedMatchingBuildsTheTreeAgain()
    {
        using var loader = new HutuViewInternal(new FakeTextMeasurer());
        var device = new HutuRenderDevice(800, 600);
        var pipeline = new RenderingPipeline(device, new FakeTextMeasurer());
        var document = await LoadAsync(loader, pipeline, Page(
            ".box { width: 200px } @media (min-width: 500px) { .box { width: 50px } }"));

        var first = pipeline.BoxTree;

        Assert.That(WidthOf(pipeline), Is.EqualTo(50).Within(0.001), "the query matched to begin with");

        Assert.That(device.UpdateViewport(400, 600), Is.True);
        pipeline.Build(document);

        Assert.That(pipeline.BoxTree, Is.Not.SameAs(first), "the query no longer matches, so the rules moved");
        Assert.That(WidthOf(pipeline), Is.EqualTo(200).Within(0.001));
    }

    [Test]
    public async Task AMediaQueryThatStartedMatchingBuildsTheTreeAgain()
    {
        using var loader = new HutuViewInternal(new FakeTextMeasurer());
        var device = new HutuRenderDevice(400, 600);
        var pipeline = new RenderingPipeline(device, new FakeTextMeasurer());
        var document = await LoadAsync(loader, pipeline, Page(
            ".box { width: 200px } @media (min-width: 500px) { .box { width: 50px } }"));

        var first = pipeline.BoxTree;

        Assert.That(WidthOf(pipeline), Is.EqualTo(200).Within(0.001), "the query did not match to begin with");

        Assert.That(device.UpdateViewport(800, 600), Is.True);
        pipeline.Build(document);

        Assert.That(pipeline.BoxTree, Is.Not.SameAs(first), "the query matches now, so the rules moved");
        Assert.That(WidthOf(pipeline), Is.EqualTo(50).Within(0.001));
    }

    [Test]
    public async Task ADifferentDocumentNeverSeesTheLastOnesStyles()
    {
        using var loader = new HutuViewInternal(new FakeTextMeasurer());
        var device = new HutuRenderDevice(800, 600);
        var pipeline = new RenderingPipeline(device, new FakeTextMeasurer());
        var first = await LoadAsync(loader, pipeline, Page(".box { width: 50px }"));

        Assert.That(WidthOf(pipeline), Is.EqualTo(50).Within(0.001));

        var second = await loader.SetHtmlDocumentAsync(Page(".box { width: 300px }"));
        pipeline.Build(second);

        Assert.That(WidthOf(pipeline), Is.EqualTo(300).Within(0.001), "the second document's own rule is the one read");
        Assert.That(pipeline.BoxTree, Is.Not.Null);
    }

    [Test]
    public async Task ADroppedTreeIsBuiltFromScratch()
    {
        using var loader = new HutuViewInternal(new FakeTextMeasurer());
        var device = new HutuRenderDevice(800, 600);
        var pipeline = new RenderingPipeline(device, new FakeTextMeasurer());
        var document = await LoadAsync(loader, pipeline, Page(".box { width: 50% }"));

        var first = pipeline.BoxTree;

        pipeline.Invalidate();

        Assert.That(device.UpdateViewport(400, 600), Is.True);
        pipeline.Build(document);

        Assert.That(pipeline.BoxTree, Is.Not.SameAs(first), "nothing is remembered once the tree has been dropped");
        Assert.That(WidthOf(pipeline), Is.EqualTo(200).Within(0.001));
    }

    [Test]
    public async Task ASecondBuildReadsTheIndexTheFirstOneBuilt()
    {
        using var loader = new HutuViewInternal(new FakeTextMeasurer());
        var device = new HutuRenderDevice(800, 600);
        var styles = new StyleResolution();
        var document = await loader.SetHtmlDocumentAsync(Page(".box { width: 50% }"));

        BoxBuilder.TryBuildFused(document, device, null, null, styles);
        var held = styles.Index;

        Assert.That(held, Is.Not.Null, "the first build remembered what it scanned");

        BoxBuilder.TryBuildFused(document, device, null, null, styles);

        Assert.That(styles.Index, Is.SameAs(held), "and the second one read the same sheets through it");
    }

    [Test]
    public async Task ARebuildThatChangesNoStyleKeepsThePicture()
    {
        using var view = new HutuViewInternal(new FakeTextMeasurer());
        view.SetViewportSize(400, 600);
        await view.SetHtml(
            "<html><body style='margin:0'><style>" +
            "a { color: #0000ff } a:hover { color: #ff0000 }" +
            "</style><a href='next.htm'>link</a></body></html>");

        var run = TextRun(view, "link");

        Assert.That(view.PointerMoved(run.BorderBox.X + 1, run.BorderBox.Y + 1).Changed, Is.True);
        Assert.That(PaintedColour(view, "link"), Is.EqualTo(0xFFFF0000u), "hovered");

        // The pointer leaves, which rebuilds the document against the same sheets and the same
        // viewport - the case the caches exist for.
        Assert.That(view.PointerMoved(1, run.BorderBox.Bottom + 30).Changed, Is.True);
        Assert.That(PaintedColour(view, "link"), Is.EqualTo(0xFF0000FFu), "and the picture is the one it was");

        // A resize afterwards is laid out rather than computed over, and has to come to the same
        // picture a document loaded at that size would.
        Assert.That(view.SetViewportSize(300, 600), Is.True);
        Assert.That(PaintedColour(view, "link"), Is.EqualTo(0xFF0000FFu));

        using var fresh = new HutuViewInternal(new FakeTextMeasurer());
        fresh.SetViewportSize(300, 600);
        await fresh.SetHtml(
            "<html><body style='margin:0'><style>" +
            "a { color: #0000ff } a:hover { color: #ff0000 }" +
            "</style><a href='next.htm'>link</a></body></html>");

        Assert.That(
            PaintedColour(view, "link"),
            Is.EqualTo(PaintedColour(fresh, "link")),
            "the reused tree answers as the freshly built one does");
        Assert.That(
            view.Layout!.Width,
            Is.EqualTo(fresh.Layout!.Width).Within(0.001),
            "and occupies the same frame");
    }

    [Test]
    public async Task ElementsThatDifferOnlyByAPresentationalHintDoNotShareAStyle()
    {
        using var view = new HutuViewInternal(new FakeTextMeasurer());
        view.SetViewportSize(400, 600);
        await view.SetHtml(
            "<html><body style='margin:0'>" +
            "<table><tr>" +
            "<td valign='top'>top</td>" +
            "<td valign='bottom'>bottom</td>" +
            "</tr></table>" +
            "</body></html>");

        Assert.That(
            TextRun(view, "top").Source.Style.VerticalAlign,
            Is.EqualTo("top"),
            "the hint is part of what a style cache has to key on");
        Assert.That(TextRun(view, "bottom").Source.Style.VerticalAlign, Is.EqualTo("bottom"));
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

    /// <summary>Gets the width of the box the <c>.box</c> element generated.</summary>
    private static Double WidthOf(RenderingPipeline pipeline)
    {
        var box = Walk(pipeline.Layout!.Root).First(node =>
            node.Kind == LayoutKind.Block && node.Source.Element?.GetAttribute("class") == "box");

        return box.BorderBox.Width;
    }

    private static LayoutBox TextRun(HutuViewInternal view, String text) =>
        Walk(view.Layout!.Root).First(box => box.Kind == LayoutKind.Text && box.Text == text);

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
}

using AngleSharp.Css;
using AngleSharp.Dom;
using Hutu.Rendering;
using Hutu.Rendering.Boxes;
using Hutu.Rendering.Layout;
using Hutu.Rendering.Media;
using Hutu.Rendering.Paint;
using Hutu.Skia;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace Hutu.Tests;

// Temporary: measures what a pointer move costs inside a document.
[TestFixture]
public sealed class HoverCostProbeTests
{
    [Test]
    public async Task Probe()
    {
        var report = new StringBuilder();

        foreach (var page in new[] { "index.htm", "Pages/lists-tables.htm" })
        {
            try
            {
                await Measure(page, report);
            }
            catch (Exception ex)
            {
                report.AppendLine($"{page}: {ex.GetType().Name}: {ex.Message}");
            }
        }

        File.WriteAllText(@"d:\_git4\Hutu\hover-probe.txt", report.ToString());
    }

    [Test]
    public async Task LayoutCost()
    {
        var report = new StringBuilder();

        report.AppendLine(await Compare("as written", null));
        report.AppendLine(await Compare("directory overflow visible", document =>
            document.QuerySelector(".directory")?.SetAttribute("style", "overflow: visible")));
        report.AppendLine(await Compare("directory no scroll, content no grid", document =>
        {
            document.QuerySelector(".directory")?.SetAttribute("style", "overflow: visible");
            document.QuerySelector(".content")?.SetAttribute("style", "display: block");
        }));
        report.AppendLine(await Compare("no grid anywhere", document =>
        {
            document.Body?.SetAttribute("style", "display: block");
            document.QuerySelector(".directory")?.SetAttribute("style", "overflow: visible");
            document.QuerySelector(".content")?.SetAttribute("style", "display: block");
            document.QuerySelector(".workspace")?.SetAttribute("style", "display: block");
        }));

        File.WriteAllText(@"d:\_git4\Hutu\layout-cost.txt", report.ToString());
    }

    [Test]
    public async Task Breakdown()
    {
        var report = new StringBuilder();

        using var skia = new SkiaTextMeasurer();
        var counting = new CountingMeasurer(skia);
        using var view = new HutuViewInternal(skia);
        view.AddEmbeddedResourceSource(typeof(Sample.Common.SampleUtils).Assembly, "Samples");
        view.SetViewportSize(900, 600);
        await view.LoadUrlAsync("index.htm");

        var device = new HutuRenderDevice(900, 600);
        var engine = new LayoutEngine(counting);
        var window = view.Document!.DefaultView!;

        _ = engine.Layout(
            BoxBuilder.Build(window.Render(device), device, (IImageProvider?)null),
            device.RenderWidth,
            device.RenderHeight);

        var boxTree = BoxBuilder.Build(window.Render(device), device, (IImageProvider?)null);

        LayoutProbe.Reset();
        counting.Reset();

        GC.Collect();
        GC.WaitForPendingFinalizers();

        var before = GC.GetTotalAllocatedBytes(true);
        var gen0 = GC.CollectionCount(0);

        var watch = Stopwatch.StartNew();
        _ = engine.Layout(boxTree, device.RenderWidth, device.RenderHeight);
        watch.Stop();

        var allocated = (GC.GetTotalAllocatedBytes(true) - before) / 1024.0 / 1024.0;
        var collections = GC.CollectionCount(0) - gen0;

        report.AppendLine(
            $"layout={watch.Elapsed.TotalMilliseconds:F1}ms " +
            $"measure={LayoutProbe.MeasureMs:F1}ms/{LayoutProbe.MeasureCalls}calls/{LayoutProbe.MeasureHits}hits " +
            $"arrange={LayoutProbe.ArrangeMs:F1}ms/{LayoutProbe.ArrangeCalls}calls " +
            $"inline={LayoutProbe.InlineMs:F1}ms/{LayoutProbe.InlineCalls}calls " +
            $"intrinsic={LayoutProbe.IntrinsicMs:F1}ms/{LayoutProbe.IntrinsicCalls}calls/{LayoutProbe.IntrinsicHits}hits/" +
            $"{LayoutProbe.IntrinsicUnique}unique/{LayoutProbe.ContentIntrinsicCalls}content " +
            $"text={counting.Ms:F1}ms/{counting.Calls}calls " +
            $"alloc={allocated:F1}MB gen0={collections}");

        File.WriteAllText(@"d:\_git4\Hutu\layout-breakdown.txt", report.ToString());
    }

    // Temporary: counts the calls the layout engine makes into the text backend.
    private sealed class CountingMeasurer : ITextMeasurer
    {
        private readonly ITextMeasurer _inner;

        public CountingMeasurer(ITextMeasurer inner) => _inner = inner;

        public Double Ms { get; private set; }

        public Int64 Calls { get; private set; }

        public void Reset()
        {
            Ms = 0;
            Calls = 0;
        }

        public FontMetrics GetMetrics(FontStyle style) => _inner.GetMetrics(style);

        public Double MeasureWidth(String text, FontStyle style)
        {
            var watch = Stopwatch.StartNew();
            var width = _inner.MeasureWidth(text, style);
            watch.Stop();

            Ms += watch.Elapsed.TotalMilliseconds;
            Calls++;
            return width;
        }
    }

    private static async Task<String> Compare(String label, Action<IDocument>? tweak)
    {
        using var measurer = new SkiaTextMeasurer();
        using var view = new HutuViewInternal(measurer);
        view.AddEmbeddedResourceSource(typeof(Sample.Common.SampleUtils).Assembly, "Samples");
        view.SetViewportSize(900, 600);
        await view.LoadUrlAsync("index.htm");

        var document = view.Document!;
        var window = document.DefaultView!;
        var device = new HutuRenderDevice(900, 600);
        var engine = new LayoutEngine(measurer);

        tweak?.Invoke(document);

        const Int32 warm = 5;
        const Int32 runs = 15;
        var style = 0.0;
        var boxes = 0.0;
        var layout = 0.0;

        for (var index = -warm; index < runs; index++)
        {
            var watch = Stopwatch.StartNew();
            var tree = window.Render(device);
            watch.Stop();
            var styleMs = watch.Elapsed.TotalMilliseconds;

            watch.Restart();
            var boxTree = BoxBuilder.Build(tree, device, (IImageProvider?)null);
            watch.Stop();
            var boxesMs = watch.Elapsed.TotalMilliseconds;

            watch.Restart();
            _ = engine.Layout(boxTree, device.RenderWidth, device.RenderHeight);
            watch.Stop();
            var layoutMs = watch.Elapsed.TotalMilliseconds;

            if (index >= 0)
            {
                style += styleMs;
                boxes += boxesMs;
                layout += layoutMs;
            }
        }

        return $"{label}: style={style / runs:F1}ms boxes={boxes / runs:F1}ms layout={layout / runs:F1}ms";
    }

    private static async Task Measure(String page, StringBuilder report)
    {
        using var measurer = new SkiaTextMeasurer();
        using var view = new HutuViewInternal(measurer);
        view.AddEmbeddedResourceSource(typeof(Sample.Common.SampleUtils).Assembly, "Samples");
        view.SetViewportSize(900, 600);

        var load = Stopwatch.StartNew();
        await view.LoadUrlAsync(page);
        load.Stop();

        var boxes = new List<LayoutBox>();
        Walk(view.Layout!.Root, boxes);

        var link = boxes.Find(box =>
            box.Kind is LayoutKind.Text &&
            ElementOf(box) is { } element &&
            element.Closest("a") is not null);

        if (link is null)
        {
            report.AppendLine($"{page}: boxes={boxes.Count} load={load.ElapsedMilliseconds}ms no link text found");
            return;
        }

        var x = link.BorderBox.X + (link.BorderBox.Width / 2);
        var y = link.BorderBox.Y + (link.BorderBox.Height / 2);

        // A point on the link and one on the page background, so moving between them changes what is
        // hovered every single time.
        const Double offX = 4;
        const Double offY = 4;

        view.PointerMoved(x, y);
        view.PointerMoved(offX, offY);

        var hit = HitTime(view, x, y);
        var still = Time(view, x, y, x, y);
        var crossing = Time(view, offX, offY, x, y);
        var entered = view.PointerMoved(x, y);
        var back = view.PointerMoved(offX, offY);

        report.AppendLine(
            $"{page}: boxes={boxes.Count} load={load.ElapsedMilliseconds}ms " +
            $"hit={hit:F3}ms still={still:F3}ms crossing={crossing:F3}ms " +
            $"entered={entered.Changed} left={back.Changed}");
    }

    private static Double Time(HutuViewInternal view, Double ax, Double ay, Double bx, Double by)
    {
        const Int32 runs = 60;

        // Warmed first, so the first run does not pay for what every later one has already built.
        view.PointerMoved(ax, ay);
        view.PointerMoved(bx, by);

        var watch = Stopwatch.StartNew();

        for (var index = 0; index < runs; index++)
        {
            view.PointerMoved(ax, ay);
            view.PointerMoved(bx, by);
        }

        watch.Stop();

        return watch.Elapsed.TotalMilliseconds / (runs * 2);
    }

    private static Double HitTime(HutuViewInternal view, Double x, Double y)
    {
        const Int32 runs = 200;

        view.LinkAt(x, y);

        var watch = Stopwatch.StartNew();

        for (var index = 0; index < runs; index++)
        {
            view.LinkAt(x, y);
        }

        watch.Stop();

        return watch.Elapsed.TotalMilliseconds / runs;
    }

    private static void Walk(LayoutBox box, List<LayoutBox> into)
    {
        into.Add(box);

        foreach (var child in box.Children)
        {
            Walk(child, into);
        }
    }

    private static IElement? ElementOf(LayoutBox box) => box.Source.Element ?? box.Source.Source?.ParentElement;
}

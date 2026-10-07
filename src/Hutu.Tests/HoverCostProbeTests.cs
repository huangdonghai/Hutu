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
using System.Linq;
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

    [Test]
    public async Task IntrinsicKeys()
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

        var boxTree = BoxBuilder.Build(window.Render(device), device, (IImageProvider?)null);

        LayoutProbe.RecordAsks = true;
        LayoutProbe.Reset();
        _ = engine.Layout(boxTree, device.RenderWidth, device.RenderHeight);
        LayoutProbe.RecordAsks = false;

        var asks = LayoutProbe.IntrinsicAsks;

        report.AppendLine($"total asks={asks.Count} boxes={asks.Select(a => a.Box).Distinct().Count()}");

        report.AppendLine();
        report.AppendLine("--- who asks ---");
        foreach (var group in asks.GroupBy(a => a.Site).OrderByDescending(g => g.Count()))
        {
            report.AppendLine(
                $"    {group.Key,-40} asks={group.Count(),-4} " +
                $"boxes={group.Select(a => a.Box).Distinct().Count(),-3} " +
                $"content={group.Count(a => a.Content)}");
        }

        report.AppendLine();
        report.AppendLine("--- worst boxes ---");
        var perBox = asks
            .GroupBy(a => (a.Box, a.Content))
            .OrderByDescending(g => g.Select(a => (a.Width, a.Height)).Distinct().Count())
            .Take(10);

        foreach (var group in perBox)
        {
            var keys = group.Select(a => (a.Width, a.Height)).Distinct().Count();
            var widths = group.Select(a => a.Width).Distinct().Count();

            report.AppendLine();
            report.AppendLine(
                $"  {group.Key.Box.Kind} <{Preview(group.Key.Box)}> content={group.Key.Content} " +
                $"calls={group.Count()} missKeys={keys} widths={widths}");

            foreach (var ask in group.Take(14))
            {
                report.AppendLine($"      w={ask.Width,-8:F1} h={ask.Height,-8:F1} {ask.Site}");
            }

            if (group.Count() > 14)
            {
                report.AppendLine($"      ... {group.Count() - 14} more");
            }
        }

        report.AppendLine();
        report.AppendLine("--- cache policies (misses = real computations) ---");
        Simulate(report, asks);

        report.AppendLine();
        report.AppendLine("--- basis dependence in the tree ---");
        var all = new List<Box>();
        Collect(boxTree!, all);
        var percent = all.Count(UsesPercent);
        var subtree = all.Count(box => SubtreeUsesPercent(box));
        report.AppendLine($"boxes={all.Count} own percentage length={percent} subtree carries one={subtree}");

        foreach (var box in all.Where(UsesPercent))
        {
            report.AppendLine($"    {box.Kind} <{Preview(box)}> w={box.Style.Width.Kind} h={box.Style.Height.Kind} " +
                $"pad={box.Style.Padding.Left.Kind} margin={box.Style.Margin.Left.Kind}");
        }

        File.WriteAllText(@"d:\_git4\Hutu\intrinsic-keys.txt", report.ToString());
    }

    /// <summary>Replays the ask sequence against alternative cache keys and counts the misses.</summary>
    private static void Simulate(StringBuilder report, List<IntrinsicAsk> asks)
    {
        Count(report, "current (box, w, h, cache)", asks, a => (a.Box, a.Width, a.Height, a.Content));
        Count(report, "shared  (box, w, h)      ", asks, a => (a.Box, a.Width, a.Height, false));
        Count(report, "width   (box, w)         ", asks, a => (a.Box, a.Width, false, false));
        Count(report, "box     (box)            ", asks, a => (a.Box, 0.0, 0.0, false));

        static void Count(StringBuilder report, String label, List<IntrinsicAsk> asks, Func<IntrinsicAsk, Object> key)
        {
            var seen = new HashSet<Object>();

            foreach (var ask in asks)
            {
                seen.Add(key(ask));
            }

            report.AppendLine($"    {label} distinct keys={seen.Count,-4} of {asks.Count} asks");
        }
    }

    /// <summary>Gets whether a style carries a length whose value depends on the containing block.</summary>
    private static Boolean UsesPercent(Box box)
    {
        var style = box.Style;

        return Percent(style.Width) || Percent(style.Height) ||
               Percent(style.MinWidth) || Percent(style.MaxWidth) ||
               Percent(style.MinHeight) || Percent(style.MaxHeight) ||
               Percent(style.Margin.Top) || Percent(style.Margin.Right) ||
               Percent(style.Margin.Bottom) || Percent(style.Margin.Left) ||
               Percent(style.Padding.Top) || Percent(style.Padding.Right) ||
               Percent(style.Padding.Bottom) || Percent(style.Padding.Left);
    }

    private static Boolean Percent(StyleLength length) => length.Kind == StyleLengthKind.Percent;

    private static Boolean SubtreeUsesPercent(Box box) =>
        UsesPercent(box) || box.Children.Any(SubtreeUsesPercent);

    private static void Collect(Box box, List<Box> into)
    {
        into.Add(box);

        foreach (var child in box.Children)
        {
            Collect(child, into);
        }
    }

    private static String Preview(Box box)
    {
        var text = box.Text ?? box.Element?.LocalName ?? box.Kind.ToString();
        return text.Length > 24 ? text[..24] : text;
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

    [Test]
    public async Task BasisFree()
    {
        var report = new StringBuilder();

        // Interleaved and repeated, so a drift over the run shows up as a pattern rather than being
        // read as the effect.
        report.AppendLine(await Compare("baseline  ", null, basisFree: false));
        report.AppendLine(await Compare("basis-free", null, basisFree: true));
        report.AppendLine(await Compare("basis-free", null, basisFree: true));
        report.AppendLine(await Compare("baseline  ", null, basisFree: false));

        File.WriteAllText(@"d:\_git4\Hutu\basis-free-ab.txt", report.ToString());
    }

    private static async Task<String> Compare(String label, Action<IDocument>? tweak, Boolean basisFree = true)
    {
        LayoutEngine.DisableBasisFreeCache = !basisFree;

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

        // One more pass with the counters cleared, so the row says what the layout asks for as well as
        // what it costs.
        var counted = BoxBuilder.Build(window.Render(device), device, (IImageProvider?)null);
        LayoutProbe.Reset();
        _ = engine.Layout(counted, device.RenderWidth, device.RenderHeight);

        return $"{label}: style={style / runs:F1}ms boxes={boxes / runs:F1}ms layout={layout / runs:F1}ms " +
            $"intrinsic={LayoutProbe.IntrinsicMs:F1}ms " +
            $"{LayoutProbe.IntrinsicCalls}+{LayoutProbe.ContentIntrinsicCalls}calls " +
            $"hits={LayoutProbe.IntrinsicHits}";
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

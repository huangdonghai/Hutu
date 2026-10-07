using AngleSharp.Css;
using AngleSharp.Dom;
using Hutu.Rendering;
using Hutu.Rendering.Boxes;
using Hutu.Rendering.Layout;
using Hutu.Rendering.Media;
using Hutu.Rendering.Paint;
using Hutu.Skia;
using NUnit.Framework;
using SkiaSharp;
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
    /// <summary>
    /// The switches are process-global statics, so a probe that leaves one thrown changes what every
    /// later probe measures. They were doing exactly that - <c>BasisFree</c> and <c>LayoutCost</c>
    /// flip <see cref="LayoutEngine.DisableBasisFreeCache"/> and never put it back, so whether a
    /// report described the shipped configuration depended on test order.
    /// <para>
    /// The three probes are off by default, so a layout nobody watches pays one static read per call.
    /// This is where they are turned on, and what is measured here is measured with them on.
    /// </para>
    /// </summary>
    [SetUp]
    public void WatchTheProbes()
    {
        LayoutEngine.DisableBasisFreeCache = false;
        LayoutEngine.AssertBasisFree = false;

        LayoutProbe.Off = false;
        StyleProbe.Off = false;
        RenderProbe.Off = false;
    }

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
            $"{LayoutProbe.ContentIntrinsicCalls}content " +
            $"text={counting.Ms:F1}ms/{counting.Calls}calls " +
            $"alloc={allocated:F1}MB gen0={collections}");

        File.WriteAllText(@"d:\_git4\Hutu\layout-breakdown.txt", report.ToString());
    }

    /// <summary>
    /// The whole render, stage by stage: style, box tree, layout, paint list, and the draw a host
    /// would do. The other probes split the first three by hand, which is why the paint list and the
    /// draw have never been measured, and why a pointer move could be timed without anyone being able
    /// to say where its time went.
    /// </summary>
    [Test]
    public async Task RenderCost()
    {
        var report = new StringBuilder();

        report.AppendLine("index.htm at 900x600. Wall clock throughout: Debug and Release differ by ~2.5x,");
        report.AppendLine("so a number only means anything next to another taken from the same build.");
        report.AppendLine();

        await RenderCostOf("index.htm", report);

        File.WriteAllText(@"d:\_git4\Hutu\render-cost.txt", report.ToString());
    }

    private static async Task RenderCostOf(String page, StringBuilder report)
    {
        using var measurer = new SkiaTextMeasurer();
        using var view = new HutuViewInternal(measurer);
        view.AddEmbeddedResourceSource(typeof(Sample.Common.SampleUtils).Assembly, "Samples");
        view.SetViewportSize(900, 600);

        // Cleared first, so "every build it made" below is this load's work and not the last test's.
        LayoutProbe.Reset();

        var load = Stopwatch.StartNew();
        await view.LoadUrlAsync(page);
        load.Stop();

        // A load builds the pipeline more than once, so the layout counters are a sum over all of its
        // builds - unlike the pipeline line, which describes the last one. The per-pass numbers are
        // further down, from a counted run that is reset first.
        report.AppendLine($"--- cold load ---");
        report.AppendLine($"  LoadUrlAsync           {load.Elapsed.TotalMilliseconds,8:F1}ms");
        report.AppendLine($"  last build, pipeline   {PipelineLine()}");
        report.AppendLine($"  every build it made    {InternalsLine()}");
        report.AppendLine($"    its contexts         {ContextLine()}");
        report.AppendLine();

        var paint = view.Paint;
        Assert.That(paint, Is.Not.Null, "the page should have painted something");

        // The pipeline stops at the paint list, so the draw is the backend's: a host hands the list to
        // the executor and the pixels come out here the same way.
        using var surface = SKSurface.Create(new SKImageInfo(
            Math.Max(1, (Int32)Math.Ceiling(paint!.Bounds.Width)),
            Math.Max(1, (Int32)Math.Ceiling(paint.Bounds.Height))));

        var executor = new SkiaPaintExecutor(measurer);
        var device = new HutuRenderDevice(900, 600);
        var window = view.Document!.DefaultView!;
        var engine = new LayoutEngine(measurer);
        var painter = new Painter();

        var tree = BoxBuilder.Build(window.Render(device), device, (IImageProvider?)null);
        Assert.That(tree, Is.Not.Null);

        const Int32 warm = 5;
        const Int32 runs = 15;

        Double styleMs = 0, boxesMs = 0, layoutMs = 0, paintMs = 0, drawMs = 0;
        var watch = new Stopwatch();

        for (var index = -warm; index < runs; index++)
        {
            watch.Restart();
            var renderTree = window.Render(device);
            watch.Stop();
            var style = watch.Elapsed.TotalMilliseconds;

            watch.Restart();
            var boxTree = BoxBuilder.Build(renderTree, device, (IImageProvider?)null);
            watch.Stop();
            var boxes = watch.Elapsed.TotalMilliseconds;

            watch.Restart();
            var layout = engine.Layout(boxTree, device.RenderWidth, device.RenderHeight);
            watch.Stop();
            var laid = watch.Elapsed.TotalMilliseconds;

            watch.Restart();
            var list = painter.Paint(layout!);
            watch.Stop();
            var painted = watch.Elapsed.TotalMilliseconds;

            watch.Restart();
            executor.Execute(list, surface.Canvas);
            watch.Stop();
            var drawn = watch.Elapsed.TotalMilliseconds;

            if (index < 0)
            {
                continue;
            }

            styleMs += style;
            boxesMs += boxes;
            layoutMs += laid;
            paintMs += painted;
            drawMs += drawn;
        }

        var total = styleMs + boxesMs + layoutMs + paintMs + drawMs;

        report.AppendLine($"--- re-layout, fixed box tree (mean of {runs}) ---");
        Row(report, "style", styleMs / runs, total / runs);
        Row(report, "boxes", boxesMs / runs, total / runs);
        Row(report, "layout", layoutMs / runs, total / runs);
        Row(report, "paintlist", paintMs / runs, total / runs);
        Row(report, "draw", drawMs / runs, total / runs);
        report.AppendLine($"  {"sum",-12}{total / runs,8:F1}ms");
        report.AppendLine();

        const Int32 allocRuns = 5;
        Double styleMb = 0, boxesMb = 0, layoutMb = 0, paintMb = 0, drawMb = 0;

        for (var index = 0; index < allocRuns; index++)
        {
            var (renderTree, a0) = Allocated(() => window.Render(device));
            var (boxTree, a1) = Allocated(() => BoxBuilder.Build(renderTree, device, (IImageProvider?)null));
            var (layout, a2) = Allocated(() => engine.Layout(boxTree, device.RenderWidth, device.RenderHeight));
            var (list, a3) = Allocated(() => painter.Paint(layout!));
            var (_, a4) = Allocated(() => Draw(executor, list, surface));

            styleMb += a0;
            boxesMb += a1;
            layoutMb += a2;
            paintMb += a3;
            drawMb += a4;
        }

        report.AppendLine($"--- allocated per stage (mean of {allocRuns}) ---");
        report.AppendLine($"  {"style",-12}{styleMb / allocRuns,8:F1}MB");
        report.AppendLine($"  {"boxes",-12}{boxesMb / allocRuns,8:F1}MB");
        report.AppendLine($"  {"layout",-12}{layoutMb / allocRuns,8:F1}MB");
        report.AppendLine($"  {"paintlist",-12}{paintMb / allocRuns,8:F1}MB");
        report.AppendLine($"  {"draw",-12}{drawMb / allocRuns,8:F1}MB");
        report.AppendLine($"  {"sum",-12}{(styleMb + boxesMb + layoutMb + paintMb + drawMb) / allocRuns,8:F1}MB");
        report.AppendLine();

        // One counted run, so the internals describe a single pass rather than a sum of fifteen.
        LayoutProbe.Reset();
        StyleProbe.Reset();
        _ = engine.Layout(tree, device.RenderWidth, device.RenderHeight);

        report.AppendLine($"--- layout internals, one pass ---");
        report.AppendLine($"  {InternalsLine()}");
        report.AppendLine($"  {ContextLine()}");
        report.AppendLine("  (inline is a subset of block: InlineLayout opens no context of its own)");
        report.AppendLine();

        StyleReads(report, tree!);
        DeclarationReads(report, tree!);

        // What the probes themselves cost. The counters are cheap; the two stopwatches and the stack
        // the context timers keep are not free. This does not measure deleting them - the call sites
        // run either way - only what their bodies are worth.
        report.AppendLine($"--- the probes' own cost (layout only, mean of {runs}) ---");

        foreach (var watching in new[] { true, false, false, true })
        {
            LayoutProbe.Off = !watching;
            StyleProbe.Off = !watching;

            report.AppendLine(
                $"  {(watching ? "probes on " : "probes off")}   layout {TimeLayout(engine, tree!, device, runs),8:F1}ms");
        }

        LayoutProbe.Off = false;
        StyleProbe.Off = false;
        report.AppendLine();

        report.AppendLine($"--- hover crossing, pointer moves that change what is hovered (mean of {runs * 2}) ---");
        Crossing(view, tree!, runs, report);
    }

    private static Double TimeLayout(LayoutEngine engine, Box tree, HutuRenderDevice device, Int32 runs)
    {
        for (var index = 0; index < 5; index++)
        {
            _ = engine.Layout(tree, device.RenderWidth, device.RenderHeight);
        }

        var watch = Stopwatch.StartNew();

        for (var index = 0; index < runs; index++)
        {
            _ = engine.Layout(tree, device.RenderWidth, device.RenderHeight);
        }

        watch.Stop();

        return watch.Elapsed.TotalMilliseconds / runs;
    }

    private static void Row(StringBuilder report, String name, Double ms, Double total) =>
        report.AppendLine($"  {name,-12}{ms,8:F1}ms {ms / total * 100,5:F1}%");

    /// <summary>
    /// What the read layer did during the counted pass, and what that is worth. The counts come from
    /// <see cref="StyleProbe"/>; the price per read comes from timing a few real property reads out of
    /// line, because timing each read where it happens would cost a fifth of the read and would
    /// perturb the very thing being measured.
    /// </summary>
    private static void StyleReads(StringBuilder report, Box tree)
    {
        // Read out first: the benchmark below performs millions of the very reads being counted, so
        // the counts have to be taken before it, and the probe has to be off while it runs or it
        // prices the instrument rather than the read.
        var lengthReads = StyleProbe.LengthReads;
        var lengthHits = StyleProbe.LengthHits;
        var textReads = StyleProbe.TextReads;
        var valueReads = StyleProbe.ValueReads;
        var byContext = ReadContextLine();
        var block = LayoutProbe.ContextMs[(Int32)ProbeContext.Block];

        var boxes = 0;
        var styles = new HashSet<ComputedStyle>();
        CollectStyles(tree, styles, ref boxes);

        const Int32 loops = 200_000;
        const Int32 shortLoops = 20_000;
        var checksum = 0.0;
        var style = tree.Style;

        Double lengthNs, shortNs, fontSizeNs, textNs, paddingNs, borderNs, fontNs;

        StyleProbe.Off = true;

        try
        {
            // Width twice at different lengths: a per-call cost that does not hold still is not CPU
            // time, and the rest of the numbers would be worthless.
            lengthNs = PerCall(() => (Int32)style.Width.Kind, loops, ref checksum);
            shortNs = PerCall(() => (Int32)style.Width.Kind, shortLoops, ref checksum);
            fontSizeNs = PerCall(() => style.FontSize, loops, ref checksum);
            textNs = PerCall(() => style.WhiteSpace.Length, loops, ref checksum);
            paddingNs = PerCall(() => (Int32)style.Padding.Top.Kind, loops, ref checksum);
            borderNs = PerCall(() => style.BorderTopWidth, loops, ref checksum);
            fontNs = PerCall(() => style.GetFont().Size, loops, ref checksum);
        }
        finally
        {
            StyleProbe.Off = false;
        }

        var reads = lengthReads + textReads + valueReads;

        // Every read priced at the cheapest one there is - a plain keyword lookup - and only the reads
        // the probe saw. This used to be reported as a range with a ceiling above it, priced at what a
        // length read cost on its own; that ceiling no longer exists, because a length read is now
        // cheaper than a keyword read. It no longer drags a resolution of the font size behind it,
        // which is the lazy font size working.
        var floor = reads * textNs;

        report.AppendLine($"--- style reads, one counted pass ---");
        report.AppendLine($"  length={lengthReads} found={lengthHits} text={textReads} value={valueReads}");
        report.AppendLine($"  by context, length/text/value:");
        report.AppendLine($"    {byContext}");
        report.AppendLine(
            $"  boxes={boxes} distinct styles={styles.Count} reads={reads} " +
            $"({reads / (Double)styles.Count:F1} per style)");
        report.AppendLine(
            $"  micro ns/call: width={lengthNs:F0} (200k) {shortNs:F0} (20k) fontSize={fontSizeNs:F0} " +
            $"whiteSpace={textNs:F0} padding={paddingNs:F0} border={borderNs:F0} font={fontNs:F0}");
        report.AppendLine($"  checksum {checksum:F0} (kept so the JIT cannot elide the reads)");
        report.AppendLine($"  fontSize on its own {fontSizeNs:F0}ns, asked for once per read that needs one");
        report.AppendLine(
            $"  floor against block's {block:F1}ms: {floor / 1_000_000.0:F1}ms " +
            $"({floor / 1_000_000.0 / Math.Max(block, 0.001) * 100:F0}% of it)");
        report.AppendLine($"  the line to read: 30% or more, memoise; under 10%, drop it");
        report.AppendLine();
    }

    /// <summary>
    /// Prices the two ways a declaration can be read. The text is what the keyword readers compare and
    /// what the length reader asks for before it has even looked at the value; the typed value beside
    /// it answers the same lookup without building a string.
    /// </summary>
    /// <remarks>
    /// Three things are separated here, because they are three different fixes: the lookup itself, the
    /// serialisation <c>GetPropertyValue</c> does on top of it, and the shorthand expansion a longhand
    /// pays for when only its shorthand was declared.
    /// </remarks>
    private static void DeclarationReads(StringBuilder report, Box tree)
    {
        // A style that really declared a padding, so the longhand rows measure an expansion rather
        // than a miss - the miss is a different path and would price the wrong thing.
        var padded = StyleWith(tree, "padding");
        var style = padded ?? tree.Style;
        var declaration = style.Declaration;

        // If the declaration answers nothing at all, every row below would be pricing a miss.
        Assert.That(
            declaration.GetPropertyValue("display"),
            Is.Not.Empty,
            "a computed declaration should always carry display; without it these rows measure nothing");

        const Int32 loops = 100_000;
        var checksum = 0.0;

        var lookup = PerCall(() => declaration.GetProperty("white-space") is null ? 0 : 1, loops, ref checksum);
        var keywordTyped = PerCall(() => declaration.GetProperty("white-space")?.RawValue is null ? 0 : 1, loops, ref checksum);
        var keywordText = PerCall(() => declaration.GetPropertyValue("white-space").Length, loops, ref checksum);

        var widthTyped = PerCall(() => declaration.GetProperty("width")?.RawValue is null ? 0 : 1, loops, ref checksum);
        var widthText = PerCall(() => declaration.GetPropertyValue("width").Length, loops, ref checksum);

        var longhandTyped = PerCall(() => declaration.GetProperty("padding-top")?.RawValue is null ? 0 : 1, loops, ref checksum);
        var longhandText = PerCall(() => declaration.GetPropertyValue("padding-top").Length, loops, ref checksum);

        // What the length reader would stop paying: it asks for the text unconditionally and then reads
        // the typed value anyway, so the text call is the one a fix removes.
        var lengthReads = StyleProbe.LengthReads;
        var saving = lengthReads * widthText / 1_000_000.0;

        report.AppendLine($"--- declaration reads: text vs typed (ns/call, {loops / 1000}k loops) ---");
        report.AppendLine(
            $"  padding declared on the style measured: {padded is not null}   checksum {checksum:F0}");
        // display is a keyword the declaration does carry, so this pair isolates the serialisation of
        // a keyword from the cost of looking one up. white-space below is carried by neither, which is
        // why its two numbers are the same: both are the fallback search.
        var carriedTyped = PerCall(() => declaration.GetProperty("display")?.RawValue is null ? 0 : 1, loops, ref checksum);
        var carriedText = PerCall(() => declaration.GetPropertyValue("display").Length, loops, ref checksum);

        report.AppendLine($"  lookup alone            {lookup,7:F0}");
        report.AppendLine($"  carried keyword typ/txt {carriedTyped,7:F0} / {carriedText,7:F0}   (display, in the index)");
        report.AppendLine($"  missed  keyword typ/txt {keywordTyped,7:F0} / {keywordText,7:F0}   (white-space, absent)");
        report.AppendLine($"  length  typed / text    {widthTyped,7:F0} / {widthText,7:F0}");
        report.AppendLine($"  longhand typed / text   {longhandTyped,7:F0} / {longhandText,7:F0}");
        report.AppendLine(
            $"  the text call the length reader makes costs {widthText:F0}ns; " +
            $"x{lengthReads} reads = {saving:F1}ms of block's {LayoutProbe.ContextMs[(Int32)ProbeContext.Block]:F1}ms");

        // Why a lookup is 1.5us rather than 20ns, and whether that is even the interesting question:
        // a miss costs the shorthand search, but a declaration that does not carry the property at all
        // would mean the readers are answering with their fallbacks and the cost is the least of it.
        report.AppendLine($"  the names Hutu reads, priced ({loops / 1000}k loops):");

        Price(report, "root", tree.Style.Declaration, loops, ref checksum);
        Price(report, "picked", declaration, loops, ref checksum);

        // How many of the tree's styles carry these at all, since a declaration that answers nothing
        // would mean the keyword readers are running on their fallbacks.
        var styles = new HashSet<ComputedStyle>();
        var boxes = 0;
        CollectStyles(tree, styles, ref boxes);

        var display = 0;
        var family = 0;

        foreach (var candidate in styles)
        {
            if (candidate.Declaration.GetPropertyValue("display").Length > 0)
            {
                display++;
            }

            if (candidate.Declaration.GetPropertyValue("font-family").Length > 0)
            {
                family++;
            }
        }

        report.AppendLine(
            $"  of {styles.Count} distinct styles, {display} carry display and {family} carry font-family");

        // Which properties the read layer keeps coming back to. This is the question a memo has to
        // answer, and it is asked per property rather than for the totals: one property read per
        // fragment per line is a different proposition from one read per box.
        report.AppendLine($"  the properties read most (a read is one logical ask, not one call):");

        foreach (var (kind, map) in new (String, Dictionary<String, Int64>)[]
        {
            ("length", StyleProbe.LengthByName),
            ("text", StyleProbe.TextByName),
            ("value", StyleProbe.ValueByName),
        })
        {
            foreach (var entry in map.OrderByDescending(pair => pair.Value).Take(24))
            {
                report.AppendLine($"    {kind,-7} {entry.Key,-22} {entry.Value}");
            }
        }

        report.AppendLine();
    }

    private static void Price(
        StringBuilder report,
        String label,
        AngleSharp.Css.Dom.ICssStyleDeclaration declaration,
        Int32 loops,
        ref Double checksum)
    {
        foreach (var name in new[]
        {
            "display", "position", "white-space", "text-align", "font-family",
        })
        {
            var value = declaration.GetPropertyValue(name);
            var ns = PerCall(() => declaration.GetProperty(name) is null ? 0 : 1, loops, ref checksum);

            report.AppendLine($"    {label,-7} {name,-14} {ns,7:F0}ns  value={value.Length}");
        }
    }

    /// <summary>Finds a box whose style declares the named property, or null when none does.</summary>
    private static ComputedStyle? StyleWith(Box box, String name)
    {
        if (box.Style.Declaration.GetProperty(name) is not null)
        {
            return box.Style;
        }

        foreach (var child in box.Children)
        {
            if (StyleWith(child, name) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    /// <summary>
    /// Times one property read, in nanoseconds. The delegate call is inside the measurement, so every
    /// number here is a few nanoseconds high.
    /// </summary>
    private static Double PerCall(Func<Double> read, Int32 loops, ref Double checksum)
    {
        var warm = 0.0;

        for (var index = 0; index < 2000; index++)
        {
            warm += read();
        }

        var watch = Stopwatch.StartNew();
        var total = 0.0;

        for (var index = 0; index < loops; index++)
        {
            total += read();
        }

        watch.Stop();
        checksum += warm + total;

        return watch.Elapsed.TotalNanoseconds / loops;
    }

    private static void CollectStyles(Box box, HashSet<ComputedStyle> into, ref Int32 boxes)
    {
        boxes++;
        into.Add(box.Style);

        foreach (var child in box.Children)
        {
            CollectStyles(child, into, ref boxes);
        }
    }

    private static String ReadContextLine()
    {
        var line = new StringBuilder();

        foreach (var context in Enum.GetValues<ProbeContext>())
        {
            var index = (Int32)context;

            line.Append($"{context.ToString().ToLowerInvariant()}: ");
            line.Append($"{StyleProbe.ContextLengthReads[index]}/");
            line.Append($"{StyleProbe.ContextTextReads[index]}/");
            line.Append($"{StyleProbe.ContextValueReads[index]}   ");
        }

        return line.ToString().TrimEnd();
    }

    private static Int32 Draw(SkiaPaintExecutor executor, PaintList list, SKSurface surface)
    {
        executor.Execute(list, surface.Canvas);
        return 0;
    }

    private static (T Value, Double Mb) Allocated<T>(Func<T> action)
    {
        var before = GC.GetTotalAllocatedBytes(true);
        var value = action();

        return (value, (GC.GetTotalAllocatedBytes(true) - before) / 1024.0 / 1024.0);
    }

    private static String PipelineLine() =>
        $"style={RenderProbe.StyleMs:F1}ms boxes={RenderProbe.BoxesMs:F1}ms " +
        $"layout={RenderProbe.LayoutMs:F1}ms paint={RenderProbe.PaintMs:F1}ms " +
        $"total={RenderProbe.TotalMs:F1}ms reused={RenderProbe.ReusedLayout}";

    private static String InternalsLine() =>
        $"measure={LayoutProbe.MeasureMs:F1}ms/{LayoutProbe.MeasureCalls}calls/{LayoutProbe.MeasureHits}hits " +
        $"arrange={LayoutProbe.ArrangeMs:F1}ms/{LayoutProbe.ArrangeCalls}calls " +
        $"inline={LayoutProbe.InlineMs:F1}ms/{LayoutProbe.InlineCalls}calls " +
        $"intrinsic={LayoutProbe.IntrinsicMs:F1}ms/{LayoutProbe.IntrinsicCalls}calls";

    /// <summary>
    /// The time each context spent on its own work. They add up to the work done rather than nesting,
    /// so a grid's number is its own arithmetic and not the block measurement of its items.
    /// </summary>
    private static String ContextLine()
    {
        var line = new StringBuilder();

        foreach (var context in Enum.GetValues<ProbeContext>())
        {
            var index = (Int32)context;

            line.Append($"{context.ToString().ToLowerInvariant()}=");
            line.Append($"{LayoutProbe.ContextMs[index]:F1}ms/{LayoutProbe.ContextCalls[index]} ");
        }

        return line.ToString().TrimEnd();
    }

    private static void Crossing(HutuViewInternal view, Box tree, Int32 runs, StringBuilder report)
    {
        var boxes = new List<LayoutBox>();
        Walk(view.Layout!.Root, boxes);

        var link = boxes.Find(box => box.Kind is LayoutKind.Text && ElementOf(box)?.Closest("a") is not null);

        if (link is null)
        {
            report.AppendLine("  no link text found");
            return;
        }

        var x = link.BorderBox.X + (link.BorderBox.Width / 2);
        var y = link.BorderBox.Y + (link.BorderBox.Height / 2);

        // A point on the link and one on the background, so moving between them changes the hovered
        // element every single time.
        view.PointerMoved(x, y);
        view.PointerMoved(4, 4);

        var watch = Stopwatch.StartNew();

        for (var index = 0; index < runs; index++)
        {
            view.PointerMoved(4, 4);
            view.PointerMoved(x, y);
        }

        watch.Stop();

        report.AppendLine($"  end to end             {watch.Elapsed.TotalMilliseconds / (runs * 2),8:F1}ms");
        report.AppendLine($"  one crossing           {PipelineLine()}");
        report.AppendLine(
            "  a pointer move does not draw - the host draws from the paint list it is handed - so the " +
            $"end to end number covers the four pipeline stages only. boxes={Count(tree)}");
    }

    private static Int32 Count(Box box)
    {
        var total = 1;

        foreach (var child in box.Children)
        {
            total += Count(child);
        }

        return total;
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

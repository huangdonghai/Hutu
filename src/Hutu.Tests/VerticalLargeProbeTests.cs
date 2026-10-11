using AngleSharp;
using AngleSharp.Css.Parser;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
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

/// <summary>
/// What one render of <c>Pages/vertical-large.htm</c> costs, stage by stage - including the two the
/// render pipeline itself never times on their own, the HTML parse and the CSS parse.
/// </summary>
/// <remarks>
/// Every stage is timed in a loop of its own, with whatever it needs built once, outside the clock,
/// and then reused for every run. Timing the pipeline end to end in one loop would charge each stage
/// for the garbage the ones before it left, and these stages differ in what they allocate by more
/// than an order of magnitude: the box tree's hundreds of megabytes beside the paint list's tens.
/// <para>
/// The two parsers are timed on the source rather than inside a load, because a load has no point
/// between them to put a stopwatch: a <c>&lt;style&gt;</c> is parsed the moment its closing tag is
/// seen, and a <c>&lt;link&gt;</c>'s sheet is fetched and parsed as a task the document waits on
/// before it is ready. What is timed here is the same text the load really parsed - the document's
/// own sheets read back out of it, and the reader's user-agent sheet - parsed again.
/// </para>
/// <para>
/// The one stage that has two implementations - the build, the cascade and the box tree together -
/// is not put in the table below but compared against itself in a section of its own. The two ways
/// differ in what they are: the render tree's is the css cascade and then the box builder, the
/// fused one is <see cref="BoxBuilder.TryBuildFused"/> over the DOM. Putting both in one table and
/// summing it would charge a render for a build it never did, so the table holds the render-tree
/// stages alone and the two builds are set side by side, in time and in bytes, underneath.
/// </para>
/// <para>
/// The render-tree build - those two stages - is the <b>deprecated</b> path: the engine now renders
/// through the fused build, so its css-cascade and box-builder figures are historical, not live
/// costs, and must never be optimised as if they were. They survive only to be held against the
/// fused build in the "one build, two ways" section, where a faster fused number already shows the
/// saving.
/// </para>
/// <para>
/// A build that came out cheaper by coming out different is no saving at all, so the comparison
/// ends by laying out and painting the fused tree and holding the counts it reaches against the
/// render tree's. The fused path abstains on a document it does not yet reproduce whole, and a null
/// there would have been timed as though it were the fastest build of the three - so that is
/// caught too, before any number is written down.
/// </para>
/// <para>
/// Explicit, because it is a measurement and not a check: three timed loops of eight runs each with
/// three warm-ups apiece take a couple of minutes, and it leaves <c>vertical-large-profile.txt</c>
/// beside the repository root, which a plain run of the suite has no business doing. A number it
/// reports means something only next to another from the same build, so it is asked for by name -
/// <c>dotnet test src/Hutu.Tests/Hutu.Tests.csproj --filter "FullyQualifiedName~VerticalLargeProbeTests"</c>
/// - and nothing about it belongs in the count the rest of the suite is judged by.
/// </para>
/// </remarks>
[TestFixture]
[Explicit]
public sealed class VerticalLargeProbeTests
{
    private const String Page = "Pages/vertical-large.htm";

    /// <summary>
    /// The repository root: where the report is left, and what the page is read from. Found by walking
    /// up from the test's own directory rather than written down, so that the one fixture which writes
    /// a file writes it where this checkout keeps it, on any machine and at any path.
    /// </summary>
    private static readonly String Root = RepositoryRoot();

    /// <summary>The page to profile, relative to <see cref="Root"/>.</summary>
    private const String SourcePath = "samples/Sample.Common/Samples/Pages/vertical-large.htm";

    private const Int32 Width = 900;

    private const Int32 Height = 600;

    private const Int32 Warm = 3;

    private const Int32 Runs = 8;

    private const Int32 AllocRuns = 3;

    /// <summary>How many times the page is repeated to make a longer document.</summary>
    private static readonly Int32[] Repeats = { 1, 4, 8 };

    /// <summary>
    /// Guards the one switch this fixture shares with the engine: the basis-free assertion is a
    /// process-global static, and another fixture may have turned it on. Left on, it measures every
    /// basis-free cache hit a second time with a reference engine, which would land in the layout
    /// figure below as though it were layout work.
    /// </summary>
    [SetUp]
    public void TurnTheAssertionOff() => LayoutEngine.AssertBasisFree = false;

    [Test]
    public async Task Profile()
    {
        var report = new StringBuilder();

        using var measurer = new SkiaTextMeasurer();
        using var view = new HutuViewInternal(measurer);
        view.AddEmbeddedResourceSource(typeof(Sample.Common.SampleUtils).Assembly, "Samples");
        view.SetViewportSize(Width, Height);

        var load = Stopwatch.StartNew();
        await view.LoadUrlAsync(Page);
        load.Stop();

        var document = view.Document!;
        var device = new HutuRenderDevice(Width, Height);
        var engine = new LayoutEngine(measurer);
        var painter = new Painter();
        var executor = new SkiaPaintExecutor(measurer);

        var source = File.ReadAllText(Path.Combine(Root, SourcePath));

        // The sheets the load really parsed, as text: the reader's user-agent sheet, then the
        // document's own. Serialising them happens once, here, and is not part of what is timed.
        var sheets = new List<String> { new UserAgentStyleSheetProvider().Default.ToCss() };

        foreach (var sheet in document.StyleSheets.OfType<AngleSharp.Css.Dom.ICssStyleSheet>())
        {
            sheets.Add(sheet.ToCss());
        }

        // A context without the CSS services, so the HTML parse is the HTML parse and nothing else:
        // AngleSharp starts parsing a <style> the moment its closing tag is seen, but only when a
        // styling service is registered at all.
        var htmlParser = BrowsingContext.New(Configuration.Default).GetService<IHtmlParser>();
        var cssParser = document.Context.GetService<ICssParser>();

        Assert.That(htmlParser, Is.Not.Null, "the default configuration carries an HTML parser");
        Assert.That(cssParser, Is.Not.Null, "the browsing context carries a CSS parser");

        var fusedBoxes = FusedBoxes(document, device);
        Assert.That(fusedBoxes, Is.Not.Null, "the page uses nothing the fused path abstains on");
        Assert.That(view.Paint, Is.Not.Null);

        using var surface = SKSurface.Create(new SKImageInfo(
            Math.Max(1, (Int32)Math.Ceiling(view.Paint!.Bounds.Width)),
            Math.Max(1, (Int32)Math.Ceiling(view.Paint.Bounds.Height))));

        var laidOut = new List<LayoutBox>();
        Walk(view.Layout!.Root, laidOut);

        report.AppendLine(
            $"{Page} at {Width}x{Height}: {source.Length / 1024}KB of source, " +
            $"{document.QuerySelectorAll("*").Length} elements, {Count(fusedBoxes!)} boxes, " +
            $"{laidOut.Count} laid out, {sheets.Count} sheets ({sheets.Sum(s => s.Length)}B of CSS).");
        report.AppendLine("Wall clock, Debug build throughout: a number means something only next to another");
        report.AppendLine($"from the same build. Each stage is the mean of {Runs}, warmed {Warm} times.");
        report.AppendLine();

        report.AppendLine("--- cold load ---");
        report.AppendLine($"  LoadUrlAsync          {load.Elapsed.TotalMilliseconds,8:F1}ms   html + css + a full build");
        report.AppendLine();

        var steps = new List<Step>
        {
            new("html parser", null, _ => htmlParser!.ParseDocument(source)),
            new("css parser", null, _ => ParseAll(cssParser!, sheets)),

            // The live build: BoxBuilder.TryBuildFused over the DOM. This is the stage the engine
            // uses, so its time and bytes are the ones worth reading.
            new(
                "fused build",
                null,
                _ => FusedBoxes(document, device)),
            new(
                "layout",
                () => FusedBoxes(document, device),
                input => engine.Layout((Box?)input, device.RenderWidth, device.RenderHeight)),
            new(
                "paint list",
                () => FusedLaid(document, device, engine),
                input => painter.Paint((LayoutResult?)input!)),
            new(
                "draw",
                () => painter.Paint(FusedLaid(document, device, engine)),
                input => Draw(executor, (PaintList?)input, surface)),
        };

        // Where the stages named below sit in the list above.
        const Int32 LayoutStep = 3;
        const Int32 PaintStep = 4;

        var times = new Double[steps.Count];
        var allocs = new Double[steps.Count];

        for (var index = 0; index < steps.Count; index++)
        {
            times[index] = Timed(steps[index].Prepare, steps[index].Run);
            allocs[index] = Allocated(steps[index]);
        }

        var total = times.Sum();

        report.AppendLine($"--- one render, stage by stage (mean of {Runs}) ---");

        for (var index = 0; index < steps.Count; index++)
        {
            Row(report, steps[index].Name, times[index], total);
        }

        report.AppendLine($"  {"sum",-14}{total,8:F1}ms");
        report.AppendLine(
            $"  {"parse",-14}{times[0] + times[1],8:F1}ms {(times[0] + times[1]) / total * 100,5:F1}%   " +
            $"what a load pays before a single style is computed");
        report.AppendLine(
            $"  {"render/paint",-14}{times[LayoutStep] + times[PaintStep],8:F1}ms " +
            $"{(times[LayoutStep] + times[PaintStep]) / total * 100,5:F1}%   " +
            $"the paint list and the drawing of it");
        report.AppendLine();

        report.AppendLine($"--- allocated per stage (mean of {AllocRuns}) ---");

        for (var index = 0; index < steps.Count; index++)
        {
            report.AppendLine($"  {steps[index].Name,-14}{allocs[index],8:F1}MB");
        }

        report.AppendLine();



        // The live layout phase split, on the fused box tree - this is the number the engine pays on
        // the live path. Fused styles carry their resolved values, so the heavy cascade-path getters
        // (notably BackgroundClip's ReadKeyword + ToLowerInvariant per box) do not run here.
        var liveProfile = engine.Profile(fusedBoxes, device.RenderWidth, device.RenderHeight);
        var liveTotal = liveProfile.TotalTime;

        report.AppendLine($"--- layout, by phase - fused box tree, LIVE path (mean of {Runs}) ---");
        Row(report, "measure", liveProfile.MeasureTime, liveTotal);
        Row(report, "arrange", liveProfile.ArrangeTime, liveTotal);
        Row(report, "decorations", liveProfile.DecorationTime, liveTotal);
        Row(report, "overflow", liveProfile.OverflowTime, liveTotal);
        report.AppendLine($"  {"sum",-14}{liveTotal,8:F1}ms");
        report.AppendLine();
        report.AppendLine($"--- layout allocation, by phase - fused box tree, LIVE path (mean of {AllocRuns}) ---");
        report.AppendLine($"  {"measure",-14}{liveProfile.MeasureAllocMB,8:F1}MB");
        report.AppendLine($"  {"arrange",-14}{liveProfile.ArrangeAllocMB,8:F1}MB");
        report.AppendLine($"  {"decorations",-14}{liveProfile.DecorationAllocMB,8:F1}MB");
        report.AppendLine($"  {"overflow",-14}{liveProfile.OverflowAllocMB,8:F1}MB");
        report.AppendLine();

        // Paint on the LIVE fused tree. Everything Paint reads (BackgroundColor, Backgrounds,
        // BorderStyles, Color, ...) is a resolved value here, so this is the true live cost of the
        // paint list, and the one worth optimising against.
        var fusedLaid = engine.Layout(fusedBoxes, device.RenderWidth, device.RenderHeight)!;
        var fusedPaintStep = new Step("fused paint", () => fusedLaid, _ => painter.Paint((LayoutResult?)_!));
        var fusedPaintTime = Timed(fusedPaintStep.Prepare, fusedPaintStep.Run);
        var fusedPaintAlloc = Allocated(fusedPaintStep);

        report.AppendLine($"--- paint list, LIVE fused path (mean of {Runs} / {AllocRuns} for bytes) ---");
        report.AppendLine($"  {"fused",-14}{fusedPaintTime,8:F1}ms {fusedPaintAlloc,8:F1}MB");
        report.AppendLine();

        // What a scroll costs. The paint list is the whole document's and a repaint is one screenful
        // of it: the canvas is put where a scroller would have put it, and the area the executor skips
        // by is read off that canvas' clip. Repeating the page 1, 4 and 8 times over is what shows the
        // cost does not follow the document - which is all the claim there can be, because a command
        // that is skipped is invisible by definition and no picture could tell the two apart. What it
        // saves is worth reading next to the stage table above, where `draw` paints every command into
        // a surface the size of the document and so has nothing to skip.
        report.AppendLine($"--- one screen repaint at {Width}x{Height}, by page length (mean of {Runs}) ---");

        using var screen = SKSurface.Create(new SKImageInfo(Width, Height));

        foreach (var repeat in Repeats)
        {
            await view.SetHtml(Repeat(source, repeat), Page);

            var painted = painter.Paint(engine.Layout(
                FusedBoxes(view.Document!, device)!,
                device.RenderWidth,
                device.RenderHeight)!);

            // Scrolled to the middle of the document, on whichever axis it grew along - a book page
            // set vertically grows sideways, and this one is read that way.
            var scroll = new Offset(
                Math.Max(0, (painted.Bounds.Width - Width) / 2),
                Math.Max(0, (painted.Bounds.Height - Height) / 2));

            var repaint = Timed(_ =>
            {
                screen.Canvas.Save();
                screen.Canvas.Translate((Single)(-scroll.X), (Single)(-scroll.Y));
                executor.Execute(painted, screen.Canvas, scroll);
                screen.Canvas.Restore();
                return null;
            });

            report.AppendLine(
                $"  {repeat,3}x  document={painted.Bounds.Width,6:F0}x{painted.Bounds.Height,5:F0}px " +
                $"commands={painted.Commands.Count,7} repaint={repaint,7:F1}ms");
        }

        report.AppendLine();

        File.WriteAllText(Path.Combine(Root, "vertical-large-profile.txt"), report.ToString());


    }

    /// <summary>
    /// Walks up from the directory the test assembly was loaded from to the one that holds the
    /// build-wide settings, which is the repository root. The page below is read from there and the
    /// report is left there, so this is the one path the fixture has to know and it finds it rather
    /// than being told: a path written down is right on one machine and wrong on every other.
    /// </summary>
    private static String RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null &&
               !File.Exists(Path.Combine(directory.FullName, "Directory.Packages.props")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? AppContext.BaseDirectory;
    }

    private static Object? ParseAll(ICssParser parser, List<String> sheets)
    {
        AngleSharp.Css.Dom.ICssStyleSheet? last = null;

        foreach (var sheet in sheets)
        {
            last = parser.ParseStyleSheet(sheet);
        }

        return last;
    }

    private static Box? FusedBoxes(IDocument document, HutuRenderDevice device) =>
        BoxBuilder.TryBuildFused(document, device, (IImageProvider?)null);

    private static LayoutResult FusedLaid(IDocument document, HutuRenderDevice device, LayoutEngine engine) =>
        engine.Layout(FusedBoxes(document, device), device.RenderWidth, device.RenderHeight)!;

    /// <summary>
    /// The page with its body repeated, which is how a document long enough to tell the two figures of
    /// a repaint apart is made without a fixture for every length. What is repeated is the body's own
    /// markup, so the copy carries the sheets, the styles and the structure the first one has.
    /// </summary>
    private static String Repeat(String source, Int32 times)
    {
        if (times <= 1)
        {
            return source;
        }

        var open = source.IndexOf('>', source.IndexOf("<body", StringComparison.OrdinalIgnoreCase));
        var close = source.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);

        if (open < 0 || close < open)
        {
            return source;
        }

        var body = source[(open + 1)..close];

        return source[..(open + 1)] + String.Concat(Enumerable.Repeat(body, times)) + source[close..];
    }

    private static Object? Draw(SkiaPaintExecutor executor, PaintList? list, SKSurface surface)
    {
        executor.Execute(list!, surface.Canvas);
        return null;
    }

    /// <summary>Times a stage that needs nothing built for it.</summary>
    private static Double Timed(Func<Object?, Object?> run) => Timed(null, run);

    /// <summary>
    /// Times a stage that needs something built for it first. What it needs is built once, outside
    /// the clock, and every run - the warm ones included - is handed that same input.
    /// </summary>
    /// <remarks>
    /// The same input is sound because nothing here writes to it, and because the one stage that
    /// keeps a cache does not keep it across passes: <see cref="LayoutEngine.Layout"/> clears all
    /// five of its dictionaries in its first lines, and the box builder makes a fresh intern table
    /// per call. So a second pass over an old tree does the whole of the work the first one did.
    /// <para>
    /// Building the input once is what makes the number the stage's own. Rebuilding it for every run
    /// meant building the render tree immediately before timing the box tree, which put 180MB on the
    /// heap in front of a stage that allocates a fifth of that - and the same again, a render, a box
    /// tree and a layout, in front of the paint list. What came out was as much a report on the
    /// collector as on the stage. Rebuilding also cost three quarters of the run: the paint list's
    /// own 118ms was preceded by 3.3s of preparation, eleven times over.
    /// </para>
    /// </remarks>
    private static Double Timed(Func<Object?>? prepare, Func<Object?, Object?> run)
    {
        var input = prepare?.Invoke();

        for (var index = 0; index < Warm; index++)
        {
            _ = run(input);
        }

        GC.Collect();
        GC.WaitForPendingFinalizers();

        var total = 0.0;
        Object? kept = null;

        for (var index = 0; index < Runs; index++)
        {
            var watch = Stopwatch.StartNew();
            kept = run(input);
            watch.Stop();
            total += watch.Elapsed.TotalMilliseconds;
        }

        GC.KeepAlive(kept);
        GC.KeepAlive(input);

        return total / Runs;
    }

    /// <summary>
    /// What one run of a stage allocates, in megabytes. The input is built once, as in
    /// <see cref="Timed(Func{Object?}?, Func{Object?, Object?})"/>, so the figure is the stage's own
    /// garbage and not the garbage of everything that fed it.
    /// </summary>
    private static Double Allocated(Step step)
    {
        var input = step.Prepare?.Invoke();
        var total = 0.0;

        for (var index = 0; index < AllocRuns; index++)
        {
            var before = GC.GetTotalAllocatedBytes(true);
            GC.KeepAlive(step.Run(input));
            total += (GC.GetTotalAllocatedBytes(true) - before) / 1024.0 / 1024.0;
        }

        GC.KeepAlive(input);

        return total / AllocRuns;
    }

    private static void Row(StringBuilder report, String name, Double ms, Double total) =>
        report.AppendLine($"  {name,-14}{ms,8:F1}ms {ms / total * 100,5:F1}%");

    private static void Walk(LayoutBox box, List<LayoutBox> into)
    {
        into.Add(box);

        foreach (var child in box.Children)
        {
            Walk(child, into);
        }
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

    /// <summary>One stage of the render, and what has to be built for it.</summary>
    private sealed record Step(
        String Name,
        Func<Object?>? Prepare,
        Func<Object?, Object?> Run);
}

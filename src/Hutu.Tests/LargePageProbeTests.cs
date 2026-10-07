using AngleSharp.Css;
using AngleSharp.Dom;
using Hutu.Rendering;
using Hutu.Rendering.Boxes;
using Hutu.Rendering.Layout;
using Hutu.Skia;
using NUnit.Framework;
using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace Hutu.Tests;

/// <summary>
/// Temporary: how building and laying out a document behave as it grows, on a page generated here
/// rather than loaded from the samples.
/// </summary>
/// <remarks>
/// The sample pages are small enough that a cost proportional to the number of elements cannot be told
/// apart from one proportional to what they declare - which is the whole question the style areas were
/// built to answer. This page repeats one row, so the number of elements grows while the number of
/// distinct declarations does not, and the two costs separate.
/// <para>
/// The declaration reads are counted, because that number is what the design is about: a page four
/// times as long should not cost four times as many reads per box. Before the style areas the sample's
/// index page paid 186 reads a box - 15104 for 81 of them - and that figure does not depend on the
/// document, so it stands in for what this page would have paid without them.
/// </para>
/// </remarks>
[TestFixture]
public sealed class LargePageProbeTests
{
    /// <summary>
    /// The probes are off by default, so a layout nobody watches pays one static read per call. This
    /// is where they are turned on for this fixture, the way HoverCostProbeTests does for its own.
    /// </summary>
    [SetUp]
    public void WatchTheProbes()
    {
        StyleProbe.Off = false;
        LayoutProbe.Off = false;
    }

    [Test]
    public async Task LargePage()
    {
        var report = new StringBuilder();

        report.AppendLine("a generated page of repeated rows at 900x600.");
        report.AppendLine("wall clock throughout, so a number means something only next to another from the same build.");
        report.AppendLine();

        foreach (var rows in new[] { 50, 200, 700 })
        {
            await Measure(rows, report);
        }

        File.WriteAllText(@"d:\_git4\Hutu\large-page.txt", report.ToString());
    }

    private static async Task Measure(Int32 rows, StringBuilder report)
    {
        using var measurer = new SkiaTextMeasurer();
        using var view = new HutuViewInternal(measurer);
        view.SetViewportSize(900, 600);
        await view.SetHtml(Document(rows));

        var device = new HutuRenderDevice(900, 600);
        var window = view.Document!.DefaultView!;
        var engine = new LayoutEngine(measurer);

        // Warmed twice, so what is measured is the work and not the first pass's tier-up.
        for (var index = 0; index < 2; index++)
        {
            _ = engine.Layout(BoxBuilder.Build(window.Render(device), device), device.RenderWidth, device.RenderHeight);
        }

        GC.Collect();
        GC.WaitForPendingFinalizers();

        StyleProbe.Reset();
        var before = GC.GetTotalAllocatedBytes(true);
        var watch = Stopwatch.StartNew();
        var tree = BoxBuilder.Build(window.Render(device), device);
        watch.Stop();
        var built = watch.Elapsed.TotalMilliseconds;
        var builtMb = (GC.GetTotalAllocatedBytes(true) - before) / 1024.0 / 1024.0;
        var builtReads = Reads();

        // Read out before the layout, because the reset below would zero them and the areas are
        // resolved while the tree is built - not while it is laid out.
        var untouched = StyleProbe.AreasUntouched;
        var areas = StyleProbe.AreasBuilt;
        var interned = StyleProbe.InternHits;
        var fromParent = StyleProbe.ParentsAdopted;

        var boxes = Count(tree!);

        StyleProbe.Reset();
        before = GC.GetTotalAllocatedBytes(true);
        watch.Restart();
        _ = engine.Layout(tree, device.RenderWidth, device.RenderHeight);
        watch.Stop();
        var laid = watch.Elapsed.TotalMilliseconds;
        var laidMb = (GC.GetTotalAllocatedBytes(true) - before) / 1024.0 / 1024.0;
        var laidReads = Reads();

        report.AppendLine(
            $"rows={rows,-4} boxes={boxes,-6} " +
            $"build={built,7:F1}ms/{builtMb,6:F1}MB reads={builtReads,-7}{builtReads / (Double)boxes,5:F1}/box   " +
            $"layout={laid,7:F1}ms/{laidMb,6:F1}MB reads={laidReads,-7}{laidReads / (Double)boxes,5:F1}/box");
        report.AppendLine(
            $"      areas: untouched={untouched} built={areas} interned={interned} fromParent={fromParent}");
    }

    private static Int64 Reads() =>
        StyleProbe.LengthReads + StyleProbe.TextReads + StyleProbe.ValueReads;

    /// <summary>
    /// Repeated rows, each an anchor and a run of text inside a bordered box with a padding, which is
    /// most of what a long document is made of.
    /// </summary>
    private static String Document(Int32 rows)
    {
        var html = new StringBuilder("<html><body style='margin:0'>");

        for (var index = 0; index < rows; index++)
        {
            html.Append("<div style='padding:4px;border:1px solid #cccccc;margin-bottom:2px'>");
            html.Append($"<a href='#item{index}' style='color:#003366'>Item {index}</a>");
            html.Append("<span style='margin-left:8px'>a line of text long enough to reach the end of the row</span>");
            html.Append("</div>");
        }

        return html.Append("</body></html>").ToString();
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
}

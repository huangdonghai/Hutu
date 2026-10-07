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
/// Deliberately written against the public surface only, with no probes: it has to compile and run on
/// the code from before the areas as well, to say what they were worth.
/// </para>
/// </remarks>
[TestFixture]
public sealed class LargePageProbeTests
{
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

        var before = GC.GetTotalAllocatedBytes(true);
        var watch = Stopwatch.StartNew();
        var tree = BoxBuilder.Build(window.Render(device), device);
        watch.Stop();
        var built = watch.Elapsed.TotalMilliseconds;
        var builtMb = (GC.GetTotalAllocatedBytes(true) - before) / 1024.0 / 1024.0;

        var boxes = Count(tree!);

        before = GC.GetTotalAllocatedBytes(true);
        watch.Restart();
        _ = engine.Layout(tree, device.RenderWidth, device.RenderHeight);
        watch.Stop();
        var laid = watch.Elapsed.TotalMilliseconds;
        var laidMb = (GC.GetTotalAllocatedBytes(true) - before) / 1024.0 / 1024.0;

        report.AppendLine(
            $"rows={rows,-4} boxes={boxes,-6} " +
            $"build={built,8:F1}ms/{builtMb,7:F1}MB  layout={laid,8:F1}ms/{laidMb,7:F1}MB");
    }

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

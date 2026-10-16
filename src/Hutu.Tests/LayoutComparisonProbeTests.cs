using AngleSharp.Dom;
using Hutu.Rendering.Layout;
using Hutu.Skia;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace Hutu.Tests;

/// <summary>
/// Dumps the box of every element on a live page as TSV, path-addressed the way the Chrome-side
/// extractor addresses it (tag.classes with per-parent sibling indexes, chained from body), so the
/// two can be joined and diffed. Not a test: run with
/// <c>HUTU_URL=... HUTU_WIDTH=3840 HUTU_HEIGHT=2160 dotnet test --filter LayoutComparisonProbe</c>.
/// </summary>
[TestFixture]
[Explicit]
public sealed class LayoutComparisonProbeTests
{
    private static readonly Int32 Width = Setting("HUTU_WIDTH", 3840);
    private static readonly Int32 Height = Setting("HUTU_HEIGHT", 2160);

    private static String Url =>
        Environment.GetEnvironmentVariable("HUTU_URL") ?? "https://www.dongchedi.com/";

    [Test]
    [NonParallelizable]
    [CancelAfter(600_000)]
    public async Task DumpBoxes()
    {
        using var images = new SkiaImageLoader();
        using var measurer = new SkiaTextMeasurer();
        using var view = new HutuViewInternal(measurer, images);
        view.SetViewportSize(Width, Height);

        await view.LoadUrlAsync(Url, CancellationToken.None);

        var document = view.Document!;
        var layout = view.Layout!;

        var rows = new List<String>();

        if (document.Body is { } body)
        {
            WalkPage(body, layout, rows);
        }

        var header = $"# viewport {Width}x{Height} content {layout.Width:F1}x{layout.Height:F1} elements {rows.Count}";
        var path = Path.Combine(RepositoryRoot(), "build", "engine-boxes.tsv");
        File.WriteAllLines(path, new[] { header }.Concat(rows));

        TestContext.Progress.WriteLine(header);
        TestContext.Progress.WriteLine("wrote " + path);
    }

    /// <summary>The tags the Chrome-side extractor writes no row for either; children are still walked.</summary>
    private static readonly HashSet<String> NoBoxes = new(StringComparer.OrdinalIgnoreCase)
    {
        "script", "style", "link", "meta", "head", "title", "base", "template",
    };

    private static Int32 Setting(String name, Int32 fallback) =>
        Int32.TryParse(Environment.GetEnvironmentVariable(name), out var value) && value > 0 ? value : fallback;

    private static void WalkPage(IElement element, LayoutResult layout, List<String> rows)
    {
        if (!NoBoxes.Contains(element.TagName))
        {
            var box = layout.FindBox(element);

            rows.Add(box is null
                ? $"{PathOf(element)}\t{element.TagName.ToLowerInvariant()}\tN\tN\tN\tN"
                : $"{PathOf(element)}\t{element.TagName.ToLowerInvariant()}" +
                  $"\t{box.BorderBox.X:F1}\t{box.BorderBox.Y:F1}\t{box.BorderBox.Width:F1}\t{box.BorderBox.Height:F1}");
        }

        foreach (var child in element.Children)
        {
            WalkPage(child, layout, rows);
        }
    }

    private static String PathOf(IElement element)
    {
        var parts = new List<String>();

        for (IElement? node = element;
             node is not null && node.NodeType == NodeType.Element &&
             !String.Equals(node.TagName, "HTML", StringComparison.OrdinalIgnoreCase);
             node = node.ParentElement)
        {
            var segment = SegmentOf(node);
            var parent = node.ParentElement;

            if (parent is not null)
            {
                var same = parent.Children.Where(child => SegmentOf(child) == segment).ToList();
                segment += ":" + (same.IndexOf(node) + 1);
            }

            parts.Insert(0, segment);
        }

        return String.Join(">", parts);
    }

    private static String SegmentOf(IElement node)
    {
        var tag = node.TagName.ToLowerInvariant();
        var classes = (node.GetAttribute("class") ?? String.Empty)
            .Split((Char[]?)null, StringSplitOptions.RemoveEmptyEntries);

        return classes.Length == 0 ? tag : tag + "." + String.Join(".", classes);
    }

    private static String RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Hutu.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? AppContext.BaseDirectory;
    }
}

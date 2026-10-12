using AngleSharp;
using AngleSharp.Css;
using AngleSharp.Css.Dom;
using AngleSharp.Css.RenderTree;
using AngleSharp.Dom;
using AngleSharp.Io;
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
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Hutu.Tests;

/// <summary>
/// What loading a real portal page - <c>https://www.163.com/</c> - actually costs, in the places the
/// engine never times: the HTML fetch, what the document waits on before it is ready, the pictures,
/// and what one build of it is made of.
/// </summary>
/// <remarks>
/// Explicit and network-dependent: it asks a live site for a page, so what it reports is that site on
/// that day, and it leaves nothing behind but what it printed. A number here means something only
/// next to another from the same build, which is why the stages are printed in one run rather than
/// pieced together from several.
/// <c>dotnet test src/Hutu.Tests/Hutu.Tests.csproj --filter "FullyQualifiedName~PortalLoadProbeTests"</c>
/// </remarks>
[TestFixture]
[Explicit]
public sealed class PortalLoadProbeTests
{
    private const String Url = "https://www.163.com/";

    private const Int32 Width = 1440;

    private const Int32 Height = 900;

    /// <summary>How many of the page's images are fetched by hand, to price a picture on this line.</summary>
    private const Int32 ImageSample = 16;

    [Test]
    [NonParallelizable]
    [CancelAfter(480_000)]
    public async Task Probe()
    {
        var stopwatch = Stopwatch.StartNew();
        var html = await FetchHtmlAsync();
        Line($"html fetch            {stopwatch.Elapsed.TotalMilliseconds,9:F0}ms   {html.Length / 1024}KB");

        // The three contexts differ only in what the loader is allowed to fetch, so the differences
        // between them are the price of the subresources rather than of the parse. Note that the
        // middle one gets no external sheet at all: registering the resource loader is what lets a
        // <link> be fetched, so a page's css is bought together with its pictures.
        var bare = await OpenInAsync("no loader at all", NewContext(null), html);
        var css = await OpenInAsync("css only          ", NewContext(false), html);
        var full = await OpenInAsync("as the app loads  ", NewContext(true), html);

        Line("");
        Line("--- the document that came back ---");
        Line($"  sheets, no loader   {DescribeSheets(bare)}");
        Line($"  sheets, css loader  {DescribeSheets(css)}");
        Line($"  sheets, full loader {DescribeSheets(full)}");
        Line($"  elements      {NumberOf(full.QuerySelectorAll("*"))}");
        Line($"  <img>         {NumberOf(full.GetElementsByTagName("img"))}");
        Line($"  <a>           {NumberOf(full.GetElementsByTagName("a"))}");
        Line($"  <link>        {NumberOf(full.GetElementsByTagName("link"))}");
        Line($"  <table>       {NumberOf(full.GetElementsByTagName("table"))}");
        Line($"  <iframe>      {NumberOf(full.GetElementsByTagName("iframe"))}");
        Line($"  <svg>         {NumberOf(full.GetElementsByTagName("svg"))}");

        Line("");
        Line("--- one render, the engine's own stages ---");
        var device = new HutuRenderDevice(Width, Height);

        var faces = Timed("font face table", () => FontFaceTable.From(full));
        var boxes = Timed("fused build", () => BoxBuilder.TryBuildFused(full, device, (IImageProvider?)null, faces));
        Line($"  fused build returned {(boxes is null ? "NULL - the page falls back to the render tree" : "a box tree")}");

        var built = boxes ?? Timed(
            "render-tree build",
            () => BoxBuilder.Build(
                Timed("render tree", () => full.DefaultView!.Render(device)),
                device,
                (IImageProvider?)null,
                faces));

        using var measurer = new SkiaTextMeasurer();
        var layout = Timed("layout", () => new LayoutEngine(measurer).Layout(built, Width, Height));
        var paint = Timed("paint list", () => new Painter().Paint(layout!));

        Line("");
        Line($"  laid out      {Walk(layout!.Root).Count} boxes, {layout.Width:F0}x{layout.Height:F0} document, {paint!.Commands.Count()} paint commands");

        var urls = Timed("image url collect", () => ImageUrlCollector.Collect(full, device));
        Line($"  images to fetch {urls.Count}");

        if (urls.Count > 0)
        {
            await SampleFetchAsync(urls);
        }

        Line("");
        Line("--- the same load through the engine's own door ---");
        await EndToEndAsync();

        Line("");
        Line("--- where a build's time goes (a rebuild is what hovering and resizing cost) ---");
        var index = Timed("rule index", () => RuleBucketIndex.Build(SheetsOf(full), device));
        var cold = Timed(
            "cascades, fresh index",
            () =>
            {
                var fresh = RuleBucketIndex.Build(SheetsOf(full), device);
                var counted = 0;

                foreach (INode node in full.QuerySelectorAll("*"))
                {
                    _ = MatchedCascade.ForElement(fresh, (IElement)node);
                    counted++;
                }

                return counted;
            });
        var warm = Timed("fused build again", () => BoxBuilder.TryBuildFused(full, device, (IImageProvider?)null, faces));
        GC.KeepAlive(index);
        GC.KeepAlive(warm);
        Line($"  the {cold} elements of the page matched against a fresh index; the rest of the build is the styles and the boxes");

        Line("");
        await StalledHostAsync();

        Line("");
        Line($"probe wall clock {stopwatch.Elapsed.TotalSeconds:F1}s");
    }

    private void Line(String text) => TestContext.Progress.WriteLine(text);

    /// <summary>The containers the portal's whole page hangs on, in the order the document meets them.</summary>
    private static readonly String[] Selectors =
    {
        "#js_index2017_wrap", "#js_N_nav_left", ".ns_area_first", ".col_lm", ".col_r", ".mod_tech", ".footer",
    };

    /// <summary>
    /// Where the page's main containers land, against what a browser puts them at. The viewport below
    /// is the one the browser was measured on - 1642 css pixels wide, its own 1657 less the scrollbar -
    /// so the two sets of numbers are about the same box.
    /// </summary>
    [Test]
    [NonParallelizable]
    [CancelAfter(120_000)]
    public async Task Landmarks()
    {
        using var measurer = new SkiaTextMeasurer();
        using var view = new HutuViewInternal(measurer);
        view.SetViewportSize(1642, 1263);

        await view.LoadDocumentAsync(Url);

        Line($"  Hutu: {view.Layout!.Width:F0}x{view.Layout.Height:F0} document, " +
             $"{Walk(view.Layout.Root).Count} boxes laid out");
        Line("  a browser at the same viewport: 1642x8440");

        foreach (var selector in Selectors)
        {
            var taken = 0;

            foreach (INode node in view.Document!.QuerySelectorAll(selector))
            {
                if (taken++ == 4)
                {
                    break;
                }

                var box = view.Layout.FindBox((IElement)node);
                var rect = box?.BorderBox;

                Line($"  {selector,-18}{(rect is null ? "no box" : $"{rect.Value.X,6:F0},{rect.Value.Y,6:F0}  {rect.Value.Width,5:F0}x{rect.Value.Height:F0}")}");
            }
        }

        // The same shape the portal is built out of, in three lines of css: two columns that are meant
        // to sit side by side inside a container that is meant to be as tall as the taller of them.
        using var probe = new HutuViewInternal(new FakeTextMeasurer());
        probe.SetViewportSize(1200, 600);

        await probe.SetHtml(
            "<html><head><style>body{margin:0}.area{width:1200px}" +
            ".lm{float:left;width:860px;height:400px;background:#eee}" +
            ".r{float:right;width:300px;height:500px;background:#ddd}</style></head>" +
            "<body><div class='area'><div class='lm'>left</div><div class='r'>right</div></div>" +
            "<p>after</p></body></html>");

        var left = Box(probe, ".lm");
        var right = Box(probe, ".r");
        var area = Box(probe, ".area");
        var after = Box(probe, "p");

        // The three numbers a browser is checked against here, stated as rules rather than as figures
        // copied from one run: the columns share a row at the two edges, the container is as tall as
        // the taller of them, and the paragraph after it begins below the container's own bottom edge
        // plus whatever margin the reader's sheet gives a <p>.
        Line($"  float demo   lm {left.X:F0},{left.Y:F0} {left.Width:F0}x{left.Height:F0} | " +
             $"r {right.X:F0},{right.Y:F0} {right.Width:F0}x{right.Height:F0} | " +
             $"area h {area.Height:F0} (want 500) | p y {after.Y:F0} (want 500 + its own top margin)");
    }

    private static Rect Box(HutuViewInternal view, String selector)
    {
        var element = view.Document!.QuerySelector(selector);

        Assert.That(element, Is.Not.Null, $"the demo document holds no {selector}");

        var box = view.Layout!.FindBox(element!);

        Assert.That(box, Is.Not.Null, $"{selector} generated no box to measure");
        return box!.BorderBox;
    }

    /// <summary>The page's own HTML, fetched with a reader's user agent so the site answers normally.</summary>
    private async Task<String> FetchHtmlAsync()
    {
        using var http = new HttpClient();
        http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0 Safari/537.36");

        var bytes = await http.GetByteArrayAsync(Url);
        var head = Encoding.UTF8.GetString(bytes, 0, Math.Min(bytes.Length, 4096));
        var charset = System.Text.RegularExpressions.Regex.Match(head, "charset=[\"']?([a-zA-Z0-9_-]+)");

        Line($"  declared charset: {(charset.Success ? charset.Groups[1].Value : "none, utf-8 assumed")}");

        return Encoding.UTF8.GetString(bytes);
    }

    private static IBrowsingContext NewContext(Boolean? resourceLoading)
    {
        var device = new HutuRenderDevice(Width, Height);

        var configuration = Configuration.Default
            .With(new UserAgentStyleSheetProvider())
            .With((Object)new WideKeywordDeclarationFactory())
            .WithCss()
            .WithRenderDevice(device);

        if (resourceLoading is not null)
        {
            configuration = configuration.WithDefaultLoader(
                new LoaderOptions { IsResourceLoadingEnabled = resourceLoading.Value });
        }

        return BrowsingContext.New(configuration);
    }

    private async Task<IDocument> OpenInAsync(String name, IBrowsingContext context, String html)
    {
        var stopwatch = Stopwatch.StartNew();

        var document = await context.OpenAsync(response => response
            .Address(Url)
            .Status(HttpStatusCode.OK)
            .Header("Content-Type", "text/html; charset=utf-8")
            .Content(html));

        stopwatch.Stop();
        Line($"{name}      {stopwatch.Elapsed.TotalMilliseconds,9:F0}ms");
        return document;
    }

    private static List<ICssStyleSheet> SheetsOf(IDocument document)
    {
        var sheets = new List<ICssStyleSheet>();

        foreach (var provider in document.Context.GetServices<ICssDefaultStyleSheetProvider>())
        {
            sheets.Add(provider.Default);
        }

        sheets.AddRange(document.GetStyleSheets().OfType<ICssStyleSheet>());
        return sheets;
    }

    private static Int32 NumberOf(IHtmlCollection<IElement> elements) => elements.Length;

    private static Int32 NumberOf(INodeList elements) => elements.Length;

    private static String DescribeSheets(IDocument document)
    {
        var sheets = document.StyleSheets.OfType<ICssStyleSheet>().ToList();

        return $"{sheets.Count} loaded, {sheets.Sum(s => s.Rules.Length)} rules, " +
               $"{document.GetElementsByTagName("link").Count(l => l.GetAttribute("rel") == "stylesheet")} requested";
    }

    /// <summary>Runs a stage once and reports its wall clock. One run: a live page is not a fixture.</summary>
    private T Timed<T>(String name, Func<T> run)
    {
        var before = GC.GetTotalAllocatedBytes(true);
        var stopwatch = Stopwatch.StartNew();
        var result = run();
        stopwatch.Stop();

        var megabytes = (GC.GetTotalAllocatedBytes(true) - before) / 1024.0 / 1024.0;
        Line($"  {name,-18}{stopwatch.Elapsed.TotalMilliseconds,9:F1}ms {megabytes,8:F1}MB");

        return result;
    }

    private static List<LayoutBox> Walk(LayoutBox box)
    {
        var into = new List<LayoutBox> { box };

        foreach (var child in box.Children)
        {
            into.AddRange(Walk(child));
        }

        return into;
    }

    /// <summary>Fetches the first handful of the page's images in parallel, the way the loader does.</summary>
    private async Task SampleFetchAsync(IReadOnlyList<String> urls)
    {
        var requester = new HookHttpRequest(Array.Empty<IRequester>());
        var sample = urls.Take(ImageSample).ToList();
        var times = new Double[sample.Count];
        var lengths = new Int64[sample.Count];
        var failures = 0;
        var stopwatch = Stopwatch.StartNew();

        Line($"  fetching {sample.Count} of them in parallel:");

        var loads = sample.Select((url, index) => Task.Run(async () =>
        {
            var one = Stopwatch.StartNew();

            try
            {
                using var response = await requester.RequestAsync(
                    new Request
                    {
                        Address = new Url(url),
                        Method = AngleSharp.Io.HttpMethod.Get,
                        Content = System.IO.Stream.Null,
                    },
                    CancellationToken.None);

                if (response is not null)
                {
                    using var buffer = new System.IO.MemoryStream();
                    await response.Content.CopyToAsync(buffer);
                    lengths[index] = buffer.Length;
                }
                else
                {
                    Interlocked.Increment(ref failures);
                }
            }
            catch (Exception exception)
            {
                Interlocked.Increment(ref failures);
                Line($"    {Truncate(url)}  failed: {exception.GetType().Name} {exception.Message}");
            }

            one.Stop();
            times[index] = one.Elapsed.TotalMilliseconds;
        }));

        await Task.WhenAll(loads);
        stopwatch.Stop();

        var bytes = lengths.Sum();
        Line($"  {sample.Count} images, {bytes / 1024}KB in all, {failures} failures, " +
             $"{stopwatch.Elapsed.TotalMilliseconds:F0}ms wall - mean per image " +
             $"{times.Where(t => t > 0).Average():F0}ms, slowest {times.Max():F0}ms");

        for (var index = 0; index < sample.Count; index++)
        {
            Line($"    {times[index],7:F0}ms  {lengths[index] / 1024,6}KB  {Truncate(sample[index])}");
        }
    }

    private static String Truncate(String url) =>
        url.Length <= 96 ? url : url[..93] + "...";

    /// <summary>The load in the two steps a drawing host now takes: the document, then its pictures.</summary>
    private async Task EndToEndAsync()
    {
        using var measurer = new SkiaTextMeasurer();
        using var images = new SkiaImageLoader();
        using var view = new HutuViewInternal(measurer, images);
        view.SetViewportSize(Width, Height);

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(6));
        var stopwatch = Stopwatch.StartNew();

        try
        {
            await view.LoadDocumentAsync(Url, cts.Token);
            stopwatch.Stop();
            Line($"  LoadDocumentAsync   {stopwatch.Elapsed.TotalSeconds,6:F1}s   " +
                 $"{view.Layout?.Height:F0}px tall - everything a reader could already be reading");

            stopwatch.Restart();
            await view.PrefetchImagesAsync(cts.Token);
            stopwatch.Stop();
            Line($"  PrefetchImagesAsync {stopwatch.Elapsed.TotalSeconds,6:F1}s   " +
                 $"{view.Layout?.Height:F0}px tall after the pictures");
        }
        catch (Exception exception)
        {
            stopwatch.Stop();
            Line($"  load took {stopwatch.Elapsed.TotalSeconds:F1}s then {exception.GetType().Name}: {exception.Message}");
        }
    }

    /// <summary>
    /// How long a request to a host that never answers takes to give up. A picture on such a host is
    /// waited out on every load of the page that refers to it, which is why the engine's requester
    /// caps the wait rather than leaving it to AngleSharp's own forty-five seconds.
    /// </summary>
    private async Task StalledHostAsync()
    {
        var requester = new HookHttpRequest(Array.Empty<IRequester>());
        var stopwatch = Stopwatch.StartNew();

        var task = requester.RequestAsync(
            new Request
            {
                Address = new Url("http://10.255.255.1/blackhole.png"),
                Method = AngleSharp.Io.HttpMethod.Get,
                Content = System.IO.Stream.Null,
            },
            CancellationToken.None);

        var finished = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(30)));
        stopwatch.Stop();

        if (finished != task)
        {
            Line("  a host that never answers: still waiting after 30s - nothing capped it");
            return;
        }

        if (task.IsCompletedSuccessfully)
        {
            task.Result?.Dispose();
            Line($"  a host that never answers: answered in {stopwatch.Elapsed.TotalSeconds:F1}s");
            return;
        }

        Line($"  a host that never answers: given up in {stopwatch.Elapsed.TotalSeconds:F1}s " +
             $"({task.Exception?.InnerException?.GetType().Name ?? "a fault"})");
    }
}

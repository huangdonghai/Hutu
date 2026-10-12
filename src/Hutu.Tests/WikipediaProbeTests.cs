using AngleSharp.Css.Dom;
using AngleSharp.Dom;
using AngleSharp.Io;
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
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Hutu.Tests;

/// <summary>
/// What the Wikipedia main page costs and where it lands, against the numbers a browser was measured
/// at on the same day: <c>1348x3854</c>, <c>#mp-upper</c> a flex row at 1040, its two columns at
/// 55% and 45%, <c>#mp-tfp</c> 1022 wide, the footer at y 3629.
/// </summary>
/// <remarks>
/// Explicit and network-dependent, like the portal probe. This page is the interesting counterexample
/// to that one: a float is rare here (24 of 2424 elements) and a flex or a grid is everywhere (478
/// containers), the authoring is built on custom properties (1341 uses of <c>var()</c> in the rules),
/// and the interface declares 180 rules with a hover, active or focus selector - which is what the
/// rebuild figure at the bottom is about.
/// <c>dotnet test src/Hutu.Tests/Hutu.Tests.csproj --filter "FullyQualifiedName~WikipediaProbeTests"</c>
/// </remarks>
[TestFixture]
[Explicit]
public sealed class WikipediaProbeTests
{
    private const String Url = "https://en.wikipedia.org/wiki/Main_Page";

    private const Int32 Width = 1348;

    private const Int32 Height = 1237;

    /// <summary>The containers whose browser geometry is quoted in the remarks, in document order.</summary>
    private static readonly String[] Selectors =
    {
        "main#content", "#bodyContent", "#mp-upper", "#mp-left", "#mp-tfa", "#mp-dyk", "#mp-right", "#mp-tfp", "footer",
    };

    /// <summary>
    /// Which page the site answers each candidate agent with. MediaWiki names the skin it chose in the
    /// body's own classes, so the answer is read from there rather than guessed from the sheet list.
    /// </summary>
    [Test]
    [NonParallelizable]
    [CancelAfter(120_000)]
    public async Task UserAgent()
    {
        var agents = new[]
        {
            null,
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) Hutu",
            "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.4 Safari/605.1.15",
        };

        foreach (var agent in agents)
        {
            var requester = new HookHttpRequest(Array.Empty<IRequester>());

            if (agent is not null)
            {
                requester.UserAgent = agent;
            }

            using var response = await requester.RequestAsync(
                new AngleSharp.Io.Request
                {
                    Address = new AngleSharp.Dom.Url(Url),
                    Method = AngleSharp.Io.HttpMethod.Get,
                    Content = System.IO.Stream.Null,
                },
                CancellationToken.None);

            using var buffer = new System.IO.MemoryStream();

            if (response is not null)
            {
                await response.Content.CopyToAsync(buffer);
            }

            var html = System.Text.Encoding.UTF8.GetString(buffer.ToArray());
            var skin = System.Text.RegularExpressions.Regex.Match(html, "skin-[a-z-]+").Value;
            var vectors = System.Text.RegularExpressions.Regex.Matches(html, "skin-vector").Count;
            var shown = agent ?? "AngleSharp's own agent";
            var status = response is null ? "no answer" : ((Int32)response.StatusCode).ToString();

            Line($"  {Truncate(shown),-50} {status,-10} {buffer.Length / 1024,5}KB  " +
                 $"first skin class: {(skin.Length == 0 ? "none" : skin)}, skin-vector mentions: {vectors}");
        }
    }

    private static String Truncate(String text) =>
        text.Length <= 50 ? text : text[..47] + "...";

    [Test]
    [NonParallelizable]
    [CancelAfter(300_000)]
    public async Task Profile()
    {
        using var measurer = new SkiaTextMeasurer();
        using var images = new SkiaImageLoader();
        using var view = new HutuViewInternal(measurer, images);
        view.SetViewportSize(Width, Height);

        var load = Stopwatch.StartNew();
        var document = await view.LoadDocumentAsync(Url, CancellationToken.None);
        load.Stop();

        Line($"LoadDocumentAsync {load.Elapsed.TotalMilliseconds,8:F0}ms   " +
             $"{document.QuerySelectorAll("*").Length} elements (a browser's live DOM: 2424)");

        // Which page the site answered with. MediaWiki picks the skin from the user agent, and the
        // engine asks as AngleSharp's own - so the markup and the sheets can be a different skin's
        // than the one a reader compared the rendering against.
        Line($"  body            {document.Body?.GetAttribute("class") ?? "no class"}");
        Line($"  links           {String.Join(" | ", document.GetElementsByTagName("link")
            .Select(l => l.GetAttribute("href"))
            .Where(h => h is { Length: > 0 } && h.Contains("styles", StringComparison.Ordinal))
            .Take(4)
            .Select(h => h!.Length > 60 ? h[..60] : h))}");

        var device = new HutuRenderDevice(Width, Height);
        var faces = FontFaceTable.From(document);

        Line("");
        Line("--- the sheets the document came with (a browser: 14 sheets, ~1761 rules, 333KB) ---");

        var sheets = document.StyleSheets.OfType<ICssStyleSheet>().ToList();

        Line($"  loaded          {sheets.Count} sheets, {sheets.Sum(s => s.Rules.Length)} rules");

        foreach (var sheet in sheets.OrderByDescending(s => s.Rules.Length).Take(6))
        {
            var href = sheet.OwnerNode?.GetAttribute("href") ?? sheet.Href ?? "inline";

            Line($"  {sheet.Rules.Length,5} rules  {href[..Math.Min(72, href.Length)]}");
        }

        // The fused path gives up on a document whose cascade carries a substitution it cannot
        // resolve, so what it gave up on is worth naming: the rules that use var(), and the
        // properties they use it on.
        var index = RuleBucketIndex.Build(sheets, device);
        var substituting = new Dictionary<String, Int32>();
        var substitutingRules = 0;

        foreach (var rule in index.Rules)
        {
            var inThisRule = false;

            foreach (var property in rule.Style)
            {
                if (property.Value?.Contains("var(", StringComparison.OrdinalIgnoreCase) == true)
                {
                    inThisRule = true;
                    substituting[property.Name] = substituting.TryGetValue(property.Name, out var held) ? held + 1 : 1;
                }
            }

            if (inThisRule)
            {
                substitutingRules++;
            }
        }

        Line($"  in the index    {index.Rules.Count} rules, {substitutingRules} of them with a var() value");
        Line("  properties      " + String.Join(", ", substituting.OrderByDescending(p => p.Value).Take(8).Select(p => $"{p.Key} {p.Value}")));

        var fused = Stopwatch.StartNew();
        var boxes = BoxBuilder.TryBuildFused(document, device, (IImageProvider?)null, faces);
        fused.Stop();
        Line($"fused build       {fused.Elapsed.TotalMilliseconds,8:F0}ms   " +
             (boxes is null
                ? "NULL - the page falls back to the render tree, so this page runs on the deprecated path"
                : "a box tree"));

        var built = boxes ?? BoxBuilder.Build(view.RenderTree, device, (IImageProvider?)null, faces);

        if (boxes is null)
        {
            // The fallback's own two stages, which the pipeline never times apart: building the
            // styled render tree, and turning it into boxes. This is the deprecated path, and the
            // page is on it because of the substitution above.
            var window = document.DefaultView!;
            var tree = Stopwatch.StartNew();
            var renderTree = window.Render(device);
            tree.Stop();

            var build = Stopwatch.StartNew();
            var legacy = BoxBuilder.Build(renderTree, device, (IImageProvider?)null, faces);
            build.Stop();

            GC.KeepAlive(legacy);
            Line($"render tree     {tree.Elapsed.TotalMilliseconds,8:F0}ms   then the box builder {build.Elapsed.TotalMilliseconds:F0}ms - the deprecated path");
        }

        var engine = new LayoutEngine(measurer);
        var profile = engine.Profile(built, Width, Height);

        Line($"layout            {profile.TotalTime,8:F1}ms   " +
             $"measure {profile.MeasureTime:F1} / arrange {profile.ArrangeTime:F1} / " +
             $"decoration {profile.DecorationTime:F1} / overflow {profile.OverflowTime:F1}");
        Line($"                  {profile.MeasureAllocMB + profile.ArrangeAllocMB,8:F1}MB   " +
             $"measure {profile.MeasureAllocMB:F1} / arrange {profile.ArrangeAllocMB:F1}");

        var laid = engine.Layout(built, Width, Height)!;
        Line($"document          {laid.Width:F0}x{laid.Height:F0}   (a browser: {Width}x3854)");
        Line($"  the pipeline's own answer: {view.Layout!.Width:F0}x{view.Layout.Height:F0}");

        // The clipping boxes that swallow the most: a box that clips stops the document's reach, so
        // one holding content well past its own bottom edge is content a scroller can never get to.
        var swallowers = Walk(laid.Root)
            .Where(node => node.ClipsOverflow)
            .Select(node => (Name: node.Source.Element?.LocalName ?? node.Kind.ToString(),
                              Class: node.Source.Element?.GetAttribute("class"),
                              Box: node.BorderBox,
                              Held: Deepest(node)))
            .Where(entry => entry.Held - entry.Box.Bottom > 1)
            .OrderByDescending(entry => entry.Held - entry.Box.Bottom)
            .Take(5);

        foreach (var entry in swallowers)
        {
            var cls = entry.Class is { Length: > 0 } names ? "." + names.Split(' ')[0] : String.Empty;

            Line($"    swallows {entry.Held - entry.Box.Bottom,7:F0}px  {entry.Name}{cls} " +
                 $"{entry.Box.X:F0},{entry.Box.Y:F0} {entry.Box.Width:F0}x{entry.Box.Height:F0}, holds to {entry.Held:F0}");
        }

        var deep = Walk(laid.Root).Select(node => node.BorderBox.Bottom).Max();
        Line($"  deepest box      {deep:F0}  (the document reports {laid.Height:F0})");

        var painted = Stopwatch.StartNew();
        var list = new Painter().Paint(laid);
        painted.Stop();
        Line($"paint list        {painted.Elapsed.TotalMilliseconds,8:F1}ms   {list.Commands.Count()} commands");

        using var surface = SKSurface.Create(new SKImageInfo(Width, Height));
        var executor = new SkiaPaintExecutor(measurer, images);
        var drew = Stopwatch.StartNew();
        executor.Execute(list, surface.Canvas);
        drew.Stop();
        Line($"draw one frame    {drew.Elapsed.TotalMilliseconds,8:F1}ms   the whole viewport");

        // What a state change costs, because a hover over anything the interface styles is a state
        // change: the whole document is built, laid out and painted again. On this page that is the
        // deprecated path every time, because of the substitution the fused resolvers cannot read -
        // so the render tree is rebuilt from scratch as well.
        var rebuild = Stopwatch.StartNew();
        var again = BoxBuilder.TryBuildFused(document, device, (IImageProvider?)null, faces) ??
            BoxBuilder.Build(document.DefaultView!.Render(device), device, (IImageProvider?)null, faces);
        var relaid = engine.Layout(again, Width, Height);
        var repainted = relaid is null ? null : new Painter().Paint(relaid);
        rebuild.Stop();
        GC.KeepAlive(repainted);
        Line($"one rebuild       {rebuild.Elapsed.TotalMilliseconds,8:F0}ms   " +
             $"180 rules of this page carry :hover, :focus or :active, and a change of state is a " +
             "full build of the document");

        Line("");
        Line("--- where the containers land ---");

        foreach (var selector in Selectors)
        {
            var element = document.QuerySelector(selector);

            if (element is null)
            {
                Line($"  {selector,-16}absent from this document");
                continue;
            }

            var rect = laid.FindBox(element)?.BorderBox;

            Line($"  {selector,-16}{(rect is null ? "no box" : $"{rect.Value.X,6:F0},{rect.Value.Y,6:F0}  {rect.Value.Width,5:F0}x{rect.Value.Height:F0}")}");
        }

        Line("");
        Line("--- what the engine thinks the page is made of ---");

        var kinds = new Dictionary<LayoutKind, Int32>();
        var flex = 0;
        var grid = 0;
        var floats = 0;
        var nowrap = 0;
        var flowRoot = 0;
        var lists = 0;
        var markers = 0;

        foreach (var node in Walk(laid.Root))
        {
            kinds[node.Kind] = kinds.TryGetValue(node.Kind, out var held) ? held + 1 : 1;

            if (node.MarkerBounds is not null)
            {
                markers++;
            }
        }

        // Counted over the elements rather than the boxes, because every box an element generates
        // carries that element's style: a flex container's text and line boxes would otherwise be
        // counted as flex containers too. This is the cascade the boxes were actually built from - the
        // page is on the render-tree path, whose styles come from AngleSharp.
        var styles = document.DefaultView!;

        foreach (var element in document.QuerySelectorAll("*"))
        {
            var computed = styles.GetComputedStyle(element);
            var display = computed.GetPropertyValue("display");

            if (display.StartsWith("flex", StringComparison.OrdinalIgnoreCase))
            {
                flex++;
            }
            else if (display.StartsWith("grid", StringComparison.OrdinalIgnoreCase))
            {
                grid++;
            }
            else if (display.Equals("flow-root", StringComparison.OrdinalIgnoreCase))
            {
                flowRoot++;
            }
            else if (display.Equals("list-item", StringComparison.OrdinalIgnoreCase))
            {
                lists++;
            }

            // An answer that is not `none` is a float only when there is an answer: a property the
            // cascade does not carry at all comes back empty, and counting that as a floated element
            // would report every element on the page as one.
            var floated = computed.GetPropertyValue("float");

            if (!String.IsNullOrWhiteSpace(floated) &&
                !floated.Equals("none", StringComparison.OrdinalIgnoreCase))
            {
                floats++;
            }

            if (computed.GetPropertyValue("white-space").Equals("nowrap", StringComparison.OrdinalIgnoreCase))
            {
                nowrap++;
            }
        }

        Line($"  boxes laid out  {Walk(laid.Root).Count} over {document.QuerySelectorAll("*").Length} elements, " +
             $"{markers} markers drawn");
        Line($"  flex / grid / flow-root / list-item: {flex} / {grid} / {flowRoot} / {lists} (a browser: 478 / 2 / 9 / 440)");
        Line($"  floats / nowrap: {floats} / {nowrap} (a browser: 24 / 326)");
        Line("  " + String.Join(" ", kinds.OrderBy(pair => pair.Key).Select(pair => $"{pair.Key} {pair.Value}")));
    }

    private static void Line(String text) => TestContext.Progress.WriteLine(text);

    /// <summary>Gets how far down the boxes a box holds reach, whatever they clip past.</summary>
    private static Double Deepest(LayoutBox box)
    {
        var deepest = box.BorderBox.Bottom;

        foreach (var child in box.Children)
        {
            deepest = Math.Max(deepest, Deepest(child));
        }

        return deepest;
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
}

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
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
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

    /// <summary>
    /// Gets the viewport the page is laid out at. A browser's window is nothing this fixture can set, so
    /// the width it was read at is handed in rather than assumed: the two documents only mean the same
    /// page when they were asked at the same width, and this one answers a narrow window with a taller
    /// document - 1222 wide gives 4384 of height where 1348 gives 3854.
    /// </summary>
    private static readonly Int32 Width = ReadViewport("HUTU_VIEWPORT_WIDTH", 1348);

    private static readonly Int32 Height = ReadViewport("HUTU_VIEWPORT_HEIGHT", 1237);

    private static Int32 ReadViewport(String name, Int32 otherwise) =>
        Int32.TryParse(Environment.GetEnvironmentVariable(name), out var pixels) && pixels > 0 ? pixels : otherwise;

    /// <summary>The containers whose browser geometry is quoted in the remarks, in document order.</summary>
    private static readonly String[] Selectors =
    {
        "main#content", "#bodyContent", "#mp-upper", "#mp-left", "#mp-tfa", "#mp-dyk", "#mp-right", "#mp-tfp", "footer",
    };

    /// <summary>
    /// The containers the float diagnostic asks about: the same ones, plus the article body they all
    /// hang inside, since that is the block whose content box a float is inset against.
    /// </summary>
    private static readonly String[] DiagnosticSelectors =
    {
        "#mw-content-text", "#bodyContent", "main#content", "#mp-upper", "#mp-left", "#mp-right",
        "#mp-tfa", "#mp-dyk", "#mp-tfp", "footer",
    };

    /// <summary>How tall the saved picture is allowed to be, in pixels: enough for the whole page.</summary>
    private const Int32 MaxShotHeight = 8000;

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

    /// <summary>The same, for a width the caller chooses: a file name needs less room than an agent.</summary>
    private static String Truncate(String text, Int32 room) =>
        text.Length <= room ? text : text[..(room - 3)] + "...";

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
             (boxes is null ? "NULL" : "a box tree"));

        if (boxes is null)
        {
            Line("the fused path abstains on this document");
            return;
        }

        var engine = new LayoutEngine(measurer);
        var profile = engine.Profile(boxes, Width, Height);

        Line($"layout            {profile.TotalTime,8:F1}ms   " +
             $"measure {profile.MeasureTime:F1} / arrange {profile.ArrangeTime:F1} / " +
             $"decoration {profile.DecorationTime:F1} / overflow {profile.OverflowTime:F1}");
        Line($"                  {profile.MeasureAllocMB + profile.ArrangeAllocMB,8:F1}MB   " +
             $"measure {profile.MeasureAllocMB:F1} / arrange {profile.ArrangeAllocMB:F1}");

        var laid = engine.Layout(boxes, Width, Height)!;
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
        // change: the whole document is built, laid out and painted again.
        var rebuild = Stopwatch.StartNew();
        var again = BoxBuilder.TryBuildFused(document, device, (IImageProvider?)null, faces);
        var relaid = engine.Layout(again, Width, Height);
        var repainted = relaid is null ? null : new Painter().Paint(relaid);
        rebuild.Stop();
        GC.KeepAlive(repainted);
        Line($"one rebuild       {rebuild.Elapsed.TotalMilliseconds,8:F0}ms   " +
             $"180 rules of this page carry :hover, :focus or :active, and a change of state is a " +
             "full build of the document");

        Line("");
        Line("--- the boxes that reach furthest right, and what makes them reach ---");

        foreach (var node in Walk(laid.Root)
            .OrderBy(box => box.BorderBox.Right)
            .Reverse()
            .Take(8))
        {
            var element = node.Source.Element;
            var classes = element?.GetAttribute("class");

            Line($"  reaches {node.BorderBox.Right,6:F0}  x {node.BorderBox.X,6:F0} w {node.BorderBox.Width,6:F0}  " +
                 $"{element?.LocalName ?? node.Kind.ToString()}"
                 + $"{(classes is { Length: > 0 } some ? "." + some.Split(' ')[0] : string.Empty)}  "
                 + $"pos:{node.Source.Style.Position.Position} float:{node.Source.Style.Float} hidden:{node.Source.Style.IsHidden}");
        }

        Line("");
        Line("--- the chain from the widest box up to the root ---");

        var parents = new Dictionary<LayoutBox, LayoutBox?>();

        void Map(LayoutBox node, LayoutBox? above)
        {
            parents[node] = above;

            foreach (var child in node.Children)
            {
                Map(child, node);
            }
        }

        Map(laid.Root, null);

        var widest = Walk(laid.Root)
            .OrderBy(box => box.BorderBox.Right)
            .Last();

        for (var at = widest; at is not null; at = parents[at])
        {
            var element = at.Source.Element;
            var classes = element?.GetAttribute("class");

            Line($"  x {at.BorderBox.X,7:F0} w {at.BorderBox.Width,6:F0}  {element?.LocalName ?? at.Kind.ToString()}"
                 + $"{(classes is { Length: > 0 } some ? "." + some.Split(' ')[0] : string.Empty)}  "
                 + $"display:{at.Source.Style.Display} pos:{at.Source.Style.Position.Position} "
                 + $"width:{(at.Source.Style.Width.HasValue ? at.Source.Style.Width.Resolve(1348).ToString("F0") : "auto")}");
        }

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
        // counted as flex containers too.
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

    /// <summary>
    /// The geometry a browser's is compared against, box by box: which document the site answered
    /// with, where every floated box landed and what kind of container holds it, where the
    /// containers the page hangs on landed, what each of them is displayed as all the way up to the
    /// root, how many lines a float actually pushed in, and the whole of it drawn to a picture.
    /// </summary>
    /// <remarks>
    /// A measurement and not a check, so nothing here fails on a number: what it prints is compared
    /// by hand against the same figures taken from a browser at the same viewport. It loads the page
    /// <em>with</em> its pictures, unlike <see cref="Profile"/>, because a floated image that never
    /// arrived is laid out at its attributes' size and would report a rectangle no reader sees.
    /// <c>dotnet test src/Hutu.Tests/Hutu.Tests.csproj --filter "FullyQualifiedName~WikipediaProbeTests.FloatAndWrapDiagnostic"</c>
    /// </remarks>
    [Test]
    [NonParallelizable]
    [CancelAfter(480_000)]
    public async Task FloatAndWrapDiagnostic()
    {
        using var measurer = new SkiaTextMeasurer();
        using var images = new SkiaImageLoader();
        using var view = new HutuViewInternal(measurer, images);
        view.SetViewportSize(Width, Height);

        var load = Stopwatch.StartNew();
        var document = await view.LoadUrlAsync(Url, CancellationToken.None);
        load.Stop();

        var laid = view.Layout;

        if (laid is null)
        {
            Line("the engine laid nothing out, so there is no geometry to compare");
            return;
        }

        // Everything below walks up and down the laid-out tree, so the parents are mapped once and
        // the questions about a box - is it drawn, what is it called - are answered from that map.
        var parents = new Dictionary<LayoutBox, LayoutBox?>();

        void Map(LayoutBox node, LayoutBox? above)
        {
            parents[node] = above;

            foreach (var child in node.Children)
            {
                Map(child, node);
            }
        }

        Map(laid.Root, null);

        LayoutBox? Above(LayoutBox node) => parents.TryGetValue(node, out var held) ? held : null;

        // Whether a box is drawn at all, which is what makes a rectangle something a reader sees:
        // this page hides a great many boxes behind its menus, and a hidden one holds its geometry
        // just as a drawn one does.
        Boolean Seen(LayoutBox node)
        {
            for (var at = node; at is not null; at = Above(at))
            {
                if (at.Source.Style.IsHidden)
                {
                    return false;
                }
            }

            return true;
        }

        String Tag(LayoutBox node)
        {
            var element = node.Source.Element;

            return element is null ? node.Kind.ToString() : Name(element);
        }

        var visible = Walk(laid.Root).Where(Seen).ToList();

        Line($"load, with images  {load.Elapsed.TotalMilliseconds,8:F0}ms   " +
             $"{document.QuerySelectorAll("*").Length} elements, viewport {Width}x{Height}");
        Line($"document           {laid.Width,8:F0}x{laid.Height:F0}   (a browser: {Width}x3854)");
        Line($"boxes              {Walk(laid.Root).Count,8} laid out, {visible.Count} of them drawn");
        Line($"deepest drawn box  {visible.Max(node => node.BorderBox.Bottom),8:F0}");
        Line($"widest drawn box   {visible.Max(node => node.BorderBox.Right),8:F0}");

        // The skin decides everything printed below it: MediaWiki answers an agent it does not
        // recognise with its mobile skin, which is another document - another markup, another set of
        // sheets - so a mismatch here means the two sets of numbers being compared describe
        // different pages, and no amount of rectangle matching would mean anything. Read as a whole
        // class rather than as the first one matching a prefix, because the body carries
        // `skin--responsive` and `skin-vector-search-vue` beside the one that names the skin.
        var body = document.Body?.GetAttribute("class") ?? "no class";
        var skins = body.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(token => token.StartsWith("skin-", StringComparison.Ordinal))
            .ToList();
        var skin = skins
                .Where(token => Regex.IsMatch(
                    token,
                    @"\Askin-(vector-2022|vector|monobook|timeless|minerva|cologneblue|modern|nostalgia)\z"))
                // The longest, because the body carries `skin-vector` beside `skin-vector-2022` and
                // it is the longer one that names the skin the sheets were served for.
                .OrderByDescending(token => token.Length)
                .FirstOrDefault()
            ?? skins.LastOrDefault()
            ?? "none";

        Line("");
        Line("--- the page the site answered with ---");
        Line($"  body class      {body}");
        Line($"  skin classes    {String.Join(" ", skins)}");
        Line($"  skin in force   {skin}");

        if (skin.StartsWith("skin-vector", StringComparison.Ordinal))
        {
            Line($"  {skin}: the desktop document a browser was measured on, so the two sets of");
            Line("  rectangles below are about one page");
        }
        else if (skin.StartsWith("skin-minerva", StringComparison.Ordinal))
        {
            Line("  *** WARNING *** the mobile skin (skin-minerva) came back. A browser was measured on");
            Line("  *** WARNING *** skin-vector-2022, so every rectangle below is about a different");
            Line("  *** WARNING *** document and the comparison against it is void.");
        }
        else
        {
            Line($"  *** WARNING *** '{skin}' names no skin this probe recognises, so the comparison");
            Line("  *** WARNING *** against a browser's numbers is doubtful.");
        }

        Line("");
        Line("--- every floated box the engine laid out, and the container that holds it ---");
        Line("    (the parent's kind is the point: a float inside a flex, a grid or a table is one the");
        Line("     block-flow band it is inset against was never handed down to. `outer` is the margin");
        Line("     box's inline span, which is the edge a line is inset to rather than the border's.)");

        // A list item is boxed twice over - the item, and the block that holds its content - and both
        // boxes carry the element's own style, so both declare the float. The outer one is the float;
        // counting the inner as well would ask the wrap about a box that stands inside the float
        // rather than beside it, and would double every figure below.
        Boolean FloatedAbove(LayoutBox node)
        {
            var element = node.Source.Element;

            if (element is null)
            {
                return false;
            }

            for (var at = Above(node); at is not null; at = Above(at))
            {
                if (ReferenceEquals(at.Source.Element, element) &&
                    at.Source.Style.Float != FloatSide.None)
                {
                    return true;
                }
            }

            return false;
        }

        var floats = new List<LayoutBox>();
        var nested = 0;
        var outOfFlow = 0;
        var hidden = 0;

        foreach (var node in Walk(laid.Root))
        {
            if (node.Source.Style.Float == FloatSide.None)
            {
                continue;
            }

            if (!Seen(node))
            {
                hidden++;
                continue;
            }

            // A float is ignored on a box taken out of flow, so one there is a declaration rather
            // than a float, and counting it as one would ask the wrap about a box no line avoids.
            if (node.Source.Style.Position.Position is PositionMode.Absolute or PositionMode.Fixed)
            {
                outOfFlow++;
                continue;
            }

            if (FloatedAbove(node))
            {
                nested++;
                continue;
            }

            floats.Add(node);
        }

        Line($"  {floats.Count} floats drawn, {nested} inner boxes of them, {hidden} hidden, " +
             $"{outOfFlow} declared but out of flow (a browser: 24 elements declaring one)");

        foreach (var node in floats)
        {
            var rect = node.BorderBox;
            var above = Above(node);

            Line($"  {node.Source.Style.Float,-6}{rect.X,7:F0},{rect.Y,7:F0} {rect.Width,6:F0}x{rect.Height,6:F0}" +
                 $"  outer {rect.X - node.Margin.Left,7:F0}..{rect.Right + node.Margin.Right,7:F0}" +
                 $" over {rect.Y - node.Margin.Top,7:F0}..{rect.Bottom + node.Margin.Bottom,7:F0}   " +
                 $"{Tag(node)} [kind={node.Source.Kind} display={node.Source.Style.Display}]");
            Line($"  {"",-58}parent: " +
                 (above is null
                     ? "none"
                     : $"{Tag(above)} [kind={above.Source.Kind} display={above.Source.Style.Display}]"));
        }

        Line("");
        Line("--- where the containers land ---");

        foreach (var selector in DiagnosticSelectors)
        {
            var element = document.QuerySelector(selector);

            if (element is null)
            {
                Line($"  {selector,-18}absent from this document");
                continue;
            }

            var box = laid.FindBox(element);
            var rect = box?.BorderBox;

            Line($"  {selector,-18}{(rect is null ? "no box" : $"{rect.Value.X,6:F0},{rect.Value.Y,6:F0}  {rect.Value.Width,5:F0}x{rect.Value.Height,5:F0}")}");
        }

        Line($"  {"document height",-18}{laid.Height,6:F0}");

        Line("");
        Line("--- what each container is displayed as, on the way up to the root ---");

        foreach (var selector in DiagnosticSelectors)
        {
            var element = document.QuerySelector(selector);
            var box = element is null ? null : laid.FindBox(element);

            if (box is null)
            {
                Line($"  {selector}: no box");
                continue;
            }

            Line($"  {selector}:");

            for (var at = box; at is not null; at = Above(at))
            {
                var rect = at.BorderBox;

                Line($"      display={at.Source.Style.Display,-11}kind={at.Source.Kind,-13}" +
                     $"{rect.X,6:F0},{rect.Y,6:F0} {rect.Width,5:F0}x{rect.Height,5:F0}   {Tag(at)}");
            }
        }

        Line("");
        Line("--- how much of the text a float actually pushed in ---");
        Line("    (both edges are asked about: a float on the left moves a line's start, a float on");
        Line("     the right moves its end, and this page's main floats are all on the right)");

        var pushedLeft = 0;
        var pulledRight = 0;
        var total = 0;
        var leftSamples = new List<String>();
        var rightSamples = new List<String>();

        void Sweep(LayoutBox node, LayoutBox? above)
        {
            if (node.Kind == LayoutKind.Line && above is { } block)
            {
                total++;

                // The line box spans the block's content box unless a float is in the way, so the
                // distance in from either edge is that float's edge - and the block's own insets are
                // what stands between its border box and its content.
                var box = node.BorderBox;
                var start = block.BorderBox.X + block.Border.Left + block.Padding.Left;
                var end = block.BorderBox.Right - block.Border.Right - block.Padding.Right;

                if (box.X - start > 0.5)
                {
                    pushedLeft++;

                    if (leftSamples.Count < 12)
                    {
                        leftSamples.Add($"    start pushed {box.X - start,5:F0} in, to {box.X,6:F0}, at y {box.Y,6:F0}, " +
                            $"inside {start,6:F0}..{end,6:F0}  of {Tag(block)}");
                    }
                }

                if (end - box.Right > 0.5)
                {
                    pulledRight++;

                    if (rightSamples.Count < 12)
                    {
                        rightSamples.Add($"    end pulled {end - box.Right,5:F0} in, to {box.Right,6:F0}, at y {box.Y,6:F0}, " +
                            $"inside {start,6:F0}..{end,6:F0}  of {Tag(block)}");
                    }
                }
            }

            foreach (var child in node.Children)
            {
                Sweep(child, node);
            }
        }

        Sweep(laid.Root, null);

        Line($"  of {total} lines: {pushedLeft} begin past a float's edge, {pulledRight} end short of one");

        foreach (var sample in leftSamples)
        {
            Line(sample);
        }

        foreach (var sample in rightSamples)
        {
            Line(sample);
        }

        // The self-check, which needs no browser: a line box that shares ground with a floated box
        // is text drawn under a picture, which is the one thing the wrap is supposed to prevent. A
        // float's own content lies inside its box by right, so a line whose block sits under the
        // float is not asked about at all.
        var overlapped = 0;
        var offenders = new List<String>();

        void Check(LayoutBox node, LayoutBox? block)
        {
            if (node.Kind == LayoutKind.Line && block is { } owner && Seen(node))
            {
                foreach (var area in floats)
                {
                    var box = node.BorderBox;
                    var edges = area.BorderBox;

                    if (box.X + 0.5 >= edges.Right || edges.X + 0.5 >= box.Right ||
                        box.Y + 0.5 >= edges.Bottom || edges.Y + 0.5 >= box.Bottom ||
                        Owns(area, owner))
                    {
                        continue;
                    }

                    overlapped++;

                    if (offenders.Count < 8)
                    {
                        offenders.Add($"    text under a picture: line " +
                            $"{box.X,6:F0}..{box.Right,6:F0} at {box.Y,5:F0} of {Tag(owner)} " +
                            $"crosses {Tag(area)} " +
                            $"at {edges.X,6:F0}..{edges.Right,6:F0} {edges.Y,5:F0}..{edges.Bottom,5:F0}");
                    }

                    break;
                }
            }

            foreach (var child in node.Children)
            {
                Check(child, node.Kind == LayoutKind.Line ? block : node);
            }
        }

        // Whether the floated element is the line's own block or stands above it in the tree: the
        // content of a float lies inside the float's box by right, and a text box borrows the style
        // of the element holding it - which is why the comparison is of elements rather than of
        // boxes, or every word inside a floated link reads as text drawn under a picture.
        Boolean Owns(LayoutBox area, LayoutBox owner)
        {
            var floated = Elem(area.Source);

            if (floated is null)
            {
                return false;
            }

            for (var at = owner; at is not null; at = Above(at))
            {
                if (ReferenceEquals(Elem(at.Source), floated))
                {
                    return true;
                }
            }

            return false;
        }

        AngleSharp.Dom.IElement? Elem(Hutu.Rendering.Boxes.Box box) => box.Element ?? box.Source?.ParentElement;

        Check(laid.Root, null);

        Line($"  lines overlapping a float they do not belong to: {overlapped} of {total}");

        foreach (var offender in offenders)
        {
            Line(offender);
        }

        // The other half of the same question: a line that was moved in has to be moved against
        // something. A shift with no float at the edge it moved to is a band read from the wrong
        // origin, which is what happens where a context hands its children the container's own corner
        // rather than rebasing it to each of them - or a band never released, which is what happens
        // where a float has ended above the line and the line still stops where it did.
        var unjustifiedStart = 0;
        var unjustifiedEnd = 0;
        var stray = new List<String>();

        // The float whose outer edge this edge of the line stands on, or null when no float accounts
        // for it. Both edges are the float's *margin* box rather than its border box: a float carries its
        // margin outside itself, and it is that box a line box is shortened around - so anything between
        // the two is a legal place to stop, and calling that unjustified would report the page's own
        // spacing as a bug. Down the page the same rule decides whether a float is in the band at all:
        // a line whose top lies between a float's border bottom and its margin bottom is still beside it.
        LayoutBox? Blame(Rect box, Boolean start)
        {
            foreach (var area in floats)
            {
                if (!Seen(area) ||
                    area.BorderBox.Bottom + area.Margin.Bottom <= box.Y ||
                    area.BorderBox.Y - area.Margin.Top >= box.Bottom)
                {
                    continue;
                }

                var there = start
                    ? box.X >= area.BorderBox.Right - 1.5 &&
                      box.X <= area.BorderBox.Right + area.Margin.Right + 1.5
                    : box.Right <= area.BorderBox.X + 1.5 &&
                      box.Right >= area.BorderBox.X - area.Margin.Left - 1.5;

                if (there)
                {
                    return area;
                }
            }

            return null;
        }

        // The float nearest the line's band, named so an unjustified shift can be checked by hand
        // against the picture rather than taken on trust. The margin box again, for the same reason.
        LayoutBox? Nearest(Rect box) => floats
            .Where(area => Seen(area) &&
                           area.BorderBox.Bottom + area.Margin.Bottom > box.Y &&
                           area.BorderBox.Y - area.Margin.Top < box.Bottom)
            .OrderBy(area => Math.Min(
                Math.Abs(box.X - area.BorderBox.Right),
                Math.Abs(box.Right - area.BorderBox.X)))
            .FirstOrDefault();

        void Stray(String edge, Double by, LayoutBox owner, Rect box, Double start, Double end)
        {
            if (stray.Count == 12)
            {
                return;
            }

            var nearest = Nearest(box);

            stray.Add($"    {edge} moved {by,5:F0} in with nothing at its edge: {Tag(owner)} " +
                $"line {box.X,6:F0}..{box.Right,6:F0}, y {box.Y,6:F0}..{box.Bottom,6:F0}, " +
                $"content {start,6:F0}..{end,6:F0}" +
                (nearest is null
                    ? ", no float overlaps this line at all"
                    : $", the nearest overlapping float is {Tag(nearest)} spanning " +
                      $"{nearest.BorderBox.X - nearest.Margin.Left,6:F0}..{nearest.BorderBox.Right + nearest.Margin.Right,6:F0} " +
                      $"over y {nearest.BorderBox.Y,6:F0}..{nearest.BorderBox.Bottom,6:F0}"));
        }

        void CheckShift(LayoutBox node, LayoutBox? block)
        {
            if (node.Kind == LayoutKind.Line && block is { } owner && Seen(node))
            {
                var box = node.BorderBox;
                var start = owner.BorderBox.X + owner.Border.Left + owner.Padding.Left;
                var end = owner.BorderBox.Right - owner.Border.Right - owner.Padding.Right;

                if (box.X - start > 0.5 && Blame(box, true) is null)
                {
                    unjustifiedStart++;
                    Stray("start", box.X - start, owner, box, start, end);
                }

                if (end - box.Right > 0.5 && Blame(box, false) is null)
                {
                    unjustifiedEnd++;
                    Stray("end", end - box.Right, owner, box, start, end);
                }
            }

            foreach (var child in node.Children)
            {
                CheckShift(child, node.Kind == LayoutKind.Line ? block : node);
            }
        }

        CheckShift(laid.Root, null);

        Line($"  of the {pushedLeft} starts and the {pulledRight} ends moved in, " +
             $"{unjustifiedStart} starts and {unjustifiedEnd} ends have no float at the edge they moved to");

        foreach (var line in stray)
        {
            Line(line);
        }

        Line("");
        Line($"--- the drawn boxes that reach furthest right, against a viewport {Width} wide ---");

        foreach (var node in visible.OrderByDescending(box => box.BorderBox.Right).Take(6))
        {
            Line($"  reaches {node.BorderBox.Right,7:F0}  x {node.BorderBox.X,7:F0} w {node.BorderBox.Width,6:F0}   " +
                 $"{Tag(node)} [kind={node.Source.Kind} display={node.Source.Style.Display} " +
                 $"float={node.Source.Style.Float} pos={node.Source.Style.Position.Position}]");
        }

        Line("");
        Line("--- the whole of it, drawn ---");

        // Drawn into a surface as tall as the document rather than as tall as the viewport, so the
        // floated main content is in the picture a browser's full-page shot is compared against.
        var tall = (Int32)Math.Min(Math.Ceiling(Math.Max(laid.Height, Height)), MaxShotHeight);

        using var surface = SKSurface.Create(new SKImageInfo(Width, tall));

        var painted = new Painter().Paint(laid);
        var drew = Stopwatch.StartNew();
        new SkiaPaintExecutor(measurer, images).Execute(painted, surface.Canvas);
        drew.Stop();

        var path = Path.Combine(RepositoryRoot(), "artifacts", "wikipedia-hutu.png");

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        using (var shot = surface.Snapshot())
        using (var png = shot.Encode(SKEncodedImageFormat.Png, 100))
        using (var into = File.Create(path))
        {
            png.SaveTo(into);
        }

        Line($"  {painted.Commands.Count()} commands drawn in {drew.Elapsed.TotalMilliseconds:F0}ms, " +
             $"{Width}x{tall} of a {laid.Height:F0} tall document");
        Line($"  {path}   {new FileInfo(path).Length / 1024}KB");
    }

    /// <summary>
    /// Every picture's geometry on one line: what the document declares about its size, what the engine
    /// sized its box from, and what the box came out as. A browser renders each of these images at its
    /// <c>width</c>/<c>height</c> attributes, which the server scales to the display size; the bitmap
    /// behind <c>src</c> is a thumbnail of a different width again, so the two columns of numbers say
    /// whether a box was built from the declared size or from the picture that arrived.
    /// <c>dotnet test src/Hutu.Tests/Hutu.Tests.csproj --filter "FullyQualifiedName~WikipediaProbeTests.ImageSizeDiagnostic"</c>
    /// </summary>
    [Test]
    [NonParallelizable]
    [CancelAfter(480_000)]
    public async Task ImageSizeDiagnostic()
    {
        using var measurer = new SkiaTextMeasurer();
        using var images = new SkiaImageLoader();
        using var view = new HutuViewInternal(measurer, images);
        view.SetViewportSize(Width, Height);

        var load = Stopwatch.StartNew();
        var document = await view.LoadUrlAsync(Url, CancellationToken.None);
        load.Stop();

        var laid = view.Layout;

        if (laid is null)
        {
            Line("the engine laid nothing out, so there is no geometry to compare");
            return;
        }

        var pictures = Walk(laid.Root)
            .Where(node => node.Source.IsReplaced)
            .ToList();

        Line($"load, with images  {load.Elapsed.TotalMilliseconds,8:F0}ms   {pictures.Count} image boxes");
        Line("");
        Line("--- each picture: the size the page declares, the size the box was built from, the size it got ---");
        Line("    file                                          attr      intrinsic      used  ratio  the constraints the style carries");

        var matches = 0;

        foreach (var node in pictures)
        {
            var box = node.Source;
            var element = box.Element;
            var rect = node.BorderBox;
            var attrWidth = element?.GetAttribute("width");
            var attrHeight = element?.GetAttribute("height");
            var declared = attrWidth is null || attrHeight is null ? "none" : attrWidth + "x" + attrHeight;
            var file = box.SourceUrl is { Length: > 0 } url ? Tail(url) : "(no url)";
            var intrinsic = box.IntrinsicSize;
            var ratio = Double.TryParse(attrWidth, out var wanted) && wanted > 0
                ? (rect.Width / wanted).ToString("F2")
                : "-";

            if (Double.TryParse(attrWidth, out var declaredWidth) && Math.Abs(rect.Width - declaredWidth) < 1.5)
            {
                matches++;
            }

            Line($"    {Truncate(file, 40),-40} {declared,9} " +
                 $"{intrinsic?.Width,5:F0}x{intrinsic?.Height,4:F0} " +
                 $"{rect.Width,5:F0}x{rect.Height,4:F0} {ratio,6}  " +
                 $"w={Describe(box.Style.Width)} h={Describe(box.Style.Height)} " +
                 $"maxW={Describe(box.Style.MaxWidth)} maxH={Describe(box.Style.MaxHeight)} " +
                 $"float={box.Style.Float} pos={box.Style.Position.Position}");
        }

        Line("");
        Line($"  {matches} of {pictures.Count} boxes are the width their element declares " +
             "(a browser lays all of them out at the declared size)");
    }

    /// <summary>
    /// Where the bullets of the "Did you know" list land, box by box, against the same list read out of
    /// a browser at its own viewport: the section at x 53 with nothing inset, its <c>ul</c> at x 79 -
    /// indented by a <c>margin-left</c> of 25.6px and with its <c>padding-left</c> reset to nothing - the
    /// items' text from x 79 as well, and the disc hanging to the left of that edge.
    /// <c>dotnet test src/Hutu.Tests/Hutu.Tests.csproj --filter "FullyQualifiedName~WikipediaProbeTests.DykIndentDiagnostic"</c>
    /// </summary>
    [Test]
    [NonParallelizable]
    [CancelAfter(300_000)]
    public async Task DykIndentDiagnostic()
    {
        using var measurer = new SkiaTextMeasurer();
        using var view = new HutuViewInternal(measurer, null);
        view.SetViewportSize(Width, Height);

        var document = await view.LoadDocumentAsync(Url, CancellationToken.None);
        var laid = view.Layout;

        if (laid is null)
        {
            Line("the engine laid nothing out, so there is no geometry to compare");
            return;
        }

        // The list and its neighbours, asked about in the same order a reader meets them.
        var asked = new[]
        {
            "#mp-dyk",
            "#mp-dyk > ul",
            "#mp-dyk > ul > li:nth-child(1)",
            "#mp-dyk > ul > li:nth-child(2)",
            "#mp-dyk > ul > li:nth-child(3)",
            "#mp-itn > ul",
            "#mp-itn > ul > li",
        };

        Line($"viewport {Width}x{Height}, no images fetched   (a browser was read at 1222 wide)");
        Line("");
        Line("    selector                              border box            content        margin-left   padding-left  list-style");

        foreach (var selector in asked)
        {
            var element = document.QuerySelector(selector);

            if (element is null)
            {
                Line($"    {Truncate(selector, 38),-38} absent from this document");
                continue;
            }

            var box = laid.FindBox(element);

            if (box is null)
            {
                Line($"    {Truncate(selector, 38),-38} matched no box");
                continue;
            }

            var style = box.Source.Style;
            var rect = box.BorderBox;
            var content = box.ContentBox;
            var marker = box.MarkerBounds;
            var line = Walk(box).FirstOrDefault(node => node.Kind == LayoutKind.Line);

            Line($"    {Truncate(selector, 38),-38} " +
                 $"{rect.X,6:F0},{rect.Y,6:F0} {rect.Width,5:F0}x{rect.Height,4:F0} " +
                 $"{content.X,6:F0}..{content.Right,5:F0} " +
                 $"{Describe(style.Margin.Left),8}({box.Margin.Left,5:F0}) " +
                 $"{Describe(style.Padding.Left),8}({box.Padding.Left,5:F0}) " +
                 $"{style.ListStylePosition,-8}" +
                 (marker is null ? String.Empty : $"  marker {marker.Value.X,5:F0}..{marker.Value.Right,5:F0}") +
                 (line is null ? String.Empty : $"  first line {line.BorderBox.X,5:F0}..{line.BorderBox.Right,5:F0}"));
        }

        Line("");
        Line("    the same boxes read out of the document's own CSSOM, which is where a value the engine");
        Line("    laid out with can be told apart from the one the cascade handed it:");

        var properties = new[]
        {
            "display", "font-size", "margin", "margin-left", "margin-inline-start",
            "padding", "padding-left", "padding-inline-start", "list-style-position", "list-style-type",
        };

        foreach (var selector in new[] { "#mp-dyk > ul", "#mp-dyk > ul > li:nth-child(1)", "#mp-itn > ul" })
        {
            var element = document.QuerySelector(selector);

            if (element is null)
            {
                continue;
            }

            var computed = view.GetComputedStyle(element);

            Line($"    {Truncate(selector, 38),-38} " +
                 $"class='{element.GetAttribute("class") ?? "-"}' style='{element.GetAttribute("style") ?? "-"}'");

            Line($"    {"",-38} " +
                 String.Join("  ", properties.Select(name =>
                     $"{name}={computed.GetPropertyValue(name) ?? "-"}")
                     .Where(pair => !pair.EndsWith("=", StringComparison.Ordinal))));
        }
    }

    /// <summary>
    /// The chain of boxes behind the two numbers a browser disagrees with - how far the page reaches
    /// along the line, and how far down - each walked up to the root with everything about its size the
    /// style declares, and with the rectangles of the siblings it was placed beside.
    /// <c>dotnet test src/Hutu.Tests/Hutu.Tests.csproj --filter "FullyQualifiedName~WikipediaProbeTests.OverflowChainDiagnostic"</c>
    /// </summary>
    [Test]
    [NonParallelizable]
    [CancelAfter(300_000)]
    public async Task OverflowChainDiagnostic()
    {
        using var measurer = new SkiaTextMeasurer();
        using var view = new HutuViewInternal(measurer, null);
        view.SetViewportSize(Width, Height);

        var document = await view.LoadDocumentAsync(Url, CancellationToken.None);
        var laid = view.Layout;

        if (laid is null)
        {
            Line("the engine laid nothing out, so there is no geometry to compare");
            return;
        }

        var parents = new Dictionary<LayoutBox, LayoutBox?>();

        void Map(LayoutBox node, LayoutBox? above)
        {
            parents[node] = above;

            foreach (var child in node.Children)
            {
                Map(child, node);
            }
        }

        Map(laid.Root, null);

        LayoutBox? Above(LayoutBox node) => parents.TryGetValue(node, out var held) ? held : null;

        Boolean Seen(LayoutBox node)
        {
            for (var at = node; at is not null; at = Above(at))
            {
                if (at.Source.Style.IsHidden)
                {
                    return false;
                }
            }

            return true;
        }

        String Tag(LayoutBox node) => node.Source.Element is { } element ? Name(element) : node.Kind.ToString();

        var visible = Walk(laid.Root).Where(Seen).ToList();

        Line($"viewport {Width}x{Height}, no images   document {laid.Width:F0}x{laid.Height:F0}");
        Line($"a browser at 1222 wide: 1207x4384 (the same document, the same day)");

        void Chain(String what, LayoutBox? from)
        {
            Line("");
            Line($"--- {what} ---");

            for (var at = from; at is not null; at = Above(at))
            {
                var style = at.Source.Style;
                var flex = style.Flex;
                var rect = at.BorderBox;

                Line($"  {Truncate(Tag(at), 32),-32} {rect.X,6:F0},{rect.Y,6:F0} {rect.Width,6:F0}x{rect.Height,5:F0}  " +
                     $"display={style.Display,-10} kind={at.Source.Kind,-10} " +
                     $"w={Describe(style.Width),7} minW={Describe(style.MinWidth),7} maxW={Describe(style.MaxWidth),7} " +
                     $"grow={flex.Grow:F0} shrink={flex.Shrink:F0} basis={Describe(flex.Basis),7} justify={flex.JustifyContent} " +
                     $"ws={style.WhiteSpace} pos={style.Position.Position}");

                var above = Above(at);

                if (above is null)
                {
                    continue;
                }

                Line($"  {"",-32}   siblings of this box: " + String.Join(" ", above.Children
                    .Where(child => child.Kind is not LayoutKind.Line or LayoutKind.Text || child.Source.Element is not null)
                    .Take(8)
                    .Select(child =>
                    {
                        var box = child.BorderBox;

                        return $"{Truncate(Tag(child), 22)} {box.X:F0},{box.Y:F0} {box.Width:F0}x{box.Height:F0}";
                    })));
            }
        }

        Chain("the box that reaches furthest along the line", visible.OrderByDescending(node => node.BorderBox.Right).FirstOrDefault());
        Chain("the box that reaches furthest down", visible.OrderByDescending(node => node.BorderBox.Bottom).FirstOrDefault());

        // The subtree of the row that overflows, which is where a size that should have come from the
        // content shows up as a size that came from the container.
        void Subtree(String selector, Int32 limit)
        {
            var element = document.QuerySelector(selector);
            var root = element is null ? null : laid.FindBox(element);

            Line("");
            Line($"--- the boxes under {selector} ---");

            if (root is null)
            {
                Line("  no box");
                return;
            }

            void WalkDown(LayoutBox at, Int32 depth)
            {
                var box = at.Source;
                var style = box.Style;
                var rect = at.BorderBox;
                var declared = box.Element is { } held ? view.GetComputedStyle(held) : null;

                Line($"  {new String(' ', depth * 2)}{Truncate(box.Element is { } named ? Name(named) : box.Kind.ToString(), 34),-34} " +
                     $"{rect.X,6:F0},{rect.Y,6:F0} {rect.Width,6:F0}x{rect.Height,5:F0}  " +
                     $"kind={box.Kind,-10} display={style.Display,-10} " +
                     $"w={Describe(style.Width),8} minW={Describe(style.MinWidth),7} " +
                     $"basis={Describe(style.Flex.Basis),7} grow={style.Flex.Grow:F0} shrink={style.Flex.Shrink:F0} " +
                     $"pos={style.Position.Position} hidden={style.IsHidden}" +
                     $"{(declared is null ? String.Empty : $"  cssW={declared.GetPropertyValue("width")}")}");

                if (depth >= limit)
                {
                    return;
                }

                foreach (var child in at.Children)
                {
                    WalkDown(child, depth + 1);
                }
            }

            WalkDown(root, 0);
        }

        Subtree(".vector-page-toolbar-container", 4);
        Subtree(".vector-header-container", 3);
        Subtree(".vector-sitenotice-container", 2);
        Subtree(".mw-body-header", 2);
        Subtree("#footer", 2);

        Line("");
        Line("--- the containers a browser's rectangles are quoted for ---");

        foreach (var selector in DiagnosticSelectors)
        {
            var element = document.QuerySelector(selector);
            var box = element is null ? null : laid.FindBox(element);
            var rect = box?.BorderBox;

            Line($"  {selector,-18}{(rect is null ? "no box" : $"{rect.Value.X,6:F0},{rect.Value.Y,6:F0}  {rect.Value.Width,5:F0}x{rect.Value.Height,5:F0}")}");
        }

        Line($"  {"document",-18}{laid.Width,6:F0},{laid.Height,6:F0}");
    }

    /// <summary>The last path segment of a URL, without its query - a file name is all a reader can match.</summary>
    private static String Tail(String url)
    {
        var slash = url.LastIndexOf('/');
        var name = slash >= 0 ? url[(slash + 1)..] : url;
        var query = name.IndexOf('?');
        return query >= 0 ? name[..query] : name;
    }

    /// <summary>A length as one line of the report above: its kind, and the number where it has one.</summary>
    private static String Describe(StyleLength length) =>
        length.Kind switch
        {
            StyleLengthKind.Unset => "unset",
            StyleLengthKind.Auto => "auto",
            StyleLengthKind.Percent => (length.Value * 100).ToString("F0") + "%",
            StyleLengthKind.Math => "math",
            StyleLengthKind.Keyword => "none",
            _ => length.Value.ToString("F0") + "px",
        };

    /// <summary>Names an element the way the document does: its tag, its id, its first class.</summary>
    private static String Name(AngleSharp.Dom.IElement element)
    {
        var id = element.GetAttribute("id");
        var classes = element.GetAttribute("class");

        return element.LocalName
            + (id is { Length: > 0 } named ? "#" + named : String.Empty)
            + (classes is { Length: > 0 } some ? "." + some.Split(' ')[0] : String.Empty);
    }

    /// <summary>
    /// Walks up from the directory the test assembly was loaded from to the one that holds the
    /// build-wide settings, which is the repository root. The picture is left there, so this is the
    /// one path the fixture has to know and it finds it rather than being told: a path written down
    /// is right on one machine and wrong on every other.
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

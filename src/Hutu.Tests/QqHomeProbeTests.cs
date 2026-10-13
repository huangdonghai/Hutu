using AngleSharp;
using AngleSharp.Css;
using AngleSharp.Css.Dom;
using AngleSharp.Dom;
using AngleSharp.Html.Dom;
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
using System.Threading;
using System.Threading.Tasks;

namespace Hutu.Tests;

/// <summary>
/// What <c>https://www.qq.com/</c> looks like through the engine, and what the page asks for that the
/// engine has no model for. The page is a server-rendered React tree over one external sheet, so the
/// markup and the cascade are both there for a static renderer - which makes the difference between
/// the two pictures a question of which features are missing rather than of whether anything rendered.
/// </summary>
/// <remarks>
/// Explicit and network-dependent, like the other portal probes: what it reports is that site on that
/// day. Nothing here fails on a number; the report is read against the same page in a browser.
/// <c>dotnet test src/Hutu.Tests/Hutu.Tests.csproj --filter "FullyQualifiedName~QqHomeProbeTests"</c>
/// </remarks>
[TestFixture]
[Explicit]
public sealed class QqHomeProbeTests
{
    private const String Url = "https://www.qq.com/";

    private static readonly Int32 Width = ReadViewport("HUTU_VIEWPORT_WIDTH", 1440);

    private static readonly Int32 Height = ReadViewport("HUTU_VIEWPORT_HEIGHT", 900);

    private static Int32 ReadViewport(String name, Int32 otherwise) =>
        Int32.TryParse(Environment.GetEnvironmentVariable(name), out var pixels) && pixels > 0 ? pixels : otherwise;

    /// <summary>How tall the saved picture is allowed to be, in pixels.</summary>
    private const Int32 MaxShotHeight = 8000;

    /// <summary>The containers the page hangs on, in the order the document meets them.</summary>
    private static readonly String[] Selectors =
    {
        "#root", "#qqhome-top-header", "#qqhome-channel-header", "#qqhome-top-banner",
        "#qqhome-command-area", "#qqhome-report-area", "#qqhome-product-area", "#qqhome-hot-picks",
        "#qqhome-hot-video", "#qqhome-channel-feed", "#qqhome-footer", "#qqhome-float-btn",
    };

    /// <summary>
    /// The properties the page declares that the engine reads nothing from, and so draws nothing for.
    /// Each one is a feature a browser answers and this renderer does not: a box moved or scaled by
    /// <c>transform</c> is drawn where it was laid out, a shadow is never painted, a clamped title runs
    /// to its full number of lines instead of stopping at three, and an animation holds whatever frame
    /// its authoring left it on.
    /// </summary>
    private static readonly String[] Unsupported =
    {
        "transform", "box-shadow", "opacity", "filter", "backdrop-filter", "clip-path",
        "-webkit-line-clamp", "line-clamp", "text-overflow", "text-shadow", "zoom", "animation",
        "background-clip", "-webkit-background-clip", "mask-image", "-webkit-mask-image",
    };

    [Test]
    [NonParallelizable]
    [CancelAfter(480_000)]
    public async Task Profile()
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

        Line($"load, with images   {load.Elapsed.TotalMilliseconds,8:F0}ms   " +
             $"{document.QuerySelectorAll("*").Length} elements, viewport {Width}x{Height}");
        Line($"document            {laid.Width,8:F0}x{laid.Height:F0}   {Walk(laid.Root).Count} boxes");

        Line("");
        Line("--- the sheets the document came with ---");

        var sheets = document.StyleSheets.OfType<ICssStyleSheet>().ToList();

        Line($"  {sheets.Count} sheets, {sheets.Sum(sheet => sheet.Rules.Length)} rules");

        foreach (var sheet in sheets.OrderByDescending(sheet => sheet.Rules.Length).Take(4))
        {
            var href = sheet.OwnerNode?.GetAttribute("href") ?? sheet.Href ?? "inline";

            Line($"  {sheet.Rules.Length,5} rules  {href[..Math.Min(90, href.Length)]}");
        }

        Line("");
        await CensusAsync(document);

        Line("");
        Line("--- where the containers land ---");

        foreach (var selector in Selectors)
        {
            var element = document.QuerySelector(selector);

            if (element is null)
            {
                Line($"  {selector,-26}absent from this document");
                continue;
            }

            var box = laid.FindBox(element);
            var rect = box?.BorderBox;

            Line($"  {selector,-26}" +
                 (rect is null
                     ? "no box"
                     : $"{rect.Value.X,6:F0},{rect.Value.Y,6:F0}  {rect.Value.Width,5:F0}x{rect.Value.Height,5:F0}") +
                 (box is null ? String.Empty : $"   display={box.Source.Style.Display} pos={box.Source.Style.Position.Position}"));
        }

        Line("");
        Line("--- the drawn boxes that reach furthest right, against a viewport " + Width + " wide ---");

        foreach (var node in Walk(laid.Root).OrderByDescending(node => node.BorderBox.Right).Take(8))
        {
            Line($"  reaches {node.BorderBox.Right,7:F0}  x {node.BorderBox.X,7:F0} w {node.BorderBox.Width,6:F0}   " +
                 $"{Tag(node)} [display={node.Source.Style.Display} pos={node.Source.Style.Position.Position}]");
        }

        Line("");
        Line("--- the whole of it, drawn ---");

        var tall = (Int32)Math.Min(Math.Ceiling(Math.Max(laid.Height, Height)), MaxShotHeight);

        using var surface = SKSurface.Create(new SKImageInfo(Width, tall));

        var painted = Stopwatch.StartNew();
        var list = new Painter().Paint(laid);
        new SkiaPaintExecutor(measurer, images).Execute(list, surface.Canvas);
        painted.Stop();

        var path = Path.Combine(RepositoryRoot(), "artifacts", "qq-hutu.png");

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        using (var shot = surface.Snapshot())
        using (var png = shot.Encode(SKEncodedImageFormat.Png, 100))
        using (var into = File.Create(path))
        {
            png.SaveTo(into);
        }

        Line($"  {list.Commands.Count()} commands drawn in {painted.Elapsed.TotalMilliseconds:F0}ms, " +
             $"{Width}x{tall} of a {laid.Height:F0} tall document");
        Line($"  {path}   {new FileInfo(path).Length / 1024}KB");
    }

    /// <summary>
    /// Counts what the page asks for, split into what the engine can answer and what it cannot. The
    /// counts of the unsupported properties are the short way of saying how much of the difference
    /// between the two pictures one feature accounts for.
    /// </summary>
    private async Task CensusAsync(IDocument document)
    {
        Line("--- what the page asks for, and whether the engine answers it ---");

        var styles = document.DefaultView!;
        var elements = document.QuerySelectorAll("*").OfType<IElement>().ToList();
        var counts = new Dictionary<String, Int32>();

        foreach (var element in elements)
        {
            var computed = styles.GetComputedStyle(element);

            foreach (var name in Unsupported)
            {
                // AngleSharp names the declarations it kept, and a property the cascade never carried
                // comes back empty, which is the difference between "asked for" and "not mentioned".
                var value = computed.GetPropertyValue(name);

                if (String.IsNullOrWhiteSpace(value))
                {
                    continue;
                }

                counts[name] = counts.TryGetValue(name, out var held) ? held + 1 : 1;
            }
        }

        var supported = new Dictionary<String, Int32>
        {
            ["flex"] = 0,
            ["grid"] = 0,
            ["absolute/fixed"] = 0,
            ["sticky"] = 0,
        };

        foreach (var element in elements)
        {
            var computed = styles.GetComputedStyle(element);
            var display = computed.GetPropertyValue("display") ?? String.Empty;

            if (display.StartsWith("flex", StringComparison.OrdinalIgnoreCase) ||
                display.StartsWith("inline-flex", StringComparison.OrdinalIgnoreCase))
            {
                supported["flex"]++;
            }
            else if (display.StartsWith("grid", StringComparison.OrdinalIgnoreCase) ||
                     display.StartsWith("inline-grid", StringComparison.OrdinalIgnoreCase))
            {
                supported["grid"]++;
            }

            var position = computed.GetPropertyValue("position") ?? String.Empty;

            if (position.Equals("absolute", StringComparison.OrdinalIgnoreCase) ||
                position.Equals("fixed", StringComparison.OrdinalIgnoreCase))
            {
                supported["absolute/fixed"]++;
            }
            else if (position.Equals("sticky", StringComparison.OrdinalIgnoreCase))
            {
                supported["sticky"]++;
            }
        }

        Line("  answered:  " + String.Join(", ", supported.Select(pair => $"{pair.Key} {pair.Value}")));
        Line("  not modelled, by the number of elements that ask for it:");

        foreach (var pair in counts.OrderByDescending(pair => pair.Value))
        {
            Line($"    {pair.Key,-24}{pair.Value,5}");
        }

        await Task.CompletedTask;
    }

    /// <summary>
    /// The chain from the page's outermost container down to the boxes that carry its text, which is
    /// where a size the engine got wrong shows up as the container it came from.
    /// </summary>
    [Test]
    [NonParallelizable]
    [CancelAfter(300_000)]
    public async Task Landmarks()
    {
        using var measurer = new SkiaTextMeasurer();
        using var view = new HutuViewInternal(measurer);
        view.SetViewportSize(Width, Height);

        var document = await view.LoadDocumentAsync(Url, CancellationToken.None);
        var laid = view.Layout;

        if (laid is null)
        {
            Line("the engine laid nothing out");
            return;
        }

        Line($"viewport {Width}x{Height}, no images   document {laid.Width:F0}x{laid.Height:F0}");

        foreach (var selector in Selectors)
        {
            var element = document.QuerySelector(selector);
            var box = element is null ? null : laid.FindBox(element);

            if (box is null)
            {
                Line($"  {selector,-26}{(element is null ? "absent" : "no box")}");
                continue;
            }

            var style = box.Source.Style;
            var rect = box.BorderBox;

            Line($"  {selector,-26}{rect.X,6:F0},{rect.Y,6:F0} {rect.Width,6:F0}x{rect.Height,5:F0}  " +
                 $"display={style.Display,-10} pos={style.Position.Position,-8} " +
                 $"w={Describe(style.Width),7} h={Describe(style.Height),7} hidden={style.IsHidden}");
        }
    }

    /// <summary>
    /// Every box under a container, one line each, with the kind the engine gave it and whether any
    /// text ended up inside. This is where missing copy is traced: an element that produced boxes but
    /// no line holds nothing a reader can read. The container is handed in because the question moves
    /// - the nav bar's order one day, the cards' sizes the next.
    /// </summary>
    [Test]
    [NonParallelizable]
    [CancelAfter(300_000)]
    public async Task Subtree()
    {
        var selector = Environment.GetEnvironmentVariable("HUTU_SUBTREE_SELECTOR") is { Length: > 0 } asked
            ? asked
            : "#qqhome-hot-picks";
        var limit = Int32.TryParse(Environment.GetEnvironmentVariable("HUTU_SUBTREE_DEPTH"), out var depth) && depth > 0
            ? depth
            : 5;

        using var measurer = new SkiaTextMeasurer();
        using var view = new HutuViewInternal(measurer);
        view.SetViewportSize(Width, Height);

        var document = await view.LoadDocumentAsync(Url, CancellationToken.None);
        var laid = view.Layout;

        if (laid is null)
        {
            Line("the engine laid nothing out");
            return;
        }

        var element = document.QuerySelector(selector);

        if (element is null)
        {
            Line($"{selector}: absent from this document");
            return;
        }

        var root = laid.FindBox(element);

        if (root is null)
        {
            Line($"{selector}: the container produced no box");
            return;
        }

        Line($"{selector}: {Walk(laid.Root).Count} boxes in the document, the subtree under {root.BorderBox.Width:F0}x{root.BorderBox.Height:F0} " +
             $"at {root.BorderBox.X:F0},{root.BorderBox.Y:F0}, read to depth {limit}");

        Int32 ordinal = 0;

        void Report(LayoutBox node, Int32 depth)
        {
            if (depth <= limit)
            {
                var style = node.Source.Style;
                var rect = node.BorderBox;
                var text = node.Text is { Length: > 0 } held ? "  text=" + Truncate(held, 24) : String.Empty;

                Line($"  {new String(' ', depth * 2)}{Truncate(Tag(node), 44),-44} " +
                     $"{rect.X,5:F0},{rect.Y,5:F0} {rect.Width,5:F0}x{rect.Height,4:F0}  " +
                     $"kind={node.Kind,-12} display={style.Display,-11} " +
                     $"fs={style.GetFont().Size:F0} order={++ordinal,3} hidden={style.IsHidden}{text}");
            }

            foreach (var child in node.Children)
            {
                Report(child, depth + 1);
            }
        }

        Report(root, 0);
    }

    /// <summary>
    /// What a click on this page costs, gesture by gesture: the hit that names a link, the hover and
    /// the press that may restyle the document, and the load that a navigation asks for afterwards.
    /// A reader reports that clicking hangs, and a hang is only ever one of these - a stage that
    /// never prints its number is the one that did not come back.
    /// <c>dotnet test src/Hutu.Tests/Hutu.Tests.csproj --filter "FullyQualifiedName~QqHomeProbeTests.LinkNavigation"</c>
    /// </summary>
    [Test]
    [NonParallelizable]
    [CancelAfter(600_000)]
    public async Task LinkNavigation()
    {
        using var measurer = new SkiaTextMeasurer();
        using var view = new HutuViewInternal(measurer);
        view.SetViewportSize(Width, Height);

        var home = Stopwatch.StartNew();
        var document = await view.LoadDocumentAsync(Url, CancellationToken.None);
        home.Stop();

        Line($"home document      {home.Elapsed.TotalMilliseconds,8:F0}ms   " +
             $"{document.QuerySelectorAll("a").Length} links, {document.QuerySelectorAll("*").Length} elements, " +
             $"{document.StyleSheets.Length} sheets");

        var picks = new[]
        {
            "#qqhome-channel-header .nav-link",
            "#qqhome-command-area a[href]",
            "#qqhome-hot-picks a[href]",
            "#qqhome-footer a[href]",
        };

        // Read out before anything is navigated to: a load replaces the layout, and the rectangles the
        // links had on this page are gone with it.
        var places = picks
            .Select(pick => (pick, anchor: document.QuerySelector(pick) as IHtmlAnchorElement))
            .Where(found => found.anchor is not null)
            .Select(found => (found.pick, found.anchor!, box: view.Layout?.FindBox(found.anchor!)?.BorderBox))
            .ToList();

        foreach (var (selector, anchor, box) in places)
        {
            Line("");
            Line($"--- {selector} ---");
            Line($"  href={anchor.Href}  target={anchor.Target ?? "(none)"}  rel={anchor.GetAttribute("rel") ?? "(none)"}  " +
                 $"box={(box is null ? "none" : $"{box.Value.Width:F0}x{box.Value.Height:F0} at {box.Value.X:F0},{box.Value.Y:F0}")}");
        }

        Line("");
        Line("--- what each address costs to load ---");

        var loadedEven = 0;

        foreach (var (selector, anchor, _) in places)
        {
            var budget = new CancellationTokenSource(TimeSpan.FromSeconds(120));
            var watched = Stopwatch.StartNew();

            try
            {
                var next = await view.LoadDocumentAsync(anchor.Href, budget.Token);
                watched.Stop();
                Line($"  {Truncate(selector, 34),-34}{watched.Elapsed.TotalMilliseconds,7:F0}ms  " +
                     $"{next.QuerySelectorAll("*").Length,5} elements, {next.StyleSheets.Length} sheets, " +
                     $"document {view.Layout?.Width,6:F0}x{view.Layout?.Height:F0}");

                // What a gesture on the page that arrived would cost: a rebuild is a viewport change
                // asked for by the pipeline, and the pipeline is what a hover drives. The width has to
                // actually move for the rebuild to happen, so the pages trade between two of them.
                var again = Stopwatch.StartNew();
                view.SetViewportSize(loadedEven % 2 == 0 ? Width + 1 : Width, Height);
                loadedEven++;
                again.Stop();
                Line($"  {"",-34}{again.Elapsed.TotalMilliseconds,7:F0}ms  one rebuild of what came back " +
                     $"({(view.Layout is null ? 0 : Walk(view.Layout.Root).Count)} boxes)");
            }
            catch (Exception exception)
            {
                watched.Stop();
                Line($"  {Truncate(selector, 34),-34}{watched.Elapsed.TotalMilliseconds,7:F0}ms  " +
                     $"{exception.GetType().Name}: {Truncate(exception.Message, 90)}");
            }
        }

        Line("");
        Line("--- the gestures, on the home page again ---");

        await view.LoadDocumentAsync(Url, CancellationToken.None);

        foreach (var (selector, anchor, box) in places)
        {
            if (box is null)
            {
                continue;
            }

            var x = box.Value.X + box.Value.Width / 2;
            var y = box.Value.Y + box.Value.Height / 2;

            var watched = Stopwatch.StartNew();
            var link = view.LinkAt(x, y);
            watched.Stop();
            Line($"  {Truncate(selector, 34),-34}LinkAt {watched.Elapsed.TotalMilliseconds,7:F1}ms -> {(link is null ? "nothing" : Truncate(link.Value.Url, 40))}");

            watched.Restart();
            var moved = view.PointerMoved(x, y);
            watched.Stop();
            Line($"  {"",-34}PointerMoved {watched.Elapsed.TotalMilliseconds,6:F1}ms restyles={moved.Changed} cursor={moved.Cursor}");

            watched.Restart();
            var pressed = view.PointerPressed(x, y);
            watched.Stop();
            Line($"  {"",-34}PointerPressed {watched.Elapsed.TotalMilliseconds,4:F1}ms restyles={pressed}");

            watched.Restart();
            var released = view.PointerReleased();
            watched.Stop();
            Line($"  {"",-34}PointerReleased {watched.Elapsed.TotalMilliseconds,3:F1}ms restyles={released}");

            watched.Restart();
            var scrolled = view.Activate(link!.Value);
            watched.Stop();
            Line($"  {"",-34}Activate {watched.Elapsed.TotalMilliseconds,15:F1}ms in-document anchor={(scrolled ?? "-")}");

            view.PointerExited();
        }
    }

    /// <summary>
    /// What one rebuild of this page is made of, and what a run of hovers costs. A click is not one
    /// gesture but three - a move that changes the hover, a press, a release - and each of them can ask
    /// for the whole document to be built again, so the price of a rebuild is the price of a click.
    /// <c>dotnet test src/Hutu.Tests/Hutu.Tests.csproj --filter "FullyQualifiedName~QqHomeProbeTests.GestureCost"</c>
    /// </summary>
    [Test]
    [NonParallelizable]
    [CancelAfter(600_000)]
    public async Task GestureCost()
    {
        using var measurer = new SkiaTextMeasurer();
        using var view = new HutuViewInternal(measurer);
        view.SetViewportSize(Width, Height);

        var document = await view.LoadDocumentAsync(Url, CancellationToken.None);
        var device = new HutuRenderDevice(Width, Height);

        Line($"document           {document.QuerySelectorAll("*").Length} elements, " +
             $"{SheetsOf(document).Sum(sheet => sheet.Rules.Length)} rules counted with the reader's own sheet");

        var faces = Timed("font face table", () => FontFaceTable.From(document));
        var index = Timed("rule index", () => RuleBucketIndex.Build(SheetsOf(document), device));
        // The build is the cost; this says how much of it is finding which rules match an element
        // rather than what is done with the answer afterwards. Asked before the build, so that the
        // index's own cache of matched cascades is as empty as it is when the build starts. Measured
        // once already: matching is 15ms of a 226ms build on this page, so what is left is the style
        // resolution per element, not the selectors.
        Timed("matching alone", () =>
        {
            foreach (var element in document.QuerySelectorAll("*"))
            {
                MatchedCascade.ForElement(index!, element);
            }

            return true;
        });

        var boxes = Timed("fused build", () => BoxBuilder.TryBuildFused(document, device, (IImageProvider?)null, faces));
        Line($"  fused build returned {(boxes is null ? "NULL - the document goes to the other path" : "a box tree")}");

        if (boxes is null)
        {
            return;
        }

        var laid = Timed("layout", () => new LayoutEngine(measurer).Layout(boxes, Width, Height));
        var painted = Timed("paint list", () => new Painter().Paint(laid!));
        Line($"  {Walk(laid!.Root).Count} boxes, {painted!.Commands.Count()} commands");

        // The same build the pipeline runs on a hover, a press and a release, asked for in turn: the
        // first of a series is never the interesting one, because whatever it warms up is warm for the
        // ones after it.
        var hoverable = new[] { "#qqhome-channel-header .nav-link", "#qqhome-command-area a[href]" }
            .Select(document.QuerySelector)
            .Where(found => found is not null)
            .ToList();

        if (hoverable.Count < 2)
        {
            Line("  the page does not offer two places to move between");
            return;
        }

        Line("");
        Line("--- six moves between two links, each of which changes what is hovered ---");

        for (var round = 0; round < 6; round++)
        {
            var element = hoverable[round % hoverable.Count];
            var box = view.Layout!.FindBox(element!);

            if (box is null)
            {
                continue;
            }

            var stopwatch = Stopwatch.StartNew();
            var info = view.PointerMoved(box.BorderBox.X + 2, box.BorderBox.Y + 2);
            stopwatch.Stop();

            Line($"  move {round} to {Truncate(Name(element!), 30),-30}{stopwatch.Elapsed.TotalMilliseconds,7:F1}ms  restyled={info.Changed}");
        }

        Line("");
        Line("--- a press and a release on a link, which the engine's own link colour always rebuilds over ---");

        var first = view.Layout!.FindBox(hoverable[0]!);
        var x = first!.BorderBox.X + 2;
        var y = first.BorderBox.Y + 2;

        var pressed = Stopwatch.StartNew();
        var changed = view.PointerPressed(x, y);
        pressed.Stop();
        Line($"  press             {pressed.Elapsed.TotalMilliseconds,7:F1}ms  restyled={changed}");

        pressed.Restart();
        var released = view.PointerReleased();
        pressed.Stop();
        Line($"  release           {pressed.Elapsed.TotalMilliseconds,7:F1}ms  restyled={released}");

        pressed.Restart();
        view.PointerExited();
        pressed.Stop();
        Line($"  exit              {pressed.Elapsed.TotalMilliseconds,7:F1}ms");
    }

    /// <summary>Names an element the way the document does: its tag, its id, its first class.</summary>
    private static String Name(IElement element)
    {
        var id = element.GetAttribute("id");
        var classes = element.GetAttribute("class");

        return element.LocalName
            + (id is { Length: > 0 } named ? "#" + named : String.Empty)
            + (classes is { Length: > 0 } some ? "." + some.Split(' ')[0] : String.Empty);
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

    private static T? Timed<T>(String name, Func<T> work)
    {
        var stopwatch = Stopwatch.StartNew();
        var answer = work();
        stopwatch.Stop();
        Line($"  {name,-16}{stopwatch.Elapsed.TotalMilliseconds,8:F1}ms");
        return answer;
    }

    /// <summary>
    /// Why one build of this page costs what it does. The rule index keeps a rule out of an element's
    /// way when the selector can be reduced to a necessary condition - a class, an id, a tag - and a
    /// selector that cannot be reduced is consulted for every element in the document. The count of
    /// those, and what they look like, is the difference between a build that walks a few rules per
    /// element and one that walks the whole sheet.
    /// <c>dotnet test src/Hutu.Tests/Hutu.Tests.csproj --filter "FullyQualifiedName~QqHomeProbeTests.BuildCost"</c>
    /// </summary>
    [Test]
    [NonParallelizable]
    [CancelAfter(300_000)]
    public async Task BuildCost()
    {
        using var measurer = new SkiaTextMeasurer();
        using var view = new HutuViewInternal(measurer);
        view.SetViewportSize(Width, Height);

        var document = await view.LoadDocumentAsync(Url, CancellationToken.None);
        var elements = document.QuerySelectorAll("*").Length;

        var styleRules = 0;
        var keyless = 0;
        var unparsed = 0;
        var hover = 0;
        var active = 0;
        var samples = new List<String>();
        var unparsedSamples = new List<String>();

        foreach (var sheet in SheetsOf(document))
        {
            Collect(sheet.Rules);
        }

        void Collect(ICssRuleList rules)
        {
            for (var i = 0; i < rules.Length; i++)
            {
                if (rules[i] is ICssStyleRule style)
                {
                    styleRules++;

                    var text = style.SelectorText ?? String.Empty;

                    if (text.Contains(":hover", StringComparison.OrdinalIgnoreCase))
                    {
                        hover++;
                    }

                    if (text.Contains(":active", StringComparison.OrdinalIgnoreCase))
                    {
                        active++;
                    }

                    if (style.Selector is null)
                    {
                        unparsed++;

                        if (unparsedSamples.Count < 6)
                        {
                            unparsedSamples.Add(Truncate(text, 150));
                        }
                    }
                    else if (SelectorKeyExtractor.Extract(style.Selector) is null)
                    {
                        keyless++;

                        if (samples.Count < 10)
                        {
                            samples.Add(Truncate(text, 150));
                        }
                    }

                    Collect(style.Rules);
                }
                else if (rules[i] is ICssGroupingRule group)
                {
                    Collect(group.Rules);
                }
            }
        }

        Line($"{elements} elements, {styleRules} style rules counted with the reader's own sheet");
        Line($"  rules whose selector did not parse  {unparsed,6}   (nothing can match them)");
        Line($"  rules the index cannot key          {keyless,6}   ({keyless * elements:N0} pairs consulted against every element)");
        Line($"  rules mentioning :hover             {hover,6}");
        Line($"  rules mentioning :active            {active,6}");
        Line("");
        Line("--- the unkeyable selectors, whole ---");

        foreach (var sample in samples)
        {
            Line($"  {sample}");
        }

        Line("");
        Line("--- the unparsable ones ---");

        foreach (var sample in unparsedSamples)
        {
            Line($"  {sample}");
        }
    }

    private static String Truncate(String text, Int32 room) =>
        text.Length <= room ? text : text[..(room - 1)] + "…";

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

    private static String Tag(LayoutBox node)
    {
        var element = node.Source.Element;
        var classes = element?.GetAttribute("class");

        return (element?.LocalName ?? node.Kind.ToString())
            + (classes is { Length: > 0 } some ? "." + some.Split(' ')[0] : String.Empty);
    }

    private static void Line(String text) => TestContext.Progress.WriteLine(text);

    private static List<LayoutBox> Walk(LayoutBox box)
    {
        var into = new List<LayoutBox> { box };

        foreach (var child in box.Children)
        {
            into.AddRange(Walk(child));
        }

        return into;
    }

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
}

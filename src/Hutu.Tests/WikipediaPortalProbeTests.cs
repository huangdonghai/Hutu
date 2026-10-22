using AngleSharp.Css.Dom;
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
using System.Threading;
using System.Threading.Tasks;

namespace Hutu.Tests;

/// <summary>
/// What <c>https://www.wikipedia.org/</c> - the language portal, not the encyclopedia's main page -
/// comes to through the engine, against the same page read in a browser at the same viewport.
/// </summary>
/// <remarks>
/// Explicit and network-dependent, like the other portal probes, so what it reports is that site on
/// that day. The page is the smallest of the ones this suite reads - three inline sheets, four hundred
/// rules, no grid and almost no float - which makes it the one place where a difference between the
/// two pictures is a whole feature missing rather than a detail of one.
/// <para>
/// Both sides are read <em>without</em> scripts. The engine runs none, and the page's own script
/// rewrites what it renders - it swaps the slogan's language, hides the language list behind a
/// <c>max-height: 0</c> and a button, and moves the <c>select</c> out of the flow - so the figures
/// quoted here were taken from a frame the browser had disarmed with <c>sandbox="allow-same-origin"</c>
/// and no <c>allow-scripts</c>, which leaves a real same-origin document at the real address with its
/// sheets and sprite intact. That variant is the one this renderer answers to: <c>html</c> still
/// <c>no-js</c>, the list open, the button gone, the document 2488 tall rather than 1237.
/// </para>
/// <para>
/// The width is the browser's content width rather than its window: a page that overflows
/// vertically spends fifteen pixels on a classic scrollbar, so 1236 of window is 1221 of initial
/// containing block - and this page's centred column is <c>max-width</c>-bound to that, which moves
/// every centred box by seven and a half pixels if it is got wrong. Its colours are the dark scheme's,
/// which the engine has no media feature for: read the colours of the quoted boxes rather than the
/// document's background.
/// </para>
/// <c>dotnet test src/Hutu.Tests/Hutu.Tests.csproj --filter "FullyQualifiedName~WikipediaPortalProbeTests"</c>
/// </remarks>
[TestFixture]
[Explicit]
public sealed class WikipediaPortalProbeTests
{
    private const String Url = "https://www.wikipedia.org/";

    private static readonly Int32 Width = ReadViewport("HUTU_VIEWPORT_WIDTH", 1221);

    private static readonly Int32 Height = ReadViewport("HUTU_VIEWPORT_HEIGHT", 1237);

    private static Int32 ReadViewport(String name, Int32 otherwise) =>
        Int32.TryParse(Environment.GetEnvironmentVariable(name), out var pixels) && pixels > 0 ? pixels : otherwise;

    /// <summary>How tall the saved picture is allowed to be, in pixels: enough for the whole page.</summary>
    private const Int32 MaxShotHeight = 8000;

    /// <summary>
    /// The containers whose browser geometry is quoted in the remarks, in document order. A repeated
    /// class is asked for as a set, since the page hangs a whole group on each of them.
    /// </summary>
    private static readonly String[] Selectors =
    {
        "html", "body", "main", "div.central-textlogo", "h1.central-textlogo-wrapper",
        "span.central-textlogo__image", "strong.localized-slogan", "img.central-featured-logo",
        "nav.central-featured", "div.search-container", "form#search-form", "fieldset",
        "div#search-input", "input#searchInput", "select#searchLanguage", "button[type=submit]",
        "i.svg-search-icon", "i.svg-arrow-down",
        "div.lang-list-button-wrapper", "button#js-lang-list-button", "div.lang-list-border",
        "div.lang-list-container", "div#js-lang-lists", "h2.bookshelf-container",
        "footer.footer", "div.footer-sidebar", "nav.other-projects", "p.site-license",
    };

    /// <summary>The groups whose members are each quoted, rather than the group alone.</summary>
    private static readonly String[] Groups =
    {
        "div.central-featured-lang", "div.langlist", "footer nav div.other-project",
    };

    /// <summary>
    /// What the page asks for that the engine reads nothing from. Each one is a feature a browser
    /// answers and this renderer does not: a list flowed into columns is written in one column, an
    /// <c>opacity</c> of zero is drawn at full strength, and an inverted sprite is drawn the way it was
    /// stored. Generated content is listed too although this page turns its own separators off -
    /// <c>.langlist&gt;ul&gt;li:before{content:none}</c> beats the <c>" · "</c> the list class would
    /// otherwise ask for - so a difference here is not one a reader of this page can see.
    /// </summary>
    private static readonly String[] Unsupported =
    {
        "column-width", "column-count", "columns", "break-inside", "content", "opacity", "filter",
        "-webkit-filter", "box-shadow", "transition", "appearance", "-webkit-appearance",
    };

    [Test]
    [NonParallelizable]
    [CancelAfter(420_000)]
    public async Task Profile()
    {
        using var measurer = new SkiaTextMeasurer();
        using var images = new SkiaImageLoader();
        using var view = new HutuViewInternal(measurer, images);
        view.SetViewportSize(Width, Height);

        var load = Stopwatch.StartNew();
        var document = await view.LoadUrlAsync(Url, CancellationToken.None);
        load.Stop();

        Line($"LoadUrlAsync        {load.Elapsed.TotalMilliseconds,8:F0}ms   " +
             $"{document.QuerySelectorAll("*").Length} elements (a browser's live DOM: 1163)");

        // The page carries no script of its own that the engine could run, so it stays in the state the
        // markup is written in: `no-js`, which is the variant a reader with JavaScript off is served.
        // Every number quoted from the browser here is from that same variant unless it says otherwise.
        Line($"  html class        {document.DocumentElement?.GetAttribute("class") ?? "none"}" +
             $"   (the browser's, before its script runs: no-js; after: js-enabled)");
        Line($"  body class        {document.Body?.GetAttribute("class") ?? "none"}");
        Line($"  lang              {document.DocumentElement?.GetAttribute("lang") ?? "none"}" +
             $"   (a browser's script rewrites this to the reader's language and fetches the slogan)");

        var sheets = document.StyleSheets.OfType<ICssStyleSheet>().ToList();

        Line("");
        Line("--- the sheets, which are all inline ---");
        Line($"  loaded            {sheets.Count} sheets, {sheets.Sum(s => s.Rules.Length)} rules " +
             "(a browser: 3 sheets, 405 rules)");

        foreach (var sheet in sheets.OrderByDescending(s => s.Rules.Length))
        {
            Line($"  {sheet.Rules.Length,5} rules   {(String.IsNullOrWhiteSpace(sheet.Href) ? "inline" : sheet.Href)}");
        }

        var media = sheets
            .SelectMany(s => s.Rules.OfType<ICssMediaRule>())
            .GroupBy(r => r.Media.MediaText, StringComparer.Ordinal)
            .OrderByDescending(g => g.Sum(r => r.Rules.Length));

        Line($"  media conditions  {String.Join(", ", media.Select(g => $"{g.Key} ({g.Sum(r => r.Rules.Length)} rules)").Take(6))}");

        var laid = view.Layout;

        if (laid is null)
        {
            Line("the engine laid nothing out, so there is no geometry to compare");
            return;
        }

        Line("");
        Line($"--- the document ---");
        Line($"  laid out          {laid.Width:F0}x{laid.Height:F0}   (a browser, no scripts: 1221x2488)");
        Line($"  deepest box       {Walk(laid.Root).Select(n => n.BorderBox.Bottom).Max():F0}");

        Line("");
        Line("--- the containers, against a browser's boxes at the same width ---");
        Line("                        engine x     y      w      h  |  browser x     y      w      h");

        foreach (var selector in Selectors)
        {
            var element = document.QuerySelector(selector);
            var box = BoxOf(laid, element);

            Line($"  {Truncate(selector, 24),-24} {Row(box)}");
        }

        Line("");
        Line("--- the groups, member by member ---");

        foreach (var selector in Groups)
        {
            var found = document.QuerySelectorAll(selector);

            Line($"  {selector}  ({found.Length} of them)");

            foreach (var element in found.Take(12))
            {
                var identity = element.Id ?? String.Empty;
                var cls = element.ClassList?.Length > 0 ? "." + String.Join(".", element.ClassList) : String.Empty;

                Line($"    {Truncate(identity.Length > 0 ? identity : cls, 26),-26} {Row(BoxOf(laid, element))}");
            }
        }

        Line("");
        Line("--- the first languages of each list, and what separates them ---");

        foreach (var list in document.QuerySelectorAll("div.langlist").Take(6))
        {
            var items = list.QuerySelectorAll("li").Take(3).ToList();
            var boxes = items.Select(item => BoxOf(laid, item)).ToList();

            Line($"  {Truncate(String.Join(".", list.ClassList), 30),-30} " +
                 $"{items.Count} items  " +
                 String.Join("  ", boxes.Select(b => b is null ? "no box" : $"{b.Value.X:F0},{b.Value.Y:F0} {b.Value.Width:F0}x{b.Value.Height:F0}")));

            var first = items.FirstOrDefault();

            if (first is not null)
            {
                var before = view.GetComputedStyle(first, "::before");

                Line($"      a li: display={view.GetComputedStyle(first).GetPropertyValue("display")} " +
                     $"columns of its parent ul={view.GetComputedStyle(list).GetPropertyValue("column-width")}/" +
                     $"{view.GetComputedStyle(list).GetPropertyValue("column-count")} " +
                     $":before content={Quote(before.GetPropertyValue("content"))}");
            }
        }

        Line("");
        Line("--- the pictures ---");

        foreach (var node in Walk(laid.Root).Where(n => n.Source.IsReplaced).Take(10))
        {
            var box = node.Source;
            var element = box.Element;
            var attr = element?.GetAttribute("width");
            var attrHeight = element?.GetAttribute("height");
            var declared = attr is null || attrHeight is null ? "none" : attr + "x" + attrHeight;
            var file = box.SourceUrl is { Length: > 0 } url ? Tail(url) : "(no url)";
            var intrinsic = box.IntrinsicSize;

            Line($"    {Truncate(file, 44),-44} {declared,9} declared, " +
                 $"{(intrinsic is null ? "nothing arrived" : $"{intrinsic.Value.Width:F0}x{intrinsic.Value.Height:F0} intrinsic")}, " +
                 $"box {node.BorderBox.Width:F0}x{node.BorderBox.Height:F0} at {node.BorderBox.X:F0},{node.BorderBox.Y:F0}");
        }

        var spritely = Walk(laid.Root)
            .Where(n => n.Source.Element?.ClassList?.Contains("sprite") == true)
            .ToList();

        Line($"  sprite elements   {spritely.Count} share one sheet, placed by background-position");

        foreach (var node in spritely.Take(6))
        {
            var layers = node.Source.Style.Backgrounds;
            var layer = layers.Count > 0 ? layers[0] : null;

            Line($"    {Truncate(String.Join(".", node.Source.Element!.ClassList), 44),-44} " +
                 $"box {node.BorderBox.Width:F0}x{node.BorderBox.Height:F0}  " +
                 $"{(layer is null ? "no background layer" : $"{layer.SizeKind} at {layer.PositionX.Resolve(node.BorderBox.Width):F0},{layer.PositionY.Resolve(node.BorderBox.Height):F0}")}");
        }

        Line("");
        Line("--- what the page asks for that the engine reads nothing from ---");

        var elements = document.QuerySelectorAll("*").OfType<IElement>().ToList();
        var counts = new Dictionary<String, Int32>();

        foreach (var element in elements)
        {
            var computed = view.GetComputedStyle(element);

            foreach (var name in Unsupported)
            {
                var value = computed.GetPropertyValue(name);

                if (String.IsNullOrWhiteSpace(value) || value == "none" || value == "auto" || value == "normal")
                {
                    continue;
                }

                counts[name] = counts.TryGetValue(name, out var held) ? held + 1 : 1;
            }
        }

        foreach (var entry in counts.OrderByDescending(c => c.Value))
        {
            Line($"  {entry.Key,-18} {entry.Value,5} elements ask for it");
        }

        Line($"  body background   {view.GetComputedStyle(document.Body!).GetPropertyValue("background-color")}" +
             "   (a browser on a dark-preference machine: rgba(16,20,24,1) - the media feature is not one this engine answers)");

        Line("");
        Line("--- the globe, which a browser lifts out of the flow and pins inside the logo block ---");

        foreach (var selector in new[] { "div.central-textlogo", "img.central-featured-logo" })
        {
            var asked = document.QuerySelector(selector);

            if (asked is null)
            {
                continue;
            }

            var cascade = view.GetComputedStyle(asked);
            var node = Walk(laid.Root).FirstOrDefault(n => ReferenceEquals(n.Source.Element, asked));

            Line($"  {Truncate(selector, 26),-26} cascade position {cascade.GetPropertyValue("position"),-9} " +
                 $"top {cascade.GetPropertyValue("top"),-8} left {cascade.GetPropertyValue("left"),-8} " +
                 $"display {cascade.GetPropertyValue("display")}");
            Line($"  {Truncate(selector, 26),-26} engine  position {node?.Source.Style.Position.Position,-9} " +
                 $"mode {node?.Source.Kind} box {(node is null ? "none" : $"{node.BorderBox.X:F0},{node.BorderBox.Y:F0} {node.BorderBox.Width:F0}x{node.BorderBox.Height:F0}")}");
        }

        Line("");
        Line("--- the sizes the page declares, and the ones the engine read ---");

        foreach (var selector in new[] { "html", "body", "div.central-textlogo", "h1.central-textlogo-wrapper",
            "span.central-textlogo__image", "strong.localized-slogan", "div.langlist ul", "div.langlist ul li" })
        {
            var asked = document.QuerySelector(selector);

            if (asked is null)
            {
                Line($"  {Truncate(selector, 30),-30} the document has no such element");
                continue;
            }

            var computed = view.GetComputedStyle(asked);

            Line($"  {Truncate(selector, 30),-30} " +
                 $"font {computed.GetPropertyValue("font-size"),-9} " +
                 $"line {computed.GetPropertyValue("line-height"),-9} " +
                 $"width {computed.GetPropertyValue("width"),-9} " +
                 $"mtop {computed.GetPropertyValue("margin-top")}");
        }

        Line("");
        Line("--- one sprite element, layer by layer ---");

        var frame = document.QuerySelector("span.central-textlogo__image");

        if (frame is not null)
        {
            var cascade = view.GetComputedStyle(frame);
            var frameBox = BoxOf(laid, frame);
            var frameNode = Walk(laid.Root).FirstOrDefault(n => ReferenceEquals(n.Source.Element, frame));

            Line($"  cascade   image      {Truncate(cascade.GetPropertyValue("background-image"), 96)}");
            Line($"  cascade   position   {cascade.GetPropertyValue("background-position")}");
            Line($"  cascade   size       {cascade.GetPropertyValue("background-size")}");
            Line($"  cascade   repeat     {cascade.GetPropertyValue("background-repeat")}");
            Line($"  engine    box        {(frameBox is null ? "no box" : $"{frameBox.Value.Width:F0}x{frameBox.Value.Height:F0}")}");

            if (frameNode is not null)
            {
                foreach (var layer in frameNode.Source.Style.Backgrounds)
                {
                    var area = frameBox!.Value;
                    var width = layer.SizeWidth.Resolve(area.Width);
                    var height = layer.SizeHeight.Resolve(area.Height);

                    Line($"  engine    layer      {(layer.Url is null ? "gradient" : Path.GetFileName(layer.Url))} " +
                         $"{layer.SizeKind} {width:F0}x{height:F0} " +
                         $"at {area.X + layer.PositionX.Resolve(area.Width - width):F0}," +
                         $"{area.Y + layer.PositionY.Resolve(area.Height - height):F0} " +
                         $"repeat={layer.RepeatX}/{layer.RepeatY}");
                }
            }
        }

        Line("");
        Line("--- the whole of it, drawn ---");

        var tall = (Int32)Math.Min(Math.Ceiling(Math.Max(laid.Height, Height)), MaxShotHeight);

        using var surface = SKSurface.Create(new SKImageInfo(Width, tall));

        var painted = new Painter().Paint(laid);
        var drew = Stopwatch.StartNew();
        new SkiaPaintExecutor(measurer, images).Execute(painted, surface.Canvas);
        drew.Stop();

        var path = Path.Combine(RepositoryRoot(), "artifacts", "wikipedia-portal-hutu.png");

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        using (var shot = surface.Snapshot())
        using (var png = shot.Encode(SKEncodedImageFormat.Png, 100))
        using (var into = File.Create(path))
        {
            png.SaveTo(into);
        }

        Line($"  {painted.Commands.Count(),8} commands drawn in {drew.Elapsed.TotalMilliseconds:F0}ms, " +
             $"{Width}x{tall} of a {laid.Height:F0} tall document");
        Line($"  {path}   {new FileInfo(path).Length / 1024}KB");
    }

    /// <summary>
    /// The box the engine gave an element, next to the box a browser gave the same one. The browser's
    /// figures are the ones this fixture was written against and are kept here rather than in a table
    /// so the two are read side by side; the ones no longer quoted are left blank.
    /// </summary>
    private static String Row(Rect? box)
    {
        if (box is null)
        {
            return "no box at all".PadRight(35) + "|";
        }

        var b = box.Value;

        return $"{b.X,7:F0} {b.Y,6:F0} {b.Width,6:F0} {b.Height,6:F0}  |";
    }

    private static Rect? BoxOf(LayoutResult layout, IElement? element)
    {
        if (element is null)
        {
            return null;
        }

        var node = Walk(layout.Root).FirstOrDefault(n => ReferenceEquals(n.Source.Element, element));

        return node?.BorderBox;
    }

    private static String Tail(String url)
    {
        var slash = url.LastIndexOf('/');

        return slash < 0 ? url : url[(slash + 1)..];
    }

    private static String Quote(String value) =>
        String.IsNullOrEmpty(value) ? "(empty)" : "\"" + value + "\"";

    private static IEnumerable<LayoutBox> Walk(LayoutBox node)
    {
        yield return node;

        foreach (var child in node.Children)
        {
            foreach (var held in Walk(child))
            {
                yield return held;
            }
        }
    }

    private static void Line(String text) => TestContext.Progress.WriteLine(text);

    private static String Truncate(String text, Int32 room) =>
        text.Length <= room ? text : text[..(room - 3)] + "...";

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

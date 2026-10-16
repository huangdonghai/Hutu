using AngleSharp;
using AngleSharp.Css;
using AngleSharp.Css.Dom;
using AngleSharp.Dom;
using Hutu.Rendering;
using Hutu.Skia;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Hutu.Tests;

/// <summary>
/// Why dongchedi.com feels slow to hover when 163.com does not: the probe enumerates the
/// page's real :hover (and :active/:focus/:target) rules and checks whether any of them
/// declares a property outside the engine's PaintOnly whitelist - which is what closes the
/// RestylesWithoutRelayout gate and forces a full relayout on every hover.
/// <c>dotnet test src/Hutu.Tests/Hutu.Tests.csproj --filter "FullyQualifiedName~DongchediProbeTests"</c>
/// </summary>
[TestFixture]
[Explicit]
public sealed class DongchediProbeTests
{
    private const String Url = "https://www.dongchedi.com/";

    private static readonly Int32 Width = ReadViewport("HUTU_VIEWPORT_WIDTH", 1440);

    private static readonly Int32 Height = ReadViewport("HUTU_VIEWPORT_HEIGHT", 900);

    private static Int32 ReadViewport(String name, Int32 otherwise) =>
        Int32.TryParse(Environment.GetEnvironmentVariable(name), out var pixels) && pixels > 0 ? pixels : otherwise;

    /// <summary>Must mirror <c>StyleResolution.PaintOnly</c> exactly - the gate is answered against it.</summary>
    private static readonly HashSet<String> PaintOnly = new(StringComparer.OrdinalIgnoreCase)
    {
        "color", "background", "background-color", "background-image", "background-position",
        "background-position-x", "background-position-y", "background-size", "background-repeat",
        "background-repeat-x", "background-repeat-y", "background-attachment", "background-origin",
        "border-color", "border-top-color", "border-right-color", "border-bottom-color", "border-left-color",
        "outline", "outline-color", "outline-style", "outline-width", "outline-offset",
        "text-decoration", "text-decoration-line", "text-decoration-color", "text-decoration-style",
        "cursor", "caret-color", "-webkit-text-fill-color", "-webkit-text-stroke-color", "-webkit-text-stroke-width",
    };

    [Test]
    [NonParallelizable]
    [CancelAfter(480_000)]
    public async Task HoverRules()
    {
        using var measurer = new SkiaTextMeasurer();
        using var view = new HutuViewInternal(measurer);
        view.SetViewportSize(Width, Height);

        var document = await view.LoadDocumentAsync(Url, CancellationToken.None);
        var sheets = SheetsOf(document);

        Line($"{document.QuerySelectorAll("*").Length} elements, " +
             $"{sheets.Sum(s => s.Rules.Length)} rules across {sheets.Count} sheets");

        var tracked = 0;
        var busters = 0;
        var busterProps = new Dictionary<String, Int32>(StringComparer.OrdinalIgnoreCase);
        var samples = new List<String>();

        foreach (var sheet in sheets)
        {
            Walk(sheet.Rules);
        }

        void Walk(ICssRuleList rules)
        {
            for (var i = 0; i < rules.Length; i++)
            {
                switch (rules[i])
                {
                    case ICssStyleRule style:
                        if (StylePseudoIndex.NamesATrackedPseudo(style.SelectorText ?? String.Empty))
                        {
                            tracked++;

                            var offList = style.Style
                                .Where(p => !PaintOnly.Contains(p.Name))
                                .Select(p => p.Name)
                                .Distinct()
                                .ToList();

                            if (offList.Count > 0)
                            {
                                busters++;

                                foreach (var name in offList)
                                {
                                    busterProps[name] = busterProps.TryGetValue(name, out var n) ? n + 1 : 1;
                                }

                                if (samples.Count < 12)
                                {
                                    samples.Add($"  {Truncate(style.SelectorText ?? "?", 70),-70} -> {String.Join(", ", offList)}");
                                }
                            }
                        }

                        Walk(style.Rules);
                        break;

                    case ICssGroupingRule group:
                        Walk(group.Rules);
                        break;
                }
            }
        }

        Line("");
        Line($"rules naming a tracked pseudo: {tracked}");
        Line(busters == 0
            ? "GATE OPEN: every tracked-pseudo rule is paint-only, hover stays on the fast path"
            : $"GATE CLOSED: {busters} of {tracked} tracked-pseudo rules declare a non-paint-only property -> full relayout per hover");

        Line("");
        Line("--- distinct non-paint-only properties used by hover/focus rules ---");

        foreach (var pair in busterProps.OrderByDescending(p => p.Value))
        {
            Line($"  {pair.Key,-26}{pair.Value,5}");
        }

        Line("");
        Line("--- sample hover/focus selectors that close the gate ---");

        foreach (var sample in samples)
        {
            Line(sample);
        }
    }

    [Test]
    [NonParallelizable]
    [CancelAfter(600_000)]
    public async Task GestureCost()
    {
        using var measurer = new SkiaTextMeasurer();
        using var view = new HutuViewInternal(measurer);
        view.SetViewportSize(Width, Height);
        view.MeasureBuildStages = true;

        var document = await view.LoadDocumentAsync(Url, CancellationToken.None);

        if (view.Layout is null)
        {
            Line("the engine laid nothing out");
            return;
        }

        Line($"{document.QuerySelectorAll("*").Length} elements, {view.Layout.Width:F0}x{view.Layout.Height:F0} document");

        Line("");
        Line("--- a vertical sweep of PointerMoved, each crossing into a new element ---");

        var restyled = 0;
        var times = new List<Double>();
        var fused = 0d;
        var laid = 0d;
        var painted = 0d;
        var fast = 0;
        var matching = 0d;
        var cascading = 0d;
        var walked = 0;
        var computed = 0;
        var held = 0;
        var fresh = 0;
        var opened = 0;
        var reused = 0;

        for (var y = 40; y < Math.Min(Height, view.Layout.Height); y += 36)
        {
            var stopwatch = Stopwatch.StartNew();
            var moved = view.PointerMoved(Width / 2, y);
            stopwatch.Stop();

            times.Add(stopwatch.Elapsed.TotalMilliseconds);

            if (moved.Changed)
            {
                restyled++;

                if (view.LastBuildStages is { } stages)
                {
                    fused += stages.Fused;
                    laid += stages.Layout;
                    painted += stages.Paint;
                    fast += stages.Fast ? 1 : 0;
                }

                if (view.LastStyleStages is { } styling)
                {
                    matching += styling.Matching;
                    cascading += styling.Cascading;
                    walked += styling.Walked;
                    computed += styling.Computed;
                    held += styling.HeldMatches;
                    fresh += styling.FreshMatches;
                }

                if (view.LastScopeLinks is { } scopes)
                {
                    opened += scopes.Opened;
                    reused += scopes.Reused;
                }
            }
        }

        Line($"  {times.Count} moves, {restyled} restyled");
        Line($"  per-move  mean {times.Average():F1}ms   max {times.Max():F1}ms   p95 {Percentile(times, 95):F1}ms");

        Line("");
        Line("--- where a restyle's time goes, summed over the sweep ---");
        Line($"  restyles measured {restyled}, of which on the fast path {fast}");

        if (restyled > 0)
        {
            Line($"  per restyle  style build {fused / restyled,6:F1}ms   layout {laid / restyled,6:F1}ms   paint {painted / restyled,6:F1}ms");
            Line($"  in total     style build {fused,7:F0}ms   layout {laid,7:F0}ms   paint {painted,7:F0}ms");

            Line("");
            Line("--- inside the style build: matching against computing ---");
            Line($"  per restyle  matching {matching / restyled,6:F1}ms   cascade+compute {cascading / restyled,6:F1}ms");
            Line($"  per restyle  elements walked {walked / restyled,6:F0}   styles computed {computed / restyled,6:F0}" +
                 $"   held matches {held / restyled,6:F0}   fresh matches {fresh / restyled,6:F0}");
            Line($"  per restyle  scope links opened {opened / restyled,6:F0}   reused {reused / restyled,6:F0}");
        }

        Line("");
        Line("--- six moves between two links, each changing what is hovered ---");

        var links = document.QuerySelectorAll("a[href]").OfType<IElement>().Take(2).ToList();

        if (links.Count == 2 && view.Layout.FindBox(links[0]) is { } first && view.Layout.FindBox(links[1]) is { } second)
        {
            for (var round = 0; round < 6; round++)
            {
                var box = round % 2 == 0 ? first : second;
                var stopwatch = Stopwatch.StartNew();
                var moved = view.PointerMoved(box.BorderBox.X + 2, box.BorderBox.Y + 2);
                stopwatch.Stop();

                var stages = view.LastBuildStages;
                Line($"  move {round}  {stopwatch.Elapsed.TotalMilliseconds,7:F1}ms  restyled={moved.Changed}" +
                     (stages is { } s
                         ? $"  style {s.Fused:F1}  layout {s.Layout:F1}  paint {s.Paint:F1}  fast={s.Fast}"
                         : String.Empty));
            }
        }
        else
        {
            Line("  the page did not offer two links to move between");
        }

        view.PointerExited();
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

    private static Double Percentile(List<Double> values, Int32 percentile)
    {
        if (values.Count == 0)
        {
            return 0;
        }

        var ordered = values.OrderBy(v => v).ToArray();
        var index = (Int32)Math.Ceiling(percentile / 100.0 * ordered.Length) - 1;
        return ordered[Math.Max(0, Math.Min(index, ordered.Length - 1))];
    }

    private static String Truncate(String text, Int32 room) =>
        text.Length <= room ? text : text[..(room - 1)] + "…";

    private static void Line(String text) => TestContext.Progress.WriteLine(text);
}

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Hutu.Avalonia;
using Hutu.Rendering.Interaction;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;

namespace Hutu.Avalonia.Tests;

/// <summary>
/// Follows a link on <c>https://www.qq.com/</c> the way the sample app does - a real pointer gesture
/// into a real window, the host's navigation handler attached, and the dispatcher left to render -
/// and prints how long every step took. The core probe measures the engine; this one measures what the
/// reader actually waits on, because the gestures go through the surface and the frames they ask for.
/// </summary>
/// <remarks>
/// Explicit and network-dependent. Nothing here asserts a number: a stage that never printed its line
/// is the stage that did not come back, which is the whole point of the run.
/// <c>dotnet test src/Hutu.Avalonia.Tests/Hutu.Avalonia.Tests.csproj --filter "FullyQualifiedName~HutuViewQqClickTests"</c>
/// </remarks>
[TestFixture]
[Explicit]
public sealed class HutuViewQqClickTests
{
    private const string Url = "https://www.qq.com/";

    private const double WindowWidth = 1222;
    private const double WindowHeight = 900;

    [AvaloniaTest]
    [NonParallelizable]
    [CancelAfter(600_000)]
    public async Task ClickALinkOnThePortal()
    {
        var window = new Window { Width = WindowWidth, Height = WindowHeight };

        try
        {
            var view = new HutuView();
            window.Content = view;
            window.Show();
            Step("window shown", TimeSpan.Zero);

            var surface = view.GetVisualDescendants().OfType<HutuSurface>().First();

            var requested = new List<LinkTarget>();

            // The host's own handler, as the sample writes it: an async void that awaits the load.
            view.NavigationRequested += link =>
            {
                requested.Add(link);
                Step($"NavigationRequested ({link.Url})", TimeSpan.Zero);
                _ = LoadAsync(view, link.Url);
            };

            async Task LoadAsync(HutuView target, string url)
            {
                var watch = Stopwatch.StartNew();

                try
                {
                    await target.LoadUrlAsync(url);
                    Step($"host awaited the load of {Shorten(url)}", watch.Elapsed);
                }
                catch (Exception exception)
                {
                    Step($"host load FAILED: {exception.GetType().Name} {exception.Message}", watch.Elapsed);

                    foreach (var line in (exception.ToString() ?? String.Empty).Split('\n').Take(14))
                    {
                        TestContext.Progress.WriteLine($"        {line.TrimEnd()}");
                    }
                }
            }

            var loaded = Stopwatch.StartNew();
            await view.LoadUrlAsync(Url);
            loaded.Stop();
            Step($"LoadUrlAsync({Shorten(Url)}) returned", loaded.Elapsed);

            var settled = Stopwatch.StartNew();
            Settle(window);
            settled.Stop();
            Step("settle after the load (layout + dispatcher + render)", settled.Elapsed);

            settled.Restart();
            var first = window.CaptureRenderedFrame();
            settled.Stop();
            Step($"CaptureRenderedFrame: {(first is null ? "no frame" : $"{first.PixelSize.Width}x{first.PixelSize.Height}")}", settled.Elapsed);
            first?.Dispose();

            // A point inside the channel bar's first link, in the window's own coordinates: the page
            // begins at the top of the surface, so document and viewport agree here.
            var at = new Point(50, 112);

            var hover = Stopwatch.StartNew();
            window.MouseMove(at, RawInputModifiers.None);
            hover.Stop();
            Step("MouseMove", hover.Elapsed);

            hover.Restart();
            Settle(window);
            hover.Stop();
            Step("settle after the hover (the rebuild it asked for, and the frame)", hover.Elapsed);

            var press = Stopwatch.StartNew();
            window.MouseDown(at, MouseButton.Left, RawInputModifiers.LeftMouseButton);
            press.Stop();
            Step("MouseDown", press.Elapsed);

            press.Restart();
            Settle(window);
            press.Stop();
            Step("settle after the press", press.Elapsed);

            var release = Stopwatch.StartNew();
            window.MouseUp(at, MouseButton.Left, RawInputModifiers.None);
            release.Stop();
            Step("MouseUp (this is where the navigation is asked for)", release.Elapsed);

            release.Restart();
            Settle(window);
            release.Stop();
            Step("settle after the release", release.Elapsed);

            // The load the host started runs on the dispatcher; drain it the way a live app would.
            var drain = Stopwatch.StartNew();

            for (var round = 0; round < 40 && requested.Count == 0; round++)
            {
                Dispatcher.UIThread.RunJobs();
                await Task.Delay(250);
            }

            drain.Stop();
            Step($"requests seen: {requested.Count} in {drain.Elapsed.TotalSeconds:F1}s", drain.Elapsed);

            // Give the navigated document a chance to arrive and be drawn, then report where it stands.
            for (var second = 0; second < 20; second++)
            {
                var before = view.GetHtml().Length;
                Settle(window);
                await Task.Delay(500);
                Settle(window);

                if (before != view.GetHtml().Length)
                {
                    Step($"document changed after {(second + 1) * 0.5:F1}s of waiting", TimeSpan.Zero);
                }
            }

            Step($"final extent {view.Extent.Width:F0}x{view.Extent.Height:F0}, viewport {view.Viewport.Width:F0}x{view.Viewport.Height:F0}", TimeSpan.Zero);

            var frame = Stopwatch.StartNew();
            var last = window.CaptureRenderedFrame();
            frame.Stop();
            Step($"CaptureRenderedFrame at the end: {(last is null ? "no frame" : $"{last.PixelSize.Width}x{last.PixelSize.Height}")}", frame.Elapsed);
            last?.Dispose();

            Assert.That(surface, Is.Not.Null, "the surface was found");
        }
        finally
        {
            window.Close();
        }
    }

    private static string Shorten(string url) =>
        url.Length <= 46 ? url : url[..43] + "...";

    private static void Step(string what, TimeSpan took) =>
        TestContext.Progress.WriteLine($"  {DateTime.Now:HH:mm:ss.fff}  {took.TotalMilliseconds,9:F0}ms  {what}");

    private static void Settle(Window window)
    {
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }
}

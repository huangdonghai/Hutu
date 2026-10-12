using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Hutu.Avalonia;
using NUnit.Framework;
using System;
using System.IO;
using System.Threading.Tasks;

namespace Hutu.Avalonia.Tests;

/// <summary>
/// Follows a link the way the sample app does - a press that begins a selection, a release that asks
/// for a navigation, and a host that loads what was named - and checks the navigation arrives.
/// </summary>
/// <remarks>
/// The shape matters. A load ends by building the document, and a build raises what the host listens
/// to; the host's answer is to invalidate a visual, which the platform only lets its own thread do.
/// When the load gave up the caller's context on the way to the network, that invalidation ran on a
/// thread pool thread and threw - inside an <c>async void</c> handler, where nothing can catch it - so
/// the reader was left with a click that did nothing. The selection is what makes the build raise the
/// event at all, which is why the gesture is a real press rather than a call.
/// </remarks>
[TestFixture]
public sealed class HutuViewFollowLinkTests
{
    private const double WindowWidth = 800;
    private const double WindowHeight = 600;

    /// <summary>Where the link's own text sits, in a document set at 40px.</summary>
    private static readonly Point OnLink = new(20, 20);

    [AvaloniaTest]
    [NonParallelizable]
    public async Task FollowingALinkAfterAPressThatSelectedTextCompletes()
    {
        var directory = Path.Combine(Path.GetTempPath(), "hutu-follow-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(directory);

        var from = Path.Combine(directory, "page-a.htm");
        var to = Path.Combine(directory, "page-b.htm");

        File.WriteAllText(from,
            "<html><body style='margin:0'><div style='font-size:40px'>" +
            "<a href='page-b.htm'>go to the other page</a></div></body></html>");
        File.WriteAllText(to,
            "<html><body style='margin:0'><div style='font-size:40px'>THE OTHER PAGE</div></body></html>");

        var window = new Window { Width = WindowWidth, Height = WindowHeight };

        try
        {
            var view = new HutuView();
            window.Content = view;
            window.Show();
            Settle(window);

            await view.LoadUrlAsync(new Uri(from).AbsoluteUri);
            Settle(window);

            Assert.That(view.GetHtml(), Does.Contain("go to the other page"), "the first page is on screen");

            Exception? failure = null;
            var arrived = new TaskCompletionSource();

            view.NavigationRequested += link =>
            {
                RunAsync(link.Url);

                async void RunAsync(string url)
                {
                    try
                    {
                        await view.LoadUrlAsync(url);
                    }
                    catch (Exception exception)
                    {
                        failure = exception;
                    }
                    finally
                    {
                        arrived.TrySetResult();
                    }
                }
            };

            // The press, on its own: it is what leaves a selection for the rebuild to drop, and the
            // dropping is what makes the build raise the event the host answers with an invalidation.
            window.MouseDown(OnLink, MouseButton.Left, RawInputModifiers.LeftMouseButton);
            Dispatcher.UIThread.RunJobs();

            Assert.That(view.Selection, Is.Not.Null, "the press began a selection, empty as it is");

            window.MouseUp(OnLink, MouseButton.Left, RawInputModifiers.None);
            Dispatcher.UIThread.RunJobs();

            var which = await Task.WhenAny(arrived.Task, Task.Delay(TimeSpan.FromSeconds(30)));

            Assert.That(which, Is.SameAs(arrived.Task), "the navigation did not finish within 30s");
            Assert.That(failure, Is.Null, $"the host's load threw: {failure?.GetType().Name} {failure?.Message}");
            Assert.That(view.GetHtml(), Does.Contain("THE OTHER PAGE"), "the link's document is what is loaded now");
        }
        finally
        {
            window.Close();
            TryDelete(directory);
        }
    }

    private static void TryDelete(string directory)
    {
        try
        {
            Directory.Delete(directory, true);
        }
        catch (IOException)
        {
            // A temp directory left behind is not worth failing a test over.
        }
    }

    private static void Settle(Window window)
    {
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }
}

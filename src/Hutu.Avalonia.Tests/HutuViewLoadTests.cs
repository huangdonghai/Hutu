using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Threading;
using Hutu.Avalonia;
using NUnit.Framework;
using System;
using System.IO;
using System.Threading.Tasks;

namespace Hutu.Avalonia.Tests;

/// <summary>
/// Covers loading a document by URL through the host, in a real window: the call has to be awaitable,
/// and the surface has to end up carrying the document the await handed back.
/// </summary>
[TestFixture]
public sealed class HutuViewLoadTests
{
    private const double WindowWidth = 800;
    private const double WindowHeight = 600;

    private static string FixturePath =>
        Path.Combine(AppContext.BaseDirectory, "Pages", "border-radius.htm");

    [AvaloniaTest]
    public async Task AnAwaitedLoadPutsTheDocumentIntoTheSurface()
    {
        Assert.That(File.Exists(FixturePath), Is.True, $"the fixture was not copied to {FixturePath}");

        var window = new Window { Width = WindowWidth, Height = WindowHeight };

        try
        {
            var view = new HutuView();
            window.Content = view;
            window.Show();

            // Arranged before the document is loaded. The viewport is then already settled, so
            // nothing but the load itself can invalidate the surface afterwards - which is what
            // makes the extent below evidence of the refresh rather than of the first pass.
            Settle(window);

            var document = await view.LoadUrlAsync(new Uri(FixturePath).AbsoluteUri);

            Assert.That(document, Is.Not.Null, "an awaited load has to hand the document back");
            Assert.That(view.GetHtml(), Is.Not.Empty, "the document reached the view");

            Settle(window);

            // The surface measures itself from the document, so a load that returned without
            // refreshing would leave the old - empty - extent in place and never scroll.
            Assert.That(
                view.Extent.Height,
                Is.GreaterThan(view.Viewport.Height),
                $"a document longer than the viewport has to produce a taller extent, not " +
                $"an extent of {view.Extent.Height} against a viewport of {view.Viewport.Height}");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// Runs layout and drains the dispatcher queue, so a read after a load is not one pass out of
    /// date.
    /// </summary>
    private static void Settle(Window window)
    {
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
    }
}

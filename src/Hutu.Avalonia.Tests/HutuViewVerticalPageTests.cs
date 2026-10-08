using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Threading;
using Hutu.Avalonia;
using NUnit.Framework;
using System.Threading.Tasks;

namespace Hutu.Avalonia.Tests;

/// <summary>
/// Covers the host side of a page that reads the other way: a document whose columns march leftward
/// is scrolled sideways, and it is opened where it begins rather than at the origin.
/// </summary>
[TestFixture]
public sealed class HutuViewVerticalPageTests
{
    private const double WindowWidth = 800;
    private const double WindowHeight = 600;

    /// <summary>A body of columns, long enough that the chapter is wider than the window.</summary>
    private static string VerticalPage =>
        "<html><body style='margin:0;writing-mode:vertical-rl'>" +
        "<p style='margin:0;font-size:16px'>" + new string('中', 4000) + "</p>" +
        "</body></html>";

    [AvaloniaTest]
    public async Task AVerticalDocumentIsAsWideAsItsColumns()
    {
        var window = new Window { Width = WindowWidth, Height = WindowHeight };

        try
        {
            var view = await ShowAsync(window, VerticalPage);

            Assert.That(
                view.Extent.Height,
                Is.EqualTo(view.Viewport.Height).Within(1),
                "the columns are cut to the viewport, so the document is no taller than it");

            Assert.That(
                view.Extent.Width,
                Is.GreaterThan(view.Viewport.Width + 100),
                $"four thousand characters do not fit into one column, and the columns they need do not fit beside the window: the extent came out {view.Extent.Width} " +
                $"against a viewport of {view.Viewport.Width}");
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaTest]
    public async Task AVerticalDocumentOpensAtItsBeginning()
    {
        var window = new Window { Width = WindowWidth, Height = WindowHeight };

        try
        {
            var view = await ShowAsync(window, VerticalPage);

            Assert.That(
                view.Offset.X,
                Is.GreaterThan(0),
                "the origin is the end of a page whose columns march leftward, so opening there would open on the last of them");

            Assert.That(
                view.Offset.X,
                Is.EqualTo(view.Extent.Width - view.Viewport.Width).Within(1),
                $"a page that reads from its right edge is opened at that edge, not at {view.Offset.X}");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>Shows a view with the document in it, arranged against the real viewport.</summary>
    private static async Task<HutuView> ShowAsync(Window window, string html)
    {
        var view = new HutuView();
        window.Content = view;
        window.Show();

        Settle(window);

        await view.SetHtml(html);

        Settle(window);

        return view;
    }

    private static void Settle(Window window)
    {
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
    }
}

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Threading;
using Hutu.Avalonia;
using Offset = Hutu.Rendering.Layout.Offset;
using NUnit.Framework;
using System.Threading.Tasks;

namespace Hutu.Avalonia.Tests;

/// <summary>
/// Covers the host side of a page that reads the other way: a document whose columns march leftward
/// is scrolled sideways, and the offset it is scrolled to counts from the corner it begins at - the
/// right one - rather than from the corner its boxes are in.
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
                Is.Zero,
                $"a scroll offset counts from the corner the document begins at, and this page begins at its right edge: the view opened at {view.Offset.X}");

            // The far end of the range is the other edge of the chapter, so a page that is scrolled
            // sideways has somewhere to go from where it was opened.
            view.ScrollTo(double.MaxValue, 0);

            Assert.That(
                view.Offset.X,
                Is.EqualTo(view.Extent.Width - view.Viewport.Width).Within(1),
                "scrolling all the way forward reaches the end of the page");
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaTest]
    public async Task AVerticalDocumentIsDrawnFromTheEdgeItBeginsAt()
    {
        var window = new Window { Width = WindowWidth, Height = WindowHeight };

        try
        {
            var view = await ShowAsync(window, VerticalPage);

            Assert.That(
                DrawnAt(view).X,
                Is.EqualTo(view.Extent.Width - view.Viewport.Width).Within(1),
                "the reader is at the beginning of the page, so it is drawn from its right edge");

            Assert.That(view.Offset.X, Is.Zero, "which is the offset zero, not a scrolled-to one");

            // A page of columns is laid out again whenever the window changes shape: how long a column
            // is decides how many of them there are, so the page is not even the same width after. The
            // reader has not moved through any of it, so the edge it is drawn from has to be the edge
            // it begins at, before and after.
            foreach (var resize in new Action[]
            {
                () => window.Width = WindowWidth - 100,
                () => window.Height = WindowHeight - 100,
            })
            {
                resize();
                Settle(window);

                Assert.That(
                    DrawnAt(view).X,
                    Is.EqualTo(view.Extent.Width - view.Viewport.Width).Within(1),
                    $"the page is anchored at the edge it begins at, whatever size it was laid out at (extent {view.Extent.Width}, viewport {view.Viewport.Width})");
            }
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>The point of the document the view is drawing from, which is the layout's frame.</summary>
    private static Offset DrawnAt(HutuView view) => ((HutuSurface)view.Content!).ScrollOffset;

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

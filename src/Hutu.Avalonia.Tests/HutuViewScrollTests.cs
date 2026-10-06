using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Hutu.Avalonia;
using NUnit.Framework;
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace Hutu.Avalonia.Tests;

/// <summary>
/// Covers the host side of scrolling: a document taller than the window has to reach the
/// <see cref="ScrollViewer"/> as scrollable extent, both for the scrollbar and for the wheel.
/// </summary>
[TestFixture]
public sealed class HutuViewScrollTests
{
    private const double WindowWidth = 800;
    private const double WindowHeight = 600;

    /// <summary>The header row above the view, so the viewport is a known amount shorter than the window.</summary>
    private const double HeaderHeight = 40;

    private static string FixturePath =>
        Path.Combine(AppContext.BaseDirectory, "Pages", "border-radius.htm");

    [AvaloniaTest]
    public async Task ALongDocumentBecomesScrollableExtent()
    {
        var window = new Window { Width = WindowWidth, Height = WindowHeight };

        try
        {
            var view = await ShowDocumentAsync(window);

            Settle(window);

            // Everything below is relative to these two, so they have to be real first: a view that
            // was never arranged reports the extent it was handed and no viewport at all.
            Assert.That(
                view.Viewport.Height,
                Is.GreaterThan(WindowHeight - HeaderHeight - 50),
                $"the view was arranged with {view.Viewport.Height}px of viewport in a {WindowHeight}px window");

            Assert.That(
                view.Extent.Height,
                Is.GreaterThan(view.Viewport.Height + 200),
                $"a document longer than the viewport has to produce a taller extent, not {view.Extent.Height} " +
                $"against a viewport of {view.Viewport.Height}");

            // The scrollbar is the visible end of the chain, and its range is fed from the extent.
            var bar = VerticalScrollBar(view);

            Assert.That(bar, Is.Not.Null, "the vertical scrollbar comes from the template and has to exist");
            Assert.That(bar!.IsVisible, Is.True, "a document that does not fit has to make the scrollbar visible");
            Assert.That(bar.Maximum, Is.GreaterThan(0), "the scrollbar range is the scrollable part of the extent");
            Assert.That(bar.ViewportSize, Is.EqualTo(view.Viewport.Height).Within(0.5));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaTest]
    public async Task ScrollingToTheBottomReachesTheEndOfTheDocument()
    {
        var window = new Window { Width = WindowWidth, Height = WindowHeight };

        try
        {
            var view = await ShowDocumentAsync(window);

            Settle(window);

            view.ScrollToBottom();

            Settle(window);

            // Progress is computed from the geometry Hutu produced rather than from the extent, so a
            // full report proves the offset arrived - it was not clamped away on the way in.
            Assert.That(
                view.VerticalScrollProgress,
                Is.EqualTo(1).Within(0.01),
                $"the view only reached {view.VerticalScrollProgress:P1} of the document");

            // An offset can only sit at the end of the extent the scroller itself believes in, so this
            // is where a stale extent - one still describing a shorter document - shows up as a
            // mismatch instead of hiding behind a clamp.
            Assert.That(
                view.Offset.Y,
                Is.EqualTo(view.Extent.Height - view.Viewport.Height).Within(1),
                $"offset {view.Offset.Y} does not match the extent {view.Extent.Height} " +
                $"minus the viewport {view.Viewport.Height}");
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaTest]
    public async Task TheWheelScrollsTheDocument()
    {
        var window = new Window { Width = WindowWidth, Height = WindowHeight };

        try
        {
            var view = await ShowDocumentAsync(window);

            Settle(window);

            Assert.That(view.Offset.Y, Is.Zero, "the document starts at the top");

            // Over the middle of the viewport, away from the scrollbar. The input helper drains the
            // dispatcher itself, so the offset has already been moved by the time it returns.
            window.MouseWheel(
                new Point(WindowWidth / 2, WindowHeight / 2),
                new Vector(0, -3),
                RawInputModifiers.None);

            Assert.That(view.Offset.Y, Is.GreaterThan(0), "a wheel-down over the document has to scroll it");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// Hosts the fixture the way the sample does - a header row above a view that takes the rest -
    /// and loads it.
    /// </summary>
    private static async Task<HutuView> ShowDocumentAsync(Window window)
    {
        Assert.That(File.Exists(FixturePath), Is.True, $"the fixture was not copied to {FixturePath}");

        var view = new HutuView();
        var grid = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };

        grid.Children.Add(new Border { Height = HeaderHeight, Background = Brushes.LightGray });
        Grid.SetRow(view, 1);
        grid.Children.Add(view);
        window.Content = grid;
        window.Show();

        // Arranged before the document is loaded, so the first pass runs against the real viewport
        // rather than against nothing.
        Settle(window);

        await view.SetHtml(await File.ReadAllTextAsync(FixturePath));

        return view;
    }

    /// <summary>
    /// Runs layout and drains the dispatcher queue.
    /// </summary>
    /// <remarks>
    /// Loading a document invalidates measure from inside the arrange pass, so reading the extent
    /// after a single <c>UpdateLayout()</c> can pick up a value that is one pass out of date. Draining
    /// here is what keeps a stale read from being mistaken for a defect.
    /// </remarks>
    private static void Settle(Window window)
    {
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
    }

    private static ScrollBar? VerticalScrollBar(ScrollViewer view)
    {
        return view.GetVisualDescendants()
            .OfType<ScrollBar>()
            .FirstOrDefault(bar => bar.Orientation == Orientation.Vertical);
    }
}

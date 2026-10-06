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
using System.Linq;
using System.Threading.Tasks;

namespace Hutu.Avalonia.Tests;

/// <summary>
/// Covers what the host does with a pointer that is only reading: the document is told what the
/// pointer is over, and answers with the shape to show and the hint to raise.
/// </summary>
/// <remarks>
/// The text is set large so a point lands well inside its line whatever glyph metrics the machine's
/// fonts produce - what is under test is the wiring between the pointer, the core and the platform,
/// not the exact geometry.
/// </remarks>
[TestFixture]
public sealed class HutuViewLinkTests
{
    private const double WindowWidth = 800;
    private const double WindowHeight = 600;

    /// <summary>A link that asks for the hand, names a hint, and sits over empty space.</summary>
    private const string Document =
        "<html><body style='margin:0'><style>" +
        "a { cursor: pointer }" +
        "</style>" +
        "<div style='font-size:40px'><a href='next.htm' title='next page'>hello world</a></div>" +
        "<div style='height:400px'></div>" +
        "</body></html>";

    /// <summary>A column of text, which is read with a pointer turned on its side.</summary>
    private const string VerticalDocument =
        "<html><body style='margin:0'>" +
        "<div style='writing-mode:vertical-rl;width:60px;height:100px;font-size:40px'>中</div>" +
        "</body></html>";

    /// <summary>A point inside the first line, near its left edge.</summary>
    private static readonly Point Start = new(5, 10);

    /// <summary>A point past the end of the text, so a drag that reaches it covers all of it.</summary>
    private static readonly Point End = new(700, 10);

    /// <summary>A point below the text, where there is nothing to point at.</summary>
    private static readonly Point Blank = new(5, 300);

    /// <summary>A point inside the only column of the vertical document.</summary>
    private static readonly Point InColumn = new(40, 20);

    [AvaloniaTest]
    public async Task HoveringALinkShowsTheHand()
    {
        var window = new Window { Width = WindowWidth, Height = WindowHeight };

        try
        {
            await ShowDocumentAsync(window, Document);
            var surface = Surface(window);

            Assert.That(surface.Cursor, Is.Null, "nothing is pointed at to begin with");

            Hover(window, Start);

            Assert.That(
                surface.Cursor?.ToString(),
                Is.EqualTo(nameof(StandardCursorType.Hand)),
                "the link asks for the hand, and the pointer has to reach it");
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaTest]
    public async Task HoveringTextSetDownAColumnShowsATurnedPointer()
    {
        var window = new Window { Width = WindowWidth, Height = WindowHeight };

        try
        {
            await ShowDocumentAsync(window, VerticalDocument);
            var surface = Surface(window);

            Hover(window, InColumn);

            Assert.That(surface.Cursor, Is.Not.Null, "a column of text is still text");

            // The turned I-beam is drawn rather than taken from the platform, so it is the one shape
            // that cannot be named by a standard cursor type - and what matters here is that the
            // upright one was not what the reader was given.
            Assert.That(
                surface.Cursor!.ToString(),
                Is.Not.EqualTo(nameof(StandardCursorType.Ibeam)),
                "a column is not read with the upright I-beam");
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaTest]
    public async Task HoveringAnElementRaisesItsHint()
    {
        var window = new Window { Width = WindowWidth, Height = WindowHeight };

        try
        {
            await ShowDocumentAsync(window, Document);
            var surface = Surface(window);

            Hover(window, Start);
            Assert.That(ToolTip.GetTip(surface), Is.EqualTo("next page"), "the link names a hint");

            Hover(window, Blank);
            Assert.That(ToolTip.GetTip(surface), Is.Null, "and it goes away with the pointer");
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaTest]
    public async Task DraggingFromALinkStillSelectsText()
    {
        var window = new Window { Width = WindowWidth, Height = WindowHeight };

        try
        {
            var view = await ShowDocumentAsync(window, Document);

            // The press marks what it went down on, which is a link here. A gesture that then drags is
            // still a selection, and the marks must not get in its way.
            window.MouseDown(Start, MouseButton.Left, RawInputModifiers.LeftMouseButton);
            window.MouseMove(End, RawInputModifiers.LeftMouseButton);
            window.MouseUp(End, MouseButton.Left, RawInputModifiers.None);
            Dispatcher.UIThread.RunJobs();

            Assert.That(view.SelectedText, Is.EqualTo("hello world"));
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>A link that has a long way to go before the anchor it names comes into view.</summary>
    private const string AnchoredDocument =
        "<html><body style='margin:0'>" +
        "<div style='font-size:40px'><a href='#bottom'>jump</a></div>" +
        "<div style='height:1200px'></div>" +
        "<div id='bottom'>bottom</div>" +
        "</body></html>";

    [AvaloniaTest]
    public async Task ClickingALinkAsksForTheAddress()
    {
        var window = new Window { Width = WindowWidth, Height = WindowHeight };

        try
        {
            var view = await ShowDocumentAsync(window, Document);

            var requested = new List<LinkTarget>();
            view.NavigationRequested += requested.Add;

            Click(window, Start);

            Assert.That(requested, Has.Count.EqualTo(1), "one click, one request");
            Assert.That(requested[0].Url, Is.Not.Null.And.EndsWith("next.htm"), "the address is resolved");
            Assert.That(requested[0].IsFragment, Is.False, "a link to another document is not a scroll");
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaTest]
    public async Task ClickingAnAnchorScrollsToIt()
    {
        var window = new Window { Width = WindowWidth, Height = WindowHeight };

        try
        {
            var view = await ShowDocumentAsync(window, AnchoredDocument);

            var requested = new List<LinkTarget>();
            view.NavigationRequested += requested.Add;

            Assert.That(view.Offset.Y, Is.EqualTo(0), "the document starts at the top");

            Click(window, Start);

            Assert.That(view.Offset.Y, Is.GreaterThan(0), "following an anchor brings it into view");
            Assert.That(requested, Is.Empty, "and there is nothing to load");
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaTest]
    public async Task DraggingToSelectALinkDoesNotFollowIt()
    {
        var window = new Window { Width = WindowWidth, Height = WindowHeight };

        try
        {
            var view = await ShowDocumentAsync(window, Document);

            var requested = new List<LinkTarget>();
            view.NavigationRequested += requested.Add;

            window.MouseDown(Start, MouseButton.Left, RawInputModifiers.LeftMouseButton);
            window.MouseMove(End, RawInputModifiers.LeftMouseButton);
            window.MouseUp(End, MouseButton.Left, RawInputModifiers.None);
            Dispatcher.UIThread.RunJobs();

            Assert.That(requested, Is.Empty, "selecting a link's text is not following it");
            Assert.That(view.SelectedText, Is.EqualTo("hello world"), "the drag was a selection after all");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>Clicks at a point: down and up in the same place, which is one click.</summary>
    private static void Click(Window window, Point point)
    {
        window.MouseDown(point, MouseButton.Left, RawInputModifiers.LeftMouseButton);
        window.MouseUp(point, MouseButton.Left, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>Moves the pointer with no button held, which is what a reader hovering does.</summary>
    private static void Hover(Window window, Point point)
    {
        window.MouseMove(point, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>The surface the view hosts, which is the element the pointer reports to.</summary>
    private static HutuSurface Surface(Window window) =>
        ((HutuView)window.Content!).GetVisualDescendants().OfType<HutuSurface>().First();

    /// <summary>Hosts the view the way the sample does - as the whole content - and loads the markup.</summary>
    private static async Task<HutuView> ShowDocumentAsync(Window window, string html)
    {
        var view = new HutuView();
        window.Content = view;
        window.Show();

        // Arranged before the document is loaded, so the first pass runs against the real viewport
        // rather than against nothing.
        Settle(window);

        await view.SetHtml(html);
        Settle(window);

        return view;
    }

    /// <summary>
    /// Runs layout and drains the dispatcher queue, so a read after a gesture is not one pass out of
    /// date.
    /// </summary>
    private static void Settle(Window window)
    {
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
    }
}

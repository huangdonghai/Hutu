using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Hutu.Avalonia;
using NUnit.Framework;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace Hutu.Avalonia.Tests;

/// <summary>
/// Covers selecting text through the host, end to end: a drag on the surface has to reach the core as
/// document coordinates, come back as a highlight, and be what Ctrl+C puts on the clipboard.
/// </summary>
/// <remarks>
/// The text is set large so that the point a gesture is aimed at lands well inside its line whatever
/// glyph metrics the machine's fonts produce - the exact geometry is not what is under test here, the
/// wiring between the pointer, the core and the clipboard is.
/// </remarks>
[TestFixture]
public sealed class HutuViewSelectionTests
{
    private const double WindowWidth = 800;
    private const double WindowHeight = 600;

    private const string Document =
        "<html><body style='margin:0'><div style='font-size:40px'>hello world</div></body></html>";

    private const string WholeDocument = "hello world";

    /// <summary>A point inside the first line, near its left edge.</summary>
    private static readonly Point Start = new(5, 10);

    /// <summary>A point past the end of the text, so a drag that reaches it covers all of it.</summary>
    private static readonly Point End = new(700, 10);

    /// <summary>
    /// The same text with empty space under it, so a point below the text is still inside the surface
    /// the pointer reports to - which is what lets a hover be aimed at nothing.
    /// </summary>
    private const string DocumentWithEmptySpace =
        "<html><body style='margin:0'><div style='font-size:40px'>hello world</div>" +
        "<div style='height:400px'></div></body></html>";

    /// <summary>A point below the text, where there is nothing to select.</summary>
    private static readonly Point Blank = new(5, 300);

    /// <summary>
    /// A page with a line at the top and a line a screen further down, so a gesture that lands on the
    /// lower one has had to be mapped through the scroll to get to it.
    /// </summary>
    private const string TwoLinesApart =
        "<html><body style='margin:0'>" +
        "<div style='font-size:40px'>BBBB</div>" +
        "<div style='height:1000px'></div>" +
        "<div style='font-size:40px'>AAAA</div>" +
        "</body></html>";

    [AvaloniaTest]
    public async Task DraggingOverTextSelectsIt()
    {
        var window = new Window { Width = WindowWidth, Height = WindowHeight };

        try
        {
            var view = await ShowDocumentAsync(window);

            Assert.That(view.HasSelection, Is.False, "nothing is selected before the reader asks for anything");

            Drag(window);

            Assert.That(view.HasSelection, Is.True);
            Assert.That(view.SelectedText, Is.EqualTo(WholeDocument));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaTest]
    public async Task CtrlCCopiesTheSelectionToTheClipboard()
    {
        var window = new Window { Width = WindowWidth, Height = WindowHeight };

        try
        {
            var view = await ShowDocumentAsync(window);

            Drag(window);

            // The press focused the surface, so the key reaches the view by bubbling up from it.
            window.KeyPress(Key.C, RawInputModifiers.Control, PhysicalKey.C, "c");

            var clipboard = TopLevel.GetTopLevel(view)!.Clipboard;
            Assert.That(clipboard, Is.Not.Null);

            Assert.That(
                await clipboard!.TryGetTextAsync(),
                Is.EqualTo(WholeDocument),
                "Ctrl+C has to put the selected text on the platform clipboard");
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaTest]
    public async Task APressWithoutADragClearsTheSelection()
    {
        var window = new Window { Width = WindowWidth, Height = WindowHeight };

        try
        {
            var view = await ShowDocumentAsync(window);

            Drag(window);
            Assert.That(view.HasSelection, Is.True);

            // A fresh press: inside the text's own box, but far enough from where the drag started
            // that it counts as a single click rather than a repeat of it.
            Click(window, new Point(400, 10));

            Assert.That(view.HasSelection, Is.False);
            Assert.That(view.SelectedText, Is.Empty);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaTest]
    public async Task ADoubleClickSelectsTheWordUnderThePointer()
    {
        var window = new Window { Width = WindowWidth, Height = WindowHeight };

        try
        {
            var view = await ShowDocumentAsync(window);

            // Two presses at the same point inside "hello", which is what the input manager counts as
            // a double click.
            Click(window, new Point(20, 10));
            Click(window, new Point(20, 10));

            Assert.That(view.SelectedText, Is.EqualTo("hello"));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaTest]
    public async Task AClickLandsOnTheTextUnderItWhereThePageIsScrolled()
    {
        var window = new Window { Width = WindowWidth, Height = WindowHeight };

        try
        {
            var view = await ShowDocumentAsync(window, TwoLinesApart);

            view.ScrollToBottom();
            Settle(window);

            // The second line is the bottom of the document, and scrolling to the bottom has put the
            // bottom of the document at the bottom of the viewport - so the point is in the middle of
            // that line. A host that took the pointer's position in the control's own coordinates and
            // added the scroll to it would be asking about a place that is not on the page at all, and
            // would come back with nothing selected.
            var line = new Point(20, view.Viewport.Height - 20);

            Click(window, line);
            Click(window, line);

            Assert.That(
                view.SelectedText,
                Is.EqualTo("AAAA"),
                "the text a pointer is over is the text that is under it, scrolled or not");
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaTest]
    public async Task CopyingWithoutASelectionReportsThatItCopiedNothing()
    {
        var window = new Window { Width = WindowWidth, Height = WindowHeight };

        try
        {
            var view = await ShowDocumentAsync(window);

            Assert.That(await view.CopyAsync(), Is.False);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaTest]
    public async Task ClearingTheSelectionThroughTheApiRemovesIt()
    {
        var window = new Window { Width = WindowWidth, Height = WindowHeight };

        try
        {
            var view = await ShowDocumentAsync(window);

            view.SelectAll();
            Assert.That(view.SelectedText, Is.EqualTo(WholeDocument));

            view.ClearSelection();

            Assert.That(view.Selection, Is.Null);
            Assert.That(view.SelectedText, Is.Empty);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaTest]
    public async Task MovingOverTextShowsTheTextCursor()
    {
        var window = new Window { Width = WindowWidth, Height = WindowHeight };

        try
        {
            await ShowDocumentAsync(window, DocumentWithEmptySpace);
            var surface = Surface(window);

            Hover(window, Blank);
            Assert.That(surface.Cursor, Is.Null, "empty space is the default cursor");

            Hover(window, Start);
            Assert.That(
                surface.Cursor?.ToString(),
                Is.EqualTo(nameof(StandardCursorType.Ibeam)),
                "text under the pointer has to show the text cursor");
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaTest]
    public async Task MovingOffTheTextRestoresTheDefaultCursor()
    {
        var window = new Window { Width = WindowWidth, Height = WindowHeight };

        try
        {
            await ShowDocumentAsync(window, DocumentWithEmptySpace);
            var surface = Surface(window);

            Hover(window, Start);
            Assert.That(surface.Cursor, Is.Not.Null);

            Hover(window, Blank);
            Assert.That(surface.Cursor, Is.Null, "the cursor has to go back once there is nothing to select");
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaTest]
    public async Task ADragKeepsTheTextCursorPastTheEndOfTheText()
    {
        var window = new Window { Width = WindowWidth, Height = WindowHeight };

        try
        {
            await ShowDocumentAsync(window, DocumentWithEmptySpace);
            var surface = Surface(window);

            window.MouseDown(Start, MouseButton.Left, RawInputModifiers.LeftMouseButton);
            window.MouseMove(Blank, RawInputModifiers.LeftMouseButton);

            // The gesture is still a text selection with the pointer well past the text, so the cursor
            // stays the text one rather than flickering back on the way.
            Assert.That(
                surface.Cursor?.ToString(),
                Is.EqualTo(nameof(StandardCursorType.Ibeam)),
                "a drag is a selection wherever the pointer wanders");

            window.MouseUp(Blank, MouseButton.Left, RawInputModifiers.None);
            Dispatcher.UIThread.RunJobs();

            // With the button up the gesture is over, so the cursor answers for the point again - which
            // is empty space here.
            Assert.That(surface.Cursor, Is.Null, "the release ends the drag, so the cursor stops being kept");
        }
        finally
        {
            window.Close();
        }
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

    /// <summary>Presses at the start of the text, drags past its end and releases.</summary>
    private static void Drag(Window window)
    {
        window.MouseDown(Start, MouseButton.Left, RawInputModifiers.LeftMouseButton);
        window.MouseMove(End, RawInputModifiers.LeftMouseButton);
        window.MouseUp(End, MouseButton.Left, RawInputModifiers.None);
    }

    /// <summary>Presses and releases without moving, which is a click.</summary>
    private static void Click(Window window, Point point)
    {
        window.MouseDown(point, MouseButton.Left, RawInputModifiers.LeftMouseButton);
        window.MouseUp(point, MouseButton.Left, RawInputModifiers.None);
    }

    /// <summary>Hosts the view the way the sample does - as the whole content - and loads the text.</summary>
    private static Task<HutuView> ShowDocumentAsync(Window window) => ShowDocumentAsync(window, Document);

    /// <summary>Hosts the view and loads the supplied markup.</summary>
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

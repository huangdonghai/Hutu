using AngleSharp.Dom;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Hutu.Rendering.Interaction;
using Hutu.Skia;
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Offset = Hutu.Rendering.Layout.Offset;
using ScrollAlignment = Hutu.Rendering.Layout.ScrollAlignment;
using TextSelection = Hutu.Rendering.Layout.TextSelection;

namespace Hutu.Avalonia;

/// <summary>
/// A scrollable view that renders a document with Hutu.
/// </summary>
/// <remarks>
/// The view owns the whole rendering stack: the Skia text measurer and paint executor, the core
/// renderer, and the self-drawing surface it hosts. It is also where the viewport is reported back
/// into the renderer, because both media queries and relative lengths depend on it - which is why a
/// resize has to rerun the pipeline rather than just repaint.
/// </remarks>
public class HutuView : ScrollViewer
{
    protected override Type StyleKeyOverride => typeof(ScrollViewer);

    private readonly SkiaTextMeasurer _measurer = new();
    private readonly SkiaImageLoader _images = new();
    private readonly HutuViewInternal _internalView;
    private readonly HutuSurface _surface;

    /// <summary>
    /// The addresses this view has been to. It lives here rather than in the renderer because it is
    /// browsing history: it outlives any one document, and another host would keep it somewhere else.
    /// </summary>
    private readonly HashSet<String> _visited = new(StringComparer.Ordinal);

    public HutuView()
    {
        // The same loader decodes the images for layout and draws them, so a run is never measured
        // against one decode and painted from another.
        _internalView = new HutuViewInternal(_measurer, _images);

        // The renderer asks where the reader has been; the answer is this view's to give, and a view
        // that was never told anything answers that nothing has been read.
        _internalView.IsVisited = _visited.Contains;

        // The surface reports a gesture; what a click on a link comes to is decided here, where the
        // scrolling that following an anchor asks for lives.
        _surface = new HutuSurface(_internalView, new SkiaPaintExecutor(_measurer, _images), Follow);
        Content = _surface;

        // A page is as wide as its content, which is not always as wide as the window: a vertical
        // document's columns march sideways, and a wide table or a picture reaches past the edge
        // whichever way it reads. A scroller that refuses to move sideways would leave that part of
        // the document with nowhere to be reached from, so the extent is allowed to be the
        // document's and the bar appears only when it is wider than the viewport.
        HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;

        // The core owns the selection and the highlight is already part of the paint list, so a
        // change leaves the host nothing to do but draw again - which also covers a selection made
        // through the API rather than by pointing at the text.
        _internalView.SelectionChanged += _surface.InvalidateVisual;
    }

    public void AddEpubResourceSource(string epubFileName)
    {
        _internalView.AddEpubResourceSource(epubFileName);
    }

    public void AddEmbeddedResourceSource(Assembly assembly, string? folder = null)
    {
        _internalView.AddEmbeddedResourceSource(assembly, folder);
    }

    /// <summary>Loads a document from a URL and repaints once it is in place.</summary>
    /// <remarks>
    /// The task is handed back rather than discarded: a load can fail, and an <c>async void</c> would
    /// put that failure on the dispatcher - where nothing can catch it - instead of on the caller.
    /// Awaiting it is also what lets a caller act on the loaded document, which is impossible while
    /// there is no completion signal to wait for.
    /// <para>
    /// The await deliberately keeps the default <c>ConfigureAwait</c>. The continuation below touches
    /// the surface, so it has to come back to the caller's context - the UI thread - rather than
    /// resume on the pool; adding <c>ConfigureAwait(false)</c> here would hand the invalidation to a
    /// thread that does not own the control.
    /// </para>
    /// </remarks>
    public async Task<IDocument> LoadUrlAsync(
        string url,
        CancellationToken cancellationToken = default)
    {
        var document = await _internalView.LoadUrlAsync(url, cancellationToken);
        _surface.Refresh();
        OpenAtTheStart();
        return document;
    }

    /// <summary>Loads a document from a string of HTML and repaints once it is in place.</summary>
    /// <remarks>
    /// The markup is parsed as it is given: <paramref name="url"/> is never requested, it only sets
    /// the document's base address, which is what relative style sheets and images are resolved
    /// against. A document that refers to neither can be loaded without one.
    /// <para>
    /// As with <see cref="LoadUrlAsync"/>, the await deliberately keeps the default
    /// <c>ConfigureAwait</c>: the continuation below touches the surface, so it has to come back to
    /// the caller's context - the UI thread - rather than resume on the pool.
    /// </para>
    /// </remarks>
    public async Task<IDocument> SetHtml(string html, string url = "")
    {
        var document = await _internalView.SetHtml(html, url);
        _surface.Refresh();
        OpenAtTheStart();
        return document;
    }

    /// <summary>
    /// Puts the viewport on the corner the document begins at.
    /// </summary>
    /// <remarks>
    /// Every offset a reader moves in is counted from that corner, so it is the origin whichever way
    /// the document reads - a vertical page's beginning is at the far end of the layout's x, which is
    /// what <see cref="InTheFrame"/> turns zero into. Loading a document therefore has to say where it
    /// is rather than leave it where the last one was scrolled to, since the scroller keeps its offset
    /// across a change of extent.
    /// </remarks>
    private void OpenAtTheStart()
    {
        ScrollTo(0, 0);

        // A scroller already at the beginning has nothing to report, and the surface has to be told
        // anyway: where the beginning falls in the layout's frame is a fact about the document that
        // has just been replaced, not about the offset.
        ApplyScrollOffset();
    }

    /// <summary>
    /// Tells the surface where in the document it is drawing.
    /// </summary>
    /// <remarks>
    /// It has to be said whenever the answer can change, which is not only when the reader scrolls: a
    /// document laid out again at another size moves the frame under a reader who has not moved, and
    /// a scroller whose own offset is unchanged reports nothing.
    /// </remarks>
    private void ApplyScrollOffset() => _surface.SetScrollOffset(InTheFrame(new Offset(Offset.X, Offset.Y)));

    /// <summary>
    /// Gets the offset the layout is drawn at for a reader who is at the given place in the document.
    /// </summary>
    /// <remarks>
    /// The two frames differ on any axis the page reads against, and which those are is a fact about
    /// the writing mode, so the core works it out. Everything below the surface - what the painter is
    /// handed, what a pointer is measured against - is in the layout's frame, so this is where the
    /// one crosses to the other.
    /// </remarks>
    private Offset InTheFrame(Offset scroll)
    {
        var layout = _internalView.Layout;

        if (layout is null)
        {
            return scroll;
        }

        // The place a reader is at is somewhere in the document, and one the scroller has not caught
        // up with yet is still nearer one end of it than past it: a document that has just been laid
        // out at another size has another range, and the scrollbar catches up a pass later. What the
        // surface is handed has to be a point of the document either way.
        var range = ScrollableRange();
        var place = new Offset(
            Math.Clamp(scroll.X, 0, range.X),
            Math.Clamp(scroll.Y, 0, range.Y));

        return layout.FrameOffset(place, _internalView.ViewportWidth, _internalView.ViewportHeight);
    }

    public string GetHtml()
    {
        return _internalView.GetHtml();
    }

    /// <summary>Gets the selected text, with one line break for each line the selection spans.</summary>
    public string SelectedText => _internalView.SelectedText;

    /// <summary>Gets whether a stretch of text is currently selected.</summary>
    public bool HasSelection => _internalView.HasSelection;

    /// <summary>
    /// Gets the selection as the two positions it was made of, or null when nothing is selected. The
    /// positions belong to the current layout and are dropped when it is replaced.
    /// </summary>
    public TextSelection? Selection => _internalView.Selection;

    /// <summary>Raised whenever the selection changes, including when a relayout drops it.</summary>
    public event Action? SelectionChanged
    {
        add => _internalView.SelectionChanged += value;
        remove => _internalView.SelectionChanged -= value;
    }

    /// <summary>
    /// Raised when a link is followed to a document other than the one it is in.
    /// </summary>
    /// <remarks>
    /// The address, the context it asks for and whether it asks to be saved all travel with the
    /// request; loading it is left to whoever listens, because whether there is anywhere to load it
    /// into is the host's to know. A link into this document is not a navigation - it is a scroll, and
    /// it has already happened by the time this would have been raised.
    /// </remarks>
    public event Action<LinkTarget>? NavigationRequested
    {
        add => _internalView.NavigationRequested += value;
        remove => _internalView.NavigationRequested -= value;
    }

    /// <summary>
    /// Records that an address has been read, so that every link to it is painted as visited.
    /// </summary>
    /// <remarks>
    /// The address is the whole one - what a link resolves to rather than what its href says - because
    /// that is what is compared against. Recording an address that is already there changes nothing, so
    /// reloading a page is not a repaint of every link on it.
    /// </remarks>
    public void MarkVisited(string url)
    {
        if (string.IsNullOrWhiteSpace(url) || !_visited.Add(url))
        {
            return;
        }

        // Marking is only visible where the document has a link to the address, which is what the
        // renderer reports: a navigation elsewhere - the first load of a page - changes nothing.
        if (_internalView.RefreshVisited())
        {
            _surface.Refresh();
        }
    }

    /// <summary>Gets the addresses that have been read, which is what links are marked against.</summary>
    public IReadOnlyCollection<string> VisitedAddresses => _visited;

    /// <summary>Clears the selection, so nothing is highlighted and a copy has nothing to take.</summary>
    public void ClearSelection()
    {
        _internalView.ClearSelection();
    }

    /// <summary>Selects everything the document can select.</summary>
    public void SelectAll()
    {
        _internalView.SelectAll();
    }

    /// <summary>
    /// Copies the selected text to the system clipboard, reporting whether there was anything to
    /// copy.
    /// </summary>
    /// <remarks>
    /// The clipboard is reached through the top level, which is what knows the platform's own. A view
    /// that is not attached to one has none, and reporting that as "nothing was copied" is friendlier
    /// than throwing out of a key handler.
    /// </remarks>
    public async Task<bool> CopyAsync()
    {
        var text = _internalView.SelectedText;

        if (text.Length == 0 || TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard)
        {
            return false;
        }

        await clipboard.SetTextAsync(text);
        return true;
    }

    /// <summary>
    /// Scrolls to an absolute offset, clamped to the range the document can scroll through, so a
    /// document height or an out-of-range delta is safe to pass in.
    /// </summary>
    /// <remarks>
    /// The offset is where the reader is in the document: zero is the corner it begins at and the far
    /// end of the range is the end of it, whichever way it reads. That is <see cref="Offset"/>'s
    /// frame, and the one the layout is drawn in is <see cref="InTheFrame"/>'s.
    /// <para>
    /// The range comes from the geometry Hutu produced rather than from
    /// <see cref="ScrollViewer.Extent"/>, which is only ever as fresh as the last layout pass. A
    /// scroll issued straight after a load would otherwise be clamped against the extent of the
    /// previous document - or, on the first load, against nothing at all - and quietly be lost.
    /// </para>
    /// </remarks>
    public void ScrollTo(double x, double y)
    {
        // The pass is caught up on rather than deferred: the scroller clamps whatever offset it is
        // handed against the extent it knows about, so it has to know about the new document first.
        // An already arranged document needs nothing, which keeps repeated scrolling - a per-frame
        // animation, say - from forcing a pass it does not need.
        if (!IsMeasureValid || !IsArrangeValid)
        {
            UpdateLayout();
        }

        var range = ScrollableRange();

        Offset = new Vector(
            Math.Clamp(x, 0, range.X),
            Math.Clamp(y, 0, range.Y));
    }

    /// <summary>Scrolls to the top of the document, keeping the horizontal position.</summary>
    public void ScrollToTop()
    {
        ScrollTo(Offset.X, 0);
    }

    /// <summary>Scrolls to the end of the document, keeping the horizontal position.</summary>
    public void ScrollToBottom()
    {
        ScrollTo(Offset.X, _internalView.Layout?.Height ?? 0);
    }

    /// <summary>Scrolls by a delta, clamped the way <see cref="ScrollTo"/> clamps.</summary>
    public void ScrollBy(double dx, double dy)
    {
        ScrollTo(Offset.X + dx, Offset.Y + dy);
    }

    /// <summary>
    /// Brings an element into view and reports whether it had a box to scroll to. By default it
    /// moves the least amount needed, so an element that is already visible stays where it is.
    /// </summary>
    public bool ScrollToElement(
        IElement element,
        ScrollAlignment alignment = ScrollAlignment.Nearest)
    {
        ArgumentNullException.ThrowIfNull(element);

        // Asked in the layout's frame, because an element's box is a rectangle of the layout, and
        // answered in it too; the view scrolls in the reader's, so the answer is turned round before
        // it is assigned.
        var frame = InTheFrame(new Offset(Offset.X, Offset.Y));
        var target = _internalView.GetVerticalFrameOffset(element, alignment, frame.Y);

        if (target is null)
        {
            return false;
        }

        var place = _internalView.Layout!.ScrollPosition(
            new Offset(frame.X, target.Value),
            _internalView.ViewportWidth,
            _internalView.ViewportHeight);

        ScrollTo(place.X, place.Y);
        return true;
    }

    /// <summary>
    /// Scrolls to an anchor - an id, or the name of an anchor element - aligning it with the top of
    /// the viewport the way following a fragment link does. Reports whether the anchor named
    /// anything in the document.
    /// </summary>
    public bool ScrollToAnchor(
        string anchor,
        ScrollAlignment alignment = ScrollAlignment.Start)
    {
        var element = _internalView.FindAnchor(anchor);

        return element is not null && ScrollToElement(element, alignment);
    }

    /// <summary>
    /// Follows a link the reader clicked: an anchor in this document is scrolled to, and anything else
    /// is handed to whoever listens for a navigation.
    /// </summary>
    /// <remarks>
    /// Which of the two it is is the core's answer, because it is a question about the document; this
    /// only acts on it. Following an anchor marks it as the target as well, so the document is built
    /// again with the styles that rule selects - hence a refresh rather than a plain repaint.
    /// </remarks>
    private void Follow(LinkTarget link)
    {
        if (_internalView.Activate(link) is { } anchor)
        {
            ScrollToAnchor(anchor);
            _surface.Refresh();
        }
    }

    /// <summary>
    /// Gets how far through the document the viewport has moved, from 0 at the beginning to 1 at the
    /// end. A document that fits is reported as fully scrolled.
    /// </summary>
    public double VerticalScrollProgress
    {
        get
        {
            var scrollable = ScrollableRange().Y;

            return scrollable <= 0 ? 1 : Math.Clamp(Offset.Y / scrollable, 0, 1);
        }
    }

    /// <summary>Gets the distance the document can scroll, which is zero where it fits the viewport.</summary>
    private Vector ScrollableRange()
    {
        var layout = _internalView.Layout;

        return new Vector(
            Math.Max(0, (layout?.Width ?? 0) - _internalView.ViewportWidth),
            Math.Max(0, (layout?.Height ?? 0) - _internalView.ViewportHeight));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var arranged = base.ArrangeOverride(finalSize);

        // The viewport feeds the style computation, so a change means the whole pipeline - not just
        // the drawing - has to run again. SetViewportSize reports whether it actually changed, which
        // is what stops the invalidation from looping.
        if (_internalView.SetViewportSize(
                Math.Max(1, (int)Math.Floor(finalSize.Width)),
                Math.Max(1, (int)Math.Floor(finalSize.Height))))
        {
            _surface.Refresh();

            // A resize lays the document out again - a column is as long as the window is tall, so
            // the page is another size - which moves the frame under a reader who has not scrolled.
            ApplyScrollOffset();
        }

        return arranged;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        // Scrolling only moves the viewport-anchored content, so it repaints rather than relayouts.
        if (change.Property == OffsetProperty)
        {
            ApplyScrollOffset();
        }
    }

    /// <summary>
    /// Answers the one shortcut a selection has: Ctrl+C.
    /// </summary>
    /// <remarks>
    /// It is handled here rather than as a key binding because copying is asynchronous and a binding
    /// has nowhere to report whether it succeeded. The event bubbles up from the focused surface, so
    /// no key handling is needed down there.
    /// </remarks>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (!e.Handled && e.Key == Key.C && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            _ = CopyAsync();
            e.Handled = true;
        }

        base.OnKeyDown(e);
    }
}

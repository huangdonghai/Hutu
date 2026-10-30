using AngleSharp.Css;
using AngleSharp.Dom;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Layout;
using Hutu.Rendering;
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
    private readonly SkiaFontLoader _fonts;
    private readonly HutuViewInternal _internalView;
    private readonly HutuSurface _surface;

    /// <summary>The page as the reader sees it: the sheet the document is drawn on, with the edge and
    /// the shadow that tell it apart from the desk around it.</summary>
    private readonly Border _frame;

    /// <summary>
    /// The addresses this view has been to. It lives here rather than in the renderer because it is
    /// browsing history: it outlives any one document, and another host would keep it somewhere else.
    /// </summary>
    private readonly HashSet<String> _visited = new(StringComparer.Ordinal);

    /// <summary>
    /// The reader's place, read at the head of a measure pass before the scroller has updated its
    /// viewport and clamped its offset, and put back once the relayout a resize produced has been
    /// measured - which is what keeps a vertical page anchored by the edge it reads from.
    /// </summary>
    private Offset _place;

    /// <summary>
    /// The place above, kept over until the extent the relayout produces is reported: the restore
    /// that runs at once can be clamped by the extent the scroller still holds, and has to be made
    /// good once the new one arrives.
    /// </summary>
    private Offset? _reanchor;

    /// <summary>The smallest the page can be zoomed to.</summary>
    private const Double MinZoom = 0.1;

    /// <summary>The largest the page can be zoomed to.</summary>
    private const Double MaxZoom = 10;

    /// <summary>The current page zoom; 1 means no scaling. It is what the keyboard and wheel drive.</summary>
    public Double Zoom { get; private set; } = 1;

    /// <summary>Raised after the page zoom changes, so a host can show the new level.</summary>
    public event Action? ZoomChanged;

    /// <summary>The window the zoom keys are listened on, kept for the detach that unregisters them.</summary>
    private TopLevel? _attachedTopLevel;

    public HutuView()
    {
        // The same loader decodes the images for layout and draws them, so a run is never measured
        // against one decode and painted from another. The font loader shares the measurer's resolver,
        // so the faces a page links to are the very typefaces measurement and painting read.
        _fonts = new SkiaFontLoader(_measurer);
        _internalView = new HutuViewInternal(_measurer, _images, _fonts);

        // The renderer asks where the reader has been; the answer is this view's to give, and a view
        // that was never told anything answers that nothing has been read.
        _internalView.IsVisited = _visited.Contains;

        // The surface reports a gesture; what a click on a link comes to is decided here, where the
        // scrolling that following an anchor asks for lives.
        _surface = new HutuSurface(_internalView, new SkiaPaintExecutor(_measurer, _images), Follow);
        _frame = new Border { Child = _surface };
        Content = _frame;

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

        // The surface catches Ctrl + the wheel before the scroller can scroll with it, and hands the
        // factor here to be applied.
        _surface.ZoomWheel += factor => ChangeZoom(Zoom * factor);
    }

    public void AddEpubResourceSource(string epubFileName)
    {
        _internalView.AddEpubResourceSource(epubFileName);
    }

    public void AddEmbeddedResourceSource(Assembly assembly, string? folder = null)
    {
        _internalView.AddEmbeddedResourceSource(assembly, folder);
    }

    /// <summary>
    /// Gets or sets the agent a page is asked as. A page's server can choose what to send from this
    /// alone - MediaWiki answers an agent it does not recognise with its mobile skin, which is another
    /// document than the desktop one a reader looking at the same address in a browser is shown - so a
    /// host that wants to be asked for a particular shape of page says so here. Set it before a load.
    /// </summary>
    public string UserAgent
    {
        get => _internalView.UserAgent;
        set => _internalView.UserAgent = value;
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
    /// <para>
    /// The document is drawn before its pictures are asked for, and drawn again when they arrive. A
    /// portal's fifty-odd images are several seconds at any reasonable fetch rate, and the text they
    /// hang from needed a tenth of one of them: making a reader wait for the whole of it is how a
    /// page that is merely slow to fill looks as though nothing had happened at all.
    /// </para>
    /// <para>
    /// The faces the document links to are fetched before the pictures are, because a font moves every
    /// character where a picture only moves what sits below it - so the text is relaid out in the
    /// typeface it was meant to be read in before the images are allowed to shift it again.
    /// </para>
    /// </remarks>
    public async Task<IDocument> LoadUrlAsync(
        string url,
        CancellationToken cancellationToken = default)
    {
        var document = await _internalView.LoadDocumentAsync(url, cancellationToken);
        _surface.Refresh();
        OpenAtTheStart();

        // Fonts first: the text is relaid out in the faces it was meant to be read in before the
        // pictures are allowed to shift the boxes around it.
        await _internalView.PrefetchFontsAsync(cancellationToken);
        await _internalView.PrefetchImagesAsync(cancellationToken);
        RedrawWithThePictures();
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
        var document = await _internalView.SetHtmlDocumentAsync(html, url);
        _surface.Refresh();
        OpenAtTheStart();

        // Fonts first, for the same reason as LoadUrlAsync: the text is relaid out in its own faces
        // before the pictures move anything below it.
        await _internalView.PrefetchFontsAsync(CancellationToken.None);
        await _internalView.PrefetchImagesAsync(CancellationToken.None);
        RedrawWithThePictures();
        return document;
    }

    /// <summary>
    /// Paints the document again once its images have been fetched and the layout has been put together
    /// around them. The scroll offset is handed to the surface again rather than only the surface being
    /// told to draw: the boxes moved under a reader who had not scrolled, and where the drawn frame
    /// stands is a fact the scroller reports only when its own offset changes.
    /// </summary>
    private void RedrawWithThePictures()
    {
        _surface.Refresh();
        ApplyScrollOffset();
    }

    /// <summary>
    /// Puts the viewport on the corner the document begins at.
    /// </summary>
    /// <remarks>
    /// Every offset a reader moves in is counted from that corner, so it is the origin whichever way
    /// the document reads - a vertical page's beginning is at the far end of the layout's x, which is
    /// what <see cref="ScrollTo"/>'s mirror turns zero into. Loading a document therefore has to say
    /// where it is rather than leave it where the last one was scrolled to, since the scroller keeps
    /// its offset across a change of extent.
    /// </remarks>
    private void OpenAtTheStart()
    {
        _reanchor = null;

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
    private void ApplyScrollOffset()
    {
        var range = ScrollableRange();

        // The scroller's own offset is the frame offset - the point of the document the viewport's
        // top left corner stands at - because the presenter drags the document under itself from the
        // layout's left edge on every axis. It is counted in the surface's pixels, which are the
        // document's pixels scaled by the zoom, so it is scaled back on the way down: the painter
        // speaks in document pixels. The reader's frame, where zero is the corner the page
        // begins at, exists only at this view's API, and the mirror between the two is taken where
        // an offset crosses - in <see cref="ScrollTo"/>, and in <see cref="ToReader"/> here.
        _surface.SetScrollOffset(new Offset(
            Math.Clamp(Offset.X, 0, range.X) / Zoom,
            Math.Clamp(Offset.Y, 0, range.Y) / Zoom));
    }

    /// <summary>
    /// Gets the viewport the document is laid out at: the window divided by the zoom. The mirror
    /// between the reader's and the scroller's frames is taken in the document's pixels, so it is
    /// this viewport - not the window's - that the mirror asks for.
    /// </summary>
    private Double LayoutViewportWidth => _internalView.ViewportWidth / Zoom;

    /// <summary>The viewport height the document is laid out at, in document pixels.</summary>
    private Double LayoutViewportHeight => _internalView.ViewportHeight / Zoom;

    /// <summary>
    /// Gets where a reader is for where the scroller says the viewport stands: the two frames differ
    /// on any axis the page reads against, and the core works out which those are. The scroller's
    /// offset arrives in the surface's pixels and is scaled back to the document's before the mirror
    /// is taken, and the answer comes back in document pixels.
    /// </summary>
    private Offset ToReader(Offset presenter)
    {
        var layout = _internalView.Layout;
        var frame = new Offset(presenter.X / Zoom, presenter.Y / Zoom);

        return layout is null
            ? frame
            : layout.ScrollPosition(frame, LayoutViewportWidth, LayoutViewportHeight);
    }

    /// <summary>
    /// Puts the viewport back at a reader's place, the place being turned into the offset the
    /// scroller counts from the layout's left edge. The mirror answers in document pixels, so the
    /// result is scaled up into the scroller's before it is assigned.
    /// </summary>
    private void Reanchor(Offset reader)
    {
        if (_internalView.Layout is not { } layout)
        {
            return;
        }

        var presenter = layout.FrameOffset(reader, LayoutViewportWidth, LayoutViewportHeight);

        Offset = new Vector(presenter.X * Zoom, presenter.Y * Zoom);
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
    /// What is recorded is the document the address names, with anything after the hash set aside: a
    /// reader's history is a list of pages rather than of places within them, and that is the shape the
    /// engine asks a link's address in. Recording an address that is already there changes nothing, so
    /// reloading a page is not a repaint of every link on it.
    /// </remarks>
    public void MarkVisited(string url)
    {
        if (string.IsNullOrWhiteSpace(url) || !_visited.Add(url.Split('#')[0]))
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

        var range = ScrollableRangeDoc();
        var reader = new Offset(
            Math.Clamp(x, 0, range.X),
            Math.Clamp(y, 0, range.Y));

        // The scroller counts from the layout's left edge on every axis, so a reader's offset - zero
        // at the corner the page begins at - has to be mirrored before it is assigned, which is what
        // puts a vertical page's first column against the right edge at the start of the document.
        // The mirror works in document pixels, so the answer is scaled up into the scroller's.
        var presenter = _internalView.Layout is { } layout
            ? layout.FrameOffset(reader, LayoutViewportWidth, LayoutViewportHeight)
            : reader;

        Offset = new Vector(presenter.X * Zoom, presenter.Y * Zoom);
    }

    /// <summary>Scrolls to the top of the document, keeping the horizontal position.</summary>
    public void ScrollToTop()
    {
        ScrollTo(ToReader(new Offset(Offset.X, Offset.Y)).X, 0);
    }

    /// <summary>Scrolls to the end of the document, keeping the horizontal position.</summary>
    public void ScrollToBottom()
    {
        ScrollTo(ToReader(new Offset(Offset.X, Offset.Y)).X, _internalView.Layout?.Height ?? 0);
    }

    /// <summary>Scrolls by a delta, clamped the way <see cref="ScrollTo"/> clamps.</summary>
    public void ScrollBy(double dx, double dy)
    {
        var at = ToReader(new Offset(Offset.X, Offset.Y));

        ScrollTo(at.X + dx, at.Y + dy);
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

        // Asked in the layout's frame, because an element's box is a rectangle of the layout, and the
        // scroller's own offset already is one - scaled back into document pixels, as the frame
        // counts in those; the answer comes back in the reader's frame, which is what ScrollTo takes.
        var target = _internalView.GetVerticalFrameOffset(element, alignment, Offset.Y / Zoom);

        if (target is null)
        {
            return false;
        }

        var place = _internalView.Layout!.ScrollPosition(
            new Offset(Offset.X / Zoom, target.Value),
            LayoutViewportWidth,
            LayoutViewportHeight);

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
    /// Scales the page, keeping the document point under the viewport's top-left where it was so a zoom
    /// grows the content out from where the reader is looking rather than from the origin.
    /// </summary>
    /// <remarks>
    /// A zoom is a relayout, not a magnification: the core is told the new level first, which lays the
    /// document out again at the scaled viewport - the window divided by the zoom - the way a browser
    /// reflows a page its reader has zoomed. The surface is told second, because the extent it reports
    /// is the new document's size in the scroller's pixels.
    /// </remarks>
    private void ChangeZoom(Double next)
    {
        var clamped = Math.Clamp(next, MinZoom, MaxZoom);

        if (Math.Abs(clamped - Zoom) < 1e-6)
        {
            return;
        }

        // The scroller's offset is in the surface's pixels, which are the document's pixels scaled by the
        // current zoom. Hold the document point at the viewport's top-left across the change.
        // A paged document is laid out at its paper, so there is nothing to reflow: the zoom is a
        // display scale of the fixed sheet, and fitting the window is the same number.
        if (IsPaged)
        {
            Zoom = clamped;
            _surface.Zoom = clamped;
            ZoomChanged?.Invoke();
            return;
        }

        var docX = Offset.X / Zoom;
        var docY = Offset.Y / Zoom;

        _internalView.Zoom = clamped;
        _surface.Zoom = clamped;
        Zoom = clamped;

        // Caught up on rather than deferred, so the extent the new layout produces is known before the
        // offset is put back: the scroller clamps whatever it is handed against the extent it holds.
        _surface.InvalidateMeasure();
        UpdateLayout();

        Offset = new Vector(docX * clamped, docY * clamped);
        ApplyScrollOffset();
        ZoomChanged?.Invoke();
    }

    /// <summary>Zooms in one step, the way Ctrl++ does.</summary>
    public void ZoomIn() => ChangeZoom(Zoom + 0.1);

    /// <summary>Zooms out one step, the way Ctrl+- does.</summary>
    public void ZoomOut() => ChangeZoom(Zoom - 0.1);

    /// <summary>Resets the zoom to 100%, the way Ctrl+0 does.</summary>
    public void ZoomReset() => ChangeZoom(1);

    /// <summary>Gets whether the document is being shown as the pages it prints on.</summary>
    public Boolean IsPaged { get; private set; }

    /// <summary>Gets the paper a document that states no page box is printed on.</summary>
    private PageSize? _paper;


    /// <summary>Gets how many pages the document comes to, or zero when it is not paged.</summary>
    public Int32 PageCount => IsPaged ? _internalView.Pages?.Count ?? 0 : 0;

    /// <summary>Gets the page being shown, counting from one, or zero when it is not paged.</summary>
    public Int32 CurrentPage => IsPaged && _surface.PageIndex >= 0 ? _surface.PageIndex + 1 : 0;

    /// <summary>Gets the size of the page being shown, before the display scale; empty when not paged.</summary>
    public Size PageExtent => IsPaged ? _surface.PageExtent : default;

    /// <summary>
    /// States the paper a document that states no page box is printed on, and prints it on that
    /// paper again when it is already paged. A document with its own page box keeps it.
    /// </summary>
    public void SetPaper(PageSize size, Hutu.Rendering.Layout.Thickness margins = default)
    {
        _paper = size;

        if (!IsPaged)
        {
            return;
        }

        _internalView.SetDefaultPage(size, margins);
        _surface.ShowPage(0);
        FitToWindow();
        PagesChanged?.Invoke();
    }

    /// <summary>Raised when the page being shown, or the number of pages, changes.</summary>
    public event Action? PagesChanged;

    /// <summary>
    /// Shows the document as the pages it prints on, or as one scrolling page again. The paper is
    /// what a document that states no page box is printed on; a document that states one keeps it.
    /// </summary>
    public void SetPaged(Boolean paged, PageSize? paper = null, Hutu.Rendering.Layout.Thickness margins = default)
    {
        if (paged == IsPaged)
        {
            return;
        }

        IsPaged = paged;

        if (paged)
        {
            if (paper is { } size)
            {
                _paper = size;
            }

            if (_paper is { } stated)
            {
                _internalView.SetDefaultPage(stated, margins);
            }
        }

        _internalView.SetMediaType(paged ? DeviceCategory.Printer : DeviceCategory.Screen);

        // A sheet on a desk: the page is a white sheet with an edge and a shadow, centred on a grey
        // desk, and the whole document is the bare page again.
        if (paged)
        {
            Background = new SolidColorBrush(Color.Parse("#FF808080"));
            _frame.Background = Brushes.White;
            _frame.BorderBrush = new SolidColorBrush(Color.Parse("#FF303030"));
            _frame.BorderThickness = new Thickness(1);
            _frame.BoxShadow = BoxShadows.Parse("0 3 12 0 #66000000");
            _frame.Margin = new Thickness(16);
            _frame.HorizontalAlignment = global::Avalonia.Layout.HorizontalAlignment.Center;
            _frame.VerticalAlignment = global::Avalonia.Layout.VerticalAlignment.Top;
        }
        else
        {
            Background = null;
            _frame.Background = null;
            _frame.BorderBrush = null;
            _frame.BorderThickness = default;
            _frame.BoxShadow = default;
            _frame.Margin = default;
            _frame.HorizontalAlignment = global::Avalonia.Layout.HorizontalAlignment.Stretch;
            _frame.VerticalAlignment = global::Avalonia.Layout.VerticalAlignment.Stretch;
        }

        _surface.ShowPage(paged ? 0 : -1);
        PagesChanged?.Invoke();

        // The sheet opens fitted, which is where a reader wants to start looking at a printed page.
        if (paged)
        {
            FitToWindow();
        }
    }

    /// <summary>
    /// Scales the page so a whole sheet stands in the window - a small sheet is enlarged to fill it,
    /// a large one shrunk - and takes the screen view back to its natural size where paging is off.
    /// </summary>
    /// <remarks>
    /// A paged document is laid out at the paper it prints on, so the fit is a display scale of a
    /// fixed sheet - the document is not laid out again, which is what a zoom in the scrolling view
    /// does (CSS zoom reflows; fitting a sheet to a window does not).
    /// </remarks>
    public void FitToWindow()
    {
        if (!IsPaged)
        {
            ZoomReset();
            return;
        }

        var page = _surface.PageExtent;
        var viewport = Bounds.Size;

        if (page.Width <= 0 || page.Height <= 0 || viewport.Width <= 0 || viewport.Height <= 0)
        {
            return;
        }

        // The frame stands 16 pixels off the edge it is centred in and one pixel of border on the sheet.
        var room = new Size(viewport.Width - 34, viewport.Height - 34);
        var scale = Math.Clamp(Math.Min(room.Width / page.Width, room.Height / page.Height), MinZoom, MaxZoom);

        Zoom = scale;
        _surface.Zoom = scale;
        Offset = new Vector(0, 0);
        ZoomChanged?.Invoke();
    }

    /// <summary>Shows one page, counting from one, kept inside the pages the document came to.</summary>
    public void ShowPage(Int32 number)
    {
        var count = PageCount;

        if (!IsPaged || count == 0)
        {
            return;
        }

        _surface.ShowPage(Math.Clamp(number - 1, 0, count - 1));
        PagesChanged?.Invoke();
    }

    /// <summary>Shows the page after this one, where there is one.</summary>
    public void NextPage()
    {
        if (IsPaged && CurrentPage < PageCount)
        {
            ShowPage(CurrentPage + 1);
        }
    }

    /// <summary>Shows the page before this one, where there is one.</summary>
    public void PreviousPage()
    {
        if (IsPaged && CurrentPage > 1)
        {
            ShowPage(CurrentPage - 1);
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
            var scrollable = ScrollableRangeDoc().Y;

            return scrollable <= 0 ? 1 : Math.Clamp(ToReader(new Offset(0, Offset.Y)).Y / scrollable, 0, 1);
        }
    }

    /// <summary>Gets the distance the document can scroll, which is zero where it fits the viewport.</summary>
    /// <remarks>
    /// The scroller's offset is counted in the surface's pixels, which are the document's pixels
    /// scaled by the zoom, so the extent is the laid-out size times the zoom less the window.
    /// </remarks>
    private Vector ScrollableRange()
    {
        var document = ScrollableRangeDoc();

        return new Vector(document.X * Zoom, document.Y * Zoom);
    }

    /// <summary>
    /// Gets the distance the document can scroll in its own pixels, which is zero where it fits the
    /// viewport it was laid out at.
    /// </summary>
    private Vector ScrollableRangeDoc()
    {
        var layout = _internalView.Layout;

        return new Vector(
            Math.Max(0, (layout?.Width ?? 0) - LayoutViewportWidth),
            Math.Max(0, (layout?.Height ?? 0) - LayoutViewportHeight));
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        // Read before the base measure runs, because it is what updates the viewport and clamps the
        // offset - and a place clamped against a viewport the document has not caught up with is a
        // place lost, which the resize below would then anchor to.
        _place = ToReader(new Offset(Offset.X, Offset.Y));

        return base.MeasureOverride(availableSize);
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
            // A resize lays the document out again - a column is as long as the window is tall, so
            // the page is another size - which moves the frame under a reader who has not scrolled.
            // The place is put back in the reader's frame, so a vertical page is anchored by the
            // edge it reads from - its right one - rather than by the left edge the scroller counts
            // from, and whatever it was reading stays where it was.
            _surface.Refresh();
            Reanchor(_place);
            _reanchor = _place;

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
        else if (change.Property == ExtentProperty && _reanchor is { } reader)
        {
            // The extent the relayout produced has been measured, so the anchor can be put back
            // without being clamped short of the edge it is held to.
            _reanchor = null;
            Reanchor(reader);
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

    /// <summary>
    /// Puts the zoom keys on the window, where a browser keeps them.
    /// </summary>
    /// <remarks>
    /// A key only reaches this view when one of its descendants holds the focus, and a reader pressing
    /// Ctrl + Plus expects the page to zoom whichever control - if any - has it. Listening at the window
    /// answers that; the handler takes the bubble phase with handled events included, so the keys are
    /// seen wherever they end up.
    /// </remarks>
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        _attachedTopLevel = TopLevel.GetTopLevel(this);
        _attachedTopLevel?.AddHandler(
            KeyDownEvent,
            OnWindowKeyDown,
            RoutingStrategies.Bubble,
            handledEventsToo: true);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _attachedTopLevel?.RemoveHandler(KeyDownEvent, OnWindowKeyDown);
        _attachedTopLevel = null;

        base.OnDetachedFromVisualTree(e);
    }

    /// <summary>
    /// Ctrl with the zoom keys scales the page, the way a browser does: Plus in, Minus out, 0 back to
    /// 100%. Everything else is left exactly as it was, so keys the focused control wants - Ctrl+C in
    /// the address bar, say - still reach it untouched.
    /// </summary>
    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (!e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            return;
        }

        switch (e.Key)
        {
            case Key.OemPlus:
            case Key.Add:
                e.Handled = true;
                ChangeZoom(Zoom + 0.1);
                break;

            case Key.OemMinus:
            case Key.Subtract:
                e.Handled = true;
                ChangeZoom(Zoom - 0.1);
                break;

            case Key.D0:
            case Key.NumPad0:
                e.Handled = true;
                ChangeZoom(1);
                break;
        }
    }

    /// <summary>
    /// Ctrl + wheel zooms the page instead of scrolling it: a wheel up grows the content, a wheel down
    /// shrinks it, both in the same 10% steps the keys use.
    /// </summary>
    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            e.Handled = true;
            ChangeZoom(Zoom * (e.Delta.Y > 0 ? 1.1 : 1 / 1.1));
            return;
        }

        base.OnPointerWheelChanged(e);
    }
}

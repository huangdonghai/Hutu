using AngleSharp.Dom;
using Avalonia;
using Avalonia.Controls;
using Hutu.Skia;
using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Offset = Hutu.Rendering.Layout.Offset;
using ScrollAlignment = Hutu.Rendering.Layout.ScrollAlignment;

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

    public HutuView()
    {
        // The same loader decodes the images for layout and draws them, so a run is never measured
        // against one decode and painted from another.
        _internalView = new HutuViewInternal(_measurer, _images);
        _surface = new HutuSurface(_internalView, new SkiaPaintExecutor(_measurer, _images));
        Content = _surface;
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
        return document;
    }

    public string GetHtml()
    {
        return _internalView.GetHtml();
    }

    /// <summary>
    /// Scrolls to an absolute offset, clamped to the range the document can scroll through, so a
    /// document height or an out-of-range delta is safe to pass in.
    /// </summary>
    /// <remarks>
    /// The range comes from the geometry Hutu produced rather than from
    /// <see cref="ScrollViewer.Extent"/>, which is only ever as fresh as the last layout pass. A
    /// scroll issued straight after a load would otherwise be clamped against the extent of the
    /// previous document - or, on the first load, against nothing at all - and quietly be lost.
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

        var target = _internalView.GetVerticalScrollOffset(element, alignment, Offset.Y);

        if (target is null)
        {
            return false;
        }

        ScrollTo(Offset.X, target.Value);
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
    /// Gets how far through the document the viewport has moved, from 0 at the top to 1 at the end.
    /// A document that fits is reported as fully scrolled.
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
        }

        return arranged;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        // Scrolling only moves the viewport-anchored content, so it repaints rather than relayouts.
        if (change.Property == OffsetProperty)
        {
            _surface.SetScrollOffset(new Offset(Offset.X, Offset.Y));
        }
    }
}

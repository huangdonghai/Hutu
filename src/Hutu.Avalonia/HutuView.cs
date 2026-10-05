using AngleSharp.Dom;
using Avalonia;
using Avalonia.Controls;
using Hutu.Skia;
using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Offset = Hutu.Rendering.Layout.Offset;

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

    public async void LoadUrlAsync(
        string url,
        CancellationToken cancellationToken = default)
    {
        await _internalView.LoadUrlAsync(url, cancellationToken);
        _surface.Refresh();
    }

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

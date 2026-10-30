using Avalonia;
using Avalonia.Media;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using Hutu.Rendering;
using Hutu.Rendering.Paint;
using Hutu.Skia;
using Offset = Hutu.Rendering.Layout.Offset;

namespace Hutu.Avalonia;

/// <summary>
/// Bridges Avalonia's drawing pipeline to Skia: it leases the platform's <c>SKCanvas</c> and runs the
/// paint list against it. The lease is the supported way to draw with Skia inside Avalonia - it
/// keeps the canvas and its transform in a consistent state.
/// </summary>
internal sealed class SkiaPaintDrawOperation : ICustomDrawOperation
{
    private readonly PaintList _paint;
    private readonly SkiaPaintExecutor _executor;
    private readonly Offset _scroll;
    private readonly Double _zoom;
    private readonly PageSlice? _page;

    public SkiaPaintDrawOperation(
        Rect bounds,
        PaintList paint,
        SkiaPaintExecutor executor,
        Offset scroll,
        Double zoom,
        PageSlice? page = null)
    {
        Bounds = bounds;
        _paint = paint;
        _executor = executor;
        _scroll = scroll;
        _zoom = zoom;
        _page = page;
    }

    public Rect Bounds { get; }

    public void Dispose()
    {
    }

    public bool HitTest(Point p) => Bounds.Contains(p);

    public bool Equals(ICustomDrawOperation? other) => false;

    public void Render(ImmediateDrawingContext context)
    {
        var feature = context.TryGetFeature<ISkiaSharpApiLeaseFeature>();

        if (feature is null)
        {
            return;
        }

        using var lease = feature.Lease();
        var canvas = lease.SkCanvas;

        // The document is drawn in its own coordinates and the page zoom is a plain scale of the whole
        // thing: a fixed or sticky box is offset by the scroll in this same - already scaled - space, so
        // the executor's own counter-translation keeps it pinned exactly as it does at zoom 1.
        canvas.Save();
        canvas.Scale((Single)_zoom);

        // A paged view draws one sheet: the page box is the canvas, and the slice is clipped to the
        // page area and moved to its top edge by the executor itself.
        if (_page is { } page)
        {
            _executor.ExecutePage(_paint, canvas, page);
        }
        else
        {
            _executor.Execute(_paint, canvas, _scroll);
        }

        canvas.Restore();
    }
}

using Avalonia;
using Avalonia.Media;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
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

    public SkiaPaintDrawOperation(Rect bounds, PaintList paint, SkiaPaintExecutor executor, Offset scroll)
    {
        Bounds = bounds;
        _paint = paint;
        _executor = executor;
        _scroll = scroll;
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
        _executor.Execute(_paint, lease.SkCanvas, _scroll);
    }
}

using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Hutu.Skia;
using Offset = Hutu.Rendering.Layout.Offset;

namespace Hutu.Avalonia;

/// <summary>
/// The self-drawing control that paints the laid-out document.
/// </summary>
/// <remarks>
/// Drawing is a pure consumer of the paint list: it never triggers style computation or layout, so
/// scrolling and redrawing stay cheap. The desired size is the document size, which is what tells
/// the surrounding <see cref="Avalonia.Controls.ScrollViewer"/> how far it may scroll.
/// <para>
/// The scroll offset is carried here rather than in layout: a <c>fixed</c> or <c>sticky</c> box is
/// moved by the executor at paint time, so scrolling repaints without ever relayouting.
/// </para>
/// </remarks>
public sealed class HutuSurface : Control
{
    private readonly HutuViewInternal _view;
    private readonly SkiaPaintExecutor _executor;
    private Offset _scroll;

    public HutuSurface(HutuViewInternal view, SkiaPaintExecutor executor)
    {
        _view = view;
        _executor = executor;
        ClipToBounds = true;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var layout = _view.Layout;

        return layout is null ? default : new Size(layout.Width, layout.Height);
    }

    public override void Render(DrawingContext context)
    {
        var paint = _view.Paint;

        if (paint is null)
        {
            return;
        }

        context.Custom(new SkiaPaintDrawOperation(
            new Rect(0, 0, paint.Bounds.Width, paint.Bounds.Height),
            paint,
            _executor,
            _scroll));
    }

    /// <summary>Reports how far the surrounding scroller has moved, so viewport-anchored content can follow.</summary>
    public void SetScrollOffset(Offset offset)
    {
        if (_scroll.X == offset.X && _scroll.Y == offset.Y)
        {
            return;
        }

        _scroll = offset;
        InvalidateVisual();
    }

    /// <summary>Re-measures and repaints; called after a document is loaded or replaced.</summary>
    public void Refresh()
    {
        InvalidateMeasure();
        InvalidateVisual();
    }
}

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Hutu.Skia;
using System;
using Offset = Hutu.Rendering.Layout.Offset;

namespace Hutu.Avalonia;

/// <summary>
/// The self-drawing control that paints the laid-out document, and the thing the reader points at.
/// </summary>
/// <remarks>
/// Drawing is a pure consumer of the paint list: it never triggers style computation or layout, so
/// scrolling and redrawing stay cheap. The desired size is the document size, which is what tells
/// the surrounding <see cref="Avalonia.Controls.ScrollViewer"/> how far it may scroll.
/// <para>
/// The scroll offset is carried here rather than in layout: a <c>fixed</c> or <c>sticky</c> box is
/// moved by the executor at paint time, so scrolling repaints without ever relayouting.
/// </para>
/// <para>
/// A pointer gesture is translated into document coordinates and handed to the core, which owns the
/// selection; the control only reports where the pointer was. That split is what lets the other
/// hosts reuse the same selection without reimplementing it.
/// </para>
/// </remarks>
public sealed class HutuSurface : Control
{
    private readonly HutuViewInternal _view;
    private readonly SkiaPaintExecutor _executor;
    private Offset _scroll;
    private Boolean _dragging;

    public HutuSurface(HutuViewInternal view, SkiaPaintExecutor executor)
    {
        _view = view;
        _executor = executor;
        ClipToBounds = true;

        // The surface takes focus so the keys that act on a selection - Ctrl+C - reach the view once
        // the reader has pressed on the text, without a second click to focus something first.
        Focusable = true;
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

    /// <summary>
    /// Starts a selection gesture: a drag from the point, unless the click count says the reader
    /// meant a word or a whole line.
    /// </summary>
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);

        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        var point = ToDocument(e.GetPosition(this));

        // A double click takes the word and a triple click the line; a longer run of clicks repeats
        // the line, which is what a reader leaning on the button expects to keep seeing.
        if (e.ClickCount >= 3)
        {
            _view.SelectLine(point.X, point.Y);
        }
        else if (e.ClickCount == 2)
        {
            _view.SelectWord(point.X, point.Y);
        }
        else
        {
            _view.BeginSelection(point.X, point.Y);
            _dragging = true;
            e.Pointer.Capture(this);
        }

        // Focus follows the press, so a key that acts on the selection - Ctrl+C - is already routed
        // to the view when it arrives, rather than landing on whatever was focused before.
        Focus();

        InvalidateVisual();
        e.Handled = true;
    }

    /// <summary>Extends a drag to the point the pointer has reached.</summary>
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);

        if (!_dragging)
        {
            return;
        }

        var point = ToDocument(e.GetPosition(this));

        _view.ExtendSelection(point.X, point.Y);
        InvalidateVisual();
    }

    /// <summary>Ends a drag at the point the button was released on.</summary>
    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);

        if (!_dragging)
        {
            return;
        }

        _dragging = false;
        e.Pointer.Capture(null);

        // The release can land somewhere the last move never reported, so the final position comes
        // from here rather than being assumed to be the previous one.
        var point = ToDocument(e.GetPosition(this));

        _view.ExtendSelection(point.X, point.Y);
        InvalidateVisual();
        e.Handled = true;
    }

    /// <summary>
    /// Forgets a drag whose pointer was taken away - a window losing the capture, say. The selection
    /// it produced stays, because that is still what the reader asked for.
    /// </summary>
    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        _dragging = false;
    }

    /// <summary>
    /// Converts a point on the control into document coordinates. The paint list is in document
    /// coordinates and a scrolled view starts partway down it, so the offset is added back here -
    /// which is what keeps scrolling out of the core, where nothing knows about it.
    /// </summary>
    private Point ToDocument(Point point) => new(point.X + _scroll.X, point.Y + _scroll.Y);

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

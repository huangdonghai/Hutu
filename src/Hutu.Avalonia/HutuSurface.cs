using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Hutu.Rendering;
using Hutu.Rendering.Interaction;
using Hutu.Skia;
using System;
using System.Collections.Generic;
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
    /// <summary>How far the pointer may stray between press and release and still be the same click.</summary>
    private const Double ClickSlop = 4;

    private readonly HutuViewInternal _view;
    private readonly SkiaPaintExecutor _executor;
    private readonly Action<LinkTarget>? _follow;
    private Offset _scroll;
    private Boolean _dragging;
    private LinkTarget? _pressed;
    private Point _pressedAt;

    // One instance rather than one per assignment, so a move over text does not allocate and the
    // cursor a test reads back is the same object every time.
    private static readonly Cursor TextCursor = new(StandardCursorType.Ibeam);

    // The rest of the shapes are built on first use rather than up front: building one asks the
    // platform for its cursor factory, and a document no pointer has moved over has no reason to.
    private static Dictionary<CursorKind, Cursor>? _cursors;

    /// <param name="follow">
    /// What to do about a link the reader clicks. It is handed in rather than decided here because
    /// following one is not always the same thing: an anchor is a scroll, and the scrolling belongs to
    /// the host that owns it.
    /// </param>
    public HutuSurface(HutuViewInternal view, SkiaPaintExecutor executor, Action<LinkTarget>? follow = null)
    {
        _view = view;
        _executor = executor;
        _follow = follow;
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
    /// Presses at the point: the document is told what the button went down on, so anything answering
    /// to <c>:active</c> shows it, and a selection gesture starts unless the click count says the
    /// reader meant a word or a whole line.
    /// </summary>
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);

        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        var point = ToDocument(e.GetPosition(this));

        // Marked before the gesture starts, because what the press changes - a rule answering to
        // `:active` - moves the very boxes the gesture is about to be placed against.
        Repaint(_view.PointerPressed(point.X, point.Y));

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

            // Remembered rather than acted on: whether this was a click on a link is only known once
            // the button comes up again, without the gesture having turned into a selection.
            _pressed = _view.LinkAt(point.X, point.Y);
            _pressedAt = e.GetPosition(this);

            e.Pointer.Capture(this);
        }

        // Focus follows the press, so a key that acts on the selection - Ctrl+C - is already routed
        // to the view when it arrives, rather than landing on whatever was focused before.
        Focus();

        InvalidateVisual();
        e.Handled = true;
    }

    /// <summary>
    /// Reports where the pointer is: a drag extends the selection it is making, and a pointer that is
    /// only hovering tells the document what it is over.
    /// </summary>
    /// <remarks>
    /// A hover is what turns a state into a repaint - the colour a link takes, the hint it raises, the
    /// shape of the pointer - so every move reports one and the document decides whether anything about
    /// it actually changed.
    /// </remarks>
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);

        var point = ToDocument(e.GetPosition(this));

        if (_dragging)
        {
            // The document underneath is left alone while the gesture runs, because a hover that
            // repainted in the middle of it would fight the selection being made - but the shape the
            // point asks for is still asked of it, which marks nothing and paints nothing.
            var asked = _view.CursorAt(point.X, point.Y);

            // A drag is a selection wherever the pointer wanders, so a point that asks for nothing in
            // particular - past the end of the text, say - is kept as text, turning with the text where
            // the gesture crosses from a line into a column. A point that names a shape of its own is
            // answered for with that instead: a link is a link for the whole of a gesture, rather than
            // being read as text the moment the button goes down.
            SetCursor(CursorFor(asked is CursorKind.Default ? CursorKind.Text : asked));
            _view.ExtendSelection(point.X, point.Y);
            InvalidateVisual();
            return;
        }

        var moved = _view.PointerMoved(point.X, point.Y);

        SetCursor(CursorFor(moved.Cursor));
        SetHint(moved.Hint);

        Repaint(moved.Changed);
    }

    /// <summary>
    /// Takes the hover, the hint and the pointer away once the pointer leaves, so nothing about the
    /// surface lingers over the control the reader moved to. A drag is left alone: the pointer is still
    /// captured, so an exit is not really a departure.
    /// </summary>
    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);

        if (_dragging)
        {
            return;
        }

        Repaint(_view.PointerExited());

        SetCursor(null);
        SetHint(null);
    }

    /// <summary>
    /// Points the cursor at the shape the document asked for, or at the platform's own when it asked
    /// for nothing in particular.
    /// </summary>
    /// <remarks>
    /// Setting the property raises a change notification, so a move that changes nothing pays nothing -
    /// which matters, because a move reports far more often than the answer changes.
    /// </remarks>
    private void SetCursor(Cursor? wanted)
    {
        if (!ReferenceEquals(Cursor, wanted))
        {
            Cursor = wanted;
        }
    }

    /// <summary>
    /// Gets the cursor for what lies under the pointer. Null leaves the pointer to the platform, which
    /// is what an element that declares nothing and holds no text asks for.
    /// </summary>
    private Cursor? CursorFor(CursorKind kind)
    {
        if (kind is CursorKind.Auto or CursorKind.Default)
        {
            return null;
        }

        // The turned I-beam is the one shape no platform carries, so it is drawn - and a drawn cursor
        // is taken at the size it was drawn, which is why it is asked for here rather than looked up
        // in the table: what size is right depends on the display this control is standing on. A
        // backend that cannot take a drawn cursor keeps the upright one rather than none at all.
        if (kind is CursorKind.VerticalText)
        {
            return VerticalTextCursor.For(Scaling) ?? TextCursor;
        }

        _cursors ??= CreateCursors();

        return _cursors.TryGetValue(kind, out var cursor) ? cursor : null;
    }

    /// <summary>Builds the shapes the platform carries once, on the first move that asks for one.</summary>
    private static Dictionary<CursorKind, Cursor> CreateCursors() => new()
    {
        [CursorKind.Text] = TextCursor,
        [CursorKind.Pointer] = new Cursor(StandardCursorType.Hand),
        [CursorKind.Move] = new Cursor(StandardCursorType.SizeAll),
        [CursorKind.Wait] = new Cursor(StandardCursorType.Wait),
        [CursorKind.Help] = new Cursor(StandardCursorType.Help),
        [CursorKind.NotAllowed] = new Cursor(StandardCursorType.No),
        [CursorKind.Crosshair] = new Cursor(StandardCursorType.Cross),
        [CursorKind.None] = new Cursor(StandardCursorType.None),
    };

    /// <summary>Gets how many pixels the display this control is on spends on a point.</summary>
    private Double Scaling => TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;

    /// <summary>
    /// Raises the hint for what lies under the pointer, which the platform shows in its own time. A
    /// hint belongs to what it describes, so it changes with the pointer and goes away with it.
    /// </summary>
    private void SetHint(String? hint)
    {
        if (!String.Equals(ToolTip.GetTip(this) as String, hint, StringComparison.Ordinal))
        {
            ToolTip.SetTip(this, hint);
        }
    }

    /// <summary>
    /// Re-measures and repaints when the document changed. Re-measuring comes with it because a state
    /// can change the size of what it is drawn on, and the surrounding scroller is told how far it may
    /// scroll by the size this control reports.
    /// </summary>
    private void Repaint(Boolean changed)
    {
        if (!changed)
        {
            return;
        }

        InvalidateMeasure();
        InvalidateVisual();
    }

    /// <summary>
    /// Follows the link the button went down on, when the gesture turns out to be a click on it.
    /// </summary>
    /// <remarks>
    /// A press that was dragged somewhere else, or that selected something on the way, is a selection
    /// rather than a click - which is what stops a reader selecting a link's text from following the
    /// link instead. The link comes from the press rather than the release, because the press is the
    /// one the reader aimed at.
    /// </remarks>
    private void Follow(LinkTarget? link, Point released)
    {
        if (link is not { } target || _follow is null || _view.HasSelection || Strayed(released))
        {
            return;
        }

        _follow(target);
    }

    /// <summary>Gets whether the pointer wandered far enough from the press to have been a drag.</summary>
    private Boolean Strayed(Point released) =>
        Math.Abs(released.X - _pressedAt.X) > ClickSlop || Math.Abs(released.Y - _pressedAt.Y) > ClickSlop;

    /// <summary>Ends a drag at the point the button was released on.</summary>
    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);

        // The press is over, so whatever was marked as being pressed is let go of again.
        Repaint(_view.PointerReleased());

        if (!_dragging)
        {
            return;
        }

        _dragging = false;

        // Taken before the capture is let go of: releasing the pointer raises the capture-lost
        // handler, which forgets the press - and the press is what remembers which link was clicked.
        var pressed = _pressed;
        _pressed = null;

        e.Pointer.Capture(null);

        // The release can land somewhere the last move never reported, so the final position comes
        // from here rather than being assumed to be the previous one.
        var point = ToDocument(e.GetPosition(this));

        _view.ExtendSelection(point.X, point.Y);

        // The gesture is over, so the pointer answers for the point again rather than staying the text
        // one it was kept as for the length of the drag - and the hover it now describes is the one the
        // document should be showing.
        var landed = _view.PointerMoved(point.X, point.Y);

        SetCursor(CursorFor(landed.Cursor));
        SetHint(landed.Hint);
        Repaint(landed.Changed);

        Follow(pressed, e.GetPosition(this));

        InvalidateVisual();
        e.Handled = true;
    }

    /// <summary>
    /// Forgets a drag whose pointer was taken away - a window losing the capture, say. The selection it
    /// produced stays, because that is still what the reader asked for.
    /// </summary>
    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        _dragging = false;
        _pressed = null;

        // Whatever was being pressed is let go of as well: the button is not coming back to this.
        Repaint(_view.PointerReleased());
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

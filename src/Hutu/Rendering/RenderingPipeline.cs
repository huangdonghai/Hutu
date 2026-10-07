using AngleSharp.Css;
using AngleSharp.Css.RenderTree;
using AngleSharp.Dom;
using Hutu.Rendering.Boxes;
using Hutu.Rendering.Layout;
using Hutu.Rendering.Media;
using Hutu.Rendering.Paint;

namespace Hutu.Rendering;

/// <summary>
/// Orchestrates the rendering pipeline for a single document: the AngleSharp.Css render tree, the
/// box tree, layout and the platform-neutral paint list.
/// </summary>
/// <remarks>
/// The renderer is pull-based. AngleSharp never signals that drawing is required, so the host
/// drives the pipeline explicitly after a document is loaded and again whenever the viewport
/// changes (both feed the style computation, which is why a resize needs a full rerun).
/// <para>
/// Layout needs a text measurer, which is a backend concern; when none is supplied the pipeline
/// stops at the box tree and <see cref="Layout"/> stays null.
/// </para>
/// </remarks>
public sealed class RenderingPipeline
{
    private readonly IRenderDevice _device;
    private readonly LayoutEngine? _layout;
    private readonly IImageProvider? _images;
    private readonly Painter _painter = new();

    public RenderingPipeline(IRenderDevice device, ITextMeasurer? textMeasurer = null, IImageProvider? images = null)
    {
        _device = device;
        _layout = textMeasurer is null ? null : new LayoutEngine(textMeasurer);
        _images = images;
    }

    /// <summary>
    /// Gets the styled render tree produced by the last run, or null when nothing was built yet.
    /// It carries DOM references and computed styles but no geometry.
    /// </summary>
    public IRenderNode? RenderTree { get; private set; }

    /// <summary>
    /// Gets the box tree produced by the last run, or null when there is nothing to render.
    /// </summary>
    public Box? BoxTree { get; private set; }

    /// <summary>
    /// Gets the laid-out tree and document size, or null when nothing was built or no text
    /// measurer is available.
    /// </summary>
    public LayoutResult? Layout { get; private set; }

    /// <summary>
    /// Gets the drawing instructions for the last run, or null when nothing was built or no text
    /// measurer is available.
    /// </summary>
    public PaintList? Paint { get; private set; }

    /// <summary>Drops the derived trees so the next run recomputes styles from the document.</summary>
    public void Invalidate()
    {
        RenderTree = null;
        BoxTree = null;
        Layout = null;
        Paint = null;
        _painter.Highlight = null;
    }

    /// <summary>
    /// Sets the rectangles to highlight and rebuilds the paint list from the layout already held.
    /// </summary>
    /// <remarks>
    /// A highlight describes the selection, which changes far more often than the document does: a
    /// drag reports a new one on every pointer move. Repainting from the layout in hand is what keeps
    /// that from laying out again on each move.
    /// <para>
    /// A highlight equal to the one already painted is left alone. A state change that only moves the
    /// pointer rebuilds the document and repaints it before restoring an unchanged selection, and the
    /// second repaint of the very same layout would be pure waste - the common case where nothing is
    /// selected is exactly this.
    /// </para>
    /// </remarks>
    public void SetHighlight(IReadOnlyDictionary<LayoutBox, Rect>? highlight)
    {
        var painted = _painter.Highlight;
        _painter.Highlight = highlight;

        if (Layout is not null && !SameHighlight(painted, highlight))
        {
            Paint = _painter.Paint(Layout);
        }
    }

    /// <summary>
    /// Gets whether two highlights name the same runs. An absent highlight and an empty one are the
    /// same directive - draw nothing - and the painter treats them interchangeably.
    /// </summary>
    private static Boolean SameHighlight(
        IReadOnlyDictionary<LayoutBox, Rect>? a,
        IReadOnlyDictionary<LayoutBox, Rect>? b)
    {
        var count = a?.Count ?? 0;

        if (count != (b?.Count ?? 0))
        {
            return false;
        }

        if (count == 0)
        {
            return true;
        }

        foreach (var (box, rect) in a!)
        {
            if (!b!.TryGetValue(box, out var other) || !other.Equals(rect))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Rebuilds the render tree, the box tree and the layout for the document using the current
    /// device, and returns the render tree. A document without a window yields null.
    /// </summary>
    public IRenderNode? Build(IDocument document)
    {
        var window = document.DefaultView;

        RenderProbe.Begin();

        RenderTree = window?.Render(_device);
        RenderProbe.Style();

        BoxTree = BoxBuilder.Build(RenderTree, _device, _images);
        RenderProbe.Boxes();

        // A highlight names boxes of the layout it was measured against, so a new layout always drops
        // it: keeping it would leave the painter looking up runs that no longer exist. The host points
        // the selection at the new boxes after the build, which is what carries it across.
        _painter.Highlight = null;

        // The viewport is the initial containing block, so both of its dimensions matter: the height
        // is what a percentage height on the root - and on everything it makes definite - resolves
        // against.
        Layout = _layout?.Layout(BoxTree, _device.RenderWidth, _device.RenderHeight);
        RenderProbe.Layout();

        Paint = Layout is null ? null : _painter.Paint(Layout);
        RenderProbe.Paint();
        return RenderTree;
    }
}

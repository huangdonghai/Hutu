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
    /// </remarks>
    public void SetHighlight(IReadOnlyDictionary<LayoutBox, Rect>? highlight)
    {
        _painter.Highlight = highlight;

        if (Layout is not null)
        {
            Paint = _painter.Paint(Layout);
        }
    }

    /// <summary>
    /// Rebuilds the render tree, the box tree and the layout for the document using the current
    /// device, and returns the render tree. A document without a window yields null.
    /// </summary>
    public IRenderNode? Build(IDocument document)
    {
        var window = document.DefaultView;
        RenderTree = window?.Render(_device);
        BoxTree = BoxBuilder.Build(RenderTree, _device, _images);

        // A highlight names boxes of the layout it was measured against, so a new layout always
        // drops it: keeping it would leave the painter looking up runs that no longer exist.
        _painter.Highlight = null;

        // The viewport is the initial containing block, so both of its dimensions matter: the height
        // is what a percentage height on the root - and on everything it makes definite - resolves
        // against.
        Layout = _layout?.Layout(BoxTree, _device.RenderWidth, _device.RenderHeight);
        Paint = Layout is null ? null : _painter.Paint(Layout);
        return RenderTree;
    }
}

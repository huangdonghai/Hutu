using AngleSharp.Css;
using AngleSharp.Dom;
using Hutu.Rendering.Boxes;
using Hutu.Rendering.Layout;
using Hutu.Rendering.Media;
using Hutu.Rendering.Paint;
using System.Collections.Generic;
using System.Diagnostics;

namespace Hutu.Rendering;

/// <summary>
/// Orchestrates the rendering pipeline for a single document: the
/// box tree, layout and the platform-neutral paint list.
/// </summary>
/// <remarks>
/// The renderer is pull-based. AngleSharp never signals that drawing is required, so the host
/// drives the pipeline explicitly after a document is loaded and again whenever the viewport
/// changes. A viewport change feeds the style computation only insofar as the styles read it, so
/// <see cref="Build"/> decides on each run how much of the pipeline has to be walked again.
/// <para>
/// Layout needs a text measurer, which is a backend concern; when none is supplied the pipeline
/// stops at the box tree and <see cref="Layout"/> stays null.
/// </para>
/// </remarks>
internal sealed class RenderingPipeline
{
    private readonly HutuRenderDevice _device;
    private readonly LayoutEngine? _layout;
    private readonly IImageProvider? _images;
    private readonly Painter _painter = new();
    private readonly StyleResolution _styles = new();

    /// <summary>The layout and paint wall clock of the last <see cref="LayOut"/>, filled only while measuring.</summary>
    private Double _laidMs;
    private Double _paintedMs;

    /// <summary>The document the box tree in hand was built from.</summary>
    private IDocument? _built;

    /// <summary>The viewport the styles in hand were computed against.</summary>
    private (Int32 Width, Int32 Height, Double Zoom) _viewport;

    /// <summary>
    /// Whether the styles in hand read the viewport while they were being resolved. It is what
    /// decides whether a viewport that has since moved could have moved them with it.
    /// </summary>
    private Boolean _stylesReadViewport;

    public RenderingPipeline(HutuRenderDevice device, ITextMeasurer? textMeasurer = null, IImageProvider? images = null)
    {
        _device = device;
        _layout = textMeasurer is null ? null : new LayoutEngine(textMeasurer);
        _images = images;
    }

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

    /// <summary>
    /// Gets the <c>@page</c> rules the sheets carry for the current device, or null before a build
    /// has read them. The page box is not laid out yet; this is the table that will be asked for its
    /// size and margins when it is.
    /// </summary>
    internal PageStyleTable? PageStyles => PageRulesFor(_built);

    /// <summary>The table read from the sheets when a build produced no box and so kept no index.</summary>
    private PageStyleTable? _standalonePages;

    private IDocument? _standalonePagesFor;

    /// <summary>
    /// Gets the page rules for the document. A build that produced a box kept the index it built, and
    /// the table rides with it; a document that produced no box - a root that displays nothing - has
    /// no index to keep, so the sheets are read once for the page box alone, which is what still has
    /// to be printed.
    /// </summary>
    private PageStyleTable? PageRulesFor(IDocument? document)
    {
        if (_styles.Index is { } index)
        {
            return index.Pages;
        }

        if (document is null)
        {
            return null;
        }

        if (_standalonePages is null || !ReferenceEquals(_standalonePagesFor, document))
        {
            _standalonePages = RuleBucketIndex.Build(BoxBuilder.SheetsOf(document), _device).Pages;
            _standalonePagesFor = document;
        }

        return _standalonePages;
    }

    /// <summary>
    /// Gets the text of every style sheet the document writes itself. A page rule's margin boxes are
    /// dropped by the CSS object model, so they are read from here; a sheet reached through a link is
    /// not among these yet.
    /// </summary>
    private static List<String> StyleTexts(IDocument? document)
    {
        var texts = new List<String>();

        if (document is not null)
        {
            foreach (var style in document.QuerySelectorAll("style"))
            {
                texts.Add(style.TextContent);
            }
        }

        return texts;
    }

    /// <summary>
    /// Gets the pages the document comes to when the device is printing, or null on screen. The
    /// slices name the tall layout they were cut from, which stays in <see cref="Layout"/>; a page is
    /// drawn by moving its slice to the page area rather than by laying it out again.
    /// </summary>
    public PagedLayout? Pages { get; private set; }

    /// <summary>The page box the last build was laid out against, or null when the medium is screen.</summary>
    internal PageGeometry? PageBox { get; private set; }

    /// <summary>
    /// Whether <see cref="Build"/> times its stages and leaves them in <see cref="LastBuildStages"/>.
    /// Off by default, and read from the timestamp clock rather than from a stopwatch, so a renderer
    /// that never asks for it pays nothing: one branch that is always false.
    /// </summary>
    public Boolean MeasureStages { get; set; }

    /// <summary>
    /// The wall clock of the last build, split into the pieces a rebuild is made of, or null while
    /// <see cref="MeasureStages"/> is off. <c>Fused</c> is the style-and-box rebuild, <c>Layout</c> is
    /// the layout, and <c>Paint</c> is the paint list; <c>Fast</c> says the build took the
    /// restyle-without-relayout path, where <c>Layout</c> is zero because no box was moved.
    /// </summary>
    public (Double Fused, Double Layout, Double Paint, Boolean Fast)? LastBuildStages { get; private set; }

    /// <summary>
    /// The breakdown of the last fused build's style half - matching against computing, and how much
    /// of the document had to be looked at again - or null while <see cref="MeasureStages"/> is off.
    /// </summary>
    public FusedProfile? LastFusedProfile { get; private set; }

    /// <summary>Drops the derived trees so the next run recomputes styles from the document.</summary>
    public void Invalidate()
    {
        BoxTree = null;
        Layout = null;
        Paint = null;
        Pages = null;
        PageBox = null;
        _painter.Highlight = null;
        _styles.Reset();
        _built = null;
        _stylesReadViewport = false;
        _standalonePages = null;
        _standalonePagesFor = null;
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
    /// Rebuilds the box tree and the layout for the document using the current device.
    /// A document without a window yields null.
    /// </summary>
    /// <remarks>
    /// A document or a viewport that has not changed is not necessarily a full run either. The
    /// styles are a function of the document, the device and the marks the interaction state has
    /// placed, so a rebuild that only moves the marks reuses the rule index and the caches behind
    /// it; a viewport that moved under an unchanged document lays the tree out again without
    /// computing it over, provided the styles never read the viewport and no media query moved; and
    /// a rebuild that only moved the colours leaves every box where the layout put it, so the tree
    /// in hand takes the new styles and only the paint list is built again.
    /// </remarks>
    public void Build(IDocument document)
    {
        var measuring = MeasureStages;
        var viewport = (_device.ViewPortWidth, _device.ViewPortHeight, _device.Zoom);
        var sameDocument = ReferenceEquals(_built, document);
        var movedViewport = viewport != _viewport;

        if (sameDocument && movedViewport && BoxTree is not null &&
            !_stylesReadViewport && _styles.StillDescribes(document, _device))
        {
            _viewport = viewport;
            LayOut();
            RecordStages(0d, _laidMs, _paintedMs, false);
            return;
        }

        if (!sameDocument || movedViewport)
        {
            // A different document brings different rules and different declarations, and an index
            // is built against a device: either way what is remembered no longer describes what is
            // about to be read.
            _styles.Reset();
        }

        _built = document;
        _viewport = viewport;

        // Taken before the walk rather than after it, so what is answered is what this build did
        // and not what the one before it left behind.
        _device.TakeViewportRead();

        // The fused path matches, cascades and computes from the DOM directly, with no render
        // tree and no per-element declaration.
        var faces = FontFaceTable.From(document);

        var profile = measuring ? new FusedProfile() : null;
        var fusedStart = measuring ? Stopwatch.GetTimestamp() : 0L;
        var fresh = BoxBuilder.TryBuildFused(document, _device, _images, faces, _styles, profile);
        var fusedMs = Elapsed(fusedStart);
        LastFusedProfile = profile;

        _stylesReadViewport = _device.TakeViewportRead();

        // The marks are the only thing that moved, the sheets say so in paint alone, and the fresh
        // tree lays every box out exactly the way the one in hand does: the geometry stands and the
        // styles are handed over. The highlight is kept rather than dropped, because it names runs
        // of the layout in hand and that layout has not been replaced.
        if (sameDocument && !movedViewport && _styles.RestylesWithoutRelayout &&
            BoxTree is not null && fresh is not null && BoxTree.SameLayout(fresh))
        {
            var adoptStart = measuring ? Stopwatch.GetTimestamp() : 0L;
            AdoptStyles(BoxTree, fresh);
            fusedMs += Elapsed(adoptStart);

            var paintStart = measuring ? Stopwatch.GetTimestamp() : 0L;
            Paint = _painter.Paint(Layout!);
            RecordStages(fusedMs, 0d, Elapsed(paintStart), true);
            return;
        }

        BoxTree = fresh;
        LayOut();
        RecordStages(fusedMs, _laidMs, _paintedMs, false);
    }

    /// <summary>
    /// Hands the tree in hand the styles a fresh build computed for it, keeping its geometry.
    /// </summary>
    /// <remarks>
    /// The two trees are known to correspond box for box - <see cref="Box.SameLayout"/> has just
    /// said so - so the walk is a straight pairing of the children.
    /// </remarks>
    private static void AdoptStyles(Box kept, Box fresh)
    {
        kept.Style = fresh.Style;
        kept.MarkerStyle = fresh.MarkerStyle;
        kept.FirstLinePaint = fresh.FirstLinePaint;

        var mine = kept.Children;
        var theirs = fresh.Children;

        for (var i = 0; i < mine.Count; i++)
        {
            AdoptStyles(mine[i], theirs[i]);
        }
    }

    /// <summary>
    /// Lays the box tree out at the viewport the device now reports and paints it. The highlight is
    /// dropped, because it names runs of the layout it was measured against.
    /// </summary>
    private void LayOut()
    {
        _painter.Highlight = null;

        var measuring = MeasureStages;
        var layoutStart = measuring ? Stopwatch.GetTimestamp() : 0L;

        // The viewport is the initial containing block, so both of its dimensions matter: the height
        // is what a percentage height on the root - and on everything it makes definite - resolves
        // against. A printing device hands the document the page area instead: the same tall layout
        // is produced at the page's width, and the page sequence cuts it into pages afterwards. A
        // named page may state another width, which the pages it names are clipped to until a layout
        // per page width exists.
        var rules = _device.Category == DeviceCategory.Printer ? PageRulesFor(_built) : null;

        // The margin boxes of the page rules are not part of what the CSS object model keeps, so they
        // are read from the sheets' own text and handed to the table the pages are asked of.
        if (rules is not null)
        {
            rules.AttachMarginBoxes(PageMarginScan.Read(StyleTexts(_built)));
        }

        var inherited = BoxTree is null ? StyleThickness.Zero : BoxTree.Style.Margin;

        // When every named page the document states is the same page, the tall layout is measured at
        // that page's width. A document with named pages of two widths needs a layout per page group,
        // which the single tall layout cannot give it.
        var named = PreferredPageName(BoxTree);
        PageGeometry? page = rules is { } activeRules
            ? named is { } pageName
                ? activeRules.Page(pageName, inherited)
                : activeRules.DefaultPage(inherited)
            : null;

        PageBox = page;

        Layout = page is { } box
            ? _layout?.Layout(BoxTree, Math.Max(1, box.ContentWidth), Math.Max(1, box.ContentHeight))
            : _layout?.Layout(BoxTree, _device.RenderWidth, _device.RenderHeight);

        var paintStart = measuring ? Stopwatch.GetTimestamp() : 0L;

        Paint = Layout is null ? null : _painter.Paint(Layout);

        Pages = rules is { } pageRules
            ? Layout is { } tall
                ? PageSequence.Build(tall, pageRules, inherited)
                : page is { } blank ? PageSequence.Blank(blank) : null
            : null;

        if (measuring)
        {
            _laidMs = Stopwatch.GetElapsedTime(layoutStart, paintStart).TotalMilliseconds;
            _paintedMs = Stopwatch.GetElapsedTime(paintStart).TotalMilliseconds;
        }
    }

    /// <summary>
    /// Gets the one page name the document's boxes state, or null when they state none or more than
    /// one. It is what lets a document that lives on a single named page be measured at that page's
    /// width, which a page box with another size would otherwise clip.
    /// </summary>
    private static String? PreferredPageName(Box? root)
    {
        if (root is null)
        {
            return null;
        }

        String? found = null;

        foreach (var box in Walk(root))
        {
            if (box.Style.PageName is not { } name)
            {
                continue;
            }

            if (found is null)
            {
                found = name;
            }
            else if (!String.Equals(found, name, StringComparison.Ordinal))
            {
                return null;
            }
        }

        return found;
    }

    private static IEnumerable<Box> Walk(Box box)
    {
        yield return box;

        foreach (var child in box.Children)
        {
            foreach (var node in Walk(child))
            {
                yield return node;
            }
        }
    }

    /// <summary>Milliseconds since a <see cref="Stopwatch"/> timestamp, or zero when nothing was timed.</summary>
    private static Double Elapsed(Int64 start) =>
        start == 0L ? 0d : Stopwatch.GetElapsedTime(start).TotalMilliseconds;

    /// <summary>Records a build's stages, unless nobody asked to measure them.</summary>
    private void RecordStages(Double fused, Double layout, Double paint, Boolean fast)
    {
        if (MeasureStages)
        {
            LastBuildStages = (fused, layout, paint, fast);
        }
    }
}

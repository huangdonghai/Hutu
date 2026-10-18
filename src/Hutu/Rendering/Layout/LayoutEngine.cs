using Hutu.Rendering.Boxes;
using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace Hutu.Rendering.Layout;

/// <summary>
/// Turns the box tree into a layout tree with resolved geometry.
/// </summary>
/// <remarks>
/// Layout runs in two phases. <see cref="Measure"/> answers how large a box wants to be given the
/// space that is available to it, without producing any geometry; <see cref="Arrange"/> then fixes
/// that size into a rectangle and positions the content. A formatting context that has to decide how
/// to share space - flex, grid, a table - needs the sizes before it can hand positions out, which is
/// exactly what the split provides.
/// <para>
/// Sizes are resolved against the containing block rather than the viewport. A percentage is a
/// fraction of the containing block, <c>width: auto</c> fills whatever its margins leave of it, and a
/// declared size covers the padding and the border only when <c>box-sizing</c> says so. The
/// containing block of a box is its parent's content box; the one the root sees is the viewport.
/// Both facts are carried by <see cref="LayoutContext"/> rather than read from the render device.
/// </para>
/// <para>
/// Measurements are cached for the duration of a pass, keyed by the box and the basis the size was
/// resolved from. A viewport-sized layout therefore measures each box at most once per distinct
/// available width, however many times a formatting context negotiates around it. The intrinsic
/// sizes a flex or grid container asks its items for are cached the same way, which matters because
/// track sizing asks for them again on every pass of its negotiation.
/// </para>
/// </remarks>
internal sealed class LayoutEngine
{
    private readonly InlineLayout _inline;
    private readonly BlockLayout _block;
    private readonly FlexFormattingContext _flex;
    private readonly GridFormattingContext _grid;
    private readonly MasonryFormattingContext _masonry;
    private readonly TableFormattingContext _table;
    private readonly PositionedLayout _positioned;
    private readonly ITextMeasurer _measurer;
    private readonly Dictionary<MeasureKey, Size> _cache = new();
    private readonly Dictionary<MeasureKey, IntrinsicSize> _intrinsic = new();
    private readonly Dictionary<MeasureKey, IntrinsicSize> _contentIntrinsic = new();

    /// <summary>
    /// The intrinsic sizes of the boxes that answer the same at every containing block. Keyed by the
    /// box alone, because the basis is the one thing the answer does not depend on - see
    /// <see cref="Box.IntrinsicIgnoresBasis"/>.
    /// </summary>
    private readonly Dictionary<Box, IntrinsicSize> _intrinsicFree = new();

    /// <summary>The same, for the content-intrinsic cache.</summary>
    private readonly Dictionary<Box, IntrinsicSize> _contentIntrinsicFree = new();

    /// <summary>
    /// The floats each container's walk placed, published for the container above it. A container
    /// that establishes no block formatting context of its own does not hold the floats inside it
    /// (CSS 2.1 §9.4.1): they take part in the context of the nearest ancestor that does, so the
    /// walk that placed them hands them up to be absorbed there. The tracks are kept in the frame
    /// of the pass that filled them, which is why the record is dropped between the two passes.
    /// </summary>
    private readonly Dictionary<Box, FloatTracks> _floats = new();

    /// <summary>The engine the assertion measures against, made on first use.</summary>
    private LayoutEngine? _reference;

    /// <summary>
    /// Gets or sets whether a cached intrinsic size is checked against an honest measurement at the
    /// containing block in hand before it is handed back.
    /// </summary>
    /// <remarks>
    /// Leaving the basis out of the key is only safe while the size really does not depend on it, and
    /// the one place that can be caught being false is where a size measured at one basis is about to
    /// be returned at another. This is a verification switch rather than a feature: it multiplies the
    /// intrinsic work, so it stays off unless a test turns it on.
    /// </remarks>
    public static Boolean AssertBasisFree { get; set; }

    public LayoutEngine(ITextMeasurer measurer)
    {
        _measurer = measurer ?? throw new ArgumentNullException(nameof(measurer));
        _inline = new InlineLayout(measurer);
        _block = new BlockLayout(this, _inline);
        _flex = new FlexFormattingContext(this);
        _grid = new GridFormattingContext(this);
        _masonry = new MasonryFormattingContext(this, _grid);
        _table = new TableFormattingContext(this);
        _positioned = new PositionedLayout(this);
    }

    /// <summary>
    /// Lays the box tree out in the given viewport. The root is placed at the viewport's own start
    /// corner; null means there is nothing to lay out.
    /// </summary>
    /// <param name="viewportHeight">
    /// The viewport's height, which is the initial containing block's. A percentage height only
    /// resolves against it when it is positive; zero means the initial containing block has no
    /// definite height and a percentage height behaves as <c>auto</c>.
    /// </param>
    public LayoutResult? Layout(Box? root, Double viewportWidth, Double viewportHeight = 0, Offset scroll = default)
    {
        if (root is null)
        {
            return null;
        }

        var width = Math.Max(1, viewportWidth);
        var viewport = new Size(width, Math.Max(0, viewportHeight));
        var context = new LayoutContext(viewport, viewport, scroll, _measurer);

        _cache.Clear();
        _intrinsic.Clear();
        _contentIntrinsic.Clear();
        _intrinsicFree.Clear();
        _contentIntrinsicFree.Clear();
        _inline.ResetPlanCache();
        _floats.Clear();

        var size = Measure(root, MeasureConstraints.FitWidth(width), context);
        var margin = BoxModel.UsedMargin(root.Style, width, size.Width);

        // The root is the one box whose containing block is the initial containing block, and that
        // block begins where the page reads from - the top left corner of the viewport in horizontal
        // writing, and its top right corner in a right-to-left vertical mode. So the root is placed
        // against those edges rather than at the physical origin, and a root narrower than the
        // viewport - which is what a page whose columns are sized to its content comes to - stands
        // against the edge it reads from rather than against the opposite one. The margin on each
        // start side is honoured and the end side takes whatever is left over, which is the ordinary
        // over-constrained rule.
        var initial = new Rect(0, 0, width, viewport.Height);
        var flow = root.Flow;
        var (inlineStart, _) = flow.InlineMargins(margin);
        var (blockStart, _) = flow.BlockMargins(margin);

        // The arranging pass works in document coordinates where the measuring one worked in each
        // box's own frame, so the floats published while measuring are of no use to it: the record
        // is dropped here and every walk publishes its own again as it goes.
        _floats.Clear();

        var rootBox = Arrange(
            root,
            flow.Map(
                initial,
                inlineStart,
                flow.InlineExtent(size.Width, size.Height),
                blockStart,
                flow.BlockExtent(size.Width, size.Height)),
            context);

        ResolveDecorations(rootBox);

        // The markers of a list are placed from the same geometry, and after it for the same reason: an
        // item's marker stands level with the first line the item reaches, which is only known once the
        // lines have been put down.
        ResolveMarkers(rootBox);

        // What the document occupies is what a viewport has to be able to reach, which is not the
        // root's box: a page whose columns run sideways, or a box placed off an edge, reaches past
        // it. It is never less than the viewport itself either, because the viewport is what the
        // page was laid out in. Measuring it settles the frame as well, because a scroll offset is a
        // distance from where the document begins.
        var content = ScrollableOverflow.Of(rootBox, initial);
        ScrollableOverflow.MoveToOrigin(rootBox, content);

        return new LayoutResult(rootBox, content.Width, content.Height);
    }

    /// <summary>
    /// How a layout's time and allocation split across its phases.
    /// </summary>
internal sealed record LayoutProfile(
        Double MeasureTime,
        Double ArrangeTime,
        Double DecorationTime,
        Double OverflowTime,
        Double MeasureAllocMB,
        Double ArrangeAllocMB,
        Double DecorationAllocMB,
        Double OverflowAllocMB,
        Double TotalTime);

    /// <summary>
    /// Runs the full layout and reports how its time and allocation divide across the phases: the
    /// measure that sizes the tree, the arrange that places it, the corner-radius resolution, and the
    /// scrollable-overflow pass. Meant for the performance probe rather than production - it repeats
    /// the work several times to average it.
    /// </summary>
    /// <remarks>
    /// The four phases are the same ones <see cref="Layout"/> performs, run in the same order; the sum
    /// of the phase times is the whole layout. Allocation is measured per phase on its own so the figure
    /// is that phase's garbage, not the garbage the ones before it left - which is why each phase is
    /// warmed on a fresh cache before it is timed.
    /// </remarks>
    public LayoutProfile Profile(Box? root, Double viewportWidth, Double viewportHeight = 0, Offset scroll = default)
    {
        if (root is null)
        {
            return new LayoutProfile(0, 0, 0, 0, 0, 0, 0, 0, 0);
        }

        const Int32 warm = 3;
        const Int32 runs = 8;
        const Int32 allocRuns = 3;

        var width = Math.Max(1, viewportWidth);
        var viewport = new Size(width, Math.Max(0, viewportHeight));
        var context = new LayoutContext(viewport, viewport, scroll, _measurer);

        // Time, with the caches cold on the first of every run as Layout itself leaves them.
        var measureTime = 0.0;
        var arrangeTime = 0.0;
        var decorationTime = 0.0;
        var overflowTime = 0.0;

        for (var iteration = 0; iteration < warm; iteration++)
        {
            RunLayout(root, width, viewport, context, null, null, null, null);
        }

        GC.Collect();
        GC.WaitForPendingFinalizers();

        for (var iteration = 0; iteration < runs; iteration++)
        {
            var measure = new Stopwatch();
            var arrange = new Stopwatch();
            var decoration = new Stopwatch();
            var overflow = new Stopwatch();

            RunLayout(root, width, viewport, context, measure, arrange, decoration, overflow);

            measureTime += measure.Elapsed.TotalMilliseconds;
            arrangeTime += arrange.Elapsed.TotalMilliseconds;
            decorationTime += decoration.Elapsed.TotalMilliseconds;
            overflowTime += overflow.Elapsed.TotalMilliseconds;
        }

        // Allocation, each phase measured on its own so the figure is that phase's garbage alone.
        var measureAlloc = 0.0;
        var arrangeAlloc = 0.0;
        var decorationAlloc = 0.0;
        var overflowAlloc = 0.0;

        for (var iteration = 0; iteration < allocRuns; iteration++)
        {
            ResetCaches();
            var before = GC.GetTotalAllocatedBytes(false);
            Measure(root, MeasureConstraints.FitWidth(width), context);
            measureAlloc += (GC.GetTotalAllocatedBytes(false) - before) / 1048576.0;
        }

        for (var iteration = 0; iteration < allocRuns; iteration++)
        {
            ResetCaches();
            var size = Measure(root, MeasureConstraints.FitWidth(width), context);
            var before = GC.GetTotalAllocatedBytes(false);
            Arrange(root, RootBorderBox(root, size, width, viewport, context), context);
            arrangeAlloc += (GC.GetTotalAllocatedBytes(false) - before) / 1048576.0;
        }

        for (var iteration = 0; iteration < allocRuns; iteration++)
        {
            ResetCaches();
            var size = Measure(root, MeasureConstraints.FitWidth(width), context);
            var rootBox = Arrange(root, RootBorderBox(root, size, width, viewport, context), context);
            var before = GC.GetTotalAllocatedBytes(false);
            ResolveDecorations(rootBox);
            decorationAlloc += (GC.GetTotalAllocatedBytes(false) - before) / 1048576.0;
        }

        for (var iteration = 0; iteration < allocRuns; iteration++)
        {
            ResetCaches();
            var size = Measure(root, MeasureConstraints.FitWidth(width), context);
            var rootBox = Arrange(root, RootBorderBox(root, size, width, viewport, context), context);
            ResolveDecorations(rootBox);
            var content = ScrollableOverflow.Of(rootBox, new Rect(0, 0, width, viewport.Height));
            var before = GC.GetTotalAllocatedBytes(false);
            ScrollableOverflow.MoveToOrigin(rootBox, content);
            overflowAlloc += (GC.GetTotalAllocatedBytes(false) - before) / 1048576.0;
        }

        return new LayoutProfile(
            measureTime / runs,
            arrangeTime / runs,
            decorationTime / runs,
            overflowTime / runs,
            measureAlloc / allocRuns,
            arrangeAlloc / allocRuns,
            decorationAlloc / allocRuns,
            overflowAlloc / allocRuns,
            (measureTime + arrangeTime + decorationTime + overflowTime) / runs);
    }

    /// <summary>Runs one full layout, timing whichever phases are handed a stopwatch.</summary>
    private void RunLayout(
        Box root,
        Double width,
        Size viewport,
        LayoutContext context,
        Stopwatch? measure,
        Stopwatch? arrange,
        Stopwatch? decoration,
        Stopwatch? overflow)
    {
        ResetCaches();

        measure?.Start();
        var size = Measure(root, MeasureConstraints.FitWidth(width), context);
        measure?.Stop();

        arrange?.Start();
        var rootBox = Arrange(root, RootBorderBox(root, size, width, viewport, context), context);
        arrange?.Stop();

        decoration?.Start();
        ResolveDecorations(rootBox);
        ResolveMarkers(rootBox);
        decoration?.Stop();

        overflow?.Start();
        var content = ScrollableOverflow.Of(rootBox, new Rect(0, 0, width, viewport.Height));
        ScrollableOverflow.MoveToOrigin(rootBox, content);
        overflow?.Stop();
    }

    /// <summary>Clears the per-pass caches so a repeat measures the work fresh, as <see cref="Layout"/> does.</summary>
    private void ResetCaches()
    {
        _cache.Clear();
        _intrinsic.Clear();
        _contentIntrinsic.Clear();
        _intrinsicFree.Clear();
        _contentIntrinsicFree.Clear();
        _inline.ResetPlanCache();
        _floats.Clear();
    }

    /// <summary>The root's border box, exactly as <see cref="Layout"/> computes it.</summary>
    private static Rect RootBorderBox(Box root, Size size, Double width, Size viewport, LayoutContext context)
    {
        var margin = BoxModel.UsedMargin(root.Style, width, size.Width);
        var initial = new Rect(0, 0, width, viewport.Height);
        var flow = root.Flow;
        var (inlineStart, _) = flow.InlineMargins(margin);
        var (blockStart, _) = flow.BlockMargins(margin);

        return flow.Map(
            initial,
            inlineStart,
            flow.InlineExtent(size.Width, size.Height),
            blockStart,
            flow.BlockExtent(size.Width, size.Height));
    }

    /// <summary>
    /// Resolves the corner radii of every box, together with the two keywords that decide what those
    /// radii clip.
    /// </summary>
    /// <remarks>
    /// A radius is relative to the size of the box it belongs to - a percentage by definition, and the
    /// rule that shrinks radii which would overlap by necessity - so it cannot be resolved while a box
    /// is being arranged, only once the whole tree has been placed. Keeping it out of the painter
    /// matters: painting runs again on every invalidation, layout only when the document changes.
    /// </remarks>
    private static void ResolveDecorations(LayoutBox box) => ResolveDecorations(box, null);

    /// <summary>
    /// Settles the paint-time facts of a box: its radii, what its background is clipped to, whether it
    /// hides what overflows, and which decorations its text is drawn under.
    /// </summary>
    /// <param name="around">
    /// The lines the boxes above this one asked for and which reach into it - the list this box hands to
    /// its children with its own lines added, and the list a child that lays its own content out is
    /// given nothing of.
    /// </param>
    private static void ResolveDecorations(LayoutBox box, List<DecorationSource>? around)
    {
        var style = box.Source.Style;

        box.Radii = CornerRadii.Resolve(style.BorderRadius, box.BorderBox.Width, box.BorderBox.Height);
        box.BackgroundClip = style.BackgroundClip;

        // An inline element has no box of its own to clip anything to, and CSS says `overflow` has no
        // effect on one: honouring it would start cutting off the element's own runs.
        box.ClipsOverflow = box.Kind != LayoutKind.Inline && style.ClipsOverflow;

        if (box.Kind == LayoutKind.Inline)
        {
            box.Radii = Sliced(box.Radii, box);
        }

        // A decoration is asked for by an element and drawn across the text inside it, so the line a
        // block asked for runs on through every inline that follows, and a child that asks for none
        // takes none of it away (CSS Text Decoration 4 §2.1). A line box and a run of text are not
        // elements and own no decoration of their own, and an anonymous box borrows a style that was
        // already paid for by the element it came from - only the element itself is counted. Which is
        // also why only an element box can interrupt: the line and the runs inside an atomic box borrow
        // that box's style, and they are the text the box's own decoration is drawn across, not a fresh
        // context that starts over.
        var element = box.Kind is not (LayoutKind.Line or LayoutKind.Text) && !box.Source.IsAnonymous;

        if (element && StandsApart(box))
        {
            around = null;
        }

        var lines = element ? SourceOf(style) : null;

        box.Decorations = lines is null
            ? around
            : around is null
                ? new List<DecorationSource> { lines }
                : [.. around, lines];

        foreach (var child in box.Children)
        {
            ResolveDecorations(child, box.Decorations);
        }
    }

    /// <summary>
    /// Gets the lines this box decorates its text with, or null where it asked for none. The colour the
    /// line is drawn in is the one the decorating box settled on: its own <c>text-decoration-color</c>,
    /// or the colour of <em>its</em> text where that says <c>currentcolor</c> - never the colour of the
    /// descendant's glyphs the line happens to run across (CSS Text Decoration 4 §2.3).
    /// </summary>
    private static DecorationSource? SourceOf(ComputedStyle style) =>
        style.HasUnderline || style.HasOverline || style.HasLineThrough
            ? new DecorationSource(
                style.HasUnderline,
                style.HasOverline,
                style.HasLineThrough,
                style.Decoration,
                style.DecorationColor ?? ComputedStyle.ToArgb(style.Color),
                style.DecorationThickness,
                style.UnderlineOffset)
            : null;

    /// <summary>
    /// Gets whether the box stands apart from the line its ancestors are drawn on: one that is set as a
    /// unit inside a line - a replaced element, an inline block - or one that lays its own content out
    /// and so takes no part in the line the decoration runs across: a float, a table part, a box placed
    /// out of flow. An ancestor's decoration neither reaches into such a box nor is drawn over it, so the
    /// line stops at its edges and resumes beyond them (CSS Text Decoration 4 §2.5, §2.10.1).
    /// </summary>
    private static Boolean StandsApart(LayoutBox box) =>
        box.IsOutOfFlow ||
        box.Kind is LayoutKind.Image or LayoutKind.Svg or LayoutKind.HorizontalRule
            or LayoutKind.Table or LayoutKind.TableRowGroup or LayoutKind.TableRow or LayoutKind.TableCell ||
        box.Source.IsReplaced ||
        box.Source.Style.Float != FloatSide.None ||

        // A box that lays its own content out - an inline block, a flex, grid or table container, whether
        // it sits in a line or stacks - takes no part in the line its ancestors are drawn on
        // (CSS Text Decoration 4 §2.5).
        box.Source.Kind is BoxKind.InlineBlock or BoxKind.Flex or BoxKind.Grid or BoxKind.Masonry
            or BoxKind.Table;

    /// <summary>
    /// Works out where each list item's marker is drawn.
    /// </summary>
    /// <remarks>
    /// A marker beside an item is not in it: it hangs in the space the list was given for the purpose,
    /// ending at the edge the item's content begins at, and standing on the baseline of the first line
    /// the item reaches - its own first line when it has one, and the first line of whatever is nested
    /// inside it when it does not, which is where a browser puts the bullet of an item that opens with a
    /// sublist.
    /// <para>
    /// The rectangle is the whole marker box, which is wider than the glyph in it: every marker carries
    /// the space that gaps it from the text, and the space belongs to the marker rather than to the item,
    /// so a padding given to the item moves the marker and its gap together, as it does in a browser.
    /// </para>
    /// <para>
    /// A column is marked too, and there the marker stands above its column rather than beside its text:
    /// along a page that runs down, the inline axis is vertical, so the same rule - the marker's end on
    /// the edge the line begins at - lands it in the gutter at the head of the column, centred across
    /// the width the column takes and upright, which is what a browser draws.
    /// </para>
    /// <para>
    /// A marker stated <c>inside</c> its item never reaches here: the builder made it the item's first
    /// inline box, so it is laid out with the text and takes room on the line, which is what an inside
    /// marker is. Only the marker that hangs beside an item is placed from the finished geometry.
    /// </para>
    /// </remarks>
    private void ResolveMarkers(LayoutBox box)
    {
        var source = box.Source;

        // Only the item's own box is given a place for its marker: a line box borrows the style and the
        // element of the item it belongs to, and marking it as well would draw the bullet once per line.
        // An item that is an atomic inline-level box is a block container too, and so is a Block here.
        if (box.Kind == LayoutKind.Block &&
            source.Marker is { Length: > 0 } text &&
            source.MarkerPosition == MarkerPosition.Outside)
        {
            var font = source.Style.GetFont();
            var metrics = _measurer.GetMetrics(font);
            var advance = _measurer.MeasureWidth(text, font);
            var content = box.ContentBox;
            var line = FirstLine(box);

            if (box.Vertical)
            {
                var em = metrics.Ascent + metrics.Descent;
                var upward = box.Flow.InlineSign < 0;

                // The column the marker stands over: the first line's, where the item reaches a line, and
                // its own border box where it does not - an empty item is still marked, the way a
                // browser marks one, and §12.5.1 fixes the marker with respect to the border.
                var column = line is { } standing ? standing.BorderBox : box.BorderBox;

                // The edge the column begins at, which is its head where the line runs down and its foot
                // where a mode reads up the page. The marker ends there, so the space it carries is what
                // leaves the room between it and the first character below.
                var edge = upward ? column.Bottom : column.Y;

                box.MarkerBounds = new Rect(
                    column.X + (column.Width - em) / 2,
                    upward ? edge : edge - advance,
                    em,
                    advance);

                // Across the column, the baseline sits a descent from the left where the ascent leans
                // right and an ascent from the left where the run turns the other way - which is the
                // side an upward column puts it on.
                box.MarkerBaseline = upward ? metrics.Ascent : metrics.Descent;
            }
            else
            {
                // Where the reader's eye first lands on the item, stated as a coordinate of the page: the
                // baseline of its first line, or - for an item that reaches no line at all - the baseline
                // the first line of its own font would have had.
                var standing = line is { } first
                    ? first.BorderBox.Y + first.Baseline
                    : content.Y + metrics.Ascent;

                // The edge the marker hangs outside is the principal box's border edge: CSS 2.1
                // §12.5.1 fixes an outside marker with respect to the principal block box's border,
                // not to the content inside its padding. A page that reads right to left puts the
                // edge on the other side - and the marker ends there rather than starting there,
                // since it is the item's text that keeps its edge and the marker that hangs out.
                var start = box.Flow.InlineSign > 0
                    ? box.BorderBox.X - advance
                    : box.BorderBox.Right;

                box.MarkerBounds = new Rect(start, standing - metrics.Ascent, advance, metrics.Ascent + metrics.Descent);
                box.MarkerBaseline = metrics.Ascent;
            }
        }

        foreach (var child in box.Children)
        {
            ResolveMarkers(child);
        }
    }

    /// <summary>
    /// Gets the first line the box reaches, searching its own lines before those of anything nested in
    /// it - which is the line a marker is drawn level with.
    /// </summary>
    private static LayoutBox? FirstLine(LayoutBox box)
    {
        if (box.Kind == LayoutKind.Line)
        {
            return box;
        }

        foreach (var child in box.Children)
        {
            if (FirstLine(child) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    /// <summary>
    /// Squares off the pair of corners on the edge an inline fragment was cut at. Everything that
    /// would have curved along that edge was drawn on the line the element began or ended on, which is
    /// what <c>box-decoration-break: slice</c> asks for; without this every line of a broken element
    /// would grow a rounded end of its own.
    /// </summary>
    private static CornerRadii Sliced(CornerRadii radii, LayoutBox box)
    {
        if (!box.SlicesInlineStart && !box.SlicesInlineEnd)
        {
            return radii;
        }

        // The inline axis of a row runs across the box, so a row is cut down its left and right edges
        // and a column across its top and bottom ones. The cut begins at the edge the text begins
        // reading from - the near edge of a left-to-right row, the far one of a right-to-left row, and
        // the top one of a column unless the mode reads up the page.
        var startAtBeginning = box.Flow.InlineSign > 0;

        var top = box.Vertical &&
            ((box.SlicesInlineStart && startAtBeginning) || (box.SlicesInlineEnd && !startAtBeginning));

        var bottom = box.Vertical &&
            ((box.SlicesInlineStart && !startAtBeginning) || (box.SlicesInlineEnd && startAtBeginning));

        var left = !box.Vertical &&
            ((box.SlicesInlineStart && startAtBeginning) || (box.SlicesInlineEnd && !startAtBeginning));

        var right = !box.Vertical &&
            ((box.SlicesInlineStart && !startAtBeginning) || (box.SlicesInlineEnd && startAtBeginning));

        return new CornerRadii(
            top || left ? CornerRadius.Zero : radii.TopLeft,
            top || right ? CornerRadius.Zero : radii.TopRight,
            bottom || right ? CornerRadius.Zero : radii.BottomRight,
            bottom || left ? CornerRadius.Zero : radii.BottomLeft);
    }

    /// <summary>
    /// Measures a box against the available space, returning the size of its border box. The result
    /// is cached, so a formatting context may ask repeatedly without paying for it repeatedly.
    /// </summary>
    internal Size Measure(Box box, MeasureConstraints constraints, LayoutContext context)
    {
        var key = new MeasureKey(
            box,
            constraints.AvailableWidth ?? context.ContainingBlock.Width,
            constraints.AvailableHeight ?? context.ContainingBlock.Height);

        if (_cache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var size = MeasureCore(box, constraints, context);

        _cache[key] = size;
        return size;
    }

    /// <summary>
    /// Produces the geometry of a measured box inside the given border box and applies whatever its
    /// <c>position</c> then asks of it: a relative displacement, or the rule that pins a sticky box.
    /// </summary>
    internal LayoutBox Arrange(Box box, Rect borderBox, LayoutContext context) =>
        Arrange(box, borderBox, context, definiteHeight: false);

    /// <summary>
    /// Produces the geometry of a measured box inside the given border box, told whether the height the
    /// border box carries is definite even though the box declares <c>height: auto</c> - which is the
    /// case for a grid or flex item stretched to its area (CSS Sizing 3 §5.2), where a percentage inside
    /// has to resolve against that stretched height rather than collapse.
    /// </summary>
    internal LayoutBox Arrange(Box box, Rect borderBox, LayoutContext context, Boolean definiteHeight)
    {
        // An anonymous box borrows its parent's style; honouring the positioning it inherits would
        // displace the wrapper a second time, because the parent's own displacement already took it
        // along.
        if (box.IsAnonymous)
        {
            return ArrangeCore(box, borderBox, context, definiteHeight);
        }

        var layout = ArrangeCore(box, borderBox, context, definiteHeight);

        var position = box.Style.Position;

        layout.Position = position.Position;
        layout.ZIndex = position.HasZIndex ? position.ZIndex : 0;

        // A transform (and a motion-path offset transform) changes only how the box is drawn, never
        // where the layout put it, so the matrix is resolved here - once the border box has its size,
        // which a translate percentage, the transform-origin and the offset position are measured
        // against - and handed to the painter. The offset transform is applied before the transform
        // property (CSS Transforms 2 §ctm), so it is the inner factor of the product.
        var transformText = box.Style.TransformText;
        var offsetPath = box.Style.OffsetValue("offset-path");
        var hasTransform = transformText is { Length: > 0 } && !transformText.Equals("none", StringComparison.OrdinalIgnoreCase);
        var hasOffset = offsetPath is { Length: > 0 } && !offsetPath.Equals("none", StringComparison.OrdinalIgnoreCase);

        if (hasTransform || hasOffset)
        {
            var frame = layout.BorderBox;
            var dimensions = box.Style.Dimensions;
            var fontSize = box.Style.FontSize;
            var (originX, originY) = TransformValue.ResolveOrigin(box.Style.TransformOriginText, frame.Width, frame.Height, dimensions, fontSize);

            Affine? matrix = hasTransform && TransformValue.Parse(transformText) is { } transformValue
                ? transformValue.Resolve(frame.Width, frame.Height, dimensions, fontSize)
                : null;

            if (MotionTransform.Compute(box.Style, frame, context.ContainingBlockRect, dimensions, fontSize, originX, originY) is { } motion)
            {
                matrix = matrix is { } m ? m.Then(motion) : motion;
            }

            // Any non-none transform or offset path makes the box a stacking context and a containing
            // block, so it is marked for the painter even when the matrix is an identity (a
            // translate(0,0)): the wrapping is what keeps its out-of-flow descendants inside it.
            layout.Transform = matrix ?? Affine.Identity;
            layout.TransformOrigin = new Offset(frame.X + originX, frame.Y + originY);
        }

        _positioned.Finish(layout, position, context);
        return layout;
    }

    /// <summary>
    /// Records the floats a container's walk placed, for the container above it to absorb when it
    /// establishes no formatting context of its own. A null record takes a stale one back: a walk
    /// that placed no floats must not leave the record an earlier walk at another extent made.
    /// </summary>
    internal void PublishFloats(Box box, FloatTracks? tracks)
    {
        if (tracks is null)
        {
            _floats.Remove(box);
        }
        else
        {
            _floats[box] = tracks;
        }
    }

    /// <summary>Gets the floats a box's own walk placed, or null when it placed none.</summary>
    internal FloatTracks? FloatsOf(Box box) => _floats.TryGetValue(box, out var tracks) ? tracks : null;

    /// <summary>
    /// Lays an out-of-flow box out against its containing block. It is the block context that calls
    /// this, once the in-flow children have been placed, because an <c>absolute</c> or <c>fixed</c>
    /// box takes no part in their stacking.
    /// </summary>
    internal LayoutBox ArrangePositioned(Box box, Offset staticPosition, LayoutContext context) =>
        _positioned.Arrange(box, staticPosition, context);

    /// <summary>Produces the geometry of a measured box inside the given border box.</summary>
    private LayoutBox ArrangeCore(Box box, Rect borderBox, LayoutContext context, Boolean definiteHeight)
    {
        return box.Kind switch
        {
            BoxKind.Flex => _flex.Arrange(box, borderBox, context),
            BoxKind.Grid => _grid.Arrange(box, borderBox, context),
            BoxKind.Masonry => _masonry.Arrange(box, borderBox, context),
            BoxKind.Table => _table.Arrange(box, borderBox, context),
            _ => box.IsReplaced
                ? ArrangeReplaced(box, borderBox, context)
                : _block.Arrange(box, borderBox, context, definiteHeight),
        };
    }

    /// <summary>
    /// Measures the sizes a box's content wants when nothing constrains it: its <c>min-content</c>
    /// and <c>max-content</c> inline sizes. A flex or grid container answers for its items, so an
    /// item with an automatic basis can ask its own intrinsic width before anything is decided.
    /// </summary>
    /// <remarks>
    /// The containing block the measurement descends with is marked as a hypothesis, because that is
    /// what it is: the answer is the width the box would need, so a percentage among the box's own
    /// declarations cannot be resolved against it without the answer being fed back into the question.
    /// </remarks>
    internal IntrinsicSize MeasureIntrinsic(Box box, LayoutContext context)
    {
        context = context.WithHypotheticalBasis();

        // A box that answers the same at every containing block is cached without the basis. The walk
        // that asks about it - a table sizing its tracks, a flex container negotiating its items -
        // asks under several, and every one of them used to be paid for separately even though the
        // answer could not differ.
        if (box.IntrinsicIgnoresBasis)
        {
            if (_intrinsicFree.TryGetValue(box, out var free))
            {
                    VerifyBasisFree(box, context, free, content: false);
                return free;
            }

            var settled = MeasureIntrinsicUncached(box, context, content: false);
            _intrinsicFree[box] = settled;
            return settled;
        }

        var key = new MeasureKey(box, context.ContainingBlock.Width, context.ContainingBlock.Height);

        if (_intrinsic.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var size = MeasureIntrinsicUncached(box, context, content: false);
        _intrinsic[key] = size;
        return size;
    }

    /// <summary>Measures a size no cache held.</summary>
    private IntrinsicSize MeasureIntrinsicUncached(Box box, LayoutContext context, Boolean content) =>
        content
            ? MeasureContentIntrinsicCore(box, context)
            : MeasureIntrinsicCore(box, context);

    /// <summary>
    /// Checks a cached basis-free size against an honest measurement at the containing block in hand.
    /// </summary>
    /// <remarks>
    /// A box wrongly marked as wanting the same size everywhere can only be found out where its size
    /// is about to be reused, so this runs on the hit and not on the measurement - the first
    /// measurement is taken at the basis in hand anyway and cannot disagree with itself. The second
    /// engine is what keeps the honest measurement from polluting this one's caches, and it keeps the
    /// basis in its own keys all the way down, so a wrong flag deep in the subtree surfaces as a
    /// difference here rather than being papered over by the same mistake twice.
    /// </remarks>
    private void VerifyBasisFree(Box box, LayoutContext context, IntrinsicSize cached, Boolean content)
    {
        if (!AssertBasisFree)
        {
            return;
        }

        var reference = _reference ??= new LayoutEngine(_measurer);
        var honest = content
            ? reference.MeasureContentIntrinsic(box, context)
            : reference.MeasureIntrinsic(box, context);

        if (honest.MinContent != cached.MinContent || honest.MaxContent != cached.MaxContent)
        {
            throw new InvalidOperationException(
                $"A {box.Kind} box is marked as wanting the same intrinsic size at every containing " +
                $"block, but it was cached as {cached.MinContent}/{cached.MaxContent} and wants " +
                $"{honest.MinContent}/{honest.MaxContent} at {context.ContainingBlock.Width}.");
        }
    }

    private IntrinsicSize MeasureIntrinsicCore(Box box, LayoutContext context) =>
        Bound(box, box.Kind switch
        {
            BoxKind.Flex => _flex.MeasureIntrinsic(box, context),
            BoxKind.Grid => _grid.MeasureIntrinsic(box, context),
            BoxKind.Masonry => _masonry.MeasureIntrinsic(box, context),
            BoxKind.Table => _table.MeasureIntrinsic(box, context),
            _ => _block.MeasureIntrinsic(box, context),
        }, context);

    private IntrinsicSize MeasureContentIntrinsicCore(Box box, LayoutContext context) =>
        Bound(box, box.Kind switch
        {
            // A flex container's content is what its items want, and the declared width of the
            // container is exactly what the callers of this path are setting aside - so the flex
            // context answers here with the measurement that does not read it.
            BoxKind.Flex => _flex.MeasureContentIntrinsic(box, context),
            BoxKind.Grid => _grid.MeasureIntrinsic(box, context),
            BoxKind.Masonry => _masonry.MeasureIntrinsic(box, context),
            BoxKind.Table => _table.MeasureIntrinsic(box, context),
            _ => _block.MeasureContentIntrinsic(box, context),
        }, context);

    /// <summary>
    /// Clamps an intrinsic inline size by the box's own min-width and max-width.
    /// </summary>
    /// <remarks>
    /// The size a box contributes to whatever holds it is the content-box size of a hypothetical
    /// auto-sized float holding only that box, clamped by its constraints (CSS Sizing 3 §5.2); the same
    /// rule stated for a flex item is that its main-size contribution is "the larger of its outer
    /// max-content size and outer preferred size if that is not auto, clamped by its min/max main size"
    /// (CSS Flexbox 1 §9.9.3). A Codex icon button states a 32px min-width over 22px of content, and
    /// without the clamp every box above it - the dropdown it labels, that dropdown's panel, the menu -
    /// measured 10px narrow, each menu label wrapped one character per line, and the page came out
    /// 621px taller than a browser draws it.
    /// <para>
    /// A percentage resolves against the containing block, so one that is a share of a block that is
    /// itself the thing being asked about is left out: the contribution of a box whose constraint is a
    /// cyclic percentage is worked out as if that constraint were absent (CSS Sizing 3 §5.2.1).
    /// </para>
    /// </remarks>
    private static IntrinsicSize Bound(Box box, IntrinsicSize size, LayoutContext context)
    {
        var style = box.Style;
        var basis = context.ContainingBlock.Width;
        var insets = style.Padding.Resolve(basis).Horizontal + BorderHorizontal(style);
        var isBorderBox = BoxModel.IsBorderBox(style);

        // Both halves of the pair are raised by the floor and cut down by the ceiling: the contribution
        // is the size of that hypothetical float, which is a box the constraints bound like any other -
        // so a 50px run of text in a box that states max-width: 24px contributes 24, not 50.
        var min = Math.Max(size.MinContent, BorderBound(style.MinWidth) ?? 0);
        var max = Math.Max(size.MaxContent, min);

        if (BorderBound(style.MaxWidth) is { } ceiling)
        {
            min = Math.Min(min, ceiling);
            max = Math.Min(max, ceiling);
        }

        return new IntrinsicSize(min, Math.Max(min, max));

        Double? BorderBound(StyleLength length)
        {
            if (!length.HasValue ||
                (length.ReadsContainingBlock && context.BasisIsHypothetical) ||
                (length.Kind == StyleLengthKind.Percent && basis <= 0))
            {
                return null;
            }

            return BoxModel.ContentBound(length, basis, insets, isBorderBox) + insets;
        }
    }

    /// <summary>
    /// The sizes a box's content wants once its own declared width is set aside. It is what the
    /// automatic minimum size of a flex item is built from, which is why the declared width must not
    /// be consulted here.
    /// </summary>
    internal IntrinsicSize MeasureContentIntrinsic(Box box, LayoutContext context)
    {
        // The same hypothesis about the containing block, for the same reason: this is an intrinsic
        // measurement of the content, and a percentage down inside it has no settled block to speak of.
        context = context.WithHypotheticalBasis();

        if (box.IntrinsicIgnoresBasis)
        {
            if (_contentIntrinsicFree.TryGetValue(box, out var free))
            {
                    VerifyBasisFree(box, context, free, content: true);
                return free;
            }

            var settled = MeasureIntrinsicUncached(box, context, content: true);
            _contentIntrinsicFree[box] = settled;
            return settled;
        }

        var key = new MeasureKey(box, context.ContainingBlock.Width, context.ContainingBlock.Height);

        if (_contentIntrinsic.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var size = MeasureIntrinsicUncached(box, context, content: true);
        _contentIntrinsic[key] = size;
        return size;
    }

    private Size MeasureCore(Box box, MeasureConstraints constraints, LayoutContext context)
    {
        return box.Kind switch
        {
            BoxKind.Flex => _flex.Measure(box, constraints, context),
            BoxKind.Grid => _grid.Measure(box, constraints, context),
            BoxKind.Masonry => _masonry.Measure(box, constraints, context),
            BoxKind.Table => _table.Measure(box, constraints, context),
            _ => box.IsReplaced
                ? MeasureReplaced(box, constraints, context)
                : _block.Measure(box, constraints, context),
        };
    }



    /// <summary>
    /// Gets the border-box inline size a content-sized width asks for, or null when the width the box
    /// states is a length rather than one of the content keywords.
    /// </summary>
    /// <remarks>
    /// <c>max-content</c> is the size the content wants on one line and <c>min-content</c> the
    /// narrowest it can be laid out in - both of which are what the intrinsic measurement already
    /// answers - and <c>fit-content</c> is the narrower of the max-content size and the space the
    /// containing block leaves, floored by the min-content size (CSS Sizing 3 §4, §5.2).
    /// </remarks>
    internal Double? ContentSizedWidth(Box box, Double available, LayoutContext context)
    {
        if (!box.Style.Width.IsContentSized)
        {
            return null;
        }

        var intrinsic = MeasureIntrinsic(box, context);

        return box.Style.Width.Kind switch
        {
            StyleLengthKind.MaxContent => intrinsic.MaxContent,
            StyleLengthKind.MinContent => intrinsic.MinContent,
            _ => Math.Max(intrinsic.MinContent, Math.Min(available, intrinsic.MaxContent)),
        };
    }

    /// <summary>
    /// Measures a replaced box - an image. Its used size comes from the intrinsic size and the CSS
    /// constraints; the insets are added around it, because the constraints describe the content.
    /// </summary>
    private static Size MeasureReplaced(Box box, MeasureConstraints constraints, LayoutContext context)
    {
        var style = box.Style;
        var basisWidth = constraints.AvailableWidth ?? context.ContainingBlock.Width;
        var basisHeight = constraints.AvailableHeight ?? context.ContainingBlock.Height;
        var (width, height) = ReplacedSizing.Resolve(box, basisWidth, basisHeight);
        var padding = style.Padding.Resolve(basisWidth);

        return new Size(
            width + padding.Horizontal + BorderHorizontal(style),
            height + padding.Vertical + BorderVertical(style));
    }

    private static LayoutBox ArrangeReplaced(Box box, Rect borderBox, LayoutContext context)
    {
        var style = box.Style;
        var basis = context.ContainingBlock.Width;

        return new LayoutBox(box, box.IsInlineSvg ? LayoutKind.Svg : LayoutKind.Image)
        {
            Margin = BoxModel.UsedMargin(style, basis, borderBox.Width),
            Padding = style.Padding.Resolve(basis),
            Border = new Thickness(
                style.BorderTopWidth,
                style.BorderRightWidth,
                style.BorderBottomWidth,
                style.BorderLeftWidth),
            ImageUrl = box.SourceUrl,
            ImageFit = style.ObjectFit,
            SvgMarkup = box.SvgMarkup,
            BorderBox = borderBox,
            Flow = box.Flow,
        };
    }

    private static Double BorderHorizontal(ComputedStyle style) =>
        style.BorderLeftWidth + style.BorderRightWidth;

    private static Double BorderVertical(ComputedStyle style) =>
        style.BorderTopWidth + style.BorderBottomWidth;

    /// <summary>
    /// The cache key: a box and the basis its size resolves from. Both dimensions are part of it
    /// because a percentage height depends on the containing block's height as much as a percentage
    /// width does on its width.
    /// </summary>
    private readonly struct MeasureKey : IEquatable<MeasureKey>
    {
        private readonly Box _box;
        private readonly Double _width;
        private readonly Double _height;

        public MeasureKey(Box box, Double width, Double height)
        {
            _box = box;
            _width = width;
            _height = height;
        }

        public Boolean Equals(MeasureKey other) =>
            ReferenceEquals(_box, other._box) &&
            _width.Equals(other._width) &&
            _height.Equals(other._height);

        public override Boolean Equals(Object? obj) => obj is MeasureKey other && Equals(other);

        public override Int32 GetHashCode() => HashCode.Combine(
            System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(_box),
            _width,
            _height);
    }
}

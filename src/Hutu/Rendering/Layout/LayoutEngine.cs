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
public sealed class LayoutEngine
{
    private readonly InlineLayout _inline;
    private readonly BlockLayout _block;
    private readonly FlexFormattingContext _flex;
    private readonly GridFormattingContext _grid;
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
    public sealed record LayoutProfile(
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
    private static void ResolveDecorations(LayoutBox box)
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

        foreach (var child in box.Children)
        {
            ResolveDecorations(child);
        }
    }

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
                // its own content box where it does not - an empty item is still marked, the way a
                // browser marks one.
                var column = line is { } standing ? standing.BorderBox : content;

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

                // The edge the content begins at is the one the line starts reading from, which a page
                // that reads right to left puts on the other side - and the marker ends there rather than
                // starting there, since it is the item's text that keeps its edge and the marker that
                // hangs out.
                var start = box.Flow.InlineSign > 0
                    ? box.ContentBox.X - advance
                    : box.ContentBox.Right;

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
    internal LayoutBox Arrange(Box box, Rect borderBox, LayoutContext context)
    {
        // An anonymous box borrows its parent's style; honouring the positioning it inherits would
        // displace the wrapper a second time, because the parent's own displacement already took it
        // along.
        if (box.IsAnonymous)
        {
            return ArrangeCore(box, borderBox, context);
        }

        var layout = ArrangeCore(box, borderBox, context);

        var position = box.Style.Position;

        layout.Position = position.Position;
        layout.ZIndex = position.HasZIndex ? position.ZIndex : 0;

        _positioned.Finish(layout, position, context);
        return layout;
    }

    /// <summary>
    /// Lays an out-of-flow box out against its containing block. It is the block context that calls
    /// this, once the in-flow children have been placed, because an <c>absolute</c> or <c>fixed</c>
    /// box takes no part in their stacking.
    /// </summary>
    internal LayoutBox ArrangePositioned(Box box, Offset staticPosition, LayoutContext context) =>
        _positioned.Arrange(box, staticPosition, context);

    /// <summary>Produces the geometry of a measured box inside the given border box.</summary>
    private LayoutBox ArrangeCore(Box box, Rect borderBox, LayoutContext context)
    {
        return box.Kind switch
        {
            BoxKind.Flex => _flex.Arrange(box, borderBox, context),
            BoxKind.Grid => _grid.Arrange(box, borderBox, context),
            BoxKind.Table => _table.Arrange(box, borderBox, context),
            _ => box.IsReplaced
                ? ArrangeReplaced(box, borderBox, context)
                : _block.Arrange(box, borderBox, context),
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
        box.Kind switch
        {
            BoxKind.Flex => _flex.MeasureIntrinsic(box, context),
            BoxKind.Grid => _grid.MeasureIntrinsic(box, context),
            BoxKind.Table => _table.MeasureIntrinsic(box, context),
            _ => _block.MeasureIntrinsic(box, context),
        };

    private IntrinsicSize MeasureContentIntrinsicCore(Box box, LayoutContext context) =>
        box.Kind switch
        {
            BoxKind.Flex => _flex.MeasureIntrinsic(box, context),
            BoxKind.Grid => _grid.MeasureIntrinsic(box, context),
            BoxKind.Table => _table.MeasureIntrinsic(box, context),
            _ => _block.MeasureContentIntrinsic(box, context),
        };

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
            BoxKind.Table => _table.Measure(box, constraints, context),
            _ => box.IsReplaced
                ? MeasureReplaced(box, constraints, context)
                : _block.Measure(box, constraints, context),
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

        return new LayoutBox(box, LayoutKind.Image)
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

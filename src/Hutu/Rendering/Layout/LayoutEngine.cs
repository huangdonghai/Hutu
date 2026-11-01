using AngleSharp.Css.Dom;
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

    /// <summary>
    /// Whether the document element hands its overflow to the viewport itself, which is the condition
    /// under which the body may hand its own over instead (CSS Overflow 3 §3.3).
    /// </summary>
    private Boolean _rootTakesOverflow;

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
        ResolveMarkers(rootBox, FloatsOf(rootBox.Source));

        // A first line is the same kind of answer, and it comes after for the same reason: the arm is read
        // while the boxes are made, but which fragments the block reaches first is only known here, with
        // the lines put down.
        Hutu.Rendering.Boxes.FirstLine.Mark(rootBox);

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
        ResolveMarkers(rootBox, FloatsOf(rootBox.Source));
        Hutu.Rendering.Boxes.FirstLine.Mark(rootBox);
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
    private void ResolveDecorations(LayoutBox box)
    {
        // The document element's own overflow is answered before the walk, because the body's used
        // overflow depends on it: a root that clips - or that contains anything, which stops the handover
        // just the same (CSS Contain 1 §2) - keeps the body's clipping for the body, and a root that does
        // not hands its own to the viewport, which then also takes the body's and leaves the body itself
        // visible (CSS Overflow 3 §3.3).
        var rootStyle = box.Source.Style;

        _rootTakesOverflow = !rootStyle.ClipsOverflow && !rootStyle.Contain.Stated;

        ResolveDecorations(box, null, null, null);

        // The inset a decorating box asked for is counted along the whole of the text it decorates, which
        // is only known once every run carries the lines reaching it.
        ResolveInsets(box);
    }

    /// <summary>
    /// Gets whether an element is the body the document element stands over, which is the one whose
    /// <c>overflow</c> the viewport may take instead of leaving it to the element.
    /// </summary>
    private static Boolean IsBodyOfRoot(AngleSharp.Dom.IElement element) =>
        element.LocalName.Equals("body", StringComparison.OrdinalIgnoreCase) &&
        element.ParentElement is { } parent && ReferenceEquals(parent, element.Owner?.DocumentElement);

    private void ResolveDecorations(LayoutBox box, List<DecorationSource>? around) =>
        ResolveDecorations(box, around, null, null);

    /// <summary>
    /// Settles the paint-time facts of a box: its radii, what its background is clipped to, whether it
    /// hides what overflows, which decorations its text is drawn under, and where those lines stand.
    /// </summary>
    /// <param name="around">
    /// The lines the boxes above this one asked for and which reach into it - the list this box hands to
    /// its children with its own lines added, and the list a child that lays its own content out is
    /// given nothing of.
    /// </param>
    /// <param name="owner">
    /// The nearest element box above this one, and <paramref name="spoke"/> the box whose element asked
    /// for the last of the lines reaching it. The two are the same box only where this box speaks with
    /// its own element's declaration again - which is what tells a run repeating the element it belongs
    /// to apart from an element repeating the one it sits inside. Two boxes asking for identical lines
    /// are still two decorating boxes, each laying its own line.
    /// </param>
    private void ResolveDecorations(
        LayoutBox box,
        List<DecorationSource>? around,
        LayoutBox? owner,
        LayoutBox? spoke)
    {
        var style = box.Source.Style;

        // A fragment of a broken box keeps the corner curves of the box it came from, so they are
        // measured against that whole box: the piece a radius really ends on is shorter than it, and a
        // radius scaled down to the fragment would round a corner the box does not have
        // (CSS Break 3 §4.7, CSS Backgrounds 3 §5.1).
        var rounded = (box.SlicesBlockStart || box.SlicesBlockEnd) && box.BackgroundOrigin is { } whole
            ? whole
            : box.BorderBox;

        box.Radii = CornerRadii.Resolve(style.BorderRadius, rounded.Width, rounded.Height);
        box.BackgroundClip = style.BackgroundClip;

        // An inline element has no box of its own to clip anything to, and CSS says `overflow` has no
        // effect on one: honouring it would start cutting off the element's own runs.
        // The root's overflow is not its own to honour either - it is fixed to the viewport instead
        // (CSS Overflow 3 §3.3), and the viewport is the window the page is drawn into, not the box the
        // document's own content made, which can be a good deal shorter.
        var isRoot = box.Source.Element is { } source &&
            ReferenceEquals(source, source.Owner?.DocumentElement);

        // The body's overflow is not its own either where the root clips nothing: the viewport takes it
        // instead, and the element it came from keeps the visible value it had before it was handed over
        // (CSS Overflow 3 §3.3). A body that contains anything keeps its overflow to itself - the handover
        // is off the moment the element asks not to be reached into (CSS Contain 1 §2).
        var isBody = _rootTakesOverflow && !style.Contain.Stated &&
            !style.Display.Equals("none", StringComparison.OrdinalIgnoreCase) &&
            box.Source.Element is { } body && IsBodyOfRoot(body);

        box.ClipsOverflow = box.Kind != LayoutKind.Inline && style.ClipsOverflow && !isRoot && !isBody;

        if (box.Kind == LayoutKind.Inline)
        {
            box.Radii = Sliced(box.Radii, box);
        }
        else if (box.SlicesBlockStart || box.SlicesBlockEnd)
        {
            // A block broken across columns keeps its corner curves only where it really ends: the piece
            // it begins on keeps its top corners, the one it ends on its bottom ones, and the edges the
            // break falls on are square.
            box.Radii = new CornerRadii(
                box.SlicesBlockStart ? CornerRadius.Zero : box.Radii.TopLeft,
                box.SlicesBlockStart ? CornerRadius.Zero : box.Radii.TopRight,
                box.SlicesBlockEnd ? CornerRadius.Zero : box.Radii.BottomRight,
                box.SlicesBlockEnd ? CornerRadius.Zero : box.Radii.BottomLeft);
        }

        // A decoration is asked for by an element and drawn across the text inside it, so the line a
        // block asked for runs on through every inline that follows, and a child that asks for none
        // takes none of it away (CSS Text Decoration 4 §2.1). An anonymous box borrows a style that was
        // already paid for by the element it came from, so only an element box can start a context of
        // its own: the line boxes and the runs inside an atomic box borrow that box's style, and they are
        // the text its own decoration is drawn across rather than a fresh context starting over. A ruby
        // annotation is apart in its own right, being set above the base the line under it decorates.
        var element = box.Kind != LayoutKind.Line && !box.Source.IsAnonymous;

        if (element && StandsApart(box) || box.IsRubyAnnotation)
        {
            around = null;
            spoke = null;
        }

        // A run of text asks for the decoration of the element it belongs to even where that element
        // was given no box of its own to say it with - a ruby base and its annotation are laid out as
        // runs - and asks for it once: the box that spoke last is the element the run belongs to only
        // where the run repeats that element's own declaration, so a child asking for the same lines as
        // its container is a decorating box in its own right and adds a second line.
        var lines = element || box.Kind == LayoutKind.Text ? SourceOf(box) : null;
        var own = ReferenceEquals(spoke, owner) ? around?.Count - 1 ?? -1 : -1;

        if (lines is null)
        {
            box.Decorations = around;
        }
        else if (around is null)
        {
            box.Decorations = new List<DecorationSource> { lines };
            own = 0;
            spoke = box;
        }
        else if (around[^1] == lines && ReferenceEquals(spoke, owner))
        {
            box.Decorations = around;
        }
        else
        {
            box.Decorations = [.. around, lines];
            own = box.Decorations.Count - 1;
            spoke = box;
        }

        box.OwnDecoration = own;

        var enclosing = element && box.Kind != LayoutKind.Text ? box : owner;

        foreach (var child in box.Children)
        {
            ResolveDecorations(child, box.Decorations, enclosing, spoke);
        }

        // One line of text is underlined as one thing, however many runs of text it is set out of, so
        // its answer is taken from the runs once they know which boxes decorate them.
        if (box.Kind == LayoutKind.Line)
        {
            SetDecorationLines(box);
        }
    }

    /// <summary>
    /// Gets the lines this box decorates its text with, or null where it asked for none. The colour the
    /// line is drawn in is the one the decorating box settled on: its own <c>text-decoration-color</c>,
    /// or the colour of <em>its</em> text where that says <c>currentcolor</c> - never the colour of the
    /// descendant's glyphs the line happens to run across (CSS Text Decoration 4 §2.3). The record carries
    /// the element that asked, so the text a line runs across can be gathered back into that one
    /// decoration when its ends are trimmed (§2.1, §2.9.1).
    /// </summary>
    private static DecorationSource? SourceOf(LayoutBox box)
    {
        var style = box.Source.Style;

        return style.HasUnderline || style.HasOverline || style.HasLineThrough
            ? new DecorationSource(
                style.HasUnderline,
                style.HasOverline,
                style.HasLineThrough,
                style.Decoration,
                style.DecorationColor ?? ComputedStyle.ToArgb(style.Color),
                style.DecorationThickness,
                style.UnderlineOffset,
                style.DecorationInset)
            {
                Speaker = box.Source,
            }
            : null;
    }

    /// <summary>
    /// Settles where the lines drawn across this line of text stand, and how thick they are. A decorating
    /// box uses one position and one thickness on each line for its underlines and overlines (CSS Text
    /// Decoration 4 §2.9), coming at them from the whole of the text it decorates on that line - its
    /// considered text - by averaging the ideal offsets those faces ask for and then standing the line on
    /// the lowest alphabetic baseline the considered text reaches (§2.5). A run standing out of the row its
    /// line is drawn on is counted as the face its parent is set in and as standing where its parent does
    /// for every box above it, which is what keeps a superscript from throwing the line off-kilter, and
    /// keeps its own row for the decoration its own element asked for. A line-through answers
    /// none of this: §2.5 has it recomputed wherever the font size changes, so that the text stays crossed
    /// out.
    /// </summary>
    private void SetDecorationLines(LayoutBox line)
    {
        var runs = new List<ConsideredRun>();
        ConsideredRuns(line, null, null, runs);

        // One answer per decorating box, told apart by the list of lines it reaches and the place in it:
        // a run's own lines and its container's are averaged separately, since each box lays its own.
        var ideals = new Dictionary<(IReadOnlyList<DecorationSource>, Int32), LineIdeal>();

        foreach (var run in runs)
        {
            for (var asked = 0; asked < run.Asked.Count; asked++)
            {
                if (!run.Shifted(asked))
                {
                    Ideal(ideals, line, run, asked).Stand(run.Baseline);
                }
            }
        }

        foreach (var run in runs)
        {
            for (var asked = 0; asked < run.Asked.Count; asked++)
            {
                var ideal = Ideal(ideals, line, run, asked);
                var shifted = run.Shifted(asked);
                var face = _measurer.GetMetrics(run.Box.Font);

                ideal.Add(
                    shifted ? _measurer.GetMetrics(run.Parent) : face,
                    shifted ? face : (FontMetrics?)null,
                    ideal.Stood - run.Baseline);
            }
        }

        foreach (var run in runs)
        {
            var placements = new List<DecorationPlacement>(run.Asked.Count);

            for (var asked = 0; asked < run.Asked.Count; asked++)
            {
                var ideal = Ideal(ideals, line, run, asked);

                placements.Add(ideal.Place(ideal.Stood - run.Baseline));
            }

            run.Box.DecorationPlacements = placements;
        }
    }

    /// <summary>
    /// Gets the record the one decorating box at this place in this run's lines is being averaged into,
    /// opening a fresh one where it has not been asked for yet. The line the box's text stands on starts
    /// at the row the line box itself was placed on, which is where a run lifted out of the row is
    /// answered from.
    /// </summary>
    private static LineIdeal Ideal(
        Dictionary<(IReadOnlyList<DecorationSource>, Int32), LineIdeal> ideals,
        LayoutBox line,
        ConsideredRun run,
        Int32 asked) =>
        ideals.TryGetValue((run.Asked, asked), out var ideal)
            ? ideal
            : ideals[(run.Asked, asked)] = new LineIdeal(Along(line));

    /// <summary>
    /// Sums what one decorating box's line takes from each run of the text it decorates: the ideal offsets
    /// and the thicknesses averaged over the runs, and the overline lifted to the highest edge they reach.
    /// </summary>
    /// <param name="stood">
    /// The row the line is drawn on, which is the lowest alphabetic baseline the runs that stand on their
    /// line's own reach, or the row the line box was placed on where none of them does.
    /// </param>
    private sealed class LineIdeal(Double stood)
    {
        private Boolean _settled;
        private Double _stood = stood;
        private Int32 _count;
        private Double _underline;
        private Double _underlineThickness;
        private Double _overline;
        private Double _overlineThickness;

        /// <summary>Takes the line down to this run's baseline, where it stands lower than any before it.</summary>
        internal void Stand(Double baseline)
        {
            if (!_settled || baseline > _stood)
            {
                _stood = baseline;
            }

            _settled = true;
        }

        /// <summary>Gets the row the line is drawn on.</summary>
        internal Double Stood => _stood;

        /// <param name="shift">
        /// How far the run's own baseline sits below the row the line stands on: an edge taken from the run
        /// is that much lower than the same edge measured from the line.
        /// </param>
        /// <param name="lifted">
        /// The run's own face where it stands out of the row its line is drawn on, or null where the face
        /// it is answered from is the one it is set in: an overline goes to the edge the run's own em box
        /// reaches, wherever it stands, while its underline is the parent's.
        /// </param>
        internal void Add(FontMetrics face, FontMetrics? lifted, Double shift)
        {
            _count++;
            _underline += Math.Abs(face.UnderlineOffset);
            _underlineThickness += face.UnderlineThickness;

            var over = lifted ?? face;

            _overlineThickness += over.OverlineThickness > 0
                ? over.OverlineThickness
                : over.UnderlineThickness;

            // A face that will not say where its overline goes is answered with the top of its own em box,
            // which is the only other height the line could be taken from.
            _overline = Math.Max(
                _overline,
                shift + (over.OverlineOffset > 0 ? Math.Abs(over.OverlineOffset) : over.Ascent));
        }

        internal DecorationPlacement Place(Double shift) =>
            new(
                shift,
                _underline / _count,
                _underlineThickness / _count,
                _overline,
                _overlineThickness / _count);
    }

    /// <summary>
    /// Gets where a box's baseline stands along the axis its decorations are offset from: down the page
    /// for a line, across it for a column.
    /// </summary>
    private static Double Along(LayoutBox box) =>
        (box.Vertical ? box.BorderBox.X : box.BorderBox.Y) + box.Baseline;

    /// <summary>
    /// Collects the runs of text whose lines this line settles: the text it carries at any depth, but not
    /// what an atomic box or a ruby annotation sets inside itself, neither of which takes part in the line
    /// the decoration runs across (CSS Text Decoration 4 §2.5).
    /// </summary>
    /// <param name="face">
    /// The font the text below is set in, and <paramref name="parent"/> the font of the element that
    /// element sits inside. The pair is stepped down whenever a box changes the font on the way in,
    /// because §2.5 answers for a run lifted out of its line's row from the face its parent is set in.
    /// </param>
    private void ConsideredRuns(LayoutBox box, FontStyle? face, FontStyle? parent, List<ConsideredRun> runs)
    {
        var own = box.Source.Style.GetFont();

        if (box.Kind != LayoutKind.Line && !box.Source.IsAnonymous &&
            (box.IsRubyAnnotation || StandsApart(box)))
        {
            return;
        }

        if (face is null || !face.Equals(own))
        {
            parent = face;
            face = own;
        }

        if (box.Kind == LayoutKind.Text && box.Decorations is { Count: > 0 } asked)
        {
            runs.Add(new ConsideredRun(
                box,
                asked,
                parent ?? box.Font,
                Along(box),
                box.OwnDecoration,
                !box.Source.Style.VerticalAlign.Equals("baseline", StringComparison.OrdinalIgnoreCase)));
        }

        foreach (var child in box.Children)
        {
            ConsideredRuns(child, face, parent, runs);
        }
    }

    /// <summary>
    /// One run of text a line settles its decorations from: where its own baseline stands, the face its
    /// parent is set in, which of the lines reaching it its own element asked for, and whether it stands
    /// out of the row its line is drawn on.
    /// </summary>
    private sealed record ConsideredRun(
        LayoutBox Box,
        IReadOnlyList<DecorationSource> Asked,
        FontStyle Parent,
        Double Baseline,
        Int32 Own,
        Boolean Raised)
    {
        /// <summary>
        /// Gets whether this run is answered from its parent for the lines asked for at this place: a run
        /// lifted out of its line's row keeps its own row only for the decoration its own element asked
        /// for, and stands where its parent does for every box above it (CSS Text Decoration 4 §2.5).
        /// </summary>
        internal Boolean Shifted(Int32 asked) => Raised && asked != Own;
    }

    /// <summary>
    /// Turns what each decorating box asked its lines to be pulled back by into a distance for every run
    /// of the text it decorates. The inset is counted along the whole of the decoration one box draws: a
    /// line that breaks is trimmed at its two outer ends and the break between the fragments is left
    /// alone, which is what keeps a broken rule reading as one rule rather than a row of short ones. Where
    /// the box was told to repeat its decoration on every line - <c>box-decoration-break: clone</c> - each
    /// fragment is trimmed at both of its own ends, and a share is taken of that fragment rather than of
    /// the whole (CSS Text Decoration 4 §2.9.1).
    /// </summary>
    private void ResolveInsets(LayoutBox root)
    {
        var decorated = new Dictionary<Object, List<TrimmedRun>>();

        CollectTrimmed(root, null, decorated);

        foreach (var runs in decorated.Values)
        {
            Trim(runs);
        }
    }

    /// <summary>
    /// Gathers the runs each decorating box asked to have its ends pulled back from, in the order the text
    /// is read, together with how much inline room the run takes.
    /// </summary>
    private static void CollectTrimmed(
        LayoutBox box,
        LayoutBox? line,
        Dictionary<Object, List<TrimmedRun>> into)
    {
        var standing = box.Kind == LayoutKind.Line ? box : line;

        if (box.Kind == LayoutKind.Text && box.Decorations is { } asked)
        {
            for (var at = 0; at < asked.Count; at++)
            {
                if (asked[at].Insets is { } insets && asked[at].Speaker is { } element)
                {
                    if (!into.TryGetValue(element, out var runs))
                    {
                        runs = into[element] = new List<TrimmedRun>();
                    }

                    runs.Add(new TrimmedRun(
                        box,
                        at,
                        box.Vertical ? box.BorderBox.Height : box.BorderBox.Width,
                        standing,
                        insets));
                }
            }
        }

        foreach (var child in box.Children)
        {
            CollectTrimmed(child, standing, into);
        }
    }

    /// <summary>Carries one decorating box's inset out along the text it decorates.</summary>
    private static void Trim(List<TrimmedRun> runs)
    {
        var insets = runs[0].Insets;

        foreach (var segment in Segments(runs, insets.PerFragment))
        {
            var total = 0.0;
            var thickness = 1.0;

            foreach (var run in segment)
            {
                total += run.Size;
                thickness = Math.Max(thickness, run.Thickness);
            }

            var (start, end) = Distances(insets, total, thickness);
            var pulls = new Double[segment.Count];
            var pushes = new Double[segment.Count];

            // One distance shared out along the stretch: the runs take what is left of it in the order
            // they are read, and a run the whole distance falls across draws nothing. A negative distance
            // is an extension and belongs to the run at that end whole, since it moves the endpoint
            // outward rather than eating the line away.
            var ahead = start;

            for (var at = 0; at < segment.Count; at++)
            {
                var size = segment[at].Size;
                pulls[at] = ahead < 0 ? ahead : Math.Max(0, Math.Min(ahead, size));
                ahead -= pulls[at];
            }

            var behind = end;

            for (var at = segment.Count - 1; at >= 0; at--)
            {
                var size = segment[at].Size;
                pushes[at] = behind < 0 ? behind : Math.Max(0, Math.Min(behind, size - pulls[at]));
                behind -= pushes[at];
            }

            for (var at = 0; at < segment.Count; at++)
            {
                segment[at].Apply(pulls[at], pushes[at]);
            }
        }
    }

    /// <summary>
    /// Splits the text one box decorates into the stretches its inset is counted across: every fragment of
    /// a box that repeats its decoration on each line stands on its own, and a box that slices its
    /// decoration is one stretch however many lines it reaches.
    /// </summary>
    private static List<List<TrimmedRun>> Segments(List<TrimmedRun> runs, Boolean perFragment)
    {
        var segments = new List<List<TrimmedRun>>();

        foreach (var run in runs)
        {
            if (segments.Count == 0 ||
                (perFragment && !ReferenceEquals(segments[^1][0].Line, run.Line)))
            {
                segments.Add(new List<TrimmedRun>());
            }

            segments[^1].Add(run);
        }

        return segments;
    }

    /// <summary>
    /// Gets the two distances a box asked its line to be pulled back by, once the inline size a share is
    /// taken of is known. <c>auto</c> takes the width of the line itself, which is enough to keep two
    /// boxes set side by side from reading as one rule - the answer §2.9.1 leaves to the engine.
    /// </summary>
    private static (Double Start, Double End) Distances(DecorationInsets insets, Double total, Double thickness) =>
        insets.Auto
            ? (thickness, thickness)
            : (insets.Start ?? (insets.StartShare ?? 0) * total,
               insets.End ?? (insets.EndShare ?? 0) * total);

    /// <summary>
    /// One run of text whose decoration has ends to pull in, with the room it takes along the line and
    /// how far each end came to standing.
    /// </summary>
    private sealed record TrimmedRun(
        LayoutBox Box,
        Int32 At,
        Double Size,
        LayoutBox? Line,
        DecorationInsets Insets)
    {
        internal Double Thickness =>
            Box.DecorationPlacements is { } placed && At < placed.Count
                ? Math.Max(1, placed[At].UnderlineThickness)
                : 1;

        /// <summary>Says to the run where the ends of this one line stand.</summary>
        internal void Apply(Double start, Double end)
        {
            if (Box.DecorationPlacements is not { } placed || At >= placed.Count ||
                (start == 0 && end == 0))
            {
                return;
            }

            placed[At] = placed[At] with { TrimStart = start, TrimEnd = end };
        }
    }

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
            or BoxKind.Table or BoxKind.RubyText;

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
    private void ResolveMarkers(LayoutBox box, FloatTracks? around)
    {
        var source = box.Source;

        // Only the item's own box is given a place for its marker: a line box borrows the style and the
        // element of the item it belongs to, and marking it as well would draw the bullet once per line.
        // An item that is an atomic inline-level box is a block container too, and so is a Block here.
        if (box.Kind == LayoutKind.Block &&
            source.Marker is { Length: > 0 } text &&
            source.MarkerPosition == MarkerPosition.Outside)
        {
            // The marker is drawn in the font its own rules settled, which is why the measure, the row it
            // stands on and the painting all ask for that style rather than the item's. Its spacing counts
            // toward the room it takes the same way a run of the document's own text is measured - the
            // width the face gives the glyphs, plus what the box asks between them.
            var marked = source.MarkerStyle ?? source.Style;
            var font = marked.GetFont();
            var metrics = _measurer.GetMetrics(font);
            var advance = _measurer.MeasureWidth(text, font) +
                marked.LetterSpacing * (box.Vertical ? text.Length : TextClusters.Count(text, 0, text.Length)) +
                marked.WordSpacing * TextChunker.WordSeparators(text, 0, text.Length);
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

                // CSS 2.1 §12.5.1 fixes an outside marker to the principal box's border edge rather than to
                // the content inside its padding, and a page that reads right to left puts that edge on the
                // other side - the marker ends there rather than starting there, since it is the item's text
                // that keeps its edge and the marker that hangs out.
                var edge = box.Flow.InlineSign > 0 ? box.BorderBox.X : box.BorderBox.Right;

                // No line begins inside a float, and the marker hangs at the head of its line: an item that
                // stands beside one puts its marker in the room the float leaves, which is where the same
                // marker stands for an item that was simply given that much margin (CSS 2.1 §12.5.1 read
                // with §9.5, the way Chrome lays it). A column asks for nothing of the kind, because a
                // float of either side cuts a column's block extent and not the head a marker hangs at.
                //
                // Two things about that room are measured against the browser rather than assumed, both of
                // them from the gecko 428810 family. What moves the marker is how far the float reaches past
                // where the item's text would begin, and the marker is carried by that distance from the edge
                // it is fixed to - the border edge, not the content edge - so an item with a border and
                // padding moves its marker by the float's reach less its own insets, not to the float's edge.
                // And a float the item holds itself narrows the item's lines but is not a float the item
                // stands beside, so it leaves the marker where the item's own edge puts it. A row that reads
                // right to left is answered the same way from its other edge, since there the room a float
                // leaves is on the item's inline-end side.
                if (around is { } tracks)
                {
                    var span = tracks.LineSpanAt(
                        standing - metrics.Ascent,
                        metrics.Ascent + metrics.Descent,
                        box.Source);

                    edge = box.Flow.InlineSign > 0
                        ? box.BorderBox.X + Math.Max(0, span.Left - content.X)
                        : box.BorderBox.Right - Math.Max(0, content.Right - span.Right);
                }

                var start = box.Flow.InlineSign > 0 ? edge - advance : edge;

                box.MarkerBounds = new Rect(start, standing - metrics.Ascent, advance, metrics.Ascent + metrics.Descent);
                box.MarkerBaseline = metrics.Ascent;
            }
        }

        foreach (var child in box.Children)
        {
            ResolveMarkers(child, FloatsOf(box.Source) ?? around);
        }
    }

    /// <summary>
    /// Gets the first line the box reaches, searching its own lines before those of anything nested in
    /// it - which is the line a marker is drawn level with.
    /// </summary>
    internal static LayoutBox? FirstLine(LayoutBox box)
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
    /// Gets the last line box in the box's normal flow, searching its own last children before those
    /// nested inside them, which is the line an inline-block's baseline is read from (CSS 2.1 §10.8.1).
    /// A box that left the flow - or a float, which takes no place in the lines - carries no line of
    /// this box with it, so its subtree is passed by.
    /// </summary>
    internal static LayoutBox? LastLine(LayoutBox box)
    {
        if (box.Kind == LayoutKind.Line)
        {
            return box;
        }

        for (var index = box.Children.Count - 1; index >= 0; index--)
        {
            var child = box.Children[index];

            if (child.IsOutOfFlow || child.Source.Style.Float != FloatSide.None)
            {
                continue;
            }

            if (LastLine(child) is { } found)
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
    /// <param name="lines">
    /// The lines of the box's own inline content this fragment takes, where the box is a block broken
    /// across its parent's columns (CSS Multicol 3 §3.1). Null takes the whole of its content.
    /// </param>
    /// <param name="displacement">
    /// How far a relative box stands off the place the flow gave it, where whoever holds the box worked
    /// that out itself instead of leaving it to the containing block handed down. A cell of a table is
    /// the case: its offset is a share of the height the row was <em>stated</em>, and the height the grid
    /// happened to make it says nothing about that.
    /// </param>
    internal LayoutBox Arrange(
        Box box,
        Rect borderBox,
        LayoutContext context,
        Boolean definiteHeight,
        LineRange? lines = null,
        FragmentSlice? slice = null,
        InlineStart? start = null,
        Offset? displacement = null)
    {
        // An anonymous box borrows its parent's style; honouring the positioning it inherits would
        // displace the wrapper a second time, because the parent's own displacement already took it
        // along.
        if (box.IsAnonymous)
        {
            return ArrangeCore(box, borderBox, context, definiteHeight, lines, slice, start);
        }

        var position = box.Style.Position;
        var offset = Offset.Zero;

        // A relative box keeps the place it had in flow: its own box and everything inside it are laid
        // out where the flow put them, then drawn where the offset says. Taking the offset in before the
        // content is arranged keeps every descendant - and every transform or motion path measured
        // against the containing block - in the coordinates the page is finally drawn in, rather than
        // leaving a subtree whose boxes were moved after its matrices were resolved.
        if (position.Position == PositionMode.Relative)
        {
            // Whoever holds the box may have settled the distance already - a table does for its cells,
            // whose share is of the height the row stated rather than the one the grid made it - and that
            // answer is taken in place of this one, never added to it.
            offset = displacement ??
                PositionedLayout.RelativeOffset(position, context.ContainingBlock, box.Flow);

            borderBox = new Rect(borderBox.X + offset.X, borderBox.Y + offset.Y, borderBox.Width, borderBox.Height);
        }

        var layout = ArrangeCore(box, borderBox, context, definiteHeight, lines, slice, start);

        layout.Position = position.Position;

        // The move is noted on the box as well as taken into its rectangle, because a container that
        // stacks or aligns what it holds asks where that content stood in the flow - and reading the
        // displaced place would count the move twice.
        layout.RelativeDisplacement = offset;

        layout.ZIndex = position.HasZIndex ? position.ZIndex : 0;

        // A z-index applies to a flex or grid item even while the item stays in the flow, where it makes
        // the item a stacking context of its own that paints at its stated level (CSS Flexbox 1 §5.4,
        // CSS Grid 1 §9). Every other in-flow box needs a position for the level to mean anything.
        layout.HasStackingLevel = box.IsItem && position.HasZIndex;
        layout.EstablishesStackingContext = box.Style.EstablishesStackingContext || layout.HasStackingLevel;
        layout.Opacity = box.Style.Opacity;
        layout.MixBlendMode = box.Style.MixBlendMode;

        // A transform (and a motion-path offset transform) changes only how the box is drawn, never
        // where the layout put it, so the matrix is resolved here - once the border box has its size,
        // which a translate percentage, the transform-origin and the offset position are measured
        // against - and handed to the painter. The offset transform is applied before the transform
        // property (CSS Transforms 2 §ctm), so it is the inner factor of the product.
        var transformText = box.Style.TransformText;
        var translateText = box.Style.TranslateText;
        var rotateText = box.Style.RotateText;
        var scaleText = box.Style.ScaleText;
        var offsetPath = box.Style.OffsetValue("offset-path");
        var hasTransform = Stated(transformText) || Stated(translateText) || Stated(rotateText) || Stated(scaleText);
        var hasOffset = Stated(offsetPath);

        if (hasTransform || hasOffset)
        {
            var frame = layout.BorderBox;
            var dimensions = box.Style.Dimensions;
            var fontSize = box.Style.FontSize;

            // A transform's lengths are computed first and grown after, so the fold asks for the size the
            // sheet states and the scale for the size the box is drawn at - the two numbers a zoom puts
            // apart, and the one the painter's own font size is not.
            var fold = box.Style.FoldingFontSize;
            var zoom = box.Style.Zoom;

            // transform-box names the box the origin is measured from: the content box when the page
            // asks for it, the border box otherwise (CSS Transforms 1 §transform-box).
            var reference = box.Style.TransformsFromContentBox ? layout.ContentBox : frame;
            var (originX, originY) = TransformValue.ResolveOrigin(
                box.Style.TransformOriginText, reference.Width, reference.Height, dimensions, fold, zoom);
            var originZ = TransformValue.ResolveOriginZ(
                box.Style.TransformOriginText, reference.Width, reference.Height, dimensions, fold, zoom);
            var originLeft = reference.X + originX;
            var originTop = reference.Y + originY;

            // The individual properties and the transform list, in the order CSS Transforms 2 §individual-transforms
            // asks for them: move, turn, resize, then transform.
            var own = TransformValue.Compose(
                translateText,
                rotateText,
                scaleText,
                Stated(transformText) ? TransformValue.Parse(transformText) : null,
                frame.Width,
                frame.Height,
                dimensions,
                fold,
                zoom);

            // The box's own matrix as a 4×4 of the page, and the perspective its parent states. A 3D
            // rendering context composes the first through every box above it, and the second applies to
            // the boxes inside the one that declared it.
            layout.OwnTransform4 = Matrix4.Translation(originLeft, originTop, originZ)
                .Then(own)
                .Then(Matrix4.Translation(-originLeft, -originTop, -originZ));
            layout.ParentView4 = context.Perspective is { } parentView ? ViewOf(parentView) : Matrix4.Identity;

            Affine matrix;

            if (context.Perspective is { } perspective || originZ != 0)
            {
                // The parent's perspective stands between it and this box: the eye is at the perspective
                // origin, and the box's own matrix is carried through it. The result is a homography of
                // the page, which is turned back to the point the painter draws a matrix about. A turn
                // that happens away from the plane's own z is carried the same way, because the move it
                // makes depends on where along z the box is cut.
                var view = layout.ParentView4;

                var page = view
                    .Then(layout.OwnTransform4)
                    .Project();

                matrix = new Affine(1, 0, 0, 1, -originLeft, -originTop)
                    .Then(page)
                    .Then(new Affine(1, 0, 0, 1, originLeft, originTop));
            }
            else
            {
                matrix = own.Project();
            }

            if (MotionTransform.Compute(box.Style, frame, context, dimensions, fontSize, originX, originY) is { } motion)
            {
                matrix = matrix.Then(motion);
            }

            // Any non-none transform or offset path makes the box a stacking context and a containing
            // block, so it is marked for the painter even when the matrix is an identity (a
            // translate(0,0)): the wrapping is what keeps its out-of-flow descendants inside it.
            layout.Transform = matrix;
            layout.TransformOrigin = new Offset(originLeft - frame.X, originTop - frame.Y);

            // A plane turned away from the viewer is seen from behind, which is what a box with
            // backface-visibility: hidden is not drawn at all for. A matrix that cannot be undone has no
            // place to draw the box in either.
            layout.FacesAway = own.Facing < 0;
            layout.SingularTransform = own.IsSingular;
        }

        _positioned.Finish(layout, position, context);
        return layout;
    }

    /// <summary>Gets whether a transform-ish property states anything but <c>none</c>.</summary>
    private static Boolean Stated(String? text) =>
        text is { Length: > 0 } value && !value.Equals("none", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Gets the perspective a box's children are seen through as a 4×4 of the page: the eye stands at the
    /// perspective origin, and every point inside is carried through it (CSS Transforms 2
    /// §perspective-property).
    /// </summary>
    private static Matrix4 ViewOf(PerspectiveView view) =>
        Matrix4.Translation(view.OriginX, view.OriginY, 0)
            .Then(Matrix4.Perspective(view.Distance))
            .Then(Matrix4.Translation(-view.OriginX, -view.OriginY, 0));

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
    /// Gets the grid area an out-of-flow box stands in, where a grid container is the frame it anchors
    /// to and the box names the lines of an area (CSS Grid 2 §10.2, measured: grid-item-oof-013).
    /// </summary>
    private readonly Dictionary<Box, Rect> _absoluteAreas = new();

    internal void SetAbsoluteArea(Box box, Rect area) => _absoluteAreas[box] = area;

    internal Rect? AbsoluteAreaOf(Box box) => _absoluteAreas.TryGetValue(box, out var area) ? area : null;

    /// <summary>
    /// Lays an out-of-flow box out against its containing block. It is the block context that calls
    /// this, once the in-flow children have been placed, because an <c>absolute</c> or <c>fixed</c>
    /// box takes no part in their stacking.
    /// </summary>
    internal LayoutBox ArrangePositioned(
        Box box,
        Offset staticPosition,
        LayoutContext context,
        StaticStanding? standing = null,
        StaticLine? line = null) =>
        _positioned.Arrange(box, staticPosition, context, standing, line);

    /// <summary>
    /// Gets the windows a fragmentainer of the given room cuts a wrapping row flex into: the lines it
    /// holds and where each window begins along the container's block axis (CSS Flexbox 1 §6).
    /// </summary>
    internal IReadOnlyList<(Double Offset, Double Height, Boolean Cut)> FlexWindows(
        Box box,
        Rect borderBox,
        LayoutContext context,
        Double room) =>
        _flex.Windows(box, borderBox, context, room);

    /// <summary>
    /// Lays one window of a fragmented flex container out. A fragmented flex container is a flex
    /// container of the window's own size, carrying the items the window falls over, so the room the
    /// window has left is shared among those items alone (CSS Flexbox 1 §6).
    /// </summary>
    internal LayoutBox ArrangeFlexFragment(
        Box box,
        Rect borderBox,
        LayoutContext context,
        Rect whole,
        Double offset,
        Double height,
        FragmentSlice? slice) =>
        _flex.ArrangeFragment(box, borderBox, context, whole, offset, height, slice);

    /// <summary>Produces the geometry of a measured box inside the given border box.</summary>
    private LayoutBox ArrangeCore(
        Box box,
        Rect borderBox,
        LayoutContext context,
        Boolean definiteHeight,
        LineRange? lines,
        FragmentSlice? slice,
        InlineStart? start)
    {
        return box.Kind switch
        {
            BoxKind.Flex => _flex.Arrange(box, borderBox, context, slice),
            BoxKind.Grid => _grid.Arrange(box, borderBox, context),
            BoxKind.Masonry => _masonry.Arrange(box, borderBox, context),
            BoxKind.Table => _table.Arrange(box, borderBox, context, slice),
            _ => box.IsReplaced
                ? ArrangeReplaced(box, borderBox, context)
                : _block.Arrange(box, borderBox, context, definiteHeight, lines, slice, start),
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
            if (length.IsContentSized)
            {
                // The keyword names the box's own content size, so the bound is the measurement just
                // taken rather than a length: `min-width: max-content` floors the contribution at the
                // box's max-content size, which is how a child's constraint widens the min-content of
                // the box that holds it (CSS Sizing 3 §5.2).
                return length.Kind switch
                {
                    StyleLengthKind.MinContent => size.MinContent,
                    StyleLengthKind.MaxContent => size.MaxContent,
                    _ => Math.Clamp(basis + insets, size.MinContent, size.MaxContent),
                };
            }

            if (!length.HasValue ||
                (length.ReadsContainingBlock && context.BasisIsHypothetical) ||
                (length.ReadsContainingBlock && basis <= 0))
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

    /// <summary>
    /// Gets how far a box's block-level content stacks, with the box's own declared block size set
    /// aside - the block-axis counterpart of the content intrinsic size, which is what the automatic
    /// minimum size of a flex item asks when its main axis is the block one (CSS Flexbox 1 §4.5).
    /// </summary>
    internal Double MeasureBlockContentHeight(Box box, Double inlineExtent, LayoutContext context) =>
        _block.MeasureBlockChildren(box, inlineExtent, 0, context);

    /// <summary>
    /// Measures what a box's content wants, with no min or max of the box's own applied - the size a
    /// content keyword on that same box asks about, which would otherwise answer itself. A caller that
    /// needs the box bounded asks <see cref="MeasureContentIntrinsic"/> instead.
    /// </summary>
    internal IntrinsicSize MeasureContentIntrinsicUnbounded(Box box, LayoutContext context)
    {
        context = context.WithHypotheticalBasis();

        return box.Kind switch
        {
            BoxKind.Flex => _flex.MeasureContentIntrinsic(box, context),
            BoxKind.Grid => _grid.MeasureIntrinsic(box, context),
            BoxKind.Masonry => _masonry.MeasureIntrinsic(box, context),
            BoxKind.Table => _table.MeasureIntrinsic(box, context),
            _ => _block.MeasureContentIntrinsic(box, context),
        };
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

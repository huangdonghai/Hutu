using AngleSharp.Css.Dom;
using Hutu.Rendering.Boxes;
using System;
using System.Collections.Generic;

namespace Hutu.Rendering.Layout;

internal sealed partial class BlockLayout
{
    /// <summary>A spanner measured at full width: its border box and the margins it presents.</summary>
    private readonly record struct SpannedBox(Size Size, Thickness Margin);

    /// <summary>
    /// One stretch of a multi-column box: a run of children the columns carry, or the single box that
    /// spans their row (CSS Multicol 3 §3.2).
    /// </summary>
    private readonly record struct MulticolSection(Box? Spanner, List<Box> Members);

    /// <summary>
    /// Splits a multi-column box into the runs of content its columns carry and the spanners that stand
    /// between them. A spanner ends the run above it and begins the one below, and it is a column's
    /// child no more than the row of columns is (CSS Multicol 3 §3.2).
    /// </summary>
    private static List<MulticolSection> MulticolSections(Box box)
    {
        var sections = new List<MulticolSection>();
        var run = new List<Box>();

        foreach (var child in box.Children)
        {
            if (!child.IsBlockLevel || PositionedLayout.IsOutOfFlow(child))
            {
                continue;
            }

            if (!child.Style.SpansColumns)
            {
                run.Add(child);
                continue;
            }

            if (run.Count > 0)
            {
                sections.Add(new MulticolSection(null, run));
                run = new List<Box>();
            }

            sections.Add(new MulticolSection(child, new List<Box>()));
        }

        if (run.Count > 0)
        {
            sections.Add(new MulticolSection(null, run));
        }

        return sections;
    }

    /// <summary>
    /// Measures a multi-column box that holds a spanner: every run of content balances over its own row
    /// of columns, and each spanner is measured across the whole content width. The box's content comes
    /// to the height of the rows stacked (CSS Multicol 3 §3.2).
    /// </summary>
    private Double MeasureMulticolSections(
        Box box,
        List<MulticolSection> sections,
        Double share,
        Double fullWidth,
        Double declaredHeight,
        Boolean heightStated,
        Int32 columns,
        LayoutContext context)
    {
        var total = 0.0;

        foreach (var section in sections)
        {
            if (section.Spanner is { } spanner)
            {
                var spanned = MeasureSpanner(spanner, box, fullWidth, declaredHeight, context);
                total += spanned.Size.Height + spanned.Margin.Top + spanned.Margin.Bottom;
                continue;
            }

            var children = MulticolChildren(box, share, declaredHeight, declaredHeight, columns, context, EffectiveMembers(section.Members, share));
            total += MulticolExtent(box.Style.Multi, columns, heightStated ? declaredHeight : null, children);
        }

        return total;
    }

    /// <summary>
    /// Measures one spanner across the container's own content width. Its margins are the ones it
    /// presents to the stack, which is what keeps it the same distance from the row above and the row
    /// below however those rows' children ended.
    /// </summary>
    private SpannedBox MeasureSpanner(
        Box spanner,
        Box container,
        Double fullWidth,
        Double declaredHeight,
        LayoutContext context)
    {
        var spanContext = context.ForContainingBlock(new Size(fullWidth, declaredHeight));
        var size = _engine.Measure(spanner, MeasureConstraints.FitWidth(fullWidth), spanContext);
        var used = BoxModel.UsedMargin(spanner.Style, fullWidth, size.Width);
        var margin = new Thickness(
            CollapsedStart(spanner, container.Flow, fullWidth),
            used.Right,
            CollapsedEnd(spanner, container.Flow, fullWidth),
            used.Left);

        return new SpannedBox(size, margin);
    }

    /// <summary>
    /// Gets the extent the columns are shared at: the height the box states for a fill, the whole stack
    /// where it states none (there is nothing to fill to, so the first column takes it all), or the
    /// least extent the children balance into (CSS Multicol 3 §3.4).
    /// </summary>
    private static Double MulticolExtent(
        MulticolStyle multi, Int32 columns, Double? declaredHeight, List<MulticolChild> children)
    {
        if (multi.Fills)
        {
            if (declaredHeight is { } declared)
            {
                return declared;
            }

            // No height to fill to: the stack runs down one column as far as it will go, and only a
            // forced column break starts another. The box then comes to the tallest of those columns -
            // what the runs between the breaks came to - rather than to the length of the whole stack,
            // which is what left a container broken in two twice as tall as its content.
            var stacked = new List<FragmentChild>(children.Count);
            var total = 0.0;

            foreach (var child in children)
            {
                stacked.Add(child.Fragment);
                total += child.Fragment.Height;
            }

            var unbounded = new Double[Math.Max(1, children.Count)];
            Array.Fill(unbounded, total);

            var counts = Fragmentainer.Fill(stacked, unbounded);
            var tallest = 0.0;
            var from = 0;

            foreach (var count in counts)
            {
                var column = 0.0;

                for (var index = 0; index < count; index++)
                {
                    column += stacked[from++].Height;
                }

                tallest = Math.Max(tallest, column);
            }

            return tallest > 0 ? tallest : total;
        }

        var fragments = new List<FragmentChild>(children.Count);

        foreach (var child in children)
        {
            fragments.Add(child.Fragment);
        }

        var balanced = Fragmentainer.Balance(fragments, columns);

        // A height the container states caps its columns: the content balances within it where it can,
        // and spills into more columns than it names where it cannot (CSS Multicol 1 §3, measured:
        // multi-line-row-flex-fragmentation-094).
        return declaredHeight is { } cap && cap < balanced ? cap : balanced;
    }

    /// <summary>Gets whether the child is pinned to an edge of its container instead of stacked in it.</summary>
    private static Boolean IsFloat(Box child) =>
        child.Style.Float != FloatSide.None &&
        child.Style.NotePlacement != NotePlacement.Inline;

    /// <summary>
    /// Gets the line an inline-level out-of-flow box would have joined, or null when its specified
    /// display was block-level - which stands at the container's own start corner instead.
    /// </summary>
    /// <remarks>
    /// A box that left the flow is blockified before it generates a box, but the place it would have
    /// had is still read off the display the page wrote: measured in Chrome, `display: inline`,
    /// `inline-block` and `inline-flex` absolutely positioned boxes all hang from a line's insertion
    /// point (shifted by text-align, shortened by the floats standing at that offset), while a
    /// `display: block` one stands at the container's start whatever the alignment says. A column's
    /// line runs down the page and this anchor is written along x, so only a row is answered here.
    /// </remarks>
    private static StaticLine? StaticLineOf(
        Box container,
        Box child,
        FloatTracks? tracks,
        Double contentX,
        Double contentWidth,
        Double top)
    {
        if (container.Flow.IsVertical ||
            !child.Style.Display.StartsWith("inline", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var (left, right) = tracks is { } floats
            ? floats.FreeSpanAt(top, 0)
            : (contentX, contentX + contentWidth);

        // The box adds no width to the line, so the insertion point stands wherever an empty line's
        // content would: at the line's start, its middle or its far end.
        var alignment = InlineLayout.AlignmentOffset(
            container.Style.TextAlign, Math.Max(0, right - left), 0, container.Flow);

        return new StaticLine(left, right, alignment, container.Flow.InlineSign);
    }

    /// <summary>
    /// Gets the band a child's line boxes stand in: the container's own floats, or the ones it inherited
    /// when nothing in it floats, with the child's content corner placed inside them.
    /// </summary>
    /// <param name="top">
    /// Where the child's border box begins along the axis the floats stack down, in the coordinates this
    /// pass is working in - relative to the content corner while measuring, absolute while arranging.
    /// </param>
    /// <param name="left">The same for the axis the floats are pinned along.</param>
    /// <param name="contentX">
    /// Where this box's content corner sits in those same coordinates, which is where the corner of the
    /// frame the inherited band is written in sits - the ancestor's tracks are not in this box's frame
    /// until the distance between the two is added back.
    /// </param>
    /// <param name="contentY">The same for the other axis.</param>
    private static FloatBand? BandFor(
        Box child,
        FloatTracks? own,
        FloatBand? inherited,
        Double top,
        Double left,
        Double right,
        Double contentX,
        Double contentY,
        Double basis)
    {
        var tracks = own ?? inherited?.Tracks;

        if (tracks is null)
        {
            return null;
        }

        var border = BorderOf(child);
        var padding = child.Style.Padding.Resolve(basis);

        // A container that holds the floats keeps their frame as its own, which is the frame the child's
        // position is already written in. One that does not hands the ancestor's frame down, and the
        // distance from the ancestor's corner to this box's is the band's own origin.
        var originY = (own is null ? inherited!.OriginY + top - contentY : top) + border.Top + padding.Top;
        var originLeft = (own is null ? inherited!.Left + left - contentX : left) + border.Left + padding.Left;

        // Where the child's content box ends: a row that reads right to left stands against this box's
        // inline-end edge, so it is that edge less the child's own trailing margin, border and padding -
        // and not the child's left edge plus a width, which is what the constraint equation has still to
        // settle (CSS 2.1 §10.3.3).
        var originEnd = (own is null ? inherited!.End : contentX + basis) - right - border.Right - padding.Right;

        // Nothing that ended above this box can narrow a line inside it: the floats in a band are placed
        // at or below the content they were met after, so once the deepest of them is level with the
        // box's own content top, the box and everything stacked in it reads at full width. Without this
        // a single float near the top of a column hands a band to every block below it, and every one of
        // them breaks its lines twice for a float it will never touch.
        if (tracks.Bottom <= originY)
        {
            return null;
        }

        return new FloatBand(tracks, originY, originLeft, originEnd);
    }

    /// <summary>
    /// Gets how far a box that keeps its own formatting context has to stand from the floats around it,
    /// how much of the container is left for it, and the band its own lines should read.
    /// </summary>
    /// <remarks>
    /// The border edge of a formatting root may not touch the margin edge of a float, which is what
    /// makes <c>overflow: hidden</c> beside a floated picture the two column layout it has been used for
    /// since then - and the box keeps its own width rather than merely shortening its lines, because a
    /// box that clips its overflow is a container of its own and not a run of text. Its own lines are
    /// then nobody else's business: nothing outside a formatting root narrows what is inside it, so the
    /// band stops here as well as the box moving.
    /// </remarks>
    private static (Double Shift, Double Room, Double Drop, FloatBand? Band) FormattingRoot(
        Box child,
        FloatBand? band,
        Double contentWidth)
    {
        if (band is null || !IsFormattingRoot(child))
        {
            return (0, contentWidth, 0, band);
        }

        var tracks = band.Tracks;
        var top = band.OriginY;

        // A formatting root whose width the author set cannot shorten itself to squeeze into the room
        // left beside a float, so when that room is narrower than the box it steps down to the next
        // row where the whole box fits - the drop that keeps a fixed-width aside clear of a floated
        // picture above it rather than shoving it past the container's edge. A box whose width is
        // <c>auto</c> just shortens instead (the shift and room below), so it stays put and is left
        // exactly where it was; only a declared extent forces the descent.
        if (DeclaredRootExtent(child, contentWidth) is { } needed)
        {
            top = tracks.FitBelow(top, needed);
        }

        var (left, right) = tracks.FreeSpanAt(top, 0.001);

        // The band's origin is the box's own content corner, while the rule speaks of its border edge:
        // the padding and border between the two are part of what may not overlap the float. Taking
        // the shift off the content corner put a padded formatting root a padding's width into the
        // float it was stepping clear of (contain-layout-ifc-022).
        var border = BorderOf(child);
        var padding = child.Style.Padding.Resolve(contentWidth);

        // A right-to-left row's content begins at its right edge, so the room a formatting root is
        // given and the distance it is shifted from that edge are read the other way round.
        var reversed = child.Flow.InlineSign < 0;

        // The edge the row ends at, which is where a right-to-left box stands and where a float on that
        // side intrudes. The container's width is not what reaches here: a box given an extent of its own
        // stands against the end edge with it, and adding a size to the other edge put a padded formatting
        // root on the wrong side of the float it was stepping clear of (CSS 2.1 §10.3.3).
        var contentRight = reversed ? band.End : band.Left + contentWidth;

        var shift = Math.Max(0, reversed
            ? contentRight + padding.Right + border.Right - right
            : left - (band.Left - padding.Left - border.Left));
        var room = Math.Max(0, Math.Min(contentWidth, reversed ? contentRight - left : right - band.Left) - shift);
        var drop = Math.Max(0, top - band.OriginY);

        return (shift, room, drop, null);
    }

    /// <summary>
    /// The inline extent a formatting root insists on - its border box when the author declared a
    /// width - or null when it leaves the width to the room (<c>auto</c>) or to a percentage of a
    /// block still being settled, neither of which forces a descent. The horizontal margins are left
    /// out: only the border box may not overlap a float, and a box standing beside one is allowed to
    /// let its trailing margin reach past the room, which is why a fixed-width aside beside a
    /// half-width float still sits beside it rather than dropping below.
    /// </summary>
    private static Double? DeclaredRootExtent(Box child, Double basis)
    {
        var width = child.Style.Width;

        if (!width.HasValue || width.Kind == StyleLengthKind.Percent || width.ReadsContainingBlock)
        {
            return null;
        }

        var border = BorderOf(child);
        var padding = child.Style.Padding.Resolve(basis);
        var content = Math.Max(0, width.Resolve(basis));

        return BoxModel.IsBorderBox(child.Style)
            ? content
            : content + padding.Horizontal + border.Horizontal;
    }

    /// <summary>
    /// Places the floats of a column: each is pinned to the block-start edge and opened at the side of
    /// the line it names, and the ones naming a side march along the line from one another.
    /// </summary>
    /// <remarks>
    /// The axes are the ones the writing mode exchanges: a float's width runs along the block axis and
    /// its height along the line, the block margins are the physical left and right ones and the inline
    /// margins the top and bottom ones, and the block start is the right edge of a column that runs
    /// right to left. The placement is measured in offsets from the content box's start edges, which is
    /// where the column's own cursor is written; the caller maps them onto the page.
    /// <para>
    /// Nothing here reserves room in the column's lines, which is a gap rather than a rule: measured in
    /// Chrome, a float does shorten the lines it stands beside down a column, and this engine lets the
    /// text run under it instead.
    /// </para>
    /// </remarks>
    private sealed class ColumnFloats
    {
        private readonly WritingModeInfo _flow;
        private readonly Double _inlineExtent;
        private readonly Dictionary<Boolean, Double> _ended = new();

        /// <summary>Gets how far down the line the floats reach, which the box's inline extent covers.</summary>
        public Double DeepestInline { get; private set; }

        public ColumnFloats(WritingModeInfo flow, Double inlineExtent)
        {
            _flow = flow;
            _inlineExtent = inlineExtent;
        }

        /// <summary>Places one float and hands back where it stands and how much of each axis it takes.</summary>
        public (Rect Placed, Size Size) Place(Box child, Size size, Double blockCursor)
        {
            var flow = _flow;
            var inlineSize = flow.InlineExtent(size.Width, size.Height);
            var blockSize = flow.BlockExtent(size.Width, size.Height);
            var margin = child.Style.Margin.Resolve(_inlineExtent);
            var (inlineStart, inlineEnd) = flow.InlineMargins(margin);
            var (blockStart, blockEnd) = flow.BlockMargins(margin);
            var atStart = flow.FloatsAtTheInlineStart(child.Style.Float);

            // The floats of one side march along the line from the edge they are pinned to, and each of
            // them stands at the block-start edge rather than one band deeper: measured in Chrome, three
            // `float: right` boxes of 40x30 in a 300x200 column come to y=170, 140 and 110, all of them
            // at x=260. A row's floats step aside instead because the edge they are pinned to runs the
            // other way - it is the edge, not the axis, that the march follows.
            var along = _ended.TryGetValue(atStart, out var ended) ? ended : 0.0;
            var block = blockCursor + blockStart;
            var inline = atStart
                ? along + inlineStart
                : _inlineExtent - along - inlineSize - inlineEnd;

            _ended[atStart] = along + inlineSize + inlineEnd;
            DeepestInline = Math.Max(DeepestInline, inline + inlineSize + inlineEnd);

            return (new Rect(inline, block, inlineSize, blockSize), size);
        }
    }

    /// <summary>
    /// Gets the tracks the container's floats are placed against, or null when nothing in it can
    /// float and the walk need not carry them.
    /// </summary>
    /// <remarks>
    /// A column is left out deliberately: its block axis runs sideways, so a float pinned to a
    /// physical edge would have to be placed against a band that stacks along x, and the tracks here
    /// only know how to stack down. Such a box keeps its place in the column stack, which is the
    /// behaviour floats had before this existed - visible, and rare on a page that reads sideways.
    /// </remarks>
    private static FloatTracks? TracksFor(Box box, Double contentWidth, Double originX)
    {
        var floats = false;

        foreach (var child in box.Children)
        {
            if (child.IsBlockLevel && IsFloat(child) && !PositionedLayout.IsOutOfFlow(child))
            {
                floats = true;
                break;
            }
        }

        return floats && !box.Flow.IsVertical
            ? new FloatTracks(originX, originX + contentWidth)
            : null;
    }

    /// <summary>
    /// Gives a container that holds floats of its own the floats of the context it stands in as well,
    /// moved by the distance between the two frames.
    /// </summary>
    /// <remarks>
    /// The context's floats are what CSS 2.1 §9.5 makes a float step clear of, and they narrow the lines of
    /// every box in that context - so a list item with a floated thumbnail beside a floated picture has to
    /// read both, and a container that holds a float is not exempt from the ones above it. The measuring
    /// pass counts from each box's own content corner and pays the distance here; the arranging pass works
    /// in document coordinates from end to end and pays nothing, which is why both callers hand in the
    /// corner their own tracks are opened at.
    /// </remarks>
    /// <param name="contentWidth">
    /// The container's own content width, which is what tells the end edge from the start one in a row that
    /// reads right to left. There the start edge is what the constraint equation has still to settle, while
    /// the end edge is known from the parent's edge and the container's trailing margin (§10.3.3) - and
    /// paying the start edge moved the context's floats to the wrong side of the box, which let a float of
    /// its own stand on top of them.
    /// </param>
    private static void StandBeside(
        FloatTracks? tracks,
        Box container,
        Double contentWidth,
        FloatBand? around,
        Double contentX,
        Double contentY)
    {
        if (tracks is { } floated && around is { } band)
        {
            var againstEnd = !container.Flow.IsVertical && container.Flow.InlineSign < 0;
            var dx = againstEnd
                ? contentX + contentWidth - band.End
                : contentX - band.Left;

            floated.Hold(band.Tracks, dx, contentY - band.OriginY);
        }
    }

    /// <summary>
    /// Gets where a child's content corner stands along the inline axis, in the frame its parent's floats
    /// are written in.
    /// </summary>
    /// <remarks>
    /// A row that reads right to left begins at the parent's end edge, so a child is measured back from
    /// there with its own trailing margin and its border box, and it is the start margin that the
    /// over-constrained equation gives up (CSS 2.1 §10.3.3). The arranging pass stands the box at this same
    /// place when it builds the rectangle; the measuring pass owes the floats the distance.
    /// </remarks>
    private static Double InlineCorner(
        Box container,
        Double contentWidth,
        Double borderBoxWidth,
        Thickness margin,
        Thickness border,
        Thickness padding,
        Double shift)
    {
        var inward = container.Flow.IsVertical || container.Flow.InlineSign > 0
            ? margin.Left + shift
            : contentWidth - margin.Right - borderBoxWidth + shift;

        return inward + border.Left + padding.Left;
    }

    /// <summary>
    /// Sizes a float against the room its edge leaves and reserves that room in the tracks. Both
    /// phases call this with the same numbers, so the width the arranging phase reads out of the
    /// measurement cache is the one the container's height was answered from.
    /// </summary>
    private (Rect BorderBox, Size Size) PlaceFloat(
        Box child,
        FloatTracks tracks,
        Double top,
        Double contentWidth,
        LayoutContext childContext,
        Double? lineTop = null,
        Double inlineOffset = 0)
    {
        var style = child.Style;
        var margin = style.Margin.Resolve(contentWidth);
        var y = tracks.ClearBelow(style.Clear, top);
        var (freeLeft, freeRight) = tracks.FreeSpanAt(y, 0);

        // A floating box is the root of a block formatting context of its own (CSS 2.1 §9.5), so nothing
        // outside it narrows what it lays out: the room its edge leaves sizes the box - asked above, as the
        // free span - and its own lines are shortened only by the floats it holds. The band stops here, which
        // also keeps the float from reading the track it has just reserved in its parent: a room taken out of
        // its own line box by itself is not a rule anywhere in the specification.
        childContext = childContext.WithFloats(null);

        // A float with a declared width is measured against the container it sits in, so a percentage
        // inside it resolves against the containing block rather than against the room a neighbour
        // left. One whose width is <c>auto</c> shrinks to fit that room: CSS gives it the preferred
        // width clamped between its own minimum and what is free, which is what makes a floated
        // thumbnail take its picture's width and a floated paragraph take the column's.
        var size = style.Width.HasValue
            ? _engine.Measure(child, MeasureConstraints.FitWidth(contentWidth), childContext)
            : ShrinkToFit(child, margin, freeRight - freeLeft, childContext);

        // An auto margin on a float is zero rather than a share of what the width left over: the
        // constraint equation that makes <c>margin: 0 auto</c> centre a block has no part for a box
        // that is not in flow to solve.
        // A float written after a line's content stands on that line, past the content it reached,
        // so long as it fits in what that row leaves: one that does not fit, or that asks to clear
        // something, keeps the place the stack reached instead (CSS 2.1 §9.5.1).
        var side = child.Flow.FloatSideOf(style.Float);
        Double? beside = null;

        if (lineTop is { } candidate && style.Clear == ClearSide.None)
        {
            var (left, right) = tracks.FreeSpanAt(candidate - margin.Top, size.Height + margin.Vertical);
            var x = side == FloatSide.Left
                ? left + inlineOffset + margin.Left
                : right - inlineOffset - size.Width - margin.Right;
            var fits = side == FloatSide.Left
                ? x + size.Width <= right + 0.001
                : x >= left - 0.001;

            if (fits)
            {
                beside = candidate;
            }
        }

        var box = tracks.Place(
            side,
            size.Width,
            size.Height,
            margin,
            beside ?? y,
            StandsClear(child.Style, contentWidth),
            child,
            beside is null ? 0 : inlineOffset);

        return (box, size);
    }

    /// <summary>
    /// Gets the block right before this one in the box's run: the box a float's place may follow. A
    /// float of its own, an out-of-flow box and a legend are left out of the run entirely, so none of
    /// them can be what stands before the float - and a float that follows another float keeps the
    /// place the stack reached rather than rising to a line it once stood beside (CSS 2.1 §9.5.1:
    /// no float is higher than one generated earlier in the source).
    /// </summary>
    private static Box? Before(Box parent, Box child)
    {
        Box? previous = null;

        foreach (var item in parent.Children)
        {
            if (ReferenceEquals(item, child))
            {
                return previous;
            }

            if (item.IsBlockLevel && !PositionedLayout.IsOutOfFlow(item) && !item.IsLegend)
            {
                previous = item;
            }
        }

        return null;
    }

    /// <summary>
    /// Gets where the last line of a block of lines stands and how far along it the content reaches:
    /// the place a float written after that content keeps to (CSS 2.1 §9.5.1). Null where there is no
    /// line to stand beside - the box is not a run of lines, its last line is empty, or the columns
    /// run down the page, where the float's side is the other axis.
    /// </summary>
    private (Double Top, Double Used)? ReachedLine(
        Box lines,
        Double contentWidth,
        WritingModeInfo flow,
        LayoutContext context,
        Double bottom)
    {
        if (!lines.IsAnonymous || flow.IsVertical || !lines.IsBlockLevel)
        {
            return null;
        }

        // A line the content ended with a forced break leaves no content for a float to follow: the
        // float stands on the line the break began, below the one that ended (measured on the float
        // reference of flexbox-baseline-multi-line-vert-001, whose float follows a <br>).
        for (var index = lines.Children.Count - 1; index >= 0; index--)
        {
            var item = lines.Children[index];

            if (item.Kind == BoxKind.Text && String.IsNullOrWhiteSpace(item.Text))
            {
                continue;
            }

            if (item.Kind == BoxKind.LineBreak)
            {
                return null;
            }

            break;
        }

        var inline = new InlineContext(_engine, context, context, context);
        var widths = _inline.LineWidths(lines, contentWidth, flow, inline);

        if (widths.Count == 0 || widths[^1] <= 0.001)
        {
            return null;
        }

        var heights = _inline.LineHeights(lines, contentWidth, flow, inline);
        var height = heights.Count > 0 ? heights[^1] : 0.0;

        return (bottom - height, widths[^1]);
    }

    /// <summary>
    /// Gets the builder for the area a float's lines stand clear of, or null where the float states no
    /// shape. The area cannot be settled before the placement, because the boxes the shape measures itself
    /// against - and the margin box it is cut to - are only known once the float has been put on its edge.
    /// </summary>
    private static Func<Rect, FloatArea?>? StandsClear(ComputedStyle style, Double basis) =>
        style.ShapeOutside is { } shape
            ? border => ShapeArea(shape, style, border, basis)
            : null;

    /// <summary>
    /// Works the shape a float states out onto the box it was put at, and folds the two things the chapter
    /// adds to it: the distance the shape is stood outward, and the float's own margin box the shape can
    /// only ever reduce (CSS Shapes 1 §2, §6.3).
    /// </summary>
    private static FloatArea? ShapeArea(ClipPathShape shape, ComputedStyle style, Rect border, Double basis)
    {
        var edges = new Thickness(
            style.BorderTopWidth,
            style.BorderRightWidth,
            style.BorderBottomWidth,
            style.BorderLeftWidth);
        var padding = style.Padding.Resolve(basis);
        var margin = style.Margin.Resolve(basis);

        var boxes = new ClipBoxes(
            border,
            new Rect(
                border.X + edges.Left,
                border.Y + edges.Top,
                Math.Max(0, border.Width - edges.Horizontal),
                Math.Max(0, border.Height - edges.Vertical)),
            default,
            new Rect(
                border.X - margin.Left,
                border.Y - margin.Top,
                border.Width + margin.Horizontal,
                border.Height + margin.Vertical));

        var content = new Rect(
            boxes.Padding.X + padding.Left,
            boxes.Padding.Y + padding.Top,
            Math.Max(0, boxes.Padding.Width - padding.Horizontal),
            Math.Max(0, boxes.Padding.Height - padding.Vertical));

        boxes = boxes with { Content = content };

        // A shape measured from a box that has one of its own - the four the grammar names - follows that
        // box's edge, curvature and all; a basic shape is read the way a clip reads it (CSS Shapes 1 §5).
        var cut = shape.Form == ClipForm.Box
            ? new ClipCut(shape.ReferenceOf(boxes), CornersOf(shape.Box, style, border, edges, padding, margin, boxes), null, false)
            : shape.Cut(boxes);

        return FloatArea.Of(cut, style.ShapeMargin?.Resolve(basis) ?? 0.0, boxes.Margin);
    }

    /// <summary>
    /// Gets the corner the box a float names came to, with the curvature that box carries. The three inner
    /// boxes take the border-box radii less the insets they stand inside, and the margin box is the one
    /// whose corners are worked out of the margin itself, because a radius does not simply grow by the
    /// distance around it (CSS Shapes 1 §5).
    /// </summary>
    private static CornerRadii CornersOf(
        ClipBox box,
        ComputedStyle style,
        Rect border,
        Thickness border_,
        Thickness padding,
        Thickness margin,
        ClipBoxes boxes)
    {
        var radii = CornerRadii.Resolve(style.BorderRadius, border.Width, border.Height);

        return box switch
        {
            ClipBox.Padding => radii.Shrink(border_),
            ClipBox.Content => radii.Shrink(new Thickness(
                border_.Top + padding.Top,
                border_.Right + padding.Right,
                border_.Bottom + padding.Bottom,
                border_.Left + padding.Left)),
            ClipBox.Margin => CornerRadii.Fit(MarginCorners(radii, margin), boxes.Margin.Width, boxes.Margin.Height),
            _ => radii,
        };
    }

    private static CornerRadii MarginCorners(CornerRadii radii, Thickness margin) =>
        new(
            new CornerRadius(Margin(radii.TopLeft.Horizontal, margin.Left), Margin(radii.TopLeft.Vertical, margin.Top)),
            new CornerRadius(Margin(radii.TopRight.Horizontal, margin.Right), Margin(radii.TopRight.Vertical, margin.Top)),
            new CornerRadius(Margin(radii.BottomRight.Horizontal, margin.Right), Margin(radii.BottomRight.Vertical, margin.Bottom)),
            new CornerRadius(Margin(radii.BottomLeft.Horizontal, margin.Left), Margin(radii.BottomLeft.Vertical, margin.Bottom)));

    /// <summary>
    /// Gets the radius of a margin box corner: the border radius moved out by the margin it stands in, with
    /// the two curves the chapter gives for the ratio between them (§5).
    /// </summary>
    private static Double Margin(Double radius, Double margin)
    {
        if (margin <= 0 || radius / margin >= 1)
        {
            return Math.Max(radius + margin, 0);
        }

        var ratio = radius / margin;

        return radius + margin * (1 + Math.Pow(ratio - 1, 3));
    }

    /// <summary>
    /// Measures a float whose width the author left to the content: the preferred width, clamped
    /// between what the float cannot go below and what its edge has left of room.
    /// </summary>
    private Size ShrinkToFit(Box child, Thickness margin, Double free, LayoutContext childContext)
    {
        var intrinsic = _engine.MeasureIntrinsic(child, childContext);
        var room = Math.Max(0, free - margin.Horizontal);
        var fit = Math.Max(intrinsic.MinContent, Math.Min(room, intrinsic.MaxContent));

        // The basis carries the margins back on, because an auto width in the box model is the
        // containing block less the margins and the insets - and the answer wanted is the border box.
        return _engine.Measure(child, MeasureConstraints.FitWidth(fit + margin.Horizontal), childContext);
    }

}

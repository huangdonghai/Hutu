using AngleSharp.Css.Dom;
using Hutu.Rendering.Boxes;
using System;
using System.Collections.Generic;

namespace Hutu.Rendering.Layout;

internal sealed partial class BlockLayout
{
    private Double ArrangeBlockChildren(
        Box box,
        LayoutBox parent,
        Double contentX,
        Double contentY,
        Double contentWidth,
        LayoutContext context)
    {
        var cursor = contentY;
        var pending = 0.0;
        var pendingGroup = MarginGroup.Zero;
        var throughTop = CollapsesThroughStart(box, box.Flow, contentWidth);
        var constraints = MeasureConstraints.FitWidth(contentWidth);
        var tracks = TracksFor(box, contentWidth, contentX);

        // The arranging pass counts from the page's own corner, so the distance between this container's
        // frame and its context's is paid by handing in the corner its tracks are opened at: in document
        // coordinates the two are the same place, and the context's floats come in where they stand.
        StandBeside(tracks, box, contentWidth, context.Floats, contentX, contentY);

        var topEscapes = throughTop;

        if (LegendOf(_engine, box, contentWidth, context) is { } legendRoom)
        {
            cursor += legendRoom.Inset;
        }

        foreach (var child in box.Children)
        {
            if (!child.IsBlockLevel)
            {
                continue;
            }

            var margin = child.Style.Margin.Resolve(contentWidth);

            // The legend stands in the fieldset's block-start border: it is placed against the
            // border box rather than against the flow, and it takes no room in the stack of
            // content that follows it - the room it takes was given to the flow above
            // (HTML rendering §15.3.12).
            if (child.IsLegend)
            {
                var legendSize = _engine.Measure(child, constraints, context);
                var legendBorder = BorderOf(box);
                var legendPadding = box.Style.Padding.Resolve(contentWidth);
                var x = box.Flow.IsRightToLeft
                    ? parent.BorderBox.Right - legendBorder.Right - legendPadding.Right -
                      margin.Right - legendSize.Width
                    : parent.BorderBox.X + legendBorder.Left + legendPadding.Left + margin.Left;
                var y = parent.BorderBox.Y +
                    (legendBorder.Top - legendSize.Height - margin.Top - margin.Bottom) / 2 + margin.Top;
                var place = new Rect(x, y, legendSize.Width, legendSize.Height);

                parent.LegendBox = place;
                parent.Children.Add(_engine.Arrange(child, place, context));
                continue;
            }

            if (tracks is { } floats && IsFloat(child))
            {
                // The same walk the measuring phase ran, at the same numbers: the width it reads out
                // of the cache is the one the container's height was answered from, and the place it
                // reserves is the one a clearing box below it is measured against. A float stands
                // outside the margin collapse on either side, but the collapsed margin the run above
                // has built up still stands between it and that box - CSS 2.1 §8.3.1 folds no margin
                // into a float's - so the float's outer edge starts below the strip, at the place
                // the next box in flow would have taken.
                var reached = Before(box, child) is { } lines
                    ? ReachedLine(lines, contentWidth, box.Flow, context.WithFloats(context.Floats), cursor)
                    : null;
                var placed = PlaceFloat(
                    child, floats, cursor + pending, contentWidth, context, reached?.Top, reached?.Used ?? 0);

                // The same seal the measuring pass put on the box, so that the two phases answer with the
                // same numbers: a float's content is arranged without the band it was placed beside.
                parent.Children.Add(_engine.Arrange(child, placed.BorderBox, context.WithFloats(null)));
                continue;
            }

            var childTop = CollapsedStart(child, box.Flow, contentWidth);
            var childBottom = CollapsedEnd(child, box.Flow, contentWidth);
            var childSelfCollapses = IsSelfCollapsing(child, box.Flow, contentWidth);

            // Same rule the measuring pass followed: while the leading run of self-collapsing
            // boxes continues, every top has already escaped through the parent's own collapsed
            // top, and the walk stops that escape at the first box with height - or at a box that
            // steps clear of a float, whose margin the collapse keeps out (CSS 2.1 §8.3.1).
            var clears = tracks is { } floated && child.Style.Clear != ClearSide.None &&
                floated.ClearBelow(child.Style.Clear, cursor + pending) >= cursor + pending + childTop;

            if (topEscapes && !clears)
            {
                if (!childSelfCollapses)
                {
                    topEscapes = false;
                }
            }
            else if (!PositionedLayout.IsOutOfFlow(child))
            {
                // A box that left the flow is in neither part of the run. CSS 2.1 §8.3.1 folds no margin
                // into it and folds none of the run into it, so the strip the walk gathered is where that
                // box would have stood, and the margins it would have carried are the positioning pass's
                // own to add (ibid. §10.3.7).
                pendingGroup = pendingGroup.With(childTop);
                pending = pendingGroup.Value;
            }

            // A fieldset whose legend stands above its border begins that much lower: the room is
            // the fieldset's own, not a margin of its content, so it is added to the space before the
            // box rather than to the collapsed margins around it (HTML rendering §15.3.12).
            if (LegendOf(_engine, child, contentWidth, context) is { } childLegend)
            {
                pending += childLegend.Overhang;
            }

            if (PositionedLayout.IsOutOfFlow(child))
            {
                // Its place in flow is remembered as the static position - the anchor an unset offset
                // falls back to - but the cursor does not advance, so the boxes after it close the gap
                // it leaves behind.
                //
                // It is placed here, in the order the document wrote it, rather than after everything
                // that follows it. Tree order is the paint order for a box with an automatic level, so a
                // box appended past its later siblings is drawn over them whatever the stacking rules
                // say - which is how a box that should be covered ends up on top. Placing it in order
                // costs nothing: it takes no part in the stacking above either way.
                // The anchor is the outer edge the run reached, with nothing of the box's own margins in
                // it. An unset edge takes that place plus those margins, and the pass that lays the box
                // out adds them, so an anchor carrying them too stood the box twice its own distance from
                // the flow - which is where the reference page of a dozen reftests stands its box.
                parent.Children.Add(_engine.ArrangePositioned(
                    child,
                    new Offset(contentX, cursor + pending),
                    context,
                    line: StaticLineOf(box, child, tracks, contentX, contentWidth, cursor + pending)));

                continue;
            }

            if (childSelfCollapses)
            {
                // A zero-height box still appears in the tree so the painter finds its style where
                // the author left it - it is the box that carries the background, if any - but the
                // cursor does not move: the pending strip continues to the next box without a stop
                // here. The size it was measured to already agrees with the pass above.
                var size0 = _engine.Measure(child, constraints, context);
                var used0 = BoxModel.UsedMargin(child.Style, contentWidth, size0.Width);

                // A cleared zero-height box is the exception, and the measuring pass said where it
                // stands: below the floats, its top margin spent on the clearance and its bottom
                // margin left for the boxes after it (CSS 2.1 §8.3.1, §9.5.2).
                var stand = cursor + pending;

                if (clears && tracks is { } belowClear)
                {
                    stand = Math.Max(stand, belowClear.ClearBelow(child.Style.Clear, stand));
                }

                // CSS 2.1 §8.3.1 gives a self-collapsing box one of two places: its top border edge
                // is the parent's own when its top margin collapses with the parent's (the escape
                // this walk has already stopped combining for, which leaves the strip empty), and
                // otherwise - the parent seals a formatting context and takes no part in it - the
                // place it would have had with a non-zero bottom border, which is after its own top
                // margin. Standing it at the cursor either way puts it at the parent's edge in the
                // second case too: a `margin-top` box that opens a grid item sat 24 pixels above
                // where a browser draws it.
                // The same edge the ordinary path stands a child at: a box with nothing in its own height
                // is still a box in the row, and a row that reads right to left begins at its end edge, so
                // the start margin alone put an empty item at the wrong side of the page (CSS 2.1 §10.3.3).
                var inward0 = box.Flow.IsVertical || box.Flow.InlineSign > 0
                    ? used0.Left
                    : contentWidth - used0.Right - size0.Width;
                var rect0 = new Rect(contentX + inward0, stand, size0.Width, size0.Height);
                parent.Children.Add(_engine.Arrange(child, rect0, context));

                if (clears && tracks is not null)
                {
                    cursor = stand;
                    pending = childBottom;
                    pendingGroup = new MarginGroup(childBottom);
                }

                continue;
            }

            // The same clearance the measuring pass applied, before the band is read: the room a
            // cleared root takes is the room at the height it lands, not at the height it started.
            if (tracks is { } cleared && child.Style.Clear != ClearSide.None)
            {
                var clearedTo = cleared.ClearBelow(child.Style.Clear, cursor + pending);
                pending = Math.Max(pending, clearedTo - cursor);
            }

            // The band and the step clear of the floats are the measuring pass's own arithmetic, run
            // again at the place the child lands, so the size read back is the one it was answered with.
            var (shift, room, drop, band) = FormattingRoot(
                child,
                BandFor(
                    child, tracks, context.Floats,
                    cursor + pending, contentX + margin.Left, margin.Right, contentX, contentY, contentWidth),
                contentWidth);

            var passContext = context.WithFloats(band);
            var childConstraints = constraints;

            if (room < contentWidth)
            {
                passContext = passContext.ForContainingBlock(new Size(room, context.ContainingBlock.Height));
                childConstraints = MeasureConstraints.FitWidth(room);
            }

            var size = _engine.Measure(child, childConstraints, passContext);
            var used = BoxModel.UsedMargin(child.Style, contentWidth, size.Width);

            // A row that reads right to left begins at its right edge, so a child narrower than the row
            // stands against that edge and the room left over is on its inline-end side, the left one.
            // The start margin is the right one there, which is also the margin the over-constrained
            // equation keeps (CSS 2.1 §10.3.3).
            var inward = box.Flow.IsVertical || box.Flow.InlineSign > 0
                ? used.Left + shift
                : contentWidth - used.Right - size.Width + shift;
            var rect = new Rect(contentX + inward, cursor + pending + drop, size.Width, size.Height);

            parent.Children.Add(_engine.Arrange(child, rect, passContext));

            // A container that establishes no formatting context of its own does not hold the floats
            // inside it: CSS 2.1 §9.4.1 puts them in the block formatting context of the nearest
            // ancestor that does, so they are absorbed here, exactly as the measuring pass did.
            //
            // No distance is added on the way in, and that is not an omission: the arranging pass
            // works in document coordinates from end to end - the tracks a container's walk fills
            // are opened at its content corner in that frame and the floats are placed where they
            // stand in it - so the floats a child's walk hands up are already written in this
            // container's frame, at the very places they occupy. The measuring pass, whose walk
            // counts from its own content top, is the one that owes the distance between the two
            // frames; asking for it here as well would move every float a second time and leave
            // the container reading floats that stand nowhere.
            if (!EstablishesContext(child) && _engine.FloatsOf(child) is { } held)
            {
                tracks ??= new FloatTracks(contentX, contentX + contentWidth);
                StandBeside(tracks, box, contentWidth, context.Floats, contentX, contentY);
                tracks.Absorb(held, 0, 0);
            }

            cursor = rect.Bottom;
            pending = childBottom;
            pendingGroup = new MarginGroup(childBottom);
        }

        // Published for the container above, which absorbs these when it holds no formatting
        // context of its own - the floats belong to the nearest ancestor that does.
        _engine.PublishFloats(box, tracks);

        // What the content occupied down the box, in the box's own coordinates. A strip that escapes
        // through the end is not part of it - the box's automatic height left it out too
        // (CSS 2.1 §8.3.1) - so a box that sized itself to its content reports no slack, and one that
        // was handed a height reports the room the content left free.
        var occupied = CollapsesThroughEnd(box, box.Flow, contentWidth) ? cursor : cursor + pending;

        return Math.Max(0, occupied - contentY);
    }

    /// <summary>
    /// Arranges one fragment of a block broken at its own lines, where those lines live in the block's
    /// single in-flow child: the floats that stand beside them belong to the fragment that begins the
    /// block, and the child takes the lines this fragment is given - planned from
    /// <paramref name="start"/> where the fragment resumes a paragraph the column beside it broke
    /// (CSS Multicol 3 §7, CSS Break 3 §4.1).
    /// </summary>
    private Double ArrangeBlockLines(
        Box box,
        LayoutBox parent,
        Double contentX,
        Double contentY,
        Double contentWidth,
        Double contentHeight,
        LineRange range,
        InlineStart? start,
        LayoutContext context)
    {
        var tracks = TracksFor(box, contentWidth, contentX);
        Box? host = null;

        foreach (var child in box.Children)
        {
            if (!child.IsBlockLevel || PositionedLayout.IsOutOfFlow(child))
            {
                continue;
            }

            if (tracks is { } floats && IsFloat(child))
            {
                var placed = PlaceFloat(child, floats, contentY, contentWidth, context);

                // A float stands in the fragment that begins the block; the ones after it read the room
                // it left without drawing it a second time.
                if (start is null)
                {
                    parent.Children.Add(_engine.Arrange(child, placed.BorderBox, context.WithFloats(null)));
                }

                continue;
            }

            host = child;
        }

        if (host is null)
        {
            return 0;
        }

        var margin = host.Style.Margin.Resolve(contentWidth);
        var band = BandFor(
            host, tracks, context.Floats, contentY, contentX, margin.Right, contentX, contentY, contentWidth);
        var hostContext = context.WithFloats(start is null ? band : null);
        var hostRect = new Rect(
            contentX + margin.Left,
            contentY + margin.Top,
            Math.Max(0, contentWidth - margin.Horizontal),
            contentHeight);

        parent.Children.Add(_engine.Arrange(host, hostRect, hostContext, false, range, null, start));

        return contentHeight;
    }

    /// <summary>
    /// Arranges the block children of a multi-column box: each column takes the children the slicer
    /// gives it, stacked from the column's top, and a forced column break ends a column even where more
    /// would fit (CSS Break 3 §4.3). The extent the columns were shared at is what the box's content
    /// comes to.
    /// </summary>
    private Double ArrangeMulticolChildren(
        Box box,
        LayoutBox parent,
        Rect content,
        Int32 columns,
        Double share,
        Boolean heightStated,
        Double declaredHeight,
        LayoutContext context)
    {
        // A container of nothing but floats packs them into its columns rather than stacking them.
        if (AllFloats(box))
        {
            return ArrangeFloatColumns(box, parent, content, columns, share, heightStated, declaredHeight, context);
        }

        if (FloatFlowColumns(box, heightStated, declaredHeight, share, context))
        {
            return ArrangeFloatFlowColumns(box, parent, content, columns, share, declaredHeight, context);
        }

        // A paragraph broken across the columns keeps its float in the first one and resumes the text
        // in the next at the column's full width.
        if (FloatHostOf(box) is { } floatHost &&
            PlanFloatParagraph(floatHost, share, heightStated ? declaredHeight : null, columns, context) is { } plan)
        {
            return ArrangeFloatParagraph(box, floatHost, plan, parent, content, share, heightStated, declaredHeight, context);
        }

        var sections = MulticolSections(box);

        // A spanner spans every column of its row, so the content above and below it are two runs laid
        // out over columns of their own (CSS Multicol 3 §3.2).
        if (sections.Exists(section => section.Spanner is not null))
        {
            return ArrangeMulticolSections(box, sections, parent, content, columns, share, heightStated, declaredHeight, context);
        }

        // A column that runs down the page is bounded by the width of the box - the axis it stacks
        // along - which this pass already holds; the height is the inline axis it shares them over.
        if (box.Flow.IsVertical)
        {
            heightStated = true;
            declaredHeight = content.Width;
        }

        var fragmentHeight = FillsTo(box, parent, heightStated, declaredHeight);
        var members = EffectiveMembers(box.Children, share) ?? box.Children;

        // A vertical container that shares the room a cut box left in a column through the plan can say
        // so for every piece in the run: a box that brings margins pays them along the block axis, which
        // is not the axis the plan spends on, and a box that asks not to be broken is given the head of a
        // column by Chrome and one piece per column here, which is what the counted cells already do.
        // Where either holds, the run is laid out from the counted cells and the boxes keep the pieces the
        // measuring pass made. (Measured: break-inside-avoid-min-block-size-2, whose `margin-block-end:
        // 20px` is a distance along x and whose two avoid boxes come out 30 + 10 at the heads of four
        // columns, never 30 + 10 + 20 + 20 of three.)
        var plansAcross = box.Flow.IsVertical && members.All(member =>
            !PositionedLayout.IsOutOfFlow(member) &&
            member.Style.Breaks.Inside != BreakValue.Avoid &&
            NoMargin(BoxModel.UsedMargin(member.Style, share, share)));
        var children = MulticolChildren(
                    box,
                    share,
                    heightStated ? declaredHeight : 0,
                    fragmentHeight,
                    columns,
                    context,
                    members,
                    plansAcross);
        var multi = box.Style.Multi;
        var extent = MulticolExtent(
            multi,
            columns,
            fragmentHeight > 0 ? fragmentHeight : null,
            children);
        var fragments = new List<FragmentChild>(children.Count);

        foreach (var child in children)
        {
            fragments.Add(child.Fragment);
        }

        // With wrapping, a column box is one `column-height` tall and the content continues in the next
        // box; the container's own height says how many rows of them fit, and content that needs more
        // runs past the last row rather than shrinking it (CSS Multicol 2 §3).
        var columnHeight = multi.Wraps && multi.Height is { } statedHeight ? statedHeight : 0.0;
        var wraps = columnHeight > 0;
        var boxHeight = wraps ? columnHeight : extent;
        var rows = wraps && declaredHeight > 0
            ? Math.Max(1, (Int32)Math.Floor((declaredHeight + multi.RowGap) / (boxHeight + multi.RowGap)))
            : 1;

        // A fill is not held to the column count: content that does not fit the stated columns spills
        // into more of them, which is how Chrome lays out a run longer than its `column-count`. A
        // balance has already been solved into the columns it was given.
        // A balanced container names its columns, but a piece the fragmentainer already cut - a window
        // of a flex container - takes a column of its own even past that count, since it has no room
        // left to share (CSS Flexbox 1 §6).
        var cutPieces = 0;

        foreach (var child in children)
        {
            if (child.Offset >= 0)
            {
                cutPieces++;
            }
        }

        var extents = new Double[Math.Max(1, wraps ? columns * rows : multi.Fills ? Math.Max(children.Count, NeededCells(children, boxHeight)) : Math.Max(columns, cutPieces))];
        Array.Fill(extents, boxHeight);

        var counts = Fragmentainer.Fill(fragments, extents);
        var cells = PlanMulticolRun(children, fragments, extents, counts);
        var childContext = context.ForContainingBlock(
            box.Flow.IsVertical ? new Size(fragmentHeight, share) : new Size(share, wraps ? boxHeight : fragmentHeight))
            .WithFragmentainer();
        // Only a wrapping container puts its columns in rows. A run that spills past the stated count
        // keeps going to the right (a `column-fill: auto` fragmentainer of one column starts a second
        // column beside it), so the row's width is the number of columns the fill actually made.
        if (box.Flow.IsVertical)
        {
            // The same flag that let the measuring pass leave a vertical box whole decides how the run is
            // laid out, so the two passes speak of one set of pieces.
            var run = plansAcross ? cells : PlainCells(children, counts);

            ArrangeMulticolRunAcross(
                children,
                run,
                parent,
                content.X,
                content.Y,
                Math.Max(1, Math.Max(counts.Count, run.Count)),
                share,
                multi.Gap,
                content.Width,
                box.Flow.BlockIsRightToLeft,
                box.Flow.IsRightToLeft,
                childContext);
        }
        else
        {
            ArrangeMulticolRun(
                children,
                cells,
                parent,
                content.X,
                content.Y,
                wraps ? columns : Math.Max(1, Math.Max(counts.Count, cells.Count)),
                share,
                multi.Gap,
                boxHeight,
                multi.RowGap,
                childContext,
                new Dictionary<Box, Double>());
        }

        // An absolutely positioned descendant can reach back over columns that its containing block's
        // own pieces never stand in - a `top` that pulls it above where the block flow put the box.
        // Each column whose window that descendant's rect crosses takes a piece of the containing
        // block, cut to the column, which carries the descendant and the block's own layer with it:
        // the opacity or filter the box establishes belongs to the fragment, not to the column the
        // flow happened to reach (CSS Position 3 §5.5, CSS Fragmentation 3 §4.6, measured:
        // abspos-in-opacity-003, whose top:-300px absolute shows in all five columns while its
        // containing block stands in the fourth).
        if (!box.Flow.IsVertical && !multi.Wraps && multi.Fills && fragmentHeight > 0)
        {
            ArrangeBackwardFragments(
                children,
                parent,
                content,
                Math.Max(1, counts.Count),
                share,
                multi.Gap,
                boxHeight,
                childContext);
        }

        _engine.PublishFloats(box, null);

        // The rules of the gaps the columns left, worked out here where the columns are known. A
        // wrapping run is as tall as the rows of boxes it filled, whatever a balance made of the same
        // content in a single row of them.
        var wrapHeight = multi.Wraps && multi.Height is { } wrapStated ? wrapStated : 0.0;
        var runHeight = wrapHeight > 0 && declaredHeight > 0
            ? Math.Max(1, (Int32)Math.Floor((declaredHeight + multi.RowGap) / (wrapHeight + multi.RowGap))) *
                (wrapHeight + multi.RowGap) - multi.RowGap
            : extent;

        // An out-of-flow child anchors to this box's padding box, which is the containing block it
        // was given; it takes no part in the columns (CSS Position 3 §5.5).
        var positioned = PositionedLayout.ForDescendants(context, box, parent.PaddingBox, parent.BorderBox);

        foreach (var child in box.Children)
        {
            if (child.IsBlockLevel && PositionedLayout.IsOutOfFlow(child))
            {
                parent.Children.Add(_engine.ArrangePositioned(
                    child,
                    new Offset(content.X, content.Y),
                    positioned));
            }
        }

        MulticolRules.Add(
            parent,
            box.Style,
            content,
            box.Flow.IsVertical
                ? new List<(Double, Double)> { (content.X, content.X + extent) }
                : new List<(Double, Double)> { (content.Y, content.Y + runHeight) });

        return extent;
    }

    /// <summary>
    /// Places a piece of each box whose absolutely positioned descendant reaches above the box itself,
    /// in every column that stands before the one the box's own pieces fill - the columns whose window
    /// that descendant's rect crosses. The piece is the containing block, stood where its own top
    /// stands inside that column's window, and cut to the window: the descendant comes with it, and so
    /// does the layer the block establishes (CSS Position 3 §5.5, CSS Fragmentation 3 §4.6).
    /// </summary>
    private void ArrangeBackwardFragments(
        List<MulticolChild> children,
        LayoutBox parent,
        Rect content,
        Int32 columns,
        Double share,
        Double gap,
        Double columnHeight,
        LayoutContext childContext)
    {
        if (columnHeight <= 0)
        {
            return;
        }

        var flowed = 0.0;

        foreach (var child in children)
        {
            var start = flowed;

            flowed += child.Fragment.Height;

            // Only a box that brings no height of its own to the flow is a pure home for its
            // out-of-flow child: such a box has nothing standing in the column beside the descendant,
            // so a fragment of it there is nothing but that descendant. A box with content of its own
            // keeps its content in its own column and overflows the earlier ones as any other box
            // does - which is how a fieldset's legend or a paragraph's absolute child stays put.
            if (!HoldsPositioned(child.Box) || child.Size.Height > 0.001)
            {
                continue;
            }

            var border = BorderOf(child.Box);
            var padding = child.Box.Style.Padding.Resolve(share);

            foreach (var (above, below) in BackwardRects(child.Box, share, childContext))
            {
                // Only the part that stands above the box's own top is missing from the columns: what
                // reaches down is already carried by the box's own pieces.
                var from = start + above;
                var to = Math.Min(start + below, start);

                if (to <= from + 0.001)
                {
                    continue;
                }

                for (var column = Math.Max(0, (Int32)Math.Floor(from / columnHeight));
                    column * columnHeight < to - 0.001 && column < columns;
                    column++)
                {
                    var window = column * columnHeight;
                    var top = Math.Max(from, window);
                    var bottom = Math.Min(to, window + columnHeight);
                    var x = content.X + column * (share + gap) + child.Margin.Left;
                    var standing = content.Y + start - window;

                    var piece = _engine.Arrange(
                        child.Box,
                        new Rect(x, standing, share, child.Size.Height),
                        childContext,
                        definiteHeight: false,
                        slice: new FragmentSlice(
                            new Thickness(border.Top, border.Right, border.Bottom, border.Left),
                            new Thickness(padding.Top, padding.Right, padding.Bottom, padding.Left),
                            true,
                            child.Last,
                            new Rect(x, standing, share, child.Size.Height)));

                    // The column shows only the band the descendant itself covers: the rest of this
                    // fragmentainer belongs to the content that stands there.
                    piece.FragmentClip = new Rect(
                        x,
                        content.Y + top - window,
                        child.Size.Width,
                        bottom - top);
                    piece.FragmentCutsInline = false;

                    parent.Children.Add(piece);
                }
            }
        }
    }

    /// <summary>Gets the rectangles, measured from a box's own top edge, that its absolutely positioned
    /// children claim <em>above</em> it - the part of them no column holding the box itself can show,
    /// because it stands before the box's first fragmentainer (CSS Position 3 §5.5). A legend is not
    /// one of these: it is laid against its fieldset's border by that box's own pass. Nothing is
    /// measured here unless a child writes a negative <c>top</c>, so the ordinary box pays nothing for
    /// the question.</summary>
    private List<(Double Above, Double Below)> BackwardRects(Box box, Double width, LayoutContext context)
    {
        var rects = new List<(Double, Double)>();

        foreach (var child in box.Children)
        {
            var position = child.Style.Position;

            if (child.IsLegend ||
                position.Position != PositionMode.Absolute || position.Top.IsAuto)
            {
                continue;
            }

            var at = position.Top.Resolve(0);

            if (at >= -0.001)
            {
                continue;
            }

            var size = _engine.Measure(child, MeasureConstraints.FitWidth(width), context);

            rects.Add((at, at + size.Height));
        }

        return rects;
    }

    /// <summary>
    /// Places the children one row of columns takes: each column stacks them from the row's top, and the
    /// pieces of one broken child that fall in the same column are drawn as one fragment of it.
    /// </summary>
    /// <param name="originX">Where the row's first column begins.</param>
    /// <param name="originY">Where the row's top edge stands.</param>
    /// <param name="boxHeight">The extent the row's columns share.</param>
    /// <param name="rowGap">The room between two rows of column boxes, where the container wraps.</param>
    /// <param name="startsAt">
    /// Where each broken child began, so a later piece can paint the background of the whole box it was
    /// cut from. It is handed in so pieces that fall in different rows and runs still agree.
    /// </param>
    /// <summary>Places the children of columns that run down the page: the columns stand one below
    /// the next along the inline axis, and each stacks its children across the block one.</summary>
    private void ArrangeMulticolRunAcross(
        List<MulticolChild> children,
        List<List<MulticolPiece>> cells,
        LayoutBox parent,
        Double originX,
        Double originY,
        Int32 columns,
        Double share,
        Double gap,
        Double boxExtent,
        Boolean blockReversed,
        Boolean inlineReversed,
        LayoutContext childContext)
    {
        for (var cell = 0; cell < cells.Count; cell++)
        {
            var column = cell % columns;

            // The columns stand one after another along the inline axis, which runs down the page here,
            // and a line that reads back from the page's far end begins its columns there too: the first
            // column of `direction: rtl` is the lowest one on the page, not the highest (CSS Writing
            // Modes 3 §3.2, CSS Multicol 1 §3, measured in Chrome on
            // wpt/css-break/table/table-col-paint-vlr-rtl, whose container spans y 192..592 and whose
            // first fragment stands at 492..592).
            var y = inlineReversed
                ? originY + (columns - 1 - column) * (share + gap)
                : originY + column * (share + gap);

            // The block axis starts at the box edge the writing mode names: the right one for
            // vertical-rl, where the pieces of a cut box run back towards the left.
            var x = blockReversed ? originX + boxExtent : originX;
            var pieces = cells[cell];

            for (var index = 0; index < pieces.Count; index++)
            {
                var portion = pieces[index];
                var child = children[portion.Child];

                var width = child.Size.Width;
                var height = child.Size.Height;

                // A column box begins a fresh stack, as a horizontal one does: the block-start margin of
                // the box that begins there stands at the head of the column.
                var start = blockReversed ? x - child.Margin.Top : x + child.Margin.Top;

                // Where the column cut the box: how much of the block extent this column holds, and how
                // far inside the whole box that piece begins. A piece the fragmentainer had already cut
                // brings its own distance; one the plan left whole stands with none.
                var held = portion.Offset >= 0 ? portion.Height : child.Offset >= 0 ? width : 0.0;
                var offset = portion.Offset >= 0 ? portion.Offset : child.Offset;
                var ends = portion.Offset >= 0 ? portion.Last && child.Last : child.Last;

                if (child.Offset >= 0 || portion.Offset >= 0)
                {
                    // A piece cut through the box owns the edges that fall inside its slice: the edges
                    // the cut fell on are the left and right ones, since the box runs across the page.
                    var cutBorder = BorderOf(child.Box);
                    var cutPadding = child.Box.Style.Padding.Resolve(height);
                    var cutClones = child.Box.Style.ClonesDecoration;
                    var cutStarts = cutClones || (offset < 0.001 && child.OwnsEdges);
                    var cutEnds = cutClones || (ends && child.OwnsEdges);
                    var pieceWidth = held > 0.001 ? held : width;
                    var wholeWidth = child.Whole > 0 ? child.Whole : pieceWidth;
                    var blockX = blockReversed ? start - pieceWidth : start;
                    var heldRect = new Rect(blockX, y + child.Margin.Left, pieceWidth, height);

                    // The fragment's own box, which its transform turns about - the part of the block
                    // extent the box holds in this column, not the whole window it is shown through
                    // (measured: transform-006 read on the axes a vertical column stacks along).
                    var fragmentRect = new Rect(
                        blockReversed
                            ? blockX + pieceWidth - (portion.Cursor >= 0 ? portion.Cursor : portion.Height)
                            : blockX,
                        y + child.Margin.Left,
                        portion.Cursor >= 0 ? portion.Cursor : portion.Height,
                        height);
                    var wholeRect = new Rect(
                        blockReversed ? blockX + offset + pieceWidth - wholeWidth : blockX - offset,
                        y + child.Margin.Left,
                        wholeWidth,
                        height);

                    // A piece the column divided is the whole box seen through the column's window, which
                    // is what the horizontal route does and what a box with content of its own needs: a
                    // table laid inside one column's room would share its columns out over that room and
                    // stack its rows from the wrong edge (CSS Fragmentation 3 §4.1, measured:
                    // table-grid-paint-vrl-rtl, whose row of 225 comes out 50 / 100 / 75 down three
                    // columns while each column is a 100-wide window on the same 400-long wrapper).
                    var laidOut = _engine.Arrange(
                        child.Box,
                        portion.Offset >= 0 ? wholeRect : heldRect,
                        childContext,
                        definiteHeight: false,
                        slice: new FragmentSlice(
                            new Thickness(
                                cutBorder.Top,
                                (blockReversed ? cutStarts : cutEnds) ? cutBorder.Right : 0,
                                cutBorder.Bottom,
                                (blockReversed ? cutEnds : cutStarts) ? cutBorder.Left : 0),
                            new Thickness(
                                cutPadding.Top,
                                (blockReversed ? cutStarts : cutEnds) ? cutPadding.Right : 0,
                                cutPadding.Bottom,
                                (blockReversed ? cutEnds : cutStarts) ? cutPadding.Left : 0),
                            cutStarts,
                            cutEnds,
                            wholeRect,
                            Piece: fragmentRect));

                    if (portion.Offset >= 0)
                    {
                        laidOut.FragmentClip = heldRect;
                        laidOut.FragmentCutsInline = false;
                    }

                    parent.Children.Add(laidOut);

                    x = blockReversed
                        ? blockX - (ends ? child.Margin.Bottom : 0)
                        : blockX + pieceWidth + (ends ? child.Margin.Bottom : 0);
                    continue;
                }

                var stood = _engine.Arrange(
                    child.Box,
                    new Rect(start - (blockReversed ? width : 0), y + child.Margin.Left, width, height),
                    childContext,
                    definiteHeight: false);

                if (!child.OwnsEdges)
                {
                    // The piece spent the margin and nothing else: what the box holds is drawn by the
                    // piece that shows the box.
                    stood.FragmentClip = stood.BorderBox;
                    stood.FragmentCutsInline = false;
                }

                parent.Children.Add(stood);

                x = blockReversed
                    ? start - width - child.Margin.Bottom
                    : start + width + child.Margin.Bottom;
            }
        }
    }

}

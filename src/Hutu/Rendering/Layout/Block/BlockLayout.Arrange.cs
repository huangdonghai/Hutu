using AngleSharp.Css.Dom;
using Hutu.Rendering.Boxes;
using System;
using System.Collections.Generic;

namespace Hutu.Rendering.Layout;

internal sealed partial class BlockLayout
{
    /// <summary>
    /// Fixes a measured block into the given border box and lays its content out inside it. The
    /// distance between the box and that rectangle is already carried by the caller, which owns the
    /// vertical stacking.
    /// </summary>
    /// <param name="definiteHeight">
    /// Whether the height the border box was handed is definite even though the box declares
    /// <c>height: auto</c>. A grid or flex item stretched to its area has such a height: CSS Sizing 3
    /// §5.2 counts a size that can be resolved from a <c>stretch</c> self-alignment as definite, and a
    /// definite size is what a percentage inside resolves against (CSS 2.1 §10.5). Without this the
    /// box's own percentage chain collapses to nothing, because the height it comes out at is read as
    /// one the content decided.
    /// </param>
    /// <param name="lines">
    /// The lines of this box's own inline content that the fragment being arranged takes, where the box
    /// is a block broken across the columns of its parent (CSS Multicol 3 §3.1). Null takes all of them.
    /// </param>
    public LayoutBox Arrange(
        Box box,
        Rect borderBox,
        LayoutContext context,
        Boolean definiteHeight = false,
        LineRange? lines = null,
        FragmentSlice? slice = null,
        InlineStart? start = null)
    {
        var style = box.Style;
        var flow = box.Flow;
        var basis = context.ContainingBlock.Width;
        var percentBasis = context.ContainingIsVertical ? context.ContainingBlock.Height : basis;

        // A fragment of a broken box draws only the edges it owns: the border and padding on an edge it
        // was cut at belong to the fragment that really has that edge (CSS Break 3 §4.7).
        var border = slice?.Border ?? BorderOf(box);
        var padding = slice?.Padding ?? style.Padding.Resolve(percentBasis);

        var layout = new LayoutBox(box, KindOf(box))
        {
            Margin = BoxModel.UsedMargin(style, percentBasis, borderBox.Width),
            Border = border,
            Padding = padding,
            BorderBox = borderBox,
            Flow = flow,
            SlicesBlockStart = slice is { Starts: false },
            SlicesBlockEnd = slice is { Ends: false },
            BackgroundOrigin = slice is null || slice.Value.Starts && slice.Value.Ends ? null : slice.Value.Whole,
        };

        if (box.Children.Count == 0)
        {
            return layout;
        }

                // A piece that a column cut out of the middle of a box draws no border and no padding at the
        // edge it was cut at, but the content it holds still stands where the whole box put it: the room
        // the decoration took at that edge is owed to the content all the same, or every piece after the
        // cut loses that much of the flow (CSS Break 3 §4.7 against §4.1, §4.3; measured: the reference
        // page of gecko/flexbox/pagination/flexbox-single-column-1a, where a container with
        // `border-block-width: 10px` and `padding-block: 5px` shows 15 pixels of a cut item's tail at the
        // head of each later column in Chrome - cyan from 208 to 222, the next item from 223 - while we
        // pulled everything after it up to the head). Only a piece the caller knows stands below that
        // room says so: a fragment whose own box begins at its own head, like the lines of a broken
        // paragraph, or a flow that resumed with a child too tall for the column before it, is owed
        // nothing, or the decoration would be paid to it a second time (measured:
        // wpt/css-break/line-after-unbreakable-float-after-padding, whose 100-pixel padding-top belongs
        // to the first column alone and whose float stands at the next column's head, at 58).
        var owedTop = slice?.OweStart ?? 0.0;

        var contentX = borderBox.X + border.Left + padding.Left;
        var contentY = borderBox.Y + border.Top + padding.Top + owedTop;
        var contentWidth = Math.Max(0, borderBox.Width - border.Horizontal - padding.Horizontal);
        var contentHeight = Math.Max(0, borderBox.Height - border.Vertical - padding.Vertical);

        // A declared height is known before the children are laid out, so it is also the containing
        // block height they resolve their own percentage heights against. A height that comes from
        // the content is not, which is why it is passed as zero there. A column swaps the two: the
        // children's containing block is bounded by the declared width, and its inline extent is the
        // content box's own height, which the box was given rather than derived from the content.
        // Either way the height handed down is the used one: CSS 2.1 §10.7 works a tentative height
        // above max-height (or below min-height) out again with the constraint in its place, so the
        // height the box comes out at is what the children are laid out against, not the height that
        // was written down.
        var insetsV = padding.Vertical + border.Vertical;
        var declaredHeight = BoxModel.DeclaredHeight(style, context.ContainingBlock.Height, insetsV);

        // The height a ratio settles is a preferred height, and the content may be taller than it - so
        // the box comes to whichever is the taller of the two. The border box was handed to this pass
        // at that height already, and the height here is the one the children are laid out against.
        var ratioHeight = BoxModel.HeightFromRatio(style, contentWidth, null, insetsV);

        var usedHeight = declaredHeight is { } declared
            ? BoxModel.ClampHeight(style, declared, context.ContainingBlock.Height, insetsV)
            : ratioHeight is not null
                ? contentHeight
                : definiteHeight
                    ? contentHeight
                    : (Double?)null;

        // The block extent the children are laid out against: this box's own used height where it has one,
        // and where the content decides it, the fallback a child in an orthogonal flow is owed (§7.3.1).
        var handedV = usedHeight ??
            OrthogonalExtent(style, context.ContainingBlock.Height, insetsV, context.Viewport.Height) ?? 0;

        var childBox = flow.IsVertical
            ? new Rect(
                contentX,
                contentY,
                BoxModel.DeclaredWidth(style, context.ContainingBlock.Width, padding.Horizontal + border.Horizontal)
                    ?? context.ContainingBlock.Width,
                usedHeight ?? contentHeight)
            : new Rect(
                contentX,
                contentY,
                contentWidth,
                handedV);

        var childContext = context.ForContainingBlock(childBox, box.Flow.IsVertical);

        // A box that holds its descendants - positioned, transformed, filtered, contained - lends the
        // whole subtree its padding box, so an absolute descendant anchors to it rather than to an
        // ancestor further up. A piece of a broken box lends it *its own* rectangle: the box was laid
        // out over the whole of itself, but the fragment stands in a column of its own, and an
        // absolute child measured against the whole would be drawn a column higher than the piece
        // that carries it (CSS Break 3 §4.5 with CSS Position 3 §5.5, measured:
        // out-of-flow-in-multicolumn-073, whose abs child with top:100px stands at the head of the
        // second column and not 100 pixels above it).
        childContext = PositionedLayout.ForDescendants(
            childContext,
            box,
            slice is { AnchorAtPiece: true, Piece: { } piece }
                ? PiecePadding(box, piece, border, padding)
                : layout.PaddingBox,
            slice is { AnchorAtPiece: true, Piece: { } anchored } ? anchored : layout.BorderBox);

        var content = new Rect(contentX, contentY, contentWidth, contentHeight);
        var used = 0.0;

        // The children of a multi-column container stand in its columns - or, when a container of
        // its own among them keeps it in the ordinary stack, in the place its columns would be.
        if (box.Style.Multi.IsMultiColumn)
        {
            childContext = childContext.WithFragmentainer();
        }

        if (HasBlockChildren(box))
        {
            used = lines is { } range
                ? ArrangeBlockLines(box, layout, contentX, contentY, contentWidth, contentHeight, range, start, childContext)
                : ColumnsOf(box, flow.InlineExtent(contentWidth, contentHeight), out var columns, out var share)
                    ? ArrangeMulticolChildren(
                        box,
                        layout,
                        content,
                        columns,
                        share,
                        usedHeight is not null,
                        usedHeight ?? 0,
                        context)
                    : flow.IsVertical
                        ? ArrangeColumnChildren(box, layout, content, childContext, childBox.Width)
                        : ArrangeBlockChildren(box, layout, contentX, contentY, contentWidth, childContext);
        }
        else
        {
            // The line's own boxes are arranged against the context they were measured against, but a box
            // that left the flow anchors to the nearest positioned ancestor - which is this box's padding
            // box when this box is the one that is positioned. Handing that along separately keeps the
            // two questions from being answered by the same context.
            //
            // An atomic box on the line is measured and arranged against this box's own content box,
            // the containing block an inline-level box stands in (CSS 2.1 §9.1.1) - at the height the
            // measuring pass settled on, so the two passes answer the same question the same way. A
            // column keeps the context its own measuring pass hands the line, which is the pair the
            // two axes are swapped into.
            //
            // The band of floats this box stands among is left behind on the way in: an atomic inline
            // box roots a formatting context of its own, and a float from outside one neither shortens
            // the lines inside it nor drops their first one below itself - the box as a whole is what
            // steps around the float, which the line it sits on has already settled (CSS 2.1 §9.4.1,
            // §9.5). The measuring pass and this one both hand the nested content this context, so the
            // two cannot disagree about where its first line begins.
            var atomic = (flow.IsVertical
                ? context
                : context.ForContainingBlock(new Rect(contentX, contentY, contentWidth, handedV), box.Flow.IsVertical))
                .WithFloats(null);

            var inline = new InlineContext(_engine, context, childContext, atomic);

            used = lines is { } range
                ? _inline.Layout(box, layout, content, flow, inline, range.From, range.To, start)
                : _inline.Layout(box, layout, content, flow, inline);
        }

        // Where the content stands along the block axis is asked once the flow has put it somewhere:
        // `align-content` moves the whole of it, and a box only has the slack to move it in when its
        // own block size came from outside rather than from the content (CSS Box Alignment 3 §5.4).
        AlignContent(box, layout, flow, flow.BlockExtent(contentWidth, contentHeight), used);

        return layout;
    }

    /// <summary>
    /// Gets the extent a child in an orthogonal flow breaks its lines against when this box has no
    /// definite block size of its own.
    /// </summary>
    /// <remarks>
    /// A horizontal box whose height comes from its content hands a vertical child no inline room at
    /// all, and CSS Writing Modes 3 §7.3.1 then calls for a fallback: the smallest of the box's own inner
    /// max size floored by its inner min size, the nearest ancestor scrollport's inner size, and the
    /// initial containing block's size. The engine already answers with the last of those when it is given
    /// nothing, so what is worked out here is the first - the pair the page stated - and it is kept within
    /// the scrollport, which is the other size the rule weighs against it.
    /// </remarks>
    private static Double? OrthogonalExtent(
        ComputedStyle style, Double basis, Double insets, Double scrollExtent)
    {
        if (!style.MaxHeight.HasValue)
        {
            // Nothing stated for the ceiling: the rule's first size is out of the running, and the
            // extents below it answer for themselves.
            return null;
        }

        var ceiling = style.MaxHeight.Resolve(basis) - insets;
        var floor = style.MinHeight.HasValue ? style.MinHeight.Resolve(basis) - insets : 0;

        // A min larger than the max wins, as it does wherever the two are worked out (CSS 2.1 §10.4.3),
        // and the scrollport is the other size the rule weighs against this one.
        return Math.Max(0, Math.Min(Math.Max(ceiling, floor), scrollExtent));
    }

    /// <summary>Measures the block children and returns the height their stacking occupies.</summary>
    /// <remarks>
    /// Margins collapse the way CSS 2.1 §8.3.1 says: adjacent siblings fold their adjoining margins
    /// into one strip, a child's outer edge adjoins the parent's own when the parent has no border
    /// or padding at that end (and does not establish a formatting context), and a box that
    /// self-collapses contributes its one combined margin to whichever strip surrounds it. The part
    /// that escapes through the box is not stored in the box's own content height - it is already
    /// accounted for in the collapsed top or bottom the box reports up and down the stack, which
    /// <see cref="CollapsedTop"/> and <see cref="CollapsedBottom"/> compute on the same rules.
    /// </remarks>
    /// <summary>
    /// Gets the fieldset's rendered legend, the room its block-start border keeps for it, and how far
    /// the box itself is pushed down: the border gives way to the legend, and half of a legend taller
    /// than the border stands above it, so the box begins that much lower while its content stays
    /// where the legend's block size puts it (HTML rendering §15.3.12).
    /// </summary>
    internal static (Double Inset, Double Overhang, Box Legend)? LegendOf(
        LayoutEngine engine, Box box, Double contentWidth, LayoutContext context)
    {
        foreach (var child in box.Children)
        {
            if (!child.IsLegend)
            {
                continue;
            }

            var size = engine.Measure(child, MeasureConstraints.FitWidth(contentWidth), context);
            var margin = child.Style.Margin.Resolve(contentWidth);

            // The border gives way to the legend's margin box, not to its border box: its margins
            // stand in the border as much as its own height does (HTML rendering §15.3.12).
            var overshoot = Math.Max(0, size.Height + margin.Top + margin.Bottom - BorderOf(box).Top);

            return (overshoot - overshoot / 2, overshoot / 2, child);
        }

        return null;
    }

    internal Double MeasureBlockChildren(Box box, Double contentWidth, Double contentHeight, LayoutContext context)
    {
        var cursor = 0.0;
        var pending = 0.0;
        var pendingGroup = MarginGroup.Zero;
        var throughTop = CollapsesThroughStart(box, box.Flow, contentWidth);
        var throughBottom = CollapsesThroughEnd(box, box.Flow, contentWidth);
        var childContext = context.ForContainingBlock(new Size(contentWidth, contentHeight), box.Flow.IsVertical);
        var constraints = MeasureConstraints.FitWidth(contentWidth);
        var tracks = TracksFor(box, contentWidth, 0);

        StandBeside(tracks, box, contentWidth, context.Floats, 0, 0);

        var topEscapes = throughTop;

        // A fieldset keeps the room its legend takes in its block-start border, and its content begins
        // below whichever of the two is deeper (HTML rendering §15.3.12).
        if (LegendOf(_engine, box, contentWidth, childContext) is { } placedLegend)
        {
            cursor += placedLegend.Inset;
        }

        // How deep the floats this container holds directly reach, margin boxes included. The
        // height counts these and not the ones absorbed from a container below: only a block
        // formatting context root grows for the floats taking part in it (CSS 2.1 §10.6.7), and
        // the engine's answer for the direct ones is the one the corpus was measured against.
        var directBottom = 0.0;

        foreach (var child in box.Children)
        {
            if (!child.IsBlockLevel)
            {
                continue;
            }

            // An out-of-flow child neither takes space nor lets its margins collapse with the ones
            // around it, so it is skipped before anything is measured. A fieldset's legend is skipped
            // for the same reason: it stands in the border, and the room it takes was given to the
            // cursor above rather than stacked here (HTML rendering §15.3.12).
            if (PositionedLayout.IsOutOfFlow(child) || child.IsLegend)
            {
                continue;
            }

            var margin = child.Style.Margin.Resolve(contentWidth);

            if (tracks is { } floats && IsFloat(child))
            {
                // The float is sized and put on its edge, and the cursor is left exactly where it
                // was: a float takes no room in the stack, which is the whole reason the container
                // has to be measured with the tracks rather than with the cursor alone. A float
                // also seals the margin run - it does not join the collapse on either side - but
                // the strip the run has built up still stands between it and the box above it:
                // CSS 2.1 §8.3.1 keeps a float's margins from collapsing with any other box, so
                // the collapsed bottom margin of the sibling before it is not folded into the
                // float's top, and the float's outer edge goes below that strip - where the next
                // box in flow would have gone.
                var beside = Before(box, child) is { } lines
                    ? ReachedLine(lines, contentWidth, box.Flow, childContext.WithFloats(context.Floats), cursor)
                    : null;
                var (placed, _) = PlaceFloat(
                    child, floats, cursor + pending, contentWidth, childContext, beside?.Top, beside?.Used ?? 0);
                directBottom = Math.Max(directBottom, placed.Bottom + margin.Bottom);
                continue;
            }

            var childTop = CollapsedStart(child, box.Flow, contentWidth);
            var childBottom = CollapsedEnd(child, box.Flow, contentWidth);
            var childSelfCollapses = IsSelfCollapsing(child, box.Flow, contentWidth);

            // A child that steps clear of a float keeps its top margin out of the collapse with the
            // box above it: CSS 2.1 §8.3.1 folds the margins of a child with clearance into nothing,
            // so the box stands where its own margin puts it and the child's margin is spent inside.
            var clears = tracks is { } floated && child.Style.Clear != ClearSide.None &&
                floated.ClearBelow(child.Style.Clear, cursor + pending) >= cursor + pending + childTop;

            // While the leading run of self-collapsing boxes continues, every top the walk meets
            // is already part of this box's own reported collapsed top and escapes upward through
            // it. The chain ends at the first box that has height of its own, and that box's top
            // joins the escape too - the run adjoins all the way down to it.
            if (topEscapes && !clears)
            {
                if (!childSelfCollapses)
                {
                    topEscapes = false;
                }
            }
            else
            {
                pendingGroup = pendingGroup.With(childTop);
                pending = pendingGroup.Value;
            }

            // A fieldset whose legend stands above its border begins that much lower: the room is
            // the fieldset's own, not a margin of its content, so it is added to the space before the
            // box rather than to the collapsed margins around it (HTML rendering §15.3.12).
            if (LegendOf(_engine, child, contentWidth, childContext) is { } childLegend)
            {
                pending += childLegend.Overhang;
            }

            if (childSelfCollapses)
            {
                // A zero-height box sits inside the pending strip without advancing the cursor: its
                // own combined margin has just been added, and the run carries it on to whichever
                // box follows. Nothing here is a place in the stack the box occupies - unless it
                // steps clear of a float, which is a place: the clearance stands its top edge below
                // the floats, the top margin spent on getting there, and its bottom margin is what
                // the boxes after it collapse with (CSS 2.1 §8.3.1, §9.5.2).
                if (clears && tracks is { } belowClear)
                {
                    cursor = Math.Max(cursor + pending, belowClear.ClearBelow(child.Style.Clear, cursor + pending));
                    pending = childBottom;
                    pendingGroup = new MarginGroup(childBottom);
                }

                continue;
            }

            // A cleared box is moved below the floats it refuses to stand beside before its band is
            // read. The room it may take depends on where it lands: once a left float has ended, the
            // same right float that left no space beside it can leave a usable column beside the
            // cleared top. What it moved through is not given back to the surrounding margins.
            if (tracks is { } cleared && child.Style.Clear != ClearSide.None)
            {
                var clearedTo = cleared.ClearBelow(child.Style.Clear, cursor + pending);
                pending = Math.Max(pending, clearedTo - cursor);
            }

            // The child's lines stand in the band of the floats around it, at the height the child
            // itself reaches: two siblings at different depths have different room beside a float,
            // so the band is answered per child rather than by the one context the container shares.
            var (shift, room, drop, band) = FormattingRoot(
                child,
                BandFor(child, tracks, context.Floats, cursor + pending, margin.Left, margin.Right, 0, 0, contentWidth),
                contentWidth);

            var passContext = childContext.WithFloats(band);
            var childConstraints = constraints;

            // A formatting root is measured against the room the floats leave it, whether it also had
            // to step sideways or only takes a narrower column beside a float on its other edge. The
            // arranging pass runs the same arithmetic to read the size back out of the cache.
            if (room < contentWidth)
            {
                passContext = passContext.ForContainingBlock(
                    new Size(room, childContext.ContainingBlock.Height));
                childConstraints = MeasureConstraints.FitWidth(room);
            }

            var size = _engine.Measure(child, childConstraints, passContext);
            // A container that establishes no formatting context of its own does not hold the floats
            // inside it: CSS 2.1 §9.4.1 puts them in the block formatting context of the nearest
            // ancestor that does, so they are absorbed here - moved into this container's frame by
            // the child's content corner, the same corner the child's own rectangle is placed at -
            // where the boxes that follow stand aside for them (§9.5) and where the next container
            // up absorbs them in turn. A container that does establish one keeps its floats to
            // itself, and so does anything floated or taken out of the flow.
            if (!EstablishesContext(child) && _engine.FloatsOf(child) is { } held)
            {
                var usedMargin = BoxModel.UsedMargin(child.Style, contentWidth, size.Width);
                var childBorder = BorderOf(child);
                var childPadding = child.Style.Padding.Resolve(contentWidth);

                tracks ??= new FloatTracks(0, contentWidth);
                StandBeside(tracks, box, contentWidth, context.Floats, 0, 0);
                tracks.Absorb(
                    held,
                    usedMargin.Left + shift + childBorder.Left + childPadding.Left,
                    cursor + pending + drop + childBorder.Top + childPadding.Top);
            }

            cursor += pending + drop + size.Height;
            pending = childBottom;
            pendingGroup = new MarginGroup(childBottom);
        }

        // The trailing strip either escaped through the box (already in the collapsed bottom this
        // box reports to its parent) or has to be reserved inside this box's content height.
        var stacked = throughBottom ? cursor : cursor + pending;

        // Published for the container above, which absorbs these when it holds no formatting
        // context of its own - the floats belong to the nearest ancestor that does.
        _engine.PublishFloats(box, tracks);

        // Only a block formatting context root grows for the floats taking part in it (CSS 2.1
        // §10.6.7): a plain container is no taller than its content reaches, and the floats hang out
        // below it into the context its ancestor keeps. "Taking part in it" includes the floats a
        // child below hands up - a root holds those as much as the ones it places itself. A float and
        // an out-of-flow box are roots themselves, and hold the floats they carry.
        var heldBottom = EstablishesContext(box) || IsFloat(box) || PositionedLayout.IsOutOfFlow(box)
            ? Math.Max(directBottom, tracks?.Bottom ?? 0)
            : 0.0;

        return Math.Max(stacked, heldBottom);
    }

    /// <summary>
    /// Gets whether the box lays its block-level children out in more than one column, and over how
    /// many: a horizontal multi-column container with room for columns. A container of nothing but
    /// floats takes the column path, since each column can hold the floats that fit beside each other;
    /// floats mixed with flow content keep the ordinary stack, because the column's own float band is
    /// what would shorten the lines around them and the columns do not carry one yet.
    /// </summary>
    private static Boolean ColumnsOf(Box box, Double contentInline, out Int32 columns, out Double share)
    {
        columns = 1;
        share = contentInline;

        if (!box.Style.Multi.IsMultiColumn)
        {
            return false;
        }

        var floats = 0;

        foreach (var child in box.Children)
        {
            if (!child.IsBlockLevel)
            {
                continue;
            }

            // An out-of-flow child stands against this box's own padding box rather than in a
            // column (CSS Position 3 §5.5), so it is no reason to keep the columns from forming -
            // unless a spanner is about, since the runs a spanner makes have not been taught where
            // those boxes stand.
            if (PositionedLayout.IsOutOfFlow(child))
            {
                if (HoldsSpanner(box))
                {
                    return false;
                }

                continue;
            }

            if (child.Style.Float != FloatSide.None)
            {
                floats++;
                continue;
            }

            // A spanner spans every column of its row (CSS Multicol 3 §3.2), which the section layout
            // below lays out; a wrapping container would have to put the spanner in a row of column
            // boxes this does not do, so that pair keeps the ordinary stack until it can.
            if (child.Style.SpansColumns && box.Style.Multi.Wraps)
            {
                return false;
            }

            // A multi-column child balances its own columns inside one column of this one, which the
            // columns do not do yet, so the box keeps the ordinary stack until they can. A child of a
            // single filled column has no columns to balance: it is one room for what it holds, and the
            // columns around it open it into their own members (CSS Multicol 3 §7.4, measured:
            // repeated-section/variable-fragmentainer-size-001, whose header and footer come back once
            // in each outer column).
            if (!child.Flow.IsVertical && child.Style.Multi.IsMultiColumn &&
                !IsSingleColumnFragmentainer(child, contentInline))
            {
                return false;
            }
        }

        // A column that runs down the page does not pack floats or wrap into rows yet: both walks
        // assume the columns advance across the page.
        if (box.Flow.IsVertical && (floats > 0 || box.Style.Multi.Wraps || HoldsAnyFloat(box, box.Children)))
        {
            return false;
        }

        // A float standing among flow boxes takes its place in the column the flow reached, as any
        // other child does, and the lines after it read the room it leaves only through the band the
        // children are handed: measured on widows-orphans-020, where a full-width float ends the
        // first column and the line that follows it opens the second.

        var used = box.Style.Multi.Used(contentInline);

        // One column is the ordinary stack unless something inside it forces a break: a fragmentainer
        // of a single column still starts another column at a forced break, and the repeated
        // fragmentainer pattern of the corpus writes `columns:1` exactly for that (CSS Break 3 §3.2).
        if (used.Count <= 1 && !HoldsForcedBreak(box, contentInline))
        {
            return false;
        }

        columns = used.Count;
        share = used.Width;
        return true;
    }

    /// <summary>Gets whether the box holds a spanner, which is the pair its columns are laid out in
    /// rows of runs for (CSS Multicol 3 §3.2).</summary>
    private static Boolean HoldsSpanner(Box box)
    {
        foreach (var child in box.Children)
        {
            if (child.Style.SpansColumns)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Gets whether a box holds a descendant taken out of the flow. The descendant is anchored to the
    /// box it finds, so a column that carries the box on into the next one carries the descendant with
    /// it (CSS Position 3 §5.5); a box that holds nothing else has no content of its own to break
    /// between and is divided by the columns themselves.
    /// </summary>
    private static Boolean HoldsPositioned(Box box)
    {
        foreach (var child in box.Children)
        {
            if (PositionedLayout.IsOutOfFlow(child) || HoldsPositioned(child))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Gets whether a column may cut a flex item with nothing to break inside - an empty box
    /// of a stated size taller than the column. A flex item is the fragmentainer's to cut; a block the
    /// column meets in the flow is pushed whole instead (CSS Flexbox 1 §6 against CSS Fragmentation 3
    /// §4.3, measured: single-line-column-flex-fragmentation-071 against borders-001).</summary>
    private static Boolean CutsAsBox(
        Box child, Size size, Boolean vertical, Thickness margin, Double columnHeight, Int32 columns,
        Boolean fills, Boolean nested) =>
        columnHeight > 0 &&
        BlockExtentOf(size, vertical) > columnHeight + 0.001 &&
        !child.IsReplaced &&
        !IsMonolithic(child, vertical) &&
        child.Children.Count == 0 &&
        !(BlockSizeIsAuto(child, vertical) && !HasBlockMinimum(child, vertical) &&
            !HasBlockMaximum(child, vertical)) &&
        fills && !child.Style.Multi.IsMultiColumn &&
        child.Kind is not (BoxKind.Grid or BoxKind.Masonry) &&
        Math.Ceiling(BlockExtentOf(size, vertical) / columnHeight - 0.001) <= columns &&
        !nested && margin.Top == 0 && margin.Bottom == 0 &&
        child.Style.Breaks.Inside != BreakValue.Avoid;

    /// <summary>Gets whether a column divides a child where it runs out of room. Only a horizontal
    /// column that fills cuts one; a table is left to the fragmentainer's own table step, a nested
    /// multicol to its own columns, and a box with block margins is left whole because the room a
    /// margin takes from the first piece is not worked out here yet.</summary>
    private static Boolean DividesAcross(
        Box child, Boolean vertical, Thickness margin, Boolean fills, Boolean nested) =>
        fills && !vertical && !nested && child.Kind != BoxKind.Table &&
        !child.Style.Multi.IsMultiColumn && child.Children.Count > 0 &&
        margin.Top == 0 && margin.Bottom == 0 &&
        (HoldsPositioned(child) || child.Kind == BoxKind.Grid);

    /// <summary>Gets whether a box that runs across the page states its own length along the axis the
    /// columns stack on and holds nothing in the flow - only what has left it, and the whitespace between
    /// the tags. Such a box is cut by the columns all the same: the piece that would have shown one of its
    /// descendants stands in a column of its own, so leaving the box whole loses that descendant with it
    /// (CSS Fragmentation 3 §4.5, measured: wpt/css-break/table/table-col-paint-vlr-rtl-ref, whose
    /// 400-long relative box of absolute bands comes out 40,100,100,55 of each across its four columns).
    /// A box with children in the flow is not this one - those are the break points the run works
    /// from - and neither is one whose length the content decides.</summary>
    private static Boolean StandsOnOutFlowOnly(Box child) =>
        child.Kind is BoxKind.Block or BoxKind.Flex &&
        !BlockSizeIsAuto(child, true) &&
        child.Children.All(part => PositionedLayout.IsOutOfFlow(part) ||
            (part.Kind == BoxKind.Text && String.IsNullOrWhiteSpace(part.Text)));

}

using Hutu.Rendering.Boxes;
using System;
using System.Collections.Generic;

namespace Hutu.Rendering.Layout;

/// <summary>
/// The block formatting context: a block stacks its block-level children and resolves the box model
/// around them.
/// </summary>
/// <remarks>
/// This is the axis the whole engine is built on. It is split into the two phases the rest of the
/// engine speaks: <see cref="Measure"/> answers how large the box wants to be at a given available
/// extent along the axis its content runs on, and <see cref="Arrange"/> fixes that size into a
/// rectangle and positions the content. The split is what lets flex, grid and table ask a child for
/// its size before they know where it goes.
/// <para>
/// A block stacks its children down the page; a block whose text runs down a column stacks them
/// across it instead, so the two paths exchange the axes and little else. Which one runs is decided
/// by <see cref="ComputedStyle.Flow"/> alone.
/// </para>
/// <para>
/// Margins collapse the way CSS 2.1 §8.3.1 says: adjacent siblings, a parent and its first or last
/// in-flow child when nothing borders or pads between them, and a box with nothing inside it at all
/// - whose own top and bottom adjoin and fold into one. A box that establishes a new formatting
/// context (float, out-of-flow, overflow other than <c>visible</c>, inline-block, table part, a
/// horizontal rule) seals its children's margins inside and is not itself folded into the stack it
/// sits in; those edges stop the walk in <see cref="CollapsedTop"/> and <see cref="CollapsedBottom"/>.
/// </para>
/// </remarks>
internal sealed class BlockLayout
{
    private readonly LayoutEngine _engine;
    private readonly InlineLayout _inline;

    public BlockLayout(LayoutEngine engine, InlineLayout inline)
    {
        _engine = engine;
        _inline = inline;
    }

    /// <summary>
    /// Measures a block box, returning the size of its <em>border</em> box. No geometry is produced:
    /// the children are only asked for their sizes, which is why the result can be cached.
    /// </summary>
    public Size Measure(Box box, MeasureConstraints constraints, LayoutContext context) =>
        box.Flow.IsVertical
            ? MeasureColumn(box, constraints, context, box.Flow)
            : MeasureRow(box, constraints, context);

    private Size MeasureRow(Box box, MeasureConstraints constraints, LayoutContext context)
    {
        var style = box.Style;
        var basisWidth = constraints.AvailableWidth ?? context.ContainingBlock.Width;
        var border = BorderOf(box);
        var padding = style.Padding.Resolve(basisWidth);
        var insets = new Thickness(
            padding.Top + border.Top,
            padding.Right + border.Right,
            padding.Bottom + border.Bottom,
            padding.Left + border.Left);

        // A width stated as one of the content keywords is settled by what the box holds rather than by
        // the containing block, so it is asked for here, where both are in hand.
        var (borderBoxWidth, contentWidth) = _engine.ContentSizedWidth(box, basisWidth, context) is { } contentSized
            ? (contentSized, Math.Max(0, contentSized - insets.Horizontal))
            : BoxModel.ResolveWidth(style, basisWidth, context.ContainingBlock.Width, insets.Horizontal);
        var basisHeight = constraints.AvailableHeight ?? context.ContainingBlock.Height;
        var declaredHeight = BoxModel.DeclaredHeight(style, basisHeight, insets.Vertical);

        // CSS 2.1 §10.7: a tentative height above max-height (or below min-height) is worked out
        // again with that constraint standing in for the computed height, so the height the box
        // comes out at is settled before the content is measured - and the content is measured
        // against it, because that height is the containing block a percentage inside resolves
        // against (§10.5). A height the content decides stays zero, which is what makes such a
        // percentage come to auto.
        var childHeight = declaredHeight is { } declared
            ? BoxModel.ClampHeight(style, declared, basisHeight, insets.Vertical)
            : 0;

        Double contentHeight;

        if (box.Children.Count == 0)
        {
            contentHeight = 0;
        }
        else if (HasBlockChildren(box))
        {
            contentHeight = ColumnsOf(box, contentWidth, out var columns, out var share)
                ? MeasureMulticolChildren(box, share, childHeight, declaredHeight is not null, columns, context)
                : MeasureBlockChildren(box, contentWidth, childHeight, context);
        }
        else
        {
            // The line's own boxes - an atomic inline-block among them - stand in a containing
            // block that is this box's content box, the same one the block children are measured
            // against, so a percentage among them resolves against the height this box comes out
            // at rather than against the one its parent was given.
            contentHeight = _inline.MeasureHeight(
                box,
                contentWidth,
                new InlineContext(
                    _engine,
                    context,
                    null,
                    context.ForContainingBlock(new Size(contentWidth, childHeight))));
        }

        var height = BoxModel.ClampHeight(style, declaredHeight ?? contentHeight, basisHeight, insets.Vertical);

        return new Size(borderBoxWidth, height + insets.Vertical);
    }

    /// <summary>
    /// Measures a box whose text runs down a column. It is the same box model with the two axes
    /// exchanged: the declared <c>height</c> is the extent the text has to run along, the padding
    /// and border above and below bound it, and the <c>width</c> comes out of the content - a column
    /// is only as wide as the columns it needs, which is the opposite of the <c>auto</c> width of a
    /// block in a row.
    /// </summary>
    private Size MeasureColumn(Box box, MeasureConstraints constraints, LayoutContext context, WritingModeInfo flow)
    {
        var style = box.Style;
        var border = BorderOf(box);

        var basisInline = constraints.AvailableHeight ?? context.ContainingBlock.Height;
        var basisBlock = constraints.AvailableWidth ?? context.ContainingBlock.Width;
        var padding = style.Padding.Resolve(basisInline);
        var insetsInline = padding.Vertical + border.Vertical;
        var insetsBlock = padding.Horizontal + border.Horizontal;

        var declaredInline = BoxModel.DeclaredHeight(style, basisInline, insetsInline);
        var declaredBlock = BoxModel.DeclaredWidth(style, basisBlock, insetsBlock);

        // The children's containing block is the box's content box, and both of its extents have to
        // be settled before they are measured: the inline one is the declared height or the extent
        // the box was given, the block one the declared width or, when it comes from the content,
        // the width the box itself was measured against. The arranging pass reconstructs the same
        // pair, so a child is never measured twice under two different constraints.
        //
        // The box's own margins along the line come off that extent, exactly as they come off the
        // width a row resolves: `ResolveWidth` is where a row subtracts them, and this is the same
        // rule with the axes exchanged. A percentage height still resolves against the whole extent
        // the box was given rather than against what its margins leave, which is also what a row does.
        var (inlineBefore, inlineAfter) = flow.InlineMargins(style.Margin.Resolve(basisInline));
        var childInline = declaredInline ?? Math.Max(0, basisInline - inlineBefore - inlineAfter - insetsInline);
        var childBlock = declaredBlock ?? context.ContainingBlock.Width;

        // An extent that comes from the content only ever decides the block size. The inline size of a
        // block-level box is the one the CSS2.1 constraint equation settles: with `height: auto` the
        // other automatic values become zero and the size follows from the equality, so the box takes
        // what the containing block has left after its margins and insets - the same arithmetic
        // `ResolveWidth` runs for a row, which is the rule the writing mode exchanges the axes of.
        Double contentBlock;

        if (box.Children.Count == 0)
        {
            contentBlock = 0;
        }
        else if (HasBlockChildren(box))
        {
            contentBlock = MeasureColumnChildren(box, childInline, childBlock, context, flow);
        }
        else
        {
            contentBlock = _inline.Measure(box, childInline, flow, new InlineContext(_engine, context)).Block;
        }

        var blockSize = BoxModel.ResolveBlockExtent(style, contentBlock, basisBlock, insetsBlock);
        var inlineSize = BoxModel.ClampHeight(style, childInline, basisInline, insetsInline);

        return new Size(blockSize + insetsBlock, inlineSize + insetsInline);
    }

    /// <summary>
    /// Measures the block children of a column and returns the extent their stacking occupies along
    /// the block axis, which is how wide the band of columns they make has to be.
    /// </summary>
    /// <remarks>
    /// Nothing here answers for the inline axis: a column is as long as the space it was given, not
    /// as long as the tallest child, so what the children reach down it does not come into the size.
    /// Margins collapse along the block axis (the physical x for a column) exactly as they do in a
    /// row - a neighbour's adjoining margins fold into one strip, a child's outer edge escapes through
    /// the box when nothing borders or pads there, and a box with nothing across it self-collapses.
    /// </remarks>
    private Double MeasureColumnChildren(
        Box box,
        Double contentInline,
        Double basisBlock,
        LayoutContext context,
        WritingModeInfo flow)
    {
        var cursor = 0.0;
        var pending = 0.0;
        var throughStart = CollapsesThroughStart(box, flow, contentInline);
        var throughEnd = CollapsesThroughEnd(box, flow, contentInline);
        var childContext = context.ForContainingBlock(new Size(basisBlock, contentInline));
        var constraints = MeasureConstraints.FitWidth(basisBlock);
        var startEscapes = throughStart;

        foreach (var child in box.Children)
        {
            if (!child.IsBlockLevel || PositionedLayout.IsOutOfFlow(child))
            {
                continue;
            }

            // The margins that separate the columns are the physical left and right ones, and which of
            // the two a box starts with depends on the direction the columns run. A child turned to the
            // other axis is a wall here - it presents only its own margin, which <see
            // cref="CollapsedStart"/> already handles by not folding through it.
            var childStart = CollapsedStart(child, flow, contentInline);
            var childEnd = CollapsedEnd(child, flow, contentInline);
            var childSelfCollapses = IsSelfCollapsing(child, flow, contentInline);

            // The leading run of self-collapsing boxes escapes through the box's own start edge, and
            // the first box with extent joins that escape; the walk stops there.
            if (startEscapes)
            {
                if (!childSelfCollapses)
                {
                    startEscapes = false;
                }
            }
            else
            {
                pending = Combine(pending, childStart);
            }

            if (childSelfCollapses)
            {
                continue;
            }

            var size = _engine.Measure(child, constraints, childContext);

            cursor += pending + flow.BlockExtent(size.Width, size.Height);
            pending = childEnd;
        }

        return throughEnd ? cursor : cursor + pending;
    }

    /// <summary>
    /// Measures the extent the box's content wants along the line when nothing constrains it. A flex
    /// item with <c>flex-basis: auto</c> and a grid track sized as <c>max-content</c> both land here.
    /// </summary>
    /// <remarks>
    /// The extents are of the box rather than of its content, so they carry the box's own padding and
    /// border - and, for a column, the ones above and below it, because that is where its line runs.
    /// They are the inline extents of a box that lays its text out, and the widths of one whose text
    /// runs down a column and whose block extent is therefore what a caller sizing it wants.
    /// </remarks>
    public IntrinsicSize MeasureIntrinsic(Box box, LayoutContext context)
    {
        var style = box.Style;
        var basis = context.ContainingBlock.Width;
        var insets = InsetsOf(box, basis);

        if (box.IsReplaced)
        {
            var (width, height) = ReplacedSizing.Resolve(box, basis, context.ContainingBlock.Height);
            var outer = box.Flow.InlineExtent(width, height) + insets;
            return new IntrinsicSize(outer, outer);
        }

        // A declared width settles both extents of a row: the content cannot be any wider than the box
        // it is confined to. A column's width is its block extent and says nothing about its line. And a
        // width that is a fraction of the containing block says nothing while that block is the thing
        // being asked about - which is the state an intrinsic measurement works in - so it is left to
        // answer as `auto` and be measured from its content, as the specification has a percentage
        // against an indefinite block come to.
        if (!box.Flow.IsVertical &&
            BoxModel.SettledInlineExtent(style, basis, insets, context.BasisIsHypothetical) is { } settled)
        {
            return new IntrinsicSize(settled, settled);
        }

        return ContentIntrinsic(box, basis, insets, context);
    }

    /// <summary>
    /// The sizes the content wants once the box's own declared width is set aside. The automatic
    /// minimum size of a flex item is the smaller of this and the declared size, so the declared size
    /// must not be allowed to answer it.
    /// </summary>
    public IntrinsicSize MeasureContentIntrinsic(Box box, LayoutContext context)
    {
        var basis = context.ContainingBlock.Width;
        var insets = InsetsOf(box, basis);

        if (box.IsReplaced)
        {
            var (width, height) = ReplacedSizing.Resolve(box, basis, context.ContainingBlock.Height);
            var outer = box.Flow.InlineExtent(width, height) + insets;
            return new IntrinsicSize(outer, outer);
        }

        return ContentIntrinsic(box, basis, insets, context);
    }

    private IntrinsicSize ContentIntrinsic(Box box, Double basis, Double insets, LayoutContext context)
    {
        Double min;
        Double max;

        if (box.Children.Count == 0)
        {
            min = 0;
            max = 0;
        }
        else if (HasBlockChildren(box))
        {
            min = 0;
            max = 0;

            // The float is the exception to stacking. A block's children run down the page one after
            // another, so the widest of them is what the block asks for - but a float leaves the stack
            // and joins the line, so a container holding a row of them is as wide as the whole row.
            // That is what two badges side by side in a footer ask their list for, and what a
            // shrink-to-fit of that list has to answer: given the widest one instead, the list is made
            // too narrow for its own row and the second badge drops below the first.
            var alongside = 0.0;

            foreach (var child in box.Children)
            {
                if (!child.IsBlockLevel || PositionedLayout.IsOutOfFlow(child))
                {
                    continue;
                }

                // The children lie beside each other along the same line this box's text would run
                // down, so it is their margins along it that widen what the box asks for.
                var childIntrinsic = _engine.MeasureIntrinsic(child, context);
                var (before, after) = child.Flow.InlineMargins(child.Style.Margin.Resolve(basis));
                min = Math.Max(min, childIntrinsic.MinContent + before + after);

                // Only along a row: a column's children already lie beside each other, and a float in
                // a column keeps its place in that stack rather than leaving it.
                if (IsFloat(child) && !child.Flow.IsVertical)
                {
                    alongside += childIntrinsic.MaxContent + before + after;
                    continue;
                }

                max = Math.Max(max, childIntrinsic.MaxContent + before + after);
            }

            max = Math.Max(max, alongside);
        }
        else
        {
            // The extents a column's content asks for are measured along the line it runs down, and
            // turning them into the block-axis size a flex or grid context would want out of them is
            // left to the phase that gives those contexts an axis of their own.
            var intrinsic = _inline.MeasureIntrinsic(box, basis, box.Flow, new InlineContext(_engine, context));
            min = intrinsic.MinContent;
            max = intrinsic.MaxContent;
        }

        // A multi-column box asks for its columns side by side: the content's extent for each column,
        // and the gaps between them, for the narrowest extent as for the widest. Chrome measured:
        // `columns: 2; column-gap: 10px` over a line that asks for a hundred pixels lays the box out
        // two hundred and ten wide, and its `min-content` over a forty-pixel word is eighty and ten.
        var multi = box.Style.Multi;
        var columns = multi.Count ?? 1;

        if (!box.Flow.IsVertical && multi.IsMultiColumn && columns > 1)
        {
            var gaps = (columns - 1) * multi.Gap;
            min = min * columns + gaps;
            max = max * columns + gaps;
        }

        return new IntrinsicSize(min + insets, max + insets);
    }

    /// <summary>
    /// Gets the padding and border the box adds around its content along the axis it sets text on.
    /// </summary>
    /// <remarks>
    /// A box whose text runs down a column is bordered above and below rather than to either side, so
    /// the extents measured along that line have to be widened by the vertical pair instead. Getting
    /// this wrong takes the insets off a size that never had them - which is a column's content given
    /// slightly less room than it asked for, so that the last thing on its first line wraps away.
    /// </remarks>
    private Double InsetsOf(Box box, Double basis)
    {
        var border = BorderOf(box);
        var padding = box.Style.Padding.Resolve(basis);

        return box.Flow.IsVertical
            ? padding.Vertical + border.Vertical
            : padding.Horizontal + border.Horizontal;
    }

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
        Box box, Rect borderBox, LayoutContext context, Boolean definiteHeight = false, LineRange? lines = null)
    {
        var style = box.Style;
        var flow = box.Flow;
        var basis = context.ContainingBlock.Width;
        var border = BorderOf(box);
        var padding = style.Padding.Resolve(basis);

        var layout = new LayoutBox(box, KindOf(box))
        {
            Margin = BoxModel.UsedMargin(style, basis, borderBox.Width),
            Border = border,
            Padding = padding,
            BorderBox = borderBox,
            Flow = flow,
        };

        if (box.Children.Count == 0)
        {
            return layout;
        }

        var contentX = borderBox.X + border.Left + padding.Left;
        var contentY = borderBox.Y + border.Top + padding.Top;
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
        var usedHeight = declaredHeight is { } declared
            ? BoxModel.ClampHeight(style, declared, context.ContainingBlock.Height, insetsV)
            : definiteHeight
                ? contentHeight
                : (Double?)null;

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
                usedHeight ?? 0);

        var childContext = context.ForContainingBlock(childBox);

        // A positioned box is what an absolutely positioned descendant anchors to, so its padding
        // box - not its content box - becomes the containing block for the whole subtree.
        if (box.Style.IsTransformed)
        {
            // A transform or offset path makes this box the containing block for absolute AND fixed
            // descendants alike (CSS Transforms 1 §transform-rendering).
            childContext = childContext.WithTransformedContainingBlock(layout.BorderBox);
        }
        else if (PositionedLayout.IsPositioned(box))
        {
            childContext = childContext.WithAbsoluteContainingBlock(layout.PaddingBox);
        }

        var content = new Rect(contentX, contentY, contentWidth, contentHeight);
        var used = 0.0;

        if (HasBlockChildren(box))
        {
            used = !flow.IsVertical && ColumnsOf(box, contentWidth, out var columns, out var share)
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
                    ? ArrangeColumnChildren(box, layout, content, childContext, context.ContainingBlock.Width)
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
            var atomic = flow.IsVertical
                ? context
                : context.ForContainingBlock(new Rect(contentX, contentY, contentWidth, usedHeight ?? 0));

            var inline = new InlineContext(_engine, context, childContext, atomic);

            used = lines is { } range
                ? _inline.Layout(box, layout, content, flow, inline, range.From, range.To)
                : _inline.Layout(box, layout, content, flow, inline);
        }

        // Where the content stands along the block axis is asked once the flow has put it somewhere:
        // `align-content` moves the whole of it, and a box only has the slack to move it in when its
        // own block size came from outside rather than from the content (CSS Box Alignment 3 §5.4).
        AlignContent(box, layout, flow, flow.BlockExtent(contentWidth, contentHeight), used);

        return layout;
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
    private Double MeasureBlockChildren(Box box, Double contentWidth, Double contentHeight, LayoutContext context)
    {
        var cursor = 0.0;
        var pending = 0.0;
        var throughTop = CollapsesThroughStart(box, box.Flow, contentWidth);
        var throughBottom = CollapsesThroughEnd(box, box.Flow, contentWidth);
        var childContext = context.ForContainingBlock(new Size(contentWidth, contentHeight));
        var constraints = MeasureConstraints.FitWidth(contentWidth);
        var tracks = TracksFor(box, contentWidth, 0);
        var topEscapes = throughTop;

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
            // around it, so it is skipped before anything is measured.
            if (PositionedLayout.IsOutOfFlow(child))
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
                var (placed, _) = PlaceFloat(child, floats, cursor + pending, contentWidth, childContext);
                directBottom = Math.Max(directBottom, placed.Bottom + margin.Bottom);
                continue;
            }

            var childTop = CollapsedStart(child, box.Flow, contentWidth);
            var childBottom = CollapsedEnd(child, box.Flow, contentWidth);
            var childSelfCollapses = IsSelfCollapsing(child, box.Flow, contentWidth);

            // While the leading run of self-collapsing boxes continues, every top the walk meets
            // is already part of this box's own reported collapsed top and escapes upward through
            // it. The chain ends at the first box that has height of its own, and that box's top
            // joins the escape too - the run adjoins all the way down to it.
            if (topEscapes)
            {
                if (!childSelfCollapses)
                {
                    topEscapes = false;
                }
            }
            else
            {
                pending = Combine(pending, childTop);
            }

            if (childSelfCollapses)
            {
                // A zero-height box sits inside the pending strip without advancing the cursor: its
                // own combined margin has just been added, and the run carries it on to whichever
                // box follows. Nothing here is a place in the stack the box occupies.
                continue;
            }

            // The child's lines stand in the band of the floats around it, at the height the child
            // itself reaches: two siblings at different depths have different room beside a float,
            // so the band is answered per child rather than by the one context the container shares.
            var (shift, room, drop, band) = FormattingRoot(
                child,
                BandFor(child, tracks, context.Floats, cursor + pending, margin.Left, 0, 0, contentWidth),
                contentWidth);

            var passContext = childContext.WithFloats(band);
            var childConstraints = constraints;

            // A box that steps clear of the floats is measured against the room that stepping leaves it,
            // and the arranging pass runs the same arithmetic to read the size back out of the cache.
            if (shift > 0)
            {
                passContext = passContext.ForContainingBlock(
                    new Size(room, childContext.ContainingBlock.Height));
                childConstraints = MeasureConstraints.FitWidth(room);
            }

            // A cleared box is moved below the floats it refuses to stand beside. What it moved
            // through is not given back to the surrounding margins - the stack resumes from where
            // the box landed.
            if (tracks is { } cleared && child.Style.Clear != ClearSide.None)
            {
                var clearedTo = cleared.ClearBelow(child.Style.Clear, cursor + pending);
                pending = Math.Max(pending, clearedTo - cursor);
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
                tracks.Absorb(
                    held,
                    usedMargin.Left + shift + childBorder.Left + childPadding.Left,
                    cursor + pending + drop + childBorder.Top + childPadding.Top);
            }

            cursor += pending + drop + size.Height;
            pending = childBottom;
        }

        // The trailing strip either escaped through the box (already in the collapsed bottom this
        // box reports to its parent) or has to be reserved inside this box's content height. A
        // container that holds floats still widens its height for them whatever the margin stack
        // says about the last box in it.
        var stacked = throughBottom ? cursor : cursor + pending;

        // Published for the container above, which absorbs these when it holds no formatting
        // context of its own - the floats belong to the nearest ancestor that does.
        _engine.PublishFloats(box, tracks);

        return Math.Max(stacked, directBottom);
    }

    /// <summary>
    /// Gets whether the box lays its block-level children out in more than one column, and over how
    /// many: a horizontal multi-column container with room for columns. A container holding a float or
    /// a box that left the flow keeps the ordinary stack for now - neither can be put in a column yet.
    /// </summary>
    private static Boolean ColumnsOf(Box box, Double contentWidth, out Int32 columns, out Double share)
    {
        columns = 1;
        share = contentWidth;

        if (box.Flow.IsVertical || !box.Style.Multi.IsMultiColumn)
        {
            return false;
        }

        foreach (var child in box.Children)
        {
            if (!child.IsBlockLevel)
            {
                continue;
            }

            if (child.Style.Float != FloatSide.None || PositionedLayout.IsOutOfFlow(child))
            {
                return false;
            }

            // A spanner spans the whole row of columns, and a multi-column child balances its own
            // columns inside one column of this one. Neither can be put in a column yet, so the box
            // keeps the ordinary stack - one column's worth - until they can.
            if (child.Style.SpansColumns || (!child.Flow.IsVertical && child.Style.Multi.IsMultiColumn))
            {
                return false;
            }
        }

        var used = box.Style.Multi.Used(contentWidth);

        if (used.Count <= 1)
        {
            return false;
        }

        columns = used.Count;
        share = used.Width;
        return true;
    }

    /// <summary>
    /// Measures the block children of a multi-column box against the width of one column and answers the
    /// height the columns come to: the <c>column-fill: auto</c> height the box states, or the least
    /// extent a balanced fill of its columns needs (CSS Multicol 3 §3.4).
    /// </summary>
    private Double MeasureMulticolChildren(
        Box box,
        Double share,
        Double declaredHeight,
        Boolean heightStated,
        Int32 columns,
        LayoutContext context)
    {
        var children = MulticolChildren(box, share, declaredHeight, context);

        // Nothing here takes part in the floats of the boxes around it yet; the container holds none of
        // its own, which is what let it take this path at all.
        _engine.PublishFloats(box, null);

        var multi = box.Style.Multi;

        // With wrapping, a column box is as tall as `column-height` and the content continues in another
        // box rather than running down one: the height the content comes to is the rows it needed
        // (CSS Multicol 2 §3). A container that stated its own height keeps it.
        if (multi.Wraps && multi.Height is { } columnHeight && columnHeight > 0)
        {
            if (heightStated && declaredHeight is { } stated)
            {
                return stated;
            }

            var total = 0.0;

            foreach (var child in children)
            {
                total += child.Fragment.Height;
            }

            var boxes = Math.Max(1, (Int32)Math.Ceiling(total / columnHeight));
            var rows = (Int32)Math.Ceiling((Double)boxes / columns);

            return rows * (columnHeight + multi.RowGap) - multi.RowGap;
        }

        return MulticolExtent(multi, columns, heightStated ? declaredHeight : null, children);
    }

    /// <summary>
    /// One thing a column takes: a whole child, or one of the lines of a child that is broken across the
    /// columns rather than moved from one to the other in one piece.
    /// </summary>
    private readonly struct MulticolChild
    {
        public MulticolChild(
            Box box, Size size, Thickness margin, FragmentChild fragment, Int32 line, Boolean last)
        {
            Box = box;
            Size = size;
            Margin = margin;
            Fragment = fragment;
            Line = line;
            Last = last;
        }

        public Box Box { get; }

        public Size Size { get; }

        public Thickness Margin { get; }

        /// <summary>Gets the piece as the column slicer sees it: its outer height and its break values.</summary>
        public FragmentChild Fragment { get; }

        /// <summary>
        /// Gets which line of the child's own content this piece is, or -1 for a child that stands whole.
        /// Two pieces of one child that land in the same column are one fragment of it.
        /// </summary>
        public Int32 Line { get; }

        /// <summary>
        /// Gets whether this is the last piece of the child, which is the one that carries the margin and
        /// the break value the child asks for after itself.
        /// </summary>
        public Boolean Last { get; }

        /// <summary>Gets whether the child is broken across the columns.</summary>
        public Boolean Broken => Line >= 0;
    }

    /// <summary>
    /// Gets whether a child of a multi-column box can be broken at its own lines. Only a block whose
    /// content is a single inline formatting context has lines to break at here - a block with block
    /// children breaks inside those first, which is the next step of the fragmentainer - and only one
    /// that brings nothing to be drawn or honoured at the edges of a fragment: a height the box states
    /// for itself, padding, a border or a background would each have to be shared over the fragments by
    /// <c>box-decoration-break</c> (CSS Break 3 §4.7), which the columns do not do yet, so such a child
    /// stays whole rather than be painted wrong.
    /// </summary>
    private static Boolean FragmentsInColumns(Box child, Double basis)
    {
        var style = child.Style;

        return !HasBlockChildren(child) &&
            !child.IsReplaced &&
            style.Height.IsAuto &&
            !style.MinHeight.HasValue &&
            !style.MaxHeight.HasValue &&
            style.Breaks.Inside == BreakValue.Auto &&
            style.Backgrounds.Count == 0 &&
            style.BackgroundColor is not { A: > 0 } &&
            style.Padding.Resolve(basis).Vertical == 0 &&
            BorderOf(child).Vertical == 0;
    }

    private List<MulticolChild> MulticolChildren(Box box, Double share, Double declaredHeight, LayoutContext context)
    {
        var childContext = context.ForContainingBlock(new Size(share, declaredHeight));
        var constraints = MeasureConstraints.FitWidth(share);
        var children = new List<MulticolChild>(box.Children.Count);

        // Which child a run of lines belongs to, so that the slicer can tell one block's lines from its
        // neighbour's and keep the orphans and widows each of them asks for.
        var runs = 0;

        foreach (var child in box.Children)
        {
            if (!child.IsBlockLevel || PositionedLayout.IsOutOfFlow(child))
            {
                continue;
            }

            var size = _engine.Measure(child, constraints, childContext);
            var used = BoxModel.UsedMargin(child.Style, share, size.Width);

            // What stands before and after the child is the margin it presents to the stack, not only
            // the one it wrote: a first child's margin travels out through a parent that does not seal
            // it, and the column has to leave that room too.
            var margin = new Thickness(
                CollapsedStart(child, box.Flow, share),
                used.Right,
                CollapsedEnd(child, box.Flow, share),
                used.Left);
            var breaks = child.Style.Breaks;

            // A child that can be broken contributes one piece per line of its own, so the column ends
            // between two of them (CSS Multicol 3 §3.1). The lines are those the plan makes at the width
            // of a column, which is the plan the arrangement lays out again - the same container at the
            // same extent, so the two passes agree without being told to.
            if (FragmentsInColumns(child, share))
            {
                var run = runs++;
                var lines = _inline.LineHeights(
                    child, share, child.Flow, new InlineContext(_engine, childContext, childContext, childContext));

                for (var line = 0; line < lines.Count; line++)
                {
                    var last = line == lines.Count - 1;

                    children.Add(new MulticolChild(
                        child,
                        new Size(size.Width, lines[line]),
                        new Thickness(
                            line == 0 ? margin.Top : 0,
                            margin.Right,
                            last ? margin.Bottom : 0,
                            margin.Left),
                        new FragmentChild(
                            lines[line] + (line == 0 ? margin.Top : 0) + (last ? margin.Bottom : 0),
                            line == 0 ? breaks.Before : BreakValue.Auto,
                            last ? breaks.After : BreakValue.Auto,
                            BreakValue.Auto,
                            run,
                            breaks.Orphans,
                            breaks.Widows),
                        line,
                        last));
                }

                continue;
            }

            children.Add(new MulticolChild(
                child,
                size,
                margin,
                new FragmentChild(size.Height + margin.Top + margin.Bottom, breaks.Before, breaks.After, breaks.Inside),
                -1,
                last: true));
        }

        return children;
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

            var total = 0.0;

            foreach (var child in children)
            {
                total += child.Fragment.Height;
            }

            return total;
        }

        var fragments = new List<FragmentChild>(children.Count);

        foreach (var child in children)
        {
            fragments.Add(child.Fragment);
        }

        return Fragmentainer.Balance(fragments, columns);
    }

    /// <summary>Gets whether the child is pinned to an edge of its container instead of stacked in it.</summary>
    private static Boolean IsFloat(Box child) => child.Style.Float != FloatSide.None;

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

        // Nothing that ended above this box can narrow a line inside it: the floats in a band are placed
        // at or below the content they were met after, so once the deepest of them is level with the
        // box's own content top, the box and everything stacked in it reads at full width. Without this
        // a single float near the top of a column hands a band to every block below it, and every one of
        // them breaks its lines twice for a float it will never touch.
        if (tracks.Bottom <= originY)
        {
            return null;
        }

        return new FloatBand(tracks, originY, originLeft);
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
        if (band is null || !(child.Style.ClipsOverflow ||
                              child.Style.Display.Contains("flow-root", StringComparison.OrdinalIgnoreCase)))
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

        var shift = Math.Max(0, left - band.Left);
        var room = Math.Max(0, Math.Min(contentWidth, right - band.Left) - shift);
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
    /// Sizes a float against the room its edge leaves and reserves that room in the tracks. Both
    /// phases call this with the same numbers, so the width the arranging phase reads out of the
    /// measurement cache is the one the container's height was answered from.
    /// </summary>
    private (Rect BorderBox, Size Size) PlaceFloat(
        Box child,
        FloatTracks tracks,
        Double top,
        Double contentWidth,
        LayoutContext childContext)
    {
        var style = child.Style;
        var margin = style.Margin.Resolve(contentWidth);
        var y = tracks.ClearBelow(style.Clear, top);
        var (freeLeft, freeRight) = tracks.FreeSpanAt(y, 0);

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
        var box = tracks.Place(style.Float, size.Width, size.Height, margin, y);

        return (box, size);
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
        var throughTop = CollapsesThroughStart(box, box.Flow, contentWidth);
        var constraints = MeasureConstraints.FitWidth(contentWidth);
        var tracks = TracksFor(box, contentWidth, contentX);
        var topEscapes = throughTop;

        foreach (var child in box.Children)
        {
            if (!child.IsBlockLevel)
            {
                continue;
            }

            var margin = child.Style.Margin.Resolve(contentWidth);

            if (tracks is { } floats && IsFloat(child))
            {
                // The same walk the measuring phase ran, at the same numbers: the width it reads out
                // of the cache is the one the container's height was answered from, and the place it
                // reserves is the one a clearing box below it is measured against. A float stands
                // outside the margin collapse on either side, but the collapsed margin the run above
                // has built up still stands between it and that box - CSS 2.1 §8.3.1 folds no margin
                // into a float's - so the float's outer edge starts below the strip, at the place
                // the next box in flow would have taken.
                var placed = PlaceFloat(child, floats, cursor + pending, contentWidth, context);
                parent.Children.Add(_engine.Arrange(child, placed.BorderBox, context));
                continue;
            }

            var childTop = CollapsedStart(child, box.Flow, contentWidth);
            var childBottom = CollapsedEnd(child, box.Flow, contentWidth);
            var childSelfCollapses = IsSelfCollapsing(child, box.Flow, contentWidth);

            // Same rule the measuring pass followed: while the leading run of self-collapsing
            // boxes continues, every top has already escaped through the parent's own collapsed
            // top, and the walk stops that escape at the first box with height.
            if (topEscapes)
            {
                if (!childSelfCollapses)
                {
                    topEscapes = false;
                }
            }
            else
            {
                pending = Combine(pending, childTop);
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
                parent.Children.Add(_engine.ArrangePositioned(
                    child,
                    new Offset(contentX + margin.Left, cursor + pending),
                    context));

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

                // CSS 2.1 §8.3.1 gives a self-collapsing box one of two places: its top border edge
                // is the parent's own when its top margin collapses with the parent's (the escape
                // this walk has already stopped combining for, which leaves the strip empty), and
                // otherwise - the parent seals a formatting context and takes no part in it - the
                // place it would have had with a non-zero bottom border, which is after its own top
                // margin. Standing it at the cursor either way puts it at the parent's edge in the
                // second case too: a `margin-top` box that opens a grid item sat 24 pixels above
                // where a browser draws it.
                var rect0 = new Rect(contentX + used0.Left, cursor + pending, size0.Width, size0.Height);
                parent.Children.Add(_engine.Arrange(child, rect0, context));
                continue;
            }

            // The band and the step clear of the floats are the measuring pass's own arithmetic, run
            // again at the place the child lands, so the size read back is the one it was answered with.
            var (shift, room, drop, band) = FormattingRoot(
                child,
                BandFor(
                    child, tracks, context.Floats,
                    cursor + pending, contentX + margin.Left, contentX, contentY, contentWidth),
                contentWidth);

            var passContext = context.WithFloats(band);
            var childConstraints = constraints;

            if (shift > 0)
            {
                passContext = passContext.ForContainingBlock(new Size(room, context.ContainingBlock.Height));
                childConstraints = MeasureConstraints.FitWidth(room);
            }

            if (tracks is { } cleared && child.Style.Clear != ClearSide.None)
            {
                var clearedTo = cleared.ClearBelow(child.Style.Clear, cursor + pending);
                pending = Math.Max(pending, clearedTo - cursor);
            }

            var size = _engine.Measure(child, childConstraints, passContext);
            var used = BoxModel.UsedMargin(child.Style, contentWidth, size.Width);
            var rect = new Rect(contentX + used.Left + shift, cursor + pending + drop, size.Width, size.Height);

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
                tracks.Absorb(held, 0, 0);
            }

            cursor = rect.Bottom;
            pending = childBottom;
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
        var children = MulticolChildren(box, share, heightStated ? declaredHeight : 0, context);
        var multi = box.Style.Multi;
        var extent = MulticolExtent(multi, columns, heightStated ? declaredHeight : null, children);
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
        var extents = new Double[Math.Max(1, wraps ? columns * rows : multi.Fills ? children.Count : columns)];
        Array.Fill(extents, boxHeight);

        var counts = Fragmentainer.Fill(fragments, extents);
        var childContext = context.ForContainingBlock(new Size(share, wraps ? boxHeight : heightStated ? declaredHeight : 0));
        var from = 0;

        for (var cell = 0; cell < counts.Count; cell++)
        {
            var column = cell % columns;
            var row = cell / columns;
            var x = content.X + column * (share + multi.Gap);
            var y = content.Y + row * (boxHeight + multi.RowGap);

            for (var index = 0; index < counts[cell]; index++)
            {
                var child = children[from++];

                // A column box begins a fresh stack: what a child brings to its top edge is added there
                // rather than folded into the margin run the box before ended with.
                y += child.Margin.Top;

                var width = child.Size.Width;
                var height = child.Size.Height;

                // The lines of one broken child that fall in the same column are one fragment of it:
                // they stand in the box drawn once for them, and only the fragment's own first and last
                // line carry the child's edges.
                var to = child.Broken ? child.Line + 1 : -1;

                while (child.Broken && index + 1 < counts[cell] && children[from].Broken &&
                    ReferenceEquals(children[from].Box, child.Box))
                {
                    var next = children[from++];
                    index++;
                    height += next.Size.Height;
                    to = next.Line + 1;
                }

                var rect = new Rect(x + child.Margin.Left, y, width, height);

                parent.Children.Add(_engine.Arrange(
                    child.Box,
                    rect,
                    childContext,
                    definiteHeight: false,
                    lines: child.Broken ? new LineRange(child.Line, to) : null));
                y += height + child.Margin.Bottom;
            }
        }

        _engine.PublishFloats(box, null);

        return extent;
    }

    /// <summary>
    /// Arranges the block children of a column: they stack along the physical x axis rather than down
    /// the page, so the cursor walks sideways and the margins that separate them are the left and the
    /// right one.
    /// </summary>
    /// <param name="basisBlock">
    /// The extent the children were measured against. It has to be the same one the measuring pass
    /// used, or a child would be measured a second time under a different constraint and its geometry
    /// would no longer agree with the extent the parent sized itself to.
    /// </param>
    private Double ArrangeColumnChildren(
        Box box,
        LayoutBox parent,
        Rect content,
        LayoutContext context,
        Double basisBlock)
    {
        var flow = box.Flow;
        var contentInline = flow.InlineExtent(content.Width, content.Height);
        var cursor = 0.0;
        var pending = 0.0;
        var throughStart = CollapsesThroughStart(box, flow, contentInline);
        var constraints = MeasureConstraints.FitWidth(basisBlock);
        var outOfFlow = new List<(Box Box, Offset Static)>();
        var startEscapes = throughStart;

        foreach (var child in box.Children)
        {
            if (!child.IsBlockLevel)
            {
                continue;
            }

            var margin = child.Style.Margin.Resolve(contentInline);
            var (beforeIt, afterIt) = flow.InlineMargins(margin);

            var childStart = CollapsedStart(child, flow, contentInline);
            var childEnd = CollapsedEnd(child, flow, contentInline);
            var childSelfCollapses = IsSelfCollapsing(child, flow, contentInline);

            if (startEscapes)
            {
                if (!childSelfCollapses)
                {
                    startEscapes = false;
                }
            }
            else
            {
                pending = Combine(pending, childStart);
            }

            if (PositionedLayout.IsOutOfFlow(child))
            {
                // Same as in a row: the box takes no space, but the place it would have had is kept
                // as the static position an unset offset falls back to.
                var anchor = flow.Map(content, beforeIt, 0, cursor + pending, 0);
                outOfFlow.Add((child, new Offset(anchor.X, anchor.Y)));
                continue;
            }

            var size = _engine.Measure(child, constraints, context);
            var blockSize = flow.BlockExtent(size.Width, size.Height);
            var inlineSize = flow.InlineExtent(size.Width, size.Height);

            // The margin that opens the child is where it begins down its column, the way a box in a
            // row begins after its left margin.
            var rect = flow.Map(content, beforeIt, inlineSize, cursor + pending, blockSize);

            parent.Children.Add(_engine.Arrange(child, rect, context));

            if (childSelfCollapses)
            {
                // A box that self-collapses has nothing across it: it sits at the cursor the stack
                // has reached and takes no step sideways, and its own combined margin - already in
                // the pending run - carries on to whichever box follows. Same as in a row.
                continue;
            }

            cursor += pending + blockSize;
            pending = childEnd;
        }

        foreach (var (child, staticPosition) in outOfFlow)
        {
            parent.Children.Add(_engine.ArrangePositioned(child, staticPosition, context));
        }

        // The extent the children occupied along the column, in the same units the box states its own
        // block size in, for the same reason as in a row.
        return Math.Max(0, cursor + pending);
    }

    /// <summary>Moves the content along the block axis to where <c>align-content</c> asks it to stand.</summary>
    /// <remarks>
    /// A block container lays its content out as one subject, so this moves the whole of it rather than
    /// sharing space out between several: <c>space-between</c> falls back to the start, and
    /// <c>space-around</c> and <c>space-evenly</c> to the middle, as their fallback alignments say
    /// (CSS Box Alignment 3 §5.4). <paramref name="room"/> is the content box's own extent along the
    /// axis, so a box that sized itself to its content keeps the content where the flow put it - there
    /// is no slack - while a box that was handed a height, either declared or stretched to it by a flex
    /// or grid container, has the slack to move it in. That last case is the one the HTML rendering
    /// rules use to centre a form control's label in the box the control was given (§15.3.10).
    /// <para>
    /// A box that left the flow is none of this method's business: it was placed against its own
    /// containing block rather than stacked with the content, so it neither counts towards the extent
    /// nor moves. The table context aligns a cell's content the same way.
    /// </para>
    /// </remarks>
    private static void AlignContent(Box box, LayoutBox container, WritingModeInfo flow, Double room, Double used)
    {
        var free = room - used;

        if (free <= 0)
        {
            return;
        }

        var shift = box.Style.AlignContent switch
        {
            FlexAlignment.Center or FlexAlignment.SpaceAround or FlexAlignment.SpaceEvenly => free / 2,
            FlexAlignment.End => free,
            _ => 0.0,
        };

        if (shift <= 0)
        {
            return;
        }

        // The axis the shift runs on: in a column the block axis is the physical x one, and it grows
        // leftward in the two right-to-left modes.
        var (dx, dy) = flow.IsVertical ? (flow.BlockSign * shift, 0.0) : (0.0, shift);

        foreach (var child in container.Children)
        {
            if (!child.IsOutOfFlow)
            {
                Shift(child, dx, dy);
            }
        }
    }

    /// <summary>Moves a box and its whole in-flow subtree along the block axis by the given distance.</summary>
    private static void Shift(LayoutBox box, Double dx, Double dy)
    {
        box.BorderBox = new Rect(box.BorderBox.X + dx, box.BorderBox.Y + dy, box.BorderBox.Width, box.BorderBox.Height);

        foreach (var child in box.Children)
        {
            // An out-of-flow descendant inside the content - an absolutely positioned box in a line,
            // say - was placed against its own containing block and is not part of what was aligned.
            if (!child.IsOutOfFlow)
            {
                Shift(child, dx, dy);
            }
        }
    }

    public static Boolean HasBlockChildren(Box box)
    {
        foreach (var child in box.Children)
        {
            // A child taken out of flow by `absolute` or `fixed` is not one of them: it is laid out
            // against a containing block of its own and takes no part in the lines or the stack this box
            // forms. Counting it would send a box whose only "block" child is out of flow down the block
            // path, where that child's static position becomes the block cursor - a fresh line - instead
            // of the place in the inline flow it would have had.
            if (child.IsBlockLevel && !PositionedLayout.IsOutOfFlow(child))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Gets the layout role the box plays. A table part is normally arranged by the table context,
    /// but a stray one that leaked into normal flow still has to announce what it is so the painter
    /// gives it the right treatment - a cell's background, for instance, is painted like a block's.
    /// </summary>
    private static LayoutKind KindOf(Box box) => box.Kind switch
    {
        // An inline-block is arranged here like any other block container: it is the line that placed
        // it, and not the box, that makes it inline-level in the first place.
        BoxKind.InlineBlock => LayoutKind.Block,
        BoxKind.HorizontalRule => LayoutKind.HorizontalRule,
        BoxKind.Table => LayoutKind.Table,
        BoxKind.TableRowGroup => LayoutKind.TableRowGroup,
        BoxKind.TableRow => LayoutKind.TableRow,
        BoxKind.TableCell => LayoutKind.TableCell,
        _ => LayoutKind.Block,
    };

    /// <summary>
    /// Gets the used border widths. A horizontal rule whose author left it borderless still draws
    /// the one-pixel line the element is for.
    /// </summary>
    public static Thickness BorderOf(Box box)
    {
        var style = box.Style;
        var border = new Thickness(
            style.BorderTopWidth,
            style.BorderRightWidth,
            style.BorderBottomWidth,
            style.BorderLeftWidth);

        return box.Kind == BoxKind.HorizontalRule && border.Horizontal == 0 && border.Vertical == 0
            ? new Thickness(1, 0, 0, 0)
            : border;
    }

    /// <summary>
    /// Folds two adjoining margins the way CSS 2.1 §8.3.1 says a run of them collapses: the greatest
    /// positive value plus the smallest negative one. A run of margins folds the same way however the
    /// pairs are bracketed, which is what lets the stack accumulate them one neighbour at a time.
    /// </summary>
    private static Double Combine(Double left, Double right) =>
        Math.Max(Math.Max(left, right), 0) + Math.Min(Math.Min(left, right), 0);

    /// <summary>
    /// Gets whether the box establishes a new block formatting context: it seals its children's
    /// margins inside itself and none of its own escapes upward. Such a box is a hard wall for the
    /// collapse walk - the chain stops here.
    /// </summary>
    /// <remarks>
    /// A float and an out-of-flow box also establish a formatting context, but every caller here
    /// already skips them before consulting this, so it does not ask about them.
    /// <para>
    /// The root element is folded in as well: CSS 2.1 §8.3.1 leaves the margins of the root's box
    /// out of the collapse with its children, so the collapsed margin of the page's first box stays
    /// inside the root rather than escaping above it and being lost to the viewport. That is what
    /// begins a page a body's worth of margin down from the top edge.
    /// </para>
    /// <para>
    /// A grid or flex item is one of these too, though nothing in its own style says so: the box a
    /// container lays out as an item establishes an independent formatting context for its contents
    /// (CSS Flexbox 1 §4, CSS Grid 1 §6.1), so the margins inside it are its own to hold. Folding
    /// them out instead loses the room a page asked for: Wikipedia's sitenotice is a `margin: 24px 0`
    /// box inside a grid item, and collapsing that margin out of the page's header grid took the
    /// whole page up by 24 pixels.
    /// </para>
    /// </remarks>
    private static Boolean EstablishesContext(Box box) =>
        box.Element?.LocalName == "html" ||
        box.IsItem ||
        box.Style.ClipsOverflow ||
        box.Style.Display.Contains("flow-root", StringComparison.OrdinalIgnoreCase) ||
        box.Kind is BoxKind.InlineBlock or BoxKind.HorizontalRule or BoxKind.Flex or BoxKind.Grid or
                    BoxKind.Table or BoxKind.TableRowGroup or BoxKind.TableRow or BoxKind.TableCell or
                    BoxKind.TableCaption;

    /// <summary>
    /// Gets whether the box seals a block formatting context of its own for the collapse walk: it
    /// establishes one, is taken out of the flow, or is floated. Such a box presents only its own
    /// margins and never lets a child's escape through it.
    /// </summary>
    private static Boolean IsWall(Box box) =>
        EstablishesContext(box) || PositionedLayout.IsOutOfFlow(box) || IsFloat(box);

    /// <summary>
    /// Gets whether the child stacks its own block children along the same physical axis
    /// <paramref name="flow"/> runs on, which is what lets a margin travel through the child along
    /// that axis. A child turned to the other axis (a row inside a column, or the reverse) lays its
    /// children out along a different edge, so nothing of theirs reaches the edge this walk collapses
    /// on - the child presents only its own margin there.
    /// </summary>
    private static Boolean SameBlockAxis(Box child, WritingModeInfo flow) =>
        child.Flow.IsVertical == flow.IsVertical && child.Flow.BlockSign == flow.BlockSign;

    /// <summary>
    /// The size the box declares along the axis <paramref name="flow"/> stacks on, or null when it
    /// leaves that to the content. A value above zero pins the far edge away from the near one and so
    /// keeps the two margins from adjoining.
    /// </summary>
    private static Double? DeclaredBlockExtent(Box box, WritingModeInfo flow, Double basis)
    {
        var border = BorderOf(box);
        var padding = box.Style.Padding.Resolve(basis);

        return flow.IsVertical
            ? BoxModel.DeclaredWidth(box.Style, basis, padding.Horizontal + border.Horizontal)
            : BoxModel.DeclaredHeight(box.Style, basis, padding.Vertical + border.Vertical);
    }

    /// <summary>
    /// Gets whether the box self-collapses along <paramref name="flow"/>: nothing sits between its two
    /// block-axis edges - no border, no padding, no in-flow content of its own, no declared extent
    /// above zero - so its start and end margins adjoin and fold into one strip that occupies nothing
    /// along the stack.
    /// </summary>
    private static Boolean IsSelfCollapsing(Box box, WritingModeInfo flow, Double basis)
    {
        if (IsWall(box) || box.IsReplaced || !SameBlockAxis(box, flow))
        {
            return false;
        }

        var border = BorderOf(box);
        var padding = box.Style.Padding.Resolve(basis);
        var (startBorder, endBorder) = flow.BlockMargins(border);
        var (startPadding, endPadding) = flow.BlockMargins(padding);

        if (startBorder > 0 || endBorder > 0 || startPadding > 0 || endPadding > 0)
        {
            return false;
        }

        if (DeclaredBlockExtent(box, flow, basis) is > 0)
        {
            return false;
        }

        foreach (var child in box.Children)
        {
            // Only a box taken clean out of the flow leaves the run untouched. A float does not: this
            // engine grows a container to the depth of the floats it holds, so a box holding one has an
            // extent of its own and its two block-axis margins do not adjoin.
            if (PositionedLayout.IsOutOfFlow(child))
            {
                continue;
            }

            // An inline-level child generates a line box, and a line box occupies the block axis; a
            // block-level child keeps the box from self-collapsing unless that child collapses in turn.
            if (!child.IsBlockLevel || IsFloat(child) || !IsSelfCollapsing(child, flow, basis))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// The margin the box presents on the block-start side for collapsing with what stands before it
    /// along <paramref name="flow"/>: its own start margin folded through every leading self-collapsing
    /// child and finally through the start of the first child that has extent - the strip that moves
    /// outward through the box when nothing separates them, and what the box's stack reads as its
    /// outer margin on that side.
    /// </summary>
    private static Double CollapsedStart(Box box, WritingModeInfo flow, Double basis)
    {
        var (own, ownEnd) = flow.BlockMargins(box.Style.Margin.Resolve(basis));

        // A wall, or a box turned to the other axis, presents only its own margin: nothing travels
        // through it along the axis this walk collapses on.
        if (IsWall(box) || !SameBlockAxis(box, flow))
        {
            return own;
        }

        var border = BorderOf(box);
        var padding = box.Style.Padding.Resolve(basis);
        var (startBorder, _) = flow.BlockMargins(border);
        var (startPadding, _) = flow.BlockMargins(padding);

        if (startBorder > 0 || startPadding > 0)
        {
            return own;
        }

        var combined = own;

        foreach (var child in box.Children)
        {
            if (!child.IsBlockLevel || PositionedLayout.IsOutOfFlow(child) || IsFloat(child))
            {
                continue;
            }

            combined = Combine(combined, CollapsedStart(child, flow, basis));

            if (!IsSelfCollapsing(child, flow, basis))
            {
                return combined;
            }
        }

        // Every in-flow block child was self-collapsing (or there were none), so the walk reached the
        // far end of the stack. If the box itself self-collapses, its end margin adjoins the run too
        // and the strip is one value the box reports on both sides.
        if (IsSelfCollapsing(box, flow, basis))
        {
            combined = Combine(combined, ownEnd);
        }

        return combined;
    }

    /// <summary>
    /// The margin the box presents on the block-end side: the mirror of <see cref="CollapsedStart"/>,
    /// walking backward through the trailing self-collapsing children.
    /// </summary>
    private static Double CollapsedEnd(Box box, WritingModeInfo flow, Double basis)
    {
        var (own, ownEnd) = flow.BlockMargins(box.Style.Margin.Resolve(basis));

        if (IsWall(box) || !SameBlockAxis(box, flow))
        {
            return ownEnd;
        }

        var border = BorderOf(box);
        var padding = box.Style.Padding.Resolve(basis);
        var (_, endBorder) = flow.BlockMargins(border);
        var (_, endPadding) = flow.BlockMargins(padding);

        if (endBorder > 0 || endPadding > 0)
        {
            return ownEnd;
        }

        // A box that declares its extent along the stacking axis - a fixed height on a row - pins its
        // far edge away from its content, so its last child's bottom margin collapses into that fixed
        // region instead of adjoining the box's own end edge and escaping below it. Giving a container
        // a height is the classic way to keep its children's margins from leaking out; the fixed-height
        // reftests count on it - the box after such a parent sits at the parent's bottom edge, not a
        // child margin further down. (An auto extent says nothing here, so the walk folds through.)
        if (DeclaredBlockExtent(box, flow, basis) is > 0)
        {
            return ownEnd;
        }

        var combined = ownEnd;

        for (var i = box.Children.Count - 1; i >= 0; i--)
        {
            var child = box.Children[i];

            if (!child.IsBlockLevel || PositionedLayout.IsOutOfFlow(child) || IsFloat(child))
            {
                continue;
            }

            combined = Combine(combined, CollapsedEnd(child, flow, basis));

            if (!IsSelfCollapsing(child, flow, basis))
            {
                return combined;
            }
        }

        if (IsSelfCollapsing(box, flow, basis))
        {
            combined = Combine(combined, own);
        }

        return combined;
    }

    /// <summary>
    /// Gets whether the box lets its first in-flow child's start margin escape through its own
    /// block-start edge: the box stacks along <paramref name="flow"/>, seals no formatting context of
    /// its own, and has no border or padding on that edge.
    /// </summary>
    private static Boolean CollapsesThroughStart(Box box, WritingModeInfo flow, Double basis)
    {
        if (IsWall(box) || !SameBlockAxis(box, flow))
        {
            return false;
        }

        var border = BorderOf(box);
        var padding = box.Style.Padding.Resolve(basis);
        var (startBorder, _) = flow.BlockMargins(border);
        var (startPadding, _) = flow.BlockMargins(padding);

        return startBorder == 0 && startPadding == 0;
    }

    /// <summary>The mirror of <see cref="CollapsesThroughStart"/>, for the last child's end margin.</summary>
    private static Boolean CollapsesThroughEnd(Box box, WritingModeInfo flow, Double basis)
    {
        if (IsWall(box) || !SameBlockAxis(box, flow))
        {
            return false;
        }

        var border = BorderOf(box);
        var padding = box.Style.Padding.Resolve(basis);
        var (_, endBorder) = flow.BlockMargins(border);
        var (_, endPadding) = flow.BlockMargins(padding);

        return endBorder == 0 && endPadding == 0;
    }
}

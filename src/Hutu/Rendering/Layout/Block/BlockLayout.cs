using AngleSharp.Css.Dom;
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
internal sealed partial class BlockLayout
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
    public Size Measure(
        Box box,
        MeasureConstraints constraints,
        LayoutContext context,
        Thickness? inlineMarginOverride = null) =>
        box.Flow.IsVertical
            ? MeasureColumn(box, constraints, context, box.Flow, inlineMarginOverride)
            : MeasureRow(box, constraints, context);

    private Size MeasureRow(Box box, MeasureConstraints constraints, LayoutContext context)
    {
        var style = box.Style;
        var basisWidth = constraints.AvailableWidth ?? context.ContainingBlock.Width;

        // A percentage padding is read against the containing block's inline size, which is its height
        // when that block is a column (CSS 2.1, CSS Writing Modes 3).
        var percentBasis = context.ContainingIsVertical ? context.ContainingBlock.Height : basisWidth;
        var border = BorderOf(box);
        var padding = style.Padding.Resolve(percentBasis);
        var insets = new Thickness(
            padding.Top + border.Top,
            padding.Right + border.Right,
            padding.Bottom + border.Bottom,
            padding.Left + border.Left);

        // A width stated as one of the content keywords is settled by what the box holds rather than by
        // the containing block, so it is asked for here, where both are in hand.
        var marginBasis = context.ContainingIsVertical ? context.ContainingBlock.Height : context.ContainingBlock.Width;
        var (borderBoxWidth, contentWidth) = constraints.DefiniteWidth is { } definite
            ? (definite, Math.Max(0, definite - insets.Horizontal))
            : _engine.ContentSizedWidth(box, basisWidth, context) is { } contentSized
                ? (contentSized, Math.Max(0, contentSized - insets.Horizontal))
                : BoxModel.ResolveWidth(style, basisWidth, context.ContainingBlock.Width, insets.Horizontal,
                    context.ContainingBlock.Height, marginBasis);
        var basisHeight = constraints.AvailableHeight ?? context.ContainingBlock.Height;
        var declaredHeight = BoxModel.DeclaredHeight(style, basisHeight, insets.Vertical);

        // A box with a ratio and a definite width takes its height from it, whatever the content asks
        // for (CSS Sizing 4 §4). The height is settled here rather than at the end, because it is the
        // height the children are measured against - the same height a declared one is.
        var ratioHeight = BoxModel.HeightFromRatio(style, contentWidth, null, insets.Vertical);

        // The height the ratio suggests is a preferred one, not a definite one: the content may be
        // taller than it, and then the content is what the box comes to (CSS Sizing 4 §4.4). It is
        // also the height the children are measured against, since a percentage inside resolves
        // against the height the box comes out at.
        var preferredHeight = declaredHeight ?? ratioHeight;

        // A box that contains its block size is not measured from its content either: it comes to the
        // height it states for itself, which is nothing where it states none (CSS Contain 2 §2.1).
        if (ContainsBlockSize(box) && declaredHeight is null)
        {
            var stated = style.Contain.BlockSize ?? style.Contain.Height;
            var contained = stated is { } intrinsic ? Math.Max(0, intrinsic.Resolve(0)) : 0.0;

            return new Size(
                BoxModel.ClampWidth(style, contentWidth + insets.Horizontal, basisWidth, insets.Horizontal),
                BoxModel.ClampHeight(style, contained, basisHeight, insets.Vertical) + insets.Vertical);
        }

        // CSS 2.1 §10.7: a tentative height above max-height (or below min-height) is worked out
        // again with that constraint standing in for the computed height, so the height the box
        // comes out at is settled before the content is measured - and the content is measured
        // against it, because that height is the containing block a percentage inside resolves
        // against (§10.5). A height the content decides stays zero, which is what makes such a
        // percentage come to auto.
        // A box whose height the content decides hands its children no block extent to work against,
        // which a child in an orthogonal flow reads as no inline room either: the fallback below is what
        // answers for it (CSS Writing Modes 3 §7.3.1).
        var childHeight = preferredHeight is { } preferred
            ? BoxModel.ClampHeight(style, preferred, basisHeight, insets.Vertical)
            : OrthogonalExtent(style, basisHeight, insets.Vertical, context.Viewport.Height) ?? 0;

        Double contentHeight;

        if (box.Children.Count == 0)
        {
            contentHeight = 0;
        }
        else if (HasBlockChildren(box))
        {
            // A column that runs down the page is bounded by the width of the box, which this pass
            // holds already; the height is the inline axis the columns are shared over.
            var columnExtent = box.Flow.IsVertical ? contentWidth : childHeight;
            contentHeight = ColumnsOf(box, box.Flow.InlineExtent(contentWidth, childHeight), out var columns, out var share)
                ? MeasureMulticolChildren(
                    box,
                    share,
                    contentWidth,
                    columnExtent,
                    declaredHeight is not null || box.Flow.IsVertical,
                    columns,
                    context)
                : MeasureBlockChildren(
                    box,
                    contentWidth,
                    childHeight,
                    box.Style.Multi.IsMultiColumn ? context.WithFragmentainer() : context);
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
                    context.ForContainingBlock(new Size(contentWidth, childHeight), box.Flow.IsVertical)));
        }

        // A height the content decides is the content's; a height the ratio suggests is the ratio's,
        // unless the content is taller and the box's automatic minimum size lets it be.
        var height = BoxModel.ClampHeight(
            style,
            declaredHeight ?? (ratioHeight is { } ratio
                ? BoxModel.ContentRaisesTheRatio(style) ? Math.Max(ratio, contentHeight) : ratio
                : contentHeight),
            basisHeight,
            insets.Vertical,
            contentHeight);

        return new Size(borderBoxWidth, height + insets.Vertical);
    }

    /// <summary>
    /// Measures a box whose text runs down a column. It is the same box model with the two axes
    /// exchanged: the declared <c>height</c> is the extent the text has to run along, the padding
    /// and border above and below bound it, and the <c>width</c> comes out of the content - a column
    /// is only as wide as the columns it needs, which is the opposite of the <c>auto</c> width of a
    /// block in a row.
    /// </summary>
    private Size MeasureColumn(
        Box box,
        MeasureConstraints constraints,
        LayoutContext context,
        WritingModeInfo flow,
        Thickness? inlineMarginOverride)
    {
        var style = box.Style;
        var border = BorderOf(box);

        var basisInline = constraints.AvailableHeight ?? context.ContainingBlock.Height;
        var basisBlock = constraints.AvailableWidth ?? context.ContainingBlock.Width;

        // Percentages are read against the containing block's inline size, not against this box's own:
        // a vertical element inside a horizontal one resolves its margins against the container's width
        // (sizing-orthogonal-percentage-margin-001).
        var percentBasis = context.ContainingIsVertical ? context.ContainingBlock.Height : context.ContainingBlock.Width;
        var padding = style.Padding.Resolve(percentBasis);
        var insetsInline = padding.Vertical + border.Vertical;
        var insetsBlock = padding.Horizontal + border.Horizontal;

        var declaredInline = BoxModel.DeclaredHeight(style, basisInline, insetsInline);
        var declaredBlock = BoxModel.DeclaredWidth(style, basisBlock, insetsBlock);

        // A normal block fills its containing block along a column just as a normal block fills its
        // width in a row. Content that cannot break overflows; it does not enlarge the containing block
        // and give itself more room on the next measure. A float, positioned box or inline-block still
        // shrinks to the inline extent its text asks for - and so does a box standing across the flow
        // it is contained in, whose own line runs where the container has no extent to hand down: the
        // room along that line is settled by what the box holds rather than by what holds the box
        // (CSS Writing Modes 3 §4.1, which exchanges the axes for the box alone).
        var fillsInline = box.IsBlockLevel &&
            !IsFloat(box) &&
            !PositionedLayout.IsOutOfFlow(box) &&
            context.ContainingIsVertical == flow.IsVertical;

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
        // The root can inherit a page flow from the body while keeping its own horizontal computed
        // margins. Its placement maps those margins onto the page flow, so measurement must use the
        // same mapping or the page starts outside the viewport and extends past it by that margin.
        var (inlineBefore, inlineAfter) = flow.InlineMargins(
            inlineMarginOverride ?? style.Margin.Resolve(percentBasis));
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
            // A container of columns is as wide as its widest column, not as wide as all its children
            // summed: the children are shared over the strips the columns make, so what the band takes
            // along the block axis is one strip's own content (CSS Multicol 2 §3, measured:
            // break-inside-avoid-min-block-size-2, whose two boxes of 40 in a `columns: 3` container of
            // `inline-size: 500px` make a band 40 wide in Chrome and 80 when the children are summed).
            if (ColumnsOf(box, childInline, out var columns, out var share))
            {
                contentBlock = MeasureMulticolChildren(
                    box,
                    share,
                    childBlock,
                    childBlock,
                    declaredBlock is not null,
                    columns,
                    context);
            }
            else
            {
                var column = MeasureColumnChildren(box, childInline, childBlock, context, flow);
                contentBlock = column.Block;
                childInline = Math.Max(childInline, column.Inline);

                if (style.Height.IsAuto && !fillsInline)
                {
                    // The columns of the content reach along the line as far as the tallest of them, and
                    // that is how long the line of a box across its container has to be.
                    childInline = Math.Max(childInline, column.FlowedInline);
                }
            }
        }
        else
        {
            // The text answers both extents: how wide the band of columns came to, and how far down the
            // longest one reached. The block children above take the same pair, and reading only the
            // first left a column of nothing but text as tall as the nothing it was measured against.
            var text = _inline.Measure(box, childInline, flow, new InlineContext(_engine, context));
            contentBlock = text.Block;

            if (style.Height.IsAuto && !fillsInline)
            {
                childInline = Math.Max(childInline, text.Inline);
            }
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
    /// Two answers come back for the inline axis. <see cref="Float"/> is how far down the line the
    /// floats reach, which a column always grows for; <see cref="FlowedInline"/> is how far the boxes
    /// in the flow reach, which only a column that is not stretched along its line takes up - the one
    /// standing across the flow that contains it, whose line runs where its container has nothing to
    /// hand down. Margins collapse along the block axis (the physical x for a column) exactly as they
    /// do in a row - a neighbour's adjoining margins fold into one strip, a child's outer edge escapes
    /// through the box when nothing borders or pads there, and a box with nothing across it
    /// self-collapses.
    /// </remarks>
    private (Double Block, Double Inline, Double FlowedInline) MeasureColumnChildren(
        Box box,
        Double contentInline,
        Double basisBlock,
        LayoutContext context,
        WritingModeInfo flow)
    {
        var cursor = 0.0;
        var pending = 0.0;
        var flowed = 0.0;
        var pendingGroup = MarginGroup.Zero;
        var throughStart = CollapsesThroughStart(box, flow, contentInline);
        var throughEnd = CollapsesThroughEnd(box, flow, contentInline);
        var childContext = context.ForContainingBlock(new Size(basisBlock, contentInline), containingIsVertical: true);
        var constraints = MeasureConstraints.FitWidth(basisBlock);
        var startEscapes = throughStart;
        var floats = new ColumnFloats(flow, contentInline);

        foreach (var child in box.Children)
        {
            if (!child.IsBlockLevel || PositionedLayout.IsOutOfFlow(child))
            {
                continue;
            }

            if (IsFloat(child))
            {
                floats.Place(child, _engine.Measure(child, constraints, childContext), cursor + pending);
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
                pendingGroup = pendingGroup.With(childStart);
                pending = pendingGroup.Value;
            }

            if (childSelfCollapses)
            {
                continue;
            }

            var size = _engine.Measure(child, constraints, childContext);

            // How far down the line the child reaches is its own extent along it, with the margins it
            // writes at either end of that axis added to it.
            var (inlineBefore, inlineAfter) = flow.InlineMargins(child.Style.Margin.Resolve(contentInline));

            flowed = Math.Max(
                flowed,
                flow.InlineExtent(size.Width, size.Height) + inlineBefore + inlineAfter);

            cursor += pending + flow.BlockExtent(size.Width, size.Height);
            pending = childEnd;
            pendingGroup = new MarginGroup(childEnd);
        }

        // A container grows for the floats it holds, and the axis it grows along is the physical one a
        // row grows along: measured in Chrome, a column holding nothing but a 50x50 float is fifty tall
        // and as wide as its declared width, so the float's height counts and its width does not.
        var stacked = throughEnd ? cursor : cursor + pending;

        return (stacked, floats.DeepestInline, flowed);
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

    /// <summary>
    /// Gets whether size containment reaches the box's inline size. An internal table box is not
    /// contained at all: its size is the table's business (CSS Contain 1 §3.2).
    /// </summary>
    private static Boolean ContainsInlineSize(Box box) =>
        box.Style.Contain.Inline && !IsInternalTable(box.Style);

    /// <summary>Gets whether size containment reaches the box's block size, for the same reason.</summary>
    private static Boolean ContainsBlockSize(Box box) =>
        box.Style.Contain.Block && !IsInternalTable(box.Style);

    /// <summary>Gets whether the box is monolithic along the axis a column cuts it: size containment on
    /// that axis leaves no break point in the box at all, so a column with no room for the whole of it
    /// carries it on and lets it overflow rather than cutting through it (CSS Contain 1 §3.1.1, CSS
    /// Fragmentation 3 §4.1, measured: float-017, whose 200-tall float of <c>contain:size</c> stands
    /// whole in the column it is pushed to, against the same float with <c>contain:layout</c>, which is
    /// cut 80+100+20 down three columns).</summary>
    private static Boolean IsMonolithic(Box box, Boolean vertical) =>
        vertical ? ContainsInlineSize(box) : ContainsBlockSize(box);

    // Size containment does not apply to a table box or an internal table box: the table's own layout
    // decides those sizes (CSS Contain 2 §3.1.1). A table caption is not one of the internal boxes -
    // the chapter says so in as many words - so containment does apply to it.
    private static Boolean IsInternalTable(ComputedStyle style) =>
        style.Display is "table" or "inline-table" or "table-row-group" or "table-header-group" or
            "table-footer-group" or "table-row" or "table-cell" or "table-column" or "table-column-group";

    private IntrinsicSize ContentIntrinsic(Box box, Double basis, Double insets, LayoutContext context)
    {
        // A box that contains its size is not measured from its content: it answers with the intrinsic
        // size the page stated for the axis it contains, and with nothing where it stated none
        // (CSS Contain 2 §2.1, CSS Sizing 4 §5).
        var contain = box.Style.Contain;

        if (ContainsInlineSize(box))
        {
            var stated = contain.InlineSize ?? (box.Flow.IsVertical ? contain.Height : contain.Width);
            var inline = stated is { } length ? Math.Max(0, length.Resolve(0)) : 0.0;

            // The columns still stand, and the gaps between them are room the box asks for even when
            // the content is the nothing size containment leaves: three empty columns of a float are
            // two 1em gaps wide, which is what Chromium gives the contained float in
            // contain-size-multicol-002.
            var columnCount = !box.Flow.IsVertical && box.Style.Multi.IsMultiColumn
                ? Math.Max(1, box.Style.Multi.Count ?? 1)
                : 1;
            var gaps = columnCount > 1 ? (columnCount - 1) * box.Style.Multi.Gap : 0;
            var outer = inline * columnCount + gaps + insets;

            return new IntrinsicSize(outer, outer);
        }

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

}

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
        var (borderBoxWidth, contentWidth) = _engine.ContentSizedWidth(box, basisWidth, context) is { } contentSized
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
        var childHeight = preferredHeight is { } preferred
            ? BoxModel.ClampHeight(style, preferred, basisHeight, insets.Vertical)
            : 0;

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
    private Size MeasureColumn(Box box, MeasureConstraints constraints, LayoutContext context, WritingModeInfo flow)
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
        var (inlineBefore, inlineAfter) = flow.InlineMargins(style.Margin.Resolve(percentBasis));
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
            }
        }
        else
        {
            // The text answers both extents: how wide the band of columns came to, and how far down the
            // longest one reached. The block children above take the same pair, and reading only the
            // first left a column of nothing but text as tall as the nothing it was measured against.
            var text = _inline.Measure(box, childInline, flow, new InlineContext(_engine, context));
            contentBlock = text.Block;

            // Only a column that stated no length down the page takes one from its text; a declared
            // length stays what the page said however much further the content runs, or a box that
            // clips its overflow would be grown past the edge it clips at.
            if (style.Height.IsAuto)
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
    /// Nothing here answers for the inline axis: a column is as long as the space it was given, not
    /// as long as the tallest child, so what the children reach down it does not come into the size.
    /// Margins collapse along the block axis (the physical x for a column) exactly as they do in a
    /// row - a neighbour's adjoining margins fold into one strip, a child's outer edge escapes through
    /// the box when nothing borders or pads there, and a box with nothing across it self-collapses.
    /// </remarks>
    private (Double Block, Double Inline) MeasureColumnChildren(
        Box box,
        Double contentInline,
        Double basisBlock,
        LayoutContext context,
        WritingModeInfo flow)
    {
        var cursor = 0.0;
        var pending = 0.0;
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

            cursor += pending + flow.BlockExtent(size.Width, size.Height);
            pending = childEnd;
            pendingGroup = new MarginGroup(childEnd);
        }

        // A container grows for the floats it holds, and the axis it grows along is the physical one a
        // row grows along: measured in Chrome, a column holding nothing but a 50x50 float is fifty tall
        // and as wide as its declared width, so the float's height counts and its width does not.
        var stacked = throughEnd ? cursor : cursor + pending;

        return (stacked, floats.DeepestInline);
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
            var atomic = flow.IsVertical
                ? context
                : context.ForContainingBlock(new Rect(contentX, contentY, contentWidth, usedHeight ?? 0), box.Flow.IsVertical);

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

            // The child's lines stand in the band of the floats around it, at the height the child
            // itself reaches: two siblings at different depths have different room beside a float,
            // so the band is answered per child rather than by the one context the container shares.
            var (shift, room, drop, band) = FormattingRoot(
                child,
                BandFor(child, tracks, context.Floats, cursor + pending, margin.Left, margin.Right, 0, 0, contentWidth),
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

    /// <summary>One place where a column may end inside a divided box: the distance from the box's top
    /// and whether that spot is the end of a line box (which orphans and widows count) rather than the
    /// end of a whole child (CSS Break 3 §4.3).</summary>
    private readonly record struct PieceUnit(Double Offset, Boolean IsLine);

    /// <summary>Gets just the places a divided box may end, for a cut that asks nothing of orphans and
    /// widows.</summary>
    private static IReadOnlyList<Double>? PieceOffsets(IReadOnlyList<PieceUnit>? units) =>
        units?.Select(unit => unit.Offset).ToList();

    /// <summary>
    /// Gets the places along a box's block axis where the units of its own content end, which is where a
    /// column that cuts the box may fall: the lines of a child that breaks at them, and the ends of the
    /// children that have none. Null where something inside refuses the reckoning - a float, an inline
    /// run, a forced break, another formatting context - and an empty list where the box holds nothing to
    /// break between (CSS Break 3 §4.2).
    /// </summary>
    private IReadOnlyList<PieceUnit>? PieceBreaks(Box box, Double width, LayoutContext context)
    {
        if (box.Kind != BoxKind.Block || box.Flow.IsVertical || box.Style.ClipsOverflow)
        {
            return null;
        }

        var basis = context.ContainingBlock.Width;
        var border = BorderOf(box);
        var padding = box.Style.Padding.Resolve(basis);
        var contentWidth = Math.Max(0, width - border.Horizontal - padding.Horizontal);
        var content = context.ForContainingBlock(new Size(
            contentWidth,
            Math.Max(0, context.ContainingBlock.Height - border.Vertical - padding.Vertical)));
        var offsets = new List<PieceUnit>();
        var cursor = border.Top + padding.Top;

        foreach (var child in box.Children)
        {
            if (PositionedLayout.IsOutOfFlow(child))
            {
                continue;
            }

            // A float is no unit the flow advances by, and no reason to leave the box whole: the
            // fragment whose flow it stands in places it, so it gives no cut point and refuses no
            // reckoning (CSS Fragmentation 3 §4.3, measured: widows-orphans-022). One level down,
            // though, a box that stands on floats is a thing of its own that the column cannot end
            // inside, and the reckoning stops there (measured: flexbox-multi-row-1a's reference,
            // whose lines are float pairs nested in the divided box).
            if (IsFloat(child))
            {
                continue;
            }

            if (HoldsAnyFloat(child, child.Children))
            {
                return null;
            }

            var (before, after) = BoundaryBreaks(child);

            if (!child.IsBlockLevel || child.IsReplaced ||
                child.Kind is BoxKind.Table or BoxKind.Flex or BoxKind.Grid or BoxKind.Masonry ||
                Fragmentainer.Forced(before) || Fragmentainer.Forced(after) ||
                child.Style.Breaks.Inside == BreakValue.Avoid)
            {
                return null;
            }

            var size = _engine.Measure(child, MeasureConstraints.FitWidth(contentWidth), content);
            var margin = BoxModel.UsedMargin(child.Style, basis, size.Width);

            cursor += margin.Top;

            var inner = FragmentsInColumns(child)
                ? LineBreaks(child, contentWidth, content).Select(at => new PieceUnit(at, true)).ToList()
                : PieceBreaks(child, contentWidth, content);

            if (inner is { Count: > 0 })
            {
                foreach (var unit in inner)
                {
                    offsets.Add(new PieceUnit(cursor + unit.Offset, unit.IsLine));
                }
            }
            else
            {
                offsets.Add(new PieceUnit(cursor + size.Height, false));
            }

            cursor += size.Height + margin.Bottom;
        }

        return offsets;
    }

    /// <summary>Gets where a box's own lines end along its block axis, counted from the top of its
    /// border box.</summary>
    private IReadOnlyList<Double> LineBreaks(Box box, Double width, LayoutContext context)
    {
        var border = BorderOf(box);
        var padding = box.Style.Padding.Resolve(context.ContainingBlock.Width);
        var lines = _inline.LineHeights(
            box, width, box.Flow, new InlineContext(_engine, context, context, context));
        var offsets = new List<Double>(lines.Count);
        var at = border.Top + padding.Top;

        foreach (var height in lines)
        {
            at += height;
            offsets.Add(at);
        }

        return offsets;
    }

    /// <summary>
    /// Gets whether a column may cut through a box that has blocks of its own inside: a plain block
    /// lying in the columns' own axis, with nothing inside the columns would have to place themselves
    /// and at least two content units for a cut to fall between (CSS Fragmentation 3 §4.3, §4.2 -
    /// measured: overflowed-block-with-no-room-after-000, whose child holds a box taller than itself).
    /// </summary>
    private Boolean DividesUnits(
        Box child,
        Boolean vertical,
        Thickness margin,
        Boolean fills,
        Boolean nested,
        Double share,
        LayoutContext context)
    {
        if (!fills || vertical || nested || margin.Bottom != 0 ||
            child.Kind != BoxKind.Block || child.Style.Multi.IsMultiColumn ||
            child.Style.Breaks.Inside == BreakValue.Avoid)
        {
            return false;
        }

        var units = PieceBreaks(child, share, context);

        // A float gives no break point of its own, so a box that stands on floats for its length has
        // nothing inside to cut between: with one unit of its own flow it moves whole, and only two
        // units let a column end between them (CSS Fragmentation 3 §4.2-§4.3, measured:
        // floats-clear-multicol-001 and the gecko flex references, whose columns are made of floats).
        return HoldsAnyFloat(child, child.Children)
            ? units is { Count: >= 2 }
            : units is { Count: >= 1 };
    }

    /// <summary>
    /// Gets whether a column flex is one the columns may cut into a piece per window: every child a
    /// block of its own in the flow, the container taller than a window, and nothing inside that the
    /// columns would have to place themselves. A piece of such a flex is laid out as a flex container
    /// of the piece's own size, so the room a window has left is shared among the items it really
    /// holds and a break takes the gap it fell in with it (CSS Flexbox 1 §6).
    /// </summary>
    private static Boolean RelaysInColumns(
        Box child, Boolean vertical, Double extent, Double columnHeight, Boolean nested)
    {
        if (vertical || nested || columnHeight <= 0 || extent <= columnHeight + 0.001 ||
            child.Kind != BoxKind.Flex || child.Children.Count == 0 || HoldsPositioned(child))
        {
            return false;
        }

        // A row flex lays its items across the block axis rather than along it, and the window's item
        // range does not read its lines yet: it keeps standing whole (CSS Flexbox 1 §6).
        if (child.Style.Flex.IsRow)
        {
            return false;
        }

        // A line that states a forced break ends the fragment wherever the page says, which the
        // window's item range does not follow yet: such a container keeps standing whole (measured:
        // multi-line-column-flex-fragmentation-044).

        foreach (var item in child.Children)
        {
            if (!item.IsBlockLevel || PositionedLayout.IsOutOfFlow(item) || IsFloat(item) ||
                Fragmentainer.Forced(item.Style.Breaks.Before) ||
                Fragmentainer.Forced(item.Style.Breaks.After))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Gets whether a wrapping row flex is one the columns cut window by window: every child a block of
    /// its own in the flow, the container taller than a window, and nothing inside that the columns
    /// would have to place themselves (CSS Flexbox 1 §6).
    /// </summary>
    private static Boolean RowRelaysInColumns(
        Box child, Boolean vertical, Double extent, Double columnHeight, Boolean nested)
    {
        // Whether a window is cut at all is the plan's to say: a flex whose lines run past the column is
        // cut even where its own box is shorter, since the content is what the column meets
        // (CSS Flexbox 1 §6, measured: multi-line-row-flex-fragmentation-068).
        if (vertical || nested || columnHeight <= 0 ||
            child.Kind != BoxKind.Flex || !child.Style.Flex.IsRow || !child.Style.Flex.Wraps ||
            child.Children.Count == 0 || HoldsPositioned(child))
        {
            return false;
        }

        foreach (var item in child.Children)
        {
            // A break stated on an item of a wrapping row is a line break inside the container rather
            // than one through the fragmentainer (CSS Flexbox 1 §6), so it is no reason to leave the
            // container whole (measured: multi-line-row-flex-fragmentation-067).
            if (!item.IsBlockLevel || PositionedLayout.IsOutOfFlow(item) || IsFloat(item))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Gets the band each item of a single-line row flex claims along the container's block
    /// axis, ordered as the fragments are to be filled, when the fragmentainer has no room at all for
    /// any of them where the container stands; null for any other shape (CSS Flexbox 1 §6).</summary>
    private List<(Int32 Item, Double From, Double To)>? ItemBandsForEmptyRoom(
        Box child,
        Boolean vertical,
        Boolean fills,
        Boolean nested,
        Double share,
        Double room,
        LayoutContext context)
    {
        var flex = child.Style.Flex;

        if (vertical || child.Flow.IsVertical || nested || !fills || room > 0.001 ||
            child.Kind != BoxKind.Flex || !flex.IsRow || flex.Wraps || child.Children.Count == 0)
        {
            return null;
        }

        foreach (var item in child.Children)
        {
            if (!item.IsBlockLevel || PositionedLayout.IsOutOfFlow(item) || IsFloat(item) ||
                Fragmentainer.Forced(item.Style.Breaks.Before) ||
                Fragmentainer.Forced(item.Style.Breaks.After))
            {
                return null;
            }
        }

        var bands = new List<(Int32, Double, Double)>();

        for (var index = 0; index < child.Children.Count; index++)
        {
            var item = child.Children[index];
            var size = _engine.Measure(item, MeasureConstraints.FitWidth(share), context);
            var margin = BoxModel.UsedMargin(item.Style, share, size.Width);

            bands.Add((index, margin.Top, margin.Top + BlockExtentOf(size, vertical)));
        }

        return bands.OrderBy(band => band.Item2).ThenBy(band => band.Item1)
            .Select(band => (band.Item1, band.Item2, band.Item3))
            .ToList();
    }

    /// <summary>Gets how far a box's out-of-flow descendants reach below its own top, which is how
    /// long a column has to carry the box before the content is spent. A box that names no offset
    /// stands where the flow would have left it, so the content before it is counted in; a fixed one
    /// is anchored to the viewport and no part of this box's length (CSS Position 3 §5.5).</summary>
    private Double PositionedReach(Box box, Double width, LayoutContext context)
    {
        var reach = 0.0;
        var flow = 0.0;

        foreach (var child in box.Children)
        {
            if (!PositionedLayout.IsOutOfFlow(child))
            {
                flow += _engine.Measure(child, MeasureConstraints.FitWidth(width), context).Height;
                continue;
            }

            if (child.Style.Position.Position == PositionMode.Fixed)
            {
                continue;
            }

            var top = child.Style.Position.Top;
            var start = top.IsAuto ? flow : top.Resolve(0);
            var height = _engine.Measure(child, MeasureConstraints.FitWidth(width), context).Height;

            reach = Math.Max(reach, start + height);
        }

        return reach;
    }

    /// <summary>Gets whether a box divided over the columns leaves every piece a column to stand
    /// in: a fragmentainer of a stated count draws no column past its last one (CSS Multicol 1
    /// §1.2), so a box that would need more pieces is left whole to overflow instead.</summary>
    private static Boolean FitsTheColumns(Double extent, Double columnHeight, Int32 columns) =>
        columnHeight <= 0 || Math.Ceiling(extent / columnHeight - 0.001) <= columns;

    /// <summary>
    /// Gets whether a child of the box asks for a forced break before or after itself, bare wrappers
    /// counted through as they are for the slicer.
    /// </summary>
    private static Boolean HoldsForcedBreak(Box box, Double contentWidth)
    {
        foreach (var child in EffectiveMembers(box.Children, contentWidth))
        {
            var (before, after) = BoundaryBreaks(child);

            if (before is BreakValue.Column or BreakValue.Page ||
                after is BreakValue.Column or BreakValue.Page)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Gets the break values a member of a fragmentainer answers with: the box's own, together with any
    /// forced break written inside a table it holds.
    /// </summary>
    /// <remarks>
    /// A table is laid on its own grid and a column cannot end between its parts, so a break the page
    /// wrote on a cell - or on a box standing in one - has nowhere inside the table to fall. It keeps the
    /// side it was written for, because that side names the edge of the uncuttable box the break can
    /// stand at (CSS Break 3 §3.1, and the borrowed pages' own comment: "Should be propagated to the
    /// outer table"). A bare wrapper is not read this way, because the slicer takes those apart into the
    /// container's own members - which is what keeps a break written inside one falling where it was
    /// written rather than at the wrapper's edge.
    /// </remarks>
    private static (BreakValue Before, BreakValue After) BoundaryBreaks(Box child)
    {
        var before = child.Style.Breaks.Before;
        var after = child.Style.Breaks.After;

        // A forced break on the first or the last item of a flex travels to the container's own
        // edge, which is the line it starts or ends (CSS Flexbox 1 §6).
        if (child.Kind == BoxKind.Flex && child.Children.Count > 0)
        {
            if (Fragmentainer.Forced(child.Children[0].Style.Breaks.Before))
            {
                before = Fragmentainer.Combine(before, child.Children[0].Style.Breaks.Before);
            }

            var last = child.Children[^1];

            if (Fragmentainer.Forced(last.Style.Breaks.After))
            {
                after = Fragmentainer.Combine(after, last.Style.Breaks.After);
            }
        }

        if (child.Kind != BoxKind.Table)
        {
            return (before, after);
        }

        foreach (var part in TableParts(child))
        {
            var breaks = part.Style.Breaks;

            // Only a forced break travels out; an avoid written inside the table is a wish about a break
            // point the table itself decides, and reaching the container with it would let a cell refuse
            // the break its own table asks for.
            if (Fragmentainer.Forced(breaks.Before))
            {
                before = Fragmentainer.Combine(before, breaks.Before);
            }

            if (Fragmentainer.Forced(breaks.After))
            {
                after = Fragmentainer.Combine(after, breaks.After);
            }
        }

        return (before, after);
    }

    /// <summary>Every box below this one, however deep, in the order the page wrote them.</summary>
    private static IEnumerable<Box> TableParts(Box box)
    {
        foreach (var child in box.Children)
        {
            yield return child;

            foreach (var deeper in TableParts(child))
            {
                yield return deeper;
            }
        }
    }

    /// <summary>
    /// Measures the block children of a multi-column box against the width of one column and answers the
    /// height the columns come to: the <c>column-fill: auto</c> height the box states, or the least
    /// extent a balanced fill of its columns needs (CSS Multicol 3 §3.4).
    /// </summary>
    private Double MeasureMulticolChildren(
        Box box,
        Double share,
        Double fullWidth,
        Double declaredHeight,
        Boolean heightStated,
        Int32 columns,
        LayoutContext context)
    {
        // Nothing here takes part in the floats of the boxes around it yet; the container holds none of
        // its own, which is what let it take this path at all.
        _engine.PublishFloats(box, null);

        // A container of nothing but floats hands each column the floats that fit beside each other,
        // and one that does not fit begins the column after it (CSS Multicol 3 §7).
        if (AllFloats(box))
        {
            return MeasureFloatColumns(box, share, declaredHeight, heightStated, context);
        }

        // A container that mixes floats with boxes of its own shares them over its stated columns the
        // same way the flow does beside a float: the float takes no room of the flow's own and the
        // column that has none left for it carries it on, with clearance asked where it is asked.
        if (FloatFlowColumns(box, heightStated, declaredHeight, share, context))
        {
            return MeasureFloatFlowColumns(box, share, declaredHeight, context);
        }

        // A paragraph broken across the columns keeps the float in its first column: the first plan
        // carries the float's band, and the column after it plans the remaining text at full width.
        if (FloatHostOf(box) is { } floatHost &&
            PlanFloatParagraph(floatHost, share, heightStated ? declaredHeight : null, columns, context) is { } plan)
        {
            return plan.Extent;
        }

        var sections = MulticolSections(box);

        // A spanner spans every column and ends the run above it: each stretch of content is shared
        // over its own row of columns, and the spanner stands between them at full width
        // (CSS Multicol 3 §3.2).
        if (sections.Exists(section => section.Spanner is not null))
        {
            return MeasureMulticolSections(box, sections, share, fullWidth, declaredHeight, heightStated, columns, context);
        }

        var children = MulticolChildren(box, share, declaredHeight, declaredHeight, columns, context, EffectiveMembers(box.Children, share));

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

        var extent = MulticolExtent(multi, columns, heightStated ? declaredHeight : null, children);

        return extent;
    }

    /// <summary>
    /// One thing a column takes: a whole child, or one of the lines of a child that is broken across the
    /// columns rather than moved from one to the other in one piece.
    /// </summary>
    private readonly struct MulticolChild
    {
        public MulticolChild(
            Box box,
            Size size,
            Thickness margin,
            FragmentChild fragment,
            Int32 line,
            Boolean last,
            Double whole = 0,
            Double offset = -1,
            Boolean divides = false,
            Double startInset = 0,
            Boolean relays = false,
            IReadOnlyList<PieceUnit>? breaks = null,
            Double reach = 0,
            Double lead = 0,
            Double trail = 0,
            Int32 item = -1,
            Boolean floats = false,
            Boolean defers = false,
            Boolean ownsEdges = true)
        {
            OwnsEdges = ownsEdges;
            Box = box;
            Size = size;
            Margin = margin;
            Fragment = fragment;
            Line = line;
            Last = last;
            Whole = whole;
            Offset = offset;
            Divides = divides;
            StartInset = startInset;
            Relays = relays;
            Breaks = breaks;
            Reach = reach > 0 ? reach : fragment.Height;
            Lead = lead;
            Trail = trail;
            Item = item;
            Floats = floats;
            Defers = defers;
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
        public Boolean Broken => Line >= 0 || Offset >= 0;

        /// <summary>Gets whether an out-of-flow descendant anchors to this child, so a column that
        /// cannot hold it whole cuts it where it ends instead of pushing it on (CSS Position 3 §5.5).
        /// </summary>
        public Boolean Divides { get; }

        /// <summary>
        /// Gets whether the pieces of this child are laid out one by one: a flex container the columns
        /// cut is a flex container of each piece's own size, so its items are distributed anew in every
        /// piece rather than laid out once against the whole (CSS Flexbox 1 §6).
        /// </summary>
        public Boolean Relays { get; }

        /// <summary>
        /// Gets how far inside the whole box this piece starts, or -1 where the piece is not cut
        /// through the box itself: a child with nothing to break between - an empty box of a stated
        /// height - is still one the fragmentainer cuts across, and the offset says which slice of it
        /// this column holds (CSS Fragmentation 3 §4.3).
        /// </summary>
        public Double Offset { get; }

        /// <summary>
        /// Gets the room the piece needs at the top of the column that takes it - its block-start
        /// border and padding - which a column with less room than that leaves for the next one rather
        /// than start the box in the middle of its own edge (measured in Chrome: a ten-pixel border is
        /// pushed on by five pixels of room and kept by ten).
        /// </summary>
        public Double StartInset { get; }

        /// <summary>
        /// Gets the room this column gives up to a repeated table section before the piece's own content
        /// begins - the header group a column after the first shows at its top (CSS Tables 3 §6.1). With
        /// <see cref="Trail"/> this is what a repeated group costs the column: the piece's content sits
        /// between the two, so the column holds lead + content + trail while the box only advances by the
        /// content.
        /// </summary>
        public Double Lead { get; }

        /// <summary>Gets the room a repeated footer group costs at the bottom of this piece's column.</summary>
        public Double Trail { get; }

        /// <summary>Gets which item of a row flex this piece paints, or -1 for a piece that holds the
        /// container's own window: a single-line row flex splits by item, since every item begins at the
        /// container's block start and none of them re-flows into the fragmentainer after it.</summary>
        public Int32 Item { get; }

        /// <summary>
        /// Gets whether the child is a float the column holds directly: it stands at the column's edge
        /// where the flow reached and takes none of that flow's room, so the boxes written after it go on
        /// filling the column as if it were not there (CSS 2.1 §9.5.1, measured:
        /// <c>wpt/css-break/float-017</c>).
        /// </summary>
        public Boolean Floats { get; }

        /// <summary>
        /// Gets whether the float that stands here spans the whole width of its column, so nothing of
        /// the flow can ever stand beside it: a column the flow has already spent room in gives such a
        /// float the head of the next one instead of the room it has left, and fills what is left of
        /// this one with what was written after the float (CSS 2.1 9.5.1 with CSS Fragmentation 3 4.1,
        /// measured: wpt/css-break/float-017).
        /// </summary>
        public Boolean Defers { get; }

        /// <summary>Gets whether this piece of the box owns the edges it carries. The piece that holds
        /// nothing but the margin a box opened with owns none: the box's borders and background belong
        /// to the piece that shows the box (CSS Break 3 §4.7).</summary>
        public Boolean OwnsEdges { get; }

        /// <summary>
        /// Gets the border box height the child would have had unbroken, for a piece of a broken child.
        /// The background is painted across that box and cut with it, which is what keeps one gradient
        /// running down every piece (CSS Break 3 §4.7).
        /// </summary>
        public Double Whole { get; }

        /// <summary>
        /// Gets where the units of the box's own content end, which is where a column that cuts the box
        /// may fall: each one says whether it is the end of a line (what orphans and widows count) or the
        /// end of a whole child. Null where nothing inside offers such places (CSS Break 3 §4.2).
        /// </summary>
        public IReadOnlyList<PieceUnit>? Breaks { get; }

        /// <summary>
        /// Gets how far a piece of this child paints, which is past <see cref="Fragment"/> where a
        /// descendant hangs below the box: the columns carry that part into the pieces after the box
        /// ends, while the flow advances by the box's own height (CSS Fragmentation 3 §4.3 - measured:
        /// overflowed-block-with-no-room-after-000, whose child holds a box 60 taller than itself).
        /// </summary>
        public Double Reach { get; }
    }

    /// <summary>
    /// Gets whether a child of a multi-column box can be broken at its own lines. Only a block whose
    /// content is a single inline formatting context has lines to break at here - a block with block
    /// children breaks inside those first, which is the next step of the fragmentainer - and only one
    /// whose own height comes from its content, with nothing inside it that refuses a break.
    /// </summary>
    /// <remarks>
    /// Its border, padding and background are no bar to being broken: the fragments share them the way
    /// <c>box-decoration-break: slice</c> says (CSS Break 3 §4.7), each drawing the edges it owns and the
    /// background painted across the whole box and cut with it.
    /// </remarks>
    /// <summary>
    /// Gets the children a column fragments, with a bare wrapper that exists only to carry a forced
    /// break replaced by what it holds.
    /// </summary>
    /// <remarks>
    /// A forced break inside a nested block still ends the column the break falls in (CSS Break 3
    /// §3.2, measured: layout and style containment leave break-before alone), but the slicer breaks a
    /// child at its own lines and keeps a block child whole. A wrapper with no border, padding, margin,
    /// background or stated size has nothing of its own to draw or place, so its children can be
    /// fragmented as if the page had written them in the column container - which is the same picture
    /// the wrapper would have given.
    /// </remarks>
    private static IReadOnlyList<Box> EffectiveMembers(IReadOnlyList<Box> source, Double share)
    {
        var carries = false;

        for (var index = 0; index < source.Count; index++)
        {
            if (IsForcedBreakCarrier(source[index], share, AvoidFollows(source, index)))
            {
                carries = true;
                break;
            }
        }

        if (!carries)
        {
            return source;
        }

        var members = new List<Box>(source.Count);

        for (var index = 0; index < source.Count; index++)
        {
            if (IsForcedBreakCarrier(source[index], share, AvoidFollows(source, index)))
            {
                FlattenBare(source[index], share, members);
            }
            else
            {
                members.Add(source[index]);
            }
        }

        return members;
    }

    /// <summary>Gets whether the member after this one forbids the break before itself, which is a
    /// break the wrapper before it may have to make room for inside its own content.</summary>
    private static Boolean AvoidFollows(IReadOnlyList<Box> source, Int32 index) =>
        index + 1 < source.Count && source[index + 1].Style.Breaks.Before == BreakValue.Avoid;

    /// <summary>Opens a bare wrapper into the members it holds, and any bare wrapper among them too:
    /// a box that brings nothing of its own is laid out as if its children stood where it does, which
    /// is what lets a break the wrapper could not take be found inside it (CSS Break 3 §4.1).</summary>
    private static void FlattenBare(Box wrapper, Double share, List<Box> members)
    {
        foreach (var child in wrapper.Children)
        {
            if (IsForcedBreakCarrier(child, share, avoidAfter: false) || IsBareWrapper(child, share))
            {
                FlattenBare(child, share, members);
            }
            else
            {
                members.Add(child);
            }
        }
    }

    /// <summary>
    /// Gets whether a block is a bare wrapper whose direct children hold a forced column break: no
    /// border, padding, margin, background or stated size of its own, no clipping, a block axis the
    /// columns share, and block children only.
    /// </summary>
    private static Boolean IsForcedBreakCarrier(Box child, Double share, Boolean avoidAfter)
    {
        if (!IsBareWrapper(child, share))
        {
            return false;
        }

        if (avoidAfter)
        {
            return true;
        }

        return ForcedBreakWithin(child, share);
    }

    /// <summary>Gets whether a box holds a forced column break among what a column would see as its
    /// members - the children it is opened into, and those of any bare wrapper among them.</summary>
    private static Boolean ForcedBreakWithin(Box box, Double share)
    {
        foreach (var child in box.Children)
        {
            if (child.Style.Breaks.Before is BreakValue.Column or BreakValue.Page ||
                child.Style.Breaks.After is BreakValue.Column or BreakValue.Page)
            {
                return true;
            }

            if (IsBareWrapper(child, share) && ForcedBreakWithin(child, share))
            {
                return true;
            }

            if (child.Kind == BoxKind.Flex && child.Children.Count > 0 &&
                (Fragmentainer.Forced(child.Children[0].Style.Breaks.Before) ||
                 Fragmentainer.Forced(child.Children[^1].Style.Breaks.After)))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Gets whether a block is a bare wrapper: no border, padding, margin, background or
    /// stated size of its own, no clipping, a block axis the columns share, and block children
    /// only.</summary>
    private static Boolean IsBareWrapper(Box child, Double share)
    {
        if (child.Children.Count == 0 || child.Flow.IsVertical)
        {
            return false;
        }

        // A column flex stacks its items down the block axis as a block stacks its children, so a
        // column may cut between them; a row flex lays them side by side, which is no list of
        // members the block axis can walk (CSS Flexbox 1 §6).
        if (child.Kind == BoxKind.Flex)
        {
            // A column flex stacks its items down the block axis as a block does, so the columns
            // may cut between them. A row flex wrapping a row of flex containers is the same list
            // one line further out: a forced break on one of them starts a new line, and the line
            // is what the columns share (CSS Flexbox 1 §6).
            var row = child.Style.Flex.IsRow;

            if (!row && child.Style.Flex.Wraps)
            {
                return false;
            }

            if (row)
            {
                foreach (var item in child.Children)
                {
                    if (item.Kind != BoxKind.Flex)
                    {
                        return false;
                    }
                }
            }
        }
        else if (child.Kind != BoxKind.Block)
        {
            return false;
        }

        var style = child.Style;

        if (!style.Width.IsAuto || !style.Height.IsAuto ||
            style.MinWidth.HasValue || style.MaxWidth.HasValue ||
            style.MinHeight.HasValue || style.MaxHeight.HasValue ||
            style.Breaks.Before != BreakValue.Auto || style.Breaks.After != BreakValue.Auto ||
            style.Breaks.Inside != BreakValue.Auto ||
            style.ClipsOverflow || ContainsBlockSize(child) ||
            style.BackgroundColor is { A: > 0 } || style.Backgrounds.Count > 0)
        {
            return false;
        }

        var border = BorderOf(child);
        var padding = style.Padding.Resolve(share);
        var margin = style.Margin.Resolve(share);

        if (border.Horizontal != 0 || border.Vertical != 0 ||
            padding.Horizontal != 0 || padding.Vertical != 0 ||
            margin.Horizontal != 0 || margin.Vertical != 0)
        {
            return false;
        }

        foreach (var grandchild in child.Children)
        {
            if (!grandchild.IsBlockLevel || PositionedLayout.IsOutOfFlow(grandchild) || IsFloat(grandchild))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Gets whether a nested multi-column container is one the columns around it may open into
    /// their own members: exactly one column of its own that it fills rather than balances, nothing it
    /// paints or bounds by itself, and only boxes of the flow inside. Such a container is a room for what
    /// it holds - the columns around it are the fragmentainers that content flows through, each of them
    /// as wide as its own single column would have been (CSS Multicol 3 §1.2, §7.4, CSS Break 3 §4.1,
    /// measured: repeated-section/variable-fragmentainer-size-001, whose header and footer come back
    /// once per outer column, and -002 where the first outer column leaves too little room for a
    /// header at all).</summary>
    private static Boolean IsSingleColumnFragmentainer(Box child, Double share)
    {
        var multi = child.Style.Multi;

        if (child.Kind != BoxKind.Block || child.Flow.IsVertical || child.Children.Count == 0 ||
            !multi.IsMultiColumn || multi.Count != 1 || !multi.Fills || multi.Wraps ||
            child.Style.ClipsOverflow || ContainsBlockSize(child) ||
            child.Style.Breaks.Before != BreakValue.Auto ||
            child.Style.Breaks.After != BreakValue.Auto ||
            child.Style.Breaks.Inside != BreakValue.Auto ||
            child.Style.BackgroundColor is { A: > 0 } || child.Style.Backgrounds.Count > 0)
        {
            return false;
        }

        var border = BorderOf(child);
        var padding = child.Style.Padding.Resolve(share);
        var margin = child.Style.Margin.Resolve(share);

        if (border.Horizontal != 0 || border.Vertical != 0 ||
            padding.Horizontal != 0 || padding.Vertical != 0 ||
            margin.Horizontal != 0 || margin.Vertical != 0)
        {
            return false;
        }

        foreach (var grandchild in child.Children)
        {
            if (!grandchild.IsBlockLevel || PositionedLayout.IsOutOfFlow(grandchild) || IsFloat(grandchild))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Gets the members a column really holds: a bare wrapper that is shorter than what
    /// it holds is opened into them, because the box that walks past the wrapper is the one the
    /// column has to cut, and the wrapper brings nothing of its own to the cut (CSS Break 3 §4.1).
    /// </summary>
    private IReadOnlyList<Box> SpliceOverset(
        IReadOnlyList<Box> source,
        Double share,
        LayoutContext context,
        Double fragmentHeight,
        out Dictionary<Box, Thickness>? carried)
    {
        carried = null;

        // Only the last member is opened: what a wrapper states in height is what the boxes after it
        // stand past, and the column keeps that room for a wrapper it does not open.
        var last = source.Count - 1;

        if (last < 0)
        {
            return source;
        }

        // A wrapper that paints nothing and holds more than a column is opened too: the content is what
        // the column has to cut, and the wrapper lends it no boundary of its own (CSS Break 3 §4.1,
        // measured: widows-block-in-inline-001). A nested container of a single filled column is opened
        // by the same leave-nothing-behind test.
        var candidate = source[last];

        var opens = IsOversetWrapper(candidate, share, context) ||
            (fragmentHeight > 0 &&
             (IsBareWrapper(candidate, share) || IsSingleColumnFragmentainer(candidate, share)) &&
             BlockExtentOf(_engine.Measure(candidate, MeasureConstraints.FitWidth(share), context), false) >
             fragmentHeight + 0.001);

        if (!opens)
        {
            return source;
        }

        var members = new List<Box>(source);
        var wrapper = members[members.Count - 1];
        var edges = wrapper.Style.Margin.Resolve(share);

        members.RemoveAt(last);
        members.AddRange(wrapper.Children);

        // What the wrapper stood aside from goes with the content it handed up: its inline margins move
        // to the boxes that now take its place, the first from the start edge and the last from the end
        // one, so the room the column shares out is the room the wrapper left (CSS 2.1 §10.3.3).
        if (wrapper.Children.Count > 0 && (edges.Left != 0 || edges.Right != 0))
        {
            carried = new Dictionary<Box, Thickness>
            {
                [wrapper.Children[0]] = new Thickness(0, 0, 0, edges.Left),
            };

            var end = wrapper.Children[wrapper.Children.Count - 1];

            carried[end] = carried.TryGetValue(end, out var begun)
                ? new Thickness(0, edges.Right, 0, begun.Left)
                : new Thickness(0, edges.Right, 0, 0);
        }

        return members;
    }

    /// <summary>Gets whether a box is a wrapper that paints nothing and is shorter than the
    /// content it holds.</summary>
    private Boolean IsOversetWrapper(Box child, Double share, LayoutContext context)
    {
        if (child.Kind != BoxKind.Block || child.Flow.IsVertical || child.Children.Count == 0)
        {
            return false;
        }

        var style = child.Style;

        // A box states the height the column leaves room for either way: by writing it, or by
        // capping what its content may make it, which is what a maximum does.
        if (!style.Width.IsAuto || (style.Height.IsAuto && !style.MaxHeight.HasValue) ||
            style.MinWidth.HasValue || style.MaxWidth.HasValue ||
            style.MinHeight.HasValue ||
            style.Breaks.Before != BreakValue.Auto || style.Breaks.After != BreakValue.Auto ||
            style.Breaks.Inside != BreakValue.Auto ||
            style.OutlineStyle != BorderLineStyle.None || style.BoxShadows.Count > 0 ||
            style.ClipsOverflow || ContainsBlockSize(child) ||
            style.BackgroundColor is { A: > 0 } || style.Backgrounds.Count > 0)
        {
            return false;
        }

        var border = BorderOf(child);
        var padding = style.Padding.Resolve(share);
        var margin = style.Margin.Resolve(share);

        // A wrapper that only stands aside from one edge - a margin that narrows it rather than a border,
        // padding or background it would paint - still lends nothing of its own to the stack: the room it
        // leaves is carried to the content the column takes instead (measured: overflowing-block-003's
        // second box, `.outer { margin-right: 25px; height: 0 }` around a block of 100).
        if (border.Horizontal != 0 || border.Vertical != 0 ||
            padding.Horizontal != 0 || padding.Vertical != 0 ||
            margin.Vertical != 0)
        {
            return false;
        }

        // The height the wrapper states is the only thing it brings to the stack; where its own
        // content walks past that, the content is what the column holds.
        var constraints = MeasureConstraints.FitWidth(share);
        var held = 0.0;
        var size = _engine.Measure(child, constraints, context);

        foreach (var grandchild in child.Children)
        {
            if (!grandchild.IsBlockLevel || PositionedLayout.IsOutOfFlow(grandchild) || IsFloat(grandchild))
            {
                return false;
            }

            var inner = _engine.Measure(grandchild, constraints, context);
            var used = BoxModel.UsedMargin(grandchild.Style, share, inner.Width);
            held += inner.Height + used.Top + used.Bottom;
        }

        return held > size.Height + 0.001;
    }

    /// <summary>Gets whether a box leaves the extent a column stacks it along to its content.</summary>
    private static Boolean BlockSizeIsAuto(Box child, Boolean vertical) =>
        vertical ? child.Style.Width.IsAuto : child.Style.Height.IsAuto;

    /// <summary>Gets whether a box caps that extent, which is what a maximum does for a box of
    /// an automatic one (CSS 2.1 §10.7).</summary>
    private static Boolean HasBlockMaximum(Box child, Boolean vertical) =>
        vertical ? child.Style.MaxWidth.HasValue : child.Style.MaxHeight.HasValue;

    /// <summary>Gets whether a box states a floor for that extent.</summary>
    private static Boolean HasBlockMinimum(Box child, Boolean vertical) =>
        vertical ? child.Style.MinWidth.HasValue : child.Style.MinHeight.HasValue;

    /// <summary>Gets the extent a column stacks a child along: the height of a box whose lines run
    /// across the page, and the width of one whose lines run down it.</summary>
    private static Double BlockExtentOf(Size size, Boolean vertical) => vertical ? size.Width : size.Height;

    private static Boolean FragmentsInColumns(Box child) =>
        !HasBlockChildren(child) && BreaksAtLines(child);

    /// <summary>
    /// Gets whether a block's lines may be broken by a column at all: nothing about it makes it a single
    /// piece - a stated height, a break it refuses inside, clipping, size containment, or another axis.
    /// Whether the lines are its own or the one child's they live in is the caller's business.
    /// </summary>
    private static Boolean BreaksAtLines(Box child)
    {
        var style = child.Style;

        return !child.IsReplaced &&
            style.Height.IsAuto &&
            !style.MinHeight.HasValue &&
            !style.MaxHeight.HasValue &&
            style.Breaks.Inside == BreakValue.Auto &&

            // A box that clips what it holds is kept whole: each fragment of a scroll container would
            // have to clip its own share of the content, which the columns do not do yet, and a
            // container sized by its content would then cut that content off at the box's own edge.
            !style.ClipsOverflow &&

            // Size containment makes a box monolithic (CSS Contain 1 §3.1.1): its size comes from its
            // own declaration, not from what it holds, so cutting it would move the content it hides.
            !ContainsBlockSize(child) &&

            // Only a block set in the same axis as the columns has lines the column can end between;
            // one lying on its side is measured along the other axis and is left whole.
            !child.Flow.IsVertical;
    }

    /// <summary>Gets the height a column of a container that fills its columns cuts a box at:
    /// the height the container states, or the height it comes to, which is what a maximum gives a
    /// container of an auto height (CSS Multicol 1 §3).</summary>
    private static Double FillsTo(Box box, LayoutBox parent, Boolean heightStated, Double declaredHeight) =>
        heightStated
            ? declaredHeight
            : box.Style.Multi.Fills
                ? Math.Max(0, parent.BorderBox.Height - parent.Border.Vertical - parent.Padding.Vertical)
                : 0.0;

    private List<MulticolChild> MulticolChildren(
        Box box,
        Double share,
        Double declaredHeight,
        Double fragmentHeight,
        Int32 columns,
        LayoutContext context,
        IReadOnlyList<Box>? members = null,
        Boolean plansAcross = false)
    {
        // What stands in a column is placed by it: a container of columns inside these ones is not
        // taken apart by its own, because the columns around it have nowhere to put the pieces.
        // A column that runs down the page divides the box's inline extent - its height - and stacks the
        // children along the block axis, which is the width: the two extents are the same numbers on the
        // box's own axes, and only what the children are handed is turned back into width and height.
        var vertical = box.Flow.IsVertical;
        var columnBox = vertical ? new Size(declaredHeight, share) : new Size(share, declaredHeight);
        var childContext = context.ForContainingBlock(columnBox).WithFragmentainer();
        var constraints = vertical
            ? MeasureConstraints.Unconstrained.WithHeight(share)
            : MeasureConstraints.FitWidth(share);
        var source = SpliceOverset(members ?? box.Children, share, childContext, fragmentHeight, out var carried);

        // A bare column flex taller than the column is fragmented between its items - and inside one
        // that does not fit - so the column is handed the items rather than the container (CSS Flexbox
        // 1 §6, measured: single-line-column-flex-fragmentation-071).
        HashSet<Box>? flexItems = null;

        if (fragmentHeight > 0)
        {
            var opened = new List<Box>(source.Count);
            var expanded = false;

            foreach (var member in source)
            {
                if (member.Kind == BoxKind.Flex && !member.Style.Flex.IsRow &&
                    IsBareWrapper(member, share) &&
                    BlockExtentOf(_engine.Measure(
                        member,
                        vertical ? MeasureConstraints.Unconstrained.WithHeight(share) : MeasureConstraints.FitWidth(share),
                        childContext), vertical) > fragmentHeight + 0.001)
                {
                    flexItems ??= new HashSet<Box>();

                    foreach (var item in member.Children)
                    {
                        flexItems.Add(item);
                        opened.Add(item);
                    }

                    expanded = true;
                    continue;
                }

                opened.Add(member);
            }

            if (expanded)
            {
                source = opened;
            }
        }
        var children = new List<MulticolChild>(source.Count);

        // The room a cut is measured against: a container that fills its columns takes them one after
        // another, and one that states the height its fragmentainers have gives a balanced chain the
        // same fixed room - which is what lets a column end inside an empty box of a stated height and
        // the pieces march on past the count the page asked for (CSS Multicol 1 §1.2, §3.4, CSS
        // Fragmentation 3 §4.3, measured: flex-container-fragmentation-006, whose flex of 400 stands in
        // four hundred-tall columns while the container states a hundred and asks for four).
        var roomStated = !box.Style.Height.IsAuto || box.Style.MaxHeight.HasValue;

        // Which child a run of lines belongs to, so that the slicer can tell one block's lines from its
        // neighbour's and keep the orphans and widows each of them asks for.
        var runs = 0;

        foreach (var child in source)
        {
            if (!child.IsBlockLevel || PositionedLayout.IsOutOfFlow(child))
            {
                continue;
            }

            // What a lifted wrapper stood aside from comes along with the box it handed up.
            var aside = carried is not null && carried.TryGetValue(child, out var extra)
                ? extra
                : Thickness.Zero;
            var size = _engine.Measure(
                child,
                vertical || aside.Horizontal == 0
                    ? constraints
                    : MeasureConstraints.FitWidth(Math.Max(0.0, share - aside.Horizontal)),
                childContext);
            var isFlexItem = flexItems is not null && flexItems.Contains(child);
            var used = BoxModel.UsedMargin(child.Style, share, vertical ? size.Height : size.Width);

            // What stands before and after the child is the margin it presents to the stack, not only
            // the one it wrote: a first child's margin travels out through a parent that does not seal
            // it, and the column has to leave that room too.
            // The four sides are the box's own: block start and end, inline end and start. A box whose
            // lines run down the page stands its inline margins above and below instead of beside it.
            var margin = new Thickness(
                CollapsedStart(child, box.Flow, share),
                vertical ? used.Bottom : used.Right + aside.Right,
                CollapsedEnd(child, box.Flow, share),
                vertical ? used.Top : used.Left + aside.Left);
            var breaks = child.Style.Breaks;
            var (before, after) = BoundaryBreaks(child);

            // What a table's repeated header and footer groups cost every column it stands in, asked of
            // the table itself before its rows are shared out (CSS Tables 3 §6.1, measured:
            // variable-fragmentainer-size-001, whose columns give up 20 at the bottom and 20 at the top).
            var (repeatHead, repeatFoot) = _engine.TableRepeats(child);

            // A float the column holds directly spends none of the column's room: the plan gives it no
            // height of its own, so the boxes the flow carries after it go on filling the column in order
            // and the float overflows the column it was written in (CSS 2.1 §9.5.1, measured:
            // wpt/css-break/float-017). A column that reads sideways leaves its floats to the stack, as
            // they have always been placed there.
            if (!vertical && IsFloat(child))
            {
                children.Add(new MulticolChild(
                    child,
                    size,
                    margin,
                    new FragmentChild(0, before, after, breaks.Inside),
                    -1,
                    last: true,
                    whole: BlockExtentOf(size, false),
                    floats: true,
                    defers: size.Width + margin.Horizontal >= share - 0.001));
                continue;
            }

            // A child that can be broken contributes one piece per line of its own, so the column ends
            // between two of them (CSS Multicol 3 §3.1). The lines are those the plan makes at the width
            // of a column, which is the plan the arrangement lays out again - the same container at the
            // same extent, so the two passes agree without being told to.
            if (FragmentsInColumns(child))
            {
                var run = runs++;
                var lines = _inline.LineHeights(
                    child, share, child.Flow, new InlineContext(_engine, childContext, childContext, childContext));

                // A box with no lines has nothing to break between, but the column can still be cut
                // through it: where the column's own height is stated and the box is taller, it is
                // sliced rather than left to overflow the sheet whole.
                var breakable = false;

                foreach (var height in lines)
                {
                    breakable |= height > 0.001;
                }

                // A box whose lines are all empty - nothing but the placeholder an out-of-flow
                // child leaves on them - has nothing to break between: it is the ordinary child
                // below, which a column may divide where it runs out of room.
                if (!breakable)
                {
                    if (!(isFlexItem && CutsAsBox(
                        child, size, vertical, margin, fragmentHeight, columns,
                        box.Style.Multi.Fills, context.InFragmentainer)) && AddBoxCut(
                        children,
                        child,
                        size,
                        margin,
                        before,
                        after,
                        fragmentHeight,
                        columns,
                        box.Style.Multi.Fills,
                        roomStated,
                        context.InFragmentainer,
                        child.Style.ClonesDecoration
                            ? BorderOf(child).Vertical + child.Style.Padding.Resolve(share).Vertical
                            : 0.0,
                        BorderOf(child).Top + child.Style.Padding.Resolve(share).Top,
                        vertical,
                        repeatHead,
                        repeatFoot,
                        plansAcross))
                    {
                        continue;
                    }

                    var dividesNoLines = DividesAcross(child, vertical, margin, box.Style.Multi.Fills, context.InFragmentainer);
                    var extentNoLines = BlockExtentOf(size, vertical);

                    if (!dividesNoLines && isFlexItem && CutsAsBox(
                        child, size, vertical, margin, fragmentHeight, columns,
                        box.Style.Multi.Fills, context.InFragmentainer))
                    {
                        dividesNoLines = true;
                    }

                    if (!dividesNoLines && HoldsPositioned(child) && BlockSizeIsAuto(child, vertical))
                    {
                        extentNoLines = Math.Max(extentNoLines, PositionedReach(child, share, childContext));
                    }

                    if (dividesNoLines)
                    {
                        extentNoLines = Math.Max(extentNoLines, PositionedReach(child, share, childContext));
                        dividesNoLines = FitsTheColumns(extentNoLines, fragmentHeight, columns);

                        if (!dividesNoLines)
                        {
                            extentNoLines = BlockExtentOf(size, vertical);
                        }
                    }

                    children.Add(new MulticolChild(
                        child,
                        size,
                        margin,
                        new FragmentChild(
                            extentNoLines + margin.Top + margin.Bottom,
                            before,
                            after,
                            breaks.Inside),
                        -1,
                        last: true,
                        divides: dividesNoLines));

                    continue;
                }

                // The child's own edges belong to the pieces that really have them: a sliced box keeps its
                // top border and padding on its first piece and its bottom ones on its last, and the room
                // they take is part of what the column has to hold.
                var border = BorderOf(child);
                var padding = child.Style.Padding.Resolve(share);

                // The slicer counts the lines alone: the border and padding a piece draws belong to the
                // fragment the lines fall in, and only the arrangement knows which fragment that is.
                for (var line = 0; line < lines.Count; line++)
                {
                    var last = line == lines.Count - 1;
                    var height = lines[line];

                    children.Add(new MulticolChild(
                        child,
                        new Size(size.Width, height),
                        new Thickness(
                            line == 0 ? margin.Top : 0,
                            margin.Right,
                            last ? margin.Bottom : 0,
                            margin.Left),
                        new FragmentChild(
                            height + (line == 0 ? margin.Top : 0) + (last ? margin.Bottom : 0),
                            line == 0 ? breaks.Before : BreakValue.Auto,
                            last ? breaks.After : BreakValue.Auto,
                            BreakValue.Auto,
                            run,
                            breaks.Orphans,
                            breaks.Widows),
                        line,
                        last,
                        size.Height));
                }

                continue;
            }

            if (!(isFlexItem && CutsAsBox(
                child, size, vertical, margin, fragmentHeight, columns,
                box.Style.Multi.Fills, context.InFragmentainer)) && AddBoxCut(
                        children,
                        child,
                        size,
                        margin,
                        before,
                        after,
                        fragmentHeight,
                        columns,
                        box.Style.Multi.Fills,
                        roomStated,
                        context.InFragmentainer,
                        child.Style.ClonesDecoration
                            ? BorderOf(child).Vertical + child.Style.Padding.Resolve(share).Vertical
                            : 0.0,
                        BorderOf(child).Top + child.Style.Padding.Resolve(share).Top,
                        vertical,
                        repeatHead,
                        repeatFoot,
                        plansAcross))
            {
                continue;
            }

            var extent = BlockExtentOf(size, vertical);

            // A box that a whole column could hold, but not the room this one has left after the margin
            // it opens with, moves on as it stands: the margin stays spent where it stood, and the box
            // begins the next column at its head - the same break the column makes when a margin is
            // taller than it is, read one step earlier (CSS Fragmentation 3 §4.3, measured:
            // gecko/css-break/vertical-wm-001, whose box of 82 with a 40 margin-block-start fills the
            // second column from its edge while the first holds nothing but that margin).
            if (fragmentHeight > 0 && margin.Top > 0.001 &&
                extent <= fragmentHeight + 0.001 &&
                extent + margin.Top > fragmentHeight + 0.001)
            {
                children.Add(new MulticolChild(
                    child,
                    vertical ? new Size(0, size.Height) : new Size(size.Width, 0),
                    margin,
                    new FragmentChild(margin.Top, before, BreakValue.Auto, breaks.Inside),
                    -1,
                    false,
                    whole: extent,
                    ownsEdges: false));

                children.Add(new MulticolChild(
                    child,
                    size,
                    new Thickness(0, margin.Right, margin.Bottom, margin.Left),
                    new FragmentChild(extent + margin.Bottom, BreakValue.Auto, after, breaks.Inside),
                    -1,
                    last: true,
                    whole: extent));

                continue;
            }

            // A row flex that does not wrap sets every item at the container's own block start, so a
            // fragmentainer with no room at all can hold none of them where the container stands: each
            // item then takes a fragmentainer of its own, in the order their block starts fall, and
            // keeps the place the main axis gave it - nothing re-flowes, and the fill does not go
            // looking for columns for ever (CSS Flexbox 1 §6, measured:
            // single-line-row-flex-fragmentation-044, where the item that begins at the top stays in
            // the first column and overflows it, and the one behind a five-pixel block-start margin
            // begins the next, standing at its top with that margin spent on the break).
            if (ItemBandsForEmptyRoom(
                    child, vertical, box.Style.Multi.Fills, context.InFragmentainer,
                    share, fragmentHeight, childContext) is { Count: > 0 } bands)
            {
                for (var index = 0; index < bands.Count; index++)
                {
                    var (claimed, from, to) = bands[index];
                    var ends = index == bands.Count - 1;

                    children.Add(new MulticolChild(
                        child,
                        size,
                        new Thickness(
                            from > 0.001 ? 0 : margin.Top,
                            margin.Right,
                            ends ? margin.Bottom : 0,
                            margin.Left),
                        new FragmentChild(
                            to - from,
                            index == 0 ? before : BreakValue.Column,
                            ends ? after : BreakValue.Auto,
                            breaks.Inside),
                        -1,
                        ends,
                        whole: BlockExtentOf(size, vertical),
                        offset: from,
                        relays: true,
                        item: claimed));
                }

                continue;
            }

            // A wrapping row flex is cut window by window: each window begins at the first line the one
            // before it could not hold, so a gap the break fell in is dropped rather than kept whole.
            if (RowRelaysInColumns(child, vertical, extent, fragmentHeight, context.InFragmentainer))
            {
                var windows = _engine.FlexWindows(
                    child,
                    new Rect(0, 0, size.Width, size.Height),
                    childContext,
                    fragmentHeight);

                // A container that takes its block size from its lines is cut where a line holds a
                // single item, and where a window cuts a line in two and the window after it carries
                // the rest: a line of several items that the break falls between goes whole to the
                // next window (measured: gecko/flexbox/pagination/flexbox-multi-row-1a against
                // multi-line-row-flex-fragmentation-065, and -093 for the window that cuts a line).
                var autoBlock = BlockSizeIsAuto(child, vertical);

                if (windows.Count > 1 &&
                    (!autoBlock || windows.Count == child.Children.Count || windows.Any(static w => w.Cut)))
                {
                    for (var index = 0; index < windows.Count; index++)
                    {
                        var window = windows[index];
                        var firstWindow = index == 0;
                        var lastWindow = index == windows.Count - 1;

                        children.Add(new MulticolChild(
                            child,
                            size,
                            new Thickness(
                                firstWindow ? margin.Top : 0,
                                margin.Right,
                                lastWindow ? margin.Bottom : 0,
                                margin.Left),
                            new FragmentChild(
                                window.Height, firstWindow ? before : BreakValue.Auto,
                                lastWindow ? after : BreakValue.Auto, breaks.Inside),
                            -1,
                            last: lastWindow,
                            whole: size.Height,
                            offset: window.Offset,
                            relays: true));
                    }

                    continue;
                }
            }

            var byShape = DividesAcross(child, vertical, margin, box.Style.Multi.Fills, context.InFragmentainer);
            var divides = byShape ||
                DividesUnits(
                    child, vertical, margin, box.Style.Multi.Fills, context.InFragmentainer, share, childContext);
            var byUnits = !byShape && divides;
            var relays = !divides && RelaysInColumns(
                child, vertical, extent, fragmentHeight, context.InFragmentainer);

            if (!divides && isFlexItem && CutsAsBox(
                child, size, vertical, margin, fragmentHeight, columns,
                box.Style.Multi.Fills, context.InFragmentainer))
            {
                divides = true;
            }

            if (relays)
            {
                divides = true;
            }

            if (!divides && HoldsPositioned(child) && BlockSizeIsAuto(child, vertical))
            {
                extent = Math.Max(extent, PositionedReach(child, share, childContext));
            }

            if (divides)
            {
                var reach = PositionedReach(child, share, childContext);

                // A box the columns hold whole, with only what stands out of the flow reaching past its
                // last fragmentainer, is still cut: every fragmentainer paints the stretch of the
                // descendant it has room for, and what is left past the last column is lost with it.
                // Leaving the box whole instead loses all of it, because the piece that would have
                // carried it stands off the columns its own box is drawn through (CSS Fragmentation 3
                // §4.5 with CSS Position 3 §5.5, measured: out-of-flow-in-multicolumn-073, whose fixed
                // box of 200 in two columns of 100 is drawn twice - 50x100 at each column head).
                var reachesOut = byShape && fragmentHeight > 0.001 &&
                    reach > extent + 0.001 && BlockExtentOf(size, vertical) <= fragmentHeight + 0.001;

                extent = Math.Max(extent, reach);
                divides = reachesOut || FitsTheColumns(extent, fragmentHeight, columns);

                if (!divides)
                {
                    extent = BlockExtentOf(size, vertical);
                }
            }

            // A flex container the columns cut is measured by what its content holds: the edges of the
            // container belong to the pieces that own them, and the room a fragment shares out is the
            // content it carries (CSS Flexbox 1 §6).
            var relayed = relays
                ? Math.Max(0, extent - BorderOf(child).Vertical - child.Style.Padding.Resolve(share).Vertical)
                : extent;

            // Where a column may fall inside the box it divides: the ends of the box's own content
            // units, which is what keeps a piece from ending in the middle of a line the page has to
            // count for widows and orphans (CSS Break 3 §4.2).
            var units = divides && !relays && !vertical ? PieceBreaks(child, share, childContext) : null;

            // What the pieces paint: the unit that stands past the box is still shown, so the last piece
            // reaches to its end, while the height the flow advances by stays the box's own. Only for a
            // box cut because of what it holds: a box the shape rule divided - one that holds positioned
            // descendants, or a grid - keeps the pieces that rule was measured with (CSS Fragmentation 3
            // §4.3, CSS Position 3 §5.5).
            var painted = byUnits && !relays && units is { Count: > 0 }
                ? Math.Max(relayed, units[units.Count - 1].Offset)
                : relayed;

            children.Add(new MulticolChild(
                child,
                size,
                margin,
                new FragmentChild(
                    relayed + margin.Top + margin.Bottom, before, after, breaks.Inside,
                    splits: relays),
                -1,
                last: true,
                divides: divides,
                relays: relays,
                breaks: units,
                reach: painted + margin.Top + margin.Bottom));
        }

        return children;
    }

    /// <summary>
    /// Cuts a child that has nothing to break between across the columns, where the column's own
    /// height is stated and the child is taller than it: the fragmentainer fills with what stands,
    /// and the rest of the box is carried on to the next column rather than left to overflow the
    /// sheet whole (CSS Fragmentation 3 §4.3). Returns whether the pieces were added.
    /// </summary>
    /// <remarks>
    /// What has structure of its own to break at is left to that - a replaced box is one picture, and
    /// a container's own children are the next step of the fragmentainer. A box that asks not to be
    /// broken is still cut once it is taller than the whole fragmentainer: the preference is spent
    /// when no fragmentainer can hold it (CSS Fragmentation 3 §4.3).
    /// </remarks>
    private static Boolean AddBoxCut(
        List<MulticolChild> children,
        Box child,
        Size size,
        Thickness margin,
        BreakValue before,
        BreakValue after,
        Double columnHeight,
        Int32 columns,
        Boolean fills,
        Boolean roomStated,
        Boolean nested,
        Double clonedInsets,
        Double startInset,
        Boolean vertical,
        Double repeatHead = 0,
        Double repeatFoot = 0,
        Boolean plansAcross = false)
    {
        // Only a box that holds nothing is cut here, and only where the room a piece stands in is a
        // fixed one: a container that fills its columns, or one that states the height its
        // fragmentainers have. A balanced container of an automatic height finds its own height from
        // what it holds instead, and cutting it would cut at a room that is not settled yet (CSS
        // Fragmentation 3 §4.3). An empty box has no content to keep whole, so its height is all its
        // own sizing - what it states, what its minimum makes it, what its border and padding add -
        // and a column shorter than that has to be cut through it.
        //
        // The stated height alone is room enough to cut an empty box, and no more: a balanced container
        // still leaves a box that holds children to those children's own breaks, which is what keeps a
        // clipping container whole (measured: overflow-clip-margin-mul-column-border-box, whose box of
        // content states its height inside a body that does too).
        var cutsHere = fills || (roomStated && child.Children.Count == 0);


        if (columnHeight <= 0 ||
            BlockExtentOf(size, vertical) <= columnHeight + 0.001 ||
            child.IsReplaced ||

            // A box that contains its size on the axis the column cuts has no break point in it at all,
            // however empty it is: the room it states is its own, so the column carries it whole to where
            // it stands and lets it overflow from there.
            IsMonolithic(child, vertical) ||

            // A box whose children are only the whitespace between tags holds nothing to break at,
            // and one that clones its decoration can be cut through that nothing: every piece is a
            // box of its own, with its own edges, so no edge has to be shared out between them
            // (measured on box-decoration-break-clone-009, whose box is written across two lines).
            // One that is merely sliced keeps its edges on the pieces the cutting pass places, which
            // is not this pass's work yet, so it is left whole.
            child.Children.Count > 0 && !(child.Style.ClonesDecoration && !HoldsContent(child)) &&
                !child.Style.ClipsOverflow && child.Kind != BoxKind.Table &&
                (!HoldsPositioned(child) || vertical) ||
            BlockSizeIsAuto(child, vertical) && !HasBlockMinimum(child, vertical) &&
                // A table sizes its block extent from its rows, and the rows are the break points the
                // column needs - along either axis: a vertical container's table runs its captions and
                // rows across the block axis (the physical x) and each column holds what fits of them,
                // the same way a horizontal one cuts down the page (measured: table-grid-paint-vrl-rtl
                // and -vlr-rtl, whose 140-tall caption and 225-tall row each come out 100 / 100 / 40 and
                // 50 / 100 / 75 across the four columns).
                !HasBlockMaximum(child, vertical) && (child.Kind != BoxKind.Table || HoldsPositioned(child)) ||
            !cutsHere ||
            child.Style.Multi.IsMultiColumn ||
            child.Kind is BoxKind.Grid or BoxKind.Masonry ||

            // Every piece has to have a column to stand in: a container of a stated count draws no
            // column past its last one, so a box that needs more pieces than it has columns would
            // lose the rest of it (CSS Multicol 1 §1.2).
            Math.Ceiling(BlockExtentOf(size, vertical) / columnHeight - 0.001) > columns ||

            // A container inside another one's columns is not taken apart by its own columns yet:
            // its pieces would have to be placed by the columns around it, which do not cut
            // through a nested container, so the box is left as it was rather than lose its tail.
            nested)
        {
            return false;
        }

        // A vertical column cuts a box the same way, but where a piece stands is only settled once the
        // columns have shared the flow out, so the box is marked as one that divides and the plan does
        // the cutting. Pre-cutting here fixed every piece to a whole column, which left the room a cut
        // one did not use in a column to nobody: the box after it moved on to a fresh column instead of
        // taking what its own column still had (CSS Fragmentation 3 §4.3, measured: the reference page
        // of table-grid-paint-vrl-rtl, whose three boxes fill the room each column is left with).
        //
        // `plansAcross` is the container's say on this: it is not given to a run whose children bring
        // margins, which a vertical box pays along the block axis the plan does not spend on, nor to one
        // that asks not to be broken, since Chrome gives every piece of such a box the head of a column
        // (measured: break-inside-avoid-min-block-size-2, whose two boxes of 40 in columns of 30 come out
        // 30 + 10 at the heads of four columns, not 30 + 10 + 20 + 20 of three).
        if (vertical && plansAcross)
        {
            var wholeAcross = BlockExtentOf(size, vertical);

            children.Add(new MulticolChild(
                child,
                size,
                margin,
                new FragmentChild(
                    wholeAcross + margin.Top + margin.Bottom,
                    before,
                    after,
                    child.Style.Breaks.Inside),
                -1,
                last: true,
                whole: wholeAcross,
                divides: true,
                startInset: startInset));

            return true;
        }

        // A box that repeats its decoration at every break adds its border and padding to each piece,
        // so what the column holds is the content (CSS Break 3 §4.7).
        var blockExtent = BlockExtentOf(size, vertical);
        var left = Math.Max(0, blockExtent - clonedInsets);
        var offset = 0.0;
        var pieces = 0;
        var top = startInset;

        // A table's repeated header and footer groups cost the column room of their own: a column after
        // the first gives its top to the copy of the header and a column the table carries on from gives
        // its bottom to the copy of the footer, and only what is left between them is the table's own
        // content (CSS Tables 3 §6.1, measured: variable-fragmentainer-size-001, whose 170 of rows come
        // out 80 / 60 / 30 in three columns of 100). Whether a table repeats at all is settled by the
        // room it finds in its FIRST column: Chrome repeats nothing once that room falls short of twice
        // the groups' height - measured across rooms 100 / 90 / 80 (three copies of each group) against
        // 70 / 60 / 50 / 40 (one, with no copy in the later full columns) in build/_repeat_matrix3.html -
        // which is why -002, whose first column keeps only 50 for the table, is drawn with nothing
        // repeated anywhere.
        var flowed = 0.0;

        foreach (var earlier in children)
        {
            flowed += earlier.Fragment.Height;
        }

        var groups = repeatHead + repeatFoot;
        var repeats = !vertical && fills && child.Kind == BoxKind.Table && groups > 0.001 &&
            columnHeight - flowed % columnHeight >= 2 * groups - 0.001 &&
            (left <= columnHeight - repeatFoot ||
             1 + (Int32)Math.Ceiling((left - (columnHeight - repeatFoot)) /
                 Math.Max(1.0, columnHeight - groups)) <= columns);

        while (left > 0.001)
        {
            var first = pieces == 0;

            // The margin only stands in front of the first piece; a piece that carries on in another
            // column has the whole column to stand in. A margin the column cannot hold is spent on the
            // column it stands in, and the box begins the next fragmentainer with nothing of it left:
            // the margin is therefore its own zero-height piece, since a piece that holds content may
            // not be pushed out of the way by the room in front of it (CSS Fragmentation 3 §4.3,
            // measured: flex-container-fragmentation-006, whose two-hundred margin leaves a column of
            // its own and a four-hundred box to the four columns after it).
            var pushed = first && margin.Top >= columnHeight - clonedInsets;
            var headMargin = pushed || !first ? 0.0 : margin.Top;
            var head = repeats && !first ? repeatHead : 0.0;
            var room = Math.Max(1, columnHeight - clonedInsets - headMargin - head);
            var content = Math.Min(left, room);
            var tail = 0.0;

            if (repeats && left - content > 0.001)
            {
                content = Math.Min(content, Math.Max(1.0, room - repeatFoot));
                tail = repeatFoot;
            }

            var height = content + clonedInsets;
            var last = left - content <= 0.001;

            if (pushed)
            {
                // The margin's own piece holds no content and draws none of the box's edges: those
                // belong to the first piece that shows the box (CSS Break 3 §4.3, §4.7).
                children.Add(new MulticolChild(
                    child,
                    vertical ? new Size(0, size.Height) : new Size(size.Width, 0),
                    new Thickness(columnHeight - clonedInsets, margin.Right, 0, margin.Left),
                    new FragmentChild(columnHeight, BreakValue.Auto, BreakValue.Auto, child.Style.Breaks.Inside),
                    -1,
                    false,
                    blockExtent,
                    0));
            }

            children.Add(new MulticolChild(
                child,
                vertical ? new Size(height, size.Height) : new Size(size.Width, height),
                new Thickness(
                    first ? headMargin : 0,
                    margin.Right,
                    last ? margin.Bottom : 0,
                    margin.Left),
                new FragmentChild(
                    height + head + tail + headMargin + (last ? margin.Bottom : 0),
                    first ? before : BreakValue.Auto,
                    last ? after : BreakValue.Auto,
                    child.Style.Breaks.Inside),
                -1,
                last,
                blockExtent,
                offset,
                startInset: first || child.Style.ClonesDecoration ? top : 0.0,
                lead: head,
                trail: tail));

            left -= content;
            offset += height;
            pieces++;
        }

        return pieces > 1;
    }

    /// <summary>Gets whether a box or anything inside it floats, which is the pair a column that runs
    /// down the page does not pack yet.</summary>
    private static Boolean HoldsAnyFloat(Box box, IReadOnlyList<Box> children)
    {
        foreach (var child in children)
        {
            if (IsFloat(child) || HoldsAnyFloat(child, child.Children))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Gets whether every in-flow block child of the box floats, and there is at least one.</summary>
    private static Boolean AllFloats(Box box)
    {
        var floats = 0;

        foreach (var child in box.Children)
        {
            if (!child.IsBlockLevel || PositionedLayout.IsOutOfFlow(child))
            {
                continue;
            }

            if (child.Style.Float == FloatSide.None)
            {
                return false;
            }

            floats++;
        }

        return floats > 0;
    }

    /// <summary>The in-flow floating children of a box, in the order the page wrote them.</summary>
    private static IEnumerable<Box> FloatChildren(Box box)
    {
        foreach (var child in box.Children)
        {
            if (child.IsBlockLevel && !PositionedLayout.IsOutOfFlow(child) && child.Style.Float != FloatSide.None)
            {
                yield return child;
            }
        }
    }

    /// <summary>A float's border box and the margins that stand around it, which is the room it claims.</summary>
    private readonly record struct MeasuredFloat(Size Size, Thickness Margin);

    /// <summary>
    /// Measures one float against a column's width: a declared width is what it is, and one that is
    /// <c>auto</c> shrinks to fit the room the column leaves, exactly as a float in a block does.
    /// </summary>
    private MeasuredFloat MeasureFloatBox(Box child, Double share, LayoutContext context)
    {
        var margin = child.Style.Margin.Resolve(share);
        var size = child.Style.Width.HasValue
            ? _engine.Measure(child, MeasureConstraints.FitWidth(share), context)
            : ShrinkToFit(child, margin, share, context);

        return new MeasuredFloat(size, margin);
    }

    /// <summary>
    /// Measures a multi-column container whose content is nothing but floats: each column takes the
    /// floats that fit side by side in it, and one that does not fit begins the column after the gap.
    /// The box's content width is shared out by the same packing the arrangement runs, so the height it
    /// comes to is the tallest column's (CSS Multicol 3 §7).
    /// </summary>
    private Double MeasureFloatColumns(Box box, Double share, Double declaredHeight, Boolean heightStated, LayoutContext context)
    {
        var childContext = context.ForContainingBlock(new Size(share, declaredHeight));
        var used = 0.0;
        var columnHeight = 0.0;
        var tallest = 0.0;

        foreach (var child in FloatChildren(box))
        {
            var measured = MeasureFloatBox(child, share, childContext);
            var outer = measured.Margin.Horizontal + measured.Size.Width;

            if (used > 0 && used + outer > share + 0.001)
            {
                tallest = Math.Max(tallest, columnHeight);
                used = 0;
                columnHeight = 0;
            }

            used += outer;
            columnHeight = Math.Max(columnHeight, measured.Margin.Vertical + measured.Size.Height);
        }

        tallest = Math.Max(tallest, columnHeight);

        return heightStated ? declaredHeight : tallest;
    }

    /// <summary>
    /// Arranges a multi-column container whose content is nothing but floats: the same packing the
    /// measuring pass ran, with each column's floats standing side by side from its top.
    /// </summary>
    private Double ArrangeFloatColumns(
        Box box,
        LayoutBox parent,
        Rect content,
        Int32 columns,
        Double share,
        Boolean heightStated,
        Double declaredHeight,
        LayoutContext context)
    {
        var multi = box.Style.Multi;
        var childContext = context.ForContainingBlock(new Size(share, heightStated ? declaredHeight : 0));
        var used = 0.0;
        var columnHeight = 0.0;
        var tallest = 0.0;
        var column = 0;

        foreach (var child in FloatChildren(box))
        {
            var measured = MeasureFloatBox(child, share, childContext);
            var outer = measured.Margin.Horizontal + measured.Size.Width;

            if (used > 0 && used + outer > share + 0.001)
            {
                tallest = Math.Max(tallest, columnHeight);
                column++;
                used = 0;
                columnHeight = 0;
            }

            var x = content.X + column * (share + multi.Gap) + used;
            var y = content.Y + measured.Margin.Top;

            parent.Children.Add(_engine.Arrange(
                child,
                new Rect(x + measured.Margin.Left, y, measured.Size.Width, measured.Size.Height),
                childContext));

            used += outer;
            columnHeight = Math.Max(columnHeight, measured.Margin.Vertical + measured.Size.Height);
        }

        tallest = Math.Max(tallest, columnHeight);

        var height = heightStated ? declaredHeight : tallest;

        MulticolRules.Add(
            parent,
            box.Style,
            content,
            new List<(Double, Double)> { (content.Y, content.Y + height) });

        _engine.PublishFloats(box, null);

        return height;
    }

    /// <summary>One box a column took in the float-and-flow sharing: where its piece stands inside the
    /// column, how much of it the column shows, and how far inside the box that piece begins. A whole
    /// box shows all of itself from zero.</summary>
    private readonly record struct FloatFlowItem(
        Box Child,
        Double X,
        Double Y,
        Size Size,
        Thickness Margin,
        Double Visible = 0,
        Double Slice = 0,
        Double Whole = 0);

    /// <summary>A child of a float-and-flow container: the box the column takes and the width it is
    /// measured at, which is the column's own except for a float lifted out of a box that only stands
    /// aside it.</summary>
    private readonly record struct FloatFlowMember(Box Child, Double Width);

    /// <summary>One column the float-and-flow sharing works over: the floats standing there, what the
    /// column holds, and how far down its own flow has come. The flow keeps its own place because a
    /// float pushed on to the next column leaves the flow behind in the one it was written in.</summary>
    private sealed class FloatFlowColumn
    {
        public FloatFlowColumn(FloatTracks tracks)
        {
            Tracks = tracks;
        }

        public FloatTracks Tracks { get; }

        public List<FloatFlowItem> Items { get; } = new();

        public Double Flow { get; set; }
    }

    /// <summary>Gets whether a box is nothing but a room for the floats it holds: a plain block with no
    /// decoration, no size of its own and no content except floats, so the floats are what the column
    /// places and the box only narrows them (measured: overflowing-block-003, whose 25px right margin
    /// makes its float 50 wide in a 75px column).</summary>
    private static Boolean FloatHostWrapper(Box child, Double share)
    {
        if (child.Kind != BoxKind.Block || child.Children.Count == 0 || child.Flow.IsVertical)
        {
            return false;
        }

        var style = child.Style;

        if (style.Multi.IsMultiColumn || style.ClipsOverflow ||
            !style.Width.IsAuto || !style.Height.IsAuto ||
            style.BackgroundColor is { A: > 0 } || style.Backgrounds.Count > 0 ||
            style.Breaks.Before != BreakValue.Auto || style.Breaks.After != BreakValue.Auto ||
            style.Breaks.Inside != BreakValue.Auto)
        {
            return false;
        }

        var border = BorderOf(child);
        var padding = style.Padding.Resolve(share);

        if (border.Horizontal != 0 || border.Vertical != 0 ||
            padding.Horizontal != 0 || padding.Vertical != 0)
        {
            return false;
        }

        var floats = 0;

        foreach (var inner in child.Children)
        {
            if (!inner.IsBlockLevel || PositionedLayout.IsOutOfFlow(inner))
            {
                continue;
            }

            if (!IsFloat(inner))
            {
                return false;
            }

            floats++;
        }

        return floats > 0;
    }

    /// <summary>The children the float-and-flow columns take: the container's own, with a box that is
    /// only a room for its floats giving up its place to them.</summary>
    private List<FloatFlowMember> FloatFlowMembers(Box box, Double share, LayoutContext context)
    {
        var members = new List<FloatFlowMember>();

        foreach (var child in EffectiveMembers(box.Children, share))
        {
            if (!child.IsBlockLevel || PositionedLayout.IsOutOfFlow(child))
            {
                continue;
            }

            if (FloatHostWrapper(child, share))
            {
                var room = Math.Max(
                    0.0,
                    share - BoxModel.UsedMargin(child.Style, share, share).Horizontal);

                foreach (var inner in child.Children)
                {
                    if (inner.IsBlockLevel && !PositionedLayout.IsOutOfFlow(inner) && IsFloat(inner))
                    {
                        members.Add(new FloatFlowMember(inner, room));
                    }
                }

                continue;
            }

            members.Add(new FloatFlowMember(child, share));
        }

        return members;
    }

    /// <summary>Gets the furthest end of a box's own content units that stands within the room given,
    /// which is where a column that runs out of room cuts it (CSS Break 3 §4.2).</summary>
    private static Double LastBreakAtMost(IReadOnlyList<Double>? units, Double room)
    {
        if (units is null)
        {
            return 0.0;
        }

        var best = 0.0;

        foreach (var at in units)
        {
            if (at > 0.001 && at <= room + 0.001)
            {
                best = Math.Max(best, at);
            }
        }

        return best;
    }

    /// <summary>Cuts a box down the columns it stands in: each column shows the part of it it has room
    /// for, the cut stays where the box's own content units end, and what is left begins at the top of
    /// the next column (CSS Fragmentation 3 §4.3, §4.5). Each piece comes back with how far inside the
    /// box it begins, how tall the column shows it, where it stands in that column, and whether it
    /// opens a new column.</summary>
    private static List<(Double Slice, Double Visible, Double Y, Boolean Fresh)> CutDownColumns(
        Double height,
        IReadOnlyList<Double>? units,
        Double startY,
        Double columnHeight,
        Thickness margin)
    {
        var pieces = new List<(Double Slice, Double Visible, Double Y, Boolean Fresh)>();
        var left = height;
        var slice = 0.0;
        var y = startY;
        var fresh = false;
        var tries = 2 + (Int32)Math.Ceiling(height / Math.Max(1.0, columnHeight));

        for (var taken = 0; taken < tries && left > 0.001; taken++)
        {
            var room = columnHeight - y - margin.Bottom;

            if (room < 0.001)
            {
                y = margin.Top;
                room = columnHeight - y - margin.Bottom;

                if (room < 0.001)
                {
                    break;
                }

                fresh = true;
            }

            var show = left;

            if (show > room)
            {
                show = LastBreakAtMost(units, room);

                if (show < 0.001)
                {
                    show = room;
                }
            }

            pieces.Add((slice, show, y, fresh));
            slice += show;
            left -= show;
            y += show;
            fresh = false;
        }

        return pieces;
    }

    /// <summary>Gets how far a box's own in-flow content reaches below its top, which is further than
    /// the height it states whenever that height is less than what it holds: the column that shows the
    /// box paints as far as the content reaches while the flow it stands in advances only by the height
    /// the box states (CSS 2.1 §10.6.3, CSS Fragmentation 3 §4.3).</summary>
    private Double ContentReach(Box box, Double width, LayoutContext context)
    {
        var border = BorderOf(box);
        var padding = box.Style.Padding.Resolve(width);
        var available = Math.Max(0, width - border.Horizontal - padding.Horizontal);
        var content = context.ForContainingBlock(new Size(available, 0));
        var cursor = border.Top + padding.Top;
        var reach = cursor;

        foreach (var child in box.Children)
        {
            if (!child.IsBlockLevel || PositionedLayout.IsOutOfFlow(child) || IsFloat(child))
            {
                continue;
            }

            var size = _engine.Measure(child, MeasureConstraints.FitWidth(available), content);
            var margin = BoxModel.UsedMargin(child.Style, width, size.Width);

            cursor += margin.Top;
            reach = Math.Max(reach, cursor + size.Height);
            cursor += size.Height + margin.Bottom;
        }

        return reach;
    }

    /// <summary>Gets the content box of a fragment, measured from the piece's own rectangle - the
    /// same insets the unbroken box would take, applied to the stretch this piece stands in.</summary>
    private static Rect PiecePadding(Box box, Rect piece, Thickness border, Thickness padding) =>
        new Rect(
            piece.X + border.Left + padding.Left,
            piece.Y + border.Top + padding.Top,
            Math.Max(0, piece.Width - border.Horizontal - padding.Horizontal),
            Math.Max(0, piece.Height - border.Vertical - padding.Vertical));

    /// <summary>Gets whether a container is the shape the float-and-flow columns handle: a horizontal
    /// container that fills columns of a stated height and holds both a float of its own and a box that
    /// stands in the flow (CSS 2.1 §9.5, CSS Multicol 3 §7, CSS Fragmentation 3 §4.3).</summary>
    private Boolean FloatFlowColumns(
        Box box,
        Boolean heightStated,
        Double declaredHeight,
        Double share,
        LayoutContext context)
    {
        if (!heightStated || declaredHeight <= 0 || box.Flow.IsVertical ||
            !box.Style.Multi.Fills || box.Style.Multi.Wraps)
        {
            return false;
        }

        var childContext = context.ForContainingBlock(new Size(share, declaredHeight));
        var floats = 0;

        foreach (var child in box.Children)
        {
            if (!child.IsBlockLevel || PositionedLayout.IsOutOfFlow(child))
            {
                continue;
            }

            if (IsFloat(child))
            {
                floats++;
                continue;
            }

            // A box that is only a room for its floats brings no flow of its own: what the column
            // shares out are the floats it holds.
            if (FloatHostWrapper(child, share))
            {
                floats += child.Children.Count(inner => IsFloat(inner));
                continue;
            }

            var size = _engine.Measure(child, MeasureConstraints.FitWidth(share), childContext);
            var margin = BoxModel.UsedMargin(child.Style, share, size.Width);

            // A box of the flow taller than a column of its own is cut across them here, every piece
            // taking the width its own column leaves beside the floats standing there. A box with lines
            // of its own is not: its pieces would each have to break the text again, which is the
            // ordinary columns' work (CSS Fragmentation 3 §4.3, §4.5).
            if (size.Height + margin.Vertical > declaredHeight + 0.001 &&
                BreaksAtLines(child))
            {
                return false;
            }
        }

        // A container whose floats are all it has to share - wrapped or not - comes here too: the one
        // of nothing but floats written straight stands handled by the packing above.
        return floats > 0;
    }

    /// <summary>
    /// Shares a container's floats and flow boxes over its columns. The flow moves down the column and
    /// stands clear of the floats it refuses to sit beside; a float takes no room of the flow's own and
    /// stands at the edge its side names, so a left and a right one fill the same band; and whatever
    /// finds no room left below it in this column begins the next one (CSS 2.1 §9.5.1-§9.5.2, CSS
    /// Fragmentation 3 §4.3, measured: tall-float-pushed-to-next-fragmentainer-002, whose two floats
    /// share the second column at 98,50 and 58,50 while the box that clears them comes to 58,110).
    /// </summary>
    private List<List<FloatFlowItem>> PlaceFloatFlowColumns(
        Box box,
        Double share,
        Double columnHeight,
        LayoutContext context)
    {
        var childContext = context.ForContainingBlock(new Size(share, columnHeight));
        var columns = new List<FloatFlowColumn>();

        // A column the sharing has not reached yet is made when it is first asked for, so a float pushed
        // forward and the flow that comes to the same column later find the floats that stand in it.
        FloatFlowColumn At(Int32 index)
        {
            while (columns.Count <= index)
            {
                columns.Add(new FloatFlowColumn(new FloatTracks(0, share)));
            }

            return columns[index];
        }

        // Whether the whole of a float stands in this column: it takes the first band the floats already
        // there leave wide enough for it, and the column has to hold the rest of it below that
        // (CSS 2.1 §9.5.1, CSS Fragmentation 3 §4.3).
        static Boolean Holds(FloatFlowColumn column, Size size, Thickness margin, Double columnHeight)
        {
            var stood = column.Tracks.FitBelow(0.0, size.Width);

            return stood + size.Height + margin.Bottom <= columnHeight + 0.001;
        }

        var at = 0;
        var members = FloatFlowMembers(box, share, childContext);

        foreach (var member in members)
        {
            var child = member.Child;
            var size = _engine.Measure(child, MeasureConstraints.FitWidth(member.Width), childContext);
            var margin = BoxModel.UsedMargin(child.Style, member.Width, size.Width);
            var outer = size.Height + margin.Top + margin.Bottom;
            var here = At(at);
            var floating = IsFloat(child);

            // A float's own band begins where the flow stands, since nothing of the flow's is spent on
            // it, though a float that asks for clearance begins it below the floats on the side it
            // clears; a box of the flow begins where it clears the floats it will not stand beside, and
            // a box that holds lines begins where those lines first find their width - a block box may
            // overlap a float, but a line that has no room beside one goes below it
            // (CSS 2.1 §9.5.1-§9.5.2, measured: widows-orphans-020 against
            // tall-float-pushed-to-next-fragmentainer-002, whose cleared box keeps overlapping the
            // narrow right-hand float rather than going below it).
            var top = floating
                ? child.Style.Clear == ClearSide.None
                    ? here.Flow + margin.Top
                    : here.Tracks.ClearBelow(child.Style.Clear, here.Flow) + margin.Top
                : BreaksAtLines(child)
                    ? here.Tracks.FitBelow(here.Tracks.ClearBelow(child.Style.Clear, here.Flow), size.Width)
                    : here.Tracks.ClearBelow(child.Style.Clear, here.Flow);

            if (floating)
            {
                // A column with no room left for a float carries the float on to the next one while the
                // flow it stood in stays behind: the float spends none of that flow, so a box written
                // after the float is still measured against the column the float was written in, and it
                // finds the pushed float waiting in the column after that
                // (CSS 2.1 §9.5.1, CSS Fragmentation 3 §4.3, measured:
                // fragmented-autowidth-fc-root-beside-floats,
                // tall-float-pushed-to-next-fragmentainer-002).
                var slot = here;
                var slotIndex = at;

                if (slot.Items.Count > 0 && top + size.Height + margin.Bottom > columnHeight + 0.001)
                {
                    slotIndex++;
                    slot = At(slotIndex);
                    top = margin.Top;

                    // A float taller than a whole column of its own is cut down them wherever it stands,
                    // so it takes the first column offered; one that a whole column can hold looks for a
                    // column that really has room for it, since the floats already waiting there may
                    // leave it no width beside them (CSS 2.1 §9.5.1, measured: gecko/bugs/563584-9d,
                    // whose three 200-wide floats each take a column of their own, against
                    // tall-float-pushed-to-next-fragmentainer-002, whose left and right float share the
                    // second column).
                    if (outer <= columnHeight + 0.001)
                    {
                        while (slotIndex < at + members.Count + 2 &&
                            !Holds(slot, size, margin, columnHeight))
                        {
                            slotIndex++;
                            slot = At(slotIndex);
                            top = margin.Top;
                        }
                    }
                }

                // A float that contains its block size is monolithic, so no column of its own cuts it
                // either: it stands whole where the push left it and overflows from there (CSS Contain 1
                // §3.1.1, measured: float-017).
                if (outer <= columnHeight + 0.001 || IsMonolithic(child, false))
                {
                    var whole = slot.Tracks.Place(
                        child.Style.Float,
                        size.Width,
                        size.Height,
                        margin,
                        top - margin.Top,
                        owner: child);

                    slot.Items.Add(new FloatFlowItem(child, whole.X, whole.Y, size, margin));
                    continue;
                }

                // A float taller than a column of its own is not left to overflow the sheet whole: the
                // column shows what it has room for, cut where the float's own lines end, and the rest
                // begins at the top of the next one (CSS Fragmentation 3 §4.5, measured:
                // overflowing-block-003, whose 100-tall float comes to 50x50 in each of two columns).
                var cuts = BreaksAtLines(child)
                    ? LineBreaks(child, member.Width, childContext)
                    : null;
                var placedColumn = slot;

                foreach (var piece in CutDownColumns(
                    size.Height,
                    cuts,
                    top,
                    columnHeight,
                    margin))
                {
                    if (piece.Fresh)
                    {
                        slotIndex++;
                        placedColumn = At(slotIndex);
                    }

                    var placed = placedColumn.Tracks.Place(
                        child.Style.Float,
                        size.Width,
                        piece.Visible,
                        margin,
                        piece.Y - margin.Top,
                        owner: child);

                    placedColumn.Items.Add(new FloatFlowItem(
                        child,
                        placed.X,
                        placed.Y,
                        size,
                        margin,
                        piece.Visible,
                        piece.Slice));
                }

                continue;
            }

            // A box of the flow taller than a column of its own is cut down the columns, and each piece
            // takes the width the column it stands in leaves beside the floats standing there: the
            // first piece beside the float that stayed, the ones after it beside the floats that were
            // pushed on (CSS 2.1 §9.4.1, §9.5, CSS Fragmentation 3 §4.3, measured:
            // fragmented-autowidth-fc-root-beside-floats, whose 300-tall root of a formatting context
            // comes to 100, 10 and 200 wide in the three columns). A box with lines of its own is left
            // to the ordinary columns, which break its text anew in every piece.
            if (outer > columnHeight + 0.001 && !BreaksAtLines(child))
            {
                var piece = at;
                var bottom = top;

                foreach (var part in CutDownColumns(
                    size.Height,
                    null,
                    top,
                    columnHeight,
                    margin))
                {
                    if (part.Fresh)
                    {
                        piece++;
                    }

                    var state = At(piece);
                    var (left, right) = state.Tracks.FreeSpanAt(part.Y, part.Visible);

                    state.Items.Add(new FloatFlowItem(
                        child,
                        left + margin.Left,
                        part.Y,
                        new Size(Math.Max(0.0, right - left - margin.Horizontal), size.Height),
                        margin,
                        part.Visible,
                        part.Slice,
                        size.Height));

                    bottom = part.Y + part.Visible;
                }

                At(piece).Flow = bottom + margin.Bottom;
                at = piece;
                continue;
            }

            // A box whose own content reaches past the height it states is cut down the columns the same
            // way, while the flow it stands in advances only by that stated height (CSS 2.1 §10.6.3,
            // CSS Fragmentation 3 §4.3; measured: overflowing-block-003, whose second box wraps a
            // 100-tall block in a height of 0 and still comes to two 50x50 squares).
            var reach = ContentReach(child, member.Width, childContext);

            if (reach > size.Height + 0.001)
            {
                var piece = at;

                foreach (var part in CutDownColumns(
                    reach,
                    PieceOffsets(PieceBreaks(child, member.Width, childContext)),
                    top,
                    columnHeight,
                    margin))
                {
                    if (part.Fresh)
                    {
                        piece++;
                    }

                    At(piece).Items.Add(new FloatFlowItem(
                        child,
                        margin.Left,
                        part.Y,
                        size,
                        margin,
                        part.Visible,
                        part.Slice,
                        reach));
                }

                At(piece).Flow = top + size.Height + margin.Bottom;
                at = piece;
                continue;
            }

            if (here.Items.Count > 0 && top + size.Height + margin.Bottom > columnHeight + 0.001)
            {
                at++;
                here = At(at);
                top = BreaksAtLines(child)
                    ? here.Tracks.FitBelow(here.Tracks.ClearBelow(child.Style.Clear, here.Flow), size.Width)
                    : here.Tracks.ClearBelow(child.Style.Clear, here.Flow);
            }

            here.Items.Add(new FloatFlowItem(child, margin.Left, top, size, margin));
            here.Flow = top + size.Height + margin.Bottom;
        }

        var runs = new List<List<FloatFlowItem>>();

        foreach (var state in columns)
        {
            runs.Add(state.Items);
        }

        return runs;
    }

    /// <summary>Measures that sharing: the container keeps the height it was told to be, and what does
    /// not fit overflows it the way a browser lets it overflow (CSS Multicol 3 §7).</summary>
    private Double MeasureFloatFlowColumns(
        Box box,
        Double share,
        Double declaredHeight,
        LayoutContext context)
    {
        return declaredHeight;
    }

    /// <summary>Arranges that sharing: each run stands in its own column, with the column's rules drawn
    /// across the height the container came to.</summary>
    private Double ArrangeFloatFlowColumns(
        Box box,
        LayoutBox parent,
        Rect content,
        Int32 columns,
        Double share,
        Double declaredHeight,
        LayoutContext context)
    {
        var multi = box.Style.Multi;
        var runs = PlaceFloatFlowColumns(box, share, declaredHeight, context);
        var childContext = context.ForContainingBlock(new Size(share, declaredHeight));

        for (var run = 0; run < runs.Count && run < columns; run++)
        {
            var originX = content.X + run * (share + multi.Gap);

            foreach (var item in runs[run])
            {
                var visible = item.Visible > 0.001 ? item.Visible : item.Size.Height;
                var wholeHeight = item.Whole > 0.001 ? item.Whole : item.Size.Height;
                var starts = item.Slice < 0.001;
                var ends = item.Slice + visible >= wholeHeight - 0.001;
                var border = BorderOf(item.Child);
                var padding = item.Child.Style.Padding.Resolve(item.Size.Width);
                var whole = new Rect(
                    originX + item.X,
                    content.Y + item.Y - item.Slice,
                    item.Size.Width,
                    wholeHeight);
                var held = new Rect(originX + item.X, content.Y + item.Y, item.Size.Width, visible);
                var arranged = _engine.Arrange(
                    item.Child,
                    whole,
                    childContext,
                    definiteHeight: false,
                    slice: new FragmentSlice(
                        new Thickness(
                            starts ? border.Top : 0,
                            border.Right,
                            ends ? border.Bottom : 0,
                            border.Left),
                        new Thickness(
                            starts ? padding.Top : 0,
                            padding.Right,
                            ends ? padding.Bottom : 0,
                            padding.Left),
                        starts,
                        ends,
                        whole,
                        item.Slice,
                        visible));

                // The piece is drawn only where its column stands, which is what lets a float taller
                // than the column be carried on in the next one (CSS Fragmentation 3 §4.1).
                arranged.FragmentClip = held;
                arranged.FragmentCutsInline = false;
                parent.Children.Add(arranged);
            }
        }

        MulticolRules.Add(
            parent,
            box.Style,
            content,
            new List<(Double, Double)> { (content.Y, content.Y + declaredHeight) });

        _engine.PublishFloats(box, null);

        return declaredHeight;
    }

    /// <summary>A block whose lines live in the one in-flow block it holds, with floats beside them.</summary>
    private readonly record struct FloatHost(Box Paragraph, Box Host);

    /// <summary>
    /// Finds the shape this path handles: a multi-column box whose only in-flow child is an undecorated
    /// block that holds a single in-flow block and at least one float. The lines belong to that inner
    /// block, and the float shortens the lines of the column that begins the paragraph only.
    /// </summary>
    private static FloatHost? FloatHostOf(Box box)
    {
        Box? paragraph = null;

        foreach (var child in box.Children)
        {
            if (!child.IsBlockLevel || PositionedLayout.IsOutOfFlow(child))
            {
                continue;
            }

            if (child.Style.Float != FloatSide.None || paragraph is not null)
            {
                return null;
            }

            paragraph = child;
        }

        if (paragraph is not { } block || !BreaksAtLines(block))
        {
            return null;
        }

        Box? host = null;
        var floats = 0;

        foreach (var child in block.Children)
        {
            if (!child.IsBlockLevel || PositionedLayout.IsOutOfFlow(child))
            {
                continue;
            }

            if (IsFloat(child))
            {
                floats++;
                continue;
            }

            if (host is not null || HasBlockChildren(child))
            {
                return null;
            }

            host = child;
        }

        return host is not null && floats > 0 ? new FloatHost(block, host) : null;
    }

    /// <summary>
    /// Gets the band the paragraph's own floats leave for the lines of its inner block: the floats are
    /// placed at the paragraph's content top, which is where the column begins them. <paramref name="reach"/>
    /// is how far below that top the deepest of them reaches, and <paramref name="linesBelowBand"/> says whether
    /// the row the band begins in is left no width at all, which is when every line of the paragraph has to
    /// stand below it rather than beside it (CSS 2.1 §9.5.1-§9.5.2).
    /// </summary>
    private FloatBand? FloatHostBand(
        FloatHost host,
        Double share,
        LayoutContext context,
        Double contentX,
        Double contentY,
        out Double reach,
        out Boolean linesBelowBand)
    {
        var tracks = TracksFor(host.Paragraph, share, contentX);
        reach = 0.0;
        linesBelowBand = false;

        if (tracks is null)
        {
            return null;
        }

        foreach (var child in host.Paragraph.Children)
        {
            if (child.IsBlockLevel && IsFloat(child) && !PositionedLayout.IsOutOfFlow(child))
            {
                PlaceFloat(child, tracks, contentY, share, context);
            }
        }

        reach = Math.Max(0.0, tracks.Bottom - contentY);

        var (freeStart, freeEnd) = tracks.FreeSpanAt(contentY, 0.001);
        linesBelowBand = freeEnd - freeStart <= 0.001;

        var margin = host.Host.Style.Margin.Resolve(share);

        return BandFor(
            host.Host,
            tracks,
            context.Floats,
            contentY,
            contentX + margin.Left,
            margin.Right,
            contentX,
            contentY,
            share);
    }

    /// <summary>One column's share of a broken float paragraph: the plan it is placed from and how
    /// many of that plan's lines it takes. A null <see cref="Start"/> is the paragraph's own first
    /// plan, the one the float's band shortens.</summary>
    private sealed record FloatColumn(InlineStart? Start, IReadOnlyList<Double> Heights, Int32 Count);

    /// <summary>The columns a float paragraph breaks into, and the height they share.</summary>
    private sealed record FloatParagraphPlan(IReadOnlyList<FloatColumn> Columns, Double Extent);

    /// <summary>
    /// Plans a float paragraph across the columns: the first plans at the column's width with the
    /// float's band, and each column after it resumes the text where the plan before it broke and
    /// plans it at the full column width (CSS Multicol 3 §7). A balance picks the least extent all the
    /// text fits in; a fill cuts each column at the height the page stated.
    /// </summary>
    private FloatParagraphPlan? PlanFloatParagraph(
        FloatHost host, Double share, Double? declaredHeight, Int32 columns, LayoutContext context)
    {
        if (columns < 2)
        {
            return null;
        }

        // The paragraph's own edges stand around its lines: the plan is made at what the border and
        // padding leave, and the fragment that owns an edge carries its room (CSS Break 3 §4.7).
        var border = BorderOf(host.Paragraph);
        var padding = host.Paragraph.Style.Padding.Resolve(share);
        var content = Math.Max(0, share - border.Horizontal - padding.Horizontal);
        var margin = host.Paragraph.Style.Margin.Resolve(share);
        var top = margin.Top + border.Top + padding.Top;
        var bottom = border.Bottom + padding.Bottom;

        var childContext = context.ForContainingBlock(new Size(content, declaredHeight ?? 0));
        var band = FloatHostBand(host, content, childContext, 0, 0, out var reach, out var linesBelowBand);
        var bandedContext = childContext.WithFloats(band);
        var banded = new InlineContext(_engine, bandedContext, bandedContext, bandedContext);
        var first = _inline.LineHeights(host.Host, content, host.Host.Flow, banded);

        if (first.Count == 0)
        {
            return null;
        }

        var plainContext = childContext.WithFloats(null);
        var plain = new InlineContext(_engine, plainContext, plainContext, plainContext);
        var orphans = Math.Max(1, host.Paragraph.Style.Breaks.Orphans);
        var widows = Math.Max(1, host.Paragraph.Style.Breaks.Widows);
        var clones = host.Paragraph.Style.ClonesDecoration;
        var whole = new FloatParagraphPlan(
            new[] { new FloatColumn(null, first, first.Count) },
            top + Height(first, first.Count) + bottom);

        if (declaredHeight is { } stated)
        {
            // A fill lets its last column overflow rather than move content past the columns it has.
            var filled = Fill(
                host, content, first, banded, plain, stated, columns, top, bottom, clones, orphans, widows,
                reach, linesBelowBand, mayOverflow: true);

            return filled is null ? whole : filled with { Extent = stated };
        }

        // The least extent the whole text fits in, found by halving the way a line balance does.
        var low = 0.0;
        var high = top + Height(first, first.Count) + bottom;
        FloatParagraphPlan? best = null;

        for (var step = 0; step < 40; step++)
        {
            var middle = (low + high) / 2;
            var plan = Fill(
                host, content, first, banded, plain, middle, columns, top, bottom, clones, orphans, widows,
                reach, linesBelowBand, mayOverflow: false);

            if (plan is null)
            {
                low = middle;
            }
            else
            {
                best = plan;
                high = middle;
            }
        }

        return best ?? whole;
    }

    /// <summary>
    /// Cuts a float paragraph's text into columns of at most <paramref name="extent"/>: each column
    /// plans what is left of the text at its own width, takes the lines that fit with the orphans and
    /// widows the paragraph states, and the next plan resumes from where this one broke. The paragraph's
    /// float begins the first column that has room for the band it needs, and a column that leaves none
    /// for a line hands it on rather than overflowing. Null says the text does not fit in the columns it
    /// was given.
    /// </summary>
    private FloatParagraphPlan? Fill(
        FloatHost host,
        Double width,
        IReadOnlyList<Double> first,
        InlineContext banded,
        InlineContext plain,
        Double extent,
        Int32 columns,
        Double top,
        Double bottom,
        Boolean clones,
        Int32 orphans,
        Int32 widows,
        Double reach,
        Boolean linesBelowBand,
        Boolean mayOverflow)
    {
        var result = new List<FloatColumn>();
        var heights = first;
        var inline = banded;
        InlineStart? start = null;
        var highest = 0.0;

        // A float the room left below the paragraph's own top edge cannot hold begins the column after
        // it, where a whole column is free for it, and the columns before it hold only the edge the
        // paragraph brought (CSS Multicol 3 §7, CSS Fragmentation 3 §4.3, measured:
        // line-after-unbreakable-float-after-padding, whose 75px float with break-inside:avoid stands at
        // the top of the second 101px column while the 100px padding-top fills the first).
        var bandColumn = mayOverflow && reach > 0.001 && reach <= extent + 0.001 && top + reach > extent + 0.001
            ? 1
            : 0;

        for (var column = 0; column < columns; column++)
        {
            // A cloned box owns both edges on every fragment; a sliced one owns the top on its first
            // and the bottom on its last (CSS Break 3 §4.7).
            var starts = clones || column == 0;
            var endsEdge = clones || column == columns - 1;
            var room = extent - (starts ? top : 0) - (endsEdge ? bottom : 0);

            if (column < bandColumn)
            {
                result.Add(new FloatColumn(new InlineStart(host.Host, 0), heights, 0));
                highest = Math.Max(highest, (starts ? top : 0) + (endsEdge ? bottom : 0));
                continue;
            }

            // The band belongs to the one column that holds its float, and a line with no room beside the
            // band stands below it: where the column has no room for that either, the float keeps the
            // column to itself and the text begins the next one at the full width
            // (CSS 2.1 §9.5.2, CSS Multicol 3 §7).
            if (column == bandColumn && linesBelowBand && Height(heights, 1) > room + 0.001)
            {
                result.Add(new FloatColumn(start, heights, 0));
                highest = Math.Max(highest, reach + (starts ? top : 0) + (endsEdge ? bottom : 0));
                heights = _inline.LineHeights(host.Host, width, host.Host.Flow, plain);
                inline = plain;
                start = new InlineStart(host.Host, 0);
                continue;
            }

            var count = FragmentSlicer.Fitting(heights, room, orphans, widows);

            if (count == 0 && column < columns - 1)
            {
                // Nothing of the text fits the room this column has left: it stands in a column after
                // this one, which begins at its own top. The band belongs to the column that holds the
                // float alone, so the text from there on is planned at the column's full width.
                result.Add(new FloatColumn(start, heights, 0));
                highest = Math.Max(
                    highest,
                    (column == bandColumn ? reach : 0.0) + (starts ? top : 0) + (endsEdge ? bottom : 0));

                if (column == bandColumn)
                {
                    heights = _inline.LineHeights(host.Host, width, host.Host.Flow, plain);
                    inline = plain;
                    start = new InlineStart(host.Host, 0);
                }

                continue;
            }

            if (count == 0)
            {
                if (!mayOverflow)
                {
                    return null;
                }

                // The last column has nowhere to hand the text on: it takes it and overflows.
                count = heights.Count;
            }
            else if (mayOverflow && column == columns - 1)
            {
                count = heights.Count;
            }

            // The fragment that ends the paragraph also owns its bottom edge: where the lines alone
            // leave no room for it, the break moves back so that edge still fits.
            if (count >= heights.Count && !endsEdge && bottom > 0.001)
            {
                var ends = FragmentSlicer.Fitting(heights, room - bottom, orphans, widows);

                if (ends <= 0)
                {
                    if (!mayOverflow)
                    {
                        return null;
                    }
                }
                else if (ends < count)
                {
                    count = ends;
                }
            }

            result.Add(new FloatColumn(start, heights, count));
            highest = Math.Max(
                highest,
                Height(heights, count) + (starts ? top : 0) + ((clones || count >= heights.Count) ? bottom : 0));

            if (count >= heights.Count)
            {
                return new FloatParagraphPlan(result, highest);
            }

            var at = _inline.EndOfLine(host.Host, width, host.Host.Flow, inline, count - 1);

            if (at is not { } resume)
            {
                return null;
            }

            start = resume;
            heights = _inline.LineHeights(host.Host, width, host.Host.Flow, plain, resume);
            inline = plain;

            if (heights.Count == 0)
            {
                return new FloatParagraphPlan(result, highest);
            }
        }

        return null;
    }

    /// <summary>
    /// Arranges a float paragraph the way it was measured: each column takes its share of the text
    /// from the plan it resumes, and the paragraph's own first fragment stands below its top margin
    /// with the float beside it (CSS Multicol 3 §7).
    /// </summary>
    private Double ArrangeFloatParagraph(
        Box box,
        FloatHost host,
        FloatParagraphPlan plan,
        LayoutBox parent,
        Rect content,
        Double share,
        Boolean heightStated,
        Double declaredHeight,
        LayoutContext context)
    {
        var multi = box.Style.Multi;
        var border = BorderOf(host.Paragraph);
        var padding = host.Paragraph.Style.Padding.Resolve(share);
        var margin = host.Paragraph.Style.Margin.Resolve(share);
        var last = plan.Columns.Count - 1;
        var wholeHeight = Height(plan.Columns[0].Heights, plan.Columns[0].Heights.Count) + border.Vertical + padding.Vertical;
        var whole = new Rect(content.X, content.Y + margin.Top, share, wholeHeight);

        // The plan's width is what the paragraph's own edges leave; the arrangement reads it back
        // from the fragment's insets, so the two passes break the lines at the same width.
        var childContext = context.ForContainingBlock(
            new Size(Math.Max(0, share - border.Horizontal - padding.Horizontal), heightStated ? declaredHeight : 0));

        for (var index = 0; index < plan.Columns.Count; index++)
        {
            var column = plan.Columns[index];
            var clones = host.Paragraph.Style.ClonesDecoration;
            var starts = clones || index == 0;
            var ends = clones || index == last;
            var height = Height(column.Heights, column.Count)
                + (starts ? border.Top + padding.Top : 0)
                + (ends ? border.Bottom + padding.Bottom : 0);

            // Each fragment draws the edges it owns, and the background is painted across the box the
            // paragraph would have been had it never been broken (CSS Break 3 §4.7).
            var slice = new FragmentSlice(
                new Thickness(starts ? border.Top : 0, border.Right, ends ? border.Bottom : 0, border.Left),
                new Thickness(starts ? padding.Top : 0, padding.Right, ends ? padding.Bottom : 0, padding.Left),
                starts,
                ends,
                whole);

            parent.Children.Add(_engine.Arrange(
                host.Paragraph,
                new Rect(
                    content.X + index * (share + multi.Gap),
                    content.Y + (starts ? margin.Top : 0),
                    share,
                    height),
                childContext,
                false,
                new LineRange(0, column.Count),
                slice,
                column.Start));
        }

        MulticolRules.Add(
            parent,
            box.Style,
            content,
            new List<(Double, Double)> { (content.Y, content.Y + plan.Extent) });

        _engine.PublishFloats(box, null);

        return plan.Extent;
    }

    /// <summary>Gets the height of the first <paramref name="count"/> lines, or of them all.</summary>
    private static Double Height(IReadOnlyList<Double> heights, Int32 count)
    {
        var total = 0.0;

        for (var index = 0; index < count && index < heights.Count; index++)
        {
            total += heights[index];
        }

        return total;
    }

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
    private static Boolean IsFloat(Box child) => child.Style.Float != FloatSide.None;

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

    /// <summary>One column's share of the children: a child that stands whole, or a piece of one the
    /// column divided - where inside that child the piece begins, how much of it the column holds,
    /// and whether it is the last piece.</summary>
    private readonly record struct MulticolPiece(
        Int32 Child, Double Offset, Double Height, Boolean Last, Double Cursor = -1);

    /// <summary>Gets the fewest columns a fill needs when a child may be divided: one that holds
    /// out-of-flow descendants takes as many columns as its height spans, and one more for the room
    /// it may lose to the column it begins in.</summary>
    private static Int32 NeededCells(List<MulticolChild> children, Double boxHeight)
    {
        var needed = 0;

        if (boxHeight <= 0)
        {
            return needed;
        }

        var cut = 0;

        foreach (var child in children)
        {
            if (child.Divides)
            {
                needed = Math.Max(needed, (Int32)Math.Ceiling(child.Fragment.Height / boxHeight - 0.001) + 1);
            }
            else if (child.Offset >= 0)
            {
                // A piece of a box the fragmentainer already cut starts where the flow has reached, so
                // the column it reaches may hold only part of it and the rest goes to the column after.
                cut++;
            }
        }

        return Math.Max(needed, children.Count + cut);
    }

    /// <summary>
    /// Shares the children over the columns, keeping a child that holds out-of-flow descendants in the
    /// column it reached: it is cut where that column runs out of room and what is left carries on at
    /// the top of the next (CSS Position 3 §5.5). Every other child stands or moves whole, exactly as
    /// the plain fill places it.
    /// </summary>
    /// <summary>Gets whether a box brings nothing to any edge of the stack it stands in.</summary>
    private static Boolean NoMargin(Thickness margin) =>
        margin.Top == 0 && margin.Right == 0 && margin.Bottom == 0 && margin.Left == 0;

    /// <summary>Shares the children out one per column, as the fill counted them, without cutting any
    /// of them further: the cells a vertical container of margined children is laid out from.</summary>
    private static List<List<MulticolPiece>> PlainCells(List<MulticolChild> children, IReadOnlyList<Int32> counts)
    {
        var cells = new List<List<MulticolPiece>>(counts.Count);
        var from = 0;

        foreach (var count in counts)
        {
            var cell = new List<MulticolPiece>(count);

            for (var index = 0; index < count; index++)
            {
                cell.Add(new MulticolPiece(from++, -1, 0, true));
            }

            cells.Add(cell);
        }

        return cells;
    }

    private static List<List<MulticolPiece>> PlanMulticolRun(
        List<MulticolChild> children,
        IReadOnlyList<FragmentChild> fragments,
        IReadOnlyList<Double> extents,
        IReadOnlyList<Int32> counts)
    {
        var cells = new List<List<MulticolPiece>>(counts.Count);

        if (!children.Exists(child => child.Divides || child.Offset >= 0 || child.Defers))
        {
            var from = 0;

            foreach (var count in counts)
            {
                var whole = new List<MulticolPiece>(count);

                for (var index = 0; index < count; index++)
                {
                    whole.Add(new MulticolPiece(from++, -1, 0, true));
                }

                cells.Add(whole);
            }

            return cells;
        }

        var placed = 0;
        var resume = -1;
        var resumeAt = 0.0;

        // Where inside the whole box a portion begins: the offset the column divides at, plus the part
        // of the piece the column before it already held. A piece the fragmentainer cut for itself
        // already begins somewhere inside the box, and that distance is added for it.
        static Double StartOf(MulticolChild child, Double within) =>
            child.Offset >= 0 ? child.Offset + within : within;

        // A piece of a box taller than the column is cut through it rather than moved on: the column
        // holds what it has room for and the rest carries on at the top of the next (CSS Fragmentation
        // 3 §4.3). The box that may be cut is one the columns already divided, or a piece of one the
        // fragmentainer cut for itself.
        static Boolean Cuts(MulticolChild child) => child.Divides || child.Offset >= 0;

        // A piece paints as far as the box lets stand - which is past the box's own height where a
        // descendant hangs below a stated height - while the flow advances only by the part of the
        // box's own height the piece holds (CSS Fragmentation 3 §4.3).
        static Double Painted(MulticolChild child) => Math.Max(child.Reach, child.Fragment.Height);

        // How much of the box itself the column holds: where inside the box is counted from its border
        // box, while a piece's outer height carries the margin that opened the box in the column where it
        // began, so that margin comes back out - otherwise a box that lets its content overflow is
        // advanced past its own end and the next column is handed a piece of a box that has none left
        // (measured: transform-006, whose box of 175 with a 50 margin comes out 50 / 100 / 25 / 0 across
        // four columns of 100). A window of a flex container keeps its own accounting: each window is a
        // container of its own, margin and all.
        static Double Held(MulticolChild child, Double within, Double take) =>
            Math.Min(
                take,
                Math.Max(0, child.Fragment.Height - (child.Relays ? 0 : child.Margin.Top) - within));

        // A column that runs out of room inside a box cuts it where one of the box's own units ends, and
        // the pieces keep the number of LINE boxes the page asks for on each side of the break: orphans
        // and widows count lines, not the whole children a box is stacked from, so a piece holding no
        // line of the run at all is legal - the run simply begins in the fragment after it
        // (CSS Break 3 §4.2-§4.3, measured: widows-orphans-021, whose orphans:3 moves all three lines
        // and the float among them to the second column while the 25px box before them stays in the first).
        // Where the room holds no such place, the cut stays where the room ends.
        static Double CutAt(MulticolChild child, Double room, Double height)
        {
            var units = child.Breaks;

            if (units is null || units.Count < 2 || room >= height - 0.001)
            {
                return Math.Min(room, height);
            }

            var orphans = Math.Max(1, child.Box.Style.Breaks.Orphans);
            var widows = Math.Max(1, child.Box.Style.Breaks.Widows);
            var totalLines = units.Count(unit => unit.IsLine);
            var best = -1.0;
            var headLines = 0;

            for (var index = 0; index < units.Count - 1; index++)
            {
                var unit = units[index];

                if (unit.IsLine)
                {
                    headLines++;
                }

                var tailLines = totalLines - headLines;

                if (unit.Offset <= 0.001 || unit.Offset > room + 0.001 ||
                    (headLines > 0 && headLines < orphans) || (tailLines > 0 && tailLines < widows))
                {
                    continue;
                }

                best = unit.Offset;
            }

            return best > 0 ? best : Math.Min(room, height);
        }

        // A float that spans the whole column, and that the column the flow reached has no room left for,
        // is set aside for the head of the next column - and the flow goes on filling the column it was
        // written in with what came after it. This is the one arrangement a float makes that a linear
        // share of the children cannot say: the float jumps the queue while what was written after it
        // steps up (CSS 2.1 9.5.1 with CSS Fragmentation 3 4.1, measured: wpt/css-break/float-017, whose
        // 200-tall full-width float stands at the head of the second column of four while the ten lines
        // written around it fill the first and the third).
        var opens = -1;
        var opensRoom = 0.0;

        for (var number = 0; number < extents.Count; number++)
        {
            var limit = extents[number];
            var used = 0.0;
            var cell = new List<MulticolPiece>();

            if (opens >= 0)
            {
                // The float opens the column it was pushed into, and what it stands for is room the
                // column no longer offers the flow: a float taller than the column leaves it wholly to
                // itself, which is where the flow after it begins the column beyond.
                cell.Add(new MulticolPiece(opens, -1, 0, true));
                used = Math.Min(opensRoom, limit);
                opens = -1;

                if (used >= limit - 0.001)
                {
                    // The column belongs to the float alone: nothing else may begin in it, not even a
                    // piece the slicer would force through for the flow to make progress - that piece
                    // would start the box over from its own top rather than carry on where the column
                    // before left it.
                    cells.Add(cell);
                    continue;
                }
            }

            // A column given wholly to the float that opens it leaves no room for the piece that
            // was cut in the column before: that piece waits for the next head instead.
            if (resume >= 0 && limit - used > 0.001)
            {
                var total = Painted(children[resume]);
                var take = Math.Min(total - resumeAt, limit - used);
                var last = resumeAt + take >= total - 0.001;
                var held = Held(children[resume], resumeAt, take);

                cell.Add(new MulticolPiece(
                    resume, StartOf(children[resume], resumeAt), take, last, held));
                used = held;

                if (last)
                {
                    placed = Math.Max(placed, resume + 1);
                    resume = -1;
                }
                else
                {
                    resumeAt += take;
                    cells.Add(cell);
                    continue;
                }
            }

            while (placed < children.Count)
            {
                var cut = ChildSlicer.Fitting(fragments, limit - used, placed);
                var fits = cut.Fits;

                if (fits == 0 && number == extents.Count - 1)
                {
                    fits = 1;
                }

                for (var index = 0; index < fits; index++)
                {
                    // The float that the column the flow reached can no longer hold is set aside for
                    // the next head: it spends none of this column's room either way, so the pieces the
                    // slicer counted on after it still stand here. A column that has held nothing yet is
                    // the float's own place, and a piece mid-cut would have to share the head with it,
                    // so neither case defers.
                    if (children[placed].Defers && resume < 0 && used > 0.001 && opens < 0 &&
                        number < extents.Count - 1 && limit - used < children[placed].Whole - 0.001)
                    {
                        opens = placed;
                        opensRoom = children[placed].Whole;
                        placed++;
                        continue;
                    }

                    used += fragments[placed].Height;
                    cell.Add(new MulticolPiece(placed++, -1, 0, true));
                }

                // The slicer takes the first child whatever its height, so that the flow makes
                // progress; a child the columns may divide is cut at the room instead of overflowing.
                if (!cut.Forced && number < extents.Count - 1 && placed > 0 &&
                    children[placed - 1].Fragment.Inside != BreakValue.Avoid &&
                    Cuts(children[placed - 1]) && used > limit + 0.001)
                {
                    var height = fragments[placed - 1].Height;

                    // The room this column gives the box is what is left of it once the box's own
                    // block-start margin stands in: the margin sits inside the column, so the piece the
                    // column holds is shorter than the column by that much (CSS Break 3 §4.3, CSS 2.1
                    // §8.3.1 - the margin is spent where the box begins, not on every piece of it).
                    var room = Math.Max(0, limit - (used - height) - children[placed - 1].Margin.Top);

                    cell.RemoveAt(cell.Count - 1);
                    placed--;

                    if (room > 0.001)
                    {
                        // What the column shows is its own room - the piece of a box whose content ends
                        // earlier still fills the column, which is where its background stops (CSS
                        // Fragmentation 3 §4.3) - while the box carries on from the unit the break fell
                        // after (CSS Break 3 §4.2).
                        var painted = Painted(children[placed]);
                        var content = CutAt(children[placed], room, painted);
                        var take = Math.Min(room, painted);
                        var last = content >= painted - 0.001;

                        cell.Add(new MulticolPiece(
                            placed,
                            StartOf(children[placed], 0),
                            take,
                            last,
                            Held(children[placed], 0, take)));

                        if (last)
                        {
                            placed++;
                        }
                        else
                        {
                            resume = placed;
                            resumeAt = content;
                        }
                    }
                    else
                    {
                        resume = placed;
                        resumeAt = 0;
                    }

                    break;
                }

                if (placed >= children.Count)
                {
                    break;
                }

                // A child that holds out-of-flow descendants, or a piece of one the fragmentainer cut,
                // does not move to the next column: the room this one has left is its first piece, and
                // the rest follows at the top of the next column (CSS Position 3 §5.5, CSS Fragmentation
                // 3 §4.3). A piece that begins with a border or padding of its own needs that much room:
                // a column with less leaves it for the next rather than start it mid-edge (measured in
                // Chrome: a ten-pixel border is pushed on by five pixels and kept by ten). A box that
                // asks not to be broken inside keeps that wish until a column of its own cannot hold it,
                // so it moves on whole rather than be started in what is left (CSS Break 3 §3.1).
                var gate = children[placed].Fragment.Inside == BreakValue.Avoid
                    ? fragments[placed].Height
                    : children[placed].StartInset;

                if (!cut.Forced && Cuts(children[placed]) &&
                    limit - used >= gate - 0.001 && limit - used > 0.001)
                {
                    var room = limit - used;
                    var painted = Painted(children[placed]);
                    var content = CutAt(children[placed], room, painted);
                    var take = Math.Min(room, painted);
                    var last = content >= painted - 0.001;

                    cell.Add(new MulticolPiece(
                        placed,
                        StartOf(children[placed], 0),
                        take,
                        last,
                        Held(children[placed], 0, take)));

                    if (last)
                    {
                        placed++;
                    }
                    else
                    {
                        resume = placed;
                        resumeAt = content;
                    }

                    break;
                }

                // The plain fill asks the slicer once per fragment; asking it again here would let the
                // progress rule drag a child that did not fit into a column that has no room left.
                break;
            }

            cells.Add(cell);

            // The flow is spent, but a float set aside for the next head still owes it that column.
            if (placed >= children.Count && resume < 0 && opens < 0)
            {
                break;
            }
        }

        if (opens >= 0)
        {
            // No extent was left to take the float the flow could not hold: give it a column of
            // its own rather than lose the box the page wrote.
            cells.Add(new List<MulticolPiece> { new MulticolPiece(opens, -1, 0, true) });
        }

        return cells;
    }

    /// <summary>Gets how far the floats standing in a piece reach below the piece's own bottom edge.
    /// A float is no part of its parent's height, yet the column that carries the flow that placed it
    /// shows it (CSS 2.1 §9.5, CSS Fragmentation 3 §4.1).</summary>
    private static Double HungBelow(LayoutBox piece)
    {
        var hang = 0.0;

        foreach (var child in piece.Children)
        {
            if (child.Source is { } source &&
                IsFloat(source) &&
                !PositionedLayout.IsOutOfFlow(source))
            {
                hang = Math.Max(hang, child.BorderBox.Bottom - piece.BorderBox.Bottom);
            }
        }

        return Math.Max(0.0, hang);
    }

    private void ArrangeMulticolRun(
        List<MulticolChild> children,
        List<List<MulticolPiece>> cells,
        LayoutBox parent,
        Double originX,
        Double originY,
        Int32 columns,
        Double share,
        Double gap,
        Double boxHeight,
        Double rowGap,
        LayoutContext childContext,
        Dictionary<Box, Double> startsAt)
    {
        for (var cell = 0; cell < cells.Count; cell++)
        {
            var column = cell % columns;
            var row = cell / columns;
            var x = originX + column * (share + gap);
            var y = originY + row * (boxHeight + rowGap);

            // Where this column box ends: what a piece of a divided box may still show is clipped
            // here rather than at the box's own bottom edge.
            var columnBottom = y + boxHeight;
            var pieces = cells[cell];

            for (var index = 0; index < pieces.Count; index++)
            {
                var portion = pieces[index];
                var child = children[portion.Child];

                // A column box begins a fresh stack: what a child brings to its top edge is added there
                // rather than folded into the margin run the box before ended with - but only in the
                // column where the child begins. A piece that carries on from the column before stands at
                // the head of its column with nothing of its margin left to pay, because the room the
                // first column gave it was already cut by that margin (CSS Fragmentation 3 §4.3,
                // measured: transform-006, whose reference page starts every one of its four fragments at
                // the top of its column although the box carries margin-top:50). Only a box that lets its
                // content reach past itself is paid this way: the pieces of a box that stands no taller
                // than what it holds keep the margin their column gives them, which is what the windows of
                // a painted-over flex container do (gecko/flexbox/pagination/flexbox-single-column-1a
                // through -1f and -multi-column-1a through -1f stay as they were; -2 and -6, whose
                // containers do reach past themselves, move +2200 and +500 and are still red).
                y += portion.Offset > 0.001 && child.Reach > child.Fragment.Height + 0.001
                    ? 0
                    : child.Margin.Top;

                var width = child.Size.Width;
                var height = child.Size.Height;

                // A float the column holds stands at the column's own edge, where the flow had reached,
                // and the flow goes on from that place: the float spends none of the column's room, and
                // its margin does not join the run the boxes of the flow fold into one another
                // (CSS 2.1 §9.5.1, §8.3.1, measured: wpt/css-break/float-017, whose 100%-wide, 200-tall
                // size-contained float leaves the ten lines written around it to fill the columns in order).
                if (child.Floats)
                {
                    var edge = child.Box.Style.Float == FloatSide.Right
                        ? x + share - width - child.Margin.Right
                        : x + child.Margin.Left;

                    parent.Children.Add(_engine.Arrange(
                        child.Box,
                        new Rect(edge, y, width, height),
                        childContext,
                        definiteHeight: false));

                    y -= child.Margin.Top;
                    continue;
                }

                // A window of a wrapping row flex is laid out as a flex container of the window's own
                // size: its lines keep the distances the whole container laid them at, the first of them
                // at the window's top, and the piece's own edge stands outside the content it holds
                // (CSS Flexbox 1 §6, measured: multi-line-row-flex-fragmentation-065/094).
                if (child.Relays && child.Offset >= 0)
                {
                    var windowBorder = BorderOf(child.Box);
                    var windowPadding = child.Box.Style.Padding.Resolve(share);
                    var windowStarts = child.Offset < 0.001;
                    var windowEdge = (windowStarts ? windowBorder.Top + windowPadding.Top : 0.0) +
                        (child.Last ? windowBorder.Bottom + windowPadding.Bottom : 0.0);
                    var windowHeight = portion.Height > 0.001 ? portion.Height : child.Fragment.Height;
                    var windowWhole = new Rect(
                        x + child.Margin.Left,
                        y - child.Offset - (windowBorder.Top + windowPadding.Top),
                        width,
                        child.Whole > 0 ? child.Whole : height);

                    var windowRect = new Rect(x + child.Margin.Left, y, width, windowHeight + windowEdge);
                    var windowSlice = new FragmentSlice(
                        new Thickness(
                            windowStarts ? windowBorder.Top : 0,
                            windowBorder.Right,
                            child.Last ? windowBorder.Bottom : 0,
                            windowBorder.Left),
                        new Thickness(
                            windowStarts ? windowPadding.Top : 0,
                            windowPadding.Right,
                            child.Last ? windowPadding.Bottom : 0,
                            windowPadding.Left),
                        windowStarts,
                        child.Last,
                        windowWhole);
                    var windowed = _engine.ArrangeFlexFragment(
                        child.Box,
                        windowRect,
                        childContext,
                        windowWhole,
                        child.Offset,
                        windowHeight,
                        windowSlice,
                        child.Item);

                    // A window is one fragmentainer's: a line a break cut through stands above the next
                    // window and is not drawn here, so what the window holds is clipped to its own box
                    // (measured: multi-line-row-flex-fragmentation-096, whose straddling line showed
                    // 50 pixels in a 60-pixel column before this).
                    windowed.FragmentClip = windowRect;
                    windowed.FragmentCutsInline = false;
                    parent.Children.Add(windowed);

                    y += windowHeight + windowEdge + (child.Last ? child.Margin.Bottom : 0);
                    continue;
                }

                // A piece of a child the column divided: the box is laid out over the whole of
                // itself, where its content and its out-of-flow descendants stand, and the column
                // shows only the stretch it holds (CSS Position 3 §5.5).
                if (portion.Offset >= 0)
                {
                    // A piece of a flex container is a flex container of the piece's own size: its
                    // items are found again in the piece's window and distributed anew within it, and
                    // only the edges the piece really has are drawn (CSS Flexbox 1 §6).
                    if (child.Relays)
                    {
                        var flexBorder = BorderOf(child.Box);
                        var flexPadding = child.Box.Style.Padding.Resolve(share);
                        var flexStarts = portion.Offset < 0.001;
                        var flexWhole = new Rect(
                            x + child.Margin.Left, y - portion.Offset, width, child.Size.Height);

                        // The piece's own edges stand outside the content it holds: the top one on the
                        // piece that starts the container, the bottom one on the piece that ends it.
                        var flexEdge = (flexStarts ? flexBorder.Top + flexPadding.Top : 0.0) +
                            (portion.Last ? flexBorder.Bottom + flexPadding.Bottom : 0.0);

                        parent.Children.Add(_engine.ArrangeFlexFragment(
                            child.Box,
                            new Rect(x + child.Margin.Left, y, width, portion.Height + flexEdge),
                            childContext,
                            flexWhole,
                            portion.Offset,
                            portion.Height,
                            new FragmentSlice(
                                new Thickness(
                                    flexStarts ? flexBorder.Top : 0,
                                    flexBorder.Right,
                                    portion.Last ? flexBorder.Bottom : 0,
                                    flexBorder.Left),
                                new Thickness(
                                    flexStarts ? flexPadding.Top : 0,
                                    flexPadding.Right,
                                    portion.Last ? flexPadding.Bottom : 0,
                                    flexPadding.Left),
                                flexStarts,
                                portion.Last,
                                flexWhole,
                                child.Item)));

                        y += portion.Height + flexEdge + (portion.Last ? child.Margin.Bottom : 0);
                        continue;
                    }

                    var wholeHeight = child.Whole > 0 ? child.Whole : height;
                    var whole = new Rect(x + child.Margin.Left, y - portion.Offset, width, wholeHeight);
                    var held = new Rect(x + child.Margin.Left, y, width, portion.Height);

                    // The fragment's own box, which is what its transform turns about: the column shows
                    // through the window it has, but the box ends where its own part of the stack ends,
                    // and a child that overflows past it rides on that fragment's transform (CSS Break 3
                    // §4.7 against CSS Transforms 1 §transform-origin, measured: transform-006, whose
                    // reference page stands four boxes of 50, 100, 25 and 0 with children of 50, 100,
                    // 100 and 50 inside them - the last two fragments being shorter than the column
                    // they stand in and still carrying the lime that reaches past them).
                    var fragment = new Rect(
                        x + child.Margin.Left,
                        y,
                        width,
                        child.Relays
                            ? portion.Height
                            : Math.Min(
                                portion.Height,
                                Math.Max(
                                    0,
                                    (child.Whole > 0 ? child.Whole : height) - portion.Offset)));
                    var cutBorder = BorderOf(child.Box);
                    var cutPadding = child.Box.Style.Padding.Resolve(share);
                    var cutStarts = portion.Offset < 0.001 && child.OwnsEdges;
                    var cutEnds = portion.Last && child.Last && child.OwnsEdges;
                    var arranged = _engine.Arrange(
                        child.Box,
                        whole,
                        childContext,
                        definiteHeight: false,
                        slice: new FragmentSlice(
                            new Thickness(
                                cutStarts ? cutBorder.Top : 0,
                                cutBorder.Right,
                                cutEnds ? cutBorder.Bottom : 0,
                                cutBorder.Left),
                            new Thickness(
                                cutStarts ? cutPadding.Top : 0,
                                cutPadding.Right,
                                cutEnds ? cutPadding.Bottom : 0,
                                cutPadding.Left),
                            cutStarts,
                            cutEnds,
                            whole,
                            Piece: fragment,
                            AnchorAtPiece: portion.Offset > 0.001));


                    // The last piece of a box the column cut by its own content units is clipped to the
                    // column, not to the box's bottom edge: a float that piece's flow placed stands
                    // below that edge and the column still shows it, while an earlier piece's float is
                    // cut at the edge and carried on by the column after it (CSS 2.1 §9.5 against CSS
                    // Fragmentation 3 §4.1, §4.3; measured: widows-orphans-022).
                    var floatHang = portion.Last && child.Last && child.Breaks is not null
                        ? HungBelow(arranged)
                        : 0.0;

                    if (floatHang > 0.001)
                    {
                        var reach = held.Bottom + floatHang;

                        // The column box is the cap only where it really ends below the piece: a
                        // piece that overflows its column keeps the clip it had.
                        arranged.FragmentClip = new Rect(
                            held.X,
                            held.Y,
                            held.Width,
                            Math.Max(
                                0.0,
                                (columnBottom > held.Bottom ? Math.Min(reach, columnBottom) : reach) - held.Y));
                        arranged.FragmentCutsInline = false;
                    }
                    else
                    {
                        arranged.FragmentClip = held;
                        arranged.FragmentCutsInline = false;
                    }
                    parent.Children.Add(arranged);
                    y += (portion.Cursor >= 0 ? portion.Cursor : portion.Height) +
                        (portion.Last ? child.Margin.Bottom : 0);
                    continue;
                }

                // The lines of one broken child that fall in the same column are one fragment of it:
                // they stand in the box drawn once for them, and only the fragment's own first and last
                // line carry the child's edges.
                var to = child.Broken && child.Offset < 0 ? child.Line + 1 : -1;
                var last = child;

                while (child.Broken && child.Offset < 0 && index + 1 < pieces.Count &&
                    children[pieces[index + 1].Child].Broken && children[pieces[index + 1].Child].Offset < 0 &&
                    ReferenceEquals(children[pieces[index + 1].Child].Box, child.Box))
                {
                    var next = children[pieces[++index].Child];

                    // Every piece of the run but the first stands where the one before it ended, and only
                    // the last of them brings the margin the child ends with: a column that forgot both
                    // came up one line's margin short for every child it broke.
                    height += next.Size.Height + next.Margin.Top;
                    last = next;
                    to = next.Line + 1;
                }

                // The room this piece stands in, from the top of the column's flow to the bottom of what
                // the column gave it - which for a table piece includes the strips its repeated sections
                // are copied into, since those copies are clipped by the piece as well (CSS Tables 3
                // §6.1, CSS Break 3 §4.3). A piece that shows only its own band of the box - one item
                // of a row flex - begins where that band begins, because the strip above it belongs to
                // the item before it, in the fragmentainer before this one (CSS Flexbox 1 §6).
                var rect = new Rect(
                    x + child.Margin.Left,
                    y,
                    width,
                    height + child.Lead + child.Trail);
                FragmentSlice? slice = null;

                // A piece cut through the box itself owns the edges that fall inside its slice, and its
                // background is painted across the box it came from - the box is not measured again at
                // the smaller height, only cut (CSS Break 3 §4.7).
                if (child.Offset >= 0)
                {
                    var cutBorder = BorderOf(child.Box);
                    var cutPadding = child.Box.Style.Padding.Resolve(share);
                    var cutClones = child.Box.Style.ClonesDecoration;
                    var cutStarts = cutClones || child.Offset < 0.001;
                    var cutEnds = cutClones || child.Last;

                    // A piece of a box that holds content is laid out over the whole box, which is
                    // where its content stands, and drawn only where the fragment holds it. A box that
                    // clones its decoration stands as a box of its own in each column instead, so its
                    // piece is its whole (CSS Break 3 §4.7).
                    //
                    // A repeated table section stands between the column's top and this piece's own
                    // content, so the box begins that much lower while the slice says where the copied
                    // groups begin and end: at the column's top, and at the bottom of the room the
                    // column gave the piece (CSS Tables 3 §6.1).
                    var holds = child.Box.Children.Count > 0 && !cutClones;
                    var standing = y + child.Lead;
                    var whole = cutClones ? rect : new Rect(rect.X, standing - child.Offset, rect.Width, child.Whole);
                    var piece = _engine.Arrange(
                        child.Box,
                        holds ? whole : rect,
                        childContext,
                        definiteHeight: false,
                        slice: new FragmentSlice(
                            new Thickness(
                                cutStarts ? cutBorder.Top : 0,
                                cutBorder.Right,
                                cutEnds ? cutBorder.Bottom : 0,
                                cutBorder.Left),
                            new Thickness(
                                cutStarts ? cutPadding.Top : 0,
                                cutPadding.Right,
                                cutEnds ? cutPadding.Bottom : 0,
                                cutPadding.Left),
                            cutStarts,
                            cutEnds,
                            whole,
                            child.Offset - child.Lead,
                            child.Lead + height + child.Trail,
                            Piece: rect));

                    if (holds)
                    {
                        // The piece's own box ends where its in-flow content does, but a float this
                        // piece places stands below that edge and the column still shows it: the clip
                        // ends where the column box does, not where the box's height does
                        // (CSS Fragmentation 3 §4.1, measured: widows-orphans-022).
                        piece.FragmentClip = rect;
                        piece.FragmentCutsInline = false;
                    }

                    parent.Children.Add(piece);

                    // The column holds the piece between the two copied groups it gives room to.
                    y += child.Lead + height + child.Trail + child.Margin.Bottom;
                    continue;
                }

                if (child.Broken)
                {
                    // The piece owns the box's edges only where they really are: its first column keeps the
                    // top border and padding, its last the bottom ones, and everything its background draws
                    // is measured against the box it would have been had it never been broken.
                    var border = BorderOf(child.Box);
                    var padding = child.Box.Style.Padding.Resolve(share);
                    var clones = child.Box.Style.ClonesDecoration;
                    var starts = clones || child.Line == 0;
                    var ends = clones || last.Last;

                    if (starts)
                    {
                        startsAt[child.Box] = y;
                    }

                    var wholeTop = startsAt.TryGetValue(child.Box, out var started) ? started : y;

                    var drawn = new Thickness(
                        starts ? border.Top : 0,
                        border.Right,
                        ends ? border.Bottom : 0,
                        border.Left);
                    var room = new Thickness(
                        starts ? padding.Top : 0,
                        padding.Right,
                        ends ? padding.Bottom : 0,
                        padding.Left);

                    height += drawn.Top + drawn.Bottom + room.Top + room.Bottom;
                    rect = new Rect(x + child.Margin.Left, y, width, height);

                    slice = new FragmentSlice(
                        drawn,
                        room,
                        starts,
                        ends,
                        new Rect(rect.X, wholeTop, rect.Width, child.Whole));
                }

                var stood = _engine.Arrange(
                    child.Box,
                    rect,
                    childContext,
                    definiteHeight: false,
                    lines: child.Broken ? new LineRange(child.Line, to) : null,
                    slice: slice);

                if (!child.OwnsEdges)
                {
                    // The piece spent the margin a box opened with and shows none of the box: what the
                    // box holds is drawn by the piece that carries it.
                    stood.FragmentClip = stood.BorderBox;
                    stood.FragmentCutsInline = false;
                }

                parent.Children.Add(stood);
                y += height + last.Margin.Bottom;
            }
        }
    }

    /// <summary>
    /// Arranges a multi-column box whose children a spanner splits into runs: each run fills its own row
    /// of columns, and the spanner stands between them at full width (CSS Multicol 3 §3.2). The rows are
    /// stacked in the container's block axis, and the column rules stop at each run's edges.
    /// </summary>
    private Double ArrangeMulticolSections(
        Box box,
        List<MulticolSection> sections,
        LayoutBox parent,
        Rect content,
        Int32 columns,
        Double share,
        Boolean heightStated,
        Double declaredHeight,
        LayoutContext context)
    {
        var multi = box.Style.Multi;
        var childContext = context.ForContainingBlock(new Size(share, heightStated ? declaredHeight : 0));
        var startsAt = new Dictionary<Box, Double>();
        var runs = new List<(Double From, Double To)>();
        var y = content.Y;

        foreach (var section in sections)
        {
            if (section.Spanner is { } spanner)
            {
                var spanned = MeasureSpanner(spanner, box, content.Width, heightStated ? declaredHeight : 0, context);
                var spanContext = context.ForContainingBlock(new Size(content.Width, heightStated ? declaredHeight : 0));

                y += spanned.Margin.Top;
                parent.Children.Add(_engine.Arrange(
                    spanner,
                    new Rect(content.X + spanned.Margin.Left, y, spanned.Size.Width, spanned.Size.Height),
                    spanContext,
                    definiteHeight: false));
                y += spanned.Size.Height + spanned.Margin.Bottom;
                continue;
            }

            var children = MulticolChildren(
                box,
                share,
                heightStated ? declaredHeight : 0,
                FillsTo(box, parent, heightStated, declaredHeight),
                columns,
                context,
                EffectiveMembers(section.Members, share));
            var extent = MulticolExtent(multi, columns, heightStated ? declaredHeight : null, children);
            var fragments = new List<FragmentChild>(children.Count);

            foreach (var child in children)
            {
                fragments.Add(child.Fragment);
            }

            // A fill is not held to the column count, as above; a balance has already been solved into
            // the columns it was given.
            var extents = new Double[Math.Max(1, multi.Fills ? children.Count : columns)];
            Array.Fill(extents, extent);

            var sectionCounts = Fragmentainer.Fill(fragments, extents);

            ArrangeMulticolRun(
                children,
                PlanMulticolRun(children, fragments, extents, sectionCounts),
                parent,
                content.X,
                y,
                columns,
                share,
                multi.Gap,
                extent,
                multi.RowGap,
                childContext,
                startsAt);

            runs.Add((y, y + extent));
            y += extent;
        }

        _engine.PublishFloats(box, null);
        MulticolRules.Add(parent, box.Style, content, runs);

        return y - content.Y;
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
        var pendingGroup = MarginGroup.Zero;
        var throughStart = CollapsesThroughStart(box, flow, contentInline);
        var constraints = MeasureConstraints.FitWidth(basisBlock);
        var outOfFlow = new List<(Box Box, Offset Static)>();
        var startEscapes = throughStart;
        var floats = new ColumnFloats(flow, contentInline);

        foreach (var child in box.Children)
        {
            if (!child.IsBlockLevel)
            {
                continue;
            }

            if (IsFloat(child) && !PositionedLayout.IsOutOfFlow(child))
            {
                // The same placement the measuring pass worked out, run again at the place the column
                // stands: a float of a column is pinned to the block-start edge and to the side of the
                // line it names, and takes no step along the stack (CSS 2.1 §9.5).
                var floating = _engine.Measure(child, constraints, context);
                var (placed, _) = floats.Place(child, floating, cursor + pending);
                parent.Children.Add(_engine.Arrange(
                    child,
                    flow.Map(content, placed.X, placed.Width, placed.Y, placed.Height),
                    context));
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
            else if (!PositionedLayout.IsOutOfFlow(child))
            {
                // Same as in a row: the box that left the flow keeps its margins out of the run.
                pendingGroup = pendingGroup.With(childStart);
                pending = pendingGroup.Value;
            }

            if (PositionedLayout.IsOutOfFlow(child))
            {
                // Same as in a row: the box takes no space, but the place it would have had is kept as
                // the static position an unset edge falls back to - the outer edge the run reached, with
                // none of the box's own margins in it, for the two reasons the arranging walk gives.
                var stood = flow.Map(content, 0, 0, cursor + pending, 0);
                outOfFlow.Add((child, new Offset(stood.X, stood.Y)));
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
            pendingGroup = new MarginGroup(childEnd);
        }

        foreach (var (child, staticPosition) in outOfFlow)
        {
            // The place the run reached is the column's block-start edge, which `vertical-rl` puts on
            // the right: an unset edge falls back to that corner rather than to the left one, so the
            // caller says which corner it handed (CSS 2.1 §10.3.7, CSS Writing Modes 3 §5.1, measured:
            // box-shadow-003, whose abs rings stand inside the column that holds them).
            parent.Children.Add(_engine.ArrangePositioned(
                child, staticPosition, context, staticFromHigh: flow.IsVertical && flow.BlockSign < 0));
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
            FlexAlignment.End or FlexAlignment.FlexEnd or FlexAlignment.Right => free,
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
    /// Gets whether the box holds anything that can make a line or a box of its own. A run of
    /// whitespace makes neither - the markup between two tags is not content - so a box carrying
    /// only that is empty as far as breaking is concerned (measured: a stated-height box written
    /// across two lines was left whole by the columns, while the same box on one line was cut).
    /// </summary>
    private static Boolean HoldsContent(Box box)
    {
        foreach (var child in box.Children)
        {
            if (child.Kind != BoxKind.Text || !String.IsNullOrWhiteSpace(child.Text))
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
    /// positive value plus the smallest negative one.
    /// </summary>
    /// <remarks>
    /// Two values fold the same way however they are paired, but a run of three or more does not: the
    /// greatest positive and the smallest negative of the whole run decide it, and folding the run's
    /// values two at a time loses one of them - "80, -40, 20, -10" is 40 as a group, while
    /// <c>Combine(Combine(Combine(80, -40), 20), -10)</c> is 30. A run that is being gathered therefore
    /// keeps <see cref="MarginGroup"/>'s two ends rather than its value.
    /// </remarks>
    private static Double Combine(Double left, Double right) =>
        Math.Max(Math.Max(left, right), 0) + Math.Min(Math.Min(left, right), 0);

    /// <summary>
    /// A run of margins being gathered: the greatest positive and the smallest negative it holds, which
    /// together are the one margin the run collapses to. The two ends are kept apart because adding a
    /// margin to the value already folded would lose whichever end the new margin does not agree with.
    /// </summary>
    private readonly struct MarginGroup
    {
        internal static readonly MarginGroup Zero = new(0, 0);

        private MarginGroup(Double positive, Double negative)
        {
            Positive = positive;
            Negative = negative;
        }

        internal MarginGroup(Double margin) : this(Math.Max(margin, 0), Math.Min(margin, 0))
        {
        }

        internal Double Positive { get; }

        internal Double Negative { get; }

        internal Double Value => Positive + Negative;

        internal MarginGroup With(Double margin) =>
            new(Math.Max(Positive, margin), Math.Min(Negative, margin));

        internal MarginGroup With(MarginGroup other) =>
            new(Math.Max(Positive, other.Positive), Math.Min(Negative, other.Negative));

        public static implicit operator Double(MarginGroup group) => group.Value;
    }

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

        // A multi-column container holds its own formatting context (CSS Multicol 3 §3.1): its
        // columns' floats stay in it, and a child's margins do not collapse through it.
        box.Style.Multi.IsMultiColumn ||
        IsFormattingRoot(box) ||
        box.Kind is BoxKind.InlineBlock or BoxKind.HorizontalRule or BoxKind.Flex or BoxKind.Grid or
                    BoxKind.Table or BoxKind.TableRowGroup or BoxKind.TableRow or BoxKind.TableCell or
                    BoxKind.TableCaption;

    /// <summary>
    /// Gets whether the box holds a block formatting context of its own - what a float beside it may
    /// not overlap, and what its children's margins do not collapse through. Clipping says so,
    /// <c>flow-root</c> says so, and so does containment of layout or paint: "the containment box
    /// establishes an independent formatting context" (CSS Contain 2 §3.2, §3.3).
    /// </summary>
    private static Boolean IsFormattingRoot(Box box) =>
        box.Style.ClipsOverflow ||
        box.Style.Display.Contains("flow-root", StringComparison.OrdinalIgnoreCase) ||
        box.Style.ContainmentApplies && (box.Style.Contain.Layout || box.Style.Contain.Paint);

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
    /// Gets the least block extent the box states for itself: a <c>min-height</c> (or a column's
    /// <c>min-width</c>) that holds the box open even when its content is nothing, which keeps its
    /// two margins from adjoining across it (CSS 2.1 §8.3.1).
    /// </summary>
    private static Double MinBlockExtent(Box box, WritingModeInfo flow)
    {
        var length = flow.IsVertical ? box.Style.MinWidth : box.Style.MinHeight;

        return length.HasValue ? length.Resolve(0) : 0;
    }

    /// <summary>
    /// Gets the block extent an <c>aspect-ratio</c> gives the box on its own, which is a height where the
    /// text runs down and a width where it runs across (CSS Sizing 4 §4).
    /// </summary>
    private static Double? RatioBlockExtent(Box box, WritingModeInfo flow, Double basis)
    {
        var style = box.Style;
        var border = BorderOf(box);
        var padding = style.Padding.Resolve(basis);
        var insets = flow.IsVertical
            ? padding.Horizontal + border.Horizontal
            : padding.Vertical + border.Vertical;

        return flow.IsVertical
            ? BoxModel.WidthFromRatio(style, basis, insets)
            : BoxModel.HeightFromRatio(style, Math.Max(0, basis - insets), null, insets);
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

        if (DeclaredBlockExtent(box, flow, basis) is > 0 || MinBlockExtent(box, flow) > 0)
        {
            return false;
        }

        // Size containment gives the box a block extent of its own even with `height: auto`: the box
        // comes to the intrinsic size the page stated for it rather than to nothing, so its margins do
        // not adjoin across it (CSS Contain 1 §3.1.1, CSS Sizing 4 §5).
        if (ContainsBlockSize(box) &&
            box.Style.Contain.Height is { } contained &&
            contained.Resolve(0) > 0)
        {
            return false;
        }

        // A block extent the ratio states is an extent of its own as much as a declared one is: the box
        // has height whatever its content leaves it with, and the margins above and below do not adjoin
        // across it. Read as nothing, a square chart's body - which holds its height by `aspect-ratio`
        // and its cells by nothing at all, since they are all flown out - collapsed through and left the
        // page above it without room for the picture.
        if (RatioBlockExtent(box, flow, basis) is > 0)
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
            // A line box that holds nothing but collapsible white space and inline elements with no
            // margins, padding or borders is zero-height and "treated as not existing for any other
            // purpose" (CSS 2.1 §9.4.2), so it does not stand between the margins either.
            if (!child.IsBlockLevel || IsFloat(child))
            {
                if (!child.IsBlockLevel && !LeavesALine(child, flow, basis))
                {
                    continue;
                }

                return false;
            }

            if (!IsSelfCollapsing(child, flow, basis))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Gets whether an inline-level box puts something on its line that makes the line exist: text that
    /// is not collapsible white space, a break, a replaced box, or an inline box with margins, padding,
    /// borders, or content of its own (CSS Inline 3 §2.3, CSS 2.1 §9.4.2).
    /// </summary>
    private static Boolean LeavesALine(Box box, WritingModeInfo flow, Double basis)
    {
        if (box.IsReplaced || box.Kind is BoxKind.LineBreak or BoxKind.InlineBlock)
        {
            return true;
        }

        if (box.Kind == BoxKind.Text)
        {
            return HoldsText(box);
        }

        if (box.Kind != BoxKind.Inline)
        {
            // An atomic inline container - inline-flex, inline-grid, inline-table, inline masonry -
            // is one piece of the line whatever it holds.
            return true;
        }

        var margin = box.Style.Margin.Resolve(basis);
        var padding = box.Style.Padding.Resolve(basis);
        var border = BorderOf(box);

        // What puts a box on its line is a decoration standing along the axis the line runs on, and it is
        // asked edge by edge: two inline-axis values that cancel out - a margin written `1px` on one side
        // and `-1px` on the other - still give the box an inline extent of its own, while a block-axis
        // value, however large, does not make the line exist and leaves it a phantom one whose margins
        // collapse through (CSS Inline 3 §2.3, corrected in w3c/csswg-drafts#9344 to count the inline
        // axis only; the borrowed pages write both halves of that distinction).
        var (marginStart, marginEnd) = flow.InlineMargins(margin);
        var (paddingStart, paddingEnd) = flow.InlineMargins(padding);
        var (borderStart, borderEnd) = flow.InlineMargins(border);

        if (marginStart != 0 || marginEnd != 0 ||
            paddingStart != 0 || paddingEnd != 0 ||
            borderStart != 0 || borderEnd != 0)
        {
            return true;
        }

        foreach (var child in box.Children)
        {
            if (PositionedLayout.IsOutOfFlow(child))
            {
                continue;
            }

            if (child.IsBlockLevel || LeavesALine(child, flow, basis))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Gets whether a text box carries anything a line keeps: a non-white-space character under a mode
    /// that collapses spaces, a preserved character under one that does not, or a kept segment break.
    /// </summary>
    private static Boolean HoldsText(Box box)
    {
        var text = box.Text;

        if (String.IsNullOrEmpty(text))
        {
            return false;
        }

        if (!box.Style.CollapsesSpaces)
        {
            return true;
        }

        foreach (var character in text!)
        {
            // Only the CSS-collapsible white space disappears at a line's edge. A no-break space is
            // white space to .NET but a glyph on the page, and an element holding one has a line.
            if (!TextChunker.IsCollapsible(character))
            {
                return true;
            }
        }

        // `pre-line` collapses the spaces but keeps the segment breaks, so a run of nothing but breaks
        // still opens a line of its own.
        return text.IndexOf('\n') >= 0 && box.Style.WhiteSpace.Equals("pre-line", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The margin the box presents on the block-start side for collapsing with what stands before it
    /// along <paramref name="flow"/>: its own start margin folded through every leading self-collapsing
    /// child and finally through the start of the first child that has extent - the strip that moves
    /// outward through the box when nothing separates them, and what the box's stack reads as its
    /// outer margin on that side.
    /// </summary>
    private static MarginGroup CollapsedStart(Box box, WritingModeInfo flow, Double basis)
    {
        var (own, ownEnd) = flow.BlockMargins(box.Style.Margin.Resolve(basis));

        // A wall, or a box turned to the other axis, presents only its own margin: nothing travels
        // through it along the axis this walk collapses on.
        if (IsWall(box) || !SameBlockAxis(box, flow))
        {
            return new MarginGroup(own);
        }

        var border = BorderOf(box);
        var padding = box.Style.Padding.Resolve(basis);
        var (startBorder, _) = flow.BlockMargins(border);
        var (startPadding, _) = flow.BlockMargins(padding);

        if (startBorder > 0 || startPadding > 0)
        {
            return new MarginGroup(own);
        }

        var combined = new MarginGroup(own);

        // A percentage margin inside the box resolves against the box's own content width, not against
        // the width of whatever holds the box: the fold into a child is asked of the child's own
        // containing block (CSS 2.1 §8.3).
        var inner = flow.IsVertical
            ? basis
            : BoxModel.DeclaredWidth(box.Style, basis, padding.Horizontal + border.Horizontal)
                ?? Math.Max(0, basis - box.Style.Margin.Resolve(basis).Horizontal - padding.Horizontal - border.Horizontal);

        // A float among the children can give the first in-flow child clearance, and a child with
        // clearance keeps its top margin out of the collapse with the box above it (CSS 2.1 §8.3.1).
        var floats = false;

        foreach (var candidate in box.Children)
        {
            if (candidate.IsBlockLevel && IsFloat(candidate) && !PositionedLayout.IsOutOfFlow(candidate))
            {
                floats = true;
                break;
            }
        }

        foreach (var child in box.Children)
        {
            if (!child.IsBlockLevel || PositionedLayout.IsOutOfFlow(child) || IsFloat(child))
            {
                continue;
            }

            if (floats && child.Style.Clear != ClearSide.None)
            {
                return combined;
            }

            combined = combined.With(CollapsedStart(child, flow, inner));

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
            combined = combined.With(ownEnd);
        }

        return combined;
    }

    /// <summary>
    /// The margin the box presents on the block-end side: the mirror of <see cref="CollapsedStart"/>,
    /// walking backward through the trailing self-collapsing children.
    /// </summary>
    private static MarginGroup CollapsedEnd(Box box, WritingModeInfo flow, Double basis)
    {
        var (own, ownEnd) = flow.BlockMargins(box.Style.Margin.Resolve(basis));

        if (IsWall(box) || !SameBlockAxis(box, flow))
        {
            return new MarginGroup(ownEnd);
        }

        var border = BorderOf(box);
        var padding = box.Style.Padding.Resolve(basis);
        var (_, endBorder) = flow.BlockMargins(border);
        var (_, endPadding) = flow.BlockMargins(padding);

        if (endBorder > 0 || endPadding > 0)
        {
            return new MarginGroup(ownEnd);
        }

        // A box that declares its extent along the stacking axis - a fixed height on a row - pins its
        // far edge away from its content, so its last child's bottom margin collapses into that fixed
        // region instead of adjoining the box's own end edge and escaping below it. Giving a container
        // a height is the classic way to keep its children's margins from leaking out; the fixed-height
        // reftests count on it - the box after such a parent sits at the parent's bottom edge, not a
        // child margin further down. (An auto extent says nothing here, so the walk folds through.)
        if (DeclaredBlockExtent(box, flow, basis) is > 0)
        {
            return new MarginGroup(ownEnd);
        }

        // A minimum height that holds the box open over nothing keeps its last child's bottom margin
        // from adjoining its own end edge: the box's end stands below the child's margin, not on it.
        // A box whose content fills it is not held open by the minimum, and folds through as before.
        if (MinBlockExtent(box, flow) > 0 && AllSelfCollapsing(box, flow, basis))
        {
            return new MarginGroup(ownEnd);
        }

        var combined = new MarginGroup(ownEnd);

        // A percentage margin inside the box resolves against the box's own content width, not against
        // the width of whatever holds the box (CSS 2.1 §8.3).
        var inner = flow.IsVertical
            ? basis
            : BoxModel.DeclaredWidth(box.Style, basis, padding.Horizontal + border.Horizontal)
                ?? Math.Max(0, basis - box.Style.Margin.Resolve(basis).Horizontal - padding.Horizontal - border.Horizontal);

        // A child that steps clear of a float keeps its margins out of the collapse with the box above
        // it on either end, so the walk stops at it the way a box with height stops the run.
        var floats = false;

        foreach (var candidate in box.Children)
        {
            if (candidate.IsBlockLevel && IsFloat(candidate) && !PositionedLayout.IsOutOfFlow(candidate))
            {
                floats = true;
                break;
            }
        }

        for (var i = box.Children.Count - 1; i >= 0; i--)
        {
            var child = box.Children[i];

            if (!child.IsBlockLevel || PositionedLayout.IsOutOfFlow(child) || IsFloat(child))
            {
                continue;
            }

            if (floats && child.Style.Clear != ClearSide.None)
            {
                return combined;
            }

            combined = combined.With(CollapsedEnd(child, flow, inner));

            if (!IsSelfCollapsing(child, flow, basis))
            {
                return combined;
            }
        }

        if (IsSelfCollapsing(box, flow, basis))
        {
            combined = combined.With(own);
        }

        return combined;
    }

    /// <summary>
    /// Gets whether every in-flow block child of the box is self-collapsing, so the box's own content
    /// along the stack is nothing at all.
    /// </summary>
    private static Boolean AllSelfCollapsing(Box box, WritingModeInfo flow, Double basis)
    {
        foreach (var child in box.Children)
        {
            if (!child.IsBlockLevel || PositionedLayout.IsOutOfFlow(child) || IsFloat(child))
            {
                continue;
            }

            if (!IsSelfCollapsing(child, flow, basis))
            {
                return false;
            }
        }

        return true;
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

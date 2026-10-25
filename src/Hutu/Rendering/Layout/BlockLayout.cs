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
            : BoxModel.ResolveWidth(style, basisWidth, context.ContainingBlock.Width, insets.Horizontal,
                context.ContainingBlock.Height);
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
            contentHeight = ColumnsOf(box, contentWidth, out var columns, out var share)
                ? MeasureMulticolChildren(box, share, contentWidth, childHeight, declaredHeight is not null, columns, context)
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
            var column = MeasureColumnChildren(box, childInline, childBlock, context, flow);
            contentBlock = column.Block;
            childInline = Math.Max(childInline, column.Inline);
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
    private (Double Block, Double Inline) MeasureColumnChildren(
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

        // A fragment of a broken box draws only the edges it owns: the border and padding on an edge it
        // was cut at belong to the fragment that really has that edge (CSS Break 3 §4.7).
        var border = slice?.Border ?? BorderOf(box);
        var padding = slice?.Padding ?? style.Padding.Resolve(basis);

        var layout = new LayoutBox(box, KindOf(box))
        {
            Margin = BoxModel.UsedMargin(style, basis, borderBox.Width),
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

        var childContext = context.ForContainingBlock(childBox);

        // A box that holds its descendants - positioned, transformed, filtered, contained - lends the
        // whole subtree its padding box, so an absolute descendant anchors to it rather than to an
        // ancestor further up.
        childContext = PositionedLayout.ForDescendants(childContext, box, layout.PaddingBox, layout.BorderBox);

        var content = new Rect(contentX, contentY, contentWidth, contentHeight);
        var used = 0.0;

        if (HasBlockChildren(box))
        {
            used = lines is { } range
                ? ArrangeBlockLines(box, layout, contentX, contentY, contentWidth, contentHeight, range, start, childContext)
                : !flow.IsVertical && ColumnsOf(box, contentWidth, out var columns, out var share)
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
                : context.ForContainingBlock(new Rect(contentX, contentY, contentWidth, usedHeight ?? 0));

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
    /// many: a horizontal multi-column container with room for columns. A container of nothing but
    /// floats takes the column path, since each column can hold the floats that fit beside each other;
    /// floats mixed with flow content keep the ordinary stack, because the column's own float band is
    /// what would shorten the lines around them and the columns do not carry one yet.
    /// </summary>
    private static Boolean ColumnsOf(Box box, Double contentWidth, out Int32 columns, out Double share)
    {
        columns = 1;
        share = contentWidth;

        if (box.Flow.IsVertical || !box.Style.Multi.IsMultiColumn)
        {
            return false;
        }

        var floats = 0;
        var flowing = 0;

        foreach (var child in box.Children)
        {
            if (!child.IsBlockLevel)
            {
                continue;
            }

            if (PositionedLayout.IsOutOfFlow(child))
            {
                return false;
            }

            if (child.Style.Float != FloatSide.None)
            {
                floats++;
                continue;
            }

            flowing++;

            // A spanner spans every column of its row (CSS Multicol 3 §3.2), which the section layout
            // below lays out; a wrapping container would have to put the spanner in a row of column
            // boxes this does not do, so that pair keeps the ordinary stack until it can.
            if (child.Style.SpansColumns && box.Style.Multi.Wraps)
            {
                return false;
            }

            // A multi-column child balances its own columns inside one column of this one, which the
            // columns do not do yet, so the box keeps the ordinary stack until they can.
            if (!child.Flow.IsVertical && child.Style.Multi.IsMultiColumn)
            {
                return false;
            }
        }

        // Floats share a column only when nothing else stands among them: a flow box would have to read
        // the room they leave, which is the band this path does not lay down yet.
        if (floats > 0 && flowing > 0)
        {
            return false;
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

        var children = MulticolChildren(box, share, declaredHeight, context);

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
            Double whole = 0)
        {
            Box = box;
            Size = size;
            Margin = margin;
            Fragment = fragment;
            Line = line;
            Last = last;
            Whole = whole;
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

        /// <summary>
        /// Gets the border box height the child would have had unbroken, for a piece of a broken child.
        /// The background is painted across that box and cut with it, which is what keeps one gradient
        /// running down every piece (CSS Break 3 §4.7).
        /// </summary>
        public Double Whole { get; }
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

    private List<MulticolChild> MulticolChildren(
        Box box,
        Double share,
        Double declaredHeight,
        LayoutContext context,
        IReadOnlyList<Box>? members = null)
    {
        var childContext = context.ForContainingBlock(new Size(share, declaredHeight));
        var constraints = MeasureConstraints.FitWidth(share);
        var source = members ?? box.Children;
        var children = new List<MulticolChild>(source.Count);

        // Which child a run of lines belongs to, so that the slicer can tell one block's lines from its
        // neighbour's and keep the orphans and widows each of them asks for.
        var runs = 0;

        foreach (var child in source)
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
            if (FragmentsInColumns(child))
            {
                var run = runs++;
                var lines = _inline.LineHeights(
                    child, share, child.Flow, new InlineContext(_engine, childContext, childContext, childContext));

                // A box with no lines has nothing to break between: it is one piece, however tall its
                // padding or border make it, and dropping it here would lose it from the flow entirely.
                if (lines.Count == 0)
                {
                    children.Add(new MulticolChild(
                        child,
                        size,
                        margin,
                        new FragmentChild(size.Height + margin.Top + margin.Bottom, breaks.Before, breaks.After, breaks.Inside),
                        -1,
                        last: true));

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
    /// placed at the paragraph's content top, which is where the column begins them.
    /// </summary>
    private FloatBand? FloatHostBand(FloatHost host, Double share, LayoutContext context, Double contentX, Double contentY)
    {
        var tracks = TracksFor(host.Paragraph, share, contentX);

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

        var margin = host.Host.Style.Margin.Resolve(share);

        return BandFor(
            host.Host,
            tracks,
            context.Floats,
            contentY,
            contentX + margin.Left,
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
        var band = FloatHostBand(host, content, childContext, 0, 0);
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
            var filled = Fill(host, content, first, banded, plain, stated, columns, top, bottom, clones, orphans, widows, mayOverflow: true);

            return filled is null ? whole : filled with { Extent = stated };
        }

        // The least extent the whole text fits in, found by halving the way a line balance does.
        var low = 0.0;
        var high = top + Height(first, first.Count) + bottom;
        FloatParagraphPlan? best = null;

        for (var step = 0; step < 40; step++)
        {
            var middle = (low + high) / 2;
            var plan = Fill(host, content, first, banded, plain, middle, columns, top, bottom, clones, orphans, widows, mayOverflow: false);

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
    /// widows the paragraph states, and the next plan resumes from where this one broke. Null says the
    /// text does not fit in the columns it was given.
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
        Boolean mayOverflow)
    {
        var result = new List<FloatColumn>();
        var heights = first;
        var inline = banded;
        InlineStart? start = null;
        var highest = 0.0;

        for (var column = 0; column < columns; column++)
        {
            // A cloned box owns both edges on every fragment; a sliced one owns the top on its first
            // and the bottom on its last (CSS Break 3 §4.7).
            var starts = clones || column == 0;
            var endsEdge = clones || column == columns - 1;
            var room = extent - (starts ? top : 0) - (endsEdge ? bottom : 0);
            var count = FragmentSlicer.Fitting(heights, room, orphans, widows);

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

            var children = MulticolChildren(box, share, declaredHeight, context, section.Members);
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
        var contentRight = band.Left + contentWidth;
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
        var box = tracks.Place(
            child.Flow.FloatSideOf(style.Float),
            size.Width,
            size.Height,
            margin,
            y,
            StandsClear(child.Style, contentWidth));

        return (box, size);
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
            else if (!PositionedLayout.IsOutOfFlow(child))
            {
                // A box that left the flow is in neither part of the run. CSS 2.1 §8.3.1 folds no margin
                // into it and folds none of the run into it, so the strip the walk gathered is where that
                // box would have stood, and the margins it would have carried are the positioning pass's
                // own to add (ibid. §10.3.7).
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
                // The anchor is the outer edge the run reached, with nothing of the box's own margins in
                // it. An unset edge takes that place plus those margins, and the pass that lays the box
                // out adds them, so an anchor carrying them too stood the box twice its own distance from
                // the flow - which is where the reference page of a dozen reftests stands its box.
                parent.Children.Add(_engine.ArrangePositioned(
                    child,
                    new Offset(contentX, cursor + pending),
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
                    parent.Children.Add(_engine.Arrange(child, placed.BorderBox, context));
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
        var band = BandFor(host, tracks, context.Floats, contentY, contentX, contentX, contentY, contentWidth);
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
        ArrangeMulticolRun(
            children,
            counts,
            parent,
            content.X,
            content.Y,
            columns,
            share,
            multi.Gap,
            boxHeight,
            multi.RowGap,
            childContext,
            new Dictionary<Box, Double>());

        _engine.PublishFloats(box, null);

        // The rules of the gaps the columns left, worked out here where the columns are known. A
        // wrapping run is as tall as the rows of boxes it filled, whatever a balance made of the same
        // content in a single row of them.
        var wrapHeight = multi.Wraps && multi.Height is { } wrapStated ? wrapStated : 0.0;
        var runHeight = wrapHeight > 0 && declaredHeight > 0
            ? Math.Max(1, (Int32)Math.Floor((declaredHeight + multi.RowGap) / (wrapHeight + multi.RowGap))) *
                (wrapHeight + multi.RowGap) - multi.RowGap
            : extent;

        MulticolRules.Add(
            parent,
            box.Style,
            content,
            new List<(Double, Double)> { (content.Y, content.Y + runHeight) });

        return extent;
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
    private void ArrangeMulticolRun(
        List<MulticolChild> children,
        IReadOnlyList<Int32> counts,
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
        var from = 0;

        for (var cell = 0; cell < counts.Count; cell++)
        {
            var column = cell % columns;
            var row = cell / columns;
            var x = originX + column * (share + gap);
            var y = originY + row * (boxHeight + rowGap);

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
                var last = child;

                while (child.Broken && index + 1 < counts[cell] && children[from].Broken &&
                    ReferenceEquals(children[from].Box, child.Box))
                {
                    var next = children[from++];
                    index++;

                    // Every piece of the run but the first stands where the one before it ended, and only
                    // the last of them brings the margin the child ends with: a column that forgot both
                    // came up one line's margin short for every child it broke.
                    height += next.Size.Height + next.Margin.Top;
                    last = next;
                    to = next.Line + 1;
                }

                var rect = new Rect(x + child.Margin.Left, y, width, height);
                FragmentSlice? slice = null;

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

                parent.Children.Add(_engine.Arrange(
                    child.Box,
                    rect,
                    childContext,
                    definiteHeight: false,
                    lines: child.Broken ? new LineRange(child.Line, to) : null,
                    slice: slice));
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

            var children = MulticolChildren(box, share, heightStated ? declaredHeight : 0, context, section.Members);
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

            ArrangeMulticolRun(
                children,
                Fragmentainer.Fill(fragments, extents),
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
                pending = Combine(pending, childStart);
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
            FlexAlignment.End or FlexAlignment.Right => free,
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

        if (DeclaredBlockExtent(box, flow, basis) is > 0)
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
    /// borders, or content of its own (CSS 2.1 §9.4.2).
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

        if (margin.Horizontal != 0 || margin.Vertical != 0 ||
            padding.Horizontal != 0 || padding.Vertical != 0 ||
            border.Horizontal != 0 || border.Vertical != 0)
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

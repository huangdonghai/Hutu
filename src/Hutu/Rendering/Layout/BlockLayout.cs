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
/// Adjacent sibling margins collapse to the larger of the two. Parent/child collapsing is not
/// modelled yet: deciding whether the margins are adjoining needs the parent's border and padding,
/// and letting a child's margin escape would move the content of every sample page, so it is left
/// for a later pass.
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

        var (borderBoxWidth, contentWidth) = BoxModel.ResolveWidth(style, basisWidth, insets.Horizontal);
        var basisHeight = constraints.AvailableHeight ?? context.ContainingBlock.Height;
        var declaredHeight = BoxModel.DeclaredHeight(style, basisHeight, insets.Vertical);

        Double contentHeight;

        if (box.Children.Count == 0)
        {
            contentHeight = 0;
        }
        else if (HasBlockChildren(box))
        {
            contentHeight = MeasureBlockChildren(box, contentWidth, declaredHeight ?? 0, context);
        }
        else
        {
            contentHeight = _inline.MeasureHeight(box, contentWidth, new InlineContext(_engine, context));
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
    /// </remarks>
    private Double MeasureColumnChildren(
        Box box,
        Double contentInline,
        Double basisBlock,
        LayoutContext context,
        WritingModeInfo flow)
    {
        var cursor = 0.0;
        var previousEndMargin = 0.0;
        var first = true;
        var childContext = context.ForContainingBlock(new Size(basisBlock, contentInline));
        var constraints = MeasureConstraints.FitWidth(basisBlock);

        foreach (var child in box.Children)
        {
            if (!child.IsBlockLevel || PositionedLayout.IsOutOfFlow(child))
            {
                continue;
            }

            // The margins that separate the columns are the physical left and right ones, and which of
            // the two a box starts with depends on the direction the columns run. The other pair is
            // across the columns, where it is the child's own room: the child took it off the extent
            // it was given to run down, which is the extent this one hands it.
            var margin = child.Style.Margin.Resolve(contentInline);
            var (startMargin, endMargin) = flow.BlockMargins(margin);
            var spacing = first ? startMargin : Math.Max(previousEndMargin, startMargin);
            var size = _engine.Measure(child, constraints, childContext);

            cursor += spacing + flow.BlockExtent(size.Width, size.Height);
            previousEndMargin = endMargin;
            first = false;
        }

        return cursor + previousEndMargin;
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
        // it is confined to. A column's width is its block extent and says nothing about its line.
        if (!box.Flow.IsVertical &&
            style.Width.HasValue &&
            !(style.Width.Kind == StyleLengthKind.Percent && basis <= 0))
        {
            var specified = Math.Max(0, style.Width.Resolve(basis));
            var borderBox = BoxModel.IsBorderBox(style) ? specified : specified + insets;
            return new IntrinsicSize(borderBox, borderBox);
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
                max = Math.Max(max, childIntrinsic.MaxContent + before + after);
            }
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
    public LayoutBox Arrange(Box box, Rect borderBox, LayoutContext context)
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
        var childBox = flow.IsVertical
            ? new Rect(
                contentX,
                contentY,
                BoxModel.DeclaredWidth(style, context.ContainingBlock.Width, padding.Horizontal + border.Horizontal)
                    ?? context.ContainingBlock.Width,
                BoxModel.DeclaredHeight(style, context.ContainingBlock.Height, padding.Vertical + border.Vertical) ?? contentHeight)
            : new Rect(
                contentX,
                contentY,
                contentWidth,
                BoxModel.DeclaredHeight(style, context.ContainingBlock.Height, padding.Vertical + border.Vertical) ?? 0);

        var childContext = context.ForContainingBlock(childBox);

        // A positioned box is what an absolutely positioned descendant anchors to, so its padding
        // box - not its content box - becomes the containing block for the whole subtree.
        if (PositionedLayout.IsPositioned(box))
        {
            childContext = childContext.WithAbsoluteContainingBlock(layout.PaddingBox);
        }

        var content = new Rect(contentX, contentY, contentWidth, contentHeight);

        if (HasBlockChildren(box))
        {
            if (flow.IsVertical)
            {
                ArrangeColumnChildren(box, layout, content, childContext, context.ContainingBlock.Width);
            }
            else
            {
                ArrangeBlockChildren(box, layout, contentX, contentY, contentWidth, childContext);
            }
        }
        else
        {
            _inline.Layout(box, layout, content, flow, new InlineContext(_engine, context));
        }

        return layout;
    }

    /// <summary>Measures the block children and returns the height their stacking occupies.</summary>
    private Double MeasureBlockChildren(Box box, Double contentWidth, Double contentHeight, LayoutContext context)
    {
        var cursor = 0.0;
        var previousBottomMargin = 0.0;
        var first = true;
        var childContext = context.ForContainingBlock(new Size(contentWidth, contentHeight));
        var constraints = MeasureConstraints.FitWidth(contentWidth);
        var tracks = TracksFor(box, contentWidth, 0);

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
                // has to be measured with the tracks rather than with the cursor alone.
                PlaceFloat(child, floats, cursor, contentWidth, childContext);
                continue;
            }

            // The parent's content box is the child's containing block, so the child is handed its
            // width rather than the width left after its own margins: resolving those is its job.
            var spacing = Spacing(child, tracks, margin, cursor, first, previousBottomMargin);

            // The child's lines stand in the band of the floats around it, at the height the child
            // itself reaches: two siblings at different depths have different room beside a float,
            // so the band is answered per child rather than by the one context the container shares.
            var (shift, room, band) = FormattingRoot(
                child,
                BandFor(child, tracks, context.Floats, cursor + spacing, margin.Left, 0, 0, contentWidth),
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

            var size = _engine.Measure(child, childConstraints, passContext);

            cursor += spacing + size.Height;
            previousBottomMargin = margin.Bottom;
            first = false;
        }

        return Math.Max(cursor + previousBottomMargin, tracks?.Bottom ?? 0);
    }

    /// <summary>
    /// Gets the distance the stack moves on before the child: its own top margin, or the larger of
    /// that and the box above it's bottom margin - pushed further still when the child clears floats.
    /// </summary>
    private static Double Spacing(
        Box child,
        FloatTracks? tracks,
        Thickness margin,
        Double cursor,
        Boolean first,
        Double previousBottomMargin)
    {
        var spacing = first ? margin.Top : Math.Max(previousBottomMargin, margin.Top);

        // A cleared box is moved below the floats it refuses to stand beside. What it moved through
        // is not given back to the margins around it, and the stack resumes from where the box
        // landed - which is what lets a portal put its "more" link under the two columns rather than
        // beside them.
        if (tracks is { } floats && child.Style.Clear != ClearSide.None)
        {
            var cleared = floats.ClearBelow(child.Style.Clear, cursor + spacing);
            spacing = Math.Max(spacing, cleared - cursor);
        }

        return spacing;
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
    private static (Double Shift, Double Room, FloatBand? Band) FormattingRoot(
        Box child,
        FloatBand? band,
        Double contentWidth)
    {
        if (band is null || !child.Style.ClipsOverflow)
        {
            return (0, contentWidth, band);
        }

        var (left, right) = band.FreeSpan();

        var shift = Math.Max(0, left - band.Left);
        var room = Math.Max(0, Math.Min(contentWidth, right - band.Left) - shift);

        return (shift, room, null);
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

    private void ArrangeBlockChildren(
        Box box,
        LayoutBox parent,
        Double contentX,
        Double contentY,
        Double contentWidth,
        LayoutContext context)
    {
        var cursor = contentY;
        var previousBottomMargin = 0.0;
        var first = true;
        var constraints = MeasureConstraints.FitWidth(contentWidth);
        var outOfFlow = new List<(Box Box, Offset Static)>();
        var tracks = TracksFor(box, contentWidth, contentX);

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
                // reserves is the one a clearing box below it is measured against.
                var placed = PlaceFloat(child, floats, cursor, contentWidth, context);
                parent.Children.Add(_engine.Arrange(child, placed.BorderBox, context));
                continue;
            }

            var spacing = Spacing(child, tracks, margin, cursor, first, previousBottomMargin);

            if (PositionedLayout.IsOutOfFlow(child))
            {
                // Its place in flow is remembered as the static position - the anchor an unset
                // offset falls back to - but the cursor does not advance, so the boxes after it
                // close the gap it leaves behind.
                outOfFlow.Add((child, new Offset(contentX + margin.Left, cursor + spacing)));
                continue;
            }

            // The band and the step clear of the floats are the measuring pass's own arithmetic, run
            // again at the place the child lands, so the size read back is the one it was answered with.
            var (shift, room, band) = FormattingRoot(
                child,
                BandFor(
                    child, tracks, context.Floats,
                    cursor + spacing, contentX + margin.Left, contentX, contentY, contentWidth),
                contentWidth);

            var passContext = context.WithFloats(band);
            var childConstraints = constraints;

            if (shift > 0)
            {
                passContext = passContext.ForContainingBlock(new Size(room, context.ContainingBlock.Height));
                childConstraints = MeasureConstraints.FitWidth(room);
            }

            var size = _engine.Measure(child, childConstraints, passContext);
            var used = BoxModel.UsedMargin(child.Style, contentWidth, size.Width);
            var rect = new Rect(contentX + used.Left + shift, cursor + spacing, size.Width, size.Height);

            parent.Children.Add(_engine.Arrange(child, rect, passContext));

            cursor = rect.Bottom;
            previousBottomMargin = margin.Bottom;
            first = false;
        }

        // The out-of-flow boxes are placed last, against the nearest positioned ancestor that the
        // context already carries, and take no part in the stacking above.
        foreach (var (child, staticPosition) in outOfFlow)
        {
            parent.Children.Add(_engine.ArrangePositioned(child, staticPosition, context));
        }
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
    private void ArrangeColumnChildren(
        Box box,
        LayoutBox parent,
        Rect content,
        LayoutContext context,
        Double basisBlock)
    {
        var flow = box.Flow;
        var contentInline = flow.InlineExtent(content.Width, content.Height);
        var cursor = 0.0;
        var previousEndMargin = 0.0;
        var first = true;
        var constraints = MeasureConstraints.FitWidth(basisBlock);
        var outOfFlow = new List<(Box Box, Offset Static)>();

        foreach (var child in box.Children)
        {
            if (!child.IsBlockLevel)
            {
                continue;
            }

            var margin = child.Style.Margin.Resolve(contentInline);
            var (startMargin, endMargin) = flow.BlockMargins(margin);
            var (beforeIt, afterIt) = flow.InlineMargins(margin);
            var spacing = first ? startMargin : Math.Max(previousEndMargin, startMargin);

            if (PositionedLayout.IsOutOfFlow(child))
            {
                // Same as in a row: the box takes no space, but the place it would have had is kept
                // as the static position an unset offset falls back to.
                var anchor = flow.Map(content, beforeIt, 0, cursor + spacing, 0);
                outOfFlow.Add((child, new Offset(anchor.X, anchor.Y)));
                continue;
            }

            var size = _engine.Measure(child, constraints, context);
            var blockSize = flow.BlockExtent(size.Width, size.Height);
            var inlineSize = flow.InlineExtent(size.Width, size.Height);

            // The margin that opens the child is where it begins down its column, the way a box in a
            // row begins after its left margin.
            var rect = flow.Map(content, beforeIt, inlineSize, cursor + spacing, blockSize);

            parent.Children.Add(_engine.Arrange(child, rect, context));

            cursor += spacing + blockSize;
            previousEndMargin = endMargin;
            first = false;
        }

        foreach (var (child, staticPosition) in outOfFlow)
        {
            parent.Children.Add(_engine.ArrangePositioned(child, staticPosition, context));
        }
    }

    public static Boolean HasBlockChildren(Box box)
    {
        foreach (var child in box.Children)
        {
            if (child.IsBlockLevel)
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
}

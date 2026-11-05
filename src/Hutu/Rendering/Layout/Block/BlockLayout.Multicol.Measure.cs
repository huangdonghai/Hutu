using AngleSharp.Css.Dom;
using Hutu.Rendering.Boxes;
using System;
using System.Collections.Generic;

namespace Hutu.Rendering.Layout;

internal sealed partial class BlockLayout
{
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

}

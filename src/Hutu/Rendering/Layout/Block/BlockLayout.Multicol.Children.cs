using AngleSharp.Css.Dom;
using Hutu.Rendering.Boxes;
using System;
using System.Collections.Generic;

namespace Hutu.Rendering.Layout;

internal sealed partial class BlockLayout
{
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
            // A box that runs across the page, states its own length along the axis the columns stack
            // on, and holds nothing in the flow is cut there even though all it carries has left the
            // flow: the piece that would have shown a descendant stands in a column of its own, so
            // leaving the box whole loses the descendant with it. A box with children in the flow is
            // not - those are the break points the run works from - nor one whose length the content
            // decides (CSS Fragmentation 3 §4.5, measured:
            // wpt/css-break/table/table-col-paint-vlr-rtl-ref, whose 400-long relative box of absolute
            // bands comes out 40,100,100,55 of each across the four columns; against
            // gecko/flexbox/pagination/flexbox-multi-column-vertical-lr-2, whose boxes hold a flex
            // container and are left to the run's own fragmenting).
            (!HoldsPositioned(child) || (vertical && !StandsOnOutFlowOnly(child))) ||
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

}

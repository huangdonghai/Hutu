using AngleSharp.Css.Dom;
using Hutu.Rendering.Boxes;
using System;
using System.Collections.Generic;

namespace Hutu.Rendering.Layout;

internal sealed partial class BlockLayout
{
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
                // A piece that carries on from the column before stands at the head of its column with
                // nothing of its own margin left to pay: the room the first column gave the box was
                // already cut by that margin, and paying it again drops every later column's whole
                // content down by it (CSS Fragmentation 3 §4.1, measured in Chrome on
                // gecko/flexbox/pagination/flexbox-single-column-1a and its reference: the container of
                // `margin-block-start: 25px` reads (13,103,40,25) in the column that begins it and
                // (63,78,40,50), (113,78,40,50), (163,78,40,25) in the ones that carry on - the head of
                // each column, not 25 below it).
                y += portion.Offset > 0.001
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
                            AnchorAtPiece: portion.Offset > 0.001,
                            // The stretch of the box this piece stands below is measured in the box's own
                            // border box, so once the flow has gone past its block-start decoration the
                            // piece owes that room to its content even though it draws none of it.
                            OweStart: portion.Offset > 0.001 ? cutBorder.Top + cutPadding.Top : 0));


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

}

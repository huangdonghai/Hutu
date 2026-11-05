using AngleSharp.Css.Dom;
using Hutu.Rendering.Boxes;
using System;
using System.Collections.Generic;

namespace Hutu.Rendering.Layout;

internal sealed partial class BlockLayout
{
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
    private List<FloatFlowColumn> PlaceFloatFlowColumns(
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
            var natural = floating
                ? child.Style.Clear == ClearSide.None
                    ? here.Flow + margin.Top
                    : here.Tracks.ClearBelow(child.Style.Clear, here.Flow) + margin.Top
                : here.Tracks.ClearBelow(child.Style.Clear, here.Flow) + margin.Top;

            // Where the box holds lines, the floats beside them are told to the box as a band and it grows
            // to hold whatever of its own drops below them; the box itself only steps down where that band
            // changes nothing, which is the shape widows-orphans-020 asks for. Laying the band down first
            // is what Chrome does for a cleared break written *inside* the box: the box begins at the
            // column's head and comes to the float's foot, rather than being a one-line box stood on it
            // (CSS 2.1 §9.5.1-§9.5.2, CSS Fragmentation 3 §4.1, measured: a `columns: 2` box holding
            // a 20x150 float and then `<div style="border-bottom:3px solid"><br clear="all">`, whose
            // cleared box Chrome reads 100 | 53 down the two columns - 153 in all).
            var top = natural;

            // What a member of the flow holds stands in the band of the floats the column already has, so
            // a clearance written *inside* it is paid against them: measured with nothing to stand aside a
            // `<br clear=all>` takes one line of room where Chrome gives it the whole height of the floats
            // it clears (CSS 2.1 §9.5.2, CSS Fragmentation 3 §4.1, measured: a `columns: 2` box holding a
            // 20x150 float and then a `<div style="border-bottom:3px solid"><br clear="all">`, whose
            // cleared box Chrome makes 100 | 53 down the two columns - 153 in all - where we made it one
            // line tall). Only ever raised: the band can shorten lines and drop them, never lift them.
            if (!floating)
            {
                var seen = childContext.WithFloats(BandFor(
                    child,
                    here.Tracks,
                    context.Floats,
                    natural - margin.Top,
                    margin.Left,
                    margin.Right,
                    0,
                    0,
                    member.Width));
                var cleared = _engine.Measure(
                    child,
                    MeasureConstraints.FitWidth(member.Width),
                    seen);

                if (cleared.Height > size.Height)
                {
                    size = cleared;
                    outer = size.Height + margin.Top + margin.Bottom;
                }
                else if (BreaksAtLines(child))
                {
                    top = here.Tracks.FitBelow(natural - margin.Top, size.Width) + margin.Top;
                }

            }

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

        return columns;
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

            foreach (var item in runs[run].Items)
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
                // The same band the plan measured with: the piece lays its content out beside the floats
                // standing in its column, and a clearance written inside it has to see them to be paid for
                // (CSS 2.1 §9.5.2, measured in the same pair of pages as the measure above). A float is
                // not handed the band, since it is one of the floats the band is made of.
                var stood = IsFloat(item.Child)
                    ? childContext
                    : childContext.WithFloats(BandFor(
                        item.Child,
                        runs[run].Tracks,
                        context.Floats,
                        item.Y,
                        item.Margin.Left,
                        item.Margin.Right,
                        0,
                        0,
                        item.Size.Width));

                var arranged = _engine.Arrange(
                    item.Child,
                    whole,
                    stood,
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

}

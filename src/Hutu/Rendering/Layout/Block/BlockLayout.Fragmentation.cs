using AngleSharp.Css.Dom;
using Hutu.Rendering.Boxes;
using System;
using System.Collections.Generic;

namespace Hutu.Rendering.Layout;

internal sealed partial class BlockLayout
{
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

}

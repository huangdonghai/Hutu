using AngleSharp.Css.Dom;
using Hutu.Rendering.Boxes;
using System;
using System.Collections.Generic;

namespace Hutu.Rendering.Layout;

internal sealed partial class BlockLayout
{
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
                child,
                staticPosition,
                context,
                staticFromHigh: flow.IsVertical && flow.BlockSign < 0,
                staticFromInline: flow.IsVertical && flow.IsRightToLeft));
        }

        // The extent the children occupied along the column, in the same units the box states its own
        // block size in, for the same reason as in a row.
        return Math.Max(0, cursor + pending);
    }

}

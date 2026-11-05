using AngleSharp.Css.Dom;
using Hutu.Rendering.Boxes;
using System;
using System.Collections.Generic;

namespace Hutu.Rendering.Layout;

internal sealed partial class BlockLayout
{
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

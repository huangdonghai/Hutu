using Hutu.Rendering.Boxes;
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using AngleSharp.Css.Dom;

namespace Hutu.Rendering.Layout;

/// <summary>
/// The table formatting context: it turns the table, row group, row and cell boxes into the grid the
/// table algorithms work on, sizes the columns and rows, and places every cell.
/// </summary>
/// <remarks>
/// The context is the only place that knows the difference between the two border models. In the
/// separated model a cell owns its whole border and the grid leaves <c>border-spacing</c> between the
/// cells; in the collapsing model the borders are resolved per grid line and each cell paints half of
/// it, so that two adjacent cells combine into a single line. The rest of the engine only sees the
/// used border the context settled on.
/// <para>
/// The grid is rebuilt by both phases rather than shared, because measuring is cached and arranging
/// must not depend on which measure call happened to run first. The work that is genuinely expensive -
/// text measurement - is therefore done twice per cell, which the layout is small enough to absorb.
/// </para>
/// </remarks>
internal sealed partial class TableFormattingContext
{
    private const Double Epsilon = 0.0001;

    private readonly LayoutEngine _engine;

    // The heights of the table's repeating groups, kept from the measure that completed the grid: the
    // columns ask for them before they share the table out, and building the grid a second time to
    // answer would settle it against whatever width and context happened to be at hand then rather
    // than the ones the table was measured at (CSS Tables 3 §6.1).
    private readonly ConditionalWeakTable<Box, Bands> _repeatedBands = new();

    /// <summary>The height of the two groups a table repeats in each of its fragments.</summary>
    private sealed record Bands(Double Head, Double Foot);

    public TableFormattingContext(LayoutEngine engine)
    {
        _engine = engine;
    }

    /// <summary>
    /// Measures the table, returning the size of its border box. The columns are shared out from the
    /// width the table is allowed and the rows are measured from the cells, so the height is known
    /// before anything is placed.
    /// </summary>
    public Size Measure(Box box, MeasureConstraints constraints, LayoutContext context)
    {
        var style = box.Style;
        var vertical = box.Flow.IsVertical;

        // The measure asks after the same pair of axes the arrange reads off the box it is handed: the
        // columns share out the extent along the axis the table sets its cells on - the page's y for a
        // table that runs across it - and the rows fill what is left along the other. Asked the other way
        // the columns are summed along the block axis, so a table of two rows of 110 and 135 measures as
        // though its rows were columns and back again (CSS Writing Modes 3 §4.1, measured:
        // wpt/css-break/table/table-col-paint-vlr-rtl, whose table is 400 along the page's x and 100
        // along its y, the same 100x400 its four fragmentainers make).
        // A percentage of the table or its cells reads the containing block's *inline* size, which is its
        // height when that block is a column (CSS Writing Modes 3 §4.1, the rule BlockLayout keeps at its
        // head); the room handed along an axis only says what that axis has left when it is given.
        var inlineRoom = context.ContainingIsVertical
            ? context.ContainingBlock.Height
            : context.ContainingBlock.Width;
        var basisInline = vertical
            ? constraints.AvailableHeight ?? inlineRoom
            : constraints.AvailableWidth ?? inlineRoom;
        var basisBlock = vertical
            ? constraints.AvailableWidth ?? context.ContainingBlock.Width
            : constraints.AvailableHeight ?? context.ContainingBlock.Height;
        var build = Build(box, basisInline, context);
        var insetsInline = vertical ? build.InsetsV : build.InsetsH;
        var insetsBlock = vertical ? build.InsetsH : build.InsetsV;

        var spacingTotal = HorizontalSpacingTotal(build);
        var minContent = build.Grid.SumMinimum() + spacingTotal;
        var maxContent = build.Grid.SumMaximum() + spacingTotal;

        var contentWidth = ResolveContentWidth(
            style,
            build,
            minContent,
            maxContent,
            basisInline,
            constraints,
            vertical);

        // The captions are measured first because the height the table declared covers them too; what is
        // left of it once they have taken their share is the height the rows have to fill. A caption
        // stands in the table's wrapper, so the room it is measured against is the table's outer width,
        // its own border and padding included (CSS 2.1 §17.4, measured: table-grid-paint-htb-ltr).
        MeasureCaptions(
            build,
            vertical ? basisInline : contentWidth + insetsInline,
            context);

        // The children that are not table parts are blocks of the table's own content, and they are
        // measured against the width the table settled on.
        MeasureOthers(build, contentWidth, context);

        // The height a table states is the height of its grid: the captions stand in the wrapper around
        // the table box and are added on top, which is what Chrome draws for a 2em-tall table with a
        // caption (measured, table-caption-and-cells).
        var declared = vertical
            ? BoxModel.DeclaredWidth(style, basisBlock, insetsBlock)
            : BoxModel.DeclaredHeight(style, basisBlock, insetsBlock);
        var gridHeight = declared;

        CompleteGrid(build, contentWidth, context, gridHeight);

        // The grid is complete here, which is the one place the repeating groups' heights are known at
        // the width and context the table was measured at.
        _repeatedBands.AddOrUpdate(
            box,
            new Bands(
                RepeatedBand(build, role: 0, RepeatRoom(context, box))?.Height ?? 0.0,
                RepeatedBand(build, role: 2, RepeatRoom(context, box))?.Height ?? 0.0));

        var othersHeight = build.OtherHeights.Count > 0
            ? build.OtherHeights.Sum()
            : 0.0;

        // A grid with no rows to grow has nothing to take the height the table declared, and the table
        // box is still that tall: its border and background frame the declared height, with the captions
        // standing around it in the wrapper (CSS 2.1 §17.5.3, measured: table-border-008, a table of two
        // captions and no rows).
        // The captions stand at the head and the foot of the stack, whichever way the page runs: along y
        // for a table that runs across it and along x for one that runs down it, where they take their
        // share of the 400 the rows and they together make (CSS Writing Modes 3 §4.1, measured:
        // wpt/css-break/table/table-col-paint-vlr-rtl, whose 50 and 75 captions stand at either end of
        // the page's x, the axis its 110 and 135 rows stack along).
        var contentHeight = Math.Max(declared ?? 0, build.Grid.Height) +
            build.CaptionTop + build.CaptionBottom + othersHeight;

        // A declared height is a floor, as CSS 2.1 §17.5.3 has it: a table asked to be shorter than its
        // rows need is as tall as the rows make it. The width answers the same way, one step further up:
        // a table asked to be narrower than its columns can go is as wide as the columns make it.
        var usedHeight = vertical
            ? BoxModel.ClampWidth(style, Math.Max(declared ?? 0, contentHeight), basisBlock, insetsBlock)
            : BoxModel.ClampHeight(style, Math.Max(declared ?? 0, contentHeight), basisBlock, insetsBlock);

        // The pair the engine's Size means as the page draws it: across, then down (BlockLayout keeps the
        // same order for a column, whose children are measured along y). The columns make the inline
        // extent and the rows with the captions make the block one, whichever way the page runs.
        var inlineTotal = Math.Max(contentWidth, build.Grid.Width) + insetsInline;
        var blockTotal = usedHeight + insetsBlock;

        return new Size(
            vertical ? blockTotal : inlineTotal,
            vertical ? inlineTotal : blockTotal);
    }

    /// <summary>
    /// The intrinsic inline sizes of the table: the sum of the column minimums and of the column
    /// maximums. A flex item or a grid track that sizes itself from a table asks for this.
    /// </summary>
    public IntrinsicSize MeasureIntrinsic(Box box, LayoutContext context)
    {
        // The pair asked for is the extent along the axis the table sets its cells on, so a table that
        // runs across the page adds the thicknesses of that axis, and a percentage reads the containing
        // block's inline size (CSS Writing Modes 3 §4.1).
        var vertical = box.Flow.IsVertical;
        var basis = context.ContainingIsVertical
            ? context.ContainingBlock.Height
            : context.ContainingBlock.Width;
        var build = Build(box, basis, context);
        var spacingTotal = HorizontalSpacingTotal(build);
        var insets = vertical ? build.InsetsV : build.InsetsH;
        var min = build.Grid.SumMinimum() + spacingTotal + insets;
        var max = build.Grid.SumMaximum() + spacingTotal + insets;

        // A width the author stated is part of what the table brings to a box that shrinks to fit, as the
        // preferred size it would rather be: a table with no cells is still as wide as it was told (CSS 2.1
        // §17.5.2.2, and §17.5.3 under the collapsed model, where the table keeps no padding of its own to
        // make the difference up). Asked by the columns alone such a table is nothing, and the float holding
        // one collapses with it - which is what wpt's collapsing-border-model-013 and -014 hold to.
        //
        // Only the preferred size is raised, never the narrowest one: the automatic minimum of an item is
        // read off this pair (CSS Flexbox 1 §4.5, §9.9.1) and a stated width is not a min-width there
        // (wpt's table-as-item-specified-width holds to exactly that). And a percentage is left out, since
        // the width it would resolve against is the very width being asked about - CSS Tables 3 §4 puts
        // such a table at its preferred width instead, which the columns already answer.
        var stated = vertical ? box.Style.Height : box.Style.Width;

        if (stated.HasValue && stated.Kind != StyleLengthKind.Percent && !stated.ReadsContainingBlock)
        {
            max = Math.Max(max, BoxModel.IsBorderBox(box.Style)
                ? Math.Max(0, stated.Resolve(basis))
                : stated.Resolve(basis) + insets);
        }

        return new IntrinsicSize(min, Math.Max(min, max));
    }

    /// <summary>Places the captions, the rows and the cells inside the table's border box.</summary>
    public LayoutBox Arrange(Box box, Rect borderBox, LayoutContext context, FragmentSlice? slice = null)
    {
        var style = box.Style;
        var vertical = box.Flow.IsVertical;

        // Percentages of the table's own box, and of its cells, read the inline axis: the height of a
        // table that runs across the page (CSS Writing Modes 3 §4.1, CSS 2.1 §17.5).
        // Percentages read the containing block's inline size, which is its height when the box that
        // holds the table is itself a column (CSS Writing Modes 3 §4.1).
        var basis = context.ContainingIsVertical
            ? context.ContainingBlock.Height
            : context.ContainingBlock.Width;
        var build = Build(box, basis, context);

        // The table's own border belongs to the table box, not to the wrapper the captions stand in. A
        // piece that covers the whole box keeps every edge, and the fragment's clip cuts the ones that
        // fall outside it - which is what draws the table box's top edge in a fragment that cut the
        // wrapper above it (table-grid-paint-htb-ltr). A piece cut through the table box itself owns only
        // the edges it really has (table-border-007). CSS 2.1 §17.4, CSS Break 3 §4.7.
        var ownEdges = slice is { } cut &&
            (Math.Abs(cut.Whole.Height - borderBox.Height) > 0.001 ||
             Math.Abs(cut.Whole.Width - borderBox.Width) > 0.001)
                ? slice
                : null;
        var border = ownEdges?.Border ?? build.Border;
        var padding = ownEdges?.Padding ?? build.Padding;
        var blockReversed = vertical && box.Flow.BlockIsRightToLeft;

        // The captions stand in the wrapper along the block axis, which is the width of a table that runs
        // across the page: what is left of the handed box once they have taken their share is the room the
        // grid has along that axis, and the side the captions straddle keeps all of it (CSS Writing Modes 3
        // §4.1 against CSS 2.1 §17.4, measured: table-grid-paint-vrl-rtl).
        MeasureCaptions(build, vertical ? borderBox.Height : borderBox.Width, context);

        // The box the table was given already accounts for the height it declared, so whatever the
        // captions do not claim is the room the rows have to fill - along the block axis, which is the
        // width of a table that runs across the page.
        var tableBlockExtent = Math.Max(
            0,
            (vertical ? borderBox.Width : borderBox.Height) - build.CaptionTop - build.CaptionBottom);
        // The columns are shared out along the inline axis - the table's height when the table runs
        // across the page - and the rows fill what the captions leave of the block axis (CSS Writing
        // Modes 3 §4.1, measured: build/_vt22.html, whose two columns are 60 tall each inside a table
        // 150 tall, and whose rows are 50 wide each inside one 130 wide).
        var contentWidth = Math.Max(
            0,
            (vertical ? borderBox.Height : borderBox.Width) -
                (vertical ? border.Vertical + padding.Vertical
                    : border.Horizontal + padding.Horizontal));
        MeasureOthers(build, contentWidth, context);

        var tableBox = vertical
            ? new Rect(
                blockReversed
                    ? borderBox.Right - build.CaptionTop - tableBlockExtent
                    : borderBox.X + build.CaptionTop,
                borderBox.Y,
                tableBlockExtent,
                borderBox.Height)
            : new Rect(
                borderBox.X,
                borderBox.Y + build.CaptionTop,
                borderBox.Width,
                tableBlockExtent);
        // The room the rows have to fill lies along the block axis, which is the table's width when the
        // table runs across the page, and the captions claim their share of that same axis whichever way
        // it runs (CSS Writing Modes 3 §4.1 against CSS 2.1 §17.5.3, measured:
        // wpt/css-writing-modes/table-cell-valign-001 - a vertical-lr table of three horizontal cells
        // fills 102 along the page's x, not the 314 its columns make along its y).
        var gridHeight = Math.Max(
            0,
            (vertical
                ? borderBox.Width - border.Horizontal - padding.Horizontal
                : borderBox.Height - border.Vertical - padding.Vertical) -
                build.CaptionTop - build.CaptionBottom);
        CompleteGrid(build, contentWidth, context, gridHeight);

        var layout = new LayoutBox(box, LayoutKind.Table)
        {
            Margin = BoxModel.UsedMargin(style, basis, borderBox.Width),
            Border = border,
            Padding = padding,
            BorderBox = borderBox,
            Flow = box.Flow,
            BorderColors = box.Style.Table.Collapse ? build.OuterColors : null,
            BorderStyles = box.Style.Table.Collapse ? build.OuterStyles : null,
            SlicesBlockStart = slice is { Starts: false },
            SlicesBlockEnd = slice is { Ends: false },
            BackgroundOrigin = slice is null || slice.Value.Starts && slice.Value.Ends
                ? null
                : slice.Value.Whole,
            TableBox = tableBox,
            // The table's own halves of its two outer horizontal lines stop where the outermost rows stop,
            // so they are carried as two bands of their own whenever a row's line on the table's vertical
            // edge is narrower than the widest there (CSS 2.1 §17.6.2).
            TableEdgeBands = box.Style.Table.Collapse &&
                build.OuterStartTop + build.OuterEndTop +
                    build.OuterStartBottom + build.OuterEndBottom > 0.001
                ? (
                    InsetInline(
                        vertical,
                        box.Flow.IsRightToLeft,
                        tableBox,
                        build.OuterStartTop,
                        build.OuterEndTop),
                    InsetInline(
                        vertical,
                        box.Flow.IsRightToLeft,
                        tableBox,
                        build.OuterStartBottom,
                        build.OuterEndBottom))
                : null,
        };

        // The grid's origins, read along the table's own axes: where the inline axis begins, and the
        // edge the block axis grows from - the right one for vertical-rl.
        var gridX = vertical
            ? blockReversed
                ? tableBox.Right - build.Border.Right - build.Padding.Right
                : tableBox.X + build.Border.Left + build.Padding.Left
            : borderBox.X + build.Border.Left + build.Padding.Left;

        // A positioned table anchors the absolutely positioned descendants the captions and cells hand
        // up to it, the same way a positioned block does: a static caption is not a containing block,
        // so its abs child passes up to the nearest positioned ancestor - this table. The anchor is the
        // outer box, not the padded one: position belongs to the table's wrapper box, and that box
        // carries no border of its own (the border the drawing shows sits on the inner table box), so
        // its padding edge is the outer edge. Insetting by this box's border would push every abs
        // descendant off by that border, the way the gecko table-abs-pos references say it should not.
        // A table that holds its descendants - positioned, transformed, filtered, contained - is what the
        // absolutely positioned descendants the captions and cells hand up to it anchor against, the same
        // way a block does: a static caption is not a containing block, so its abs child passes up to the
        // nearest ancestor that holds one - this table. The rectangle handed down is the outer box rather
        // than the padded one: position belongs to the table's wrapper box, and that box carries no border
        // of its own (the border the drawing shows sits on the inner table box), so its padding edge is the
        // outer edge. Insetting by this box's border would push every abs descendant off by that border,
        // the way the gecko table-abs-pos references say it should not.
        var childContext = PositionedLayout.ForDescendants(context, box, layout.BorderBox, layout.BorderBox);

        // A caption stands in the table's wrapper, across the whole of the table's outer width: the
        // table's own border and padding are inside the box the captions straddle, not around them
        // (CSS 2.1 §17.4, measured: table-grid-paint-htb-ltr).
        var captionY = borderBox.Y;
        var blockCursor = blockReversed ? borderBox.Right : borderBox.X;

        foreach (var caption in build.Captions)
        {
            if (caption.Side != CaptionSide.Top)
            {
                continue;
            }

            if (vertical)
            {
                layout.Children.Add(ArrangeCaptionAcross(caption, blockCursor, borderBox.Y, childContext));
                blockCursor += blockReversed ? -caption.OuterBlock : caption.OuterBlock;
            }
            else
            {
                layout.Children.Add(ArrangeCaption(caption, borderBox.X, captionY, borderBox.Width, childContext));
                captionY += caption.OuterHeight;
            }
        }

        // The captions stand in the wrapper around the table box, so under the collapsing model the
        // outer halves of the table's own lines fall between the caption and the first row.
        var gridY = box.Style.Table.Collapse
            ? borderBox.Y + build.CaptionTop + build.Border.Top + build.Padding.Top
            : borderBox.Y + build.Border.Top + build.Padding.Top +
                (vertical ? 0 : build.CaptionTop);
        ArrangeRows(build, layout, gridX, gridY, childContext, slice?.Piece);
        ArrangeColumnBands(build, layout, gridX, gridY);

        var bottomY = gridY + build.Grid.Height;

        // The children that are not table parts are blocks of the table's own content, and they stand
        // below the rows - the table holds them beside its grid rather than around it.
        for (var index = 0; index < build.Others.Count; index++)
        {
            var other = build.Others[index];
            var margin = build.OtherMargins[index];
            var width = build.OtherWidths[index];
            var height = build.OtherHeights[index] - margin.Vertical;

            var otherContext = childContext.ForContainingBlock(new Size(width, 0));

            layout.Children.Add(_engine.Arrange(
                other,
                new Rect(gridX + margin.Left, bottomY + margin.Top, width, height),
                otherContext));

            bottomY += build.OtherHeights[index];
        }

        // The bottom captions stand under the table box - under its bottom border - rather than under the
        // rows, and the wrapper ends with them (CSS 2.1 §17.4).
        var captionBottomY = borderBox.Bottom - build.CaptionBottom;
        var bottomCursor = blockReversed ? tableBox.X : tableBox.Right;

        foreach (var caption in build.Captions)
        {
            if (caption.Side != CaptionSide.Bottom)
            {
                continue;
            }

            if (vertical)
            {
                layout.Children.Add(ArrangeCaptionAcross(caption, bottomCursor, borderBox.Y, childContext));
                bottomCursor += blockReversed ? -caption.OuterBlock : caption.OuterBlock;
            }
            else
            {
                layout.Children.Add(ArrangeCaption(caption, borderBox.X, captionBottomY, borderBox.Width, childContext));
                captionBottomY += caption.OuterHeight;
            }
        }

        RepeatSections(build, box, layout, borderBox, gridY, context, slice);

        return layout;
    }

    /// <summary>
    /// Gets the height the table's repeating groups take in every fragment it is drawn in: the header
    /// group that stands at each fragment's top and the footer group that stands at its bottom. The
    /// columns ask this before they share the table out, since a repeated group is not painted over the
    /// room the rows took - it takes room of its own (CSS Tables 3 §6.2: "When the header rows are being
    /// repeated, user agents must leave room"). The measure that completed the
    /// grid left the two heights behind; a table that has not been measured has nothing for the columns
    /// to give up room to, which is a table they share out whole.
    /// </summary>
    internal (Double Head, Double Foot) RepeatedBands(Box box) =>
        _repeatedBands.TryGetValue(box, out var bands) ? (bands.Head, bands.Foot) : (0.0, 0.0);

    /// <summary>
    /// Draws the table's repeating sections again where a fragment after the first needs them: the header
    /// group at the fragment's top and, where the table goes on, the footer group at its bottom. The copy
    /// is the table laid out whole, shifted so the section stands where the fragment begins (or ends),
    /// and clipped to the section's own band so nothing else of it shows (CSS Tables 3 §6.1, measured:
    /// repeated-section/text-in-nested-multicol).
    /// </summary>
    private void RepeatSections(
        TableBuild build,
        Box box,
        LayoutBox layout,
        Rect borderBox,
        Double gridY,
        LayoutContext context,
        FragmentSlice? slice)
    {
        if (slice is not { Height: > 0.001 } fragment)
        {
            return;
        }

        var top = borderBox.Y + fragment.Offset;
        var bottom = top + fragment.Height;

        // The header group is copied where the fragment begins without it: the first fragment shows the
        // group it really has, and a later one shows the copy at its top - which is also where the piece's
        // own content starts once the column has given up room for the copy (CSS Tables 3 §6.2).
        var room = RepeatRoom(context, box);

        if (!fragment.Starts && RepeatedBand(build, role: 0, room) is { } head && head.Height <= fragment.Height + 0.001)
        {
            Repeat(head, top, bandAt: top);
        }

        if (!fragment.Ends && RepeatedBand(build, role: 2, room) is { } foot &&
            foot.Height <= fragment.Height + 0.001)
        {
            Repeat(foot, bottom - foot.Height, bandAt: bottom - foot.Height);
        }

        void Repeat((Double Top, Double Height) section, Double stand, Double bandAt)
        {
            var band = new Rect(borderBox.X, bandAt, borderBox.Width, section.Height);

            if (band.Y < borderBox.Y - 0.001 || band.Bottom > borderBox.Bottom + 0.001)
            {
                return;
            }

            var shift = stand - (gridY + section.Top);
            var copy = Arrange(
                box,
                new Rect(borderBox.X, borderBox.Y + shift, borderBox.Width, borderBox.Height),
                context);

            copy.FragmentClip = band;
            layout.Children.Add(copy);
        }
    }

    /// <summary>
    /// Gets the block size the fragmentainer this table stands in states for its fragments, which is the
    /// room a repeated group is measured against: a column's height for a table that runs down the page,
    /// its width for one whose lines run across it. Zero says nothing was stated, and a balanced
    /// container of an automatic height comes out that way (CSS Tables 3 §6.2).
    /// </summary>
    private static Double RepeatRoom(LayoutContext context, Box box) =>
        box.Flow.IsVertical ? context.ContainingBlock.Width : context.ContainingBlock.Height;

    /// <summary>
    /// Gets the band the group a fragment repeats takes inside the grid, in grid coordinates, or null
    /// where the table repeats nothing of that kind. Only the group the table hoists - its first header
    /// or its first footer - is ever copied, and only when that group itself asks not to be broken: a
    /// header or footer free to split across fragments is left where its rows fall, which is what a
    /// table of `display:table-header-group` blocks with no `break-inside` draws (CSS Tables 3 §6.2,
    /// measured: sections-and-captions-mixed-order, whose header shows once although the table runs
    /// over four columns, against variable-fragmentainer-size-001, whose header shows in every one).
    /// A further group of the same role is body content and is not copied either.
    /// <para>
    /// The same clause caps what a fragment may copy: the copy is asked for only where it reaches no
    /// further than a quarter of the fragmentainer's own block size, which is what Chrome answers for a
    /// 20 or 25 tall header in a column a hundred tall and refuses for a 26, and repeats a 40 in a
    /// column two hundred tall and refuses a 51 (measured: build/_repthresh.html). A fragmentainer that
    /// states no block size - a balanced container of an automatic height - has no quarter to be a
    /// quarter of, and the copy is asked for on the room alone.
    /// </para>
    /// </summary>
    private static (Double Top, Double Height)? RepeatedBand(TableBuild build, Int32 role, Double room)
    {
        var group = role == 0 ? build.HeadGroup : role == 2 ? build.FootGroup : null;

        if (group is null || group.Style.Breaks.Inside != BreakValue.Avoid)
        {
            return null;
        }

        var top = Double.NaN;
        var bottom = 0.0;

        foreach (var row in build.Grid.Rows)
        {
            if (!ReferenceEquals(row.Group, group))
            {
                continue;
            }

            if (Double.IsNaN(top))
            {
                top = row.Y;
            }

            bottom = row.Y + row.Height;
        }

        if (Double.IsNaN(top) || bottom <= top)
        {
            return null;
        }

        var band = (Top: top, Height: bottom - top);

        // A header or footer taller than a quarter of the fragmentainer is left to its own place.
        if (room > 0.001 && band.Height > room / 4 + Epsilon)
        {
            return null;
        }

        return band;
    }

    /// <summary>
    /// Turns a place in the table's own axes into the rectangle the page draws it in. A table that runs
    /// down the page measures its columns along x and stacks its rows along y; one that runs across the
    /// page measures its columns along y and stacks its rows along x, from the block-start edge the
    /// writing mode names - the right one for vertical-rl, so a row grows back towards the left
    /// (CSS Writing Modes 3 §4.1, measured: build/_vt22.html, a 2x2 table in vertical-rl whose rows
    /// stand at x=714 and x=654 fifty wide each and whose columns stand at y=10 and y=80 sixty tall
    /// each, inside a table 130 wide and 150 tall).
    /// </summary>
    /// <summary>
    /// Stands a rectangle in from the two ends of the flow's own inline axis. Those are the page's x for a
    /// table that runs across it only when the writing mode runs that way: left-to-right takes the insets
    /// as left and right, right-to-left the other way round, and a table that runs down the page stands
    /// its columns along y whichever way the page itself turns.
    /// </summary>
    private static Rect InsetInline(Boolean vertical, Boolean reversed, Rect rect, Double start, Double end)
    {
        var taken = Math.Min(start + end, vertical ? rect.Height : rect.Width);

        if (taken <= 0.001)
        {
            return rect;
        }

        return vertical
            ? new Rect(rect.X, rect.Y + start, rect.Width, Math.Max(0.0, rect.Height - taken))
            : reversed
                ? new Rect(rect.X + end, rect.Y, Math.Max(0.0, rect.Width - taken), rect.Height)
                : new Rect(rect.X + start, rect.Y, Math.Max(0.0, rect.Width - taken), rect.Height);
    }

    private static Rect FlowRect(
        Boolean vertical,
        Boolean blockReversed,
        Double inlineStart,
        Double inlineAt,
        Double inlineExtent,
        Double blockStart,
        Double blockAt,
        Double blockExtent)
    {
        if (!vertical)
        {
            return new Rect(
                inlineStart + inlineAt,
                blockStart + blockAt,
                inlineExtent,
                blockExtent);
        }

        return new Rect(
            blockReversed ? blockStart - blockAt - blockExtent : blockStart + blockAt,
            inlineStart + inlineAt,
            blockExtent,
            inlineExtent);
    }

    /// <summary>
    /// Places one caption of a table that runs across the page: its own extent along the stack is its
    /// width, the side it straddles is its height, and the edge it begins at is the block-start edge the
    /// writing mode names - the right one for vertical-rl, from which it grows back towards the left
    /// (CSS Writing Modes 3 §4.1 against CSS 2.1 §17.4, measured: table-grid-paint-vrl-rtl, whose
    /// caption of 140 stands 652..792 in the wrapper that ends at the container's right edge).
    /// </summary>
    private LayoutBox ArrangeCaptionAcross(CaptionEntry caption, Double blockStart, Double crossStart, LayoutContext context)
    {
        var margin = caption.Margin;
        var reversed = caption.Box.Flow.BlockIsRightToLeft;
        var boxX = reversed ? blockStart - margin.Right - caption.Width : blockStart + margin.Left;
        var boxY = crossStart + margin.Top;

        var captionContext = context.ForContainingBlock(new Size(caption.Width, caption.Height));

        if (PositionedLayout.IsPositioned(caption.Box))
        {
            captionContext = captionContext.WithAbsoluteContainingBlock(
                new Rect(boxX, boxY, caption.Width, caption.Height));
        }

        return _engine.Arrange(
            caption.Box,
            new Rect(boxX, boxY, caption.Width, caption.Height),
            captionContext,
            definiteHeight: false);
    }

    private LayoutBox ArrangeCaption(CaptionEntry caption, Double x, Double y, Double width, LayoutContext context)
    {
        var margin = caption.Margin;

        // A caption is a block box in the table's flow, so its own margins shift it inside the band
        // the table gave it, the same way a block's margins sit it off its containing box. Its width
        // is its own measured one - the table's content width when it left the width to the content,
        // but its declared width when it named one, even wider than the table. The absolutely
        // positioned descendants anchor to where that box actually landed.
        var boxX = x + margin.Left;
        var boxY = y + margin.Top;
        var boxWidth = caption.Width;

        var captionContext = context.ForContainingBlock(new Size(boxWidth, 0));

        // A positioned caption anchors its own absolutely positioned descendants, as a block would.
        if (PositionedLayout.IsPositioned(caption.Box))
        {
            captionContext = captionContext.WithAbsoluteContainingBlock(new Rect(boxX, boxY, boxWidth, caption.Height));
        }

        return _engine.Arrange(caption.Box, new Rect(boxX, boxY, boxWidth, caption.Height), captionContext);
    }

    /// <summary>
    /// Places the background of each column behind the rows it spans. A column paints in the cells'
    /// bands and not in the spacing between them, and each row's band is its own picture, so a gradient
    /// on a column starts afresh at every row (CSS 2.1 §17.5, measured in Chrome: col backgrounds are
    /// laid row band by row band, with the border-spacing left to the table).
    /// </summary>
    /// <remarks>
    /// The bands are inserted ahead of the rows so that they are drawn under them: a cell's own
    /// background covers the column's, and a row's covers the column's where the two meet.
    /// </remarks>
    private void ArrangeColumnBands(TableBuild build, LayoutBox table, Double gridX, Double gridY)
    {
        var grid = build.Grid;

        if (grid.Rows.Count == 0 || grid.Columns == 0)
        {
            return;
        }

        var bands = new List<LayoutBox>();
        var vertical = build.Box.Flow.IsVertical;
        var blockReversed = vertical && build.Box.Flow.BlockIsRightToLeft;
        var inlineStart = vertical ? gridY : gridX;
        var blockStart = vertical ? gridX : gridY;

        for (var column = 0; column < grid.Columns; column++)
        {
            var own = column < build.ColumnBoxes.Count ? build.ColumnBoxes[column] : null;
            var group = column < build.ColumnGroupBoxes.Count ? build.ColumnGroupBoxes[column] : null;

            if (!Paints(group) && !Paints(own))
            {
                continue;
            }

            var columnAt = grid.ColumnX[column];
            var columnExtent = grid.Widths[column];

            foreach (var row in grid.Rows)
            {
                var place = FlowRect(
                    vertical, blockReversed,
                    inlineStart, columnAt, columnExtent,
                    blockStart, row.Y, row.Height);

                if (Paints(group))
                {
                    bands.Add(Band(group!, place.X, place.Y, place.Width, place.Height));
                }

                if (Paints(own))
                {
                    bands.Add(Band(own!, place.X, place.Y, place.Width, place.Height));
                }
            }
        }

        table.Children.InsertRange(0, bands);
    }

    /// <summary>Gets whether a table part paints a background of its own.</summary>
    private static Boolean Paints(Box? part) =>
        part is not null &&
        (part.Style.BackgroundColor is { A: > 0 } || part.Style.Backgrounds.Count > 0);

    /// <summary>One column's band over one row, which paints the column's background and nothing else.</summary>
    private static LayoutBox Band(Box part, Double x, Double y, Double width, Double height) =>
        new(part, LayoutKind.Block)
        {
            BorderBox = new Rect(x, y, width, height),
            SuppressBorder = true,
        };

    /// <summary>
    /// Lays the rows out, nesting each run of rows that share a row group inside the group's box so
    /// that a row group background can be painted behind its rows.
    /// </summary>
    private void ArrangeRows(TableBuild build, LayoutBox table, Double gridX, Double gridY, LayoutContext context, Rect? piece)
    {
        var grid = build.Grid;
        var spacing = build.Style.Collapse ? 0.0 : build.Style.VerticalSpacing;
        // The room between two columns and the room between two rows are two different numbers, and a
        // cell that spans over one of them grows across that one only: `border-spacing: 0 2px` leaves the
        // width of a colspan alone while it adds to a rowspan. Reading the vertical distance on both axes
        // made a cell across two columns as wide as the columns plus a row gap, which is a box no row of
        // the table occupies (§17.5.0.1).
        var inlineSpacing = build.Style.Collapse ? 0.0 : build.Style.HorizontalSpacing;
        Box? groupBox = null;
        LayoutBox? group = null;
        // A row group that is moved takes its rows with it, so a row's own displacement is the group's
        // plus whatever the row states.
        Offset groupShift = Offset.Zero;
        Offset rowShift = Offset.Zero;
        // What a percentage offset on a part is a share of is the height its containing block *stated*:
        // a group's is the table's, a row's is the group around it or the table where it stands outside
        // one. A part whose row or group or table is only as tall as the rows happen to make has no such
        // whole to be a share of, and the offset then says nothing rather than reaching all the way down
        // the grid it grew to (CSS 2.1 §10.5 - "if the height of the containing block is specified
        // explicitly" - which is what the borrowed pages state in their own asserts: the same `top: 100%`
        // resolves to a row's ten pixels and not to the hundred the row was grown to by its content).
        var tableHeight = StatedHeight(build.Box, 0.0);
        var vertical = build.Box.Flow.IsVertical;
        var blockReversed = vertical && build.Box.Flow.BlockIsRightToLeft;
        var inlineStart = vertical ? gridY : gridX;
        var blockStart = vertical ? gridX : gridY;

        foreach (var row in grid.Rows)
        {
            if (!ReferenceEquals(row.Group, groupBox))
            {
                if (group is not null)
                {
                    // The rows this group has taken were moved already, and a group's box was grown from
                    // their unshifted places: the group's own ground moves on its own terms, or the
                    // background would slide out from under the content it belongs to.
                    group.BorderBox = new Rect(
                        group.BorderBox.X + groupShift.X,
                        group.BorderBox.Y + groupShift.Y,
                        group.BorderBox.Width,
                        group.BorderBox.Height);
                }

                groupBox = row.Group;
                group = groupBox is null
                    ? null
                    : new LayoutBox(groupBox, LayoutKind.TableRowGroup)
                    {
                        BorderBox = FlowRect(
                            vertical, blockReversed,
                            inlineStart, 0.0, grid.Width,
                            blockStart, row.Y, row.Height),
                        // The part says its own position, as every other box built by the engine does: a
                        // row group standing `relative` is drawn among the positioned descendants rather
                        // than with the in-flow content, which is what puts its ground over the marker a
                        // page leaves showing underneath it.
                        Position = groupBox.Style.Position.Position,
                    };

                groupShift = PartOffset(groupBox, grid.Width, tableHeight);

                if (group is not null)
                {
                    table.Children.Add(group);
                }
            }

            var rowLayout = new LayoutBox(row.Box, LayoutKind.TableRow)
            {
                BorderBox = FlowRect(
                    vertical, blockReversed,
                    inlineStart, 0.0, grid.Width,
                    blockStart, row.Y, row.Height),
                Position = row.Box.Style.Position.Position,
            };

            var own = PartOffset(row.Box, grid.Width, group is null ? tableHeight : StatedHeight(groupBox, 0.0));
            rowShift = new Offset(groupShift.X + own.X, groupShift.Y + own.Y);

            if (group is null)
            {
                table.Children.Add(rowLayout);
            }
            else
            {
                group.Children.Add(rowLayout);

                if (vertical)
                {
                    ExpandGroupAcross(group, blockStart + row.Y, row.Height, spacing, blockReversed);
                }
                else
                {
                    ExpandGroup(group, gridY + row.Y, row.Height, spacing);
                }
            }

            // A positioned row - or, failing that, the row group around it - is what an absolutely
            // positioned descendant anchors to. What `position` does to a table part is left undefined by
            // CSS 2.1, and a suite that keeps its green square inside the row is saying it means the row:
            // left unset, such a descendant falls all the way back to the initial containing block and is
            // drawn wherever the page starts, rather than beside the content it belongs to.
            //
            // The box used is the row's content box - the span its cells cover - rather than its border
            // box. A row is laid out across the table's grid, and under the separate model its cells begin
            // a spacing in from either edge; anchoring to the outer edge would put a descendant two pixels
            // out from the content it belongs beside.
            var inset = grid.ColumnX.Length > 0 ? grid.ColumnX[0] : 0.0;
            Rect? anchored = null;

            if (PositionedLayout.IsPositioned(row.Box))
            {
                anchored = FlowRect(
                    vertical, blockReversed,
                    inlineStart, inset, Math.Max(0, grid.Width - 2 * inset),
                    blockStart, row.Y, row.Height);
            }
            else if (group is { } around && PositionedLayout.IsPositioned(groupBox!))
            {
                anchored = new Rect(
                    around.BorderBox.X + inset,
                    around.BorderBox.Y,
                    Math.Max(0, around.BorderBox.Width - 2 * inset),
                    around.BorderBox.Height);
            }

            var rowContext = anchored is { } at ? context.WithAbsoluteContainingBlock(at) : context;

            foreach (var cell in row.Cells)
            {
                rowLayout.Children.Add(ArrangeCell(build, cell, gridX, gridY, inlineSpacing, spacing, rowContext, piece));
            }

            // The row is moved whole, content and all, once it has been placed: what stands inside a row
            // that stepped out of its grid place steps along with it, an absolutely positioned descendant
            // included - its containing block is this row's border box as the row is actually drawn.
            MoveWithContent(rowLayout, rowShift);
        }

        if (group is not null)
        {
            group.BorderBox = new Rect(
                group.BorderBox.X + groupShift.X,
                group.BorderBox.Y + groupShift.Y,
                group.BorderBox.Width,
                group.BorderBox.Height);
        }
    }

    /// <summary>
    /// Gets the distance a table part that states a relative offset is moved by: a row group carries its
    /// own, and the rows inside it carry that distance as well as theirs, because a row's place in the
    /// flow is a place inside the group's box (CSS Positioned 3 §6.2, which puts a table part on the same
    /// footing as a block box; §6.3 that a relative offset is a displacement of the box and its content).
    /// </summary>
    private static Offset PartOffset(Box? part, Double width, Double? height)
    {
        if (part is null || !PositionedLayout.IsPositioned(part) ||
            part.Style.Position.Position != PositionMode.Relative)
        {
            return Offset.Zero;
        }

        return PositionedLayout.RelativeOffset(part.Style.Position, new Size(width, height ?? 0.0), part.Flow);
    }

    /// <summary>
    /// Gets the height a part stated in its own style, or nothing where it left the height to its rows.
    /// A share named in per cent is read against that number rather than against the height the rows
    /// came to, so the question is only ever what the page wrote; a per cent is not an answer here, since
    /// the whole it would be a share of is the very height being asked for.
    /// </summary>
    private static Double? StatedHeight(Box? part, Double basis)
    {
        if (part is null)
        {
            return null;
        }

        var height = part.Style.Height.HasValue ? part.Style.Height : part.HintedHeight;

        return height is { HasValue: true } stated && stated.Kind != StyleLengthKind.Percent
            ? Math.Max(0, stated.Resolve(basis))
            : (Double?)null;
    }

    /// <summary>
    /// Moves a box and everything it holds. Unlike the shift that aligns a cell's content, this one takes
    /// the out-of-flow descendants with it: an absolutely positioned box standing inside a row that moved
    /// was placed against that row's own offset, so the two move together - leaving it behind would keep
    /// it pinned to where the row would have stood.
    /// </summary>
    private static void MoveWithContent(LayoutBox box, Offset shift)
    {
        if (shift.X == 0 && shift.Y == 0)
        {
            return;
        }

        box.BorderBox = new Rect(
            box.BorderBox.X + shift.X,
            box.BorderBox.Y + shift.Y,
            box.BorderBox.Width,
            box.BorderBox.Height);

        foreach (var child in box.Children)
        {
            MoveWithContent(child, shift);
        }
    }

    /// <summary>
    /// Grows a row group's box along the block axis of a table that runs across the page, so that it
    /// covers the rows it has received: the axis the rows stack along is the width there, and it runs
    /// back from the edge vertical-rl begins at.
    /// </summary>
    private static void ExpandGroupAcross(
        LayoutBox group, Double blockAt, Double blockExtent, Double spacing, Boolean reversed)
    {
        var edge = reversed
            ? Math.Min(group.BorderBox.X, blockAt - blockExtent - spacing / 2)
            : Math.Max(group.BorderBox.Right, blockAt + blockExtent + spacing / 2);

        group.BorderBox = reversed
            ? new Rect(
                edge,
                group.BorderBox.Y,
                Math.Max(0.0, group.BorderBox.Right - edge),
                group.BorderBox.Height)
            : new Rect(
                group.BorderBox.X,
                group.BorderBox.Y,
                Math.Max(0.0, edge - group.BorderBox.X),
                group.BorderBox.Height);
    }

    /// <summary>Grows a row group's box so that it covers the rows it has received so far.</summary>
    private static void ExpandGroup(LayoutBox group, Double rowY, Double rowHeight, Double spacing)
    {
        var bottom = Math.Max(group.BorderBox.Bottom, rowY + rowHeight + spacing / 2);

        group.BorderBox = new Rect(
            group.BorderBox.X,
            group.BorderBox.Y,
            group.BorderBox.Width,
            Math.Max(0, bottom - group.BorderBox.Y));
    }

    private LayoutBox ArrangeCell(
        TableBuild build,
        TableCellInfo cell,
        Double gridX,
        Double gridY,
        Double inlineSpacing,
        Double spacing,
        LayoutContext context, Rect? piece)
    {
        var grid = build.Grid;
        var vertical = build.Box.Flow.IsVertical;
        var blockReversed = vertical && build.Box.Flow.BlockIsRightToLeft;
        var width = ColumnSpanWidth(grid, cell, inlineSpacing);
        var height = RowSpanHeight(grid, cell, spacing);

        // A cell stands at its column along the inline axis and at its row along the block one, whichever
        // way the table is turned: for a table that runs across the page that puts the columns down the
        // page and the rows across it (CSS Writing Modes 3 §4.1, measured: build/_vt22.html, where the
        // two cells of a row come back at y=10 and y=80 rather than side by side in x).
        var rect = FlowRect(
            vertical, blockReversed,
            vertical ? gridY : gridX, grid.ColumnX[cell.Column], width,
            vertical ? gridX : gridY, grid.Rows[cell.Row].Y, height);

        // On the table's own outer edge the collapsed line is shared with nobody, so its width stands
        // centred on the grid line the widest row there makes: the cell's box stands in by half the
        // difference between the two widths, which leaves the border - painted whole inside the box that
        // is left - reaching from that centre out both ways. Without it a narrower row's line fills the
        // edge and its content starts where the widest row's content would (CSS 2.1 §17.6.2, measured:
        // wpt/css-break/table/table-collapsed-borders-paint-at-boundary, where the black row's 15-pixel
        // line does stand at the column's own edge while a 10-pixel line begins 2.5 in from it, and
        // Chrome's own cell rects read columnLeft + 7.5 for a table reading 92 wide at columnLeft).
        rect = InsetInline(
            vertical,
            build.Box.Flow.IsRightToLeft,
            rect,
            cell.InlineStartInset,
            cell.InlineEndInset);

        var x = rect.X;
        var y = rect.Y;

        // The room the cell's own content has, on the axes the page draws in: the insets are physical, so
        // what they take off is the physical pair the rectangle is drawn with.
        var contentWidth = Math.Max(0, rect.Width - cell.Insets.Horizontal);
        var contentHeight = Math.Max(0, rect.Height - cell.Insets.Vertical);
        var cellContext = context.ForContainingBlock(new Size(contentWidth, contentHeight));

        // A cell written `position: relative` is displaced by the table rather than by the general path,
        // because what a share of its offset is a share of is the height the row was *stated* - and the
        // height the grid ended up making it says nothing about that. A row that named no height gives a
        // `top: 100%` nothing to be taken from, so the cell stays where the grid laid it instead of
        // reaching all the way down the content that grew the row; a row that named ten moves the cell
        // ten, not the hundred its cell made it (CSS 2.1 §10.5 - "if the height of the containing block
        // is specified explicitly" - which is what the borrowed pages state, in the two directions, as
        // 010 and 013). The width a share of the horizontal offsets is taken from is the row's span
        // across the grid, which is the footing a row or a group standing off that grid is given too.
        var displacement = PartOffset(cell.Box, grid.Width, StatedHeight(grid.Rows[cell.Row].Box, 0.0));

        // A positioned cell anchors its own absolutely positioned descendants, the same way a positioned
        // block does: without this they look past the cell to whatever encloses the table.
        if (PositionedLayout.IsPositioned(cell.Box))
        {
            cellContext = cellContext.WithAbsoluteContainingBlock(new Rect(
                x + cell.UsedBorder.Left,
                y + cell.UsedBorder.Top,
                Math.Max(0, width - cell.UsedBorder.Horizontal),
                Math.Max(0, height - cell.UsedBorder.Vertical)));
        }

        // A cell whose room the table's stated height filled has a definite height, so a percentage
        // inside it resolves; one a row or a neighbour made tall does not (CSS Sizing 3 §5.2, measured
        // in Chrome). A height the cell itself wrote is already definite through its own style.
        var definite = cell.Box.HintedHeight is { HasValue: true } hint && hint.Kind != StyleLengthKind.Percent;

        for (var row = cell.Row; row < cell.Row + cell.RowSpan && row < grid.Rows.Count; row++)
        {
            definite |= grid.Rows[row].FilledByTable;
        }

        var layout = _engine.Arrange(cell.Box, rect, cellContext, definite, displacement: displacement);

        // The used border replaces whatever the cell's own style asked for: under the collapsing model
        // it is half of a shared line, so the box's border and the one it paints are not the same.
        layout.Border = cell.UsedBorder;
        layout.Padding = cell.Padding;

        if (cell.Colors is { } colors)
        {
            layout.BorderColors = colors;
            layout.BorderStyles = cell.LineStyles;

            // The line a cell's two vertical edges run along reaches to the further side of the
            // horizontal lines it meets: the shared line goes whole with the row above it, while the
            // table's own outer halves belong to its first and last rows (CSS 2.1 §17.6.2, measured:
            // table-collapsed-borders-paint-htb-ltr, whose lime band runs 120..180 over a row whose box
            // is 125..175).
            // The upper half of a shared line goes with the row above - unless that row is not in this
            // fragment at all: a cell whose top edge is the fragment's own top edge still shows the
            // mitre its wide vertical edges reach above the line, so the band begins at that edge rather
            // than half a line below it (CSS 2.1 17.6.2, measured:
            // wpt/css-break/table/table-collapsed-borders-paint-at-boundary, whose continued fragments
            // open with the 15-pixel-wide vertical rather than with the horizontal line's colour).
            var cutAbove = piece is { } band && cell.Row > 0 && rect.Y <= band.Y + 0.001;

            var overhang = cell.Row == 0 || cutAbove
                ? -cell.UsedBorder.Top
                : cell.UsedBorder.Top;
            layout.SideBand = new Rect(
                rect.X,
                rect.Y + overhang,
                rect.Width,
                Math.Max(0, rect.Height - overhang + cell.UsedBorder.Bottom));
        }

        var hide = build.Style.HideEmptyCells && !build.Style.Collapse && cell.IsEmpty;
        layout.SuppressBackground = hide;
        layout.SuppressBorder = hide;

        // The alignment belongs to the cell, not to the table it sits in, and it runs along the cell's own
        // block axis: the width of a table that runs across the page where the cell runs with it, and its
        // height where the cell is turned aside inside one - which is the pair `vertical-align` speaks of
        // (CSS Writing Modes 3 §4.1, measured: wpt/css-writing-modes/table-cell-valign-001, three
        // horizontal cells of Ahem inside a vertical-lr table, whose top/middle/bottom each shift the
        // cell's own glyphs along the page's y, the axis those cells stack their lines along).
        AlignContent(
            layout,
            cell.Box.Style.Table.VerticalAlign,
            cell.Box.Flow.IsVertical
                ? Math.Max(0, rect.Width - cell.Insets.Horizontal)
                : contentHeight);

        return layout;
    }

    /// <summary>
    /// Shifts a cell's content down when it is shorter than the row and the cell asks for middle or
    /// bottom alignment. The height comes from the row, so a one-line cell in a tall row has to be
    /// moved rather than stretched.
    /// </summary>
    /// <remarks>
    /// A box that left the flow is none of this method's business: it is measured against its own
    /// containing block rather than stacked with the cell's content, so it neither says how tall that
    /// content is nor moves when it is aligned. Only the in-flow children are counted and shifted.
    /// <para>
    /// What is counted is where each child stood in the flow rather than where it is drawn. A child set
    /// <c>position: relative</c> keeps the room it had and steps out of it (CSS 2.1 §9.4.3), so a cell
    /// holding one hundred pixels of content that was moved ten up still holds one hundred pixels of
    /// content - and reading the moved place would leave the alignment to charge the same move a second
    /// time, which is how a cell asked to stand at the top of its row came to sit half a move off it.
    /// </para>
    /// </remarks>
    private static void AlignContent(LayoutBox cell, CellVerticalAlignment align, Double contentHeight)
    {
        if (cell.Children.Count == 0 || align == CellVerticalAlignment.Top)
        {
            return;
        }

        var top = cell.ContentBox.Y;
        var bottom = top;

        foreach (var child in cell.Children)
        {
            if (child.IsOutOfFlow)
            {
                continue;
            }

            bottom = Math.Max(
                bottom,
                child.BorderBox.Bottom - child.RelativeDisplacement.Y + child.Margin.Bottom);

            // A float counts as the content it takes room for, wherever it stands in the subtree -
            // measured in Chrome, a cell holding nothing but a tall float has no slack to align
            // anything in, its content box having grown to hold that float.
            bottom = Math.Max(bottom, Floats(child));
        }

        static Double Floats(LayoutBox box)
        {
            var bottom = 0.0;

            foreach (var child in box.Children)
            {
                if (child.IsOutOfFlow)
                {
                    continue;
                }

                if (child.Source.Style.Float != FloatSide.None)
                {
                    bottom = Math.Max(
                        bottom,
                        child.BorderBox.Bottom - child.RelativeDisplacement.Y + child.Margin.Bottom);
                }

                bottom = Math.Max(bottom, Floats(child));
            }

            return bottom;
        }

        var used = bottom - top;
        var free = contentHeight - used;

        if (free <= Epsilon)
        {
            return;
        }

        var shift = align switch
        {
            CellVerticalAlignment.Middle => free / 2,
            CellVerticalAlignment.Bottom => free,
            _ => 0.0,
        };

        if (shift <= 0)
        {
            return;
        }

        foreach (var child in cell.Children)
        {
            if (!child.IsOutOfFlow)
            {
                Shift(child, shift);
            }
        }
    }

    /// <summary>Moves a box and its whole in-flow subtree down by the given distance.</summary>
    private static void Shift(LayoutBox box, Double dy)
    {
        box.BorderBox = new Rect(box.BorderBox.X, box.BorderBox.Y + dy, box.BorderBox.Width, box.BorderBox.Height);

        foreach (var child in box.Children)
        {
            // An out-of-flow descendant inside the content - an absolutely positioned box in a line,
            // say - was placed against its own containing block and is not part of what was aligned.
            if (!child.IsOutOfFlow)
            {
                Shift(child, dy);
            }
        }
    }

    /// <summary>Gets the border-box width a cell occupies, including the spacing it spans over.</summary>
    private static Double ColumnSpanWidth(TableGrid grid, TableCellInfo cell, Double spacing)
    {
        var width = 0.0;
        var last = Math.Min(cell.Column + cell.Span, grid.Columns);

        for (var i = cell.Column; i < last; i++)
        {
            width += grid.Widths[i];
        }

        return width + (spacing * Math.Max(0, cell.Span - 1));
    }

    /// <summary>Gets the border-box height a cell occupies, including the spacing it spans over.</summary>
    private static Double RowSpanHeight(TableGrid grid, TableCellInfo cell, Double spacing)
    {
        var height = 0.0;
        var last = Math.Min(cell.Row + cell.RowSpan, grid.Rows.Count);

        for (var i = cell.Row; i < last; i++)
        {
            height += grid.Rows[i].Height;
        }

        return height + (spacing * Math.Max(0, cell.RowSpan - 1));
    }

    /// <summary>Gets the space the columns and their border spacing need between them.</summary>
    private static Double HorizontalSpacingTotal(TableBuild build) =>
        build.Style.Collapse || build.Grid.Columns == 0
            ? 0.0
            : build.Style.HorizontalSpacing * (build.Grid.Columns + 1);

    /// <summary>
    /// Resolves the width of the table's content box. A declared width wins, subject to the columns'
    /// minimum; an automatic width shrinks to fit, which is what makes a table without a width only
    /// as wide as its content.
    /// <para>
    /// The floor is the automatic algorithm's, and the fixed one says otherwise: a table laid out fixed
    /// takes the width it declared, its remaining columns dividing "the remaining horizontal table space"
    /// and "cells in subsequent rows do not affect column widths" (CSS 2.1 §17.5.2.1). What a cell holds
    /// is then content that overflows the column it stands in, to be hidden or left standing by that
    /// column's own overflow - not a reason for the table to grow. A fixed table asked to be narrower than
    /// its words are wide is narrow, and its words are cut.
    /// </para>
    /// </summary>
    private static Double ResolveContentWidth(
        ComputedStyle style,
        TableBuild build,
        Double minContent,
        Double maxContent,
        Double basis,
        MeasureConstraints constraints,
        Boolean vertical)
    {
        // The extent asked for is the one along the axis the table sets its cells on: its width for a
        // table that runs across the page, its height for one that runs down it (CSS Writing Modes 3 §4.1).
        var insets = vertical ? build.InsetsV : build.InsetsH;
        var declared = DeclaredContentWidth(
            vertical ? style.Height : style.Width,
            basis,
            insets,
            BoxModel.IsBorderBox(style));
        Double content;

        if (declared is { } inline)
        {
            content = build.Style.Fixed ? inline : Math.Max(inline, minContent);
        }
        else if ((vertical ? constraints.AvailableHeight : constraints.AvailableWidth) is { } available)
        {
            var room = Math.Max(0, available - insets);
            content = Math.Clamp(room, minContent, Math.Max(minContent, maxContent));
        }
        else
        {
            content = Math.Max(minContent, maxContent);
        }

        var isBorderBox = BoxModel.IsBorderBox(style);

        if (BoxModel.ContentBound(vertical ? style.MaxHeight : style.MaxWidth, basis, insets, isBorderBox) is { } maximum &&
            content > maximum)
        {
            content = maximum;
        }

        if (BoxModel.ContentBound(vertical ? style.MinHeight : style.MinWidth, basis, insets, isBorderBox) is { } minimum &&
            content < minimum)
        {
            content = minimum;
        }

        return Math.Max(0, content);
    }

    /// <summary>Gets a declared extent along the table's own axes as a content-box extent, or null where
    /// there is none.</summary>
    private static Double? DeclaredContentWidth(
        StyleLength width,
        Double basis,
        Double insets,
        Boolean isBorderBox)
    {
        if (!width.HasValue || (width.Kind == StyleLengthKind.Percent && basis <= 0))
        {
            return null;
        }

        var resolved = Math.Max(0, width.Resolve(basis));
        return isBorderBox ? Math.Max(0, resolved - insets) : resolved;
    }
}

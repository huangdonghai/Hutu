using AngleSharp.Css.Dom;
using Hutu.Rendering.Boxes;
using System.Buffers;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;

using System.Globalization;

namespace Hutu.Rendering.Layout;

internal sealed partial class InlineLayout
{
    /// <summary>One character the intrinsic walk drew, with where its room can be asked for again.</summary>
    private readonly record struct DrawnGlyph(
        ComputedStyle Style,
        String Source,
        Int32 Start,
        Int32 Length,
        WritingModeInfo Flow);

    /// <summary>
    /// Gets the room the opening glyph of a first formatted line asks for under the value: nothing at
    /// all, because the glyph hangs outside the line the content is measured at.
    /// </summary>
    private Double FirstHangRoom(
        ComputedStyle style,
        String source,
        Int32 start,
        Int32 length,
        Boolean firstOfElement,
        WritingModeInfo flow)
    {
        if (!firstOfElement || !HangingPunctuation.HangsAtStart(style.HangingPunctuation))
        {
            return 0;
        }

        var units = TextClusters.UnitsAt(source, start, start + length);

        return HangingPunctuation.IsOpening(TextChunker.CodePointAt(source, start))
            ? Measure(style, source, start, units, flow)
            : 0;
    }

    /// <summary>
    /// Gets the room the closing glyph of a line asks for under the value: nothing at all, because the
    /// glyph hangs outside the line. <c>allow-end</c> hangs only a line that would not otherwise fit,
    /// and the widest line always fits.
    /// </summary>
    private Double EndHang(DrawnGlyph glyph, Boolean last) =>
        HangingPunctuation.HangsAtEnd(
            glyph.Style.HangingPunctuation,
            TextChunker.CodePointAt(glyph.Source, glyph.Start),
            overflows: false,
            last)
            ? Measure(glyph.Style, glyph.Source, glyph.Start, glyph.Length, glyph.Flow)
            : 0;

    /// <summary>
    /// Lays out the inline children of the container inside the given content box, appending line
    /// boxes to it, and returns the extent they occupied along the block axis.
    /// </summary>
    public Double Layout(Box container, LayoutBox parent, Rect contentBox, WritingModeInfo flow, InlineContext context)
    {
        return LayoutCore(container, parent, contentBox, flow, context);
    }

    /// <summary>
    /// Gets how tall each of the container's lines stands at the given width. A block whose parent is
    /// multi-column is broken between its own lines (CSS Multicol 3 §3.1, CSS Break 3 §4.4), and the
    /// parent needs those heights to know where a column ends.
    /// </summary>
    public IReadOnlyList<Double> LineHeights(
        Box container, Double maxInline, WritingModeInfo flow, InlineContext context) =>
        Heights(Plan(container, maxInline, flow, context, Indentation(container, flow, maxInline)));

    /// <summary>
    /// Gets how tall each line of the content from <paramref name="start"/> on stands: what a column
    /// that resumes a broken paragraph needs, where the lines are longer than the first column's
    /// because the float that shortened them is not beside them.
    /// </summary>
    internal IReadOnlyList<Double> LineHeights(
        Box container, Double maxInline, WritingModeInfo flow, InlineContext context, InlineStart start) =>
        Heights(Plan(container, maxInline, flow, context, Indentation(container, flow, maxInline), start));

    /// <summary>
    /// Lays the lines numbered <paramref name="from"/> up to <paramref name="to"/> into the given content
    /// box, which is one fragment of a block broken across columns. The plan is the whole content's, so
    /// the line a fragment ends on is not the line the block ends on and is not aligned as one; the
    /// fragment that resumes a break starts where its first line stands rather than under a trimmed
    /// leading, which belongs to the fragment that begins the block.
    /// </summary>
    public Double Layout(
        Box container,
        LayoutBox parent,
        Rect contentBox,
        WritingModeInfo flow,
        InlineContext context,
        Int32 from,
        Int32 to,
        InlineStart? start = null)
    {
        var maxInline = flow.InlineExtent(contentBox.Width, contentBox.Height);
        var lines = Plan(container, maxInline, flow, context, Indentation(container, flow, maxInline), start);

        AlignLastLines(lines, maxInline);
        DistributeSpace(lines);

        var (trimStart, _) = Trim(container, lines);

        // A block broken across columns is placed one fragment at a time, so the inline elements it
        // holds are whole only within the fragment being placed. What that costs is written up under
        // Placement. A fragment that resumes partway through starts where its plan does: the trimmed
        // leading belongs to the fragment that began the block.
        var placement = new Placement(context.Engine);
        var used = PlaceLines(
            container, parent, lines, from, to, contentBox, flow, context, start is null && from == 0 ? -trimStart : 0, placement);
        placement.Finish(flow);

        return used;
    }

    private Double LayoutCore(Box container, LayoutBox parent, Rect contentBox, WritingModeInfo flow, InlineContext context)
    {
        var maxInline = flow.InlineExtent(contentBox.Width, contentBox.Height);
        var multi = container.Style.Multi;

        // A multi-column container breaks its lines at the width of one column, so the plan it needs is
        // a different one from the block's own extent: it is made here, at the column's width, and the
        // columns share it.
        if (!flow.IsVertical && multi.IsMultiColumn)
        {
            var (columns, share) = multi.Used(maxInline);

            if (columns > 1)
            {
                return LayoutColumns(container, parent, contentBox, flow, context, columns, share, multi);
            }
        }

        // The lines the measuring phase broke this content into, if it broke them at the same extent -
        // which is what the block layout makes sure of, since it reconstructs the child's containing
        // block from the same arithmetic it measured against.
        var lines = Plan(container, maxInline, flow, context, Indentation(container, flow, maxInline));

        // Which lines the block's alignment reaches is settled before the room is spread: the last line
        // and the ones a forced break ended are the block's `text-align-last` to align, and a line it
        // leaves justified is spread like any other.
        AlignLastLines(lines, maxInline);

        // Justification is the arrangement's business and not the breaking's: the same lines the
        // measuring broke are spread across their spaces here, where the room they were given is
        // settled and will not move again.
        DistributeSpace(lines);

        // CSS Inline 3 §5.1: a block may trim the leading away at its start and end, so the first and last
        // lines stand with their text edge on the block's edge. The line box is not resized - the lines
        // simply start above the block's start and the block's height ends at the last text edge.
        var (trimStart, trimEnd) = Trim(container, lines);
        var placement = new Placement(context.Engine);
        var cursor = PlaceLines(container, parent, lines, 0, lines.Count, contentBox, flow, context, -trimStart, placement);

        // Whatever the lines held back is placed once the whole of the content stands where it does, and
        // the displaced elements are moved before the container's height is handed back - which is a
        // question of where the drawing lands, not of how tall the block came out.
        placement.Finish(flow);

        return Math.Max(0, cursor - trimEnd);
    }

    /// <summary>
    /// Lays the lines out in the container's columns: each column takes the lines the line slicer gives
    /// it (CSS Break 3 §3.3 keeps the orphans and widows), and the lines of one column stand at its own
    /// inline offset. The columns end at the height they were shared at, which the measure settled.
    /// </summary>
    private Double LayoutColumns(
        Box container,
        LayoutBox parent,
        Rect contentBox,
        WritingModeInfo flow,
        InlineContext context,
        Int32 columns,
        Double share,
        MulticolStyle multi)
    {
        var lines = Plan(container, share, flow, context, Indentation(container, flow, share));
        AlignLastLines(lines, share);
        DistributeSpace(lines);

        var heights = Heights(lines);
        var keeps = Keeps(lines);
        var breaks = container.Style.Breaks;
        var starts = Leadings(container, lines, atStart: true);
        var ends = Leadings(container, lines, atStart: false);
        // A container that wraps its columns makes a grid of column boxes, one `column-height` tall,
        // and the lines run down a box, on into the next column and then into the next row.
        var columnHeight = multi.Wraps && multi.Height is { } stated ? stated : 0.0;
        var wraps = columnHeight > 0;
        var rows = wraps && contentBox.Height > 0
            ? Math.Max(1, (Int32)Math.Floor((contentBox.Height + multi.RowGap) / (columnHeight + multi.RowGap)))
            : 1;

        var extent = wraps
            ? columnHeight
            : multi.Fills
                ? contentBox.Height
                : Fragmentainer.BalanceLines(
                    heights,
                    columns,
                    breaks.Orphans,
                    breaks.Widows,
                    starts,
                    ends,
                    keeps);

        // A fill is not held to the column count: content that does not fit the stated columns spills
        // into more of them, which is how Chrome lays a run too long for its `column-count` out.
        var counts = Fragmentainer.FillLines(
            heights,
            extent,
            wraps ? columns * rows : multi.Fills ? Int32.MaxValue : columns,
            breaks.Orphans,
            breaks.Widows,
            starts,
            ends,
            spills: multi.Fills && !wraps,
            keeps: keeps);

        // Each column trims the leading off its own first and last lines (CSS Inline 3 §6.2), so every
        // column begins with its first line standing where that line's text edge asks.
        var (trimStart, trimEnd) = Trim(container, lines);
        var from = 0;

        // The columns share one placement, because an inline element reaching down past the end of one
        // is one box standing in several of them - and the boxes it holds are anchored to the whole of it.
        var placement = new Placement(context.Engine);

        for (var cell = 0; cell < counts.Count; cell++)
        {
            var count = counts[cell];
            var area = new Rect(
                contentBox.X + (cell % columns) * (share + multi.Gap),
                contentBox.Y + (cell / columns) * (columnHeight + multi.RowGap),
                share,
                extent);

            PlaceLines(container, parent, lines, from, from + count, area, flow, context, -starts[from], placement);
            from += count;
        }

        placement.Finish(flow);
        MulticolRules.Add(
            parent,
            container.Style,
            contentBox,
            new List<(Double, Double)> { (contentBox.Y, contentBox.Y + extent) });

        return Math.Max(0, extent - trimStart - trimEnd);
    }

    /// <summary>
    /// Builds the line flow of one margin-note group at the inline extent the page margin offers.
    /// Margin notes use the ordinary line composer but are placed as one block after the document
    /// layout has settled.
    /// </summary>
    internal MarginNoteFlow BuildMarginNoteFlow(
        Box note,
        Double inlineExtent,
        WritingModeInfo flow,
        InlineContext context)
    {
        var source = NoteSource.Read(note);
        var items = NoteItems(
            source,
            0,
            source.Text.Length,
            inlineExtent,
            flow,
            upright: false);
        var lines = items.Count == 0
            ? new List<Line>()
            : BuildLines(
                items,
                inlineExtent,
                flow,
                context,
                note.Style,
                Indentation(note, flow, inlineExtent),
                emergency: false,
                settleEdges: true);

        ReturnItems(items);
        AlignLastLines(lines, inlineExtent);
        DistributeSpace(lines);

        var block = 0.0;

        foreach (var line in lines)
        {
            block += Math.Max(0, line.Height) + line.GapBefore;
        }

        return new MarginNoteFlow(note, lines, flow, block, this);
    }

    /// <summary>Places a margin-note flow into its already-positioned content rectangle.</summary>
    internal void PlaceMarginNoteFlow(
        MarginNoteFlow flow,
        LayoutBox parent,
        Rect contentBox,
        InlineContext context)
    {
        if (flow.BlockSize <= 0)
        {
            return;
        }

        flow.Place(parent, contentBox, context);
    }

    /// <summary>Gets the heights the lines take when stacked, the gaps a float made included.</summary>
    /// <summary>Gets how far along its line each of the container's lines reaches at the given width:
    /// the place a float written after that content stands at, and a right one ends at (CSS 2.1 §9.5.1).
    /// </summary>
    public IReadOnlyList<Double> LineWidths(
        Box container, Double maxInline, WritingModeInfo flow, InlineContext context) =>
        Widths(Plan(container, maxInline, flow, context, Indentation(container, flow, maxInline)));

    private static List<Double> Widths(List<Line> lines)
    {
        var widths = new List<Double>(lines.Count);

        foreach (var line in lines)
        {
            // `Used` rather than `Extent`: the extent is the room the line was broken at, which is the
            // whole block unless a float shortened it, while a float set after the line has to begin
            // where the line's own content reaches (CSS 2.1 §9.5.1).
            widths.Add(Math.Max(0, line.Used));
        }

        return widths;
    }

    private static List<Double> Heights(List<Line> lines)
    {
        var heights = new List<Double>(lines.Count);

        foreach (var line in lines)
        {
            heights.Add(Math.Max(0, line.Height) + line.GapBefore + line.Tail);
        }

        return heights;
    }

    /// <summary>Gets the boundaries between lines that a fragment break must not cut.</summary>
    private static List<Boolean> Keeps(List<Line> lines)
    {
        var keeps = new List<Boolean>(lines.Count);

        foreach (var line in lines)
        {
            keeps.Add(line.KeepWithNext);
        }

        return keeps;
    }

    private static Double Total(List<Double> heights)
    {
        var total = 0.0;

        foreach (var height in heights)
        {
            total += height;
        }

        return total;
    }

    /// <summary>
    /// Places lines <paramref name="from"/> to <paramref name="to"/> into the content box, stacking them
    /// from <paramref name="start"/>, and returns where the stack ended.
    /// </summary>
    private Double PlaceLines(
        Box container,
        LayoutBox parent,
        List<Line> lines,
        Int32 from,
        Int32 to,
        Rect contentBox,
        WritingModeInfo flow,
        InlineContext context,
        Double start,
        Placement placement)
    {
        var cursor = start;
        var step = new InlineStep();

        for (var lineIndex = from; lineIndex < to; lineIndex++)
        {
            var line = lines[lineIndex];

            // A line is placed for what is on it, not for how tall it came out: a run set with no line
            // height at all still has glyphs, and they are drawn overflowing the line they sit on.
            if (line.Fragments.Count == 0)
            {
                cursor += Math.Max(0, line.Height) + line.GapBefore;
                continue;
            }

            // The room a line had to fall through to get clear of a float belongs ahead of it, not after:
            // the line that dropped stands at the height it reached, and the ones below it stack from there.
            cursor += line.GapBefore;

            // A line whose text runs both ways is drawn in the order the bidi algorithm gives it: the
            // runs are split where their direction changes, and the pieces stand in the line by their
            // levels (UAX #9 L2). A line of one direction is left exactly as the markup wrote it.
            // A column is a line like any other - it only runs the other way - and the level a run is
            // set at says which end of its room its first character stands at there too.
            BidiOrder(line, flow, container.Style);

            // The line box spans the whole inline extent of the content box and only as much of the
            // block one as it needs, so in a row it is the content width and the line's own height,
            // and in a column the content height and the band the text runs down.
            var lineBox = new LayoutBox(container, LayoutKind.Line)
            {
                BorderBox = flow.Map(contentBox, line.Left, line.Extent, cursor, line.Height),
                Baseline = flow.BlockOffsetFromLeft(line.Height, line.Ascent),
                Flow = flow,
            };

            foreach (var marker in line.Fragments)
            {
                if (marker.MarginNote is { } margin)
                {
                    context.Engine.RegisterMarginNote(margin.Box, lineBox);
                }
            }

            var open = new List<OpenInline>();
            var origin = line.Offset + line.Left;
            var last = line.Fragments[^1];

            foreach (var fragment in line.Fragments)
            {
                // A glyph hanging off the line's start edge stands one advance before the line: the
                // boxes it belongs to reach out over it, and the line's own cursor is where the
                // content after it begins (CSS Text 3 §8.2.1).
                var box = fragment.HangSide == HangStart ? origin - fragment.Width : origin;

                if (fragment.InterlineGap || fragment.InterlineNote is not null ||
                    fragment.MarginNote is not null)
                {
                    origin = box + fragment.Width;
                    continue;
                }

                // The inline elements the run sits inside are opened and closed around it, each taking
                // a box that begins and ends where the run reaches its edges. The step charges the same
                // padding and border the line was measured with, applied here one edge at a time so
                // that every box lands where the extent says it should.
                step.Clear();
                Advance(open, fragment.Inline, line.ContinuesFromPrevious, box, apply: true, step);

                // An element the run leaves is never cut: a cut is what carrying on to the next line
                // is, and an element that ends in the middle of one ends for good. Its box runs to the
                // far side of its own end edge, which is the edge the run it holds stops short of - and
                // its end margin then takes its room on the line, after that box.
                foreach (var left in step.Closed)
                {
                    box += left.Ancestor.InlineEndInset;
                    CloseBox(left, box, contentBox, flow, line, cursor, context, placement);
                    box += left.Ancestor.InlineEndMargin;
                }

                foreach (var entered in step.Opened)
                {
                    // The edge the element begins at is the one Advance worked out: a box the line
                    // resumes inside was cut at its start edge before the break, and pays nothing for
                    // that edge - or for the margin that stands outside it - again here.
                    box = entered.Start;
                    OpenBox(entered, open, lineBox, flow, line);
                    box += entered.Inset;
                }

                // The spaces a `pre-wrap` line hangs are drawn where they stand but take none of the
                // room the line was given, so the box of the run that trails into them is narrowed by
                // that much: either way the glyphs land where a line without the spaces would have put
                // them, which is what a right-to-line-end hanging looks like from both directions.
                var room = fragment.Equals(last) ? fragment.Width - line.Hanging : fragment.Width;

                Place(fragment, open, lineBox, contentBox, flow, line, box, cursor, context, room, placement, _measurer);

                // A glyph hung from the end edge keeps its advance on the line's boxes - the background
                // of an inline element covers it - while the room the line was measured at already
                // leaves it out.
                origin = box + fragment.Width;
            }

            // The elements the line ends inside carry on to the next line, so each keeps its end edge
            // only if it does not: where it was cut, that edge belongs to the line the element ends on.
            for (var index = open.Count - 1; index >= 0; index--)
            {
                var left = open[index];
                var cut = !left.Ancestor.Box.Style.ClonesDecoration &&
                    line.ContinuesToNext?.Contains(left.Ancestor) == true;

                origin += cut ? 0 : left.Ancestor.InlineEndInset;
                CloseBox(left, origin, contentBox, flow, line, cursor, context, placement);

                if (!cut)
                {
                    origin += left.Ancestor.InlineEndMargin;
                }
            }

            parent.Children.Add(lineBox);
            cursor += line.Height;
        }

        // A break that clears at the content's end carries the block down to the floats it clears,
        // past the line the break stands on (CSS 2.1 §9.5.2).
        if (to >= lines.Count && start >= -0.0001 && lines.Count > 0)
        {
            cursor = Math.Max(cursor, lines[^1].Tail);
        }

        return cursor;
    }

    /// <summary>
    /// Splits a line's runs where their direction changes, so each piece of text is shaped in the
    /// direction it is read in: a Latin word inside a right-to-left line keeps its letters in the order
    /// a reader of it expects, and a neutral at the end of such a line is set at the line's own level
    /// (UAX #9 W, N, I). The pieces keep the logical order of the runs - the line places its fragments
    /// from the edge its own direction begins at - and each records the direction the algorithm gave
    /// it, which the painter turns the glyphs of a run round by where the shaper would not.
    /// </summary>
    /// <remarks>
    /// The levels are resolved over the whole line, not over one fragment at a time: what the strong
    /// text before a space says about it is a question about the line, and the whitespace at the line's
    /// end is the only whitespace the rules reset (L1). An element that asks for an embedding sets its
    /// content at a level of its own, and an override also replaces every character's class with its
    /// direction (X1-X8); the line is one string by then, so the marks that say so are written around
    /// each run's text, resolved with it, and dropped. A line that holds no change of direction is left
    /// as the markup wrote it and pays one pass over its classes.
    /// </remarks>
    private void BidiOrder(Line line, WritingModeInfo flow, ComputedStyle container)
    {
        var baseLevel = flow.IsRightToLeft ? 1 : 0;

        var changed = false;
        var ordered = new List<TextFragment>(line.Fragments.Count);
        var group = new List<TextFragment>();
        var joined = new System.Text.StringBuilder();

        void Flush()
        {
            if (group.Count == 0)
            {
                return;
            }

            var text = joined.ToString();

            // `unicode-bidi: plaintext` takes the paragraph's direction from the first strong character
            // written on the line, so a line is resolved at the level its own text asks for (P2, P3).
            var level = container.PlaintextParagraph || group[0].Style.PlaintextParagraph
                ? Bidi.ParagraphLevel(text, baseLevel)
                : baseLevel;

            // An element that asks for an embedding sets its content at a level of its own, and an
            // override also replaces every character's class with its own direction (UAX #9 X1-X8).
            // The runs were flattened into one string, so the marks that say so are written around
            // each run's own text here, read back with it, and dropped. For the container only the
            // override reaches the line: its direction is the line's base level already, so an embed
            // or an isolate embeds the line in what it already is, while an override rewrites the
            // direction of every character written on it (CSS Writing Modes 3 §5.2).
            var wrapped = new System.Text.StringBuilder(text.Length + (group.Count * 8) + 4);
            Int32[]? rentedStarts = null;
            Span<Int32> starts = group.Count <= StackScratchLimit
                ? stackalloc Int32[group.Count]
                : (rentedStarts = RentInts(group.Count));
            var lineMarks = Overrides(container) ? Controls(container) : default;

            if (lineMarks.Open is { } opening)
            {
                wrapped.Append(opening);
            }

            for (var index = 0; index < group.Count; index++)
            {
                var fragment = group[index];
                var marks = new List<(String Open, String Close)>(3);

                foreach (var style in Embeddings(fragment))
                {
                    marks.Add(Controls(style));
                }

                foreach (var (mark, _) in marks)
                {
                    wrapped.Append(mark);
                }

                starts[index] = wrapped.Length;
                wrapped.Append(fragment.Text);

                for (var at = marks.Count - 1; at >= 0; at--)
                {
                    wrapped.Append(marks[at].Close);
                }
            }

            if (lineMarks.Close is { } closing)
            {
                wrapped.Append(closing);
            }

            using var levels = Bidi.LevelsView(wrapped.ToString(), level);

            for (var index = 0; index < group.Count; index++)
            {
                var fragment = group[index];
                var own = RentInts(fragment.Length);

                try
                {
                    levels.CopyTo(starts[index], own.AsSpan(0, fragment.Length));

                    var uniform = true;

                    for (var at = 1; at < fragment.Length; at++)
                    {
                        if (own[at] != own[0])
                        {
                            uniform = false;
                            break;
                        }
                    }

                    if (uniform)
                    {
                        fragment.RightToLeft = own[0] % 2 == 1;
                        fragment.Level = own[0];
                        ordered.Add(fragment);
                        continue;
                    }

                    changed = true;
                    var runs = Bidi.Runs(fragment.Text, own.AsSpan(0, fragment.Length));
                    var pieces = new List<TextFragment>(runs.Count);
                    var earned = 0.0;

                foreach (var run in runs)
                {
                    var width = MeasureChunk(_measurer, fragment.Style, fragment.Text, run.Start, run.Length, fragment.Flow);
                    earned += width;

                    var piece = NewFragment(
                        fragment.Text, run.Start, run.Length, width,
                        fragment.Style, fragment.Box, fragment.Flow, fragment.Inline);

                    piece.RightToLeft = run.Level % 2 == 1;
                    piece.Level = run.Level;
                    piece.Trims = SliceMask(
                        fragment.Trims,
                        fragment.Start + run.Start,
                        run.Length,
                        clearBoundary: false,
                        offsetDelta: -fragment.Start);
                    piece.AutoSpaces = SliceMask(
                        fragment.AutoSpaces,
                        fragment.Start + run.Start,
                        run.Length,
                        clearBoundary: true,
                        offsetDelta: -fragment.Start);
                    piece.AutoSpaceRoom = fragment.AutoSpaceRoom;
                    pieces.Add(piece);
                }

                // The room a merge folded into the run stands between two pieces of it, and the pieces
                // are boxes of their own now: the room is the gap after the piece it follows, and the
                // mask no longer draws it (the piece was given the slice with its trailing mark cleared).
                for (var at = 1; at < runs.Count; at++)
                {
                    if (SpacedBefore(fragment, runs[at].Start))
                    {
                        var piece = pieces[at - 1];
                        piece.Width += fragment.AutoSpaceRoom;
                        earned += fragment.AutoSpaceRoom;
                    }
                }

                if (pieces.Count > 0)
                {
                    // A piece holds the places of the run it was cut from, and the room the line gave
                    // each of them: the painter spreads the share wherever the piece's own text offers
                    // a place, so a place the split lost at a boundary is handed to the piece that
                    // ends the run, where the line's own edge stands.
                    var expansion = 0.0;

                    if (fragment.Expansions > 0 && fragment.Justify != 0)
                    {
                        var given = 0;

                        foreach (var piece in pieces)
                        {
                            var room = fragment.Expansions - given;

                            if (room <= 0)
                            {
                                break;
                            }

                            var places = Math.Min(
                                room,
                                TextClusters.ExpansionPointCount(
                                    piece.Source, piece.Start, piece.Length, piece.Style.TextJustify));

                            if (places <= 0)
                            {
                                continue;
                            }

                            piece.Expand(fragment.Justify, places);
                            given += places;
                            expansion += fragment.Justify * places;
                        }

                        if (given < fragment.Expansions)
                        {
                            var places = fragment.Expansions - given;

                            pieces[^1].Expand(fragment.Justify, pieces[^1].Expansions + places);
                            expansion += fragment.Justify * places;
                        }
                    }

                    if (Math.Abs(fragment.Width - earned - expansion) > 0.0001)
                    {
                        var piece = pieces[^1];
                        piece.Width += fragment.Width - earned - expansion;
                    }

                    // A hung glyph is one character and keeps the edge of the run it was cut from:
                    // the piece that begins at the run's start, or the one that ends at its end.
                    if (fragment.HangSide == HangStart)
                    {
                        var piece = pieces[0];
                        piece.HangSide = HangStart;
                    }
                    else if (fragment.HangSide == HangEnd)
                    {
                        var piece = pieces[^1];
                        piece.HangSide = HangEnd;
                    }

                    // The space a pre-wrap line hangs is at the end of its own text, so it is the last
                    // piece that carries it: the line takes the room off that piece and the painter
                    // draws it without the characters it hangs.
                    if (fragment.Hanging > 0)
                    {
                        var piece = pieces[^1];
                        piece.Hanging = Math.Min(fragment.Hanging, piece.Length);
                    }
                }

                    foreach (var piece in pieces)
                    {
                        ordered.Add(piece);
                    }
                }
                finally
                {
                    ReturnInts(own);
                }
            }

            group.Clear();
            joined.Clear();

            if (rentedStarts is not null)
            {
                ReturnInts(rentedStarts);
            }
        }

        foreach (var fragment in line.Fragments)
        {
            if (fragment.Length == 0 || fragment.IsReplaced || fragment.Atomic is not null ||
                fragment.Ruby is not null || fragment.OutOfFlow)
            {
                if (fragment.InterlineGap || fragment.InterlineNote is not null ||
                    fragment.MarginNote is not null)
                {
                    fragment.Level = baseLevel;
                }

                Flush();
                ordered.Add(fragment);
                continue;
            }

            // `unicode-bidi: plaintext` makes an element's own text a paragraph: its direction is read
            // off the first strong character written there, not off the text around it (P2, P3), so a
            // run that asks for it is resolved apart from the runs beside it.
            if (group.Count > 0 && group[^1].Style.PlaintextParagraph != fragment.Style.PlaintextParagraph)
            {
                Flush();
            }

            group.Add(fragment);
            joined.Append(fragment.Text);
        }

        Flush();

        if (changed)
        {
            line.Fragments.Clear();
            line.Fragments.AddRange(ordered);
        }

        // A column mirrors its inline axis along the column, where the same reversal already stands:
        // what is left to order is a line's runs.
        if (!flow.IsVertical)
        {
            Reorder(line, baseLevel);
        }
    }

    /// <summary>
    /// Puts the line's runs in the order they are drawn in (UAX #9 L2). The flow lays a right-to-left
    /// line from its right edge, which is the paragraph-level reversal L2 makes - so the runs are
    /// handed over that way round, and the reversal cancels the mirror.
    /// </summary>
    /// <remarks>
    /// Only a line whose every run knows its level is put through this: a box that is not text has a
    /// level the line has not asked for, and moving the runs around it would guess.
    /// </remarks>
    private static void Reorder(Line line, Int32 baseLevel)
    {
        if (line.Fragments.Count < 2)
        {
            return;
        }

        Int32[]? rentedLevels = null;
        Span<Int32> levels = line.Fragments.Count <= StackScratchLimit
            ? stackalloc Int32[line.Fragments.Count]
            : (rentedLevels = RentInts(line.Fragments.Count));

        for (var index = 0; index < line.Fragments.Count; index++)
        {
            if (line.Fragments[index].Level < 0)
            {
                if (rentedLevels is not null)
                {
                    ReturnInts(rentedLevels);
                }

                return;
            }

            levels[index] = line.Fragments[index].Level;
        }

        var visual = Bidi.VisualOrder(levels);

        if (rentedLevels is not null)
        {
            ReturnInts(rentedLevels);
        }

        if (baseLevel == 1)
        {
            Array.Reverse(visual);
        }

        var reordered = new List<TextFragment>(visual.Length);

        foreach (var at in visual)
        {
            reordered.Add(line.Fragments[at]);
        }

        line.Fragments.Clear();
        line.Fragments.AddRange(reordered);
    }

    /// <summary>
    /// Gets the elements around one run that ask for a bidi embedding, from the outermost in. An
    /// element with no <c>unicode-bidi</c> of its own leaves its content in the embedding it
    /// inherited. The block container is not here: its own embedding wraps the whole line turn.
    /// </summary>
    private static List<ComputedStyle> Embeddings(TextFragment fragment)
    {
        var chain = new List<ComputedStyle>(3);
        var ancestors = RentAncestorList();

        for (var ancestor = fragment.Inline; ancestor is not null; ancestor = ancestor.Parent)
        {
            ancestors.Add(ancestor);
        }

        ancestors.Reverse();

        foreach (var ancestor in ancestors)
        {
            if (IsEmbedding(ancestor.Box.Style))
            {
                chain.Add(ancestor.Box.Style);
            }
        }

        ReturnAncestorList(ancestors);
        return chain;
    }

    private static Boolean IsEmbedding(ComputedStyle style) =>
        style.UnicodeBidiKeyword?.Trim().ToLowerInvariant() is
            "embed" or "bidi-override" or "isolate" or "isolate-override";

    /// <summary>
    /// Gets whether an element rewrites the direction of the characters written on it rather than
    /// only setting the level they are set at (CSS Writing Modes 3 §5.2).
    /// </summary>
    private static Boolean Overrides(ComputedStyle style) =>
        style.UnicodeBidiKeyword?.Trim().ToLowerInvariant() is "bidi-override" or "isolate-override";

    /// <summary>
    /// Gets the pair of control characters that put an element's text at the level, and in the
    /// direction, its <c>unicode-bidi</c> and <c>direction</c> ask for (UAX #9 X1-X8).
    /// </summary>
    private static (String Open, String Close) Controls(ComputedStyle style)
    {
        var rightToLeft = style.Direction == TextDirection.RightToLeft;

        return style.UnicodeBidiKeyword?.Trim().ToLowerInvariant() switch
        {
            "isolate-override" => (
                Upright(rightToLeft ? 0x2067 : 0x2066) + Upright(rightToLeft ? 0x202E : 0x202D),
                Upright(0x202C) + Upright(0x2069)),
            "isolate" => (
                Upright(rightToLeft ? 0x2067 : 0x2066),
                Upright(0x2069)),
            "bidi-override" => (
                Upright(rightToLeft ? 0x202E : 0x202D),
                Upright(0x202C)),
            _ => (
                Upright(rightToLeft ? 0x202B : 0x202A),
                Upright(0x202C)),
        };
    }

    private static String Upright(Int32 codePoint) => Char.ConvertFromUtf32(codePoint);

    /// <summary>
    /// Gets the leading every line would have cut off one of its edges, zero where the block trims no
    /// edge or the line carries nothing to measure a text edge on. A column asks for these per line:
    /// the trim reaches the first and the last line of every column, not only the block's own ends.
    /// </summary>
    private List<Double> Leadings(Box container, List<Line> lines, Boolean atStart)
    {
        var trims = new List<Double>(lines.Count);
        var trim = container.Style.TextBoxTrim;
        var applies = atStart
            ? trim is TextBoxTrim.Start or TextBoxTrim.Both
            : trim is TextBoxTrim.End or TextBoxTrim.Both;
        var edge = atStart ? container.Style.TextBoxEdgeStart : container.Style.TextBoxEdgeEnd;

        foreach (var line in lines)
        {
            trims.Add(applies && line.Fragments.Count > 0 ? Leading(line, edge, atStart) : 0);
        }

        return trims;
    }

    /// <summary>
    /// Gets the leading the block cuts away at its start and its end: what stands between the line's box
    /// edge and the line its <c>text-box-edge</c> names, for the ends the trim reaches.
    /// </summary>
    private (Double Start, Double End) Trim(Box container, List<Line> lines)
    {
        var trim = container.Style.TextBoxTrim;

        if (trim == TextBoxTrim.None)
        {
            return (0, 0);
        }

        var (first, last) = ContentEnds(lines);
        var start = first is { } firstLine && trim is TextBoxTrim.Start or TextBoxTrim.Both
            ? Leading(firstLine, container.Style.TextBoxEdgeStart, atStart: true)
            : 0;
        var end = last is { } lastLine && trim is TextBoxTrim.End or TextBoxTrim.Both
            ? Leading(lastLine, container.Style.TextBoxEdgeEnd, atStart: false)
            : 0;

        return (start, end);
    }

    /// <summary>Gets the first and the last line of the block that carry anything on them.</summary>
    private static (Line? First, Line? Last) ContentEnds(List<Line> lines)
    {
        Line? first = null;
        Line? last = null;

        foreach (var line in lines)
        {
            if (line.Fragments.Count == 0)
            {
                continue;
            }

            first ??= line;
            last = line;
        }

        return (first, last);
    }

    /// <summary>
    /// Gets the distance from the line box's edge to the line its <c>text-box-edge</c> names: the text
    /// box, a capital's top, the x-height, or the alphabetic baseline itself.
    /// </summary>
    private Double Leading(Line line, TextBoxEdge edge, Boolean atStart)
    {
        var fragment = atStart ? line.Fragments[0] : line.Fragments[^1];
        var metrics = _measurer.GetRunMetrics(fragment.Text, fragment.Style.GetFont());

        // What is trimmed is everything between the line box and the edge asked for: the room the line
        // takes beyond the text - the block's strut, or a run set taller than this one - and the part of
        // the text box the edge leaves. The room is read off the line's own sides rather than halved,
        // because a strut and a borrowed face need not stand the same distance above and below.
        var outside = atStart
            ? line.Ascent - metrics.Ascent
            : line.Height - line.Ascent - metrics.Descent;
        var inside = atStart
            ? metrics.Ascent - Edge(metrics, edge, atStart: true)
            : metrics.Descent - Edge(metrics, edge, atStart: false);

        return Math.Max(0, outside) + Math.Max(0, inside);
    }

    private static Double Edge(FontMetrics metrics, TextBoxEdge edge, Boolean atStart) => edge switch
    {
        TextBoxEdge.Cap => metrics.CapHeight,
        TextBoxEdge.Ex => metrics.XHeight,
        TextBoxEdge.Alphabetic => 0,
        TextBoxEdge.Ideographic or TextBoxEdge.IdeographicInk => atStart ? metrics.Ascent : metrics.Descent,
        _ => atStart ? metrics.Ascent : metrics.Descent,
    };

    /// <summary>
    /// Opens the box an inline element draws through on this line.
    /// </summary>
    /// <remarks>
    /// The band is the element's font content area, which is what makes it taller than the glyphs
    /// without being as tall as the line: inline padding and border overflow the line rather than
    /// widening it, so nothing here can move the lines below. An element the line is cut through draws
    /// neither the padding, the border nor the curve of the edge it was cut at.
    /// </remarks>
    private static void OpenBox(OpenInline item, List<OpenInline> open, LayoutBox lineBox, WritingModeInfo flow, Line line)
    {
        var ancestor = item.Ancestor;
        var clones = ancestor.Box.Style.ClonesDecoration;
        var cutStart = !clones && line.ContinuesFromPrevious?.Contains(ancestor) == true;
        var cutEnd = !clones && line.ContinuesToNext?.Contains(ancestor) == true;

        item.Node = new LayoutBox(ancestor.Box, LayoutKind.Inline)
        {
            Flow = flow,
            Margin = ancestor.NoteContent ? ancestor.Margin : default,
            Padding = ancestor.PaddingFor(cutStart, cutEnd),
            Border = ancestor.BorderFor(cutStart, cutEnd),
            SlicesInlineStart = cutStart,
            SlicesInlineEnd = cutEnd,

            // The painting walks the box tree rather than re-reading styles, and an inline box is built
            // here rather than arranged, so this is where it says whether it forms a context of its own -
            // which decides whether the levels written inside it are answered by the context above.
            EstablishesStackingContext = ancestor.Box.Style.EstablishesStackingContext,
            Opacity = ancestor.Box.Style.Opacity,
        };

        // A relatively placed inline keeps the place the line gave it and is drawn where its offset puts
        // it, which is what the pass that closes the box does. It carries the same answer as a block does,
        // because the layer it belongs to comes from the position and the level rather than from the kind
        // of box: an inline that states a level under `relative` is painted with the levels around it, not
        // with the lines the text stands on (CSS 2.1 §Appendix E). Only `relative` is written here: a box
        // that leaves the flow altogether is gathered by the positioning pass rather than by the line.
        var stated = ancestor.Box.Style.Position;

        if (stated.Position == PositionMode.Relative)
        {
            item.Node.Position = PositionMode.Relative;
            item.Node.ZIndex = stated.HasZIndex ? stated.ZIndex : 0;
        }

        // Everything opened before this one on the line encloses it, so the box just inside it - or the
        // line itself when this is the outermost - is where its box belongs.
        var index = open.IndexOf(item);
        var owner = index > 0 ? open[index - 1].Node! : lineBox;
        owner.Children.Add(item.Node);
    }

    /// <summary>
    /// Closes the box an inline element drew through on this line, spanning it from where the element
    /// began to where it ends.
    /// </summary>
    private static void CloseBox(
        OpenInline item,
        Double end,
        Rect contentBox,
        WritingModeInfo flow,
        Line line,
        Double cursor,
        InlineContext context,
        Placement placement)
    {
        var ancestor = item.Ancestor;

        // The band sits astride the line's shared baseline, offset by the element's own vertical
        // alignment, so a run that is raised or lowered takes the band around it with it while the
        // line's baseline stays where the rest of the line put it.
        var shift = line.BandShifts is { } bands && bands.TryGetValue(ancestor, out var aligned)
            ? aligned
            : 0;

        // The band an element draws is as tall as the font it is set in, so on a line the fitting scaled
        // it goes with the text - while the padding, border and margin around it are lengths the page
        // wrote, and those stand at the size they were written at (CSS Text 5 §1.1).
        var ascent = ancestor.Ascent * line.Scale;
        var descent = ancestor.Descent * line.Scale;
        var blockOffset = cursor + line.Ascent + shift - ascent - ancestor.BlockStartInset;
        var blockSize = ascent + descent + ancestor.BlockStartInset + ancestor.BlockEndInset;

        item.Node!.BorderBox = flow.Map(contentBox, item.Start, end - item.Start, blockOffset, blockSize);

        // Where this fragment stands is what the boxes flown out of the element will be anchored to, so
        // the fragment is written up before anything moves it (CSS Positioned Layout 3 2.1).
        placement.Fragment(ancestor, item.Node);

        // A relative element keeps the place it had in the flow - the lines and the boxes around it are
        // measured as though it never moved - and is drawn where its offset puts it. Its fragments are
        // made one per line here rather than by the pass that arranges every other kind of box, so this
        // is where each of them is moved, and what is drawn through the fragment goes with it: the text,
        // the line under it, whatever background the element asks for (CSS 2.1 §9.4.3).
        //
        // The move itself waits for the boxes held back under the element, because those are arranged
        // against the place the element had and then carried along by it - which is what an offset on an
        // ancestor does to everything inside it.
        var position = ancestor.Box.Style.Position;

        if (position.Position == PositionMode.Relative)
        {
            placement.Shift(item.Node, PositionedLayout.RelativeOffset(position, context.Containing.ContainingBlock, item.Node.Flow));
        }
    }

    /// <summary>Builds the node a run is drawn through and puts it in the innermost box open around it.</summary>
    private void Place(
        TextFragment fragment,
        List<OpenInline> open,
        LayoutBox lineBox,
        Rect contentBox,
        WritingModeInfo flow,
        Line line,
        Double origin,
        Double cursor,
        InlineContext context,
        Double room,
        Placement placement,
        ITextMeasurer measurer)
    {
        if (fragment.OutOfFlow)
        {
            // A box that left the flow is placed against its containing block rather than drawn as part of
            // the line. Where it stood on the line is its static position: the place it would have had had
            // it stayed in flow, which is what an offset left unset falls back to. The line only says
            // where that was - the box's own geometry, and whatever offsets it declares, are the
            // positioning pass's business.
            var stood = flow.Map(contentBox, origin, 0, cursor, 0);
            var against = context.Absolute ?? context.Containing;

            // Where the box hangs from is the line's own business as well: an inline-level box grows
            // from the insertion point along the way the line reads, while a block-level one keeps the
            // content's start edge whatever the line's alignment did - measured in Chrome, where
            // `text-align: center` on a 100px container puts a 20px `display: inline` absolute box at
            // 50 but a `display: block` one at 0, and a right-to-left line hangs the inline-level box
            // leftwards from its insertion point. A column's insertion point runs down the page and
            // this anchor is written along x, so only a row is answered here.
            StaticLine? hang = null;

            if (!flow.IsVertical)
            {
                var at = fragment.Box.Style.Display.StartsWith("inline", StringComparison.OrdinalIgnoreCase)
                    ? stood.X
                    : flow.InlineSign > 0 ? contentBox.X : contentBox.Right;

                hang = new StaticLine(at, at, 0, flow.InlineSign);
            }

            // An inline element the run sits inside answers for the box when it is the nearest one
            // positioned - which the line cannot settle on its own, since the rectangle is drawn over
            // every fragment of that element (CSS Positioned Layout 3 2.1).
            var anchor = AnchoredBy(fragment.Inline);

            if (anchor is null)
            {
                Attach(open, lineBox, context.Engine.ArrangePositioned(
                    fragment.Box,
                    new Offset(stood.X, stood.Y),
                    against,
                    line: hang,
                    staticFromHigh: flow.IsVertical && flow.BlockSign < 0,
                    staticFromInline: flow.IsVertical && flow.IsRightToLeft));
            }
            else
            {
                var (owner, index) = Standing(open, lineBox);
                placement.Hold(fragment.Box, anchor, owner, index, against, new Offset(stood.X, stood.Y), hang);
            }

            return;
        }

        if (fragment.Note is { } note)
        {
            PlaceNote(
                fragment,
                note,
                open,
                lineBox,
                contentBox,
                flow,
                line,
                origin,
                cursor,
                context,
                placement);

            return;
        }

        if (fragment.Atomic is { } atomic)
        {
            // The box is arranged as the block container it is - it brings its own geometry, radii and
            // children with it - and the line only decides where it goes: the margins along the line
            // are taken out of the room the line reserved, and the box's own baseline is put on the
            // line's, which is what makes its text sit with the text around it.
            var frame = flow.Map(
                contentBox,
                origin + atomic.InlineStartMargin,
                atomic.InlineSize - atomic.InlineStartMargin - atomic.InlineEndMargin,
                cursor + line.Ascent + fragment.BaselineShift - atomic.Ascent,
                atomic.BlockSize);

            // Its own content is under the same inline element, so whatever leaves the flow inside the
            // atomic box anchors to that element too - which waits for the element to be whole.
            var anchor = AnchoredBy(fragment.Inline);

            if (anchor is null)
            {
                Attach(open, lineBox, context.Engine.Arrange(fragment.Box, frame, context.Atomic));
            }
            else
            {
                var (owner, index) = Standing(open, lineBox);
                placement.Hold(fragment.Box, anchor, owner, index, context.Atomic, frame);
            }

            return;
        }

        if (fragment.IsReplaced)
        {
            // The bottom margin edge of a replaced inline rests on the baseline, so its box is lifted
            // by its own height rather than filling the line box. The band of the element around it is
            // still the font's content area, so it does not grow to cover an image taller than its text.
            Attach(open, lineBox, new LayoutBox(fragment.Box, LayoutKind.Image)
            {
                Font = FittedFont(line, fragment.Style),
                FitScale = line.Scale,
                Baseline = flow.BlockOffsetFromLeft(line.Height, line.Ascent + fragment.BaselineShift),
                Flow = fragment.Flow,
                ImageUrl = fragment.Box.SourceUrl,
                ImageFit = fragment.Style.ObjectFit,
                BorderBox = flow.Map(
                    contentBox,
                    origin,
                    fragment.Width,
                    cursor + line.Ascent - fragment.ContentHeight,
                    fragment.ContentHeight),
            });

            return;
        }

        if (fragment.Ruby is { } annotation)
        {
            // An annotation does not make the line taller: the line box is the base's, exactly as if
            // the pair were a plain inline and the reading were not there (CSS Ruby 1 §3.6). The
            // reading is laid over the base's content edge, which the shared baseline fixes, and it
            // only grows the line when it reaches past the leading the line already has (Build).
            var metrics = measurer.GetMetrics(FittedFont(line, fragment.Style));
            // The turned base is centred across the line by the drawing, so the reading that stands off
            // its edge is stood off the same middle - see `Marks` for the same exchange.
            var turned = fragment.Flow.IsVertical && !fragment.Flow.IsSideways &&
                fragment.Flow.Orientation == TextOrientation.Sideways;
            var baseline = (turned
                ? (line.Height + metrics.Ascent - metrics.Descent) / 2
                : line.Ascent) + fragment.BaselineShift;

            // The base keeps the band its own line-height asks for rather than the whole line box: a
            // line grown by the reading - or by a taller neighbour - leaves the base in the band around
            // the shared baseline, which is where its own leading puts it and where the reading, standing
            // just past its content edge, no longer covers it.
            var band = Math.Min(line.Height, LineBoxHeight(measurer, fragment.Style, FittedFont(line, fragment.Style)));
            var bandTop = baseline - metrics.Ascent - ((band - metrics.Ascent - metrics.Descent) / 2);

            // A reading that hung over the text beside its pair pushed the pair along to make room for
            // itself; otherwise the base keeps the middle of the room the two halves share.
            var standing = fragment.Hang ?? RubyAligned(
                fragment.Box.Style.RubyAlign,
                fragment.Width,
                fragment.BaseWidth);

            Attach(open, lineBox, new LayoutBox(fragment.Box, LayoutKind.Text)
            {
                Text = fragment.Text,
                Font = FittedFont(line, fragment.Style),
                FitScale = line.Scale,
                Baseline = flow.BlockOffsetFromLeft(band, baseline - bandTop),
                Flow = fragment.Flow,
                BorderBox = flow.Map(
                    contentBox,
                    origin + standing,
                    fragment.BaseWidth,
                    cursor + bandTop,
                    band),
            });

            Attach(open, lineBox, AnnotationBox(
                annotation,
                contentBox,
                flow,
                origin + standing,
                fragment.BaseWidth,
                cursor + (annotation.Under
                    ? baseline + metrics.Descent
                    : baseline - metrics.Ascent - annotation.Block),
                fragment.Box.Style.RubyAlign));

            return;
        }

        var text = fragment.Hanging > 0
            ? fragment.Source.Substring(fragment.Start, fragment.Length - fragment.Hanging)
            : fragment.Text;

        // The shaper reads a run's direction off the first character that names a script, so a run the
        // bidi algorithm set at a level the characters do not agree with - a Latin page turned around
        // by an override, or an override that keeps a right-to-left word left to right - is drawn with
        // its glyphs mirrored across the run's own room.
        var backwards = fragment.RightToLeft is { } rightToLeft
            ? rightToLeft != measurer.ReadsRightToLeft(text)
            : (Boolean?)null;

        var run = new LayoutBox(fragment.Box, LayoutKind.Text)
        {
            Text = text,
            Trims = MaskText(fragment.Trims, fragment.Start, text.Length),
            AutoSpaces = MaskText(fragment.AutoSpaces, fragment.Start, text.Length),
            AutoSpaceRoom = fragment.AutoSpaceRoom,
            Font = FittedFont(line, fragment.Style),
            FitScale = line.Scale,
            Baseline = flow.BlockOffsetFromLeft(line.Height, line.Ascent + fragment.BaselineShift),
            Flow = fragment.Flow,
            HangSide = fragment.HangSide,
            Justification = fragment.Justify,
            Justifies = fragment.Expansions,
            Reversed = backwards,
            BorderBox = flow.Map(contentBox, origin, room, cursor, line.Height),
        };

        run.Marks = Marks(
            fragment, run.Text!, flow, contentBox, origin, room, cursor, line, measurer);

        Attach(open, lineBox, run);
    }

    /// <summary>Places one two-line inline note fragment and its two ordinary text lines.</summary>
    private void PlaceNote(
        TextFragment fragment,
        NoteRun note,
        List<OpenInline> open,
        LayoutBox lineBox,
        Rect contentBox,
        WritingModeInfo flow,
        Line line,
        Double origin,
        Double cursor,
        InlineContext context,
        Placement placement)
    {
        var (marginStart, _) = flow.InlineMargins(note.Margin);
        var (paddingStart, paddingEnd) = flow.InlineMargins(note.Padding);
        var (borderStart, borderEnd) = flow.InlineMargins(note.Border);
        var (blockMarginStart, blockMarginEnd) = flow.BlockMargins(note.Margin);
        var (blockPaddingStart, blockPaddingEnd) = flow.BlockMargins(note.Padding);
        var (blockBorderStart, blockBorderEnd) = flow.BlockMargins(note.Border);
        var contentInline = Math.Max(note.FirstWidth, note.SecondWidth);
        var borderInline = contentInline + paddingStart + paddingEnd + borderStart + borderEnd;
        var firstBlock = note.First?.Height ?? note.EmptyLineHeight;
        var secondBlock = note.Second?.Height ?? note.EmptyLineHeight;
        var borderBlock = firstBlock + secondBlock + blockPaddingStart + blockPaddingEnd +
            blockBorderStart + blockBorderEnd;
        var marginBoxBlock = borderBlock + blockMarginStart + blockMarginEnd;
        var marginBoxTop = cursor + ((line.Height - marginBoxBlock) / 2);

        var outer = new LayoutBox(fragment.Box, LayoutKind.Inline)
        {
            Margin = note.Margin,
            Padding = note.Padding,
            Border = note.Border,
            Flow = flow,
            BorderBox = flow.Map(
                contentBox,
                origin + marginStart,
                borderInline,
                marginBoxTop + blockMarginStart,
                borderBlock),
        };

        Attach(open, lineBox, outer);

        void PlaceRow(Line? row, Double rowOffset)
        {
            if (row is not { } line)
            {
                return;
            }

            PlaceLines(
                fragment.Box,
                outer,
                [line],
                0,
                1,
                outer.ContentBox,
                note.Flow,
                context,
                rowOffset,
                placement);
        }

        PlaceRow(note.First, 0);
        PlaceRow(note.Second, firstBlock);
    }

    /// <summary>
    /// The marks a run of text is drawn with: one over each character the page asked to have marked,
    /// centred on that character within the room the run was given - so a line that was spread carries its
    /// marks with the spread - and set against the edge of the line its position named. Nothing here
    /// changes a size: the line keeps the height it was laid out with, which is what makes a mark paint
    /// rather than content (CSS Text Decoration 4 §5.2).
    /// </summary>
    private static List<EmphasisMark>? Marks(
        TextFragment fragment,
        String text,
        WritingModeInfo flow,
        Rect contentBox,
        Double origin,
        Double room,
        Double cursor,
        Line line,
        ITextMeasurer measurer)
    {
        var emphasis = fragment.Style.Emphasis;

        // A base carrying its own reading is not marked as well - the reading stands where the mark
        // would - and a run with no room along the line has no character to mark.
        if (!emphasis.IsSet || fragment.Ruby is not null || room <= 0 || text.Length == 0)
        {
            return null;
        }

        var font = fragment.Style.GetFont();

        // A mark is drawn at half the size of the text it marks, which is the size a reading of that base
        // comes to.
        var drawn = (font.Manager ?? FontStyleManager.Shared).GetOrAdd(
            font.Family,
            Math.Max(1, font.Size / 2),
            font.Weight,
            font.IsItalic);
        var metrics = measurer.GetMetrics(drawn);

        // A mark is set solid in a strip its own size: the glyph is lifted to the middle of that strip,
        // which is how a reading of the same size stands (CSS Ruby 1 §3.2 and the `rt` rule the
        // rendering requirements give it), and it is why a mark neither hangs below the line's headroom
        // nor reaches into the line above.
        var block = drawn.Size;
        var ascent = metrics.Ascent + ((block - (metrics.Ascent + metrics.Descent)) / 2);

        if (block <= 0)
        {
            return null;
        }

        var width = measurer.MeasureAdvance(emphasis.Mark!, drawn, flow);
        var spacing = fragment.Style.LetterSpacing;
        var words = fragment.Style.WordSpacing;
        var face = measurer.GetMetrics(font);
        // A turned run stands with its em box centred across the line it fills, and the drawing places
        // its baseline from that middle rather than from the ascent the line was measured with - which
        // a taller neighbour, or a face resolved for the glyphs rather than the one the page named,
        // moves. The marks stand off the text's own edge, so they are stood off the same middle, or a
        // mark drifts away from the letters it marks (CSS Text Decoration 4 §5.2 with Writing Modes 3
        // §5.2, which is what makes the two mirrored vertical modes read the same side of the text).
        var turned = fragment.Flow.IsVertical && !fragment.Flow.IsSideways &&
            fragment.Flow.Orientation == TextOrientation.Sideways;
        var baseline = (turned
            ? (line.Height + face.Ascent - face.Descent) / 2
            : line.Ascent) + fragment.BaselineShift;

        // A mark stands just outside the text it marks - above the content edge for a mark on the
        // ascent side, below it for one on the descent side - and takes no room in the line: it may
        // reach into the line above or below, which is the author's business to leave room for
        // (CSS Text Decoration 3 §3.3, measured against Chrome).
        var start = cursor + (emphasis.KeepsItsStripOnTheAscentSide(fragment.Flow)
            ? baseline - face.Ascent - block
            : baseline + face.Descent);
        var colour = emphasis.HasColor ? emphasis.Color : ComputedStyle.ToArgb(fragment.Style.Color);
        var elements = new List<Int32>();
        var advances = new List<Double>();
        var total = 0.0;

        for (var run = StringInfo.GetTextElementEnumerator(text); run.MoveNext(); )
        {
            var element = (String)run.Current!;
            var advance = measurer.MeasureAdvance(element, font, flow);

            // The room between one character and the next is the spacing the page wrote after it, which
            // carries the marks along with the letters - and the space a word ends on is a word space too.
            advance += spacing + (Char.IsWhiteSpace(element[0]) ? words : 0);

            // A character left unmarked still takes its room on the line, so what it leaves behind is its
            // place in the walk: the marks after it stand over the characters they mark, not where a line
            // without the character would have put them.
            advances.Add(advance);
            total += advance;

            if (!emphasis.Skips(element))
            {
                elements.Add(advances.Count - 1);
            }
        }

        if (elements.Count == 0)
        {
            return null;
        }

        // The run was measured as a whole and the line may have been spread since, so the marks follow the
        // spread rather than the plain advances. A backwards run starts at the far end of its room.
        var share = total > 0 ? room / total : 0;
        var backwards = fragment.RightToLeft ?? flow.IsRightToLeft;
        var marks = new List<EmphasisMark>(elements.Count);
        var walk = 0.0;
        var wanted = 0;

        // The walk goes over every character of the run, not only the marked ones, and charges each what
        // it takes: a stop, a bracket or a space asks for no mark of its own, but it stands in the way of
        // the letters after it, and a mark set at the count of the marked characters alone would sit that
        // many characters too far back (CSS Text Decoration 4 §5.4).
        for (var position = 0; position < advances.Count; position++)
        {
            if (wanted < elements.Count && elements[wanted] == position)
            {
                var centre = walk + (advances[position] / 2);

                marks.Add(new EmphasisMark(
                    flow.Map(
                        contentBox,
                        backwards
                            ? origin + room - (centre * share) - (width / 2)
                            : origin + (centre * share) - (width / 2),
                        width,
                        start,
                        block),
                    emphasis.Mark!,
                    flow.BlockOffsetFromLeft(block, ascent),
                    drawn,
                    colour));

                wanted++;
            }

            walk += advances[position];
        }

        return marks;
    }

    /// <summary>Adds a node inside the innermost inline element still open, or inside the line itself.</summary>
    private static void Attach(List<OpenInline> open, LayoutBox lineBox, LayoutBox node) =>
        (open.Count > 0 ? open[^1].Node! : lineBox).Children.Add(node);

    /// <summary>
    /// Gets the node a run's box belongs inside, and how many boxes already stood in it when the run came
    /// to be placed - which is where a box held back to the end of the container goes again, so that the
    /// content of the line reads in the order the line was built in.
    /// </summary>
    private static (LayoutBox Owner, Int32 Index) Standing(List<OpenInline> open, LayoutBox lineBox)
    {
        var owner = open.Count > 0 ? open[^1].Node! : lineBox;

        return (owner, owner.Children.Count);
    }

    /// <summary>
    /// Gets the innermost inline element a run sits inside that lends its descendants a frame, or null
    /// when none of them does: the nearest ancestor whose <c>position</c> is anything but static
    /// (CSS 2.1 10.3.7), or the nearest carrying a filter (CSS Filter Effects 1 2.4) - and an inline
    /// element is one of those whatever its display.
    /// </summary>
    private static InlineAncestor? AnchoredBy(InlineAncestor? chain)
    {
        for (var ancestor = chain; ancestor is not null; ancestor = ancestor.Parent)
        {
            if (PositionedLayout.LendsAFrame(ancestor.Box, inlineLevel: true))
            {
                return ancestor;
            }
        }

        return null;
    }

    /// <summary>
    /// Builds the box that draws a ruby's annotation beside its base, centred within the room the pair
    /// takes along the inline axis and placed against the block-start edge of the line.
    /// </summary>
    private static LayoutBox AnnotationBox(
        RubyAnnotation annotation,
        Rect contentBox,
        WritingModeInfo flow,
        Double origin,
        Double room,
        Double cursor,
        RubyAlign align)
    {
        return new LayoutBox(annotation.Box, LayoutKind.Text)
        {
            Text = annotation.Text,
            Font = annotation.Style.GetFont(),
            Flow = annotation.Flow,
            IsRubyAnnotation = true,
            Baseline = flow.BlockOffsetFromLeft(annotation.Block, annotation.Ascent),
            BorderBox = flow.Map(
                contentBox,
                origin + RubyAligned(align, room, annotation.Width),
                annotation.Width,
                cursor,
                annotation.Block),
        };
    }

}

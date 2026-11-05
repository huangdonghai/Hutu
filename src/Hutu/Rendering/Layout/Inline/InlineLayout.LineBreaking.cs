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
    /// <summary>
    /// Splits a text node on its explicit newlines. They survive white-space processing only for
    /// preformatted content, where each one forces a line break rather than being drawn as a glyph.
    /// </summary>
    private static void AddText(String text, ComputedStyle style, Box box, List<InlineItem> items, InlineAncestor? chain)
    {
        var start = 0;

        while (true)
        {
            var index = text.IndexOf('\n', start);

            if (index < 0)
            {
                if (start < text.Length)
                {
                    items.Add(InlineItem.CreateText(text[start..], style, box, chain));
                }

                return;
            }

            if (index > start)
            {
                items.Add(InlineItem.CreateText(text[start..index], style, box, chain));
            }

            items.Add(InlineItem.CreateBreak(style, box, chain));
            start = index + 1;
        }
    }

    private List<Line> BuildLines(
        List<InlineItem> items,
        Double maxInline,
        WritingModeInfo flow,
        InlineContext context,
        ComputedStyle strut,
        TextIndentation indent = default,
        Boolean emergency = true,
        Boolean singleLine = false,
        Boolean settleEdges = true,
        Double blockStart = 0,
        IReadOnlyList<Double>? slotExtents = null,
        IReadOnlyList<Double>? minimumInlineStarts = null,
        Int32 initialSlot = 0,
        IReadOnlyList<Line>? slotBodyLines = null)
    {
        var builder = _builders.Count > 0 ? _builders.Pop() : new LineBuilder(this);

        try
        {
            return BuildLinesCore(
                items, maxInline, flow, context, strut, indent, emergency, singleLine,
                settleEdges, blockStart, slotExtents, minimumInlineStarts, initialSlot,
                slotBodyLines, builder);
        }
        finally
        {
            _builders.Push(builder);
        }
    }

    private List<Line> BuildLinesCore(
        List<InlineItem> items,
        Double maxInline,
        WritingModeInfo flow,
        InlineContext context,
        ComputedStyle strut,
        TextIndentation indent = default,
        Boolean emergency = true,
        Boolean singleLine = false,
        Boolean settleEdges = true,
        Double blockStart = 0,
        IReadOnlyList<Double>? slotExtents = null,
        IReadOnlyList<Double>? minimumInlineStarts = null,
        Int32 initialSlot = 0,
        IReadOnlyList<Line>? slotBodyLines = null,
        LineBuilder builder = default)
    {
        var lines = new List<Line>();

        // Where the content's last drawing character stands: the white space a page trails into is
        // not drawn at the end of the line, so it does not keep the glyph in front of it from being
        // the last one the line ends at.
        var lastDrawn = LastDrawnContent(items);

        // The elements a line resumes inside, which is what the next builder needs to know so that it
        // draws no start edge for an element whose start edge is already on the line above.
        var carried = (HashSet<InlineAncestor>?)null;

        // How much room each line has, which is the block's extent for every block that has nothing
        // floated beside it and the float's far edge for the lines that have. A column is left out, as
        // it has been for floats themselves: the band a line would be shortened by runs sideways there.
        var room = new LineRoom(
            flow.IsVertical || singleLine ? null : context.Containing.Floats,
            singleLine ? Double.PositiveInfinity : maxInline,
            flow.InlineSign < 0,
            blockStart);

        var slot = initialSlot;

        Double Inset(Double indent)
        {
            var leading = indent > 0 ? Math.Max(0, indent - room.Inset) : indent;
            var minimumInlineStart = minimumInlineStarts is { } starts && slot < starts.Count
                ? starts[slot]
                : 0;

            if (minimumInlineStart > room.Inset)
            {
                leading = Math.Max(leading, minimumInlineStart - room.Inset);
            }

            return leading;
        }

        // The distance a line is set in by, measured from the edge its room begins at: a line the
        // modifiers put the distance on takes it, and every other line begins where the box does.
        Double Settled(Boolean first, Boolean afterBreak) =>
            Inset(indent.Carries(first, afterBreak) ? indent.Distance : 0);

        // An indent is measured from the content edge and a float shortens the line from that same edge,
        // so a float standing where the indent reaches leaves the line to start where the float ends, not
        // that far past it. A negative indent hangs out of the box and is never pulled back in.
        var leading = Settled(first: true, afterBreak: false);
        builder = builder.Reset(
            strut!,
            leading,
            flow.IsVertical,
            _measurer,
            startsParagraph: true,
            firstLine: settleEdges);

        // The place the line could be cut back to at a soft hyphen, and the steps it has taken since:
        // they begin the next line when the run after the hyphen turns out not to fit.
        LineBuilder.Checkpoint? breakPoint = null;
        List<TailStep>? following = null;

        // The one buffer the steps after an opportunity are kept in. Text that may break after every
        // character made a fresh opportunity at every one of them, and a fresh list each time; the
        // buffer outlives the line and the block, because a break only ever reads the newest one.
        // A nested call would clear the steps an outer line still holds, so the walk must stay a tree
        // of calls that never re-enters itself - it does: a line plan makes its lines, and the note
        // and balance passes that build their own run after it, not inside it.
        var tail = _tail;
        tail.Clear();

        // The room a stop or comma at the end of the line was left off for, which it only keeps while
        // it is the last thing on the line: the next thing put there takes that room back, and the
        // fit it is asked about is made against the line as it really is (CSS Text 3 §8.2.1).
        var pendingGrace = 0.0;

        void Charge()
        {
            if (pendingGrace > 0)
            {
                builder.Widen(pendingGrace);
                pendingGrace = 0;
            }
        }

        void Close(LineBuilder filled, Boolean forced = false, Boolean hyphen = false)
        {
            // The room a cleared break moved is taken by the line that follows it, so the line
            // before no longer reaches past itself.
            if (lines.Count > 0)
            {
                var previous = lines[^1];
                previous.Tail = 0;
            }

            var line = filled.Build(_measurer, room.Room, carried, forced, hyphen);

            line.Left = room.Inset;
            line.Extent = room.Room;
            line.GapBefore = room.Gap;
            line.ForceEnded = forced;
            line.SlotIndex = slotExtents is null ? -1 : slot;

            lines.Add(line);
            carried = line.ContinuesToNext;
            room.Next(line.Height);

            if (slotExtents is { Count: > 0 } && slot < slotExtents.Count - 1)
            {
                var extent = slotExtents[slot];

                if (slotBodyLines is { } bodyLines && slot < bodyLines.Count)
                {
                    var noteStyle = line.Governing ?? strut;
                    extent -= InterlineBodyAdjustment(bodyLines[slot], noteStyle);
                }

                room.Skip(Math.Max(0, extent));
                slot++;
            }
        }

        void NextLine(Boolean forced = false, Boolean hyphenate = false)
        {
            Close(builder, forced, hyphenate);
            builder = builder.Reset(
                strut,
                carried,
                flow.IsVertical,
                _measurer,
                startsParagraph: forced,
                indent: Settled(first: false, afterBreak: forced));
            breakPoint = null;
            following = null;
            pendingGrace = 0;
        }

        void AdvanceToSlot(Int32 target)
        {
            if (slotExtents is not { Count: > 0 } || target <= slot)
            {
                return;
            }

            if (builder.HasContent)
            {
                NextLine(forced: true);
            }
            else
            {
                builder = builder.Reset(
                    strut,
                    carried,
                    flow.IsVertical,
                    _measurer,
                    startsParagraph: false,
                    indent: Settled(first: false, afterBreak: false));
                breakPoint = null;
                following = null;
                pendingGrace = 0;
            }

            while (slot < target && slot < slotExtents.Count - 1)
            {
                room.Skip(slotExtents[slot]);
                slot++;
                builder = builder.Reset(
                    strut,
                    carried,
                    flow.IsVertical,
                    _measurer,
                    startsParagraph: false,
                    indent: Settled(first: false, afterBreak: false));
            }
        }

        void Replay(List<TailStep> steps)
        {
            foreach (var step in steps)
            {
                if (step.OutOfFlow)
                {
                    builder.AddOutOfFlow(step.Box, step.Style, step.Inline);
                }
                else if (step.Empty)
                {
                    builder.AddEmpty(step.Box, step.Style, step.Flow, step.Inline!);
                }
                else
                {
                    builder.Add(
                        step.Source, step.Start, step.Length, step.Advance,
                        step.Style, step.Box, step.Flow, step.Inline);
                }
            }
        }

        // A box that does not fit ends the line at the last opportunity before the element it belongs
        // to, when the steps taken since that opportunity are the element's own: an element that does
        // not wrap its content is one unit the line moves whole, and the box is part of it.
        Boolean HandsBack(InlineAncestor? unit) =>
            breakPoint is not null && following is { } taken &&
            taken.Exists(step => SitsWithin(step.Inline, unit));

        Boolean HandBack(InlineAncestor? unit)
        {
            if (breakPoint is not { } point || following is not { } steps ||
                !steps.Exists(step => SitsWithin(step.Inline, unit)))
            {
                return false;
            }

            builder.Restore(point);
            NextLine();
            breakPoint = null;
            following = null;
            Replay(steps);

            return true;
        }

        // Where each item begins along the line the builder is filling. A reading hanging past its pair
        // asks the room of a neighbour whose tabs are already placed, and only the line can say where.
        var begins = RentDoubles(items.Count);

        for (var index = 0; index < items.Count; index++)
        {
            var item = items[index];

            begins[index] = builder.Width;

            if (item.IsBreak)
            {
                NextLine(forced: true);

                // A break that asks to clear stands the lines after it below the floats it names;
                // this is the `<br clear>` a page still writes for the job (HTML rendering §15.3.3).
                var dropped = room.Clear(item.Style.Clear);

                // A break that clears at the end of the content carries the block down to the
                // floats it clears, even though no line follows it to stand there. A line that
                // does follow takes the room instead, and the tail is dropped where it is built.
                if (dropped > 0 && lines.Count > 0)
                {
                    var previous = lines[^1];
                    previous.Tail = Math.Max(previous.Tail, room.Top);
                }


                continue;
            }

            // Whatever comes beside the glyph the line was left short for takes that room back: the
            // glyph hangs only while it ends the line, and from here it does not.
            Charge();

            if (item.InterlineAnchor is { } anchor)
            {
                AdvanceToSlot(anchor.Slot);

                var target = Math.Max(builder.Width, anchor.Position - room.Inset);

                if (room.Room > 0)
                {
                    target = Math.Min(target, Math.Max(builder.Width, room.Room));
                }

                builder.AddInterlineGap(item.Box, item.Style, flow, target - builder.Width);
                continue;
            }

            if (item.InterlineNote is { } interlineNote)
            {
                builder.AddInterlineNote(item.Box, item.Style, flow, item.Inline, interlineNote);
                continue;
            }

            if (item.MarginNote is { } marginNote)
            {
                builder.AddMarginNote(item.Box, item.Style, flow, item.Inline, marginNote);
                continue;
            }

            // Only runs of text are kept for the line after the box the loop is about to place: a box
            // is placed where it stands and hands the line back only to the opportunity before the
            // element it belongs to, so the cut a later run looks for never stands past it.
            if (item.IsOutOfFlow)
            {
                // No room is taken and the cursor does not move, so where the cursor stands when the box
                // is met is the place it would have had. The fragment carries that much and nothing else.
                builder.AddOutOfFlow(item.Box, item.Style, item.Inline);

                if (breakPoint is not null && following is not null)
                {
                    following.Add(new TailStep(
                        String.Empty, 0, 0, 0, item.Style, item.Box, flow, item.Inline, OutOfFlow: true));
                }

                continue;
            }

            // An inline element that holds nothing still stands on the line for the margins, borders
            // and padding it carries along that axis. It takes that room where the line has come to,
            // and a line with no room left for it ends at the last opportunity before it - the box
            // stands on the line after this one, not across its own edge (CSS 2.1 §8.1, §9.4.2).
            if (item.IsEmpty)
            {
                var ancestor = item.Inline!;
                var after = builder.ExtentFor(ancestor) + ancestor.InlineEndInset + ancestor.InlineEndMargin;

                if (room.Room > 0 && after > room.Room)
                {
                    if (breakPoint is { } point && builder.HasContent)
                    {
                        var replay = following;

                        builder.Restore(point);
                        NextLine();
                        breakPoint = null;

                        if (replay is not null)
                        {
                            Replay(replay);
                        }
                    }
                    else if (builder.HasContent)
                    {
                        NextLine();
                    }
                    else if (room.Shortened)
                    {
                        room.Drop(after);
                    }
                }

                builder.AddEmpty(item.Box, item.Style, item.Box.Flow, ancestor);

                if (breakPoint is not null && following is not null)
                {
                    following.Add(new TailStep(
                        String.Empty, 0, 0, 0, item.Style, item.Box, flow, ancestor, Empty: true));
                }

                continue;
            }

            if (item.NoteSource is { Text.Length: > 0 } noteSource)
            {
                var noteText = noteSource.Text;
                var note = item.Box;
                var noteStyle = item.Style;
                var noteFlow = note.Flow;
                var noteLineHeight = LineBoxHeight(noteStyle, noteStyle.GetFont());
                var margin = noteStyle.Margin.Resolve(maxInline);
                var padding = noteStyle.Padding.Resolve(maxInline);
                var border = BlockLayout.BorderOf(note);
                var (marginStart, marginEnd) = noteFlow.InlineMargins(margin);
                var (paddingStart, paddingEnd) = noteFlow.InlineMargins(padding);
                var (borderStart, borderEnd) = noteFlow.InlineMargins(border);
                var startEdges = marginStart + paddingStart + borderStart;
                var endEdges = marginEnd + paddingEnd + borderEnd;
                var clonesDecoration = noteStyle.ClonesDecoration;
                var offset = 0;
                var firstFragment = true;
                var measured = new Dictionary<(Int32 Start, Int32 End, Double Basis, Boolean Upright), Line?>();
                var hoistedMarginNotes = new HashSet<Box>();
                var hoistedInterlineNotes = new HashSet<Box>();

                // The orientation the note's current line is being set in, which every piece of it
                // takes: a note is upright as a whole, not row by row.
                var segmentUprightNow = false;

                Line? MeasuredLine(Int32 start, Int32 end, Double basis)
                {
                    var key = (start, end, basis, segmentUprightNow);

                    if (!measured.TryGetValue(key, out var line))
                    {
                        line = NoteLine(
                            noteSource,
                            start,
                            end,
                            noteStyle,
                            noteFlow,
                            context,
                            basis,
                            segmentUprightNow);
                        measured.Add(key, line);
                    }

                    return line;
                }

                while (offset < noteText.Length)
                {
                    if (noteText[offset] == '\n')
                    {
                        if (builder.HasContent)
                        {
                            NextLine();
                        }

                        offset++;
                        continue;
                    }

                    var segmentEnd = noteText.IndexOf('\n', offset);

                    if (segmentEnd < 0)
                    {
                        segmentEnd = noteText.Length;
                    }

                    var boundaries = new List<Int32> { offset };

                    for (var at = offset; at < segmentEnd;)
                    {
                        at += TextClusters.UnitsAt(noteText, at, segmentEnd);
                        boundaries.Add(at);
                    }

                    var count = boundaries.Count - 1;
                    // Ask for the same extent Build() will account from these fragments. The running
                    // estimate includes indent and temporary punctuation grace, which would otherwise
                    // make this note choose too few characters for the room it really has.
                    var available = room.Room - builder.Indent - builder.CurrentUsed();
                    var segmentIsEnd = segmentEnd == noteText.Length;

                    Boolean AllowsBreakAt(Int32 at)
                    {
                        if (at <= offset || at >= segmentEnd)
                        {
                            return true;
                        }

                        var before = CodePointBefore(noteText, at);
                        var after = TextChunker.CodePointAt(noteText, at);
                        var beforeKind = CharUnicodeInfo.GetUnicodeCategory(before);
                        var afterKind = CharUnicodeInfo.GetUnicodeCategory(after);

                        static Boolean IsBreakSensitive(UnicodeCategory category) =>
                            category is UnicodeCategory.SpaceSeparator or
                                UnicodeCategory.LineSeparator or
                                UnicodeCategory.ParagraphSeparator or
                                UnicodeCategory.ConnectorPunctuation or
                                UnicodeCategory.DashPunctuation or
                                UnicodeCategory.OpenPunctuation or
                                UnicodeCategory.ClosePunctuation or
                                UnicodeCategory.InitialQuotePunctuation or
                                UnicodeCategory.FinalQuotePunctuation or
                                UnicodeCategory.OtherPunctuation;

                        if (IsBreakSensitive(afterKind) ||
                            beforeKind is UnicodeCategory.OpenPunctuation or
                                UnicodeCategory.InitialQuotePunctuation)
                        {
                            return false;
                        }

                        if (!IsBreakSensitive(beforeKind))
                        {
                            return true;
                        }

                        var style = noteSource.SpanAt(at).Style;
                        return LineBreaks.MayBreak(before, after, style.WordBreakingRules);
                    }

                    var advances = RentDoubles(count + 1);
                    var segmentUpright = noteFlow.IsVertical &&
                        HasUprightCharacters(noteText, offset, segmentEnd);
                    segmentUprightNow = segmentUpright;
                    var estimateFlow = segmentUpright
                        ? new WritingModeInfo(
                            noteFlow.Mode,
                            TextOrientation.Upright,
                            noteFlow.CombinesUpright,
                            noteFlow.Direction)
                        : noteFlow;

                    // Cumulative cluster advances answer both the seed and the widths a pair of
                    // candidates is judged by, so a candidate costs no line at all: only the pair the
                    // line finally takes is built.
                    for (var unit = 0; unit < count; unit++)
                    {
                        var start = boundaries[unit];
                        var end = boundaries[unit + 1];
                        var span = noteSource.SpanAt(start);
                        advances[unit + 1] = advances[unit] +
                            MeasureChunk(_measurer, span.Style, noteText, start, end - start, estimateFlow);
                    }

                    // The room a half takes, read off the cumulative advances the segment already
                    // measured: the whole segment's orientation is the note's, so a half is the same
                    // clusters in the same flow, and only the boundary rules at a cut are asked of the
                    // line (the ending grace below).
                    Double HalfWidth(Int32 from, Int32 to) =>
                        from >= to ? 0 : advances[to] - advances[from];

                    // Punctuation prohibitions can cover every boundary near the width-balanced
                    // estimate. Walk outward for legal splits instead of falling back to one line.
                    List<Int32> NearbySplits(Int32 take, Int32 center, Int32 wanted)
                    {
                        var splits = new List<Int32>(wanted);
                        var left = Math.Min(take - 1, center);
                        var right = Math.Min(take, center + 1);

                        while (splits.Count < wanted && (left >= 1 || right <= take))
                        {
                            if (left >= 1)
                            {
                                if (AllowsBreakAt(boundaries[left]))
                                {
                                    splits.Add(left);
                                }

                                left--;
                            }

                            if (splits.Count < wanted && right <= take)
                            {
                                if (AllowsBreakAt(boundaries[right]))
                                {
                                    splits.Add(right);
                                }

                                right++;
                            }
                        }

                        if (splits.Count == 0)
                        {
                            // The end boundary is always legal, even where a long unbreakable unit
                            // leaves no interior split available.
                            splits.Add(take);
                        }

                        return splits;
                    }

                    (Line? First, Line? Second) SplitAt(Int32 take, Int32 at, Double content)
                    {
                        var end = boundaries[take];
                        var split = boundaries[at];
                        var first = MeasuredLine(offset, split, content);
                        var second = MeasuredLine(split, end, content);

                        return (first, second);
                    }

                    // The two rows a note is set in: the first is never shorter than the second. Among
                    // the splits that keep it so the narrowest first row wins - the pair that is most
                    // balanced - and a split that cannot keep it only stands where none can.
                    Boolean Improves(
                        Boolean keeps, Double first, Double needed, Boolean bestKeeps,
                        Double bestFirst, Double bestNeeded, Int32 at, Int32 bestAt)
                    {
                        if (keeps != bestKeeps)
                        {
                            return keeps;
                        }

                        if (keeps)
                        {
                            if (first < bestFirst - 0.001)
                            {
                                return true;
                            }

                            if (Math.Abs(first - bestFirst) > 0.001)
                            {
                                return false;
                            }
                        }

                        if (needed < bestNeeded - 0.001)
                        {
                            return true;
                        }

                        return Math.Abs(needed - bestNeeded) <= 0.001 && at > bestAt;
                    }

                    (Int32 Split, Double Needed) Balanced(Int32 take, Boolean last)
                    {
                        var estimate = Estimate(take, last);
                        var startEdge = firstFragment || clonesDecoration ? startEdges : 0;
                        var endEdge = last || clonesDecoration ? endEdges : 0;
                        var bestAt = estimate.Split;

                        // A note that ends here ends at the line's end, where a trailing stop or comma
                        // may hang: the pair that fits is the pair the built line would report.
                        var grace = 0.0;

                        if (last && take == count)
                        {
                            var start = boundaries[take - 1];
                            var taken = boundaries[take] - start;
                            var style = noteSource.SpanAt(start).Style;
                            var endGrace = EndGrace(_measurer, style, noteText, start, taken, estimateFlow);
                            var hangGrace = HangGrace(
                                _measurer, style, noteText, start, taken, segmentEnd,
                                lastOfContent: true, estimateFlow);
                            grace = Math.Max(endGrace, hangGrace);
                        }
                        var bestKeeps = false;
                        var bestFirst = Double.PositiveInfinity;
                        var bestNeeded = Double.PositiveInfinity;

                        foreach (var at in NearbySplits(take, estimate.Split, wanted: 3))
                        {
                            var split = boundaries[at];
                            var first = HalfWidth(0, at);
                            var second = Math.Max(0, HalfWidth(at, take) - grace);
                            var needed = Math.Max(first, second) + startEdge + endEdge;
                            var keeps = first + 0.001 >= second;

                            if (Improves(
                                keeps, first, needed, bestKeeps, bestFirst, bestNeeded, at, bestAt))
                            {
                                bestKeeps = keeps;
                                bestFirst = first;
                                bestNeeded = needed;
                                bestAt = at;
                            }
                        }

                        if (Double.IsPositiveInfinity(bestNeeded))
                        {
                            bestAt = take;
                            bestNeeded = HalfWidth(0, take) + startEdge + endEdge;
                        }

                        return (bestAt, bestNeeded);
                    }

                    (Int32 Split, Double Needed) Estimate(Int32 take, Boolean last)
                    {
                        var startEdge = firstFragment || clonesDecoration ? startEdges : 0;
                        var endEdge = last || clonesDecoration ? endEdges : 0;
                        var target = advances[take] / 2;
                        var upper = Math.Max(1, take);
                        var found = Array.BinarySearch(advances, 0, take + 1, target);

                        if (found < 0)
                        {
                            found = ~found;
                        }

                        var bestMaximum = Double.PositiveInfinity;
                        var bestSplit = upper;

                        foreach (var at in NearbySplits(take, found, wanted: 6))
                        {
                            var firstWidth = advances[at];
                            var secondWidth = advances[take] - advances[at];
                            var maximum = Math.Max(firstWidth, secondWidth);

                            if (maximum < bestMaximum - 0.001 ||
                                (Math.Abs(maximum - bestMaximum) <= 0.001 && at > bestSplit))
                            {
                                bestMaximum = maximum;
                                bestSplit = at;
                            }
                        }

                        return (bestSplit, bestMaximum + startEdge + endEdge);
                    }

                    var best = 0;
                    var bestLast = false;
                    var chosen = default((Int32 Split, Double Needed));

                    if (segmentIsEnd && count > 0)
                    {
                        var whole = Estimate(count, last: true);

                        if (whole.Needed <= available)
                        {
                            best = count;
                            bestLast = true;
                        }
                    }

                    if (best == 0)
                    {
                        var low = 1;
                        var high = segmentIsEnd ? count - 1 : count;

                        while (low <= high)
                        {
                            var middle = low + ((high - low) / 2);

                            if (Estimate(middle, last: false).Needed <= available)
                            {
                                best = middle;
                                bestLast = false;
                                low = middle + 1;
                            }
                            else
                            {
                                high = middle - 1;
                            }
                        }
                    }

                    if (best > 0)
                    {
                        chosen = Balanced(best, bestLast);

                        if (chosen.Needed > available && bestLast)
                        {
                            best = segmentIsEnd ? count - 1 : count;
                            bestLast = false;
                            chosen = best > 0
                                ? Balanced(best, last: false)
                                : default;
                        }

                        while (!bestLast && best > 0 && chosen.Needed > available)
                        {
                            best--;
                            chosen = Balanced(best, last: false);
                        }

                        while (!bestLast && best > 0 && !AllowsBreakAt(boundaries[best]))
                        {
                            best--;
                            chosen = Balanced(best, last: false);
                        }
                    }

                    if (best == 0)
                    {
                        if (builder.HasContent)
                        {
                            NextLine();
                            ReturnDoubles(advances);
                            continue;
                        }

                        // A note that cannot fit even one character in an empty line overflows rather
                        // than stalling the line breaker.
                        best = 1;
                        bestLast = segmentIsEnd;
                        chosen = Balanced(best, bestLast);
                    }

                    // The cumulative-advance search is only a fast estimate. Its measured split
                    // can often fit a few more graphemes, so probe forward until the next prefix
                    // genuinely overflows.
                    var furthest = segmentIsEnd ? count - 1 : count;

                    while (!bestLast && best < furthest)
                    {
                        var next = best + 1;

                        while (next <= furthest && !AllowsBreakAt(boundaries[next]))
                        {
                            next++;
                        }

                        if (next > furthest)
                        {
                            break;
                        }

                        var wider = Balanced(next, last: false);

                        if (wider.Needed > available)
                        {
                            break;
                        }

                        best = next;
                        chosen = wider;
                    }

                    if (segmentIsEnd && !bestLast && best == count - 1)
                    {
                        var whole = Balanced(count, last: true);

                        if (whole.Needed <= available)
                        {
                            best = count;
                            bestLast = true;
                            chosen = whole;
                        }
                    }

                    var prefixEnd = boundaries[best];
                    var content = Math.Max(1, available -
                        (firstFragment || clonesDecoration ? startEdges : 0) -
                        (bestLast || clonesDecoration ? endEdges : 0));
                    var drawn = SplitAt(best, chosen.Split, content);
                    var firstLine = drawn.First;
                    var secondLine = drawn.Second;
                    var firstWidth = firstLine?.Used ?? 0;
                    var secondWidth = secondLine?.Used ?? 0;

                    var drawsNothing = firstLine is null && secondLine is null;
                    var fragmentMargin = clonesDecoration
                        ? margin
                        : SliceEdges(noteFlow, margin, firstFragment, bestLast);
                    var fragmentPadding = clonesDecoration
                        ? padding
                        : SliceEdges(noteFlow, padding, firstFragment, bestLast);
                    var fragmentBorder = clonesDecoration
                        ? border
                        : SliceEdges(noteFlow, border, firstFragment, bestLast);
                    var (blockMarginStart, blockMarginEnd) = noteFlow.BlockMargins(margin);
                    var (blockPaddingStart, blockPaddingEnd) = noteFlow.BlockMargins(padding);
                    var (blockBorderStart, blockBorderEnd) = noteFlow.BlockMargins(border);
                    var blockSize = (firstLine?.Height ?? noteLineHeight) +
                        (secondLine?.Height ?? noteLineHeight) +
                        blockMarginStart + blockMarginEnd +
                        blockPaddingStart + blockPaddingEnd +
                        blockBorderStart + blockBorderEnd;
                    var run = new NoteRun(
                        firstLine,
                        secondLine,
                        noteFlow,
                        noteLineHeight,
                        blockSize,
                        Math.Max(0, (LineBoxHeight(strut, strut.GetFont()) - strut.FontSize) / 2),
                        prefixEnd < segmentEnd,
                        fragmentMargin,
                        fragmentPadding,
                        fragmentBorder);
                    var inlineSize = Math.Max(firstWidth, secondWidth) +
                        (firstFragment || clonesDecoration ? startEdges : 0) +
                        (bestLast || clonesDecoration ? endEdges : 0);
                    var noteStart = builder.Width;
                    var noteContentStart = noteStart +
                        (firstFragment || clonesDecoration ? startEdges : 0);

                    if (!drawsNothing)
                    {
                        builder.AddNote(note, noteStyle, noteFlow, item.Inline, run, inlineSize);
                        firstFragment = false;
                    }

                    void HoistNested(Line? row)
                    {
                        if (row is not { } line)
                        {
                            return;
                        }

                        foreach (var marker in line.Fragments)
                        {
                            if (marker.InterlineNote is not { } nested)
                            {
                                if (marker.MarginNote is { } margin)
                                {
                                    if (hoistedMarginNotes.Add(margin.Box))
                                    {
                                        builder.AddMarginNote(
                                            marker.Box,
                                            marker.Style,
                                            flow,
                                            marker.Inline,
                                            margin);
                                    }
                                }

                                continue;
                            }

                            var at = InterlineAnchorPosition(line, marker, 0);
                            if (hoistedInterlineNotes.Add(nested.Box))
                            {
                                builder.AddMappedInterlineNote(
                                    marker.Box,
                                    marker.Style,
                                    noteFlow,
                                    nested,
                                    noteContentStart + at);
                            }
                        }
                    }

                    HoistNested(firstLine);
                    HoistNested(secondLine);

                    ReturnDoubles(advances);
                    offset = prefixEnd;

                    if (offset < noteText.Length)
                    {
                        if (noteText[offset] == '\n')
                        {
                            offset++;
                        }

                        if (offset < noteText.Length && !drawsNothing)
                        {
                            NextLine();
                        }
                    }
                }

                breakPoint = null;
                following = null;
                pendingGrace = 0;
                continue;
            }

            if (item.IsReplaced)
            {
                // The container's inline extent is the containing block of everything on the line, so
                // that is what a percentage among the item's constraints resolves against. A line box
                // has no definite extent across itself, so a percentage on the other axis is `auto`.
                var (width, height) = ReplacedSizing.Resolve(item.Box, flow.IsVertical ? 0 : maxInline, flow.IsVertical ? maxInline : 0);
                var inlineSize = flow.InlineExtent(width, height);
                var blockSize = flow.BlockExtent(width, height);

                var band = room.Height;
                room.Grow(blockSize);

                if (room.Room > 0 && builder.ExtentFor(item.Inline) + inlineSize > room.Room)
                {
                    if (builder.HasContent)
                    {
                        room.Restore(band);

                        if (!HandBack(item.Inline))
                        {
                            NextLine();
                        }

                        room.Grow(blockSize);
                    }
                    else if (room.Shortened)
                    {
                        // CSS 2.1 §9.5: a line box a float has shortened, too small for the content
                        // standing on it, is shifted downward until some content fits or no floats
                        // are left - the wide picture beside the floated one goes below it rather
                        // than overflowing the room the float left.
                        room.Drop(builder.ExtentFor(item.Inline) + inlineSize);
                    }
                }

                builder.AddReplaced(item.Box, inlineSize, blockSize, item.Inline);
                breakPoint = null;
                following = null;
                continue;
            }

            // An inline-block is placed as one piece on the same terms as a replaced box, but its size
            // is the layout's own to work out, which is why it is measured here rather than resolved
            // from the box's intrinsic one.
            if (item.IsAtomic)
            {
                // CSS 2.1 §10.3.9: an inline-block whose width is `auto` shrinks to fit the space its
                // line offers - the whole line, not what is left of it. Handing it the room the cursor
                // has left instead lets the box be squeezed into the last few pixels of a line it never
                // fitted on: a news item that should have moved down to the next line wraps into a
                // column a couple of characters wide and grows as tall as the block it sits in.
                var line = room.Room <= 0 ? Double.PositiveInfinity : room.Room;

                var atomic = MeasureInlineBlock(item.Box, maxInline, line, flow, context);

                // The height the box gives the line is asked of the shape before the box is put on the
                // line, because the room the line has is the room across the whole band it stands in.
                var band = room.Height;
                room.Grow(atomic.BlockSize + atomic.BlockStartMargin + atomic.BlockEndMargin);

                // An inline-block that does not fit ends the line before it where the line may end:
                // where the value its own text is written with lets the line break, or where the box
                // is part of a unit that began after that opportunity and can be handed back whole.
                // A box the line may not break beside - one written under `white-space: nowrap` and
                // belonging to no unit that can move - overflows instead (CSS Text 3 §5.1, §5.2).
                if (room.Room > 0 && builder.ExtentFor(item.Inline) + atomic.InlineSize > room.Room)
                {
                    var mayBreak = item.Style.WrapsLines || BeginsHere(builder, item.Inline);
                    var handsBack = !mayBreak && builder.HasContent && HandsBack(item.Inline);

                    if (builder.HasContent && (mayBreak || handsBack))
                    {
                        room.Restore(band);

                        if (!HandBack(item.Inline))
                        {
                            NextLine();
                        }

                        room.Grow(atomic.BlockSize + atomic.BlockStartMargin + atomic.BlockEndMargin);
                    }
                    else if (room.Shortened)
                    {
                        // CSS 2.1 §9.5: a line box a float has shortened, too small for the content
                        // standing on it, is shifted downward until some content fits or no floats
                        // are left. The box then stands on a wider line, so it is measured again
                        // against the room that line has rather than against the one it left.
                        room.Drop(builder.ExtentFor(item.Inline) + atomic.InlineSize);

                        line = room.Room <= 0 ? Double.PositiveInfinity : room.Room;
                        atomic = MeasureInlineBlock(item.Box, maxInline, line, flow, context);
                    }
                }

                builder.AddAtomic(item.Box, atomic, item.Inline);
                breakPoint = null;
                following = null;
                continue;
            }

            // A ruby is placed as one piece: breaking between a base and its annotation would leave a
            // reading stranded without the characters it belongs to.
            if (item.IsRuby)
            {
                // The pair's own flow is what its two halves are oriented by, so an inline element
                // that asks for a different orientation keeps it inside the line it sits in. The base
                // is then drawn in the orientation its own characters resolve to - a CJK base stands
                // upright under `mixed`, exactly as the same character would outside a ruby.
                var rubyFlow = item.Box.Flow;
                var baseFlow = ResolveOrientation(item.Text!, rubyFlow);
                var annotation = MeasureAnnotation(item);
                var baseWidth = PairWidth(item.Style, strut, item.Text!, builder.Width, rubyFlow);
                var (before, after) = OverhangRoom(
                    items, index, builder.Width, baseWidth, annotation.Width, begins, strut);
                var extent = RubyExtent(item, baseWidth, annotation.Width, before, after);

                var band = room.Height;
                room.Grow(LineBoxHeight(item.Style, item.Style.GetFont()));

                if (room.Room > 0 && builder.ExtentFor(item.Inline) + extent > room.Room &&
                    item.Style.WrapsLines)
                {
                    if (builder.HasContent)
                    {
                        room.Restore(band);

                        if (!HandBack(item.Inline))
                        {
                            NextLine();
                        }

                        room.Grow(LineBoxHeight(item.Style, item.Style.GetFont()));
                    }
                    else if (room.Shortened)
                    {
                        // CSS 2.1 §9.5: a line box a float has shortened, too small for the content
                        // standing on it, is shifted downward until some content fits or no floats
                        // are left.
                        room.Drop(builder.ExtentFor(item.Inline) + extent);
                    }

                    // The pair now stands where a different line left it, and the room beside it is
                    // another question: it is asked again of the place the pair holds here.
                    begins[index] = builder.Width;
                    (before, after) = OverhangRoom(
                        items, index, builder.Width, baseWidth, annotation.Width, begins, strut);
                    extent = RubyExtent(item, baseWidth, annotation.Width, before, after);
                }

                builder.AddRuby(
                    item.Text!,
                    extent,
                    item.Style,
                    item.Box,
                    baseFlow,
                    annotation,
                    baseWidth,
                    RubyHang(item, extent, baseWidth, annotation.Width, after),
                    item.Inline);
                breakPoint = null;
                following = null;
                continue;
            }

            // An inline element may ask for an orientation of its own, so each run is measured and
            // drawn in the flow of the box it came from rather than in the container's. The font is
            // taken once for the item, because every piece of it is set in that one font.
            var itemFlow = item.Flow ?? item.Box.Flow;
            var font = item.Style.GetFont();

            // A line does not break inside ruby: the base and its reading stand together, so a soft
            // hyphen written there offers no place to end and draws no mark (CSS Ruby 1 §3).
            var inRuby = InsideRuby(item.Inline);

            // The words a line carries make it as tall as the line height their own style asks for, and
            // that band is what a shape beside the line is read across.
            room.Grow(LineBoxHeight(item.Style, font));

            // A text node that follows letters is inside the word they began: its own first hyphen does
            // not open one, and the chunker is told so (UAX14 LB20a).
            var continuesWord = builder.LastCodePoint is { } prior &&
                LineBreaks.ContinuesWord(prior, item.Style.WordBreakingRules);

            var textPlan = TextPlanFor(
                item.Text!,
                item.Style,
                itemFlow,
                item.Style.WordBreakingRules,
                continuesWord,
                needWidths: !itemFlow.IsVertical);

            for (var chunkIndex = 0; chunkIndex < textPlan.Chunks.Length; chunkIndex++)
            {
                var chunk = textPlan.Chunks[chunkIndex];

                foreach (var run in new OrientedPieces(item.Text!, chunk.Start, chunk.Length, itemFlow))
                {
                    var piece = !itemFlow.IsVertical &&
                        run.Start == chunk.Start &&
                        run.Length == chunk.Length
                        ? new MeasuredRun(
                            run.Source,
                            run.Start,
                            run.Length,
                            textPlan.Widths[chunkIndex],
                            itemFlow,
                            TextChunker.IsCollapsible(run.Source, run.Start, run.Length))
                        : Measure(item.Style, font, run, itemFlow);

                    // A tab's room is the line running on to its next stop, which is a question about
                    // where the line stands rather than about the font, so it is asked here where that
                    // is known and not of the measurer.
                    var width = piece.IsWhiteSpace
                        ? WhiteSpaceWidth(item.Style, strut, run.Source, run.Start, run.Length, builder.Width, itemFlow)
                        : piece.Width;

                    var runTrim = builder.PreviewTrim(run.Source, run.Start, run.Length, item.Style, itemFlow);
                    var runAuto = builder.PreviewAutoSpace(run.Source, run.Start, run.Length, item.Style, itemFlow);

                    // White space a line begins with is not drawn, but only where the mode collapses it:
                    // `pre`, `pre-wrap` and `break-spaces` keep it, so a line that begins with one
                    // begins with it (and `break-spaces` makes it take room, which is what wraps the
                    // word after it). The node a run of text starts with may begin with the one space an
                    // element's edge collapsed into, and only the line knows whether that edge is a
                    // line's start - so the piece is dropped here rather than when the nodes were
                    // collapsed.
                    if (piece.IsWhiteSpace && !builder.HasContent && item.Style.CollapsesSpaces)
                    {
                        continue;
                    }

                    // White space never begins a line of its own: the break opportunity the other modes
                    // give it is after the run, not before it, so it stands at the end of the line it
                    // would have overflowed - hung, where the mode hangs its spaces, or taking room,
                    // where it does not - and takes the following word down with it. That is also why a
                    // `<br>` behind a space ends the line the space overflowed rather than an empty one.
                    var spaces = piece.IsWhiteSpace;

                    // A run the mode breaks inside is one that holds a character a line may end after:
                    // §4.1.3 gives `break-spaces` that opportunity after every preserved white space
                    // character *and* after every other space separator, so a piece the chunker left as
                    // "two letters and two ideographic spaces" is stepped as well.
                    var inside = item.Style.WrapsInsideSpaces;
                    var stepsSpaces = inside && TextChunker.HoldsBreakSpace(run.Source, run.Start, run.Length);

                    // The properties that tailor where a line may end put their opportunities in two different
                    // places (CSS Text 3 §5.4-§5.6): before the first character of a run of preserved white
                    // space, to keep a line from overflowing at all, and between the characters of a word.
                    // Only `word-break: break-all` leaves the first of these alone, because the rule it sets
                    // aside is the one about words.
                    // The opportunity before a preserved space is set aside by `word-break: break-all`,
                    // which is the one value that leaves the word rules standing and fills the line
                    // instead. `line-break: anywhere` sets those rules aside too, so there the line
                    // may still end before a space it would not have room for (CSS Text 3 §5.4-§5.6).
                    var beforeFirst = stepsSpaces && item.Style.WrapsBeforeSpaces &&
                        (!item.Style.BreaksBetweenLetters || item.Style.BreaksAnywhere);

                    // A word is stepped only where the break inside it is worth something: where the value
                    // gives it as an opportunity of its own, or where the word will not fit on a line at all
                    // and so no amount of moving it down would help.
                    var letters = !spaces &&
                        (item.Style.BreaksAnywhere || item.Style.BreaksBetweenLetters);
                    var stepsWord = !spaces &&
                        (letters || (emergency && item.Style.WrapsLongWords && width > room.Room));

                    var steps = (stepsSpaces || stepsWord) && room.Room > 0 &&
                        builder.ExtentFor(item.Inline) + width + runAuto.BoundaryDelta - runAuto.RetroDelta - runTrim.BoundaryDelta - runTrim.RetroDelta > room.Room &&
                        item.Style.WrapsLines;

                    var end = run.Start + run.Length;
                    var at = run.Start;
                    var previous = -1;

                    // A chunk the page may end a line after carries the mark it would draw: the hyphenate
                    // character is part of how much room the chunk needs, since a line that takes it and
                    // then breaks after it is one character longer than the chunk alone (CSS Text 3
                    // §6.2). Without this, "im-ple-" was set in a line five and a half characters wide.
                    var mark = run.Source[end - 1] == '­' && item.Style.WrapsLines
                        ? HyphenWidth(_measurer, item.Style, itemFlow)
                        : 0;

                    // A word broken only because nothing else would serve is begun on a line of its own
                    // first: the room the line had left is not filled with the end of it, which is what
                    // makes the break inside a word the last resort the property names. Where the licence
                    // stands between every two characters there is nothing to move down, and the line is
                    // filled to its end edge and cut there - but the licence to break between letters is
                    // worth nothing inside a run the rules hold together, and such a run is one unit the
                    // line has no business filling the remainder of with its head.
                    var movesWhole = !letters || !CutsInside(run.Source, run.Start, run.Length, item.Style);
                    var moved = false;

                    // `break-spaces` is the one mode whose spaces at the end of a line take room, so a word
                    // asked for the whole of itself is asked for the space that follows it too: where that
                    // space would not fit, the line ends at the opportunity before the word rather than
                    // between the word and its space - the break CSS Text 3 §5.3 does not give the mode,
                    // and the one `overflow-wrap: break-word` allows only where the line has no earlier
                    // opportunity to end at, which is not the case when a word is standing on it.
                    var trailed = item.Style.WrapsInsideSpaces && !spaces && end < run.Source.Length &&
                        TextChunker.IsBreakSpace(run.Source[end])
                            ? WhiteSpaceWidth(item.Style, strut, run.Source, end, 1, builder.Width, itemFlow)
                            : 0;

                    if (steps && stepsWord && movesWhole && builder.HasContent)
                    {
                        NextLine();
                        moved = true;
                    }

                    while (at < end)
                    {
                        // A glyph the line was left short for is only short while it ends the line.
                        Charge();

                        var taken = steps ? TextClusters.UnitsAt(run.Source, at, end) : run.Length;

                        // The room a character of the run asks for is asked of the line it stands on: a tab
                        // runs the line on to its next stop, and the stop is a question about where the line
                        // has come to rather than about the font.
                        var advance = !steps
                            ? width
                            : spaces
                                ? WhiteSpaceWidth(item.Style, strut, run.Source, at, taken, builder.Width, itemFlow)
                                : Measure(item.Style, run.Source, at, taken, itemFlow);

                        var trim = builder.PreviewTrim(run.Source, at, taken, item.Style, itemFlow);
                        var auto = builder.PreviewAutoSpace(run.Source, at, taken, item.Style, itemFlow);

                        // Where the break may be taken. The mode's own licence stands after a space it keeps,
                        // whichever piece that space was written in; the tailorable ones stand inside a word
                        // they were asked about, and for a word the script breaks between letters only between
                        // two of them - the punctuation keeps the rules it had, as does the character that
                        // opened the piece, whose boundary the chunking already answered.
                        var breaks = at == run.Start
                            ? spaces
                                ? beforeFirst
                                : !moved && builder.CanBreakBefore(run.Source, run.Start, item.Style)
                            : (inside && TextChunker.IsBreakSpace(run.Source[previous])) ||
                                (stepsWord &&
                                    (!item.Style.BreaksBetweenLetters ||
                                        item.Style.BreaksAnywhere ||
                                        item.Style.CountsLongWordBreaks ||
                                        LineBreaks.MayBreak(run.Source, previous, at, item.Style.WordBreakingRules)));

                        // A run that opens with content asks for the room the whole of it needs before it is
                        // stepped: the opportunity inside it is what the line falls back on once the run
                        // stands on a line of its own and still does not fit, not a licence to fill the last
                        // of the line above with the end of it. A run that opens with the space itself is the
                        // mode's own case - every character of it takes room where it stands. Where the value
                        // gives the break as an opportunity of its own, the line is filled to its end edge.
                        var asks = (at == run.Start && !spaces && !letters &&
                            !TextChunker.IsBreakSpace(run.Source[run.Start])
                            ? AskWidth(item, strut, run.Source, run.Start, run.Length, width, trailed, mark,
                                builder.Width, stepsSpaces, itemFlow)
                            : advance) - trim.BoundaryDelta + auto.BoundaryDelta - auto.RetroDelta;

                        // A closing punctuation that ends the line may give up its trailing half. The
                        // breaker keeps it on the line where that half is what fails to fit; the line
                        // close takes the room off for real.
                        var endGrace = EndGrace(
                            _measurer, item.Style, run.Source, at, taken, itemFlow);

                        // A line whose last character is a stop or comma may let that character hang
                        // rather than break the line before it, which is the room `allow-end` gives
                        // (CSS Text 3 §8.2.1).
                        var lastOfContent = index == lastDrawn.Item && at + taken == lastDrawn.End;

                        // A border or padding of the element the run sits in stands between the glyph
                        // and the line's end edge only where that element ends on this line: an
                        // element the line cuts draws its end edge on a later line. White space the
                        // line trails into is not drawn, so it does not put the glyph before it back
                        // inside the element.
                        var blocked = OnlySpaceRemains(item.Text!, at + taken) &&
                            EndEdgeOnLine(items, index, item.Inline);

                        var hangGrace = blocked
                            ? 0
                            : HangGrace(
                                _measurer, item.Style, run.Source, at, taken, end, lastOfContent, itemFlow);

                        // The margin the element the run sits in takes after its last words stands at
                        // the end of the line the run ends: the line asks for it as it asks for the
                        // words, and a negative one leaves the line that much shorter (CSS 2.1 §9.4.2).
                        var closing = 0.0;

                        if (at + taken == end && end == item.Text!.Length && item.Inline is { } owner &&
                            (index + 1 >= items.Count || !SitsWithin(items[index + 1].Inline, owner)))
                        {
                            closing = owner.InlineEndMargin;
                        }

                        // A soft hyphen is the one opportunity a line takes even when it has no room at
                        // all: the word has nowhere else to go, and the mark is drawn where the page put
                        // the hyphen (CSS Text 3 §6.1). Chrome measured on a zero-width box does the same.
                        var hyphen = builder.EndsInSoftHyphen && item.Style.WrapsLines && !inRuby;

                        // `auto-phrase` gives hyphenation up rather than overflow: the rest of the word is
                        // measured against the room the line has, and a word that fits runs on with its
                        // soft hyphens drawn as nothing. Only a word that cannot fit keeps them (CSS Text
                        // 4 §5.6, measured by word-break-auto-phrase-006 and -008).
                        if (hyphen && item.Style.WordBreak.Equals("auto-phrase", StringComparison.OrdinalIgnoreCase))
                        {
                            var wordEnd = at;

                            while (wordEnd < run.Source.Length && !TextChunker.IsCollapsible(run.Source[wordEnd]))
                            {
                                wordEnd += TextClusters.UnitsAt(run.Source, wordEnd, run.Source.Length);
                            }

                            var rest = Measure(item.Style, run.Source, at, wordEnd - at, itemFlow);

                            if (builder.ExtentFor(item.Inline) + rest <= room.Room)
                            {
                                hyphen = false;
                                breaks = false;
                                breakPoint = null;
                                following = null;
                            }
                        }

                        // The line may end at the boundary this step begins at: the state it stands in
                        // before the step is kept, and the steps taken after it are kept beside it, so a
                        // line forced to end earlier can back up to the last place it was allowed to.
                        if (breaks && builder.HasContent && !inRuby)
                        {
                            breakPoint = builder.Capture();
                            tail.Clear();
                            following = tail;
                        }

                        // A line that ran past its last opportunity may have to give back what came
                        // after it: this step cannot fit where it stands and nothing here lets the line
                        // end, so the line ends at the opportunity it last passed and the steps since it
                        // begin the next line. Chrome measured: "X XX " in a four-character box ends
                        // before the space rather than overflowing the line, and "high-shy-wa" then "y"
                        // breaks at the hyphen rather than letting the y overflow the word it completes
                        // (CSS Text 3 §5.1 - a line ends at an opportunity, not where the room runs out).
                        // A collapsible space is not given back: it hangs at the end of the line instead.
                        // A preserved space is worth an earlier line only when something else follows it:
                        // a space at the very end of the text overflows with the word it trails, because
                        // moving it down would open a line nobody asked for.
                        var followed = at + taken < run.Source.Length &&
                            !TextChunker.IsCollapsible(run.Source, at + taken, 1);

                        if (breakPoint is { } point && !breaks && builder.HasContent && !inRuby &&
                            advance > 0 && !String.IsNullOrEmpty(item.Text) &&
                            (item.Style.WrapsLines || BeginsHere(builder, item.Inline)) &&
                            (!spaces || (item.Style.WrapsInsideSpaces && followed)) &&
                            builder.ExtentFor(item.Inline) + advance + closing + auto.BoundaryDelta - auto.RetroDelta - trim.BoundaryDelta - trim.RetroDelta - Math.Max(endGrace, hangGrace) > room.Room)
                        {
                            var replay = following;

                            builder.Restore(point);
                            NextLine(hyphenate: builder.EndsInSoftHyphen);
                            breakPoint = null;

                            if (replay is not null)
                            {
                                Replay(replay);
                            }
                        }

                        if (breaks && (room.Room > 0 || hyphen) &&
                            builder.ExtentFor(item.Inline) + asks + closing - trim.RetroDelta - Math.Max(endGrace, hangGrace) > room.Room &&
                            item.Style.WrapsLines)
                        {
                            // The mark a break draws is part of the line it ends: a shortened line that
                            // cannot hold it moves down first, the way any line too small for its content
                            // does (CSS 2.1 §9.5). Without this the line broke at the soft hyphen and
                            // overhung the float by the width of the mark.
                            if (hyphen && room.Shortened)
                            {
                                var trailing = builder.ExtentFor(item.Inline) +
                                    (builder.EndsInSoftHyphen ? HyphenWidth(_measurer, item.Style, itemFlow) : 0);

                                if (trailing > room.Room)
                                {
                                    room.Drop(trailing);
                                }
                            }

                            if (builder.ExtentFor(item.Inline) + asks + closing - trim.RetroDelta - Math.Max(endGrace, hangGrace) > room.Room)
                            {
                                if (builder.HasContent)
                                {
                                    NextLine(hyphenate: hyphen);
                                    moved = false;
                                }
                                else if (room.Shortened)
                                {
                                    // CSS 2.1 §9.5: a line box a float has shortened, too small for the
                                    // content standing on it, is shifted downward until some content fits
                                    // or no floats are left - the long word beside the floated picture goes
                                    // below it rather than overflowing the room the float left, which is
                                    // the specification's own example of the rule.
                                    room.Drop(builder.ExtentFor(item.Inline) + asks - trim.RetroDelta - Math.Max(endGrace, hangGrace));
                                }
                            }
                        }

                        builder.Add(run.Source, at, taken, advance, item.Style, item.Box, piece.Flow, item.Inline);

                        // The stop or comma the line was measured without is only without it while it
                        // ends the line; the next thing put there is asked for its room (CSS Text 3
                        // §8.2.1).
                        if (hangGrace > 0)
                        {
                            pendingGrace = hangGrace;
                        }

                        // The steps taken after the last opportunity are kept beside it, so the line
                        // after the break can begin with them.
                        if (breakPoint is not null && following is not null)
                        {
                            following.Add(new TailStep(
                                run.Source, at, taken, advance, item.Style, item.Box, piece.Flow, item.Inline));
                        }

                        previous = at;
                        at += taken;
                    }
                }
            }
        }

        // Closed for any fragment rather than for any content: a line whose only fragment is a box that
        // left the flow draws nothing, but that box still has to be placed.
        if (builder.HasFragments)
        {
            Close(builder);
        }

        SettleNotesAtLineEnds(lines);

        // Which inline elements the lines hand on is settled before the edges are: an element that
        // ends at a line's end is not one the line cut, and the glyph standing there is the last of
        // its element's last formatted line.
        MarkSlices(lines, maxInline);

        if (settleEdges)
        {
            SettleLines(lines);
        }

        ReturnDoubles(begins);
        return lines;
    }

}

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
    /// Turns the zero-width interline-note anchors left on body lines into one note stream whose lines
    /// share the body's interlinear slots. A note line that fills up continues in the slot of the next
    /// body line rather than adding another line in front of the same one.
    /// </summary>
    private List<Line> AddInterlineNotes(
        Box container,
        List<Line> bodyLines,
        Double maxInline,
        WritingModeInfo flow,
        InlineContext context)
    {
        var anchors = new List<(Int32 Slot, InterlineNoteSource Note, Double Position)>();
        var slotTops = RentDoubles(bodyLines.Count);
        var slotExtents = RentDoubles(bodyLines.Count);
        var minimumStarts = RentDoubles(bodyLines.Count);
        var top = 0.0;

        for (var bodyIndex = 0; bodyIndex < bodyLines.Count; bodyIndex++)
        {
            var bodyLine = bodyLines[bodyIndex];
            top += bodyLine.GapBefore;
            slotTops[bodyIndex] = top;
            slotExtents[bodyIndex] = bodyLine.GapBefore + bodyLine.Height;
            minimumStarts[bodyIndex] = bodyIndex == 0 ? bodyLine.Indent : 0;

            var markers = bodyLine.Fragments
                .Where(fragment => fragment.InterlineNote is not null)
                .ToList();

            if (markers.Count > 0)
            {
                var bodyAlignment = BodyLineAlignment(container, bodyLine, bodyIndex, bodyLines.Count);

                if (bodyAlignment.Equals("justify", StringComparison.OrdinalIgnoreCase))
                {
                    bodyLine.Alignment = bodyAlignment;
                    DistributeSpace([bodyLine]);
                }

                var bodyOffset = BodyLineInlineOffset(container, bodyLine, bodyIndex, bodyLines.Count);

                foreach (var marker in markers)
                {
                    anchors.Add((
                        bodyIndex,
                        marker.InterlineNote!,
                        InterlineAnchorPosition(bodyLine, marker, bodyOffset)));
                }

                bodyLine.Fragments.RemoveAll(fragment => fragment.InterlineNote is not null);
            }

            top += bodyLine.Height;
        }

        if (anchors.Count == 0)
        {
            ReturnDoubles(slotTops);
            ReturnDoubles(slotExtents);
            ReturnDoubles(minimumStarts);
            return bodyLines;
        }

        var noteLines = BuildInterlineFlow(
            container,
            anchors,
            maxInline,
            flow,
            context,
            new ArraySegment<Double>(slotTops, 0, bodyLines.Count),
            new ArraySegment<Double>(slotExtents, 0, bodyLines.Count),
            new ArraySegment<Double>(minimumStarts, 0, bodyLines.Count),
            anchors[0].Slot,
            bodyLines);
        ReturnDoubles(slotTops);
        ReturnDoubles(slotExtents);
        ReturnDoubles(minimumStarts);
        var bySlot = new Dictionary<Int32, List<Line>>();

        foreach (var noteLine in noteLines)
        {
            var slot = Math.Clamp(noteLine.SlotIndex, 0, bodyLines.Count - 1);
            noteLine.SlotIndex = slot;

            if (!bySlot.TryGetValue(slot, out var slotLines))
            {
                bySlot.Add(slot, slotLines = new List<Line>());
            }

            slotLines.Add(noteLine);
        }

        var lines = new List<Line>(bodyLines.Count);

        for (var bodyIndex = 0; bodyIndex < bodyLines.Count; bodyIndex++)
        {
            var bodyLine = bodyLines[bodyIndex];
            if (bySlot.TryGetValue(bodyIndex, out var notes) && notes.Count > 0)
            {
                foreach (var noteLine in notes)
                {
                    foreach (var marker in noteLine.Fragments
                        .Where(fragment => fragment.MarginNote is not null)
                        .ToList())
                    {
                        bodyLine.Fragments.Add(marker);
                        noteLine.Fragments.Remove(marker);
                    }

                    lines.Add(noteLine);
                }

                var lastNote = notes[^1];
                lastNote.KeepWithNext = true;
                bodyLine.GapBefore -= InterlineBodyAdjustment(container, bodyLine, notes[^1]);
            }

            lines.Add(bodyLine);
        }

        return lines;
    }

    /// <summary>Gets the inline coordinate at which an interline-note anchor stands on its body line.</summary>
    private Double InterlineAnchorPosition(Line line, TextFragment marker, Double alignment)
    {
        if (marker.InterlinePosition is { } positioned)
        {
            return line.Left + alignment + positioned;
        }

        var open = new List<OpenInline>();
        var at = line.Indent;

        foreach (var fragment in line.Fragments)
        {
            at += Advance(open, fragment.Inline, line.ContinuesFromPrevious, at, apply: true, step: null);

            if (fragment.Equals(marker))
            {
                return line.Left + alignment + at;
            }

            at += fragment.Width;
        }

        return line.Left + alignment + at;
    }

    /// <summary>Gets the inline offset the body line's own alignment gives it.</summary>
    private static Double BodyLineInlineOffset(
        Box container,
        Line line,
        Int32 index,
        Int32 count)
    {
        var alignment = BodyLineAlignment(container, line, index, count);

        var fragmentFlow = line.Fragments.Count > 0
            ? line.Fragments[0].Flow
            : container.Flow;

        return AlignmentOffset(
            alignment,
            Math.Max(0, line.Extent - line.Indent),
            line.Used,
            fragmentFlow);
    }

    private static String BodyLineAlignment(Box container, Line line, Int32 index, Int32 count)
    {
        var style = line.Governing ?? container.Style;
        var last = index == count - 1 || line.ForceEnded;

        return last
            ? (style.TextAlignLast.Equals("auto", StringComparison.OrdinalIgnoreCase)
                ? (style.TextAlign.Equals("justify", StringComparison.OrdinalIgnoreCase)
                    ? "start"
                    : style.TextAlign)
                : style.TextAlignLast)
            : style.TextAlign;
    }

    private static ComputedStyle FirstTextStyle(List<TextFragment> fragments, ComputedStyle fallback)
    {
        foreach (var fragment in fragments)
        {
            if (fragment.Length > 0)
            {
                return fragment.Style;
            }
        }

        return fallback;
    }

    /// <summary>
    /// Gets the leading the note and body lines share at their boundary, which is removed from the
    /// ordinary gap between their two line boxes so the note stands nearer to the body it annotates.
    /// </summary>
    private Double InterlineBodyAdjustment(Box container, Line bodyLine, Line noteLine)
    {
        var noteStyle = noteLine.Governing;

        if (noteStyle is null)
        {
            for (var index = noteLine.Fragments.Count - 1; index >= 0; index--)
            {
                var fragment = noteLine.Fragments[index];

                if (!fragment.InterlineGap)
                {
                    noteStyle = fragment.Style;
                    break;
                }
            }
        }

        noteStyle ??= container.Style;

        return InterlineBodyAdjustment(bodyLine, noteStyle.Value);
    }

    /// <summary>
    /// Gets how far the body line moves toward its interline note. Without an inline note this is the
    /// smaller of the two half-leadings; with one, the effective body boundary is the first subline's
    /// block-start edge and that boundary is overlapped by the two half-leadings' smaller value.
    /// </summary>
    private Double InterlineBodyAdjustment(Line bodyLine, ComputedStyle noteStyle)
    {
        var noteGap = HalfLeading(noteStyle);
        var inline = InlineNoteFirstAdjustment(bodyLine, noteGap);

        if (inline is { } adjustment)
        {
            return adjustment;
        }

        var bodyStyle = bodyLine.Governing ??
            (bodyLine.Fragments.Count > 0 ? bodyLine.Fragments[0].Style : noteStyle);
        return Math.Min(HalfLeading(bodyStyle), noteGap);
    }

    /// <summary>
    /// Gets the line movement needed to put an interline note against the nearest inline-note first
    /// subline, including the centring and block decorations between the body edge and that subline.
    /// </summary>
    private Double? InlineNoteFirstAdjustment(Line bodyLine, Double noteGap)
    {
        Double? adjustment = null;

        foreach (var fragment in bodyLine.Fragments)
        {
            if (fragment.Note is not { First: { } first } note)
            {
                continue;
            }

            var style = first.Governing ?? FirstTextStyle(first.Fragments, fragment.Style);
            var (blockMarginStart, _) = note.Flow.BlockMargins(note.Margin);
            var (blockPaddingStart, _) = note.Flow.BlockMargins(note.Padding);
            var (blockBorderStart, _) = note.Flow.BlockMargins(note.Border);
            var boundary =
                ((bodyLine.Height - note.BlockSize) / 2) +
                blockMarginStart +
                blockBorderStart +
                blockPaddingStart;
            var overlap = Math.Min(HalfLeading(style), noteGap);
            var current = boundary + overlap;

            adjustment = adjustment is { } existing ? Math.Min(existing, current) : current;
        }

        return adjustment;
    }

    /// <summary>Gets the space a line's leading adds above or below its glyphs.</summary>
    private Double HalfLeading(ComputedStyle style)
    {
        var font = style.GetFont();
        return Math.Max(0, (LineBoxHeight(style, font) - style.FontSize) / 2);
    }

    /// <summary>
    /// Builds the shared note flow in DOM order. Each line fills the interlinear slot of one body line;
    /// when it fills up, the remaining text continues in the next slot rather than adding a second note
    /// line in front of the same body line.
    /// </summary>
    private List<Line> BuildInterlineFlow(
        Box container,
        List<(Int32 Slot, InterlineNoteSource Note, Double Position)> anchors,
        Double maxInline,
        WritingModeInfo flow,
        InlineContext context,
        IReadOnlyList<Double> slotTops,
        IReadOnlyList<Double> slotExtents,
        IReadOnlyList<Double> minimumInlineStarts,
        Int32 firstSlot,
        IReadOnlyList<Line> slotBodyLines)
    {
        var items = RentItems();
        ComputedStyle? strut = null;

        foreach (var (slot, note, position) in anchors)
        {
            var root = new InlineAncestor(note.Box, null, maxInline, flow, _measurer, noteContent: true);
            var noteItems = NoteItems(
                note.Content,
                0,
                note.Content.Text.Length,
                maxInline,
                flow,
                upright: false,
                root: root);

            items.Add(InlineItem.CreateInterlineAnchor(slot, position, note.Box));

            foreach (var item in noteItems)
            {
                items.Add(item);
            }

            ReturnItems(noteItems);
            strut ??= note.Box.Style;
        }

        if (items.Count == 0 || strut is null)
        {
            ReturnItems(items);
            return new List<Line>();
        }

        var lineStrut = strut ?? container.Style;
        var noteLines = BuildLines(
            items,
            maxInline,
            flow,
            context,
            lineStrut,
            emergency: false,
            singleLine: false,
            settleEdges: true,
            blockStart: slotTops[firstSlot],
            slotExtents: slotExtents,
            minimumInlineStarts: minimumInlineStarts,
            initialSlot: firstSlot,
            slotBodyLines: slotBodyLines);
        var justify = container.Style.TextAlign.Equals("justify", StringComparison.OrdinalIgnoreCase);

        for (var index = 0; index < noteLines.Count; index++)
        {
            var line = noteLines[index];
            line.IsInterlineNote = true;
            line.Alignment = justify && index < noteLines.Count - 1 && !line.ForceEnded
                ? "justify"
                : "start";
            line.Offset = Aligned(line, maxInline);
        }

        ReturnItems(items);
        return noteLines;
    }

    /// <summary>
    /// Settles the sublines of notes that are the last in-flow content of an outer line. A note in
    /// the middle of a line keeps its natural width: justification and hanging punctuation describe a
    /// line edge, and the note has not reached one there.
    /// </summary>
    private void SettleNotesAtLineEnds(List<Line> lines)
    {
        for (var lineIndex = 0; lineIndex < lines.Count; lineIndex++)
        {
            var line = lines[lineIndex];
            TextFragment? last = null;
            var hasVisible = false;
            var allNotes = true;

            for (var index = line.Fragments.Count - 1; index >= 0; index--)
            {
                var fragment = line.Fragments[index];

                if (fragment.OutOfFlow)
                {
                    continue;
                }

                if (!HasInk(fragment))
                {
                    continue;
                }

                hasVisible = true;
                allNotes &= fragment.Note is not null;
                last ??= fragment;
            }

            if (last?.Note is not { } note)
            {
                continue;
            }

            var lastFragment = last.Value;

            // A line whose visible content is entirely notes is a note layout line even when its
            // naturally ending fragment leaves some trailing room. That trailing room is outside
            // the note, just as the remaining part of a line box is outside a short final line.
            var fullNoteLine = note.ReachedLineEnd || (hasVisible && allNotes);
            note.EndsOuterLine = fullNoteLine;

            var rows = new List<Line>(2);

            if (note.First is { } first)
            {
                rows.Add(first);
            }

            if (note.Second is { } second)
            {
                rows.Add(second);
            }

            foreach (var row in rows)
            {
                SettleLine(
                    row,
                    firstLine: true,
                    lastLine: fullNoteLine || lineIndex == lines.Count - 1,
                    forceLineEnd: fullNoteLine);
            }

            var naturalContent = rows.Count == 0 ? 0 : rows.Max(row => row.Used);
            var alignLast = lastFragment.Style.TextAlignLast;
            var lastSpokenFor = lineIndex == lines.Count - 1 || line.ForceEnded;
            var justified = alignLast.Equals("justify", StringComparison.OrdinalIgnoreCase) ||
                (!lastSpokenFor &&
                    lastFragment.Style.TextAlign.Equals("justify", StringComparison.OrdinalIgnoreCase));

            foreach (var row in rows)
            {
                row.Left = 0;
                row.Indent = 0;
                row.Extent = naturalContent;
                row.Offset = 0;

                if (justified)
                {
                    row.Alignment = "justify";
                }
            }

            var grown = naturalContent + note.InlineEdges;
            line.Used += grown - lastFragment.Width;
            lastFragment.Width = grown;
            line.Offset = Aligned(line, line.Extent);
        }
    }

    private sealed record InterlineNoteSource(NoteSource Content, Box Box);

    private readonly record struct InterlineFlowAnchor(Int32 Slot, Double Position);

    /// <summary>The styled text an inline note holds, kept as spans so nested markup survives.</summary>
    private sealed class NoteSource
    {
        private Int32 _lastSpan = -1;

        private NoteSource(
            String text,
            IReadOnlyList<NoteSpan> spans,
            IReadOnlyList<NestedInterlineNote> nestedInterline,
            IReadOnlyList<NestedMarginNote> nestedMargin)
        {
            Text = text;
            Spans = spans;
            NestedInterline = nestedInterline;
            NestedMargin = nestedMargin;
        }

        public String Text { get; }

        public IReadOnlyList<NoteSpan> Spans { get; }

        /// <summary>Gets the interline notes hoisted out of this inline note's text.</summary>
        public IReadOnlyList<NestedInterlineNote> NestedInterline { get; }

        /// <summary>Gets the margin notes hoisted out of this note's text.</summary>
        public IReadOnlyList<NestedMarginNote> NestedMargin { get; }

        /// <summary>Gets the styled span holding the character at the text position.</summary>
        public NoteSpan SpanAt(Int32 position)
        {
            var index = _lastSpan;

            if ((UInt32)index < (UInt32)Spans.Count)
            {
                var span = Spans[index];

                while (position >= span.Start + span.Length && index + 1 < Spans.Count)
                {
                    span = Spans[++index];
                }

                if (position >= span.Start && position < span.Start + span.Length)
                {
                    _lastSpan = index;
                    return span;
                }
            }

            var low = 0;
            var high = Spans.Count - 1;

            while (low <= high)
            {
                var middle = low + ((high - low) / 2);
                var span = Spans[middle];

                if (position < span.Start)
                {
                    high = middle - 1;
                }
                else if (position >= span.Start + span.Length)
                {
                    low = middle + 1;
                }
                else
                {
                    _lastSpan = middle;
                    return span;
                }
            }

            throw new InvalidOperationException("A note's text position must belong to one of its spans.");
        }

        /// <summary>
        /// Gets whether the note still has something to draw after its text nodes' white-space rules
        /// collapse their spaces. A note made only of ordinary spaces is the same as no note at all.
        /// </summary>
        public Boolean HasInk
        {
            get
            {
                foreach (var span in Spans)
                {
                    for (var at = 0; at < span.Length; at++)
                    {
                        if (!span.Style.CollapsesSpaces ||
                            !TextChunker.IsCollapsible(Text, span.Start + at, 1))
                        {
                            return true;
                        }
                    }
                }

                return false;
            }
        }

        public static NoteSource Read(
            Box note,
            Boolean hoistInterline = false,
            Boolean hoistMargin = false)
        {
            var text = new System.Text.StringBuilder();
            var spans = new List<NoteSpan>();
            var nested = new List<NestedInterlineNote>();
            var nestedMargin = new List<NestedMarginNote>();
            Append(note, text, spans, nested, nestedMargin, new List<Box>(), hoistInterline, hoistMargin);

            return new NoteSource(text.ToString(), spans, nested, nestedMargin);
        }

        private static void Append(
            Box box,
            System.Text.StringBuilder text,
            List<NoteSpan> spans,
            List<NestedInterlineNote> nested,
            List<NestedMarginNote> nestedMargin,
            List<Box> ancestors,
            Boolean hoistInterline,
            Boolean hoistMargin)
        {
            if (box.Kind == BoxKind.Text)
            {
                if (box.Text is { Length: > 0 } run)
                {
                    // Segment breaks in the source are ordinary collapsible spaces unless this
                    // element's white-space mode preserves them. Keeping them as line feeds here
                    // would turn the pretty-printing of the document into hard note breaks.
                    if (box.Style.CollapsesSpaces &&
                        !box.Style.KeepsSegmentBreaks &&
                        (run.Contains('\r') || run.Contains('\n')))
                    {
                        run = run.Replace('\r', ' ').Replace('\n', ' ');
                    }

                    spans.Add(new NoteSpan(
                        text.Length,
                        run.Length,
                        box.Style,
                        box,
                        box.Flow,
                        ancestors.ToArray()));
                    text.Append(run);
                }

                return;
            }

            if (box.Kind == BoxKind.LineBreak)
            {
                text.Append('\n');
                return;
            }

            if (hoistInterline &&
                box.Kind == BoxKind.Inline &&
                box.Style.NotePlacement == NotePlacement.Interline)
            {
                var content = Read(box, hoistMargin: true);
                nested.Add(new NestedInterlineNote(
                    text.Length,
                    new InterlineNoteSource(content, box),
                    ancestors.ToArray()));
                return;
            }

            if (hoistMargin &&
                box.Kind == BoxKind.Inline &&
                box.Style.NotePlacement == NotePlacement.Margin)
            {
                nestedMargin.Add(new NestedMarginNote(
                    text.Length,
                    new MarginNoteSource(box),
                    ancestors.ToArray()));
                return;
            }

            foreach (var child in box.Children)
            {
                var isNested = child.Kind == BoxKind.Inline;

                if (isNested)
                {
                    ancestors.Add(child);
                }

                Append(
                    child,
                    text,
                    spans,
                    nested,
                    nestedMargin,
                    ancestors,
                    hoistInterline,
                    hoistMargin);

                if (isNested)
                {
                    ancestors.RemoveAt(ancestors.Count - 1);
                }
            }
        }
    }

    private readonly record struct NoteSpan(
        Int32 Start,
        Int32 Length,
        ComputedStyle Style,
        Box Box,
        WritingModeInfo Flow,
        IReadOnlyList<Box> Ancestors);

    private readonly record struct NestedInterlineNote(
        Int32 Position,
        InterlineNoteSource Note,
        IReadOnlyList<Box> Ancestors);

    private readonly record struct NestedMarginNote(
        Int32 Position,
        MarginNoteSource Note,
        IReadOnlyList<Box> Ancestors);

    private sealed record MarginNoteSource(Box Box);

    private readonly record struct NoteExpansion(
        TextFragment Fragment,
        Line? First,
        Line? Second,
        Double Edges,
        Boolean EndsLine,
        Int32 FirstPlaces,
        Int32 SecondPlaces,
        Double FirstWidth,
        Double SecondWidth);

    /// <summary>One outer fragment of a two-line inline note.</summary>
    private sealed class NoteRun
    {
        public NoteRun(
            Line? first,
            Line? second,
            WritingModeInfo flow,
            Double emptyLineHeight,
            Double blockSize,
            Double lineGap,
            Boolean reachedLineEnd,
            Thickness margin,
            Thickness padding,
            Thickness border)
        {
            First = first;
            Second = second;
            Flow = flow;
            EmptyLineHeight = emptyLineHeight;
            BlockSize = blockSize;
            LineGap = lineGap;
            ReachedLineEnd = reachedLineEnd;
            Margin = margin;
            Padding = padding;
            Border = border;
        }

        public Line? First { get; }

        public Line? Second { get; }

        public Double FirstWidth => First?.Used ?? 0;

        public Double SecondWidth => Second?.Used ?? 0;

        public WritingModeInfo Flow { get; }

        /// <summary>Gets the computed note line-height used when one subline is empty.</summary>
        public Double EmptyLineHeight { get; }

        public Double BlockSize { get; }

        /// <summary>Gets the body's half-leading kept at each block edge of an expanded note line.</summary>
        public Double LineGap { get; }

        /// <summary>Gets whether more content in this segment was pushed to the next outer line.</summary>
        public Boolean ReachedLineEnd { get; }

        /// <summary>Gets or sets whether this note is the last visible content on its outer line.</summary>
        public Boolean EndsOuterLine { get; set; }

        /// <summary>Gets this fragment's inline margin, padding and border.</summary>
        public Double InlineEdges
        {
            get
            {
                var (marginStart, marginEnd) = Flow.InlineMargins(Margin);
                var (paddingStart, paddingEnd) = Flow.InlineMargins(Padding);
                var (borderStart, borderEnd) = Flow.InlineMargins(Border);
                return marginStart + marginEnd + paddingStart + paddingEnd + borderStart + borderEnd;
            }
        }

        public Thickness Margin { get; }

        public Thickness Padding { get; }

        public Thickness Border { get; }
    }

}

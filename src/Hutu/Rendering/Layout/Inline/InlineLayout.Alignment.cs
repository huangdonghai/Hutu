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
    /// Walks the pieces a range of text is measured and drawn in: wherever the orientation changes, and
    /// wherever <c>text-combine-upright</c> sets a short run as one box. Text along a line is one piece,
    /// the whole range, measured exactly as it always was.
    /// </summary>
    /// <remarks>
    /// Walked rather than collected, because a column of upright text is one character to a piece - the
    /// smallest thing the engine splits at and the one asked for most often. On a page of CJK the walk
    /// runs tens of thousands of times per layout, and a list per piece is a heap object per character;
    /// the pieces are ranges of the same string besides, not copies of it.
    /// </remarks>
    private struct OrientedPieces
    {
        private readonly String _source;
        private readonly WritingModeInfo _flow;
        private readonly Int32 _origin;
        private readonly Int32 _length;
        private Int32 _index;
        private Boolean _taken;

        public OrientedPieces(String source, Int32 origin, Int32 length, WritingModeInfo flow)
        {
            _source = source;
            _flow = flow;
            _origin = origin;
            _length = length;
            _index = origin;
            _taken = false;
            Current = default;
        }

        public OrientedPieces GetEnumerator() => this;

        public OrientedRun Current { get; private set; }

        public Boolean MoveNext()
        {
            if (!_flow.IsVertical)
            {
                if (_taken || _length == 0)
                {
                    return false;
                }

                _taken = true;
                Current = OrientedRun.Line(_source, _origin, _length);
                return true;
            }

            var end = _origin + _length;

            if (_index >= end)
            {
                return false;
            }

            // Stepped by code point, not by character: the pair written above the BMP is one character of
            // the text and stands in one cell, and taken by character it was two - each half turned or
            // upright by what a lone surrogate says about nothing.
            if (_flow.CombinesUpright && TextChunker.IsCombinable(TextChunker.CodePointAt(_source, _index)))
            {
                var count = 0;

                while (_index + count < end &&
                       TextChunker.IsCombinable(TextChunker.CodePointAt(_source, _index + count)))
                {
                    count += TextChunker.UnitsAt(_source, _index + count);
                }

                // Only a short run reads as one box. A longer one is ordinary text that happens to be
                // turned, and combining it would squeeze a whole word into a single em.
                if (count <= 4)
                {
                    Current = OrientedRun.Combined(_source, _index, count);
                    _index += count;
                    return true;
                }
            }

            var start = _index;
            var upright = _flow.IsUpright(TextChunker.CodePointAt(_source, start));

            _index += TextChunker.UnitsAt(_source, start);

            while (_index < end && _flow.IsUpright(TextChunker.CodePointAt(_source, _index)) == upright)
            {
                _index += TextChunker.UnitsAt(_source, _index);
            }

            Current = OrientedRun.Of(_source, start, _index - start, upright);
            return true;
        }
    }

    /// <summary>
    /// Measures one piece of a range: how far it advances and which way its glyphs are set. Horizontal
    /// text is measured as the whole run it is, exactly as before, so the horizontal path is unchanged
    /// down to the numbers.
    /// </summary>
    private MeasuredRun Measure(ComputedStyle style, FontStyle font, OrientedRun run, WritingModeInfo flow)
    {
        var width = MeasureChunk(_measurer, style, run.Source, run.Start, run.Length, flow);

        return new MeasuredRun(
            run.Source,
            run.Start,
            run.Length,
            width,
            PinOrientation(flow, run),
            TextChunker.IsCollapsible(run.Source, run.Start, run.Length));
    }

    /// <summary>
    /// Gets the room a run of white space takes where it holds tabs. A tab is not a glyph with an
    /// advance of its own: it runs the line on to the next multiple of the tab size counted from where
    /// the line began (CSS Text 3 §4.2), so the answer depends on how far the line has already come and
    /// has to be asked of the line rather than of the font.
    /// </summary>
    private Double WhiteSpaceWidth(
        ComputedStyle style,
        ComputedStyle tabStyle,
        String source,
        Int32 start,
        Int32 length,
        Double position,
        WritingModeInfo flow)
    {
        if (source.IndexOf('\t', start, length) < 0)
        {
            return Measure(style, source, start, length, flow);
        }

        // The stop is the width of a space in the font of the nearest block container, not in the
        // inline element the tab happens to stand in: an integer tab size is that many spaces of the
        // block's font, while a length is already absolute (CSS Text 3 §4.2).
        var unit = style.TabLength ?? style.TabUnits * Measure(tabStyle, " ", flow);

        if (unit <= 0)
        {
            return 0;
        }

        var width = 0.0;
        var index = start;
        var end = start + length;

        while (index < end)
        {
            if (source[index] == '\t')
            {
                var at = position + width;
                var next = (Math.Floor(at / unit) + 1) * unit;

                // A tab that would move the line less than half a character is taken to the stop after
                // the nearest one instead, so a tab does not become a hairline space (CSS Text 3 §4.2).
                var half = Measure(tabStyle, "0", flow) / 2;

                if (next - at < half)
                {
                    next += unit;
                }

                width = next - position;
                index++;
                continue;
            }

            var from = index;

            while (index < end && source[index] != '\t')
            {
                index++;
            }

            width += Measure(style, source, from, index - from, flow);
        }

        return width;
    }

    /// <summary>
    /// Gets the flow a piece of text is drawn in: the orientation the piece was split on, so that what
    /// the measurer treats as upright is also what the painter draws upright.
    /// </summary>
    /// <remarks>
    /// A combined run is drawn as one upright box and keeps the mode's own orientation; the rest are
    /// pinned to the orientation they were split on. Text that is not vertical is handed back as it
    /// is, because orientation means nothing along a line.
    /// </remarks>
    private static WritingModeInfo PinOrientation(WritingModeInfo flow, OrientedRun run)
    {
        if (!flow.IsVertical)
        {
            return flow;
        }

        return run.IsCombined
            ? flow
            : new WritingModeInfo(
                flow.Mode,
                run.IsUpright ? TextOrientation.Upright : TextOrientation.Sideways,
                false,
                flow.Direction);
    }

    /// <summary>
    /// Gets the flow a piece of text that is set as a whole - a ruby base, say - is drawn in, which is
    /// the orientation of its first piece. That is the whole of it whenever the base is the single
    /// character a base usually is; a base that genuinely mixes orientations takes the first, the same
    /// approximation the reading beside it already makes.
    /// </summary>
    private static WritingModeInfo ResolveOrientation(String text, WritingModeInfo flow)
    {
        if (!flow.IsVertical)
        {
            return flow;
        }

        foreach (var run in new OrientedPieces(text, 0, text.Length, flow))
        {
            return PinOrientation(flow, run);
        }

        return flow;
    }

    /// <summary>
    /// Gets where a line's runs start along the inline axis: aligned in what the line's indentation
    /// leaves of the content box, and then moved along by that indentation.
    /// </summary>
    private static Double Aligned(Line line, Double maxInline) =>
        line.Fragments.Count == 0
            ? 0
            : AlignmentOffset(
                line.Alignment,
                maxInline - line.Indent,
                line.Used,
                line.Fragments[0].Flow) + line.Indent;

    /// <summary>
    /// Gives the lines the block's <c>text-align-last</c> speaks for - the one that ends the block and
    /// every one a forced break ended - the alignment that property states, and takes the offset they
    /// stand at with it (CSS Text 3 §6.3).
    /// </summary>
    /// <remarks>
    /// <c>auto</c> is not a fifth way of aligning a line but a delegation: the affected line takes
    /// <c>text-align</c> as it is, unless that asks for justification. A justified line the room was not
    /// spread across does not keep the offset its spread neighbours were given - it stands at its start
    /// edge, which is what <c>text-align-last: auto</c> comes to.
    /// </remarks>
    private static void AlignLastLines(List<Line> lines, Double maxInline)
    {
        // Walked from the end, because the line that ends the block is the one the property is named for
        // and the rest it speaks for are only those the page was told to break.
        for (var position = lines.Count - 1; position >= 0; position--)
        {
            var line = lines[position];

            if (line.IsInterlineNote ||
                line.Fragments.Count == 0 ||
                (!line.ForceEnded && position != lines.Count - 1))
            {
                continue;
            }

            var style = line.Governing ?? line.Fragments[0].Style;
            var alignLast = style.TextAlignLast;
            var aligned = alignLast.Equals("auto", StringComparison.OrdinalIgnoreCase)
                ? (style.TextAlign.Equals("justify", StringComparison.OrdinalIgnoreCase)
                    ? "start"
                    : style.TextAlign)
                : alignLast;

            if (aligned.Equals(line.Alignment, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            line.Alignment = aligned;
            line.Offset = Aligned(line, maxInline);
        }
    }

    /// <summary>
    /// Spreads the room a justified line did not use across the spaces inside it, which is all
    /// inter-word justification is (CSS Text 3 §5.7.1). Which lines are justified is the alignment the
    /// line was given by the pass above, so the line that ends the block and the ones a forced break
    /// ended are spread only when <c>text-align-last</c> says so. A line with no space left in it has
    /// nowhere honest to put the room, and is left standing as it is - the engine does not space out
    /// letters.
    /// </summary>
    /// <remarks>
    /// The room is owed out as steps of a running total rather than as one share repeated, so the
    /// fractions fall on different spaces and the last of them lands exactly on the edge the line was
    /// given. A plan is kept and asked again, so every line starts from nothing owed: the room it was
    /// given can be different the next time round.
    /// </remarks>
    private static void DistributeSpace(List<Line> lines)
    {
        for (var position = 0; position < lines.Count; position++)
        {
            var line = lines[position];

            // A plan is kept and asked again, and the room a line was given can be different the next
            // time round: every expansion, including the two rows of a note, comes back out first.
            ResetExpansion(line);

            if (line.Fragments.Count == 0 ||
                !line.Alignment.Equals("justify", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var bodyPlaces = 0;
            var notes = new List<NoteExpansion>();

            for (var index = 0; index < line.Fragments.Count; index++)
            {
                var fragment = line.Fragments[index];

                if (fragment.Note is not { } note)
                {
                    bodyPlaces += Places(line, index);
                    continue;
                }

                var firstWidth = note.First?.Used ?? 0;
                var secondWidth = note.Second?.Used ?? 0;
                var naturalContent = Math.Max(firstWidth, secondWidth);

                notes.Add(new NoteExpansion(
                    fragment,
                    note.First,
                    note.Second,
                    Math.Max(0, fragment.Width - naturalContent),
                    note.EndsOuterLine,
                    note.First is { } firstRow ? LinePlaces(firstRow) : 0,
                    note.Second is { } secondRow ? LinePlaces(secondRow) : 0,
                    firstWidth,
                    secondWidth));
            }

            var free = (line.Extent - line.Indent) - line.Used;

            if (free <= 0)
            {
                foreach (var note in notes)
                {
                    if (note.EndsLine)
                    {
                        ApplyNoteExpansion(note, 0);
                    }
                }

                continue;
            }

            if (notes.Count == 0)
            {
                if (bodyPlaces > 0)
                {
                    ExpandByShare(line, free / bodyPlaces);
                }

                continue;
            }

            Double Growth(Double share)
            {
                var growth = bodyPlaces * share;

                foreach (var note in notes)
                {
                    var first = note.FirstWidth + (note.FirstPlaces * share);
                    var second = note.SecondWidth + (note.SecondPlaces * share);
                    growth += Math.Max(first, second) - Math.Max(note.FirstWidth, note.SecondWidth);
                }

                return growth;
            }

            if (bodyPlaces == 0 &&
                !notes.Exists(note => note.FirstPlaces > 0 || note.SecondPlaces > 0))
            {
                continue;
            }

            var high = 1.0;

            while (Growth(high) < free && high < 1_000_000_000)
            {
                high *= 2;
            }

            var low = 0.0;

            for (var step = 0; step < 60; step++)
            {
                var middle = (low + high) / 2;

                if (Growth(middle) < free)
                {
                    low = middle;
                }
                else
                {
                    high = middle;
                }
            }

            var share = (low + high) / 2;

            if (share <= 0)
            {
                continue;
            }

            ExpandByShare(line, share);

            foreach (var note in notes)
            {
                ApplyNoteExpansion(note, share);
            }
        }
    }

    /// <summary>Restores every fragment and note subline to the extent it had before justification.</summary>
    private static void ResetExpansion(Line line)
    {
        if (line.NaturalUsed is { } used)
        {
            line.Used = used;
        }
        else
        {
            line.NaturalUsed = line.Used;
        }

        if (line.NaturalExtent is { } extent)
        {
            line.Extent = extent;
        }
        else
        {
            line.NaturalExtent = line.Extent;
        }

        foreach (var fragment in line.Fragments)
        {
            fragment.Expand(0, 0);

            if (fragment.Note is { First: { } first })
            {
                ResetExpansion(first);
            }

            if (fragment.Note is { Second: { } second })
            {
                ResetExpansion(second);
            }
        }
    }

    /// <summary>
    /// Gives every expansion place on a line the same share and returns the width it added. The room is
    /// owed out on a running total so fractions fall on different places and the last one lands exactly.
    /// </summary>
    private static Double ExpandByShare(Line line, Double share)
    {
        var owed = 0.0;
        var given = 0.0;

        for (var index = 0; index < line.Fragments.Count; index++)
        {
            var fragment = line.Fragments[index];
            var count = Places(line, index);

            if (count == 0)
            {
                continue;
            }

            owed += share * count;
            fragment.Expand((owed - given) / count, count);
            given = owed;
        }

        return given;
    }

    /// <summary>
    /// Expands a note's two sublines with the line-wide share. A subline that reaches the outer line
    /// end also fills the natural width of the wider subline, which is the room inside the note box.
    /// </summary>
    private static void ApplyNoteExpansion(NoteExpansion note, Double share)
    {
        var target = Math.Max(
            note.FirstWidth + (note.FirstPlaces * share),
            note.SecondWidth + (note.SecondPlaces * share));
        var firstShare = note.EndsLine &&
            note.First is not null &&
            note.FirstPlaces > 0 &&
            note.FirstWidth < target
            ? (target - note.FirstWidth) / note.FirstPlaces
            : share;
        var secondShare = note.EndsLine &&
            note.Second is not null &&
            note.SecondPlaces > 0 &&
            note.SecondWidth < target
            ? (target - note.SecondWidth) / note.SecondPlaces
            : share;
        var firstGrowth = note.First is { } firstRow ? ExpandByShare(firstRow, firstShare) : 0;
        var secondGrowth = note.Second is { } secondRow ? ExpandByShare(secondRow, secondShare) : 0;
        var firstUsed = note.FirstWidth + firstGrowth;
        var secondUsed = note.SecondWidth + secondGrowth;
        var content = Math.Max(firstUsed, secondUsed);

        if (note.First is { } first)
        {
            first.Used = firstUsed;
            first.Extent = content;
            first.Offset = 0;
        }

        if (note.Second is { } second)
        {
            second.Used = secondUsed;
            second.Extent = content;
            second.Offset = 0;
        }

        note.Fragment.SetJustifiedWidth(content + note.Edges);
    }

    /// <summary>Gets every expansion place held by the fragments of one line.</summary>
    private static Int32 LinePlaces(Line line)
    {
        var places = 0;

        for (var index = 0; index < line.Fragments.Count; index++)
        {
            places += Places(line, index);
        }

        return places;
    }

    /// <summary>
    /// Gets how many places the fragment at this point of the line holds a share of the room for: the
    /// runs of white space it stands between and, where the text expands between characters, the seams
    /// between the ideographs it is made of. A place is counted for the fragment on its left, whose
    /// advance carries the room, which is also how a fragment that ends in a space already worked; the
    /// seam into the fragment after it is therefore this one's, and is what lets a line of Chinese -
    /// where two ideographs may be split between the pieces the markup made - be paid in full.
    /// </summary>
    /// <remarks>
    /// The place a line trails into is left out: a line is not justified into the room past its own edge
    /// (CSS Text 3 §5.7.1), whichever lines the block ends up spreading.
    /// </remarks>
    private static Int32 Places(Line line, Int32 index)
    {
        var fragment = line.Fragments[index];

        // A glyph hung off the line's edge takes no room in the line and no place in it: the room a
        // justified line spreads is owed to the text the line still holds, and a share falling on the
        // seam into a hung glyph is a share falling into the margin (CSS Text 3 §8.2.1).
        if (fragment.HangSide != 0)
        {
            return 0;
        }

        var last = fragment.Start + fragment.Length - 1;
        var justify = fragment.Style.TextJustify;
        var places = 0;
        var atEdge = false;

        var points = TextClusters.ExpansionPointsView(
            fragment.Source, fragment.Start, fragment.Length, justify);
        try
        {
            for (var at = 0; at < points.Length; at++)
            {
                var point = points[at];

                if (point < last)
                {
                    places++;
                    continue;
                }

                // The room past the edge of the line is nowhere to put, and a place standing at the edge of
                // the fragment already carries the seam into what comes next.
                atEdge = true;

                if (!EndsLine(line, index))
                {
                    places++;
                }
            }
        }
        finally
        {
            points.Dispose();
        }

        // A fragment that carries no text - the empty inline box a page puts between two words - has no
        // first and no last character to ask about, so the seam either side of it is not a question.
        if (atEdge || fragment.Length == 0 || EndsLine(line, index))
        {
            return places;
        }

        var following = line.Fragments[index + 1];

        if (following.Length == 0)
        {
            return places;
        }

        return places + (TextClusters.Seam(
            fragment.Source, last, following.Source, following.Start, justify)
            ? 1
            : 0);
    }

    /// <summary>
    /// Gets whether the fragment is the one the text of the line ends with: the last fragment, or the one
    /// before a glyph hung off the line's edge, whose trailing place is the line's trailing as well.
    /// </summary>
    private static Boolean EndsLine(Line line, Int32 index) =>
        index == line.Fragments.Count - 1 || line.Fragments[index + 1].HangSide != 0;

    /// <summary>
    /// Gets the distance the first line of a box is set in from the edge it begins at.
    /// </summary>
    /// <remarks>
    /// A percentage among the indentation is a share of the containing block's inline extent, which is
    /// the block the text sits in rather than the one it makes - the same extent every other length of
    /// the line resolves against.
    /// </remarks>
    /// <summary>
    /// Gets the distance the first line of the container is set in by, as a percentage of the
    /// container's own content box along the line (CSS Text 3 §8.1, measured: a border-box 120px
    /// div with 20px of right inset indents 50px for `text-indent: 50%`, not 50% of the containing
    /// block - text-indent-percentage-002).
    /// </summary>
    /// <summary>
    /// The distance a block's lines are set in by, with the two modifiers that say which of them carry it:
    /// <c>hanging</c> puts the distance on every line but the ones the rule names, and <c>each-line</c>
    /// adds the line after every forced break to the ones it names (CSS Text 3 §5.2).
    /// </summary>
    private readonly record struct TextIndentation(Double Distance, Boolean Hanging, Boolean EachLine)
    {
        /// <summary>
        /// Gets whether a line carries the distance. <paramref name="first"/> says the line begins the
        /// block's content and <paramref name="afterBreak"/> says a forced break ended the line before it,
        /// which are the two lines the property names; <c>hanging</c> puts it on every other line.
        /// </summary>
        internal Boolean Carries(Boolean first, Boolean afterBreak)
        {
            var named = first || (EachLine && afterBreak);

            return Hanging ? !named : named;
        }
    }

    private static TextIndentation Indentation(Box container, WritingModeInfo flow, Double contentInline)
    {
        var indent = container.Style.TextIndent;

        // A run of inline content that a block child broke away from the head of its box is wrapped in an
        // anonymous block of its own, and only the first of those wraps is the line the indentation was
        // written for: the block's first line stands where its content begins, not where each fragment of
        // it happens to resume (CSS Text 3 §5.2).
        if (!indent.HasValue || (container.IsAnonymous && !container.StartsParentContent))
        {
            return default;
        }

        return new TextIndentation(
            indent.Resolve(contentInline),
            container.Style.TextIndentHanging,
            container.Style.TextIndentEachLine);
    }

    /// <summary>
    /// Gets where the line's content starts along the inline axis, which is what <c>text-align</c>
    /// asks for and which end the line reads from when it asks for nothing.
    /// </summary>
    /// <remarks>
    /// <c>start</c> and <c>end</c> are logical and follow the direction the line reads in, while
    /// <c>left</c> and <c>right</c> are physical and do not; the initial value is <c>start</c>, so an
    /// unaligned right-to-left line sits against its right edge.
    /// </remarks>
    internal static Double AlignmentOffset(String textAlign, Double maxInline, Double used, WritingModeInfo flow)
    {
        if (maxInline <= used)
        {
            return 0;
        }

        var free = maxInline - used;

        if (textAlign.Equals("center", StringComparison.OrdinalIgnoreCase))
        {
            return free / 2;
        }

        // The offset is measured from the edge the line begins at, which `Map` then turns into a
        // physical place; only the two physical keywords have to look at which edge that is.
        if (textAlign.Equals("left", StringComparison.OrdinalIgnoreCase))
        {
            return flow.InlineSign > 0 ? 0 : free;
        }

        if (textAlign.Equals("right", StringComparison.OrdinalIgnoreCase))
        {
            return flow.InlineSign > 0 ? free : 0;
        }

        // `start`, `justify` and the initial value begin at the start; `end` moves over to the far end.
        return textAlign.Equals("end", StringComparison.OrdinalIgnoreCase) ? free : 0;
    }

}

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
    /// One step a line took: a piece of a run, the room it was charged, and where it came from, kept so
    /// the step can be taken again on the line after a soft hyphen.
    /// </summary>
    private readonly record struct TailStep(
        String Source,
        Int32 Start,
        Int32 Length,
        Double Advance,
        ComputedStyle Style,
        Box Box,
        WritingModeInfo Flow,
        InlineAncestor? Inline,
        Boolean OutOfFlow = false,
        Boolean Empty = false);

    private struct LineBuilder
    {
        private readonly InlineLayout _owner;
        private List<TextFragment> _fragments;
        private List<OpenInline> _open;
        private List<OpenInline> _buildOpen;
        private List<OpenInline> _usedOpen;
        private Double[] _above;
        private Double[] _below;
        private PreviousText? _previousLast;
        private Boolean _previousKnown;

        /// <summary>The chain a captured line stood with, kept from one capture to the next.</summary>
        private OpenInline[] _opened;
        private HashSet<InlineAncestor>? _carried;
        private ComputedStyle? _fallback;
        private Boolean _vertical;
        private Double _indent;
        private ITextMeasurer _measurer;
        private Boolean _startsParagraph;
        private Boolean _firstLine;

        public LineBuilder(InlineLayout owner)
        {
            _owner = owner;
            _fragments = new List<TextFragment>();
            _open = new List<OpenInline>();
            _buildOpen = new List<OpenInline>();
            _usedOpen = new List<OpenInline>();
            _above = Array.Empty<Double>();
            _below = Array.Empty<Double>();
            _previousLast = null;
            _previousKnown = false;
            _opened = Array.Empty<OpenInline>();
            _carried = null;
            _fallback = null;
            _vertical = false;
            _indent = 0;
            _measurer = null!;
            _startsParagraph = false;
            _firstLine = false;
            Width = 0;
        }

        public void ReleaseReferences()
        {
            _fragments.Clear();
            _open.Clear();
            _buildOpen.Clear();
            _usedOpen.Clear();
            Array.Clear(_opened, 0, _opened.Length);
            _previousKnown = false;
        }

        /// <param name="indent">
        /// The distance the line is set in from the edge it begins at. It is the extent the line starts
        /// out having used, so the run that would not have fitted without it is carried to the next
        /// line - which is what an indented first line does to the text on it.
        /// </param>
        public LineBuilder Reset(
            ComputedStyle fallback,
            Double indent,
            Boolean vertical,
            ITextMeasurer measurer,
            Boolean startsParagraph,
            Boolean firstLine = false)
        {
            _fallback = fallback;
            _carried = null;
            _indent = indent;
            _vertical = vertical;
            _measurer = measurer;
            _startsParagraph = startsParagraph;
            _firstLine = firstLine;
            Width = indent;
            _fragments.Clear();
            _open.Clear();
            _buildOpen.Clear();
            _usedOpen.Clear();
            _previousKnown = false;
            return this;
        }

        public LineBuilder Reset(
            ComputedStyle fallback,
            HashSet<InlineAncestor>? carried,
            Boolean vertical,
            ITextMeasurer measurer,
            Boolean startsParagraph,
            Double indent = 0)
        {
            _fallback = fallback;
            _carried = carried;
            _indent = indent;
            _vertical = vertical;
            _measurer = measurer;
            _startsParagraph = startsParagraph;
            _firstLine = false;
            Width = indent;
            _fragments.Clear();
            _open.Clear();
            _buildOpen.Clear();
            _usedOpen.Clear();
            _previousKnown = false;
            return this;
        }

        /// <summary>
        /// Gets the extent the line takes as it is being built, which is what the wrap test is made
        /// against while the line is still growing. It is only an estimate of what the line ends up
        /// taking - trimming the trailing spaces can still take a run back out - so
        /// <see cref="Build"/> works the extent it hands back out afresh instead of reusing this.
        /// </summary>
        public Double Width { get; private set; }

        public Double Indent => _indent;

        /// <summary>
        /// Gets the room the runs already on the line take, using the same fragment accounting as
        /// <see cref="Build"/> rather than the running wrapping estimate.
        /// </summary>
        public Double CurrentUsed()
        {
            var open = _usedOpen;
            open.Clear();
            var used = 0.0;

            foreach (var fragment in _fragments)
            {
                used += Advance(open, fragment.Inline, _carried, origin: 0, apply: true, step: null);
                used += fragment.Width;
            }

            foreach (var item in open)
            {
                used += item.Ancestor.InlineEndInset;
            }

            return used;
        }

        /// <summary>
        /// Charges room the line had left off for a glyph that turned out to stand inside it: a stop
        /// or comma keeps its advance only while it ends the line.
        /// </summary>
        public void Widen(Double room) => Width += room;

        /// <summary>
        /// Gets whether the line already holds the element, which is what says a run is not the first
        /// one written in it: an element the line has not opened yet begins where the run stands.
        /// </summary>
        public Boolean IsOpen(InlineAncestor inline) =>
            _open.Exists(entry => ReferenceEquals(entry.Ancestor, inline));

        /// <summary>
        /// Gets whether the line carries anything drawn on it. A box that left the flow is not: it stands
        /// on the line only to say where it would have been, so a line holding nothing else is an empty
        /// line and takes no height.
        /// </summary>
        public Boolean HasContent =>
            _fragments.Exists(fragment => !fragment.OutOfFlow && !fragment.InterlineGap);

        /// <summary>
        /// Gets whether the line draws anything yet. An empty inline box is content but no ink, and a
        /// glyph hangs from the line's start edge just the same past one.
        /// </summary>
        private Boolean HasInk => _fragments.Exists(fragment => !fragment.OutOfFlow && HasInk(fragment));

        /// <summary>
        /// Gets whether a run is the opening glyph an element's first formatted line hangs: it stands
        /// at the line's start, its element begins here, and no border or padding of its own stands
        /// between it and the edge (CSS Text 3 §8.2.1).
        /// </summary>
        private Boolean HangsStart(ComputedStyle style, String source, Int32 start, InlineAncestor? inline)
        {
            if (!HangingPunctuation.HangsAtStart(style.HangingPunctuation) ||
                !HangingPunctuation.IsOpening(TextChunker.CodePointAt(source, start)))
            {
                return false;
            }

            // The block's own text reaches the edge on its first line; an inline element's text on
            // the line the element begins on, which is the line this run is written on.
            var first = inline is null ? _firstLine : _carried?.Contains(inline) != true;

            return first && !EdgeBlocked(_carried, inline, start: true);
        }

        /// <summary>
        /// Gets whether the line carries any fragment at all, drawn or not. A line with nothing but a box
        /// that left the flow still has to be closed, or that box is never placed.
        /// </summary>
        public Boolean HasFragments => _fragments.Count > 0;

        /// <summary>
        /// Gets whether the line ends in a soft hyphen that may end it: the mark is drawn there, and only
        /// where the line carries something before the hyphen - a line of soft hyphens alone is where the
        /// previous one was cut, and cutting it again would draw a mark with nothing before it.
        /// </summary>
        public Boolean EndsInSoftHyphen
        {
            get
            {
                // A box that left the flow holds none of the line's text, so the word runs straight on
                // across it: the hyphen the line ends on is still the hyphen the line before it wrote.
                TextFragment? last = null;
                var wordsBefore = 0;

                for (var index = _fragments.Count - 1; index >= 0; index--)
                {
                    if (_fragments[index].OutOfFlow ||
                        _fragments[index].InterlineGap ||
                        _fragments[index].InterlineNote is not null ||
                        _fragments[index].MarginNote is not null)
                    {
                        continue;
                    }

                    last = _fragments[index];
                    wordsBefore = index;
                    break;
                }

                if (last is not { } run ||
                    !(run.Length > 0 && !run.IsReplaced && run.Ruby is null && run.Atomic is null))
                {
                    return false;
                }

                var trailing = run.TrailingSoftHyphens;

                return trailing > 0 && (run.Length > trailing || wordsBefore > 0);
            }
        }

        /// <summary>
        /// Gets a place the line can be cut back to: the runs it holds, the room they took, and the
        /// elements their chain had opened.
        /// </summary>
        /// <remarks>
        /// The opened chain is copied into a buffer the builder keeps rather than into a fresh array:
        /// a page whose text may break after every character took a snapshot at every one of them, and
        /// only the newest snapshot is ever read back.
        /// </remarks>
        public Checkpoint Capture()
        {
            var opened = _open.Count;

            if (_opened.Length < opened)
            {
                Array.Resize(ref _opened, Math.Max(opened, Math.Max(4, _opened.Length * 2)));
            }

            _open.CopyTo(0, _opened, 0, opened);

            return new Checkpoint(
                _fragments.Count,
                _fragments.Count > 0 ? _fragments[^1].Length : 0,
                _fragments.Count > 0 ? _fragments[^1].Width : 0,
                _fragments.Count > 0 ? _fragments[^1].Trims : default,
                _fragments.Count > 0 ? _fragments[^1].AutoSpaces : default,
                Width,
                _opened,
                opened);
        }

        /// <summary>
        /// Cuts the line back to a place it stood at. The caller has kept the steps taken since and
        /// takes them again on the next line, so nothing is drawn here and the boxes those runs would
        /// open are never made - the placing pass runs only when a line closes.
        /// </summary>
        public void Restore(Checkpoint at)
        {
            _previousKnown = false;

            if (_fragments.Count > at.Fragments)
            {
                _fragments.RemoveRange(at.Fragments, _fragments.Count - at.Fragments);
            }

            if (at.Fragments > 0 && _fragments.Count == at.Fragments)
            {
                var last = _fragments[^1];
                last.Length = at.Length;
                last.Width = at.RunWidth;
                last.Trims = at.RunTrims;
                last.AutoSpaces = at.RunAutoSpaces;
            }

            Width = at.Width;
            _open.Clear();

            for (var index = 0; index < at.Opened; index++)
            {
                _open.Add(at.Open[index]);
            }
        }

        /// <summary>The state a line may be cut back to, kept while a soft hyphen offers the place.</summary>
        public readonly record struct Checkpoint(
            Int32 Fragments,
            Int32 Length,
            Double RunWidth,
            MaskSpan RunTrims,
            MaskSpan RunAutoSpaces,
            Double Width,
            OpenInline[] Open,
            Int32 Opened);

        /// <summary>
        /// Gets the code point the line's text ends on, or null where it ends on a box rather than a run.
        /// </summary>
        public Int32? LastCodePoint
        {
            get
            {
                // A box the page took out of the flow stands in the line's list but is not part of its
                // text, so the word the line has begun carries straight on across it; a box that holds
                // text of its own - a replaced picture, a ruby pair, an atomic line - really does end
                // the word.
                for (var index = _fragments.Count - 1; index >= 0; index--)
                {
                    var fragment = _fragments[index];

                    if (fragment.OutOfFlow || fragment.InterlineGap ||
                        fragment.InterlineNote is not null || fragment.MarginNote is not null)
                    {
                        continue;
                    }

                    if (fragment.Length == 0 || fragment.IsReplaced || fragment.Ruby is not null ||
                        fragment.Atomic is not null)
                    {
                        return null;
                    }

                    return TextChunker.CodePointBefore(fragment.Source, fragment.Start + fragment.Length);
                }

                return null;
            }
        }

        /// <summary>
        /// Gets the extent the line would already take if the next run sat in the given chain. The
        /// wrap test asks this rather than reading <see cref="Width"/>, so that the padding and border
        /// a run brings with it are counted before the run itself is measured against what is left.
        /// </summary>
        public Double ExtentFor(InlineAncestor? inline) =>
            Width + Advance(_open, inline, _carried, origin: 0, apply: false, step: null);

        /// <summary>
        /// Adds an inline element that holds nothing: the line keeps the margins, borders and padding
        /// the box carries along it, and the box is drawn where they put it (CSS 2.1 §8.1).
        /// </summary>
        public void AddEmpty(Box box, ComputedStyle style, WritingModeInfo flow, InlineAncestor inline)
        {
            _previousKnown = false;
            Enter(inline);

            _fragments.Add(_owner.NewFragment(String.Empty, 0, 0, 0, style, box, flow, inline));
        }

        /// <summary>Adds an atomic replaced box, which occupies its own used size on the line.</summary>
        public void AddReplaced(Box box, Double width, Double height, InlineAncestor? inline)
        {
            _previousKnown = false;
            Enter(inline);

            _fragments.Add(_owner.NewFragment(
                String.Empty,
                0,
                0,
                width,
                box.Style,
                box,
                box.Flow,
                inline,
                isReplaced: true,
                contentHeight: height));

            Width += width;
        }

        /// <summary>
        /// Adds a box that left the flow. It takes no room: the cursor does not move and the line grows no
        /// taller for it, so what the fragment records is only where the cursor stood - the place the box
        /// would have had, which is the static position it is placed by.
        /// </summary>
        public void AddOutOfFlow(Box box, ComputedStyle style, InlineAncestor? inline)
        {
            _previousKnown = false;
            Enter(inline);

            _fragments.Add(_owner.NewFragment(
                String.Empty,
                0,
                0,
                0,
                style,
                box,
                box.Flow,
                inline,
                outOfFlow: true));
        }

        /// <summary>
        /// Adds an inline-block, which the line places whole. The width it takes along the line is the
        /// one it was measured to together with its own margins, which the box is then drawn inside of.
        /// </summary>
        public void AddAtomic(Box box, AtomicInline atomic, InlineAncestor? inline)
        {
            _previousKnown = false;
            Enter(inline);

            _fragments.Add(_owner.NewFragment(
                String.Empty,
                0,
                0,
                atomic.InlineSize,
                box.Style,
                box,
                box.Flow,
                inline,
                atomic: atomic));

            Width += atomic.InlineSize;
        }

        /// <summary>Adds one outer fragment of a two-line inline note.</summary>
        public void AddNote(Box box, ComputedStyle style, WritingModeInfo flow, InlineAncestor? inline, NoteRun note, Double width)
        {
            _previousKnown = false;
            Enter(inline);

            _fragments.Add(_owner.NewFragment(
                String.Empty,
                0,
                0,
                width,
                style,
                box,
                flow,
                inline,
                note: note));

            Width += width;
        }

        /// <summary>Adds a zero-width marker that records where an interline note was declared.</summary>
        public void AddInterlineNote(
            Box box,
            ComputedStyle style,
            WritingModeInfo flow,
            InlineAncestor? inline,
            InterlineNoteSource note)
        {
            _previousKnown = false;
            Enter(inline);

            _fragments.Add(_owner.NewFragment(
                String.Empty,
                0,
                0,
                0,
                style,
                box,
                flow,
                inline,
                interlineNote: note));
        }

        /// <summary>Adds a zero-width marker for a margin note declared in the line.</summary>
        public void AddMarginNote(
            Box box,
            ComputedStyle style,
            WritingModeInfo flow,
            InlineAncestor? inline,
            MarginNoteSource note)
        {
            _previousKnown = false;
            _fragments.Add(_owner.NewFragment(
                String.Empty,
                0,
                0,
                0,
                style,
                box,
                flow,
                null,
                marginNote: note));
        }

        /// <summary>Adds fixed empty room used to place an interline note's midpoint.</summary>
        public void AddInterlineGap(Box box, ComputedStyle style, WritingModeInfo flow, Double width)
        {
            _previousKnown = false;

            if (width <= 0)
            {
                return;
            }

            _fragments.Add(_owner.NewFragment(
                String.Empty,
                0,
                0,
                width,
                style,
                box,
                flow,
                null,
                interlineGap: true));

            Width += width;
        }

        /// <summary>Adds a hoisted interline-note marker at a position inside the line it came from.</summary>
        public void AddMappedInterlineNote(
            Box box,
            ComputedStyle style,
            WritingModeInfo flow,
            InterlineNoteSource note,
            Double position)
        {
            _previousKnown = false;
            _fragments.Add(_owner.NewFragment(
                String.Empty,
                0,
                0,
                0,
                style,
                box,
                flow,
                null,
                interlineNote: note,
                interlinePosition: position));
        }

        /// <summary>
        /// Gets the last character the line has already set, or null where the line holds nothing or
        /// a box that is not text stands between it and what comes next.
        /// </summary>
        private PreviousText? Previous
        {
            get
            {
                if (_previousKnown)
                {
                    return _previousLast;
                }

                for (var index = _fragments.Count - 1; index >= 0; index--)
                {
                    var fragment = _fragments[index];

                    if (fragment.OutOfFlow)
                    {
                        continue;
                    }

                    if (fragment.Note is not null)
                    {
                        _previousLast = null;
                        _previousKnown = true;
                        return null;
                    }

                    if (fragment.Length == 0)
                    {
                        continue;
                    }

                    if (fragment.IsReplaced || fragment.Atomic is not null || fragment.Ruby is not null)
                    {
                        _previousLast = null;
                        _previousKnown = true;
                        return null;
                    }

                    var codePoint = TextClusters.BaseBefore(
                        fragment.Source, fragment.Start + fragment.Length);
                    var stop = fragment.Start + fragment.Length;
                    var take = stop >= 2 && Char.IsSurrogatePair(fragment.Source, stop - 2) ? 2 : 1;
                    var before = fragment.Length > take
                        ? CodePointBefore(fragment.Source, stop - take)
                        : 0;
                    var last = fragment.Length - 1;
                    var trimmed = _owner.CodeAt(fragment.Trims, fragment.Start + last) != '\0';

                    _previousLast = new PreviousText(
                        NeighborOf(_measurer, fragment.Style, codePoint, fragment.Flow),
                        fragment.Style,
                        codePoint,
                        trimmed,
                        fragment.Flow,
                        before);
                    _previousKnown = true;
                    return _previousLast;
                }

                _previousLast = null;
                _previousKnown = true;
                return null;
            }
        }

        private void Remember(TextFragment fragment)
        {
            if (fragment.Length == 0)
            {
                _previousKnown = false;
                return;
            }

            var stop = fragment.Start + fragment.Length;
            var codePoint = TextClusters.BaseBefore(fragment.Source, stop);
            var take = stop >= 2 && Char.IsSurrogatePair(fragment.Source, stop - 2) ? 2 : 1;
            var before = fragment.Length > take
                ? CodePointBefore(fragment.Source, stop - take)
                : 0;
            var trimmed = _owner.CodeAt(
                fragment.Trims,
                fragment.Start + fragment.Length - 1) != '\0';

            _previousLast = new PreviousText(
                NeighborOf(_measurer, fragment.Style, codePoint, fragment.Flow),
                fragment.Style,
                codePoint,
                trimmed,
                fragment.Flow,
                before);
            _previousKnown = true;
        }

        /// <summary>
        /// Gets what adding this range would trim, without adding it: the caller asks before it tests
        /// whether the range fits, and the answer has to be the one the add will take.
        /// </summary>
        public TrimStep PreviewTrim(
            String source,
            Int32 start,
            Int32 length,
            ComputedStyle style,
            WritingModeInfo flow) =>
            PlanTrim(
                _measurer,
                source,
                start,
                length,
                style,
                flow,
                Previous,
                !HasContent,
                _startsParagraph,
                mask: null);

        /// <summary>
        /// Gets what adding this range would space, without adding it: the caller asks before it tests
        /// whether the range fits, and the answer has to be the one the add will take.
        /// </summary>
        public AutoSpaceStep PreviewAutoSpace(
            String source,
            Int32 start,
            Int32 length,
            ComputedStyle style,
            WritingModeInfo flow) =>
            PlanAutoSpace(
                _measurer,
                source,
                start,
                length,
                style,
                flow,
                Previous,
                mask: null);

        /// <summary>
        /// Takes the room a trimmed character gives up off the last run the line holds, and marks it
        /// in the run's mask. The caller has already found that this character begins the collapse.
        /// </summary>
        private void TrimLast(Double delta, Char code)
        {
            for (var index = _fragments.Count - 1; index >= 0; index--)
            {
                var fragment = _fragments[index];

                if (fragment.OutOfFlow)
                {
                    continue;
                }

                if (fragment.IsReplaced || fragment.Atomic is not null || fragment.Ruby is not null)
                {
                    return;
                }

                if (fragment.Length == 0)
                {
                    continue;
                }

                var last = fragment.Length - 1;

                if (last < 0 || _owner.CodeAt(fragment.Trims, fragment.Start + last) != '\0')
                {
                    return;
                }

                _owner.AppendMark(ref fragment.TrimsRef, fragment.Start + last, code);
                fragment.Width -= delta;
                Width -= delta;
                return;
            }
        }

        /// <summary>
        /// Widens the last run the line holds by the room a boundary to the run arriving after it
        /// adds, which is where the gap between the two is drawn from.
        /// </summary>
        private void WidenLast(Double delta)
        {
            for (var index = _fragments.Count - 1; index >= 0; index--)
            {
                var fragment = _fragments[index];

                if (fragment.OutOfFlow || fragment.Length == 0)
                {
                    continue;
                }

                fragment.Width += delta;
                Width += delta;
                return;
            }
        }

        /// <summary>
        /// Takes the room a replaced space gives up off the last run the line holds, and marks the
        /// space, so the drawing gives it the boundary's room instead of its own.
        /// </summary>
        private void ReplaceLastSpace(Double delta)
        {
            for (var index = _fragments.Count - 1; index >= 0; index--)
            {
                var fragment = _fragments[index];

                if (fragment.OutOfFlow)
                {
                    continue;
                }

                if (fragment.IsReplaced || fragment.Atomic is not null || fragment.Ruby is not null)
                {
                    return;
                }

                if (fragment.Length == 0)
                {
                    continue;
                }

                var text = fragment.Text;
                var at = fragment.Length - 1;

                while (at > 0 && Char.IsLowSurrogate(text[at]))
                {
                    at--;
                }

                if (at < 0 || _owner.CodeAt(fragment.AutoSpaces, fragment.Start + at) != '\0')
                {
                    return;
                }

                _owner.AppendMark(ref fragment.AutoSpacesRef, fragment.Start + at, AutoSpaceReplace);
                fragment.Width -= delta;
                Width -= delta;
                return;
            }
        }

        /// <summary>
        /// Marks the last character the line holds as one a spaced boundary follows, which is what the
        /// merged run needs to draw the room the boundary between its pieces added.
        /// </summary>
        private void MarkLastBoundary(TextFragment fragment, Int32 before)
        {
            var text = fragment.Text;
            var at = Math.Max(0, before - 1);

            while (at > 0 && Char.IsLowSurrogate(text[at]))
            {
                at--;
            }

            if (at < text.Length)
            {
                _owner.AppendMark(ref fragment.AutoSpacesRef, fragment.Start + at, AutoSpaceAfter);
            }
        }

        public void Add(
            String source,
            Int32 start,
            Int32 length,
            Double width,
            ComputedStyle style,
            Box box,
            WritingModeInfo flow,
            InlineAncestor? inline)
        {
            var isSpace = TextChunker.IsCollapsible(source, start, length);

            // CSS Text 3 §4.1.1: a collapsible space at the head of a line is removed - the line begins
            // with what follows the break, not with the white space that stood around it. A mode that
            // keeps its spaces (pre, pre-wrap, break-spaces) keeps them wherever the line starts.
            if (isSpace && _fragments.Count == 0 && style.CollapsesSpaces)
            {
                return;
            }

            // White space that follows white space across an element's edge collapses into the space
            // already on the line: the pieces come from two nodes, so no node's own collapse saw them
            // together, and the line is where their neighbourhood finally shows up.
            if (isSpace && _fragments.Count > 0 && EndsInCollapsibleSpace(_fragments[^1]))
            {
                return;
            }

            var previous = Previous;
            _owner._trimMarks.Clear();
            _owner._autoMarks.Clear();

            var trim = PlanTrim(
                _measurer,
                source,
                start,
                length,
                style,
                flow,
                previous,
                !HasContent,
                _startsParagraph,
                _owner._trimMarks);

            if (trim.RetroDelta != 0)
            {
                TrimLast(trim.RetroDelta, trim.RetroCode);
                _previousKnown = false;

                // The trim just marked the character this looks back on, and a mark is part of what
                // it answers: only the walk has to be taken again, and only here.
                previous = Previous;
            }

            width -= trim.BoundaryDelta;

            var auto = PlanAutoSpace(
                _measurer,
                source,
                start,
                length,
                style,
                flow,
                previous,
                _owner._autoMarks);

            if (auto.RetroDelta != 0)
            {
                ReplaceLastSpace(auto.RetroDelta);
            }

            // The room of a boundary stands between the run already on the line and the one arriving,
            // so it widens the run behind it: the cursor that places the new run is the sum of the
            // rooms before it. A merge into that run is the same edit, and there the mask has to
            // carry it too, because one run draws both pieces.
            if (auto.BoundaryDelta != 0)
            {
                if (auto.Inside)
                {
                    width += auto.BoundaryDelta;
                }
                else
                {
                    WidenLast(auto.BoundaryDelta);
                }
            }

            // A line that begins with an opening glyph the property hangs gives up that glyph's
            // advance: it stands outside the line, so the room the line is filled to no longer counts
            // it (CSS Text 3 §8.2.1). The line's close cuts the glyph out and takes the same room off
            // the extent it reports.
            if (!isSpace && !HasInk && HangsStart(style, source, start, inline))
            {
                Width -= MeasureChunk(
                    _measurer, style, source, start,
                    TextClusters.UnitsAt(source, start, start + length), flow);
            }

            Enter(inline);

            // A tab draws nothing: the room it took was the line running on to a stop, not a glyph, so
            // the run is drawn as the spaces it stands between. The width the layout settled on is kept
            // either way; only the characters handed to the painter change.
            var drawn = HasTab(source, start, length) ? ToSpaces(source, start, length) : null;

            if (_fragments.Count > 0)
            {
                var last = _fragments[^1];

                // A run that ended in a tab is one nothing may be written into: the tab was charged the
                // room up to its stop, and the character that follows it stands after that room rather
                // than after the one space the tab is drawn as. Starting a new fragment there is what
                // puts the following text on the stop the layout settled on.
                //
                // Chunks of one text node belong to one run. Keeping them together stops the paint
                // commands - and the decorations drawn for them - from being split per word. Pieces
                // that are drawn differently, a turned word among upright text, are not the same run
                // however they happened to be chunked, and neither are pieces from either side of an
                // inline element's edge.
                if (!last.FromTab &&
                    ReferenceEquals(last.Box, box) && last.Flow.Equals(flow) && ReferenceEquals(last.Inline, inline))
                {
                    // The run grows by the piece added and nothing is copied, because the piece is the
                    // next thing in the same string - which every chunk of one text node is. A run whose
                    // pieces come from two strings is the one case that still has to be written out: a
                    // box with more than one text child, and one merge per element rather than one per
                    // character.
                    var before = last.Length;

                    var sameSource = ReferenceEquals(last.Source, source) && last.Start + last.Length == start;

                    if (drawn is null && sameSource)
                    {
                        last.Length += length;
                    }
                    else
                    {
                        _owner.RebaseMask(ref last.TrimsRef, last.Start);
                        _owner.RebaseMask(ref last.AutoSpacesRef, last.Start);
                        last.MergeInto(last.Text + (drawn ?? source.Substring(start, length)));
                    }

                    _owner.AppendMask(
                        ref last.TrimsRef,
                        _owner._trimMarks,
                        sameSource && drawn is null ? 0 : before - start);

                    // The two pieces are drawn as one run, so the room the boundary added is not a gap
                    // between boxes any more: it rides in the merged run's mask, on the last character
                    // of the piece the run already held.
                    if (auto.BoundaryDelta != 0 && !auto.Inside)
                    {
                        MarkLastBoundary(last, before);
                    }

                    _owner.AppendMask(
                        ref last.AutoSpacesRef,
                        _owner._autoMarks,
                        sameSource && drawn is null ? 0 : before - start);
                    last.Width += width;
                    last.FromTab = drawn is not null;
                    Width += width;
                    Remember(last);
                    return;
                }
            }

            var added = _owner.NewFragment(source, start, length, width, style, box, flow, inline);
            added.Trims = _owner.CaptureMask(_owner._trimMarks);
            added.AutoSpaces = _owner.CaptureMask(_owner._autoMarks);
            added.AutoSpaceRoom = auto.Room;

            if (drawn is not null)
            {
                _owner.RebaseMask(ref added.TrimsRef, start);
                _owner.RebaseMask(ref added.AutoSpacesRef, start);
                added.MergeInto(drawn);
                added.FromTab = true;
            }

            _fragments.Add(added);
            Width += width;
            Remember(added);
        }

        /// <summary>
        /// Gets whether a line may end where a new run begins. A run from another box is shaped and
        /// chunked on its own, so the chunker cannot see the character standing before it: the pair
        /// is asked here, across the box edge. Two letters from either side of an inline element are
        /// one word, and splitting them put "PASS" and "FAIL" on two lines where the page wrote one
        /// word across two elements (contain-paint-table-002).
        /// </summary>
        public Boolean CanBreakBefore(String source, Int32 start, ComputedStyle style)
        {
            // A box that left the flow holds none of the line's text, so the question is asked of the
            // last run the line really carries (the same reason the other look-backs pass such boxes by).
            TextFragment? last = null;

            for (var index = _fragments.Count - 1; index >= 0; index--)
            {
                if (_fragments[index].OutOfFlow)
                {
                    continue;
                }

                last = _fragments[index];
                break;
            }

            if (last is not { } run ||
                !(run.Length > 0 && !run.IsReplaced && run.Ruby is null && run.Atomic is null))
            {
                return true;
            }

            // A run that opens with a space the mode *keeps* stands where no licence reaches: §4.1.3
            // puts the opportunity after every space the mode keeps, never before the first of them, and
            // the licences for breaking within words (§5.2-§5.5) speak of words - the space in front of
            // one is not one. Measured on "X　XX　X" in a four-character box, where the line ends at the
            // second X and the space hangs past the edge, and on `XXX<span>　</span>X`, where it does not
            // begin the next line either. Two spaces meeting across an element's edge are the exception:
            // there the break after the first of them is exactly the place the mode gives, and
            // `line-break: anywhere` sets the whole question aside.
            var opensWithKeptSpace = TextChunker.IsBreakSpace(source[start]) &&
                !TextChunker.IsCollapsible(source[start]);
            var holdsKeptSpace = TextChunker.IsBreakSpace(run.Source[run.Start + run.Length - 1]) &&
                !TextChunker.IsCollapsible(run.Source[run.Start + run.Length - 1]);

            if (opensWithKeptSpace && !holdsKeptSpace && !style.BreaksAnywhere)
            {
                return false;
            }

            // Where the page licensed a break anywhere - `line-break: anywhere`, `overflow-wrap` in
            // either value, `break-spaces`, `word-break: break-all` - the rules between the two
            // characters are set aside, and the edge of an inline element is no place to bring them
            // back.
            if (style.BreaksAnywhere || style.BreaksBetweenLetters || style.WrapsLongWords || style.WrapsInsideSpaces)
            {
                return true;
            }

            var end = run.Start + run.Length;

            return LineBreaks.MayBreak(
                TextChunker.CodePointBefore(run.Source, end),
                TextChunker.CodePointAt(source, start),
                style.WordBreakingRules);
        }

        private static Boolean HasTab(String source, Int32 start, Int32 length) =>            source.IndexOf('\t', start, length) >= 0;

        private static String ToSpaces(String source, Int32 start, Int32 length) =>
            source.Substring(start, length).Replace('\t', ' ');

        // White space only collapses into the run before it where the mode collapses it. `break-spaces`
        // keeps every character of a preserved run, so two of them meeting across an element's edge stay
        // two, which is what makes the run long enough to break inside.
        private static Boolean EndsInCollapsibleSpace(TextFragment fragment) =>
            !fragment.IsReplaced &&
            fragment.Atomic is null &&
            fragment.Ruby is null &&
            fragment.Length > 0 &&
            fragment.Style.CollapsesSpaces &&
            TextChunker.IsCollapsible(fragment.Source, fragment.Start + fragment.Length - 1, 1);

        /// <summary>
        /// Adds a ruby pair, which never joins the run before it: the base has an annotation of its own
        /// and the two are drawn as separate pieces whatever they are set in.
        /// </summary>
        /// <param name="width">The room the pair needs, which is the wider of the base and the annotation.</param>
        /// <param name="baseWidth">The extent the base itself takes, which centres it in that room.</param>
        public void AddRuby(
            String text,
            Double width,
            ComputedStyle style,
            Box box,
            WritingModeInfo flow,
            RubyAnnotation ruby,
            Double baseWidth,
            Double? hang,
            InlineAncestor? inline)
        {
            _previousKnown = false;
            Enter(inline);

            _fragments.Add(_owner.NewFragment(
                text,
                0,
                text.Length,
                width,
                style,
                box,
                flow,
                inline,
                ruby: ruby,
                baseWidth: baseWidth,
                hang: hang));

            Width += width;
        }

        /// <summary>
        /// Moves the line into the chain of inline elements the next run sits in, taking the padding
        /// and border the edges of that move are worth along with it.
        /// </summary>
        private void Enter(InlineAncestor? inline) =>
            Width += Advance(_open, inline, _carried, origin: 0, apply: true, step: null);

        /// <summary>
        /// Closes the line and hands it back, with the extent it takes along the inline axis and the
        /// report of which elements it ends inside.
        /// </summary>
        public Line Build(
            ITextMeasurer measurer,
            Double maxInline,
            HashSet<InlineAncestor>? carried,
            Boolean forced,
            Boolean hyphen)
        {
            TrimTrailingSpaces(measurer);

            var line = _owner.NewLine(carried);
            line.Fragments.AddRange(_fragments);

            // The extent is worked out afresh from the runs the trimming left, because the padding and
            // border a chain change brings in are part of it and a run taken back out takes them with it.
            var open = _buildOpen;
            open.Clear();
            var used = 0.0;

            // Gathered from nothing rather than from zero: a face set on a line shorter than itself has a
            // negative descent as well as a negative ascent, and starting from zero would hold every line
            // to at least the height of the tallest font on it.
            var ascent = Double.NegativeInfinity;
            var descent = Double.NegativeInfinity;
            Double? statedLine = null;
            var onlyText = true;
            var noteBlockSize = 0.0;
            var noteGap = 0.0;

            // How far each fragment reaches above and below the baseline it stands on, kept because a
            // box's alignment is worked out from it once the line knows how far it reached.
            if (_above.Length < line.Fragments.Count)
            {
                _above = new Double[line.Fragments.Count];
                _below = new Double[line.Fragments.Count];
            }

            var above = _above;
            var below = _below;
            Array.Clear(above, 0, line.Fragments.Count);
            Array.Clear(below, 0, line.Fragments.Count);
            var runAscent = Double.NegativeInfinity;
            var runDescent = Double.NegativeInfinity;
            var inFlow = false;

            for (var position = 0; position < line.Fragments.Count; position++)
            {
                var fragment = line.Fragments[position];

                used += Advance(open, fragment.Inline, carried, origin: 0, apply: true, step: null);
                used += fragment.Width;

                if (fragment.OutOfFlow)
                {
                    // A box that left the flow takes no room across the line and reaches nothing above or
                    // below its baseline: it is placed against a containing block of its own, and the line
                    // it stood on is neither wider nor taller for it.
                    continue;
                }

                if (fragment.InterlineGap)
                {
                    above[position] = 0;
                    below[position] = 0;
                    continue;
                }

                if (fragment.InterlineNote is not null || fragment.MarginNote is not null)
                {
                    inFlow = true;
                    above[position] = 0;
                    below[position] = 0;
                    continue;
                }

                inFlow = true;

                // A note occupies room along the line. Its block size joins the line's ascent and
                // descent symmetrically, so a note larger than the base line grows the line around it.
                if (fragment.Note is not null)
                {
                    onlyText = false;
                    noteBlockSize = Math.Max(noteBlockSize, fragment.Note.BlockSize);
                    noteGap = Math.Max(noteGap, fragment.Note.LineGap);
                    continue;
                }

                // A run lifted out of its row by vertical-align reaches past the line-height its
                // text asks for, and that reach is the line's own height rather than an overflow
                // of the face; the clamp below is only for the face's content area.
                if (!IsBaseline(fragment.Style) || fragment.BaselineShift != 0)
                {
                    onlyText = false;
                }

                // An inline element that holds nothing gives the line no height of its own: the line
                // still exists - the room the box takes along it is what keeps the margins around the
                // block from folding through it - and is zero-height otherwise (CSS 2.1 §9.4.2, §10.8).
                if (fragment.Length == 0 &&
                    !fragment.IsReplaced &&
                    fragment.Atomic is null &&
                    fragment.Ruby is null &&
                    CarriesAnInlineEdge(fragment.Inline))
                {
                    above[position] = 0;
                    below[position] = 0;
                    continue;
                }

                // A replaced inline rests its bottom margin edge on the baseline, so it contributes
                // its whole height as ascent and nothing as descent.
                if (fragment.IsReplaced)
                {
                    onlyText = false;
                    // Its bottom margin edge rests on the baseline, so it reaches nothing below it - a
                    // descent of zero, which is not the same as leaving it out where the line gathers
                    // descents from nothing because a run of text may hang above the baseline.
                    above[position] = fragment.ContentHeight;
                    below[position] = 0;
                    ascent = Math.Max(ascent, above[position]);
                    descent = Math.Max(descent, below[position]);
                    continue;
                }

                // An inline-block is as tall as its own content and stands on its own baseline, so the
                // line grows by whatever the box reaches above and below that either way - its margins
                // across the line included, since those are part of the room it takes up there.
                if (fragment.Atomic is { } atomic)
                {
                    onlyText = false;
                    above[position] = atomic.BlockStartMargin + atomic.Ascent;
                    below[position] = atomic.BlockEndMargin + (atomic.BlockSize - atomic.Ascent);
                    ascent = Math.Max(ascent, above[position]);
                    descent = Math.Max(descent, below[position]);
                    continue;
                }

                // The glyphs of one element may come from several fonts, so the metrics are the ones the
                // run is really set with rather than the ones the requested font would give: a line whose
                // characters fell to a fallback is as tall as that fallback (CSS 2.2 §10.8.1).
                var metrics = measurer.GetRunMetrics(fragment.Text, fragment.Style.GetFont());
                var fragmentAscent = metrics.Ascent;
                var fragmentDescent = metrics.Descent;
                var lineHeight = fragment.Style.LineHeight;

                if (lineHeight is { } stated && (statedLine is null || stated > statedLine))
                {
                    statedLine = stated;
                }

                if (lineHeight.HasValue)
                {
                    // Half the leading, negative included: a face taller than the line height the page
                    // asked for overflows a line box that is that height and no taller.
                    var halfLeading = (lineHeight.Value - (fragmentAscent + fragmentDescent)) / 2;
                    fragmentAscent += halfLeading;
                    fragmentDescent += halfLeading;
                }

                // An annotation stands over its base, on the block-start side, but it does not make the
                // line taller than the base's own line-height does (CSS Ruby 1 §3.6): the leading the
                // line already has is where a reading is meant to sit. Only a reading that reaches
                // past that leading - the distance from the base's content edge to the line box's edge -
                // adds the difference, so that stacked lines do not overlap.
                if (fragment.Ruby is { } annotation)
                {
                    onlyText = false;
                    if (annotation.Under)
                    {
                        fragmentDescent += Math.Max(0, annotation.Block - (fragmentDescent - metrics.Descent));
                    }
                    else
                    {
                        fragmentAscent += Math.Max(0, annotation.Block - (fragmentAscent - metrics.Ascent));
                    }
                }
                else if (fragment.Style.Emphasis is { IsSet: true })
                {
                    // Marks are drawn in the manner of a reading, but they take no room of their own:
                    // the line keeps the height its text asks for and the marks stand outside it
                    // (CSS Text Decoration 3 §3.3). A page that wants room for them leaves it with
                    // line-height or padding, as the tests beside a reading do.
                }

                above[position] = fragmentAscent;
                below[position] = fragmentDescent;
                runAscent = Math.Max(runAscent, fragmentAscent);
                runDescent = Math.Max(runDescent, fragmentDescent);
                ascent = Math.Max(ascent, fragmentAscent);
                descent = Math.Max(descent, fragmentDescent);
            }

            // An element the line ends inside may have been cut there, and only the line below can say
            // whether it was: until that is known the end edge it would draw here is counted, so that
            // the line is neither aligned nor broken as if the element had none. MarkSlices takes the
            // edge back off the ones that turn out not to carry on.
            if (open.Count > 0)
            {
                var pending = new HashSet<InlineAncestor>();

                foreach (var item in open)
                {
                    pending.Add(item.Ancestor);
                    used += item.Ancestor.InlineEndInset;
                }

                line.ContinuesToNext = pending;
            }

            // Every line box starts with the container's strut: its own font and line height, a
            // zero-width box standing on the baseline (CSS 2.1 §10.8). Usually the runs on the line ask
            // for as much or more, but a run set at no size - or one whose line height is zero - does
            // not, and the line keeps the room the block's text would have taken.
            // A line holding an inline-block, a picture or a ruby pair already says how far it reaches
            // on its own; the corpus's buttons and badges are measured without the strut's descent added
            // under the box that stands on the baseline, so the strut answers only for a line of text.
            // A column's width comes from the runs set down it, each standing the block's font and
            // line height as it inherits them; the strut is asked only where a break left a line with
            // no run to speak for it, which no run on that line can make wider or narrower.
            // A line holding nothing but the anchors of empty inline elements exists for the room they
            // take along it and for nothing else: it takes no height from the strut (CSS 2.1 §9.4.2).
            var anchorsOnly = HasContent && !_fragments.Exists(fragment =>
                !fragment.OutOfFlow &&
                (fragment.Length > 0 || fragment.IsReplaced || fragment.Atomic is not null ||
                    fragment.Ruby is not null || fragment.Note is not null ||
                    fragment.InterlineNote is not null));

            var textual = !anchorsOnly && (_vertical
                ? _fragments.Count == 0 ||
                    _fragments.TrueForAll(fragment => fragment.OutOfFlow || fragment.Note is not null)
                : _fragments.TrueForAll(fragment =>
                    !fragment.IsReplaced && fragment.Atomic is null && fragment.Ruby is null));

            var strut = textual && _fallback is { } fallback && (HasContent || forced)
                ? Stretched(measurer, fallback)
                : ((Double Ascent, Double Descent)?)null;

            if (strut is { } strutMetrics)
            {
                ascent = Double.IsNegativeInfinity(ascent)
                    ? strutMetrics.Ascent
                    : Math.Max(ascent, strutMetrics.Ascent);
                descent = Double.IsNegativeInfinity(descent)
                    ? strutMetrics.Descent
                    : Math.Max(descent, strutMetrics.Descent);

                // The block's own line-height is a floor for the line, however small the runs on it
                // ask to be (CSS 2.1 §10.8.1). It is the strut that carries that answer.
                var strutHeight = strutMetrics.Ascent + strutMetrics.Descent;

                // Only a line height the block *stated* also caps the line: `normal` says the line is as
                // tall as the faces on it, so a block written at no size must not shrink the line its
                // runs ask for - which is how a `pre` at `font-size: 0` came to lay two twelve-pixel
                // lines on top of one another (CSS 2.1 §10.8.1).
                if (_fallback?.LineHeight is not null)
                {
                    statedLine = statedLine is { } stated ? Math.Max(stated, strutHeight) : strutHeight;
                }
            }
            else if (Double.IsNegativeInfinity(ascent))
            {
                // A line holding nothing in flow - one whose only content left the flow, and which
                // exists only to say where that box's static position is - is a zero-height line box
                // (CSS 2.1 §9.4.2), unless a break ended it (the strut then keeps it standing).
                ascent = 0;
                descent = 0;
            }

            // The line box is grown from the boxes as they end up standing (CSS 2.1 §10.8.1): a run
            // raised by `super`, or aligned to the `top` of the line, lifts the height the line takes,
            // and a lowered one reaches below it. The reference each alignment is taken against is the
            // line as it was gathered above, before any box on it was moved.
            var alignedReach = Double.NegativeInfinity;
            var alignedDepth = Double.NegativeInfinity;
            Dictionary<InlineAncestor, Double>? bands = null;

            if (inFlow)
            {
                for (var position = 0; position < line.Fragments.Count; position++)
                {
                    var fragment = line.Fragments[position];

                    if (fragment.OutOfFlow)
                    {
                        continue;
                    }

                    if (fragment.InterlineGap || fragment.InterlineNote is not null ||
                        fragment.MarginNote is not null)
                    {
                        continue;
                    }

                    if (IsBaseline(fragment.Style) && fragment.Inline?.AnyAlignment != true)
                    {
                        // Nothing asks for this box to stand anywhere but on the baseline the line
                        // shares, so it stays where the gathering above put it.
                        alignedReach = Math.Max(alignedReach, above[position]);
                        alignedDepth = Math.Max(alignedDepth, below[position]);
                        continue;
                    }

                    // Each box on the chain stands on the baseline of the box around it, so an aligned
                    // element carries both the text inside it and the bands of the elements within it -
                    // and the innermost box is the one the run's own <c>vertical-align</c> was written
                    // on, which is why it is counted once here rather than again for the run.
                    // `top` and `bottom` are the exception: they are taken against the line box rather
                    // than against the parent, so they say where the chain stands instead of adding.
                    var parents = Chain(fragment.Inline);
                    var running = 0.0;

                    if (parents.Count == 0 && fragment.Box.Element is not null)
                    {
                        // A box with an element of its own is aligned by what that element says. Text
                        // sitting straight in the block has no such box: its style is the block's, and a
                        // block's own <c>vertical-align</c> places the block - a cell in its row - and
                        // never lifts the words the block holds (CSS 2.1 §10.8.1, measured against Chrome
                        // on a div and a table cell that both say `middle`).
                        running = Alignment(
                            measurer, fragment.Style, _fallback, above[position], below[position], ascent, descent);
                    }

                    for (var step = parents.Count - 1; step >= 0; step--)
                    {
                        var ancestor = parents[step];
                        var own = Alignment(
                            measurer,
                            ancestor.Box.Style,
                            ancestor.Parent?.Box.Style ?? _fallback,
                            ancestor.Ascent,
                            ancestor.Descent,
                            ascent,
                            descent);

                        running = own == 0
                            ? running
                            : IsAtTheLineEdge(ancestor.Box.Style)
                                ? own
                                : running + own;

                        if (running != 0)
                        {
                            bands ??= new Dictionary<InlineAncestor, Double>();
                            bands[ancestor] = running;
                        }
                    }

                    fragment.BaselineShift = running;
                    alignedReach = Math.Max(alignedReach, above[position] - running);
                    alignedDepth = Math.Max(alignedDepth, below[position] + running);
                    ReturnAncestorList(parents);
                }
            }

            // The line box is grown from the boxes as they end up standing, not from where they stood
            // before <c>vertical-align</c> moved them (CSS 2.1 §10.8.1). A box aligned to the top of the
            // line hangs its whole height below that edge, so the reach and depth gathered above are the
            // reference the alignment was measured against, not the line's final extent.
            if (!Double.IsNegativeInfinity(alignedReach))
            {
                // A run of text still reaches as far as its face does even when `vertical-align` lifts
                // it, which is the room the line keeps under a superscript; a box that stands on its
                // own baseline is measured only where the alignment leaves it.
                ascent = Math.Max(alignedReach, runAscent);
                descent = Math.Max(alignedDepth, runDescent);

                if (strut is { } band)
                {
                    ascent = Math.Max(ascent, band.Ascent);
                    descent = Math.Max(descent, band.Descent);
                }
            }

            // A note stands across the line rather than on its baseline. It therefore contributes its
            // total block size to the line's centre line, not half of it above and below the text
            // baseline: an asymmetric font baseline must not make a note that exactly fills the line
            // grow it. A larger note adds the same room at both edges, keeping its margin box centred
            // in the line that now contains it.
            var lineBlockSize = ascent + descent;

            if (noteBlockSize > lineBlockSize)
            {
                // The note first expands the line to its own margin-box block size. The body's
                // inter-line gap is then added outside that expanded line, once on each edge.
                var expanded = noteBlockSize;
                var withGap = expanded + (noteGap * 2);
                var extra = (withGap - lineBlockSize) / 2;
                ascent += extra;
                descent += extra;
            }

            // A line the page's own soft hyphen ended draws the mark where the character stood: the
            // hyphenate character takes its place and its room, and the run keeps reading as the word
            // the page spelled (CSS Text 3 §6.2). A line that ends anywhere else keeps the hyphen as the
            // invisible character it is, taking nothing and drawing nothing.
            if (hyphen)
            {
                // The mark stands in the run the word was written in, which is the last run that holds
                // text of this line: a box that left the flow may stand after it and is not part of the
                // word (the same reason the look-backs pass such boxes by).
                TextFragment? last = null;
                var at = -1;

                for (var index = _fragments.Count - 1; index >= 0; index--)
                {
                    if (_fragments[index].OutOfFlow)
                    {
                        continue;
                    }

                    last = _fragments[index];
                    at = index;
                    break;
                }

                if (last is { } run &&
                    run.Length > 0 &&
                    !run.IsReplaced &&
                    run.Ruby is null &&
                    run.Atomic is null &&
                    run.Source[run.Start + run.Length - 1] == '­' &&
                    !String.IsNullOrEmpty(run.Style.HyphenateCharacter))
                {
                    // A run of soft hyphens stands where the page could have cut the word once: the whole
                    // run gives way to the single mark the break draws.
                    var text = run.Text;
                    var trailing = 0;

                    while (trailing < text.Length && text[text.Length - 1 - trailing] == (Char)0x00AD)
                    {
                        trailing++;
                    }

                    var mark = HyphenMark(run.Style, run.Flow);
                    var shy = text.Substring(text.Length - trailing, trailing);
                    var gained = MeasureChunk(measurer, run.Style, mark, 0, mark.Length, run.Flow) -
                        MeasureChunk(measurer, run.Style, shy, 0, shy.Length, run.Flow);

                    run.MergeInto(text[..^trailing] + mark);
                    run.Width += gained;
                    used += gained;

                    // The mark now stands in the run's text, so the line is as tall as the character it
                    // draws: a column's upright hyphen is taller than the letters beside it, and the
                    // line that does not count it stands a little narrower than the same word spelled
                    // with the hyphen written out (hyphens-vertical-001).
                    var drawnMark = measurer.GetRunMetrics(mark, run.Style.GetFont());
                    var markAscent = drawnMark.Ascent;
                    var markDescent = drawnMark.Descent;

                    if (run.Style.LineHeight is { } markLine)
                    {
                        var half = (markLine - (markAscent + markDescent)) / 2;
                        markAscent += half;
                        markDescent += half;
                    }

                    above[at] = Math.Max(above[at], markAscent);
                    below[at] = Math.Max(below[at], markDescent);
                    ascent = Math.Max(ascent, above[at]);
                    descent = Math.Max(descent, below[at]);
                }
            }

            line.BandShifts = bands;
            line.Ascent = ascent;
            line.Height = ascent + descent;

            // A line of text is as tall as the line-height it asks for, however much taller the
            // face's own content area is: the glyphs overflow the line rather than growing it
            // (CSS 2.1 §10.8.1). A replaced box, an atomic inline or a ruby reading is content of
            // its own and is left to grow the line as it does.
            if (onlyText && statedLine is { } limit && line.Height > limit)
            {
                line.Height = limit;
            }

            // The end of the line is settled once every line is built: the trim a closing punctuation
            // gives up and the glyph a hanging value takes outside the line are decisions about the
            // line's own edge, and which of the two applies depends on whether this is the last line.
            line.Hanging = HangingTrail(measurer);
            line.Used = used - line.Hanging;
            line.Indent = _indent;
            line.Governing = Governing(line.Fragments.Count > 0 ? line.Fragments[0] : null);
            line.Alignment = line.Governing?.TextAlign ?? CssInitialValues.TextAlign;
            line.Offset = Aligned(line, maxInline);

            return line;
        }

        /// <summary>
        /// Gets the strut of a line box: the container's font and line height, with the leading the
        /// line height puts above and below the face it is set in.
        /// </summary>
        private static (Double Ascent, Double Descent) Stretched(ITextMeasurer measurer, ComputedStyle style)
        {
            var metrics = measurer.GetMetrics(style.GetFont());
            var ascent = metrics.Ascent;
            var descent = metrics.Descent;

            if (style.LineHeight is { } lineHeight)
            {
                var halfLeading = (lineHeight - (ascent + descent)) / 2;
                ascent += halfLeading;
                descent += halfLeading;
            }

            return (ascent, descent);
        }

        /// <summary>
        /// Gets the style the line is aligned by. A box the line carries whole brings its own
        /// <c>text-align</c>, but that states how the text inside the box is set and not where the box
        /// stands, so the style the line was built with governs there; every other line takes the
        /// alignment from what is on it, which is how a <c>::first-line</c> rule reaches the line it
        /// styles (CSS Text 3 §6.2).
        /// </summary>
        private ComputedStyle? Governing(TextFragment? first) =>
            first is not { } fragment
                ? _fallback
                : fragment.Atomic is not null || fragment.IsReplaced || fragment.Note is not null
                    ? _fallback ?? fragment.Style
                    : fragment.Style;

        /// <summary>
        /// Gets the width of the white space the line trails into where its mode hangs it, marking each
        /// run it took the room off so that the painter draws the word without it: a `pre-wrap` line
        /// keeps those spaces rather than drawing them away, and they take none of its advance
        /// (CSS Text 3 §4.1.3) - so the room it was given is shared over what does not hang.
        /// </summary>
        private Double HangingTrail(ITextMeasurer measurer)
        {
            var hanging = 0.0;

            for (var index = _fragments.Count - 1; index >= 0; index--)
            {
                var fragment = _fragments[index];

                if (fragment.IsReplaced ||
                    fragment.Atomic is not null ||
                    fragment.Ruby is not null ||
                    fragment.Length == 0 ||
                    !fragment.Style.WhiteSpace.Equals("pre-wrap", StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }

                var trailing = fragment.TrailingSpaces();

                if (trailing == 0)
                {
                    break;
                }

                fragment.Hanging = trailing;

                // A fragment that is nothing but the spaces hanging is worth what the layout measured
                // it at, which for a run carrying tabs is the distance to the last stop rather than a
                // count of spaces.
                hanging += trailing < fragment.Length
                    ? MeasureChunk(
                        measurer,
                        fragment.Style,
                        fragment.Source,
                        fragment.Start + fragment.Length - trailing,
                        trailing,
                        fragment.Flow)
                    : fragment.Width;

                if (trailing < fragment.Length)
                {
                    break;
                }
            }

            return hanging;
        }

        /// <summary>
        /// Takes the collapsible spaces a line trails into off, which a line never draws at its end.
        /// Only the runs themselves are touched: the extent is worked out from what is left when the
        /// line is built.
        /// </summary>
        private void TrimTrailingSpaces(ITextMeasurer measurer)
        {
            while (_fragments.Count > 0)
            {
                var last = _fragments[^1];

                // A pair is not a run of text that can be shortened: its base belongs to the annotation
                // drawn over it, so trailing space inside one is left where it is.
                if (last.Ruby is not null)
                {
                    break;
                }

                // White space a preformatted mode preserves is not taken off: it hangs at the end of the
                // line it was written on rather than being removed, and a line holding nothing else is
                // still a line box at the height of its strut (CSS Text 3 §4.1.3). Only the modes that
                // collapse their spaces lose the run at the line's end.
                if (!last.Style.CollapsesSpaces)
                {
                    break;
                }

                if (TextChunker.IsCollapsible(last.Source, last.Start, last.Length))
                {
                    _fragments.RemoveAt(_fragments.Count - 1);
                    continue;
                }

                var trailing = last.TrailingSpaces();

                if (trailing == 0)
                {
                    break;
                }

                if (trailing >= last.Length)
                {
                    _fragments.RemoveAt(_fragments.Count - 1);
                    continue;
                }

                // Shortening the run is moving its end, not cutting a string: the characters the line
                // drops were never copied out of the text node to begin with.
                last.Length -= trailing;
                last.Width = MeasureChunk(measurer, last.Style, last.Source, last.Start, last.Length, last.Flow);
                break;
            }
        }
    }
}

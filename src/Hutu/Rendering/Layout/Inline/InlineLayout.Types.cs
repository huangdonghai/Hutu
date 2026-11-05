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
    /// <summary>A piece of a text node that shares one orientation and is measured and drawn as one.</summary>
    private readonly struct OrientedRun
    {
        private OrientedRun(String source, Int32 start, Int32 length, Boolean upright, Boolean combined)
        {
            Source = source;
            Start = start;
            Length = length;
            IsUpright = upright;
            IsCombined = combined;
        }

        /// <summary>Gets the string the run is a piece of.</summary>
        public String Source { get; }

        /// <summary>Gets where in that string the run begins.</summary>
        public Int32 Start { get; }

        /// <summary>Gets how many characters the run takes.</summary>
        public Int32 Length { get; }

        /// <summary>
        /// Gets the run as a string, which is what the measurer speaks: made on asking, because most
        /// runs are only ever advanced over and never handed to anything that wants the characters.
        /// </summary>
        public String Text => Source.Substring(Start, Length);

        public Boolean IsUpright { get; }

        /// <summary>Gets whether the run is set as a single upright box rather than turned.</summary>
        public Boolean IsCombined { get; }

        public static OrientedRun Combined(String source, Int32 start, Int32 length) => new(source, start, length, true, true);

        /// <summary>Gets the whole of a range along a line, which is not split by orientation at all.</summary>
        public static OrientedRun Line(String source, Int32 start, Int32 length) => new(source, start, length, false, false);

        /// <summary>
        /// Gets a piece by the one distinction a column draws on it: upright characters take their em
        /// box and are drawn one to a cell, turned ones take the width they had along a line.
        /// </summary>
        public static OrientedRun Of(String source, Int32 start, Int32 length, Boolean upright) =>
            new(source, start, length, upright, false);

        /// <summary>
        /// Gets the distance the run takes along the inline axis of the column it sits in: an upright
        /// character takes its em box, a turned run the width it would have had in a line, and a
        /// combined run the single em it is compressed into.
        /// </summary>
        public Double Advance(FontStyle font, ITextMeasurer measurer, Double fontSize, WritingModeInfo flow)
        {
            if (IsCombined)
            {
                return fontSize;
            }

            if (!IsUpright)
            {
                return measurer.MeasureAdvance(Text, font, flow);
            }

            var total = 0.0;

            // Walked over the source rather than over a string of this run's: an upright run is measured
            // character by character, and the answer is the character's and the font's, so neither the
            // character nor a string holding it is made afresh to ask for it. It is walked by code point,
            // because one character of the text is one cell of the column however many of the string's
            // characters it takes to write it.
            for (var index = Start; index < Start + Length;)
            {
                var codePoint = TextChunker.CodePointAt(Source, index);
                total += measurer.MeasureUprightStep(codePoint, font, flow);

                index += TextChunker.UnitsAt(Source, index);
            }

            return total;
        }
    }

    /// <summary>An oriented run together with the advance it was measured to, ready for the builder.</summary>
    private readonly struct MeasuredRun
    {
        public MeasuredRun(String source, Int32 start, Int32 length, Double width, WritingModeInfo flow, Boolean isWhiteSpace)
        {
            Source = source;
            Start = start;
            Length = length;
            Width = width;
            Flow = flow;
            IsWhiteSpace = isWhiteSpace;
        }

        /// <summary>Gets the string the run is a piece of, with the run's own place in it.</summary>
        public String Source { get; }

        public Int32 Start { get; }

        public Int32 Length { get; }

        public Double Width { get; }

        public WritingModeInfo Flow { get; }

        public Boolean IsWhiteSpace { get; }
    }

    private readonly struct InlineItem
    {
        private InlineItem(
            String? text,
            ComputedStyle style,
            Box box,
            InlineAncestor? inline,
            Boolean isBreak,
            Boolean isReplaced,
            String? annotationText = null,
            ComputedStyle annotationStyle = default,
            Box? annotationBox = null,
            Boolean atomic = false,
            Boolean outOfFlow = false,
            Boolean empty = false,
            NoteSource? noteSource = null,
            InterlineNoteSource? interlineNote = null,
            InterlineFlowAnchor? interlineAnchor = null,
            MarginNoteSource? marginNote = null,
            WritingModeInfo? flow = null)
        {
            Text = text;
            Style = style;
            Box = box;
            Inline = inline;
            IsBreak = isBreak;
            IsReplaced = isReplaced;
            AnnotationText = annotationText;
            AnnotationStyle = annotationStyle;
            AnnotationBox = annotationBox;
            IsAtomic = atomic;
            IsOutOfFlow = outOfFlow;
            IsEmpty = empty;
            NoteSource = noteSource;
            InterlineNote = interlineNote;
            InterlineAnchor = interlineAnchor;
            MarginNote = marginNote;
            Flow = flow;
        }

        public String? Text { get; }

        public ComputedStyle Style { get; }

        public Box Box { get; }

        /// <summary>
        /// Gets the innermost inline element the item sits inside, or null when it sits in none. The
        /// rest of the chain is reached through <see cref="InlineAncestor.Parent"/>.
        /// </summary>
        public InlineAncestor? Inline { get; }

        public Boolean IsBreak { get; }

        public Boolean IsReplaced { get; }

        /// <summary>
        /// Gets whether the item is an inline-block: an atomic box the line places whole, whose size is
        /// the layout's own to work out rather than something the box resolves by itself.
        /// </summary>
        public Boolean IsAtomic { get; }

        /// <summary>
        /// Gets whether the item is a box that left the flow. It stands on the line only to say where it
        /// would have been: it takes no room, and it is placed against a containing block of its own
        /// rather than drawn as part of the line.
        /// </summary>
        public Boolean IsOutOfFlow { get; }

        /// <summary>
        /// Gets whether the item is an inline element with nothing inside it that takes room: the box
        /// stands on the line for the margins, borders and padding it carries along it, and draws no
        /// text (CSS 2.1 §9.4.2, §8.1).
        /// </summary>
        public Boolean IsEmpty { get; }

        /// <summary>Gets the source of the inline note this item carries, or null for ordinary content.</summary>
        public NoteSource? NoteSource { get; }

        /// <summary>Gets the interline note whose zero-width position this item marks.</summary>
        public InterlineNoteSource? InterlineNote { get; }

        /// <summary>Gets the midpoint target that begins an interline note in its shared flow.</summary>
        public InterlineFlowAnchor? InterlineAnchor { get; }

        /// <summary>Gets the margin note whose zero-width declaration this item marks.</summary>
        public MarginNoteSource? MarginNote { get; }

        /// <summary>Gets the flow this run uses where it differs from its source box's flow.</summary>
        public WritingModeInfo? Flow { get; }

        /// <summary>Gets the annotation of a ruby item, or null when the item carries none.</summary>
        public String? AnnotationText { get; }

        /// <summary>Gets the style the annotation is set in, which the <c>rt</c> element shrank for it.</summary>
        public ComputedStyle AnnotationStyle { get; }

        /// <summary>Gets the box the annotation came from, so its own geometry can be traced back to the tree.</summary>
        public Box? AnnotationBox { get; }

        /// <summary>Gets whether the item is a ruby pair: base text with an annotation over it.</summary>
        public Boolean IsRuby => AnnotationText is not null;

        public static InlineItem CreateText(
            String text,
            ComputedStyle style,
            Box box,
            InlineAncestor? inline,
            WritingModeInfo? flow = null) =>
            new(text, style, box, inline, false, false, flow: flow);

        public static InlineItem CreateBreak(ComputedStyle style, Box box, InlineAncestor? inline) =>
            new(null, style, box, inline, true, false);

        public static InlineItem CreateReplaced(Box box, InlineAncestor? inline) =>
            new(null, box.Style, box, inline, false, true);

        public static InlineItem CreateAtomic(Box box, InlineAncestor? inline) =>
            new(null, box.Style, box, inline, false, false, atomic: true);

        public static InlineItem CreateOutOfFlow(Box box, InlineAncestor? inline) =>
            new(null, box.Style, box, inline, false, false, outOfFlow: true);

        public static InlineItem CreateEmpty(Box box, InlineAncestor inline) =>
            new(String.Empty, box.Style, box, inline, false, false, empty: true);

        public static InlineItem CreateRuby(
            String text,
            ComputedStyle style,
            Box box,
            String annotationText,
            ComputedStyle annotationStyle,
            Box annotationBox,
            InlineAncestor? inline) =>
            new(text, style, box, inline, false, false, annotationText, annotationStyle, annotationBox);

        public static InlineItem CreateNote(NoteSource source, Box box, InlineAncestor? inline) =>
            new(null, box.Style, box, inline, false, false, noteSource: source);

        public static InlineItem CreateInterlineNote(NoteSource source, Box box, InlineAncestor? inline) =>
            new(null, box.Style, box, inline, false, false,
                interlineNote: new InterlineNoteSource(source, box));

        public static InlineItem CreateInterlineAnchor(Int32 slot, Double position, Box box) =>
            new(null, box.Style, box, null, false, false,
                interlineAnchor: new InterlineFlowAnchor(slot, position));

        public static InlineItem CreateMarginNote(Box box, InlineAncestor? inline) =>
            new(null, box.Style, box, inline, false, false,
                marginNote: new MarginNoteSource(box));
    }

    private struct FragmentNode
    {
        public FragmentNode(
            String source,
            Int32 start,
            Int32 length,
            Double width,
            ComputedStyle style,
            Box box,
            WritingModeInfo flow,
            InlineAncestor? inline,
            Boolean isReplaced = false,
            Double contentHeight = 0,
            RubyAnnotation? ruby = null,
            Double baseWidth = 0,
            AtomicInline? atomic = null,
            Boolean outOfFlow = false,
            NoteRun? note = null,
            InterlineNoteSource? interlineNote = null,
            Boolean interlineGap = false,
            Double? interlinePosition = null,
            MarginNoteSource? marginNote = null,
            Double? hang = null)
        {
            Reset(
                source, start, length, width, style, box, flow, inline, isReplaced,
                contentHeight, ruby, baseWidth, atomic, outOfFlow, note, interlineNote,
                interlineGap, interlinePosition, marginNote, hang);
        }

        public void Reset(
            String source,
            Int32 start,
            Int32 length,
            Double width,
            ComputedStyle style,
            Box box,
            WritingModeInfo flow,
            InlineAncestor? inline,
            Boolean isReplaced = false,
            Double contentHeight = 0,
            RubyAnnotation? ruby = null,
            Double baseWidth = 0,
            AtomicInline? atomic = null,
            Boolean outOfFlow = false,
            NoteRun? note = null,
            InterlineNoteSource? interlineNote = null,
            Boolean interlineGap = false,
            Double? interlinePosition = null,
            MarginNoteSource? marginNote = null,
            Double? hang = null)
        {
            Source = source;
            Start = start;
            Length = length;
            Width = width;
            Style = style;
            Box = box;
            Flow = flow;
            Inline = inline;
            IsReplaced = isReplaced;
            ContentHeight = contentHeight;
            Ruby = ruby;
            BaseWidth = baseWidth;
            Atomic = atomic;
            OutOfFlow = outOfFlow;
            Note = note;
            InterlineNote = interlineNote;
            InterlineGap = interlineGap;
            InterlinePosition = interlinePosition;
            MarginNote = marginNote;
            Hang = hang;
            RightToLeft = null;
            Level = -1;
            FromTab = false;
            Hanging = 0;
            HangSide = 0;
            Trims = default;
            AutoSpaces = default;
            AutoSpaceRoom = 0;
            Measured = null;
            Justify = 0;
            Expansions = 0;
            BaselineShift = 0;
        }

        /// <summary>Gets whether the fragment is a box that left the flow, placed but not drawn here.</summary>
        public Boolean OutOfFlow { get; private set; }

        /// <summary>Gets the two-line inline note this fragment draws, or null for ordinary content.</summary>
        public NoteRun? Note { get; private set; }

        /// <summary>Gets the interline note whose zero-width position this fragment marks.</summary>
        public InterlineNoteSource? InterlineNote { get; private set; }

        /// <summary>Gets whether this zero-length fragment is fixed positioning room in an interline flow.</summary>
        public Boolean InterlineGap { get; private set; }

        /// <summary>Gets the line-relative position a hoisted interline-note marker stands at.</summary>
        public Double? InterlinePosition { get; private set; }

        /// <summary>Gets the margin note whose zero-width declaration this fragment marks.</summary>
        public MarginNoteSource? MarginNote { get; private set; }

        /// <summary>
        /// Gets or sets whether the run reads right to left at the level the bidi algorithm gave it, or
        /// null for a run the algorithm never looked at. It is the direction the text is displayed in,
        /// which the characters alone do not say when an override asks for the other one.
        /// </summary>
        public Boolean? RightToLeft { get; set; }

        /// <summary>
        /// Gets or sets the bidi embedding level the run was resolved at, or -1 where it was never
        /// asked for - a box that is not text has no level of its own.
        /// </summary>
        public Int32 Level { get; set; } = -1;

        /// <summary>
        /// Gets the annotation that goes on the block-start side of this fragment's text, or null when
        /// the fragment is ordinary text.
        /// </summary>
        public RubyAnnotation? Ruby { get; private set; }

        /// <summary>
        /// Gets the extent the base text itself takes along the inline axis. For a ruby fragment the
        /// fragment is as long as the wider of its two parts, and this is what centres the base inside
        /// the room the annotation needed.
        /// </summary>
        public Double BaseWidth { get; private set; }

        /// <summary>
        /// Gets how far a ruby's base stands inside the room the pair takes, where the reading has hung
        /// over the text beside it; null where the pair's halves are simply centred on each other.
        /// </summary>
        public Double? Hang { get; private set; }

        /// <summary>
        /// Gets the string the fragment's text is a piece of. A fragment whose pieces came from two
        /// different strings carries a string of its own, which is the one case a merge has to write
        /// out - and it is one per element rather than one per character.
        /// </summary>
        private String _source = String.Empty;
        private Int32 _start;
        private Int32 _length;
        private Int32 _trailingSoftHyphens = -1;
        private String? _text;

        public String Source
        {
            get => _source;
            set
            {
                _source = value;
                _trailingSoftHyphens = -1;
                _text = null;
            }
        }

        /// <summary>Gets where in <see cref="Source"/> the fragment's text begins.</summary>
        public Int32 Start
        {
            get => _start;
            set
            {
                _start = value;
                _trailingSoftHyphens = -1;
                _text = null;
            }
        }

        /// <summary>Gets how many characters the fragment holds.</summary>
        public Int32 Length
        {
            get => _length;
            set
            {
                _length = value;
                _trailingSoftHyphens = -1;
                _text = null;
            }
        }

        /// <summary>
        /// Gets or sets whether the fragment's run ends in a tab. A tab is drawn as one space but was
        /// charged the room up to its stop, so a run written into it would put its first glyph where the
        /// space ends rather than where the stop does.
        /// </summary>
        public Boolean FromTab { get; set; }

        /// <summary>
        /// Gets or sets how many of the fragment's trailing characters hang past the line's advance:
        /// the spaces a <c>pre-wrap</c> line trails into are neither room the line takes nor ink it
        /// draws, so the painter is handed the run without them.
        /// </summary>
        public Int32 Hanging { get; set; }

        /// <summary>
        /// Gets or sets the edge of the line the fragment hangs from - <see cref="HangStart"/> or
        /// <see cref="HangEnd"/> - or zero when it hangs from none. The glyph stands outside the room
        /// the line was measured at, which is what the line's alignment and justification read
        /// (CSS Text 3 §8.2.1).
        /// </summary>
        public Int32 HangSide { get; set; }

        /// <summary>
        /// Gets the fragment's text as a string, which is what the node the painter draws out of wants.
        /// Asked once per fragment, when the line is placed - not once per chunk of it, as it was while
        /// the chunks were being appended one character at a time.
        /// </summary>
        public String Text => _text ??= Source.Substring(Start, Length);

        /// <summary>
        /// Gets how many soft hyphens the fragment ends with. The answer is kept because a line asks it
        /// again as it grows, and reading it off the source avoids making a string for the tail.
        /// </summary>
        public Int32 TrailingSoftHyphens
        {
            get
            {
                if (_trailingSoftHyphens < 0)
                {
                    var count = 0;

                    for (var index = _start + _length - 1;
                         index >= _start && _source[index] == (Char)0x00AD;
                         index--)
                    {
                        count++;
                    }

                    _trailingSoftHyphens = count;
                }

                return _trailingSoftHyphens;
            }
        }

        /// <summary>Takes the fragment's text to be the whole of the given string.</summary>
        public void MergeInto(String text)
        {
            Source = text;
            Start = 0;
            Length = text.Length;
        }

        /// <summary>
        /// Takes characters off the fragment's start, leaving it to draw the rest: a glyph hanging
        /// from the line's start edge is cut off the run it began rather than copied out of it.
        /// </summary>
        public void TrimStart(Int32 length)
        {
            Start += length;
            Length -= length;
        }

        /// <summary>Gets how many spaces the fragment ends with, which is what a line does not draw.</summary>
        public Int32 TrailingSpaces()
        {
            var count = 0;

            for (var index = Start + Length - 1; index >= Start && Source[index] == ' '; index--)
            {
                count++;
            }

            return count;
        }

        public Double Width { get; set; }

        /// <summary>Gets or sets the sparse marks the line set half-width: the source offsets whose
        /// character is a trimmed opening punctuation, closing punctuation or middle dot (CSS Text 4
        /// §8.5). An empty span is a run with nothing to trim.</summary>
        public MaskSpan Trims;

        /// <summary>Gets or sets the sparse boundaries the property widened: the source offset where
        /// a space follows the unit, or the source offset of the space a boundary replaces (CSS Text 4
        /// §8.4). An empty span is a run with no widened boundary.</summary>
        public MaskSpan AutoSpaces;

        /// <summary>
        /// Gets or sets the room one spaced boundary of the fragment adds, which is the eighth of the
        /// font's ideograph advance the layout measured.
        /// </summary>
        public Double AutoSpaceRoom { get; set; }

        /// <summary>
        /// Gets the width the fragment was measured at, before any justified room was folded into it, or
        /// nothing until the first arrangement asks for it: a fragment joins its neighbours while the line
        /// is being built, and only when the line is arranged is its own width settled.
        /// </summary>
        public Double? Measured { get; private set; }

        /// <summary>
        /// Gets the share of the line's unused room owed to every place between its words that this
        /// fragment holds, which the width above is grown by and the painter repeats after each of
        /// those places so that the glyphs land where the layout put them.
        /// </summary>
        public Double Justify { get; private set; }

        /// <summary>
        /// Gets how many places between its words the fragment holds a share for, which is how many times
        /// the painter repeats the share: the place a line trails into is already left out of the count.
        /// </summary>
        public Int32 Expansions { get; private set; }

        /// <summary>Folds this fragment's share of a justified line's room into the width it stands at.</summary>
        public void Expand(Double share, Int32 places)
        {
            Measured ??= Width;
            Justify = share;
            Expansions = places;
            Width = Measured.Value + (share * places);
        }

        /// <summary>Sets the width justification gave a fragment whose growth is not one repeated share.</summary>
        public void SetJustifiedWidth(Double width)
        {
            Measured ??= Width;
            Width = width;
        }

        public ComputedStyle Style { get; private set; }

        public Box Box { get; private set; } = null!;

        /// <summary>
        /// Gets the innermost inline element the run sits inside, or null when it sits in none. The
        /// rest of the chain is reached through <see cref="InlineAncestor.Parent"/>.
        /// </summary>
        public InlineAncestor? Inline { get; private set; }

        /// <summary>Gets the orientation the fragment's glyphs are drawn in, which in a column may
        /// differ from one run to the next even inside a single element.</summary>
        public WritingModeInfo Flow { get; private set; }

        /// <summary>Gets whether this fragment is an atomic replaced box rather than text.</summary>
        public Boolean IsReplaced { get; private set; }

        /// <summary>
        /// Gets the measurements of an inline-block, or null when the fragment is not one. The width of
        /// such a fragment is what the box's own margins add to the size it was measured to, so the size
        /// the line reserved and the size the box is drawn at are not the same number.
        /// </summary>
        public AtomicInline? Atomic { get; private set; }

        /// <summary>Gets the extent of the replaced content along the block axis, which sits on the baseline.</summary>
        public Double ContentHeight { get; private set; }

        /// <summary>
        /// Gets the offset the line's shared baseline is moved by for this fragment, which is where
        /// <c>vertical-align</c> puts it. Set while the line is built, because the reference the
        /// alignment is taken against - the line as it stands before anything is aligned - is not known
        /// until the runs on it have been measured.
        /// </summary>
        public Double BaselineShift { get; set; }
    }

    /// <summary>Fixed-size chunks of fragment state. A fragment is an int handle into these chunks,
    /// so a line can hold runs without one heap object per run.</summary>
    private sealed class FragmentStore
    {
        private const Int32 ChunkShift = 8;
        private const Int32 ChunkSize = 1 << ChunkShift;
        private const Int32 ChunkMask = ChunkSize - 1;

        private FragmentNode[][] _chunks = Array.Empty<FragmentNode[]>();
        private Int32 _count;

        public Int32 Count => _count;

        public ref FragmentNode this[Int32 id] => ref _chunks[id >> ChunkShift][id & ChunkMask];

        public Int32 Add(in FragmentNode node)
        {
            var id = _count++;
            var chunk = id >> ChunkShift;

            if (chunk >= _chunks.Length)
            {
                Array.Resize(ref _chunks, Math.Max(chunk + 1, Math.Max(4, _chunks.Length * 2)));
            }

            _chunks[chunk] ??= new FragmentNode[ChunkSize];
            _chunks[chunk][id & ChunkMask] = node;
            return id;
        }

        public void Clear()
        {
            for (var id = 0; id < _count; id++)
            {
                ref var node = ref this[id];
                node = default;
            }

            _count = 0;
        }
    }

    /// <summary>A stable handle to one fragment in the chunked store.</summary>
    private readonly struct TextFragment : IEquatable<TextFragment>
    {
        private readonly FragmentStore _store;
        private readonly Int32 _id;

        public TextFragment(FragmentStore store, Int32 id)
        {
            _store = store;
            _id = id;
        }

        private ref FragmentNode Node => ref _store[_id];

        public Boolean OutOfFlow => Node.OutOfFlow;

        public NoteRun? Note => Node.Note;

        public InterlineNoteSource? InterlineNote => Node.InterlineNote;

        public Boolean InterlineGap => Node.InterlineGap;

        public Double? InterlinePosition => Node.InterlinePosition;

        public MarginNoteSource? MarginNote => Node.MarginNote;

        public Boolean? RightToLeft
        {
            get => Node.RightToLeft;
            set => Node.RightToLeft = value;
        }

        public Int32 Level
        {
            get => Node.Level;
            set => Node.Level = value;
        }

        public RubyAnnotation? Ruby => Node.Ruby;

        public Double BaseWidth => Node.BaseWidth;

        public Double? Hang => Node.Hang;

        public String Source
        {
            get => Node.Source;
            private set => Node.Source = value;
        }

        public Int32 Start
        {
            get => Node.Start;
            private set => Node.Start = value;
        }

        public Int32 Length
        {
            get => Node.Length;
            set => Node.Length = value;
        }

        public Boolean FromTab
        {
            get => Node.FromTab;
            set => Node.FromTab = value;
        }

        public Int32 Hanging
        {
            get => Node.Hanging;
            set => Node.Hanging = value;
        }

        public Int32 HangSide
        {
            get => Node.HangSide;
            set => Node.HangSide = value;
        }

        public String Text => Node.Text;

        public Int32 TrailingSoftHyphens => Node.TrailingSoftHyphens;

        public Double Width
        {
            get => Node.Width;
            set => Node.Width = value;
        }

        public MaskSpan Trims
        {
            get => Node.Trims;
            set => Node.Trims = value;
        }

        public ref MaskSpan TrimsRef => ref Node.Trims;

        public MaskSpan AutoSpaces
        {
            get => Node.AutoSpaces;
            set => Node.AutoSpaces = value;
        }

        public ref MaskSpan AutoSpacesRef => ref Node.AutoSpaces;

        public Double AutoSpaceRoom
        {
            get => Node.AutoSpaceRoom;
            set => Node.AutoSpaceRoom = value;
        }

        public Double? Measured => Node.Measured;

        public Double Justify => Node.Justify;

        public Int32 Expansions => Node.Expansions;

        public ComputedStyle Style => Node.Style;

        public Box Box => Node.Box;

        public InlineAncestor? Inline => Node.Inline;

        public WritingModeInfo Flow => Node.Flow;

        public Boolean IsReplaced => Node.IsReplaced;

        public AtomicInline? Atomic => Node.Atomic;

        public Double ContentHeight => Node.ContentHeight;

        public Double BaselineShift
        {
            get => Node.BaselineShift;
            set => Node.BaselineShift = value;
        }

        public void MergeInto(String text)
        {
            Source = text;
            Start = 0;
            Length = text.Length;
        }

        public void TrimStart(Int32 length)
        {
            Start += length;
            Length -= length;
        }

        public Int32 TrailingSpaces() => Node.TrailingSpaces();

        public void Expand(Double share, Int32 places) => Node.Expand(share, places);

        public void SetJustifiedWidth(Double width) => Node.SetJustifiedWidth(width);

        public Boolean Equals(TextFragment other) =>
            ReferenceEquals(_store, other._store) && _id == other._id;

        public override Boolean Equals(Object? obj) =>
            obj is TextFragment other && Equals(other);

        public override Int32 GetHashCode() =>
            HashCode.Combine(RuntimeHelpers.GetHashCode(_store), _id);
    }

    /// <summary>
    /// Gets the offset <c>vertical-align</c> gives a box's baseline against the one the line shares.
    /// </summary>
    /// <remarks>
    /// The box reaches <c>above</c> the baseline it is set on and <c>below</c> it; the line it stands in
    /// reached <c>reach</c> above and <c>depth</c> below before anything was aligned, and that is the
    /// frame CSS 2.1 §10.8.1 gives the alignments against - the line box is grown *from* the aligned
    /// boxes rather than serving as their reference. The two extremes are why this is asked of the line
    /// rather than of the box: <c>top</c> and <c>bottom</c> cannot be known while the box is alone.
    /// <para>
    /// <c>middle</c> asks for half the parent's x-height, which the engine does not have from the face
    /// yet; it uses the half-em <c>ex</c> the style layer already stands in for that unit, so the two
    /// agree. A <c>&lt;length&gt;</c> or a <c>&lt;percentage&gt;</c> is not carried to the layout as a
    /// resolved number either, and is left on the baseline until the style layer sends one.
    /// </para>
    /// </remarks>
    /// <summary>Gets whether the style leaves its box on the baseline the line shares.</summary>
    private static Boolean IsBaseline(ComputedStyle style) =>
        style.VerticalAlign.Equals("baseline", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Gets whether the alignment is taken against the line box itself rather than against the box the
    /// element sits in, which is what makes it a place to stand instead of another step to climb.
    /// </summary>
    private static Boolean IsAtTheLineEdge(ComputedStyle style) =>
        style.VerticalAlign.Equals("top", StringComparison.OrdinalIgnoreCase) ||
        style.VerticalAlign.Equals("bottom", StringComparison.OrdinalIgnoreCase);

    /// <summary>Gets the chain of inline elements a run sits inside, innermost last.</summary>
    private static List<InlineAncestor> Chain(InlineAncestor? inline)
    {
        var chain = RentAncestorList();

        for (var ancestor = inline; ancestor is not null; ancestor = ancestor.Parent)
        {
            chain.Add(ancestor);
        }

        return chain;
    }

    private static Double Alignment(
        ITextMeasurer measurer,
        ComputedStyle style,
        ComputedStyle? parent,
        Double above,
        Double below,
        Double reach,
        Double depth)
    {
        var alignment = style.VerticalAlign;

        if (alignment.Equals("baseline", StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        if (alignment.Equals("super", StringComparison.OrdinalIgnoreCase))
        {
            return -0.4 * style.FontSize;
        }

        if (alignment.Equals("sub", StringComparison.OrdinalIgnoreCase))
        {
            return 0.2 * style.FontSize;
        }

        if (alignment.Equals("top", StringComparison.OrdinalIgnoreCase))
        {
            return above - reach;
        }

        if (alignment.Equals("bottom", StringComparison.OrdinalIgnoreCase))
        {
            return depth - below;
        }

        // The parent's text, not the line's: the reference is the box this one sits in.
        var reference = parent ?? style;

        if (alignment.Equals("text-top", StringComparison.OrdinalIgnoreCase))
        {
            return above - measurer.GetMetrics(reference.GetFont()).Ascent;
        }

        if (alignment.Equals("text-bottom", StringComparison.OrdinalIgnoreCase))
        {
            return measurer.GetMetrics(reference.GetFont()).Descent - below;
        }

        if (alignment.Equals("middle", StringComparison.OrdinalIgnoreCase))
        {
            // Half the parent's x-height is what the middle of the box is lifted by, so a face that states
            // one is followed rather than guessed at. Where it says nothing, the engine's own half-em `ex`
            // stands in for it, as it does wherever that unit is read.
            var parentMetrics = measurer.GetMetrics(reference.GetFont());
            var halfXHeight = parentMetrics.XHeight > 0 ? parentMetrics.XHeight / 2 : 0.25 * reference.FontSize;

            return ((above - below) / 2) - halfXHeight;
        }

        return 0;
    }

    /// <summary>
    /// The measurements of an inline-block, taken once on the line that places it.
    /// </summary>
    /// <remarks>
    /// The size along the line is the size the box is drawn at with its own margins added, since the
    /// line reserves the room for those too; the size across it is the box's alone, and the ascent says
    /// where inside that the box's baseline falls. The margins are what the line reserves and grows by
    /// on top of those sizes.
    /// </remarks>
    private readonly struct AtomicInline
    {
        public AtomicInline(
            Double inlineSize,
            Double blockSize,
            Double ascent,
            Double inlineStartMargin,
            Double inlineEndMargin,
            Double blockStartMargin,
            Double blockEndMargin)
        {
            InlineSize = inlineSize;
            BlockSize = blockSize;
            Ascent = ascent;
            InlineStartMargin = inlineStartMargin;
            InlineEndMargin = inlineEndMargin;
            BlockStartMargin = blockStartMargin;
            BlockEndMargin = blockEndMargin;
        }

        /// <summary>Gets the extent the box takes along the line, its own inline margins included.</summary>
        public Double InlineSize { get; }

        /// <summary>Gets the extent the box takes along the block axis, its own block margins aside.</summary>
        public Double BlockSize { get; }

        /// <summary>Gets the distance from the block-start edge of the border box to the box's baseline.</summary>
        public Double Ascent { get; }

        /// <summary>Gets the margin taken before the box along the line.</summary>
        public Double InlineStartMargin { get; }

        /// <summary>Gets the margin taken after the box along the line.</summary>
        public Double InlineEndMargin { get; }

        /// <summary>Gets the margin taken before the box across the line, above its ascent.</summary>
        public Double BlockStartMargin { get; }

        /// <summary>Gets the margin taken after the box across the line, below its descent.</summary>
        public Double BlockEndMargin { get; }
    }

    /// <summary>
    /// The annotation of a ruby fragment, measured and ready to be put beside its base text.
    /// </summary>
    /// <remarks>
    /// It costs the line the extent it takes along the block axis and nothing along the inline one
    /// beyond what the base already asked for, unless it is longer - a long reading over a short
    /// character widens the room the pair needs rather than being squeezed.
    /// </remarks>
    private sealed class RubyAnnotation
    {
        public RubyAnnotation(
            String text,
            Double width,
            Double block,
            Double ascent,
            Boolean under,
            ComputedStyle style,
            Box box,
            WritingModeInfo flow)
        {
            Text = text;
            Width = width;
            Block = block;
            Ascent = ascent;
            Under = under;
            Style = style;
            Box = box;
            Flow = flow;
        }

        /// <summary>
        /// Gets whether the annotation goes on the block-end side of its base, which is what
        /// <c>ruby-position: under</c> asks for: a reading placed after the base rather than before it.
        /// </summary>
        public Boolean Under { get; }

        public String Text { get; }

        /// <summary>Gets the extent the annotation takes along the inline axis.</summary>
        public Double Width { get; }

        /// <summary>Gets the extent the annotation takes along the block axis, which the line grows by.</summary>
        public Double Block { get; }

        /// <summary>Gets the annotation's own ascent, which positions its baseline inside that extent.</summary>
        public Double Ascent { get; }

        public ComputedStyle Style { get; }

        public Box Box { get; }

        public WritingModeInfo Flow { get; }
    }

    /// <summary>
    /// One breaking of one container's inline content: the box it was broken out of, the extent it was
    /// broken at, the indentation its first line started with, the writing mode it was read in, and the
    /// floats its lines had to stand aside for together with how far down and in the container sits
    /// among them.
    /// </summary>
    /// <remarks>
    /// The container is compared by identity rather than by its own equality: two boxes that happen to
    /// carry the same style are still two containers whose lines belong to each of them alone.
    /// <para>
    /// The three float parts are what a line's room is asked from, and they are the reason a block beside
    /// a float is broken twice in a pass - once while measuring, from its own origin, and once while
    /// arranging, at the height it actually lands at. A block with nothing floated near it pays nothing:
    /// all three are null and zero in both phases, and the plan it made while measuring is the one the
    /// placing reads.
    /// </para>
    /// </remarks>
    private readonly struct LinePlanKey : IEquatable<LinePlanKey>
    {
        public LinePlanKey(Box container, Double maxInline, TextIndentation indent, Int32 flow, FloatBand? band)
        {
            Container = container;
            MaxInline = maxInline;
            Indent = indent;
            Flow = flow;
            Tracks = band?.Tracks;
            BandY = band?.OriginY ?? 0;
            BandLeft = band?.Left ?? 0;
        }

        public Box Container { get; }

        public Double MaxInline { get; }

        public TextIndentation Indent { get; }

        public Int32 Flow { get; }

        public FloatTracks? Tracks { get; }

        public Double BandY { get; }

        public Double BandLeft { get; }

        public Boolean Equals(LinePlanKey other) =>
            ReferenceEquals(Container, other.Container) &&
            MaxInline == other.MaxInline &&
            Indent == other.Indent &&
            Flow == other.Flow &&
            ReferenceEquals(Tracks, other.Tracks) &&
            BandY == other.BandY &&
            BandLeft == other.BandLeft;

        public override Boolean Equals(Object? obj) => obj is LinePlanKey other && Equals(other);

        public override Int32 GetHashCode() =>
            HashCode.Combine(
                RuntimeHelpers.GetHashCode(Container),
                MaxInline,
                Indent,
                Flow,
                RuntimeHelpers.GetHashCode(Tracks),
                BandY,
                BandLeft);
    }

    /// <summary>
    /// How much room the line being built has, which is the block's own extent less whatever a float
    /// standing at that line's height takes of it.
    /// </summary>
    /// <remarks>
    /// The room is worked out once per line, when it opens, rather than per run added to it: a line
    /// whose width moved while it was being filled would let the same word break on one pass and
    /// stand on the next, and a block would settle at whatever the order of measuring happened to be.
    /// <para>
    /// A line that finds no room at all - a float across the whole width, or two meeting in the middle
    /// - is dropped below the floats in its band, which is what the specification does for a line box
    /// too narrow for anything and what the block's height then has to count. One dropped still
    /// narrower than a character gives up and overflows, the way a float wider than its container does.
    /// </para>
    /// </remarks>
    private sealed class LineRoom
    {
        /// <summary>How fine the descent of a shortened line is scanned, in steps over the float.</summary>
        private const Double UnitScan = 1000;

        /// <summary>The layout unit a browser places boxes in, which is the tolerance a fit is judged by.</summary>
        private const Double Unit = 1.0 / 64;

        private readonly FloatBand? _band;
        private readonly Double _max;
        private readonly Boolean _reversed;

        /// <summary>How far down the band the open line stands, which is what a shape is read across.</summary>
        private Double _height;

        public LineRoom(FloatBand? band, Double max, Boolean reversed, Double start = 0)
        {
            _band = band;
            _max = max;
            _reversed = reversed;
            Top = start;
            Open();
        }

        /// <summary>Gets how far down the block's lines have reached, which is where the next one stands.</summary>
        public Double Top { get; private set; }

        /// <summary>Gets how far the line that is open now reaches along the inline axis.</summary>
        public Double Room { get; private set; }

        /// <summary>Gets the height the open line has been grown to so far.</summary>
        public Double Height => _height;

        /// <summary>Gets where that line begins inside the block's content box.</summary>
        public Double Inset { get; private set; }

        /// <summary>Gets the room it had to drop through to get there, which the block's stack owes it.</summary>
        public Double Gap { get; private set; }

        /// <summary>
        /// Moves on to the next line, at the height the one just built took.
        /// </summary>
        public void Next(Double height)
        {
            Top += height;
            _height = 0;
            Open();
        }

        /// <summary>Advances over a body line's block extent without emitting another note line.</summary>
        public void Skip(Double height)
        {
            if (height <= 0)
            {
                return;
            }

            Top += height;
            _height = 0;
            Open();
        }

        /// <summary>
        /// Stands the lines that are to come below the floats of one side: a break that asks to clear
        /// puts whatever follows it under the floats it names (CSS 2.1 §9.5.2). Gets the distance the
        /// room was carried down, which is zero where there was nothing of that side to clear.
        /// </summary>
        public Double Clear(ClearSide side)
        {
            if (_band is not { } band || side == ClearSide.None)
            {
                return 0;
            }

            var cleared = band.Below(Top, side);

            if (cleared <= Top + 0.001)
            {
                return 0;
            }

            var dropped = cleared - Top;
            Top = cleared;
            Open();
            Gap += dropped;

            return dropped;
        }

        /// <summary>
        /// Stands the content that is about to be placed on the line, and asks the room again over the
        /// band it makes: a line box is shortened to avoid the whole of the float area it stands in, so
        /// the height a piece of content gives the line is part of the question the room answers, and not
        /// something the line discovers after it has been filled (CSS Shapes 1 §1.3).
        /// </summary>
        public void Grow(Double height)
        {
            if (height <= _height)
            {
                return;
            }

            _height = height;

            if (_band is { } band)
            {
                Room = band.Room(Top, _height, _max, _reversed, out var inset);
                Inset = inset;
            }
        }

        /// <summary>
        /// Takes back a growth the line did not keep: an item measured against the band it would have
        /// made, but which did not fit on the line, is turned away before the line is built, and the
        /// answer the shape gave for the taller band is no longer the line's (CSS Shapes 1 §1.3).
        /// </summary>
        public void Restore(Double height)
        {
            if (height >= _height)
            {
                return;
            }

            _height = height;

            if (_band is { } band)
            {
                Room = band.Room(Top, _height, _max, _reversed, out var inset);
                Inset = inset;
            }
        }

        private void Open()
        {
            Gap = 0;
            _height = 0;

            if (_band is not { } band)
            {
                Room = _max;
                Inset = 0;
                return;
            }

            Room = band.Room(Top, _height, _max, _reversed, out var inset);
            Inset = inset;

            if (Room > 0)
            {
                return;
            }

            var below = band.Below(Top);

            if (below > Top)
            {
                Gap = below - Top;
                Top = below;

                Room = band.Room(Top, _height, _max, _reversed, out inset);
                Inset = inset;
            }

            if (Room <= 0)
            {
                Room = _max;
                Inset = 0;
            }
        }

        /// <summary>
        /// Gets whether a float is standing in the line's way, so the room it has is less than the
        /// block's own extent - the case the specification's shift-down rule is about.
        /// </summary>
        public Boolean Shortened => Inset > 0 || Room < _max;

        /// <summary>
        /// Shifts the open line down until the content standing on it fits, which is what CSS 2.1 §9.5
        /// asks of a shortened line box too small for what is on it: it moves downward, and its width is
        /// recomputed, until some content fits or no floats are left. The descent is added to the gap,
        /// the room the block's stack owes the line for getting there.
        /// </summary>
        /// <remarks>
        /// A shaped float leaves more room the further down a line stands, so the line comes to rest at
        /// the first height the content fits at rather than below the whole float - which is the
        /// difference between a long word moving down a curved shape's edge and dropping past it
        /// (CSS Shapes 1 §1.3). The scan is a pixel at a time, the grain a line is placed on.
        /// </remarks>
        public void Drop(Double needed)
        {
            if (_band is not { } band)
            {
                return;
            }

            var below = band.Below(Top);

            if (below <= Top)
            {
                return;
            }

            var step = Math.Max(1.0, (below - Top) / UnitScan);

            for (var y = Top + step; y < below; y += step)
            {
                var room = band.Room(y, _height, _max, _reversed, out var inset);

                if (room + Unit >= needed)
                {
                    Gap = y - Top;
                    Top = y;
                    Room = room;
                    Inset = inset;
                    return;
                }
            }

            Gap = below - Top;
            Top = below;

            Room = band.Room(Top, _height, _max, _reversed, out var rest);
            Inset = rest;

            if (Room <= 0)
            {
                Room = _max;
                Inset = 0;
            }
        }
    }

    private struct LineNode
    {
        public List<TextFragment> Fragments;

        public Double Ascent { get; set; }

        public Double Height { get; set; }

        /// <summary>
        /// Gets or sets how far the block's content reaches past this line, where a break that clears
        /// stands at the content's end: the line before it owes the room the clearance took, and no
        /// line follows to take it (CSS 2.1 §9.5.2).
        /// </summary>
        public Double Tail { get; set; }

        /// <summary>
        /// Gets or sets where the line begins inside its block's content box: zero for every line of a
        /// block with nothing floated beside it, and the far edge of the float for one that has.
        /// </summary>
        public Double Left { get; set; }

        /// <summary>
        /// Gets or sets the extent the line was broken at, which is the block's content extent unless a
        /// float shortens it. Alignment works within this much, and the line box is drawn this wide.
        /// </summary>
        public Double Extent { get; set; }

        /// <summary>
        /// Gets or sets the room the line had to drop to get clear of a float that left it nothing to
        /// stand in, which the block's own stack has to add back where it counts lines out.
        /// </summary>
        public Double GapBefore { get; set; }

        /// <summary>Gets the extent the line's fragments take along the inline axis, before alignment.</summary>
        public Double Used { get; set; }

        /// <summary>Gets or sets the used extent before justification, which every later pass starts from.</summary>
        public Double? NaturalUsed { get; set; }

        /// <summary>Gets or sets the extent the line was broken at before justification, which a later pass restores.</summary>
        public Double? NaturalExtent { get; set; }

        /// <summary>
        /// Gets or sets how much of the last fragment's width hangs past the line's advance. The spaces
        /// a <c>pre-wrap</c> line trails into are drawn where they stand but take no room (CSS Text 3
        /// §4.1.3), so the box that ends the line is narrowed by this much and the glyphs land where a
        /// line without them would have put them.
        /// </summary>
        public Double Hanging { get; set; }

        /// <summary>
        /// Gets the distance the line is set in from the edge it begins at, which is the indentation
        /// the first line of a box carries. It is not part of <see cref="Used"/>: the runs are aligned
        /// in what the indentation leaves, and the indent is what they are then all moved by.
        /// </summary>
        public Double Indent { get; set; }

        /// <summary>
        /// Gets or sets how much the line's text was scaled to fill its line box by <c>text-fit</c>, or one
        /// where the line stands at the size the page wrote. The runs it holds are already measured at the
        /// scaled size; what is left is to draw them with a font of that size, which is the question the
        /// placing pass asks this for (CSS Text 5 §1.1).
        /// </summary>
        public Double Scale { get; set; }

        /// <summary>Gets where the line's content starts along the inline axis, after alignment.</summary>
        public Double Offset { get; set; }

        /// <summary>
        /// Gets or sets the keyword the line is aligned by: what <c>text-align</c> gave it, unless the
        /// block's <c>text-align-last</c> speaks for this line - which it does for the line that ends the
        /// block and for every line a forced break ended (CSS Text 3 §6.3).
        /// </summary>
        public String Alignment { get; set; }

        /// <summary>
        /// Gets or sets the style whose <c>text-align</c> and <c>text-align-last</c> the line is set with.
        /// It is the style of the first thing on the line, which is the block's own as it stands in
        /// text, except where the line begins with a box it carries whole - and there it is the block's
        /// style, because what that box declares reaches inside it and not out.
        /// </summary>
        public ComputedStyle? Governing { get; set; }

        /// <summary>
        /// Gets the inline elements this line carries on from an earlier one, or null when nothing was
        /// cut. Their start edges belong to the line the element began on, so this line shows neither
        /// the padding nor the border nor the curve of that side.
        /// </summary>
        public HashSet<InlineAncestor>? ContinuesFromPrevious { get; set; }

        /// <summary>
        /// Gets the inline elements this line hands on to a later one, or null when none were cut. The
        /// mirror of <see cref="ContinuesFromPrevious"/> at the other end of the line.
        /// </summary>
        public HashSet<InlineAncestor>? ContinuesToNext { get; set; }

        /// <summary>
        /// Gets how each inline element that aligned its own box on this line moves the band it draws,
        /// or null while none did. Held by the line rather than by the element because what an alignment
        /// is taken against - <c>top</c> of the line, <c>bottom</c> of it - belongs to the line, and one
        /// element's chain of them stands on several lines.
        /// </summary>
        public Dictionary<InlineAncestor, Double>? BandShifts { get; set; }

        /// <summary>
        /// Gets whether the line ends where the page forced it to - a line feed in preformatted text, a
        /// <c>&lt;br&gt;</c> - rather than being broken where the room ran out. The block's
        /// <c>text-align-last</c> speaks for such a line as it does for the one that ends the block
        /// (CSS Text 3 §6.3).
        /// </summary>
        public Boolean ForceEnded { get; set; }

        /// <summary>Gets or sets whether this line belongs to a shared interline-note flow.</summary>
        public Boolean IsInterlineNote { get; set; }

        /// <summary>Gets or sets whether a fragment break should keep this line with the one after it.</summary>
        public Boolean KeepWithNext { get; set; }

        /// <summary>Gets or sets the body-line slot this note line fills, or -1 for an ordinary line.</summary>
        public Int32 SlotIndex { get; set; }
    }

    /// <summary>Fixed-size chunks of line state, addressed by an int handle like the fragments a
    /// line holds.</summary>
    private sealed class LineStore
    {
        private const Int32 ChunkShift = 8;
        private const Int32 ChunkSize = 1 << ChunkShift;
        private const Int32 ChunkMask = ChunkSize - 1;

        private LineNode[][] _chunks = Array.Empty<LineNode[]>();
        private Int32 _count;

        public ref LineNode this[Int32 id] => ref _chunks[id >> ChunkShift][id & ChunkMask];

        public Int32 Add(in LineNode node)
        {
            var id = _count++;
            var chunk = id >> ChunkShift;

            if (chunk >= _chunks.Length)
            {
                Array.Resize(ref _chunks, Math.Max(chunk + 1, Math.Max(4, _chunks.Length * 2)));
            }

            _chunks[chunk] ??= new LineNode[ChunkSize];
            _chunks[chunk][id & ChunkMask] = node;
            return id;
        }

        public void Clear()
        {
            for (var id = 0; id < _count; id++)
            {
                ref var node = ref this[id];
                node.Fragments?.Clear();
                node = default;
            }

            _count = 0;
        }
    }

    /// <summary>A stable handle to one line in the chunked store.</summary>
    private readonly struct Line : IEquatable<Line>
    {
        private readonly LineStore _store;
        private readonly Int32 _id;

        public Line(LineStore store, Int32 id)
        {
            _store = store;
            _id = id;
        }

        private ref LineNode Node => ref _store[_id];

        public List<TextFragment> Fragments => Node.Fragments;

        public Double Ascent
        {
            get => Node.Ascent;
            set => Node.Ascent = value;
        }

        public Double Height
        {
            get => Node.Height;
            set => Node.Height = value;
        }

        public Double Tail
        {
            get => Node.Tail;
            set => Node.Tail = value;
        }

        public Double Left
        {
            get => Node.Left;
            set => Node.Left = value;
        }

        public Double Extent
        {
            get => Node.Extent;
            set => Node.Extent = value;
        }

        public Double GapBefore
        {
            get => Node.GapBefore;
            set => Node.GapBefore = value;
        }

        public Double Used
        {
            get => Node.Used;
            set => Node.Used = value;
        }

        public Double? NaturalUsed
        {
            get => Node.NaturalUsed;
            set => Node.NaturalUsed = value;
        }

        public Double? NaturalExtent
        {
            get => Node.NaturalExtent;
            set => Node.NaturalExtent = value;
        }

        public Double Hanging
        {
            get => Node.Hanging;
            set => Node.Hanging = value;
        }

        public Double Indent
        {
            get => Node.Indent;
            set => Node.Indent = value;
        }

        public Double Scale
        {
            get => Node.Scale;
            set => Node.Scale = value;
        }

        public Double Offset
        {
            get => Node.Offset;
            set => Node.Offset = value;
        }

        public String Alignment
        {
            get => Node.Alignment;
            set => Node.Alignment = value;
        }

        public ComputedStyle? Governing
        {
            get => Node.Governing;
            set => Node.Governing = value;
        }

        public HashSet<InlineAncestor>? ContinuesFromPrevious
        {
            get => Node.ContinuesFromPrevious;
            set => Node.ContinuesFromPrevious = value;
        }

        public HashSet<InlineAncestor>? ContinuesToNext
        {
            get => Node.ContinuesToNext;
            set => Node.ContinuesToNext = value;
        }

        public Dictionary<InlineAncestor, Double>? BandShifts
        {
            get => Node.BandShifts;
            set => Node.BandShifts = value;
        }

        public Boolean ForceEnded
        {
            get => Node.ForceEnded;
            set => Node.ForceEnded = value;
        }

        public Boolean IsInterlineNote
        {
            get => Node.IsInterlineNote;
            set => Node.IsInterlineNote = value;
        }

        public Boolean KeepWithNext
        {
            get => Node.KeepWithNext;
            set => Node.KeepWithNext = value;
        }

        public Int32 SlotIndex
        {
            get => Node.SlotIndex;
            set => Node.SlotIndex = value;
        }

        public Boolean Equals(Line other) =>
            ReferenceEquals(_store, other._store) && _id == other._id;

        public override Boolean Equals(Object? obj) =>
            obj is Line other && Equals(other);

        public override Int32 GetHashCode() =>
            HashCode.Combine(RuntimeHelpers.GetHashCode(_store), _id);
    }

    /// <summary>
    /// One inline element a run sits inside, with its box model already resolved.
    /// </summary>
    /// <remarks>
    /// The box tree has no parent pointer and the flattening above throws the element away, so this
    /// chain is what remembers where a run came from. One instance is built per inline element each
    /// time the content is collected, and every run under that element shares it, which is why the
    /// nodes of a line can be reconstructed by comparing chains by reference.
    /// <para>
    /// The padding and border are kept in both forms: physically, so a fragment can hand the painter
    /// the sides it actually draws, and as the four logical insets, so the layout can talk about the
    /// edge a line is cut at without asking which way the text runs.
    /// </para>
    /// </remarks>
    private sealed class InlineAncestor
    {
        private const Int32 Top = 0;
        private const Int32 Right = 1;
        private const Int32 Bottom = 2;
        private const Int32 Left = 3;

        private readonly Int32 _inlineStartSide;
        private readonly Int32 _inlineEndSide;

        public InlineAncestor(
            Box box,
            InlineAncestor? parent,
            Double basis,
            WritingModeInfo flow,
            ITextMeasurer measurer,
            Boolean noteContent = false)
        {
            Box = box;
            Parent = parent;
            NoteContent = noteContent;

            Padding = box.Style.Padding.Resolve(basis);
            Border = BlockLayout.BorderOf(box);
            Margin = box.Style.Margin.Resolve(basis);

            var metrics = measurer.GetMetrics(box.Style.GetFont());
            Ascent = metrics.Ascent;
            Descent = metrics.Descent;
            AnyAlignment = !IsBaseline(box.Style) || (parent?.AnyAlignment ?? false);

            // The element's decoration is placed in the frame its line was laid out in, so it is the
            // container's writing mode - and not the element's own - that decides which physical side
            // each logical edge falls on.
            _inlineStartSide = InlineSide(flow, start: true);
            _inlineEndSide = InlineSide(flow, start: false);

            InlineStartInset = Insets(_inlineStartSide);
            InlineEndInset = Insets(_inlineEndSide);

            // The margins the element takes along the axis its text runs, which is room the line gives
            // it rather than part of the box it draws: an inline element's horizontal margins take their
            // place on the line like any other horizontal margin (CSS 2.1 §8.1), while the vertical ones
            // never grow the line box. They are held apart from the insets above, which describe the
            // border box the element's edges are drawn at.
            InlineStartMargin = Read(Margin, _inlineStartSide);
            InlineEndMargin = Read(Margin, _inlineEndSide);
            BlockStartInset = Insets(BlockSide(flow, start: true));
            BlockEndInset = Insets(BlockSide(flow, start: false));
        }

        /// <summary>Gets the element this chain entry stands for.</summary>
        public Box Box { get; }

        /// <summary>Gets the element this one sits inside, or null when it is the outermost.</summary>
        public InlineAncestor? Parent { get; }

        /// <summary>Gets whether this inline wrapper was reconstructed from note content.</summary>
        public Boolean NoteContent { get; }

        /// <summary>Gets the padding the element resolved to, all four sides.</summary>
        public Thickness Padding { get; }

        /// <summary>Gets the border widths the element resolved to, all four sides.</summary>
        public Thickness Border { get; }

        /// <summary>Gets the margins the element resolved to, all four sides.</summary>
        public Thickness Margin { get; }

        /// <summary>Gets the room the element's margin takes at the start of the inline axis.</summary>
        public Double InlineStartMargin { get; }

        /// <summary>Gets the room the element's margin takes at the end of the inline axis.</summary>
        public Double InlineEndMargin { get; }

        /// <summary>Gets the extent the element's padding and border add at the start of the inline axis.</summary>
        public Double InlineStartInset { get; }

        /// <summary>Gets the extent the element's padding and border add at the end of the inline axis.</summary>
        public Double InlineEndInset { get; }

        /// <summary>Gets the extent the element's padding and border add at the start of the block axis.</summary>
        public Double BlockStartInset { get; }

        /// <summary>Gets the extent the element's padding and border add at the end of the block axis.</summary>
        public Double BlockEndInset { get; }

        /// <summary>Gets the distance from the top of the element's font to its baseline.</summary>
        public Double Ascent { get; }

        /// <summary>Gets the distance from the element's baseline to the bottom of its font.</summary>
        public Double Descent { get; }

        /// <summary>
        /// Gets whether this element, or any element it sits inside, asks for an alignment of its own
        /// box. Carried down the chain so a line with nothing aligned on it can skip the walk entirely.
        /// </summary>
        public Boolean AnyAlignment { get; }

        /// <summary>
        /// Gets the padding a fragment of this element draws. An edge the element was cut at carries
        /// none of it: everything that would have been drawn there was drawn on the line the element
        /// began or ended on.
        /// </summary>
        public Thickness PaddingFor(Boolean cutStart, Boolean cutEnd) =>
            Sliced(Padding, cutStart, cutEnd);

        /// <summary>Gets the border a fragment of this element draws, cut edges carrying none.</summary>
        public Thickness BorderFor(Boolean cutStart, Boolean cutEnd) =>
            Sliced(Border, cutStart, cutEnd);

        private Thickness Sliced(Thickness thickness, Boolean cutStart, Boolean cutEnd)
        {
            // An element cut at both ends draws neither side border: the two edges stand on the
            // lines the element began and ends on, and this line holds only its middle.
            var sliced = thickness;

            if (cutStart)
            {
                sliced = Clear(sliced, _inlineStartSide);
            }

            if (cutEnd)
            {
                sliced = Clear(sliced, _inlineEndSide);
            }

            return sliced;
        }

        /// <summary>
        /// Gets the physical side the inline axis starts on: the left edge of a left-to-right row, the
        /// right edge of a right-to-left one, the top edge of a column - and the bottom edge under
        /// <c>sideways-lr</c>, the one mode that runs up the page.
        /// </summary>
        private static Int32 InlineSide(WritingModeInfo flow, Boolean start) => flow.IsVertical
            ? start == (flow.InlineSign > 0) ? Top : Bottom
            : start == (flow.InlineSign > 0) ? Left : Right;

        /// <summary>
        /// Gets the physical side the block axis starts on: the top edge of a row, and of a column the
        /// left edge unless the mode is a right-to-left one, whose first column is the right-hand one.
        /// </summary>
        private static Int32 BlockSide(WritingModeInfo flow, Boolean start) => flow.IsVertical
            ? start == (flow.BlockSign > 0) ? Left : Right
            : start ? Top : Bottom;

        /// <summary>Gets the padding and border the element adds along one physical side.</summary>
        private Double Insets(Int32 side) => Read(Padding, side) + Read(Border, side);

        private static Double Read(Thickness thickness, Int32 side) => side switch
        {
            Top => thickness.Top,
            Bottom => thickness.Bottom,
            Left => thickness.Left,
            _ => thickness.Right,
        };

        private static Thickness Clear(Thickness t, Int32 side) => side switch
        {
            Top => new Thickness(0, t.Right, t.Bottom, t.Left),
            Bottom => new Thickness(t.Top, t.Right, 0, t.Left),
            Left => new Thickness(t.Top, t.Right, t.Bottom, 0),
            _ => new Thickness(t.Top, 0, t.Bottom, t.Left),
        };
    }

}

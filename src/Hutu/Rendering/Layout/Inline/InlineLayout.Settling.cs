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
    /// <summary>The side of a line a fragment hangs from: the line's start edge, or its end edge.</summary>
    private const Int32 HangStart = 1;

    private const Int32 HangEnd = 2;

    /// <summary>
    /// Settles what stands past each line's edges once every line is built (CSS Text 3 §8.2.1,
    /// §8.2): a closing punctuation either gives up half its blank at the line's end
    /// (<c>text-spacing-trim</c>) or hangs there whole, and an opening glyph on an element's first
    /// formatted line is drawn before the line begins. Hanging is what a value of
    /// <c>hanging-punctuation</c> reaches; the glyph keeps its ink and gives up its advance, which is
    /// the negative margin on the edge of its parent inline box the specification describes.
    /// </summary>
    /// <remarks>
    /// At most one character hangs at each edge, a non-zero border or padding between the character
    /// and the edge keeps it from hanging, and a glyph that hangs leaves nothing for the trim to give
    /// up: it is outside the room the line was measured at, so the line does not overflow because of
    /// it and the trim would only narrow it.
    /// </remarks>
    private void SettleLines(List<Line> lines)
    {
        for (var index = 0; index < lines.Count; index++)
        {
            SettleLine(
                lines[index],
                firstLine: index == 0,
                lastLine: index == lines.Count - 1,
                forceLineEnd: false);
        }
    }

    /// <summary>Settles the edges of one line with the line boundaries its surrounding block gives it.</summary>
    private void SettleLine(Line line, Boolean firstLine, Boolean lastLine, Boolean forceLineEnd)
    {
        // The opening glyph hangs first: it stands outside the line, so the room the line has is
        // what the rest of the line measures into - and the trim at the end of the line asks
        // whether the line overflows on what is left.
        var opening = HangAtStart(line, firstLine);
        var used = line.Used - opening;
        var hung = HangAtEnd(line, lastLine, used, forceLineEnd);
        var trimmed = 0.0;

        if (hung > 0)
        {
            line.Used = used - hung;
        }
        else
        {
            trimmed = TrimLineEnd(line, line.Extent, used);
            line.Used = used - trimmed;
        }

        if (hung > 0 || opening > 0 || trimmed > 0)
        {
            line.Offset = Aligned(line, line.Extent);
        }
    }

    /// <summary>
    /// Takes the room a closing punctuation gives up at the end of the line. <c>trim-end</c> always
    /// takes it; <c>allow-end</c> only where the line would not otherwise fit. The room of the last
    /// visible character is what changes; its ink does not move, because the blank it gives up lies
    /// after it (CSS Text 4 §8.5.2).
    /// </summary>
    private Double TrimLineEnd(Line line, Double maxInline, Double used)
    {
        for (var index = line.Fragments.Count - 1; index >= 0; index--)
        {
            var fragment = line.Fragments[index];

            if (fragment.OutOfFlow || fragment.InterlineGap ||
                fragment.InterlineNote is not null || fragment.MarginNote is not null)
            {
                continue;
            }

            if (fragment.IsReplaced || fragment.Atomic is not null || fragment.Ruby is not null)
            {
                return 0;
            }

            var visible = fragment.Length - fragment.Hanging;

            if (visible <= 0)
            {
                // An empty box the line ends at stands in the way of the trim as it does of the
                // hang: its border is the edge the punctuation would have to cross.
                if (CarriesAnInlineEdge(fragment.Inline))
                {
                    return 0;
                }

                continue;
            }

            var codePoint = CodePointBefore(fragment.Source, fragment.Start + visible);
            var kind = KindOf(fragment.Style, codePoint);
            var value = fragment.Style.TextSpacingTrim;

            if (kind == PunctuationClass.None ||
                value.All ||
                TextSpacingTrim.HasExclusiveFeature(fragment.Style.ResolvedFont.Features) ||
                !IsFullwidthPunctuation(_measurer, fragment.Style, codePoint, fragment.Flow))
            {
                return 0;
            }

            var overflows = used > maxInline - line.Indent;

            if (!TextSpacingTrim.TrimsAtLineEnd(value, kind, overflows))
            {
                return 0;
            }

            var at = visible - 1;

            if (at < 0 || CodeAt(fragment.Trims, fragment.Start + at) != '\0')
            {
                return 0;
            }

            AppendMark(ref fragment.TrimsRef, fragment.Start + at, TrimClosing);

            var delta = TrimDelta(_measurer, fragment.Style, codePoint, fragment.Flow);
            fragment.Width -= delta;

            return delta;
        }

        return 0;
    }

    /// <summary>
    /// Gets whether a fragment draws something at a line's edge. A run whose characters all hang -
    /// the spaces a <c>pre-wrap</c> line trails into - draws nothing, and neither does a box that left
    /// the flow; a zero-length inline box with a border draws only its edge, which still stands in
    /// the way of anything hanging past it.
    /// </summary>
    private static Boolean HasInk(TextFragment fragment) =>
        !fragment.InterlineGap &&
        fragment.InterlineNote is null &&
        fragment.MarginNote is null &&
        (fragment.IsReplaced ||
            fragment.Atomic is not null ||
            fragment.Ruby is not null ||
            fragment.Note is not null ||
            fragment.Length > fragment.Hanging ||
            (fragment.Length == 0 && (fragment.Width > 0 || CarriesAnInlineEdge(fragment.Inline))));

    /// <summary>Hangs the line's opening glyph where the property reaches it, and answers with its advance.</summary>
    private Double HangAtStart(Line line, Boolean firstLine)
    {
        for (var index = 0; index < line.Fragments.Count; index++)
        {
            var fragment = line.Fragments[index];

            if (fragment.OutOfFlow || !HasInk(fragment))
            {
                continue;
            }

            if (fragment.IsReplaced || fragment.Atomic is not null || fragment.Ruby is not null ||
                fragment.Length - fragment.Hanging <= 0 || fragment.HangSide != 0)
            {
                return 0;
            }

            // The glyph is at the start of an element's first formatted line: the block's own text
            // reaches it on the block's first line, and an inline element's text on the line it
            // begins on (CSS Text 3 §8.2.1).
            var first = fragment.Inline is null
                ? firstLine
                : line.ContinuesFromPrevious?.Contains(fragment.Inline) != true;

            if (!first || !HangingPunctuation.HangsAtStart(fragment.Style.HangingPunctuation))
            {
                return 0;
            }

            var units = TextClusters.UnitsAt(fragment.Source, fragment.Start, fragment.Start + fragment.Length - fragment.Hanging);

            if (!HangingPunctuation.IsOpening(TextChunker.CodePointAt(fragment.Source, fragment.Start)))
            {
                return 0;
            }

            if (EdgeBlocked(line.ContinuesFromPrevious, fragment.Inline, start: true))
            {
                return 0;
            }

            return Hang(line, fragment, fragment.Start, units, HangStart);
        }

        return 0;
    }

    /// <summary>Hangs the line's closing glyph where the property reaches it, and answers with its advance.</summary>
    private Double HangAtEnd(Line line, Boolean lastLine, Double used, Boolean forceLineEnd)
    {
        for (var index = line.Fragments.Count - 1; index >= 0; index--)
        {
            var fragment = line.Fragments[index];

            if (fragment.OutOfFlow || !HasInk(fragment))
            {
                continue;
            }

            if (fragment.IsReplaced || fragment.Atomic is not null || fragment.Ruby is not null ||
                fragment.Length - fragment.Hanging <= 0 || fragment.HangSide != 0)
            {
                return 0;
            }

            // The glyph is at the end of an element's last formatted line: the block's own text
            // reaches it on the block's last line, and an inline element's text on the line it ends
            // on - which a cut element has not reached yet (CSS Text 3 §8.2.1).
            var last = fragment.Inline is null
                ? lastLine
                : line.ContinuesToNext?.Contains(fragment.Inline) != true;

            var stop = fragment.Start + fragment.Length - fragment.Hanging;

            // White space the line trails into is not drawn, so the glyph it hangs is the last one
            // before it.
            while (stop > fragment.Start)
            {
                var before = TextClusters.ClusterStart(fragment.Source, stop);

                if (!TextChunker.IsCollapsible(fragment.Source, before, stop - before))
                {
                    break;
                }

                stop = before;
            }

            if (stop <= fragment.Start)
            {
                return 0;
            }

            var start = TextClusters.ClusterStart(fragment.Source, stop);
            var overflows = forceLineEnd || used > line.Extent - line.Indent;
            var hangs = HangingPunctuation.HangsAtEnd(
                fragment.Style.HangingPunctuation,
                TextChunker.CodePointAt(fragment.Source, start),
                overflows,
                last);

            if (!hangs || EdgeBlocked(line.ContinuesToNext, fragment.Inline, start: false))
            {
                return 0;
            }

            return Hang(line, fragment, start, stop - start, HangEnd);
        }

        return 0;
    }

    /// <summary>
    /// Gets whether nothing but white space a line collapses stands from a position to the end of the
    /// text: the glyph before that position is the last the line draws.
    /// </summary>
    private static Boolean OnlySpaceRemains(String text, Int32 at)
    {
        for (var index = at; index < text.Length;)
        {
            var units = TextClusters.UnitsAt(text, index, text.Length);

            if (!TextChunker.IsCollapsible(text, index, units))
            {
                return false;
            }

            index += units;
        }

        return true;
    }

    /// <summary>
    /// Gets the last character the content draws, as the item it stands in and the position its run
    /// ends at - or item <c>-1</c> where the content's last line draws no character at all: a forced
    /// break at the end leaves an empty last line, and a box draws no character.
    /// </summary>
    private static (Int32 Item, Int32 End) LastDrawnContent(List<InlineItem> items)
    {
        for (var index = items.Count - 1; index >= 0; index--)
        {
            var item = items[index];

            if (item.IsOutOfFlow)
            {
                continue;
            }

            if (item.IsBreak ||
                item.IsReplaced ||
                item.IsAtomic ||
                item.IsRuby ||
                item.IsEmpty)
            {
                return (-1, -1);
            }

            if (item.Text is not { Length: > 0 } text)
            {
                continue;
            }

            var at = text.Length;

            while (at > 0)
            {
                var start = TextClusters.ClusterStart(text, at);

                if (!TextChunker.IsCollapsible(text, start, at - start))
                {
                    return (index, at);
                }

                at = start;
            }
        }

        return (-1, -1);
    }

    /// <summary>
    /// Gets whether a non-zero border or padding of an element the run sits in will stand between the
    /// glyph at its end and the line's end edge: the element's end edge is drawn on this line only
    /// when nothing after the run belongs to it, so an element the line cuts does not stand in the way.
    /// </summary>
    private static Boolean EndEdgeOnLine(List<InlineItem> items, Int32 index, InlineAncestor? inline)
    {
        for (var ancestor = inline; ancestor is not null; ancestor = ancestor.Parent)
        {
            if (ancestor.InlineEndInset <= 0)
            {
                continue;
            }

            var ends = true;

            for (var at = index + 1; at < items.Count && ends; at++)
            {
                for (var chain = items[at].Inline; chain is not null; chain = chain.Parent)
                {
                    if (ReferenceEquals(chain, ancestor))
                    {
                        ends = false;
                        break;
                    }
                }
            }

            if (ends)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Gets whether a non-zero border or padding stands between a glyph and the line's edge on the
    /// side it would hang from. An edge the element was cut at belongs to another line, so it is not
    /// in the way (CSS Text 3 §8.2.1).
    /// </summary>
    private static Boolean EdgeBlocked(HashSet<InlineAncestor>? cut, InlineAncestor? inline, Boolean start)
    {
        for (var ancestor = inline; ancestor is not null; ancestor = ancestor.Parent)
        {
            if (cut?.Contains(ancestor) == true)
            {
                continue;
            }

            if ((start ? ancestor.InlineStartInset : ancestor.InlineEndInset) > 0)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Cuts one character off the edge of a run and marks it as hanging from <paramref name="side"/>,
    /// giving up its advance. The room is measured at the character itself, so a punctuation set in a
    /// smaller font hangs by its own width rather than the line's.
    /// </summary>
    private Double Hang(Line line, TextFragment fragment, Int32 start, Int32 length, Int32 side)
    {
        var width = MeasureChunk(_measurer, fragment.Style, fragment.Source, start, length, fragment.Flow);

        var hung = NewFragment(
            fragment.Source,
            start,
            length,
            width,
            fragment.Style,
            fragment.Box,
            fragment.Flow,
            fragment.Inline);

        hung.RightToLeft = fragment.RightToLeft;
        hung.Level = fragment.Level;
        hung.BaselineShift = fragment.BaselineShift;
        hung.HangSide = side;
        hung.Trims = SliceMask(fragment.Trims, start, length, clearBoundary: false);
        hung.AutoSpaces = SliceMask(fragment.AutoSpaces, start, length, clearBoundary: false);

        var at = line.Fragments.IndexOf(fragment);
        var rest = fragment.Length - length;

        if (side == HangStart)
        {
            fragment.TrimStart(length);
            fragment.Width -= width;
            line.Fragments.Insert(at, hung);
        }
        else
        {
            var kept = start - fragment.Start;

            fragment.Length = kept;
            fragment.Width -= width;
            line.Fragments.Insert(at + 1, hung);
        }

        return width;
    }

    /// <summary>
    /// Gets whether the stepping could cut the run anywhere inside it, which is what the line asks before
    /// it fills its remaining room with the head of a run that will not fit.
    /// </summary>
    /// <remarks>
    /// The two break values that license a cut answer for the run themselves: <c>line-break: anywhere</c>
    /// sets the rules aside and <c>overflow-wrap: anywhere</c> reaches every character of a word, so every
    /// boundary inside it is open. <c>word-break: break-all</c> is the one that leaves them standing, and
    /// it gives nothing to a run the rules hold together - a period takes no break before itself and none
    /// between its own marks ([UAX14] LB13), so "X.." is one unit however the page spells its break value,
    /// which is how Chrome lays it out (CSS Text 3 §5.6).
    /// </remarks>
    /// <param name="source">The text the run was cut from.</param>
    /// <param name="start">The index the run begins at.</param>
    /// <param name="length">How many characters the run holds.</param>
    /// <param name="style">The style the run is set in, which is what gives the cut its licence.</param>
    private static Boolean CutsInside(String source, Int32 start, Int32 length, ComputedStyle style)
    {
        var end = start + length;
        var previous = start;
        var at = start + TextClusters.UnitsAt(source, start, end);

        while (at < end)
        {
            // The same answer the walk gives at a boundary inside the run, read for every one of them.
            if ((style.WrapsInsideSpaces && TextChunker.IsBreakSpace(source[previous])) ||
                !style.BreaksBetweenLetters || style.BreaksAnywhere || style.CountsLongWordBreaks ||
                LineBreaks.MayBreak(source, previous, at, style.WordBreakingRules))
            {
                return true;
            }

            previous = at;
            at += TextClusters.UnitsAt(source, at, end);
        }

        return false;
    }

    /// <summary>
    /// Works out which lines cut an inline element in half. An element a line hands on to the next one
    /// was cut there, so that line draws none of the element's end edge and the line that resumes it
    /// draws none of its start edge - those two edges belong where the element began and where it ends.
    /// </summary>
    /// <remarks>
    /// The extent a line takes had to be settled before this could be known, so an element that turns
    /// out not to carry on has the end edge it was given taken back off the line's extent here.
    /// </remarks>
    private static void MarkSlices(List<Line> lines, Double maxInline)
    {
        for (var index = 0; index < lines.Count; index++)
        {
            var line = lines[index];

            if (line.ContinuesToNext is not { Count: > 0 } pending)
            {
                continue;
            }

            // Nothing carries on past the last line, so an element the content ends inside ends there.
            var next = index + 1 < lines.Count ? Inlines(lines[index + 1]) : new HashSet<InlineAncestor>();
            var dropped = 0.0;

            foreach (var ancestor in pending)
            {
                if (!next.Contains(ancestor))
                {
                    dropped += ancestor.InlineEndInset;
                }
            }

            pending.IntersectWith(next);
            line.Used -= dropped;

            if (pending.Count == 0)
            {
                line.ContinuesToNext = null;
                continue;
            }

            // The line is aligned on the extent it ends up with, which may have shrunk by the edge an
            // element that stops here no longer draws twice.
            line.Offset = Aligned(line, maxInline);
            var nextLine = lines[index + 1];
            nextLine.ContinuesFromPrevious = pending;
        }
    }

    /// <summary>Gets the inline elements a line carries a run of, at any depth.</summary>
    private static HashSet<InlineAncestor> Inlines(Line line)
    {
        var inlines = new HashSet<InlineAncestor>();

        foreach (var fragment in line.Fragments)
        {
            for (var ancestor = fragment.Inline; ancestor is not null; ancestor = ancestor.Parent)
            {
                inlines.Add(ancestor);
            }
        }

        return inlines;
    }

    /// <summary>
    /// Settles the padding and border that stepping from one chain of inline elements to another adds
    /// along the inline axis, and reports what it adds. The two chains are compared from the outside
    /// in, so the elements they share are left standing; the ones the step leaves behind give up their
    /// end edge and the ones it enters take up their start edge.
    /// </summary>
    /// <param name="open">
    /// The elements the step starts from, outermost first. The step leaves it holding the ones it ends
    /// with, which is what keeps the caller's picture of what is open in step with the runs it places.
    /// </param>
    /// <param name="chain">The innermost element the run sits inside, or null when it sits in none.</param>
    /// <param name="carried">
    /// The elements the line resumed inside. Their start edge was drawn on the line the element began
    /// on, so nothing is charged for it again here.
    /// </param>
    /// <param name="origin">Where the step is taken, which is the offset the elements it enters begin at.</param>
    /// <param name="apply">
    /// Whether to move the open elements to the new chain as well as to price the move. The wrap test
    /// asks for the price alone: a run that does not fit goes on to the next line, and it has entered
    /// nothing until it is placed.
    /// </param>
    /// <param name="step">Receives the elements the step left and the edges it charged for, or null when only the extent is wanted.</param>
    private static Double Advance(
        List<OpenInline> open,
        InlineAncestor? chain,
        HashSet<InlineAncestor>? carried,
        Double origin,
        Boolean apply,
        InlineStep? step)
    {
        // Almost every run in a document sits in no inline element at all, which costs nothing.
        if (chain is null && open.Count == 0)
        {
            return 0;
        }

        // Outermost first, so that this chain and the open one line up and can be compared from the
        // outside in. A step only happens where an element begins or ends, so this is rare.
        var target = RentAncestorList();

        for (var ancestor = chain; ancestor is not null; ancestor = ancestor.Parent)
        {
            target.Add(ancestor);
        }

        target.Reverse();

        var shared = 0;

        while (shared < open.Count && shared < target.Count &&
               ReferenceEquals(open[shared].Ancestor, target[shared]))
        {
            shared++;
        }

        var added = 0.0;
        var position = origin;

        // The elements the run leaves are closed from the inside out, so that the edge of the one
        // wrapped around the others is drawn outside theirs. Each ends its box at the padding and
        // border it was measured with, and then takes its end margin, which is room on the line rather
        // than part of the box - the edge the next run starts after.
        for (var index = open.Count - 1; index >= shared; index--)
        {
            var item = open[index];

            step?.Closed.Add(item);
            added += item.Ancestor.InlineEndInset;
            position += item.Ancestor.InlineEndInset;
            added += item.Ancestor.InlineEndMargin;
            position += item.Ancestor.InlineEndMargin;
        }

        if (apply)
        {
            open.RemoveRange(shared, open.Count - shared);
        }

        for (var index = shared; index < target.Count; index++)
        {
            var ancestor = target[index];

            // An element the line resumed inside was cut at its start edge, which was drawn before the
            // break; only an element a line begins inside pays nothing for that edge here. Its margin
            // was charged at the start of the fragment the element began in too - a margin belongs to
            // the ends of the whole element, not to each piece a break cut it into.
            var resumed = !ancestor.Box.Style.ClonesDecoration &&
                carried?.Contains(ancestor) == true;
            var inset = resumed ? 0 : ancestor.InlineStartInset;
            var margin = resumed ? 0 : ancestor.InlineStartMargin;
            var item = new OpenInline(ancestor, position + margin, inset);

            step?.Opened.Add(item);

            if (apply)
            {
                open.Add(item);
            }

            added += margin + inset;
            position += margin + inset;
        }

        ReturnAncestorList(target);
        return added;
    }

    /// <summary>
    /// An inline element that is open at some point of a line, with the box the line gave it.
    /// </summary>
    /// <remarks>
    /// A context is broken into lines in one pass and its boxes are placed in another, and only the
    /// chain of elements and the arithmetic of walking it are shared between the two - the measuring
    /// pass has no boxes to hand and the placing pass has nothing else. Walking the chain in one place
    /// is what stops the extent a line is measured to from drifting away from the extent its boxes are
    /// placed at.
    /// </remarks>
    private sealed class OpenInline
    {
        public OpenInline(InlineAncestor ancestor, Double start, Double inset)
        {
            Ancestor = ancestor;
            Start = start;
            Inset = inset;
        }

        /// <summary>Gets the element.</summary>
        public InlineAncestor Ancestor { get; }

        /// <summary>Gets the offset along the inline axis the element's box begins at.</summary>
        public Double Start { get; }

        /// <summary>
        /// Gets the extent the step that entered the element charged for its start edge, which is
        /// nothing when the element was already open before the line began.
        /// </summary>
        public Double Inset { get; }

        /// <summary>Gets or sets the node the element draws through. Only the placing pass makes one.</summary>
        public LayoutBox? Node { get; set; }
    }

    /// <summary>
    /// The boxes one container's walk could not place when it met them, with the inline elements they
    /// were waiting for.
    /// </summary>
    /// <remarks>
    /// <para>
    /// An inline element whose <c>position</c> is not static is the containing block for the boxes flown
    /// out of its content, and the rectangle they resolve their offsets against is drawn from the start
    /// edges of the element's first fragment to the end edges of its last (CSS Positioned Layout 3 2.1).
    /// Neither end of that is known while a single line is being placed: the element may carry on past the
    /// line it stands on, or past the column. So a box that anchors to an inline element is held here till
    /// the walk has put down the last fragment, and an atomic inline standing under one is held with it,
    /// because the content it arranges flies to the same rectangle.
    /// </para>
    /// <para>
    /// The displacement a relative element takes is held back for the same reason. It carries everything
    /// drawn through the element, and a box arranged once the element had moved would ride on none of it -
    /// so the anchors are read from the places the fragments had before any of them moved, and the move
    /// then takes the held boxes along with the text, exactly as it takes the fragments themselves.
    /// </para>
    /// </remarks>
    /// <summary>The ordinary line flow belonging to one margin-note group.</summary>
    internal sealed class MarginNoteFlow
    {
        internal MarginNoteFlow(
            Box note,
            Object lines,
            WritingModeInfo flow,
            Double blockSize,
            InlineLayout owner)
        {
            _owner = owner;
            Note = note;
            _lines = (List<Line>)lines;
            Flow = flow;
            BlockSize = blockSize;
        }

        private readonly InlineLayout _owner;
        private readonly List<Line> _lines;

        public Box Note { get; }

        public WritingModeInfo Flow { get; }

        public Double BlockSize { get; }

        internal void Place(LayoutBox parent, Rect contentBox, InlineContext context)
        {
            var placement = new Placement(context.Engine);
            _owner.PlaceLines(
                Note,
                parent,
                _lines,
                0,
                _lines.Count,
                contentBox,
                Flow,
                context,
                0,
                placement);
            placement.Finish(Flow);
        }
    }

    private sealed class Placement
    {
        private readonly LayoutEngine _engine;
        private readonly Dictionary<InlineAncestor, Edges> _anchors = new();
        private readonly List<Held> _held = new();
        private readonly List<(LayoutBox Box, Offset Shift)> _shifts = new();

        public Placement(LayoutEngine engine) => _engine = engine;

        /// <summary>
        /// Notes one fragment of an inline element: the first one the walk hands over is kept as the start
        /// of the rectangle and every later one takes the end of it.
        /// </summary>
        public void Fragment(InlineAncestor ancestor, LayoutBox fragment)
        {
            if (!PositionedLayout.LendsAFrame(ancestor.Box, inlineLevel: true))
            {
                return;
            }

            var box = fragment.PaddingBox;

            _anchors[ancestor] = _anchors.TryGetValue(ancestor, out var seen)
                ? new Edges(seen.First, box)
                : new Edges(box, box);
        }

        /// <summary>Holds back the place a relative element's offset gives its whole subtree.</summary>
        public void Shift(LayoutBox fragment, Offset offset)
        {
            if (offset.X != 0 || offset.Y != 0)
            {
                _shifts.Add((fragment, offset));
            }
        }

        /// <summary>
        /// Holds a box that left the flow, with the place on the line it stood in - which is the static
        /// position an offset it does not declare falls back to.
        /// </summary>
        /// <param name="against">
        /// The context the box would have been placed against had the inline element above it not answered
        /// for it, which is where the offsets the element does not settle are still read from.
        /// </param>
        /// <param name="index">
        /// How many boxes already stood in <paramref name="owner"/> when the line came to this one, which
        /// is where it goes again so that the line reads in the order it was built in.
        /// </param>
        public void Hold(Box box, InlineAncestor anchor, LayoutBox owner, Int32 index, LayoutContext against, Offset stood, StaticLine? line = null) =>
            _held.Add(new Held(box, anchor, owner, index, against, stood, null, line));

        /// <summary>Holds an atomic inline box, with the frame the line gave it.</summary>
        /// <inheritdoc cref="Hold(Box, InlineAncestor, LayoutBox, Int32, LayoutContext, Offset)" path="/param[@name='against']"/>
        /// <inheritdoc cref="Hold(Box, InlineAncestor, LayoutBox, Int32, LayoutContext, Offset)" path="/param[@name='index']"/>
        public void Hold(Box box, InlineAncestor anchor, LayoutBox owner, Int32 index, LayoutContext against, Rect frame) =>
            _held.Add(new Held(box, anchor, owner, index, against, default, frame, null));

        /// <summary>Places every box that was held, then moves the fragments their offsets displace.</summary>
        public void Finish(WritingModeInfo flow)
        {
            var placed = new Dictionary<LayoutBox, Int32>();

            foreach (var held in _held)
            {
                // A filter holds the fixed ones behind it as well, the way it does on a block; a position
                // that is merely not static leaves them to the viewport (CSS Filter Effects 1 2.4).
                var frame = ContainingBlockOf(held.Anchor, flow);
                var against = held.Anchor.Box.Style.FormsContainingBlock(inlineLevel: true)
                    ? held.Against.WithCapturedContainingBlock(frame)
                    : held.Against.WithAbsoluteContainingBlock(frame);
                var node = held.Frame is { } area
                    ? _engine.Arrange(held.Box, area, against)
                    : _engine.ArrangePositioned(
                        held.Box,
                        held.Stood,
                        against,
                        line: held.Line,
                        staticFromHigh: flow.IsVertical && flow.BlockSign < 0,
                        staticFromInline: flow.IsVertical && flow.IsRightToLeft);

                // The siblings placed while the box waited are not the ones it stood among, so the place it
                // was counted at moves along by the boxes already put back into that owner.
                var before = placed.TryGetValue(held.Owner, out var seen) ? seen : 0;
                held.Owner.Children.Insert(Math.Min(held.Index + before, held.Owner.Children.Count), node);
                placed[held.Owner] = before + 1;
            }

            foreach (var (box, shift) in _shifts)
            {
                PositionedLayout.Translate(box, shift);
            }
        }

        /// <summary>
        /// Gets the rectangle an inline element's out-of-flow descendants resolve their offsets against:
        /// the first fragment's start edges to the last fragment's end edges, in the direction the
        /// container's text runs. Fragments on different lines leave nothing between their inline edges -
        /// an element broken over two lines gives a box that starts where its first fragment starts, which
        /// is what the rule comes to and what Chrome draws.
        /// </summary>
        private Rect ContainingBlockOf(InlineAncestor anchor, WritingModeInfo flow)
        {
            var (first, last) = _anchors[anchor];

            var inline = Math.Max(0,
                flow.InlineSign * flow.InlineStart(last) + flow.InlineExtent(last.Width, last.Height) -
                flow.InlineSign * flow.InlineStart(first));
            var block = Math.Max(0,
                flow.BlockSign * flow.BlockStart(last) + flow.BlockExtent(last.Width, last.Height) -
                flow.BlockSign * flow.BlockStart(first));

            return flow.Map(first, 0, inline, 0, block);
        }

        /// <summary>The padding boxes of the first and the last fragment an inline element left.</summary>
        private readonly record struct Edges(Rect First, Rect Last);

        /// <summary>
        /// A box the walk held back. <see cref="Stood"/> is what a box that left the flow is placed from,
        /// and <see cref="Frame"/> the frame an atomic inline was given - one of the two is always there,
        /// and which it is is answered by the frame standing empty.
        /// </summary>
        private readonly record struct Held(
            Box Box,
            InlineAncestor Anchor,
            LayoutBox Owner,
            Int32 Index,
            LayoutContext Against,
            Offset Stood,
            Rect? Frame,
            StaticLine? Line);
    }

    /// <summary>What one step between two chains of inline elements did to the elements it left and entered.</summary>
    /// <remarks>
    /// An element the step left draws its end edge, which is always the whole of it - a cut is what
    /// carrying on to the next line is, and nothing left in the middle of a line carries on anywhere.
    /// An element the step entered draws its start edge only as far as <see cref="OpenInline.Inset"/>
    /// says, which is nothing for one the line was already inside.
    /// </remarks>
    private sealed class InlineStep
    {
        /// <summary>Gets the elements the step left, innermost first, in the order their end edges are laid out.</summary>
        public List<OpenInline> Closed { get; } = new();

        /// <summary>Gets the elements the step entered, outermost first, in the order their start edges are laid out.</summary>
        public List<OpenInline> Opened { get; } = new();

        /// <summary>Empties the step so that it can be filled again.</summary>
        public void Clear()
        {
            Closed.Clear();
            Opened.Clear();
        }
    }

    /// <summary>
    /// Measures an inline-block: how much room it takes along the line, how far it reaches along the
    /// block axis, and where its baseline falls inside that.
    /// </summary>
    /// <param name="basis">
    /// The inline extent of the containing block, which is what a percentage among the box's margins,
    /// padding and border resolves against.
    /// </param>
    /// <param name="room">
    /// The room the box's line offers its margin box - negative when there is less than none - or
    /// positive infinity when the line has no definite extent to measure it against. It is the line's
    /// own room rather than what the cursor has left of it, because CSS 2.1 §10.3.9 shrinks the box to
    /// what the line can hold and leaves the line breaking to move a box that does not fit where the
    /// cursor stands down to the next line.
    /// </param>
    /// <remarks>
    /// The engine is handed the extent the box should come out at as the basis it is measured against,
    /// not as a size imposed on it: an <c>auto</c> width fills the extent it is measured against, so a
    /// box measured against the extent it should come out at comes out at that extent. Its own margins
    /// are part of that arithmetic, because they are already inside the extent the line reserved.
    /// <para>
    /// A column does not behave that way: its <c>auto</c> size is what the content asks for rather than
    /// the extent it is measured against, so handing it the room as a basis would not bound anything.
    /// The room left for the box's own content is given to it instead, and the box takes whatever its
    /// content then comes out at.
    /// </para>
    /// </remarks>
    private AtomicInline MeasureInlineBlock(Box box, Double basis, Double room, WritingModeInfo flow, InlineContext context)
    {
        var intrinsic = context.Engine.MeasureIntrinsic(box, context.Atomic);

        // The margins are kept in both directions: along the line they are part of what the line
        // reserves, and across it they are part of what the line grows by.
        var margin = box.Style.Margin.Resolve(basis);
        var (start, end) = flow.InlineMargins(margin);
        var (blockStart, blockEnd) = flow.BlockMargins(margin);
        var margins = start + end;

        // CSS2.1 §10.3.9: shrink-to-fit is the size the content prefers, brought down to what is
        // available and never taken below the size the content cannot go under. All three extents are
        // margin boxes, since that is what has to fit on the line.
        var shrink = Math.Min(intrinsic.MaxContent + margins, Math.Max(intrinsic.MinContent + margins, room));

        // Either way the box is measured against the extent it should come out at, because an `auto`
        // size fills what it is measured against however the line runs - and the box's own margins and
        // insets come off it inside the box model, which is what leaves a column content the room the
        // box was given rather than the room plus everything around it.
        var constraints = box.Flow.IsVertical
            ? MeasureConstraints.Unconstrained.WithHeight(shrink)
            : MeasureConstraints.FitWidth(shrink);

        var size = context.Engine.Measure(box, constraints, context.Atomic);

        return new AtomicInline(
            flow.InlineExtent(size.Width, size.Height) + margins,
            flow.BlockExtent(size.Width, size.Height),
            InlineBlockBaseline(box, size, shrink, flow, context),
            start,
            end,
            blockStart,
            blockEnd);
    }

    /// <summary>
    /// Gets where an inline-block's baseline falls, as a distance from the block-start edge of its
    /// border box.
    /// </summary>
    /// <remarks>
    /// CSS2.1 §10.8.1 makes the baseline that of the last line box in the box's normal flow, and the
    /// box's bottom margin edge when it has none - which is what a box holding block-level content has,
    /// since it never forms a line of its own to stand on. The lines are built here, from the same
    /// content and the same extent the arrangement will use, rather than read back off the arranged
    /// box, because the line the box sits on has to know how tall it is before anything is arranged.
    /// <para>
    /// A box set in a column, or standing in one, falls back to the bottom edge: the baseline of a line
    /// in that frame is measured from a different edge than the one the line here places the box
    /// against.
    /// </para>
    /// </remarks>
    private Double InlineBlockBaseline(Box box, Size size, Double basis, WritingModeInfo flow, InlineContext context)
    {
        // Answering the question can mean laying the box out, so the answer is kept for the rest of the
        // pass: a chain of boxes inside boxes would otherwise pay for the whole subtree again at every
        // step down it.
        var key = new BaselineKey(
            box, size.Width, size.Height, basis,
            context.Atomic.ContainingBlock.Width, context.Atomic.ContainingBlock.Height);

        if (_baselines.TryGetValue(key, out var held))
        {
            return held;
        }

        var answer = MeasureInlineBlockBaseline(box, size, basis, flow, context);
        _baselines[key] = answer;
        return answer;
    }

    private Double MeasureInlineBlockBaseline(Box box, Size size, Double basis, WritingModeInfo flow, InlineContext context)
    {
        // The box's baseline can only be carried to the line it stands on while the two are set the same
        // way: a box in a column stands on a line of its own frame, and the line here would be measuring
        // a baseline from an edge the box does not have one against. A box that holds blocks has no line
        // of its own to stand on either, and falls back to its end edge.
        if (flow.IsVertical != box.Flow.IsVertical)
        {
            return flow.BlockExtent(size.Width, size.Height);
        }

        // A flex or grid container has no line box of its own, but its first item does, and that item's
        // baseline is the container's: the item may be aligned to the start, the middle or the end of
        // the container's cross axis, and the container moves its baseline with it. Chrome places a
        // marker beside a list item that opens with an inline-flex on that baseline, not on the
        // container's end edge (CSS Flexbox 1 §8.5, CSS Grid 1 §11.1). A container that contains its
        // contents speaks only for its own box: the contents do not reach the line it stands on.
        if (!box.Flow.IsVertical && box.Kind is BoxKind.Flex or BoxKind.Grid or BoxKind.Masonry)
        {
            var flexPadding = box.Style.Padding.Resolve(basis);
            var flexBorder = BlockLayout.BorderOf(box);
            var contentBlock = Math.Max(0, size.Height - flexPadding.Vertical - flexBorder.Vertical);

            if (box.Style.Contain is not { Stated: true } && contentBlock > 0)
            {
                var container = context.Engine.Arrange(
                    box, new Rect(0, 0, size.Width, size.Height), context.Atomic);
                var first = FirstLineOfContainer(container);

                if (first is { } line)
                {
                    return BlockStartBaseline(
                        line, container.RelativeDisplacement, box.Flow, flow.BlockExtent(size.Width, size.Height));
                }
            }

            return flow.BlockExtent(size.Width, size.Height);
        }

        if (HasBlockChildren(box) || box.Children.Count == 0)
        {
            // A block child carries its own lines, and the last of them is the one CSS 2.1 §10.8.1
            // answers with: it may stand inside the child, three levels down, so it cannot be read from
            // the box's own inline content. The box is laid out for the question and the answer thrown
            // away - in an engine of its own, so the out-of-flow children the trial places are not
            // placed a second time when the box is arranged for real. A box whose overflow is clipped
            // has its content taken out of the question: §10.8.1 gives it the bottom margin edge.
            if (!box.Style.ClipsOverflow)
            {
                var probe = new LayoutEngine(_measurer);
                var arranged = probe.Arrange(
                    box, new Rect(0, 0, size.Width, size.Height), context.Atomic);

                if (LastLineInFlow(arranged, arranged.RelativeDisplacement) is { } found)
                {
                    return BlockStartBaseline(
                        found.Line, found.Displacement, flow, flow.BlockExtent(size.Width, size.Height));
                }
            }

            return flow.BlockExtent(size.Width, size.Height);
        }

        var style = box.Style;
        var padding = style.Padding.Resolve(basis);
        var border = BlockLayout.BorderOf(box);

        // Its own baseline is measured from the edge its lines begin at - the top of a row, and the right
        // of a column that runs leftward - and along the extent its text has to run along.
        var vertical = box.Flow.IsVertical;
        var start = vertical ? padding.Right + border.Right : padding.Top + border.Top;
        var contentInline = vertical
            ? Math.Max(0, size.Height - padding.Vertical - border.Vertical)
            : Math.Max(0, size.Width - padding.Horizontal - border.Horizontal);

        var items = RentItems();
        Collect(box, items, box.Flow, null, contentInline);

        var lines = BuildLines(items, contentInline, box.Flow, context, style, Indentation(box, box.Flow, contentInline));

        if (lines.Count == 0)
        {
            // A box with nothing to set has no line of its own, so its baseline is the one its font
            // would have given it.
            ReturnItems(items);
            return start + _measurer.GetMetrics(style.GetFont()).Ascent;
        }

        var baseline = start;

        for (var index = 0; index < lines.Count - 1; index++)
        {
            baseline += lines[index].Height;
        }

        ReturnItems(items);
        return baseline + lines[^1].Ascent;
    }

    /// <summary>Gets whether a box's children include any that stack rather than forming lines.</summary>
    private static Boolean HasBlockChildren(Box box)
    {
        foreach (var child in box.Children)
        {
            if (child.IsBlockLevel)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Gets the line a flex or grid container stands on for its baseline: the first line an item of the
    /// container makes, read from the block-start edge of the container inward, and within one line the
    /// item at the main-start edge. A wrapping container may put the line its markup wrote first at the
    /// far edge - <c>wrap-reverse</c> does - and the line a browser reads first is still the one nearest
    /// the block-start edge, so the marker and the line the container stands on take their baseline from
    /// that line (CSS Flexbox 1 §8.5).
    /// </summary>
    private static LayoutBox? FirstLineOfContainer(LayoutBox container)
    {
        var flex = container.Source.Style.Flex;
        var row = flex.IsRow;
        var flow = container.Flow;
        var rtl = container.Flow.IsRightToLeft;
        var reverse = row
            ? flex.Direction == FlexDirection.RowReverse
            : flex.Direction == FlexDirection.ColumnReverse;
        LayoutBox? best = null;
        var bestBlock = Double.PositiveInfinity;
        var bestMain = Double.PositiveInfinity;
        var bestX = Double.PositiveInfinity;

        void Walk(LayoutBox node)
        {
            if (node.Kind == LayoutKind.Line)
            {
                var box = node.BorderBox;

                // The box the line stands beside reads first from its own block-start edge inward: the
                // top of a row, the right or left of a column. Within one line the item at the main-start
                // edge speaks for it, and the leftmost line settles the case where two wrap-reversed
                // lines share both coordinates (CSS Flexbox 1 §8.5).
                var block = flow.IsVertical ? (flow.BlockSign < 0 ? -box.X : box.X) : box.Y;
                var main = row ? (rtl ? -box.X : box.X) : box.Y;

                if (reverse)
                {
                    main = -main;
                }

                if (block < bestBlock - 0.01 ||
                    (Math.Abs(block - bestBlock) <= 0.01 &&
                        (main < bestMain - 0.01 ||
                            (Math.Abs(main - bestMain) <= 0.01 && box.X < bestX))))
                {
                    best = node;
                    bestBlock = block;
                    bestMain = main;
                    bestX = box.X;
                }

                return;
            }

            foreach (var child in node.Children)
            {
                if (child.IsOutOfFlow || child.Source.Style.Float != FloatSide.None)
                {
                    continue;
                }

                Walk(child);
            }
        }

        Walk(container);

        return best;
    }

    /// <summary>
    /// Gets the last line box in the box's normal flow, searching its own last children before those
    /// nested inside them, together with how far relative positioning moved the line on the way down.
    /// A box that left the flow - or a float, which takes no place in the lines - carries no line of
    /// this box with it, so its subtree is passed by.
    /// </summary>
    private static (LayoutBox Line, Offset Displacement)? LastLineInFlow(LayoutBox box, Offset carried)
    {
        if (box.Kind == LayoutKind.Line)
        {
            // A line box standing only to say where an out-of-flow box would have gone is not a line
            // of the box's normal flow, and its baseline is not the box's: §10.8.1 asks for the last
            // line the content formed, and content that left the flow forms none.
            var inFlow = false;

            foreach (var child in box.Children)
            {
                if (!child.IsOutOfFlow && child.Source.Style.Float == FloatSide.None)
                {
                    inFlow = true;
                    break;
                }
            }

            return inFlow || box.Children.Count == 0 ? (box, carried) : null;
        }

        for (var index = box.Children.Count - 1; index >= 0; index--)
        {
            var child = box.Children[index];

            if (child.IsOutOfFlow || child.Source.Style.Float != FloatSide.None)
            {
                continue;
            }

            var shift = new Offset(
                carried.X + child.RelativeDisplacement.X,
                carried.Y + child.RelativeDisplacement.Y);

            if (LastLineInFlow(child, shift) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    /// <summary>
    /// Gets how far a line's baseline stands from the block-start edge of the box that holds it, which
    /// is the form an inline-block's baseline has to take: a row measures it down from the top, and a
    /// column from the right or the left, whichever edge its block axis begins at. The line's place is
    /// read before relative positioning moved it, which is what <paramref name="displacement"/> puts
    /// back (CSS 2.1 §9.4.3).
    /// </summary>
    private static Double BlockStartBaseline(
        LayoutBox line, Offset displacement, WritingModeInfo flow, Double blockSize)
    {
        var baseline = flow.IsVertical
            ? line.BorderBox.X - displacement.X + line.Baseline
            : line.BorderBox.Y - displacement.Y + line.Baseline;

        return flow.BlockSign < 0 ? blockSize - baseline : baseline;
    }
    /// <summary>Gets the padding and border a box adds along its own inline axis.</summary>
    /// <remarks>
    /// A box whose text runs down a column borders its content above and below rather than to either
    /// side, so the insets that count along its line are the vertical pair; a box in a row takes the
    /// horizontal one. A percentage among them resolves against the extent the box is measured against,
    /// which is the basis the box model would use for it in a row.
    /// </remarks>
    private static Double InlineInsets(Box box, Double basis)
    {
        var padding = box.Style.Padding.Resolve(basis);
        var border = BlockLayout.BorderOf(box);

        return box.Flow.IsVertical
            ? padding.Vertical + border.Vertical
            : padding.Horizontal + border.Horizontal;
    }

    /// <summary>Measures a whole string of text the way the writing mode advances it.</summary>
    private Double Measure(ComputedStyle style, String chunk, WritingModeInfo flow) =>
        MeasureChunk(_measurer, style, chunk, 0, chunk.Length, flow);

    /// <summary>Measures a range of text the way the writing mode advances it.</summary>
    private Double Measure(ComputedStyle style, String source, Int32 start, Int32 length, WritingModeInfo flow) =>
        MeasureChunk(_measurer, style, source, start, length, flow);

    /// <summary>
    /// Measures a range against the measurer, adding the letter spacing the style asks for on top of
    /// every character - the last one included, which is what CSS says a spaced run is worth.
    /// </summary>
    /// <remarks>
    /// A range rather than a string because the range is what the line keeps: a string made here for
    /// the measurer's sake is characters copied that the run would only copy again when it merged with
    /// the piece after it.
    /// </remarks>
    /// <summary>
    /// Gets whether the box's chain stands inside a <c>ruby</c> element, where a line may not break.
    /// </summary>
    private static Boolean InsideRuby(InlineAncestor? inline)
    {
        for (var ancestor = inline; ancestor is not null; ancestor = ancestor.Parent)
        {
            if (ancestor.Box.Element?.LocalName.Equals("ruby", StringComparison.OrdinalIgnoreCase) == true)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Gets the mark a break draws. A column draws the upright hyphen: the hyphen-minus a line sets its
    /// mark in is turned on its side where a column carries it, and the upright form is the one a reading
    /// of the same base draws beside the text (CSS Text 4 §6.2, measured on the vertical soft-hyphen
    /// pairs).
    /// </summary>
    private static String HyphenMark(ComputedStyle style, WritingModeInfo flow) =>
        flow.IsVertical && style.HyphenateCharacter == "-" ? UprightHyphen : style.HyphenateCharacter;

    /// <summary>The mark a browser draws at a break: the upright hyphen, U+2010.</summary>
    private static readonly String UprightHyphen = Char.ConvertFromUtf32(0x2010);

    /// <summary>
    /// Measures what a pair's own characters ask of the line. A tab inside the base runs the line on to
    /// its next stop rather than by an advance of its own, which is a question about where the pair
    /// stands rather than about the font (CSS Text 3 §4.2).
    /// </summary>
    private Double PairWidth(
        ComputedStyle style,
        ComputedStyle tabStyle,
        String text,
        Double position,
        WritingModeInfo flow) =>
        text.IndexOf('\t') < 0
            ? Measure(style, text, flow)
            : WhiteSpaceWidth(style, tabStyle, text, 0, text.Length, position, flow);

    /// <summary>
    /// Gets the room a run that opens with content asks the line for before the line steps inside it. The
    /// opportunity inside a word is what the line falls back on once the word stands on a line of its own
    /// and still does not fit, not a licence to fill the last of the line above with the end of it, so the
    /// whole of the word is asked for - with the space that trails it and the mark a break draws. Where the
    /// mode gives the line a place to end inside the run, after a preserved space it keeps (CSS Text 3
    /// §4.1.3), the ask stops at the first of those: the line may genuinely be brought to an end there, and
    /// a three-character box holding two kana and four ideographic spaces fills with one of those spaces
    /// rather than moving the second kana down to begin the line.
    /// </summary>
    private Double AskWidth(
        InlineItem item,
        ComputedStyle strut,
        String source,
        Int32 start,
        Int32 length,
        Double width,
        Double trailed,
        Double mark,
        Double position,
        Boolean stepsSpaces,
        WritingModeInfo flow)
    {
        if (stepsSpaces)
        {
            var end = start + length;
            var at = start;

            while (at < end && !TextChunker.IsBreakSpace(source[at]))
            {
                at += TextClusters.UnitsAt(source, at, end);
            }

            if (at > start && at < end)
            {
                // The content up to the first space it holds, and that space: the line ends after it.
                var leading = Measure(item.Style, source, start, at - start, flow);

                return leading + WhiteSpaceWidth(
                    item.Style,
                    strut,
                    source,
                    at,
                    TextClusters.UnitsAt(source, at, end),
                    position + leading,
                    flow);
            }
        }

        return width + trailed + mark;
    }

    /// <summary>
    /// Gets the room the mark a break draws takes: the hyphenate character, set in the run's own font.
    /// </summary>
    private static Double HyphenWidth(ITextMeasurer measurer, ComputedStyle style, WritingModeInfo flow) =>
        String.IsNullOrEmpty(style.HyphenateCharacter)
            ? 0
            : MeasureChunk(measurer, style, HyphenMark(style, flow), 0, HyphenMark(style, flow).Length, flow);

    private static Double MeasureChunk(
        ITextMeasurer measurer,
        ComputedStyle style,
        String source,
        Int32 start,
        Int32 length,
        WritingModeInfo flow) =>
        (flow.IsVertical
            ? MeasureColumn(source, start, length, style.GetFont(), style.FontSize, flow, measurer)
            : measurer.MeasureWidth(source.Substring(start, length), style.GetFont()))
        + (style.LetterSpacing * SpacingUnits(source, start, length, flow))
        + (style.WordSpacing * TextChunker.WordSeparators(source, start, length))
        + AutoSpaceWithin(measurer, style, source, start, length, flow)
        - TrimWithin(measurer, style, source, start, length, flow);

    /// <summary>The codes a trimmed character wears in a fragment's trim mask.</summary>
    private const Char TrimOpening = '\u0001';

    private const Char TrimClosing = '\u0002';
    private const Char TrimMiddle = '\u0003';

}

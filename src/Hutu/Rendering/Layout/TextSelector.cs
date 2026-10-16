using AngleSharp.Css.Dom;
using System;
using System.Collections.Generic;
using System.Text;

namespace Hutu.Rendering.Layout;

/// <summary>
/// Answers the questions a selection interaction asks of a laid-out document: which character sits
/// under a point, which rectangles a range covers, and what text that range holds.
/// </summary>
/// <remarks>
/// The runs are collected once, in document order, and a character's place on its line is resolved
/// through a per-run table of prefix widths built on demand. Prefix widths are what turn a point into
/// an index, because the measurer only reports the advance of a string: the advance of everything
/// before a character is what says where that character starts. The advance is read along the axis the
/// run itself travels - x for horizontal text, y for a column - so a hit lands on the same character
/// whichever way the text is written. Keeping the table is also what stops a drag from re-measuring a
/// paragraph on every pointer move, and it is dropped together with the selector, i.e. with the layout
/// it was built from.
/// <para>
/// Text inside a <c>fixed</c> or <c>sticky</c> box is left out. Its geometry is viewport-relative -
/// fixed boxes are laid out against the viewport and sticky ones are shifted at paint time - so
/// mapping a point onto it would need the scroll offset as well, which is a host concern. Every other
/// run is in document coordinates and needs nothing but the point.
/// </para>
/// </remarks>
internal sealed class TextSelector
{
    private readonly ITextMeasurer _measurer;
    private readonly List<LayoutBox> _runs = new();
    private readonly List<LayoutBox?> _lines = new();
    private readonly Dictionary<LayoutBox, Int32> _order = new();
    private readonly Dictionary<LayoutBox, Double[]> _prefixes = new();

    /// <param name="measurer">
    /// The same measurer the layout was measured with: advance widths have to agree with the ones the
    /// runs were placed by, or a hit lands on a character next to the one under the pointer.
    /// </param>
    public TextSelector(LayoutResult layout, ITextMeasurer measurer)
    {
        ArgumentNullException.ThrowIfNull(layout);
        _measurer = measurer ?? throw new ArgumentNullException(nameof(measurer));

        Collect(layout.Root, null, false);
    }

    /// <summary>Gets the text runs a selection may cover, in document order.</summary>
    public IReadOnlyList<LayoutBox> Runs => _runs;

    /// <summary>
    /// Gets the position under a point in document coordinates, or null when there is no text to hit.
    /// </summary>
    /// <remarks>
    /// A point that lands on no run at all - in the margin beside a paragraph, or below the last line
    /// - resolves to the nearest one, so a drag that starts or ends outside the text still selects
    /// something sensible rather than losing the gesture.
    /// </remarks>
    public TextPosition? HitTest(Double x, Double y)
    {
        var hit = TryHit(x, y, out var run) ? run : Nearest(x, y);

        return hit is null ? null : new TextPosition(hit, IndexAt(hit, InlineCoordinate(hit, x, y)));
    }

    /// <summary>
    /// Gets whether a point lands on a run - on text a reader could select - without falling back to
    /// the nearest one.
    /// </summary>
    /// <remarks>
    /// This is the strict half of <see cref="HitTest"/>, and the difference is the whole point: a hit
    /// test answers "what would a gesture here select", so it snaps a point in the margin onto the
    /// closest line rather than losing a drag that wanders outside the text; a pointer resting in that
    /// same margin can select nothing, so its cursor has to stay the default. Nothing here measures
    /// text, so it is cheap enough to ask on every pointer move.
    /// </remarks>
    public Boolean IsTextAt(Double x, Double y) => TryHit(x, y, out _);

    /// <summary>
    /// Gets the rectangles a selection covers, in document coordinates: one per line fragment, so a
    /// range that spans several lines reads as the several pieces of highlight a reader expects.
    /// </summary>
    public IReadOnlyList<Rect> Rects(TextSelection? selection)
    {
        var fragments = Fragments(selection);
        var rects = new List<Rect>(fragments.Count);

        foreach (var (_, rect) in fragments)
        {
            rects.Add(rect);
        }

        return rects;
    }

    /// <summary>
    /// Gets the rectangle each covered run has to highlight, keyed by the run itself.
    /// </summary>
    /// <remarks>
    /// Keyed by run rather than returned as a list because the painter meets a run while walking the
    /// layout tree and has to ask, in constant time, whether that run is covered. A run the range
    /// merely touches is left out, so a plain click highlights nothing at all.
    /// </remarks>
    public IReadOnlyDictionary<LayoutBox, Rect> Highlights(TextSelection? selection)
    {
        var fragments = Fragments(selection);
        var highlights = new Dictionary<LayoutBox, Rect>(fragments.Count);

        foreach (var (run, rect) in fragments)
        {
            highlights[run] = rect;
        }

        return highlights;
    }

    /// <summary>Gets the text a selection covers, with one line break per line it spans.</summary>
    public String Text(TextSelection? selection)
    {
        if (!TryNormalize(selection, out var first, out var last, out var start, out var end))
        {
            return String.Empty;
        }

        var builder = new StringBuilder();
        LayoutBox? previous = null;

        for (var order = first; order <= last; order++)
        {
            var run = _runs[order];
            var text = run.Text!;
            var from = order == first ? start.Index : 0;
            var to = order == last ? end.Index : text.Length;

            if (to <= from)
            {
                continue;
            }

            var line = _lines[order];

            // A line break is not a character anyone can select, so a range that crosses one joins
            // the lines the way a copy is expected to: one newline for each line left behind.
            if (builder.Length > 0 && !ReferenceEquals(line, previous))
            {
                builder.Append('\n');
            }

            builder.Append(text, from, to - from);
            previous = line;
        }

        return builder.ToString();
    }

    /// <summary>
    /// Gets the selection a double click makes: the chunk the position sits in, which is a word, a run
    /// of white space or a single CJK character - the same units line breaking may separate.
    /// </summary>
    public TextSelection Word(TextPosition position)
    {
        if (position.Box?.Text is not { Length: > 0 } text)
        {
            return new TextSelection(position, position);
        }

        var run = position.Box;
        var index = Math.Clamp(position.Index, 0, text.Length);
        var start = 0;
        var last = (Start: 0, End: 0);

        foreach (var chunk in TextChunker.Chunks(text))
        {
            var end = start + chunk.Length;
            last = (start, end);

            if (index < end)
            {
                return new TextSelection(new TextPosition(run, start), new TextPosition(run, end));
            }

            start = end;
        }

        // A click past the last character takes the last chunk rather than nothing.
        return new TextSelection(new TextPosition(run, last.Start), new TextPosition(run, last.End));
    }

    /// <summary>Gets the selection a triple click makes: every run of the line the position sits on.</summary>
    public TextSelection Line(TextPosition position)
    {
        var clamped = Clamp(position);

        if (clamped.Box is null || !_order.TryGetValue(clamped.Box, out var order))
        {
            return new TextSelection(clamped, clamped);
        }

        var line = _lines[order];
        var first = order;
        var last = order;

        while (first > 0 && ReferenceEquals(_lines[first - 1], line))
        {
            first--;
        }

        while (last + 1 < _runs.Count && ReferenceEquals(_lines[last + 1], line))
        {
            last++;
        }

        var start = _runs[first];
        var end = _runs[last];

        return new TextSelection(
            new TextPosition(start, 0),
            new TextPosition(end, end.Text?.Length ?? 0));
    }

    /// <summary>Gets the selection covering everything selectable, or null when there is no text.</summary>
    public TextSelection? All()
    {
        if (_runs.Count == 0)
        {
            return null;
        }

        var first = _runs[0];
        var last = _runs[^1];

        return new TextSelection(
            new TextPosition(first, 0),
            new TextPosition(last, last.Text?.Length ?? 0));
    }

    /// <summary>
    /// Collects the selectable runs. A viewport-anchored subtree is skipped whole: everything inside
    /// a fixed box is in the same coordinates as the box itself, so the flag travels down with it.
    /// </summary>
    private void Collect(LayoutBox box, LayoutBox? line, Boolean viewportAnchored)
    {
        viewportAnchored |= box.Position is PositionMode.Fixed or PositionMode.Sticky;

        if (box.Kind == LayoutKind.Line)
        {
            line = box;
        }

        // A marker that stands inside its item is laid out with the text but is not part of it: the
        // reader never chose it, so selecting across it passes over it and a copy does not bring it back.
        // Text no one can see is left out for the same reason the painter skips it - a selection made
        // across a hidden menu would copy words the reader never read.
        if (!viewportAnchored && box.Kind == LayoutKind.Text && !String.IsNullOrEmpty(box.Text) &&
            !box.Source.IsMarker && !box.Source.IsGenerated && !box.Source.Style.IsHidden)
        {
            _order[box] = _runs.Count;
            _runs.Add(box);
            _lines.Add(line);
        }

        foreach (var child in box.Children)
        {
            Collect(child, line, viewportAnchored);
        }
    }

    /// <summary>
    /// Splits a selection into the piece of a run each line fragment covers, in document order. Both
    /// the highlight and the copy are built from these, so the geometry a reader sees and the
    /// characters a copy takes cannot disagree about where a line begins and ends.
    /// </summary>
    private List<(LayoutBox Run, Rect Rect)> Fragments(TextSelection? selection)
    {
        var fragments = new List<(LayoutBox, Rect)>();

        if (!TryNormalize(selection, out var first, out var last, out var start, out var end))
        {
            return fragments;
        }

        for (var order = first; order <= last; order++)
        {
            var run = _runs[order];
            var from = order == first ? OffsetAt(run, start.Index) : InlineBegin(run);
            var to = order == last ? OffsetAt(run, end.Index) : InlineEnd(run);

            // A range that only touches a run covers nothing of it; an empty one produces no
            // rectangle at all, which is what makes a plain click leave no highlight behind.
            if (to != from)
            {
                fragments.Add((run, Fragment(run, from, to)));
            }
        }

        return fragments;
    }

    /// <summary>
    /// Builds the rectangle a piece of a run covers, spanning the run's whole block extent: the height
    /// of a horizontal run, or the width of a column.
    /// </summary>
    private static Rect Fragment(LayoutBox run, Double from, Double to)
    {
        var box = run.BorderBox;
        var low = Math.Min(from, to);
        var high = Math.Max(from, to);

        return run.Flow.IsVertical
            ? new Rect(box.X, low, box.Width, high - low)
            : new Rect(low, box.Y, high - low, box.Height);
    }

    /// <summary>
    /// Orders a selection's two ends and clamps both to the runs they name. Returns false when either
    /// end belongs to another layout - a stale selection has no rectangles and no text, and saying so
    /// is better than throwing.
    /// </summary>
    private Boolean TryNormalize(
        TextSelection? selection,
        out Int32 first,
        out Int32 last,
        out TextPosition start,
        out TextPosition end)
    {
        first = last = 0;
        start = end = default;

        if (selection?.Anchor.Box is null || selection.Caret.Box is null)
        {
            return false;
        }

        if (!_order.TryGetValue(selection.Anchor.Box, out var anchorOrder) ||
            !_order.TryGetValue(selection.Caret.Box, out var caretOrder))
        {
            return false;
        }

        var anchor = Clamp(selection.Anchor);
        var caret = Clamp(selection.Caret);

        // Only the layout knows which box comes first, so the two orders - and the offsets within
        // one box - decide which end is the earlier one.
        var anchorFirst = anchorOrder < caretOrder ||
            (anchorOrder == caretOrder && anchor.Index <= caret.Index);

        start = anchorFirst ? anchor : caret;
        end = anchorFirst ? caret : anchor;
        first = Math.Min(anchorOrder, caretOrder);
        last = Math.Max(anchorOrder, caretOrder);
        return true;
    }

    /// <summary>
    /// Clamps an offset to the run it names. A default position names no run at all, which is kept as
    /// it is: it stands for "no selection", and inventing a run for it would be worse than saying so.
    /// </summary>
    private static TextPosition Clamp(TextPosition position) =>
        position.Box is null
            ? position
            : new TextPosition(position.Box, Math.Clamp(position.Index, 0, position.Box.Text?.Length ?? 0));

    /// <summary>
    /// Finds the run a point lands on, or reports false when it lands on none. The last hit wins:
    /// document order is the closest thing the layout tree has to a stacking order, so an overlapping
    /// positioned box comes later and covers what it overlaps.
    /// </summary>
    private Boolean TryHit(Double x, Double y, out LayoutBox? run)
    {
        run = null;

        foreach (var candidate in _runs)
        {
            var box = candidate.BorderBox;

            if (y >= box.Y && y < box.Bottom && x >= box.X && x <= box.Right)
            {
                run = candidate;
            }
        }

        return run is not null;
    }

    /// <summary>
    /// Gets the run nearest a point, comparing distance along the block axis first and along the
    /// inline axis second, so a point past the end of a paragraph takes its last line rather than a
    /// nearer column. Both axes are the run's own, so the comparison holds whichever way it is written.
    /// </summary>
    private LayoutBox? Nearest(Double x, Double y)
    {
        LayoutBox? best = null;
        var bestBlock = Double.MaxValue;
        var bestInline = Double.MaxValue;

        foreach (var run in _runs)
        {
            var box = run.BorderBox;
            var vertical = run.Flow.IsVertical;

            var blockCoordinate = vertical ? x : y;
            var blockLow = vertical ? box.X : box.Y;
            var blockHigh = vertical ? box.Right : box.Bottom;

            var inline = InlineCoordinate(run, x, y);
            var inlineBegin = InlineBegin(run);
            var inlineEnd = InlineEnd(run);
            var inlineLow = Math.Min(inlineBegin, inlineEnd);
            var inlineHigh = Math.Max(inlineBegin, inlineEnd);

            var block = blockCoordinate < blockLow ? blockLow - blockCoordinate
                : blockCoordinate >= blockHigh ? blockCoordinate - blockHigh
                : 0;
            var inlineDistance = inline < inlineLow ? inlineLow - inline
                : inline > inlineHigh ? inline - inlineHigh
                : 0;

            if (block < bestBlock || (block == bestBlock && inlineDistance < bestInline))
            {
                best = run;
                bestBlock = block;
                bestInline = inlineDistance;
            }
        }

        return best;
    }

    /// <summary>Gets which character boundary a point along the run's inline axis falls nearest to.</summary>
    private Int32 IndexAt(LayoutBox run, Double inline)
    {
        var distance = (inline - InlineBegin(run)) * run.Flow.InlineSign;
        var prefix = Prefixes(run);
        var length = prefix[^1];

        if (distance <= 0)
        {
            return 0;
        }

        if (distance >= length)
        {
            return prefix.Length - 1;
        }

        // The last boundary at or before the point; the binary search is what makes a hit on a long
        // paragraph cost a handful of table reads instead of a scan per character.
        var low = 0;
        var high = prefix.Length - 1;

        while (low < high)
        {
            var middle = (low + high + 1) / 2;

            if (prefix[middle] <= distance)
            {
                low = middle;
            }
            else
            {
                high = middle - 1;
            }
        }

        if (low >= prefix.Length - 1)
        {
            return prefix.Length - 1;
        }

        // The nearer of the two boundaries the point sits between, which is what makes the middle of
        // a character select that character rather than its neighbour.
        return distance - prefix[low] < prefix[low + 1] - distance ? low : low + 1;
    }

    /// <summary>Gets the coordinate of a character boundary along the run's inline axis.</summary>
    private Double OffsetAt(LayoutBox run, Int32 index)
    {
        var prefix = Prefixes(run);

        return InlineBegin(run) + prefix[Math.Clamp(index, 0, prefix.Length - 1)] * run.Flow.InlineSign;
    }

    /// <summary>Gets how far along its inline axis a point sits: an x for horizontal text, a y for a column.</summary>
    private static Double InlineCoordinate(LayoutBox run, Double x, Double y) => run.Flow.IsVertical ? y : x;

    /// <summary>Gets the coordinate a run starts at along its inline axis, where its reading starts.</summary>
    private static Double InlineBegin(LayoutBox run)
    {
        var box = run.BorderBox;

        if (run.Flow.InlineSign < 0)
        {
            return run.Flow.IsVertical ? box.Bottom : box.Right;
        }

        return run.Flow.IsVertical ? box.Y : box.X;
    }

    /// <summary>Gets the coordinate a run ends at along its inline axis.</summary>
    private static Double InlineEnd(LayoutBox run)
    {
        var box = run.BorderBox;

        if (run.Flow.InlineSign < 0)
        {
            return run.Flow.IsVertical ? box.Y : box.X;
        }

        return run.Flow.IsVertical ? box.Bottom : box.Right;
    }

    /// <summary>
    /// Gets the running advances of a run's characters, one entry per boundary, along the axis the run
    /// travels. Measured from the start of the run each time, because an advance can depend on what
    /// precedes it - kerning, at least - and the table is what pays for the repeated measuring exactly
    /// once.
    /// </summary>
    private Double[] Prefixes(LayoutBox run)
    {
        if (_prefixes.TryGetValue(run, out var cached))
        {
            return cached;
        }

        var text = run.Text ?? String.Empty;
        var prefix = new Double[text.Length + 1];

        // The letter spacing the layout gave the run is added here too, so a boundary falls where the
        // layout put it; without it a caret would drift further off with every character.
        var spacing = run.Source.Style.LetterSpacing;

        // A column sets an upright character in its em box rather than at its natural width, so the
        // boundaries are stepped the way the layout stepped them: the table has to agree with the run
        // the characters were placed by, or a hit lands on a neighbour.
        for (var index = 1; index <= text.Length; index++)
        {
            prefix[index] = run.Flow.IsVertical
                ? prefix[index - 1] + ColumnStep(run, text[index - 1]) + spacing
                : _measurer.MeasureAdvance(text[..index], run.Font, run.Flow) + (spacing * index);
        }

        _prefixes[run] = prefix;
        return prefix;
    }

    /// <summary>
    /// Gets how far one character of a column advances: a turned run by the width it would have had in
    /// a line, an upright one by its em box, and a run combined upright is spread across the single em
    /// the whole of it is squeezed into.
    /// </summary>
    private Double ColumnStep(LayoutBox run, Char character)
    {
        if (run.Flow.CombinesUpright)
        {
            return run.Font.Size / Math.Max(1, run.Text?.Length ?? 1);
        }

        var advance = _measurer.MeasureAdvance(character, run.Font, run.Flow);

        return run.Flow.IsUpright(character) ? Math.Max(advance, run.Font.Size) : advance;
    }
}

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
/// through a per-run table of prefix widths built on demand. Prefix widths are what turn an x
/// coordinate into an index, because the measurer only reports the advance of a string: the width of
/// everything before a character is what says where that character starts. Keeping the table is also
/// what stops a drag from re-measuring a paragraph on every pointer move, and it is dropped together
/// with the selector, i.e. with the layout it was built from.
/// <para>
/// Text inside a <c>fixed</c> or <c>sticky</c> box is left out. Its geometry is viewport-relative -
/// fixed boxes are laid out against the viewport and sticky ones are shifted at paint time - so
/// mapping a point onto it would need the scroll offset as well, which is a host concern. Every other
/// run is in document coordinates and needs nothing but the point.
/// </para>
/// </remarks>
public sealed class TextSelector
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
        LayoutBox? hit = null;

        foreach (var run in _runs)
        {
            var box = run.BorderBox;

            // The last run that contains the point wins: document order is the closest thing the
            // layout tree has to a stacking order, and an overlapping positioned box comes later.
            if (y >= box.Y && y < box.Bottom && x >= box.X && x <= box.Right)
            {
                hit = run;
            }
        }

        hit ??= Nearest(x, y);

        return hit is null ? null : new TextPosition(hit, IndexAtX(hit, x));
    }

    /// <summary>
    /// Gets the rectangles a selection covers, in document coordinates: one per line fragment, so a
    /// range that spans several lines reads as the several pieces of highlight a reader expects.
    /// </summary>
    public IReadOnlyList<Rect> Rects(TextSelection? selection)
    {
        var rects = new List<Rect>();

        if (!TryNormalize(selection, out var first, out var last, out var start, out var end))
        {
            return rects;
        }

        for (var order = first; order <= last; order++)
        {
            var run = _runs[order];
            var box = run.BorderBox;
            var from = order == first ? XAt(run, start.Index) : box.X;
            var to = order == last ? XAt(run, end.Index) : box.Right;

            // A range that only touches a run covers nothing of it; an empty one produces no
            // rectangle at all, which is what makes a plain click leave no highlight behind.
            if (to > from)
            {
                rects.Add(new Rect(from, box.Y, to - from, box.Height));
            }
        }

        return rects;
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

        if (!viewportAnchored && box.Kind == LayoutKind.Text && !String.IsNullOrEmpty(box.Text))
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
    /// Gets the run nearest a point, comparing distance down the page first and along the line
    /// second, so a point below a paragraph takes its last line rather than a nearer column.
    /// </summary>
    private LayoutBox? Nearest(Double x, Double y)
    {
        LayoutBox? best = null;
        var bestVertical = Double.MaxValue;
        var bestHorizontal = Double.MaxValue;

        foreach (var run in _runs)
        {
            var box = run.BorderBox;
            var vertical = y < box.Y ? box.Y - y : y >= box.Bottom ? y - box.Bottom : 0;
            var horizontal = x < box.X ? box.X - x : x > box.Right ? x - box.Right : 0;

            if (vertical < bestVertical || (vertical == bestVertical && horizontal < bestHorizontal))
            {
                best = run;
                bestVertical = vertical;
                bestHorizontal = horizontal;
            }
        }

        return best;
    }

    /// <summary>Gets which character boundary an x coordinate on the run falls nearest to.</summary>
    private Int32 IndexAtX(LayoutBox run, Double x)
    {
        var box = run.BorderBox;

        if (x <= box.X)
        {
            return 0;
        }

        var prefix = Prefixes(run);

        if (x >= box.Right)
        {
            return prefix.Length - 1;
        }

        var offset = x - box.X;

        // The last boundary at or before the point; the binary search is what makes a hit on a long
        // paragraph cost a handful of table reads instead of a scan per character.
        var low = 0;
        var high = prefix.Length - 1;

        while (low < high)
        {
            var middle = (low + high + 1) / 2;

            if (prefix[middle] <= offset)
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
        return offset - prefix[low] < prefix[low + 1] - offset ? low : low + 1;
    }

    /// <summary>Gets the x coordinate of a character boundary on a run.</summary>
    private Double XAt(LayoutBox run, Int32 index)
    {
        var prefix = Prefixes(run);

        return run.BorderBox.X + prefix[Math.Clamp(index, 0, prefix.Length - 1)];
    }

    /// <summary>
    /// Gets the running widths of a run's characters, one entry per boundary. Measured from the start
    /// of the run each time, because an advance can depend on what precedes it - kerning, at least -
    /// and the table is what pays for the repeated measuring exactly once.
    /// </summary>
    private Double[] Prefixes(LayoutBox run)
    {
        if (_prefixes.TryGetValue(run, out var cached))
        {
            return cached;
        }

        var text = run.Text ?? String.Empty;
        var prefix = new Double[text.Length + 1];

        for (var index = 1; index <= text.Length; index++)
        {
            prefix[index] = _measurer.MeasureWidth(text[..index], run.Font);
        }

        _prefixes[run] = prefix;
        return prefix;
    }
}

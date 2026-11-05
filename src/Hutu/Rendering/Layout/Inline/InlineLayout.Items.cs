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
    /// Flattens the container's inline content into the stream of runs and breaks the lines are built
    /// from, remembering for each run the chain of inline elements it sits inside.
    /// </summary>
    /// <param name="chain">The elements the content is already inside, innermost first.</param>
    /// <param name="basis">
    /// The inline extent a percentage among an element's padding and border resolves against. There is
    /// no definite line yet, so the extent the content is being fitted into stands in for one.
    /// </param>
    private void Collect(Box container, List<InlineItem> items, WritingModeInfo flow, InlineAncestor? chain, Double basis)
    {
        foreach (var child in container.Children)
        {
            // A note's placement owns the element's participation in flow before position and float
            // are considered: those properties are ignored on the defining element itself.
            if (child.Kind == BoxKind.Inline &&
                child.Style.NotePlacement == NotePlacement.Inline &&
                !InsideRubyText(chain))
            {
                var note = NoteSource.Read(child, hoistInterline: true, hoistMargin: true);

                if (note.Text.Length > 0 && note.HasInk)
                {
                    items.Add(InlineItem.CreateNote(note, child, chain));
                }
                else
                {
                    foreach (var nested in note.NestedInterline)
                    {
                        items.Add(InlineItem.CreateInterlineNote(
                            nested.Note.Content,
                            nested.Note.Box,
                            chain));
                    }

                    foreach (var nested in note.NestedMargin)
                    {
                        items.Add(InlineItem.CreateMarginNote(nested.Note.Box, chain));
                    }
                }

                continue;
            }

            if (child.Kind == BoxKind.Inline &&
                child.Style.NotePlacement == NotePlacement.Interline &&
                !InsideRubyText(chain))
            {
                var note = NoteSource.Read(child, hoistMargin: true);

                if (note.Text.Length > 0 && note.HasInk)
                {
                    items.Add(InlineItem.CreateInterlineNote(note, child, chain));
                }

                continue;
            }

            if (child.Kind == BoxKind.Inline &&
                child.Style.NotePlacement == NotePlacement.Margin &&
                !InsideRubyText(chain))
            {
                var note = NoteSource.Read(child);

                if (note.Text.Length > 0 && note.HasInk)
                {
                    items.Add(InlineItem.CreateMarginNote(child, chain));
                }

                continue;
            }

            // A box that left the flow is not part of the line: it is placed against a containing block of
            // its own. It is kept as an item of no extent so the place it would have had is still known -
            // that place is the static position an offset left unset falls back to - while the line it
            // sits on is neither wider nor taller for it.
            if (PositionedLayout.IsOutOfFlow(child))
            {
                items.Add(InlineItem.CreateOutOfFlow(child, chain));
                continue;
            }

            switch (child.Kind)
            {
                case BoxKind.Text:
                    if (!String.IsNullOrEmpty(child.Text))
                    {
                        AddText(child.Text, child.Style, child, items, chain);
                    }

                    break;

                case BoxKind.LineBreak:
                    items.Add(InlineItem.CreateBreak(child.Style, child, chain));
                    break;

                // A replaced inline box is atomic: unlike other inline boxes it is not flattened into
                // its text runs, it occupies its own used size on the line.
                case BoxKind.Inline when child.IsReplaced:
                    items.Add(InlineItem.CreateReplaced(child, chain));
                    break;

                // An inline-block is atomic: it takes part in the line as one piece of the size its own
                // content asks for, and is laid out as the block container it is rather than being
                // broken into the runs that would otherwise carry the line.
                case BoxKind.InlineBlock:
                    items.Add(InlineItem.CreateAtomic(child, chain));
                    break;

                // An inline-flex, inline-grid or inline-table box is atomic on the same terms: its
                // outer role is inline, so the line carries it whole, while its inner role is the
                // container its kind names and its own context lays the content out that way. Only the
                // inline spellings reach here - a block-level container is not inline content, and its
                // box never stands among the runs a line is built from.
                case BoxKind.Flex:
                case BoxKind.Grid:
                case BoxKind.Masonry:
                case BoxKind.Table:
                    if (child.IsInlineOuter)
                    {
                        items.Add(InlineItem.CreateAtomic(child, chain));
                    }

                    break;

                // A ruby pairs its base text with the annotation that goes over it, so the two are
                // gathered into one item instead of being run together as ordinary text. One that
                // holds nothing still stands on the line for the edges its box carries along it.
                case BoxKind.Ruby:
                {
                    var before = items.Count;
                    AddRuby(child, items, chain);

                    if (items.Count == before)
                    {
                        var ancestor = new InlineAncestor(child, chain, basis, flow, _measurer);

                        if (CarriesAnInlineEdge(ancestor))
                        {
                            items.Add(InlineItem.CreateEmpty(child, ancestor));
                        }
                    }

                    break;
                }

                // An ordinary inline element keeps its own box. It is not flattened away: the runs inside
                // it go on carrying it, so the line can give it back the box that draws it.
                case BoxKind.Inline:
                {
                    var ancestor = new InlineAncestor(child, chain, basis, flow, _measurer);
                    var before = items.Count;

                    Collect(child, items, flow, ancestor, basis);

                    // A box with nothing inside it still stands on the line for the margins, borders
                    // and padding it carries along that axis, and draws its box there. One that
                    // carries none of them draws nothing at all, and would be a line of its own.
                    if (items.Count == before && CarriesAnInlineEdge(ancestor))
                    {
                        items.Add(InlineItem.CreateEmpty(child, ancestor));
                    }

                    break;
                }

                // A base or a reading is a run like any other; only the pair around them is set apart.
                case BoxKind.RubyBase:
                case BoxKind.RubyText:
                    Collect(child, items, flow, chain, basis);
                    break;
            }
        }
    }

    /// <summary>
    /// Gets whether an inline element carries a margin, border or padding along the axis a line runs,
    /// which is what the line keeps room for even when the element holds nothing.
    /// </summary>
    private static Boolean CarriesAnInlineEdge(InlineAncestor? ancestor) =>
        ancestor is not null &&
        (ancestor.InlineStartInset > 0 ||
            ancestor.InlineEndInset > 0 ||
            ancestor.InlineStartMargin != 0 ||
            ancestor.InlineEndMargin != 0);

    /// <summary>
    /// Gets whether the element a run is written in begins at that run: the line has not opened it
    /// yet. An element that does not wrap its text is one unit the line moves whole, so a run that
    /// opens one may be handed back to the opportunity before it even though nothing inside it may
    /// be broken (CSS Text 3 §5.1, §5.2).
    /// </summary>
    private static Boolean BeginsHere(LineBuilder builder, InlineAncestor? inline) =>
        inline is not null && !builder.IsOpen(inline);

    /// <summary>
    /// Gets whether an item sits inside an element, at any depth - the test that says the steps
    /// taken since a break opportunity belong to the box that now fails to fit.
    /// </summary>
    private static Boolean SitsWithin(InlineAncestor? chain, InlineAncestor? unit)
    {
        if (unit is null)
        {
            return false;
        }

        for (var at = chain; at is not null; at = at.Parent)
        {
            if (ReferenceEquals(at, unit))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Turns a <c>ruby</c> element into one item carrying its base text and the annotation beside it.
    /// </summary>
    /// <remarks>
    /// A base is whatever is not an annotation: an explicit <c>rb</c>, the element's own text, or any
    /// other inline content. Several bases under one annotation are read as a single base, which is
    /// what all but the longest ruby passage comes to.
    /// </remarks>
    private static void AddRuby(Box ruby, List<InlineItem> items, InlineAncestor? chain)
    {
        // A ruby is a sequence of pairs: each reading is set over the base it follows, and it is that
        // base alone the reading is centred on - which is what puts a mark above its own character rather
        // than spread out along the whole run (CSS Ruby 1 §3.1).
        var bases = new List<String>();
        var readings = new List<(String Text, ComputedStyle Style, Box Box)>();

        var baseText = new System.Text.StringBuilder();
        var readingText = new System.Text.StringBuilder();
        var readingStyle = ruby.Style;
        Box? readingBox = null;

        void CloseReading()
        {
            if (readingText.Length > 0)
            {
                readings.Add((readingText.ToString(), readingStyle, readingBox ?? ruby));
            }

            readingText = new System.Text.StringBuilder();
            readingBox = null;
            readingStyle = ruby.Style;
        }

        foreach (var child in ruby.Children)
        {
            if (child.Kind == BoxKind.RubyText)
            {
                // The base of the pair ends where its reading begins.
                if (baseText.Length > 0)
                {
                    bases.Add(baseText.ToString());
                    baseText = new System.Text.StringBuilder();
                }

                Flatten(child, readingText);

                // The first element of a reading is the one it is set in; a second written beside it
                // joins the text of the same pair, as it always has.
                readingBox ??= child;
                readingStyle = child.Style;
                continue;
            }

            // A base begins where the reading of the pair before it ends.
            CloseReading();
            Flatten(child, baseText);
        }

        CloseReading();

        if (baseText.Length > 0)
        {
            bases.Add(baseText.ToString());
        }

        if (bases.Count == 0)
        {
            // A ruby with nothing to annotate has nothing to contribute.
            return;
        }

        if (readings.Count == 0)
        {
            // Without an annotation there is nothing to set the base apart from ordinary text.
            items.Add(InlineItem.CreateText(String.Concat(bases), ruby.Style, ruby, chain));
            return;
        }

        // Each base carrying the reading that follows it, and at most one base at the end carrying none -
        // a reading with no base before it, or anything else that will not pair off, is set as one
        // passage over the whole ruby, which is what all but the shortest ruby passage comes to.
        var off = bases.Count - readings.Count;

        if (off == 0 || (off == 1 && bases[^1].Length > 0))
        {
            for (var index = 0; index < readings.Count; index++)
            {
                var (text, style, box) = readings[index];

                items.Add(InlineItem.CreateRuby(
                    bases[index],
                    ruby.Style,
                    ruby,
                    text,
                    style,
                    box,
                    chain));
            }

            if (off == 1)
            {
                items.Add(InlineItem.CreateText(bases[^1], ruby.Style, ruby, chain));
            }

            return;
        }

        items.Add(InlineItem.CreateRuby(
            String.Concat(bases),
            ruby.Style,
            ruby,
            String.Concat(readings.ConvertAll(reading => reading.Text)),
            readings[0].Style,
            readings[0].Box,
            chain));
    }

    /// <summary>Appends every character of text held anywhere under a box, in document order.</summary>
    private static void Flatten(Box box, System.Text.StringBuilder text)
    {
        if (box.Kind == BoxKind.Text)
        {
            text.Append(box.Text);
            return;
        }

        foreach (var child in box.Children)
        {
            Flatten(child, text);
        }
    }

    /// <summary>Gets whether a run sits inside the reading half of a ruby pair, where notes do not apply.</summary>
    private static Boolean InsideRubyText(InlineAncestor? chain)
    {
        for (var ancestor = chain; ancestor is not null; ancestor = ancestor.Parent)
        {
            if (ancestor.Box.Element?.LocalName is "rt" or "rtc")
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Takes the physical edges one fragment of a sliced inline box carries: inline-start only on the
    /// first fragment, inline-end only on the last, and both block edges everywhere between.
    /// </summary>
    private static Thickness SliceEdges(WritingModeInfo flow, Thickness edges, Boolean first, Boolean last)
    {
        var top = edges.Top;
        var right = edges.Right;
        var bottom = edges.Bottom;
        var left = edges.Left;

        if (!flow.IsVertical)
        {
            if (flow.InlineSign > 0)
            {
                if (!first)
                {
                    left = 0;
                }

                if (!last)
                {
                    right = 0;
                }
            }
            else
            {
                if (!first)
                {
                    right = 0;
                }

                if (!last)
                {
                    left = 0;
                }
            }
        }
        else if (flow.InlineSign > 0)
        {
            if (!first)
            {
                top = 0;
            }

            if (!last)
            {
                bottom = 0;
            }
        }
        else
        {
            if (!first)
            {
                bottom = 0;
            }

            if (!last)
            {
                top = 0;
            }
        }

        return new Thickness(top, right, bottom, left);
    }

    /// <summary>Turns one range of a note into ordinary inline items for the shared line composer.</summary>
    private List<InlineItem> NoteItems(
        NoteSource source,
        Int32 start,
        Int32 end,
        Double basis,
        WritingModeInfo flow,
        Boolean upright,
        InlineAncestor? root = null)
    {
        var items = RentItems();
        var ancestors = new Dictionary<Box, InlineAncestor>();
        var markers = source.NestedInterline
            .Where(marker =>
                marker.Position >= start &&
                (marker.Position < end || (marker.Position == end && end == source.Text.Length)))
            .ToList();
        var markerIndex = 0;
        var marginMarkers = source.NestedMargin
            .Where(marker =>
                marker.Position >= start &&
                (marker.Position < end || (marker.Position == end && end == source.Text.Length)))
            .ToList();
        var marginIndex = 0;

        InlineAncestor? ChainFor(IReadOnlyList<Box> boxes)
        {
            InlineAncestor? chain = root;

            foreach (var ancestor in boxes)
            {
                if (!ancestors.TryGetValue(ancestor, out chain))
                {
                    chain = new InlineAncestor(ancestor, chain, basis, flow, _measurer, noteContent: true);
                    ancestors.Add(ancestor, chain);
                }
            }

            return chain;
        }

        void AddMarker(NestedInterlineNote marker)
        {
            items.Add(InlineItem.CreateInterlineNote(
                marker.Note.Content,
                marker.Note.Box,
                ChainFor(marker.Ancestors)));
        }

        void AddMarginMarker(NestedMarginNote marker)
        {
            items.Add(InlineItem.CreateMarginNote(
                marker.Note.Box,
                ChainFor(marker.Ancestors)));
        }

        void AddMarkersThrough(Int32 position)
        {
            while (true)
            {
                var interlinePosition = markerIndex < markers.Count
                    ? markers[markerIndex].Position
                    : Int32.MaxValue;
                var marginPosition = marginIndex < marginMarkers.Count
                    ? marginMarkers[marginIndex].Position
                    : Int32.MaxValue;
                var next = Math.Min(interlinePosition, marginPosition);

                if (next > position)
                {
                    return;
                }

                if (interlinePosition <= marginPosition)
                {
                    AddMarker(markers[markerIndex++]);
                }
                else
                {
                    AddMarginMarker(marginMarkers[marginIndex++]);
                }
            }
        }

        void AddText(NoteSpan span, Int32 from, Int32 to)
        {
            if (to <= from)
            {
                return;
            }

            var text = source.Text[from..to];
            var partFlow = upright
                ? new WritingModeInfo(
                    flow.Mode,
                    TextOrientation.Upright,
                    flow.CombinesUpright,
                    flow.Direction)
                : flow;

            items.Add(InlineItem.CreateText(
                text,
                span.Style,
                span.Box,
                ChainFor(span.Ancestors),
                partFlow));
        }

        foreach (var span in source.Spans)
        {
            var from = Math.Max(start, span.Start);
            var to = Math.Min(end, span.Start + span.Length);

            if (to <= from)
            {
                continue;
            }

            AddMarkersThrough(from);

            while (true)
            {
                var interlinePosition = markerIndex < markers.Count
                    ? markers[markerIndex].Position
                    : Int32.MaxValue;
                var marginPosition = marginIndex < marginMarkers.Count
                    ? marginMarkers[marginIndex].Position
                    : Int32.MaxValue;
                var at = Math.Min(interlinePosition, marginPosition);

                if (at >= to)
                {
                    break;
                }

                AddText(span, from, at);

                if (interlinePosition <= marginPosition)
                {
                    AddMarker(markers[markerIndex++]);
                }
                else
                {
                    AddMarginMarker(marginMarkers[marginIndex++]);
                }

                from = at;
            }

            AddText(span, from, to);
            AddMarkersThrough(to);
        }

        AddMarkersThrough(end);
        return items;
    }

    private static Boolean HasUprightCharacters(String text, Int32 start, Int32 end)
    {
        foreach (var rune in text[start..end].EnumerateRunes())
        {
            var value = rune.Value;

            if (value is >= 0x2E80 and <= 0x9FFF or
                >= 0xF900 and <= 0xFAFF or
                >= 0xFE30 and <= 0xFE4F or
                >= 0xFF00 and <= 0xFFEF or
                >= 0x20000 and <= 0x3FFFF)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Lays one note subline through the ordinary line composer, forced to a single line.</summary>
    private Line? NoteLine(
        NoteSource source,
        Int32 start,
        Int32 end,
        ComputedStyle noteStyle,
        WritingModeInfo flow,
        InlineContext context,
        Double maxInline,
        Boolean? orientation = null)
    {
        if (end <= start)
        {
            return null;
        }

        // A note is set in one orientation: the one its whole text asks for, not the one a piece of
        // it would ask for on its own. The caller that splits a note hands the decided value in.
        var upright = orientation ?? (flow.IsVertical && HasUprightCharacters(source.Text, start, end));
        var noteItems = NoteItems(source, start, end, maxInline, flow, upright);
        var lines = BuildLines(
            noteItems,
            maxInline,
            flow,
            context,
            noteStyle,
            emergency: false,
            singleLine: true,
            settleEdges: false);
        ReturnItems(noteItems);

        if (lines.Count == 0)
        {
            return null;
        }

        if (lines.Count != 1)
        {
            var excerpt = source.Text.Substring(start, Math.Min(80, end - start));
            throw new InvalidOperationException(
                $"An inline-note subline must build exactly one line; got {lines.Count} for \"{excerpt}\".");
        }

        var line = lines[0];

        if (line.Used <= 0 && !line.Fragments.Exists(HasInk))
        {
            return null;
        }

        line.Left = 0;
        line.GapBefore = 0;
        line.Extent = line.Used;
        line.Offset = 0;

        return line;
    }

}

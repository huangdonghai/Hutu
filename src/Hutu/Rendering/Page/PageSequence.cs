using Hutu.Rendering.Boxes;
using Hutu.Rendering.Layout;
using System;
using System.Collections.Generic;

namespace Hutu.Rendering;

/// <summary>
/// Cuts one tall layout into the pages a printed document comes to.
/// </summary>
/// <remarks>
/// The document is laid out once at the width of the page area; a page is then a slice of that tall
/// layout, so no line is ever cut and a box that spans a break is painted once and cut with it, the
/// way <c>box-decoration-break: slice</c> asks (CSS Break 3 §4.7). What decides a cut is the flow of
/// break opportunities: the top edge of every block box and every line, carrying the break values the
/// boxes state, the <c>break-inside: avoid</c> of the boxes they stand in, and the orphans and widows
/// of the line run a cut falls inside (CSS Break 3 §3).
/// </remarks>
internal static class PageSequence
{
    /// <summary>
    /// Gets the one blank page a document with no box at all prints: a root that generates nothing
    /// still prints a sheet, and the sheet is the page box the rule states (CSS Page 3 §3).
    /// </summary>
    public static PagedLayout Blank(PageGeometry geometry) =>
        new(geometry,
            new[] { new PageSlice(1, geometry, new Rect(0, 0, geometry.ContentWidth, 0)) },
            0);

    public static PagedLayout Build(LayoutResult layout, PageStyleTable styles, StyleThickness inheritedMargins)
    {
        var units = new List<FlowUnit>();
        new Collector().Collect(
            layout.Root, units, BreakValue.Auto, BreakValue.Auto, Array.Empty<Int32>(), null);

        var documentHeight = 0.0;

        foreach (var unit in units)
        {
            documentHeight = Math.Max(documentHeight, unit.Bottom);
        }

        // A document with no flow unit at all - an absolutely positioned picture, a root that states
        // only a height - still has the size the layout gave it. The units are the flow's own end,
        // because the layout's height includes the viewport the document was measured in.
        if (units.Count == 0)
        {
            documentHeight = Math.Max(0, layout.Height);
        }

        var pages = new List<PageSlice>();

        if (documentHeight <= 0.001)
        {
            var empty = styles.DefaultPage(inheritedMargins);
            pages.Add(new PageSlice(1, empty, new Rect(0, 0, empty.ContentWidth, 0), null));
            return new PagedLayout(empty, pages, documentHeight);
        }

        var start = 0.0;
        var number = 1;
        PageGeometry? first = null;

        while (start < documentHeight - 0.001)
        {
            var name = NameAt(units, start);
            var geometry = styles.Page(name, inheritedMargins);
            var contentHeight = Math.Max(1, geometry.ContentHeight);
            var limit = Math.Min(documentHeight, start + contentHeight);
            var cut = Cut(units, start, limit, documentHeight);

            if (cut <= start + 0.001)
            {
                cut = Math.Min(documentHeight, Math.Max(limit, start + 0.001));
            }

            if (cut <= start)
            {
                cut = documentHeight;
            }

            first ??= geometry;
            pages.Add(new PageSlice(
                number++, geometry, new Rect(0, start, geometry.ContentWidth, Math.Max(0, cut - start)), name));

            start = cut;
        }

        return new PagedLayout(first ?? styles.DefaultPage(inheritedMargins), pages, documentHeight);
    }

    /// <summary>Gets the page name the content at the head of a page states.</summary>
    private static String? NameAt(List<FlowUnit> units, Double start)
    {
        foreach (var unit in units)
        {
            if (unit.Bottom > start + 0.001)
            {
                return unit.PageName;
            }
        }

        return null;
    }

    /// <summary>
    /// Gets where the page that begins at <paramref name="start"/> ends: the first forced break inside
    /// the page, else the last break the flow allows, else the last break it merely prefers, and with
    /// none of those the page's own limit - a monolithic box taller than the page has to be cut
    /// somewhere, or the flow would never advance.
    /// </summary>
    private static Double Cut(List<FlowUnit> units, Double start, Double limit, Double end)
    {
        Double? forced = null;
        Double? allowed = null;
        Double? avoided = null;
        var linesOnPage = new Dictionary<Int32, Int32>();

        for (var index = 0; index < units.Count; index++)
        {
            var unit = units[index];

            if (index > 0)
            {
                var y = unit.Top;

                if (y > start + 0.001 && y <= limit + 0.001)
                {
                    var kind = Boundary(units[index - 1], unit, linesOnPage);

                    if (kind == BreakClass.Forced)
                    {
                        forced = y;
                        break;
                    }

                    if (kind == BreakClass.Allowed)
                    {
                        allowed = y;
                    }
                    else if (kind == BreakClass.Avoid)
                    {
                        avoided = y;
                    }
                }
            }

            if (unit.IsLine && unit.Bottom > start + 0.001)
            {
                linesOnPage[unit.Run] = linesOnPage.TryGetValue(unit.Run, out var count) ? count + 1 : 1;
            }
        }

        if (forced is { } forcedAt)
        {
            return Math.Min(forcedAt, end);
        }

        // Everything that is left fits the page: no break opportunity is better than the document's
        // own end.
        if (end <= limit + 0.001)
        {
            return end;
        }

        if (allowed is { } allowedAt)
        {
            return Math.Min(allowedAt, end);
        }

        if (avoided is { } avoidedAt)
        {
            return Math.Min(avoidedAt, end);
        }

        return Math.Min(limit, end);
    }

    private static BreakClass Boundary(FlowUnit previous, FlowUnit next, Dictionary<Int32, Int32> linesOnPage)
    {
        if (!String.Equals(previous.PageName, next.PageName, StringComparison.Ordinal))
        {
            return BreakClass.Forced;
        }

        var combined = Fragmentainer.Combine(previous.After, next.Before);

        if (combined is BreakValue.Page or BreakValue.Left or BreakValue.Right)
        {
            return BreakClass.Forced;
        }

        if (combined == BreakValue.Avoid || previous.KeepWithNext || SharesAvoid(previous, next))
        {
            return BreakClass.Avoid;
        }

        if (previous.IsLine && next.IsLine && previous.Run == next.Run)
        {
            var before = linesOnPage.TryGetValue(next.Run, out var count) ? count : 0;
            var after = next.LineTotal - next.LineIndex;

            if (before < next.Orphans || after < next.Widows)
            {
                return BreakClass.Avoid;
            }
        }

        return BreakClass.Allowed;
    }

    private static Boolean SharesAvoid(FlowUnit one, FlowUnit other)
    {
        if (one.Avoid.Length == 0 || other.Avoid.Length == 0)
        {
            return false;
        }

        var i = 0;
        var j = 0;

        while (i < one.Avoid.Length && j < other.Avoid.Length)
        {
            if (one.Avoid[i] == other.Avoid[j])
            {
                return true;
            }

            if (one.Avoid[i] < other.Avoid[j])
            {
                i++;
            }
            else
            {
                j++;
            }
        }

        return false;
    }

    private enum BreakClass : Byte
    {
        Allowed,
        Avoid,
        Forced,
    }

    private sealed class Collector
    {
        private Int32 _nextRun;
        private Int32 _nextAvoid;

        public void Collect(
            LayoutBox box,
            List<FlowUnit> units,
            BreakValue inheritedBefore,
            BreakValue inheritedAfter,
            Int32[] avoid,
            String? pageName)
        {
            var source = box.Source;

            if (PositionedLayout.IsOutOfFlow(source) || source.Style.Float != FloatSide.None)
            {
                return;
            }

            var name = source.Style.PageName ?? pageName;
            var ownBefore = PageValue(source.Style.Breaks.Before);
            var ownAfter = PageValue(source.Style.Breaks.After);

            if (box.Kind == LayoutKind.Table)
            {
                CollectTable(box, units, inheritedBefore, inheritedAfter, avoid, name);
                return;
            }

            if (IsMonolithic(box))
            {
                units.Add(BoxUnit(
                    box,
                    Fragmentainer.Combine(inheritedBefore, ownBefore),
                    Fragmentainer.Combine(inheritedAfter, ownAfter),
                    avoid,
                    name));
                return;
            }

            var before = Fragmentainer.Combine(inheritedBefore, ownBefore);
            var after = Fragmentainer.Combine(inheritedAfter, ownAfter);
            var inside = source.Style.Breaks.Inside == BreakValue.Avoid
                ? Append(avoid, ++_nextAvoid)
                : avoid;

            var first = units.Count;
            var flow = FlowChildren(box);

            for (var index = 0; index < flow.Count;)
            {
                var child = flow[index];
                var childBefore = index == 0 ? before : BreakValue.Auto;
                var childAfter = index == flow.Count - 1 ? after : BreakValue.Auto;

                if (child.Kind == LayoutKind.Line)
                {
                    var end = index;

                    while (end < flow.Count && flow[end].Kind == LayoutKind.Line)
                    {
                        end++;
                    }

                    CollectLines(box, flow, index, end, units, childBefore, childAfter, inside, name);
                    index = end;
                    continue;
                }

                Collect(child, units, childBefore, childAfter, inside, name);
                index++;
            }

            // Nothing in flow: the box itself is the one piece its break values stand on.
            if (units.Count == first)
            {
                units.Add(BoxUnit(box, before, after, avoid, name));
            }
        }

        /// <summary>
        /// Collects a table as the rows a page may end between. A row is one piece - the engine lays
        /// every cell of it on the same line - and a row group's own break values and avoid reach its
        /// first and last row (CSS Break 3 §3.1, §4.6). A caption stays with the row after it where the
        /// table can manage that, so a page does not end between a table and its own caption.
        /// </summary>
        private void CollectTable(
            LayoutBox table,
            List<FlowUnit> units,
            BreakValue inheritedBefore,
            BreakValue inheritedAfter,
            Int32[] avoid,
            String? pageName)
        {
            var source = table.Source;
            var name = source.Style.PageName ?? pageName;
            var before = Fragmentainer.Combine(inheritedBefore, PageValue(source.Style.Breaks.Before));
            var after = Fragmentainer.Combine(inheritedAfter, PageValue(source.Style.Breaks.After));
            var inside = source.Style.Breaks.Inside == BreakValue.Avoid
                ? Append(avoid, ++_nextAvoid)
                : avoid;

            var first = units.Count;
            var flow = new List<LayoutBox>(table.Children.Count);

            foreach (var child in table.Children)
            {
                if (PositionedLayout.IsOutOfFlow(child.Source) ||
                    child.Source.Style.Float != FloatSide.None)
                {
                    continue;
                }

                flow.Add(child);
            }

            for (var index = 0; index < flow.Count; index++)
            {
                var child = flow[index];
                var childBefore = index == 0 ? before : BreakValue.Auto;
                var childAfter = index == flow.Count - 1 ? after : BreakValue.Auto;

                if (child.Kind == LayoutKind.TableRowGroup)
                {
                    CollectRowGroup(child, units, childBefore, childAfter, inside, name);
                }
                else if (child.Kind == LayoutKind.TableRow)
                {
                    AddRow(child, units, childBefore, childAfter, inside, name);
                }
                else
                {
                    Collect(child, units, childBefore, childAfter, inside, name);

                    if (child.Source.Kind == BoxKind.TableCaption && units.Count > first)
                    {
                        units[^1].KeepWithNext = true;
                    }
                }
            }

            if (units.Count == first)
            {
                units.Add(BoxUnit(table, before, after, avoid, name));
            }
        }

        private void CollectRowGroup(
            LayoutBox group,
            List<FlowUnit> units,
            BreakValue inheritedBefore,
            BreakValue inheritedAfter,
            Int32[] avoid,
            String? pageName)
        {
            var source = group.Source;
            var name = source.Style.PageName ?? pageName;
            var before = Fragmentainer.Combine(inheritedBefore, PageValue(source.Style.Breaks.Before));
            var after = Fragmentainer.Combine(inheritedAfter, PageValue(source.Style.Breaks.After));
            var inside = source.Style.Breaks.Inside == BreakValue.Avoid
                ? Append(avoid, ++_nextAvoid)
                : avoid;

            var first = units.Count;
            var rows = new List<LayoutBox>(group.Children.Count);

            foreach (var child in group.Children)
            {
                if (child.Kind == LayoutKind.TableRow &&
                    !PositionedLayout.IsOutOfFlow(child.Source) &&
                    child.Source.Style.Float == FloatSide.None)
                {
                    rows.Add(child);
                }
            }

            for (var index = 0; index < rows.Count; index++)
            {
                AddRow(
                    rows[index],
                    units,
                    index == 0 ? before : BreakValue.Auto,
                    index == rows.Count - 1 ? after : BreakValue.Auto,
                    inside,
                    name);
            }

            if (units.Count == first)
            {
                units.Add(BoxUnit(group, before, after, avoid, name));
            }
        }

        private static void AddRow(
            LayoutBox row,
            List<FlowUnit> units,
            BreakValue before,
            BreakValue after,
            Int32[] avoid,
            String? pageName)
        {
            units.Add(BoxUnit(
                row,
                Fragmentainer.Combine(before, PageValue(row.Source.Style.Breaks.Before)),
                Fragmentainer.Combine(after, PageValue(row.Source.Style.Breaks.After)),
                avoid,
                pageName));
        }

        private void CollectLines(
            LayoutBox owner,
            List<LayoutBox> flow,
            Int32 from,
            Int32 to,
            List<FlowUnit> units,
            BreakValue before,
            BreakValue after,
            Int32[] avoid,
            String? pageName)
        {
            var run = ++_nextRun;
            var total = to - from;
            var breaks = owner.Source.Style.Breaks;
            var orphans = Math.Max(1, breaks.Orphans);
            var widows = Math.Max(1, breaks.Widows);

            for (var index = from; index < to; index++)
            {
                var line = flow[index];
                var unit = new FlowUnit
                {
                    Top = line.BorderBox.Y,
                    Bottom = line.BorderBox.Bottom,
                    Avoid = avoid,
                    PageName = pageName,
                    Run = run,
                    LineIndex = index - from,
                    LineTotal = total,
                    Orphans = orphans,
                    Widows = widows,
                };

                if (index == from)
                {
                    unit.Before = before;
                }

                if (index == to - 1)
                {
                    unit.After = after;
                }

                units.Add(unit);
            }
        }

        private static FlowUnit BoxUnit(
            LayoutBox box,
            BreakValue before,
            BreakValue after,
            Int32[] avoid,
            String? pageName) =>
            new()
            {
                Top = box.BorderBox.Y,
                Bottom = box.BorderBox.Bottom,
                Before = before,
                After = after,
                Avoid = avoid,
                PageName = pageName,
            };

        /// <summary>
        /// Gets the children that take part in block flow, in the order they were laid. A float and an
        /// out-of-flow box take no place a page can break at; the text and inline boxes of a line are
        /// inside the line, which is the unit a break falls between.
        /// </summary>
        private static List<LayoutBox> FlowChildren(LayoutBox box)
        {
            var flow = new List<LayoutBox>(box.Children.Count);

            foreach (var child in box.Children)
            {
                if (child.Kind is LayoutKind.Text or LayoutKind.Inline ||
                    PositionedLayout.IsOutOfFlow(child.Source) ||
                    child.Source.Style.Float != FloatSide.None)
                {
                    continue;
                }

                flow.Add(child);
            }

            return flow;
        }

        /// <summary>
        /// Gets whether the box is one piece, so a page cannot end inside it. A replaced box, a table,
        /// a flex or grid container, a multi-column container and a box that clips its overflow are all
        /// laid as one picture by the engine; a vertical flow is left whole until paged columns are
        /// implemented.
        /// </summary>
        private static Boolean IsMonolithic(LayoutBox box)
        {
            if (box.Kind is LayoutKind.Image or LayoutKind.Svg or LayoutKind.HorizontalRule or
                LayoutKind.TableRowGroup or LayoutKind.TableRow or LayoutKind.TableCell)
            {
                return true;
            }

            var source = box.Source;

            return source.IsReplaced ||
                source.Kind is BoxKind.Flex or BoxKind.Grid or BoxKind.Masonry or BoxKind.InlineBlock ||
                source.Style.Multi.IsMultiColumn ||
                source.Style.ClipsOverflow ||
                source.Flow.IsVertical;
        }
    }

    private static Int32[] Append(Int32[] chain, Int32 id)
    {
        var next = new Int32[chain.Length + 1];
        Array.Copy(chain, next, chain.Length);
        next[^1] = id;
        return next;
    }

    /// <summary>
    /// Reads a break value as a paged flow sees it: a column break has no page to end, while a page,
    /// left or right value forces one (CSS Break 3 §3.1). The avoid of either refinement is kept as an
    /// avoid; telling <c>avoid-page</c> from <c>avoid-column</c> needs their two styles apart, which
    /// the reader does not keep yet.
    /// </summary>
    private static BreakValue PageValue(BreakValue value) =>
        value == BreakValue.Column ? BreakValue.Auto : value;

    private sealed class FlowUnit
    {
        public Double Top { get; init; }

        public Double Bottom { get; init; }

        public BreakValue Before { get; set; } = BreakValue.Auto;

        public BreakValue After { get; set; } = BreakValue.Auto;

        /// <summary>Gets whether a break may not fall between this unit and the one after it where
        /// there is any other place to break - a table caption and the row it belongs to.</summary>
        public Boolean KeepWithNext { get; set; }

        public Int32[] Avoid { get; init; } = Array.Empty<Int32>();

        public String? PageName { get; init; }

        public Int32 Run { get; init; } = -1;

        public Int32 LineIndex { get; init; }

        public Int32 LineTotal { get; init; }

        public Int32 Orphans { get; init; } = 1;

        public Int32 Widows { get; init; } = 1;

        public Boolean IsLine => Run >= 0;
    }
}

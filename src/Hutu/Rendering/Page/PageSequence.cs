using Hutu.Rendering.Boxes;
using Hutu.Rendering.Layout;
using System;
using System.Collections.Generic;
using System.Linq;

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
        var collector = new Collector();
        collector.Collect(
            layout.Root, units, BreakValue.Auto, BreakValue.Auto, Array.Empty<Int32>(), null);

        var documentHeight = collector.OutOfFlowBottom;

        foreach (var unit in units)
        {
            documentHeight = Math.Max(documentHeight, unit.Bottom);
        }

        // A box's own height may extend past the last line or child that gave it its flow, and a
        // positioned box that takes no page break still paints. The root is left out: its box is
        // the window the document was measured in, which is not content.
        foreach (var child in layout.Root.Children)
        {
            documentHeight = Math.Max(documentHeight, ContentBottom(child));
        }

        // A document with no flow unit at all - an absolutely positioned picture, a root that states
        // only a height - still has the size the layout gave it. The units are the flow's own end,
        // because the layout's height includes the viewport the document was measured in.
        if (units.Count == 0 && documentHeight <= 0)
        {
            documentHeight = Math.Max(0, layout.Height);
        }

        var pages = new List<PageSlice>();

        if (documentHeight <= 0.001)
        {
            var empty = styles.DefaultPage(inheritedMargins);
            pages.Add(new PageSlice(1, empty, new Rect(0, 0, empty.ContentWidth, 0), null));
            return new PagedLayout(empty, WithMarginBoxes(pages, styles), documentHeight);
        }

        var start = 0.0;
        var number = 1;
        PageGeometry? first = null;
        var tail = false;
        BreakValue? pendingSide = null;

        while (start < documentHeight - 0.001 || tail)
        {
            var name = NameAt(units, start);
            var isLeft = number % 2 == 0;

            // A forced left or right break names the side the next page must stand on. When the page
            // the flow reached stands on the other side, a blank page is printed to put it right
            // (CSS Page 3 §3.2, and the `:blank` page a reader styles).
            if (pendingSide is { } side &&
                (side == BreakValue.Left) != isLeft)
            {
                var blank = styles.Page(name, number, isLeft, blank: true, inheritedMargins);
                pages.Add(new PageSlice(
                    number++, blank, new Rect(0, start, blank.ContentWidth, 0), name, Blank: true));
                isLeft = !isLeft;
            }

            pendingSide = null;

            var geometry = styles.Page(name, number, isLeft, blank: false, inheritedMargins);

            // A forced break may leave nothing behind it but a zero-height box. That box is still on
            // the next page, and the page it is on is printed even though it has no height.
            if (start >= documentHeight - 0.001)
            {
                first ??= geometry;
                pages.Add(new PageSlice(
                    number, geometry, new Rect(0, start, geometry.ContentWidth, 0), name));
                break;
            }

            var contentHeight = Math.Max(1, geometry.ContentHeight);
            var limit = Math.Min(documentHeight, start + contentHeight);
            var cut = Cut(units, start, limit, documentHeight, out var forcedBreak, out var resume);
            tail = forcedBreak is BreakValue.Page or BreakValue.Left or BreakValue.Right &&
                HasUnitFrom(units, cut);
            pendingSide = forcedBreak is BreakValue.Left or BreakValue.Right ? forcedBreak : null;

            // A float is one picture while it can be: the page ends where a float that asks not to be
            // broken begins, and where one that merely would not fit begins, so it starts the next
            // page whole (CSS 2.1 §13.3.1). A float taller than a page, or a page's own forced break,
            // may cut it. The forced break says where the page ends and is not moved.
            if (forcedBreak == BreakValue.Auto &&
                FloatEdge(collector.Floats, cut, start, contentHeight) is { } floatedEdge)
            {
                cut = floatedEdge;
                resume = floatedEdge;
            }

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

            start = Math.Min(documentHeight, Math.Max(cut, resume));
        }

        return new PagedLayout(first ?? styles.DefaultPage(inheritedMargins), WithMarginBoxes(pages, styles), documentHeight);
    }

    /// <summary>
    /// Hands every page the text its margin boxes carry. The boxes name the page's own number and the
    /// document's count, so they are read once the count is known.
    /// </summary>
    private static List<PageSlice> WithMarginBoxes(List<PageSlice> pages, PageStyleTable styles)
    {
        for (var index = 0; index < pages.Count; index++)
        {
            var page = pages[index];
            var boxes = styles.MarginBoxes(
                page.Geometry, page.PageName, page.Number, pages.Count, page.Number % 2 == 0, page.Blank);

            if (boxes.Count > 0)
            {
                pages[index] = page with { MarginBoxes = boxes };
            }
        }

        return pages;
    }

    /// <summary>Gets whether any flow unit begins at or after <paramref name="y"/>.</summary>
    private static Boolean HasUnitFrom(List<FlowUnit> units, Double y)
    {
        foreach (var unit in units)
        {
            if (unit.Top >= y - 0.001)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Gets where a page ending at <paramref name="cut"/> has to end instead so that it does not cut
    /// through a float that begins on it, or null where no such float lies across the cut. A float
    /// that asks not to be broken is never cut; one that does is cut only when it is taller than a
    /// page, since it could not stand whole on the next one either (CSS 2.1 §13.3.1).
    /// </summary>
    private static Double? FloatEdge(
        IReadOnlyList<(Double Top, Double Bottom, Boolean Avoid)> floats, Double cut, Double start, Double pageHeight)
    {
        Double? edge = null;

        foreach (var (top, bottom, avoid) in floats)
        {
            if (top > start + 0.001 &&
                cut > top + 0.001 &&
                cut < bottom - 0.001 &&
                (avoid || bottom - top <= pageHeight + 0.001))
            {
                edge = edge is { } held ? Math.Min(held, top) : top;
            }
        }

        return edge;
    }

    /// <summary>
    /// Gets the lowest edge the box's picture reaches. A box that clips its overflow stops at its own
    /// edge: what it hides is not content a page has to reach.
    /// </summary>
    private static Double ContentBottom(LayoutBox box)
    {
        var bottom = box.BorderBox.Bottom;

        if (box.Source.Style.ClipsOverflow)
        {
            return bottom;
        }

        foreach (var child in box.Children)
        {
            bottom = Math.Max(bottom, ContentBottom(child));
        }

        return bottom;
    }

    /// <summary>Gets the page name the content at the head of a page states.</summary>
    private static String? NameAt(List<FlowUnit> units, Double start)
    {
        foreach (var unit in units)
        {
            if (unit.Bottom > start + 0.001 ||
                (unit.Top >= start - 0.001 && unit.Bottom >= start - 0.001))
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
    /// <remarks>
    /// A box that asks not to be broken inside and cannot fit the room the page has left begins the
    /// next page instead (CSS Break 3 §4.4): the page ends where the box starts, however much room was
    /// left, and the box is cut only when it cannot fit a page of its own either. That decision is
    /// taken before the breaks the flow allows, because every one of those after it falls inside the
    /// box and would split the very thing the page was holding whole.
    /// </remarks>
    private static Double Cut(
        List<FlowUnit> units, Double start, Double limit, Double end,
        out BreakValue forcedBreak, out Double resume)
    {
        forcedBreak = BreakValue.Auto;
        resume = start;
        (Double Y, Double Resume)? forced = null;
        (Double Y, Double Resume)? pushed = null;
        (Double Y, Double Resume)? allowed = null;
        (Double Y, Double Resume)? avoided = null;
        var linesOnPage = new Dictionary<Int32, Int32>();

        for (var index = 0; index < units.Count; index++)
        {
            var unit = units[index];

            if (index > 0)
            {
                var y = unit.StartBoundarySet ? unit.StartBoundaryY : unit.Top;
                var resumeAt = y;

                // A gap between two flex items is truncated by the break: the page ends at the item
                // before it, and the next page begins at the item after it.
                if (unit.ResumeSet)
                {
                    y = unit.ResumeFrom;
                    resumeAt = unit.ResumeY;
                }

                if (y > start + 0.001 && y <= limit + 0.001)
                {
                    var kind = Boundary(units[index - 1], unit, linesOnPage, out var value);
                    var point = (Y: y, Resume: resumeAt);

                    if (kind == BreakClass.Forced)
                    {
                        forced = point;
                        forcedBreak = value;
                        break;
                    }

                    // The unit begins an avoid-inside box whose far edge lies past the page: the box
                    // does not fit the room that is left, so it is moved rather than split - unless it
                    // stands at the page's own start, where there is no earlier page to move it to.
                    if (pushed is null && unit.StartsAvoidBottom > limit + 0.001)
                    {
                        pushed = point;
                    }

                    if (kind == BreakClass.Allowed)
                    {
                        // A box that begins with nothing reaching this page - only its leading
                        // space, a cleared line standing below a float - is no place to end it:
                        // the page fills with what stands there, and the box's first line starts
                        // the next page. The float that fills the page's foot stays on it.
                        if (!(unit.StartBoundarySet && unit.Top > limit + 0.001))
                        {
                            allowed = point;
                        }
                    }
                    else if (kind == BreakClass.Avoid)
                    {
                        avoided = point;
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
            resume = forcedAt.Resume;
            return Math.Min(forcedAt.Y, end);
        }

        // Everything that is left fits the page: no break opportunity is better than the document's
        // own end.
        if (end <= limit + 0.001)
        {
            resume = end;
            return end;
        }

        if (pushed is { } pushedAt)
        {
            resume = pushedAt.Resume;
            return Math.Min(pushedAt.Y, end);
        }

        if (allowed is { } allowedAt)
        {
            resume = allowedAt.Resume;
            return Math.Min(allowedAt.Y, end);
        }

        if (avoided is { } avoidedAt)
        {
            resume = avoidedAt.Resume;
            return Math.Min(avoidedAt.Y, end);
        }

        resume = Math.Min(limit, end);
        return resume;
    }

    private static BreakClass Boundary(
        FlowUnit previous,
        FlowUnit next,
        Dictionary<Int32, Int32> linesOnPage,
        out BreakValue forcedValue)
    {
        forcedValue = BreakValue.Auto;

        if (!String.Equals(previous.EndNameSet ? previous.EndName : previous.PageName,
                next.StartNameSet ? next.StartName : next.PageName,
                StringComparison.Ordinal))
        {
            forcedValue = BreakValue.Page;
            return BreakClass.Forced;
        }

        var combined = Fragmentainer.Combine(previous.After, next.Before);

        if (combined is BreakValue.Page or BreakValue.Left or BreakValue.Right)
        {
            forcedValue = combined;
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

        /// <summary>
        /// Gets the lowest edge of a box that took no place in the flow - an absolutely positioned
        /// or floating box. It takes no page break of its own, but a page has to reach as far as it
        /// paints, or the picture after the flow would be cut off at the last page's edge.
        /// </summary>
        public Double OutOfFlowBottom { get; private set; }

        /// <summary>Gets the spans the floats take, which no page that has room for one may cut through.</summary>
        public List<(Double Top, Double Bottom, Boolean Avoid)> Floats { get; } = new();

        public void Collect(
            LayoutBox box,
            List<FlowUnit> units,
            BreakValue inheritedBefore,
            BreakValue inheritedAfter,
            Int32[] avoid,
            String? pageName,
            Boolean pageNames = true,
            Boolean ownPageName = true)
        {
            var source = box.Source;

            if (PositionedLayout.IsOutOfFlow(source) || source.Style.Float != FloatSide.None)
            {
                OutOfFlowBottom = Math.Max(OutOfFlowBottom, DeepestBottom(box));

                // A float is drawn whole on one page: the page it begins on ends before it when it
                // would not fit, so the span it takes is what the cut is held away from.
                if (source.Style.Float != FloatSide.None)
                {
                    var kept = source.Style.Breaks.Inside == BreakValue.Avoid;
                    Floats.Add((box.BorderBox.Y, kept ? box.BorderBox.Bottom + box.Margin.Bottom : box.BorderBox.Bottom, kept));
                }

                // A positioned box takes no place in the flow, but the page names written inside it
                // still say which pages its picture is printed on; it joins the page sequence for
                // that name alone. A float and a box with no page name take no page break at all.
                if (!NamesItsPage(box))
                {
                    return;
                }
            }

            var name = pageNames && ownPageName && source.Style.PageName is { } own ? own : pageName;
            var ownBefore = PageValue(source.Style.Breaks.Before);
            var ownAfter = PageValue(source.Style.Breaks.After);

            if (box.Kind == LayoutKind.Table)
            {
                CollectTable(box, units, inheritedBefore, inheritedAfter, avoid, name, pageNames);
                return;
            }

            if (source.Kind == BoxKind.Flex)
            {
                CollectFlex(
                    box, units, inheritedBefore, inheritedAfter, avoid,
                    FlexPageName(box, name, pageNames));
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

            // A float takes no place in this flow - it is not a break opportunity - but the span it
            // covers is one no page that has room for the whole of it may end inside. A float that
            // asks not to be broken is placed with its margin box whole; one that does not only has
            // to fit its own box, and its margin may run over the page's edge (CSS 2.1 §9.5.1).
            foreach (var child in box.Children)
            {
                if (!PositionedLayout.IsOutOfFlow(child.Source) &&
                    child.Source.Style.Float != FloatSide.None)
                {
                    var kept = child.Source.Style.Breaks.Inside == BreakValue.Avoid;
                    var bottom = kept
                        ? child.BorderBox.Bottom + child.Margin.Bottom
                        : child.BorderBox.Bottom;

                    Floats.Add((child.BorderBox.Y, bottom, kept));
                }
            }

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

                Collect(child, units, childBefore, childAfter, inside, name, pageNames);
                index++;
            }

            // Nothing in flow: the box itself is the one piece its break values stand on.
            if (units.Count == first)
            {
                units.Add(BoxUnit(box, before, after, avoid, name));
            }
            else
            {
                // A break before this box falls at its own border edge, not between its padding and
                // its first child: the box's edges and padding belong to the fragment that begins it.
                units[first].StartBoundaryY = box.BorderBox.Y;
                units[first].StartBoundarySet = true;

                if (inside.Length != avoid.Length)
                {
                    MarkAvoidStart(units, first, box);
                }
            }
        }

        /// <summary>
        /// Notes on the unit that begins a box how far the box asks not to be broken inside. A page
        /// that cannot hold the whole of it moves the box to the next one instead of splitting it.
        /// </summary>
        private static void MarkAvoidStart(List<FlowUnit> units, Int32 first, LayoutBox box)
        {
            if (units.Count > first)
            {
                units[first].StartsAvoidBottom =
                    Math.Max(units[first].StartsAvoidBottom, ContentBottom(box));
            }
        }

        /// <summary>
        /// Collects a flex container as the pieces a page may end between: a row container breaks
        /// between its flex lines, and a column container between its items (CSS Flexbox 1 §9.3).
        /// A single-line row is one piece, because a page cannot end beside the items of the line.
        /// </summary>
        private void CollectFlex(
            LayoutBox flex,
            List<FlowUnit> units,
            BreakValue inheritedBefore,
            BreakValue inheritedAfter,
            Int32[] avoid,
            String? name)
        {
            var source = flex.Source;
            var before = Fragmentainer.Combine(inheritedBefore, PageValue(source.Style.Breaks.Before));
            var after = Fragmentainer.Combine(inheritedAfter, PageValue(source.Style.Breaks.After));
            var inside = source.Style.Breaks.Inside == BreakValue.Avoid
                ? Append(avoid, ++_nextAvoid)
                : avoid;

            var items = new List<LayoutBox>(flex.Children.Count);

            foreach (var child in flex.Children)
            {
                if (!PositionedLayout.IsOutOfFlow(child.Source) &&
                    child.Source.Style.Float == FloatSide.None)
                {
                    items.Add(child);
                }
            }

            var first = units.Count;

            if (source.Style.Flex.IsRow)
            {
                var lines = FlexLines(items);

                for (var index = 0; index < lines.Count; index++)
                {
                    var line = lines[index];

                    if (line.Count == 1)
                    {
                        // A line of one item is the item: its own content is what continues down the
                        // page, so a break written inside it is a break of the line.
                        Collect(
                            line[0],
                            units,
                            index == 0 ? before : BreakValue.Auto,
                            index == lines.Count - 1 ? after : BreakValue.Auto,
                            inside,
                            name,
                            pageNames: true,
                            ownPageName: false);
                        continue;
                    }

                    var top = Double.MaxValue;
                    var bottom = Double.MinValue;
                    var lineBefore = BreakValue.Auto;
                    var lineAfter = BreakValue.Auto;

                    foreach (var item in line)
                    {
                        top = Math.Min(top, item.BorderBox.Y);
                        bottom = Math.Max(bottom, item.BorderBox.Bottom);

                        // A break written on an item is a break of the line it stands on: a row's
                        // items sit beside each other, and the page can only end between lines.
                        lineBefore = Fragmentainer.Combine(
                            lineBefore, PageValue(item.Source.Style.Breaks.Before));
                        lineAfter = Fragmentainer.Combine(
                            lineAfter, PageValue(item.Source.Style.Breaks.After));
                    }

                    units.Add(new FlowUnit
                    {
                        Top = top,
                        Bottom = bottom,
                        Before = Fragmentainer.Combine(index == 0 ? before : BreakValue.Auto, lineBefore),
                        After = Fragmentainer.Combine(
                            index == lines.Count - 1 ? after : BreakValue.Auto, lineAfter),
                        PageName = name,
                        Avoid = inside,
                    });
                }
            }
            else
            {
                var ordered = items
                    .OrderBy(item => item.BorderBox.X)
                    .ThenBy(item => item.BorderBox.Y)
                    .ToList();

                var previousBottom = Double.NegativeInfinity;

                for (var index = 0; index < ordered.Count; index++)
                {
                    var item = ordered[index];
                    var itemFirst = units.Count;

                    Collect(
                        item,
                        units,
                        index == 0 ? before : BreakValue.Auto,
                        index == ordered.Count - 1 ? after : BreakValue.Auto,
                        inside,
                        name,
                        pageNames: true,
                        ownPageName: false);

                    // The row-gap and the margin between two items are truncated by a page break: the
                    // page ends at the item before the gap and the next one starts at its own edge.
                    if (units.Count > itemFirst &&
                        previousBottom > Double.NegativeInfinity &&
                        item.BorderBox.Y > previousBottom + 0.001)
                    {
                        units[itemFirst].ResumeSet = true;
                        units[itemFirst].ResumeFrom = previousBottom;
                        units[itemFirst].ResumeY = item.BorderBox.Y;
                    }

                    previousBottom = item.BorderBox.Bottom;
                }
            }

            if (units.Count == first)
            {
                units.Add(BoxUnit(flex, before, after, avoid, name));
            }

            // The container answers for its own edges: what one of its items calls a page is a name
            // inside the container, and the box after it stands on the container's page.
            units[first].StartName = name;
            units[first].StartNameSet = true;
            units[first].StartBoundaryY = flex.BorderBox.Y;
            units[first].StartBoundarySet = true;

            if (inside.Length != avoid.Length)
            {
                MarkAvoidStart(units, first, flex);
            }
            units[^1].EndName = name;
            units[^1].EndNameSet = true;
        }

        /// <summary>
        /// Gets the page name a flex container takes from its items: the name they all share, or the
        /// name the container stands in when they do not agree. A page name written deeper inside an
        /// item does not reach through the container - the flex container answers for its items alone
        /// (CSS Page 3 §using-named-pages).
        /// </summary>
        private static String? FlexPageName(LayoutBox flex, String? inherited, Boolean pageNames)
        {
            if (!pageNames || flex.Source.Style.PageName is not null)
            {
                return inherited;
            }

            String? common = null;

            foreach (var item in flex.Children)
            {
                if (PositionedLayout.IsOutOfFlow(item.Source) ||
                    item.Source.Style.Float != FloatSide.None)
                {
                    continue;
                }

                var itemName = item.Source.Style.PageName;

                if (itemName is null)
                {
                    return inherited;
                }

                if (common is null)
                {
                    common = itemName;
                }
                else if (!String.Equals(common, itemName, StringComparison.Ordinal))
                {
                    return inherited;
                }
            }

            return common ?? inherited;
        }

        /// <summary>
        /// Groups flex items into the lines their boxes stand on. The items of one line overlap down
        /// the page; an item whose top is below every box of the line before it begins the next.
        /// </summary>
        private static List<List<LayoutBox>> FlexLines(List<LayoutBox> items)
        {
            var lines = new List<List<LayoutBox>>();
            var bottom = Double.MinValue;

            foreach (var item in items.OrderBy(item => item.BorderBox.Y).ThenBy(item => item.BorderBox.X))
            {
                if (lines.Count == 0 || item.BorderBox.Y >= bottom - 0.5)
                {
                    lines.Add(new List<LayoutBox>());
                    bottom = item.BorderBox.Bottom;
                }
                else
                {
                    bottom = Math.Max(bottom, item.BorderBox.Bottom);
                }

                lines[^1].Add(item);
            }

            return lines;
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
            String? pageName,
            Boolean pageNames)
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
                    CollectRowGroup(child, units, childBefore, childAfter, inside, name, pageNames);
                }
                else if (child.Kind == LayoutKind.TableRow)
                {
                    AddRow(child, units, childBefore, childAfter, inside, name);
                }
                else
                {
                    Collect(child, units, childBefore, childAfter, inside, name, pageNames);

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
            else
            {
                units[first].StartBoundaryY = table.BorderBox.Y;
                units[first].StartBoundarySet = true;

                if (inside.Length != avoid.Length)
                {
                    MarkAvoidStart(units, first, table);
                }
            }
        }

        private void CollectRowGroup(
            LayoutBox group,
            List<FlowUnit> units,
            BreakValue inheritedBefore,
            BreakValue inheritedAfter,
            Int32[] avoid,
            String? pageName,
            Boolean pageNames)
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
            else if (inside.Length != avoid.Length)
            {
                MarkAvoidStart(units, first, group);
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

                // A line that holds only a positioned box is no line on the page: the box it holds
                // is collected below, and a zero-height run of no text is not a break opportunity.
                if (HasFlowContent(line) || line.BorderBox.Height > 0.001)
                {
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

                foreach (var child in line.Children)
                {
                    if (PositionedLayout.IsOutOfFlow(child.Source) &&
                        child.Source.Style.Position.IsAbsolute &&
                        NamesPage(child))
                    {
                        Collect(child, units, BreakValue.Auto, BreakValue.Auto, avoid, pageName);
                    }
                }
            }
        }

        private static Boolean HasFlowContent(LayoutBox line)
        {
            foreach (var child in line.Children)
            {
                if (!PositionedLayout.IsOutOfFlow(child.Source) &&
                    child.Source.Style.Float == FloatSide.None)
                {
                    return true;
                }
            }

            return false;
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

        private static Boolean NamesPage(LayoutBox box)
        {
            if (box.Source.Style.PageName is not null)
            {
                return true;
            }

            foreach (var child in box.Children)
            {
                if (NamesPage(child))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Gets whether an absolutely positioned box carries page names into the page sequence. Only a
        /// box left at its static place does: one placed by its own offsets is a picture on the page it
        /// was hung from, and the names inside it do not move it or the pages around it.
        /// </summary>
        private static Boolean NamesItsPage(LayoutBox box)
        {
            var position = box.Source.Style.Position;

            return position.IsAbsolute &&
                position.Top.IsAuto &&
                position.Right.IsAuto &&
                position.Bottom.IsAuto &&
                position.Left.IsAuto &&
                NamesPage(box);
        }

        private static Double DeepestBottom(LayoutBox box)
        {
            var bottom = box.BorderBox.Bottom;

            foreach (var child in box.Children)
            {
                bottom = Math.Max(bottom, DeepestBottom(child));
            }

            return bottom;
        }

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
                    child.Source.Style.Float != FloatSide.None ||
                    (PositionedLayout.IsOutOfFlow(child.Source) && !NamesItsPage(child)))
                {
                    continue;
                }

                flow.Add(child);
            }

            return flow;
        }

        /// <summary>
        /// Gets whether the box is one piece, so a page cannot end inside it. A replaced box, a table,
        /// a flex or grid container, a multi-column container and a scroll container are all laid as
        /// one picture by the engine; a vertical flow is left whole until paged columns are
        /// implemented.
        /// </summary>
        /// <remarks>
        /// A box that hides its overflow is not among them: clipping says what a fragment shows, not
        /// that the box has only one fragment. Only a box the reader can scroll is one picture (CSS
        /// Break 3 §3.1), because the content the scrollbar reaches is not something a page can carry
        /// on.
        /// </remarks>
        private static Boolean IsMonolithic(LayoutBox box)
        {
            if (box.Kind is LayoutKind.Image or LayoutKind.Svg or LayoutKind.HorizontalRule or
                LayoutKind.TableRowGroup or LayoutKind.TableRow or LayoutKind.TableCell)
            {
                return true;
            }

            var source = box.Source;

            return source.IsReplaced ||
                source.Kind is BoxKind.Grid or BoxKind.Masonry or BoxKind.InlineBlock ||
                source.Style.Multi.IsMultiColumn ||
                Scrolls(source.Style) ||
                source.Style.Contain.Block ||
                source.Flow.IsVertical;
        }

        /// <summary>
        /// Gets whether an axis of the box is one the reader can scroll, which is what makes the box
        /// one picture no page can carry on (CSS Break 3 §3.1). <c>hidden</c> hides what it holds but
        /// cannot be scrolled to, so it fragments like any other box.
        /// </summary>
        private static Boolean Scrolls(ComputedStyle style) =>
            IsScrollable(style.OverflowX) || IsScrollable(style.OverflowY);

        private static Boolean IsScrollable(String value) =>
            value.Equals("auto", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("scroll", StringComparison.OrdinalIgnoreCase);
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

        /// <summary>
        /// Gets the name the boundary at the box's start sees, where it differs from the name of the
        /// page its own content stands on. A flex container whose own name is auto begins on the
        /// page it stands on even when its first item names a page; the name is what the item's own
        /// page is called, not a page break written at the container's edge.
        /// </summary>
        public String? StartName { get; set; }

        public Boolean StartNameSet { get; set; }

        /// <summary>Gets the document edge a break before this unit falls at, when the unit begins a
        /// box whose padding stands between that edge and the unit's own top.</summary>
        public Double StartBoundaryY { get; set; }

        public Boolean StartBoundarySet { get; set; }

        /// <summary>
        /// Gets the far edge of the box this unit begins where the box asks not to be broken inside,
        /// or zero where it begins none. A page that cannot hold the whole box moves it instead of
        /// breaking inside, so this edge is what tells a move from a split.
        /// </summary>
        public Double StartsAvoidBottom { get; set; }

        /// <summary>Gets whether a gap stands before this unit, which a page break truncates.</summary>
        public Boolean ResumeSet { get; set; }

        public Double ResumeFrom { get; set; }

        public Double ResumeY { get; set; }

        /// <summary>Gets the name the boundary at the box's end sees, where it differs from
        /// <see cref="PageName"/>, by the same rule at the other edge.</summary>
        public String? EndName { get; set; }

        public Boolean EndNameSet { get; set; }

        public Int32 Run { get; init; } = -1;

        public Int32 LineIndex { get; init; }

        public Int32 LineTotal { get; init; }

        public Int32 Orphans { get; init; } = 1;

        public Int32 Widows { get; init; } = 1;

        public Boolean IsLine => Run >= 0;
    }
}

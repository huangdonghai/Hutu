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
    /// The axis a document's pages follow: the block axis of its root, which runs down the page in a
    /// horizontal flow and across it in a vertical one (CSS Writing Modes 3 §6.1). Every position here
    /// is the block-axis distance from where the flow begins, so a page is always the band between two
    /// of them - whichever physical edge that band touches.
    /// </summary>
    private readonly struct PageAxis
    {
        private readonly Boolean _vertical;
        private readonly Boolean _reversed;
        private readonly Double _inline;

        private PageAxis(Boolean vertical, Boolean reversed, Double inline)
        {
            _vertical = vertical;
            _reversed = reversed;
            _inline = inline;
        }

        public static PageAxis Of(WritingModeInfo flow, Double layoutInline) =>
            flow.IsVertical
                ? new PageAxis(
                    true,
                    flow.Mode is WritingMode.VerticalRl or WritingMode.SidewaysRl,
                    layoutInline)
                : default;

        public Boolean IsVertical => _vertical;

        /// <summary>Gets where the box begins along the axis.</summary>
        public Double Start(LayoutBox box) => _vertical
            ? (_reversed ? _inline - box.BorderBox.Right : box.BorderBox.X)
            : box.BorderBox.Y;

        /// <summary>Gets where the box ends along the axis.</summary>
        public Double End(LayoutBox box) => _vertical
            ? (_reversed ? _inline - box.BorderBox.X : box.BorderBox.Right)
            : box.BorderBox.Bottom;

        public Double Start(Rect rect) => _vertical
            ? (_reversed ? _inline - (rect.X + rect.Width) : rect.X)
            : rect.Y;

        public Double End(Rect rect) => _vertical
            ? (_reversed ? _inline - rect.X : rect.X + rect.Width)
            : rect.Y + rect.Height;

        /// <summary>Gets the block extent of the page area: how much of the axis one page covers.</summary>
        public Double PageExtent(PageGeometry page) => _vertical ? page.ContentWidth : page.ContentHeight;

        /// <summary>Gets the document rectangle the page between two positions shows.</summary>
        public Rect Band(Double from, Double to, PageGeometry page) => _vertical
            ? new Rect(_reversed ? _inline - to : from, 0, Math.Max(0, to - from), page.ContentHeight)
            : new Rect(0, from, page.ContentWidth, Math.Max(0, to - from));

        /// <summary>Gets the margin a box keeps at the end of the axis.</summary>
        public Double EndMargin(Thickness margin) =>
            _vertical ? (_reversed ? margin.Left : margin.Right) : margin.Bottom;
    }

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
        // A vertical document's pages follow its block axis, which runs across the sheet rather than
        // down it; the same cut then serves both, over positions read along that axis.
        var axis = PageAxis.Of(layout.Root.Flow, layout.Width);
        var units = new List<FlowUnit>();
        var collector = new Collector(axis);
        collector.Collect(
            layout.Root, units, BreakValue.Auto, BreakValue.Auto, Array.Empty<Int32>(), null);
        collector.ClampClipped(layout.Root, units, 0, 0);

        var documentExtent = collector.OutOfFlowBottom;

        foreach (var unit in units)
        {
            documentExtent = Math.Max(documentExtent, unit.Bottom);
        }

        // A box's own extent may reach past the last line or child that gave it its flow, and a
        // positioned box that takes no page break still paints. The root is left out: its box is
        // the window the document was measured in, which is not content.
        foreach (var child in layout.Root.Children)
        {
            documentExtent = Math.Max(documentExtent, ContentBottom(child, axis));
        }

        // A document with no flow unit at all - an absolutely positioned picture, a root that states
        // only a height - still has the size the layout gave it. The units are the flow's own end,
        // because the layout's size includes the viewport the document was measured in.
        if (units.Count == 0 && documentExtent <= 0)
        {
            documentExtent = Math.Max(0, axis.IsVertical ? layout.Width : layout.Height);
        }

        var pages = new List<PageSlice>();

        if (documentExtent <= 0.001)
        {
            var empty = styles.DefaultPage(inheritedMargins);
            pages.Add(new PageSlice(1, empty, axis.Band(0, 0, empty), null));
            return new PagedLayout(empty, WithMarginBoxes(pages, styles), documentExtent);
        }

        var start = 0.0;
        var number = 1;
        PageGeometry? first = null;
        var tail = false;
        BreakValue? pendingSide = null;

        while (start < documentExtent - 0.001 || tail)
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
                    number++, blank, axis.Band(start, start, blank), name, Blank: true));
                isLeft = !isLeft;
            }

            pendingSide = null;

            var geometry = styles.Page(name, number, isLeft, blank: false, inheritedMargins);

            // A forced break may leave nothing behind it but a zero-height box. That box is still on
            // the next page, and the page it is on is printed even though it has no height.
            if (start >= documentExtent - 0.001)
            {
                first ??= geometry;
                pages.Add(new PageSlice(
                    number, geometry, axis.Band(start, start, geometry), name));
                break;
            }

            // A fragment of a table below the first one carries the table's header again at its
            // top, and its footer again at the bottom; the room both take is room the fragment's
            // own content does not have (CSS Break 3 §5.2). A vertical document's tables are left
            // whole for now.
            var header = HeaderAt(collector.Headers, axis, start, geometry);
            var cloneLead = CloneEdgeAt(collector.Clones, units, start, top: true);
            var lead = (header is { } band ? axis.End(band) - axis.Start(band) : 0.0) +
                (cloneLead?.Height ?? 0.0);

            var pageExtent = Math.Max(1, axis.PageExtent(geometry));
            var footer = FooterAt(
                collector.Footers, axis, start, start + Math.Max(1, pageExtent - lead), lead, pageExtent, geometry);
            var trail = footer is { } foot ? axis.End(foot) - axis.Start(foot) : 0.0;

            var contentExtent = Math.Max(1, pageExtent - lead - trail);
            var limit = Math.Min(documentExtent, start + contentExtent);
            var cut = Cut(units, start, limit, documentExtent, out var forcedBreak, out var resume);
            tail = forcedBreak is BreakValue.Page or BreakValue.Left or BreakValue.Right &&
                HasUnitFrom(units, cut);
            pendingSide = forcedBreak is BreakValue.Left or BreakValue.Right ? forcedBreak : null;

            // A float is one picture while it can be: the page ends where a float that asks not to be
            // broken begins, and where one that merely would not fit begins, so it starts the next
            // page whole (CSS 2.1 §13.3.1). A float taller than a page, or a page's own forced break,
            // may cut it. The forced break says where the page ends and is not moved.
            if (forcedBreak == BreakValue.Auto &&
                FloatEdge(collector.Floats, cut, start, contentExtent) is { } floatedEdge)
            {
                cut = floatedEdge;
                resume = floatedEdge;
            }

            if (cut <= start + 0.001)
            {
                cut = Math.Min(documentExtent, Math.Max(limit, start + 0.001));
            }

            if (cut <= start)
            {
                cut = documentExtent;
            }

            // A box that repeats its decoration gives up its block-end edge to the page it is cut
            // in: the page ends that much short and draws the edge there, and the page after begins
            // with the box’s block-start edge (CSS Break 3 §5.4).
            var cloneTrail = CloneEdgeAt(collector.Clones, units, cut, top: false);

            if (cloneTrail is { } cutEdge)
            {
                trail += cutEdge.Height;

                var refined = Math.Min(documentExtent, start + Math.Max(1, pageExtent - lead - trail));
                var again = Cut(units, start, refined, documentExtent, out _, out var resumed);

                if (again > start + 0.001)
                {
                    cut = again;
                    resume = resumed;
                }
            }

            first ??= geometry;

            var repeats = new List<PageRepeat>(2);

            if (header is { } repeated)
            {
                repeats.Add(new PageRepeat(
                    repeated,
                    new Rect(geometry.Margins.Left + repeated.X, geometry.Margins.Top, repeated.Width, repeated.Height)));
            }

            if (footer is { } repeatedFoot)
            {
                repeats.Add(new PageRepeat(
                    repeatedFoot,
                    new Rect(
                        geometry.Margins.Left + repeatedFoot.X,
                        geometry.Margins.Top + geometry.ContentHeight - repeatedFoot.Height,
                        repeatedFoot.Width,
                        repeatedFoot.Height)));
            }

            if (cloneLead is { } leadEdge)
            {
                repeats.Add(new PageRepeat(
                    leadEdge.Strip,
                    new Rect(
                        geometry.Margins.Left + leadEdge.Strip.X,
                        geometry.Margins.Top,
                        leadEdge.Strip.Width,
                        leadEdge.Strip.Height)));
            }

            if (cloneTrail is { } trailEdge)
            {
                repeats.Add(new PageRepeat(
                    trailEdge.Strip,
                    new Rect(
                        geometry.Margins.Left + trailEdge.Strip.X,
                        geometry.Margins.Top + geometry.ContentHeight - trailEdge.Strip.Height,
                        trailEdge.Strip.Width,
                        trailEdge.Strip.Height)));
            }

            pages.Add(new PageSlice(
                number++, geometry, axis.Band(start, cut, geometry), name,
                Repeats: repeats.Count > 0 ? repeats : null, Lead: lead, Trail: trail));

            start = Math.Min(documentExtent, Math.Max(cut, resume));
        }

        return new PagedLayout(first ?? styles.DefaultPage(inheritedMargins), WithMarginBoxes(pages, styles), documentExtent);
    }

    /// <summary>
    /// Gets the header band a page beginning at <paramref name="start"/> repeats, or null where the
    /// page begins no fragment that needs one: the first fragment of a table carries its header in
    /// the flow, and a page outside the table carries none at all.
    /// </summary>
    /// <summary>
    /// Gets the edge a page carries where it begins or ends inside a box that repeats its
    /// decoration, with the strip that edge is drawn from: a box’s first fragment runs to the first
    /// break inside it whatever the page edges say, and every fragment after that ends at the page
    /// edge it reaches (CSS Break 3 §5.4).
    /// </summary>
    private static (Double Height, Rect Strip)? CloneEdgeAt(
        List<(Rect Box, Double Top, Double Bottom, Rect TopStrip, Rect BottomStrip)> boxes,
        List<FlowUnit> units,
        Double at,
        Boolean top)
    {
        foreach (var box in boxes)
        {
            if (at <= box.Box.Y + 0.001 || at >= box.Box.Bottom - 0.001 ||
                at < FirstBreakOf(box.Box, units) - 0.001)
            {
                continue;
            }

            var height = top ? box.Top : box.Bottom;
            var strip = top ? box.TopStrip : box.BottomStrip;

            if (height > 0.001)
            {
                return (height, strip);
            }
        }

        return null;
    }

    /// <summary>
    /// Gets where the first fragment of a repeating box ends: the second piece of flow inside it, or
    /// its own block-end where it holds one piece only - a box of one monolithic child has no break
    /// inside it, and its one fragment runs on past every page edge.
    /// </summary>
    private static Double FirstBreakOf(Rect box, List<FlowUnit> units)
    {
        var seen = 0;

        foreach (var unit in units)
        {
            if (unit.Top <= box.Y + 0.001 || unit.Top >= box.Bottom - 0.001)
            {
                continue;
            }

            if (++seen == 2)
            {
                return unit.Top;
            }
        }

        return box.Bottom;
    }

    private static Rect? HeaderAt(
        IReadOnlyList<(Rect Table, Rect Header, Double Rows, Double LastRow)> headers,
        PageAxis axis,
        Double start,
        PageGeometry page)
    {
        if (axis.IsVertical)
        {
            return null;
        }

        Rect? found = null;

        foreach (var (table, header, rows, lastRow) in headers)
        {
            if (start > rows + 0.001 &&
                start <= lastRow + 0.001 &&
                start < table.Bottom - 0.001 &&
                header.Height > 0.001 &&
                header.Height < page.ContentHeight - 1)
            {
                found = header;
            }
        }

        return found;
    }

    /// <summary>
    /// Gets the footer band a page beginning at <paramref name="start"/> keeps at its bottom, or null
    /// where the table does not continue past this page: a footer stands again at the bottom of each
    /// fragment the table goes on in, and the fragment the table ends on carries it in its flow.
    /// </summary>
    private static Rect? FooterAt(
        IReadOnlyList<(Rect Table, Rect Footer, Double Rows, Double LastRow)> footers,
        PageAxis axis,
        Double start,
        Double provisional,
        Double lead,
        Double pageExtent,
        PageGeometry page)
    {
        if (axis.IsVertical)
        {
            return null;
        }

        Rect? found = null;

        foreach (var (table, footer, rows, lastRow) in footers)
        {
            if (start > rows + 0.001 &&
                start <= lastRow + 0.001 &&
                start < table.Bottom - 0.001 &&
                provisional < table.Bottom - 0.001 &&
                footer.Height > 0.001 &&
                lead + footer.Height < pageExtent - 1)
            {
                found = footer;
            }
        }

        return found;
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
    /// Gets the furthest edge the box's picture reaches along the page axis. A box that clips its
    /// overflow stops at its own edge: what it hides is not content a page has to reach.
    /// </summary>
    private static Double ContentBottom(LayoutBox box, PageAxis axis)
    {
        var bottom = axis.End(box);

        if (box.Source.Style.ClipsOverflow)
        {
            return bottom;
        }

        foreach (var child in box.Children)
        {
            bottom = Math.Max(bottom, ContentBottom(child, axis));
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
    /// <para>
    /// A break is where the flow *cannot* go on, not merely where it may: a box the page has already
    /// begun, or which begins at the break itself, and which no page could hold whole, runs on past
    /// every break the flow offers, so the page is filled to its own edge and the box is cut there
    /// (CSS Fragmentation 3 §4.3). A box that begins at the break and asks not to be broken is the
    /// exception the rule above makes: that one is moved whole even when it is too tall to arrive
    /// anywhere.
    /// </para>
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

        // The room a page of this size has for content, and the boxes standing on this page that are
        // taller than any page can hold.
        var room = limit - start;
        List<(Double Top, Double Bottom)>? tooTall = null;

        for (var index = 0; index < units.Count; index++)
        {
            var unit = units[index];

            // A box that runs past this page's edge and is taller than the page itself cannot be
            // carried whole to the next one; it is the reason a page is filled rather than ended
            // early - but only where it already stands on this page, since a box that begins at the
            // break is a box the page can still move (CSS Fragmentation 3 §4.3).
            if (unit.Bottom > limit + 0.001 && unit.Bottom - unit.Top > room + 0.001)
            {
                (tooTall ??= new List<(Double, Double)>()).Add((unit.Top, unit.Bottom));
            }

            if (index > 0)
            {
                var y = unit.StartBoundarySet ? unit.StartBoundaryY : unit.Top;
                var resumeAt = y;

                // A gap between two flex items may be ended anywhere inside it: a page whose edge
                // falls in the gap ends at that edge, and the page after begins there too. A page
                // that ends exactly where the gap begins leaves the whole gap behind, and the page
                // after begins at the item beyond it. A gap that begins past the page's own edge
                // is no break this page can take.
                if (unit.ResumeSet)
                {
                    if (unit.ResumeFrom > limit + 0.001)
                    {
                        y = unit.ResumeFrom;
                        resumeAt = y;
                    }
                    else
                    {
                        y = Math.Min(limit, unit.ResumeY);
                        resumeAt = unit.ResumeFrom >= limit - 0.001 ? unit.ResumeY : y;
                    }
                }

                if (y > start + 0.001 && y <= limit + 0.001 && !unit.SealedBefore)
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
                        // the next page. An empty box is no place to end a page either, and for the
                        // same reason: nothing stands there to be carried over.
                        var hollow = unit.Shell ||
                            unit.Bottom <= y + 0.001 ||
                            (unit.StartBoundarySet && unit.Top > limit + 0.001);

                        if (!hollow)
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

        // The content reaches the page's own edge: the page is full, and the flow ends where the
        // content does - a fieldset's bottom border belongs to the page after, since the box is cut
        // there and its border is drawn at the box's true end (CSS Fragmentation 3 §5.4).
        foreach (var unit in units)
        {
            // Only where the content that reaches the edge is the last of its run: a line with more
            // lines behind it is one the widows rule may still move back, and a flex gap is ended
            // by the gap rather than by the page (CSS Break 3 §3.3, §4.4).
            if (unit.Top < limit - 0.001 && Math.Abs(unit.Bottom - limit) <= 0.5 &&
                !unit.ResumeSet && (!unit.IsLine || unit.LineIndex == unit.LineTotal - 1))
            {
                resume = limit;
                return Math.Min(limit, end);
            }
        }

        if (allowed is { } allowedAt)
        {
            // The break the flow allows stands inside a box that no page can hold whole and that this
            // page has already begun: the box runs on past the break to the page's own edge, and there
            // is nothing to end the page on but that edge.
            if (FillsThePage(tooTall, allowedAt.Y))
            {
                resume = limit;
                return Math.Min(limit, end);
            }

            resume = allowedAt.Resume;
            return Math.Min(allowedAt.Y, end);
        }

        if (avoided is { } avoidedAt)
        {
            if (FillsThePage(tooTall, avoidedAt.Y))
            {
                resume = limit;
                return Math.Min(limit, end);
            }

            resume = avoidedAt.Resume;
            return Math.Min(avoidedAt.Y, end);
        }

        resume = Math.Min(limit, end);
        return resume;
    }

    /// <summary>
    /// Gets whether one of the boxes too tall for a page of its own stands at the break already, so
    /// that the room after the break is that box rather than empty space. A box that begins *at* the
    /// break is the one the page could still move whole, and the page that asks not to be broken inside
    /// is moved by the rule above this one; a box too tall for any page and already begun has nowhere
    /// to be moved to.
    /// </summary>
    private static Boolean FillsThePage(List<(Double Top, Double Bottom)>? tooTall, Double at)
    {
        if (tooTall is null)
        {
            return false;
        }

        foreach (var span in tooTall)
        {
            if (span.Top <= at + 0.001)
            {
                return true;
            }
        }

        return false;
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
        private readonly PageAxis _axis;
        private Int32 _nextRun;
        private Int32 _nextAvoid;

        public Collector(PageAxis axis) => _axis = axis;

        /// <summary>
        /// Gets the lowest edge of a box that took no place in the flow - an absolutely positioned
        /// or floating box. It takes no page break of its own, but a page has to reach as far as it
        /// paints, or the picture after the flow would be cut off at the last page's edge.
        /// </summary>
        public Double OutOfFlowBottom { get; private set; }

        /// <summary>Gets the spans the floats take, which no page that has room for one may cut through.</summary>
        public List<(Double Top, Double Bottom, Boolean Avoid)> Floats { get; } = new();

        /// <summary>
        /// Gets the boxes that repeat their decoration at every fragment edge, with the room their
        /// block-start and block-end edges take, the strips both are drawn from, and the first point
        /// inside the box the flow can break at - its first fragment runs to there whatever the page
        /// edges say, and every fragment after it ends at the page edge it reaches (CSS Break 3 §5.4).
        /// </summary>
        public List<(Rect Box, Double Top, Double Bottom, Rect TopStrip, Rect BottomStrip)> Clones { get; } = new();

        /// <summary>
        /// Gets the tables that carry a header, with the band it takes, where the table's own rows
        /// begin, and where the last of them begins. A fragment carries the header again at its top
        /// when the table's rows have begun on an earlier page and a row of them begins on this one
        /// (CSS Break 3 §5.2).
        /// </summary>
        public List<(Rect Table, Rect Header, Double Rows, Double LastRow)> Headers { get; } = new();

        /// <summary>
        /// Gets the tables that carry a footer, with the band it takes. A fragment of the table below
        /// the first keeps the footer standing at its bottom, and the footer's rows are one piece a
        /// page cannot end inside.
        /// </summary>
        public List<(Rect Table, Rect Footer, Double Rows, Double LastRow)> Footers { get; } = new();

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
                    var end = _axis.End(box) + (kept ? _axis.EndMargin(box.Margin) : 0);
                    Floats.Add((_axis.Start(box), end, kept));
                }

                // A positioned box takes no place in the flow, but the page names written inside it
                // still say which pages its picture is printed on; it joins the page sequence for
                // that name alone. A float and a box with no page name take no page break at all.
                if (!NamesItsPage(box))
                {
                    return;
                }
            }

            if (!_axis.IsVertical && source.Style.ClonesDecoration)
            {
                var edgeTop = box.Border.Top + box.Padding.Top;
                var edgeBottom = box.Border.Bottom + box.Padding.Bottom;

                if (edgeTop > 0.001 || edgeBottom > 0.001)
                {
                    Clones.Add((
                        box.BorderBox,
                        edgeTop,
                        edgeBottom,
                        new Rect(box.BorderBox.X, box.BorderBox.Y, box.BorderBox.Width, edgeTop),
                        new Rect(
                            box.BorderBox.X,
                            box.BorderBox.Bottom - edgeBottom,
                            box.BorderBox.Width,
                            edgeBottom)));
                }
            }

            var name = pageNames && ownPageName && source.Flow.IsVertical == _axis.IsVertical &&
                source.Style.PageName is { } own
                    ? own
                    : pageName;
            // A fieldset's legend stands in the border rather than in the flow, so a break asked for
            // before it is asked for before nothing and is dropped; one asked for after it still
            // separates it from the content that follows (HTML rendering §15.3.12).
            var ownBefore = box.Source.IsLegend ? BreakValue.Auto : PageValue(source.Style.Breaks.Before);
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

            if (source.Kind is BoxKind.Grid or BoxKind.Masonry)
            {
                CollectGrid(
                    box, units, inheritedBefore, inheritedAfter, avoid,
                    FlexPageName(box, name, pageNames));
                return;
            }

            // A multi-column container that a spanner splits breaks between its sections: the page
            // ends before a spanner that does not fit and carries it whole, and each column block on
            // either side of it is a piece of the flow (CSS Multicol 1 §6).
            if (source.Style.Multi.IsMultiColumn && Spans(box))
            {
                CollectMulticolSections(box, units, inheritedBefore, inheritedAfter, avoid, name);
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
                    var end = _axis.End(child) + (kept ? _axis.EndMargin(child.Margin) : 0);

                    Floats.Add((_axis.Start(child), end, kept));
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

                var pieceUnit = units.Count;
                var pieceFloat = Floats.Count;
                Collect(child, units, childBefore, childAfter, inside, name, pageNames);
                ClampClipped(child, units, pieceUnit, pieceFloat);
                index++;
            }

            // Nothing in flow: the box itself is the one piece its break values stand on - unless
            // it holds nothing but floats, in which case the page would be ending before the room
            // those floats take rather than before anything that could be carried over.
            if (units.Count == first)
            {
                var unit = BoxUnit(box, before, after, avoid, name);
                unit.Shell = flow.Count == 0 && box.Children.Count > 0;

                if (box.Source.IsLegend)
                {
                    unit.SealedBefore = true;
                }

                units.Add(unit);
            }
            else
            {
                // A break before this box falls at its own border edge, not between its padding and
                // its first child: the box's edges and padding belong to the fragment that begins it.
                units[first].StartBoundaryY = _axis.Start(box);
                units[first].StartBoundarySet = true;

                if (box.Source.IsLegend)
                {
                    units[first].SealedBefore = true;
                }

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
        private void MarkAvoidStart(List<FlowUnit> units, Int32 first, LayoutBox box)
        {
            if (units.Count > first)
            {
                units[first].StartsAvoidBottom =
                    Math.Max(units[first].StartsAvoidBottom, _axis.End(box));
            }
        }

        /// <summary>
        /// Gets whether one of the container's in-flow children spans every column, which is what
        /// splits a multi-column container into column blocks a page can end between.
        /// </summary>
        private static Boolean Spans(LayoutBox box)
        {
            foreach (var child in box.Children)
            {
                if (!PositionedLayout.IsOutOfFlow(child.Source) &&
                    child.Source.Style.Float == FloatSide.None &&
                    child.Source.Style.SpansColumns)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Collects a spanner-split multi-column container as the pieces a page may end between: every
        /// spanner stands alone, and the column blocks around it are one piece each (CSS Multicol 1 §6).
        /// </summary>
        private void CollectMulticolSections(
            LayoutBox box,
            List<FlowUnit> units,
            BreakValue inheritedBefore,
            BreakValue inheritedAfter,
            Int32[] avoid,
            String? pageName)
        {
            var groups = new List<(Double Top, Double Bottom)>();
            (Double Top, Double Bottom)? run = null;

            foreach (var child in box.Children)
            {
                if (PositionedLayout.IsOutOfFlow(child.Source) ||
                    child.Source.Style.Float != FloatSide.None)
                {
                    continue;
                }

                var top = _axis.Start(child);
                var bottom = _axis.End(child);

                if (child.Source.Style.SpansColumns)
                {
                    if (run is { } open)
                    {
                        groups.Add(open);
                        run = null;
                    }

                    groups.Add((top, bottom));
                    continue;
                }

                run = run is { } growing
                    ? (Math.Min(growing.Top, top), Math.Max(growing.Bottom, bottom))
                    : (top, bottom);
            }

            if (run is { } last)
            {
                groups.Add(last);
            }

            for (var index = 0; index < groups.Count; index++)
            {
                var unit = new FlowUnit
                {
                    Top = groups[index].Top,
                    Bottom = groups[index].Bottom,
                    Before = index == 0 ? inheritedBefore : BreakValue.Auto,
                    After = index == groups.Count - 1 ? inheritedAfter : BreakValue.Auto,
                    Avoid = avoid,
                    PageName = pageName,
                };

                if (index == 0)
                {
                    unit.StartBoundaryY = _axis.Start(box);
                    unit.StartBoundarySet = true;
                }

                units.Add(unit);
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
                        top = Math.Min(top, _axis.Start(item));
                        bottom = Math.Max(bottom, _axis.End(item));

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
                var ordered = _axis.IsVertical
                    ? items.OrderBy(item => _axis.Start(item)).ThenBy(item => item.BorderBox.Y).ToList()
                    : items.OrderBy(item => item.BorderBox.X).ThenBy(item => item.BorderBox.Y).ToList();

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
                        _axis.Start(item) > previousBottom + 0.001)
                    {
                        units[itemFirst].ResumeSet = true;
                        units[itemFirst].ResumeFrom = previousBottom;
                        units[itemFirst].ResumeY = _axis.Start(item);
                    }

                    previousBottom = _axis.End(item);
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
            units[first].StartBoundaryY = _axis.Start(flex);
            units[first].StartBoundarySet = true;

            if (inside.Length != avoid.Length)
            {
                MarkAvoidStart(units, first, flex);
            }
            units[^1].EndName = name;
            units[^1].EndNameSet = true;
        }

        /// <summary>
        /// Collects a grid container as the rows a page may end between (CSS Grid 1 §12.6): the items
        /// of one line stand beside each other, so a page can only end between lines. A line with one
        /// item is collected as the item, since what continues below the page is the item's own
        /// content.
        /// </summary>
        private void CollectGrid(
            LayoutBox grid,
            List<FlowUnit> units,
            BreakValue inheritedBefore,
            BreakValue inheritedAfter,
            Int32[] avoid,
            String? name)
        {
            var source = grid.Source;
            var before = Fragmentainer.Combine(inheritedBefore, PageValue(source.Style.Breaks.Before));
            var after = Fragmentainer.Combine(inheritedAfter, PageValue(source.Style.Breaks.After));
            var inside = source.Style.Breaks.Inside == BreakValue.Avoid
                ? Append(avoid, ++_nextAvoid)
                : avoid;

            var items = new List<LayoutBox>(grid.Children.Count);

            foreach (var child in grid.Children)
            {
                if (!PositionedLayout.IsOutOfFlow(child.Source) &&
                    child.Source.Style.Float == FloatSide.None)
                {
                    items.Add(child);
                }
            }

            var first = units.Count;

            if (items.Count == 1)
            {
                Collect(items[0], units, before, after, inside, name, pageNames: true, ownPageName: false);
            }
            else if (items.Count > 1)
            {
                var lines = FlexLines(items);

                for (var index = 0; index < lines.Count; index++)
                {
                    var line = lines[index];

                    if (line.Count == 1)
                    {
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
                        top = Math.Min(top, _axis.Start(item));
                        bottom = Math.Max(bottom, _axis.End(item));
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

            if (units.Count == first)
            {
                units.Add(BoxUnit(grid, before, after, avoid, name));
            }

            units[first].StartName = name;
            units[first].StartNameSet = true;
            units[first].StartBoundaryY = _axis.Start(grid);
            units[first].StartBoundarySet = true;

            if (inside.Length != avoid.Length)
            {
                MarkAvoidStart(units, first, grid);
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
        private String? FlexPageName(LayoutBox flex, String? inherited, Boolean pageNames)
        {
            if (!pageNames || flex.Source.Style.PageName is not null ||
                flex.Source.Flow.IsVertical != _axis.IsVertical)
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

                var itemName = item.Source.Flow.IsVertical == _axis.IsVertical
                    ? item.Source.Style.PageName
                    : null;

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
        /// Groups the items into the lines their boxes stand on. The items of one line overlap along
        /// the page axis; an item whose start is past every box of the line before it begins the next.
        /// </summary>
        private List<List<LayoutBox>> FlexLines(List<LayoutBox> items)
        {
            var lines = new List<List<LayoutBox>>();
            var bottom = Double.MinValue;

            foreach (var item in items.OrderBy(item => _axis.Start(item)).ThenBy(item => item.BorderBox.Y))
            {
                if (lines.Count == 0 || _axis.Start(item) >= bottom - 0.5)
                {
                    lines.Add(new List<LayoutBox>());
                    bottom = _axis.End(item);
                }
                else
                {
                    bottom = Math.Max(bottom, _axis.End(item));
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
            var name = source.Flow.IsVertical == _axis.IsVertical && source.Style.PageName is { } own
                ? own
                : pageName;
            var before = Fragmentainer.Combine(inheritedBefore, PageValue(source.Style.Breaks.Before));
            var after = Fragmentainer.Combine(inheritedAfter, PageValue(source.Style.Breaks.After));
            var inside = source.Style.Breaks.Inside == BreakValue.Avoid
                ? Append(avoid, ++_nextAvoid)
                : avoid;

            var first = units.Count;
            var flow = new List<LayoutBox>(table.Children.Count);
            Rect? header = null;
            Rect? footer = null;
            LayoutBox? foot = null;
            var rows = Double.MaxValue;
            var lastRow = Double.MinValue;

            foreach (var child in table.Children)
            {
                if (PositionedLayout.IsOutOfFlow(child.Source) ||
                    child.Source.Style.Float != FloatSide.None)
                {
                    continue;
                }

                flow.Add(child);

                // The header and the footer are the table's first group of each of those two roles, read
                // from the display the page computes to rather than the tag it wrote: a further header or
                // footer group is an ordinary body of rows and keeps its place (CSS Tables 3 §3.1). Where
                // they begin says whether a page shows rows of its own: a header repeated over the first
                // fragment would stand where it already stands, and one repeated over a fragment holding
                // no row but the last one's edge would be inventing content the page does not carry.
                if (child.Kind == LayoutKind.TableRowGroup && header is null &&
                    TableStyle.SectionRole(child.Source.Style.Display) == 0)
                {
                    header = child.BorderBox;
                }
                else if (child.Kind == LayoutKind.TableRowGroup && footer is null &&
                    TableStyle.SectionRole(child.Source.Style.Display) == 2)
                {
                    footer = child.BorderBox;
                    foot = child;
                }
                else if (child.Kind is LayoutKind.TableRowGroup or LayoutKind.TableRow)
                {
                    rows = Math.Min(rows, child.BorderBox.Y);

                    if (child.Kind == LayoutKind.TableRow)
                    {
                        lastRow = Math.Max(lastRow, child.BorderBox.Y);
                    }
                    else
                    {
                        foreach (var row in child.Children)
                        {
                            if (row.Kind == LayoutKind.TableRow)
                            {
                                lastRow = Math.Max(lastRow, row.BorderBox.Y);
                            }
                        }
                    }
                }
            }

            if (rows < Double.MaxValue && lastRow > Double.MinValue)
            {
                if (header is { } headerBand)
                {
                    Headers.Add((table.BorderBox, headerBand, rows, lastRow));
                }

                if (footer is { } footerBand)
                {
                    Footers.Add((table.BorderBox, footerBand, rows, lastRow));
                }
            }

            for (var index = 0; index < flow.Count; index++)
            {
                var child = flow[index];
                var childBefore = index == 0 ? before : BreakValue.Auto;
                var childAfter = index == flow.Count - 1 ? after : BreakValue.Auto;

                if (child.Kind == LayoutKind.TableRowGroup)
                {
                    CollectRowGroup(child, units, childBefore, childAfter, inside, name, pageNames, ReferenceEquals(child, foot));
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
                units[first].StartBoundaryY = _axis.Start(table);
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
            Boolean pageNames,
            Boolean repeatsAsFooter)
        {
            var source = group.Source;
            var name = source.Flow.IsVertical == _axis.IsVertical && source.Style.PageName is { } own
                ? own
                : pageName;
            var before = Fragmentainer.Combine(inheritedBefore, PageValue(source.Style.Breaks.Before));
            var after = Fragmentainer.Combine(inheritedAfter, PageValue(source.Style.Breaks.After));
            var inside = source.Style.Breaks.Inside == BreakValue.Avoid
                ? Append(avoid, ++_nextAvoid)
                : avoid;

            var first = units.Count;

            // The footer is repeated whole at the bottom of every fragment, so a page may not end
            // inside a row of it: the rows are one piece of the flow. Only the table's first
            // footer-role group is one - a later one is an ordinary body of rows (CSS Tables 3 §3.1).
            if (repeatsAsFooter)
            {
                units.Add(BoxUnit(group, before, after, inside, name));
                return;
            }

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

        private void AddRow(
            LayoutBox row,
            List<FlowUnit> units,
            BreakValue before,
            BreakValue after,
            Int32[] avoid,
            String? pageName)
        {
            var unit = BoxUnit(
                row,
                Fragmentainer.Combine(before, PageValue(row.Source.Style.Breaks.Before)),
                Fragmentainer.Combine(after, PageValue(row.Source.Style.Breaks.After)),
                avoid,
                pageName);

            // The row is the box its own value stands on: a page that cannot hold it moves it whole
            // rather than splitting it, so the mark belongs on the row's unit and not on a wrapper's
            // (CSS Break 3 §4.4).
            if (row.Source.Style.Breaks.Inside == BreakValue.Avoid)
            {
                unit.StartsAvoidBottom = Math.Max(unit.StartsAvoidBottom, unit.Bottom);
            }

            units.Add(unit);
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
                if (HasFlowContent(line) || _axis.End(line) - _axis.Start(line) > 0.001)
                {
                    var unit = new FlowUnit
                    {
                        Top = _axis.Start(line),
                        Bottom = _axis.End(line),
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

                    // An inline that asks not to be broken - an inline table, an inline block - makes
                    // its line one piece: a page without room for the line moves it whole, and does so
                    // even when the piece is too tall for a page of its own, because moving is what the
                    // value asks for and breaking is what the piece refuses (CSS Break 3 §4.4).
                    foreach (var child in line.Children)
                    {
                        if (!PositionedLayout.IsOutOfFlow(child.Source) &&
                            child.Source.Style.Breaks.Inside == BreakValue.Avoid)
                        {
                            unit.StartsAvoidBottom =
                                Math.Max(unit.StartsAvoidBottom, _axis.End(child));
                        }
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

        private FlowUnit BoxUnit(
            LayoutBox box,
            BreakValue before,
            BreakValue after,
            Int32[] avoid,
            String? pageName) =>
            new()
            {
                Top = _axis.Start(box),
                Bottom = _axis.End(box),
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

        private Double DeepestBottom(LayoutBox box)
        {
            var bottom = _axis.End(box);

            // What a box hides cannot be scrolled to, so it is no part of the document's own length.
            if (box.Source.Style.ClipsOverflow)
            {
                return bottom;
            }

            foreach (var child in box.Children)
            {
                bottom = Math.Max(bottom, DeepestBottom(child));
            }

            return bottom;
        }

        /// <summary>
        /// Cuts what a clipping box holds down to the box's own end: the content past it is painted
        /// nowhere, so it neither lengthens the document nor gives a page a place to end.
        /// </summary>
        public void ClampClipped(
            LayoutBox box, List<FlowUnit> units, Int32 unitStart, Int32 floatStart)
        {
            if (!box.Source.Style.ClipsOverflow)
            {
                return;
            }

            var end = _axis.End(box);

            for (var index = units.Count - 1; index >= unitStart; index--)
            {
                var unit = units[index];

                if (unit.Top >= end - 0.001)
                {
                    units.RemoveAt(index);
                    continue;
                }

                if (unit.Bottom > end)
                {
                    unit.Bottom = end;
                }
            }

            for (var index = floatStart; index < Floats.Count; index++)
            {
                var span = Floats[index];

                if (span.Bottom > end)
                {
                    Floats[index] = (span.Top, Math.Max(span.Top, end), span.Avoid);
                }
            }
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
                source.Kind is BoxKind.InlineBlock ||
                // A one-column container is the ordinary stack with a fragmentainer of its own:
                // nothing visible splits, so a page may break inside it as it would in a block,
                // and a multi-column container it holds is the one the page can end in
                // (CSS Multicol 1 §3.4).
                (source.Style.Multi.IsMultiColumn &&
                    source.Style.Multi.Used(box.ContentBox.Width).Count > 1) ||
                Scrolls(source.Style) ||
                source.Style.Contain.Block;
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

        public Double Bottom { get; set; }

        public BreakValue Before { get; set; } = BreakValue.Auto;

        public BreakValue After { get; set; } = BreakValue.Auto;

        /// <summary>Gets whether a break may not fall between this unit and the one after it where
        /// there is any other place to break - a table caption and the row it belongs to.</summary>
        public Boolean KeepWithNext { get; set; }

        /// <summary>Gets whether no break may fall before this unit: a fieldset's legend stands in
        /// the border rather than in the flow, so nothing ahead of it is a place a page may end
        /// (HTML rendering §15.3.12).</summary>
        public Boolean SealedBefore { get; set; }

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
        /// Gets whether the unit stands for a box that holds no flow content of its own - only
        /// floats - so its span is room the floats take rather than content a page carries.
        /// </summary>
        public Boolean Shell { get; set; }

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

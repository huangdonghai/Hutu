using Hutu.Rendering.Boxes;
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace Hutu.Rendering.Layout;

/// <summary>The extent inline content occupies along each of the two axes it is laid out in.</summary>
/// <remarks>
/// Which physical axis either one lands on is the writing mode's business, not the content's: a block
/// asks for the pair and then decides what to do with it.
/// </remarks>
public readonly struct InlineContentSize
{
    public InlineContentSize(Double inline, Double block)
    {
        Inline = inline;
        Block = block;
    }

    /// <summary>Gets the extent along the axis the text runs, which fills the content box's inline size.</summary>
    public Double Inline { get; }

    /// <summary>Gets the extent along the axis the lines advance, which is what the content needs to stack.</summary>
    public Double Block { get; }
}

/// <summary>
/// Lays out an inline formatting context: it flattens the inline content of a block into text runs,
/// breaks them into line boxes and positions every run.
/// </summary>
/// <remarks>
/// The tree is flattened into a single stream of runs plus explicit breaks, but the inline elements
/// the runs came from are not forgotten: every run carries the chain of boxes it sits inside, and
/// every such element is given a <see cref="LayoutKind.Inline"/> node on every line it reaches. Those
/// nodes nest, so an outer element's background lands under an inner one's, and an element broken over
/// several lines leaves one fragment per line with the cut edges left bare, which is what
/// <c>box-decoration-break: slice</c> asks for. An element is given a node whether or not it draws
/// anything, because whether it draws anything is a question for the painter: a change that only adds
/// a background is a change the layout is deliberately reused through, so the node has to be there
/// for the background to have somewhere to land. An <c>inline-block</c> is the one inline-level box
/// that does not enter the stream at all: it is placed whole, at the size its own content asks for,
/// through the engine that lays that content out.
/// <para>
/// Line height is the tallest run on the line, expanded by half-leading when the element sets an
/// explicit leading, and the baseline is shared by the whole line. An inline element's own band is
/// the exception: it is the font's content area, and it neither grows the line nor is grown by it -
/// inline padding and borders overflow the line rather than widen it. An inline-block is the other
/// exception the other way round: it is as tall as its own content and does grow the line, which is
/// what puts its text on the line's baseline with the text around it.
/// </para>
/// <para>
/// Everything is written in terms of the two logical axes rather than x and y, so the same code lays
/// out a row of lines and a column of them: <em>inline</em> is the extent a line runs along and where
/// a run sits within it, <em>block</em> is the extent a line occupies and where the next one starts.
/// Horizontal writing puts inline on x and block on y, vertical writing swaps the two, and
/// <see cref="WritingModeInfo"/> is the only thing that knows which is which.
/// </para>
/// </remarks>
public sealed class InlineLayout
{
    private readonly ITextMeasurer _measurer;

    /// <summary>
    /// The lines each container was broken into, kept for the rest of the layout pass.
    /// </summary>
    /// <remarks>
    /// A context is broken into lines by the measuring phase and its lines are placed by the arranging
    /// one, and both of them used to run the whole breaking themselves: the same text chunked, oriented,
    /// measured and lined up twice, for the second time producing nothing but the placement of what the
    /// first had already settled. A line the placing phase reads is a line it does not have to make,
    /// which is what this dictionary is for - a plan is settled once <see cref="BuildLines"/> returns
    /// (<see cref="LineBuilder.Build"/> and <see cref="MarkSlices"/> are the only writers) and every
    /// reader after that only places.<para>
    /// The key carries the resolved indentation as well as the inline extent, because a percentage
    /// <c>text-indent</c> is a share of the containing block, and the two phases reach this container
    /// through contexts that are not always the same. Two that disagree about the indent are two
    /// different breakings and get their own plan.<br/>
    /// It lives exactly as long as the pass does, cleared with the engine's own caches, so a repeat
    /// layout never inherits lines from a tree it has not measured.<br/>
    /// What is held is not free: the lines stay live until the pass ends instead of becoming garbage
    /// when the measuring phase does. It is the cheaper half of the trade - the placing phase used to
    /// build its own copy of every one of them and throw it away straight after.
    /// </remarks>
    private readonly Dictionary<LinePlanKey, List<Line>> _plans = new();

    /// <summary>Drops the plans, which belong to one layout pass as the engine's own caches do.</summary>
    internal void ResetPlanCache() => _plans.Clear();

    /// <summary>
    /// Gets the lines the container's inline content is broken into at this extent, making them the
    /// first time the pass asks and handing back the same lines every time it asks again.
    /// </summary>
    private List<Line> Plan(
        Box container,
        Double maxInline,
        WritingModeInfo flow,
        InlineContext context,
        Double indent)
    {
        var key = new LinePlanKey(container, maxInline, indent, FlowSignature(flow), context.Containing.Floats);

        if (_plans.TryGetValue(key, out var known))
        {
            return known;
        }

        var items = new List<InlineItem>();
        Collect(container, items, flow, null, maxInline);

        // Content with nothing to break still gets a plan: the phases ask again for the same answer and
        // an empty list is the same work saved as any other.
        var lines = items.Count == 0
            ? new List<Line>()
            : BuildLines(items, maxInline, flow, context, indent);

        _plans[key] = lines;
        return lines;
    }

    /// <summary>
    /// Packs the writing mode, orientation, direction and upright combining into the one number the
    /// plan key wants. <see cref="WritingModeInfo"/> is a value type that answers equality through the
    /// reflection-based default rather than an override, so hashing and comparing it whole is what
    /// this avoids.
    /// </summary>
    private static Int32 FlowSignature(WritingModeInfo flow) =>
        (((Int32)flow.Mode) << 12) | (((Int32)flow.Orientation) << 8) | (((Int32)flow.Direction) << 1) |
        (flow.CombinesUpright ? 1 : 0);

    public InlineLayout(ITextMeasurer measurer) => _measurer = measurer;

    /// <summary>
    /// Measures the extent the inline children need along the block axis at the given inline extent,
    /// without producing any geometry. This is what the measuring phase of a block asks for before it
    /// knows where the lines will sit.
    /// </summary>
    public Double MeasureHeight(Box container, Double contentWidth, InlineContext context) =>
        Measure(container, contentWidth, container.Flow, context).Block;

    /// <summary>
    /// Measures the extents the inline children need when they are given <paramref name="maxInline"/>
    /// to run along. The block extent is what the lines of a row stack into, or the width of the band
    /// of columns a column stacks into.
    /// </summary>
    public InlineContentSize Measure(Box container, Double maxInline, WritingModeInfo flow, InlineContext context)
    {
        return MeasureCore(container, maxInline, flow, context);
    }

    private InlineContentSize MeasureCore(Box container, Double maxInline, WritingModeInfo flow, InlineContext context)
    {
        var lines = Plan(container, maxInline, flow, context, Indentation(container, flow, context));

        var block = 0.0;
        var inline = 0.0;

        foreach (var line in lines)
        {
            if (line.Height > 0)
            {
                block += line.Height;
            }

            // Room a line had to drop through to get clear of a float is height the block owes it too.
            block += line.GapBefore;

            // The room the line takes is what its runs take plus the distance it was set in by, which
            // only the first line of a box has any of.
            inline = Math.Max(inline, line.Indent + line.Used);
        }

        return new InlineContentSize(inline, block);
    }

    /// <summary>
    /// Measures the intrinsic inline extents of the container's content: the largest thing that cannot
    /// be broken - <c>min-content</c> - and the extent on a single line - <c>max-content</c>.
    /// </summary>
    /// <param name="basis">
    /// The inline extent a percentage among a replaced box's constraints resolves against; there is no
    /// definite line to use, so the containing block's inline extent stands in.
    /// </param>
    public IntrinsicSize MeasureIntrinsic(Box container, Double basis, WritingModeInfo flow, InlineContext context)
    {
        return MeasureIntrinsicCore(container, basis, flow, context);
    }

    private IntrinsicSize MeasureIntrinsicCore(Box container, Double basis, WritingModeInfo flow, InlineContext context)
    {
        var items = new List<InlineItem>();
        Collect(container, items, flow, null, basis);

        // The first line carries the box's indentation ahead of anything set on it, so both extents the
        // content asks for are that much wider: one line is the indent and the line, and the narrowest
        // break is the indent and the longest thing that cannot be broken.
        var indent = Indentation(container, flow, context);

        var minContent = 0.0;
        var maxContent = indent;
        var unbreakable = indent;

        // Whether the stream has reached anything a line would draw. White space ahead of it asks for
        // no room, because the line it would begin is the one line that does not draw it.
        var sawContent = false;

        // Nothing here is broken into lines, so an element is opened once and stays open: the padding
        // it brings with it is charged to the run that enters it, wherever in the stream that is.
        var open = new List<OpenInline>();

        foreach (var item in items)
        {
            if (item.IsBreak)
            {
                minContent = Math.Max(minContent, unbreakable);
                unbreakable = 0;
                continue;
            }

            // Padding and border are part of what an element asks for, so they count towards both the
            // extent the content needs on one line and the extent the narrowest break can leave it.
            var insets = Advance(open, item.Inline, carried: null, origin: 0, apply: true, step: null);
            maxContent += insets;
            unbreakable += insets;

            if (item.IsReplaced)
            {
                var (width, height) = ReplacedSizing.Resolve(item.Box, basis, 0);
                var extent = flow.InlineExtent(width, height);
                minContent = Math.Max(minContent, extent);
                maxContent += extent;
                unbreakable += extent;
                sawContent = true;
                continue;
            }

            // An inline-block is atomic too: it takes the extent its own content asks for and offers no
            // break inside itself, however its content would have been broken had it stood on its own.
            if (item.IsAtomic)
            {
                var intrinsic = context.Engine.MeasureIntrinsic(item.Box, context.Containing);
                var (start, end) = flow.InlineMargins(item.Box.Style.Margin.Resolve(basis));
                var extent = intrinsic.MaxContent + start + end;

                maxContent += extent;
                unbreakable += extent;
                sawContent = true;
                minContent = Math.Max(minContent, intrinsic.MinContent + start + end);
                continue;
            }

            // A ruby pair cannot be broken between its two parts, so it asks for the room the wider of
            // them needs and offers no break opportunity inside itself.
            if (item.IsRuby)
            {
                var extent = Math.Max(
                    Measure(item.Style, item.Text!, flow),
                    Measure(item.AnnotationStyle, item.AnnotationText!, flow));

                maxContent += extent;
                unbreakable += extent;
                sawContent = true;
                continue;
            }

            // The chunks are ranges of the item's own text: only a width is wanted here, and a string
            // per chunk would be characters copied for a number.
            var source = item.Text!;

            foreach (var chunk in TextChunker.Ranges(source))
            {
                var width = Measure(item.Style, source, chunk.Start, chunk.Length, flow);
                maxContent += width;

                if (TextChunker.IsWhiteSpace(source, chunk.Start, chunk.Length))
                {
                    // A collapsible space the line would begin with is not drawn, so it asks for no
                    // room in either extent and leaves the break it stands at uncounted as well.
                    if (!sawContent && !item.Style.IsPreformatted)
                    {
                        continue;
                    }

                    // A collapsible space is a break opportunity: the run before it can be split.
                    minContent = Math.Max(minContent, unbreakable);
                    unbreakable = 0;
                }
                else if (TextChunker.IsCjk(source[chunk.Start]))
                {
                    sawContent = true;

                    // A line may break on either side of a CJK character, so the character stands
                    // alone rather than joining the run around it, and it is the run beside it - the
                    // Latin word, say - that decides how narrow the content can go.
                    minContent = Math.Max(minContent, Math.Max(unbreakable, width));
                    unbreakable = 0;
                }
                else
                {
                    sawContent = true;
                    unbreakable += width;
                }
            }
        }

        // An element the stream ends inside still draws its closing edge, so it is charged for it here.
        foreach (var item in open)
        {
            maxContent += item.Ancestor.InlineEndInset;
            unbreakable += item.Ancestor.InlineEndInset;
        }

        minContent = Math.Max(minContent, unbreakable);
        return new IntrinsicSize(minContent, Math.Max(maxContent, minContent));
    }

    /// <summary>
    /// Lays out the inline children of the container inside the given content box, appending line
    /// boxes to it, and returns the extent they occupied along the block axis.
    /// </summary>
    public Double Layout(Box container, LayoutBox parent, Rect contentBox, WritingModeInfo flow, InlineContext context)
    {
        return LayoutCore(container, parent, contentBox, flow, context);
    }

    private Double LayoutCore(Box container, LayoutBox parent, Rect contentBox, WritingModeInfo flow, InlineContext context)
    {
        var maxInline = flow.InlineExtent(contentBox.Width, contentBox.Height);

        // The lines the measuring phase broke this content into, if it broke them at the same extent -
        // which is what the block layout makes sure of, since it reconstructs the child's containing
        // block from the same arithmetic it measured against.
        var lines = Plan(container, maxInline, flow, context, Indentation(container, flow, context));

        var cursor = 0.0;
        var step = new InlineStep();

        foreach (var line in lines)
        {
            // A line is placed for what is on it, not for how tall it came out: a run set with no line
            // height at all still has glyphs, and they are drawn overflowing the line they sit on.
            if (line.Fragments.Count == 0)
            {
                cursor += Math.Max(0, line.Height) + line.GapBefore;
                continue;
            }

            // The room a line had to fall through to get clear of a float belongs ahead of it, not after:
            // the line that dropped stands at the height it reached, and the ones below it stack from there.
            cursor += line.GapBefore;

            // The line box spans the whole inline extent of the content box and only as much of the
            // block one as it needs, so in a row it is the content width and the line's own height,
            // and in a column the content height and the band the text runs down.
            var lineBox = new LayoutBox(container, LayoutKind.Line)
            {
                BorderBox = flow.Map(contentBox, line.Left, line.Extent, cursor, line.Height),
                Baseline = flow.BlockOffsetFromLeft(line.Height, line.Ascent),
                Flow = flow,
            };

            var open = new List<OpenInline>();
            var origin = line.Offset + line.Left;

            foreach (var fragment in line.Fragments)
            {
                // The inline elements the run sits inside are opened and closed around it, each taking
                // a box that begins and ends where the run reaches its edges. The step charges the same
                // padding and border the line was measured with, applied here one edge at a time so
                // that every box lands where the extent says it should.
                step.Clear();
                Advance(open, fragment.Inline, line.ContinuesFromPrevious, origin, apply: true, step);

                // An element the run leaves is never cut: a cut is what carrying on to the next line
                // is, and an element that ends in the middle of one ends for good. Its box runs to the
                // far side of its own end edge, which is the edge the run it holds stops short of.
                foreach (var left in step.Closed)
                {
                    origin += left.Ancestor.InlineEndInset;
                    CloseBox(left, origin, contentBox, flow, line, cursor);
                }

                foreach (var entered in step.Opened)
                {
                    OpenBox(entered, open, lineBox, flow, line);
                    origin += entered.Inset;
                }

                Place(fragment, open, lineBox, contentBox, flow, line, origin, cursor, context);
                origin += fragment.Width;
            }

            // The elements the line ends inside carry on to the next line, so each keeps its end edge
            // only if it does not: where it was cut, that edge belongs to the line the element ends on.
            for (var index = open.Count - 1; index >= 0; index--)
            {
                var left = open[index];
                var cut = line.ContinuesToNext?.Contains(left.Ancestor) == true;

                origin += cut ? 0 : left.Ancestor.InlineEndInset;
                CloseBox(left, origin, contentBox, flow, line, cursor);
            }

            parent.Children.Add(lineBox);
            cursor += line.Height;
        }

        return cursor;
    }

    /// <summary>
    /// Opens the box an inline element draws through on this line.
    /// </summary>
    /// <remarks>
    /// The band is the element's font content area, which is what makes it taller than the glyphs
    /// without being as tall as the line: inline padding and border overflow the line rather than
    /// widening it, so nothing here can move the lines below. An element the line is cut through draws
    /// neither the padding, the border nor the curve of the edge it was cut at.
    /// </remarks>
    private static void OpenBox(OpenInline item, List<OpenInline> open, LayoutBox lineBox, WritingModeInfo flow, Line line)
    {
        var ancestor = item.Ancestor;
        var cutStart = line.ContinuesFromPrevious?.Contains(ancestor) == true;
        var cutEnd = line.ContinuesToNext?.Contains(ancestor) == true;

        item.Node = new LayoutBox(ancestor.Box, LayoutKind.Inline)
        {
            Flow = flow,
            Padding = ancestor.PaddingFor(cutStart, cutEnd),
            Border = ancestor.BorderFor(cutStart, cutEnd),
            SlicesInlineStart = cutStart,
            SlicesInlineEnd = cutEnd,
        };

        // Everything opened before this one on the line encloses it, so the box just inside it - or the
        // line itself when this is the outermost - is where its box belongs.
        var index = open.IndexOf(item);
        var owner = index > 0 ? open[index - 1].Node! : lineBox;
        owner.Children.Add(item.Node);
    }

    /// <summary>
    /// Closes the box an inline element drew through on this line, spanning it from where the element
    /// began to where it ends.
    /// </summary>
    private static void CloseBox(OpenInline item, Double end, Rect contentBox, WritingModeInfo flow, Line line, Double cursor)
    {
        var ancestor = item.Ancestor;

        // The band sits astride the line's shared baseline, offset by the element's own vertical
        // alignment, so a run that is raised or lowered takes the band around it with it while the
        // line's baseline stays where the rest of the line put it.
        var blockOffset = cursor + line.Ascent + ancestor.BaselineShift - ancestor.Ascent - ancestor.BlockStartInset;
        var blockSize = ancestor.Ascent + ancestor.Descent + ancestor.BlockStartInset + ancestor.BlockEndInset;

        item.Node!.BorderBox = flow.Map(contentBox, item.Start, end - item.Start, blockOffset, blockSize);
    }

    /// <summary>Builds the node a run is drawn through and puts it in the innermost box open around it.</summary>
    private static void Place(
        TextFragment fragment,
        List<OpenInline> open,
        LayoutBox lineBox,
        Rect contentBox,
        WritingModeInfo flow,
        Line line,
        Double origin,
        Double cursor,
        InlineContext context)
    {
        if (fragment.Atomic is { } atomic)
        {
            // The box is arranged as the block container it is - it brings its own geometry, radii and
            // children with it - and the line only decides where it goes: the margins along the line
            // are taken out of the room the line reserved, and the box's own baseline is put on the
            // line's, which is what makes its text sit with the text around it.
            Attach(open, lineBox, context.Engine.Arrange(
                fragment.Box,
                flow.Map(
                    contentBox,
                    origin + atomic.InlineStartMargin,
                    atomic.InlineSize - atomic.InlineStartMargin - atomic.InlineEndMargin,
                    cursor + line.Ascent - atomic.Ascent,
                    atomic.BlockSize),
                context.Containing));

            return;
        }

        if (fragment.IsReplaced)
        {
            // The bottom margin edge of a replaced inline rests on the baseline, so its box is lifted
            // by its own height rather than filling the line box. The band of the element around it is
            // still the font's content area, so it does not grow to cover an image taller than its text.
            Attach(open, lineBox, new LayoutBox(fragment.Box, LayoutKind.Image)
            {
                Font = fragment.Style.GetFont(),
                Baseline = flow.BlockOffsetFromLeft(line.Height, line.Ascent + fragment.BaselineShift),
                Flow = fragment.Flow,
                ImageUrl = fragment.Box.SourceUrl,
                ImageFit = fragment.Style.ObjectFit,
                BorderBox = flow.Map(
                    contentBox,
                    origin,
                    fragment.Width,
                    cursor + line.Ascent - fragment.ContentHeight,
                    fragment.ContentHeight),
            });

            return;
        }

        if (fragment.Ruby is { } annotation)
        {
            // The annotation takes a strip at one end of the line's block extent and the base keeps
            // the rest, which is the end away from the reading. Along the inline axis the two share the
            // room the wider of them asked for, each centred in it, which is what stops a long reading
            // from hanging off one side.
            var baseBlock = line.Height - annotation.Block;

            // An upright character in a column is drawn centred in its box, so a base drawn that way
            // has to be boxed centred on the band as well - boxed against the far end it would sit half
            // a reading off the column line its neighbours stand on. A base that is turned, or set in a
            // line, is drawn from the line's shared baseline instead, and the end away from the reading
            // is where that baseline puts it.
            var baseOffset = fragment.Flow.IsVertical && fragment.Flow.Orientation == TextOrientation.Upright
                ? cursor + (annotation.Block / 2)
                : cursor + (annotation.Under ? 0 : annotation.Block);

            Attach(open, lineBox, new LayoutBox(fragment.Box, LayoutKind.Text)
            {
                Text = fragment.Text,
                Font = fragment.Style.GetFont(),
                Baseline = flow.BlockOffsetFromLeft(
                    baseBlock,
                    line.Ascent + fragment.BaselineShift - (annotation.Under ? 0 : annotation.Block)),
                Flow = fragment.Flow,
                BorderBox = flow.Map(
                    contentBox,
                    origin + ((fragment.Width - fragment.BaseWidth) / 2),
                    fragment.BaseWidth,
                    baseOffset,
                    baseBlock),
            });

            Attach(open, lineBox, AnnotationBox(
                annotation,
                contentBox,
                flow,
                origin,
                fragment.Width,
                cursor + (annotation.Under ? line.Height - annotation.Block : 0)));

            return;
        }

        Attach(open, lineBox, new LayoutBox(fragment.Box, LayoutKind.Text)
        {
            Text = fragment.Text,
            Font = fragment.Style.GetFont(),
            Baseline = flow.BlockOffsetFromLeft(line.Height, line.Ascent + fragment.BaselineShift),
            Flow = fragment.Flow,
            BorderBox = flow.Map(contentBox, origin, fragment.Width, cursor, line.Height),
        });
    }

    /// <summary>Adds a node inside the innermost inline element still open, or inside the line itself.</summary>
    private static void Attach(List<OpenInline> open, LayoutBox lineBox, LayoutBox node) =>
        (open.Count > 0 ? open[^1].Node! : lineBox).Children.Add(node);

    /// <summary>
    /// Builds the box that draws a ruby's annotation beside its base, centred within the room the pair
    /// takes along the inline axis and placed against the block-start edge of the line.
    /// </summary>
    private static LayoutBox AnnotationBox(
        RubyAnnotation annotation,
        Rect contentBox,
        WritingModeInfo flow,
        Double origin,
        Double room,
        Double cursor)
    {
        return new LayoutBox(annotation.Box, LayoutKind.Text)
        {
            Text = annotation.Text,
            Font = annotation.Style.GetFont(),
            Flow = annotation.Flow,
            Baseline = flow.BlockOffsetFromLeft(annotation.Block, annotation.Ascent),
            BorderBox = flow.Map(
                contentBox,
                origin + ((room - annotation.Width) / 2),
                annotation.Width,
                cursor,
                annotation.Block),
        };
    }

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

                // A ruby pairs its base text with the annotation that goes over it, so the two are
                // gathered into one item instead of being run together as ordinary text.
                case BoxKind.Ruby:
                    AddRuby(child, items, chain);
                    break;

                // An ordinary inline element keeps its own box. It is not flattened away: the runs inside
                // it go on carrying it, so the line can give it back the box that draws it.
                case BoxKind.Inline:
                    Collect(
                        child,
                        items,
                        flow,
                        new InlineAncestor(child, chain, basis, flow, _measurer),
                        basis);
                    break;

                // A base or a reading is a run like any other; only the pair around them is set apart.
                case BoxKind.RubyBase:
                case BoxKind.RubyText:
                    Collect(child, items, flow, chain, basis);
                    break;
            }
        }
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
        var baseText = new System.Text.StringBuilder();
        var annotationText = new System.Text.StringBuilder();
        var annotationStyle = ruby.Style;
        Box? annotationBox = null;

        foreach (var child in ruby.Children)
        {
            if (child.Kind == BoxKind.RubyText)
            {
                Flatten(child, annotationText);

                // The first annotation is the one the reading is set in; a pair carries one of them,
                // whatever the markup wrapped it in.
                if (annotationBox is null)
                {
                    annotationStyle = child.Style;
                    annotationBox = child;
                }
            }
            else
            {
                Flatten(child, baseText);
            }
        }

        if (baseText.Length == 0)
        {
            // A ruby with nothing to annotate has nothing to contribute.
            return;
        }

        if (annotationText.Length == 0 || annotationBox is null)
        {
            // Without an annotation there is nothing to set the base apart from ordinary text.
            items.Add(InlineItem.CreateText(baseText.ToString(), ruby.Style, ruby, chain));
            return;
        }

        items.Add(InlineItem.CreateRuby(
            baseText.ToString(),
            ruby.Style,
            ruby,
            annotationText.ToString(),
            annotationStyle,
            annotationBox,
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

    /// <summary>
    /// Splits a text node on its explicit newlines. They survive white-space processing only for
    /// preformatted content, where each one forces a line break rather than being drawn as a glyph.
    /// </summary>
    private static void AddText(String text, ComputedStyle style, Box box, List<InlineItem> items, InlineAncestor? chain)
    {
        var start = 0;

        while (true)
        {
            var index = text.IndexOf('\n', start);

            if (index < 0)
            {
                if (start < text.Length)
                {
                    items.Add(InlineItem.CreateText(text[start..], style, box, chain));
                }

                return;
            }

            if (index > start)
            {
                items.Add(InlineItem.CreateText(text[start..index], style, box, chain));
            }

            items.Add(InlineItem.CreateBreak(style, box, chain));
            start = index + 1;
        }
    }

    private List<Line> BuildLines(
        List<InlineItem> items,
        Double maxInline,
        WritingModeInfo flow,
        InlineContext context,
        Double indent = 0)
    {
        var lines = new List<Line>();

        // The elements a line resumes inside, which is what the next builder needs to know so that it
        // draws no start edge for an element whose start edge is already on the line above.
        var carried = (HashSet<InlineAncestor>?)null;

        // How much room each line has, which is the block's extent for every block that has nothing
        // floated beside it and the float's far edge for the lines that have. A column is left out, as
        // it has been for floats themselves: the band a line would be shortened by runs sideways there.
        var room = new LineRoom(
            flow.IsVertical ? null : context.Containing.Floats,
            maxInline);

        // Only the first line of the content is set in; the line after a forced break begins where any
        // other line does, which is what leaving the indent behind on the first builder says.
        var builder = new LineBuilder(indent);

        void Close(LineBuilder filled)
        {
            var line = filled.Build(_measurer, room.Room, carried);

            line.Left = room.Inset;
            line.Extent = room.Room;
            line.GapBefore = room.Gap;

            lines.Add(line);
            carried = line.ContinuesToNext;
            room.Next(line.Height);
        }

        foreach (var item in items)
        {
            if (item.IsBreak)
            {
                Close(builder);
                builder = new LineBuilder(item.Style, carried);
                continue;
            }

            if (item.IsReplaced)
            {
                // The container's inline extent is the containing block of everything on the line, so
                // that is what a percentage among the item's constraints resolves against. A line box
                // has no definite extent across itself, so a percentage on the other axis is `auto`.
                var (width, height) = ReplacedSizing.Resolve(item.Box, flow.IsVertical ? 0 : maxInline, flow.IsVertical ? maxInline : 0);
                var inlineSize = flow.InlineExtent(width, height);
                var blockSize = flow.BlockExtent(width, height);

                if (builder.HasContent && room.Room > 0 && builder.ExtentFor(item.Inline) + inlineSize > room.Room)
                {
                    Close(builder);
                    builder = new LineBuilder(item.Style, carried);
                }

                builder.AddReplaced(item.Box, inlineSize, blockSize, item.Inline);
                continue;
            }

            // An inline-block is placed as one piece on the same terms as a replaced box, but its size
            // is the layout's own to work out, which is why it is measured here rather than resolved
            // from the box's intrinsic one.
            if (item.IsAtomic)
            {
                // The room the line has left is measured along the axis the line itself runs along, so
                // it is a physical width in a row and a height in a column. Nothing is bounded when the
                // line has no definite extent at all.
                var spare = room.Room <= 0 ? Double.PositiveInfinity : room.Room - builder.Width;

                var atomic = MeasureInlineBlock(item.Box, maxInline, spare, flow, context);

                if (builder.HasContent && room.Room > 0 && builder.ExtentFor(item.Inline) + atomic.InlineSize > room.Room)
                {
                    Close(builder);
                    builder = new LineBuilder(item.Style, carried);
                }

                builder.AddAtomic(item.Box, atomic, item.Inline);
                continue;
            }

            // A ruby is placed as one piece: breaking between a base and its annotation would leave a
            // reading stranded without the characters it belongs to.
            if (item.IsRuby)
            {
                // The pair's own flow is what its two halves are oriented by, so an inline element
                // that asks for a different orientation keeps it inside the line it sits in. The base
                // is then drawn in the orientation its own characters resolve to - a CJK base stands
                // upright under `mixed`, exactly as the same character would outside a ruby.
                var rubyFlow = item.Box.Flow;
                var baseFlow = ResolveOrientation(item.Text!, rubyFlow);
                var annotation = MeasureAnnotation(item);
                var baseWidth = Measure(item.Style, item.Text!, rubyFlow);
                var extent = Math.Max(baseWidth, annotation.Width);

                if (builder.HasContent && room.Room > 0 && builder.ExtentFor(item.Inline) + extent > room.Room)
                {
                    Close(builder);
                    builder = new LineBuilder(item.Style, carried);
                }

                builder.AddRuby(item.Text!, extent, item.Style, item.Box, baseFlow, annotation, baseWidth, item.Inline);
                continue;
            }

            // An inline element may ask for an orientation of its own, so each run is measured and
            // drawn in the flow of the box it came from rather than in the container's. The font is
            // taken once for the item, because every piece of it is set in that one font.
            var itemFlow = item.Box.Flow;
            var font = item.Style.GetFont();

            foreach (var chunk in TextChunker.Ranges(item.Text!))
            {
                foreach (var run in new OrientedPieces(item.Text!, chunk.Start, chunk.Length, itemFlow))
                {
                    var piece = Measure(item.Style, font, run, itemFlow);

                    // White space a line begins with is not drawn. The node a run of text starts with
                    // may begin with the one space an element's edge collapsed into, and only the line
                    // knows whether that edge is a line's start - so the piece is dropped here rather
                    // than when the nodes were collapsed.
                    if (piece.IsWhiteSpace && !builder.HasContent && !item.Style.IsPreformatted)
                    {
                        continue;
                    }

                    if (builder.HasContent && room.Room > 0 && builder.ExtentFor(item.Inline) + piece.Width > room.Room)
                    {
                        Close(builder);
                        builder = new LineBuilder(item.Style, carried);

                        // The space that triggered the break is consumed by it.
                        if (piece.IsWhiteSpace)
                        {
                            continue;
                        }
                    }

                    builder.Add(piece.Source, piece.Start, piece.Length, piece.Width, item.Style, item.Box, piece.Flow, item.Inline);
                }
            }
        }

        if (builder.HasContent)
        {
            Close(builder);
        }

        MarkSlices(lines, maxInline);
        return lines;
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
            lines[index + 1].ContinuesFromPrevious = pending;
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
        var target = new List<InlineAncestor>();

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
        // wrapped around the others is drawn outside theirs.
        for (var index = open.Count - 1; index >= shared; index--)
        {
            var item = open[index];

            step?.Closed.Add(item);
            added += item.Ancestor.InlineEndInset;
            position += item.Ancestor.InlineEndInset;
        }

        if (apply)
        {
            open.RemoveRange(shared, open.Count - shared);
        }

        for (var index = shared; index < target.Count; index++)
        {
            var ancestor = target[index];

            // An element the line resumed inside was cut at its start edge, which was drawn before the
            // break; only an element a line begins inside pays nothing for that edge here.
            var inset = carried?.Contains(ancestor) == true ? 0 : ancestor.InlineStartInset;
            var item = new OpenInline(ancestor, position, inset);

            step?.Opened.Add(item);

            if (apply)
            {
                open.Add(item);
            }

            added += inset;
            position += inset;
        }

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
    /// The room the line has left for the box's margin box - negative when there is less than none -
    /// or positive infinity when the line has no definite extent to measure it against.
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
        var intrinsic = context.Engine.MeasureIntrinsic(box, context.Containing);

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

        var size = context.Engine.Measure(box, constraints, context.Containing);

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
        // The box's baseline can only be carried to the line it stands on while the two are set the same
        // way: a box in a column stands on a line of its own frame, and the line here would be measuring
        // a baseline from an edge the box does not have one against. A box that holds blocks has no line
        // of its own to stand on either, and falls back to its end edge.
        if (flow.IsVertical != box.Flow.IsVertical || HasBlockChildren(box) || box.Children.Count == 0)
        {
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

        var items = new List<InlineItem>();
        Collect(box, items, box.Flow, null, contentInline);

        var lines = BuildLines(items, contentInline, box.Flow, context, Indentation(box, box.Flow, context));

        if (lines.Count == 0)
        {
            // A box with nothing to set has no line of its own, so its baseline is the one its font
            // would have given it.
            return start + _measurer.GetMetrics(style.GetFont()).Ascent;
        }

        var baseline = start;

        for (var index = 0; index < lines.Count - 1; index++)
        {
            baseline += lines[index].Height;
        }

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
        + (style.LetterSpacing * length);

    /// <summary>
    /// Measures the annotation of a ruby item: how far it reaches along the inline axis, how much room
    /// it needs beside the base, and the orientation its glyphs are drawn in.
    /// </summary>
    /// <remarks>
    /// The annotation is set in a writing mode of its own only in that its characters may orient
    /// differently from the base's - a reading in Latin is turned where the kanji beside it stay
    /// upright - so it is measured the same way any other run is and keeps the orientation its first
    /// run resolved to.
    /// </remarks>
    private RubyAnnotation MeasureAnnotation(InlineItem item)
    {
        var style = item.AnnotationStyle;
        var text = item.AnnotationText!;
        var flow = item.AnnotationBox!.Flow;
        var metrics = _measurer.GetMetrics(style.GetFont());
        var ascent = metrics.Ascent;
        var descent = metrics.Descent;

        if (style.LineHeight is { } lineHeight)
        {
            // Half the leading, which is negative when the font is taller than the line it is set on -
            // a face whose own ascent and descent come to more than the declared line height is drawn
            // overflowing a line box that is still as tall as the page asked for.
            var halfLeading = (lineHeight - (ascent + descent)) / 2;
            ascent += halfLeading;
            descent += halfLeading;
        }

        return new RubyAnnotation(
            text,
            Measure(style, text, flow),
            ascent + descent,
            ascent,
            item.Box.Style.HasRubyUnder,
            style,
            item.AnnotationBox!,
            ResolveOrientation(text, flow));
    }

    /// <summary>
    /// Measures a range of text the way a column advances it. Upright text advances by its em box
    /// rather than by its horizontal width, so a Latin letter that would leave room to spare in a line
    /// still takes a whole em in a column, and text combined upright is compressed into a single em.
    /// </summary>
    private static Double MeasureColumn(
        String source,
        Int32 origin,
        Int32 length,
        FontStyle font,
        Double fontSize,
        WritingModeInfo flow,
        ITextMeasurer measurer)
    {
        var total = 0.0;

        foreach (var run in new OrientedPieces(source, origin, length, flow))
        {
            total += run.Advance(font, measurer, fontSize, flow);
        }

        return total;
    }

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

            if (_flow.CombinesUpright && TextChunker.IsCombinable(_source[_index]))
            {
                var count = 0;

                while (_index + count < end && TextChunker.IsCombinable(_source[_index + count]))
                {
                    count++;
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

            var upright = _flow.IsUpright(_source[_index]);
            var start = _index;

            while (_index < end && _flow.IsUpright(_source[_index]) == upright)
            {
                _index++;
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
        var spacing = style.LetterSpacing * run.Length;

        return new MeasuredRun(
            run.Source,
            run.Start,
            run.Length,
            flow.IsVertical
                ? run.Advance(font, _measurer, style.FontSize, flow) + spacing
                : _measurer.MeasureWidth(run.Source.Substring(run.Start, run.Length), font) + spacing,
            PinOrientation(flow, run),
            TextChunker.IsWhiteSpace(run.Source, run.Start, run.Length));
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
                line.Fragments[0].Style.TextAlign,
                maxInline - line.Indent,
                line.Used,
                line.Fragments[0].Flow) + line.Indent;

    /// <summary>
    /// Gets the distance the first line of a box is set in from the edge it begins at.
    /// </summary>
    /// <remarks>
    /// A percentage among the indentation is a share of the containing block's inline extent, which is
    /// the block the text sits in rather than the one it makes - the same extent every other length of
    /// the line resolves against.
    /// </remarks>
    private static Double Indentation(Box container, WritingModeInfo flow, InlineContext context)
    {
        var indent = container.Style.TextIndent;

        if (!indent.HasValue)
        {
            return 0;
        }

        var containing = context.Containing.ContainingBlock;

        return indent.Resolve(flow.InlineExtent(containing.Width, containing.Height));
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
    private static Double AlignmentOffset(String textAlign, Double maxInline, Double used, WritingModeInfo flow)
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
            // character nor a string holding it is made afresh to ask for it.
            for (var index = Start; index < Start + Length; index++)
            {
                total += Math.Max(measurer.MeasureAdvance(Source[index], font, flow), fontSize);
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
            Boolean atomic = false)
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

        /// <summary>Gets the annotation of a ruby item, or null when the item carries none.</summary>
        public String? AnnotationText { get; }

        /// <summary>Gets the style the annotation is set in, which the <c>rt</c> element shrank for it.</summary>
        public ComputedStyle AnnotationStyle { get; }

        /// <summary>Gets the box the annotation came from, so its own geometry can be traced back to the tree.</summary>
        public Box? AnnotationBox { get; }

        /// <summary>Gets whether the item is a ruby pair: base text with an annotation over it.</summary>
        public Boolean IsRuby => AnnotationText is not null;

        public static InlineItem CreateText(String text, ComputedStyle style, Box box, InlineAncestor? inline) =>
            new(text, style, box, inline, false, false);

        public static InlineItem CreateBreak(ComputedStyle style, Box box, InlineAncestor? inline) =>
            new(null, style, box, inline, true, false);

        public static InlineItem CreateReplaced(Box box, InlineAncestor? inline) =>
            new(null, box.Style, box, inline, false, true);

        public static InlineItem CreateAtomic(Box box, InlineAncestor? inline) =>
            new(null, box.Style, box, inline, false, false, atomic: true);

        public static InlineItem CreateRuby(
            String text,
            ComputedStyle style,
            Box box,
            String annotationText,
            ComputedStyle annotationStyle,
            Box annotationBox,
            InlineAncestor? inline) =>
            new(text, style, box, inline, false, false, annotationText, annotationStyle, annotationBox);
    }

    private sealed class TextFragment
    {
        public TextFragment(
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
            AtomicInline? atomic = null)
        {
            Source = source;
            Start = start;
            Length = length;
            Width = width;
            Style = style;
            Box = box;
            Flow = flow;
            Inline = inline;
            BaselineShift = Shift(style);
            IsReplaced = isReplaced;
            ContentHeight = contentHeight;
            Ruby = ruby;
            BaseWidth = baseWidth;
            Atomic = atomic;
        }

        /// <summary>
        /// Gets the annotation that goes on the block-start side of this fragment's text, or null when
        /// the fragment is ordinary text.
        /// </summary>
        public RubyAnnotation? Ruby { get; }

        /// <summary>
        /// Gets the extent the base text itself takes along the inline axis. For a ruby fragment the
        /// fragment is as long as the wider of its two parts, and this is what centres the base inside
        /// the room the annotation needed.
        /// </summary>
        public Double BaseWidth { get; }

        /// <summary>
        /// Gets the string the fragment's text is a piece of. A fragment whose pieces came from two
        /// different strings carries a string of its own, which is the one case a merge has to write
        /// out - and it is one per element rather than one per character.
        /// </summary>
        public String Source { get; private set; }

        /// <summary>Gets where in <see cref="Source"/> the fragment's text begins.</summary>
        public Int32 Start { get; private set; }

        /// <summary>Gets how many characters the fragment holds.</summary>
        public Int32 Length { get; set; }

        /// <summary>
        /// Gets the fragment's text as a string, which is what the node the painter draws out of wants.
        /// Asked once per fragment, when the line is placed - not once per chunk of it, as it was while
        /// the chunks were being appended one character at a time.
        /// </summary>
        public String Text => Source.Substring(Start, Length);

        /// <summary>Takes the fragment's text to be the whole of the given string.</summary>
        public void MergeInto(String text)
        {
            Source = text;
            Start = 0;
            Length = text.Length;
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

        public ComputedStyle Style { get; }

        public Box Box { get; }

        /// <summary>
        /// Gets the innermost inline element the run sits inside, or null when it sits in none. The
        /// rest of the chain is reached through <see cref="InlineAncestor.Parent"/>.
        /// </summary>
        public InlineAncestor? Inline { get; }

        /// <summary>Gets the orientation the fragment's glyphs are drawn in, which in a column may
        /// differ from one run to the next even inside a single element.</summary>
        public WritingModeInfo Flow { get; }

        /// <summary>Gets whether this fragment is an atomic replaced box rather than text.</summary>
        public Boolean IsReplaced { get; }

        /// <summary>
        /// Gets the measurements of an inline-block, or null when the fragment is not one. The width of
        /// such a fragment is what the box's own margins add to the size it was measured to, so the size
        /// the line reserved and the size the box is drawn at are not the same number.
        /// </summary>
        public AtomicInline? Atomic { get; }

        /// <summary>Gets the extent of the replaced content along the block axis, which sits on the baseline.</summary>
        public Double ContentHeight { get; }

        /// <summary>
        /// Gets the offset to apply to the run's baseline, which is how <c>&lt;sup&gt;</c> and
        /// <c>&lt;sub&gt;</c> are raised and lowered.
        /// </summary>
        public Double BaselineShift { get; }
    }

    /// <summary>
    /// Gets the offset <c>vertical-align</c> applies to a run's baseline: a superscript is raised above
    /// it and a subscript lowered below, each by a fraction of the font size. Every other alignment is
    /// the line's business rather than the run's and leaves the baseline where it is.
    /// </summary>
    private static Double Shift(ComputedStyle style)
    {
        var alignment = style.VerticalAlign;

        if (alignment.Equals("super", StringComparison.OrdinalIgnoreCase))
        {
            return -0.4 * style.FontSize;
        }

        return alignment.Equals("sub", StringComparison.OrdinalIgnoreCase)
            ? 0.2 * style.FontSize
            : 0;
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
        public LinePlanKey(Box container, Double maxInline, Double indent, Int32 flow, FloatBand? band)
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

        public Double Indent { get; }

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
        private readonly FloatBand? _band;
        private readonly Double _max;

        public LineRoom(FloatBand? band, Double max)
        {
            _band = band;
            _max = max;
            Open();
        }

        /// <summary>Gets how far down the block's lines have reached, which is where the next one stands.</summary>
        public Double Top { get; private set; }

        /// <summary>Gets how far the line that is open now reaches along the inline axis.</summary>
        public Double Room { get; private set; }

        /// <summary>Gets where that line begins inside the block's content box.</summary>
        public Double Inset { get; private set; }

        /// <summary>Gets the room it had to drop through to get there, which the block's stack owes it.</summary>
        public Double Gap { get; private set; }

        /// <summary>Moves on to the next line, at the height the one just built took.</summary>
        public void Next(Double height)
        {
            Top += height;
            Open();
        }

        private void Open()
        {
            Gap = 0;

            if (_band is not { } band)
            {
                Room = _max;
                Inset = 0;
                return;
            }

            Room = band.Room(Top, _max, out var inset);
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

                Room = band.Room(Top, _max, out inset);
                Inset = inset;
            }

            if (Room <= 0)
            {
                Room = _max;
                Inset = 0;
            }
        }
    }

    private sealed class Line
    {
        public List<TextFragment> Fragments { get; } = new();

        public Double Ascent { get; set; }

        public Double Height { get; set; }

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

        /// <summary>
        /// Gets the distance the line is set in from the edge it begins at, which is the indentation
        /// the first line of a box carries. It is not part of <see cref="Used"/>: the runs are aligned
        /// in what the indentation leaves, and the indent is what they are then all moved by.
        /// </summary>
        public Double Indent { get; set; }

        /// <summary>Gets where the line's content starts along the inline axis, after alignment.</summary>
        public Double Offset { get; set; }

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

        public InlineAncestor(Box box, InlineAncestor? parent, Double basis, WritingModeInfo flow, ITextMeasurer measurer)
        {
            Box = box;
            Parent = parent;

            Padding = box.Style.Padding.Resolve(basis);
            Border = BlockLayout.BorderOf(box);

            var metrics = measurer.GetMetrics(box.Style.GetFont());
            Ascent = metrics.Ascent;
            Descent = metrics.Descent;
            BaselineShift = Shift(box.Style);

            // The element's decoration is placed in the frame its line was laid out in, so it is the
            // container's writing mode - and not the element's own - that decides which physical side
            // each logical edge falls on.
            _inlineStartSide = InlineSide(flow, start: true);
            _inlineEndSide = InlineSide(flow, start: false);

            InlineStartInset = Insets(_inlineStartSide);
            InlineEndInset = Insets(_inlineEndSide);
            BlockStartInset = Insets(BlockSide(flow, start: true));
            BlockEndInset = Insets(BlockSide(flow, start: false));
        }

        /// <summary>Gets the element this chain entry stands for.</summary>
        public Box Box { get; }

        /// <summary>Gets the element this one sits inside, or null when it is the outermost.</summary>
        public InlineAncestor? Parent { get; }

        /// <summary>Gets the padding the element resolved to, all four sides.</summary>
        public Thickness Padding { get; }

        /// <summary>Gets the border widths the element resolved to, all four sides.</summary>
        public Thickness Border { get; }

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

        /// <summary>Gets the offset the element's own <c>vertical-align</c> applies to its band.</summary>
        public Double BaselineShift { get; }

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

        private Thickness Sliced(Thickness thickness, Boolean cutStart, Boolean cutEnd) =>
            cutStart ? Clear(thickness, _inlineStartSide)
            : cutEnd ? Clear(thickness, _inlineEndSide)
            : thickness;

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

    private sealed class LineBuilder
    {
        private readonly List<TextFragment> _fragments = new();
        private readonly List<OpenInline> _open = new();
        private readonly HashSet<InlineAncestor>? _carried;
        private readonly ComputedStyle? _fallback;
        private readonly Double _indent;

        public LineBuilder()
        {
        }

        /// <param name="indent">
        /// The distance the line is set in from the edge it begins at. It is the extent the line starts
        /// out having used, so the run that would not have fitted without it is carried to the next
        /// line - which is what an indented first line does to the text on it.
        /// </param>
        public LineBuilder(Double indent)
        {
            _indent = indent;
            Width = indent;
        }

        public LineBuilder(ComputedStyle fallback, HashSet<InlineAncestor>? carried)
        {
            _fallback = fallback;
            _carried = carried;
        }

        /// <summary>
        /// Gets the extent the line takes as it is being built, which is what the wrap test is made
        /// against while the line is still growing. It is only an estimate of what the line ends up
        /// taking - trimming the trailing spaces can still take a run back out - so
        /// <see cref="Build"/> works the extent it hands back out afresh instead of reusing this.
        /// </summary>
        public Double Width { get; private set; }

        public Boolean HasContent => _fragments.Count > 0;

        /// <summary>
        /// Gets the extent the line would already take if the next run sat in the given chain. The
        /// wrap test asks this rather than reading <see cref="Width"/>, so that the padding and border
        /// a run brings with it are counted before the run itself is measured against what is left.
        /// </summary>
        public Double ExtentFor(InlineAncestor? inline) =>
            Width + Advance(_open, inline, _carried, origin: 0, apply: false, step: null);

        /// <summary>Adds an atomic replaced box, which occupies its own used size on the line.</summary>
        public void AddReplaced(Box box, Double width, Double height, InlineAncestor? inline)
        {
            Enter(inline);

            _fragments.Add(new TextFragment(
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
        /// Adds an inline-block, which the line places whole. The width it takes along the line is the
        /// one it was measured to together with its own margins, which the box is then drawn inside of.
        /// </summary>
        public void AddAtomic(Box box, AtomicInline atomic, InlineAncestor? inline)
        {
            Enter(inline);

            _fragments.Add(new TextFragment(
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
            var isSpace = TextChunker.IsWhiteSpace(source, start, length);

            // White space that follows white space across an element's edge collapses into the space
            // already on the line: the pieces come from two nodes, so no node's own collapse saw them
            // together, and the line is where their neighbourhood finally shows up.
            if (isSpace && _fragments.Count > 0 && EndsInCollapsibleSpace(_fragments[^1]))
            {
                return;
            }

            Enter(inline);

            if (_fragments.Count > 0)
            {
                var last = _fragments[^1];

                // Chunks of one text node belong to one run. Keeping them together stops the paint
                // commands - and the decorations drawn for them - from being split per word. Pieces
                // that are drawn differently, a turned word among upright text, are not the same run
                // however they happened to be chunked, and neither are pieces from either side of an
                // inline element's edge.
                if (ReferenceEquals(last.Box, box) && last.Flow.Equals(flow) && ReferenceEquals(last.Inline, inline))
                {
                    // The run grows by the piece added and nothing is copied, because the piece is the
                    // next thing in the same string - which every chunk of one text node is. A run whose
                    // pieces come from two strings is the one case that still has to be written out: a
                    // box with more than one text child, and one merge per element rather than one per
                    // character.
                    if (ReferenceEquals(last.Source, source) && last.Start + last.Length == start)
                    {
                        last.Length += length;
                    }
                    else
                    {
                        last.MergeInto(last.Text + source.Substring(start, length));
                    }

                    last.Width += width;
                    Width += width;
                    return;
                }
            }

            _fragments.Add(new TextFragment(source, start, length, width, style, box, flow, inline));
            Width += width;
        }

        private static Boolean EndsInCollapsibleSpace(TextFragment fragment) =>
            !fragment.IsReplaced &&
            fragment.Atomic is null &&
            fragment.Ruby is null &&
            fragment.Length > 0 &&
            !fragment.Style.IsPreformatted &&
            TextChunker.IsWhiteSpace(fragment.Source, fragment.Start + fragment.Length - 1, 1);

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
            InlineAncestor? inline)
        {
            Enter(inline);

            _fragments.Add(new TextFragment(
                text,
                0,
                text.Length,
                width,
                style,
                box,
                flow,
                inline,
                ruby: ruby,
                baseWidth: baseWidth));

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
        public Line Build(ITextMeasurer measurer, Double maxInline, HashSet<InlineAncestor>? carried)
        {
            TrimTrailingSpaces(measurer);

            var line = new Line { ContinuesFromPrevious = carried };
            line.Fragments.AddRange(_fragments);

            // The extent is worked out afresh from the runs the trimming left, because the padding and
            // border a chain change brings in are part of it and a run taken back out takes them with it.
            var open = new List<OpenInline>();
            var used = 0.0;

            // Gathered from nothing rather than from zero: a face set on a line shorter than itself has a
            // negative descent as well as a negative ascent, and starting at zero would hold every line
            // to at least the height of the tallest font on it.
            // Gathered from nothing rather than from zero: a face set on a line shorter than itself has a
            // negative descent as well as a negative ascent, and starting at zero would hold every line
            // to at least the height of the tallest font on it.
            var ascent = Double.NegativeInfinity;
            var descent = Double.NegativeInfinity;

            foreach (var fragment in line.Fragments)
            {
                used += Advance(open, fragment.Inline, carried, origin: 0, apply: true, step: null);
                used += fragment.Width;

                // A replaced inline rests its bottom margin edge on the baseline, so it contributes
                // its whole height as ascent and nothing as descent.
                if (fragment.IsReplaced)
                {
                    // Its bottom margin edge rests on the baseline, so it reaches nothing below it - a
                    // descent of zero, which is not the same as leaving it out where the line gathers
                    // descents from nothing because a run of text may hang above the baseline.
                    ascent = Math.Max(ascent, fragment.ContentHeight);
                    descent = Math.Max(descent, 0);
                    continue;
                }

                // An inline-block is as tall as its own content and stands on its own baseline, so the
                // line grows by whatever the box reaches above and below that either way - its margins
                // across the line included, since those are part of the room it takes up there.
                if (fragment.Atomic is { } atomic)
                {
                    ascent = Math.Max(ascent, atomic.BlockStartMargin + atomic.Ascent);
                    descent = Math.Max(descent, atomic.BlockEndMargin + (atomic.BlockSize - atomic.Ascent));
                    continue;
                }

                var metrics = measurer.GetMetrics(fragment.Style.GetFont());
                var fragmentAscent = metrics.Ascent;
                var fragmentDescent = metrics.Descent;
                var lineHeight = fragment.Style.LineHeight;

                if (lineHeight.HasValue)
                {
                    // Half the leading, negative included: a face taller than the line height the page
                    // asked for overflows a line box that is that height and no taller.
                    var halfLeading = (lineHeight.Value - (fragmentAscent + fragmentDescent)) / 2;
                    fragmentAscent += halfLeading;
                    fragmentDescent += halfLeading;
                }

                // An annotation stands over its base, on the block-start side, so it lifts the baseline
                // the whole line shares by its own extent instead of making the base taller. One asked
                // for `under` takes its room from the other side of the baseline and leaves it where it
                // would have been.
                if (fragment.Ruby is { } annotation)
                {
                    if (annotation.Under)
                    {
                        fragmentDescent += annotation.Block;
                    }
                    else
                    {
                        fragmentAscent += annotation.Block;
                    }
                }

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

            if (line.Fragments.Count == 0)
            {
                // An empty line still occupies a line box, so an explicit break has height.
                var metrics = _fallback is { } fallback ? measurer.GetMetrics(fallback.GetFont()) : default;
                ascent = _fallback is null ? 0 : metrics.Ascent;
                descent = _fallback is null ? 0 : metrics.Descent;
            }

            line.Ascent = ascent;
            line.Height = ascent + descent;
            line.Used = used;
            line.Indent = _indent;
            line.Offset = Aligned(line, maxInline);

            return line;
        }

        /// <summary>
        /// Takes the line's trailing spaces off, which a line never draws at its end. Only the runs
        /// themselves are touched: the extent is worked out from what is left when the line is built.
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

                if (TextChunker.IsWhiteSpace(last.Source, last.Start, last.Length))
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

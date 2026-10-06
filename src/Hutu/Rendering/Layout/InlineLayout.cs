using Hutu.Rendering.Boxes;
using System;
using System.Collections.Generic;

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
/// Inline elements do not nest geometrically here - their contribution is the style their text
/// inherits - so the tree is flattened into a single stream of runs plus explicit breaks. Line
/// height is the tallest run on the line, expanded by half-leading when the element sets an
/// explicit leading, and the baseline is shared by the whole line.
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

    public InlineLayout(ITextMeasurer measurer) => _measurer = measurer;

    /// <summary>
    /// Measures the extent the inline children need along the block axis at the given inline extent,
    /// without producing any geometry. This is what the measuring phase of a block asks for before it
    /// knows where the lines will sit.
    /// </summary>
    public Double MeasureHeight(Box container, Double contentWidth) =>
        Measure(container, contentWidth, container.Flow).Block;

    /// <summary>
    /// Measures the extents the inline children need when they are given <paramref name="maxInline"/>
    /// to run along. The block extent is what the lines of a row stack into, or the width of the band
    /// of columns a column stacks into.
    /// </summary>
    public InlineContentSize Measure(Box container, Double maxInline, WritingModeInfo flow)
    {
        var items = new List<InlineItem>();
        Collect(container, items, flow);

        if (items.Count == 0)
        {
            return default;
        }

        var block = 0.0;
        var inline = 0.0;

        foreach (var line in BuildLines(items, maxInline, flow))
        {
            if (line.Height > 0)
            {
                block += line.Height;
            }

            inline = Math.Max(inline, line.Used);
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
    public IntrinsicSize MeasureIntrinsic(Box container, Double basis, WritingModeInfo flow)
    {
        var items = new List<InlineItem>();
        Collect(container, items, flow);

        var minContent = 0.0;
        var maxContent = 0.0;
        var unbreakable = 0.0;

        foreach (var item in items)
        {
            if (item.IsBreak)
            {
                minContent = Math.Max(minContent, unbreakable);
                unbreakable = 0;
                continue;
            }

            if (item.IsReplaced)
            {
                var (width, height) = ReplacedSizing.Resolve(item.Box, basis, 0);
                var extent = flow.InlineExtent(width, height);
                minContent = Math.Max(minContent, extent);
                maxContent += extent;
                unbreakable += extent;
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
                continue;
            }

            foreach (var chunk in TextChunker.Chunks(item.Text!))
            {
                var width = Measure(item.Style, chunk, flow);
                maxContent += width;

                if (TextChunker.IsWhiteSpace(chunk))
                {
                    // A collapsible space is a break opportunity: the run before it can be split.
                    minContent = Math.Max(minContent, unbreakable);
                    unbreakable = 0;
                }
                else
                {
                    unbreakable += width;
                }
            }
        }

        minContent = Math.Max(minContent, unbreakable);
        return new IntrinsicSize(minContent, Math.Max(maxContent, minContent));
    }

    /// <summary>
    /// Lays out the inline children of the container inside the given content box, appending line
    /// boxes to it, and returns the extent they occupied along the block axis.
    /// </summary>
    public Double Layout(Box container, LayoutBox parent, Rect contentBox, WritingModeInfo flow)
    {
        var items = new List<InlineItem>();
        Collect(container, items, flow);

        if (items.Count == 0)
        {
            return 0;
        }

        var maxInline = flow.InlineExtent(contentBox.Width, contentBox.Height);
        var lines = BuildLines(items, maxInline, flow);
        var cursor = 0.0;

        foreach (var line in lines)
        {
            if (line.Height <= 0)
            {
                continue;
            }

            // The line box spans the whole inline extent of the content box and only as much of the
            // block one as it needs, so in a row it is the content width and the line's own height,
            // and in a column the content height and the band the text runs down.
            var lineBox = new LayoutBox(container, LayoutKind.Line)
            {
                BorderBox = flow.Map(contentBox, 0, maxInline, cursor, line.Height),
                Baseline = flow.BlockOffsetFromLeft(line.Height, line.Ascent),
                Flow = flow,
            };

            var origin = line.Offset;

            foreach (var fragment in line.Fragments)
            {
                var child = new LayoutBox(
                    fragment.Box,
                    fragment.IsReplaced ? LayoutKind.Image : LayoutKind.Text)
                {
                    Font = fragment.Style.GetFont(),
                    Baseline = flow.BlockOffsetFromLeft(line.Height, line.Ascent + fragment.BaselineShift),
                    Flow = fragment.Flow,
                };

                if (fragment.IsReplaced)
                {
                    // The bottom margin edge of a replaced inline rests on the baseline, so its box
                    // is lifted by its own height rather than filling the line box.
                    child.ImageUrl = fragment.Box.SourceUrl;
                    child.ImageFit = fragment.Style.ObjectFit;
                    child.BorderBox = flow.Map(
                        contentBox,
                        origin,
                        fragment.Width,
                        cursor + line.Ascent - fragment.ContentHeight,
                        fragment.ContentHeight);
                }
                else if (fragment.Ruby is { } annotation)
                {
                    // The annotation takes a strip at one end of the line's block extent and the base
                    // keeps the rest, which is the end away from the reading. Along the inline axis the
                    // two share the room the wider of them asked for, each centred in it, which is what
                    // stops a long reading from hanging off one side.
                    var baseBlock = line.Height - annotation.Block;

                    // An upright character in a column is drawn centred in its box, so a base drawn
                    // that way has to be boxed centred on the band as well - boxed against the far end
                    // it would sit half a reading off the column line its neighbours stand on. A base
                    // that is turned, or set in a line, is drawn from the line's shared baseline
                    // instead, and the end away from the reading is where that baseline puts it.
                    var baseOffset = fragment.Flow.IsVertical && fragment.Flow.Orientation == TextOrientation.Upright
                        ? cursor + (annotation.Block / 2)
                        : cursor + (annotation.Under ? 0 : annotation.Block);

                    child.Text = fragment.Text;
                    child.Baseline = flow.BlockOffsetFromLeft(
                        baseBlock,
                        line.Ascent + fragment.BaselineShift - (annotation.Under ? 0 : annotation.Block));

                    child.BorderBox = flow.Map(
                        contentBox,
                        origin + ((fragment.Width - fragment.BaseWidth) / 2),
                        fragment.BaseWidth,
                        baseOffset,
                        baseBlock);

                    lineBox.Children.Add(child);
                    lineBox.Children.Add(AnnotationBox(
                        annotation,
                        contentBox,
                        flow,
                        origin,
                        fragment.Width,
                        cursor + (annotation.Under ? line.Height - annotation.Block : 0)));

                    origin += fragment.Width;
                    continue;
                }
                else
                {
                    child.Text = fragment.Text;
                    child.BorderBox = flow.Map(contentBox, origin, fragment.Width, cursor, line.Height);
                }

                lineBox.Children.Add(child);
                origin += fragment.Width;
            }

            parent.Children.Add(lineBox);
            cursor += line.Height;
        }

        return cursor;
    }

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

    private static void Collect(Box container, List<InlineItem> items, WritingModeInfo flow)
    {
        foreach (var child in container.Children)
        {
            switch (child.Kind)
            {
                case BoxKind.Text:
                    if (!String.IsNullOrEmpty(child.Text))
                    {
                        AddText(child.Text, child.Style, child, items);
                    }

                    break;

                case BoxKind.LineBreak:
                    items.Add(InlineItem.CreateBreak(child.Style, child));
                    break;

                // A replaced inline box is atomic: unlike other inline boxes it is not flattened into
                // its text runs, it occupies its own used size on the line.
                case BoxKind.Inline when child.IsReplaced:
                    items.Add(InlineItem.CreateReplaced(child));
                    break;

                // A ruby pairs its base text with the annotation that goes over it, so the two are
                // gathered into one item instead of being run together as ordinary text.
                case BoxKind.Ruby:
                    AddRuby(child, items);
                    break;

                case BoxKind.Inline:
                case BoxKind.RubyBase:
                case BoxKind.RubyText:
                    Collect(child, items, flow);
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
    private static void AddRuby(Box ruby, List<InlineItem> items)
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
            items.Add(InlineItem.CreateText(baseText.ToString(), ruby.Style, ruby));
            return;
        }

        items.Add(InlineItem.CreateRuby(
            baseText.ToString(),
            ruby.Style,
            ruby,
            annotationText.ToString(),
            annotationStyle,
            annotationBox));
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
    private static void AddText(String text, ComputedStyle style, Box box, List<InlineItem> items)
    {
        var start = 0;

        while (true)
        {
            var index = text.IndexOf('\n', start);

            if (index < 0)
            {
                if (start < text.Length)
                {
                    items.Add(InlineItem.CreateText(text[start..], style, box));
                }

                return;
            }

            if (index > start)
            {
                items.Add(InlineItem.CreateText(text[start..index], style, box));
            }

            items.Add(InlineItem.CreateBreak(style, box));
            start = index + 1;
        }
    }

    private List<Line> BuildLines(List<InlineItem> items, Double maxInline, WritingModeInfo flow)
    {
        var lines = new List<Line>();
        var builder = new LineBuilder();

        foreach (var item in items)
        {
            if (item.IsBreak)
            {
                lines.Add(builder.Build(_measurer, maxInline));
                builder = new LineBuilder(item.Style);
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

                if (builder.HasContent && maxInline > 0 && builder.Width + inlineSize > maxInline)
                {
                    lines.Add(builder.Build(_measurer, maxInline));
                    builder = new LineBuilder(item.Style);
                }

                builder.AddReplaced(item.Box, inlineSize, blockSize);
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

                if (builder.HasContent && maxInline > 0 && builder.Width + extent > maxInline)
                {
                    lines.Add(builder.Build(_measurer, maxInline));
                    builder = new LineBuilder(item.Style);
                }

                builder.AddRuby(item.Text!, extent, item.Style, item.Box, baseFlow, annotation, baseWidth);
                continue;
            }

            // An inline element may ask for an orientation of its own, so each run is measured and
            // drawn in the flow of the box it came from rather than in the container's.
            var itemFlow = item.Box.Flow;

            foreach (var chunk in TextChunker.Chunks(item.Text!))
            {
                foreach (var run in Runs(chunk, item.Style, itemFlow))
                {
                    if (builder.HasContent && maxInline > 0 && builder.Width + run.Width > maxInline)
                    {
                        lines.Add(builder.Build(_measurer, maxInline));
                        builder = new LineBuilder(item.Style);

                        // The space that triggered the break is consumed by it.
                        if (run.IsWhiteSpace)
                        {
                            continue;
                        }
                    }

                    builder.Add(run.Text, run.Width, item.Style, item.Box, run.Flow);
                }
            }
        }

        if (builder.HasContent)
        {
            lines.Add(builder.Build(_measurer, maxInline));
        }

        return lines;
    }

    /// <summary>Measures a chunk of text the way the writing mode advances it.</summary>
    private Double Measure(ComputedStyle style, String chunk, WritingModeInfo flow) =>
        flow.IsVertical
            ? MeasureColumn(chunk, style.GetFont(), style.FontSize, flow, _measurer)
            : _measurer.MeasureWidth(chunk, style.GetFont());

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
            var halfLeading = Math.Max(0, lineHeight - (ascent + descent)) / 2;
            ascent += halfLeading;
            descent += halfLeading;
        }

        var runs = Runs(text, style, flow);

        return new RubyAnnotation(
            text,
            Measure(style, text, flow),
            ascent + descent,
            ascent,
            item.Box.Style.HasRubyUnder,
            style,
            item.AnnotationBox!,
            runs.Count > 0 ? runs[0].Flow : flow);
    }

    /// <summary>
    /// Measures a chunk the way a column advances it. Upright text advances by its em box rather than
    /// by its horizontal width, so a Latin letter that would leave room to spare in a line still takes
    /// a whole em in a column, and text combined upright is compressed into a single em.
    /// </summary>
    private static Double MeasureColumn(String chunk, FontStyle font, Double fontSize, WritingModeInfo flow, ITextMeasurer measurer)
    {
        var total = 0.0;

        foreach (var run in Orient(chunk, flow))
        {
            total += run.Advance(font, measurer, fontSize, flow);
        }

        return total;
    }

    /// <summary>
    /// Splits a chunk into the pieces a column has to measure and draw separately, which is wherever
    /// the orientation changes and wherever <c>text-combine-upright</c> sets a short run as one box.
    /// </summary>
    private static List<OrientedRun> Orient(String chunk, WritingModeInfo flow)
    {
        var runs = new List<OrientedRun>();
        var index = 0;

        while (index < chunk.Length)
        {
            if (flow.CombinesUpright && TextChunker.IsCombinable(chunk[index]))
            {
                var length = 0;

                while (index + length < chunk.Length && TextChunker.IsCombinable(chunk[index + length]))
                {
                    length++;
                }

                // Only a short run reads as one box. A longer one is ordinary text that happens to be
                // turned, and combining it would squeeze a whole word into a single em.
                if (length <= 4)
                {
                    runs.Add(OrientedRun.Combined(chunk.Substring(index, length)));
                    index += length;
                    continue;
                }
            }

            var upright = flow.IsUpright(chunk[index]);
            var start = index;

            while (index < chunk.Length && flow.IsUpright(chunk[index]) == upright)
            {
                index++;
            }

            runs.Add(upright
                ? OrientedRun.Upright(chunk[start..index])
                : OrientedRun.Rotated(chunk[start..index]));
        }

        return runs;
    }

    /// <summary>
    /// Turns a chunk into the runs that are measured and drawn separately, carrying the advance and
    /// the orientation of each. Horizontal text stays one run, measured exactly as it was before, so
    /// the horizontal path is unchanged down to the numbers.
    /// </summary>
    private List<MeasuredRun> Runs(String chunk, ComputedStyle style, WritingModeInfo flow)
    {
        var runs = new List<MeasuredRun>();

        if (!flow.IsVertical)
        {
            runs.Add(new MeasuredRun(
                chunk,
                _measurer.MeasureWidth(chunk, style.GetFont()),
                flow,
                TextChunker.IsWhiteSpace(chunk)));

            return runs;
        }

        var font = style.GetFont();

        foreach (var run in Orient(chunk, flow))
        {
            runs.Add(new MeasuredRun(
                run.Text,
                run.Advance(font, _measurer, style.FontSize, flow),
                PinOrientation(flow, run),
                TextChunker.IsWhiteSpace(run.Text)));
        }

        return runs;
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
                false);
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

        foreach (var run in Orient(text, flow))
        {
            return PinOrientation(flow, run);
        }

        return flow;
    }

    private static Double AlignmentOffset(String textAlign, Double maxInline, Double used)
    {
        if (maxInline <= used)
        {
            return 0;
        }

        return textAlign.Equals("center", StringComparison.OrdinalIgnoreCase)
            ? (maxInline - used) / 2
            : textAlign.Equals("right", StringComparison.OrdinalIgnoreCase) ||
              textAlign.Equals("end", StringComparison.OrdinalIgnoreCase)
                ? maxInline - used
                : 0;
    }

    /// <summary>A piece of a text node that shares one orientation and is measured and drawn as one.</summary>
    private readonly struct OrientedRun
    {
        private OrientedRun(String text, Boolean upright, Boolean combined)
        {
            Text = text;
            IsUpright = upright;
            IsCombined = combined;
        }

        public String Text { get; }

        public Boolean IsUpright { get; }

        /// <summary>Gets whether the run is set as a single upright box rather than turned.</summary>
        public Boolean IsCombined { get; }

        public static OrientedRun Upright(String text) => new(text, true, false);

        public static OrientedRun Rotated(String text) => new(text, false, false);

        public static OrientedRun Combined(String text) => new(text, true, true);

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

            foreach (var character in Text)
            {
                total += Math.Max(measurer.MeasureAdvance(character.ToString(), font, flow), fontSize);
            }

            return total;
        }
    }

    /// <summary>An oriented run together with the advance it was measured to, ready for the builder.</summary>
    private readonly struct MeasuredRun
    {
        public MeasuredRun(String text, Double width, WritingModeInfo flow, Boolean isWhiteSpace)
        {
            Text = text;
            Width = width;
            Flow = flow;
            IsWhiteSpace = isWhiteSpace;
        }

        public String Text { get; }

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
            Boolean isBreak,
            Boolean isReplaced,
            String? annotationText = null,
            ComputedStyle annotationStyle = default,
            Box? annotationBox = null)
        {
            Text = text;
            Style = style;
            Box = box;
            IsBreak = isBreak;
            IsReplaced = isReplaced;
            AnnotationText = annotationText;
            AnnotationStyle = annotationStyle;
            AnnotationBox = annotationBox;
        }

        public String? Text { get; }

        public ComputedStyle Style { get; }

        public Box Box { get; }

        public Boolean IsBreak { get; }

        public Boolean IsReplaced { get; }

        /// <summary>Gets the annotation of a ruby item, or null when the item carries none.</summary>
        public String? AnnotationText { get; }

        /// <summary>Gets the style the annotation is set in, which the <c>rt</c> element shrank for it.</summary>
        public ComputedStyle AnnotationStyle { get; }

        /// <summary>Gets the box the annotation came from, so its own geometry can be traced back to the tree.</summary>
        public Box? AnnotationBox { get; }

        /// <summary>Gets whether the item is a ruby pair: base text with an annotation over it.</summary>
        public Boolean IsRuby => AnnotationText is not null;

        public static InlineItem CreateText(String text, ComputedStyle style, Box box) =>
            new(text, style, box, false, false);

        public static InlineItem CreateBreak(ComputedStyle style, Box box) =>
            new(null, style, box, true, false);

        public static InlineItem CreateReplaced(Box box) =>
            new(null, box.Style, box, false, true);

        public static InlineItem CreateRuby(
            String text,
            ComputedStyle style,
            Box box,
            String annotationText,
            ComputedStyle annotationStyle,
            Box annotationBox) =>
            new(text, style, box, false, false, annotationText, annotationStyle, annotationBox);
    }

    private sealed class TextFragment
    {
        public TextFragment(
            String text,
            Double width,
            ComputedStyle style,
            Box box,
            WritingModeInfo flow,
            Boolean isReplaced = false,
            Double contentHeight = 0,
            RubyAnnotation? ruby = null,
            Double baseWidth = 0)
        {
            Text = text;
            Width = width;
            Style = style;
            Box = box;
            Flow = flow;
            BaselineShift = ComputeBaselineShift(style);
            IsReplaced = isReplaced;
            ContentHeight = contentHeight;
            Ruby = ruby;
            BaseWidth = baseWidth;
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

        public String Text { get; set; }

        public Double Width { get; set; }

        public ComputedStyle Style { get; }

        public Box Box { get; }

        /// <summary>Gets the orientation the fragment's glyphs are drawn in, which in a column may
        /// differ from one run to the next even inside a single element.</summary>
        public WritingModeInfo Flow { get; }

        /// <summary>Gets whether this fragment is an atomic replaced box rather than text.</summary>
        public Boolean IsReplaced { get; }

        /// <summary>Gets the extent of the replaced content along the block axis, which sits on the baseline.</summary>
        public Double ContentHeight { get; }

        /// <summary>
        /// Gets the offset to apply to the run's baseline, which is how <c>&lt;sup&gt;</c> and
        /// <c>&lt;sub&gt;</c> are raised and lowered.
        /// </summary>
        public Double BaselineShift { get; }

        private static Double ComputeBaselineShift(ComputedStyle style)
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

    private sealed class Line
    {
        public List<TextFragment> Fragments { get; } = new();

        public Double Ascent { get; set; }

        public Double Height { get; set; }

        /// <summary>Gets the extent the line's fragments take along the inline axis, before alignment.</summary>
        public Double Used { get; set; }

        /// <summary>Gets where the line's content starts along the inline axis, after alignment.</summary>
        public Double Offset { get; set; }
    }

    private sealed class LineBuilder
    {
        private readonly List<TextFragment> _fragments = new();
        private readonly ComputedStyle? _fallback;

        public LineBuilder()
        {
        }

        public LineBuilder(ComputedStyle fallback) => _fallback = fallback;

        public Double Width { get; private set; }

        public Boolean HasContent => _fragments.Count > 0;

        /// <summary>Adds an atomic replaced box, which occupies its own used size on the line.</summary>
        public void AddReplaced(Box box, Double width, Double height)
        {
            _fragments.Add(new TextFragment(
                String.Empty,
                width,
                box.Style,
                box,
                box.Flow,
                isReplaced: true,
                contentHeight: height));

            Width += width;
        }

        public void Add(String text, Double width, ComputedStyle style, Box box, WritingModeInfo flow)
        {
            if (_fragments.Count > 0)
            {
                var last = _fragments[^1];

                // Chunks of one text node belong to one run. Keeping them together stops the paint
                // commands - and the decorations drawn for them - from being split per word. Pieces
                // that are drawn differently, a turned word among upright text, are not the same run
                // however they happened to be chunked.
                if (ReferenceEquals(last.Box, box) && last.Flow.Equals(flow))
                {
                    last.Text += text;
                    last.Width += width;
                    Width += width;
                    return;
                }
            }

            _fragments.Add(new TextFragment(text, width, style, box, flow));
            Width += width;
        }

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
            Double baseWidth)
        {
            _fragments.Add(new TextFragment(
                text,
                width,
                style,
                box,
                flow,
                ruby: ruby,
                baseWidth: baseWidth));

            Width += width;
        }

        public Line Build(ITextMeasurer measurer, Double maxInline)
        {
            TrimTrailingSpaces(measurer);

            var line = new Line();
            line.Fragments.AddRange(_fragments);

            var used = 0.0;
            var ascent = 0.0;
            var descent = 0.0;

            foreach (var fragment in line.Fragments)
            {
                used += fragment.Width;

                // A replaced inline rests its bottom margin edge on the baseline, so it contributes
                // its whole height as ascent and nothing as descent.
                if (fragment.IsReplaced)
                {
                    ascent = Math.Max(ascent, fragment.ContentHeight);
                    continue;
                }

                var metrics = measurer.GetMetrics(fragment.Style.GetFont());
                var fragmentAscent = metrics.Ascent;
                var fragmentDescent = metrics.Descent;
                var lineHeight = fragment.Style.LineHeight;

                if (lineHeight.HasValue)
                {
                    var halfLeading = Math.Max(0, lineHeight.Value - (fragmentAscent + fragmentDescent)) / 2;
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

            if (line.Fragments.Count == 0 && _fallback is { } fallback)
            {
                // An empty line still occupies a line box, so an explicit break has height.
                var metrics = measurer.GetMetrics(fallback.GetFont());
                ascent = metrics.Ascent;
                descent = metrics.Descent;
            }

            line.Ascent = ascent;
            line.Height = ascent + descent;
            line.Used = used;
            line.Offset = line.Fragments.Count > 0
                ? AlignmentOffset(line.Fragments[0].Style.TextAlign, maxInline, used)
                : 0;

            return line;
        }

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

                if (TextChunker.IsWhiteSpace(last.Text))
                {
                    Width -= last.Width;
                    _fragments.RemoveAt(_fragments.Count - 1);
                    continue;
                }

                var trimmed = last.Text.TrimEnd(' ');

                if (trimmed.Length == last.Text.Length)
                {
                    break;
                }

                Width -= last.Width;

                if (trimmed.Length == 0)
                {
                    _fragments.RemoveAt(_fragments.Count - 1);
                    continue;
                }

                last.Text = trimmed;
                last.Width = last.Flow.IsVertical
                    ? MeasureColumn(trimmed, last.Style.GetFont(), last.Style.FontSize, last.Flow, measurer)
                    : measurer.MeasureWidth(trimmed, last.Style.GetFont());
                Width += last.Width;
                break;
            }
        }
    }
}

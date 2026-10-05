using Hutu.Rendering.Boxes;
using System;
using System.Collections.Generic;

namespace Hutu.Rendering.Layout;

/// <summary>
/// Lays out an inline formatting context: it flattens the inline content of a block into text runs,
/// breaks them into line boxes and positions every run.
/// </summary>
/// <remarks>
/// Inline elements do not nest geometrically here - their contribution is the style their text
/// inherits - so the tree is flattened into a single stream of runs plus explicit breaks. Line
/// height is the tallest run on the line, expanded by half-leading when the element sets an
/// explicit leading, and the baseline is shared by the whole line.
/// </remarks>
public sealed class InlineLayout
{
    private readonly ITextMeasurer _measurer;

    public InlineLayout(ITextMeasurer measurer) => _measurer = measurer;

    /// <summary>
    /// Lays out the inline children of the container, appending line boxes to it, and returns the
    /// used height.
    /// </summary>
    public Double Layout(Box container, LayoutBox parent, Double contentX, Double contentY, Double contentWidth)
    {
        var items = new List<InlineItem>();
        Collect(container, items);

        if (items.Count == 0)
        {
            return 0;
        }

        var lines = BuildLines(items, contentWidth);
        var cursorY = contentY;

        foreach (var line in lines)
        {
            if (line.Height <= 0)
            {
                continue;
            }

            var lineBox = new LayoutBox(container, LayoutKind.Line)
            {
                BorderBox = new Rect(contentX, cursorY, contentWidth, line.Height),
                Baseline = line.Ascent,
            };

            var originX = contentX + line.Offset;

            foreach (var fragment in line.Fragments)
            {
                var child = new LayoutBox(
                    fragment.Box,
                    fragment.IsReplaced ? LayoutKind.Image : LayoutKind.Text)
                {
                    Font = fragment.Style.GetFont(),
                    Baseline = line.Ascent + fragment.BaselineShift,
                };

                if (fragment.IsReplaced)
                {
                    // The bottom margin edge of a replaced inline rests on the baseline, so its box
                    // is lifted by its own height rather than filling the line box.
                    child.ImageUrl = fragment.Box.SourceUrl;
                    child.ImageFit = fragment.Style.ObjectFit;
                    child.CornerRadius = fragment.Style.CornerRadius;
                    child.BorderBox = new Rect(
                        originX,
                        cursorY + line.Ascent - fragment.ContentHeight,
                        fragment.Width,
                        fragment.ContentHeight);
                }
                else
                {
                    child.Text = fragment.Text;
                    child.BorderBox = new Rect(originX, cursorY, fragment.Width, line.Height);
                }

                lineBox.Children.Add(child);
                originX += fragment.Width;
            }

            parent.Children.Add(lineBox);
            cursorY += line.Height;
        }

        return cursorY - contentY;
    }

    private static void Collect(Box container, List<InlineItem> items)
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

                case BoxKind.Inline:
                    Collect(child, items);
                    break;
            }
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

    private List<Line> BuildLines(List<InlineItem> items, Double maxWidth)
    {
        var lines = new List<Line>();
        var builder = new LineBuilder();

        foreach (var item in items)
        {
            if (item.IsBreak)
            {
                lines.Add(builder.Build(_measurer, maxWidth));
                builder = new LineBuilder(item.Style);
                continue;
            }

            if (item.IsReplaced)
            {
                var (width, height) = ReplacedSizing.Resolve(item.Box);

                if (builder.HasContent && maxWidth > 0 && builder.Width + width > maxWidth)
                {
                    lines.Add(builder.Build(_measurer, maxWidth));
                    builder = new LineBuilder(item.Style);
                }

                builder.AddReplaced(item.Box, width, height);
                continue;
            }

            foreach (var chunk in TextChunker.Chunks(item.Text!))
            {
                var width = _measurer.MeasureWidth(chunk, item.Style.GetFont());

                if (builder.HasContent && maxWidth > 0 && builder.Width + width > maxWidth)
                {
                    lines.Add(builder.Build(_measurer, maxWidth));
                    builder = new LineBuilder(item.Style);

                    // The space that triggered the break is consumed by it.
                    if (TextChunker.IsWhiteSpace(chunk))
                    {
                        continue;
                    }
                }

                builder.Add(chunk, width, item.Style, item.Box);
            }
        }

        if (builder.HasContent)
        {
            lines.Add(builder.Build(_measurer, maxWidth));
        }

        return lines;
    }

    private static Double AlignmentOffset(String textAlign, Double maxWidth, Double used)
    {
        if (maxWidth <= used)
        {
            return 0;
        }

        return textAlign.Equals("center", StringComparison.OrdinalIgnoreCase)
            ? (maxWidth - used) / 2
            : textAlign.Equals("right", StringComparison.OrdinalIgnoreCase) ||
              textAlign.Equals("end", StringComparison.OrdinalIgnoreCase)
                ? maxWidth - used
                : 0;
    }

    private readonly struct InlineItem
    {
        private InlineItem(String? text, ComputedStyle style, Box box, Boolean isBreak, Boolean isReplaced)
        {
            Text = text;
            Style = style;
            Box = box;
            IsBreak = isBreak;
            IsReplaced = isReplaced;
        }

        public String? Text { get; }

        public ComputedStyle Style { get; }

        public Box Box { get; }

        public Boolean IsBreak { get; }

        public Boolean IsReplaced { get; }

        public static InlineItem CreateText(String text, ComputedStyle style, Box box) =>
            new(text, style, box, false, false);

        public static InlineItem CreateBreak(ComputedStyle style, Box box) =>
            new(null, style, box, true, false);

        public static InlineItem CreateReplaced(Box box) =>
            new(null, box.Style, box, false, true);
    }

    private sealed class TextFragment
    {
        public TextFragment(
            String text,
            Double width,
            ComputedStyle style,
            Box box,
            Boolean isReplaced = false,
            Double contentHeight = 0)
        {
            Text = text;
            Width = width;
            Style = style;
            Box = box;
            BaselineShift = ComputeBaselineShift(style);
            IsReplaced = isReplaced;
            ContentHeight = contentHeight;
        }

        public String Text { get; set; }

        public Double Width { get; set; }

        public ComputedStyle Style { get; }

        public Box Box { get; }

        /// <summary>Gets whether this fragment is an atomic replaced box rather than text.</summary>
        public Boolean IsReplaced { get; }

        /// <summary>Gets the height of the replaced content, which sits on the baseline.</summary>
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

    private sealed class Line
    {
        public List<TextFragment> Fragments { get; } = new();

        public Double Ascent { get; set; }

        public Double Height { get; set; }

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
                isReplaced: true,
                contentHeight: height));

            Width += width;
        }

        public void Add(String text, Double width, ComputedStyle style, Box box)
        {
            if (_fragments.Count > 0)
            {
                var last = _fragments[^1];

                // Chunks of one text node belong to one run. Keeping them together stops the paint
                // commands - and the decorations drawn for them - from being split per word.
                if (ReferenceEquals(last.Box, box))
                {
                    last.Text += text;
                    last.Width += width;
                    Width += width;
                    return;
                }
            }

            _fragments.Add(new TextFragment(text, width, style, box));
            Width += width;
        }

        public Line Build(ITextMeasurer measurer, Double maxWidth)
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
            line.Offset = line.Fragments.Count > 0
                ? AlignmentOffset(line.Fragments[0].Style.TextAlign, maxWidth, used)
                : 0;

            return line;
        }

        private void TrimTrailingSpaces(ITextMeasurer measurer)
        {
            while (_fragments.Count > 0)
            {
                var last = _fragments[^1];

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
                last.Width = measurer.MeasureWidth(trimmed, last.Style.GetFont());
                Width += last.Width;
                break;
            }
        }
    }
}

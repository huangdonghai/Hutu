using Hutu.Rendering.Layout;
using SkiaSharp;
using System;
using System.Collections.Concurrent;

namespace Hutu.Skia;

/// <summary>
/// Keeps the drawn form of text: a positioned <see cref="SKTextBlob"/> per text, style and spacing,
/// built once out of the decomposition the measurer answers, and drawn as one call after that.
/// </summary>
/// <remarks>
/// Building a blob is where drawing text spends its per-frame time - the glyph buffers, the
/// positions and the native object behind them are made again for text a page draws every frame.
/// The blobs here carry their glyphs relative to their own origin, so the one object answers every
/// frame and every scroll: the caller draws it at the run's position and the glyphs land where the
/// layout measured them. A column's blob is keyed by the width of the column as well, because an
/// upright character is centred across it.
/// </remarks>
public sealed class SkiaTextBlobCache : IDisposable
{
    private readonly SkiaTextMeasurer _measurer;
    private readonly ConcurrentDictionary<TextBlobKey, SKTextBlob?> _runs = new();
    private readonly ConcurrentDictionary<UprightBlobKey, SKTextBlob?> _columns = new();

    // A blob is native memory, so the loser of the usual get-or-add race would leak what it built:
    // the build is kept under a lock, which costs nothing that matters, since it runs once per key.
    private readonly Object _gate = new();

    public SkiaTextBlobCache(SkiaTextMeasurer measurer) => _measurer = measurer;

    /// <summary>
    /// Gets the blob a run of horizontal text is drawn as, with its glyphs positioned relative to the
    /// run's own origin, so it is drawn at wherever the run starts.
    /// </summary>
    public SKTextBlob? GetRunBlob(FontStyle style, String text, Double letterSpacing)
    {
        var key = new TextBlobKey(style, text, letterSpacing);

        lock (_gate)
        {
            if (_runs.TryGetValue(key, out var cached))
            {
                return cached;
            }

            var blob = BuildRunBlob(style, text, letterSpacing);
            _runs[key] = blob;
            return blob;
        }
    }

    /// <summary>
    /// Gets the blob a column of upright characters is drawn as, with its glyphs positioned relative
    /// to the column's own origin, so it is drawn at wherever the column starts.
    /// </summary>
    /// <remarks>
    /// The column width is part of what the blob is made of - each character is centred across it -
    /// which is why it stands in the key rather than being left to the caller to translate.
    /// </remarks>
    public SKTextBlob? GetColumnBlob(FontStyle style, String text, Double letterSpacing, Double columnWidth)
    {
        var key = new UprightBlobKey(style, text, letterSpacing, columnWidth);

        lock (_gate)
        {
            if (_columns.TryGetValue(key, out var cached))
            {
                return cached;
            }

            var blob = BuildColumnBlob(style, text, letterSpacing, columnWidth);
            _columns[key] = blob;
            return blob;
        }
    }

    public void Dispose()
    {
        foreach (var blob in _runs.Values)
        {
            blob?.Dispose();
        }

        foreach (var blob in _columns.Values)
        {
            blob?.Dispose();
        }

        _runs.Clear();
        _columns.Clear();
    }

    /// <summary>
    /// Builds a run's blob out of the runs of the decomposition: each stretch contributes one
    /// positioned run of the glyphs its font set it in, starting where the stretches before it ended.
    /// </summary>
    /// <remarks>
    /// The letter spacing is folded into each glyph's position and into the distance the next
    /// stretch starts at, by the characters the layout counted, so the drawn text occupies exactly
    /// the room the layout measured it into.
    /// </remarks>
    private SKTextBlob? BuildRunBlob(FontStyle style, String text, Double letterSpacing)
    {
        using var builder = new SKTextBlobBuilder();
        var pen = 0f;

        foreach (var run in _measurer.Decompose(text, style))
        {
            if (run.Shaped.Succeeded)
            {
                AddShaped(builder, run, pen, letterSpacing);
            }
            else
            {
                AddMapped(builder, run, pen, letterSpacing);
            }

            pen += (Single)(run.Advance + (letterSpacing * run.Text.Length));
        }

        return builder.Build();
    }

    private static void AddShaped(SKTextBlobBuilder builder, SkiaTextRun run, Single pen, Double letterSpacing)
    {
        var glyphs = run.Shaped.Glyphs;
        var points = run.Shaped.Points;
        var count = glyphs.Length;

        if (count == 0 || points.Length < count)
        {
            return;
        }

        var positions = new SKPoint[count];

        for (var index = 0; index < count; index++)
        {
            positions[index] = new SKPoint(
                pen + points[index].X + (Single)(letterSpacing * index),
                points[index].Y);
        }

        var placed = builder.AllocatePositionedRun(run.Font, count);
        placed.SetGlyphs(glyphs);
        placed.SetPositions(positions);
    }

    /// <summary>
    /// Adds a run the shaper could not handle as the font's own mapping, one glyph per character,
    /// which is the way it was drawn before shaping answered for it.
    /// </summary>
    private static void AddMapped(SKTextBlobBuilder builder, SkiaTextRun run, Single pen, Double letterSpacing)
    {
        if (run.Text.Length == 0)
        {
            return;
        }

        var glyphs = run.Font.GetGlyphs(run.Text);
        var positions = run.Font.GetGlyphPositions(run.Text, new SKPoint(pen, 0));

        for (var index = 0; index < positions.Length; index++)
        {
            positions[index].X += (Single)(letterSpacing * index);
        }

        var placed = builder.AllocatePositionedRun(run.Font, glyphs.Length);
        placed.SetGlyphs(glyphs);
        placed.SetPositions(positions);
    }

    /// <summary>
    /// Builds a column's blob out of the cells of the decomposition: one glyph per character, at the
    /// place within its cell the layout's steps and the font's own baseline ask for.
    /// </summary>
    /// <remarks>
    /// The step is the one the layout gave the cell - the character's advance raised to the em box
    /// and the spacing added after it - and where the baseline goes inside that step is the font's
    /// to say, falling back to the middle of the em box for a font that will not. A character with
    /// no glyph of its own to draw is set by its font's own mapping of the text it stands for.
    /// </remarks>
    private SKTextBlob? BuildColumnBlob(FontStyle style, String text, Double letterSpacing, Double columnWidth)
    {
        var metrics = _measurer.GetMetrics(style);
        var emBox = metrics.Ascent + metrics.Descent;
        var cells = _measurer.DecomposeUpright(text, style);

        using var builder = new SKTextBlobBuilder();
        var inline = 0.0;

        foreach (var cell in cells)
        {
            var step = Math.Max(cell.Advance, style.Size) + letterSpacing;
            var baseline = cell.BaselineOffset is { } offset
                ? (Single)(inline + offset)
                : (Single)(inline + ((step - emBox) / 2) + metrics.Ascent);
            var centre = (Single)((columnWidth - cell.CentreWidth) / 2);

            if (cell.DrawByGlyph)
            {
                AddGlyph(builder, cell.Font, cell.Glyph, centre, baseline);
            }
            else
            {
                AddMappedText(builder, cell.Font, cell.Text, centre, baseline);
            }

            inline += step;
        }

        return builder.Build();
    }

    private static void AddGlyph(SKTextBlobBuilder builder, SKFont font, UInt16 glyph, Single x, Single y)
    {
        Span<UInt16> glyphs = stackalloc UInt16[1];
        glyphs[0] = glyph;

        Span<SKPoint> positions = stackalloc SKPoint[1];
        positions[0] = new SKPoint(x, y);

        var placed = builder.AllocatePositionedRun(font, 1);
        placed.SetGlyphs(glyphs);
        placed.SetPositions(positions);
    }

    private static void AddMappedText(SKTextBlobBuilder builder, SKFont font, String text, Single x, Single y)
    {
        if (text.Length == 0)
        {
            return;
        }

        var glyphs = font.GetGlyphs(text);
        var positions = font.GetGlyphPositions(text, new SKPoint(x, y));

        var placed = builder.AllocatePositionedRun(font, glyphs.Length);
        placed.SetGlyphs(glyphs);
        placed.SetPositions(positions);
    }

    /// <summary>The style, text and spacing a run's blob was built for, which is what makes it reusable.</summary>
    private readonly struct TextBlobKey : IEquatable<TextBlobKey>
    {
        private readonly String _text;

        public TextBlobKey(FontStyle style, String text, Double spacing)
        {
            Style = style;
            _text = text;
            Spacing = spacing;
        }

        public FontStyle Style { get; }

        public Double Spacing { get; }

        public Boolean Equals(TextBlobKey other) =>
            Style.Equals(other.Style) &&
            Spacing.Equals(other.Spacing) &&
            String.Equals(_text, other._text, StringComparison.Ordinal);

        public override Boolean Equals(Object? obj) => obj is TextBlobKey other && Equals(other);

        public override Int32 GetHashCode()
        {
            unchecked
            {
                var hash = Style.GetHashCode();
                hash = (hash * 397) ^ Spacing.GetHashCode();
                return (hash * 397) ^ StringComparer.Ordinal.GetHashCode(_text);
            }
        }
    }

    /// <summary>The style, text, spacing and column width a column's blob was built for.</summary>
    private readonly struct UprightBlobKey : IEquatable<UprightBlobKey>
    {
        private readonly String _text;

        public UprightBlobKey(FontStyle style, String text, Double spacing, Double columnWidth)
        {
            Style = style;
            _text = text;
            Spacing = spacing;
            ColumnWidth = columnWidth;
        }

        public FontStyle Style { get; }

        public Double Spacing { get; }

        public Double ColumnWidth { get; }

        public Boolean Equals(UprightBlobKey other) =>
            Style.Equals(other.Style) &&
            Spacing.Equals(other.Spacing) &&
            ColumnWidth.Equals(other.ColumnWidth) &&
            String.Equals(_text, other._text, StringComparison.Ordinal);

        public override Boolean Equals(Object? obj) => obj is UprightBlobKey other && Equals(other);

        public override Int32 GetHashCode()
        {
            unchecked
            {
                var hash = Style.GetHashCode();
                hash = (hash * 397) ^ Spacing.GetHashCode();
                hash = (hash * 397) ^ ColumnWidth.GetHashCode();
                return (hash * 397) ^ StringComparer.Ordinal.GetHashCode(_text);
            }
        }
    }
}

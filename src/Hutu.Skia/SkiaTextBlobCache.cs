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
/// layout measured them. A column's blob centres each upright character across the em box, so it
/// answers at whatever width the column is drawn at as well.
/// </remarks>
internal sealed class SkiaTextBlobCache : IDisposable
{
    private readonly SkiaTextMeasurer _measurer;
    private readonly ConcurrentDictionary<TextBlobKey, SKTextBlob?> _blobs = new();

    // A blob is native memory, so the loser of the usual get-or-add race would leak what it built:
    // the build is kept under a lock, which costs nothing that matters, since it runs once per key.
    private readonly Object _gate = new();

    public SkiaTextBlobCache(SkiaTextMeasurer measurer) => _measurer = measurer;

    /// <summary>
    /// Gets the blob a run of horizontal text is drawn as, with its glyphs positioned relative to the
    /// run's own origin, so it is drawn at wherever the run starts.
    /// </summary>
    /// <summary>
    /// Gets the blob a run of horizontal text is drawn as, with its glyphs positioned relative to the
    /// run's own origin, so it is drawn at wherever the run starts.
    /// </summary>
    /// <param name="justification">
    /// The room a justified line owes after every place between the run's words, and how many of those
    /// places the text holds: the glyphs set after each of them stand that much further along, which is
    /// what makes the drawn text fill the room it was measured into rather than bunched at one end of it.
    /// </param>
    /// <param name="wordSpacing">
    /// The room <c>word-spacing</c> adds after each of the run's word separators - the same count the
    /// layout charged the run for, so the glyphs land where the run's width says they stand.
    /// </param>
    public SKTextBlob? GetRunBlob(
        FontStyle style,
        String text,
        Double letterSpacing,
        Double justification = 0,
        Int32 justifies = 0,
        String expand = "auto",
        Double wordSpacing = 0) =>
        GetBlob(BlobKind.Run, style, text, letterSpacing, justification, justifies, expand, wordSpacing);

    /// <summary>
    /// Gets the blob a column of upright characters is drawn as, with its glyphs positioned relative
    /// to the column's own origin, so it is drawn at wherever the column starts.
    /// </summary>
    /// <remarks>
    /// Each character is centred across the em box rather than across the column's own width, so the
    /// one blob answers a column of any width: the caller draws it where the column's width puts it.
    /// </remarks>
    /// <param name="justification">
    /// The room a justified line owes after each of this column's places, which every cell after that
    /// place carries down the column with it - the same debt the run along a line is drawn with, paid
    /// here between the upright cells rather than between the glyphs of one run.
    /// </param>
    /// <param name="justifies">How many places this column holds shares for.</param>
    /// <param name="expand">Which boundaries of the text those places are, as text-justify says.</param>
    public SKTextBlob? GetColumnBlob(
        FontStyle style,
        String text,
        Double letterSpacing,
        Double justification = 0,
        Int32 justifies = 0,
        String expand = "auto",
        Double wordSpacing = 0) =>
        GetBlob(BlobKind.Column, style, text, letterSpacing, justification, justifies, expand, wordSpacing);

    private SKTextBlob? GetBlob(
        BlobKind kind,
        FontStyle style,
        String text,
        Double letterSpacing,
        Double justification,
        Int32 justifies,
        String expand,
        Double wordSpacing)
    {
        var key = new TextBlobKey(
            kind, style, text, letterSpacing, justification, justifies, expand, wordSpacing);

        lock (_gate)
        {
            if (_blobs.TryGetValue(key, out var cached))
            {
                return cached;
            }

            var blob = kind == BlobKind.Run
                ? BuildRunBlob(style, text, letterSpacing, justification, justifies, expand, wordSpacing)
                : BuildColumnBlob(style, text, letterSpacing, justification, justifies, expand, wordSpacing);
            _blobs[key] = blob;
            return blob;
        }
    }

    /// <summary>
    /// Empties the cache, so the next draw builds its blobs out of the glyphs of the faces that are
    /// here now rather than the ones a face still on the wire was standing in for.
    /// </summary>
    /// <remarks>
    /// What is dropped is not disposed: a paint already underway may still be drawing one of the
    /// blobs, and each is native memory the next build lays down again anyway. The gate is taken so a
    /// build racing the clear cannot put the blob it made back into the emptied cache.
    /// </remarks>
    public void Clear()
    {
        lock (_gate)
        {
            _blobs.Clear();
        }
    }

    public void Dispose()
    {
        foreach (var blob in _blobs.Values)
        {
            blob?.Dispose();
        }

        _blobs.Clear();
    }

    /// <summary>
    /// Builds a run's blob out of the runs of the decomposition: each stretch contributes one
    /// positioned run of the glyphs its font set it in, starting where the stretches before it ended.
    /// </summary>
    /// <remarks>
    /// The letter spacing is folded into each glyph's position and into the distance the next stretch
    /// starts at by the characters the stretch is made of - the same count the layout charged the line
    /// for, which is what makes the drawn text occupy exactly the room it was measured into. Where a
    /// glyph is set, though, is the shaper's knowledge: two glyphs welded out of one character are
    /// still standing in it, and only the buffer says which glyphs came from where.
    /// </remarks>
    private SKTextBlob? BuildRunBlob(
        FontStyle style,
        String text,
        Double letterSpacing,
        Double justification,
        Int32 justifies,
        String expand,
        Double wordSpacing)
    {
        using var builder = new SKTextBlobBuilder();
        var pen = 0f;

        // The places the line still owes a share to, paid out in the order the text runs: a stretch
        // takes as many as it holds and the rest go to the stretches after it.
        var owing = justifies;
        var points = Places(text, justifies, justification, expand);
        var separators = wordSpacing == 0
            ? Array.Empty<Int32>()
            : TextClusters.WordSeparatorPlaces(text);
        var offset = 0;

        foreach (var run in _measurer.Decompose(text, style))
        {
            var characters = letterSpacing == 0
                ? 0
                : TextClusters.Count(run.Text, 0, run.Text.Length);

            // The places were found in the whole run's text, so each stretch is given the ones that fall
            // inside it, told again from where the stretch itself begins.
            var ends = Slice(points, offset, run.Text.Length);
            var places = Math.Min(ends.Length, owing);
            owing -= places;
            var words = Slice(separators, offset, run.Text.Length);

            var clusters = (letterSpacing != 0 || justification != 0 || wordSpacing != 0) && run.Shaped.Succeeded
                ? _measurer.Shaper.ShapeClusters(run.Font, run.Text)
                : null;

            if (clusters is { Length: > 0 } && clusters.Length != run.Shaped.Glyphs.Length)
            {
                // A shaped answer and a clustered answer that disagree are not the same run twice
                // told: the spacing then falls by the glyph, as it did before it was asked where.
                clusters = null;
            }

            if (run.Shaped.Succeeded)
            {
                AddShaped(builder, run, pen, letterSpacing, justification, places, ends, wordSpacing, words, clusters);
            }
            else
            {
                AddMapped(builder, run, pen, letterSpacing, wordSpacing, words);
            }

            offset += run.Text.Length;

            pen += (Single)(run.Advance + (letterSpacing * characters) + (justification * places) +
                (wordSpacing * words.Length));
        }

        return builder.Build();
    }

    /// <summary>
    /// Gets the places of the whole run that fall inside one stretch of it, named from where the stretch
    /// begins - which is the frame the glyphs of that stretch are set out in.
    /// </summary>
    private static Int32[] Slice(Int32[] points, Int32 start, Int32 length)
    {
        if (points.Length == 0)
        {
            return points;
        }

        var inside = new List<Int32>(points.Length);

        foreach (var point in points)
        {
            if (point >= start && point < start + length)
            {
                inside.Add(point - start);
            }
        }

        return inside.ToArray();
    }

    /// <summary>
    /// Gets where the room of a justified run goes, as the index within it after which each share is
    /// paid: the end of every run of white space and, where the text expands between its characters,
    /// every seam the style asks for between them.
    /// </summary>
    private static Int32[] Places(String text, Int32 justifies, Double justification, String expand)
    {
        if (justification == 0)
        {
            return Array.Empty<Int32>();
        }

        var points = TextClusters.ExpansionPoints(text, 0, text.Length, expand);

        // The seam a run cannot see in its own text - the one between its last ideograph and the first
        // of what the line goes on with, which the layout counted for this run - is paid at its end,
        // where it moves none of this run's glyphs and only carries the pen on.
        return points.Length < justifies ? [.. points, text.Length - 1] : points;
    }

    private static void AddShaped(
        SKTextBlobBuilder builder,
        SkiaTextRun run,
        Single pen,
        Double letterSpacing,
        Double justification,
        Int32 places,
        Int32[] ends,
        Double wordSpacing,
        Int32[] words,
        Int32[]? clusters)
    {
        var glyphs = run.Shaped.Glyphs;
        var points = run.Shaped.Points;
        var count = glyphs.Length;

        if (count == 0 || points.Length < count)
        {
            return;
        }

        var positions = new SKPoint[count];
        var crossed = 0;
        var spread = 0;
        var reached = 0;
        var paid = 0;

        // The pen walks the text in the order the glyphs are drawn in, which is the order the writing
        // runs: a right-to-left run's characters count backwards from where they stand in the string, so
        // the places it has left behind are the ones further along the string rather than earlier.
        var backwards = clusters is { Length: > 1 } && clusters[0] > clusters[^1];

        for (var index = 0; index < count; index++)
        {
            // A place between two words is paid for once the pen has gone past it, and a glyph made
            // from the same character as the one before it has crossed nothing either way.
            while (clusters is not null &&
                   index < clusters.Length &&
                   spread < places &&
                   reached < ends.Length &&
                   (backwards
                        ? ends[reached] > clusters[index]
                        : ends[reached] < clusters[index]))
            {
                reached++;
                spread++;
            }

            // The room of word-spacing is owed at every separator rather than shared out among some of
            // them, so nothing caps it: the pen pays each one as it walks past.
            while (clusters is not null &&
                   index < clusters.Length &&
                   paid < words.Length &&
                   (backwards
                        ? words[paid] > clusters[index]
                        : words[paid] < clusters[index]))
            {
                paid++;
            }

            if (index > 0 && (clusters is null || clusters[index] != clusters[index - 1]))
            {
                crossed++;
            }

            positions[index] = new SKPoint(
                pen + points[index].X +
                (Single)((letterSpacing * crossed) + (justification * spread) + (wordSpacing * paid)),
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
    private static void AddMapped(
        SKTextBlobBuilder builder,
        SkiaTextRun run,
        Single pen,
        Double letterSpacing,
        Double wordSpacing,
        Int32[] words)
    {
        if (run.Text.Length == 0)
        {
            return;
        }

        var glyphs = run.Font.GetGlyphs(run.Text);
        var positions = run.Font.GetGlyphPositions(run.Text, new SKPoint(pen, 0));
        var crossed = 0;

        for (var index = 0; index < positions.Length; index++)
        {
            while (crossed < words.Length && words[crossed] < index)
            {
                crossed++;
            }

            // One glyph per character is the whole of what this path knows, so the separators it has
            // left behind are counted by the same index the spacing is.
            positions[index].X += (Single)((letterSpacing * index) + (wordSpacing * crossed));
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
    private SKTextBlob? BuildColumnBlob(
        FontStyle style,
        String text,
        Double letterSpacing,
        Double justification,
        Int32 justifies,
        String expand,
        Double wordSpacing)
    {
        var metrics = _measurer.GetMetrics(style);
        var emBox = metrics.Ascent + metrics.Descent;
        var cells = _measurer.DecomposeUpright(text, style);

        // The room goes after each place in the text the cells were cut from, which is read by the
        // character each cell stands for rather than by the glyph it draws: the form a font substitutes
        // for a character down a column need not be the character the document wrote.
        var points = Places(text, justifies, justification, expand);
        var separators = wordSpacing == 0
            ? Array.Empty<Int32>()
            : TextClusters.WordSeparatorPlaces(text);

        using var builder = new SKTextBlobBuilder();
        var inline = 0.0;
        var crossed = 0;
        var paid = 0;

        foreach (var cell in cells)
        {
            while (crossed < points.Length && points[crossed] < cell.Index)
            {
                crossed++;
            }

            while (paid < separators.Length && separators[paid] < cell.Index)
            {
                paid++;
            }

            var step = Math.Max(cell.Advance, style.Size) + letterSpacing;
            var reached = inline + (justification * crossed) + (wordSpacing * paid);
            var baseline = cell.BaselineOffset is { } offset
                ? (Single)(reached + offset)
                : (Single)(reached + ((step - emBox) / 2) + metrics.Ascent);
            var centre = (Single)((emBox - cell.CentreWidth) / 2);

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

    /// <summary>Whether a blob sets its text along a line or down a column.</summary>
    private enum BlobKind
    {
        Run,
        Column
    }

    /// <summary>The kind, style, text and spacing a blob was built for, which is what makes it reusable.</summary>
    private readonly struct TextBlobKey : IEquatable<TextBlobKey>
    {
        private readonly String _text;

        public TextBlobKey(
            BlobKind kind,
            FontStyle style,
            String text,
            Double spacing,
            Double justification,
            Int32 justifies,
            String expand,
            Double wordSpacing)
        {
            Kind = kind;
            Style = style;
            _text = text;
            Spacing = spacing;
            Justification = justification;
            Justifies = justifies;
            Expand = expand;
            WordSpacing = wordSpacing;
        }

        public BlobKind Kind { get; }

        public FontStyle Style { get; }

        public Double Spacing { get; }

        /// <summary>Gets the justified room a place between the words is owed, and how many places.</summary>
        public Double Justification { get; }

        public Int32 Justifies { get; }

        /// <summary>Gets which boundaries of the text the room goes after, as text-justify says.</summary>
        public String Expand { get; }

        /// <summary>Gets the room each of the run's word separators was given.</summary>
        public Double WordSpacing { get; }

        public Boolean Equals(TextBlobKey other) =>
            Kind == other.Kind &&
            Style.Equals(other.Style) &&
            Spacing.Equals(other.Spacing) &&
            Justification.Equals(other.Justification) &&
            Justifies == other.Justifies &&
            WordSpacing.Equals(other.WordSpacing) &&
            String.Equals(Expand, other.Expand, StringComparison.OrdinalIgnoreCase) &&
            String.Equals(_text, other._text, StringComparison.Ordinal);

        public override Boolean Equals(Object? obj) => obj is TextBlobKey other && Equals(other);

        public override Int32 GetHashCode()
        {
            unchecked
            {
                var hash = Kind.GetHashCode();
                hash = (hash * 397) ^ Style.GetHashCode();
                hash = (hash * 397) ^ Spacing.GetHashCode();
                hash = (hash * 397) ^ Justification.GetHashCode();
                hash = (hash * 397) ^ Justifies;
                hash = (hash * 397) ^ StringComparer.OrdinalIgnoreCase.GetHashCode(Expand);
                hash = (hash * 397) ^ WordSpacing.GetHashCode();
                return (hash * 397) ^ StringComparer.Ordinal.GetHashCode(_text);
            }
        }
    }
}

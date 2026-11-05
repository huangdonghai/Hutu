using HarfBuzzSharp;
using SkiaSharp;
using SkiaSharp.HarfBuzz;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using HarfBuzzBuffer = HarfBuzzSharp.Buffer;

namespace Hutu.Skia;

/// <summary>
/// Reaches the shape a font makes of text: the glyphs a run is set in along its baseline, and the
/// glyphs a character is set in down a column, with the place each glyph is drawn at.
/// </summary>
/// <remarks>
/// The horizontal path names no feature, because HarfBuzz infers the script and the direction from
/// the text and shapes the buffer the way that script is written. The vertical path names none
/// either - HarfBuzz turns <c>vert</c> on by itself for a buffer that does not run horizontally, so
/// a buffer running top to bottom is the whole of the request.
/// <para>
/// Every run is shaped here, whatever script it carries: measurement and drawing both ask this one
/// class, so the width a line is laid out to is by construction the width its glyphs fill. Shaping
/// is the expensive half of drawing text, so it is done once and the answer is kept, by face, size,
/// text and direction. A page repeats its words far more often than it invents them, so after the
/// first sight of a run the cost is a lookup.
/// </para>
/// </remarks>
internal sealed class SkiaTextShaper : IDisposable
{
    private readonly ConcurrentDictionary<IntPtr, SKShaper> _shapers = new();
    private readonly ConcurrentDictionary<ShapedRunKey, ShapedRun> _runs = new();

    // A face reached through HarfBuzz rather than through Skia's shaper, which is what the runs that
    // name their own features are set with: Skia's shaper takes no feature list, and the library's own
    // font does (CSS Fonts 4 §6.4). Kept per typeface because a face is expensive to open and the
    // scale - the one thing that changes per run - is set on it before every shaping.
    private readonly ConcurrentDictionary<IntPtr, HarfBuzzFace> _faces = new();

    // The features a value names, parsed once. A page that asks for the same list on every line of a
    // paragraph pays for the parse once.
    private readonly ConcurrentDictionary<String, Feature[]> _featureLists = new();

    // The direction HarfBuzz takes from a run, kept by the text it read. A page repeats its words far
    // more often than it invents them, and guessing the segment properties is a buffer walk per run.
    private readonly Dictionary<String, Boolean> _directions = new(StringComparer.Ordinal);
    private const Int32 DirectionCacheLimit = 4096;

    // HarfBuzz shapes into a buffer rather than out of one, so a buffer is reused for every request;
    // it is not thread safe, which the lock and the cache between them make irrelevant.
    private readonly HarfBuzzBuffer _buffer = new();
    private readonly Object _shapeLock = new();

    /// <summary>
    /// Shapes a run of text into the glyphs a font sets it in, with the place each glyph is drawn at
    /// along the baseline. The direction is the buffer's to guess, so an Arabic run comes back with
    /// its letters joined and set right to left and a Hebrew run with its letters reordered, while a
    /// run in a language that does neither is left as it was.
    /// </summary>
    /// <remarks>
    /// The answer is kept per face, size and text. The positions are lengths rather than fractions,
    /// which is why the size is part of the key.
    /// </remarks>
    public ShapedRun Shape(SKFont font, String text) => Shape(font, text, null);

    /// <summary>
    /// Shapes a run of text the way a face sets it when the page asks for OpenType features:
    /// <paramref name="features"/> is the resolved <c>font-feature-settings</c>, and a run that asks
    /// for none is shaped by Skia's shaper as it always was.
    /// </summary>
    /// <remarks>
    /// Two shapers, because the two answer differently: Skia's reads the script and the direction from
    /// the text alone and takes no feature list, and HarfBuzz's font takes one. A run with features is
    /// therefore shaped through the library directly - the glyphs and positions come back in the same
    /// frame, which is why the one <see cref="ShapedRun"/> serves both.
    /// </remarks>
    public ShapedRun Shape(SKFont font, String text, String? features)
    {
        if (font.Typeface is not { } typeface || text.Length == 0)
        {
            return ShapedRun.None;
        }

        return _runs.GetOrAdd(
            new ShapedRunKey(typeface.Handle, font.Size, text, Vertical: false, features),
            _ => ShapeRun(font, text, features));
    }

    /// <summary>
    /// Gets whether the shaper reads a run right to left: the direction it takes from the first
    /// character that names a script, which is the direction it shapes the run in. Characters that
    /// name no script - a space, a full stop, a bidi control - leave the answer at left to right.
    /// </summary>
    public Boolean ReadsRightToLeft(String text)
    {
        if (text.Length == 0)
        {
            return false;
        }

        lock (_shapeLock)
        {
            if (_directions.TryGetValue(text, out var known))
            {
                return known;
            }

            _buffer.Reset();
            _buffer.AddUtf16(text);
            _buffer.GuessSegmentProperties();

            var rightToLeft = _buffer.Direction == Direction.RightToLeft;

            if (_directions.Count >= DirectionCacheLimit)
            {
                _directions.Clear();
            }

            _directions.Add(text, rightToLeft);
            return rightToLeft;
        }
    }

    /// <summary>
    /// Shapes a run of text the way a column sets it: into the glyphs a font's <c>vert</c> feature
    /// answers for, with where each glyph's baseline goes within the cell the layout gives it.
    /// </summary>
    /// <remarks>
    /// A vertical run's points answer a different question than a horizontal one's: the y of each is
    /// the distance below the top of the character's cell that its baseline sits, already scaled to
    /// the size the font is drawn at, which is the number a column needs to place the glyph. The
    /// distance is normalized against the glyph's own vertical advance, so it holds whether the font
    /// measures that step as an em or as its own box. The advance is the run's natural step down the
    /// column; the layout steps a column by its own arithmetic and does not read it.
    /// </remarks>
    public ShapedRun ShapeVertical(SKFont font, String text) => ShapeVertical(font, text, null);

    /// <summary>Shapes a run down a column, with the features the page asked for where it asked for any.</summary>
    public ShapedRun ShapeVertical(SKFont font, String text, String? features)
    {
        if (font.Typeface is not { } typeface || text.Length == 0)
        {
            return ShapedRun.None;
        }

        return _runs.GetOrAdd(
            new ShapedRunKey(typeface.Handle, font.Size, text, Vertical: true, features),
            _ => ShapeVerticalRun(font, text, features));
    }

    /// <summary>
    /// Gets, for every glyph the font sets the run in, the place in the text of the character the glyph
    /// was made from.
    /// </summary>
    /// <remarks>
    /// Only the letter spacing asks for it, and only once per run, because it is the one question the
    /// library's own answer cannot reply: the positions <see cref="Shape"/> hands back are scaled the
    /// way that method scales them, and its result carries no character with it. The buffer does, and
    /// the buffer is what says whether two glyphs stood together in one character - a ligature, a tone
    /// mark, an ideograph of the extension planes - which is the fact the spacing has to fall by.
    /// </remarks>
    public Int32[] ShapeClusters(SKFont font, String text) => ShapeClusters(font, text, null);

    /// <summary>Gets the glyphs' places in the text, shaped the way the page's features ask.</summary>
    public Int32[] ShapeClusters(SKFont font, String text, String? features)
    {
        if (font.Typeface is not { } typeface || text.Length == 0)
        {
            return Array.Empty<Int32>();
        }

        // The clusters belong to the same shaping that placed the glyphs, so a run cut at its spaces
        // is clustered the same way: each piece is counted from the place it stands at in the text.
        if (StandsAcrossSpaces(font, text))
        {
            var across = new List<Int32>();

            foreach (var (start, length) in Pieces(text))
            {
                foreach (var cluster in Clusters(font, text.Substring(start, length), features))
                {
                    across.Add(cluster + start);
                }
            }

            return across.ToArray();
        }

        return Clusters(font, text, features);
    }

    private Int32[] Clusters(SKFont font, String text, String? features)
    {
        try
        {
            lock (_shapeLock)
            {
                _buffer.Reset();
                _buffer.AddUtf16(text);
                _buffer.GuessSegmentProperties();

                ShapeBuffer(font, features);

                var infos = _buffer.GlyphInfos;
                var clusters = new Int32[infos.Length];

                for (var index = 0; index < infos.Length; index++)
                {
                    clusters[index] = (Int32)infos[index].Cluster;
                }

                return clusters;
            }
        }
        catch (Exception exception)
        {
            // The spacing then falls by the glyph rather than by the character, which is how it was
            // drawn before anything asked: a shaper that will not answer is a page spaced slightly
            // wrong, not a page that loses its text.
            Debug.WriteLine($"Clustering '{text}' for {font.Typeface?.FamilyName} failed: {exception}");
            return Array.Empty<Int32>();
        }
    }

    private ShapedRun ShapeRun(SKFont font, String text, String? features)
    {
        // HarfBuzz answers a kerning pair across a space the way it answers one between two letters,
        // and a browser does not: its lookups stop at the word boundary. The difference is small but
        // it is not invisible - a sentence whose letters were cut into pieces by an element keeps its
        // letters where they were, while the same sentence shaped as one run slides them a little -
        // and the borrowed pages compare those two spellings against each other. Shaping each piece on
        // its own is that stop: within a piece every pair still answers, across a space none does.
        if (StandsAcrossSpaces(font, text))
        {
            return ShapePieces(font, text, features);
        }

        return ShapePiece(font, text, features);
    }

    /// <summary>
    /// Gets whether the text holds a letter on one side of a space and another on the other, so that a
    /// pair on either side of that space is the only thing shaping whole could get wrong. A run the
    /// script reads back to front is left alone: its pieces are laid out by the shaper's own pen, and
    /// the order of pieces across a space is the writing's, not something this split may decide.
    /// </summary>
    private Boolean StandsAcrossSpaces(SKFont font, String text)
    {
        var spaces = 0;
        var letters = 0;

        for (var at = 0; at < text.Length; at++)
        {
            if (Char.IsWhiteSpace(text[at]))
            {
                spaces++;
            }
            else
            {
                letters++;

                // A word standing at either edge of the run has no pair to lose across that edge.
                if (spaces > 0 && spaces < at && letters > 1 && at < text.Length - 1)
                {
                    return !ReadsRightToLeft(text);
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Shapes the pieces of a run separated by white space and lays them down where they were written,
    /// with each piece's glyphs standing at the pen it reached.
    /// </summary>
    private ShapedRun ShapePieces(SKFont font, String text, String? features)
    {
        var glyphs = new List<UInt16>();
        var points = new List<SKPoint>();
        var pen = 0.0;

        foreach (var (start, length) in Pieces(text))
        {
            var piece = ShapePiece(font, text.Substring(start, length), features);

            if (!piece.Succeeded)
            {
                // One piece the face cannot set is the whole run it stands in: the caller draws the
                // text unshaped, which is what it does for a run it cannot shape at all.
                return ShapedRun.None;
            }

            for (var at = 0; at < piece.Glyphs.Length; at++)
            {
                glyphs.Add(piece.Glyphs[at]);

                var point = piece.Points.Length > at ? piece.Points[at] : SKPoint.Empty;
                points.Add(new SKPoint(point.X + (Single)pen, point.Y));
            }

            pen += piece.Advance;
        }

        return new ShapedRun(glyphs.ToArray(), points.ToArray(), (Single)pen, true);
    }

    /// <summary>
    /// Cuts the text at the white space it holds, giving the runs of letters and the runs of space
    /// between them in the order they were written.
    /// </summary>
    private static List<(Int32 Start, Int32 Length)> Pieces(String text)
    {
        var pieces = new List<(Int32, Int32)>();
        var start = 0;

        while (start < text.Length)
        {
            var white = Char.IsWhiteSpace(text[start]);
            var at = start + 1;

            while (at < text.Length && Char.IsWhiteSpace(text[at]) == white)
            {
                at++;
            }

            pieces.Add((start, at - start));
            start = at;
        }

        return pieces;
    }

    private ShapedRun ShapePiece(SKFont font, String text, String? features)
    {
        if (!String.IsNullOrEmpty(features))
        {
            return ShapeWithFeatures(font, text, features!, vertical: false);
        }

        try
        {
            SKShaper.Result result;

            lock (_shapeLock)
            {
                result = Shaper(font.Typeface!).Shape(text, font);
            }

            if (result.Codepoints.Length == 0)
            {
                return ShapedRun.None;
            }

            var glyphs = new UInt16[result.Codepoints.Length];

            for (var index = 0; index < glyphs.Length; index++)
            {
                glyphs[index] = (UInt16)result.Codepoints[index];
            }

            return new ShapedRun(glyphs, result.Points, result.Width, true);
        }
        catch (Exception exception)
        {
            // Drawing falls back to the unshaped text rather than dropping the run, so a face the
            // shaper cannot handle is a page that reads as it always did, not one that loses a word.
            Debug.WriteLine($"Shaping '{text}' for {font.Typeface?.FamilyName} failed: {exception}");
            return ShapedRun.None;
        }
    }

    private ShapedRun ShapeVerticalRun(SKFont font, String text, String? features)
    {
        if (!String.IsNullOrEmpty(features))
        {
            return ShapeWithFeatures(font, text, features!, vertical: true);
        }

        try
        {
            lock (_shapeLock)
            {
                _buffer.Reset();
                _buffer.AddUtf16(text);
                _buffer.Direction = Direction.TopToBottom;
                _buffer.GuessSegmentProperties();

                Shaper(font.Typeface!).Shape(_buffer, font);

                var infos = _buffer.GlyphInfos;
                var positions = _buffer.GlyphPositions;

                if (infos.Length == 0)
                {
                    return ShapedRun.None;
                }

                var glyphs = new UInt16[infos.Length];
                var points = new SKPoint[infos.Length];
                var advances = new Single[infos.Length];
                var advance = 0f;

                // Skia's shaper repeats a horizontal advance down a column for some faces (Microsoft
                // YaHei says 512 where its vmtx says 2048), so the step is asked of the library's face
                // instead. The points above keep the origin Skia read; only the step is replaced.
                var face = HarfBuzzFont(font.Typeface!);

                if (face is not null)
                {
                    var scale = ScaleOf(font.Size);
                    face.SetScale(scale, scale);
                }

                for (var index = 0; index < infos.Length; index++)
                {
                    glyphs[index] = (UInt16)infos[index].Codepoint;

                    var step = Math.Abs(positions[index].YAdvance);

                    points[index] = new SKPoint(
                        positions[index].XOffset,
                        step <= 0 ? 0 : -(Single)positions[index].YOffset * font.Size / step);

                    advances[index] = face is null
                        ? positions[index].YAdvance
                        : face.GetVerticalGlyphAdvance(glyphs[index]) / FixedPoint;
                    advance += advances[index];
                }

                return new ShapedRun(glyphs, points, advance, true, advances);
            }
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"Shaping '{text}' vertically for {font.Typeface?.FamilyName} failed: {exception}");
            return ShapedRun.None;
        }
    }

    public void Dispose()
    {
        foreach (var shaper in _shapers.Values)
        {
            shaper.Dispose();
        }

        foreach (var face in _faces.Values)
        {
            face.Dispose();
        }

        _shapers.Clear();
        _faces.Clear();
        _featureLists.Clear();
        _runs.Clear();
        _buffer.Dispose();
    }

    private SKShaper Shaper(SKTypeface typeface) =>
        _shapers.GetOrAdd(typeface.Handle, _ => new SKShaper(typeface));

    /// <summary>
    /// Shapes the buffer with the page's features where it asked for any, and with Skia's shaper where
    /// it did not. The faces that answer are interchangeable - both set the same glyphs at the same
    /// places - so one buffer serves either.
    /// </summary>
    private Boolean ShapeBuffer(SKFont font, String? features)
    {
        if (String.IsNullOrEmpty(features))
        {
            Shaper(font.Typeface!).Shape(_buffer, font);

            return true;
        }

        var face = HarfBuzzFont(font.Typeface!);

        if (face is null)
        {
            Shaper(font.Typeface!).Shape(_buffer, font);

            return false;
        }

        var scale = ScaleOf(font.Size);
        face.SetScale(scale, scale);
        face.Shape(_buffer, FeatureList(features!));

        return true;
    }

    /// <summary>
    /// Shapes a run through the library's own font, which is the one path that takes a feature list.
    /// </summary>
    /// <remarks>
    /// The positions come back in the same frame Skia's shaper answers in - offsets from the pen along
    /// the baseline, and the advance the run takes - once the fixed-point steps the library counts in
    /// are divided out. Measured against Skia's shaper with no features, the two agree glyph for glyph
    /// and pixel for pixel, which is what lets a page ask for a feature on one line and not the next.
    /// </remarks>
    private ShapedRun ShapeWithFeatures(SKFont font, String text, String features, Boolean vertical)
    {
        if (font.Typeface is not { } typeface)
        {
            return ShapedRun.None;
        }

        try
        {
            lock (_shapeLock)
            {
                _buffer.Reset();
                _buffer.AddUtf16(text);

                if (vertical)
                {
                    _buffer.Direction = Direction.TopToBottom;
                }

                _buffer.GuessSegmentProperties();

                var scale = ScaleOf(font.Size);
                var face = HarfBuzzFont(typeface);
                face!.SetScale(scale, scale);
                face.Shape(_buffer, FeatureList(features));

                var infos = _buffer.GlyphInfos;
                var positions = _buffer.GlyphPositions;

                if (infos.Length == 0)
                {
                    return ShapedRun.None;
                }

                var glyphs = new UInt16[infos.Length];
                var points = new SKPoint[infos.Length];
                var advances = vertical ? new Single[infos.Length] : null;
                var advance = 0f;
                var walked = 0f;

                for (var index = 0; index < infos.Length; index++)
                {
                    glyphs[index] = (UInt16)infos[index].Codepoint;

                    if (vertical)
                    {
                        // The same reading of a column's origin the Skia path takes: the offset is a
                        // share of the step, which is what makes it independent of the scale asked for.
                        var step = Math.Abs(positions[index].YAdvance);

                        points[index] = new SKPoint(
                            positions[index].XOffset / FixedPoint,
                            step <= 0 ? 0 : -(Single)positions[index].YOffset * font.Size / step);

                        advances![index] = positions[index].YAdvance / FixedPoint;
                        advance += advances[index];
                    }
                    else
                    {
                        // Where the glyph stands in the run, and not where the face moved it to: the
                        // library answers an offset from the pen for each glyph, while the shape this
                        // type is read as carries the place itself - the pen walked along the run.
                        // Read as an offset, every glyph of a run would be drawn on its first.
                        points[index] = new SKPoint(
                            walked + (positions[index].XOffset / FixedPoint),
                            positions[index].YOffset / FixedPoint);

                        walked += positions[index].XAdvance / FixedPoint;
                        advance = walked;
                    }
                }

                return new ShapedRun(glyphs, points, advance, true, advances);
            }
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"Shaping '{text}' with {features} for {font.Typeface?.FamilyName} failed: {exception}");

            return ShapedRun.None;
        }
    }

    /// <summary>
    /// Gets the library's font for a face, opened from the same data Skia holds. Nothing is opened
    /// twice: a page's features all reach the one face its text is set in.
    /// </summary>
    private HarfBuzzSharp.Font? HarfBuzzFont(SKTypeface typeface)
    {
        if (_faces.TryGetValue(typeface.Handle, out var known))
        {
            return known.Font;
        }

        var stream = typeface.OpenStream(out var index);

        if (stream is null)
        {
            // A face the platform holds without data of its own - some system fonts - cannot be
            // opened a second time, and the run is then shaped without its features rather than lost.
            return null;
        }

        using (stream)
        {
            var blob = stream.ToHarfBuzzBlob();
            var face = new Face(blob, index);
            var font = new HarfBuzzSharp.Font(face);
            var opened = new HarfBuzzFace(blob, face, font);
            var kept = _faces.GetOrAdd(typeface.Handle, opened);

            if (!ReferenceEquals(kept, opened))
            {
                opened.Dispose();
            }

            return kept.Font;
        }
    }

    private Feature[] FeatureList(String features) => _featureLists.GetOrAdd(features, ParseFeatures);

    private static Feature[] ParseFeatures(String features)
    {
        var list = new List<Feature>();

        foreach (var entry in features.Split(','))
        {
            var at = entry.IndexOf('=');

            if (at <= 0 ||
                !UInt32.TryParse(entry[(at + 1)..], out var value))
            {
                continue;
            }

            try
            {
                list.Add(new Feature(Tag.Parse(entry[..at]), value));
            }
            catch (Exception)
            {
                // A tag the library will not take names no feature, and the entries around it stand.
            }
        }

        return list.ToArray();
    }

    private static Int32 ScaleOf(Single size) => Math.Max(1, (Int32)Math.Round(size * FixedPoint));

    /// <summary>The steps the library counts a position in: sixty-four to the pixel.</summary>
    private const Single FixedPoint = 64f;

    /// <summary>
    /// A face held open through the library: the blob it was read from and the face over it, kept
    /// alive for as long as the font made from them is in use.
    /// </summary>
    private sealed class HarfBuzzFace : IDisposable
    {
        private readonly Blob _blob;
        private readonly Face _face;

        public HarfBuzzFace(Blob blob, Face face, HarfBuzzSharp.Font font)
        {
            _blob = blob;
            _face = face;
            Font = font;
        }

        public HarfBuzzSharp.Font Font { get; }

        public void Dispose()
        {
            Font.Dispose();
            _face.Dispose();
            _blob.Dispose();
        }
    }

    /// <summary>The face, size, text and direction a shaped run was taken from, which is what makes it reusable.</summary>
    private readonly struct ShapedRunKey : IEquatable<ShapedRunKey>
    {
        private readonly String _text;
        private readonly String? _features;

        public ShapedRunKey(IntPtr typeface, Single size, String text, Boolean Vertical, String? features)
        {
            Typeface = typeface;
            Size = size;
            _text = text;
            this.Vertical = Vertical;
            _features = String.IsNullOrEmpty(features) ? null : features;
        }

        public IntPtr Typeface { get; }

        public Single Size { get; }

        public Boolean Vertical { get; }

        public Boolean Equals(ShapedRunKey other) =>
            Typeface == other.Typeface &&
            Size.Equals(other.Size) &&
            Vertical == other.Vertical &&
            String.Equals(_text, other._text, StringComparison.Ordinal) &&
            String.Equals(_features, other._features, StringComparison.Ordinal);

        public override Boolean Equals(Object? obj) => obj is ShapedRunKey other && Equals(other);

        public override Int32 GetHashCode()
        {
            unchecked
            {
                var hash = Typeface.GetHashCode();
                hash = (hash * 397) ^ Size.GetHashCode();
                hash = (hash * 397) ^ (Vertical ? 1 : 0);
                hash = (hash * 397) ^ (_features is null ? 0 : StringComparer.Ordinal.GetHashCode(_features));
                return (hash * 397) ^ StringComparer.Ordinal.GetHashCode(_text);
            }
        }
    }
}

/// <summary>
/// A run of text shaped into the glyphs a font sets it in, with where each glyph is drawn at and how
/// far the whole run advances.
/// </summary>
/// <remarks>
/// The one answer serves both directions. Along a line the positions are distances from the start of
/// the run on its baseline, so a caller drawing the run at a pen adds them to that pen, and the
/// advance is the distance along the baseline. Down a column the positions are distances from the
/// top of the character's cell - see <see cref="SkiaTextShaper.ShapeVertical"/> - and the advance is
/// the distance down the column. A run the shaper could not handle comes back as
/// <see cref="None"/>, whose <see cref="Succeeded"/> is false and which the caller draws as unshaped
/// text instead.
/// </remarks>
internal readonly struct ShapedRun
{
    public ShapedRun(
        UInt16[] glyphs,
        SKPoint[] points,
        Single advance,
        Boolean succeeded,
        Single[]? verticalAdvances = null)
    {
        Glyphs = glyphs;
        Points = points;
        Advance = advance;
        Succeeded = succeeded;
        VerticalAdvances = verticalAdvances ?? Array.Empty<Single>();
    }

    /// <summary>Gets the run that stands for shaping that did not happen, so the caller falls back.</summary>
    public static ShapedRun None { get; } = new(Array.Empty<UInt16>(), Array.Empty<SKPoint>(), 0, false);

    /// <summary>Gets the glyphs the font sets the run in, in the order they are drawn.</summary>
    public UInt16[] Glyphs { get; }

    /// <summary>Gets the place each glyph is drawn at, relative to where the run starts.</summary>
    public SKPoint[] Points { get; }

    /// <summary>Gets the distance the whole run advances along the axis it is set on.</summary>
    public Single Advance { get; }

    /// <summary>Gets the advance of each glyph down a column, or an empty array for a line.</summary>
    public Single[] VerticalAdvances { get; }

    /// <summary>Gets whether the run was shaped, rather than left as the caller had it.</summary>
    public Boolean Succeeded { get; }
}

using HarfBuzzSharp;
using SkiaSharp;
using SkiaSharp.HarfBuzz;
using System;
using System.Collections.Concurrent;
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
    public ShapedRun Shape(SKFont font, String text)
    {
        if (font.Typeface is not { } typeface || text.Length == 0)
        {
            return ShapedRun.None;
        }

        return _runs.GetOrAdd(
            new ShapedRunKey(typeface.Handle, font.Size, text, Vertical: false),
            _ => ShapeRun(font, text));
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
    public ShapedRun ShapeVertical(SKFont font, String text)
    {
        if (font.Typeface is not { } typeface || text.Length == 0)
        {
            return ShapedRun.None;
        }

        return _runs.GetOrAdd(
            new ShapedRunKey(typeface.Handle, font.Size, text, Vertical: true),
            _ => ShapeVerticalRun(font, text));
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
    public Int32[] ShapeClusters(SKFont font, String text)
    {
        if (font.Typeface is not { } typeface || text.Length == 0)
        {
            return Array.Empty<Int32>();
        }

        try
        {
            lock (_shapeLock)
            {
                _buffer.Reset();
                _buffer.AddUtf16(text);
                _buffer.GuessSegmentProperties();

                Shaper(typeface).Shape(_buffer, font);

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
            Debug.WriteLine($"Clustering '{text}' for {typeface.FamilyName} failed: {exception}");
            return Array.Empty<Int32>();
        }
    }

    private ShapedRun ShapeRun(SKFont font, String text)
    {
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

    private ShapedRun ShapeVerticalRun(SKFont font, String text)
    {
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
                var advance = 0f;

                for (var index = 0; index < infos.Length; index++)
                {
                    glyphs[index] = (UInt16)infos[index].Codepoint;

                    // Where the pen has to put the glyph's baseline for a character running down a
                    // column. The offset is a distance, and the vertical advance is what it should
                    // be read against, so the two together give it scaled to the size the font is
                    // drawn at - which holds whether the font measures that step as an em or as its
                    // own box. A glyph that takes no step of its own sits at the top of its cell.
                    var step = Math.Abs(positions[index].YAdvance);

                    points[index] = new SKPoint(
                        positions[index].XOffset,
                        step <= 0 ? 0 : -(Single)positions[index].YOffset * font.Size / step);

                    advance += positions[index].YAdvance;
                }

                return new ShapedRun(glyphs, points, advance, true);
            }
        }
        catch (Exception exception)
        {
            // Layout has already been measured against the horizontal glyph, so a face that cannot be
            // shaped at all is a character drawn as it always was rather than a page that fails to
            // paint. It is written down because it is not something a reader can report.
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

        _shapers.Clear();
        _runs.Clear();
        _buffer.Dispose();
    }

    private SKShaper Shaper(SKTypeface typeface) =>
        _shapers.GetOrAdd(typeface.Handle, _ => new SKShaper(typeface));

    /// <summary>The face, size, text and direction a shaped run was taken from, which is what makes it reusable.</summary>
    private readonly struct ShapedRunKey : IEquatable<ShapedRunKey>
    {
        private readonly String _text;

        public ShapedRunKey(IntPtr typeface, Single size, String text, Boolean Vertical)
        {
            Typeface = typeface;
            Size = size;
            _text = text;
            this.Vertical = Vertical;
        }

        public IntPtr Typeface { get; }

        public Single Size { get; }

        public Boolean Vertical { get; }

        public Boolean Equals(ShapedRunKey other) =>
            Typeface == other.Typeface &&
            Size.Equals(other.Size) &&
            Vertical == other.Vertical &&
            String.Equals(_text, other._text, StringComparison.Ordinal);

        public override Boolean Equals(Object? obj) => obj is ShapedRunKey other && Equals(other);

        public override Int32 GetHashCode()
        {
            unchecked
            {
                var hash = Typeface.GetHashCode();
                hash = (hash * 397) ^ Size.GetHashCode();
                hash = (hash * 397) ^ (Vertical ? 1 : 0);
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
    public ShapedRun(UInt16[] glyphs, SKPoint[] points, Single advance, Boolean succeeded)
    {
        Glyphs = glyphs;
        Points = points;
        Advance = advance;
        Succeeded = succeeded;
    }

    /// <summary>Gets the run that stands for shaping that did not happen, so the caller falls back.</summary>
    public static ShapedRun None { get; } = new(Array.Empty<UInt16>(), Array.Empty<SKPoint>(), 0, false);

    /// <summary>Gets the glyphs the font sets the run in, in the order they are drawn.</summary>
    public UInt16[] Glyphs { get; }

    /// <summary>Gets the place each glyph is drawn at, relative to where the run starts.</summary>
    public SKPoint[] Points { get; }

    /// <summary>Gets the distance the whole run advances along the axis it is set on.</summary>
    public Single Advance { get; }

    /// <summary>Gets whether the run was shaped, rather than left as the caller had it.</summary>
    public Boolean Succeeded { get; }
}

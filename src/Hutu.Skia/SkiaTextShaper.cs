using HarfBuzzSharp;
using SkiaSharp;
using SkiaSharp.HarfBuzz;
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using HarfBuzzBuffer = HarfBuzzSharp.Buffer;

namespace Hutu.Skia;

/// <summary>
/// Reaches the shape a font makes of text that is not drawn one glyph per character: the joined and
/// reordered glyphs of the scripts that need shaping, and the vertical form a font sets a character
/// in when the text runs down a column.
/// </summary>
/// <remarks>
/// The vertical path names no feature: HarfBuzz turns <c>vert</c> on by itself for a buffer that does
/// not run horizontally - it enables the tag with a global search so that a font is asked for the
/// feature even where it does not list it for the script at hand - so a buffer running top to bottom
/// is the whole of the request. The run path names none either, because HarfBuzz infers the script
/// and the direction from the text and shapes the buffer the way that script is written.
/// <para>
/// Shaping is the expensive half of drawing text, so it is done once and the answer is kept: a column
/// keeps the form of each character by face, and a run keeps its glyphs by face, size and text. A page
/// repeats its words far more often than it invents them, so after the first sight of a run the cost
/// is a lookup. A vertical form does not depend on the size the font is set at, so the face alone keys
/// that cache; a shaped run is a length, so its size is part of its key.
/// </para>
/// </remarks>
public sealed class SkiaTextShaper : IDisposable
{
    /// <summary>
    /// The code point ranges whose scripts a font has to shape rather than map one character to one
    /// glyph: the joining scripts of the Middle East, the Indic and South-East Asian scripts that
    /// reorder and combine their marks, and the presentation forms those scripts ask for.
    /// </summary>
    /// <remarks>
    /// Shaping is only asked for where it changes the picture. A Latin or CJK run is measured and
    /// drawn by mapping each character straight to its glyph, which is both faster and what the rest
    /// of the engine has always done; a Hebrew run needs no joining but is reordered, and is shaped
    /// for the same reason.
    /// </remarks>
    private static readonly (Int32 Start, Int32 End)[] ShapingRanges =
    {
        (0x0590, 0x05FF),   // Hebrew
        (0x0600, 0x06FF),   // Arabic
        (0x0700, 0x074F),   // Syriac
        (0x0750, 0x077F),   // Arabic Supplement
        (0x0780, 0x07BF),   // Thaana
        (0x07C0, 0x07FF),   // NKo
        (0x0800, 0x083F),   // Samaritan
        (0x0840, 0x085F),   // Mandaic
        (0x0860, 0x08FF),   // Syriac Supplement and Arabic Extended
        (0x0900, 0x0DFF),   // Devanagari through Sinhala
        (0x0E00, 0x0EFF),   // Thai and Lao
        (0x0F00, 0x0FFF),   // Tibetan
        (0x1000, 0x109F),   // Myanmar
        (0x1780, 0x17FF),   // Khmer
        (0x1800, 0x18AF),   // Mongolian
        (0x1100, 0x11FF),   // Hangul Jamo, which combine into a syllable
        (0xFB1D, 0xFB4F),   // Hebrew presentation forms
        (0xFB50, 0xFDFF),   // Arabic presentation forms A
        (0xFE70, 0xFEFF),   // Arabic presentation forms B
        (0x1EE00, 0x1EEFF), // Arabic mathematical alphabetic symbols
    };

    private readonly ConcurrentDictionary<IntPtr, SKShaper> _shapers = new();
    private readonly ConcurrentDictionary<VerticalFormKey, VerticalForm> _forms = new();
    private readonly ConcurrentDictionary<ShapedRunKey, ShapedRun> _runs = new();

    // HarfBuzz shapes into a buffer rather than out of one, so a buffer is reused for every request;
    // it is not thread safe, which the lock and the cache between them make irrelevant.
    private readonly HarfBuzzBuffer _buffer = new();
    private readonly Object _shapeLock = new();

    /// <summary>
    /// Gets whether a run holds any character a font shapes rather than draws one glyph per code
    /// point. It is the switch that keeps this whole path off the Latin and CJK text a page is mostly
    /// made of, and on for the scripts - Arabic above all - where a character's picture depends on its
    /// neighbours.
    /// </summary>
    public static Boolean NeedsShaping(String text)
    {
        var index = 0;

        while (index < text.Length)
        {
            var codepoint = Char.IsSurrogatePair(text, index) ? Char.ConvertToUtf32(text, index) : text[index];

            foreach (var (start, end) in ShapingRanges)
            {
                if (codepoint >= start && codepoint <= end)
                {
                    return true;
                }
            }

            index += Char.IsSurrogatePair(text, index) ? 2 : 1;
        }

        return false;
    }

    /// <summary>
    /// Shapes a run of text into the glyphs a font sets it in, with the place each glyph is drawn at
    /// along the baseline. The direction is the buffer's to guess, so an Arabic run comes back with
    /// its letters joined and set right to left and a Hebrew run with its letters reordered, while a
    /// run in a language that does neither is left as it was.
    /// </summary>
    /// <remarks>
    /// Shaping is not cheap, so the answer is kept per face, size and text. Unlike the vertical forms
    /// the result depends on the size the font is drawn at, because the positions are lengths rather
    /// than fractions, which is why the size is part of the key.
    /// </remarks>
    public ShapedRun Shape(SKFont font, String text)
    {
        if (font.Typeface is not { } typeface || text.Length == 0)
        {
            return ShapedRun.None;
        }

        return _runs.GetOrAdd(
            new ShapedRunKey(typeface.Handle, font.Size, text),
            _ => ShapeRun(font, text));
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

    /// <summary>
    /// Gets the form a column sets a character in. A font that has no form of its own for the
    /// character - and one whose feature leaves it alone - answers with the very glyph a line would
    /// have drawn, which is reported as no substitution so the caller can fall back to a form it can
    /// name; the origin is reported either way, because where a glyph goes is the font's business
    /// whether or not it moved the glyph.
    /// </summary>
    public VerticalForm GetVerticalForm(SKFont font, Int32 codepoint) =>
        font.Typeface is { } typeface
            ? _forms.GetOrAdd(new VerticalFormKey(typeface.Handle, codepoint), _ => Shape(typeface, font, codepoint))
            : default;

    /// <summary>
    /// Draws a glyph the shaper named at the pen position a column's character is drawn at. The glyph
    /// is drawn by id rather than by character because it need not stand for one a document could
    /// name - the vertical form of the prolonged sound mark is the usual example.
    /// </summary>
    public void DrawVerticalGlyph(SKCanvas canvas, SKFont font, UInt16 glyph, Single x, Single y, SKPaint paint)
    {
        Span<UInt16> glyphs = stackalloc UInt16[1];
        glyphs[0] = glyph;

        Span<SKPoint> positions = stackalloc SKPoint[1];
        positions[0] = new SKPoint(x, y);

        using var builder = new SKTextBlobBuilder();
        var run = builder.AllocatePositionedRun(font, 1);
        run.SetGlyphs(glyphs);
        run.SetPositions(positions);

        using var blob = builder.Build();

        if (blob is null)
        {
            return;
        }

        // The run carries absolute positions, so the blob itself is drawn at the origin.
        canvas.DrawText(blob, 0, 0, paint);
    }

    public void Dispose()
    {
        foreach (var shaper in _shapers.Values)
        {
            shaper.Dispose();
        }

        _shapers.Clear();
        _forms.Clear();
        _runs.Clear();
        _buffer.Dispose();
    }

    /// <summary>
    /// Shapes a character down a column and reports the glyph and the place the font gives it.
    /// </summary>
    private VerticalForm Shape(SKTypeface typeface, SKFont font, Int32 codepoint)
    {
        // The glyph a line would use is the yardstick for "the feature did nothing": a font may answer
        // with it either because it has no form of its own for the character or because its feature
        // leaves the character alone, and neither is a substitution.
        var horizontal = typeface.GetGlyph(codepoint);

        try
        {
            UInt16 glyph;
            Single origin;

            lock (_shapeLock)
            {
                _buffer.Reset();
                _buffer.AddUtf16(Char.ConvertFromUtf32(codepoint));
                _buffer.Direction = Direction.TopToBottom;
                _buffer.GuessSegmentProperties();

                Shaper(typeface).Shape(_buffer, font);

                var glyphs = _buffer.GlyphInfos;
                var positions = _buffer.GlyphPositions;

                // Only a form that is still the one character can fill the step the layout gave it, so
                // a substitution that grew into a cluster is left alone rather than drawn in part - a
                // character is drawn into the room of a character here.
                if (glyphs.Length != 1)
                {
                    return default;
                }

                glyph = (UInt16)glyphs[0].Codepoint;

                // Where the pen has to put the glyph's baseline for a character running down a column.
                // The offset is a distance, and the vertical advance is what it should be read
                // against, so the two together give it as a fraction of the step the glyph takes -
                // which holds whether the font measures that step as an em or as its own box.
                var step = Math.Abs(positions[0].YAdvance);
                origin = step <= 0 ? 0 : -(Single)positions[0].YOffset / step;
            }

            return new VerticalForm(
                glyph,
                glyph != 0 && glyph != horizontal,
                true,
                origin);
        }
        catch (Exception exception)
        {
            // Layout has already been measured against the horizontal glyph, so a face that cannot be
            // shaped at all is a character drawn as it always was rather than a page that fails to
            // paint. It is written down because it is not something a reader can report.
            Debug.WriteLine($"Shaping U+{codepoint:X4} for {typeface.FamilyName} failed: {exception}");
            return default;
        }
    }

    private SKShaper Shaper(SKTypeface typeface) =>
        _shapers.GetOrAdd(typeface.Handle, _ => new SKShaper(typeface));

    private readonly struct VerticalFormKey : IEquatable<VerticalFormKey>
    {
        public VerticalFormKey(IntPtr typeface, Int32 codepoint)
        {
            Typeface = typeface;
            Codepoint = codepoint;
        }

        public IntPtr Typeface { get; }

        public Int32 Codepoint { get; }

        public Boolean Equals(VerticalFormKey other) => Typeface == other.Typeface && Codepoint == other.Codepoint;

        public override Boolean Equals(Object? obj) => obj is VerticalFormKey other && Equals(other);

        public override Int32 GetHashCode() => (Typeface.GetHashCode() * 397) ^ Codepoint;
    }

    /// <summary>The face, size and text a shaped run was taken from, which is what makes it reusable.</summary>
    private readonly struct ShapedRunKey : IEquatable<ShapedRunKey>
    {
        private readonly String _text;

        public ShapedRunKey(IntPtr typeface, Single size, String text)
        {
            Typeface = typeface;
            Size = size;
            _text = text;
        }

        public IntPtr Typeface { get; }

        public Single Size { get; }

        public Boolean Equals(ShapedRunKey other) =>
            Typeface == other.Typeface &&
            Size.Equals(other.Size) &&
            String.Equals(_text, other._text, StringComparison.Ordinal);

        public override Boolean Equals(Object? obj) => obj is ShapedRunKey other && Equals(other);

        public override Int32 GetHashCode()
        {
            unchecked
            {
                var hash = Typeface.GetHashCode();
                hash = (hash * 397) ^ Size.GetHashCode();
                return (hash * 397) ^ StringComparer.Ordinal.GetHashCode(_text);
            }
        }
    }
}

/// <summary>
/// What a column needs to draw one character: the glyph the font sets it in, and how far down the
/// character's step the font puts that glyph's baseline.
/// </summary>
/// <remarks>
/// The origin is a fraction of the step rather than a length, so it costs nothing to keep beside the
/// glyph and is scaled to the size the font is drawn at when it is used. A font declares a vertical
/// origin for every glyph, and it moves that origin for the marks that would otherwise sit against the
/// corner of their cell - 、 and 。 in the CJK faces are the loudest example - so a column that ignores
/// it draws those marks half a character out of place.
/// </remarks>
public readonly struct VerticalForm
{
    public VerticalForm(UInt16 glyph, Boolean substituted, Boolean hasOrigin, Single origin)
    {
        Glyph = glyph;
        IsSubstituted = substituted;
        HasOrigin = hasOrigin;
        Origin = origin;
    }

    /// <summary>Gets the glyph the font sets the character in, or zero when it would not say.</summary>
    public UInt16 Glyph { get; }

    /// <summary>Gets whether the font moved the character to a glyph of its own.</summary>
    public Boolean IsSubstituted { get; }

    /// <summary>Gets whether the origin is one the font stated, rather than the default nothing.</summary>
    public Boolean HasOrigin { get; }

    /// <summary>Gets how far down the step the glyph's baseline goes, as a fraction of the step.</summary>
    public Single Origin { get; }
}

/// <summary>
/// A run of text shaped into the glyphs a font sets it in, with where each glyph is drawn along the
/// baseline and how far the whole run reaches.
/// </summary>
/// <remarks>
/// The positions are distances from the start of the run, so a caller drawing the run at a pen adds
/// them to that pen. A run the shaper could not handle comes back as <see cref="None"/>, whose
/// <see cref="Succeeded"/> is false and which the caller draws as unshaped text instead.
/// </remarks>
public readonly struct ShapedRun
{
    public ShapedRun(UInt16[] glyphs, SKPoint[] points, Single width, Boolean succeeded)
    {
        Glyphs = glyphs;
        Points = points;
        Width = width;
        Succeeded = succeeded;
    }

    /// <summary>Gets the run that stands for shaping that did not happen, so the caller falls back.</summary>
    public static ShapedRun None { get; } = new(Array.Empty<UInt16>(), Array.Empty<SKPoint>(), 0, false);

    /// <summary>Gets the glyphs the font sets the run in, in the order they are drawn.</summary>
    public UInt16[] Glyphs { get; }

    /// <summary>Gets the place each glyph is drawn at, relative to the start of the run.</summary>
    public SKPoint[] Points { get; }

    /// <summary>Gets the distance the whole run advances along the baseline.</summary>
    public Single Width { get; }

    /// <summary>Gets whether the run was shaped, rather than left as the caller had it.</summary>
    public Boolean Succeeded { get; }
}

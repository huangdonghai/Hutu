using HarfBuzzSharp;
using SkiaSharp;
using SkiaSharp.HarfBuzz;
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using HarfBuzzBuffer = HarfBuzzSharp.Buffer;

namespace Hutu.Skia;

/// <summary>
/// Reaches the form a font sets a character in when the text runs down a column: the glyph it
/// substitutes through its <c>vert</c> feature, and where the font puts that glyph down the cell.
/// </summary>
/// <remarks>
/// No feature is ever named. HarfBuzz turns <c>vert</c> on by itself for a buffer that does not run
/// horizontally - it enables the tag with a global search so that a font is asked for the feature even
/// where it does not list it for the script at hand - so a buffer running top to bottom is the whole
/// of the request. That matters here because this SkiaSharp has no shaping API of its own to pass
/// features through.
/// <para>
/// Shaping is the expensive half of drawing a character, so it is done once per typeface and
/// character and the answer is kept, misses included: a page repeats its punctuation far more often
/// than it invents it, and after the first sight of a character the column pays only a lookup. The
/// answer does not depend on the size the font is set at, so the face alone keys the cache.
/// </para>
/// </remarks>
public sealed class SkiaTextShaper : IDisposable
{
    private readonly ConcurrentDictionary<IntPtr, SKShaper> _shapers = new();
    private readonly ConcurrentDictionary<VerticalFormKey, VerticalForm> _forms = new();

    // HarfBuzz shapes into a buffer rather than out of one, so a buffer is reused for every request;
    // it is not thread safe, which the lock and the cache between them make irrelevant.
    private readonly HarfBuzzBuffer _buffer = new();

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

            lock (_buffer)
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

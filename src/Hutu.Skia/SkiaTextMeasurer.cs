using Hutu.Rendering.Layout;
using SkiaSharp;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace Hutu.Skia;

/// <summary>
/// Skia-backed implementation of <see cref="ITextMeasurer"/>: font metrics and advance widths.
/// </summary>
/// <remarks>
/// Fonts and metrics are created on demand and cached by <see cref="FontStyle"/>, because the
/// layout engine asks for both per text fragment and the underlying calls cross into native code.
/// The fonts are also shared with the paint executor, so a run is measured and drawn with the very
/// same font. A character the family has no glyph for is handed to a system font that does, which is
/// what lets a Latin family carry CJK text without a box per character.
/// <para>
/// Every stretch of text is decomposed once - <see cref="Decompose"/> along a line and
/// <see cref="DecomposeUpright"/> down a column - into the fonts, glyphs and positions it is drawn
/// with, and both measurement and painting read that one answer. That is what makes the width a
/// line is laid out to the width its glyphs fill: the two sides of the engine cannot drift apart,
/// because there is only the one set of numbers to drift from.
/// </para>
/// </remarks>
public sealed class SkiaTextMeasurer : ITextMeasurer, IDisposable
{
    private readonly SkiaFontResolver _resolver;
    private readonly SkiaTextShaper _shaper = new();
    private readonly SkiaTextBlobCache _blobs;
    private readonly ConcurrentDictionary<FontStyle, SKFont> _fonts = new();
    private readonly ConcurrentDictionary<FallbackFontKey, SKFont> _fallbackFonts = new();
    private readonly ConcurrentDictionary<FontStyle, FontMetrics> _metrics = new();
    private readonly ConcurrentDictionary<DecompositionKey, IReadOnlyList<SkiaTextRun>> _decompositions = new();
    private readonly ConcurrentDictionary<DecompositionKey, IReadOnlyList<UprightCell>> _uprightCells = new();

    public SkiaTextMeasurer(SkiaFontResolver? resolver = null)
    {
        _resolver = resolver ?? new SkiaFontResolver();
        _blobs = new SkiaTextBlobCache(this);
    }

    /// <summary>Gets the resolver this measurer shares its typefaces with.</summary>
    public SkiaFontResolver Fonts => _resolver;

    /// <summary>Gets the shaper every glyph this measurer speaks of was shaped by.</summary>
    public SkiaTextShaper Shaper => _shaper;

    /// <summary>Gets the cache of drawn text the paint executor reads its blobs from.</summary>
    public SkiaTextBlobCache Blobs => _blobs;

    /// <summary>Gets the cached font for the style; the paint executor draws with the same instance.</summary>
    public SKFont GetFont(FontStyle style) => _fonts.GetOrAdd(style, CreateFont);

    /// <summary>
    /// Gets the font a character is drawn with: the style's own font when it carries the glyph, and
    /// otherwise a system font that does. Measurement and painting both ask here, so a run is never
    /// advanced by a width its glyphs do not fill.
    /// </summary>
    public SKFont GetFont(FontStyle style, Int32 codepoint)
    {
        var primary = GetFont(style);
        return RunFont(style, primary, codepoint) ?? primary;
    }

    /// <summary>
    /// Gets whether any font the style can reach carries the glyph for a code point, the family's own
    /// or a system fallback. A caller asks before substituting one character for another, because a
    /// substitution into a glyph nothing has would draw the box it was meant to avoid.
    /// </summary>
    public Boolean HasGlyph(FontStyle style, Int32 codepoint) =>
        GetFont(style, codepoint).Typeface?.GetGlyph(codepoint) != 0;

    public FontMetrics GetMetrics(FontStyle style) => _metrics.GetOrAdd(style, Measure);

    /// <summary>
    /// Decomposes a stretch of text into the runs a single font draws, one per font change, each
    /// with the glyphs the shaper set it in and the advance it was measured to.
    /// </summary>
    /// <remarks>
    /// This is the one answer measurement and painting both read: the layout folds the advances into
    /// where a line breaks, and the painter folds them into where each run starts. The decomposition
    /// is kept by style and text, so a page's words are split and shaped once rather than per frame.
    /// </remarks>
    public IReadOnlyList<SkiaTextRun> Decompose(String text, FontStyle style)
    {
        if (String.IsNullOrEmpty(text))
        {
            return Array.Empty<SkiaTextRun>();
        }

        return _decompositions.GetOrAdd(
            new DecompositionKey(style, text),
            _ => DecomposeInto(text, style, GetFont(style)));
    }

    /// <summary>
    /// Decomposes a column of upright characters into one cell per character, each resolved into
    /// everything drawing it takes: the font and glyph it ends up set in, where the font puts its
    /// baseline within the cell, and the widths it is stepped and centred by.
    /// </summary>
    /// <remarks>
    /// A column steps by the layout's own arithmetic - one em per character, the spacing added after
    /// it - which a shaped run's natural advances do not follow, so the decomposition keeps the
    /// layout's step beside the glyphs rather than replacing it. The decisions a cell is made of are
    /// the ones a line would make per character: the fallback font that carries the glyph, the
    /// vertical form the font substitutes for it, and the presentation form Unicode reserved when
    /// the font does not answer.
    /// </remarks>
    public IReadOnlyList<UprightCell> DecomposeUpright(String text, FontStyle style)
    {
        if (String.IsNullOrEmpty(text))
        {
            return Array.Empty<UprightCell>();
        }

        return _uprightCells.GetOrAdd(
            new DecompositionKey(style, text),
            _ => DecomposeUprightInto(text, style));
    }

    public Double MeasureWidth(String text, FontStyle style)
    {
        if (String.IsNullOrEmpty(text))
        {
            return 0;
        }

        var width = 0.0;

        foreach (var run in Decompose(text, style))
        {
            width += run.Advance;
        }

        return width;
    }

    public void Dispose()
    {
        foreach (var font in _fonts.Values)
        {
            font.Dispose();
        }

        foreach (var font in _fallbackFonts.Values)
        {
            font.Dispose();
        }

        _fonts.Clear();
        _fallbackFonts.Clear();
        _metrics.Clear();
        _decompositions.Clear();
        _uprightCells.Clear();
        _blobs.Dispose();
        _shaper.Dispose();
        _resolver.Dispose();
    }

    private List<SkiaTextRun> DecomposeInto(String text, FontStyle style, SKFont primary)
    {
        var runs = new List<SkiaTextRun>();
        var font = primary;
        var start = 0;
        var index = 0;

        while (index < text.Length)
        {
            var fallback = RunFont(style, primary, CodepointAt(text, index));

            if (fallback is not null && !ReferenceEquals(fallback, font))
            {
                if (index > start)
                {
                    AddRun(runs, font, text.Substring(start, index - start));
                }

                font = fallback;
                start = index;
            }
            else if (fallback is null && !ReferenceEquals(font, primary))
            {
                if (index > start)
                {
                    AddRun(runs, font, text.Substring(start, index - start));
                }

                font = primary;
                start = index;
            }

            index += WidthAt(text, index);
        }

        AddRun(runs, font, text.Substring(start));

        return runs;
    }

    private void AddRun(List<SkiaTextRun> runs, SKFont font, String text)
    {
        // Every stretch is shaped, whatever script it carries: the width a run is measured to is the
        // width of the glyphs it will be drawn with, so the two sides of the engine read one answer.
        runs.Add(new SkiaTextRun(font, text, _shaper.Shape(font, text)));
    }

    private List<UprightCell> DecomposeUprightInto(String text, FontStyle style)
    {
        var cells = new List<UprightCell>(text.Length);

        foreach (var character in text)
        {
            var characterText = character.ToString();
            var font = GetFont(style, character);
            var vertical = _shaper.ShapeVertical(font, characterText);
            var hasOrigin = vertical.Succeeded && vertical.Glyphs.Length == 1;
            var advance = MeasureWidth(characterText, style);

            // The form the font substitutes for the character is drawn by id, because it need not
            // stand for one a document could name. The cell is still centred by the character's own
            // width, which is the room the layout gave it.
            if (hasOrigin && IsSubstituted(font, character, vertical.Glyphs[0]))
            {
                cells.Add(new UprightCell(
                    font,
                    vertical.Glyphs[0],
                    characterText,
                    drawByGlyph: true,
                    vertical.Points[0].Y,
                    advance,
                    advance));
                continue;
            }

            // A font that has no form of its own is asked for the form Unicode reserved; the form is
            // a glyph of the same font, drawn where the font puts that glyph.
            if (VerticalForms.TryGet(character, out var replacement) && HasGlyph(style, replacement))
            {
                var replacementText = replacement.ToString();
                var replacementFont = GetFont(style, replacement);
                var replacementRun = _shaper.ShapeVertical(replacementFont, replacementText);
                var replacementHasOrigin = replacementRun.Succeeded && replacementRun.Glyphs.Length == 1;
                var centreWidth = MeasureWidth(replacementText, style);

                cells.Add(new UprightCell(
                    replacementFont,
                    0,
                    replacementText,
                    drawByGlyph: false,
                    replacementHasOrigin ? replacementRun.Points[0].Y : null,
                    advance,
                    centreWidth));
                continue;
            }

            // A character with no form at all is drawn as it was, on the baseline the font declared
            // for it when it declared one at all.
            cells.Add(new UprightCell(
                font,
                0,
                characterText,
                drawByGlyph: false,
                hasOrigin ? vertical.Points[0].Y : null,
                advance,
                advance));
        }

        return cells;
    }

    /// <summary>Whether the font answered a vertical run with a glyph of its own rather than the one a line would use.</summary>
    private static Boolean IsSubstituted(SKFont font, Int32 codepoint, UInt16 glyph) =>
        glyph != 0 && font.Typeface?.GetGlyph(codepoint) != glyph;

    /// <summary>
    /// Gets the font from another family that carries the character, or <c>null</c> when the primary
    /// font has the glyph, the character paints nothing anyway, or the platform offers nothing better.
    /// </summary>
    private SKFont? RunFont(FontStyle style, SKFont primary, Int32 codepoint)
    {
        var typeface = primary.Typeface;

        if (typeface is null || DrawsNothing(codepoint) || typeface.GetGlyph(codepoint) != 0)
        {
            return null;
        }

        var face = _resolver.ResolveFallback(style, codepoint);

        // The font is cached by typeface rather than by character, so two characters the same face
        // carries share one font and the run they sit in is not needlessly split between them.
        return face is null
            ? null
            : _fallbackFonts.GetOrAdd(new FallbackFontKey(style, face.Handle), _ => new SKFont(face, (Single)style.Size));
    }

    private FontMetrics Measure(FontStyle style)
    {
        // GetFontMetrics reports the recommended line spacing; the metrics themselves are always
        // filled in. The decoration offsets are optional because a font may not define them.
        GetFont(style).GetFontMetrics(out var metrics);

        return new FontMetrics(
            -metrics.Ascent,
            metrics.Descent,
            metrics.Leading,
            metrics.UnderlinePosition ?? 0,
            metrics.UnderlineThickness ?? 0,
            metrics.StrikeoutPosition ?? 0,
            metrics.StrikeoutThickness ?? 0);
    }

    private SKFont CreateFont(FontStyle style) => new(_resolver.Resolve(style), (Single)style.Size);

    private static Int32 CodepointAt(String text, Int32 index) =>
        Char.IsSurrogatePair(text, index) ? Char.ConvertToUtf32(text, index) : text[index];

    private static Int32 WidthAt(String text, Int32 index) => Char.IsSurrogatePair(text, index) ? 2 : 1;

    /// <summary>Whether the code point paints nothing: a space or a control code has no glyph to want.</summary>
    private static Boolean DrawsNothing(Int32 codepoint) =>
        codepoint <= Char.MaxValue && (Char.IsWhiteSpace((Char)codepoint) || Char.IsControl((Char)codepoint));

    private readonly struct DecompositionKey : IEquatable<DecompositionKey>
    {
        private readonly String _text;

        public DecompositionKey(FontStyle style, String text)
        {
            Style = style;
            _text = text;
        }

        public FontStyle Style { get; }

        public Boolean Equals(DecompositionKey other) =>
            Style.Equals(other.Style) &&
            String.Equals(_text, other._text, StringComparison.Ordinal);

        public override Boolean Equals(Object? obj) => obj is DecompositionKey other && Equals(other);

        public override Int32 GetHashCode()
        {
            unchecked
            {
                return (Style.GetHashCode() * 397) ^ StringComparer.Ordinal.GetHashCode(_text);
            }
        }
    }

    private readonly struct FallbackFontKey : IEquatable<FallbackFontKey>
    {
        private readonly FontStyle _style;

        public FallbackFontKey(FontStyle style, IntPtr typeface)
        {
            _style = style;
            Typeface = typeface;
        }

        public IntPtr Typeface { get; }

        public Boolean Equals(FallbackFontKey other) => Typeface == other.Typeface && _style.Equals(other._style);

        public override Boolean Equals(Object? obj) => obj is FallbackFontKey other && Equals(other);

        public override Int32 GetHashCode() => (_style.GetHashCode() * 397) ^ Typeface.GetHashCode();
    }
}

/// <summary>
/// A stretch of text a single font draws, together with the glyphs the shaper set it in and the
/// advance it was measured to.
/// </summary>
/// <remarks>
/// The decomposition of a text is the one answer measurement and painting both read, so the run
/// carries its shaped form beside its width: a caller who draws the glyphs lands them within the
/// advance another caller laid the run out by.
/// </remarks>
public readonly struct SkiaTextRun
{
    public SkiaTextRun(SKFont font, String text, ShapedRun shaped)
    {
        Font = font;
        Text = text;
        Shaped = shaped;
        Advance = shaped.Succeeded ? shaped.Advance : font.MeasureText(text);
    }

    /// <summary>Gets the font the stretch is measured and drawn with.</summary>
    public SKFont Font { get; }

    /// <summary>Gets the characters of the stretch.</summary>
    public String Text { get; }

    /// <summary>Gets the glyphs the shaper set the stretch in, or <see cref="ShapedRun.None"/> to draw it by the font's own mapping.</summary>
    public ShapedRun Shaped { get; }

    /// <summary>Gets the distance the stretch advances along the baseline, without the letter spacing.</summary>
    public Double Advance { get; }
}

/// <summary>
/// One character of a column, resolved into everything drawing it takes. The cell is stepped by the
/// layout's own arithmetic - <see cref="Advance"/> with the em floor and the spacing applied over it
/// - and its glyph drawn inside that step, so what is drawn lands where the layout measured it.
/// </summary>
public readonly struct UprightCell
{
    public UprightCell(
        SKFont font,
        UInt16 glyph,
        String text,
        Boolean drawByGlyph,
        Single? baselineOffset,
        Double advance,
        Double centreWidth)
    {
        Font = font;
        Glyph = glyph;
        Text = text;
        DrawByGlyph = drawByGlyph;
        BaselineOffset = baselineOffset;
        Advance = advance;
        CentreWidth = centreWidth;
    }

    /// <summary>Gets the font the cell is drawn with, after the fallback and the substitution.</summary>
    public SKFont Font { get; }

    /// <summary>Gets the glyph the font substitutes for the character, when <see cref="DrawByGlyph"/> is set.</summary>
    public UInt16 Glyph { get; }

    /// <summary>Gets the text the cell draws when it is not drawn by glyph id: the character, or the presentation form that stands for it.</summary>
    public String Text { get; }

    /// <summary>Gets whether the cell is drawn by glyph id, which a form the document could not name needs.</summary>
    public Boolean DrawByGlyph { get; }

    /// <summary>
    /// Gets how far below the top of the cell the glyph's baseline sits, when the font declared one;
    /// otherwise the drawing side stands it on the middle of the em box.
    /// </summary>
    public Single? BaselineOffset { get; }

    /// <summary>Gets the advance the layout gave the character along the column, before the em floor and the spacing.</summary>
    public Double Advance { get; }

    /// <summary>Gets the width the cell is centred across the column by.</summary>
    public Double CentreWidth { get; }
}

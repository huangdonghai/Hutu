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
    private readonly SkiaOpenTypeMetrics _openType = new();
    private readonly SkiaTextBlobCache _blobs;
    private readonly ConcurrentDictionary<FontStyle, SKFont> _fonts = new();
    private readonly ConcurrentDictionary<FallbackFontKey, SKFont> _fallbackFonts = new();
    private readonly ConcurrentDictionary<FontStyle, FontMetrics> _metrics = new();
    private readonly ConcurrentDictionary<DecompositionKey, IReadOnlyList<SkiaTextRun>> _decompositions = new();
    private readonly ConcurrentDictionary<DecompositionKey, IReadOnlyList<UprightCell>> _uprightCells = new();

    /// <summary>
    /// How far one character advances in one style, which is what a column of upright text asks once per
    /// character. Kept by character rather than by string because the answer belongs to the pair: a page
    /// of repeated text then costs a table of the characters it actually uses.
    /// </summary>
    private readonly ConcurrentDictionary<CodePointAdvanceKey, Double> _advances = new();

    public SkiaTextMeasurer() : this(null)
    {
    }

    /// <summary>
    /// Creates a measurer over a resolver someone else already built. A host has no reason to reach
    /// it: the typefaces are the engine's business, and the resolver it hands round is <see cref="Fonts"/>.
    /// </summary>
    internal SkiaTextMeasurer(SkiaFontResolver? resolver)
    {
        _resolver = resolver ?? new SkiaFontResolver();
        _blobs = new SkiaTextBlobCache(this);
        _resolver.Registered += Invalidate;
    }

    /// <summary>Gets the resolver this measurer shares its typefaces with.</summary>
    internal SkiaFontResolver Fonts => _resolver;

    /// <summary>Gets the shaper every glyph this measurer speaks of was shaped by.</summary>
    internal SkiaTextShaper Shaper => _shaper;

    /// <summary>Gets the cache of drawn text the paint executor reads its blobs from.</summary>
    internal SkiaTextBlobCache Blobs => _blobs;

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
    /// Gets the metrics a stretch of text is set with: the tallest ascent and descent among the fonts
    /// its glyphs resolve to. The decomposition already knows which face each stretch lands in, so this
    /// is the same answer the painter draws with rather than a second guess about the fallback.
    /// </summary>
    /// <remarks>
    /// The primary font's metrics are the floor - they are what a run of characters it carries is set
    /// with, and what an empty or font-less stretch answers with - and every run the shaper handed to
    /// another face can only raise the line the text stands on.
    /// </remarks>
    public FontMetrics GetRunMetrics(String text, FontStyle style)
    {
        var primary = GetMetrics(style);
        var ascent = primary.Ascent;
        var descent = primary.Descent;
        var gap = primary.LineGap;

        // Every run is read, a lone one included: a text the primary font cannot set at all comes back
        // as a single run in the fallback face, and that face's metrics are the ones the line follows.
        foreach (var run in Decompose(text, style))
        {
            run.Font.GetFontMetrics(out var metrics);
            ascent = Math.Max(ascent, -metrics.Ascent);
            descent = Math.Max(descent, metrics.Descent);
            gap = Math.Max(gap, metrics.Leading);
        }

        if (ascent == primary.Ascent && descent == primary.Descent && gap == primary.LineGap)
        {
            return primary;
        }

        // The decoration offsets stay the primary font's: they describe how the painter underlines the
        // text, which is one line drawn across the run and not one per face it was set in.
        return new FontMetrics(
            ascent,
            descent,
            gap,
            primary.UnderlineOffset,
            primary.UnderlineThickness,
            primary.StrikeoutOffset,
            primary.StrikeoutThickness,
            capHeight: primary.CapHeight);
    }

    /// <summary>
    /// Decomposes a stretch of text into the runs a single font draws, one per font change, each
    /// with the glyphs the shaper set it in and the advance it was measured to.
    /// </summary>
    /// <remarks>
    /// This is the one answer measurement and painting both read: the layout folds the advances into
    /// where a line breaks, and the painter folds them into where each run starts. The decomposition
    /// is kept by style and text, so a page's words are split and shaped once rather than per frame.
    /// <para>
    /// Both decompositions are the backend's own arithmetic: a run names the typeface and the glyph
    /// indices this drawing surface draws with, which is nothing a host or another backend can use.
    /// </para>
    /// </remarks>
    internal IReadOnlyList<SkiaTextRun> Decompose(String text, FontStyle style)
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
    internal IReadOnlyList<UprightCell> DecomposeUpright(String text, FontStyle style)
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

    /// <summary>
    /// Gets how far one code point advances along the axis it runs on. A column steps by this once per
    /// character, so it is answered from a table rather than by writing the character out as a string
    /// and decomposing that again - the same answer, looked up on a key the column never allocates.
    /// </summary>
    /// <remarks>
    /// The key is a code point and not a character, because a column that stepped by characters stood two
    /// cells high for the ideograph written above the BMP: each half of the pair was measured as the
    /// glyph a font keeps for what it has no glyph of, and the character the pair was written for was
    /// never asked about at all.
    /// </remarks>
    public Double MeasureAdvance(Int32 codePoint, FontStyle style, WritingModeInfo flow)
    {
        // The flow is not read, exactly as the string form does not read it: what a character advances
        // along a column is the width of the glyph the style resolves to, and a backend that lets a
        // script say otherwise overrides both forms together.
        _ = flow;

        return _advances.GetOrAdd(
            new CodePointAdvanceKey(style, codePoint),
            _ => MeasureWidth(Char.ConvertFromUtf32(codePoint), style));
    }

    /// <summary>
    /// Lets go of every answer measured or drawn before a fetched face landed: the font, metrics,
    /// decomposition, cell and advance tables, and the drawn blobs built from them.
    /// </summary>
    /// <remarks>
    /// A family is asked for before its font is fetched - the first layout is built the moment the
    /// document is parsed - and that earlier question is answered with a system or default face. The
    /// typeface is thrown away by the resolver when the font registers, but the answers above it are
    /// cached here and in the blob cache, and a relayout that read them would keep drawing the
    /// placeholder the family was given while it was still on the wire.
    /// <para>
    /// The tables are emptied rather than disposed: a paint already underway may still be holding a
    /// font or a blob it was handed, and the faces behind them are the resolver's to keep.
    /// </para>
    /// </remarks>
    private void Invalidate()
    {
        _fonts.Clear();
        _fallbackFonts.Clear();
        _metrics.Clear();
        _decompositions.Clear();
        _uprightCells.Clear();
        _advances.Clear();
        _blobs.Clear();
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
        _advances.Clear();
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

        // Walked by code point, because a column takes one cell to the character of its text and the
        // pair written above the BMP is one character: asked by character, each half was drawn in a cell
        // of its own as the glyph a font keeps for what it has none of.
        for (var position = 0; position < text.Length;)
        {
            var length = WidthAt(text, position);
            var codePoint = CodepointAt(text, position);
            var characterText = text.Substring(position, length);
            var start = position;

            position += length;

            var font = GetFont(style, codePoint);
            var vertical = _shaper.ShapeVertical(font, characterText);
            var hasOrigin = vertical.Succeeded && vertical.Glyphs.Length == 1;
            var advance = MeasureWidth(characterText, style);

            // The form the font substitutes for the character is drawn by id, because it need not
            // stand for one a document could name. The cell is still centred by the character's own
            // width, which is the room the layout gave it.
            if (hasOrigin && IsSubstituted(font, codePoint, vertical.Glyphs[0]))
            {
                cells.Add(new UprightCell(
                    font,
                    vertical.Glyphs[0],
                    characterText,
                    drawByGlyph: true,
                    vertical.Points[0].Y,
                    advance,
                    advance,
                    start));
                continue;
            }

            // A font that has no form of its own is asked for the form Unicode reserved; the form is
            // a glyph of the same font, drawn where the font puts that glyph. The reserved forms are a
            // block of the BMP, so a character from the planes above has no form to be asked for.
            if (codePoint <= Char.MaxValue &&
                VerticalForms.TryGet((Char)codePoint, out var replacement) && HasGlyph(style, replacement))
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
                    centreWidth,
                    start));
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
                advance,
                start));
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
        var font = GetFont(style);
        font.GetFontMetrics(out var metrics);

        return new FontMetrics(
            -metrics.Ascent,
            metrics.Descent,
            metrics.Leading,
            metrics.UnderlinePosition ?? 0,
            metrics.UnderlineThickness ?? 0,
            metrics.StrikeoutPosition ?? 0,
            metrics.StrikeoutThickness ?? 0,
            // Skia's metrics carry no x-height, so the face itself is asked what it stands its lowercase
            // at; a font that does not say leaves the field empty and the layout guesses from the em.
            xHeight: _openType.GetXHeight(font.Typeface, (Single)style.Size) ?? 0,
            capHeight: metrics.CapHeight);
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

    /// <summary>The style and the character whose advance is kept together.</summary>
    private readonly struct CodePointAdvanceKey : IEquatable<CodePointAdvanceKey>
    {
        public CodePointAdvanceKey(FontStyle style, Int32 codePoint)
        {
            Style = style;
            CodePoint = codePoint;
        }

        public FontStyle Style { get; }

        public Int32 CodePoint { get; }

        public Boolean Equals(CodePointAdvanceKey other) =>
            Style.Equals(other.Style) && CodePoint == other.CodePoint;

        public override Boolean Equals(Object? obj) => obj is CodePointAdvanceKey other && Equals(other);

        public override Int32 GetHashCode() => (Style.GetHashCode() * 397) ^ CodePoint;
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
internal readonly struct SkiaTextRun
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
internal readonly struct UprightCell
{
    public UprightCell(
        SKFont font,
        UInt16 glyph,
        String text,
        Boolean drawByGlyph,
        Single? baselineOffset,
        Double advance,
        Double centreWidth,
        Int32 index)
    {
        Font = font;
        Glyph = glyph;
        Text = text;
        DrawByGlyph = drawByGlyph;
        BaselineOffset = baselineOffset;
        Advance = advance;
        CentreWidth = centreWidth;
        Index = index;
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

    /// <summary>
    /// Gets where in the text the character this cell stands for begins. The cell is drawn as the form
    /// the font keeps for it, which need not be the character the document wrote, so the place a
    /// justified line puts its room - a seam in the text, not in the glyphs - is read from here.
    /// </summary>
    public Int32 Index { get; }
}

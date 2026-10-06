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
/// what lets a Latin family carry CJK text without a box per character. The one place shaping is asked
/// for is a column, which needs the glyph a font sets a character in when it stands upright; plain
/// advance widths are accurate for the Latin and CJK samples this stage targets.
/// </remarks>
public sealed class SkiaTextMeasurer : ITextMeasurer, IDisposable
{
    private readonly SkiaFontResolver _resolver;
    private readonly SkiaTextShaper _shaper = new();
    private readonly ConcurrentDictionary<FontStyle, SKFont> _fonts = new();
    private readonly ConcurrentDictionary<FallbackFontKey, SKFont> _fallbackFonts = new();
    private readonly ConcurrentDictionary<FontStyle, FontMetrics> _metrics = new();

    public SkiaTextMeasurer(SkiaFontResolver? resolver = null) => _resolver = resolver ?? new SkiaFontResolver();

    /// <summary>Gets the resolver this measurer shares its typefaces with.</summary>
    public SkiaFontResolver Fonts => _resolver;

    /// <summary>Gets the shaper that answers what glyph a column sets a character in.</summary>
    public SkiaTextShaper Shaper => _shaper;

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

    public Double MeasureWidth(String text, FontStyle style)
    {
        if (String.IsNullOrEmpty(text))
        {
            return 0;
        }

        var primary = GetFont(style);

        // The common case is a run the family covers whole, which needs no split and no allocation.
        if (!NeedsFallback(text, primary))
        {
            return primary.MeasureText(text);
        }

        var width = 0.0;

        foreach (var run in Runs(text, style, primary))
        {
            width += run.Font.MeasureText(run.Text);
        }

        return width;
    }

    /// <summary>
    /// Splits the text into the stretches a single font draws, one per font change. The paint executor
    /// walks the very same split, which is what keeps what is drawn on the width it was measured with.
    /// </summary>
    public IReadOnlyList<SkiaTextRun> GetRuns(String text, FontStyle style)
    {
        if (String.IsNullOrEmpty(text))
        {
            return Array.Empty<SkiaTextRun>();
        }

        return Runs(text, style, GetFont(style));
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
        _shaper.Dispose();
        _resolver.Dispose();
    }

    private List<SkiaTextRun> Runs(String text, FontStyle style, SKFont primary)
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
                    runs.Add(new SkiaTextRun(font, text.Substring(start, index - start)));
                }

                font = fallback;
                start = index;
            }
            else if (fallback is null && !ReferenceEquals(font, primary))
            {
                if (index > start)
                {
                    runs.Add(new SkiaTextRun(font, text.Substring(start, index - start)));
                }

                font = primary;
                start = index;
            }

            index += WidthAt(text, index);
        }

        runs.Add(new SkiaTextRun(font, text.Substring(start)));

        return runs;
    }

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

    private static Boolean NeedsFallback(String text, SKFont primary)
    {
        var typeface = primary.Typeface;

        if (typeface is null)
        {
            return false;
        }

        var index = 0;

        while (index < text.Length)
        {
            var codepoint = CodepointAt(text, index);

            if (!DrawsNothing(codepoint) && typeface.GetGlyph(codepoint) == 0)
            {
                return true;
            }

            index += WidthAt(text, index);
        }

        return false;
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

/// <summary>A stretch of text a single font draws, together with that font.</summary>
public readonly struct SkiaTextRun
{
    public SkiaTextRun(SKFont font, String text)
    {
        Font = font;
        Text = text;
    }

    /// <summary>Gets the font the stretch is measured and drawn with.</summary>
    public SKFont Font { get; }

    /// <summary>Gets the characters of the stretch.</summary>
    public String Text { get; }
}

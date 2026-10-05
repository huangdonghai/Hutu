using Hutu.Rendering.Layout;
using SkiaSharp;
using System;
using System.Collections.Concurrent;

namespace Hutu.Skia;

/// <summary>
/// Skia-backed implementation of <see cref="ITextMeasurer"/>: font metrics and advance widths.
/// </summary>
/// <remarks>
/// Fonts and metrics are created on demand and cached by <see cref="FontStyle"/>, because the
/// layout engine asks for both per text fragment and the underlying calls cross into native code.
/// The fonts are also shared with the paint executor, so a run is measured and drawn with the very
/// same font. Shaping through HarfBuzz is layered on when complex scripts and vertical writing are
/// implemented; plain advance widths are accurate for the Latin and CJK samples this stage targets.
/// </remarks>
public sealed class SkiaTextMeasurer : ITextMeasurer, IDisposable
{
    private readonly SkiaFontResolver _resolver;
    private readonly ConcurrentDictionary<FontStyle, SKFont> _fonts = new();
    private readonly ConcurrentDictionary<FontStyle, FontMetrics> _metrics = new();

    public SkiaTextMeasurer(SkiaFontResolver? resolver = null) => _resolver = resolver ?? new SkiaFontResolver();

    /// <summary>Gets the resolver this measurer shares its typefaces with.</summary>
    public SkiaFontResolver Fonts => _resolver;

    /// <summary>Gets the cached font for the style; the paint executor draws with the same instance.</summary>
    public SKFont GetFont(FontStyle style) => _fonts.GetOrAdd(style, CreateFont);

    public FontMetrics GetMetrics(FontStyle style) => _metrics.GetOrAdd(style, Measure);

    public Double MeasureWidth(String text, FontStyle style)
    {
        if (String.IsNullOrEmpty(text))
        {
            return 0;
        }

        return GetFont(style).MeasureText(text);
    }

    public void Dispose()
    {
        foreach (var font in _fonts.Values)
        {
            font.Dispose();
        }

        _fonts.Clear();
        _metrics.Clear();
        _resolver.Dispose();
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
}

using HarfBuzzSharp;
using SkiaSharp;
using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;

namespace Hutu.Skia;

/// <summary>
/// Reads what the face itself says about its heights, which the metrics Skia hands back do not cover:
/// Skia's font metrics carry no x-height or typographic em box, which
/// <c>vertical-align: middle</c> and a horizontal under-underline are measured from.
/// </summary>
/// <remarks>
/// HarfBuzz opens the face and supplies the named tables the platform interface does not expose.
/// Nothing of it is kept: the face is opened over the memory the typeface owns, the numbers wanted
/// are read out of it, and every native handle is closed again before the call returns, so no cached
/// answer can outlive the font it came from. A face that answers nothing is reported as no number at
/// all rather than guessed at, so the caller falls back to its own approximation.
/// </remarks>
internal sealed class SkiaOpenTypeMetrics
{
    private readonly ConcurrentDictionary<IntPtr, Double?> _xHeights = new();
    private readonly ConcurrentDictionary<IntPtr, Double?> _emDescents = new();

    /// <summary>
    /// Gets the distance from the baseline to the top of a lowercase letter, in pixels at the size the
    /// font is drawn, or null when the face does not say.
    /// </summary>
    public Double? GetXHeight(SKTypeface? typeface, Single size)
    {
        if (typeface is null)
        {
            return null;
        }

        // The ratio is what the face states and the size is what the caller asks, so the reading is
        // kept by typeface and scaled again on every answer; a face is opened once per font, not once
        // per style drawn with it.
        var ratio = _xHeights.GetOrAdd(typeface.Handle, _ => ReadXHeight(typeface));

        return ratio is null ? null : ratio.Value * size;
    }

    /// <summary>
    /// Gets the distance from the baseline to the bottom of the normalized typographic em box, in
    /// pixels at the size the font is drawn. A face without OS/2 typographic metrics falls back to
    /// the ascent and descent it was measured with.
    /// </summary>
    public Double GetEmDescent(
        SKTypeface? typeface,
        Single size,
        Double ascent,
        Double descent)
    {
        if (size <= 0)
        {
            return 0;
        }

        var ratio = typeface is null
            ? null
            : _emDescents.GetOrAdd(typeface.Handle, _ => ReadEmDescent(typeface));

        ratio ??= ascent + descent > 0 ? descent / (ascent + descent) : 0;

        return ratio.Value * size;
    }

    /// <summary>
    /// Gets the x-height as a fraction of the em, which is the only form the face's own numbers have.
    /// </summary>
    private static Double? ReadXHeight(SKTypeface typeface)
    {
        try
        {
            using var stream = typeface.OpenStream();

            if (stream is null || stream.GetMemoryBase() == IntPtr.Zero || stream.Length <= 0)
            {
                return null;
            }

            // The blob borrows the stream's memory rather than copying it, which is safe only because
            // everything read from it happens while the stream is still standing open below it.
            using var blob = new Blob(stream.GetMemoryBase(), (Int32)stream.Length, MemoryMode.ReadOnly);
            using var face = new Face(blob, 0);
            using var font = new Font(face);

            // The number is in the face's own design units, so the em it was drawn on is what turns it
            // into a fraction: Ahem states eight hundred on a thousand, which is its whole 0.8em box.
            if (!font.OpenTypeMetrics.TryGetPosition(OpenTypeMetricsTag.XHeight, out var position) ||
                position <= 0 || face.UnitsPerEm <= 0)
            {
                return null;
            }

            return position / (Double)face.UnitsPerEm;
        }
        catch (Exception)
        {
            // A face that will not open is a font with no x-height to ask, which is what the caller
            // already handles for the fonts that never filled the field in.
            return null;
        }
    }

    /// <summary>
    /// Reads the normalized proportion the OS/2 table gives to the bottom of the em box. This is the
    /// metric a horizontal <c>text-underline-position: under</c> resolves against, not the font's
    /// hhea descent.
    /// </summary>
    private static Double? ReadEmDescent(SKTypeface typeface)
    {
        try
        {
            using var stream = typeface.OpenStream(out var index);

            if (stream is null || stream.GetMemoryBase() == IntPtr.Zero || stream.Length <= 0)
            {
                return null;
            }

            using var blob = new Blob(stream.GetMemoryBase(), (Int32)stream.Length, MemoryMode.ReadOnly);
            using var face = new Face(blob, index);
            using var table = face.ReferenceTable(Tag.Parse("OS/2"));

            // sTypoAscender and sTypoDescender sit at fixed offsets in every OS/2 table version.
            if (table.Length < 72 || face.UnitsPerEm <= 0)
            {
                return null;
            }

            var data = table.AsSpan();
            var ascent = BinaryPrimitives.ReadInt16BigEndian(data[68..70]);
            var descent = BinaryPrimitives.ReadInt16BigEndian(data[70..72]);
            var height = ascent - descent;

            return ascent > 0 && descent <= 0 && height > 0
                ? -descent / (Double)height
                : null;
        }
        catch (Exception)
        {
            return null;
        }
    }
}

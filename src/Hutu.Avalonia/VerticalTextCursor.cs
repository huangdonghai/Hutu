using Avalonia;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Hutu.Skia;
using System;
using System.Collections.Generic;
using System.IO;

namespace Hutu.Avalonia;

/// <summary>
/// The pointer a column of text is read with: an I-beam turned on its side.
/// </summary>
/// <remarks>
/// Avalonia's standard cursors stop at the upright I-beam, so the turned one is drawn and handed over
/// as a bitmap. A drawn cursor is taken at the pixel size it was drawn at, with nothing scaling it to
/// the display, so one drawn for an unscaled screen comes out at a fraction of the size of the
/// platform's own cursors standing beside it on a screen that scales. It is therefore drawn for the
/// scale it will be shown at, and one is kept for each scale in turn.
/// <para>
/// Nothing about it is essential - it says which way the text runs rather than what may be done with
/// it - so a backend that cannot take a drawn cursor answers null and the reader keeps the upright one.
/// </para>
/// </remarks>
internal static class VerticalTextCursor
{
    /// <summary>The size a pointer is drawn at on an unscaled display, which is the size the platform draws its own at.</summary>
    private const Int32 Size = 20;

    // One cursor per size, so a move over a column asks for the object it asked for last time: the
    // property one is assigned to compares by reference, and a cursor drawn afresh per move would
    // both allocate and tell the platform to rebuild its pointer every time.
    private static readonly Dictionary<Int32, Cursor?> Built = new();

    /// <summary>
    /// Gets the turned I-beam drawn for a display, or null when this backend cannot take a drawn
    /// cursor.
    /// </summary>
    /// <param name="scaling">How many pixels the display spends on a point.</param>
    public static Cursor? For(Double scaling)
    {
        var pixels = Math.Max(4, (Int32)Math.Round(Size * (scaling > 0 ? scaling : 1)));

        lock (Built)
        {
            if (!Built.TryGetValue(pixels, out var cursor))
            {
                Built[pixels] = cursor = Create(pixels);
            }

            return cursor;
        }
    }

    private static Cursor? Create(Int32 pixels)
    {
        try
        {
            using var stream = new MemoryStream(CursorImages.VerticalIBeam(pixels));
            var bitmap = new Bitmap(stream);

            // The hot spot is the middle of the stem, which is the point the pointer stands for and
            // where an I-beam is read from.
            return new Cursor(bitmap, new PixelPoint(bitmap.PixelSize.Width / 2, bitmap.PixelSize.Height / 2));
        }
        catch (Exception)
        {
            return null;
        }
    }
}

using Avalonia;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Hutu.Skia;
using System;
using System.IO;

namespace Hutu.Avalonia;

/// <summary>
/// The pointer a column of text is read with: an I-beam turned on its side.
/// </summary>
/// <remarks>
/// Avalonia's standard cursors stop at the upright I-beam, so the turned one is drawn and handed over
/// as a bitmap. Nothing about it is essential - it says which way the text runs rather than what may
/// be done with it - so a backend that cannot take a drawn cursor answers null and the reader keeps
/// the upright one.
/// </remarks>
internal static class VerticalTextCursor
{
    private static readonly Lazy<Cursor?> Instance = new(Create);

    /// <summary>Gets the turned I-beam, or null when this backend cannot take a drawn cursor.</summary>
    public static Cursor? Current => Instance.Value;

    private static Cursor? Create()
    {
        try
        {
            using var stream = new MemoryStream(CursorImages.VerticalIBeam());
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

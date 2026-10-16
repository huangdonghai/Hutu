using Hutu.Rendering.Media;
using SkiaSharp;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Hutu.Skia;

/// <summary>
/// Fetches the fonts a document links to and hands the typefaces to the resolver, ahead of layout.
/// </summary>
/// <remarks>
/// A family a page names in <c>@font-face</c> is a name the platform has never heard of, and the font
/// behind it does not exist until it has been fetched. This is the piece that makes it exist: the
/// bytes come back, are turned into an SFNT file if they were packed, and the typeface is registered
/// under the name the page asked for.
/// <para>
/// The sources of a family are tried in the order the rule wrote them, and the first one that can be
/// activated wins - which is what CSS Fonts 3 §4.3 asks for. A source that cannot be fetched, or whose
/// bytes cannot be turned into a font, is passed over rather than failing the family: a sheet that
/// lists four sources is a sheet prepared for exactly this.
/// </para>
/// <para>
/// A family already held is never asked for again, so a relayout - or a second document that links the
/// same font - costs nothing.
/// </para>
/// </remarks>
public sealed class SkiaFontLoader : IFontLoader
{
    private readonly SkiaFontResolver _fonts;

    /// <summary>
    /// Creates a loader over the resolver a measurer already built, so the typefaces it registers are
    /// the ones measurement and painting both read.
    /// </summary>
    public SkiaFontLoader(SkiaTextMeasurer measurer) => _fonts = measurer.Fonts;

    public async Task LoadAsync(
        IReadOnlyList<FontRequest> faces,
        ResourceFetcher fetch,
        CancellationToken cancellationToken)
    {
        for (var i = 0; i < faces.Count; i++)
        {
            var face = faces[i];

            if (_fonts.Holds(face.Family))
            {
                continue;
            }

            Byte[]? bytes = null;

            if (face.Url.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                // A font baked into the sheet is not fetched - it is already here, in the URL itself.
                bytes = Data(face.Url);
            }
            else
            {
                try
                {
                    using var stream = await fetch(face.Url, cancellationToken).ConfigureAwait(false);

                    if (stream is null)
                    {
                        continue;
                    }

                    using var sink = new MemoryStream();
                    await stream.CopyToAsync(sink, cancellationToken).ConfigureAwait(false);
                    bytes = sink.ToArray();
                }
                catch (Exception)
                {
                    // One source that cannot be fetched is not the page failing: the next one is tried.
                }
            }

            if (bytes is null || FontDecoder.ToSfnt(bytes) is not { } sfnt)
            {
                continue;
            }

            var typeface = SKTypeface.FromStream(new MemoryStream(sfnt));

            if (typeface is null)
            {
                continue;
            }

            _fonts.Register(face.Family, typeface);
        }
    }

    /// <summary>
    /// Gets the bytes a <c>data:</c> URL carries. Only the base64 spelling is read, which is the one
    /// every sheet that embeds a font uses - the percent-encoded spelling would need a whole URL
    /// decoder for a case nobody writes.
    /// </summary>
    private static Byte[]? Data(String url)
    {
        var comma = url.IndexOf(',');

        if (comma < 0 || !url[..comma].Contains("base64", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        try
        {
            return Convert.FromBase64String(url[(comma + 1)..]);
        }
        catch (Exception)
        {
            return null;
        }
    }
}

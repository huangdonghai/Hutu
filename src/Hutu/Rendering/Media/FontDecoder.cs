namespace Hutu.Rendering.Media;

/// <summary>
/// Turns whatever shape a font file arrives in into the shape the platform reads: an SFNT file.
/// </summary>
/// <remarks>
/// A font a page links to may be a plain TrueType or OpenType file, or the same file packed as WOFF
/// 1.0 or WOFF 2.0. Which it is can only be told from the file itself - the <c>format()</c> hint in
/// the <c>src</c> descriptor is a hint and nothing more - so the signature at the front of it is what
/// decides. A file that carries none of the two signatures is assumed to be unpacked already.
/// <para>
/// This is public because it is a service a host needs: the host owns the fetching, and what it gets
/// back is bytes it cannot hand to a font manager until they have been through here.
/// </para>
/// </remarks>
public static class FontDecoder
{
    private const UInt32 Woff2 = 0x774F4632; // 'wOF2'
    private const UInt32 Woff1 = 0x774F4646; // 'wOFF'

    /// <summary>
    /// Gets the SFNT font the given file holds, or null when it is neither a packable font nor a font
    /// at all - which a caller reads as a source that failed, and moves on to the next one.
    /// </summary>
    public static Byte[]? ToSfnt(Byte[] file)
    {
        if (file.Length < 4)
        {
            return null;
        }

        var signature = ((UInt32)file[0] << 24) | ((UInt32)file[1] << 16) | ((UInt32)file[2] << 8) | file[3];

        return signature switch
        {
            Woff2 => Woff2Decoder.ToSfnt(file),
            Woff1 => Woff1Decoder.ToSfnt(file),
            _ => file,
        };
    }
}

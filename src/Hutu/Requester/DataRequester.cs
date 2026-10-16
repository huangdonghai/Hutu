using AngleSharp;
using AngleSharp.Io;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Hutu;

/// <summary>
/// Serves a <c>data:</c> URL, whose resource is not fetched from anywhere: the URL carries it.
/// </summary>
/// <remarks>
/// RFC 2397 spells one as <c>data:[&lt;mediatype&gt;][;base64],&lt;data&gt;</c> - the media type
/// defaults to <c>text/plain;charset=US-ASCII</c>, and the payload is percent-encoded bytes except in
/// the <c>base64</c> spelling, which is the only form that survives a binary picture.
/// <para>
/// A page that bakes a picture into its markup has said everything there is to say about it: the
/// picture is as reproducible as the markup around it, which is why a deterministic drawing can load
/// one and why a picture declared this way must not be the one picture that never arrives. It is also
/// how a placeholder keeps its box before the real picture lands - an inline SVG whose size the page
/// states - and a box sized to the specification's default object size instead would make the page
/// jump the moment the picture it stood in for arrived.
/// </para>
/// </remarks>
internal sealed class DataRequester : BaseRequester
{
    private const String Prefix = "data:";

    /// <summary>The media type RFC 2397 assumes when the URL names none.</summary>
    private const String DefaultMediaType = "text/plain;charset=US-ASCII";

    public override bool SupportsProtocol(string protocol)
    {
        return string.Equals(protocol, "data", StringComparison.OrdinalIgnoreCase);
    }

    protected override Task<IResponse?> PerformRequestAsync(
        Request request,
        CancellationToken cancel)
    {
        cancel.ThrowIfCancellationRequested();

        var url = request.Address;

        if (!SupportsProtocol(url.Scheme))
            return Task.FromResult<IResponse?>(null);

        // The payload is read from the href rather than from a component of it: a data URL has no
        // authority and no path, so everything after the scheme is exactly what the page wrote.
        if (!TryDecode(url.Href, out var mediaType, out var bytes))
            return Task.FromResult<IResponse?>(null);

        var response = VirtualResponse.Create(response => response
            .Address(url)
            .Status(HttpStatusCode.OK)
            .Header("Content-Type", mediaType)
            .Content(new MemoryStream(bytes), shouldDispose: true));

        return Task.FromResult<IResponse?>(response);
    }

    /// <summary>
    /// Splits a data URL into its media type and the bytes it carries, or reports that it cannot be
    /// read as one.
    /// </summary>
    private static Boolean TryDecode(String url, out String mediaType, out Byte[] bytes)
    {
        mediaType = DefaultMediaType;
        bytes = Array.Empty<Byte>();

        if (!url.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // No comma means no payload; it is also the separator that tells a payload beginning with a
        // comma from a header that does not.
        var comma = url.IndexOf(',', Prefix.Length);

        if (comma < 0)
        {
            return false;
        }

        var header = url[Prefix.Length..comma];
        var payload = url[(comma + 1)..];

        if (header.EndsWith(";base64", StringComparison.OrdinalIgnoreCase))
        {
            header = header[..^";base64".Length];

            try
            {
                bytes = Convert.FromBase64String(payload);
            }
            catch (FormatException)
            {
                // A payload that claims base64 and is not has nothing to render; it fails the load the
                // way a truncated picture does rather than taking the page down with it.
                return false;
            }
        }
        else
        {
            bytes = Unescape(payload);
        }

        var semicolon = header.IndexOf(';');
        mediaType = semicolon >= 0 ? header[..semicolon] : header;

        if (String.IsNullOrWhiteSpace(mediaType))
        {
            mediaType = DefaultMediaType;
        }

        return true;
    }

    /// <summary>
    /// Turns the percent-encoded spelling back into the bytes it stands for, one byte per
    /// <c>%XX</c> and one for every character that got through unescaped.
    /// </summary>
    /// <remarks>
    /// It works on bytes rather than on a decoded string because only the URL is text: a UTF-8
    /// sequence arrives as several <c>%XX</c> escapes, and putting each of them back one at a time is
    /// what keeps a multi-byte character whole. <c>+</c> is left as it stands - that spelling of a
    /// space belongs to form submissions, not to a data URL, and turning it round would corrupt the
    /// plenty of payloads whose markup or base64 contains one.
    /// </remarks>
    private static Byte[] Unescape(String payload)
    {
        var bytes = new List<Byte>(payload.Length);

        for (var i = 0; i < payload.Length; i++)
        {
            var character = payload[i];

            if (character == '%' && i + 2 < payload.Length)
            {
                var high = Hex(payload[i + 1]);
                var low = Hex(payload[i + 2]);

                if (high >= 0 && low >= 0)
                {
                    bytes.Add((Byte)((high << 4) | low));
                    i += 2;
                    continue;
                }
            }

            if (character < 0x80)
            {
                bytes.Add((Byte)character);
            }
            else
            {
                // A character that was never escaped is carried as its own UTF-8 bytes rather than
                // refused: a page is free to write one, and that is what the header's encoding means.
                bytes.AddRange(Encoding.UTF8.GetBytes(character.ToString()));
            }
        }

        return bytes.ToArray();
    }

    /// <summary>Reads one hexadecimal digit, or -1 when the character is not one.</summary>
    private static Int32 Hex(Char character)
    {
        if (character >= '0' && character <= '9')
            return character - '0';

        if (character >= 'a' && character <= 'f')
            return character - 'a' + 10;

        if (character >= 'A' && character <= 'F')
            return character - 'A' + 10;

        return -1;
    }
}

using System;
using System.IO;

namespace Hutu;

/// <summary>
/// Maps a name onto the content type its response should carry. It is shared by the requesters that
/// serve resources from somewhere other than the network, so a local file and an embedded resource
/// of the same extension are reported identically.
/// </summary>
internal static class ContentTypes
{
    public static String FromPath(String path)
    {
        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".html" => "text/html",
            ".htm" => "text/html",
            ".xhtml" => "application/xhtml+xml",
            ".css" => "text/css",
            ".js" => "text/javascript",
            ".mjs" => "text/javascript",
            ".json" => "application/json",
            ".xml" => "application/xml",
            ".svg" => "image/svg+xml",
            ".png" => "image/png",
            ".jpg" => "image/jpeg",
            ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            ".ico" => "image/x-icon",
            ".bmp" => "image/bmp",
            ".woff" => "font/woff",
            ".woff2" => "font/woff2",
            ".ttf" => "font/ttf",
            ".otf" => "font/otf",
            _ => "application/octet-stream"
        };
    }
}

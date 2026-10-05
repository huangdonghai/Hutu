using AngleSharp;
using AngleSharp.Dom;
using AngleSharp.Io;
using System;
using System.IO;
using System.Net;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace Hutu;

public sealed class EmbeddedResourceRequester : BaseRequester
{
    private readonly Assembly _assembly;
    private readonly string _folder;
    private readonly string _protocol = "resource";

    public EmbeddedResourceRequester(Assembly assembly, string? folder = null)
    {
        _assembly = assembly ?? throw new ArgumentNullException(nameof(assembly));
        _folder = folder ?? "";
    }

    public override bool SupportsProtocol(string protocol)
    {
        return string.Equals(protocol, "res", StringComparison.OrdinalIgnoreCase);
    }

    private string UrlToPath(Url url)
    {
        var path = _assembly.GetName().Name + ".";
        path += string.IsNullOrEmpty(_folder) ? "" : _folder + ".";
        path += Uri.UnescapeDataString(url.Data).TrimStart('/').Replace('/', '.');
        return path;
    }

    protected override Task<IResponse?> PerformRequestAsync(
        Request request,
        CancellationToken cancel)
    {
        cancel.ThrowIfCancellationRequested();

        var url = request.Address;

        if (!SupportsProtocol(url.Scheme))
            return Task.FromResult<IResponse?>(null);

        var resourceName = UrlToPath(url);

        if (resourceName.Length == 0)
            return Task.FromResult<IResponse?>(null);

        var stream = _assembly.GetManifestResourceStream(resourceName);

        if (stream is null)
            return Task.FromResult<IResponse?>(null);

        var response = VirtualResponse.Create(response => response
            .Address(url)
            .Status(HttpStatusCode.OK)
            .Header("Content-Type", GetContentType(resourceName))
            .Content(stream, shouldDispose: true));

        return Task.FromResult<IResponse?>(response);
    }

    private static string GetContentType(string resourceName)
    {
        return Path.GetExtension(resourceName).ToLowerInvariant() switch
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

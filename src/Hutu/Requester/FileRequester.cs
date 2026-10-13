using AngleSharp;
using AngleSharp.Dom;
using AngleSharp.Io;
using System;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace Hutu;

/// <summary>
/// Serves a document and its resources straight off the file system, so a <c>file://</c> URL can be
/// opened without a server in between, and so a page loaded from disk can still pull in the style
/// sheets and images it refers to relatively.
/// </summary>
internal sealed class FileRequester : BaseRequester
{
    public override bool SupportsProtocol(string protocol)
    {
        return string.Equals(protocol, "file", StringComparison.OrdinalIgnoreCase);
    }

    protected override Task<IResponse?> PerformRequestAsync(
        Request request,
        CancellationToken cancel)
    {
        cancel.ThrowIfCancellationRequested();

        var url = request.Address;

        if (!SupportsProtocol(url.Scheme))
            return Task.FromResult<IResponse?>(null);

        var path = UrlToPath(url);

        if (path is null || !File.Exists(path))
            return Task.FromResult<IResponse?>(null);

        var response = VirtualResponse.Create(response => response
            .Address(url)
            .Status(HttpStatusCode.OK)
            .Header("Content-Type", ContentTypes.FromPath(path))
            .Content(File.OpenRead(path), shouldDispose: true));

        return Task.FromResult<IResponse?>(response);
    }

    /// <summary>
    /// Turns the URL back into a path: <c>file:///C:/a/b.htm</c> becomes <c>C:\a\b.htm</c>, while the
    /// host of a UNC URL is kept as the share prefix so <c>file://server/share/b.htm</c> still names
    /// the file it looks like it names.
    /// </summary>
    private static string? UrlToPath(Url url)
    {
        if (string.IsNullOrEmpty(url.Path))
            return null;

        var path = Uri.UnescapeDataString(url.Path).Replace('/', Path.DirectorySeparatorChar);

        if (!string.IsNullOrEmpty(url.HostName) && !path.StartsWith(@"\\", StringComparison.Ordinal))
            return $@"\\{url.HostName}{path}";

        return path.Length > 0 && path[0] == Path.DirectorySeparatorChar ? path[1..] : path;
    }
}

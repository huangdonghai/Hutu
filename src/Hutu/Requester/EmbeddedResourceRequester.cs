using AngleSharp;
using AngleSharp.Dom;
using AngleSharp.Io;
using System;
using System.Net;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace Hutu;

public sealed class EmbeddedResourceRequester : BaseRequester
{
    private readonly Assembly _assembly;
    private readonly string _folder;

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
            .Header("Content-Type", ContentTypes.FromPath(resourceName))
            .Content(stream, shouldDispose: true));

        return Task.FromResult<IResponse?>(response);
    }
}

using AngleSharp.Dom;
using AngleSharp.Io;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Hutu;

public sealed class HookHttpRequest : BaseRequester
{
    private readonly IEnumerable<IRequester> _resourceRequesters;
    private readonly DefaultHttpRequester _defaultHttpRequester = new();

    public HookHttpRequest(IEnumerable<IRequester> resourceRequesters)
    {
        _resourceRequesters = resourceRequesters;
    }

    public override bool SupportsProtocol(string protocol)
    {
        return protocol is "http" or "https";
    }

    protected override async Task<IResponse?> PerformRequestAsync(
        Request request,
        CancellationToken cancel)
    {
        var address = request.Address;
        var targetProtocol = address.HostName.ToLowerInvariant() switch
        {
            "res" => "res",
            "epub" => "epub",
            _ => null
        };

        if (targetProtocol is null)
            return await _defaultHttpRequester.RequestAsync(request, cancel).ConfigureAwait(false);

        var targetAddress = new Url($"{targetProtocol}:{address.Path}");
        var resourceRequest = new Request
        {
            Address = targetAddress,
            Method = request.Method,
            Headers = request.Headers,
            Content = request.Content
        };

        foreach (var requester in _resourceRequesters.Where(requester => requester.SupportsProtocol(targetProtocol)))
        {
            var response = await requester.RequestAsync(resourceRequest, cancel).ConfigureAwait(false);
            if (response is not null)
                return response;
        }

        return null;
    }
}

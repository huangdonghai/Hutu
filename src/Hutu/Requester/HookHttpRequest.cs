using AngleSharp.Dom;
using AngleSharp.Io;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Hutu;

internal sealed class HookHttpRequest : BaseRequester
{
    /// <summary>
    /// How long a host has to answer. AngleSharp's requester waits 45s, which is a lifetime for a
    /// picture: a page that refers to one server that has stopped answering would hold its whole
    /// load - and a reader with it - for three quarters of a minute. A real page on a real line
    /// answers in seconds, and what does not is not going to.
    /// </summary>
    private static readonly TimeSpan AnswerTimeout = TimeSpan.FromSeconds(15);

    private readonly IEnumerable<IRequester> _resourceRequesters;
    private readonly DefaultHttpRequester _defaultHttpRequester;

    /// <summary>
    /// Creates the requester, asking pages as <paramref name="userAgent"/> - or as AngleSharp's own
    /// agent when none is given, which is what a page's server-side skin detection sees.
    /// </summary>
    public HookHttpRequest(IEnumerable<IRequester> resourceRequesters, String? userAgent = null)
    {
        _resourceRequesters = resourceRequesters;
        _defaultHttpRequester = userAgent is { Length: > 0 } agent
            ? new DefaultHttpRequester(agent) { Timeout = AnswerTimeout }
            : new DefaultHttpRequester { Timeout = AnswerTimeout };
    }

    /// <summary>Gets or sets the agent the next request is announced as.</summary>
    public String UserAgent
    {
        get => _defaultHttpRequester.Headers["User-Agent"];
        set => _defaultHttpRequester.Headers["User-Agent"] = value;
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

using AngleSharp;
using AngleSharp.Dom;
using AngleSharp.Io;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Hutu;

/// <summary>
/// Loads the resources AngleSharp needs to build a document, but leaves pictures to the engine.
/// </summary>
/// <remarks>
/// AngleSharp treats every <c>img</c> as a loadable resource and delays the document until each
/// request comes back. The bytes are not what this reader draws from: the host's
/// <see cref="Rendering.Media.IImageLoader"/>
/// fetches and decodes them later, through the same requesters and the image cache. Letting both
/// load them makes every picture cost a request in each half of opening a page - and the first half
/// has no use for the answer.
/// </remarks>
internal sealed class HutuResourceLoader : IResourceLoader
{
    private readonly IResourceLoader _inner;

    public HutuResourceLoader(IBrowsingContext context)
        : this(new DefaultResourceLoader(context))
    {
    }

    internal HutuResourceLoader(IResourceLoader inner)
    {
        _inner = inner;
    }

    public IDownload FetchAsync(ResourceRequest request) =>
        IsPicture(request.Source)
            ? new SkippedDownload(request.Target, request.Source)
            : _inner.FetchAsync(request);

    public IEnumerable<IDownload> GetDownloads() => _inner.GetDownloads();

    /// <summary>
    /// Gets whether an element's resource is a picture the renderer fetches itself rather than one
    /// AngleSharp has to see. The image input is the other element that asks for image data.
    /// </summary>
    internal static Boolean IsPicture(IElement source) =>
        source.LocalName.Equals("img", StringComparison.OrdinalIgnoreCase) ||
        (source.LocalName.Equals("input", StringComparison.OrdinalIgnoreCase) &&
         source.GetAttribute("type")?.Equals("image", StringComparison.OrdinalIgnoreCase) == true);

    private sealed class SkippedDownload : IDownload
    {
        public SkippedDownload(Url target, Object? source)
        {
            Target = target;
            Source = source;
        }

        public Task<IResponse> Task { get; } = System.Threading.Tasks.Task.FromResult(default(IResponse)!);

        public Url Target { get; }

        public Object? Source { get; }

        public Boolean IsCompleted => true;

        public Boolean IsRunning => false;

        public void Cancel()
        {
        }
    }
}

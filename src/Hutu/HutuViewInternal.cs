using AngleSharp;
using AngleSharp.Css;
using AngleSharp.Css.Dom;
using AngleSharp.Dom;
using AngleSharp.Css.RenderTree;
using AngleSharp.Io;
using Hutu.Rendering;
using Hutu.Rendering.Layout;
using Hutu.Rendering.Media;
using Hutu.Rendering.Paint;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace Hutu;

public sealed class HutuViewInternal : IDisposable
{
    private readonly List<IRequester> _resourceRequesters = new();
    private readonly HutuRenderDevice _renderDevice = new();
    private readonly RenderingPipeline _pipeline;
    private readonly IImageLoader? _imageLoader;
    private IBrowsingContext? _context;

    /// <summary>
    /// Creates the renderer. The text measurer and the image loader are supplied by the platform
    /// backend: without the measurer the pipeline stops at the box tree, and without the loader
    /// images are sized from their attributes alone.
    /// </summary>
    public HutuViewInternal(ITextMeasurer? textMeasurer = null, IImageLoader? imageLoader = null)
    {
        _pipeline = new RenderingPipeline(_renderDevice, textMeasurer, imageLoader);
        _imageLoader = imageLoader;
    }

    public IDocument? Document { get; private set; }

    /// <summary>
    /// Gets the styled render tree for the current document, or null when nothing is loaded. It
    /// carries DOM references and computed styles but no geometry.
    /// </summary>
    public IRenderNode? RenderTree => _pipeline.RenderTree;

    /// <summary>
    /// Gets the laid-out tree and the document size, or null when nothing is loaded or no text
    /// measurer was supplied.
    /// </summary>
    public LayoutResult? Layout => _pipeline.Layout;

    /// <summary>
    /// Gets the drawing instructions for the current layout, or null when nothing is loaded or no
    /// text measurer was supplied.
    /// </summary>
    public PaintList? Paint => _pipeline.Paint;

    /// <summary>Gets the width the styles were last computed against.</summary>
    public int ViewportWidth => _renderDevice.ViewPortWidth;

    /// <summary>Gets the height the styles were last computed against.</summary>
    public int ViewportHeight => _renderDevice.ViewPortHeight;

    public void AddEpubResourceSource(string epubFileName)
    {
        _resourceRequesters.Add(new EpubRequester(epubFileName));
    }

    public void AddEmbeddedResourceSource(Assembly assembly, string? folder = null)
    {
        _resourceRequesters.Add(new EmbeddedResourceRequester(assembly, folder));
    }

    public async Task<IDocument> LoadUrlAsync(
        string? url,
        CancellationToken cancellationToken = default)
    {
        url = NormalizeUrl(url);

        var context = CreateBrowsingContext();

        try
        {
            var document = await context.OpenAsync(url, cancellationToken)
                .ConfigureAwait(false);
            SetDocument(document, context);
            await PrefetchImagesAsync(cancellationToken).ConfigureAwait(false);
            return document;
        }
        catch
        {
            context.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Parses the supplied HTML without requesting the URL; the URL is used as the base for relative resources.
    /// </summary>
    public async Task<IDocument> SetHtml(string html, string? url = null)
    {
        ArgumentNullException.ThrowIfNull(html);

        var context = CreateBrowsingContext();

        try
        {
            // A virtual response parses the provided content while preserving its URL as the document base.
            var document = await context.OpenAsync(response => response
                    .Address(NormalizeUrl(url))
                    .Status(HttpStatusCode.OK)
                    .Header("Content-Type", "text/html; charset=utf-8")
                    .Content(html))
                .ConfigureAwait(false);

            SetDocument(document, context);
            await PrefetchImagesAsync(CancellationToken.None).ConfigureAwait(false);
            return document;
        }
        catch
        {
            context.Dispose();
            throw;
        }
    }

    public string GetHtml()
    {
        return Document?.DocumentElement?.OuterHtml ?? string.Empty;
    }

    public ICssStyleDeclaration GetComputedStyle(
        IElement element,
        string pseudoElement = "")
    {
        ArgumentNullException.ThrowIfNull(element);

        var window = Document?.DefaultView
            ?? throw new InvalidOperationException("Load a document before requesting computed styles.");

        return window.GetComputedStyle(element, pseudoElement);
    }

    /// <summary>
    /// Gets the document-space rectangle of the first box an element generated, or null when it
    /// generated none - the element may be unrendered, or nothing may be laid out yet.
    /// </summary>
    /// <remarks>
    /// A box pinned to the viewport by <c>position: fixed</c> reports viewport coordinates, since
    /// that is where it was placed.
    /// </remarks>
    public Rect? GetElementBounds(IElement element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return Layout?.FindBox(element)?.BorderBox;
    }

    /// <summary>
    /// Resolves an anchor to the element it names: an id, or the name of an anchor element. The
    /// fragment part of a URL is what is read from <paramref name="anchor"/>, so a link's href can
    /// be passed in unchanged.
    /// </summary>
    public IElement? FindAnchor(string anchor)
    {
        if (Document is null || string.IsNullOrWhiteSpace(anchor))
        {
            return null;
        }

        // A fragment always trails the URL, so anything before the hash names the document rather
        // than the target - and the view has already resolved that by loading it.
        var hash = anchor.LastIndexOf('#');
        var name = (hash >= 0 ? anchor[(hash + 1)..] : anchor).Trim();

        if (name.Length == 0)
        {
            return null;
        }

        var byId = Document.GetElementById(name);

        if (byId is not null)
        {
            return byId;
        }

        foreach (var element in Document.GetElementsByTagName("a"))
        {
            if (string.Equals(element.GetAttribute("name"), name, StringComparison.Ordinal))
            {
                return element;
            }
        }

        return null;
    }

    /// <summary>
    /// Gets the vertical offset that brings an element into view, or null when it generated no box.
    /// The viewport the styles were last computed for is what the element is placed against, since
    /// that is the width the document was laid out at.
    /// </summary>
    public double? GetVerticalScrollOffset(
        IElement element,
        ScrollAlignment alignment,
        double currentOffset = 0)
    {
        ArgumentNullException.ThrowIfNull(element);

        var layout = Layout;

        if (layout is null)
        {
            return null;
        }

        var bounds = layout.FindBox(element)?.BorderBox;

        return bounds is null
            ? null
            : layout.VerticalScrollOffsetFor(bounds.Value, ViewportHeight, alignment, currentOffset);
    }

    /// <summary>
    /// Updates the viewport the styles are computed against and rebuilds the render tree. Returns
    /// whether the size actually changed, so the host can skip a relayout when it did not.
    /// </summary>
    public bool SetViewportSize(int width, int height)
    {
        if (!_renderDevice.UpdateViewport(width, height))
            return false;

        UpdateRenderTree();
        return true;
    }

    public void Dispose()
    {
        Document = null;
        _pipeline.Invalidate();
        _context?.Dispose();
        _context = null;

        foreach (var requester in _resourceRequesters)
        {
            if (requester is IDisposable disposableRequester)
                disposableRequester.Dispose();
        }

        _resourceRequesters.Clear();
    }

    /// <summary>
    /// Creates a CSS-enabled context and adds registered requesters for relative resources.
    /// </summary>
    private IBrowsingContext CreateBrowsingContext()
    {
        var configuration = Configuration.Default
            .WithCss()
            .With(new Html5DefaultStyleSheetProvider())
            .WithRenderDevice(_renderDevice);

        configuration = configuration.With(CreateRequesterChain());

        configuration = configuration.WithDefaultLoader(
            new LoaderOptions { IsResourceLoadingEnabled = true });

        return BrowsingContext.New(configuration);
    }

    /// <summary>
    /// Supplies a unique resource URL when absent and assigns the resource scheme to scheme-less paths.
    /// </summary>
    private static string NormalizeUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            url = $"/{Guid.NewGuid():N}.html";
        else
            url = url.Trim();

        var schemeSeparator = url.IndexOf(':');

        if (schemeSeparator > 0 && Uri.CheckSchemeName(url[..schemeSeparator]))
            return url;

        return $"res:{url}";
    }

    /// <summary>
    /// Switches the active document only after parsing succeeds, then releases the previous context.
    /// </summary>
    private void SetDocument(IDocument document, IBrowsingContext context)
    {
        var previousContext = _context;
        _context = context;
        Document = document;
        previousContext?.Dispose();

        UpdateRenderTree();
    }

    /// <summary>
    /// Recomputes the styled render tree for the active document, if any. The render tree is what
    /// the box tree is built from; it carries no geometry.
    /// </summary>
    private void UpdateRenderTree()
    {
        if (Document is null)
            return;

        _pipeline.Build(Document);
    }

    /// <summary>
    /// The requesters the browsing context uses, in the order they are consulted.
    /// </summary>
    private List<IRequester> CreateRequesterChain() =>
        new(_resourceRequesters) { new FileRequester(), new HookHttpRequest(_resourceRequesters) };

    /// <summary>
    /// Fetches and decodes the images the document refers to, then lays out again so the boxes can
    /// use their intrinsic sizes. Prefetching is what lets the pipeline itself stay synchronous;
    /// the second pass is cheap and relayouts triggered by resizing reuse the cache.
    /// </summary>
    private async Task PrefetchImagesAsync(CancellationToken cancellationToken)
    {
        if (_imageLoader is null)
        {
            return;
        }

        var urls = ImageUrlCollector.Collect(_pipeline.RenderTree);

        if (urls.Count == 0)
        {
            return;
        }

        var requesters = CreateRequesterChain();

        await _imageLoader
            .LoadAsync(urls, (url, token) => FetchResourceAsync(requesters, url, token), cancellationToken)
            .ConfigureAwait(false);

        // Only worth rerunning when at least one image actually became measurable; a page whose
        // images all failed keeps the layout it already has.
        foreach (var url in urls)
        {
            if (_imageLoader.GetSize(url) is not null)
            {
                UpdateRenderTree();
                return;
            }
        }
    }

    /// <summary>
    /// Reads a resource through the same requesters the document itself uses, so res://, epub:// and
    /// http(s) behave identically. Returns null when no requester handles the URL.
    /// </summary>
    private static async Task<Stream?> FetchResourceAsync(
        List<IRequester> requesters,
        string url,
        CancellationToken cancellationToken)
    {
        var address = new Url(url);
        var request = new Request
        {
            Address = address,
            Method = AngleSharp.Io.HttpMethod.Get,
            Content = Stream.Null,
        };

        foreach (var requester in requesters)
        {
            if (!requester.SupportsProtocol(address.Scheme))
            {
                continue;
            }

            var response = await requester.RequestAsync(request, cancellationToken).ConfigureAwait(false);

            if (response is null)
            {
                continue;
            }

            using (response)
            {
                var buffer = new MemoryStream();
                await response.Content.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
                buffer.Position = 0;
                return buffer;
            }
        }

        return null;
    }
}

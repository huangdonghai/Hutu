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
    private readonly ITextMeasurer? _textMeasurer;
    private IBrowsingContext? _context;
    private TextSelector? _selector;
    private TextSelection? _selection;

    /// <summary>
    /// Creates the renderer. The text measurer and the image loader are supplied by the platform
    /// backend: without the measurer the pipeline stops at the box tree, and without the loader
    /// images are sized from their attributes alone.
    /// </summary>
    public HutuViewInternal(ITextMeasurer? textMeasurer = null, IImageLoader? imageLoader = null)
    {
        _pipeline = new RenderingPipeline(_renderDevice, textMeasurer, imageLoader);
        _imageLoader = imageLoader;
        // Kept rather than only handed on: the selector that answers selection questions has to
        // measure against the same fonts the layout was placed with.
        _textMeasurer = textMeasurer;
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

    /// <summary>Gets the current selection, or null when nothing is selected.</summary>
    public TextSelection? Selection => _selection;

    /// <summary>Gets whether a stretch of text is currently selected.</summary>
    public bool HasSelection => _selection is { IsEmpty: false };

    /// <summary>
    /// Gets the selected text, with one line break for each line the selection spans. Empty when
    /// nothing is selected, so a copy is a no-op rather than an error.
    /// </summary>
    public string SelectedText => _selector?.Text(_selection) ?? string.Empty;

    /// <summary>
    /// Raised whenever the selection changes, including when a relayout drops it. A host listens to
    /// repaint; the highlight itself is already part of the paint list.
    /// </summary>
    public event Action? SelectionChanged;

    /// <summary>
    /// Starts a selection at a point in document coordinates, replacing whatever was selected.
    /// </summary>
    /// <remarks>
    /// The point is in document coordinates: a host that scrolls adds its offset before calling, so
    /// nothing here has to know about scrolling. A point that hits no text clears the selection,
    /// which is what makes a click in the margin leave nothing behind.
    /// </remarks>
    public void BeginSelection(double x, double y)
    {
        var position = _selector?.HitTest(x, y);

        Apply(position is null ? null : new TextSelection(position.Value, position.Value));
    }

    /// <summary>
    /// Moves the caret of the current selection to a point in document coordinates, keeping the
    /// anchor where the gesture started. Does nothing when there is no selection to extend.
    /// </summary>
    public void ExtendSelection(double x, double y)
    {
        if (_selection is null)
        {
            return;
        }

        if (_selector?.HitTest(x, y) is { } position)
        {
            Apply(new TextSelection(_selection.Anchor, position));
        }
    }

    /// <summary>Selects the word under a point in document coordinates, as a double click does.</summary>
    public void SelectWord(double x, double y) =>
        Select(x, y, static (selector, position) => selector.Word(position));

    /// <summary>Selects every run of the line under a point, as a triple click does.</summary>
    public void SelectLine(double x, double y) =>
        Select(x, y, static (selector, position) => selector.Line(position));

    /// <summary>Selects everything the document can select. Does nothing when there is no text.</summary>
    public void SelectAll() => Apply(_selector?.All());

    /// <summary>Clears the selection, so nothing is highlighted and a copy has nothing to take.</summary>
    public void ClearSelection() => Apply(null);

    /// <summary>
    /// Gets whether a point in document coordinates lands on text a reader could select. A host asks
    /// this to shape its pointer - the text cursor over text, the default one everywhere else.
    /// </summary>
    /// <remarks>
    /// It is deliberately not the question <see cref="BeginSelection"/> asks: that one snaps a point
    /// outside the text onto the nearest line so a gesture is never lost, whereas a pointer resting
    /// there has nothing to select. The two would otherwise disagree, and a reader would see a text
    /// cursor over a stretch of page a click could not select.
    /// </remarks>
    public bool HasTextAt(double x, double y) => _selector?.IsTextAt(x, y) ?? false;

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
        _selector = null;
        _selection = null;
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

        // The selector answers against the boxes of the layout it was built from, so it is rebuilt
        // with the new one - and the selection it described goes with it, because a position naming
        // a box that no longer exists means nothing. The pipeline has already dropped the highlight
        // along with the layout, so nothing needs repainting here.
        _selector = _pipeline.Layout is { } layout && _textMeasurer is not null
            ? new TextSelector(layout, _textMeasurer)
            : null;

        if (_selection is not null)
        {
            _selection = null;
            SelectionChanged?.Invoke();
        }
    }

    /// <summary>
    /// Publishes a selection: records it, turns it into the highlight the painter draws, and tells
    /// the host. An unchanged selection is skipped, so a drag that stays on one character - or a
    /// click that clears an already empty selection - costs nothing.
    /// </summary>
    private void Apply(TextSelection? selection)
    {
        if (Object.Equals(_selection, selection))
        {
            return;
        }

        _selection = selection;
        _pipeline.SetHighlight(_selector?.Highlights(selection));
        SelectionChanged?.Invoke();
    }

    /// <summary>Applies the selection a gesture makes at a point, or clears it when nothing is hit.</summary>
    private void Select(double x, double y, Func<TextSelector, TextPosition, TextSelection> make)
    {
        if (_selector is { } selector && selector.HitTest(x, y) is { } position)
        {
            Apply(make(selector, position));
        }
        else
        {
            Apply(null);
        }
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

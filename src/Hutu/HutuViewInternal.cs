using AngleSharp;
using AngleSharp.Css;
using AngleSharp.Css.Dom;
using AngleSharp.Dom;
using AngleSharp.Io;
using System;
using System.Collections.Generic;
using System.Net;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace Hutu;

public sealed class HutuViewInternal : IDisposable
{
    private readonly List<IRequester> _resourceRequesters = new();
    private IBrowsingContext? _context;

    public IDocument? Document { get; private set; }

    public void AddEpubResourceSource(string epubFileName)
    {
        _resourceRequesters.Add(new EpubRequester(epubFileName));
    }

    public void AddEmbeddedResourceSource(Assembly assembly)
    {
        _resourceRequesters.Add(new EmbeddedResourceRequester(assembly));
    }

    public async Task<IDocument> LoadUrlAsync(
        string url,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(url))
            throw new ArgumentException("A URL is required.", nameof(url));

        var context = CreateBrowsingContext();

        try
        {
            var document = await context.OpenAsync(url, cancellationToken)
                .ConfigureAwait(false);
            SetDocument(document, context);
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
    public async Task<IDocument> SetHtml(string html, string url = "")
    {
        ArgumentNullException.ThrowIfNull(html);

        var context = CreateBrowsingContext();

        try
        {
            // A virtual response parses the provided content while preserving its URL as the document base.
            var document = await context.OpenAsync(response => response
                    .Address(string.IsNullOrWhiteSpace(url) ? "about:blank" : url)
                    .Status(HttpStatusCode.OK)
                    .Header("Content-Type", "text/html; charset=utf-8")
                    .Content(html))
                .ConfigureAwait(false);

            SetDocument(document, context);
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

    public void Dispose()
    {
        Document = null;
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
        var configuration = Configuration.Default.WithCss();

        if (_resourceRequesters.Count > 0)
            configuration = configuration.With(_resourceRequesters);

        configuration = configuration.WithDefaultLoader(
            new LoaderOptions { IsResourceLoadingEnabled = true });

        return BrowsingContext.New(configuration);
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
    }
}

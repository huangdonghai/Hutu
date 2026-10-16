using AngleSharp;
using AngleSharp.Css;
using AngleSharp.Css.Dom;
using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using AngleSharp.Io;
using Hutu.Rendering;
using Hutu.Rendering.Interaction;
using Hutu.Rendering.Layout;
using Hutu.Rendering.Media;
using Hutu.Rendering.Paint;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
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
    private readonly InteractionState _state = new();
    private readonly StylePseudoIndex _pseudos = new();
    private UserAgentStyleSheetProvider? _uaProvider;
    private IBrowsingContext? _context;
    private TextSelector? _selector;
    private ElementHitTester? _hitTester;

    /// <summary>The layout the selector and the hit tester were built against.</summary>
    private LayoutResult? _answered;
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
    /// Gets or sets how the host answers whether an address has been visited before. It is the answer
    /// <c>:visited</c> is matched against, and it stays the host's to give: where a reader has already
    /// been is browsing history, which is not the document's business and differs per host.
    /// </summary>
    /// <remarks>
    /// Without an answer no link is ever marked visited, which is a page that shows everything as new.
    /// </remarks>
    public Func<String, Boolean>? IsVisited { get; set; }

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

    /// <summary>
    /// Gets or sets whether the next builds time their stages, read back through
    /// <see cref="LastBuildStages"/>. Off by default, so the renderer pays nothing for a measurement
    /// nobody asked for.
    /// </summary>
    public Boolean MeasureBuildStages
    {
        get => _pipeline.MeasureStages;
        set => _pipeline.MeasureStages = value;
    }

    /// <summary>
    /// The stages of the last build - the style-and-box rebuild, the layout and the paint list, and
    /// whether it took the restyle-without-relayout path - or null while <see cref="MeasureBuildStages"/>
    /// is off. This is what tells a hover's cost apart from its path.
    /// </summary>
    public (Double Fused, Double Layout, Double Paint, Boolean Fast)? LastBuildStages => _pipeline.LastBuildStages;

    /// <summary>
    /// How the style half of the last build split: the wall clock spent matching, the wall clock spent
    /// opening variable scopes and computing, and the counts that say how much of the document had to
    /// be looked at again. Null while <see cref="MeasureBuildStages"/> is off. This is what tells a
    /// slow rebuild apart from one that merely read a large document - and it is the pair a fix has to
    /// move.
    /// </summary>
    public (Double Matching, Double Cascading, Int32 Walked, Int32 Computed, Int32 HeldMatches, Int32 FreshMatches)? LastStyleStages =>
        _pipeline.LastFusedProfile is { } profile
            ? (profile.Matching, profile.Cascading, profile.Walked, profile.Computed, profile.HeldMatches, profile.FreshMatches)
            : null;

    /// <summary>
    /// How many variable-scope links the last build created, and how many lookups were handed a link that
    /// was already open. A style stands down only when the scope it read through is the same object the
    /// last build left, so a chain that has to be built again is a chain under which every style is
    /// computed again. Null while <see cref="MeasureBuildStages"/> is off.
    /// </summary>
    public (Int32 Opened, Int32 Reused)? LastScopeLinks =>
        _pipeline.LastFusedProfile is { } profile ? (profile.OpenedScopes, profile.ReusedScopes) : null;

    /// <summary>Gets the width the styles were last computed against.</summary>
    public int ViewportWidth => _renderDevice.ViewPortWidth;

    /// <summary>Gets the height the styles were last computed against.</summary>
    public int ViewportHeight => _renderDevice.ViewPortHeight;

    /// <summary>
    /// Gets or sets the page zoom. A change lays the document out again at the scaled viewport - the
    /// window divided by the zoom - which is what makes a zoomed page reflow rather than merely
    /// magnify. The viewport itself is untouched, so no size report is needed from the host.
    /// </summary>
    public Double Zoom
    {
        get => _renderDevice.Zoom;
        set
        {
            if (Math.Abs(_renderDevice.Zoom - value) < 1e-6)
            {
                return;
            }

            _renderDevice.Zoom = value;
            UpdateRenderTree();
        }
    }

    /// <summary>
    /// Gets or sets the agent a page is asked as. It is the engine's own name rather than a borrowed
    /// browser's, because a reader is entitled to know what fetched them - but it has to say something
    /// about the shape of the reader, since a page's server can pick what to send from it alone:
    /// MediaWiki answers an agent it does not recognise with its mobile skin, which is another
    /// document - another markup, another set of style sheets - than the desktop one the reader was
    /// comparing the rendering against.
    /// </summary>
    /// <remarks>
    /// Set it before a load: the browsing context built for that load is what carries it, and the
    /// images fetched afterwards go out through the same chain.
    /// </remarks>
    public String UserAgent { get; set; } = DefaultUserAgent;

    /// <summary>The agent a host that says nothing is asking as: a desktop reader, under the engine's own name.</summary>
    private const String DefaultUserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) Hutu";

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
    /// Raised when a link is followed to a document other than the one it is in.
    /// </summary>
    /// <remarks>
    /// Loading is the host's business, so the request is handed over rather than acted on: that is what
    /// lets one host open the address in place and another hand it to something else entirely. A link
    /// into the document it is in never gets here - it is a scroll, which the document can do itself.
    /// </remarks>
    public event Action<LinkTarget>? NavigationRequested;

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

    /// <summary>
    /// Moves the pointer to a point in document coordinates, marking what it is over.
    /// </summary>
    /// <remarks>
    /// The point is in document coordinates: a host that scrolls adds its offset before calling, so
    /// nothing here has to know about scrolling. The answer carries both whether the document has to
    /// be painted again and what the pointer should look like there, because one hit test serves both.
    /// </remarks>
    public PointerInfo PointerMoved(double x, double y)
    {
        var hit = Hit(x, y);
        var restyle = HoverRestyles(_state.Hover(hit.Chain));

        if (restyle)
        {
            // The hit is taken again with the rebuild, because `:hover` may have moved the very
            // box the pointer is over. A state that changed no style leaves the geometry alone,
            // so the hit in hand stays valid.
            hit = Hit(x, y);
        }

        return new PointerInfo(restyle, CursorFor(hit), HintFor(hit));
    }

    /// <summary>
    /// Presses at a point in document coordinates, marking what the button went down on. Returns
    /// whether the document changed and has to be painted again.
    /// </summary>
    public bool PointerPressed(double x, double y)
    {
        var chain = Hit(x, y).Chain;
        return PressRestyles(_state.Press(chain), chain);
    }

    /// <summary>Releases the button, clearing whatever was being pressed.</summary>
    public bool PointerReleased()
    {
        // The snapshot is taken first: releasing takes the marks back, and what was pressed is
        // what decides whether letting go could have changed anything.
        var pressed = _state.Pressed;
        return PressRestyles(_state.Release(), pressed);
    }

    /// <summary>
    /// Rebuilds the document when a press state changed the styles, carrying the selection across,
    /// and reports whether it did. A press with no :active rule behind it changes no match - and
    /// with no followable link under it, not the engine's own link colour either - so the document
    /// is left alone.
    /// </summary>
    private bool PressRestyles(bool changed, IReadOnlyList<IElement> flipped)
    {
        var restyle = changed &&
            (_pseudos.MayAffect(PseudoClassNames.Active) || HasFollowableLink(flipped));

        if (restyle)
        {
            Rebuild(CaptureSelection());
        }

        return restyle;
    }

    /// <summary>
    /// Gets whether any element could be coloured by the engine's link colour, which answers the
    /// active mark. Mirrors <c>ComputedStyle.IsFollowable</c>; keep the two in sync.
    /// </summary>
    private static bool HasFollowableLink(IReadOnlyList<IElement> elements)
    {
        foreach (var element in elements)
        {
            if (element is IHtmlAnchorElement anchor && anchor.HasAttribute("href"))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Re-reads the visited marks against the answer the host now gives, and rebuilds when that
    /// changed what the document shows. A host calls this after recording an address it has just
    /// loaded, which is what turns the links to it purple.
    /// </summary>
    /// <returns>
    /// Whether anything changed. Marking an address that was already marked changes nothing, so a
    /// reload or a repeated navigation does not rebuild the document.
    /// </returns>
    public bool RefreshVisited() => Change(MarkVisited());

    /// <summary>Leaves the document, clearing the hover: a pointer that is gone is over nothing.</summary>
    public bool PointerExited() => HoverRestyles(_state.Hover(Array.Empty<IElement>()));

    /// <summary>
    /// Gets the link a point in document coordinates is on, or null when it is on none. A host asks
    /// this when the button goes down, so that what is let go of is what was pressed.
    /// </summary>
    public LinkTarget? LinkAt(double x, double y) => Hit(x, y).Link;

    /// <summary>
    /// Gets the pointer a point in document coordinates asks for: what the box under it declares, and
    /// otherwise what lies under it - a link the hand wherever on it the point is, text set down a
    /// column read with an I-beam turned on its side, text along a line with an upright one, and
    /// everything else with the ordinary arrow.
    /// </summary>
    /// <remarks>
    /// This is what a move reports, asked as a question about a point rather than as a move to it:
    /// nothing is marked and nothing is painted, so a host can ask in the middle of a gesture, where a
    /// hover that repainted would fight the selection being made.
    /// </remarks>
    public CursorKind CursorAt(double x, double y) => CursorFor(Hit(x, y));

    /// <summary>
    /// Follows a link a reader clicked.
    /// </summary>
    /// <returns>
    /// The anchor the link names when it points into the document it is in, so the host can bring it
    /// into view - it is marked as the target on the way. Null when there is nothing to scroll to:
    /// either the link points elsewhere and the host has been asked to load it instead, or it names an
    /// anchor this document does not have.
    /// </returns>
    public string? Activate(LinkTarget link)
    {
        if (!link.IsFragment)
        {
            NavigationRequested?.Invoke(link);
            return null;
        }

        if (link.Fragment is not { Length: > 0 } fragment || FindAnchor(fragment) is not { } anchor)
        {
            return null;
        }

        ApplyTarget(anchor);
        return fragment;
    }

    public void AddEpubResourceSource(string epubFileName)
    {
        _resourceRequesters.Add(new EpubRequester(epubFileName));
    }

    public void AddEmbeddedResourceSource(Assembly assembly, string? folder = null)
    {
        _resourceRequesters.Add(new EmbeddedResourceRequester(assembly, folder));
    }

    /// <summary>
    /// Loads a document and every image it refers to, the whole picture ready when the returned task
    /// completes. This is <see cref="LoadDocumentAsync"/> followed by <see cref="PrefetchImagesAsync"/>;
    /// a host that draws should load them separately, so the page can be on screen while its pictures
    /// are still arriving.
    /// </summary>
    public async Task<IDocument> LoadUrlAsync(
        string? url,
        CancellationToken cancellationToken = default)
    {
        var document = await LoadDocumentAsync(url, cancellationToken).ConfigureAwait(false);
        await PrefetchImagesAsync(cancellationToken).ConfigureAwait(false);
        return document;
    }

    /// <summary>
    /// Loads a document and builds it once, without waiting for any image. The text, the colours and
    /// the geometry are all there when the task completes - only the replaced boxes are sized from
    /// their attributes rather than from the picture that will not arrive until later.
    /// </summary>
    /// <remarks>
    /// The await keeps the caller's context, unlike a fetch that only hands a result back. What runs
    /// after it - building the document, and the events a build raises - belongs to whoever asked, and a
    /// host answers those by invalidating a visual, which the platform only lets the thread that owns it
    /// do. Dropping the context here lost a navigation outright: the host's handler was an <c>async</c>
    /// <c>void</c>, so the exception it threw on the wrong thread had nowhere to be seen and the reader
    /// was left with a click that did nothing at all.
    /// </remarks>
    public async Task<IDocument> LoadDocumentAsync(
        string? url,
        CancellationToken cancellationToken = default)
    {
        url = NormalizeUrl(url);

        var context = CreateBrowsingContext();

        try
        {
            var document = await context.OpenAsync(url, cancellationToken);
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
    /// Parses the supplied HTML without requesting the URL, and fetches the images it refers to; the
    /// URL is used as the base for relative resources.
    /// </summary>
    public async Task<IDocument> SetHtml(string html, string? url = null)
    {
        var document = await SetHtmlDocumentAsync(html, url).ConfigureAwait(false);
        await PrefetchImagesAsync(CancellationToken.None).ConfigureAwait(false);
        return document;
    }

    /// <summary>
    /// Parses the supplied HTML without requesting the URL and without waiting for its images; the URL
    /// is used as the base for relative resources. Split from the pictures for the same reason
    /// <see cref="LoadDocumentAsync"/> is.
    /// </summary>
    public async Task<IDocument> SetHtmlDocumentAsync(string html, string? url = null)
    {
        ArgumentNullException.ThrowIfNull(html);

        var context = CreateBrowsingContext();

        try
        {
            // A virtual response parses the provided content while preserving its URL as the document
            // base. The context is kept for the same reason LoadDocumentAsync keeps it.
            var document = await context.OpenAsync(response => response
                    .Address(NormalizeUrl(url))
                    .Status(HttpStatusCode.OK)
                    .Header("Content-Type", "text/html; charset=utf-8")
                    .Content(WithoutCdataInStyles(html)));

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

    /// <summary>The body of one style element, which is where an XHTML sheet hides its markers.</summary>
    private static readonly Regex StyleBody = new(
        @"(?<open><style\b[^>]*>)(?<body>.*?)(?=</style>)",
        RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Takes the CDATA markers out of the stylesheets, leaving everything else alone.
    /// </summary>
    /// <remarks>
    /// A page written the XHTML way wraps its CSS in <c>&lt;![CDATA[ ... ]]&gt;</c>. In HTML those
    /// markers mean nothing - a style element is raw text - so they reach the CSS parser as part of the
    /// sheet and every rule in it is thrown away, which is how a reference page comes back with no styles
    /// at all and its plain div measures as a full-width empty block. Stripping them inside the style
    /// elements only leaves text that might legitimately carry the characters untouched.
    /// </remarks>
    private static String WithoutCdataInStyles(String html) =>
        StyleBody.Replace(html, match =>
            match.Groups["open"].Value +
            match.Groups["body"].Value
                .Replace("<![CDATA[", String.Empty, StringComparison.Ordinal)
                .Replace("]]>", String.Empty, StringComparison.Ordinal));

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
    /// Gets the vertical frame offset that brings an element into view, or null when it generated no
    /// box. The viewport the styles were last computed for is what the element is placed against,
    /// since that is the width the document was laid out at.
    /// </summary>
    /// <remarks>
    /// It answers in the frame the boxes are in rather than in the reader's, because both what it is
    /// asked and what it answers with are about a rectangle of the layout. A host that keeps its
    /// scrollbar in the reader's frame turns the answer round with
    /// <see cref="LayoutResult.ScrollPosition"/>.
    /// </remarks>
    public double? GetVerticalFrameOffset(
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
            : layout.VerticalFrameOffsetFor(
                bounds.Value,
                ViewportHeight / Zoom,
                alignment,
                currentOffset);
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
        _hitTester = null;
        _selection = null;
        _state.Clear();
        _pseudos.Clear();
        _uaProvider = null;
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
        // The reader's own sheet and its reading of declarations are registered before the CSS services,
        // which ship their own only when nothing else has: two sheets at the user-agent level would be
        // two sources to reconcile, and the declaration factory is asked for by name - whichever is
        // registered answers, so the one that wraps AngleSharp's has to be the one to hand.
        var configuration = Configuration.Default
            .With(_uaProvider = new UserAgentStyleSheetProvider())
            .With((Object)new WideKeywordDeclarationFactory())
            .WithCss()
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

        // The marks belong to the document being left, so they are taken back before the new one is
        // built: nothing about where the pointer was carries over to a different page. What has been
        // read before does carry over, so it is placed before the first build rather than after it.
        _state.Clear();
        MarkVisited();

        UpdateRenderTree();
    }

    /// <summary>
    /// Rebuilds the box tree for the active document, dropping whatever was selected, which
    /// is what a document or a viewport changing asks for.
    /// </summary>
    private void UpdateRenderTree() => Rebuild(null);

    /// <summary>
    /// Rebuilds everything the document is described by, carrying the selection across when it can
    /// be found again.
    /// </summary>
    /// <param name="restore">
    /// Where the selection was, in terms that outlive a relayout. Null drops it, which is what
    /// loading a document asks for; a selection that cannot be found again afterwards is dropped
    /// too, because a position naming a box that no longer exists means nothing.
    /// </param>
    /// <remarks>
    /// A rebuild is what a state change needs - the styles, and so the boxes, are computed from the
    /// states - and it is also what would otherwise throw a selection away, since the boxes it names
    /// are replaced. Carrying it across is what lets a reader keep a stretch of text selected while
    /// the pointer moves over a link and the document is built again underneath.
    /// </remarks>
    private void Rebuild(SelectionSnapshot? restore)
    {
        if (Document is null)
            return;

        // The index never grows older than the picture: it is refreshed wherever a full build
        // runs, so between builds the sheets cannot have taken effect yet.
        _pseudos.Refresh(Document, _uaProvider?.Default);

        _pipeline.Build(Document);

        // The selector and the hit tester answer against the boxes of the layout they were built
        // from, so they are rebuilt with a new one. A rebuild that only moved the colours leaves the
        // layout in hand untouched - the pipeline keeps the very instance it had - so they still
        // answer against it and stand.
        if (!ReferenceEquals(_pipeline.Layout, _answered))
        {
            _answered = _pipeline.Layout;

            _selector = _pipeline.Layout is { } layout && _textMeasurer is not null
                ? new TextSelector(layout, _textMeasurer)
                : null;

            _hitTester = _pipeline.Layout is { } measured ? new ElementHitTester(measured) : null;
        }

        var had = _selection is not null;
        _selection = restore is { } snapshot ? Restore(snapshot) : null;

        // The pipeline drops the highlight along with the layout, so the selection has to be pointed
        // at the new boxes again or it would be a selection nothing draws.
        _pipeline.SetHighlight(_selector?.Highlights(_selection));

        if (had && _selection is null)
        {
            SelectionChanged?.Invoke();
        }
    }

    /// <summary>
    /// Records where a selection is, as the runs its two ends name and the offsets within them. The
    /// run order is what survives a relayout: the boxes are new, but the same document lays the same
    /// runs out in the same order, so an index still names the same text.
    /// </summary>
    private SelectionSnapshot? CaptureSelection()
    {
        if (_selection is not { } selection || _selector is null)
        {
            return null;
        }

        var anchor = RunAt(selection.Anchor.Box);
        var caret = RunAt(selection.Caret.Box);

        return anchor < 0 || caret < 0
            ? null
            : new SelectionSnapshot(anchor, selection.Anchor.Index, caret, selection.Caret.Index);
    }

    /// <summary>
    /// Gets the selection a snapshot describes against the layout in hand, or null when it names a
    /// run that is no longer there.
    /// </summary>
    private TextSelection? Restore(SelectionSnapshot snapshot)
    {
        if (_selector is not { } selector ||
            snapshot.AnchorRun >= selector.Runs.Count ||
            snapshot.CaretRun >= selector.Runs.Count)
        {
            return null;
        }

        return new TextSelection(
            Clamp(selector.Runs[snapshot.AnchorRun], snapshot.AnchorIndex),
            Clamp(selector.Runs[snapshot.CaretRun], snapshot.CaretIndex));
    }

    /// <summary>Gets where a run sits in the collected order, or -1 when it is not one of them.</summary>
    private int RunAt(LayoutBox? box)
    {
        if (box is null || _selector is null)
        {
            return -1;
        }

        var runs = _selector.Runs;

        for (var index = 0; index < runs.Count; index++)
        {
            if (ReferenceEquals(runs[index], box))
            {
                return index;
            }
        }

        return -1;
    }

    /// <summary>Gets a position on a run, with the offset pulled back inside the text it names.</summary>
    private static TextPosition Clamp(LayoutBox run, int index) =>
        new(run, Math.Clamp(index, 0, run.Text?.Length ?? 0));

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

    /// <summary>Gets what a point in document coordinates lands on, or nothing when there is no
    /// layout to hit against.</summary>
    private HitResult Hit(double x, double y) => _hitTester?.Hit(x, y) ?? HitResult.None;

    /// <summary>
    /// Rebuilds the document when a hover state changed the styles, carrying the selection across,
    /// and reports whether it did. A hover that no selector mentions moves marks but changes no
    /// style, so the document is left alone - and the answer stays false, because nothing about
    /// the drawing moved either.
    /// </summary>
    private bool HoverRestyles(bool changed)
    {
        var restyle = changed && _pseudos.MayAffect(PseudoClassNames.Hover);

        if (restyle)
        {
            Rebuild(CaptureSelection());
        }

        return restyle;
    }

    /// <summary>
    /// Rebuilds the document when a state changed, carrying the selection across, and reports whether
    /// it did.
    /// </summary>
    private bool Change(bool changed)
    {
        if (changed)
        {
            Rebuild(CaptureSelection());
        }

        return changed;
    }

    /// <summary>
    /// Marks the anchor a link was followed to, which is what <c>:target</c> answers to. The mark is a
    /// state the styles are computed from, so the document is built again when it moves.
    /// </summary>
    private void ApplyTarget(IElement anchor)
    {
        // The mark is a state the styles are computed from, so the document is built again when
        // it moves - unless no selector mentions :target, in which case nothing could have changed.
        if (_state.Target(anchor) && _pseudos.MayAffect(PseudoClassNames.Target))
        {
            Rebuild(CaptureSelection());
        }
    }

    /// <summary>
    /// Puts the visited mark on the links the host says it has been to, and takes it off the rest.
    /// </summary>
    /// <remarks>
    /// Only the mark is placed here; whether to build the document again is the caller's to decide,
    /// because the two callers want different things. Loading a document marks before the first build,
    /// where a rebuild would be wasted work; a host recording an address marks after it, where the
    /// build is exactly what shows the new colour.
    /// </remarks>
    private bool MarkVisited() =>
        Document is not null &&
        IsVisited is not null &&
        _state.Visited(Document.GetElementsByTagName("a"), IsVisited);

    /// <summary>
    /// Gets the pointer a hit asks for: what the element declares, and otherwise what lies under the
    /// point - a link the hand wherever on it the point is, text set down a column read with a turned
    /// I-beam, text along a line with an upright one, and everything else with the ordinary arrow.
    /// </summary>
    private static CursorKind CursorFor(HitResult hit)
    {
        var declared = hit.Box?.Source.Style.Cursor ?? CursorKind.Auto;

        if (declared != CursorKind.Auto)
        {
            return declared;
        }

        // A link answers for all of itself, not only for the anchor box: every point in it is the
        // link, and a selection cannot be started from one - dragging across the words a link wraps
        // selects them, but a press there is a press on the link. So the shapes that say "text here"
        // are not asked for over one. It cannot be said by the element's own style, which only ever
        // sees one element at a time - the box under the point is often a span inside the link, which
        // is not itself a link and so cannot know that one of its ancestors is.
        if (hit.Link is not null)
        {
            return CursorKind.Pointer;
        }

        if (!hit.IsText)
        {
            return CursorKind.Default;
        }

        return hit.IsVerticalText ? CursorKind.VerticalText : CursorKind.Text;
    }

    /// <summary>
    /// Gets the hint a point asks for: the nearest element it is inside that names one, which is how
    /// a title on a wrapper is shown for everything the wrapper holds.
    /// </summary>
    private static string? HintFor(HitResult hit)
    {
        foreach (var element in hit.Chain)
        {
            if (element.GetAttribute("title") is { Length: > 0 } title && !string.IsNullOrWhiteSpace(title))
            {
                return title;
            }
        }

        return null;
    }

    /// <summary>
    /// The requesters the browsing context uses, in the order they are consulted.
    /// </summary>
    private List<IRequester> CreateRequesterChain() =>
        new(_resourceRequesters) { new FileRequester(), new HookHttpRequest(_resourceRequesters, UserAgent) };

    /// <summary>
    /// Fetches and decodes the images the document refers to, then lays the document out again so the
    /// boxes can use their intrinsic sizes.
    /// </summary>
    /// <remarks>
    /// Prefetching is what lets the pipeline itself stay synchronous. It is separate from the load -
    /// and a host that draws is expected to put a paint between the two - because it is the slowest
    /// part of opening a real page by an order of magnitude: a portal's fifty-odd pictures at a tenth
    /// of a second apiece are seven seconds of a reader looking at nothing, and the text of that page
    /// was ready after the first fraction of one of them.
    /// <para>
    /// The second pass is cheap beside that, and relayouts triggered by resizing reuse the cache: an
    /// image already fetched - or already failed - is never asked for again.
    /// </para>
    /// </remarks>
    public async Task PrefetchImagesAsync(CancellationToken cancellationToken) =>
        await PrefetchImagesAsync(null, cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Fetches only the pictures that come in through the file system or ride inside the page itself.
    /// </summary>
    /// <remarks>
    /// A reftest has to reach the same picture on every machine and on every run, which a picture pulled
    /// over http cannot promise - it is not there when the network is not, and it is a different picture
    /// the moment the server changes its mind. A <c>file:</c> reference beside the page and a <c>data:</c>
    /// URL baked into it are both fixed, so those and nothing else are what a deterministic drawing loads.
    /// </remarks>
    public async Task PrefetchLocalImagesAsync(CancellationToken cancellationToken) =>
        await PrefetchImagesAsync(IsLocal, cancellationToken).ConfigureAwait(false);

    private static Boolean IsLocal(String url) =>
        url.StartsWith("file:", StringComparison.OrdinalIgnoreCase) ||
        url.StartsWith("data:", StringComparison.OrdinalIgnoreCase);

    private async Task PrefetchImagesAsync(Func<String, Boolean>? shouldFetch, CancellationToken cancellationToken)
    {
        if (_imageLoader is null)
        {
            return;
        }

        IReadOnlyList<String> urls = ImageUrlCollector.Collect(Document!, _renderDevice);

        if (shouldFetch is not null)
        {
            urls = urls.Where(shouldFetch).ToList();
        }

        if (urls.Count == 0)
        {
            return;
        }

        var requesters = CreateRequesterChain();

        // The context is kept for the same reason LoadDocumentAsync keeps it: what follows is a
        // rebuild, and a rebuild raises what the host may listen to. The fetch itself ran on the pool
        // whatever this says; only its completion comes back here.
        await _imageLoader
            .LoadAsync(urls, (url, token) => FetchResourceAsync(requesters, url, token), cancellationToken);

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

    /// <summary>
    /// Where a selection is, in the terms that outlive the boxes it was made against: which run each
    /// end belongs to, and how far into that run it reaches.
    /// </summary>
    private readonly struct SelectionSnapshot
    {
        public SelectionSnapshot(int anchorRun, int anchorIndex, int caretRun, int caretIndex)
        {
            AnchorRun = anchorRun;
            AnchorIndex = anchorIndex;
            CaretRun = caretRun;
            CaretIndex = caretIndex;
        }

        public int AnchorRun { get; }

        public int AnchorIndex { get; }

        public int CaretRun { get; }

        public int CaretIndex { get; }
    }
}

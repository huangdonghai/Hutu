using AngleSharp.Css;
using AngleSharp.Css.Dom;
using Hutu.Rendering;
using Hutu.Rendering.Boxes;
using Hutu.Rendering.Layout;
using Hutu.Rendering.Paint;
using Hutu.Skia;
using SkiaSharp;
using System.Globalization;

namespace Hutu.Conformance;

/// <summary>
/// One box, in the terms both sides are asked in.
/// </summary>
/// <param name="Box">The border box, or null when the engine built no box for the element.</param>
/// <param name="Styles">The computed values the case asked for, as the engine resolved them.</param>
internal sealed record EngineBox(Rect? Box, IReadOnlyDictionary<String, String> Styles, Boolean HasBox);

/// <summary>
/// Reads a page through the engine and answers the same questions the reference was captured with.
/// </summary>
/// <remarks>
/// The answers come from the engine's own resolved values rather than from the cascaded declaration
/// strings, which is the whole point: a page measured against <c>1rem = 16px</c> and one measured
/// against <c>1rem = 10px</c> produce the same declaration text and different boxes, and it is the
/// boxes a reader sees.
/// </remarks>
internal sealed class EngineReader : IDisposable
{
    private readonly SkiaTextMeasurer? _typefaces;
    private readonly SkiaImageLoader? _images;
    private readonly HutuViewInternal _view;

    private EngineReader(ITextMeasurer text, SkiaImageLoader? images, Int32 width, Int32 height)
    {
        _typefaces = text as SkiaTextMeasurer;
        _images = images;

        // The faces a page links to are part of the page: a borrowed reftest reaches Ahem and the CJK
        // subsets by `@font-face`, and without a loader those declarations are made and never answered,
        // so the page is drawn in whichever face the platform falls back to and a pair that asks a face
        // to do something is measured in a font nobody named. The fixed-metrics reader asks for no
        // typefaces at all, and is given no loader for the same reason.
        _view = new HutuViewInternal(
            text,
            images,
            text is SkiaTextMeasurer measurer ? new SkiaFontLoader(measurer) : null);
        _view.SetViewportSize(width, height);
    }

    /// <summary>
    /// Reads a page with the machine's real typefaces, which is what a comparison against a browser
    /// has to use: the browser measured the same glyphs or the two are not measuring the same page.
    /// </summary>
    public static EngineReader WithTypefaces(Int32 width, Int32 height) =>
        new(new SkiaTextMeasurer(), new SkiaImageLoader(), width, height);

    /// <summary>
    /// Reads a page with fixed metrics, which is what a golden wants: the answer is the layout's
    /// arithmetic alone, and a font update on the build machine cannot move it.
    /// </summary>
    public static EngineReader WithFixedMetrics(Int32 width, Int32 height) =>
        new(new FixedTextMeasurer(), null, width, height);

    public Double DocumentWidth => _view.Layout?.Width ?? 0;

    public Double DocumentHeight => _view.Layout?.Height ?? 0;

    /// <summary>Gets the laid-out tree, which is what a golden is written from.</summary>
    public LayoutResult? Layout => _view.Layout;

    /// <summary>Gets the drawing instructions for that tree, in paint order.</summary>
    public PaintList? Paint => _view.Paint;

    /// <summary>Gets the document the page was parsed into, which is what a property census reads.</summary>
    public AngleSharp.Dom.IDocument? Document => _view.Document;

    /// <summary>
    /// Gets or sets the page zoom. It is set after a load because a zoom is a reflow: the document is
    /// laid out again at the scaled viewport, which is exactly the property the invariant that compares
    /// a zoom against a narrower window depends on.
    /// </summary>
    public Double Zoom
    {
        get => _view.Zoom;
        set => _view.Zoom = value;
    }

    public async Task LoadAsync(String html, String baseAddress)
    {
        // A reftest run has been to exactly one address: the page it is drawing. That is the answer the
        // corpus's visited-link tests are written against - href="" names the page and is read, a name
        // beside it like href="unvisited" is not - and both sides of a pair are drawn under it, as
        // Chrome draws both. Anything after the hash names a place in this page rather than another one.
        var page = baseAddress.Split('#')[0];
        _view.IsVisited = url => String.Equals(url.Split('#')[0], page, StringComparison.Ordinal);

        await _view.SetHtmlDocumentAsync(html, baseAddress);

        // A reftest that reaches a picture through a file beside the page draws it; one that would
        // reach it over the network does not, because a drawing has to come to the same pixels on every
        // machine. A reader built on fixed metrics has no loader at all, so these are a no-op there.
        //
        // The faces come first: a font changes every line of the page, an image only the boxes below
        // it, so a picture fetched before the face it was measured beside would be laid out twice.
        await _view.PrefetchLocalFontsAsync(default);
        await _view.PrefetchLocalImagesAsync(default);
    }

    private SkiaPaintExecutor? _executor;

    /// <summary>
    /// Draws the page into a bitmap of the given size, on white, the way a host's surface would show
    /// it. This is what a reftest compares.
    /// </summary>
    /// <remarks>
    /// It needs the real measurer: a reftest asks whether two documents come to the same picture, and
    /// a picture is drawn by the same executor a host uses. The corpus keeps its reftests free of text
    /// so that the answer does not depend on which fonts the machine happens to have - the moment a
    /// test has words in it, the build has to pin them.
    /// </remarks>
    public SKBitmap Render(Int32 width, Int32 height)
    {
        if (_typefaces is null)
        {
            throw new InvalidOperationException("a reftest is drawn with real typefaces; this reader was built with fixed metrics");
        }

        _executor ??= new SkiaPaintExecutor(_typefaces, _images);

        using var surface = SKSurface.Create(new SKImageInfo(width, height))
            ?? throw new InvalidOperationException("Skia could not make a surface");

        // A page with nothing to paint - an empty document, or one whose root is display:none and so builds
        // no box tree - is not an error but a blank canvas. A host shows it as the page's own background,
        // which with no box left to carry one is the default white; the reference for such a page is a bare
        // document on that same white. Draw the canvas rather than refusing to answer.
        if (Paint is null)
        {
            surface.Canvas.Clear(SKColors.White);
        }
        else
        {
            _executor.Execute(Paint, surface.Canvas);
        }

        using var image = surface.Snapshot();

        return SKBitmap.FromImage(image);
    }

    /// <summary>
    /// Gets the pages the current document comes to when it is printed, or null when it was read for
    /// a screen.
    /// </summary>
    public PagedLayout? Pages => _view.Pages;

    /// <summary>
    /// Asks the page as a printing device, so <c>@media print</c> and <c>@page</c> apply. It has to be
    /// said before the page is loaded: the medium is part of what the styles are computed from.
    /// </summary>
    public void UsePrintMedia()
    {
        _view.SetMediaType(DeviceCategory.Printer);

        // The suite's print runner gives a document that states no page box a five-by-three inch
        // page with half-inch margins; a pair that does state one states its own over this.
        _view.SetDefaultPage(
            new PageSize(5 * 96, 3 * 96),
            new Thickness(0.5 * 96, 0.5 * 96, 0.5 * 96, 0.5 * 96));
    }

    /// <summary>
    /// Draws every page of a printed document, each at the page box's own size and on white.
    /// </summary>
    public IReadOnlyList<SKBitmap> RenderPages()
    {
        if (_typefaces is null)
        {
            throw new InvalidOperationException("a page is drawn with real typefaces; this reader was built with fixed metrics");
        }

        if (_view.Pages is not { } pages)
        {
            return Array.Empty<SKBitmap>();
        }

        _executor ??= new SkiaPaintExecutor(_typefaces, _images);

        var drawn = new List<SKBitmap>(pages.Count);

        foreach (var page in pages.Pages)
        {
            var width = Math.Max(1, (Int32)Math.Ceiling(page.Geometry.Width));
            var height = Math.Max(1, (Int32)Math.Ceiling(page.Geometry.Height));

            using var surface = SKSurface.Create(new SKImageInfo(width, height))
                ?? throw new InvalidOperationException("Skia could not make a surface");

            surface.Canvas.Clear(SKColors.White);

            // A root that generates no box still prints its sheet: there is nothing to draw, and
            // the white the surface was made on is the page.
            if (Paint is not null)
            {
                _executor.ExecutePage(Paint, surface.Canvas, page);
            }

            using var image = surface.Snapshot();
            drawn.Add(SKBitmap.FromImage(image));
        }

        return drawn;
    }

    /// <summary>
    /// Reads the box the engine gave the element a selector matched, and the sizes it resolved for it.
    /// </summary>
    public EngineBox? Read(String selector, Int32 index, IReadOnlyList<String> styles)
    {
        if (_view.Document is not { } document)
        {
            return null;
        }

        var matches = document.QuerySelectorAll(selector);

        if (index >= matches.Length)
        {
            return null;
        }

        var element = matches[index];
        var node = _view.Layout is { } layout
            ? Walk(layout.Root).FirstOrDefault(box => ReferenceEquals(box.Source.Element, element))
            : null;

        return new EngineBox(node?.BorderBox, Values(node, styles), node is not null);
    }

    /// <summary>
    /// The engine's answer for one computed property, spelled the way a browser spells it.
    /// </summary>
    /// <remarks>
    /// <c>display</c> is reported as the outer role the box is laid out with rather than as the
    /// declaration: a browser's computed value of an absolutely positioned <c>inline</c> is
    /// <c>block</c>, because the specification blockifies it before any box is generated, and an
    /// engine that agrees about the box while quoting a different word is not the disagreement the
    /// report is looking for.
    /// </remarks>
    private static IReadOnlyDictionary<String, String> Values(LayoutBox? node, IReadOnlyList<String> styles)
    {
        var values = new Dictionary<String, String>(StringComparer.Ordinal);

        if (node is null)
        {
            return values;
        }

        var style = node.Source.Style;

        foreach (var name in styles)
        {
            values[name] = name switch
            {
                "font-size" => Pixels(style.FontSize),
                "line-height" => style.LineHeight is { } height ? Pixels(height) : "normal",
                "display" => Display(style),
                "position" => style.Position.Position.ToString().ToLowerInvariant(),
                _ => Unknown(name),
            };
        }

        return values;
    }

    private static String Display(ComputedStyle style)
    {
        var display = style.Display;

        if (style.Position.Position is PositionMode.Absolute or PositionMode.Fixed &&
            display.StartsWith("inline", StringComparison.OrdinalIgnoreCase))
        {
            return "block";
        }

        return display;
    }

    private static String Pixels(Double value) =>
        value.ToString("0.###", CultureInfo.InvariantCulture) + "px";

    private static String Unknown(String name) =>
        $"?{name}";

    private static IEnumerable<LayoutBox> Walk(LayoutBox node)
    {
        yield return node;

        foreach (var child in node.Children)
        {
            foreach (var descendant in Walk(child))
            {
                yield return descendant;
            }
        }
    }

    public void Dispose()
    {
        _view.Dispose();
        _typefaces?.Dispose();
        _images?.Dispose();
    }
}

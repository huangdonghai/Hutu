using AngleSharp.Css;
using Hutu.Rendering.Layout;
using System;
using System.Collections.Generic;

namespace Hutu.Rendering;

/// <summary>
/// The render device AngleSharp.Css consults while cascading and while resolving relative lengths.
/// </summary>
/// <remarks>
/// The device is read in two places, both during style computation rather than during drawing:
/// media queries are validated against the viewport (<c>StyleCollection.CollectRules</c>), and
/// relative lengths (<c>%</c>, <c>em</c>, <c>rem</c>, <c>vw</c>/<c>vh</c>) are converted against
/// <see cref="RenderWidth"/>/<see cref="RenderHeight"/> and <see cref="RootFontSize"/>
/// (<c>CssLengthValue.ToPixel</c>).
/// <para>
/// The conversion throws when a dimension is not strictly positive, so this device always reports
/// a usable viewport and the host is expected to update it before triggering a relayout.
/// </para>
/// </remarks>
internal sealed class HutuRenderDevice : IRenderDevice, IRenderDevicePreferences, IFontMetricSource
{
    /// <summary>The viewport assumed until the host reports its real size.</summary>
    public const Int32 DefaultViewportWidth = 800;

    /// <summary>The viewport height assumed until the host reports its real size.</summary>
    public const Int32 DefaultViewportHeight = 600;

    private readonly Dictionary<String, String> _preferences = new(StringComparer.OrdinalIgnoreCase);

    public HutuRenderDevice(Int32 viewportWidth = DefaultViewportWidth, Int32 viewportHeight = DefaultViewportHeight)
    {
        ViewPortWidth = viewportWidth > 0 ? viewportWidth : DefaultViewportWidth;
        ViewPortHeight = viewportHeight > 0 ? viewportHeight : DefaultViewportHeight;
    }

    /// <inheritdoc />
    public Int32 ViewPortWidth { get; private set; }

    /// <inheritdoc />
    public Int32 ViewPortHeight { get; private set; }

    /// <summary>
    /// Takes whether the render width or height has been read since the last call, and clears the
    /// mark.
    /// </summary>
    /// <remarks>
    /// The two dimensions are the only viewport-relative input a style resolution has. Every other
    /// unit is absolute or font-relative, and a percentage is deliberately left unresolved by
    /// <see cref="LengthReader"/> so that layout can resolve it against the containing block - a
    /// <c>calc()</c> that folds to a percentage being the one exception, which reads them here too.
    /// So a build that never touched them produced styles that cannot move when the viewport does,
    /// which is what lets a resize lay the tree out again without computing it over.
    /// </remarks>
    internal Boolean TakeViewportRead()
    {
        var read = _readViewport;
        _readViewport = false;
        return read;
    }

    private Boolean _readViewport;

    /// <inheritdoc />
    public Double RenderWidth
    {
        get
        {
            _readViewport = true;
            return ViewPortWidth / Zoom;
        }
    }

    /// <inheritdoc />
    public Double RenderHeight
    {
        get
        {
            _readViewport = true;
            return ViewPortHeight / Zoom;
        }
    }

    /// <summary>How much the page is scaled for display, 1 meaning no scaling.</summary>
    private Double _zoom = 1;

    /// <summary>
    /// Gets or sets the page zoom, the way a browser's Ctrl + does. The viewport the document is laid
    /// out at is the window divided by the zoom, so a change reflows the page: the content is laid out
    /// again at the scaled width and drawn that much larger, rather than being magnified after the fact.
    /// </summary>
    /// <remarks>
    /// The font size and the resolution are left alone, because a zoomed pixel is still a pixel: a
    /// 16px face keeps its 16 CSS pixels and is merely drawn over two of them. It is the viewport -
    /// and with it every percentage, every <c>vw</c>, and every media query - that scales.
    /// </remarks>
    public Double Zoom
    {
        get => _zoom;
        set => _zoom = value > 0 ? value : 1;
    }

    /// <summary>
    /// Gets or sets the root font size in pixels, which is what every <c>rem</c> in the document is a
    /// share of.
    /// </summary>
    /// <remarks>
    /// The number is the document's to decide - <c>html { font-size: 62.5% }</c> is the idiom a whole
    /// design system is built on, and a page that says it means ten, not sixteen - but the length
    /// conversion is AngleSharp's and it has only this device to ask. So the box builder hands the
    /// answer over once the root's own size is resolved, and hands over the initial value until it has:
    /// a document that says nothing, and any property of the root element read before its size is known,
    /// are measured against that.
    /// </remarks>
    public Double RootFontSize { get; set; } = InitialFontSize;

    /// <summary>
    /// Gets the font size a document that says nothing starts from: the <c>medium</c> every browser
    /// begins at. It is also what a relative <c>font-size</c> on the root element resolves against,
    /// since the root is the one element with no parent to take a size from.
    /// </summary>
    public const Double InitialFontSize = 16;

    /// <inheritdoc />
    public Double FontSize => RootFontSize;

    internal ITextMeasurer? Text { get; set; }

    /// <inheritdoc />
    public Double? XHeight(FontStyle font)
    {
        var height = Text?.GetMetrics(font).XHeight ?? 0;

        return height > 0 ? height : null;
    }

    /// <inheritdoc />
    public Double? ZeroAdvance(FontStyle font)
    {
        var width = Text?.MeasureWidth("0", font) ?? 0;

        return width > 0 ? width : null;
    }

    /// <inheritdoc />
    public Boolean IsInterlaced => false;

    /// <inheritdoc />
    public Boolean IsScripting => false;

    /// <inheritdoc />
    public Boolean IsGrid => false;

    /// <inheritdoc />
    public Int32 DeviceWidth => ViewPortWidth;

    /// <inheritdoc />
    public Int32 DeviceHeight => ViewPortHeight;

    /// <inheritdoc />
    public Int32 Resolution => 96;

    /// <inheritdoc />
    public Int32 Frequency => 60;

    /// <inheritdoc />
    public Int32 ColorBits => 32;

    /// <inheritdoc />
    public Int32 MonochromeBits => 0;

    /// <inheritdoc />
    public DeviceCategory Category => DeviceCategory.Screen;

    /// <inheritdoc />
    public IReadOnlyDictionary<String, String> Preferences => _preferences;

    /// <summary>
    /// Updates the viewport, clamping to at least one pixel so relative length conversion never
    /// fails. Returns whether the size actually changed.
    /// </summary>
    public Boolean UpdateViewport(Int32 width, Int32 height)
    {
        var nextWidth = Math.Max(1, width);
        var nextHeight = Math.Max(1, height);

        if (nextWidth == ViewPortWidth && nextHeight == ViewPortHeight)
        {
            return false;
        }

        ViewPortWidth = nextWidth;
        ViewPortHeight = nextHeight;
        return true;
    }
}

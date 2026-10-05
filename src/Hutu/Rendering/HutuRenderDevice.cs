using AngleSharp.Css;
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
/// <see cref="RenderWidth"/>/<see cref="RenderHeight"/> (<c>CssLengthValue.ToPixel</c>).
/// <para>
/// The conversion throws when a dimension is not strictly positive, so this device always reports
/// a usable viewport and the host is expected to update it before triggering a relayout.
/// </para>
/// </remarks>
public sealed class HutuRenderDevice : IRenderDevice, IRenderDevicePreferences
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

    /// <inheritdoc />
    public Double RenderWidth => ViewPortWidth;

    /// <inheritdoc />
    public Double RenderHeight => ViewPortHeight;

    /// <summary>
    /// Gets or sets the default font size in pixels. It stands in for the root font size, so it
    /// is what <c>em</c> and <c>rem</c> resolve against.
    /// </summary>
    public Double FontSize { get; set; } = 16;

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

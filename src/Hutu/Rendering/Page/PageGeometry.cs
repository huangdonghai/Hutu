using System;
using Hutu.Rendering.Layout;

namespace Hutu.Rendering;

/// <summary>
/// The size of a page box, in CSS pixels.
/// </summary>
/// <remarks>
/// CSS Page 3 §3.3 leaves the size of a page the document says nothing about to the user agent, and
/// gives no name to that answer. The one here is Letter: it is the default a Chromium print pipeline
/// produces when nobody asks for another, which is the behaviour a PDF made without a <c>@page</c>
/// rule has to match.
/// </remarks>
public readonly record struct PageSize(Double Width, Double Height)
{
    /// <summary>Gets the size of a page the document states none for.</summary>
    public static PageSize Default { get; } = new(8.5 * 96, 11 * 96);

    /// <summary>
    /// Gets the size turned the way the orientation word asks: <c>landscape</c> is the wider of the
    /// two dimensions used as the width, <c>portrait</c> the taller.
    /// </summary>
    public PageSize Oriented(Boolean landscape) =>
        landscape
            ? new PageSize(Math.Max(Width, Height), Math.Min(Width, Height))
            : new PageSize(Math.Min(Width, Height), Math.Max(Width, Height));

    public override String ToString() =>
        Width.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) + "x" +
        Height.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>
/// The page box a page is laid in: its size and the margins around the page area (CSS Page 3 §3).
/// </summary>
public readonly record struct PageGeometry(PageSize Size, Thickness Margins)
{
    /// <summary>Gets the geometry of a page the document says nothing about.</summary>
    public static PageGeometry Default { get; } = new(PageSize.Default, Thickness.Zero);

    public Double Width => Size.Width;

    public Double Height => Size.Height;

    /// <summary>Gets the inline extent of the page area, which is what the document is laid in.</summary>
    public Double ContentWidth => Math.Max(0, Size.Width - Margins.Horizontal);

    /// <summary>Gets the block extent of the page area.</summary>
    public Double ContentHeight => Math.Max(0, Size.Height - Margins.Vertical);

    public override String ToString() => $"{Size} margins {Margins.Top}/{Margins.Right}/{Margins.Bottom}/{Margins.Left}";
}

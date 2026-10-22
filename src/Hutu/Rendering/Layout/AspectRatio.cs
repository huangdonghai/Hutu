using System;
using System.Globalization;

namespace Hutu.Rendering.Layout;

/// <summary>
/// The ratio a box's width and height keep to, as CSS Sizing 4 §4 states it.
/// </summary>
/// <remarks>
/// The value is a pair of numbers written with a slash between them - <c>16/9</c> - or the keyword
/// <c>auto</c> on one side of it, which asks that side to come from the content rather than from the
/// ratio. A ratio of zero is not a ratio: nothing can keep to it, so it is no ratio at all.
/// </remarks>
/// <param name="Width">The width the ratio gives for a height of one.</param>
/// <param name="Height">The height the ratio gives for a width of one.</param>
internal readonly record struct AspectRatio(Double Width, Double Height)
{
    /// <summary>Gets the ratio as a plain number: how many times as wide as it is high.</summary>
    public Double Value => Height > 0 ? Width / Height : 0.0;

    /// <summary>
    /// Reads a ratio out of the text of a declaration, or null when the text names none.
    /// </summary>
    /// <remarks>
    /// The grammar is small: a number, a slash and a number, with <c>auto</c> standing for either side
    /// that the page left to the content. A ratio whose numbers are not both there, or whose height is
    /// zero, is not a ratio and leaves the box as it was.
    /// </remarks>
    public static AspectRatio? Parse(String? text)
    {
        if (String.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var value = text.Trim();

        // A ratio may name the side that comes from the content, and only that side: "auto 16/9" is a
        // height of nothing and a width of sixteen ninths, and "auto auto" names no ratio at all.
        var autoWidth = false;
        var autoHeight = false;

        if (value.StartsWith("auto", StringComparison.OrdinalIgnoreCase))
        {
            autoWidth = true;
            value = value[4..].Trim();
        }

        var slash = value.IndexOf('/');

        if (slash < 0)
        {
            return null;
        }

        var width = value[..slash].Trim();
        var height = value[(slash + 1)..].Trim();

        if (autoHeight)
        {
            return null;
        }

        if (width.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            autoWidth = true;
            width = String.Empty;
        }

        if (height.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            autoHeight = true;
            height = String.Empty;
        }

        // Both sides left to the content is no ratio, and a side that is left alone with the other
        // given is a half ratio: the given side fixes the other.
        if (autoWidth && autoHeight)
        {
            return null;
        }

        if (autoWidth)
        {
            return Number(height) is { } onlyHeight ? new AspectRatio(0, onlyHeight) : null;
        }

        if (autoHeight)
        {
            return Number(width) is { } onlyWidth ? new AspectRatio(onlyWidth, 0) : null;
        }

        if (Number(width) is not { } fullWidth || Number(height) is not { } fullHeight)
        {
            return null;
        }

        return fullHeight > 0 ? new AspectRatio(fullWidth, fullHeight) : null;
    }

    /// <summary>Reads a number, which may be written with a fraction's denominator beside it.</summary>
    private static Double? Number(String text)
    {
        if (text.Length == 0)
        {
            return null;
        }

        return Double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
    }
}

namespace Hutu.Rendering.Layout;

/// <summary>Bridges the CSS-facing style wrapper onto the layout-side font description.</summary>
internal static class ComputedStyleExtensions
{
    public static FontStyle GetFont(this ComputedStyle style)
    {
        var values = style.Values;
        var area = values.Font;
        var size = style.FontSize;

        if (ReferenceEquals(values.HandleManager, values.FontManager) &&
            ReferenceEquals(values.HandleArea, area) &&
            values.HandleSize.Equals(size))
        {
            return values.Handle;
        }

        var font = values.FontManager.GetOrAdd(
            area.Family,
            size,
            area.Weight,
            area.Italic,
            area.Adjust,
            area.Features);

        values.HandleArea = area;
        values.HandleManager = values.FontManager;
        values.HandleSize = size;
        values.Handle = font;

        return font;
    }
}

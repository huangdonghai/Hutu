namespace Hutu.Rendering.Layout;

/// <summary>Bridges the CSS-facing style wrapper onto the layout-side font description.</summary>
internal static class ComputedStyleExtensions
{
    public static FontStyle GetFont(this ComputedStyle style) =>
        new(style.FontFamily, style.FontSize, style.FontWeight, style.IsItalic, style.ResolvedFont.Adjust);
}

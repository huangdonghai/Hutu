using System;

namespace Hutu.Rendering.Layout;

/// <summary>
/// One laid-out line of text as produced by line breaking, before it is positioned in a line box.
/// </summary>
internal readonly struct TextLine
{
    public TextLine(String text, Double width)
    {
        Text = text;
        Width = width;
    }

    /// <summary>Gets the text of the line, with trailing collapsible spaces already removed.</summary>
    public String Text { get; }

    /// <summary>Gets the measured advance width of the line, in pixels.</summary>
    public Double Width { get; }
}

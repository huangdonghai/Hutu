using System;

namespace Hutu.Rendering.Layout;

/// <summary>
/// The shape a text decoration is drawn in, as CSS Text Decoration 4 §3.3 puts it.
/// </summary>
public enum DecorationShape : Byte
{
    /// <summary>An unbroken line.</summary>
    Solid,

    /// <summary>Two lines with a gap between them.</summary>
    Double,

    /// <summary>A line broken into round dots.</summary>
    Dotted,

    /// <summary>A line broken into dashes.</summary>
    Dashed,

    /// <summary>A line that runs up and down as it goes.</summary>
    Wavy,
}

/// <summary>
/// One decorating box's lines, as they are drawn across the text they reach.
/// </summary>
/// <remarks>
/// A decoration is set on an element and drawn over the text of the boxes inside it (CSS Text Decoration
/// 4 §2.1), so what a run of text has to draw is not one answer but a list: every box around it that
/// asked for lines, from the outermost in, each drawing its own line in its own shape and its own
/// colour. A descendant that asks for none takes nothing away - the line the ancestor asked for still
/// runs across it - and the lines are painted outermost first so that the inner ones lie over them
/// (CSS Text Decoration 4 §5.1).
/// </remarks>
public sealed record DecorationSource(
    Boolean Underline,
    Boolean Overline,
    Boolean LineThrough,
    DecorationShape Shape,
    UInt32 Color,
    Double? Thickness,
    Double? UnderlineOffset);

/// <summary>
/// Where one decorating box's line stands on one line of text and how thick it is, settled from all the
/// text that box decorates on that line: for underlines and overlines a decorating box uses a single
/// position and a single thickness on each line, whatever the sizes of the text it runs across
/// (CSS Text Decoration 4 §2.9). The distances are taken along the axis the line is offset from - down
/// the page for a line, across it for a column - and from the baseline the line as a whole stands on,
/// which is not the baseline of the run drawing them where that run sits higher or lower. A line-through
/// answers none of this: §2.5 has it recomputed wherever the font size changes, so that the text stays
/// crossed out.
/// </summary>
public sealed record DecorationPlacement(
    Double BaselineShift,
    Double UnderlineOffset,
    Double UnderlineThickness,
    Double OverlineOffset,
    Double OverlineThickness);

/// <summary>
/// Reads the words a decoration was declared with: which lines the element asks for, and in what shape.
/// </summary>
internal static class DecorationText
{
    private static readonly Char[] Separators = [' ', ',', '\t', '\n', '/'];

    /// <summary>
    /// Gets whether the declared text names the given line kind. The kinds are matched as words and
    /// not as fragments of the text, because <c>overline</c> is not an <c>underline</c> and the two
    /// share the <c>line</c> a fragment match would stop at.
    /// </summary>
    public static Boolean Names(String? line, String? shorthand, String kind) =>
        Mentions(String.IsNullOrWhiteSpace(line) ? shorthand : line, kind);

    /// <summary>
    /// Gets the shape the decoration is drawn in, from its own longhand where that was declared and
    /// from the shorthand otherwise - which is how <c>text-decoration: underline wavy</c> says both at
    /// once. A page that names no shape gets a solid line, the initial value.
    /// </summary>
    public static DecorationShape Shape(String? declared, String? shorthand)
    {
        var text = String.IsNullOrWhiteSpace(declared) ? shorthand : declared;

        if (String.IsNullOrWhiteSpace(text))
        {
            return DecorationShape.Solid;
        }

        foreach (var token in Split(text))
        {
            switch (token)
            {
                case "double":
                    return DecorationShape.Double;
                case "dotted":
                    return DecorationShape.Dotted;
                case "dashed":
                    return DecorationShape.Dashed;
                case "wavy":
                    return DecorationShape.Wavy;
            }
        }

        return DecorationShape.Solid;
    }

    private static Boolean Mentions(String? text, String kind)
    {
        if (String.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        foreach (var token in Split(text))
        {
            if (token.Equals(kind, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static String[] Split(String? text) =>
        text!.Split(Separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}

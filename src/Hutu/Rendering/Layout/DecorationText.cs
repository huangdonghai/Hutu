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
/// Whether a decoration's line is broken where the text's ink crosses it. Only underlines and overlines
/// skip at all - a line-through is always continuous - and <c>auto</c> leaves the answer to the engine,
/// which is what lets a script whose glyphs are drawn across the rule keep it whole
/// (CSS Text Decoration 4 §2.10.4).
/// </summary>
public enum DecorationSkip : Byte
{
    /// <summary>Skip where the ink falls, as long as the script of the text wants it.</summary>
    Auto,

    /// <summary>Draw the line straight through, whatever the glyphs do.</summary>
    None,

    /// <summary>Skip where the ink falls, whatever script the text is set in.</summary>
    All,
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
    Double? UnderlineOffset,
    DecorationInsets? Insets)
{
    /// <summary>
    /// Gets the element that asked for these lines. It is not part of what the record answers: two boxes
    /// asking for identical lines are still two decorating boxes, and this is how the run of text one of
    /// them decorates finds its way back to the box that owns the line (CSS Text Decoration 4 §2.1).
    /// </summary>
    public Object? Speaker { get; init; }

    /// <summary>
    /// Gets the font the decorating box is set in. A column stands the line it draws at a distance
    /// taken from that font - its inline box and its own underline metric - rather than from the font
    /// of each run the line happens to cross, so that a note set smaller than the text beside it is
    /// lined at its own size and a run that changes face does not move the line (§2.9). It is not part
    /// of what the record answers, for the reason <see cref="Speaker"/> is not.
    /// </summary>
    public FontStyle Font { get; init; }

    /// <summary>
    /// Gets where the decorating box begins along the line. A wavy line is counted from that place, so
    /// that the runs the box is drawn in carry on one wave rather than each beginning a new one where it
    /// starts - which is where the browser beside this one counts it from as well (§3.3). It is not part
    /// of what the record answers, for the reason <see cref="Speaker"/> is not.
    /// </summary>
    public Double Start { get; init; }

    /// <summary>
    /// Gets where this decorating box stands its underlines (CSS Text Decoration 4 §2.7). Two boxes
    /// that draw the same line but stand it on different sides are different decorators, so the
    /// position is part of the value the record compares.
    /// </summary>
    public TextUnderlinePosition Position { get; init; } = TextUnderlinePosition.Auto;

    public Boolean Equals(DecorationSource? other) =>
        other is not null &&
        Underline == other.Underline &&
        Overline == other.Overline &&
        LineThrough == other.LineThrough &&
        Shape == other.Shape &&
        Color == other.Color &&
        Thickness == other.Thickness &&
        UnderlineOffset == other.UnderlineOffset &&
        Insets == other.Insets &&
        Position == other.Position;

    public override int GetHashCode() =>
        HashCode.Combine(
            HashCode.Combine(
                Underline,
                Overline,
                LineThrough,
                (Int32)Shape,
                Color),
            Thickness,
            UnderlineOffset,
            Insets,
            Position);
}

/// <summary>
/// How far back the ends of the lines a box asks for stand: <c>text-decoration-inset</c>, in the two
/// units it takes. A length is the distance in pixels and a share is held back to be taken from the
/// inline size the decorated text fills, since that is what the property measures against - the whole
/// of it where the decoration is sliced across lines, and one fragment's own where the box repeats its
/// decoration on every line it reaches (CSS Text Decoration 4 §2.9.1). Where nothing was said the line
/// runs to the edges it was given; <c>auto</c> leaves the distance to the engine, which takes the
/// width of the line itself so that two boxes set side by side do not read as one long rule.
/// </summary>
/// <param name="Auto">
/// Whether the page asked for the engine's own answer rather than a distance.
/// </param>
/// <param name="PerFragment">
/// Whether the box repeats its decoration at each line it reaches - <c>box-decoration-break: clone</c> -
/// which is where the two units above are measured from.
/// </param>
public sealed record DecorationInsets(
    Double? Start,
    Double? End,
    Double? StartShare,
    Double? EndShare,
    Boolean Auto,
    Boolean PerFragment);

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
    Double OverlineThickness)
{
    /// <summary>
    /// Gets how far the start of this run's stretch of the line is pulled inward, and
    /// <see cref="TrimEnd"/> how far its end is: what <c>text-decoration-inset</c> came to for the run,
    /// counted along the whole of the line the decorating box decorates (CSS Text Decoration 4 §2.9.1).
    /// </summary>
    public Double TrimStart { get; init; }

    public Double TrimEnd { get; init; }
}

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

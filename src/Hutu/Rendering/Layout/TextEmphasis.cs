using AngleSharp.Css.Dom;
using AngleSharp.Css.Values;
using Hutu.Rendering.Media;
using System;
using System.Collections.Generic;
using System.Globalization;

namespace Hutu.Rendering.Layout;

/// <summary>
/// Which edge of the line's block extent the marks of <c>text-emphasis</c> stand on, which is what the
/// <c>over</c> and <c>under</c> half of the value says (CSS Text Decoration 4 §5.2).
/// </summary>
internal enum EmphasisEdge
{
    /// <summary>The block-start edge, which is what <c>over</c> asks for.</summary>
    Start,

    /// <summary>The block-end edge, which is what <c>under</c> asks for.</summary>
    End,
}

/// <summary>
/// Which physical side of the text the marks stand on, which is what the <c>right</c> and <c>left</c>
/// half of the value says. Only a column answers it: on a line those two say nothing, and a line reads
/// the edge from <c>over</c> and <c>under</c> alone. The half always has a value, because the initial
/// value of the property names a side - <c>over right</c> - so a page that writes only <c>under</c>
/// still stands its marks to the right of a column (CSS Text Decoration 4 §5.2).
/// </summary>
internal enum EmphasisSide
{
    /// <summary>The marks stand to the right of the text, whatever the writing mode is.</summary>
    Right,

    /// <summary>The marks stand to the left of the text, whatever the writing mode is.</summary>
    Left,
}

/// <summary>Which characters of a run go unmarked (CSS Text Decoration 4 §5.4).</summary>
[Flags]
internal enum EmphasisSkip
{
    /// <summary>Every character takes a mark, spaces included.</summary>
    None = 0,

    /// <summary>Spaces are left alone.</summary>
    Space = 1,

    /// <summary>Punctuation is left alone.</summary>
    Punctuation = 2,

    /// <summary>Symbols are left alone.</summary>
    Symbols = 4,

    /// <summary>
    /// What a page that says nothing asks for: the spaces, the punctuation and the symbols, so a marked
    /// English word keeps its letters and loses its brackets and a CJK sentence is marked letter by
    /// letter with no mark over any of its stops (CSS Text Decoration 4 §5.4.1).
    /// </summary>
    Auto = 8,
}

/// <summary>
/// One mark, where it stands, what it is drawn from and in what colour - everything the painter needs
/// and nothing else, because a mark takes no room the layout has to keep account of.
/// </summary>
internal readonly record struct EmphasisMark(
    Rect Band,
    String Text,
    Double Baseline,
    FontStyle Font,
    UInt32 Color);

/// <summary>
/// The marks <c>text-emphasis</c> asks a run to be drawn with: the glyph or string the style came to,
/// the edge of the line they stand against, the colour they are drawn in, and what the run leaves
/// unmarked.
/// </summary>
/// <remarks>
/// Marks are paint and nothing else: they take no room in the line, which is why the style is read here
/// and each mark is placed against the room the layout already gave the text.
/// </remarks>
internal readonly struct TextEmphasis
{
    /// <summary>Gets the marks nothing asks for.</summary>
    internal static readonly TextEmphasis None = default;

    /// <summary>Gets the string drawn over each character that takes a mark.</summary>
    public String? Mark { get; init; }

    /// <summary>Gets the edge of the line's block extent the marks stand against.</summary>
    public EmphasisEdge Edge { get; init; }

    /// <summary>Gets the side of a column the marks stand on, named or come to by default.</summary>
    public EmphasisSide Side { get; init; }

    /// <summary>Gets which characters of the run are left unmarked.</summary>
    public EmphasisSkip Skip { get; init; }

    /// <summary>Gets the colour the marks are drawn in, where the page named one.</summary>
    public UInt32 Color { get; init; }

    /// <summary>Gets whether the page named a colour at all.</summary>
    public Boolean HasColor { get; init; }

    /// <summary>Gets whether this asks for anything to be drawn.</summary>
    public Boolean IsSet => Mark is { Length: > 0 };

    /// <summary>
    /// Gets whether the marks stand at the far edge of the line's block extent in this flow. The two
    /// halves of the value answer different flows: a line reads <c>over</c> and <c>under</c> and pays no
    /// heed to the side, while a column reads only the side - which its value always gives it, the
    /// initial value naming the right.
    /// </summary>
    internal Boolean StandsAtTheEnd(WritingModeInfo flow)
    {
        if (!flow.IsVertical)
        {
            return Edge == EmphasisEdge.End;
        }

        return Side == EmphasisSide.Right ? !flow.StartsAtTheRight : flow.StartsAtTheRight;
    }

    /// <summary>
    /// Gets whether a character of the run is left unmarked. A space is never marked unless the page
    /// asked for every character, and what nothing was said about leaves the punctuation and the symbols
    /// alone beside it - which is what a CJK sentence's references show: the marks stand over the
    /// letters and the digits, and no mark stands over <c>。</c>, <c>，</c> or <c>…</c>.
    /// </summary>
    internal Boolean Skips(String element)
    {
        if (Skip == EmphasisSkip.None || element.Length == 0)
        {
            return false;
        }

        var first = element[0];

        // A page that says nothing asks for the spaces, the punctuation and the symbols to go unmarked;
        // naming the sets asks for exactly those.
        var asked = Skip == EmphasisSkip.Auto
            ? EmphasisSkip.Space | EmphasisSkip.Punctuation | EmphasisSkip.Symbols
            : Skip;

        if ((asked & EmphasisSkip.Space) != 0 && Char.IsWhiteSpace(first))
        {
            return true;
        }

        if ((asked & EmphasisSkip.Punctuation) != 0 && IsPunctuation(first))
        {
            return true;
        }

        return (asked & EmphasisSkip.Symbols) != 0 && IsSymbol(first);
    }

    private static Boolean IsPunctuation(Char character) =>
        Char.GetUnicodeCategory(character) switch
        {
            UnicodeCategory.OpenPunctuation => true,
            UnicodeCategory.ClosePunctuation => true,
            UnicodeCategory.ConnectorPunctuation => true,
            UnicodeCategory.DashPunctuation => true,
            UnicodeCategory.OtherPunctuation => true,
            UnicodeCategory.InitialQuotePunctuation => true,
            UnicodeCategory.FinalQuotePunctuation => true,
            _ => false,
        };

    private static Boolean IsSymbol(Char character) =>
        Char.GetUnicodeCategory(character) switch
        {
            UnicodeCategory.MathSymbol => true,
            UnicodeCategory.CurrencySymbol => true,
            UnicodeCategory.ModifierSymbol => true,
            UnicodeCategory.OtherSymbol => true,
            _ => false,
        };

    /// <summary>Reads the marks of a style the box builder resolved rather than fused.</summary>
    internal static TextEmphasis Read(ComputedStyle style) => Read(
        name => style.Declaration.TryGetProperty(name),
        style.Color);

    /// <summary>
    /// Reads the marks the cascade settled: the shape or string the style came to, the edge, the colour
    /// and what is skipped. The shorthand is read token by token beside the longhands, and a longhand the
    /// page wrote answers for its own part.
    /// </summary>
    internal static TextEmphasis Read(
        Func<String, ICssProperty?> find,
        CssColorValue color,
        TextEmphasis inherited = default)
    {
        // Every part of the marks is inherited on its own: an element that names only the position
        // keeps the marks and the colour its ancestors asked for, and one that names none of them takes
        // everything from above (CSS Text Decoration 4 §5.2).
        var mark = inherited.Mark;
        var shape = (String?)null;
        var filled = true;
        var edge = inherited.Edge;
        var side = inherited.Side;
        var skip = inherited.IsSet ? inherited.Skip : EmphasisSkip.Auto;
        var paint = inherited.HasColor ? inherited.Color : ComputedStyle.ToArgb(color);
        var named = inherited.HasColor;

        // `text-emphasis` carries the style as part of itself, so a page that wrote only the shorthand
        // is read from it, and a longhand written beside it answers the shape on its own.
        var written = Value(Declared(find, "text-emphasis-style"));

        if (String.IsNullOrEmpty(written))
        {
            written = Value(Declared(find, "text-emphasis"));
        }

        foreach (var token in Words(written))
        {
            var word = Unquote(token);

            if (word.Length == 0)
            {
                continue;
            }

            if (String.Equals(word, "none", StringComparison.OrdinalIgnoreCase))
            {
                mark = null;
                continue;
            }

            if (word.Equals("filled", StringComparison.OrdinalIgnoreCase))
            {
                filled = true;
            }
            else if (word.Equals("open", StringComparison.OrdinalIgnoreCase))
            {
                filled = false;
            }
            else if (Glyph(word, filled) is { } drawn)
            {
                shape = drawn;
            }
            else if (IsQuoted(token))
            {
                mark = word;
            }
            else if (BackgroundLayerParser.ParseColor(token) is { } argb)
            {
                paint = argb;
                named = true;
            }
        }

        mark ??= shape;

        foreach (var token in Words(Value(Declared(find, "text-emphasis-position"))))
        {
            if (token.Equals("under", StringComparison.OrdinalIgnoreCase))
            {
                edge = EmphasisEdge.End;
            }
            else if (token.Equals("over", StringComparison.OrdinalIgnoreCase))
            {
                edge = EmphasisEdge.Start;
            }
            else if (token.Equals("right", StringComparison.OrdinalIgnoreCase))
            {
                side = EmphasisSide.Right;
            }
            else if (token.Equals("left", StringComparison.OrdinalIgnoreCase))
            {
                side = EmphasisSide.Left;
            }
        }

        var skipped = Value(Declared(find, "text-emphasis-skip"));

        if (!String.IsNullOrEmpty(skipped))
        {
            var words = Words(skipped);
            var sets = EmphasisSkip.None;

            foreach (var token in words)
            {
                sets |= token.ToLowerInvariant() switch
                {
                    "space" or "nospaces" or "leading-space" => EmphasisSkip.Space,
                    "punctuation" => EmphasisSkip.Punctuation,
                    "symbols" => EmphasisSkip.Symbols,
                    "auto" => EmphasisSkip.Auto,
                    _ => EmphasisSkip.None,
                };
            }

            // `skip: none` says the sets the page named - which is none at all - and an unreadable value
            // leaves the reading the specification starts with.
            skip = sets;
        }

        foreach (var token in Words(Value(Declared(find, "text-emphasis-color"))))
        {
            if (BackgroundLayerParser.ParseColor(token) is { } colour)
            {
                paint = colour;
                named = true;
            }
        }

        if (mark is null)
        {
            return None;
        }

        return new TextEmphasis
        {
            Mark = mark,
            Edge = edge,
            Side = side,
            Skip = skip,
            Color = paint,
            HasColor = named,
        };
    }

    /// <summary>Gets the glyph a shape keyword draws, with the fill the page asked for or <c>filled</c>.</summary>
    private static String? Glyph(String word, Boolean filled) => word.ToLowerInvariant() switch
    {
        "dot" => Character(filled ? 0x2022 : 0x25e6),
        "circle" => Character(filled ? 0x25cf : 0x25cb),
        "double-circle" => Character(filled ? 0x25c9 : 0x25ce),
        "triangle" => Character(filled ? 0x25b2 : 0x25b3),
        "sesame" => Character(filled ? 0xfe45 : 0xfe46),
        _ => null,
    };

    private static String Character(Int32 codePoint) => Char.ConvertFromUtf32(codePoint);

    private static String? Value(ICssProperty? declared) => declared?.Value;

    /// <summary>
    /// Gets a declaration the element itself wrote: the cascade hands out an <c>initial</c> property for
    /// a name nobody authored, and that is not something the element asked for.
    /// </summary>
    private static ICssProperty? Declared(Func<String, ICssProperty?> find, String name)
    {
        var declared = find(name);

        if (declared is null || String.IsNullOrWhiteSpace(declared.Value))
        {
            return null;
        }

        return declared.Value.Trim().Equals("initial", StringComparison.OrdinalIgnoreCase)
            ? null
            : declared;
    }

    private static Boolean IsQuoted(String token) =>
        token.Length > 1 && (token[0] == '"' || token[0] == '\'');

    private static String Unquote(String token) => IsQuoted(token) ? token[1..^1] : token;

    /// <summary>The tokens of a value, split on white space outside a quoted string.</summary>
    private static List<String> Words(String? value)
    {
        var words = new List<String>();

        if (String.IsNullOrEmpty(value))
        {
            return words;
        }

        var quote = '\0';
        var start = 0;

        for (var index = 0; index < value!.Length; index++)
        {
            var character = value[index];

            if (quote == '\0' && (character == '"' || character == '\''))
            {
                quote = character;
            }
            else if (character == quote)
            {
                quote = '\0';
            }
            else if (quote == '\0' && Char.IsWhiteSpace(character))
            {
                words.Add(value[start..index]);
                start = index + 1;
            }
        }

        words.Add(value[start..]);

        return words;
    }
}

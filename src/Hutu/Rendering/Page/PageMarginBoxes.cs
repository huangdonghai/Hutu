using Hutu.Rendering.Layout;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Hutu.Rendering;

/// <summary>One of the sixteen boxes a page may set content in, around its page area (CSS Page 3 §5.3).</summary>
public enum PageMarginSlot : Byte
{
    TopLeftCorner,
    TopLeft,
    TopCenter,
    TopRight,
    TopRightCorner,
    RightTop,
    RightMiddle,
    RightBottom,
    BottomRightCorner,
    BottomRight,
    BottomCenter,
    BottomLeft,
    BottomLeftCorner,
    LeftBottom,
    LeftMiddle,
    LeftTop,
}

/// <summary>Where a margin box's text stands along its inline axis.</summary>
public enum PageTextAlign : Byte
{
    Start,
    Center,
    End,
}

/// <summary>Where a margin box's text stands along its block axis.</summary>
public enum PageTextVertical : Byte
{
    Top,
    Middle,
    Bottom,
}

/// <summary>
/// The text a page's margin box carries, resolved for one page: where it stands and how it is set.
/// </summary>
public readonly record struct PageMarginBand(
    Rect Area,
    String Text,
    UInt32 Color,
    Double FontSize,
    String Family,
    Int32 Weight,
    Boolean Italic,
    PageTextAlign Align,
    PageTextVertical Vertical);

/// <summary>One margin box's declarations, as the page wrote them: read when a page needs its text.</summary>
internal readonly record struct PageMarginBox(PageMarginSlot Slot, String? Content, String? Color, String? FontSize,
    String? FontFamily, String? FontWeight, String? FontStyle, String? TextAlign, String? VerticalAlign);

/// <summary>The margin boxes of one <c>@page</c> rule, with the selector that says which pages they answer.</summary>
internal readonly record struct PageMarginRule(String Selector, IReadOnlyList<PageMarginBox> Boxes);

/// <summary>
/// Reads the margin boxes of the <c>@page</c> rules out of the sheets' own text (CSS Page 3 §5.2).
/// </summary>
/// <remarks>
/// The CSS object model keeps a page rule's declarations and drops the at-rules nested in it, so the
/// boxes are read where they still stand: in the text the page wrote. What is read is the boxes'
/// declarations alone; the geometry the boxes stand in is settled when a page is laid out, and the
/// text they carry when the page's number is known.
/// </remarks>
internal static class PageMarginScan
{
    private static readonly Dictionary<String, PageMarginSlot> Slots = new(StringComparer.OrdinalIgnoreCase)
    {
        ["top-left-corner"] = PageMarginSlot.TopLeftCorner,
        ["top-left"] = PageMarginSlot.TopLeft,
        ["top-center"] = PageMarginSlot.TopCenter,
        ["top-right"] = PageMarginSlot.TopRight,
        ["top-right-corner"] = PageMarginSlot.TopRightCorner,
        ["right-top"] = PageMarginSlot.RightTop,
        ["right-middle"] = PageMarginSlot.RightMiddle,
        ["right-bottom"] = PageMarginSlot.RightBottom,
        ["bottom-right-corner"] = PageMarginSlot.BottomRightCorner,
        ["bottom-right"] = PageMarginSlot.BottomRight,
        ["bottom-center"] = PageMarginSlot.BottomCenter,
        ["bottom-left"] = PageMarginSlot.BottomLeft,
        ["bottom-left-corner"] = PageMarginSlot.BottomLeftCorner,
        ["left-bottom"] = PageMarginSlot.LeftBottom,
        ["left-middle"] = PageMarginSlot.LeftMiddle,
        ["left-top"] = PageMarginSlot.LeftTop,
    };

    /// <summary>Gets the margin boxes every <c>@page</c> rule of the given stylesheet texts carries.</summary>
    public static List<PageMarginRule> Read(IEnumerable<String> sheets)
    {
        var rules = new List<PageMarginRule>();

        foreach (var text in sheets)
        {
            ReadInto(text, rules);
        }

        return rules;
    }

    private static void ReadInto(String css, List<PageMarginRule> rules)
    {
        var at = 0;

        while (at < css.Length)
        {
            at = SkipTo(css, at, "@page");

            if (at < 0)
            {
                return;
            }

            at += 5;
            var open = IndexOfOutside(css, at, '{');

            if (open < 0)
            {
                return;
            }

            var selector = css[at..open].Trim();
            var close = BlockEnd(css, open);

            if (close < 0)
            {
                return;
            }

            var boxes = ReadBoxes(css, open + 1, close);

            if (boxes.Count > 0)
            {
                rules.Add(new PageMarginRule(selector, boxes));
            }

            at = close + 1;
        }
    }

    /// <summary>Reads the margin boxes standing directly in a page rule's block.</summary>
    private static List<PageMarginBox> ReadBoxes(String css, Int32 from, Int32 to)
    {
        var boxes = new List<PageMarginBox>();
        var at = from;

        while (at < to)
        {
            at = SkipSpaceAndComments(css, at);

            if (at >= to)
            {
                break;
            }

            if (css[at] != '@')
            {
                at = NextTopLevel(css, at, to, ";{");

                if (at < to && css[at] == '{')
                {
                    var end = BlockEnd(css, at);
                    at = end < 0 ? to : end + 1;
                }
                else
                {
                    at++;
                }

                continue;
            }

            var nameStart = at + 1;
            var nameEnd = nameStart;

            while (nameEnd < to && (Char.IsLetterOrDigit(css[nameEnd]) || css[nameEnd] == '-'))
            {
                nameEnd++;
            }

            var name = css[nameStart..nameEnd];
            var open = SkipSpaceAndComments(css, nameEnd);

            if (open >= to || css[open] != '{')
            {
                at = NextTopLevel(css, nameEnd, to, ";{");
                at = at < to && css[at] == '{' ? BlockEnd(css, at) + 1 : at + 1;
                continue;
            }

            var close = BlockEnd(css, open);

            if (close < 0)
            {
                break;
            }

            if (Slots.TryGetValue(name, out var slot))
            {
                boxes.Add(ReadBox(slot, css, open + 1, close));
            }

            at = close + 1;
        }

        return boxes;
    }

    private static PageMarginBox ReadBox(PageMarginSlot slot, String css, Int32 from, Int32 to)
    {
        var declarations = Declarations(css, from, to);

        return new PageMarginBox(
            slot,
            Value(declarations, "content"),
            Value(declarations, "color"),
            Value(declarations, "font-size"),
            Value(declarations, "font-family"),
            Value(declarations, "font-weight"),
            Value(declarations, "font-style"),
            Value(declarations, "text-align"),
            Value(declarations, "vertical-align"));
    }

    private static Dictionary<String, String> Declarations(String css, Int32 from, Int32 to)
    {
        var declarations = new Dictionary<String, String>(StringComparer.OrdinalIgnoreCase);
        var at = from;

        while (at < to)
        {
            var end = NextTopLevel(css, at, to, ";");
            var colon = IndexOfOutside(css, at, ':', end);

            if (colon > at)
            {
                var name = css[at..colon].Trim();
                var value = css[(colon + 1)..Math.Min(end, to)].Trim();

                if (name.Length > 0 && value.Length > 0)
                {
                    declarations[name] = value;
                }
            }

            at = end + 1;
        }

        return declarations;
    }

    private static String? Value(Dictionary<String, String> declarations, String name) =>
        declarations.TryGetValue(name, out var value) ? value : null;

    /// <summary>
    /// Gets the text a <c>content</c> declaration comes to on a page: its strings with the page's own
    /// numbers put where the counters stand (CSS GCPM 3 §7, CSS Page 3 §5.2).
    /// </summary>
    public static String Content(String? declared, Int32 number, Int32 pages)
    {
        if (String.IsNullOrWhiteSpace(declared))
        {
            return String.Empty;
        }

        var text = declared.Trim();

        if (text.Equals("none", StringComparison.OrdinalIgnoreCase) ||
            text.Equals("normal", StringComparison.OrdinalIgnoreCase))
        {
            return String.Empty;
        }

        var built = new StringBuilder();
        var at = 0;

        while (at < text.Length)
        {
            var c = text[at];

            if (Char.IsWhiteSpace(c))
            {
                at++;
                continue;
            }

            if (c is '"' or '\'')
            {
                var quote = c;
                at++;

                while (at < text.Length && text[at] != quote)
                {
                    if (text[at] == '\\' && at + 1 < text.Length)
                    {
                        at++;
                    }

                    built.Append(text[at]);
                    at++;
                }

                at++;
                continue;
            }

            if (Ident(text, at, "counter") && Function(text, at + 7) is { } argument)
            {
                var name = argument.Trim();

                if (name.Equals("page", StringComparison.OrdinalIgnoreCase))
                {
                    built.Append(number.ToString(CultureInfo.InvariantCulture));
                }
                else if (name.Equals("pages", StringComparison.OrdinalIgnoreCase))
                {
                    built.Append(pages.ToString(CultureInfo.InvariantCulture));
                }

                at += 7;
                var close = text.IndexOf(')', at);
                at = close < 0 ? text.Length : close + 1;
                continue;
            }

            // A component this slice does not render - a string-set name or an attribute - is passed
            // over where it stands rather than read as text.
            while (at < text.Length && !Char.IsWhiteSpace(text[at]))
            {
                at++;
            }
        }

        return built.ToString();
    }

    /// <summary>Gets the argument of the function opening at <paramref name="open"/>, or null.</summary>
    private static String? Function(String text, Int32 open)
    {
        if (open >= text.Length || text[open] != '(')
        {
            return null;
        }

        var close = text.IndexOf(')', open + 1);

        return close < 0 ? null : text[(open + 1)..close];
    }

    private static Boolean Ident(String text, Int32 at, String name) =>
        String.Compare(text, at, name, 0, name.Length, StringComparison.OrdinalIgnoreCase) == 0 &&
        (at + name.Length >= text.Length || !Char.IsLetterOrDigit(text[at + name.Length]) && text[at + name.Length] != '-');

    /// <summary>Gets where the block opening at <paramref name="open"/> ends, or -1.</summary>
    private static Int32 BlockEnd(String css, Int32 open)
    {
        var depth = 0;
        var at = open;

        while (at < css.Length)
        {
            var c = css[at];

            if (c is '"' or '\'')
            {
                at = SkipString(css, at);
                continue;
            }

            if (c == '/' && at + 1 < css.Length && css[at + 1] == '*')
            {
                at = SkipComment(css, at);
                continue;
            }

            if (c == '{')
            {
                depth++;
            }
            else if (c == '}')
            {
                depth--;

                if (depth == 0)
                {
                    return at;
                }
            }

            at++;
        }

        return -1;
    }

    /// <summary>Gets where <paramref name="word"/> stands outside strings and comments, or -1.</summary>
    private static Int32 SkipTo(String css, Int32 from, String word)
    {
        var at = from;

        while (at < css.Length)
        {
            var c = css[at];

            if (c is '"' or '\'')
            {
                at = SkipString(css, at);
                continue;
            }

            if (c == '/' && at + 1 < css.Length && css[at + 1] == '*')
            {
                at = SkipComment(css, at);
                continue;
            }

            if (String.Compare(css, at, word, 0, word.Length, StringComparison.OrdinalIgnoreCase) == 0 &&
                (at + word.Length >= css.Length || !Char.IsLetter(css[at + word.Length])))
            {
                return at;
            }

            at++;
        }

        return -1;
    }

    /// <summary>Gets the first <paramref name="wanted"/> character outside strings, comments and parentheses.</summary>
    private static Int32 IndexOfOutside(String css, Int32 from, Char wanted, Int32 to = -1)
    {
        var end = to < 0 ? css.Length : to;
        var at = from;
        var parens = 0;

        while (at < end)
        {
            var c = css[at];

            if (c is '"' or '\'')
            {
                at = SkipString(css, at);
                continue;
            }

            if (c == '/' && at + 1 < css.Length && css[at + 1] == '*')
            {
                at = SkipComment(css, at);
                continue;
            }

            if (c == '(')
            {
                parens++;
            }
            else if (c == ')')
            {
                parens = Math.Max(0, parens - 1);
            }
            else if (c == wanted && parens == 0)
            {
                return at;
            }

            at++;
        }

        return -1;
    }

    /// <summary>Gets the first character of <paramref name="stops"/> outside strings, comments and blocks.</summary>
    private static Int32 NextTopLevel(String css, Int32 from, Int32 to, String stops)
    {
        var at = from;

        while (at < to)
        {
            var c = css[at];

            if (c is '"' or '\'')
            {
                at = SkipString(css, at);
                continue;
            }

            if (c == '/' && at + 1 < css.Length && css[at + 1] == '*')
            {
                at = SkipComment(css, at);
                continue;
            }

            if (stops.IndexOf(c) >= 0)
            {
                return at;
            }

            at++;
        }

        return to;
    }

    private static Int32 SkipSpaceAndComments(String css, Int32 at)
    {
        while (at < css.Length)
        {
            if (Char.IsWhiteSpace(css[at]))
            {
                at++;
            }
            else if (css[at] == '/' && at + 1 < css.Length && css[at + 1] == '*')
            {
                at = SkipComment(css, at);
            }
            else
            {
                return at;
            }
        }

        return at;
    }

    private static Int32 SkipString(String css, Int32 at)
    {
        var quote = css[at];
        at++;

        while (at < css.Length)
        {
            if (css[at] == '\\')
            {
                at += 2;
                continue;
            }

            if (css[at] == quote)
            {
                return at + 1;
            }

            at++;
        }

        return at;
    }

    private static Int32 SkipComment(String css, Int32 at)
    {
        var end = css.IndexOf("*/", at + 2, StringComparison.Ordinal);

        return end < 0 ? css.Length : end + 2;
    }
}

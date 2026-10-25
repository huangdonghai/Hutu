using System;
using System.Text;

namespace Hutu.Rendering.Boxes;

/// <summary>
/// Turns the quotation marks a <c>content</c> value asks for into the strings the page's
/// <c>quotes</c> gives at the depth they stand at.
/// </summary>
/// <remarks>
/// The builder reads a content value while walking the element it belongs to, and a pseudo-element is
/// made after the element's children are - so the builder's own order is not the order the marks nest
/// in. The keywords are therefore carried as private sentinels and resolved once the box tree stands,
/// in the order the page shows: the ::before box, the element's content, the ::after box.
/// <para>
/// Style containment copies the depth at the boundary and throws away what the subtree did to it
/// (CSS Contain 2 §3.4): "the depth of quote nesting in the subtree is unchanged and starts at the
/// value that its context normally implies, but ... changes ... inside the subtree do not affect the
/// depth ... outside".
/// </para>
/// </remarks>
internal static class QuoteResolver
{
    /// <summary>The sentinel a <c>content: open-quote</c> leaves for this pass.</summary>
    internal const Char Open = '\uE000';

    /// <summary>The sentinel a <c>content: close-quote</c> leaves for this pass.</summary>
    internal const Char Close = '\uE001';

    /// <summary>The sentinel a <c>content: no-open-quote</c> leaves for this pass.</summary>
    internal const Char NoOpen = '\uE002';

    /// <summary>The sentinel a <c>content: no-close-quote</c> leaves for this pass.</summary>
    internal const Char NoClose = '\uE003';

    /// <summary>Resolves every mark the tree carries, from the depth the page opens at.</summary>
    internal static void Resolve(Box root) => Walk(root, QuoteSet.Default, 0);

    private static Int32 Walk(Box box, QuoteSet inherited, Int32 depth)
    {
        var quotes = box.Element is not null && !box.IsGenerated && !box.IsAnonymous
            ? box.Quotes ?? inherited
            : inherited;

        // The element at the base of a style-contained subtree is outside it: its own content reads the
        // depth around it, and only what stands inside starts from a copy that is put back afterwards.
        var scoped = box.Element is not null && !box.IsGenerated && !box.IsAnonymous && box.Style.Contain.Style;
        var outside = depth;

        if (box.Kind == BoxKind.Text && box.Text is { Length: > 0 } text && Holds(text))
        {
            box.Text = Rewrite(text, quotes, ref depth);
        }

        foreach (var child in box.Children)
        {
            depth = Walk(child, quotes, depth);
        }

        return scoped ? outside : depth;
    }

    private static Boolean Holds(String text) =>
        text.IndexOfAny(new[] { Open, Close, NoOpen, NoClose }) >= 0;

    private static String Rewrite(String text, QuoteSet quotes, ref Int32 depth)
    {
        var drawn = new StringBuilder(text.Length);
        var changed = false;

        foreach (var character in text)
        {
            switch (character)
            {
                case Open:
                    if (!quotes.IsNone)
                    {
                        drawn.Append(quotes.PairAt(depth).Open);
                    }

                    depth++;
                    changed = true;
                    break;

                case Close:
                    if (depth > 0)
                    {
                        depth--;

                        if (!quotes.IsNone)
                        {
                            drawn.Append(quotes.PairAt(depth).Close);
                        }
                    }

                    changed = true;
                    break;

                case NoOpen:
                    depth++;
                    changed = true;
                    break;

                case NoClose:
                    if (depth > 0)
                    {
                        depth--;
                    }

                    changed = true;
                    break;

                default:
                    drawn.Append(character);
                    break;
            }
        }

        return changed ? drawn.ToString() : text;
    }
}

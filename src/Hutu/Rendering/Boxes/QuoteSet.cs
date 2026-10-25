using System;
using System.Collections.Generic;
using System.Text;

namespace Hutu.Rendering.Boxes;

/// <summary>
/// The quotation pairs an element hands down through the <c>quotes</c> property (CSS Generated
/// Content 3 §3): each depth is one opening and one closing string.
/// </summary>
internal sealed class QuoteSet
{
    /// <summary>The pairs a page that states none of its own reads with - the HTML rendering rules' own.</summary>
    internal static readonly QuoteSet Default = new(new[] { ("\u201C", "\u201D"), ("\u2018", "\u2019") });

    /// <summary>The set <c>quotes: none</c> asks for: a keyword still nests, but spells nothing.</summary>
    internal static readonly QuoteSet None = new(Array.Empty<(String Open, String Close)>());

    private readonly (String Open, String Close)[] _pairs;

    private QuoteSet((String Open, String Close)[] pairs)
    {
        _pairs = pairs;
    }

    /// <summary>Gets whether the set is <c>none</c>, which draws no mark at any depth.</summary>
    internal Boolean IsNone => _pairs.Length == 0;

    /// <summary>
    /// Gets the pair a depth reads with. A depth past the pairs the page wrote reads the last pair
    /// (CSS 2.1 §12.3.2).
    /// </summary>
    internal (String Open, String Close) PairAt(Int32 depth) =>
        _pairs.Length == 0
            ? (String.Empty, String.Empty)
            : _pairs[Math.Min(Math.Max(depth, 0), _pairs.Length - 1)];

    /// <summary>
    /// Reads the element's own declaration over what it inherits. A declaration this reader cannot
    /// take is left out and the inherited set stands, which is what a dropped declaration means.
    /// </summary>
    internal static QuoteSet Read(String? declared, QuoteSet inherited)
    {
        if (String.IsNullOrWhiteSpace(declared))
        {
            return inherited;
        }

        var text = declared!.Trim();

        if (text.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            return None;
        }

        if (text.Equals("auto", StringComparison.OrdinalIgnoreCase) ||
            text.Equals("initial", StringComparison.OrdinalIgnoreCase))
        {
            return Default;
        }

        if (text.Equals("inherit", StringComparison.OrdinalIgnoreCase) ||
            text.Equals("unset", StringComparison.OrdinalIgnoreCase) ||
            text.Equals("revert", StringComparison.OrdinalIgnoreCase) ||
            text.Equals("revert-layer", StringComparison.OrdinalIgnoreCase))
        {
            return inherited;
        }

        var strings = new List<String>();
        var at = 0;

        while (at < text.Length)
        {
            if (Char.IsWhiteSpace(text[at]))
            {
                at++;
                continue;
            }

            if (text[at] is not ('"' or '\''))
            {
                return inherited;
            }

            var run = new StringBuilder();
            GeneratedContent.ReadRun(text, ref at, run);
            strings.Add(run.ToString());
        }

        var pairs = new List<(String Open, String Close)>();

        for (var index = 0; index + 1 < strings.Count; index += 2)
        {
            pairs.Add((strings[index], strings[index + 1]));
        }

        // An odd count is not the grammar's shape; the last string stands with an empty closing mark
        // rather than throwing away the pairs the page did state.
        if (strings.Count % 2 == 1)
        {
            pairs.Add((strings[^1], String.Empty));
        }

        return pairs.Count == 0 ? inherited : new QuoteSet(pairs.ToArray());
    }
}

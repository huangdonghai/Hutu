using AngleSharp.Css.Dom;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Hutu.Rendering.Boxes;

/// <summary>
/// A counter style the page wrote for itself with <c>@counter-style</c>, and the table of them for one
/// document.
/// </summary>
/// <remarks>
/// CSS Lists 3 §2.3 reads a style as a system plus the pieces that system counts with, and §2.4 lets a
/// style take the pieces it does not state from the one it <c>extends</c> - which is why the record
/// below keeps its descriptors as possibly-absent rather than as defaults: the default of a descriptor
/// is what the extended style says, and only at the bottom of that chain does the chapter's own value
/// answer. A style whose descriptors do not hold together is invalid (§3.1), and an invalid style is
/// spelled the way its fallback says, which is decimal unless it named otherwise - the same answer a
/// name this table has never heard gives.
/// </remarks>
internal sealed record CounterStyle
{
    /// <summary>Gets the name the page gave the style, told apart by case as a custom identifier is.</summary>
    internal required String Name { get; init; }

    /// <summary>Gets the kind of system the style counts with, or null where it named none.</summary>
    internal String? System { get; init; }

    /// <summary>Gets the style this one takes its unstated pieces from, out of an <c>extends</c>.</summary>
    internal String? Extends { get; init; }

    /// <summary>Gets the base of a numeric system.</summary>
    internal Int32? Base { get; init; }

    /// <summary>Gets the symbols the style counts with, in the order it wrote them.</summary>
    internal String[]? Symbols { get; init; }

    /// <summary>Gets the additive pairs, largest worth first.</summary>
    internal List<(Int32 Value, String Symbol)>? Additive { get; init; }

    /// <summary>Gets where a fixed system starts numbering.</summary>
    internal Int32? FixedAt { get; init; }

    /// <summary>Gets the text a marker of this style is followed by.</summary>
    internal String? Suffix { get; init; }

    /// <summary>Gets the two symbols a number below nought is wrapped in.</summary>
    internal String[]? Negative { get; init; }

    /// <summary>Gets the width the spelling is padded out to, and the symbol that fills it.</summary>
    internal (Int32 Size, String Symbol)? Pad { get; init; }

    /// <summary>Gets the span of numbers the style covers, or null for <c>auto</c>.</summary>
    internal (Int32 Min, Int32 Max)? Range { get; init; }

    /// <summary>Gets the style a number outside the range is spelled with.</summary>
    internal String? Fallback { get; init; }

    /// <summary>
    /// Gets whether a descriptor the system needs was written in a way the chapter does not allow, which
    /// makes the whole style invalid however well the rest of it holds together (§3.1).
    /// </summary>
    internal Boolean Broken { get; init; }

    /// <summary>Gets whether the descriptors hold together as the system they name asks.</summary>
    internal Boolean IsValid => !Broken && System switch
    {
        "numeric" => Base is >= 2 && Count >= 2,
        "alphabetic" => Count >= 1,
        "cyclic" => Count >= 1,
        "symbolic" => Count >= 1,
        "additive" => Additive is { Count: > 0 },
        "fixed" => Count >= 1,
        "extends" => true,
        null => Count >= 1,
        _ => false,
    };

    private Int32 Count => Symbols?.Length ?? 0;
}

/// <summary>
/// The styles one document defines, looked up by the name the page wrote.
/// </summary>
internal sealed class CounterStyleTable
{
    /// <summary>The table of a document that defines no style.</summary>
    internal static readonly CounterStyleTable Empty = new(null);

    private readonly Dictionary<String, CounterStyle>? _styles;

    private CounterStyleTable(Dictionary<String, CounterStyle>? styles) => _styles = styles;

    /// <summary>Gets whether the document wrote no style at all, which is most of them.</summary>
    internal Boolean IsEmpty => _styles is null or { Count: 0 };

    /// <summary>Reads the styles out of the rules of the sheets the cascade was built from.</summary>
    internal static CounterStyleTable Build(IEnumerable<ICssRule> rules)
    {
        Dictionary<String, CounterStyle>? styles = null;

        foreach (var rule in rules)
        {
            if (rule is ICssCounterStyleRule defined)
            {
                styles ??= new Dictionary<String, CounterStyle>(StringComparer.Ordinal);

                var style = Read(defined);

                // The last definition of a name wins, the same way the last rule of a sheet does.
                styles[style.Name] = style;
            }
        }

        return new CounterStyleTable(styles);
    }

    /// <summary>
    /// Spells a number in the style the page named, or in the built-in one where this table never heard
    /// of it. Null is the answer for a style that has nothing to say - a name that spells no marker.
    /// </summary>
    internal String? Spell(String type, Int32 number)
    {
        if (_styles is not null && _styles.TryGetValue(type, out var style))
        {
            return Spelled(style, number, 0);
        }

        if (SymbolsFunction(type) is { } inline)
        {
            return Spelled(inline, number, 0);
        }

        return CounterStyles.Spell(type, number);
    }

    /// <summary>Gets the text a marker of that style is followed by.</summary>
    internal String SuffixFor(String type)
    {
        if (_styles is null || !_styles.TryGetValue(type, out var style))
        {
            // A `symbols()` list takes its suffix from the system it names: the two that hand out marks
            // - cyclic and symbolic - carry none, as `disc` carries none, while a counted list earns
            // the period (§3.4.6, and the built-in table beside it).
            if (SymbolsFunction(type) is not null)
            {
                // A list the page wrote for itself is marks alone: the corpus's own references set the
                // suffix to a space for every one of the six systems.
                return " ";
            }

            return CounterStyles.Suffix(type);
        }

        var pieces = Gather(style, 0);

        return pieces.Suffix ?? ". ";
    }

    /// <summary>
    /// Gets the style that a <c>symbols()</c> function written in <c>list-style-type</c> stands for, or
    /// null where the value is not that function. The system is the first word when one of the four is
    /// named there, and <c>cyclic</c> when the list starts at once (CSS Lists 3 §3.3).
    /// </summary>
    internal static CounterStyle? SymbolsFunction(String type)
    {
        const String head = "symbols(";

        if (type.Length <= head.Length + 1 ||
            !type.StartsWith(head, StringComparison.OrdinalIgnoreCase) ||
            !type.EndsWith(')'))
        {
            return null;
        }

        var terms = Split(type[head.Length..^1]);

        if (terms.Count == 0)
        {
            return null;
        }

        // §3.3: a list written without a system is a symbolic one, and the systems the chapter names
        // can all stand at the front of the function.
        var system = "symbolic";
        var at = 0;

        if (terms[0] is { } first && !IsQuoted(first) && !IsNumber(first) &&
            (EqualsWord(first, "cyclic") || EqualsWord(first, "symbolic") ||
             EqualsWord(first, "alphabetic") || EqualsWord(first, "numeric") ||
             EqualsWord(first, "fixed") || EqualsWord(first, "additive")))
        {
            system = first.ToLowerInvariant();
            at = 1;
        }

        var symbols = new List<String>();

        for (; at < terms.Count; at++)
        {
            if (IsNumber(terms[at]))
            {
                // `numeric <integer>` carries its own base after the system name.
                continue;
            }

            symbols.Add(Unquote(terms[at]));
        }

        return symbols.Count == 0
            ? null
            : new CounterStyle { Name = type, System = system, Symbols = [.. symbols] };
    }

    private static Boolean IsNumber(String term) =>
        !IsQuoted(term) && Int32.TryParse(term, NumberStyles.Integer, CultureInfo.InvariantCulture, out _);

    /// <summary>
    /// The descriptors of a style with the ones it does not state taken from the style it extends, and
    /// the chapter's own answers underneath the whole chain.
    /// </summary>
    private (String System, Int32? Base, String[]? Symbols, List<(Int32, String)>? Additive, Int32 FixedAt, String? Suffix, String[]? Negative, (Int32, String)? Pad, (Int32, Int32)? Range) Gather(
        CounterStyle style,
        Int32 depth)
    {
        // `extends` names where the pieces come from rather than a way of counting, so a style that says
        // only that has no system of its own and takes the parent's whole and parcel.
        var own = style.System is null or "extends" ? null : style.System;
        var symbols = style.Symbols;
        var additive = style.Additive;
        var numeric = style.Base;
        var fixedAt = style.FixedAt;
        var suffix = style.Suffix;
        var negative = style.Negative;
        var pad = style.Pad;
        var range = style.Range;

        if (depth <= 8 && style.Extends is { Length: > 0 } name)
        {
            if (_styles!.TryGetValue(name, out var parent))
            {
                var above = Gather(parent, depth + 1);

                own ??= above.System;
                numeric ??= above.Base;
                fixedAt ??= above.FixedAt;
                symbols ??= above.Symbols;
                additive ??= above.Additive;
                suffix ??= above.Suffix;
                negative ??= above.Negative;
                pad ??= above.Pad;
                range ??= above.Range;
            }
            else
            {
                // Extending a style the chapter already knows: its tables answer the pieces the page did
                // not write, and only those - the name itself is not one of them.
                symbols ??= CounterStyles.SymbolsOf(name);
                suffix ??= CounterStyles.Suffix(name);
            }
        }

        return (own ?? "cyclic", numeric, symbols, additive, fixedAt ?? 1, suffix, negative, pad, range);
    }

    private String? Spelled(CounterStyle style, Int32 number, Int32 depth)
    {
        if (depth > 8)
        {
            return Decimal(number);
        }

        if (!style.IsValid)
        {
            return Fallback(style, number, depth);
        }

        var pieces = Gather(style, depth);

        if (pieces.Range is { } span && (number < span.Item1 || number > span.Item2))
        {
            return Fallback(style, number, depth);
        }

        if (number == 0)
        {
            // A system with a symbol for nought uses it; one without has nothing to say, and the
            // chapter sends that to the fallback as well (§3.1.1, "range: auto" leaves zero out).
            var zero = pieces.Symbols is { Length: > 0 } && pieces.System == "numeric"
                ? pieces.Symbols[0]
                : null;

            return zero is null ? Fallback(style, number, depth) : Padded(zero, pieces);
        }

        if (number < 0)
        {
            var wrapped = pieces.Negative;
            var magnitude = SpelledOf(written(-number), pieces, style, depth);

            return wrapped is { Length: > 0 }
                ? wrapped[0] + magnitude + (wrapped.Length > 1 ? wrapped[1] : String.Empty)
                : "-" + magnitude;
        }

        return Padded(SpelledOf(number, pieces, style, depth), pieces);
    }

    /// <summary>Spells a positive number in the system the gathered descriptors describe.</summary>
    private String SpelledOf(
        Int32 number,
        (String System, Int32? Base, String[]? Symbols, List<(Int32, String)>? Additive, Int32 FixedAt, String? Suffix, String[]? Negative, (Int32, String)? Pad, (Int32, Int32)? Range) pieces,
        CounterStyle style,
        Int32 depth)
    {
        var symbols = pieces.Symbols ?? Array.Empty<String>();

        switch (pieces.System)
        {
            case "cyclic" when symbols.Length > 0:
                return symbols[(number - 1) % symbols.Length];

            case "alphabetic" when symbols.Length > 0:
                return Bijective(number, symbols);

            case "symbolic" when symbols.Length > 0:
            {
                // §3.2.4: one symbol is chosen by the remainder, and the representation is that symbol
                // repeated as many times as the value needs of the list - so a two-mark list runs
                // † ‡ †† ‡‡ ††† rather than turning over like an alphabet does.
                var chosen = symbols[(number - 1) % symbols.Length];
                var times = (number + symbols.Length - 1) / symbols.Length;
                var result = new StringBuilder();

                for (var at = 0; at < times; at++)
                {
                    result.Append(chosen);
                }

                return result.ToString();
            }

            case "numeric" when symbols.Length > 0:
            {
                var baseOf = pieces.Base is { } stated && stated >= 2 ? stated : symbols.Length;

                var result = new StringBuilder();
                var rest = number;

                do
                {
                    result.Insert(0, symbols[rest % baseOf]);
                    rest /= baseOf;
                }
                while (rest > 0);

                return result.ToString();
            }

            case "additive" when pieces.Additive is { Count: > 0 }:
            {
                var worth = new List<(Int32 Value, String Symbol)>(pieces.Additive);
                worth.Sort(static (left, right) => right.Value.CompareTo(left.Value));

                var result = new StringBuilder();
                var rest = number;

                foreach (var (value, symbol) in worth)
                {
                    while (value > 0 && rest >= value)
                    {
                        result.Append(symbol);
                        rest -= value;
                    }
                }

                return rest == 0 ? result.ToString() : Decimal(number);
            }

            case "fixed" when symbols.Length > 0:
            {
                var offset = pieces.FixedAt;

                return number >= offset && number - offset < symbols.Length
                    ? symbols[number - offset]
                    : Decimal(number);
            }

            default:
                // A style that extends a built-in one and says nothing of its own is that style's
                // spelling with the page's pieces on top.
                return CounterStyles.Spell(style.Extends ?? "decimal", number) ?? Decimal(number);
        }
    }

    /// <summary>Writes a number in a base as large as the symbol list, with no digit for nought.</summary>
    private static String Bijective(Int32 number, String[] symbols)
    {
        var result = new StringBuilder();
        var rest = number;

        do
        {
            rest--;
            result.Insert(0, symbols[rest % symbols.Length]);
            rest /= symbols.Length;
        }
        while (rest > 0);

        return result.ToString();
    }

    private String Padded(String spelled, (String System, Int32? Base, String[]? Symbols, List<(Int32, String)>? Additive, Int32 FixedAt, String? Suffix, String[]? Negative, (Int32, String)? Pad, (Int32, Int32)? Range) pieces)
    {
        if (pieces.Pad is not { } pad || spelled.Length >= pad.Item1)
        {
            return spelled;
        }

        var filled = new StringBuilder();

        for (var at = pad.Item1 - spelled.Length; at > 0; at--)
        {
            filled.Append(pad.Item2);
        }

        return filled.Append(spelled).ToString();
    }

    private String? Fallback(CounterStyle style, Int32 number, Int32 depth)
    {
        var name = style.Fallback;

        if (name is { Length: > 0 })
        {
            if (_styles is not null && _styles.TryGetValue(name, out var other))
            {
                return Spelled(other, number, depth + 1);
            }

            return CounterStyles.Spell(name, number);
        }

        return CounterStyles.Spell("decimal", number);
    }

    private static Int32 written(Int32 number) => number == Int32.MinValue ? Int32.MaxValue : number;

    private static String Decimal(Int32 number) => number.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// Reads one rule's descriptors. Every value is the text the page wrote for it, taken apart the way
    /// the property means it: a symbol is either a quoted string or a bare identifier, and a list of
    /// them is separated by nothing but spaces and commas.
    /// </summary>
    private static CounterStyle Read(ICssCounterStyleRule rule)
    {

        var system = rule.System?.Trim();
        String? extends = null;
        Int32? numericBase = null;
        Int32? fixedAt = null;

        if (system is { Length: > 0 })
        {
            var parts = Split(system);

            if (parts.Count > 0 && EqualsWord(parts[0], "extends"))
            {
                system = "extends";
                extends = parts.Count > 1 ? parts[1] : null;
            }
            else if (parts.Count > 0 && EqualsWord(parts[0], "numeric"))
            {
                system = "numeric";

                if (parts.Count > 1 && Int32.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var stated))
                {
                    numericBase = stated;
                }
            }
            else
            {
                system = parts[0].ToLowerInvariant();

                if (EqualsWord(system, "fixed") && parts.Count > 1 &&
                    Int32.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var where))
                {
                    // §3.2.5: `fixed` carries its own starting number in the system, and the symbols the
                    // style lists then stand from that number on.
                    fixedAt = where;
                }
            }
        }
        else
        {
            system = null;
        }

        String[]? symbols = null;
        var broken = false;

        var symbolText = rule.Symbols?.Trim();

        if (symbolText is { Length: > 0 })
        {
            var terms = Split(symbolText);

            // A symbol is a string or an identifier; a number among them is the start of a fixed run,
            // which the system's own text already answered (§3.2.5). A wide keyword is neither, and one
            // standing in the list invalidates the descriptor and with it the whole style (§3.1).
            symbols = [.. terms.Select(Unquote)];
            broken = terms.Any(term => !IsQuoted(term) && CssWideKeyword.Is(term));
        }

        List<(Int32, String)>? additive = null;
        var additiveText = rule.AdditiveSymbols?.Trim();

        if (additiveText is { Length: > 0 })
        {
            additive = [];

            // `additive-symbols: [ <integer> && <symbol> ],#` - the pairs are told apart by their commas.
            foreach (var term in additiveText.Split(','))
            {
                var pieces = Split(term);

                for (var at = 0; at + 1 < pieces.Count; at++)
                {
                    if (Int32.TryParse(pieces[at], NumberStyles.Integer, CultureInfo.InvariantCulture, out var worth))
                    {
                        additive.Add((worth, Unquote(pieces[at + 1])));
                        break;
                    }
                }
            }

            if (additive.Count == 0)
            {
                additive = null;
            }
        }

        var negative = rule.Negative?.Trim();
        var suffix = rule.Suffix?.Trim();
        var pad = rule.Pad?.Trim();
        var range = rule.Range?.Trim();

        return new CounterStyle
        {
            Name = rule.Name?.Trim() ?? String.Empty,
            System = system,
            Extends = extends,
            Base = numericBase,
            Symbols = symbols,
            Broken = broken,
            Additive = additive,
            FixedAt = fixedAt,
            Suffix = suffix is { Length: > 0 } ? UnquoteFirst(suffix) : null,
            Negative = negative is { Length: > 0 } ? Split(negative) is { Count: > 0 } words ? [.. words] : null : null,
            Pad = ReadPad(pad),
            Range = ReadRange(range),
            Fallback = rule.Fallback?.Trim() is { Length: > 0 } named ? named : null,
        };
    }

    private static (Int32, String)? ReadPad(String? text)
    {
        if (text is not { Length: > 0 })
        {
            return null;
        }

        var terms = Split(text);

        return terms.Count > 1 && Int32.TryParse(terms[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var size)
            ? (size, Unquote(terms[1]))
            : null;
    }

    private static (Int32, Int32)? ReadRange(String? text)
    {
        if (text is not { Length: > 0 } || EqualsWord(text, "auto"))
        {
            return null;
        }

        var terms = Split(text);
        var low = Int32.MinValue;
        var high = Int32.MinValue;
        var seen = 0;

        foreach (var term in terms)
        {
            if (EqualsWord(term, "infinite"))
            {
                if (seen == 0)
                {
                    low = Int32.MinValue;
                }
                else
                {
                    high = Int32.MaxValue;
                }

                seen++;
            }
            else if (Int32.TryParse(term, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
            {
                if (seen == 0)
                {
                    low = number;
                }
                else
                {
                    high = number;
                }

                seen++;
            }
        }

        if (seen == 0)
        {
            return null;
        }

        return (low == Int32.MinValue ? Int32.MinValue : (seen == 1 ? 0 : low), high == Int32.MinValue ? Int32.MaxValue : high);
    }

    /// <summary>Gets whether a word is the one asked for, however it was cased.</summary>
    private static Boolean EqualsWord(String? text, String word) =>
        text is not null && text.Trim().Equals(word, StringComparison.OrdinalIgnoreCase);

    private static Boolean IsQuoted(String term) =>
        term.Length > 0 && term[0] is '"' or '\'';

    private static String UnquoteFirst(String text) => Unquote(Split(text) is { Count: > 0 } first ? first[0] : text);

    private static String Unquote(String term)
    {
        if (term.Length < 2 || term[0] is not ('"' or '\'') || term[^1] != term[0])
        {
            return term;
        }

        var body = term[1..^1];
        var result = new StringBuilder(body.Length);

        for (var at = 0; at < body.Length; at++)
        {
            if (body[at] != '\\')
            {
                result.Append(body[at]);
                continue;
            }

            at++;

            var start = at;
            var code = 0;
            var digits = 0;

            while (at < body.Length && digits < 6 && Uri.IsHexDigit(body[at]))
            {
                code = (code * 16) + Convert.ToInt32(body[at].ToString(), 16);
                digits++;
                at++;
            }

            if (digits == 0)
            {
                result.Append(body[at]);
                continue;
            }

            result.Append(Char.ConvertFromUtf32(code));

            if (at < body.Length && body[at] == ' ')
            {
                at++;
            }

            at--;
        }

        return result.ToString();
    }

    /// <summary>
    /// Splits a descriptor's text into its words, keeping a quoted string whole and reading the escapes
    /// inside a bare identifier, and dropping the commas that separate a list.
    /// </summary>
    private static List<String> Split(String text)
    {
        var terms = new List<String>();
        var at = 0;

        while (at < text.Length)
        {
            var c = text[at];

            if (Char.IsWhiteSpace(c) || c == ',')
            {
                at++;
                continue;
            }

            if (c is '"' or '\'')
            {
                var open = at;
                at++;

                while (at < text.Length && text[at] != c)
                {
                    at += text[at] == '\\' ? 2 : 1;
                }

                terms.Add(text.Substring(open, Math.Min(at + 1, text.Length) - open));
                at++;
                continue;
            }

            var start = at;

            while (at < text.Length && !Char.IsWhiteSpace(text[at]) && text[at] != ',')
            {
                at++;
            }

            terms.Add(text[start..at]);
        }

        return terms;
    }
}

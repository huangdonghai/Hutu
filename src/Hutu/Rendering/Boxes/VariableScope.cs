using AngleSharp.Css.Dom;
using AngleSharp.Css.Parser;
using System;
using System.Text;
using System.Collections.Generic;

namespace Hutu.Rendering.Boxes;

/// <summary>
/// The custom properties an element can see, and the <c>var()</c> references its declarations are read
/// through - so that the fused path, which reads cascaded values exactly as they were written, can read
/// a declaration whose value lives in a variable some ancestor declared.
/// </summary>
/// <remarks>
/// A scope is one link in a chain that runs from an element up through every ancestor that declared a
/// custom property. Nothing else makes a link: an element that declares none shares its parent's node
/// outright, which is what keeps the chain as short as the page's variable declarations rather than as
/// deep as its tree, and what keeps one node serving the whole subtree under it - so the answers it
/// remembers are shared the same way.
/// <para>
/// A name is looked for at the link that declares it and nowhere else below, and its value is then
/// substituted <em>there</em>, in the scope it was written in - which is what makes
/// <c>--a: var(--b)</c> on an element that also declares <c>--b</c> read that element's own <c>--b</c>,
/// while a child that redeclares <c>--b</c> and uses <c>--a</c> gets its own. A declaration that cannot
/// be resolved - a name nobody declared and no fallback given, a name whose own value does not resolve,
/// a chain that comes back around to itself - leaves the <em>property</em> undeclared for that element.
/// That is the specification's "invalid at computed-value time", and it is what the resolvers already do
/// with a miss: a non-inherited property takes its initial value and an inherited one takes what its
/// parent had, because a miss is exactly what they were built to answer.
/// </para>
/// </remarks>
internal sealed class VariableScope
{
    /// <summary>How deep a chain of references is followed before it is called circular.</summary>
    private const Int32 Deepest = 32;

    private readonly MatchedCascade? _owner;
    private readonly VariableScope? _parent;
    private readonly ICssParser _parser;
    private String[]? _names;

    /// <summary>
    /// How many links have been created and how many lookups were handed a link already open, since
    /// <see cref="ResetCounters"/>. Counted always - two increments against a dictionary probe - and read
    /// only by the probes, which use the pair to tell a chain a rebuild can stand on from one it builds again.
    /// </summary>
    private static Int32 _opened;
    private static Int32 _reused;

    /// <summary>The value of each name this scope declares, substituted; a null is one that does not resolve.</summary>
    private Dictionary<String, String?>? _values;

    /// <summary>
    /// What a reader of some cascade asked for, already substituted and parsed. Keyed by the cascade as
    /// well, because one scope serves every element under it and those elements match different rules.
    /// </summary>
    private Dictionary<Request, ICssProperty?>? _read;

    /// <summary>
    /// The links already opened under this one, by the cascade they were opened for - which is what
    /// keeps one node serving all the alike-matching elements under it.
    /// </summary>
    private Dictionary<MatchedCascade, VariableScope>? _children;

    private VariableScope(MatchedCascade owner, VariableScope? parent, ICssParser parser)
    {
        _owner = owner;
        _parent = parent;
        _parser = parser;
    }

    /// <summary>
    /// Gets whether a cascade needs a scope to be read through: it declares custom properties, or it
    /// names one in a value - which needs the scope even when nothing here declares the name, because
    /// the answer may be the fallback written after it.
    /// </summary>
    public static Boolean NeedsParser(MatchedCascade cascade) =>
        cascade.Scan() is { } scan && (scan.CustomProperties.Count > 0 || scan.UsesVariable);

    /// <summary>Gets how many scope links were created since the counters were reset.</summary>
    internal static Int32 Opened => _opened;

    /// <summary>Gets how many lookups were handed a link already open since the counters were reset.</summary>
    internal static Int32 Reused => _reused;

    /// <summary>Starts the counters over, for a caller that means to read them after one build.</summary>
    internal static void ResetCounters()
    {
        _opened = 0;
        _reused = 0;
    }

    /// <summary>
    /// Gets the scope an element reads through: a new link when its own cascade declares a custom
    /// property or asks for one, and the parent's link untouched when it does neither. Public because
    /// the walk that opens one is not the only code that assembles a style - the parity test builds
    /// styles of its own and has to thread the same chain through it.
    /// </summary>
    /// <param name="heads">
    /// The head links of the document, by the cascade they were opened for, for a caller that means
    /// them to outlive the build. A link whose parent is nothing has no parent object to be filed
    /// under, which is why it cannot be held the way every other link is; null for a caller that
    /// builds once and has nothing to carry over.
    /// </param>
    public static VariableScope? Open(
        MatchedCascade cascade,
        VariableScope? parent,
        ICssParser? parser,
        Dictionary<MatchedCascade, VariableScope>? heads = null)
    {
        if (parser is null || !NeedsParser(cascade))
        {
            return parent;
        }

        // One cascade under one parent resolves the same every time, so it is the same link. Without
        // this a page whose boxes all sit under a single declaring root would hand every element its
        // own node, and the areas that are shared per cascade - most of a build's work - would stop
        // being shared at all, since a node is part of what keys them.
        //
        // The head of a chain is the same reading with a parent of nothing at all, and it is the one
        // link that cannot be held here: there is no parent object to file it under. It goes in the
        // caller's table instead, and that table outliving the build is what keeps a chain from
        // starting over - a head rebuilt is a head whose own _children is empty, and every link under
        // it is rebuilt with it whatever the rest of the tree decided.
        if (parent is null)
        {
            if (heads is not null && heads.TryGetValue(cascade, out var head))
            {
                _reused++;
                return head;
            }
        }
        else if (parent._children is not null && parent._children.TryGetValue(cascade, out var known))
        {
            _reused++;
            return known;
        }

        var scope = new VariableScope(cascade, parent, parser);
        _opened++;

        if (parent is null)
        {
            if (heads is not null)
            {
                heads[cascade] = scope;
            }
        }
        else
        {
            (parent._children ??= new Dictionary<MatchedCascade, VariableScope>())[cascade] = scope;
        }

        return scope;
    }

    /// <summary>
    /// Reads a property of a cascade with its <c>var()</c> references resolved. A declaration that
    /// mentions none is handed back exactly as the cascade holds it, which is the same object the
    /// cascade's own lookup remembers - the common case on any page, and free.
    /// </summary>
    /// <remarks>
    /// A longhand of a shorthand written as a variable arrives with no text of its own to substitute -
    /// the cascade keeps an empty placeholder where the expansion will be - and it is left to the
    /// reader above to reach the value by computing the element's style. What is resolved here is a
    /// reference written into a value the cascade can state.
    /// </remarks>
    internal ICssProperty? ReadProperty(MatchedCascade cascade, String name)
    {
        var declared = cascade.Find(name);
        var text = declared?.Value;

        if (text is null || !Mentions(text))
        {
            return declared;
        }

        return Remember(cascade, name, Substitute(text, this, 0, new Chain()));
    }

    private ICssProperty? Remember(MatchedCascade cascade, String name, String? substituted)
    {
        var key = new Request(cascade, name);

        if (_read is not null && _read.TryGetValue(key, out var remembered))
        {
            return remembered;
        }

        var resolved = substituted is null || ReferenceEquals(substituted, Circular)
            ? null
            : Parse(name, substituted);

        (_read ??= new Dictionary<Request, ICssProperty?>())[key] = resolved;
        return resolved;
    }

    /// <summary>
    /// Reads a longhand the cascade holds only as the shadow of a shorthand written as a variable: the
    /// shorthand's own text, substituted in this scope, parsed, and taken apart for the name asked.
    /// </summary>
    /// <remarks>
    /// The parser cannot split a value it cannot read, so <c>background: var(--color, var(--color-1))</c>
    /// reaches the declaration as empty longhands with the whole of the shorthand's text kept behind them
    /// (CSS Variables 1 §3). The CSSOM expands that for an element, but the boxes it has no computed style
    /// for - a generated one whose arm carries a positional pseudo-class, a marker, a first line - come
    /// only to this engine's own reading. Substituting here is what gives those boxes the value the page
    /// wrote, in the scope the declaration stands in.
    /// </remarks>
    internal ICssProperty? ReadShorthandLonghand(
        MatchedCascade cascade,
        String name,
        String shorthandName,
        String shorthandText)
    {
        // The name of a shorthand is not a property this scope is otherwise asked for, so the two
        // readings cannot collide in the one table.
        var key = new Request(cascade, shorthandName + ":" + name);

        if (_read is not null && _read.TryGetValue(key, out var remembered))
        {
            return remembered;
        }

        var substituted = Substitute(shorthandText, this, 0, new Chain());
        ICssProperty? resolved = null;

        if (substituted is not null && !ReferenceEquals(substituted, Circular))
        {
            try
            {
                resolved = _parser.ParseDeclaration(shorthandName + ":" + substituted)?.GetProperty(name);
            }
            catch (Exception)
            {
                resolved = null;
            }
        }

        (_read ??= new Dictionary<Request, ICssProperty?>())[key] = resolved;
        return resolved;
    }

    /// <summary>
    /// Replaces every <c>var()</c> in a value with what the scope resolves its name to, or with that
    /// reference's own fallback. Null for a reference the scope cannot answer, which is what makes the
    /// whole declaration invalid rather than one of its parts.
    /// </summary>
    private static String? Substitute(String text, VariableScope at, Int32 depth, Chain chain)
    {
        if (!Mentions(text))
        {
            return text;
        }

        if (depth > Deepest)
        {
            return null;
        }

        var start = 0;
        String? result = null;

        while (start < text.Length)
        {
            var open = IndexOfReference(text, start);

            if (open < 0)
            {
                Add(ref result, text, start, text.Length - start);
                break;
            }

            var close = Matching(text, open + Function.Length);

            if (close < 0)
            {
                // An unbalanced reference is a parse error rather than a name that happens to be
                // missing, and a parse error is what the shared rule is: no value, no declaration.
                return null;
            }

            Add(ref result, text, start, open - start);

            var resolved = Resolve(at, text, open + Function.Length, close, depth, chain);

            if (resolved is null || ReferenceEquals(resolved, Circular))
            {
                return resolved;
            }

            Add(ref result, resolved, 0, resolved.Length);
            start = close + 1;
        }

        return result ?? String.Empty;
    }

    /// <summary>Resolves one reference: its name, and the fallback after the first comma when it has one.</summary>
    private static String? Resolve(
        VariableScope at,
        String text,
        Int32 from,
        Int32 to,
        Int32 depth,
        Chain chain)
    {
        var body = text.AsSpan(from, to - from);
        var comma = TopLevelComma(body);
        var name = Unescaped((comma < 0 ? body : body[..comma]).Trim().ToString());
        var fallback = comma < 0 ? null : body[(comma + 1)..].Trim().ToString();

        if (!name.StartsWith("--", StringComparison.Ordinal))
        {
            return null;
        }

        // The name wins over the fallback even when it does not resolve: a declaration of it that is
        // itself invalid shadows whatever an ancestor said, which is what the specification's
        // guaranteed-invalid value means for a custom property.
        var value = at.Lookup(name, depth + 1, chain);

        if (value is not null)
        {
            // A name that came back around on itself is not a name with no value: whoever is standing
            // in the ring has no fallback to reach, because the ring is why it has nothing.
            return ReferenceEquals(value, Circular) ? Circular : value;
        }

        // A fallback written as nothing at all is still a fallback, and substitutes as nothing (CSS Variables 1 §3).
        return fallback is null ? null : Substitute(fallback, at, depth + 1, chain);
    }

    /// <summary>
    /// Gets a name's value as the scope that declares it resolves it, or null when no scope here does.
    /// </summary>
    private String? Lookup(String name, Int32 depth, Chain chain)
    {
        var names = _names ??= DeclaredNames();

        if (Array.IndexOf(names, name) < 0)
        {
            return _parent?.Lookup(name, depth, chain);
        }

        if (_values is not null && _values.TryGetValue(name, out var remembered))
        {
            return remembered;
        }

        if (!chain.Enter(name))
        {
            return Circular;
        }

        // The declaration's own text is substituted in this scope, not the one that asked: a variable
        // written as another variable keeps its own neighbourhood.
        var declared = _owner!.Find(name)?.Value;
        var resolved = declared is null ? null : Substitute(declared, this, depth, chain);

        chain.Open.Remove(name);

        if (ReferenceEquals(resolved, Circular))
        {
            (_values ??= new Dictionary<String, String?>(StringComparer.Ordinal))[name] = null;

            if (String.Equals(chain.Closed, name, StringComparison.Ordinal))
            {
                // The ring closes at this name. Above it the name is simply one that holds nothing,
                // and a fallback written where the name is used may answer for it; inside the ring no
                // such fallback is reached, because the ring - not a missing name - is why there is
                // nothing to substitute (CSS Variables 1 §3.1).
                chain.Closed = null;
                return null;
            }

            return Circular;
        }

        resolved = Widened(name, resolved, depth);

        (_values ??= new Dictionary<String, String?>(StringComparer.Ordinal))[name] = resolved;
        return resolved;
    }

    /// <summary>
    /// Reads a value that has turned out to be one of the words every property takes for itself rather
    /// than a value of its own. The first leaves the name holding nothing; the rest take what the
    /// element's parent had, which a custom property always inherits - so the four are two meanings.
    /// </summary>
    private String? Widened(String name, String? value, Int32 depth)
    {
        var text = value?.Trim();

        if (text is null || text.Length == 0)
        {
            return value;
        }

        if (text.Equals("initial", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (text.Equals("inherit", StringComparison.OrdinalIgnoreCase) ||
            text.Equals("unset", StringComparison.OrdinalIgnoreCase) ||
            text.Equals("revert", StringComparison.OrdinalIgnoreCase) ||
            text.Equals("revert-layer", StringComparison.OrdinalIgnoreCase))
        {
            return _parent?.Lookup(name, depth, new Chain());
        }

        return value;
    }

    private String[] DeclaredNames()
    {
        var declared = _owner!.Scan().CustomProperties;
        var distinct = new List<String>(declared.Count);
        var seen = new HashSet<String>(StringComparer.Ordinal);

        foreach (var name in declared)
        {
            // A name declared twice in one cascade is one entry: the lookup takes the last
            // declaration of it anyway, and walking a list that repeats itself would resolve the same
            // text twice for the answer once.
            if (name.Length > 2 && seen.Add(name))
            {
                distinct.Add(name);
            }
        }

        return distinct.ToArray();
    }

    /// <summary>Parses the substituted text back into a declaration, the way the shorthand machinery does.</summary>
    private ICssProperty? Parse(String name, String text)
    {
        try
        {
            var declaration = _parser.ParseDeclaration(name + ":" + text);

            // A value the substituted text no longer fits the property - an empty variable, a length
            // where a colour was asked for - is not a declaration at all, which is the same answer as
            // a reference that resolved to nothing.
            return declaration?.GetProperty(name);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>The <c>var(</c> spelling, which is matched case-insensitively as every CSS keyword is.</summary>
    private const String Function = "var(";

    /// <summary>
    /// Stands for a value that has no value because the chain of references came back around to itself
    /// while resolving it. Held as one object and told apart by identity, because no substituted text
    /// can be this one.
    /// </summary>
    private static readonly String Circular = new('*', 1);

    private static Boolean Mentions(String text) => IndexOfReference(text, 0) >= 0;

    private static Int32 IndexOfReference(String text, Int32 from)
    {
        for (var at = from; at + Function.Length <= text.Length; at++)
        {
            if (text[at] is 'v' or 'V' &&
                text[at + 1] is 'a' or 'A' &&
                text[at + 2] is 'r' or 'R' &&
                text[at + 3] == '(')
            {
                return at;
            }
        }

        return -1;
    }

    /// <summary>
    /// Gets the name a reference asks for as the sheet's parser would have filed it: the escapes it is
    /// spelled with read to the characters they stand for, because a declaration is stored under those
    /// characters. An escape that names no character - zero, a lone surrogate, a number past the last
    /// code point - names the replacement character, which is what makes two spellings one name
    /// (CSS Syntax 3 §4.3.7).
    /// </summary>
    private static String Unescaped(String name)
    {
        if (!name.Contains('\\', StringComparison.Ordinal))
        {
            return name;
        }

        var text = new StringBuilder(name.Length);

        for (var at = 0; at < name.Length; at++)
        {
            if (name[at] != '\\')
            {
                text.Append(name[at]);
                continue;
            }

            at = ReadEscape(name, at + 1, text);
        }

        return text.ToString();
    }

    /// <summary>Reads the escape whose backslash was just consumed, and says where the name goes on.</summary>
    private static Int32 ReadEscape(String name, Int32 at, StringBuilder text)
    {
        var start = at;
        var value = 0;

        while (at < name.Length && at - start < 6 && IsHex(name[at]))
        {
            value = (value * 16) + (name[at] is >= '0' and <= '9'
                ? name[at] - '0'
                : Char.ToLowerInvariant(name[at]) - 'a' + 10);
            at++;
        }

        if (at == start)
        {
            // Nothing to read as a number: the character after the backslash stands for itself, and a
            // backslash with nothing after it is the replacement character.
            text.Append(at < name.Length ? name[at++] : '\uFFFD');
            return at - 1;
        }

        if (at < name.Length && Char.IsWhiteSpace(name[at]))
        {
            // One space ends the number and is part of the escape rather than a character of the name.
            at++;
        }

        text.Append(value is <= 0 or > 0x10FFFF or (>= 0xD800 and <= 0xDFFF)
            ? "\uFFFD"
            : Char.ConvertFromUtf32(value));

        return at - 1;
    }

    private static Boolean IsHex(Char c) =>
        c is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F';

    /// <summary>Gets the paren that closes a reference, past any nested function or quoted string.</summary>
    private static Int32 Matching(String text, Int32 from)
    {
        var depth = 1;
        Char quote = '\0';

        for (var at = from; at < text.Length; at++)
        {
            var c = text[at];

            if (quote != '\0')
            {
                if (c == quote)
                {
                    quote = '\0';
                }
                else if (c == '\\')
                {
                    at++;
                }
            }
            else if (c is '"' or '\'')
            {
                quote = c;
            }
            else if (c == '(')
            {
                depth++;
            }
            else if (c == ')' && --depth == 0)
            {
                return at;
            }
        }

        return -1;
    }

    /// <summary>Gets the first comma that is inside the reference itself rather than inside a function in it.</summary>
    private static Int32 TopLevelComma(ReadOnlySpan<Char> body)
    {
        var depth = 0;
        Char quote = '\0';

        for (var at = 0; at < body.Length; at++)
        {
            var c = body[at];

            if (quote != '\0')
            {
                if (c == quote)
                {
                    quote = '\0';
                }
            }
            else if (c is '"' or '\'')
            {
                quote = c;
            }
            else if (c == '(')
            {
                depth++;
            }
            else if (c == ')')
            {
                depth--;
            }
            else if (c == ',' && depth == 0)
            {
                return at;
            }
        }

        return -1;
    }

    private static void Add(ref String? target, String text, Int32 from, Int32 length)
    {
        if (length <= 0)
        {
            return;
        }

        if (target is { Length: > 0 } && Joins(target[target.Length - 1]) && Joins(text[from]))
        {
            // What substitution puts back is tokens, not characters: two of them written with nothing
            // between are still two, and a reader handed the joined text would otherwise make one word
            // of what the page wrote as two (CSS Variables 1 §4.1).
            target += " ";
        }

        target = target is null ? text.Substring(from, length) : target + text.Substring(from, length);
    }

    /// <summary>Gets whether a character can be part of a token that another such character welds onto.</summary>
    private static Boolean Joins(Char c) =>
        Char.IsLetterOrDigit(c) || c > '\u00A0' || c is '-' or '_' or '.' or '%' or '#' or '\\';

    /// <summary>
    /// The names one run of nested references is holding open while it resolves them, and the one of
    /// them it was asked for again before it had finished.
    /// </summary>
    private sealed class Chain
    {
        internal readonly HashSet<String> Open = new(StringComparer.Ordinal);

        /// <summary>Gets or sets the name whose own re-entry closed the ring, null while none has.</summary>
        internal String? Closed { get; set; }

        /// <summary>Opens a name, and says whether it was not already standing open.</summary>
        internal Boolean Enter(String name)
        {
            if (Open.Add(name))
            {
                return true;
            }

            Closed ??= name;
            return false;
        }
    }

    /// <summary>One question asked of one cascade through this scope.</summary>
    private readonly struct Request : IEquatable<Request>
    {
        private readonly MatchedCascade _cascade;
        private readonly String _name;

        internal Request(MatchedCascade cascade, String name)
        {
            _cascade = cascade;
            _name = name;
        }

        public Boolean Equals(Request other) =>
            ReferenceEquals(_cascade, other._cascade) && String.Equals(_name, other._name, StringComparison.Ordinal);

        public override Boolean Equals(Object? obj) => obj is Request other && Equals(other);

        public override Int32 GetHashCode() =>
            HashCode.Combine(System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(_cascade), _name);
    }
}

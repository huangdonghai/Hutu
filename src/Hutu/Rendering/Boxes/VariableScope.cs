using AngleSharp.Css.Dom;
using AngleSharp.Css.Parser;
using System;
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
public sealed class VariableScope
{
    /// <summary>How deep a chain of references is followed before it is called circular.</summary>
    private const Int32 Deepest = 32;

    private readonly MatchedCascade? _owner;
    private readonly VariableScope? _parent;
    private readonly ICssParser _parser;
    private String[]? _names;

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

    /// <summary>
    /// Gets the scope an element reads through: a new link when its own cascade declares a custom
    /// property or asks for one, and the parent's link untouched when it does neither. Public because
    /// the walk that opens one is not the only code that assembles a style - the parity test builds
    /// styles of its own and has to thread the same chain through it.
    /// </summary>
    public static VariableScope? Open(MatchedCascade cascade, VariableScope? parent, ICssParser? parser)
    {
        if (parser is null || !NeedsParser(cascade))
        {
            return parent;
        }

        // One cascade under one parent resolves the same every time, so it is the same link. Without
        // this a page whose boxes all sit under a single declaring root would hand every element its
        // own node, and the areas that are shared per cascade - most of a build's work - would stop
        // being shared at all, since a node is part of what keys them.
        if (parent is not null)
        {
            if (parent._children is not null && parent._children.TryGetValue(cascade, out var known))
            {
                return known;
            }
        }

        var scope = new VariableScope(cascade, parent, parser);

        if (parent is not null)
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

        return Remember(cascade, name, Substitute(text, this, 0));
    }

    private ICssProperty? Remember(MatchedCascade cascade, String name, String? substituted)
    {
        var key = new Request(cascade, name);

        if (_read is not null && _read.TryGetValue(key, out var remembered))
        {
            return remembered;
        }

        var resolved = substituted is null ? null : Parse(name, substituted);

        (_read ??= new Dictionary<Request, ICssProperty?>())[key] = resolved;
        return resolved;
    }

    /// <summary>
    /// Replaces every <c>var()</c> in a value with what the scope resolves its name to, or with that
    /// reference's own fallback. Null for a reference the scope cannot answer, which is what makes the
    /// whole declaration invalid rather than one of its parts.
    /// </summary>
    private static String? Substitute(String text, VariableScope at, Int32 depth)
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
                Append(ref result, text, start, text.Length - start);
                break;
            }

            var close = Matching(text, open + Function.Length);

            if (close < 0)
            {
                // An unbalanced reference is a parse error rather than a name that happens to be
                // missing, and a parse error is what the shared rule is: no value, no declaration.
                return null;
            }

            Append(ref result, text, start, open - start);

            var resolved = Resolve(at, text, open + Function.Length, close, depth);

            if (resolved is null)
            {
                return null;
            }

            Append(ref result, resolved, 0, resolved.Length);
            start = close + 1;
        }

        return result ?? String.Empty;
    }

    /// <summary>Resolves one reference: its name, and the fallback after the first comma when it has one.</summary>
    private static String? Resolve(VariableScope at, String text, Int32 from, Int32 to, Int32 depth)
    {
        var body = text.AsSpan(from, to - from);
        var comma = TopLevelComma(body);
        var name = (comma < 0 ? body : body[..comma]).Trim();
        var fallback = comma < 0 ? null : body[(comma + 1)..].Trim();

        if (!name.StartsWith("--", StringComparison.Ordinal))
        {
            return null;
        }

        // The name wins over the fallback even when it does not resolve: a declaration of it that is
        // itself invalid shadows whatever an ancestor said, which is what the specification's
        // guaranteed-invalid value means for a custom property.
        var value = at.Lookup(name.ToString(), depth + 1);

        if (value is not null)
        {
            return value;
        }

        return fallback.IsEmpty ? null : Substitute(fallback.ToString(), at, depth + 1);
    }

    /// <summary>
    /// Gets a name's value as the scope that declares it resolves it, or null when no scope here does.
    /// </summary>
    private String? Lookup(String name, Int32 depth)
    {
        var names = _names ??= DeclaredNames();

        if (Array.IndexOf(names, name) < 0)
        {
            return _parent?.Lookup(name, depth);
        }

        if (_values is not null && _values.TryGetValue(name, out var remembered))
        {
            return remembered;
        }

        // The declaration's own text is substituted in this scope, not the one that asked: a variable
        // written as another variable keeps its own neighbourhood.
        var declared = _owner!.Find(name)?.Value;
        var resolved = declared is null ? null : Substitute(declared, this, depth);

        (_values ??= new Dictionary<String, String?>(StringComparer.Ordinal))[name] = resolved;
        return resolved;
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

    private static void Append(ref String? target, String text, Int32 from, Int32 length)
    {
        if (length <= 0)
        {
            return;
        }

        target = target is null ? text.Substring(from, length) : target + text.Substring(from, length);
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

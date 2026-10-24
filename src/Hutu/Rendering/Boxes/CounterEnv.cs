using AngleSharp.Dom;
using System;
using System.Collections.Generic;

namespace Hutu.Rendering.Boxes;

/// <summary>
/// The named counters an element stands among: the ones in its scope, each with the value it came to.
/// </summary>
/// <remarks>
/// CSS Lists 3 §4.4 gives every counter a name, the element that created it and a value, and holds them
/// as a set whose last mention of a name is the innermost one. The chain below is that set, hung from
/// the innermost counter inwards: a node is one counter, and the walk the builder runs hands it to each
/// child and to the sibling after it and to nobody else - which is what keeps a counter created deep
/// inside one subtree out of the next, and what leaves an element that was never shown, and so created
/// nothing, out of the chain entirely (§4.5).
/// <para>
/// Names are told apart the way a custom identifier is (CSS Values 3 §4.3.10): case and all. The two
/// counters <c>foo</c> and <c>Foo</c> are two counters, which is what a page that resets both means.
/// </para>
/// </remarks>
internal sealed class CounterEnv
{
    /// <summary>The environment of a page that counts nothing.</summary>
    internal static readonly CounterEnv Empty = new(null, null, null, 0);

    private readonly CounterEnv? _inner;
    private readonly Object? _origin;
    private readonly String? _name;
    private readonly Int32 _value;

    private CounterEnv(CounterEnv? inner, Object? origin, String? name, Int32 value)
    {
        _inner = inner;
        _origin = origin;
        _name = name;
        _value = value;
    }

    /// <summary>
    /// Gets the chain with this element's counters hung on it, read off the three declarations it wrote.
    /// The chain comes back as it went in where the element names no counter, which is every element of
    /// every page but the ones that count.
    /// </summary>
    /// <param name="origin">
    /// What the counters written here belong to - the element for an element, and the element's own
    /// generated box for a pseudo-element, since §4.4.1 counts a pseudo-element's parent as the element
    /// it is written on.
    /// </param>
    /// <param name="above">
    /// The first thing above <paramref name="origin"/> whose counters stand outside it: the parent of an
    /// element, and the element itself for one of its pseudo-elements.
    /// </param>
    /// <remarks>
    /// The three are applied in the order the chapter states them - reset, then set, then increment -
    /// because an element that both resets a counter and steps it means the step to come on top of the
    /// reset rather than to replace it (§4.4.2, "the counter is incremented after the element's own
    /// counter-reset and counter-set").
    /// </remarks>
    internal static CounterEnv? Extend(CounterEnv? env, MatchedCascade cascade, Object origin, IElement? above)
    {
        var reset = cascade.Find("counter-reset")?.Value;
        var set = cascade.Find("counter-set")?.Value;
        var increment = cascade.Find("counter-increment")?.Value;

        if (String.IsNullOrWhiteSpace(reset) &&
            String.IsNullOrWhiteSpace(set) &&
            String.IsNullOrWhiteSpace(increment))
        {
            return env;
        }

        var mine = env ?? Empty;

        mine = Apply(mine, reset, CounterWrite.Reset, origin, above);
        mine = Apply(mine, set, CounterWrite.Set, origin, above);
        mine = Apply(mine, increment, CounterWrite.Increment, origin, above);

        return mine;
    }

    /// <summary>Gets the value of the innermost counter of that name, or nought where there is none.</summary>
    /// <remarks>
    /// A name nothing has set is still a counter, and the chapter gives it the value it was created with:
    /// <c>counter(x)</c> on a page that never named <c>x</c> says 0 (§4.4.2, "instantiated ... when named
    /// in the counter() notation").
    /// </remarks>
    internal Int32 ValueOf(String name)
    {
        for (var at = this; at._name is not null; at = at._inner!)
        {
            if (String.Equals(at._name, name, StringComparison.Ordinal))
            {
                return at._value;
            }
        }

        return 0;
    }

    /// <summary>
    /// Gets every value of that name in scope, outermost first, which is what <c>counters()</c> joins
    /// with its separator (§4.3, "from the outermost counter to the innermost").
    /// </summary>
    internal List<Int32> ValuesOf(String name)
    {
        var values = new List<Int32>();

        for (var at = this; at._name is not null; at = at._inner!)
        {
            if (String.Equals(at._name, name, StringComparison.Ordinal))
            {
                values.Add(at._value);
            }
        }

        // The walk ran from the innermost counter out, and the number is read the other way.
        values.Reverse();

        if (values.Count == 0)
        {
            values.Add(0);
        }

        return values;
    }

    /// <summary>What one term of a counter declaration does to the counter it names.</summary>
    private enum CounterWrite
    {
        Reset,
        Set,
        Increment,
    }

    /// <summary>
    /// Hangs the counters of one declaration on the chain, reading its terms left to right: a name and
    /// the number that follows it, or a name standing for the number its own property means.
    /// </summary>
    private static CounterEnv Apply(CounterEnv env, String? text, CounterWrite write, Object origin, IElement? above)
    {
        if (String.IsNullOrWhiteSpace(text))
        {
            return env;
        }

        var terms = text!.Split((Char[]?)null, StringSplitOptions.RemoveEmptyEntries);

        // A wide keyword is the cascade's answer to a question this chapter does not ask, and the first
        // term of such a value is not the name of a counter.
        if (terms.Length == 1 && CssWideKeyword.Is(terms[0]))
        {
            return env;
        }

        var at = 0;

        while (at < terms.Length)
        {
            var name = terms[at];

            if (name.Equals("none", StringComparison.OrdinalIgnoreCase) || name.StartsWith("-", StringComparison.Ordinal))
            {
                // A leading dash is an escape or a negative number standing where a name should be: either
                // way the declaration is not the list of names and numbers this reads.
                return env;
            }

            at++;

            var stated = Int32.MinValue;

            if (at < terms.Length && Int32.TryParse(terms[at], out var number))
            {
                stated = number;
                at++;
            }

            var value = write switch
            {
                CounterWrite.Reset => stated == Int32.MinValue ? 0 : stated,
                CounterWrite.Set => stated == Int32.MinValue ? env.ValueOf(name) : stated,
                _ => env.ValueOf(name) + (stated == Int32.MinValue ? 1 : stated),
            };

            env = Instantiate(env, origin, above, name, value);
        }

        return env;
    }

    /// <summary>
    /// Puts a counter of that name, belonging to <paramref name="origin"/>, at the inner end of the
    /// chain - and takes out of it the counter it stands in place of.
    /// </summary>
    /// <remarks>
    /// §4.4.2: "Newly instantiated counters replace identically-named counters originating from previous
    /// siblings, but are added in addition to identically-named counters originating from ancestor
    /// elements." So the question the old counter has to answer is whether it came from something above
    /// this one, and the walk up <paramref name="above"/> is the whole of that test. It is the difference
    /// between a list of sections, where each item's number stands beside the others for
    /// <c>counters()</c> to read, and a run of siblings each starting the count again, where only the
    /// last of them is left.
    /// </remarks>
    private static CounterEnv Instantiate(
        CounterEnv env,
        Object origin,
        IElement? above,
        String name,
        Int32 value)
    {
        for (var at = env; at._name is not null; at = at._inner!)
        {
            if (!String.Equals(at._name, name, StringComparison.Ordinal))
            {
                continue;
            }

            return StandsAbove(at._origin, above)
                ? new CounterEnv(env, origin, name, value)
                : new CounterEnv(env.Without(at), origin, name, value);
        }

        return new CounterEnv(env, origin, name, value);
    }

    /// <summary>Gets whether a counter's creator is one of the things standing above this one.</summary>
    private static Boolean StandsAbove(Object? creator, IElement? above)
    {
        for (var node = above; node is not null; node = node.ParentElement)
        {
            if (ReferenceEquals(creator, node))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Returns the chain with one counter taken out of it, leaving the order of the rest.</summary>
    private CounterEnv Without(CounterEnv gone)
    {
        var kept = new List<CounterEnv>();

        for (var at = this; at._name is not null; at = at._inner!)
        {
            if (!ReferenceEquals(at, gone))
            {
                kept.Add(at);
            }
        }

        // The walk ran inwards, so the survivors are hung back on one another from the outer end in.
        CounterEnv chain = Empty;

        for (var at = kept.Count - 1; at >= 0; at--)
        {
            chain = new CounterEnv(chain, kept[at]._origin, kept[at]._name, kept[at]._value);
        }

        return chain;
    }
}

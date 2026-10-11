using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace Hutu.Rendering.Boxes;

/// <summary>
/// What one fused build remembers of the areas it has already resolved, keyed by what went into them
/// rather than by what came out.
/// </summary>
/// <remarks>
/// The intern table answers "has anything equal to this area been built?" - a question that can only be
/// asked once the area exists, so it saves the memory an area takes and not the work of resolving it.
/// This answers the earlier question, "has this same input already been resolved?", from the input alone:
/// the cascade an element matched, the one font size its lengths would be read at, and where an area
/// inherits from a parent, the parent's own interned instance. When it says yes, the declarations behind
/// that area are not looked at at all, which is the part <see cref="StyleIntern"/> cannot reach.
/// <para>
/// Every area whose value is decided by those inputs is here: the border, the display, the outline and
/// the decoration, which read the cascade alone; the font and the text, which also read what the parent
/// passed down - and the parent's interned area is a fair stand-in for that, because the two hold the
/// same values by construction; and the box model, which additionally follows the element's own tag name
/// and its parent's through the margins the sample stylesheet states, so both tags go into its key too.
/// What stays out is the keywords - the colours, the background layers, the cursor - which follow the
/// element's live state: a cache that could not say which anchor has been visited would hand a link the
/// colour of the one it happens to match, and that answer only changes when the reader clicks.
/// <para>
/// It lives for one build, created and dropped beside the intern table, and holds the interned instance
/// rather than the candidate, so what it hands back is the one object every table agrees on.
/// </para>
/// </para>
/// </remarks>
public sealed class FusedAreaCache
{
    private readonly Dictionary<AreaKey, BorderArea> _border = new();
    private readonly Dictionary<AreaKey, DisplayPositioningArea> _display = new();
    private readonly Dictionary<AreaKey, VisualEffectsArea> _visual = new();
    private readonly Dictionary<AreaKey, TextDecorationArea> _decoration = new();
    private readonly Dictionary<AreaKey, FontArea> _font = new();
    private readonly Dictionary<AreaKey, TextArea> _text = new();
    private readonly Dictionary<AreaKey, BoxModelArea> _boxModel = new();

    /// <summary>
    /// The tag names met so far, each answered by one string.
    /// </summary>
    /// <remarks>
    /// A key holds a tag by reference, and a reference is only worth comparing if equal tags share one -
    /// which the parser does not promise. Interning here also settles the case once per document rather
    /// than lowering the same name per element: the defaults are spelled in lower case, so the folded
    /// form is what is kept, and the lookup above ignores case so an <c>&lt;P&gt;</c> joins the
    /// <c>&lt;p&gt;</c> it is the same tag as.
    /// </remarks>
    private readonly Dictionary<String, String> _tags = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Gets the font already resolved for this cascade, size and inherited font.</summary>
    internal Boolean TryFont(AreaKey key, out FontArea area) => _font.TryGetValue(key, out area!);

    /// <summary>Remembers the font resolved for this key.</summary>
    internal void Store(AreaKey key, FontArea area) => _font[key] = area;

    /// <summary>Gets the text behaviour already resolved for this cascade, size and inherited text.</summary>
    internal Boolean TryText(AreaKey key, out TextArea area) => _text.TryGetValue(key, out area!);

    /// <summary>Remembers the text behaviour resolved for this key.</summary>
    internal void Store(AreaKey key, TextArea area) => _text[key] = area;

    /// <summary>Gets the box model already resolved for this element's kind of place in the tree.</summary>
    internal Boolean TryBoxModel(AreaKey key, out BoxModelArea area) => _boxModel.TryGetValue(key, out area!);

    /// <summary>Remembers the box model resolved for this key.</summary>
    internal void Store(AreaKey key, BoxModelArea area) => _boxModel[key] = area;

    /// <summary>Gets the one string this build stands a tag name by, lowering it the first time.</summary>
    internal String Tag(String name)
    {
        if (_tags.TryGetValue(name, out var known))
        {
            return known;
        }

        var folded = name.ToLowerInvariant();
        _tags[name] = folded;

        return folded;
    }

    /// <summary>Gets the border already resolved for this cascade and size, if there is one.</summary>
    /// <remarks>
    /// The class is public because the factory's signature is, but the areas it hands out are internal to
    /// the engine, so the members are too - nothing outside this assembly is meant to ask.
    /// </remarks>
    internal Boolean TryBorder(AreaKey key, out BorderArea area) => _border.TryGetValue(key, out area!);

    /// <summary>Remembers the border resolved for this cascade and size.</summary>
    internal void Store(AreaKey key, BorderArea area) => _border[key] = area;

    /// <summary>Gets the display already resolved for this cascade, if there is one.</summary>
    internal Boolean TryDisplay(AreaKey key, out DisplayPositioningArea area) => _display.TryGetValue(key, out area!);

    /// <summary>Remembers the display resolved for this cascade.</summary>
    internal void Store(AreaKey key, DisplayPositioningArea area) => _display[key] = area;

    /// <summary>Gets the outline already resolved for this cascade and size, if there is one.</summary>
    internal Boolean TryVisual(AreaKey key, out VisualEffectsArea area) => _visual.TryGetValue(key, out area!);

    /// <summary>Remembers the outline resolved for this cascade and size.</summary>
    internal void Store(AreaKey key, VisualEffectsArea area) => _visual[key] = area;

    /// <summary>Gets the decoration already resolved for this cascade, if there is one.</summary>
    internal Boolean TryDecoration(AreaKey key, out TextDecorationArea area) => _decoration.TryGetValue(key, out area!);

    /// <summary>Remembers the decoration resolved for this cascade.</summary>
    internal void Store(AreaKey key, TextDecorationArea area) => _decoration[key] = area;
}

/// <summary>
/// One resolution's input: the cascade an element matched, the one length an area was read at, the two
/// identities the resolution reads beyond that, and the inherited flow packed into a number. The scalars
/// are zero and the references null for the areas that read none of them, which keeps those to one entry
/// per cascade rather than one per chance variation.
/// </summary>
/// <remarks>
/// The cascade is taken by identity. It is itself shared between the elements that matched alike - the
/// rule index hands one instance to every element whose match came out the same - so an identity here is
/// already a statement about the declarations behind it, and comparing two of them by reference is both
/// the cheap and the exact answer. The object slots are taken the same way by intent: each holds
/// something already one object per distinct value - a parent's interned area, a tag name folded through
/// <see cref="FusedAreaCache.Tag"/>, the property a line-height was authored in - so a reference says
/// what the values are without walking them. And the stamp packs four inherited enumerals into fields
/// wide enough to hold each one's every value, which makes it a name for the combination rather than a
/// digest of it: no two flows share a stamp, so no two flows can be confused for one.
/// </remarks>
public readonly struct AreaKey : IEquatable<AreaKey>
{
    public AreaKey(MatchedCascade cascade, Double scalar, Object? inherited = null, Object? identity = null, Int32 stamp = 0)
    {
        Cascade = cascade;
        Scalar = scalar;
        Inherited = inherited;
        Identity = identity;
        Stamp = stamp;
    }

    public MatchedCascade Cascade { get; }

    public Double Scalar { get; }

    /// <summary>Gets what the resolution inherits: a parent's area, or a parent's tag name.</summary>
    public Object? Inherited { get; }

    /// <summary>
    /// Gets the element's own contribution beyond the declarations: the property a relative
    /// <c>line-height</c> was authored in, or the element's own tag name - the two things a cascade and
    /// a size cannot tell apart.
    /// </summary>
    public Object? Identity { get; }

    /// <summary>Gets the flow the parent passed down, packed. Zero where no flow is read.</summary>
    public Int32 Stamp { get; }

    public Boolean Equals(AreaKey other) =>
        ReferenceEquals(Cascade, other.Cascade) &&
        ReferenceEquals(Inherited, other.Inherited) &&
        ReferenceEquals(Identity, other.Identity) &&
        Stamp == other.Stamp &&
        BitConverter.DoubleToInt64Bits(Scalar) == BitConverter.DoubleToInt64Bits(other.Scalar);

    public override Boolean Equals(Object? obj) => obj is AreaKey other && Equals(other);

    /// <remarks>
    /// The scalar is compared and hashed by its bits, the way <see cref="StyleLength"/> answers: a key
    /// that called two different sizes one would hand a box another box's border, and the number's own
    /// <c>==</c> is willing to say <c>0</c> and <c>-0</c> are the same size. Hashing the same bits the
    /// equality compares keeps the two from ever disagreeing. The references are hashed by identity, so
    /// no interned area is walked twice - once to hash and once again to agree.
    /// </remarks>
    public override Int32 GetHashCode() =>
        HashCode.Combine(
            RuntimeHelpers.GetHashCode(Cascade),
            BitConverter.DoubleToInt64Bits(Scalar),
            RuntimeHelpers.GetHashCode(Inherited),
            RuntimeHelpers.GetHashCode(Identity),
            Stamp);
}

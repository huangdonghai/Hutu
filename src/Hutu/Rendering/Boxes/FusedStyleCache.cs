using AngleSharp.Css;
using AngleSharp.Css.Dom;
using AngleSharp.Dom;
using Hutu.Rendering.Media;
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace Hutu.Rendering.Boxes;

/// <summary>What one element's style was resolved from: the match, the inherited areas, the scope, and
/// the part of the element a style can see.</summary>
internal readonly struct StyleKey : IEquatable<StyleKey>
{
    public StyleKey(
        MatchedCascade cascade,
        ComputedStyle.StyleValues? parent,
        String? parentTag,
        ICssProperty? parentLineHeight,
        VariableScope? variables,
        String tag,
        String? hints,
        Double rootFontSize,
        Byte marks)
    {
        Cascade = cascade;
        Parent = parent;
        ParentTag = parentTag;
        ParentLineHeight = parentLineHeight;
        Variables = variables;
        Tag = tag;
        Hints = hints;
        RootFontSize = rootFontSize;
        Marks = marks;
    }

    public MatchedCascade Cascade { get; }
    public ComputedStyle.StyleValues? Parent { get; }
    public String? ParentTag { get; }
    public ICssProperty? ParentLineHeight { get; }
    public VariableScope? Variables { get; }
    public String Tag { get; }
    public String? Hints { get; }
    public Double RootFontSize { get; }
    public Byte Marks { get; }

    public Boolean Equals(StyleKey other) =>
        ReferenceEquals(Cascade, other.Cascade) &&
        ReferenceEquals(Parent, other.Parent) &&
        String.Equals(ParentTag, other.ParentTag, StringComparison.Ordinal) &&
        ReferenceEquals(ParentLineHeight, other.ParentLineHeight) &&
        ReferenceEquals(Variables, other.Variables) &&
        String.Equals(Tag, other.Tag, StringComparison.Ordinal) &&
        String.Equals(Hints, other.Hints, StringComparison.Ordinal) &&
        RootFontSize == other.RootFontSize &&
        Marks == other.Marks;

    public override Boolean Equals(Object? value) => value is StyleKey other && Equals(other);

    public override Int32 GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(RuntimeHelpers.GetHashCode(Cascade));
        hash.Add(RuntimeHelpers.GetHashCode(Parent));
        hash.Add(ParentTag, StringComparer.Ordinal);
        hash.Add(RuntimeHelpers.GetHashCode(ParentLineHeight));
        hash.Add(RuntimeHelpers.GetHashCode(Variables));
        hash.Add(Tag, StringComparer.Ordinal);
        hash.Add(Hints, StringComparer.Ordinal);
        hash.Add(RootFontSize);
        hash.Add(Marks);
        return hash.ToHashCode();
    }
}

/// <summary>One resolved style, held by the inputs that produced it.</summary>
internal sealed class StyleEntry
{
    private readonly ComputedStyle.StyleValues _values;

    public StyleEntry(ComputedStyle.StyleValues values, Double zoom, ColorFilter filter)
    {
        _values = values;
        Zoom = zoom;
        Filter = filter;
    }

    public Double Zoom { get; }
    public ColorFilter Filter { get; }

    public ComputedStyle Create(IElement element, IRenderDimensions dimensions, FontFaceTable? faces) =>
        new(dimensions, element, _values.Copy(), faces, Zoom, Filter);
}

/// <summary>
/// The styles one build has already resolved, keyed by everything a fused style is a function of.
/// </summary>
/// <remarks>
/// Unlike <see cref="FusedAreaCache"/>, which answers for the seven areas whose inputs it can name,
/// this holds the whole style: the areas, the keyword block, the zoom and the filter. A document
/// whose elements fall into a few matching groups reads each group's declarations once, not once
/// per element. The cache lives for one build; a rebuild whose inputs moved starts from empty.
/// </remarks>
internal sealed class FusedStyleCache
{
    private readonly Dictionary<StyleKey, StyleEntry> _entries = new();

    public Int32 Hits { get; private set; }
    public Int32 Misses { get; private set; }
    public Int32 Count => _entries.Count;

    public Boolean TryGet(StyleKey key, out StyleEntry entry)
    {
        if (_entries.TryGetValue(key, out entry!))
        {
            Hits++;
            return true;
        }

        Misses++;
        return false;
    }

    public void Add(StyleKey key, StyleEntry entry) => _entries[key] = entry;

    public void Clear()
    {
        _entries.Clear();
        Hits = 0;
        Misses = 0;
    }
}

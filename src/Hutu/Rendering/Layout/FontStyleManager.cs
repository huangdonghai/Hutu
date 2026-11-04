using System;
using System.Collections.Generic;

namespace Hutu.Rendering.Layout;

/// <summary>
/// Interns resolved font requests for one renderer.
/// </summary>
/// <remarks>
/// A document uses a handful of faces and sizes, not one object per box. The manager keeps the
/// strings, doubles and the parsed feature answer once; a <see cref="FontStyle"/> handle is then
/// just the entry reference. The entry's sequential number is its id, and the id is the hash. This
/// is the table a page's layout and paint share, so a relayout resolves the same face to the same
/// entry rather than rebuilding and rehashing strings for every run.
/// <para>
/// A public <see cref="FontStyle"/> constructor uses <see cref="Shared"/>; the renderer's own
/// manager is supplied through <see cref="Hutu.Rendering.ComputedStyle"/> so its entries live and
/// die with the document.
/// </para>
/// </remarks>
public sealed class FontStyleManager
{
    internal static FontStyleManager Shared { get; } = new();

    private readonly Object _gate = new();
    private readonly Dictionary<FontStyleKey, FontStyleEntry> _entries = new();
    private readonly List<FontStyleEntry> _ordered = new();
    private readonly Dictionary<String, String> _text = new(StringComparer.Ordinal);

    /// <summary>Gets how many distinct styles this manager holds.</summary>
    public Int32 Count
    {
        get
        {
            lock (_gate)
            {
                return _ordered.Count;
            }
        }
    }

    /// <summary>Gets the style's handle, or adds its one entry when the manager has not seen it.</summary>
    public FontStyle GetOrAdd(
        String family,
        Double size,
        Int32 weight = 400,
        Boolean italic = false,
        Double? adjust = null,
        String? features = null)
    {
        family = String.IsNullOrWhiteSpace(family) ? "sans-serif" : family;
        features = String.IsNullOrWhiteSpace(features) ? null : features;

        var key = new FontStyleKey(family, size, weight, italic, adjust, features);

        lock (_gate)
        {
            if (_entries.TryGetValue(key, out var existing))
            {
                return new FontStyle(existing);
            }

            family = Text(family);
            var internedFeatures = features is null ? null : Text(features);
            var entry = new FontStyleEntry(
                this,
                _ordered.Count + 1,
                family,
                size,
                weight,
                italic,
                adjust,
                internedFeatures);

            _ordered.Add(entry);
            _entries.Add(new FontStyleKey(family, size, weight, italic, adjust, internedFeatures), entry);

            return new FontStyle(entry);
        }
    }

    private String Text(String value)
    {
        if (_text.TryGetValue(value, out var interned))
        {
            return interned;
        }

        _text.Add(value, value);

        return value;
    }

    private readonly struct FontStyleKey : IEquatable<FontStyleKey>
    {
        private readonly String _family;
        private readonly Double _size;
        private readonly Int32 _weight;
        private readonly Boolean _italic;
        private readonly Double? _adjust;
        private readonly String? _features;

        public FontStyleKey(
            String family,
            Double size,
            Int32 weight,
            Boolean italic,
            Double? adjust,
            String? features)
        {
            _family = family;
            _size = size;
            _weight = weight;
            _italic = italic;
            _adjust = adjust;
            _features = features;
        }

        public Boolean Equals(FontStyleKey other) =>
            _size.Equals(other._size) &&
            _weight == other._weight &&
            _italic == other._italic &&
            Nullable.Equals(_adjust, other._adjust) &&
            String.Equals(_family, other._family, StringComparison.Ordinal) &&
            String.Equals(_features, other._features, StringComparison.Ordinal);

        public override Boolean Equals(Object? obj) => obj is FontStyleKey other && Equals(other);

        public override Int32 GetHashCode()
        {
            unchecked
            {
                var hash = StringComparer.Ordinal.GetHashCode(_family);
                hash = (hash * 397) ^ _size.GetHashCode();
                hash = (hash * 397) ^ _adjust.GetHashCode();
                hash = (hash * 397) ^ _weight;
                hash = (hash * 397) ^ (_italic ? 1 : 0);
                hash = (hash * 397) ^ (_features is null ? 0 : StringComparer.Ordinal.GetHashCode(_features));
                return hash;
            }
        }
    }
}

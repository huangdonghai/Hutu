using Hutu.Rendering.Layout;
using System;
using System.Collections.Generic;

namespace Hutu.Rendering;

/// <summary>
/// The copy-on-write discipline every area record obeys. Declared once here rather than as instance
/// methods on each record.
/// </summary>
/// <remarks>
/// An area is immutable and shared: the same instance stands for every box whose values agree, which
/// is what keeps a document from paying for a value per element. Nothing may write to an area after
/// it is built - not to "fix up" one field, not to cache a derived value on it - because a write is
/// visible to every box sharing it, and the mistake would show up as one box's declaration quietly
/// changing another's.
/// </remarks>
internal static class ComputedStyleCow
{
    /// <summary>
    /// Produces the record that results from changing one property, or returns <paramref name="self"/>
    /// when it already holds that value - so a write that changes nothing allocates nothing.
    /// </summary>
    internal static TSelf SetPropertyValue<TSelf, TValue>(
        this TSelf self,
        TValue currentValue,
        TValue newValue,
        Func<TSelf, TValue, TSelf> apply) =>
        EqualityComparer<TValue>.Default.Equals(currentValue, newValue) ? self : apply(self, newValue);

    /// <summary>
    /// The area-level counterpart: swaps <paramref name="newArea"/> onto <paramref name="self"/> unless
    /// it is already there.
    /// </summary>
    /// <remarks>
    /// Compared by reference, deliberately. A structural comparison would agree with a reference
    /// comparison at every call site here - the incoming area is either the one already held or a
    /// freshly built, therefore different, object - while costing a scan of every field to say so.
    /// </remarks>
    internal static TSelf AdoptArea<TSelf, TArea>(
        this TSelf self,
        TArea currentArea,
        TArea newArea,
        Func<TSelf, TArea, TSelf> apply)
        where TArea : class =>
        ReferenceEquals(currentArea, newArea) ? self : apply(self, newArea);
}

/// <summary>
/// What the four border sides are drawn with, how thick they are, and the corner radii. Everything
/// here is resolved: a width that was <c>medium</c> is a number of pixels, and a style that was
/// <c>double</c> keeps its keyword because the collapsing model breaks ties by it.
/// </summary>
/// <remarks>
/// The colours are not here. A border colour resolves <c>currentcolor</c> against the element's own
/// colour, and that colour depends on live state - whether the anchor has been visited, and a second
/// selector match to tell a declared colour from an inherited one - so putting the colours in would
/// move that cost from "when the colour is asked for" to "every box at construction".
/// </remarks>
internal sealed record BorderArea
{
    /// <summary>
    /// What a box that declares no border property at all shares: no strokes, nothing drawn, square
    /// corners.
    /// </summary>
    internal static readonly BorderArea Default = new();

    internal String TopStyle { get; init; } = CssInitialValues.BorderStyle;

    internal String RightStyle { get; init; } = CssInitialValues.BorderStyle;

    internal String BottomStyle { get; init; } = CssInitialValues.BorderStyle;

    internal String LeftStyle { get; init; } = CssInitialValues.BorderStyle;

    /// <summary>Gets how thick each side is, which is zero whenever its style draws nothing.</summary>
    internal Double TopWidth { get; init; }

    internal Double RightWidth { get; init; }

    internal Double BottomWidth { get; init; }

    internal Double LeftWidth { get; init; }

    /// <summary>
    /// Gets the four strokes as the values the painter and the collapsing model want. Stored rather
    /// than derived per read because the derivation lowercases the keyword, which allocates.
    /// </summary>
    internal BorderStyles Lines { get; init; }

    /// <summary>Gets the corner radii, with any percentage still unresolved - a radius is relative to
    /// the box's own size, which layout is what knows.</summary>
    internal StyleCornerRadii Radius { get; init; }

    /// <summary>Gets whether the side draws anything, which is the question the box model asks.</summary>
    internal static Boolean Draws(String style) =>
        !style.Equals("none", StringComparison.OrdinalIgnoreCase) &&
        !style.Equals("hidden", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// The box a declared width and height describe, and the lengths that place and fill it.
/// </summary>
/// <remarks>
/// Every length here keeps its unit: a percentage is relative to the containing block, which only the
/// layout pass knows, so resolving one here would be wrong rather than early. What is saved is the
/// parse - the declaration is read once instead of on every access - not the resolution.
/// <para>
/// The initialisers are not decoration: the resolver is skipped entirely for an element that declares
/// none of these properties, and what it would have produced for such an element is exactly this.
/// A default that disagreed with the reader's own fallback would make skipping it wrong.
/// </para>
/// </remarks>
internal sealed record BoxModelArea
{
    /// <summary>What a box that declares no width, height, margin or padding shares.</summary>
    internal static readonly BoxModelArea Default = new();

    internal BoxSizing BoxSizing { get; init; } = BoxSizing.ContentBox;

    internal StyleLength Width { get; init; } = StyleLength.Auto;

    internal StyleLength Height { get; init; } = StyleLength.Auto;

    internal StyleLength MinWidth { get; init; } = StyleLength.None;

    internal StyleLength MaxWidth { get; init; } = StyleLength.None;

    internal StyleLength MinHeight { get; init; } = StyleLength.None;

    internal StyleLength MaxHeight { get; init; } = StyleLength.None;

    internal StyleThickness Margin { get; init; } = new(StyleLength.Zero, StyleLength.Zero, StyleLength.Zero, StyleLength.Zero);

    internal StyleThickness Padding { get; init; } = new(StyleLength.Zero, StyleLength.Zero, StyleLength.Zero, StyleLength.Zero);
}

/// <summary>
/// One intern table per area, alive for one box tree.
/// </summary>
/// <remarks>
/// Two boxes that resolve to the same values should be one object, whether or not they are related:
/// sibling list items that both declare <c>font-weight: bold</c> agree as much as a parent and child
/// that inherit it do, and only a table keyed by value catches both. Sharing by parent alone would not.
/// <para>
/// The table is built per box tree and dropped with it. It holds the strings the declarations handed
/// over, so one alive across documents would keep every value ever seen.
/// </para>
/// <para>
/// Interning is the last resort, not the first: a reader that can tell from the declaration that an
/// area is untouched, or that a parent's area is already in hand, should take that path and never
/// build or hash anything. The table cannot help there - it can only be consulted once the candidate
/// exists, by which point the allocation it saves has already been paid.
/// </para>
/// </remarks>
internal sealed class StyleIntern
{
    private readonly Dictionary<BorderArea, BorderArea> _border = new();

    private readonly Dictionary<BoxModelArea, BoxModelArea> _boxModel = new();

    /// <summary>Gets the instance standing for a box model, building it only if the value is new.</summary>
    internal BoxModelArea BoxModel(BoxModelArea area)
    {
        if (_boxModel.TryGetValue(area, out var existing))
        {
            StyleProbe.NoteInternHit();

            return existing;
        }

        StyleProbe.NoteAreaBuilt();
        _boxModel[area] = area;

        return area;
    }

    /// <summary>
    /// Gets the instance standing for <paramref name="area"/>, which is <paramref name="area"/> itself
    /// the first time that value is seen.
    /// </summary>
    internal BorderArea Border(BorderArea area)
    {
        if (_border.TryGetValue(area, out var existing))
        {
            StyleProbe.NoteInternHit();

            return existing;
        }

        StyleProbe.NoteAreaBuilt();
        _border[area] = area;

        return area;
    }
}

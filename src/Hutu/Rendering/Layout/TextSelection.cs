using System;

namespace Hutu.Rendering.Layout;

/// <summary>
/// A selection: the position it was anchored at and the position the caret was moved to.
/// </summary>
/// <remarks>
/// The two ends are kept the way they were made rather than sorted, because a drag can travel in
/// either direction and the anchor is what a later shift-click or a re-drag extends from. Which end
/// comes first in the document is a question only the layout can answer, so the ordering is left to
/// <see cref="TextSelector"/>.
/// </remarks>
public sealed class TextSelection : IEquatable<TextSelection>
{
    public TextSelection(TextPosition anchor, TextPosition caret)
    {
        Anchor = anchor;
        Caret = caret;
    }

    /// <summary>Gets the position the selection was started from.</summary>
    public TextPosition Anchor { get; }

    /// <summary>Gets the position the caret has been moved to.</summary>
    public TextPosition Caret { get; }

    /// <summary>Gets whether the selection covers no character at all.</summary>
    public Boolean IsEmpty => Anchor.Equals(Caret);

    public Boolean Equals(TextSelection? other) =>
        other is not null && Anchor.Equals(other.Anchor) && Caret.Equals(other.Caret);

    public override Boolean Equals(Object? obj) => Equals(obj as TextSelection);

    public override Int32 GetHashCode() => HashCode.Combine(Anchor, Caret);
}

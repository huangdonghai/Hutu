using System;

namespace Hutu.Rendering.Layout;

/// <summary>
/// A place in the laid-out text: a run plus a character offset inside it.
/// </summary>
/// <remarks>
/// The offset indexes <see cref="LayoutBox.Text"/> rather than the source text node. That string is
/// the text as it was actually laid out - white space collapsed, trailing spaces trimmed and the run
/// already split by line breaking - so an offset into it is unambiguous about what the reader sees,
/// which is exactly what a selection is made of. Indexing the DOM instead would have to invert all
/// three of those transformations and would still disagree with the glyphs on screen.
/// <para>
/// Boxes are compared by reference: a position is only meaningful against the layout it was taken
/// from, and a relayout produces new boxes rather than equal ones.
/// </para>
/// </remarks>
public readonly struct TextPosition : IEquatable<TextPosition>
{
    public TextPosition(LayoutBox box, Int32 index)
    {
        Box = box ?? throw new ArgumentNullException(nameof(box));
        Index = index;
    }

    /// <summary>Gets the text run the position belongs to.</summary>
    public LayoutBox Box { get; }

    /// <summary>Gets the character offset inside the run's text.</summary>
    public Int32 Index { get; }

    public Boolean Equals(TextPosition other) =>
        ReferenceEquals(Box, other.Box) && Index == other.Index;

    public override Boolean Equals(Object? obj) => obj is TextPosition other && Equals(other);

    public override Int32 GetHashCode() => HashCode.Combine(
        System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(Box),
        Index);

    public override String ToString() => $"TextPosition({Index})";
}

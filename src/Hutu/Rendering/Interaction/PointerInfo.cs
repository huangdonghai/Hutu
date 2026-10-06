using System;

namespace Hutu.Rendering.Interaction;

/// <summary>
/// What a host learns from a pointer move: whether the document's picture changed, and what the
/// pointer should look like there.
/// </summary>
/// <remarks>
/// The two travel together because they come from one hit test - the host cannot shape the pointer
/// without knowing what is under it, and cannot know what is under it without the hit the hover
/// state was built from.
/// </remarks>
public readonly struct PointerInfo
{
    public PointerInfo(Boolean changed, CursorKind cursor, String? hint)
    {
        Changed = changed;
        Cursor = cursor;
        Hint = hint;
    }

    /// <summary>
    /// Gets whether the document changed and has to be laid out and painted again. False means the
    /// pointer stayed within the element it was already over, so nothing about the drawing moved.
    /// </summary>
    public Boolean Changed { get; }

    /// <summary>Gets the pointer the document asks for under the point.</summary>
    public CursorKind Cursor { get; }

    /// <summary>Gets the hint the point asks to be shown, or null when it asks for none.</summary>
    public String? Hint { get; }
}

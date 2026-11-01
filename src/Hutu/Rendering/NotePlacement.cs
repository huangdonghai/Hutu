namespace Hutu.Rendering;

/// <summary>How a note element places the note it defines.</summary>
internal enum NotePlacement : Byte
{
    /// <summary>The element stays ordinary inline content.</summary>
    None,

    /// <summary>A two-line annotation at the note's inline position.</summary>
    Inline,

    /// <summary>A shared note flow on the line-over side of the line the note stands on.</summary>
    Interline,

    /// <summary>An annotation in the page or container margin.</summary>
    Margin,
}

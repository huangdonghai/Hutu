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

/// <summary>The side of the page margin where a margin note is placed.</summary>
internal enum NoteSide : Byte
{
    /// <summary>Uses the wider static inline-axis margin of the root element.</summary>
    Auto,

    /// <summary>The inline-start margin side: left in a row, top in a column.</summary>
    InlineStart,

    /// <summary>The inline-end margin side: right in a row, bottom in a column.</summary>
    InlineEnd,
}

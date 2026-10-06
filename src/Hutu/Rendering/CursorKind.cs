namespace Hutu.Rendering;

/// <summary>
/// The pointer a box asks for, i.e. what the <c>cursor</c> property resolves to.
/// </summary>
/// <remarks>
/// The keywords are normalised to one member each so a host can map them without knowing CSS, and
/// <see cref="Auto"/> is kept apart from <see cref="Default"/>: the former means "decide from what
/// is under the pointer", which is what makes text show a text cursor without anything asking for
/// one, and the latter is a box saying it wants the ordinary arrow.
/// </remarks>
public enum CursorKind : byte
{
    /// <summary>Nothing was declared: what is under the pointer decides.</summary>
    Auto,

    /// <summary>The ordinary arrow, as <c>cursor: default</c> asks for.</summary>
    Default,

    /// <summary>A hand, as a link asks for.</summary>
    Pointer,

    /// <summary>Text set along a line, whose cursor is an upright I-beam.</summary>
    Text,

    /// <summary>
    /// Text set down a column, whose cursor is an I-beam turned on its side - the one shape no
    /// platform offers as a standard cursor, so a host has to draw it.
    /// </summary>
    VerticalText,

    /// <summary>Something that can be moved.</summary>
    Move,

    /// <summary>The application is busy.</summary>
    Wait,

    /// <summary>Help is available.</summary>
    Help,

    /// <summary>The action is not allowed here.</summary>
    NotAllowed,

    /// <summary>A precise position is being asked for.</summary>
    Crosshair,

    /// <summary>No pointer at all.</summary>
    None,
}

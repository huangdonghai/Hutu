using AngleSharp.Css.Dom;
using System;

namespace Hutu.Rendering.Layout;

/// <summary>What a break property asks to happen between two boxes.</summary>
internal enum BreakValue : Byte
{
    /// <summary>The engine may break wherever the content allows.</summary>
    Auto,

    /// <summary>Do not break here if there is anywhere else to break.</summary>
    Avoid,

    /// <summary>Begin a new column, or end the one before.</summary>
    Column,

    /// <summary>Begin a new page, or end the one before.</summary>
    Page,

    /// <summary>Force the next page and make it a left page.</summary>
    Left,

    /// <summary>Force the next page and make it a right page.</summary>
    Right,
}

/// <summary>
/// The fragmentation properties of a box (CSS Fragmentation 3): where a break may fall around it, what
/// it refuses inside itself, and the fewest lines a fragment may keep at its ends.
/// </summary>
/// <remarks>
/// The legacy <c>page-break-*</c> longhands mean the same thing under the older name and are read when
/// the modern one says nothing: <c>always</c> is a forced page break, and <c>avoid</c> is the same avoid
/// (CSS Fragmentation 3 §4.2). Nothing reads this yet - the fragmentation model that does is the next
/// slice - so it is wired and tested on its own.
/// </remarks>
internal readonly struct BreakStyle
{
    private BreakStyle(
        BreakValue before, BreakValue after, BreakValue inside, Int32 widows, Int32 orphans, Boolean spansColumns)
    {
        Before = before;
        After = after;
        Inside = inside;
        Widows = widows;
        Orphans = orphans;
        SpansColumns = spansColumns;
    }

    /// <summary>Gets what the box asks for before itself.</summary>
    public BreakValue Before { get; }

    /// <summary>Gets what it asks for after itself.</summary>
    public BreakValue After { get; }

    /// <summary>Gets what it refuses inside itself.</summary>
    public BreakValue Inside { get; }

    /// <summary>Gets the fewest lines a fragment must keep at its end.</summary>
    public Int32 Widows { get; }

    /// <summary>Gets the fewest lines a fragment must keep at its start.</summary>
    public Int32 Orphans { get; }

    /// <summary>Gets whether the box asks to span every column of the container it stands in.</summary>
    public Boolean SpansColumns { get; }

    /// <summary>Gets whether the box says anything about breaking at all.</summary>
    public Boolean IsPlain => Before == BreakValue.Auto && After == BreakValue.Auto && Inside == BreakValue.Auto;

    /// <summary>
    /// Reads the box's own declarations. The break properties are not inherited and begin at <c>auto</c>;
    /// <c>widows</c> and <c>orphans</c> are inherited (CSS Break 3 §3.3), so a box that says nothing takes
    /// the count handed down rather than the initial two.
    /// </summary>
    public static BreakStyle ReadFused(Func<String, ICssProperty?> find, Int32 widows, Int32 orphans) =>
        new(
            Between(find("break-before")?.Value, find("page-break-before")?.Value),
            Between(find("break-after")?.Value, find("page-break-after")?.Value),
            InsideOf(find("break-inside")?.Value, find("page-break-inside")?.Value),
            Count(find("widows")?.Value, widows),
            Count(find("orphans")?.Value, orphans),
            String.Equals(find("column-span")?.Value?.Trim(), "all", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Reads a before or after value. <c>avoid-page</c> and <c>avoid-column</c> are both an avoid: the
    /// fragmentainer they name is a refinement the engine does not tell apart yet, and treating one as a
    /// forced break would be the wrong answer where it counts.
    /// </summary>
    private static BreakValue Between(String? modern, String? legacy)
    {
        var text = Text(modern) ?? Text(legacy);

        return text switch
        {
            "avoid" or "avoid-page" or "avoid-column" => BreakValue.Avoid,
            "column" => BreakValue.Column,
            "page" or "always" => BreakValue.Page,
            "left" => BreakValue.Left,
            "right" => BreakValue.Right,
            "recto" => BreakValue.Right,
            "verso" => BreakValue.Left,
            _ => BreakValue.Auto,
        };
    }

    private static BreakValue InsideOf(String? modern, String? legacy) =>
        (Text(modern) ?? Text(legacy)) switch
        {
            "avoid" or "avoid-page" or "avoid-column" => BreakValue.Avoid,
            _ => BreakValue.Auto,
        };

    private static String? Text(String? value) =>
        String.IsNullOrWhiteSpace(value) ? null : value.Trim().ToLowerInvariant();

    private static Int32 Count(String? value, Int32 fallback) =>
        Int32.TryParse(value?.Trim(), out var count) && count >= 1 ? count : fallback;
}

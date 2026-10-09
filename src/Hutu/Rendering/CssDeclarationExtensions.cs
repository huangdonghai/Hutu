using AngleSharp.Css.Dom;

namespace Hutu.Rendering;

/// <summary>
/// Reads a declaration the way a reader that only wants to know what is there should: without asking
/// it to invent anything.
/// </summary>
/// <remarks>
/// <c>GetPropertyValue</c> asks the declaration for a property model and serialises it, and a
/// declaration answers a name it does not carry with one it builds for the occasion - a shorthand
/// rebuilt from scratch, value by value - which it then throws away. <see cref="TryGetValue"/> asks
/// the same question of <c>TryGetProperty</c>, which answers with null and nothing else; the empty
/// string is what comes out here because it is what the serialising lookup answered for the same
/// absence, so a reader written against that one goes on reading the same way.
/// <para>
/// What the cheap lookup does not do is rebuild a name a declaration expands instead of carrying, so
/// a reader that wants one of those - <c>text-decoration</c>, <c>gap</c> - has to ask for the model:
/// <c>GetPropertyValue</c> here, or <see cref="ComputedStyle.RawShorthand"/> where the value is
/// wanted with its type intact.
/// </para>
/// </remarks>
internal static class CssDeclarationExtensions
{
    /// <summary>
    /// Gets the value of the named property as it was written, or the empty string when the
    /// declaration does not carry it.
    /// </summary>
    public static String TryGetValue(this ICssProperties? declaration, String name) =>
        declaration?.TryGetProperty(name)?.Value ?? String.Empty;
}

using System.Text.Json;

namespace Hutu.Conformance;

/// <summary>
/// What the engine does with a CSS property, stated once for the whole corpus.
/// </summary>
/// <remarks>
/// The point is not to document the engine - the code does that, and better. It is to make a page
/// that starts using something unmodelled into a test failure. A renderer's real risk is not the
/// feature it knows it lacks, it is the feature a page began relying on and nobody noticed, because
/// the page still came out looking like a page.
/// <para>
/// Three states, and the difference between the last two is the whole reason for having the file:
/// <c>modelled</c> means the engine reads the property and does something with it; <c>partial</c>
/// means it reads it and gets some of the cases wrong; <c>unsupported</c> means it never looks. A
/// property nobody has decided is <c>unknown</c>, and a page that declares one fails the run.
/// </para>
/// </remarks>
internal sealed record CssSupport(Dictionary<String, String> Properties)
{
    public const String Artifact = "css-support.json";

    private static readonly JsonSerializerOptions Format = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static CssSupport Load(String path) =>
        System.Text.Json.JsonSerializer.Deserialize<CssSupport>(
            System.IO.File.ReadAllText(path), Format)
        ?? throw new InvalidDataException($"{path} held no registry");

    /// <summary>The state a property is registered as, or null when nobody has looked at it.</summary>
    public String? State(String property) =>
        Properties.TryGetValue(property, out var entry) ? Status(entry) : null;

    public static String Status(String entry)
    {
        var colon = entry.IndexOf(':');

        return (colon < 0 ? entry : entry[..colon]).Trim().ToLowerInvariant();
    }

    public static String Reason(String entry)
    {
        var colon = entry.IndexOf(':');

        return colon < 0 ? string.Empty : entry[(colon + 1)..].Trim();
    }

    public static IEnumerable<String> States => ["modelled", "partial", "unsupported"];
}

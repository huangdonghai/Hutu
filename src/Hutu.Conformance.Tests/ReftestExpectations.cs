using System.Text.Json;

namespace Hutu.Conformance;

/// <summary>
/// Which reftests the engine is known to get wrong, and why.
/// </summary>
/// <remarks>
/// The pattern is the one every browser's test harness uses, and for the same reason: a suite that is
/// red on a known gap goes quiet, because nobody reads the eightieth failure and the seventy-ninth
/// stops mattering. Registering the gap keeps the run green where it should be green and loud where it
/// should be loud, and it turns the list of failures into a queue with reasons attached.
/// <para>
/// An entry is <c>pass</c> - the default, and worth writing only to override a directory's entry - or
/// <c>fail: &lt;why&gt;</c>. The last is a claim with an expiry date: the day the engine draws the pair
/// the same, the test fails and says so, because a stale expectation is how a fixed bug keeps looking
/// unfixed.
/// </para>
/// </remarks>
internal sealed record ReftestExpectations(Dictionary<String, String> Tests)
{
    public const String Artifact = "reftest-expectations.json";

    private static readonly JsonSerializerOptions Format = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static ReftestExpectations Load(String path) =>
        System.Text.Json.JsonSerializer.Deserialize<ReftestExpectations>(
            System.IO.File.ReadAllText(path), Format)
        ?? throw new InvalidDataException($"{path} held no expectations");

    public static String Folder => Path.Combine(Corpus.Root, "conformance");

    public static String PathOf => Path.Combine(Folder, Artifact);

    /// <summary>
    /// The state a test is registered as: <c>pass</c>, or <c>fail</c> with the reason beside it.
    /// </summary>
    /// <remarks>
    /// A trailing <c>/*</c> on a key stands for everything under that directory, which is how a whole
    /// area of an unmodelled feature is registered once rather than a hundred times - and why the
    /// longest matching key wins: what is said about one test beats what is said about its folder.
    /// </remarks>
    public (String State, String Reason) For(String relative)
    {
        var best = -1;
        String? entry = null;

        foreach (var (key, value) in Tests)
        {
            var prefix = key.EndsWith("/*", StringComparison.Ordinal) ? key[..^1] : key;

            var matches = key.EndsWith("/*", StringComparison.Ordinal)
                ? relative.StartsWith(prefix, StringComparison.Ordinal)
                : String.Equals(key, relative, StringComparison.Ordinal);

            if (matches && prefix.Length > best)
            {
                best = prefix.Length;
                entry = value;
            }
        }

        if (entry is null)
        {
            return ("pass", String.Empty);
        }

        return (ReftestStatus.Status(entry), ReftestStatus.Reason(entry));
    }

    public Boolean Registers(String relative) => Tests.ContainsKey(relative);
}

/// <summary>
/// The two words a registry entry can start with, shared by the reftest and the property registries.
/// </summary>
internal static class ReftestStatus
{
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
}

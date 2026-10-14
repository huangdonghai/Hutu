using System.Text.Json;
using System.Text.Json.Serialization;

namespace Hutu.Reference;

/// <summary>
/// What a conformance case asks for: the page to read, the viewport to read it at, which boxes to
/// compare, and how far two numbers may be apart before the difference is a difference.
/// </summary>
/// <remarks>
/// The manifest is written by hand and is the one file that says what a case is about; the reference
/// it is compared against is written by the collector. Keeping the two apart is what lets a case be
/// reviewed without also having to review a machine's dump, and lets the reference be re-captured
/// when the browser changes without touching the question.
/// </remarks>
public sealed record ReferenceCase
{
    /// <summary>The page to render, relative to the directory the case file is in.</summary>
    public required String Page { get; init; }

    /// <summary>
    /// Gets or sets the width the page is read at. It is the browser's <em>content</em> width rather
    /// than its window: a page that overflows vertically spends about fifteen pixels on a classic
    /// scrollbar, and reading the two sides at different widths moves every centred box.
    /// </summary>
    public Int32 ViewportWidth { get; init; } = 1221;

    public Int32 ViewportHeight { get; init; } = 1237;

    /// <summary>
    /// Gets how far a side may be from the other before it is reported. Half a CSS pixel is the
    /// default: a browser rounds a box to the device pixel grid in a way the layout arithmetic does
    /// not, and the difference between that and a real error is which side of one it lands on.
    /// </summary>
    public Double Tolerance { get; init; } = 0.6;

    /// <summary>The selectors to compare, each matched by <c>querySelector</c> - the first hit.</summary>
    public IReadOnlyList<String> Selectors { get; init; } = [];

    /// <summary>
    /// The selectors whose every match is compared, for the groups a page repeats: the ten featured
    /// languages, the twelve sister projects, one list of each size.
    /// </summary>
    public IReadOnlyList<String> Groups { get; init; } = [];

    /// <summary>
    /// The computed properties to compare alongside the geometry. Geometry alone hides a wrong scale
    /// behind a page that happens to fit; the size a box's text is set at does not.
    /// </summary>
    public IReadOnlyList<String> Styles { get; init; } = ["font-size", "line-height", "display", "position"];

    /// <summary>
    /// Gets the properties the reference carries for a box but the engine is not asked about. A page
    /// whose measurement depends on a feature the engine does not model is recorded here so the case
    /// fails loudly rather than quietly agreeing on the parts that happen to match.
    /// </summary>
    public IReadOnlyList<String> KnownGaps { get; init; } = [];

    /// <summary>
    /// Gets why the case is expected to differ, when it is. A gap the engine has is registered rather
    /// than left out of the corpus: the case runs, the report prints what is wrong, and the day the
    /// engine gets it right the marker becomes the failure - which is how a known wrong answer cannot
    /// quietly become an unknown one.
    /// </summary>
    public String? KnownGap { get; init; }

    public static ReferenceCase Load(String path) =>
        Schema.Read<ReferenceCase>(path);

    public void Save(String path) => Schema.Write(path, this);
}

/// <summary>What a browser said about a page, once, in an environment written down.</summary>
public sealed record ReferenceDocument
{
    /// <summary>The page the reference was captured from, relative to the case directory where it can.</summary>
    public required String Source { get; init; }

    public required CaptureEnvironment Environment { get; init; }

    public IReadOnlyList<ReferenceElement> Elements { get; init; } = [];

    public static ReferenceDocument Load(String path) => Schema.Read<ReferenceDocument>(path);

    public void Save(String path) => Schema.Write(path, this);
}

/// <summary>
/// The conditions a reference was read under. It is part of the reference rather than a comment
/// because a number without them is not reproducible: the same page is another size at another
/// device scale, in another colour scheme, and - for a page that carries scripts - with or without
/// them running.
/// </summary>
public sealed record CaptureEnvironment
{
    public required String Browser { get; init; }

    /// <summary>The channel the browser came from: <c>msedge</c> is the one installed on the machine.</summary>
    public required String Channel { get; init; }

    public Int32 ViewportWidth { get; init; }

    public Int32 ViewportHeight { get; init; }

    public Double DeviceScaleFactor { get; init; }

    /// <summary>Light, always: a page that carries a dark scheme paints it from a media feature the
    /// engine does not answer, so a dark reference compares a colour choice against a missing feature.</summary>
    public required String ColorScheme { get; init; }

    /// <summary>The language the document was served as, which decides what text is measured.</summary>
    public required String Language { get; init; }

    public required String UserAgent { get; init; }

    /// <summary><c>documentElement.clientWidth</c> - the width the page was actually laid out in.</summary>
    public Int32 ClientWidth { get; init; }

    public Int32 ClientHeight { get; init; }

    public Int32 ScrollWidth { get; init; }

    public Int32 ScrollHeight { get; init; }

    /// <summary>How many script elements were disarmed in the copy the browser read.</summary>
    public Int32 ScriptsNeutralized { get; init; }

    /// <summary>How many inline event handlers and refreshes were stripped along with them.</summary>
    public Int32 HandlersStripped { get; init; }

    /// <summary>How many <c>&lt;noscript&gt;</c> blocks the page carries, since those read differently
    /// to a browser that runs scripts and to a reader that runs none.</summary>
    public Int32 NoscriptBlocks { get; init; }

    /// <summary>Typefaces the document brings with itself. Zero means every glyph came from a font
    /// installed on the machine, which is the number that says how portable the geometry is.</summary>
    public Int32 WebFonts { get; init; }
}

/// <summary>One box, and the handful of computed values that say what it was set to be.</summary>
public sealed record ReferenceElement
{
    public required String Selector { get; init; }

    /// <summary>Which of a selector's matches this is. The first is zero.</summary>
    public Int32 Index { get; init; }

    /// <summary>Whether anything matched at all, which is a fact worth recording: a page that changes
    /// its markup makes a silent absence look like a missing box.</summary>
    public Boolean Found { get; init; }

    /// <summary>The border box, in document coordinates - the viewport's scroll is added back.</summary>
    public Double X { get; init; }

    public Double Y { get; init; }

    public Double Width { get; init; }

    public Double Height { get; init; }

    /// <summary>The computed properties the case asked for, keyed by their CSS name.</summary>
    /// <remarks>
    /// A concrete dictionary rather than a read-only view: the browser's answer is turned into this
    /// type by Playwright's own converter, which builds what it is told to build and cannot be told to
    /// build an interface.
    /// </remarks>
    public Dictionary<String, String> Styles { get; init; } = new();
}

/// <summary>
/// Reads and writes the two documents both ends of the harness speak.
/// </summary>
/// <remarks>
/// One place on purpose: the collector writes a reference the differ reads, and the two only agree
/// while they agree on the names and the shape. The formatting is fixed here too - indented, LF,
/// camelCase - because a reference is checked in and reviewed, and a re-capture that reshuffles
/// whitespace hides the numbers that actually moved.
/// </remarks>
public static class Schema
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static T Read<T>(String path)
    {
        using var stream = File.OpenRead(path);
        var value = JsonSerializer.Deserialize<T>(stream, Options);

        return value ?? throw new InvalidDataException($"{path} held no {typeof(T).Name}");
    }

    public static void Write<T>(String path, T value)
    {
        var directory = Path.GetDirectoryName(path);

        if (directory is { Length: > 0 })
        {
            Directory.CreateDirectory(directory);
        }

        var text = JsonSerializer.Serialize(value, Options).Replace("\r\n", "\n");
        File.WriteAllText(path, text.EndsWith('\n') ? text : text + "\n");
    }
}

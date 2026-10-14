using Hutu.Reference;

namespace Hutu.Conformance;

/// <summary>
/// The corpus: every case directory under the repository's <c>conformance</c> folder, and the files
/// that make a case what it is.
/// </summary>
/// <remarks>
/// A case is a directory holding <c>case.json</c> and <c>page.html</c>, plus whichever artifacts it
/// is checked with: a <c>reference.json</c> for the browser comparison, a <c>layout.expected.txt</c>
/// for the golden. Each test asks for the cases that have its own artifact, which is what lets a case
/// be added for one purpose without having to be captured for the other.
/// </remarks>
internal static class Corpus
{
    /// <summary>The artifact a browser reference is written to.</summary>
    public const String Reference = "reference.json";

    /// <summary>The artifact a layout golden is written to.</summary>
    public const String Golden = "layout.expected.txt";

    public static String Root { get; } = FindRoot();

    public static IEnumerable<String> With(String artifact)
    {
        var folder = Path.Combine(Root, "conformance");

        if (!Directory.Exists(folder))
        {
            return [];
        }

        return Directory.EnumerateDirectories(folder)
            .Where(d => File.Exists(Path.Combine(d, "case.json")) && File.Exists(Path.Combine(d, artifact)))
            .Select(d => d.Replace('\\', '/'))
            .OrderBy(d => d, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// Every case, whether or not it has been captured yet. This is what an update run works over -
    /// a case whose golden has never been written is exactly the one that needs writing, and asking
    /// the file to exist first would make it impossible to create.
    /// </summary>
    public static IEnumerable<String> All() => With("case.json").Where(d => File.Exists(Path.Combine(d, "page.html"))).ToList();

    public static ReferenceCase Case(String directory) =>
        ReferenceCase.Load(Path.Combine(directory, "case.json"));

    public static String PagePath(ReferenceCase request, String directory) =>
        Path.GetFullPath(Path.Combine(directory, request.Page));

    /// <summary>
    /// Reads the case's page, resolved through the engine at the width the case asks for - or at
    /// another width, when a test is asking what the page does as the window changes.
    /// </summary>
    public static async Task<EngineReader> RenderAsync(
        ReferenceCase request,
        String directory,
        Boolean withTypefaces,
        Int32? width = null,
        Double zoom = 1)
    {
        var page = PagePath(request, directory);
        var viewport = width ?? request.ViewportWidth;

        var engine = withTypefaces
            ? EngineReader.WithTypefaces(viewport, request.ViewportHeight)
            : EngineReader.WithFixedMetrics(viewport, request.ViewportHeight);

        await engine.LoadAsync(await File.ReadAllTextAsync(page), new Uri(page).AbsoluteUri);

        // Last, because setting it reflows: a test that compared a layout taken before the zoom with
        // one taken after it would be comparing the same page at two viewports and calling it a
        // property of zoom.
        if (zoom != 1)
        {
            engine.Zoom = zoom;
        }

        return engine;
    }

    private static String FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null &&
               !File.Exists(Path.Combine(directory.FullName, "Directory.Packages.props")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? AppContext.BaseDirectory;
    }
}

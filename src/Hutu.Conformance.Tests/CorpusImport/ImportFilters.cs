using System.Text.RegularExpressions;

namespace Hutu.Conformance;

/// <summary>Why a candidate page was turned away before it was ever drawn, if it was.</summary>
internal enum ImportVerdict
{
    /// <summary>The page is plain static layout, words and all, and is worth bringing in.</summary>
    Keep,

    /// <summary>The page runs script, waits for it, or reaches for something outside plain layout.</summary>
    Scripted,

    /// <summary>The page's picture depends on how big the window is, and the harness fixes one size.</summary>
    Viewport,
}

/// <summary>
/// The gate a page has to pass before it is brought in.
/// </summary>
/// <remarks>
/// Two rules, and both are ways of saying the same thing: what this suite measures is static layout, and
/// a page that asks about anything else would be measuring the harness rather than the engine.
/// <para>
/// Words are not one of the things it asks about. A page may say anything at all and still be a fair test
/// of layout, because a pair is drawn twice by the same engine with the same fonts and compared against
/// itself - the font cancels, and what is left is the layout. What words cannot do is be pinned down to
/// the pixel: their width belongs to whatever fonts the machine has, which is why the corpus the run
/// <em>asserts</em> on carries no text, and why these pages are scored rather than guarded
/// (<see cref="ImportedCorpusTests"/>). A number that moves when the fonts move is still a number worth
/// watching, and a corpus that quietly excludes every page with a word in it would say nothing at all
/// about line breaking, font sizing or the way a font metric feeds a line box.
/// </para>
/// </remarks>
internal static class ImportFilters
{
    private static readonly RegexOptions Options =
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.Singleline;

    /// <summary>
    /// The page is not plain static layout: it drives itself with script, embeds a nested document, draws
    /// through a canvas or vector surface, or pulls a picture or font across the network. Each is named,
    /// so a report can tell them apart.
    /// </summary>
    private static readonly (String What, Regex Rule)[] Scripted =
    [
        ("script", new(@"<script\b", Options)),
        ("a wait for script", new(@"reftest-wait", Options)),
        ("testharness", new(@"testharness", Options)),
        ("a nested document", new(@"<iframe\b", Options)),
        ("a canvas", new(@"<canvas\b", Options)),
        ("vector drawing", new(@"<svg\b", Options)),
        ("a video", new(@"<video\b", Options)),
        ("an audio", new(@"<audio\b", Options)),
        ("an embedded object", new(@"<object\b", Options)),
        ("an embed", new(@"<embed\b", Options)),
        ("math markup", new(@"<math\b", Options)),
        ("a picture", new(@"<img\b", Options)),
        ("keyframes", new(@"@keyframes", Options)),
        ("animation", new(@"animation\s*:", Options)),
        ("a transition", new(@"transition\s*:", Options)),
        ("something fetched by url", new(@"url\s*\(", Options)),
        ("a script file", new(@"\.js\b", Options)),
    ];

    private static readonly Regex ViewportUnit = new(@"\d(\.\d+)?(vw|vh|vmin|vmax)\b", Options);
    private static readonly Regex ViewportMedia = new(@"@media[^{]*\b(width|height)\b", Options);

    /// <summary>Judges one page, from its own text, and says why it was kept or what it failed.</summary>
    public static ImportVerdict Judge(String html) => Judge(html, out _);

    /// <summary>
    /// Judges a page and names what it reaches for when it reaches for something. One pass over the rules
    /// rather than two: the importer reads tens of thousands of pages, and asking the same question of
    /// each of them twice to reach an answer it already had is the difference between a run that finishes
    /// and one that does not.
    /// </summary>
    /// <param name="what">What turned the page away, when it was turned away as not plain layout.</param>
    public static ImportVerdict Judge(String html, out String? what)
    {
        foreach (var (name, rule) in Scripted)
        {
            if (rule.IsMatch(html))
            {
                what = name;
                return ImportVerdict.Scripted;
            }
        }

        what = null;

        if (ViewportUnit.IsMatch(html) || ViewportMedia.IsMatch(html))
        {
            return ImportVerdict.Viewport;
        }

        return ImportVerdict.Keep;
    }
}

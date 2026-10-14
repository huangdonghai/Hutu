using System.Text.RegularExpressions;

namespace Hutu.Reference;

/// <summary>The page as the browser read it, and what was taken out to get there.</summary>
public sealed record NeutralizedPage(String Html, Int32 Scripts, Int32 Handlers, Int32 Refreshes, Int32 Noscript)
{
    /// <summary>
    /// Writes the copy beside the original, where its relative addresses still reach their assets.
    /// </summary>
    public String WriteNextTo(String originalPath)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(originalPath)) ?? ".";
        var name = Path.GetFileNameWithoutExtension(originalPath);
        var path = Path.Combine(directory, name + ".nojs.html");
        File.WriteAllText(path, Html);
        return path;
    }
}

/// <summary>
/// Takes the scripts out of a document so that a browser reads it the way a static renderer does.
/// </summary>
/// <remarks>
/// The collector cannot simply turn JavaScript off in the browser: measuring a page is itself a script,
/// and a context with <c>javaScriptEnabled: false</c> answers nothing. So the page is disarmed instead
/// of the browser - every script element loses its source and its body, every inline handler and every
/// refresh meta loses its attribute - and the copy that goes to the browser can run the one script
/// asked of it and no other.
/// <para>
/// The counts are carried into the reference rather than dropped, because the whole value of this
/// file is that a reader can see what was taken out: a page whose layout its own script decides will
/// report a document that neither a browser with scripts nor this renderer would produce, and that is
/// a fact about the reference, not about the engine.
/// </para>
/// <para>
/// A <c>&lt;noscript&gt;</c> block is counted but left alone. It is the one place where a browser that
/// runs scripts and a reader that runs none genuinely disagree about what the document says, and the
/// count is what tells a reviewer whether the case they are looking at has that in it.
/// </para>
/// </remarks>
public static class PageNeutralizer
{
    private static readonly Regex ScriptBlock = new(
        @"<script(?<attrs>[^>]*)>(?<body>.*?)</script>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex ScriptTag = new(
        @"<script(?<attrs>[^>]*)>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex Handler = new(
        @"\s+on[a-z-]+\s*=\s*(?<quote>""[^""]*""|'[^']*'|[^\s""'=<>`]+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex Refresh = new(
        @"<meta\b[^>]*http-equiv\s*=\s*(?<quote>""refresh""|'refresh'|refresh\b)[^>]*>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex Noscript = new(
        @"<noscript\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static NeutralizedPage Of(String html)
    {
        var scripts = 0;
        var refreshed = 0;
        var handlers = 0;

        // The paired form first, so a script's body goes with it; then any opening tag left over, which
        // is a document whose script was never closed - malformed enough that the parser's own recovery
        // is the only thing that knows where it ends, and the last thing to be trusted with running it.
        html = ScriptBlock.Replace(html, match =>
        {
            scripts++;
            return "<script data-hutu-inert=\"1\"></script>";
        });

        html = ScriptTag.Replace(html, match =>
        {
            scripts++;
            return "<script data-hutu-inert=\"1\">";
        });

        html = Refresh.Replace(html, match =>
        {
            refreshed++;
            return string.Empty;
        });

        html = Handler.Replace(html, match =>
        {
            handlers++;
            return string.Empty;
        });

        return new NeutralizedPage(
            html,
            scripts,
            handlers,
            refreshed,
            Noscript.Matches(html).Count);
    }
}

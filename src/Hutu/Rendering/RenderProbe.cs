using System;
using System.Diagnostics;

namespace Hutu.Rendering;

/// <summary>
/// Temporary: what the last <see cref="RenderingPipeline.Build"/> spent where, so a probe can see a
/// whole render rather than the three stages a test happens to call by hand. A test that reproduces
/// the pipeline itself misses the layout-reuse decision, and that decision is the whole reason a
/// pointer move can change what is hovered without laying the document out again.
/// </summary>
/// <remarks>
/// The four stages are the ones a build has; drawing the paint list is the backend's and is not
/// measured here. Every time is wall clock, and a stage that was skipped reads as zero.
/// </remarks>
public static class RenderProbe
{
    public static Double StyleMs;

    public static Double BoxesMs;

    public static Double LayoutMs;

    public static Double PaintMs;

    public static Double TotalMs;

    /// <summary>
    /// Gets whether this build kept the previous layout instead of laying out again. When it did,
    /// <see cref="LayoutMs"/> is the cost of the decision to reuse - building the fresh tree and
    /// comparing the two - and not the cost of a layout.
    /// </summary>
    public static Boolean ReusedLayout;

    /// <summary>
    /// Temporary: whether a build is being watched. Off by default, so the switch is the only thing a
    /// build pays for. A probe test turns it on.
    /// </summary>
    public static Boolean Off = true;

    private static readonly Stopwatch Watch = new();

    /// <summary>Temporary: starts measuring a build.</summary>
    public static void Begin()
    {
        StyleMs = 0;
        BoxesMs = 0;
        LayoutMs = 0;
        PaintMs = 0;
        TotalMs = 0;
        ReusedLayout = false;

        if (Off)
        {
            return;
        }

        Watch.Restart();
    }

    /// <summary>Temporary: closes the style stage, which is where the render tree is built.</summary>
    public static void Style() => Close(ref StyleMs);

    /// <summary>Temporary: closes the box tree stage.</summary>
    public static void Boxes() => Close(ref BoxesMs);

    /// <summary>Temporary: closes the layout stage, which on a reused build is the reuse decision.</summary>
    public static void Layout() => Close(ref LayoutMs);

    /// <summary>Temporary: closes the paint stage, the last a build has.</summary>
    public static void Paint()
    {
        Close(ref PaintMs);
        Watch.Stop();
        TotalMs = StyleMs + BoxesMs + LayoutMs + PaintMs;
    }

    private static void Close(ref Double target)
    {
        // The watch is only running when Begin was allowed to start it, so an unwatched build leaves
        // every stage at zero rather than picking up where the last watched one left off.
        if (!Watch.IsRunning)
        {
            return;
        }

        Watch.Stop();
        target = Watch.Elapsed.TotalMilliseconds;
        Watch.Restart();
    }
}

using Hutu.Rendering.Boxes;
using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace Hutu.Rendering.Layout;

/// <summary>Temporary: one intrinsic measurement the engine was asked for.</summary>
public readonly record struct IntrinsicAsk(
    Box Box,
    Double Width,
    Double Height,
    Boolean Content,
    String Site);

/// <summary>
/// Temporary: which formatting context the engine dispatched a box to, so a probe can see where the
/// time went rather than only that a measure or an arrange happened.
/// </summary>
public enum ProbeContext
{
    Block,
    Replaced,
    Flex,
    Grid,
    Table,
}

// Temporary: counts what a layout pass asks for, so a probe can see where its time goes.
public static class LayoutProbe
{
    public static Int64 MeasureCalls;

    public static Int64 MeasureHits;

    public static Int64 IntrinsicCalls;

    public static Int64 ContentIntrinsicCalls;

    public static Int64 ArrangeCalls;

    public static Double MeasureMs;

    public static Double ArrangeMs;

    public static Double InlineMs;

    public static Int64 InlineCalls;

    public static Double IntrinsicMs;

    public static Int64 IntrinsicHits;

    /// <summary>Temporary: every intrinsic measurement asked for, in call order, with the basis it was
    /// asked at and the call site that asked - so a probe can see which dimension of the cache key is
    /// splitting one box's work into several entries, and who is varying it.</summary>
    public static readonly List<IntrinsicAsk> IntrinsicAsks = new();

    /// <summary>
    /// Temporary: the time each formatting context spent on its own work, indexed by
    /// <see cref="ProbeContext"/>. The time a context delegated to another is charged to the other,
    /// so these add up to the work done rather than counting a nested grid inside its parent's block
    /// measurement as well.
    /// </summary>
    public static readonly Double[] ContextMs = new Double[5];

    /// <summary>Temporary: how many times each formatting context was entered.</summary>
    public static readonly Int64[] ContextCalls = new Int64[5];

    /// <summary>
    /// Temporary: whether the probe does any of its work. Off by default, so a layout that nobody is
    /// watching pays one static read per call rather than the counters, the stopwatches and the stack
    /// the context timers keep. A probe test turns it on.
    /// </summary>
    public static Boolean Off = true;

    /// <summary>
    /// Temporary: the formatting context innermost on the stack, or null outside every one. Read by
    /// the style probe, which charges a declaration read to whichever context asked for it.
    /// </summary>
    public static ProbeContext? CurrentContext =>
        !Off && ContextStack.Count > 0 ? ContextStack[^1] : null;

    private static readonly Stopwatch MeasureWatch = new();
    private static readonly Stopwatch ArrangeWatch = new();
    private static readonly Stopwatch InlineWatch = new();
    private static readonly Stopwatch IntrinsicWatch = new();
    private static readonly Stopwatch ContextWatch = new();
    private static readonly List<ProbeContext> ContextStack = new();
    private static Int32 _inlineDepth;
    private static Int32 _intrinsicDepth;
    private static Int32 _measureDepth;
    private static Int32 _arrangeDepth;

    public static void Reset()
    {
        MeasureCalls = 0;
        MeasureHits = 0;
        IntrinsicCalls = 0;
        ContentIntrinsicCalls = 0;
        ArrangeCalls = 0;
        MeasureMs = 0;
        ArrangeMs = 0;
        InlineMs = 0;
        InlineCalls = 0;
        IntrinsicMs = 0;
        IntrinsicHits = 0;
        IntrinsicAsks.Clear();

        for (var index = 0; index < ContextMs.Length; index++)
        {
            ContextMs[index] = 0;
            ContextCalls[index] = 0;
        }

        ContextStack.Clear();
        ContextWatch.Reset();

        _measureDepth = 0;
        _arrangeDepth = 0;
        _inlineDepth = 0;
        _intrinsicDepth = 0;
    }

    /// <summary>Temporary: counts a measure the engine was asked for.</summary>
    public static void CountMeasure()
    {
        if (!Off)
        {
            MeasureCalls++;
        }
    }

    /// <summary>Temporary: counts a measure the cache answered.</summary>
    public static void CountMeasureHit()
    {
        if (!Off)
        {
            MeasureHits++;
        }
    }

    /// <summary>Temporary: counts an arrange the engine was asked for.</summary>
    public static void CountArrange()
    {
        if (!Off)
        {
            ArrangeCalls++;
        }
    }

    /// <summary>Temporary: counts an intrinsic measurement the engine was asked for.</summary>
    public static void CountIntrinsic()
    {
        if (!Off)
        {
            IntrinsicCalls++;
        }
    }

    /// <summary>Temporary: counts a content-intrinsic measurement the engine was asked for.</summary>
    public static void CountContentIntrinsic()
    {
        if (!Off)
        {
            ContentIntrinsicCalls++;
        }
    }

    /// <summary>Temporary: counts an intrinsic measurement the cache answered.</summary>
    public static void CountIntrinsicHit()
    {
        if (!Off)
        {
            IntrinsicHits++;
        }
    }

    /// <summary>
    /// Temporary: opens a formatting context's span. The one watch is stopped and its elapsed time
    /// charged to the context that was innermost until now, so a grid's total is its own arithmetic
    /// and not the block measurement of the items it delegated to. Time spent outside every context
    /// is charged to nobody.
    /// </summary>
    public static void EnterContext(ProbeContext context)
    {
        if (Off)
        {
            return;
        }

        FlushContext();
        ContextCalls[(Int32)context]++;
        ContextStack.Add(context);
        ContextWatch.Restart();
    }

    /// <summary>Temporary: closes the innermost formatting context's span.</summary>
    public static void LeaveContext()
    {
        if (Off)
        {
            return;
        }

        FlushContext();

        if (ContextStack.Count > 0)
        {
            ContextStack.RemoveAt(ContextStack.Count - 1);
        }

        ContextWatch.Restart();
    }

    private static void FlushContext()
    {
        if (!ContextWatch.IsRunning)
        {
            return;
        }

        ContextWatch.Stop();

        if (ContextStack.Count > 0)
        {
            ContextMs[(Int32)ContextStack[^1]] += ContextWatch.Elapsed.TotalMilliseconds;
        }
    }

    /// <summary>Temporary: whether the ask log is being kept. Reading the call site costs a stack
    /// walk, so this stays off unless a probe is looking at it.</summary>
    public static Boolean RecordAsks;

    /// <summary>Temporary: records one intrinsic measurement the engine was asked for.</summary>
    public static void NoteIntrinsicKey(Box box, Double width, Double height, Boolean content)
    {
        if (!Off && RecordAsks)
        {
            IntrinsicAsks.Add(new IntrinsicAsk(box, width, height, content, Site()));
        }
    }

    /// <summary>The first frame past the engine's own entry points, i.e. the context that asked.</summary>
    private static String Site()
    {
        foreach (var frame in new StackTrace(2, false).GetFrames())
        {
            var method = frame.GetMethod();
            var name = method?.DeclaringType?.Name;

            if (name is null or nameof(LayoutEngine) or nameof(LayoutProbe))
            {
                continue;
            }

            return $"{name}.{method!.Name}";
        }

        return "?";
    }

    public static void EnterIntrinsic(Box box)
    {
        if (Off)
        {
            return;
        }

        if (_intrinsicDepth++ == 0)
        {
            IntrinsicWatch.Restart();
        }
    }

    public static void LeaveIntrinsic()
    {
        if (Off)
        {
            return;
        }

        if (--_intrinsicDepth == 0)
        {
            IntrinsicWatch.Stop();
            IntrinsicMs += IntrinsicWatch.Elapsed.TotalMilliseconds;
        }
    }

    public static void EnterInline()
    {
        if (Off)
        {
            return;
        }

        InlineCalls++;

        if (_inlineDepth++ == 0)
        {
            InlineWatch.Restart();
        }
    }

    public static void LeaveInline()
    {
        if (Off)
        {
            return;
        }

        if (--_inlineDepth == 0)
        {
            InlineWatch.Stop();
            InlineMs += InlineWatch.Elapsed.TotalMilliseconds;
        }
    }

    public static void EnterMeasure()
    {
        if (Off)
        {
            return;
        }

        if (_measureDepth++ == 0)
        {
            MeasureWatch.Restart();
        }
    }

    public static void LeaveMeasure()
    {
        if (Off)
        {
            return;
        }

        if (--_measureDepth == 0)
        {
            MeasureWatch.Stop();
            MeasureMs += MeasureWatch.Elapsed.TotalMilliseconds;
        }
    }

    public static void EnterArrange()
    {
        if (Off)
        {
            return;
        }

        if (_arrangeDepth++ == 0)
        {
            ArrangeWatch.Restart();
        }
    }

    public static void LeaveArrange()
    {
        if (Off)
        {
            return;
        }

        if (--_arrangeDepth == 0)
        {
            ArrangeWatch.Stop();
            ArrangeMs += ArrangeWatch.Elapsed.TotalMilliseconds;
        }
    }
}

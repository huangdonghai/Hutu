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

    public static Int64 IntrinsicUnique;

    /// <summary>Temporary: every intrinsic measurement asked for, in call order, with the basis it was
    /// asked at and the call site that asked - so a probe can see which dimension of the cache key is
    /// splitting one box's work into several entries, and who is varying it.</summary>
    public static readonly List<IntrinsicAsk> IntrinsicAsks = new();

    private static readonly Stopwatch MeasureWatch = new();
    private static readonly Stopwatch ArrangeWatch = new();
    private static readonly Stopwatch InlineWatch = new();
    private static readonly Stopwatch IntrinsicWatch = new();
    private static readonly HashSet<Box> IntrinsicSeen = new();
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
        IntrinsicUnique = 0;
        IntrinsicSeen.Clear();
        IntrinsicAsks.Clear();
        _measureDepth = 0;
        _arrangeDepth = 0;
        _inlineDepth = 0;
        _intrinsicDepth = 0;
    }

    /// <summary>Temporary: whether the ask log is being kept. Reading the call site costs a stack
    /// walk, so this stays off unless a probe is looking at it.</summary>
    public static Boolean RecordAsks;

    /// <summary>Temporary: records one intrinsic measurement the engine was asked for.</summary>
    public static void NoteIntrinsicKey(Box box, Double width, Double height, Boolean content)
    {
        if (RecordAsks)
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
        if (IntrinsicSeen.Add(box))
        {
            IntrinsicUnique++;
        }

        if (_intrinsicDepth++ == 0)
        {
            IntrinsicWatch.Restart();
        }
    }

    public static void LeaveIntrinsic()
    {
        if (--_intrinsicDepth == 0)
        {
            IntrinsicWatch.Stop();
            IntrinsicMs += IntrinsicWatch.Elapsed.TotalMilliseconds;
        }
    }

    public static void EnterInline()
    {
        InlineCalls++;

        if (_inlineDepth++ == 0)
        {
            InlineWatch.Restart();
        }
    }

    public static void LeaveInline()
    {
        if (--_inlineDepth == 0)
        {
            InlineWatch.Stop();
            InlineMs += InlineWatch.Elapsed.TotalMilliseconds;
        }
    }

    public static void EnterMeasure()
    {
        if (_measureDepth++ == 0)
        {
            MeasureWatch.Restart();
        }
    }

    public static void LeaveMeasure()
    {
        if (--_measureDepth == 0)
        {
            MeasureWatch.Stop();
            MeasureMs += MeasureWatch.Elapsed.TotalMilliseconds;
        }
    }

    public static void EnterArrange()
    {
        if (_arrangeDepth++ == 0)
        {
            ArrangeWatch.Restart();
        }
    }

    public static void LeaveArrange()
    {
        if (--_arrangeDepth == 0)
        {
            ArrangeWatch.Stop();
            ArrangeMs += ArrangeWatch.Elapsed.TotalMilliseconds;
        }
    }
}

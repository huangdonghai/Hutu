using Hutu.Rendering.Layout;
using System.Collections.Generic;
using System;

namespace Hutu.Rendering;

/// <summary>
/// Temporary: how often the read layer reads a declaration, and who asked. A layout pass asks the
/// same box for its padding over and over - once per phase, and again in each formatting context it
/// takes part in - and every one of those reads parses the cascaded declaration from scratch.
/// </summary>
/// <remarks>
/// This counts rather than times. A read is a dictionary lookup and a string compare, on the order of
/// a hundred nanoseconds, while a stopwatch pair around it costs a fifth of that; timing each read
/// would perturb the thing being measured by more than the difference the caller is trying to find.
/// The caller multiplies these counts by a separately measured cost per read instead.
/// </remarks>
public static class StyleProbe
{
    /// <summary>Temporary: logical length reads, one per length the read layer was asked for.</summary>
    public static Int64 LengthReads;

    /// <summary>
    /// Temporary: of those, the reads that found a declared value. The rest are a property nobody
    /// declared, which costs two failed lookups and yields the fallback.
    /// </summary>
    public static Int64 LengthHits;

    /// <summary>Temporary: reads that materialise the declaration's text, which allocates a string.</summary>
    public static Int64 TextReads;

    /// <summary>Temporary: reads that go straight to the typed value, which does not.</summary>
    public static Int64 ValueReads;

    /// <summary>
    /// Temporary: reads per property name, one map per kind. A count per declaration read says how
    /// much the read layer works; this says which properties it keeps asking for, which is what decides
    /// whether remembering one is worth the field.
    /// </summary>
    public static readonly Dictionary<String, Int64> LengthByName = new(StringComparer.Ordinal);

    public static readonly Dictionary<String, Int64> TextByName = new(StringComparer.Ordinal);

    public static readonly Dictionary<String, Int64> ValueByName = new(StringComparer.Ordinal);

    /// <summary>Temporary: the same counts charged to the innermost formatting context that asked.</summary>
    public static readonly Int64[] ContextLengthReads = new Int64[5];

    public static readonly Int64[] ContextTextReads = new Int64[5];

    public static readonly Int64[] ContextValueReads = new Int64[5];

    public static void Reset()
    {
        LengthReads = 0;
        LengthHits = 0;
        TextReads = 0;
        ValueReads = 0;

        Array.Clear(ContextLengthReads);
        Array.Clear(ContextTextReads);
        Array.Clear(ContextValueReads);

        LengthByName.Clear();
        TextByName.Clear();
        ValueByName.Clear();
    }

    /// <summary>
    /// Temporary: whether the counters are kept. Off by default, so a pass nobody is watching pays one
    /// static read per declaration read and nothing else. A probe test turns it on - and turns it off
    /// again while it times a single read, because an instrument inside the thing it measures reports
    /// its own price.
    /// </summary>
    public static Boolean Off = true;

    /// <summary>Temporary: records one logical length read.</summary>
    public static void NoteLengthRead(String name) => Note(name, LengthByName, ref LengthReads, ContextLengthReads);

    /// <summary>Temporary: records one length read that found a value.</summary>
    public static void NoteLengthHit()
    {
        if (!Off)
        {
            LengthHits++;
        }
    }

    /// <summary>Temporary: records one read that materialised the declaration's text.</summary>
    public static void NoteTextRead(String name) => Note(name, TextByName, ref TextReads, ContextTextReads);

    /// <summary>Temporary: records one read of a typed value.</summary>
    public static void NoteValueRead(String name) => Note(name, ValueByName, ref ValueReads, ContextValueReads);

    private static void Note(String name, Dictionary<String, Int64> byName, ref Int64 total, Int64[] byContext)
    {
        if (Off)
        {
            return;
        }

        total++;
        byName[name] = byName.TryGetValue(name, out var count) ? count + 1 : 1;

        // Attribution is best-effort in one direction only: a read raised outside every formatting
        // context - from the painter, or from the engine's own bookkeeping - lands in the total and
        // in no context, which is why the two do not have to add up.
        if (LayoutProbe.CurrentContext is { } context)
        {
            byContext[(Int32)context]++;
        }
    }
}

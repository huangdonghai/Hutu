using AngleSharp.Css.Dom;
using Hutu.Rendering.Boxes;
using System.Buffers;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;

using System.Globalization;

namespace Hutu.Rendering.Layout;

internal sealed partial class InlineLayout
{
    /// <summary>
    /// The previous character a line has already set, with everything the collapse rules ask of it:
    /// its class, whether it is still full-width, the size it stands at, the value its own style
    /// computed, and whether it was already trimmed.
    /// </summary>
    private readonly record struct PreviousText(
        TrimNeighbor Neighbor,
        ComputedStyle Style,
        Int32 CodePoint,
        Boolean Trimmed,
        WritingModeInfo Flow,
        Int32 Before);

    /// <summary>
    /// What one range of text contributes to a line's trims: the mask to draw with, the room the
    /// first character gives up because of where the line put it, and the room the character before
    /// the range gives up because this range begins with a punctuation beside it.
    /// </summary>
    private readonly record struct TrimStep(
        Double BoundaryDelta,
        Double RetroDelta,
        Char RetroCode);

    /// <summary>
    /// Gets the room the punctuation inside a range gives up for its neighbors. Only characters with
    /// both neighbors inside the range are counted: the two ends belong to whatever stands beside
    /// them on the line, which the line builder settles when it knows what that is.
    /// </summary>
    private static Double TrimWithin(
        ITextMeasurer measurer,
        ComputedStyle style,
        String source,
        Int32 start,
        Int32 length,
        WritingModeInfo flow)
    {
        var value = style.TextSpacingTrim;

        if (length <= 0 ||
            TextSpacingTrim.HasExclusiveFeature(style.ResolvedFont.Features) ||
            (!value.All && !value.Adjacent))
        {
            return 0;
        }

        var total = 0.0;
        var end = start + length;

        for (var index = start; index < end;)
        {
            var codePoint = TextChunker.CodePointAt(source, index);
            var take = Char.IsSurrogatePair(source, index) ? 2 : 1;

            if (!TextSpacingTrim.IsCandidate(codePoint))
            {
                index += take;
                continue;
            }

            var kind = KindOf(style, codePoint);

            if (kind != PunctuationClass.None && IsFullwidthPunctuation(measurer, style, codePoint, flow))
            {
                var trimmed = value.All;

                if (!trimmed && value.Adjacent)
                {
                    if (kind == PunctuationClass.Opening && index > start)
                    {
                        trimmed = TextSpacingTrim.TrimsOpening(
                            NeighborOf(measurer, style, CodePointBefore(source, index), flow),
                            style.FontSize);
                    }
                    else if (kind == PunctuationClass.Closing && index + take < end)
                    {
                        trimmed = TextSpacingTrim.TrimsClosing(
                            NeighborOf(measurer, style, TextChunker.CodePointAt(source, index + take), flow),
                            style.FontSize);
                    }
                }

                if (trimmed)
                {
                    total += TrimDelta(measurer, style, codePoint, flow);
                }
            }

            index += take;
        }

        return total;
    }

    /// <summary>The codes an autospaced boundary wears in a fragment's autospace mask.</summary>
    private const Char AutoSpaceAfter = '\u0001';

    private const Char AutoSpaceReplace = '\u0002';

    /// <summary>What one range of text contributes to a line's autospacing.</summary>
    private readonly record struct AutoSpaceStep(
        Double BoundaryDelta,
        Double Room,
        Double RetroDelta,
        Boolean Inside);

    /// <summary>
    /// Gets the room the property adds between the characters inside a range. Only characters with
    /// both neighbors inside the range are counted: the two ends belong to whatever stands beside
    /// them on the line, which the line builder settles when it knows what that is.
    /// </summary>
    private static Double AutoSpaceWithin(
        ITextMeasurer measurer,
        ComputedStyle style,
        String source,
        Int32 start,
        Int32 length,
        WritingModeInfo flow)
    {
        var value = style.TextAutospace;

        if (value.Off || length <= 0)
        {
            return 0;
        }

        var space = AutoSpaceWidth(measurer, style, flow);

        if (space <= 0)
        {
            return 0;
        }

        var total = 0.0;
        var end = start + length;

        for (var index = start; index < end;)
        {
            // Stepped by cluster: a base with the marks and selectors that stand on it is one
            // character, and a boundary falls between characters rather than inside one.
            var next = index + TextClusters.UnitsAt(source, index, end);

            if (next < end)
            {
                total += PairRoom(measurer, style, value, source, index, next, end, flow, space, out _);
            }

            index = next;
        }

        return total;
    }

    /// <summary>
    /// Plans the autospacing of one range the line is about to add: the room the boundary with the
    /// character already standing on the line adds, and the mask the run draws with.
    /// </summary>
    /// <remarks>
    /// A boundary's room is added to the room the range was measured at, and no mask is needed for it:
    /// the fragments stand side by side on the line, so the room between them is a gap the boxes
    /// already leave. It is the boundaries inside one run that need the mask, because a run's glyphs
    /// are set one after another by its own shaping.
    /// </remarks>
    private static AutoSpaceStep PlanAutoSpace(
        ITextMeasurer measurer,
        String source,
        Int32 start,
        Int32 length,
        ComputedStyle style,
        WritingModeInfo flow,
        PreviousText? previous,
        List<MaskMark>? mask)
    {
        var value = style.TextAutospace;

        if (value.Off || length <= 0)
        {
            return default;
        }

        var space = AutoSpaceWidth(measurer, style, flow);

        if (space <= 0)
        {
            return default;
        }

        var boundary = 0.0;
        var retro = 0.0;
        var inside = false;
        var end = start + length;

        // A range that begins with a mark or a selector continues the character before it, so the
        // boundary is not between the two runs but after the character this one completes: the pair
        // the property reads stands between the base before and the first character of this range
        // that begins one of its own.
        var pairAt = start;

        while (pairAt < end && TextClusters.Continues(source, pairAt))
        {
            pairAt += TextClusters.UnitsAt(source, pairAt, end);
        }

        if (previous is { } last && pairAt < end)
        {
            var room = RoomBetween(
                measurer,
                style,
                value,
                last.CodePoint,
                last.Flow,
                TextChunker.CodePointAt(source, pairAt),
                flow,
                space);

            if (value.Replace &&
                pairAt == start &&
                last.CodePoint == ' ' &&
                last.Before != 0 &&
                RoomBetween(
                    measurer,
                    style,
                    value,
                    last.Before,
                    last.Flow,
                    TextChunker.CodePointAt(source, pairAt),
                    flow,
                    space) is var replaced and > 0)
            {
                // The space the run before this one ends with is where the boundary stands: it takes
                // the room the boundary is worth and gives up its own.
                retro = measurer.MeasureWidth(" ", last.Style.GetFont()) - replaced;
            }
            else if (room > 0)
            {
                boundary = room;

                if (pairAt > start)
                {
                    // The room stands after the character this range completes, inside the run it is
                    // drawn with rather than in the gap before it.
                    inside = true;

                    if (mask is not null)
                    {
                        // The character this range completes is the cluster its first units make, so
                        // the room is marked on that cluster - the one the range opens with.
                        mask.Add(new MaskMark(start, AutoSpaceAfter));
                    }
                }
            }
        }

        if (mask is not null)
        {

            // A space that opens the range is a boundary the caller may keep between two runs; a
            // replace value takes its room, so the space stands in the cell the boundary is worth
            // rather than its own.
            var first = TextChunker.UnitsAt(source, start);

            if (value.Replace && start < end && source[start] == ' ' && start + first < end &&
                previous is { } spaced &&
                RoomBetween(
                    measurer,
                    style,
                    value,
                    spaced.CodePoint,
                    spaced.Flow,
                    TextChunker.CodePointAt(source, start + first),
                    flow,
                    space) is var replaced and > 0)
            {
                mask.Add(new MaskMark(start, AutoSpaceReplace));
                boundary -= measurer.MeasureWidth(" ", style.GetFont()) - replaced;
            }

            for (var index = start; index < end;)
            {
                var next = index + TextClusters.UnitsAt(source, index, end);

                if (next < end)
                {
                    PairRoom(measurer, style, value, source, index, next, end, flow, space, out var code);

                    if (code == AutoSpaceAfter)
                    {
                        mask.Add(new MaskMark(index, code));
                    }
                    else if (code == AutoSpaceReplace)
                    {
                        mask.Add(new MaskMark(next, code));
                    }
                }

                index = next;
            }
        }

        return new AutoSpaceStep(boundary, space, retro, inside);
    }

    /// <summary>
    /// Gets the room one pair of neighboring characters adds, and the mask code that draws it:
    /// <see cref="AutoSpaceAfter"/> on the left character, or <see cref="AutoSpaceReplace"/> on the
    /// space a <c>replace</c> value takes the place of.
    /// </summary>
    private static Double PairRoom(
        ITextMeasurer measurer,
        ComputedStyle style,
        TextAutospaceValue value,
        String source,
        Int32 leftAt,
        Int32 rightAt,
        Int32 end,
        WritingModeInfo flow,
        Double space,
        out Char code)
    {
        code = '\0';

        var room = RoomBetween(
            measurer,
            style,
            value,
            TextChunker.CodePointAt(source, leftAt),
            flow,
            TextChunker.CodePointAt(source, rightAt),
            flow,
            space);

        if (room > 0)
        {
            code = AutoSpaceAfter;
            return room;
        }

        if (value.Replace && source[rightAt] == ' ' && rightAt + 1 < end)
        {
            var replaced = RoomBetween(
                measurer,
                style,
                value,
                TextChunker.CodePointAt(source, leftAt),
                flow,
                TextChunker.CodePointAt(source, rightAt + 1),
                flow,
                space);

            if (replaced > 0)
            {
                code = AutoSpaceReplace;
                return replaced - measurer.MeasureWidth(" ", style.GetFont());
            }
        }

        return 0;
    }

    /// <summary>
    /// Gets whether the property separates this pair of characters: the classes it names, or the
    /// no-break space the language's typography puts around a punctuation.
    /// </summary>
    /// <summary>
    /// Gets the room one boundary between two characters adds: the eighth of the CJK advance the
    /// property names, or the width of the no-break space the language's typography puts beside a
    /// punctuation.
    /// </summary>
    private static Double RoomBetween(
        ITextMeasurer measurer,
        ComputedStyle style,
        TextAutospaceValue value,
        Int32 left,
        WritingModeInfo leftFlow,
        Int32 right,
        WritingModeInfo rightFlow,
        Double space)
    {
        if (TextAutospace.ShouldSpace(
            value,
            TextAutospace.ClassOf(left, IsSetUpright(leftFlow, left)),
            TextAutospace.ClassOf(right, IsSetUpright(rightFlow, right))))
        {
            return space;
        }

        return TextAutospace.Punctuation(value, style.Language, right) == PunctuationSpace.Before ||
               TextAutospace.Punctuation(value, style.Language, left) == PunctuationSpace.After
            ? measurer.MeasureWidth("\u202F", style.GetFont())
            : 0;
    }

    /// <summary>
    /// Gets whether a character is set upright by the column it stands in, by
    /// <c>text-orientation: upright</c> or by <c>text-combine-upright</c> - neither of which is a
    /// neighbor the property spaces (CSS Text 4 §8.4).
    /// </summary>
    private static Boolean IsSetUpright(WritingModeInfo flow, Int32 codePoint) =>
        flow.IsVertical && (flow.CombinesUpright || flow.IsUpright(codePoint));

    /// <summary>Gets the room one boundary is widened by: an eighth of the font's CJK advance.</summary>
    private static Double AutoSpaceWidth(
        ITextMeasurer measurer, ComputedStyle style, WritingModeInfo flow)
    {
        var font = style.GetFont();
        var ideograph = flow.IsVertical
            ? measurer.MeasureUprightAdvance(0x6C34, font)
            : measurer.MeasureWidth("\u6C34", font);

        return ideograph > 0 ? ideograph * TextAutospace.SpaceFraction : 0;
    }

    /// <summary>
    /// Gets the room one autospaced boundary asks for, where the two characters stand side by side on
    /// the one line the content needs.
    /// </summary>
    private static Double AutoSpaceBetween(
        ITextMeasurer measurer, ComputedStyle style, Int32 left, Int32 right, WritingModeInfo flow)
    {
        var value = style.TextAutospace;

        if (value.Off)
        {
            return 0;
        }

        var space = AutoSpaceWidth(measurer, style, flow);

        return space <= 0 ? 0 : RoomBetween(measurer, style, value, left, flow, right, flow, space);
    }

    /// <summary>
    /// Plans the trims of one range the line is about to add: the mask it will draw with, the room
    /// its first character gives up at the line's start or beside the character already standing
    /// there, and the room the previous character gives up because this range begins beside it.
    /// </summary>
    private static TrimStep PlanTrim(
        ITextMeasurer measurer,
        String source,
        Int32 start,
        Int32 length,
        ComputedStyle style,
        WritingModeInfo flow,
        PreviousText? previous,
        Boolean lineEmpty,
        Boolean startsParagraph,
        List<MaskMark>? mask)
    {
        if (length <= 0)
        {
            return default;
        }

        var value = style.TextSpacingTrim;
        var boundary = 0.0;
        var enabled = !TextSpacingTrim.HasExclusiveFeature(style.ResolvedFont.Features);

        // The first character stands where the range cannot see a neighbor: at the line's start, or
        // beside what the line already holds. A value that trims everywhere already takes its room
        // in the measurement, so only the positioned modes are read here.
        var firstCodePoint = TextChunker.CodePointAt(source, start);
        var firstKind = TextSpacingTrim.IsCandidate(firstCodePoint)
            ? KindOf(style, firstCodePoint)
            : PunctuationClass.None;

        if (enabled &&
            !value.All &&
            firstKind != PunctuationClass.None &&
            IsFullwidthPunctuation(measurer, style, firstCodePoint, flow))
        {
            var trimmed = lineEmpty
                ? TextSpacingTrim.TrimsAtLineStart(value, firstKind, startsParagraph)
                : value.Adjacent &&
                  firstKind == PunctuationClass.Opening &&
                  previous is { } left &&
                  TextSpacingTrim.TrimsOpening(left.Neighbor, style.FontSize);

            if (trimmed)
            {
                if (mask is not null)
                {
                    mask.Add(new MaskMark(start, CodeOf(firstKind)));
                }

                boundary = TrimDelta(measurer, style, firstCodePoint, flow);
            }
        }

        var retro = 0.0;
        var retroCode = '\0';

        if (!lineEmpty &&
            previous is { Trimmed: false, Neighbor.Class: PunctuationClass.Closing } lastText)
        {
            var previousValue = lastText.Style.TextSpacingTrim;
            var previousEnabled =
                !TextSpacingTrim.HasExclusiveFeature(lastText.Style.ResolvedFont.Features) &&
                (previousValue.All || previousValue.Adjacent);

            if (previousEnabled &&
                IsFullwidthPunctuation(measurer, lastText.Style, lastText.CodePoint, lastText.Flow) &&
                TextSpacingTrim.TrimsClosing(
                    NeighborOf(measurer, style, firstCodePoint, flow),
                    lastText.Style.FontSize))
            {
                retro = TrimDelta(measurer, lastText.Style, lastText.CodePoint, lastText.Flow);
                retroCode = TrimClosing;
            }
        }

        if (enabled && mask is not null)
        {
            var end = start + length;

            for (var index = start; index < end;)
            {
                var codePoint = TextChunker.CodePointAt(source, index);
                var take = Char.IsSurrogatePair(source, index) ? 2 : 1;

                if (!TextSpacingTrim.IsCandidate(codePoint))
                {
                    index += take;
                    continue;
                }

                var kind = KindOf(style, codePoint);

                if (kind != PunctuationClass.None && IsFullwidthPunctuation(measurer, style, codePoint, flow))
                {
                    var trimmed = value.All;

                    if (!trimmed && value.Adjacent)
                    {
                        if (kind == PunctuationClass.Opening && index > start)
                        {
                            trimmed = TextSpacingTrim.TrimsOpening(
                                NeighborOf(measurer, style, CodePointBefore(source, index), flow),
                                style.FontSize);
                        }
                        else if (kind == PunctuationClass.Closing && index + take < end)
                        {
                            trimmed = TextSpacingTrim.TrimsClosing(
                                NeighborOf(measurer, style, TextChunker.CodePointAt(source, index + take), flow),
                                style.FontSize);
                        }
                    }

                    if (trimmed)
                    {
                        mask.Add(new MaskMark(index, CodeOf(kind)));
                    }
                }

                index += take;
            }
        }

        return new TrimStep(boundary, retro, retroCode);
    }

    private MaskSpan CaptureMask(List<MaskMark> marks)
    {
        if (marks.Count == 0)
        {
            return default;
        }

        var start = _maskMarks.Count;
        _maskMarks.AddRange(marks);
        return new MaskSpan(start, marks.Count);
    }

    private void AppendMask(ref MaskSpan span, List<MaskMark> marks, Int32 offsetDelta = 0)
    {
        if (marks.Count == 0)
        {
            return;
        }

        MoveMaskToEnd(ref span);

        foreach (var mark in marks)
        {
            _maskMarks.Add(new MaskMark(mark.Offset + offsetDelta, mark.Code));
        }

        span = new MaskSpan(span.Start, span.Count + marks.Count);
    }

    private void AppendMark(ref MaskSpan span, Int32 offset, Char code)
    {
        MoveMaskToEnd(ref span);
        _maskMarks.Add(new MaskMark(offset, code));
        span = new MaskSpan(span.Start, span.Count + 1);
    }

    private void MoveMaskToEnd(ref MaskSpan span)
    {
        if (span.Count == 0)
        {
            span = new MaskSpan(_maskMarks.Count, 0);
            return;
        }

        if (span.Start + span.Count == _maskMarks.Count)
        {
            return;
        }

        var start = _maskMarks.Count;

        for (var index = 0; index < span.Count; index++)
        {
            _maskMarks.Add(_maskMarks[span.Start + index]);
        }

        span = new MaskSpan(start, span.Count);
    }

    private void RebaseMask(ref MaskSpan span, Int32 origin)
    {
        if (span.Count == 0)
        {
            return;
        }

        var start = _maskMarks.Count;

        for (var index = 0; index < span.Count; index++)
        {
            var mark = _maskMarks[span.Start + index];
            _maskMarks.Add(new MaskMark(mark.Offset - origin, mark.Code));
        }

        span = new MaskSpan(start, span.Count);
    }

    /// <summary>Gets the part of a mask one source range covers, or an empty span where it marks
    /// nothing.</summary>
    private MaskSpan SliceMask(
        MaskSpan span,
        Int32 start,
        Int32 length,
        Boolean clearBoundary,
        Int32 offsetDelta = 0)
    {
        if (span.Count == 0 || length <= 0)
        {
            return default;
        }

        var end = start + length;
        var kept = 0;

        for (var index = 0; index < span.Count; index++)
        {
            var mark = _maskMarks[span.Start + index];

            if (mark.Offset >= start &&
                mark.Offset < end &&
                !(clearBoundary && mark.Offset == end - 1 && mark.Code == AutoSpaceAfter))
            {
                kept++;
            }
        }

        if (kept == 0)
        {
            return default;
        }

        var target = _maskMarks.Count;

        for (var index = 0; index < span.Count; index++)
        {
            var mark = _maskMarks[span.Start + index];

            if (mark.Offset >= start &&
                mark.Offset < end &&
                !(clearBoundary && mark.Offset == end - 1 && mark.Code == AutoSpaceAfter))
            {
                _maskMarks.Add(new MaskMark(mark.Offset + offsetDelta, mark.Code));
            }
        }

        return new MaskSpan(target, kept);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private Char CodeAt(MaskSpan span, Int32 offset)
    {
        for (var index = span.Start + span.Count - 1; index >= span.Start; index--)
        {
            var mark = _maskMarks[index];

            if (mark.Offset == offset)
            {
                return mark.Code;
            }
        }

        return '\0';
    }

    /// <summary>Materialises the mask a painter takes, once per placed run. The line builder keeps
    /// marks sparse and in source coordinates, so a run that merges many chunks never writes out their
    /// growing shared prefix.</summary>
    private String? MaskText(MaskSpan span, Int32 start, Int32 length)
    {
        if (span.Count == 0 || length <= 0)
        {
            return null;
        }

        var end = start + length;
        var marked = false;

        for (var index = 0; index < span.Count; index++)
        {
            var offset = _maskMarks[span.Start + index].Offset;

            if (offset >= start && offset < end)
            {
                marked = true;
                break;
            }
        }

        if (!marked)
        {
            return null;
        }

        return String.Create(
            length,
            (_maskMarks, span, start, end),
            static (chars, state) =>
            {
                for (var index = 0; index < state.span.Count; index++)
                {
                    var mark = state._maskMarks[state.span.Start + index];
                    var at = mark.Offset - state.start;

                    if (at >= 0 && at < chars.Length)
                    {
                        chars[at] = mark.Code;
                    }
                }
            });
    }

    /// <summary>
    /// Gets whether the character before a place in a fragment is one a spaced boundary follows.
    /// </summary>
    private Boolean SpacedBefore(TextFragment fragment, Int32 at)
    {
        if (fragment.AutoSpaces.Count == 0 || at <= 0)
        {
            return false;
        }

        var index = at - 1;

        while (index > 0 && Char.IsLowSurrogate(fragment.Text[index]))
        {
            index--;
        }

        return CodeAt(fragment.AutoSpaces, fragment.Start + index) == AutoSpaceAfter;
    }

    private static Char CodeOf(PunctuationClass kind) => kind switch
    {
        PunctuationClass.Opening => TrimOpening,
        PunctuationClass.Closing => TrimClosing,
        PunctuationClass.Middle => TrimMiddle,
        _ => '\0',
    };

    private static PunctuationClass KindOf(ComputedStyle style, Int32 codePoint) =>
        TextSpacingTrim.Classify(codePoint, TextSpacingTrim.LanguageOf(style.Language));

    /// <summary>Gets what the collapse rules need to know about one character standing beside another.</summary>
    private static TrimNeighbor NeighborOf(
        ITextMeasurer measurer, ComputedStyle style, Int32 codePoint, WritingModeInfo? flow) =>
        new(
            KindOf(style, codePoint),
            codePoint == 0x3000,
            KindOf(style, codePoint) == PunctuationClass.Closing &&
                IsFullwidthPunctuation(measurer, style, codePoint, flow),
            style.FontSize);

    /// <summary>
    /// Gets whether the character still advances by the fullwidth one. A glyph the font already sets
    /// proportionally is simultaneously full- and half-width, and the property leaves it alone.
    /// </summary>
    private static Boolean IsFullwidthPunctuation(
        ITextMeasurer measurer, ComputedStyle style, Int32 codePoint, WritingModeInfo? flow = null)
    {
        if (TextSpacingTrim.HasExclusiveFeature(style.ResolvedFont.Features))
        {
            return false;
        }

        // Down an upright column every character owns a cell of the whole em, whatever width it has
        // along a line: the steps a column takes are floored at the em box there. A narrow Latin quote
        // stood upright in such a column is therefore as full-width down it as the ideograph beside
        // it, which is the pair the collapse rules read. Measuring its line width instead says the
        // glyph is already proportional and leaves the trim unmarked, which makes every column of
        // these pages one character too long (CSS Writing Modes 3 §4.1.2 with CSS Text 4 §4.4,
        // measured: the text-spacing-trim-*-vrl variants, where Chrome trims the U+2018 that follows
        // a U+FF09 down an upright column). A turned run is left to the rule above, since it really
        // does advance by the width the character has along a line.
        if (flow is { IsVertical: true, Orientation: TextOrientation.Upright })
        {
            return true;
        }

        var fullwidth = measurer.MeasureWidth("\u6C34", style.GetFont());

        if (fullwidth <= 0)
        {
            fullwidth = style.FontSize;
        }

        return TextSpacingTrim.IsFullwidth(
            measurer.MeasureAdvance(codePoint, style.GetFont(), flow ?? WritingModeInfo.Horizontal),
            fullwidth);
    }

    /// <summary>Gets the room one trimmed character gives up along the line or column it is set on.</summary>
    private static Double TrimDelta(
        ITextMeasurer measurer, ComputedStyle style, Int32 codePoint, WritingModeInfo flow)
    {
        if (!flow.IsVertical)
        {
            return measurer.MeasureAdvance(codePoint, style.GetFont(), flow) / 2;
        }

        var font = style.GetFont();
        var step = measurer.MeasureUprightStep(codePoint, font, flow);

        return step / 2;
    }

    /// <summary>
    /// Gets the room a closing punctuation can give up if it ends the line. The breaker keeps such a
    /// character on the line where only its trailing half fails to fit; the line's close takes the
    /// room off for real. <c>trim-end</c> always has this room; <c>allow-end</c> has it because the
    /// line does not otherwise fit.
    /// </summary>
    private static Double EndGrace(
        ITextMeasurer measurer,
        ComputedStyle style,
        String source,
        Int32 at,
        Int32 taken,
        WritingModeInfo flow)
    {
        if (taken <= 0)
        {
            return 0;
        }

        var codePoint = CodePointBefore(source, at + taken);
        var kind = KindOf(style, codePoint);
        var value = style.TextSpacingTrim;

        if (kind != PunctuationClass.Closing ||
            value.All ||
            value.End == SpacingTrimEnd.Space ||
            TextSpacingTrim.HasExclusiveFeature(style.ResolvedFont.Features) ||
            !IsFullwidthPunctuation(measurer, style, codePoint))
        {
            return 0;
        }

        return TrimDelta(measurer, style, codePoint, flow);
    }

    private static Int32 CodePointBefore(String text, Int32 index) =>
        index >= 2 && Char.IsSurrogatePair(text, index - 2)
            ? Char.ConvertToUtf32(text[index - 2], text[index - 1])
            : text[index - 1];

    /// <summary>
    /// Gets the room a stop or comma at the end of a run gives up when the line cannot otherwise hold
    /// it: <c>allow-end</c> hangs the character instead of breaking the line before it, and the line's
    /// close takes its advance off for real (CSS Text 3 §8.2.1).
    /// </summary>
    private static Double HangGrace(
        ITextMeasurer measurer,
        ComputedStyle style,
        String source,
        Int32 at,
        Int32 taken,
        Int32 runEnd,
        Boolean lastOfContent,
        WritingModeInfo flow)
    {
        if (taken <= 0 || at + taken != runEnd)
        {
            return 0;
        }

        var end = at + taken;
        var start = TextClusters.ClusterStart(source, end);
        var codePoint = TextChunker.CodePointAt(source, start);

        // The glyph the breaker may keep past the line's edge: a stop or comma under `allow-end` or
        // `force-end`, and a closing bracket or quote under `last` where this is the content's final
        // line (CSS Text 3 §8.2.1). A line the value would not hang the glyph from takes no grace.
        return HangingPunctuation.HangsAtEnd(style.HangingPunctuation, codePoint, overflows: true, lastOfContent)
            ? MeasureChunk(measurer, style, source, start, end - start, flow)
            : 0;
    }

    /// <summary>
    /// Gets how many times the run's letter spacing is charged along a line, where the glyphs are set
    /// from the characters they were shaped out of, so the character is the unit and not the half of an
    /// encoding that stands for it. A column is left to its own count: it gives every character of the
    /// text a cell of its own, and the cells are stepped by the same units the drawing steps by.
    /// </summary>
    private static Int32 SpacingUnits(String source, Int32 start, Int32 length, WritingModeInfo flow) =>
        flow.IsVertical ? length : TextClusters.Count(source, start, length);

    /// <summary>
    /// Measures the annotation of a ruby item: how far it reaches along the inline axis, how much room
    /// it needs beside the base, and the orientation its glyphs are drawn in.
    /// </summary>
    /// <remarks>
    /// The annotation is set in a writing mode of its own only in that its characters may orient
    /// differently from the base's - a reading in Latin is turned where the kanji beside it stay
    /// upright - so it is measured the same way any other run is and keeps the orientation its first
    /// run resolved to.
    /// </remarks>
    /// <summary>
    /// Gets how tall a line of text set in the given style is - the band the words it carries stand in,
    /// which is the line height the style asks for and the font's own height where it asks for none.
    /// </summary>
    private Double LineBoxHeight(ComputedStyle style, FontStyle font) =>
        LineBoxHeight(_measurer, style, font);

    /// <summary>
    /// Gets how tall a line of text set in the given style is - the band the words it carries stand in,
    /// which is the line height the style asks for and the font's own height where it asks for none.
    /// </summary>
    private static Double LineBoxHeight(ITextMeasurer measurer, ComputedStyle style, FontStyle font)
    {
        if (style.LineHeight is { } stated)
        {
            return stated;
        }

        var metrics = measurer.GetMetrics(font);

        return metrics.Ascent + metrics.Descent;
    }

    private RubyAnnotation MeasureAnnotation(InlineItem item)
    {
        var style = item.AnnotationStyle;
        var text = item.AnnotationText!;
        var flow = item.AnnotationBox!.Flow;
        var metrics = _measurer.GetMetrics(style.GetFont());
        var ascent = metrics.Ascent;
        var descent = metrics.Descent;

        if (style.LineHeight is { } lineHeight)
        {
            // Half the leading, which is negative when the font is taller than the line it is set on -
            // a face whose own ascent and descent come to more than the declared line height is drawn
            // overflowing a line box that is still as tall as the page asked for.
            var halfLeading = (lineHeight - (ascent + descent)) / 2;
            ascent += halfLeading;
            descent += halfLeading;
        }

        return new RubyAnnotation(
            text,
            Measure(style, text, flow),
            ascent + descent,
            ascent,
            item.Box.Style.HasRubyUnder,
            style,
            item.AnnotationBox!,
            ResolveOrientation(text, flow));
    }

    /// <summary>
    /// Gets how far a reading may hang past its pair on each side: the room the text standing
    /// beside the pair gives up (CSS Ruby 1 §5.1).
    /// </summary>
    private (Double Before, Double After) OverhangRoom(
        List<InlineItem> items,
        Int32 index,
        Double position,
        Double baseWidth,
        Double annotationWidth,
        Double[] begins,
        ComputedStyle tabStyle)
    {
        var pair = items[index].Text;
        var facing = String.IsNullOrEmpty(pair) ? '\0' : pair![0];
        var before = Spaces(items, index - 1, fromEnd: true, position, facing, begins, tabStyle);

        // A reading that cannot hang far enough forces its pair wider, and the wider pair carries the
        // text after it along the line. What that side gives up is therefore asked of the place the
        // reading itself reaches, which the room already found on this side decides.
        var end = ReadingEnd(items[index], position, baseWidth, annotationWidth, before);
        var after = Spaces(items, index + 1, fromEnd: false, end,
            String.IsNullOrEmpty(pair) ? '\0' : pair[^1], begins, tabStyle);

        return (before, after);
    }

    /// <summary>
    /// Gets where a reading reaches along the line: the pair's own characters stand where its alignment
    /// puts them once the room beside it has been taken, and the reading is laid over them from there
    /// (CSS Ruby 1 §4.3, §5.1).
    /// </summary>
    private static Double ReadingEnd(
        InlineItem item,
        Double position,
        Double baseWidth,
        Double annotationWidth,
        Double before)
    {
        var excess = Math.Max(0, annotationWidth - baseWidth);
        var start = position + HangPast(item, excess, before, fromEnd: true);

        return item.Box.Style.RubyAlign switch
        {
            RubyAlign.Start or RubyAlign.SpaceBetween => start + annotationWidth,
            RubyAlign.End => start + baseWidth,
            _ => start + (baseWidth + annotationWidth) / 2,
        };
    }

    /// <summary>
    /// Measures the room one side of a pair gives up to a reading. The characters standing against the
    /// pair are gathered outward - white space of any width, the blank side of a fullwidth mark, a
    /// quarter of a middle dot - until one gives up nothing, and they are measured from the place the
    /// line holds them at, because a tab's room is a distance to a stop (CSS Ruby 1 §5.1). A neighbour
    /// that is not a run of text gives up nothing at all.
    /// </summary>
    private Double Spaces(
        List<InlineItem> items,
        Int32 at,
        Boolean fromEnd,
        Double position,
        Char facing,
        Double[] begins,
        ComputedStyle tabStyle)
    {
        var given = 0.0;
        var edge = position;

        for (var index = at; index >= 0 && index < items.Count; index += fromEnd ? -1 : 1)
        {
            var item = items[index];

            if (item.IsRuby || item.IsReplaced || item.IsAtomic || item.IsBreak || item.IsOutOfFlow ||
                String.IsNullOrEmpty(item.Text))
            {
                break;
            }

            var text = item.Text!;
            var flow = item.Box.Flow;

            // How many characters at the edge a reading may cover, counted outward until one keeps its
            // whole room for itself.
            var taken = 0;

            while (taken < text.Length &&
                HangShare(item, text, fromEnd ? text.Length - 1 - taken : taken, fromEnd, facing) > 0)
            {
                taken++;
            }

            if (taken == 0)
            {
                break;
            }

            var first = fromEnd ? text.Length - taken : 0;

            // A tab runs the line on to its next stop rather than by any advance of its own, so what the
            // neighbour gives up is measured from where the line stands at its edge: for the one behind
            // the pair that is where the line left it, and for the one ahead where the reading ends.
            var place = fromEnd
                ? begins[index] + WhiteSpaceWidth(item.Style, tabStyle, text, 0, first, begins[index], flow)
                : edge;

            // The characters are then paid for in the order the line sets them: white space gives up all
            // of its room, a mark only the part of the fullwidth one that faces the pair.
            var cursor = first;

            while (cursor < first + taken)
            {
                if (IsOverhangSpace(text[cursor]))
                {
                    var next = cursor;

                    while (next < first + taken && IsOverhangSpace(text[next]))
                    {
                        next++;
                    }

                    var run = WhiteSpaceWidth(item.Style, tabStyle, text, cursor, next - cursor, place, flow);
                    given += run;
                    place += run;
                    cursor = next;
                    continue;
                }

                var units = TextChunker.UnitsAt(text, cursor);

                given += HangShare(item, text, cursor, fromEnd, facing) *
                    FullwidthRoom(item.Style, flow);
                place += Measure(item.Style, text, cursor, units, flow);
                cursor += units;
            }

            edge = fromEnd ? begins[index] : place;

            if (taken < text.Length)
            {
                break;
            }
        }

        return given;
    }

    /// <summary>
    /// Gets whether a character is one a reading may hang over. CSS Ruby 1 §5.1 names preserved white
    /// space, no-break space and the other space separators - and, of punctuation, only part of the
    /// advance. The pages beside a pair use the ideographic space, a space separator like the rest.
    /// </summary>
    private static Boolean IsOverhangSpace(Char character) =>
        character == '\t' || Char.GetUnicodeCategory(character) == UnicodeCategory.SpaceSeparator;

    /// <summary>
    /// Gets what part of a full character's room a character standing beside a pair gives up to the
    /// reading: all of white space, half of a fullwidth bracket on the blank side that faces the pair,
    /// and a quarter of a middle dot on either side. A mark whose blank has already been taken by
    /// <c>text-spacing-trim</c> has none left to give, and any other character gives up nothing at all -
    /// which is where the reading stops (CSS Ruby 1 §5.1).
    /// </summary>
    private Double HangShare(InlineItem item, String text, Int32 at, Boolean fromEnd, Char pair)
    {
        var character = text[at];

        if (IsOverhangSpace(character))
        {
            return 1;
        }

        var style = item.Style;
        var codePoint = (Int32)character;
        var kind = KindOf(style, codePoint);

        // Only the blank side that looks at the pair is offered: an opening mark keeps its room on its
        // far side and a closing mark on this, and a middle dot is blank on both.
        var share = kind switch
        {
            PunctuationClass.Opening => fromEnd ? 0 : 0.5,
            PunctuationClass.Closing => fromEnd ? 0.5 : 0,
            PunctuationClass.Middle => 0.25,
            _ => 0,
        };

        if (share == 0 ||
            (kind != PunctuationClass.Middle &&
                !IsFullwidthPunctuation(_measurer, style, codePoint)))
        {
            // A narrow parenthesis is a letter the reading must not cover, however the pair is set.
            return 0;
        }

        // The character the pair stands against, and the one beyond, decide whether the blank is still
        // there: a mark already trimmed by `text-spacing-trim` gives up nothing (CSS Ruby 1 §5.1).
        var value = style.TextSpacingTrim;

        if (value.All)
        {
            return 0;
        }

        var inward = at == (fromEnd ? text.Length - 1 : 0)
            ? pair
            : fromEnd ? text[at + 1] : text[at - 1];

        if (value.Adjacent)
        {
            var trims = kind switch
            {
                PunctuationClass.Opening => TextSpacingTrim.TrimsOpening(
                    NeighborOf(_measurer, style, inward, null), style.FontSize),
                PunctuationClass.Closing => TextSpacingTrim.TrimsClosing(
                    NeighborOf(_measurer, style, inward, null), style.FontSize),
                _ => false,
            };

            if (trims)
            {
                return 0;
            }
        }

        return share;
    }

    /// <summary>
    /// Gets the room one full character takes along the line: the ideographic space, which is the width
    /// a fullwidth form is measured in.
    /// </summary>
    private Double FullwidthRoom(ComputedStyle style, WritingModeInfo flow) =>
        Measure(style, "\u3000", flow);

    /// <summary>
    /// Gets how much room a pair must take for itself on one side, past its own characters, when the
    /// reading does not fit in the room that side gives up (CSS Ruby 1 §5.1).
    /// </summary>
    private static Double HangPast(InlineItem item, Double excess, Double room, Boolean fromEnd)
    {
        if (excess <= 0 || item.Box.Style.RubyOverhang != RubyOverhangMode.Spaces)
        {
            return 0;
        }

        // Which side the reading hangs from is `ruby-align`'s business: from the start it asks room of
        // the far side only, from the end of the near side only, and from the centre of both.
        return item.Box.Style.RubyAlign switch
        {
            RubyAlign.Start or RubyAlign.SpaceBetween => fromEnd ? 0 : Math.Max(0, excess - room),
            RubyAlign.End => fromEnd ? Math.Max(0, excess - room) : 0,
            _ => Math.Max(0, (excess / 2) - room),
        };
    }

    /// <summary>
    /// Gets the room a ruby pair takes along the line: the wider of its base and its reading, less
    /// what the reading may hang over the text beside it (CSS Ruby 1 §5.1).
    /// </summary>
    private static Double RubyExtent(
        InlineItem item,
        Double baseWidth,
        Double annotationWidth,
        Double before,
        Double after)
    {
        var excess = annotationWidth - baseWidth;

        if (excess <= 0)
        {
            return baseWidth;
        }

        if (item.Box.Style.RubyOverhang == RubyOverhangMode.Spaces)
        {
            // A reading may cover the spaces beside it and no further: once those run out, the pair
            // grows by what is left on that side.
            return baseWidth +
                HangPast(item, excess, before, fromEnd: true) +
                HangPast(item, excess, after, fromEnd: false);
        }

        // What `auto` allows is the user agent's choice. Chrome lets a reading hang half an em past
        // its base in total - a quarter of it on each side - whatever stands beside it.
        return Math.Max(baseWidth, annotationWidth - (item.Style.GetFont().Size / 2));
    }

    /// <summary>
    /// Gets where a box narrower than its ruby column stands in it, which is what <c>ruby-align</c>
    /// says (CSS Ruby 1 §4.3).
    /// </summary>
    private static Double RubyAligned(RubyAlign align, Double extent, Double width) => align switch
    {
        RubyAlign.Start or RubyAlign.SpaceBetween => 0,
        RubyAlign.End => extent - width,
        _ => (extent - width) / 2,
    };

    /// <summary>
    /// Gets how far a pair's own characters stand inside the room it takes: what the reading took from
    /// the near side is what pushes them along, and the reading is laid over them from there. Nothing is
    /// pushed where the room the pair may take is the user agent's choice, which hangs the reading past
    /// the pair evenly instead (CSS Ruby 1 §4.3, §5.1).
    /// </summary>
    private static Double? RubyHang(
        InlineItem item,
        Double extent,
        Double baseWidth,
        Double annotationWidth,
        Double after) =>
        item.Box.Style.RubyOverhang == RubyOverhangMode.Spaces
            ? extent - baseWidth - HangPast(item, Math.Max(0, annotationWidth - baseWidth), after, fromEnd: false)
            : null;

    /// <summary>
    /// Measures a range of text the way a column advances it. Upright text advances by its em box
    /// rather than by its horizontal width, so a Latin letter that would leave room to spare in a line
    /// still takes a whole em in a column, and text combined upright is compressed into a single em.
    /// </summary>
    private static Double MeasureColumn(
        String source,
        Int32 origin,
        Int32 length,
        FontStyle font,
        Double fontSize,
        WritingModeInfo flow,
        ITextMeasurer measurer)
    {
        var total = 0.0;

        foreach (var run in new OrientedPieces(source, origin, length, flow))
        {
            total += run.Advance(font, measurer, fontSize, flow);
        }

        return total;
    }

}

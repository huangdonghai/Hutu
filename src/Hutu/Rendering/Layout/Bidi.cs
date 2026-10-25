using System;
using System.Collections.Generic;

namespace Hutu.Rendering.Layout;

/// <summary>
/// The Unicode bidirectional algorithm (UAX #9), to the depth a line of text needs: the embedding level
/// of every code point of a run, and the order the runs of a line are drawn in.
/// </summary>
/// <remarks>
/// <para>
/// A paragraph is resolved from its base direction, every character takes an embedding level from the
/// weak, neutral and implicit rules, and the line is reordered by those levels (L2). The explicit
/// embeddings and isolates are honoured to the depth the properties go: a run's content is set at the
/// level its own marks ask for, and a character whose level cannot be drawn is read at the level it
/// would have had without them.
/// </para>
/// <para>
/// Paired brackets (N0) are not resolved: a bracket takes the direction of the text around it by the
/// neutral rules, which is what a page that writes its brackets in a right-to-left sentence expects of
/// them anyway.
/// </para>
/// </remarks>
internal static class Bidi
{
    /// <summary>Gets the embedding level of each code point of the text.</summary>
    /// <param name="text">The text of the paragraph run.</param>
    /// <param name="baseLevel">The paragraph's level: 0 for left-to-right, 1 for right-to-left.</param>
    internal static Int32[] Levels(String text, Int32 baseLevel)
    {
        var count = text.Length;

        if (count == 0)
        {
            return Array.Empty<Int32>();
        }

        var classes = new BidiClass[count];
        var levels = new Int32[count];
        var points = new Int32[count];

        for (var at = 0; at < count;)
        {
            var point = Char.ConvertToUtf32(text, at);
            var units = Char.IsSurrogatePair(text, at) ? 2 : 1;
            classes[at] = BidiClasses.Of(point);
            points[at] = units;

            for (var unit = 1; unit < units; unit++)
            {
                classes[at + unit] = classes[at];
            }

            at += units;
        }

        // X1-X8: the explicit embeddings. The stack holds the level every character between a mark and
        // its pop is set at, and the override a mark may put over the characters' own classes.
        var stack = new Stack<(Int32 Level, BidiClass Override, Boolean Isolate)>();
        stack.Push((baseLevel, BidiClass.ON, false));
        var overflow = 0;

        for (var at = 0; at < count; at++)
        {
            var type = classes[at];

            switch (type)
            {
                case BidiClass.RLE or BidiClass.RLO or BidiClass.LRE or BidiClass.LRO:
                {
                    var level = stack.Peek().Level;
                    var next = type is BidiClass.RLE or BidiClass.RLO
                        ? level + (level % 2 == 1 ? 2 : 1)
                        : level + (level % 2 == 0 ? 2 : 1);

                    if (next <= 125 && overflow == 0)
                    {
                        level = next;
                    }
                    else
                    {
                        overflow++;
                    }

                    stack.Push((level, type is BidiClass.RLO ? BidiClass.R : type is BidiClass.LRO ? BidiClass.L : BidiClass.ON, false));
                    break;
                }

                case BidiClass.PDF:
                    if (overflow > 0)
                    {
                        overflow--;
                    }
                    else if (stack.Count > 1 && !stack.Peek().Isolate)
                    {
                        stack.Pop();
                    }

                    break;

                case BidiClass.RLI or BidiClass.LRI or BidiClass.FSI:
                {
                    // X5a: the isolate initiator stands at the level it was written at; the text it
                    // holds starts a step further in.
                    var level = stack.Peek().Level;
                    var next = type is BidiClass.LRI
                        ? level + (level % 2 == 0 ? 2 : 1)
                        : level + (level % 2 == 1 ? 2 : 1);

                    levels[at] = level;

                    if (next <= 125)
                    {
                        level = next;
                    }

                    stack.Push((level, BidiClass.ON, true));
                    break;
                }

                case BidiClass.PDI:
                    if (stack.Count > 1 && stack.Peek().Isolate)
                    {
                        stack.Pop();
                    }

                    levels[at] = stack.Count > 1 ? stack.Peek().Level : baseLevel;
                    break;

                default:
                    levels[at] = stack.Count > 1 ? stack.Peek().Level : baseLevel;
                    break;
            }

            if (type != BidiClass.BN && stack.Count > 1 && stack.Peek().Override != BidiClass.ON)
            {
                classes[at] = stack.Peek().Override;
            }
        }

        // X9: the characters that only steer the ordering are taken out of the text the weak and
        // neutral rules read, which is what lets a neutral run carry on across them.
        for (var at = 0; at < count; at++)
        {
            if (classes[at] is BidiClass.LRE or BidiClass.RLE or BidiClass.LRO or BidiClass.RLO or
                BidiClass.PDF or BidiClass.LRI or BidiClass.RLI or BidiClass.FSI or BidiClass.PDI)
            {
                classes[at] = BidiClass.BN;
            }
        }

        // W1-W7: the weak classes, resolved across the paragraph.
        var lastStrong = baseLevel % 2 == 1 ? BidiClass.R : BidiClass.L;

        for (var at = 0; at < count; at++)
        {
            var type = classes[at];

            if (type == BidiClass.NSM)
            {
                classes[at] = at > 0 && classes[at - 1] is not (BidiClass.LRI or BidiClass.RLI or BidiClass.FSI or BidiClass.PDI)
                    ? classes[at - 1]
                    : BidiClass.ON;
            }
        }

        for (var at = 0; at < count; at++)
        {
            var type = classes[at];

            if (type == BidiClass.EN && lastStrong == BidiClass.AL)
            {
                classes[at] = BidiClass.AN;
            }

            if (classes[at] == BidiClass.AL)
            {
                classes[at] = BidiClass.R;
            }

            if (classes[at] is BidiClass.L or BidiClass.R)
            {
                lastStrong = classes[at];
            }
        }

        for (var at = 1; at < count - 1; at++)
        {
            if (classes[at] == BidiClass.ES &&
                classes[at - 1] == BidiClass.EN && classes[at + 1] == BidiClass.EN)
            {
                classes[at] = BidiClass.EN;
            }
            else if (classes[at] == BidiClass.CS)
            {
                if (classes[at - 1] == BidiClass.EN && classes[at + 1] == BidiClass.EN)
                {
                    classes[at] = BidiClass.EN;
                }
                else if (classes[at - 1] == BidiClass.AN && classes[at + 1] == BidiClass.AN)
                {
                    classes[at] = BidiClass.AN;
                }
            }
        }

        for (var at = 0; at < count; at++)
        {
            if (classes[at] != BidiClass.ET)
            {
                continue;
            }

            var from = at;

            while (from < count && classes[from] == BidiClass.ET)
            {
                from++;
            }

            var before = at > 0 && classes[at - 1] == BidiClass.EN;
            var after = from < count && classes[from] == BidiClass.EN;

            if (before || after)
            {
                for (var at2 = at; at2 < from; at2++)
                {
                    classes[at2] = BidiClass.EN;
                }
            }

            at = from - 1;
        }

        for (var at = 0; at < count; at++)
        {
            if (classes[at] is BidiClass.ES or BidiClass.ET or BidiClass.CS)
            {
                classes[at] = BidiClass.ON;
            }
        }

        lastStrong = baseLevel % 2 == 1 ? BidiClass.R : BidiClass.L;

        for (var at = 0; at < count; at++)
        {
            if (classes[at] is BidiClass.L or BidiClass.R)
            {
                lastStrong = classes[at];
            }
            else if (classes[at] == BidiClass.EN && lastStrong == BidiClass.L)
            {
                classes[at] = BidiClass.L;
            }
        }

        // N1, N2: a run of neutrals takes the direction of the strong text on either side of it where
        // the two agree, and the paragraph's own direction where they do not.
        for (var at = 0; at < count; at++)
        {
            if (!IsNeutral(classes[at]))
            {
                continue;
            }

            var from = at;

            while (from < count && IsNeutral(classes[from]))
            {
                from++;
            }

            // A neutral run that reaches the edge of its sequence takes the direction the sequence is
            // embedded at, which is the level the characters stand at rather than the paragraph's own
            // (that is what the sos and eos of an isolating run sequence are, UAX #9 §3.3).
            var embedding = levels[at] % 2 == 1 ? BidiClass.R : BidiClass.L;
            var before = at > 0 ? Direction(classes[at - 1]) : embedding;
            var after = from < count ? Direction(classes[from]) : embedding;
            var taken = before == after ? before : baseLevel == 0 ? BidiClass.L : BidiClass.R;

            for (var at2 = at; at2 < from; at2++)
            {
                classes[at2] = taken;
            }

            at = from - 1;
        }

        // I1, I2: the implicit levels.
        for (var at = 0; at < count; at++)
        {
            if (levels[at] % 2 == 0)
            {
                levels[at] += classes[at] switch
                {
                    BidiClass.R => 1,
                    BidiClass.AN or BidiClass.EN => 2,
                    _ => 0,
                };
            }
            else
            {
                levels[at] += classes[at] is BidiClass.L or BidiClass.AN or BidiClass.EN ? 1 : 0;
            }
        }

        // X9: a boundary-neutral character is not drawn and takes no part in the ordering - a joiner
        // stays with the letters it joins - so it follows the character before it, and a leading one
        // follows the first character that is drawn.
        for (var at = 0; at < count; at++)
        {
            if (classes[at] != BidiClass.BN)
            {
                continue;
            }

            if (at > 0)
            {
                levels[at] = levels[at - 1];
                continue;
            }

            var followed = baseLevel;

            for (var next = at + 1; next < count; next++)
            {
                if (classes[next] != BidiClass.BN)
                {
                    followed = levels[next];
                    break;
                }
            }

            levels[at] = followed;
        }

        // L1: the white space at the end of the paragraph, and a separator at it, stands at the
        // paragraph's own level.
        for (var at = count - 1; at >= 0; at--)
        {
            if (classes[at] is BidiClass.WS or BidiClass.S or BidiClass.B or BidiClass.BN ||
                classes[at] is BidiClass.LRI or BidiClass.RLI or BidiClass.FSI or BidiClass.PDI)
            {
                levels[at] = baseLevel;
                continue;
            }

            break;
        }

        return levels;
    }

    /// <summary>
    /// Gets the paragraph level a line of <c>unicode-bidi: plaintext</c> takes from its own text: the
    /// direction of the first character that has one, or the fallback where the line holds none (P2, P3).
    /// </summary>
    internal static Int32 ParagraphLevel(String text, Int32 fallback)
    {
        for (var at = 0; at < text.Length;)
        {
            var cls = BidiClasses.Of(Char.ConvertToUtf32(text, at));

            if (cls == BidiClass.L)
            {
                return 0;
            }

            if (cls is BidiClass.R or BidiClass.AL)
            {
                return 1;
            }

            at += Char.IsSurrogatePair(text, at) ? 2 : 1;
        }

        return fallback;
    }

    private static Boolean IsNeutral(BidiClass type) =>
        type is BidiClass.B or BidiClass.S or BidiClass.WS or BidiClass.ON or BidiClass.BN;

    private static BidiClass Direction(BidiClass type) => type switch
    {
        BidiClass.R or BidiClass.EN or BidiClass.AN => BidiClass.R,
        _ => BidiClass.L,
    };

    /// <summary>Gets the runs of equal level the text is made of, in logical order.</summary>
    internal static List<(Int32 Start, Int32 Length, Int32 Level)> Runs(String text, Int32[] levels)
    {
        var runs = new List<(Int32 Start, Int32 Length, Int32 Level)>();

        if (levels.Length == 0)
        {
            return runs;
        }

        var start = 0;
        var level = levels[0];

        for (var at = 1; at <= levels.Length; at++)
        {
            if (at < levels.Length && levels[at] == level)
            {
                continue;
            }

            runs.Add((start, at - start, level));
            start = at;

            if (at < levels.Length)
            {
                level = levels[at];
            }
        }

        return runs;
    }

    /// <summary>
    /// Gets the order the runs of a line are drawn in, from the highest level down to the lowest odd
    /// one, each contiguous run at a level or above reversed (UAX #9 L2).
    /// </summary>
    internal static Int32[] VisualOrder(IReadOnlyList<Int32> levels)
    {
        var order = new Int32[levels.Count];

        for (var at = 0; at < order.Length; at++)
        {
            order[at] = at;
        }

        var highest = 0;
        var lowestOdd = Int32.MaxValue;

        foreach (var level in levels)
        {
            highest = Math.Max(highest, level);

            if (level % 2 == 1)
            {
                lowestOdd = Math.Min(lowestOdd, level);
            }
        }

        if (lowestOdd == Int32.MaxValue)
        {
            return order;
        }

        for (var level = highest; level >= lowestOdd; level--)
        {
            for (var at = 0; at < order.Length;)
            {
                if (levels[order[at]] < level)
                {
                    at++;
                    continue;
                }

                var to = at;

                while (to < order.Length && levels[order[to]] >= level)
                {
                    to++;
                }

                Array.Reverse(order, at, to - at);
                at = to;
            }
        }

        return order;
    }
}

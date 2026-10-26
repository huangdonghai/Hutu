using System;
using System.Collections.Generic;
using Hutu.Rendering.Layout;
using Hutu.Rendering.Paint;

namespace Hutu.Rendering;

/// <summary>One command of a <c>shape()</c>, with its distances still in the units the page wrote.</summary>
internal enum ShapeCommandKind
{
    Move,
    Line,
    HLine,
    VLine,
    Curve,
    Smooth,
    Close,
}

/// <summary>Where a curve's control point counts from.</summary>
internal enum ShapeOrigin
{
    /// <summary>The reference box's top left corner.</summary>
    Absolute,

    /// <summary>The point the segment starts at.</summary>
    Start,

    /// <summary>The point the segment ends at.</summary>
    End,

    /// <summary>The reference box's own origin.</summary>
    Origin,
}

internal readonly record struct ShapePoint(StyleLength X, StyleLength Y, ShapeOrigin Origin)
{
    public static ShapePoint Absolute(StyleLength x, StyleLength y) => new(x, y, ShapeOrigin.Absolute);
}

internal sealed class ShapeCommand
{
    public required ShapeCommandKind Kind { get; init; }

    public Boolean Relative { get; init; }

    public StyleLength X { get; init; }

    public StyleLength Y { get; init; }

    public ShapePoint? Control1 { get; init; }

    public ShapePoint? Control2 { get; init; }
}

internal sealed partial class ClipPathShape
{
    /// <summary>
    /// Reads <c>shape()</c>: a fill rule and a start point, then commands that move, draw straight
    /// lines and curves, and close the outline (CSS Shapes 2 §4.1). The distances stay unresolved
    /// because a percentage is a share of the reference box, which only the cut knows.
    /// </summary>
    private static ClipPathShape? ReadShapeFunction(String body, Reader terms, ClipBox box)
    {
        var pieces = Pieces(body);

        if (pieces.Count == 0)
        {
            return null;
        }

        var commands = new List<ShapeCommand>();
        var evenOdd = false;
        var started = false;

        foreach (var piece in pieces)
        {
            var words = Words(piece);

            if (words.Count == 0)
            {
                continue;
            }

            var at = 0;

            if (!started && (Is(words[0], "evenodd") || Is(words[0], "nonzero")))
            {
                evenOdd = Is(words[0], "evenodd");
                at++;
            }

            if (!started && at < words.Count && Is(words[at], "from"))
            {
                at++;

                if (!TakePosition(words, ref at, terms, out var start, horizontal: false, vertical: false))
                {
                    return null;
                }

                commands.Add(new ShapeCommand
                {
                    Kind = ShapeCommandKind.Move,
                    X = start.X,
                    Y = start.Y,
                });

                started = true;
                continue;
            }

            if (!started || at >= words.Count)
            {
                return null;
            }

            var verb = words[at].ToLowerInvariant();
            at++;

            switch (verb)
            {
                case "move":
                case "line":
                {
                    var (relative, ok) = TakeByOrTo(words, ref at);

                    if (!ok || !TakePosition(words, ref at, terms, out var point, horizontal: false, vertical: false))
                    {
                        return null;
                    }

                    commands.Add(new ShapeCommand
                    {
                        Kind = verb == "move" ? ShapeCommandKind.Move : ShapeCommandKind.Line,
                        Relative = relative,
                        X = point.X,
                        Y = point.Y,
                    });

                    break;
                }

                case "hline":
                case "vline":
                {
                    var (relative, ok) = TakeByOrTo(words, ref at);

                    if (!ok || !TakeLine(words, ref at, terms, verb == "hline", out var length))
                    {
                        return null;
                    }

                    commands.Add(new ShapeCommand
                    {
                        Kind = verb == "hline" ? ShapeCommandKind.HLine : ShapeCommandKind.VLine,
                        Relative = relative,
                        X = verb == "hline" ? length : StyleLength.Zero,
                        Y = verb == "vline" ? length : StyleLength.Zero,
                    });

                    break;
                }

                case "curve":
                case "smooth":
                {
                    var (relative, ok) = TakeByOrTo(words, ref at);

                    if (!ok || !TakePosition(words, ref at, terms, out var end, horizontal: false, vertical: false))
                    {
                        return null;
                    }

                    ShapePoint? first = null;
                    ShapePoint? second = null;

                    if (at < words.Count && Is(words[at], "with"))
                    {
                        at++;

                        if (!TakeControl(words, ref at, terms, out var control))
                        {
                            return null;
                        }

                        first = control;

                        if (at < words.Count && words[at] == "/")
                        {
                            at++;

                            if (!TakeControl(words, ref at, terms, out var other))
                            {
                                return null;
                            }

                            second = other;
                        }
                    }

                    commands.Add(new ShapeCommand
                    {
                        Kind = verb == "curve" ? ShapeCommandKind.Curve : ShapeCommandKind.Smooth,
                        Relative = relative,
                        X = end.X,
                        Y = end.Y,
                        Control1 = first,
                        Control2 = second,
                    });

                    break;
                }

                case "close":
                    commands.Add(new ShapeCommand { Kind = ShapeCommandKind.Close });
                    break;

                default:
                    return null;
            }
        }

        return commands.Count == 0
            ? null
            : new ClipPathShape
            {
                Form = ClipForm.Shape,
                Box = box,
                EvenOdd = evenOdd,
                ShapeCommands = commands,
            };
    }

    /// <summary>Reads the <c>by</c> or <c>to</c> a command names, which decides whether the target is relative.</summary>
    private static (Boolean Relative, Boolean Ok) TakeByOrTo(List<String> words, ref Int32 at)
    {
        if (at >= words.Count)
        {
            return (false, false);
        }

        if (Is(words[at], "by"))
        {
            at++;
            return (true, true);
        }

        if (Is(words[at], "to"))
        {
            at++;
            return (false, true);
        }

        return (false, false);
    }

    /// <summary>
    /// Reads one distance along the axis a line command names. A keyword stands for the edge or the
    /// middle of the reference box; a length is read as the page wrote it.
    /// </summary>
    private static Boolean TakeLine(
        List<String> words, ref Int32 at, Reader terms, Boolean horizontal, out StyleLength length)
    {
        length = StyleLength.Zero;

        if (at >= words.Count)
        {
            return false;
        }

        var word = words[at];

        if (Is(word, "center"))
        {
            at++;
            length = StyleLength.FromPercent(0.5);
            return true;
        }

        if (KeywordOnAxis(word, horizontal, out var share))
        {
            at++;
            length = StyleLength.FromPercent(share);
            return true;
        }

        if (terms.Read(word) is not { } read)
        {
            return false;
        }

        at++;
        length = read;
        return true;
    }

    /// <summary>Reads a position, with its two axes in either order and keywords folded to shares.</summary>
    private static Boolean TakePosition(
        List<String> words,
        ref Int32 at,
        Reader terms,
        out ShapePoint point,
        Boolean horizontal,
        Boolean vertical)
    {
        point = default;

        if (at >= words.Count)
        {
            return false;
        }

        var first = words[at];

        // A keyword on one axis leaves the other axis to the middle unless a second word names it.
        if (KeywordOnAxis(first, horizontal: true, out var firstX))
        {
            at++;
            var y = StyleLength.FromPercent(0.5);

            if (at < words.Count && words[at] != "/" && !Is(words[at], "with") && !Is(words[at], "from"))
            {
                var second = words[at];

                if (KeywordOnAxis(second, horizontal: false, out var secondY))
                {
                    at++;
                    y = StyleLength.FromPercent(secondY);
                }
                else if (Is(second, "center"))
                {
                    at++;
                }
                else if (terms.Read(second) is { } secondLength)
                {
                    at++;
                    y = secondLength;
                }
            }

            point = ShapePoint.Absolute(StyleLength.FromPercent(firstX), y);
            return true;
        }

        if (KeywordOnAxis(first, horizontal: false, out var firstY))
        {
            at++;
            var x = StyleLength.FromPercent(0.5);

            if (at < words.Count && words[at] != "/" && !Is(words[at], "with") && !Is(words[at], "from"))
            {
                var second = words[at];

                if (KeywordOnAxis(second, horizontal: true, out var secondX))
                {
                    at++;
                    x = StyleLength.FromPercent(secondX);
                }
                else if (Is(second, "center"))
                {
                    at++;
                }
                else if (terms.Read(second) is { } secondLength)
                {
                    at++;
                    x = secondLength;
                }
            }

            point = ShapePoint.Absolute(x, StyleLength.FromPercent(firstY));
            return true;
        }

        if (Is(first, "center"))
        {
            at++;
            var x = StyleLength.FromPercent(0.5);
            var y = StyleLength.FromPercent(0.5);

            if (at < words.Count && words[at] != "/" && !Is(words[at], "with") && !Is(words[at], "from"))
            {
                var second = words[at];

                if (KeywordOnAxis(second, horizontal: true, out var secondX))
                {
                    at++;
                    x = StyleLength.FromPercent(secondX);
                }
                else if (KeywordOnAxis(second, horizontal: false, out var secondY))
                {
                    at++;
                    y = StyleLength.FromPercent(secondY);
                }
                else if (terms.Read(second) is { } secondLength)
                {
                    at++;
                    y = secondLength;
                }
            }

            point = ShapePoint.Absolute(x, y);
            return true;
        }

        if (terms.Read(first) is not { } length)
        {
            return false;
        }

        at++;
        var other = StyleLength.FromPercent(0.5);

        if (at < words.Count && words[at] != "/" && !Is(words[at], "with") && !Is(words[at], "from"))
        {
            var second = words[at];

            if (KeywordOnAxis(second, horizontal: false, out var secondY))
            {
                at++;
                other = StyleLength.FromPercent(secondY);
            }
            else if (Is(second, "center"))
            {
                at++;
            }
            else if (terms.Read(second) is { } secondLength)
            {
                at++;
                other = secondLength;
            }
        }

        point = ShapePoint.Absolute(length, other);
        return true;
    }

    /// <summary>Reads one control point, and the point it counts from when the page names one.</summary>
    private static Boolean TakeControl(
        List<String> words, ref Int32 at, Reader terms, out ShapePoint control)
    {
        control = default;

        if (!TakePosition(words, ref at, terms, out var point, horizontal: false, vertical: false))
        {
            return false;
        }

        var origin = ShapeOrigin.Absolute;

        if (at < words.Count && Is(words[at], "from") && at + 1 < words.Count)
        {
            var named = words[at + 1].ToLowerInvariant();
            origin = named switch
            {
                "start" => ShapeOrigin.Start,
                "end" => ShapeOrigin.End,
                "origin" => ShapeOrigin.Origin,
                _ => ShapeOrigin.Absolute,
            };

            at += 2;
        }

        control = new ShapePoint(point.X, point.Y, origin);
        return true;
    }

    /// <summary>Reads a keyword as a share of the axis it names: left/top 0, center 50%, right/bottom 100%.</summary>
    private static Boolean KeywordOnAxis(String word, Boolean horizontal, out Double share)
    {
        switch (word.ToLowerInvariant())
        {
            case "left":
                share = 0;
                return horizontal;

            case "right":
                share = 1;
                return horizontal;

            case "top":
                share = 0;
                return !horizontal;

            case "bottom":
                share = 1;
                return !horizontal;

            default:
                share = 0;
                return false;
        }
    }

    /// <summary>Resolves a <c>shape()</c>'s commands to the outline the painter cuts with.</summary>
    private ClipCut ShapeOf(Rect reference)
    {
        var outline = new List<ClipSegment>();
        var currentX = 0.0;
        var currentY = 0.0;
        var startX = 0.0;
        var startY = 0.0;
        var lastControlX = 0.0;
        var lastControlY = 0.0;
        var hasCurve = false;

        var left = Double.PositiveInfinity;
        var top = Double.PositiveInfinity;
        var right = Double.NegativeInfinity;
        var bottom = Double.NegativeInfinity;

        void Extend(Double x, Double y)
        {
            left = Math.Min(left, x);
            right = Math.Max(right, x);
            top = Math.Min(top, y);
            bottom = Math.Max(bottom, y);
        }

        foreach (var command in ShapeCommands ?? Array.Empty<ShapeCommand>())
        {
            switch (command.Kind)
            {
                case ShapeCommandKind.Move:
                case ShapeCommandKind.Line:
                {
                    var x = command.Relative ? currentX + XOf(command.X, reference) : reference.X + XOf(command.X, reference);
                    var y = command.Relative ? currentY + YOf(command.Y, reference) : reference.Y + YOf(command.Y, reference);

                    if (command.Kind == ShapeCommandKind.Move)
                    {
                        outline.Add(ClipSegment.To(ClipEdge.Begin, x, y));
                        startX = x;
                        startY = y;
                    }
                    else
                    {
                        outline.Add(ClipSegment.To(ClipEdge.Line, x, y));
                    }

                    Extend(x, y);
                    currentX = x;
                    currentY = y;
                    hasCurve = false;
                    break;
                }

                case ShapeCommandKind.HLine:
                {
                    var x = command.Relative ? currentX + XOf(command.X, reference) : reference.X + XOf(command.X, reference);
                    outline.Add(ClipSegment.To(ClipEdge.Line, x, currentY));
                    Extend(x, currentY);
                    currentX = x;
                    hasCurve = false;
                    break;
                }

                case ShapeCommandKind.VLine:
                {
                    var y = command.Relative ? currentY + YOf(command.Y, reference) : reference.Y + YOf(command.Y, reference);
                    outline.Add(ClipSegment.To(ClipEdge.Line, currentX, y));
                    Extend(currentX, y);
                    currentY = y;
                    hasCurve = false;
                    break;
                }

                case ShapeCommandKind.Curve:
                case ShapeCommandKind.Smooth:
                {
                    var endX = command.Relative ? currentX + XOf(command.X, reference) : reference.X + XOf(command.X, reference);
                    var endY = command.Relative ? currentY + YOf(command.Y, reference) : reference.Y + YOf(command.Y, reference);

                    var first = command.Control1 is { } one
                        ? Control(one, reference, currentX, currentY, endX, endY, command.Relative)
                        : ((Double X, Double Y)?)null;

                    var second = command.Control2 is { } two
                        ? Control(two, reference, currentX, currentY, endX, endY, command.Relative)
                        : ((Double X, Double Y)?)null;

                    var reflectX = hasCurve ? 2 * currentX - lastControlX : currentX;
                    var reflectY = hasCurve ? 2 * currentY - lastControlY : currentY;

                    Double control1X;
                    Double control1Y;
                    Double control2X;
                    Double control2Y;

                    if (command.Kind == ShapeCommandKind.Smooth)
                    {
                        if (first is { } smoothControl && second is null)
                        {
                            // A smooth point names only the control the curve ends on; the one it leaves
                            // by is the reflection of the curve before it (CSS Shapes 2 §4.1).
                            control1X = reflectX;
                            control1Y = reflectY;
                            control2X = smoothControl.X;
                            control2Y = smoothControl.Y;
                            lastControlX = control2X;
                            lastControlY = control2Y;
                        }
                        else if (second is { } smoothSecond)
                        {
                            control1X = first?.X ?? reflectX;
                            control1Y = first?.Y ?? reflectY;
                            control2X = smoothSecond.X;
                            control2Y = smoothSecond.Y;
                            lastControlX = control2X;
                            lastControlY = control2Y;
                        }
                        else
                        {
                            // No control at all is a quadratic smooth: the reflected point is the one
                            // control a quadratic has, and a cubic stands in for it below.
                            (control1X, control1Y, control2X, control2Y) = Quadratic(
                                currentX, currentY, reflectX, reflectY, endX, endY);
                            lastControlX = reflectX;
                            lastControlY = reflectY;
                        }
                    }
                    else if (first is { } only && second is null)
                    {
                        // A curve with one control is a quadratic; two are a cubic.
                        (control1X, control1Y, control2X, control2Y) = Quadratic(
                            currentX, currentY, only.X, only.Y, endX, endY);
                        lastControlX = only.X;
                        lastControlY = only.Y;
                    }
                    else
                    {
                        control1X = first?.X ?? currentX;
                        control1Y = first?.Y ?? currentY;
                        control2X = second?.X ?? endX;
                        control2Y = second?.Y ?? endY;
                        lastControlX = control2X;
                        lastControlY = control2Y;
                    }

                    outline.Add(ClipSegment.Curving(endX, endY, control1X, control1Y, control2X, control2Y));
                    Extend(endX, endY);
                    Extend(control1X, control1Y);
                    Extend(control2X, control2Y);
                    hasCurve = true;
                    currentX = endX;
                    currentY = endY;
                    break;
                }

                case ShapeCommandKind.Close:
                    outline.Add(ClipSegment.Closed);
                    currentX = startX;
                    currentY = startY;
                    hasCurve = false;
                    break;
            }
        }

        if (outline.Count == 0)
        {
            return new ClipCut(reference, CornerRadii.None, null, EvenOdd);
        }

        return new ClipCut(
            new Rect(left, top, right - left, bottom - top),
            CornerRadii.None,
            outline,
            EvenOdd);
    }

    /// <summary>Converts a quadratic curve into the two controls a cubic is drawn with.</summary>
    private static (Double C1X, Double C1Y, Double C2X, Double C2Y) Quadratic(
        Double startX,
        Double startY,
        Double controlX,
        Double controlY,
        Double endX,
        Double endY) =>
        (startX + 2.0 / 3.0 * (controlX - startX),
         startY + 2.0 / 3.0 * (controlY - startY),
         endX + 2.0 / 3.0 * (controlX - endX),
         endY + 2.0 / 3.0 * (controlY - endY));

    private static (Double X, Double Y) Control(
        ShapePoint point,
        Rect reference,
        Double startX,
        Double startY,
        Double endX,
        Double endY,
        Boolean relative)
    {
        var (baseX, baseY) = point.Origin switch
        {
            ShapeOrigin.Start => (startX, startY),
            ShapeOrigin.End => (endX, endY),
            ShapeOrigin.Origin => (reference.X, reference.Y),
            _ => relative ? (startX, startY) : (reference.X, reference.Y),
        };

        return (baseX + XOf(point.X, reference), baseY + YOf(point.Y, reference));
    }

    private static Double XOf(StyleLength length, Rect reference) => length.Resolve(reference.Width);

    private static Double YOf(StyleLength length, Rect reference) => length.Resolve(reference.Height);
}

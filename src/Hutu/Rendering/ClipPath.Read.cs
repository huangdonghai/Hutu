using System;
using AngleSharp.Css;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Hutu.Rendering.Paint;

namespace Hutu.Rendering;

/// <summary>
/// Reading the words a <c>clip-path</c> declaration was written with.
/// </summary>
/// <remarks>
/// The shapes are taken apart the way the grammar writes them, and a value that does not stand up reads as no
/// shape at all - which is what a parse with a real grammar would have done to a declaration it had to drop.
/// <c>url()</c> answers that way too: an SVG <c>clipPath</c> element is a shape whose outline lives in the
/// document rather than in the declaration, and this reader was not given it.
/// </remarks>
internal sealed partial class ClipPathShape
{
    /// <summary>Reads the declaration's text, or null where it states no shape this engine can draw.</summary>
    public static ClipPathShape? Read(String? text, IRenderDimensions dimensions, LengthReader.FontSource font) =>
        Read(text, dimensions, font, ClipBox.Border, shapesOnly: false);

    /// <summary>
    /// Reads a <c>shape-outside</c> declaration, whose grammar is the shapes above with a different box
    /// vocabulary: <c>&lt;shape-box&gt;</c> names the four CSS boxes and no SVG ones, and where the page names
    /// none the shape measures itself against the margin box rather than the border box (CSS Shapes 1 §6.1).
    /// An <c>&lt;image&gt;</c> is the third alternative the grammar offers and is read as no shape, which is
    /// what the chapter says a fetch that yields nothing leaves: the float area unaffected.
    /// </summary>
    public static ClipPathShape? ReadForFloat(String? text, IRenderDimensions dimensions, LengthReader.FontSource font) =>
        Read(text, dimensions, font, ClipBox.Margin, shapesOnly: true);

    private static ClipPathShape? Read(
        String? text,
        IRenderDimensions dimensions,
        LengthReader.FontSource font,
        ClipBox stated,
        Boolean shapesOnly)
    {
        var value = text?.Trim();

        if (value is null || value.Length == 0 || Is(value, "none"))
        {
            return null;
        }

        // A box on its own is the whole of the value. Anything else is a shape with an optional box beside
        // it, and CSS Masking 1 §5.1 gives the two as `<basic-shape> || <geometry-box>`, so either may come
        // first.
        if (BoxNamed(value, shapesOnly) is { } whole)
        {
            return new ClipPathShape { Form = ClipForm.Box, Box = whole };
        }

        var terms = new Reader(dimensions, font);
        var (shape, box) = TakeBox(value, stated, shapesOnly);

        var open = shape.IndexOf('(');
        var last = shape.Length - 1;

        if (open <= 0 || last < open || shape[last] != ')')
        {
            return null;
        }

        var body = shape[(open + 1)..last];

        switch (shape[..open].Trim().ToLowerInvariant())
        {
            case "inset":
                return ReadInsets(body, terms, box, edges: false);

            case "rect":
                return ReadInsets(body, terms, box, edges: true);

            case "xywh":
                return ReadXywh(body, terms, box);

            case "circle":
                return ReadRadial(body, terms, box, circle: true);

            case "ellipse":
                return ReadRadial(body, terms, box, circle: false);

            case "polygon":
                return ReadPolygon(body, terms, box);

            case "path":
                return ReadPath(body, terms, box);

            default:
                return null;
        }
    }

    /// <summary>Splits the value into the shape it wrote and the box it named, whichever end the box is on.</summary>
    private static (String Shape, ClipBox Box) TakeBox(String value, ClipBox stated, Boolean shapesOnly)
    {
        var words = Words(value);

        if (words.Count > 1)
        {
            if (BoxNamed(words[0], shapesOnly) is { } first)
            {
                return (Join(words, 1, words.Count), first);
            }

            if (BoxNamed(words[^1], shapesOnly) is { } last)
            {
                return (Join(words, 0, words.Count - 1), last);
            }
        }

        return (value, stated);
    }

    /// <summary>Reads <c>inset()</c>, and <c>rect()</c>, which names the same four edges as places.</summary>
    private static ClipPathShape? ReadInsets(String body, Reader terms, ClipBox box, Boolean edges)
    {
        var (sides, round) = TakeRound(body);
        var words = Words(sides);

        if (words.Count is 0 or > 4)
        {
            return null;
        }

        // The insets follow the margin shorthand: an omitted third takes the first and an omitted fourth the
        // second, which is what makes `inset(10px 20px)` one pair of distances repeated (CSS Shapes 1 §3.1).
        var top = terms.Read(words[0]);
        var right = words.Count > 1 ? terms.Read(words[1]) : top;
        var bottom = words.Count > 2 ? terms.Read(words[2]) : top;
        var left = words.Count > 3 ? terms.Read(words[3]) : right;

        if (top is null || right is null || bottom is null || left is null)
        {
            return null;
        }

        var shape = new ClipPathShape
        {
            Form = ClipForm.Rectangle,
            Box = box,
            InsetTop = top.Value,
            InsetRight = right.Value,
            InsetBottom = bottom.Value,
            InsetLeft = left.Value,
            SidesAreEdges = edges,
        };

        return round is null ? shape : WithCorners(shape, round, terms) ? shape : null;
    }

    /// <summary>Reads <c>xywh()</c>, which gives the rectangle's own place and size rather than its edges.</summary>
    private static ClipPathShape? ReadXywh(String body, Reader terms, ClipBox box)
    {
        var (sides, round) = TakeRound(body);
        var words = Words(sides);

        if (words.Count != 4)
        {
            return null;
        }

        var x = terms.Read(words[0]);
        var y = terms.Read(words[1]);
        var width = terms.Read(words[2]);
        var height = terms.Read(words[3]);

        // The two sizes are the grammar's `<length-percentage [0,∞]>`: a negative one is no size at all, and
        // a declaration that says none is dropped rather than drawn as an empty cut.
        if (x is null || y is null || width is null || height is null ||
            Negative(width.Value) || Negative(height.Value))
        {
            return null;
        }

        var shape = new ClipPathShape
        {
            Form = ClipForm.Rectangle,
            Box = box,
            OffsetX = x.Value,
            OffsetY = y.Value,
            ExtentX = width.Value,
            ExtentY = height.Value,
            PlacedBySize = true,
        };

        return round is null ? shape : WithCorners(shape, round, terms) ? shape : null;
    }

    /// <summary>
    /// Reads <c>circle()</c> or <c>ellipse()</c>: a radius per axis, or the edge or corner it reaches to,
    /// beside the place it is centred on.
    /// </summary>
    private static ClipPathShape? ReadRadial(String body, Reader terms, ClipBox box, Boolean circle)
    {
        var words = Words(body);
        var at = words.FindIndex(word => Is(word, "at"));

        var radii = at < 0 ? words : words.GetRange(0, at);
        var place = at < 0 ? new List<String>() : words.GetRange(at + 1, words.Count - at - 1);

        if (radii.Count is > 2 || (at == 0 && radii.Count == 0 && place.Count == 0))
        {
            return null;
        }

        var shape = new ClipPathShape
        {
            Form = ClipForm.Ellipse,
            Box = box,
            OneRadius = circle,
        };

        // Each axis takes either the length the page wrote or the reach it named, and the two may arrive in
        // either order; a circle has one radius standing for both of them.
        StyleLength? first = null;
        StyleLength? second = null;
        ClipReach? near = null;
        ClipReach? beyond = null;

        foreach (var word in radii)
        {
            if (ReadReach(word) is { } named)
            {
                if (near is null)
                {
                    near = named;
                }
                else
                {
                    beyond = named;
                }

                continue;
            }

            if (terms.Read(word) is not { } length)
            {
                return null;
            }

            if (first is null)
            {
                first = length;
            }
            else
            {
                second = length;
            }
        }

        if (circle && (second is not null || beyond is not null))
        {
            // One radius is what a circle has; a second, however it was written, is the grammar saying no.
            return null;
        }

        if (!circle && ((first is null) != (second is null)))
        {
            // An ellipse writes both of its radii or neither of them.
            return null;
        }

        if (circle)
        {
            // A circle left with no radius of its own reaches the farthest corner, which is the one reading
            // of it that still covers the box it was written on.
            shape.RadiusHorizontal = first;
            shape.ReachHorizontal = first is null ? near ?? ClipReach.FarthestCorner : null;

            return ReadPlace(shape, place, terms) ? shape : null;
        }

        shape.RadiusHorizontal = first;
        shape.RadiusVertical = second;
        shape.ReachHorizontal = first is null ? near ?? ClipReach.FarthestSide : null;
        shape.ReachVertical = second is null ? beyond ?? near ?? ClipReach.FarthestSide : null;

        return ReadPlace(shape, place, terms) ? shape : null;
    }

    /// <summary>Turns a `<position>` into the two shares of the box the centre sits at.</summary>
    private static Boolean ReadPlace(ClipPathShape shape, List<String> place, Reader terms)
    {
        var x = StyleLength.FromPercent(0.5);
        var y = StyleLength.FromPercent(0.5);
        var sides = false;
        var ends = false;

        for (var index = 0; index < place.Count; index++)
        {
            var word = place[index];

            if (Is(word, "left") || Is(word, "right"))
            {
                if (sides)
                {
                    return false;
                }

                var edge = Is(word, "left") ? 0.0 : 1.0;
                x = StyleLength.FromPercent(edge);

                if (Placement(place, index, edge, terms, out var offset))
                {
                    x = offset ?? x;
                    index++;
                }

                sides = true;
            }
            else if (Is(word, "top") || Is(word, "bottom"))
            {
                if (ends)
                {
                    return false;
                }

                var edge = Is(word, "top") ? 0.0 : 1.0;
                y = StyleLength.FromPercent(edge);

                if (Placement(place, index, edge, terms, out var offset))
                {
                    y = offset ?? y;
                    index++;
                }

                ends = true;
            }
            else if (Is(word, "center") || Is(word, "centre"))
            {
                // One bare `center` is the middle of both axes; beside a keyword of one axis it is the middle
                // of the axis that keyword left out.
                if (!sides)
                {
                    x = StyleLength.FromPercent(0.5);
                    sides = true;
                }
                else if (!ends)
                {
                    y = StyleLength.FromPercent(0.5);
                    ends = true;
                }
                else
                {
                    return false;
                }
            }
            else if (terms.Read(word) is not { } length)
            {
                return false;
            }
            else if (!sides)
            {
                x = length;
                sides = true;
            }
            else if (!ends)
            {
                y = length;
                ends = true;
            }
            else
            {
                return false;
            }
        }

        shape.CenterX = x;
        shape.CenterY = y;

        return true;
    }

    /// <summary>Gets whether a word names an edge or the middle rather than a distance.</summary>
    private static Boolean SideWord(String word) =>
        Is(word, "left") || Is(word, "right") || Is(word, "top") || Is(word, "bottom") ||
        Is(word, "center") || Is(word, "centre");

    /// <summary>
    /// Gets the centre an edge names when a distance stands after it: <c>left 40px</c> is forty pixels in
    /// from the left edge and <c>right 40px</c> forty in from the right (CSS Shapes 1 §3.3, and the
    /// position syntax CSS Backgrounds 3 §3.6 gives it).
    /// </summary>
    /// <remarks>
    /// False means the next word is not a distance at all, which is the two-value form - <c>left top</c>.
    /// A distance the arithmetic cannot move - a <c>calc()</c> measured from the right edge - is taken
    /// as read and leaves the centre on the edge.
    /// </remarks>
    private static Boolean Placement(
        List<String> place,
        Int32 index,
        Double edge,
        Reader terms,
        out StyleLength? centre)
    {
        centre = null;

        if (index + 1 >= place.Count || SideWord(place[index + 1]) ||
            terms.Read(place[index + 1]) is not { } distance)
        {
            return false;
        }

        if (edge <= 0)
        {
            centre = StyleLength.Sum(new[] { StyleLength.FromPercent(0), distance });
            return true;
        }

        var away = distance.Kind switch
        {
            StyleLengthKind.Pixels => StyleLength.FromPixels(-distance.Value),
            StyleLengthKind.Percent => StyleLength.FromPercent(-distance.Value),
            _ => (StyleLength?)null,
        };

        if (away is { } inward)
        {
            centre = StyleLength.Sum(new[] { StyleLength.FromPercent(1), inward });
        }

        return true;
    }

    /// <summary>Reads <c>polygon()</c>, whose points come in pairs and may be given either winding rule.</summary>
    private static ClipPathShape? ReadPolygon(String body, Reader terms, ClipBox box)
    {
        var parts = Pieces(body);

        if (parts.Count == 0)
        {
            return null;
        }

        var evenOdd = false;

        if (Is(parts[0], "evenodd") || Is(parts[0], "nonzero"))
        {
            evenOdd = Is(parts[0], "evenodd");
            parts.RemoveAt(0);

            if (parts.Count == 0)
            {
                return null;
            }
        }

        // The corner-rounding argument turns the shape's own edges by a length. It is the one part of this
        // grammar the corpus never asks for, and reading past it would draw a shape with square corners.
        if (Is(parts[0], "round"))
        {
            return null;
        }

        var points = new List<StyleLength>(parts.Count * 2);

        foreach (var part in parts)
        {
            var pair = Words(part);

            if (pair.Count != 2)
            {
                return null;
            }

            if (terms.Read(pair[0]) is not { } x || terms.Read(pair[1]) is not { } y)
            {
                return null;
            }

            points.Add(x);
            points.Add(y);
        }

        // Three points are the fewest an outline encloses anything with.
        if (points.Count < 6)
        {
            return null;
        }

        return new ClipPathShape
        {
            Form = ClipForm.Polygon,
            Box = box,
            EvenOdd = evenOdd,
            Points = points.ToArray(),
        };
    }

    /// <summary>Reads <c>path()</c>, whose own coordinates are the one part of a shape written in pixels.</summary>
    private static ClipPathShape? ReadPath(String body, Reader terms, ClipBox box)
    {
        var parts = Pieces(body);
        var evenOdd = false;

        if (parts.Count == 2 && (Is(parts[0], "evenodd") || Is(parts[0], "nonzero")))
        {
            evenOdd = Is(parts[0], "evenodd");
            parts.RemoveAt(0);
        }

        if (parts.Count != 1 || Unquote(parts[0]) is not { } data)
        {
            return null;
        }

        var outline = ReadOutline(data);

        if (outline is null || outline.Count == 0)
        {
            return null;
        }

        if (outline[0].Edge != ClipEdge.Begin)
        {
            // A path that opens on a curve still starts somewhere, and where it starts is the origin of
            // the coordinate system the string was written in.
            outline.Insert(0, ClipSegment.To(ClipEdge.Begin, 0, 0));
        }

        return new ClipPathShape
        {
            Form = ClipForm.Path,
            Box = box,
            EvenOdd = evenOdd,
            Segments = outline,
        };
    }

    /// <summary>
    /// Reads an SVG path out of the string it was written in, turning every curve into the cubic the clip is
    /// drawn with and carrying a smooth point on from the curve before it.
    /// </summary>
    /// <remarks>
    /// An elliptical arc is the piece left out: its own numbers do not say where the edge goes until they are
    /// solved over, and a shape that uses one is read as no shape rather than as a shape drawn wrong.
    /// </remarks>
    private static List<ClipSegment>? ReadOutline(String data)
    {
        var outline = new List<ClipSegment>();
        var index = 0;
        var x = 0.0;
        var y = 0.0;
        var anchorX = 0.0;
        var anchorY = 0.0;
        var controlX = 0.0;
        var controlY = 0.0;
        var control = false;
        var command = '\0';

        while (index < data.Length)
        {
            var character = data[index];

            if (Char.IsWhiteSpace(character) || character == ',')
            {
                index++;
                continue;
            }

            if (Char.IsLetter(character))
            {
                command = character;
                index++;

                if (command is 'Z' or 'z')
                {
                    outline.Add(ClipSegment.Closed);
                    x = anchorX;
                    y = anchorY;
                    control = false;
                    command = '\0';
                }

                continue;
            }

            if (command == '\0')
            {
                return null;
            }

            var relative = Char.IsLower(command);
            var kind = Char.ToUpperInvariant(command);

            switch (kind)
            {
                case 'M':
                {
                    if (!Number(data, ref index, out var to) || !Number(data, ref index, out var past))
                    {
                        return null;
                    }

                    x = relative ? x + to : to;
                    y = relative ? y + past : past;
                    outline.Add(ClipSegment.To(ClipEdge.Begin, x, y));
                    anchorX = x;
                    anchorY = y;
                    control = false;

                    // Numbers written again after a move draw lines from where the move left off.
                    command = relative ? 'l' : 'L';
                    break;
                }

                case 'L':
                {
                    if (!Number(data, ref index, out var to) || !Number(data, ref index, out var past))
                    {
                        return null;
                    }

                    x = relative ? x + to : to;
                    y = relative ? y + past : past;
                    outline.Add(ClipSegment.To(ClipEdge.Line, x, y));
                    control = false;
                    break;
                }

                case 'H':
                {
                    if (!Number(data, ref index, out var to))
                    {
                        return null;
                    }

                    x = relative ? x + to : to;
                    outline.Add(ClipSegment.To(ClipEdge.Line, x, y));
                    control = false;
                    break;
                }

                case 'V':
                {
                    if (!Number(data, ref index, out var past))
                    {
                        return null;
                    }

                    y = relative ? y + past : past;
                    outline.Add(ClipSegment.To(ClipEdge.Line, x, y));
                    control = false;
                    break;
                }

                case 'C':
                {
                    if (!Number(data, ref index, out var one) || !Number(data, ref index, out var two) ||
                        !Number(data, ref index, out var three) || !Number(data, ref index, out var four) ||
                        !Number(data, ref index, out var to) || !Number(data, ref index, out var past))
                    {
                        return null;
                    }

                    controlX = relative ? x + one : one;
                    controlY = relative ? y + two : two;
                    var secondX = relative ? x + three : three;
                    var secondY = relative ? y + four : four;

                    x = relative ? x + to : to;
                    y = relative ? y + past : past;

                    outline.Add(ClipSegment.Curving(x, y, controlX, controlY, secondX, secondY));
                    controlX = secondX;
                    controlY = secondY;
                    control = true;
                    break;
                }

                case 'S':
                {
                    if (!Number(data, ref index, out var three) || !Number(data, ref index, out var four) ||
                        !Number(data, ref index, out var to) || !Number(data, ref index, out var past))
                    {
                        return null;
                    }

                    // The curve's first control is the second one of the curve before it, stood on its head
                    // about the point the two share - and where there was no such curve, the point itself.
                    var firstX = control ? 2 * x - controlX : x;
                    var firstY = control ? 2 * y - controlY : y;

                    controlX = relative ? x + three : three;
                    controlY = relative ? y + four : four;

                    x = relative ? x + to : to;
                    y = relative ? y + past : past;

                    outline.Add(ClipSegment.Curving(x, y, firstX, firstY, controlX, controlY));
                    control = true;
                    break;
                }

                case 'Q':
                case 'T':
                {
                    var wrote = kind == 'Q';
                    var fromX = x;
                    var fromY = y;
                    Double focusX;
                    Double focusY;

                    if (wrote)
                    {
                        if (!Number(data, ref index, out var one) ||
                            !Number(data, ref index, out var two) ||
                            !Number(data, ref index, out var to) ||
                            !Number(data, ref index, out var past))
                        {
                            return null;
                        }

                        focusX = relative ? fromX + one : one;
                        focusY = relative ? fromY + two : two;

                        x = relative ? fromX + to : to;
                        y = relative ? fromY + past : past;
                    }
                    else
                    {
                        if (!Number(data, ref index, out var to) ||
                            !Number(data, ref index, out var past))
                        {
                            return null;
                        }

                        // A smooth quadratic's focus is the one the curve before it was drawn with, stood on
                        // its head about the point the two share - and where there was no such curve, that
                        // point itself, which is a straight edge.
                        focusX = control ? 2 * fromX - controlX : fromX;
                        focusY = control ? 2 * fromY - controlY : fromY;

                        x = relative ? fromX + to : to;
                        y = relative ? fromY + past : past;
                    }

                    // A quadratic edge is the cubic whose two controls each stand two thirds of the way from
                    // their end toward the focus (SVG 1.1 §8.3), which is what lets one kind of curve reach
                    // the canvas.
                    outline.Add(ClipSegment.Curving(
                        x,
                        y,
                        fromX + (2 / 3.0) * (focusX - fromX),
                        fromY + (2 / 3.0) * (focusY - fromY),
                        x + (2 / 3.0) * (focusX - x),
                        y + (2 / 3.0) * (focusY - y)));

                    controlX = focusX;
                    controlY = focusY;
                    control = true;
                    break;
                }

                default:
                    return null;
            }
        }

        return outline;
    }

    /// <summary>Reads one number of a path, which may be pressed against the one before it.</summary>
    private static Boolean Number(String data, ref Int32 index, out Double value)
    {
        value = 0;

        while (index < data.Length && (Char.IsWhiteSpace(data[index]) || data[index] == ','))
        {
            index++;
        }

        var start = index;

        if (index < data.Length && data[index] is '-' or '+')
        {
            index++;
        }

        var digits = 0;
        var dots = 0;

        while (index < data.Length)
        {
            var character = data[index];

            if (Char.IsDigit(character))
            {
                digits++;
                index++;
            }
            else if (character == '.' && dots == 0)
            {
                dots++;
                index++;
            }
            else if ((character is 'e' or 'E') && digits > 0)
            {
                index++;

                if (index < data.Length && data[index] is '-' or '+')
                {
                    index++;
                }
            }
            else
            {
                break;
            }
        }

        return digits + dots > 0 &&
               Double.TryParse(
                   data[start..index],
                   NumberStyles.Float,
                   CultureInfo.InvariantCulture,
                   out value);
    }

    /// <summary>
    /// Splits a value into the four edges and the <c>round</c> argument, which the grammar puts at the end of
    /// the list rather than behind a comma.
    /// </summary>
    private static (String Sides, String? Round) TakeRound(String body)
    {
        var words = Words(body);
        var at = words.FindIndex(word => Is(word, "round"));

        if (at < 0)
        {
            return (body, null);
        }

        return (Join(words, 0, at), Join(words, at + 1, words.Count));
    }

    /// <summary>
    /// Reads the corners a rectangle was asked to round off, which are the <c>border-radius</c> shorthand
    /// with a slash between the two axes of each corner.
    /// </summary>
    private static Boolean WithCorners(ClipPathShape shape, String round, Reader terms)
    {
        var halves = round.Split('/', StringSplitOptions.RemoveEmptyEntries);

        if (halves.Length is 0 or > 2)
        {
            return false;
        }

        var horizontal = Words(halves[0]);
        var vertical = halves.Length > 1 ? Words(halves[1]) : horizontal;

        if (horizontal.Count is 0 or > 4 || vertical.Count is 0 or > 4)
        {
            return false;
        }

        if (!ReadCorners(horizontal, vertical, terms, out var corners))
        {
            return false;
        }

        shape.Corners = corners;
        return true;
    }

    private static Boolean ReadCorners(
        List<String> horizontal,
        List<String> vertical,
        Reader terms,
        out StyleCornerRadii corners)
    {
        var x = new StyleLength[4];
        var y = new StyleLength[4];

        for (var index = 0; index < horizontal.Count; index++)
        {
            if (terms.Read(horizontal[index]) is not { } width || terms.Read(vertical[index]) is not { } height)
            {
                corners = default;
                return false;
            }

            x[index] = width;
            y[index] = height;
        }

        // Four corners, named once to four times, filled in the order the shorthand fills them.
        if (horizontal.Count < 2)
        {
            x[1] = x[0];
            y[1] = y[0];
        }

        x[2] = horizontal.Count > 2 ? x[2] : x[0];
        y[2] = horizontal.Count > 2 ? y[2] : y[0];
        x[3] = horizontal.Count > 3 ? x[3] : horizontal.Count == 3 ? x[1] : x[0];
        y[3] = horizontal.Count > 3 ? y[3] : horizontal.Count == 3 ? y[1] : y[0];

        corners = new StyleCornerRadii(
            new StyleCornerRadius(x[0], y[0]),
            new StyleCornerRadius(x[1], y[1]),
            new StyleCornerRadius(x[2], y[2]),
            new StyleCornerRadius(x[3], y[3]));

        return true;
    }

    /// <summary>Gets the reach a radius was left to, or null where the word names something else.</summary>
    private static ClipReach? ReadReach(String word) =>
        word.ToLowerInvariant() switch
        {
            "closest-side" => ClipReach.ClosestSide,
            "farthest-side" => ClipReach.FarthestSide,
            "closest-corner" => ClipReach.ClosestCorner,
            "farthest-corner" => ClipReach.FarthestCorner,
            _ => null,
        };

    private static Boolean Negative(StyleLength length) =>
        length.Kind == StyleLengthKind.Pixels && length.Value < 0;

    private static Boolean Is(String word, String name) =>
        word.Equals(name, StringComparison.OrdinalIgnoreCase);

    private static String? Unquote(String text)
    {
        var trimmed = text.Trim();

        if (trimmed.Length < 2 || (trimmed[0] != '"' && trimmed[0] != '\'') || trimmed[^1] != trimmed[0])
        {
            return null;
        }

        return trimmed[1..^1];
    }

    /// <summary>Splits a value at the white space and the commas that are not inside a function or a string.</summary>
    private static List<String> Words(String text) => Split(text, commasOnly: false);

    /// <summary>Splits a list at the commas that are not inside a function or a string.</summary>
    private static List<String> Pieces(String text) => Split(text, commasOnly: true);

    private static List<String> Split(String text, Boolean commasOnly)
    {
        var parts = new List<String>();
        var buffer = new StringBuilder();
        var depth = 0;
        var quote = '\0';

        void Flush()
        {
            var word = buffer.ToString().Trim();

            if (word.Length > 0)
            {
                parts.Add(word);
            }

            buffer.Clear();
        }

        foreach (var character in text)
        {
            if (quote != '\0')
            {
                buffer.Append(character);

                if (character == quote)
                {
                    quote = '\0';
                }

                continue;
            }

            if (character is '"' or '\'')
            {
                quote = character;
                buffer.Append(character);
                continue;
            }

            if (character == '(')
            {
                depth++;
            }
            else if (character == ')')
            {
                depth--;
            }
            else if (depth == 0 && (character == ',' || (!commasOnly && Char.IsWhiteSpace(character))))
            {
                Flush();
                continue;
            }

            buffer.Append(character);
        }

        Flush();
        return parts;
    }

    private static String Join(List<String> words, Int32 from, Int32 to) =>
        String.Join(" ", words.GetRange(from, Math.Max(0, to - from)));

    /// <summary>Turns one word of a shape into a length, folding the units the page wrote it in.</summary>
    private sealed class Reader
    {
        private readonly IRenderDimensions _dimensions;
        private readonly LengthReader.FontSource _font;

        public Reader(IRenderDimensions dimensions, LengthReader.FontSource font)
        {
            _dimensions = dimensions;
            _font = font;
        }

        public StyleLength? Read(String text)
        {
            var trimmed = text.Trim();

            if (trimmed.Length == 0)
            {
                return null;
            }

            // A function argument keeps its unit, so a percentage among them is still a percentage when the
            // box it belongs to is measured - which is the whole of what the shape is waiting for.
            if (CssMathLengths.Evaluate(trimmed, Fold) is { } math)
            {
                return math;
            }

            // The arithmetic of a `calc()` is read as words rather than folded by the CSSOM, which resolves
            // every percentage inside it against the viewport: a point is a share of the box the shape is
            // cut against, and the style has not been told what that box is.
            if (CalcText.Read(trimmed, Fold) is { } calc)
            {
                return CalcText.ToLength(calc);
            }

            return Fold(trimmed);
        }

        private StyleLength? Fold(String atom) =>
            LengthReader.FromText(atom, RenderMode.Horizontal, _dimensions, _font);
    }
}

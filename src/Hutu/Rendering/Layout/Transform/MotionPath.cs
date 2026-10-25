using System;
using System.Collections.Generic;
using System.Globalization;

namespace Hutu.Rendering.Layout;

/// <summary>
/// An <em>offset path</em> from CSS Motion Path, kept as straight segments and elliptical arcs so a point
/// and a direction can be read off it at a distance. Built from <c>offset-path</c>'s <c>path()</c> (SVG path
/// data), the equivalent paths of the basic shapes, or a <c>ray()</c>.
/// </summary>
/// <remarks>
/// An arc keeps its exact geometry: the distance into it is turned back into the angle it stands at, so a
/// quarter of a circle lands on the quarter rather than on a chord near it - which matters in a reftest,
/// where a box half a pixel off is a page that fails. Bézier curves are still sampled into short straight
/// segments; the distance along the path is measured over what is there (Motion Path §path-distance leaves
/// the sampling to the engine). A closed path counts its closing segment, as SVG's direction of a path does.
/// </remarks>
internal sealed class MotionPath
{
    private readonly List<Double> _x = new();
    private readonly List<Double> _y = new();

    /// <summary>The exact direction where each segment starts and ends, which is the segment's own angle
    /// for a line and the curve's derivative at the ends of an arc.</summary>
    private readonly List<Double> _fromAngle = new();
    private readonly List<Double> _toAngle = new();

    /// <summary>The elliptical arc each segment is, or null where it is a straight one.</summary>
    private readonly List<ArcSegment?> _arcs = new();

    private Double[] _cumulative = Array.Empty<Double>();
    private Boolean _closed;

    /// <summary>One exact elliptical arc, in the path's own coordinate system.</summary>
    private sealed class ArcSegment
    {
        internal Double Cx;
        internal Double Cy;
        internal Double Rx;
        internal Double Ry;
        internal Double Cos;
        internal Double Sin;
        internal Double Start;
        internal Double Sweep;
        internal Double Length;
    }

    private MotionPath()
    {
    }

    /// <summary>Gets the total length of the path; a point at <c>offset-distance</c> is read against it.</summary>
    public Double Total { get; private set; }

    /// <summary>Gets whether the path ends where it began, which makes its distance run as a loop.</summary>
    public Boolean Closed => _closed;

    /// <summary>
    /// The point at a distance along the path, with the direction there (the tangent, as the angle the
    /// segment makes with the positive x-axis). The distance is clamped into the path.
    /// </summary>
    public (Double X, Double Y, Double Angle) PointAt(Double distance)
    {
        if (_x.Count == 0)
        {
            return (0, 0, 0);
        }

        if (_arcs.Count == 0)
        {
            return (_x[0], _y[0], 0);
        }

        var d = Math.Clamp(distance, 0, Total);
        var segment = 0;

        while (segment < _arcs.Count - 1 && _cumulative[segment + 1] < d - 1e-9)
        {
            segment++;
        }

        var span = _cumulative[segment + 1] - _cumulative[segment];
        var local = d - _cumulative[segment];
        var t = span > 0 ? local / span : 0;

        if (_arcs[segment] is { } arc)
        {
            return PointOnArc(arc, local);
        }

        var dx = _x[segment + 1] - _x[segment];
        var dy = _y[segment + 1] - _y[segment];

        // A sampled segment knows the direction at both ends, so the direction inside it is read between
        // them rather than from the chord.
        var angle = Math.Atan2(dy, dx);

        if (segment < _fromAngle.Count)
        {
            var sweep = _toAngle[segment] - _fromAngle[segment];

            while (sweep > Math.PI)
            {
                sweep -= 2 * Math.PI;
            }

            while (sweep < -Math.PI)
            {
                sweep += 2 * Math.PI;
            }

            angle = _fromAngle[segment] + sweep * t;
        }

        return (
            _x[segment] + dx * t,
            _y[segment] + dy * t,
            angle);
    }

    private static (Double X, Double Y, Double Angle) PointOnArc(ArcSegment arc, Double local)
    {
        var fraction = arc.Length > 0 ? Math.Clamp(local / arc.Length, 0, 1) : 0;

        // The angle is found by bisecting the arc length rather than by dividing it, because an ellipse's
        // length is not proportional to its angle.
        if (arc.Length > 0 && fraction > 0 && fraction < 1)
        {
            var lo = 0.0;
            var hi = 1.0;

            for (var i = 0; i < 60; i++)
            {
                var mid = (lo + hi) / 2;

                if (ArcLength(arc, mid) < local)
                {
                    lo = mid;
                }
                else
                {
                    hi = mid;
                }
            }

            fraction = (lo + hi) / 2;
        }

        var theta = arc.Start + arc.Sweep * fraction;
        var cos = Math.Cos(theta);
        var sin = Math.Sin(theta);

        var x = arc.Cx + arc.Cos * arc.Rx * cos - arc.Sin * arc.Ry * sin;
        var y = arc.Cy + arc.Sin * arc.Rx * cos + arc.Cos * arc.Ry * sin;

        var dx = -arc.Cos * arc.Rx * sin - arc.Sin * arc.Ry * cos;
        var dy = -arc.Sin * arc.Rx * sin + arc.Cos * arc.Ry * cos;

        return (x, y, Math.Atan2(dy, dx));
    }

    /// <summary>The length of the arc from its start to <paramref name="fraction"/> of its sweep.</summary>
    private static Double ArcLength(ArcSegment arc, Double fraction)
    {
        var to = arc.Start + arc.Sweep * fraction;

        if (Math.Abs(arc.Rx - arc.Ry) < 1e-12)
        {
            return Math.Abs(arc.Rx * (to - arc.Start));
        }

        var sign = arc.Sweep < 0 ? -1 : 1;
        var from = arc.Start;
        var end = to;

        return sign > 0
            ? Integrate(t => Math.Sqrt(Sq(arc.Rx * Math.Sin(t)) + Sq(arc.Ry * Math.Cos(t))), from, end)
            : Integrate(t => Math.Sqrt(Sq(arc.Rx * Math.Sin(t)) + Sq(arc.Ry * Math.Cos(t))), end, from);
    }

    /// <summary>Adaptive Simpson's rule, which integrates the ellipse's speed closely enough that the
    /// point on it lands where the exact angle says.</summary>
    private static Double Integrate(Func<Double, Double> f, Double a, Double b)
    {
        var fa = f(a);
        var fb = f(b);
        var m = (a + b) / 2;
        var fm = f(m);
        var whole = (b - a) / 6 * (fa + 4 * fm + fb);

        return Refine(f, a, b, fa, fm, fb, whole, 1e-13, 40);
    }

    private static Double Refine(Func<Double, Double> f, Double a, Double b, Double fa, Double fm, Double fb, Double whole, Double tolerance, Int32 depth)
    {
        var m = (a + b) / 2;
        var lm = (a + m) / 2;
        var rm = (m + b) / 2;
        var flm = f(lm);
        var frm = f(rm);
        var left = (m - a) / 6 * (fa + 4 * flm + fm);
        var right = (b - m) / 6 * (fm + 4 * frm + fb);
        var sum = left + right;

        if (depth <= 0 || Math.Abs(sum - whole) <= tolerance)
        {
            return sum + (sum - whole) / 15;
        }

        return Refine(f, a, m, fa, flm, fm, left, tolerance / 2, depth - 1) +
               Refine(f, m, b, fm, frm, fb, right, tolerance / 2, depth - 1);
    }

    private void Finish()
    {
        _cumulative = new Double[_arcs.Count + 1];
        var running = 0.0;

        for (var i = 0; i < _arcs.Count; i++)
        {
            running += _arcs[i]?.Length ?? Math.Sqrt(Sq(_x[i + 1] - _x[i]) + Sq(_y[i + 1] - _y[i]));
            _cumulative[i + 1] = running;
        }

        Total = running;
    }

    private void Move(Double x, Double y)
    {
        if (_x.Count > 0 && !_closed)
        {
            // A path with more than one sub-path is unusual for offset-path; keep the first continuous
            // run, which is what the distance is measured along.
        }

        _x.Add(x);
        _y.Add(y);
    }

    private void Line(Double x, Double y)
    {
        if (_x.Count == 0)
        {
            Move(x, y);
            return;
        }

        var angle = Math.Atan2(y - _y[^1], x - _x[^1]);
        _x.Add(x);
        _y.Add(y);
        _fromAngle.Add(angle);
        _toAngle.Add(angle);
        _arcs.Add(null);
    }

    /// <summary>Adds an exact elliptical arc as one segment, with the direction the curve has at each end
    /// of the step.</summary>
    private void ArcStep(ArcSegment arc, Double x, Double y, Double fromAngle, Double toAngle)
    {
        _x.Add(x);
        _y.Add(y);
        _fromAngle.Add(fromAngle);
        _toAngle.Add(toAngle);
        _arcs.Add(arc);
    }

    private void Close()
    {
        _closed = true;

        // Return to the start of the whole path for the closing segment's length and direction.
        if (_x.Count > 0)
        {
            Line(_x[0], _y[0]);
        }
    }

    /// <summary>Builds a path from SVG path data (the <c>path('…)</c> argument).</summary>
    public static MotionPath FromPathData(String? data)
    {
        var path = new MotionPath();

        if (String.IsNullOrWhiteSpace(data))
        {
            path.Finish();
            return path;
        }

        var tokens = Tokenize(data);
        var i = 0;
        Double cx = 0, cy = 0, sx = 0, sy = 0;
        Double lastCx = 0, lastCy = 0, lastQx = 0, lastQy = 0;
        var relative = true;
        var command = ' ';

        while (i < tokens.Count)
        {
            if (!IsNumber(tokens[i]))
            {
                command = Char.ToLower(tokens[i][0], CultureInfo.InvariantCulture);
                relative = Char.IsLower(tokens[i][0]);
                i++;

                if (command == 'z')
                {
                    path.Close();
                    cx = sx;
                    cy = sy;
                    continue;
                }
            }

            Double N() => Parse(tokens[i++]);

            switch (command)
            {
                case 'm':
                    cx = N() + (relative ? cx : 0);
                    cy = N() + (relative ? cy : 0);
                    path.Move(cx, cy);
                    sx = cx;
                    sy = cy;
                    command = relative ? 'l' : 'L';
                    break;

                case 'l':
                    cx = N() + (relative ? cx : 0);
                    cy = N() + (relative ? cy : 0);
                    path.Line(cx, cy);
                    break;

                case 'h':
                    cx = N() + (relative ? cx : 0);
                    path.Line(cx, cy);
                    break;

                case 'v':
                    cy = N() + (relative ? cy : 0);
                    path.Line(cx, cy);
                    break;

                case 'c': {
                    var x1 = N() + (relative ? cx : 0);
                    var y1 = N() + (relative ? cy : 0);
                    var x2 = N() + (relative ? cx : 0);
                    var y2 = N() + (relative ? cy : 0);
                    var x = N() + (relative ? cx : 0);
                    var y = N() + (relative ? cy : 0);
                    Curve(path, cx, cy, x1, y1, x2, y2, x, y);
                    lastCx = x2;
                    lastCy = y2;
                    cx = x;
                    cy = y;
                    break;
                }

                case 's': {
                    var (r1x, r1y) = relative ? (2 * cx - lastCx, 2 * cy - lastCy) : (N(), N());
                    var x2 = N() + (relative ? cx : 0);
                    var y2 = N() + (relative ? cy : 0);
                    var x = N() + (relative ? cx : 0);
                    var y = N() + (relative ? cy : 0);
                    Curve(path, cx, cy, r1x, r1y, x2, y2, x, y);
                    lastCx = x2;
                    lastCy = y2;
                    cx = x;
                    cy = y;
                    break;
                }

                case 'q': {
                    var x1 = N() + (relative ? cx : 0);
                    var y1 = N() + (relative ? cy : 0);
                    var x = N() + (relative ? cx : 0);
                    var y = N() + (relative ? cy : 0);
                    Quad(path, cx, cy, x1, y1, x, y);
                    lastQx = x1;
                    lastQy = y1;
                    cx = x;
                    cy = y;
                    break;
                }

                case 't': {
                    var (r1x, r1y) = relative ? (2 * cx - lastQx, 2 * cy - lastQy) : (N(), N());
                    var x = N() + (relative ? cx : 0);
                    var y = N() + (relative ? cy : 0);
                    Quad(path, cx, cy, r1x, r1y, x, y);
                    lastQx = r1x;
                    lastQy = r1y;
                    cx = x;
                    cy = y;
                    break;
                }

                case 'a': {
                    var rx = N();
                    var ry = N();
                    var rot = N();
                    var large = N() != 0;
                    var sweep = N() != 0;
                    var x = N() + (relative ? cx : 0);
                    var y = N() + (relative ? cy : 0);
                    Arc(path, cx, cy, rx, ry, rot, large, sweep, x, y);
                    cx = x;
                    cy = y;
                    break;
                }

                default:
                    i = tokens.Count;
                    break;
            }
        }

        path.Finish();
        return path;
    }

    /// <summary>
    /// The equivalent path of a circle/ellipse (Motion Path §paths): its outline starting at the rightmost
    /// point, four quarter arcs proceeding clockwise, closed. Coordinates are in the path's own system.
    /// </summary>
    public static MotionPath FromEllipse(Double cx, Double cy, Double rx, Double ry)
    {
        var path = new MotionPath();

        if (rx <= 0 || ry <= 0)
        {
            path.Move(cx, cy);
            path.Finish();
            return path;
        }

        path.Move(cx + rx, cy);
        Arc(path, cx + rx, cy, rx, ry, 0, false, true, cx, cy + ry);
        Arc(path, cx, cy + ry, rx, ry, 0, false, true, cx - rx, cy);
        Arc(path, cx - rx, cy, rx, ry, 0, false, true, cx, cy - ry);
        Arc(path, cx, cy - ry, rx, ry, 0, false, true, cx + rx, cy);
        path.Close();
        path.Finish();
        return path;
    }

    /// <summary>The equivalent path of an (optionally rounded) rectangle: from the left end of the top
    /// edge, clockwise (Motion Path §paths).</summary>
    public static MotionPath FromRect(Double x, Double y, Double width, Double height, Double radius)
    {
        var path = new MotionPath();
        var r = Math.Max(0, Math.Min(radius, Math.Min(width, height) / 2));
        var right = x + width;
        var bottom = y + height;

        path.Move(x + r, y);
        path.Line(right - r, y);

        if (r > 0)
        {
            Arc(path, right - r, y, r, r, 0, false, true, right, y + r);
        }

        path.Line(right, bottom - r);

        if (r > 0)
        {
            Arc(path, right, bottom - r, r, r, 0, false, true, right - r, bottom);
        }

        path.Line(x + r, bottom);

        if (r > 0)
        {
            Arc(path, x + r, bottom, r, r, 0, false, true, x, bottom - r);
        }

        path.Line(x, y + r);

        if (r > 0)
        {
            Arc(path, x, y + r, r, r, 0, false, true, x + r, y);
        }

        path.Close();
        path.Finish();
        return path;
    }

    /// <summary>The equivalent path of a polygon: straight segments joining each point, closed.</summary>
    public static MotionPath FromPolygon(IReadOnlyList<(Double X, Double Y)> points)
    {
        var path = new MotionPath();

        if (points.Count == 0)
        {
            path.Finish();
            return path;
        }

        path.Move(points[0].X, points[0].Y);

        for (var i = 1; i < points.Count; i++)
        {
            path.Line(points[i].X, points[i].Y);
        }

        path.Close();
        path.Finish();
        return path;
    }

    /// <summary>A ray: a single segment from the origin along a bearing (0deg up, clockwise positive).</summary>
    public static MotionPath FromRay(Double originX, Double originY, Double bearingRadians, Double length)
    {
        var path = new MotionPath();
        path.Move(originX, originY);
        path.Line(originX + Math.Sin(bearingRadians) * length, originY - Math.Cos(bearingRadians) * length);
        path.Finish();
        return path;
    }

    private static void Curve(MotionPath path, Double x0, Double y0, Double x1, Double y1, Double x2, Double y2, Double x3, Double y3)
    {
        const int Steps = 20;

        for (var s = 1; s <= Steps; s++)
        {
            var t = (Double)s / Steps;
            var u = 1 - t;
            path.Line(
                u * u * u * x0 + 3 * u * u * t * x1 + 3 * u * t * t * x2 + t * t * t * x3,
                u * u * u * y0 + 3 * u * u * t * y1 + 3 * u * t * t * y2 + t * t * t * y3);
        }
    }

    private static void Quad(MotionPath path, Double x0, Double y0, Double x1, Double y1, Double x2, Double y2)
    {
        // A quadratic is a cubic with the control point a third of the way from each end.
        Curve(path, x0, y0, x0 + (2.0 / 3) * (x1 - x0), y0 + (2.0 / 3) * (y1 - y0), x2 + (2.0 / 3) * (x1 - x2), y2 + (2.0 / 3) * (y1 - y2), x2, y2);
    }

    private static void Arc(MotionPath path, Double x0, Double y0, Double rx, Double ry, Double angleDeg, Boolean large, Boolean sweep, Double x, Double y)
    {
        if (rx == 0 || ry == 0)
        {
            path.Line(x, y);
            return;
        }

        rx = Math.Abs(rx);
        ry = Math.Abs(ry);
        var phi = angleDeg * Math.PI / 180;
        var cos = Math.Cos(phi);
        var sin = Math.Sin(phi);

        var dx = (x0 - x) / 2;
        var dy = (y0 - y) / 2;
        var x1p = cos * dx + sin * dy;
        var y1p = -sin * dx + cos * dy;

        var lambda = Sq(x1p) / Sq(rx) + Sq(y1p) / Sq(ry);

        if (lambda > 1)
        {
            var scale = Math.Sqrt(lambda);
            rx *= scale;
            ry *= scale;
        }

        var sign = large == sweep ? -1 : 1;
        var num = Sq(rx) * Sq(y1p) + Sq(ry) * Sq(x1p);
        var radical = Math.Sqrt(Math.Max(0, (Sq(rx) * Sq(ry) - num) / num)) * sign;
        var cxp = radical * rx * y1p / ry;
        var cyp = -radical * ry * x1p / rx;

        var cx = cos * cxp - sin * cyp + (x0 + x) / 2;
        var cy = sin * cxp + cos * cyp + (y0 + y) / 2;

        Double Theta(Double ux, Double uy, Double vx, Double vy)
        {
            var dot = ux * vx + uy * vy;
            var len = Math.Sqrt((ux * ux + uy * uy) * (vx * vx + vy * vy));
            var a = Math.Acos(Math.Clamp(dot / len, -1, 1));

            if (ux * vy - uy * vx < 0)
            {
                a = -a;
            }

            return a;
        }

        var phi1 = Theta(1, 0, (x1p - cxp) / rx, (y1p - cyp) / ry);
        var delta = Theta((x1p - cxp) / rx, (y1p - cyp) / ry, (-x1p - cxp) / rx, (-y1p - cyp) / ry);

        if (!sweep && delta > 0)
        {
            delta -= 2 * Math.PI;
        }
        else if (sweep && delta < 0)
        {
            delta += 2 * Math.PI;
        }

        var arc = new ArcSegment
        {
            Cx = cx,
            Cy = cy,
            Rx = rx,
            Ry = ry,
            Cos = cos,
            Sin = sin,
            Start = phi1,
            Sweep = delta,
        };

        arc.Length = ArcLength(arc, 1);

        var endAngle = phi1 + delta;
        var startTangent = Math.Atan2(
            -sin * rx * Math.Sin(phi1) + cos * ry * Math.Cos(phi1),
            -cos * rx * Math.Sin(phi1) - sin * ry * Math.Cos(phi1));
        var endTangent = Math.Atan2(
            -sin * rx * Math.Sin(endAngle) + cos * ry * Math.Cos(endAngle),
            -cos * rx * Math.Sin(endAngle) - sin * ry * Math.Cos(endAngle));

        path.ArcStep(arc, x, y, startTangent, endTangent);
    }

    private static List<String> Tokenize(String data)
    {
        var tokens = new List<String>();
        var i = 0;

        while (i < data.Length)
        {
            var c = data[i];

            if (Char.IsWhiteSpace(c) || c == ',')
            {
                i++;
                continue;
            }

            if (Char.IsLetter(c))
            {
                tokens.Add(c.ToString());
                i++;
                continue;
            }

            var start = i;

            if (c is '-' or '+' or '.')
            {
                i++;
            }

            var seenDot = c == '.';

            while (i < data.Length)
            {
                var d = data[i];

                if (Char.IsDigit(d))
                {
                    i++;
                }
                else if (d == '.' && !seenDot)
                {
                    seenDot = true;
                    i++;
                }
                else if ((d is 'e' or 'E') && i + 1 < data.Length && (Char.IsDigit(data[i + 1]) || data[i + 1] is '-' or '+'))
                {
                    i += 2;
                }
                else
                {
                    break;
                }
            }

            tokens.Add(data[start..i]);
        }

        return tokens;
    }

    private static Boolean IsNumber(String token) =>
        token.Length > 0 && (Char.IsDigit(token[0]) || token[0] is '-' or '+' or '.');

    private static Double Parse(String token) =>
        Double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;

    private static Double Sq(Double v) => v * v;
}

using System;
using System.Collections.Generic;
using System.Globalization;

namespace Hutu.Rendering.Layout;

/// <summary>
/// An <em>offset path</em> from CSS Motion Path, flattened to a polyline so a point and a direction can
/// be read off it at a distance. Built from <c>offset-path</c>'s <c>path()</c> (SVG path data), the
/// equivalent paths of the basic shapes, or a <c>ray()</c>.
/// </summary>
/// <remarks>
/// Curves and elliptical arcs are sampled into short straight segments; the distance along the path is
/// measured over that polyline (Motion Path §path-distance, which leaves the sampling to the engine). A
/// closed path counts its closing segment, as SVG's direction of a path does.
/// </remarks>
internal sealed class MotionPath
{
    private readonly List<Double> _x = new();
    private readonly List<Double> _y = new();
    private Double[] _cumulative = Array.Empty<Double>();
    private Boolean _closed;

    private MotionPath()
    {
    }

    /// <summary>Gets the total length of the path; a point at <c>offset-distance</c> is read against it.</summary>
    public Double Total { get; private set; }

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

        if (_x.Count == 1)
        {
            return (_x[0], _y[0], 0);
        }

        var d = Math.Clamp(distance, 0, Total);

        var segment = 1;

        while (segment < _cumulative.Length - 1 && _cumulative[segment] < d)
        {
            segment++;
        }

        var from = segment - 1;
        var span = _cumulative[segment] - _cumulative[from];
        var t = span > 0 ? (d - _cumulative[from]) / span : 0;

        var dx = _x[segment] - _x[from];
        var dy = _y[segment] - _y[from];

        return (
            _x[from] + dx * t,
            _y[from] + dy * t,
            Math.Atan2(dy, dx));
    }

    private void Finish()
    {
        _cumulative = new Double[_x.Count];
        var running = 0.0;

        for (var i = 1; i < _x.Count; i++)
        {
            running += Math.Sqrt(Sq(_x[i] - _x[i - 1]) + Sq(_y[i] - _y[i - 1]));
            _cumulative[i] = running;
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

        _x.Add(x);
        _y.Add(y);
    }

    private void Close()
    {
        _closed = true;

        // Return to the start of the whole path for the closing segment's length and direction.
        if (_x.Count > 0)
        {
            _x.Add(_x[0]);
            _y.Add(_y[0]);
        }
    }

    /// <summary>Builds a path from SVG path data (the <c>path('…')</c> argument).</summary>
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

        const int Steps = 20;

        for (var s = 1; s <= Steps; s++)
        {
            var t = phi1 + delta * s / Steps;
            var px = cos * rx * Math.Cos(t) - sin * ry * Math.Sin(t) + cx;
            var py = sin * rx * Math.Cos(t) + cos * ry * Math.Sin(t) + cy;
            path.Line(px, py);
        }
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

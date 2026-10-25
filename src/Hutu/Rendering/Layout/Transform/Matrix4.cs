using System;

namespace Hutu.Rendering.Layout;

/// <summary>
/// A 4×4 transformation matrix, kept in the row-major order a point is read with: a point is a column,
/// and <c>p' = M·p</c>. It carries the third dimension that <see cref="Affine"/> cannot - a rotation
/// about the x or y axis, a translation along z, and the perspective divide - so a flat box can be
/// carried into the plane it is drawn on.
/// </summary>
/// <remarks>
/// CSS writes <c>matrix3d()</c>'s sixteen numbers column by column, and its matrix with the rows and
/// columns swapped in the prose; here the numbers are read out of the argument list by the cell they
/// name, which is why the fourth row's first three cells are the translation.
/// </remarks>
internal readonly struct Matrix4
{
    public Matrix4(
        Double m11, Double m12, Double m13, Double m14,
        Double m21, Double m22, Double m23, Double m24,
        Double m31, Double m32, Double m33, Double m34,
        Double m41, Double m42, Double m43, Double m44)
    {
        M11 = m11;
        M12 = m12;
        M13 = m13;
        M14 = m14;
        M21 = m21;
        M22 = m22;
        M23 = m23;
        M24 = m24;
        M31 = m31;
        M32 = m32;
        M33 = m33;
        M34 = m34;
        M41 = m41;
        M42 = m42;
        M43 = m43;
        M44 = m44;
    }

    public Double M11 { get; }

    public Double M12 { get; }

    public Double M13 { get; }

    public Double M14 { get; }

    public Double M21 { get; }

    public Double M22 { get; }

    public Double M23 { get; }

    public Double M24 { get; }

    public Double M31 { get; }

    public Double M32 { get; }

    public Double M33 { get; }

    public Double M34 { get; }

    public Double M41 { get; }

    public Double M42 { get; }

    public Double M43 { get; }

    public Double M44 { get; }

    public static Matrix4 Identity { get; } = new(
        1, 0, 0, 0,
        0, 1, 0, 0,
        0, 0, 1, 0,
        0, 0, 0, 1);

    /// <summary>Composes two matrices so that a point is carried by <c>other</c> first, then this.</summary>
    public Matrix4 Then(Matrix4 other) => new(
        M11 * other.M11 + M12 * other.M21 + M13 * other.M31 + M14 * other.M41,
        M11 * other.M12 + M12 * other.M22 + M13 * other.M32 + M14 * other.M42,
        M11 * other.M13 + M12 * other.M23 + M13 * other.M33 + M14 * other.M43,
        M11 * other.M14 + M12 * other.M24 + M13 * other.M34 + M14 * other.M44,

        M21 * other.M11 + M22 * other.M21 + M23 * other.M31 + M24 * other.M41,
        M21 * other.M12 + M22 * other.M22 + M23 * other.M32 + M24 * other.M42,
        M21 * other.M13 + M22 * other.M23 + M23 * other.M33 + M24 * other.M43,
        M21 * other.M14 + M22 * other.M24 + M23 * other.M34 + M24 * other.M44,

        M31 * other.M11 + M32 * other.M21 + M33 * other.M31 + M34 * other.M41,
        M31 * other.M12 + M32 * other.M22 + M33 * other.M32 + M34 * other.M42,
        M31 * other.M13 + M32 * other.M23 + M33 * other.M33 + M34 * other.M43,
        M31 * other.M14 + M32 * other.M24 + M33 * other.M34 + M34 * other.M44,

        M41 * other.M11 + M42 * other.M21 + M43 * other.M31 + M44 * other.M41,
        M41 * other.M12 + M42 * other.M22 + M43 * other.M32 + M44 * other.M42,
        M41 * other.M13 + M42 * other.M23 + M43 * other.M33 + M44 * other.M43,
        M41 * other.M14 + M42 * other.M24 + M43 * other.M34 + M44 * other.M44);

    public static Matrix4 Translation(Double x, Double y, Double z) => new(
        1, 0, 0, x,
        0, 1, 0, y,
        0, 0, 1, z,
        0, 0, 0, 1);

    public static Matrix4 Scaling(Double x, Double y, Double z) => new(
        x, 0, 0, 0,
        0, y, 0, 0,
        0, 0, z, 0,
        0, 0, 0, 1);

    /// <summary>A rotation of <paramref name="angle"/> radians about a direction, clockwise for a
    /// positive angle when looking down the axis toward the origin, which is CSS's convention.</summary>
    public static Matrix4 Rotation(Double x, Double y, Double z, Double angle)
    {
        var length = Math.Sqrt(x * x + y * y + z * z);

        if (length == 0)
        {
            return Identity;
        }

        x /= length;
        y /= length;
        z /= length;

        var c = Math.Cos(angle);
        var s = Math.Sin(angle);
        var t = 1 - c;

        return new Matrix4(
            t * x * x + c, t * x * y - s * z, t * x * z + s * y, 0,
            t * x * y + s * z, t * y * y + c, t * y * z - s * x, 0,
            t * x * z - s * y, t * y * z + s * x, t * z * z + c, 0,
            0, 0, 0, 1);
    }

    /// <summary>The perspective of a viewer <paramref name="distance"/> away, with the eye at the
    /// origin looking down the negative z axis: points in front of the plane shrink the further they
    /// stand toward the viewer.</summary>
    public static Matrix4 Perspective(Double distance) =>
        distance > 0
            ? new Matrix4(
                1, 0, 0, 0,
                0, 1, 0, 0,
                0, 0, 1, 0,
                0, 0, -1 / distance, 1)
            : Identity;

    public static Matrix4 SkewX(Double angle) => new(
        1, Math.Tan(angle), 0, 0,
        0, 1, 0, 0,
        0, 0, 1, 0,
        0, 0, 0, 1);

    public static Matrix4 SkewY(Double angle) => new(
        1, 0, 0, 0,
        Math.Tan(angle), 1, 0, 0,
        0, 0, 1, 0,
        0, 0, 0, 1);

    /// <summary>
    /// Reduces the matrix to the plane <c>z = 0</c>: what is left is a homography of the plane, which
    /// is all a flat box needs to be drawn where the third dimension puts it.
    /// </summary>
    public Affine Project() => new(
        M11, M21, M12, M22, M14, M24,
        M41, M42, M44);

    /// <summary>Gets whether the plane's front or back is turned toward the viewer, read from where
    /// the z axis itself lands: a plane whose third axis points away is one seen from behind.</summary>
    public Double Facing => M33;

    /// <summary>
    /// Gets whether the matrix cannot be undone - a scale of zero along any axis, or a plane flattened
    /// onto a line. CSS Transforms 2 §mathematical-description draws nothing through such a matrix,
    /// because there is no point in the page a point of the box came from.
    /// </summary>
    public Boolean IsSingular
    {
        get
        {
            // The determinant by expansion, with the rows scaled first so the answer does not depend on
            // how large the numbers are.
            var values = new Double[16]
            {
                M11, M12, M13, M14,
                M21, M22, M23, M24,
                M31, M32, M33, M34,
                M41, M42, M43, M44,
            };

            var largest = 0.0;

            foreach (var value in values)
            {
                largest = Math.Max(largest, Math.Abs(value));
            }

            if (largest == 0)
            {
                return true;
            }

            for (var row = 0; row < 4; row++)
            {
                for (var column = 0; column < 4; column++)
                {
                    values[row * 4 + column] /= largest;
                }
            }

            return Math.Abs(Determinant(values)) < 1e-12;
        }
    }

    /// <summary>The determinant of a row-major 4×4 matrix, by Gaussian elimination.</summary>
    private static Double Determinant(Double[] matrix)
    {
        var values = (Double[])matrix.Clone();
        var determinant = 1.0;

        for (var column = 0; column < 4; column++)
        {
            var pivot = column;

            for (var row = column + 1; row < 4; row++)
            {
                if (Math.Abs(values[row * 4 + column]) > Math.Abs(values[pivot * 4 + column]))
                {
                    pivot = row;
                }
            }

            if (values[pivot * 4 + column] == 0)
            {
                return 0;
            }

            if (pivot != column)
            {
                for (var k = 0; k < 4; k++)
                {
                    (values[column * 4 + k], values[pivot * 4 + k]) = (values[pivot * 4 + k], values[column * 4 + k]);
                }

                determinant = -determinant;
            }

            var diagonal = values[column * 4 + column];
            determinant *= diagonal;

            for (var row = column + 1; row < 4; row++)
            {
                var factor = values[row * 4 + column] / diagonal;

                for (var k = column; k < 4; k++)
                {
                    values[row * 4 + k] -= factor * values[column * 4 + k];
                }
            }
        }

        return determinant;
    }
}

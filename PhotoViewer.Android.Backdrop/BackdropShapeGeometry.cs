using System;
using Gfx = Android.Graphics;

namespace PhotoViewer.Android.Backdrop;

/// <summary>
/// 形状轮廓与 G2 连续曲率的逐项移植，对应上游 Shapes 1.2.1 的
/// <c>RoundedRectangleOutline.kt</c> 与 <c>ContinuousCurvatureRoundedRectangleCornerBuilder.kt</c>。
/// </summary>
/// <remarks>
/// 分支与上游一致：零半径输出矩形；<see cref="BackdropCornerStyle.Circular"/>、或正方形且四角达到
/// 短边一半时输出普通圆角矩形；其余情况用 G2 连续曲率三次曲线。数值常量与求解公式不改。
/// </remarks>
internal static class BackdropShapeGeometry
{
    /// <summary>普通矩形路径。</summary>
    public static void BuildRectangle(Gfx.Path path, float width, float height)
    {
        path.Rewind();
        path.AddRect(0f, 0f, width, height, Gfx.Path.Direction.Cw!);
    }

    /// <summary>胶囊路径：半径取短边一半，再按圆角矩形规则分支。</summary>
    public static void BuildCapsule(
        Gfx.Path path,
        float width,
        float height,
        BackdropCornerStyle style)
    {
        var radius = MathF.Min(width, height) * 0.5f;
        BuildRoundedRectangle(path, width, height, radius, style);
    }

    /// <summary>四角相同的圆角矩形路径。</summary>
    public static void BuildRoundedRectangle(
        Gfx.Path path,
        float width,
        float height,
        float radius,
        BackdropCornerStyle style)
    {
        path.Rewind();

        if (width <= 0f || height <= 0f)
        {
            return;
        }

        var maxRadius = MathF.Min(width, height) * 0.5f;
        radius = Math.Clamp(radius, 0f, maxRadius);

        if (radius == 0f)
        {
            BuildRectangle(path, width, height);
            return;
        }

        if (style == BackdropCornerStyle.Circular || (width == height && radius >= maxRadius))
        {
            path.AddRoundRect(0f, 0f, width, height, radius, radius, Gfx.Path.Direction.Cw!);
            return;
        }

        BuildContinuousCurvature(path, width, height, radius);
    }

    /// <summary>四角可不同的圆角矩形路径。</summary>
    public static void BuildUnevenRoundedRectangle(
        Gfx.Path path,
        float width,
        float height,
        BackdropCornerRadii corners,
        BackdropCornerStyle style)
    {
        path.Rewind();

        if (width <= 0f || height <= 0f)
        {
            return;
        }

        var maxRadius = MathF.Min(width, height) * 0.5f;
        var topLeft = Math.Clamp(corners.TopLeft, 0f, maxRadius);
        var topRight = Math.Clamp(corners.TopRight, 0f, maxRadius);
        var bottomRight = Math.Clamp(corners.BottomRight, 0f, maxRadius);
        var bottomLeft = Math.Clamp(corners.BottomLeft, 0f, maxRadius);

        if (topLeft == 0f && topRight == 0f && bottomRight == 0f && bottomLeft == 0f)
        {
            BuildRectangle(path, width, height);
            return;
        }

        if (style == BackdropCornerStyle.Circular ||
            (width == height &&
                topLeft >= maxRadius &&
                topRight >= maxRadius &&
                bottomRight >= maxRadius &&
                bottomLeft >= maxRadius))
        {
            path.AddRoundRect(
                0f,
                0f,
                width,
                height,
                [
                    topLeft, topLeft,
                    topRight, topRight,
                    bottomRight, bottomRight,
                    bottomLeft, bottomLeft,
                ],
                Gfx.Path.Direction.Cw!);
            return;
        }

        BuildContinuousCurvature(path, width, height, topLeft, topRight, bottomRight, bottomLeft);
    }

    private static void BuildContinuousCurvature(
        Gfx.Path path,
        float width,
        float height,
        float radius)
    {
        var builder = ContinuousCurvatureCornerBuilder.Default;
        var w = (double)width;
        var h = (double)height;

        var r = (double)radius;
        var tW = Math.Clamp(((width * 0.5) - r) / r, 0d, 1d);
        var tH = Math.Clamp(((height * 0.5) - r) / r, 0d, 1d);
        var p = builder.GetCornerBezierPoints(tW, tH);
        if (p.Length < 20) return;

        var x = w - r;
        var y = 0d;
        path.MoveTo((float)(x + (p[0] * r)), (float)(y + (p[1] * r)));
        path.CubicTo(
            (float)(x + (p[2] * r)), (float)(y + (p[3] * r)),
            (float)(x + (p[4] * r)), (float)(y + (p[5] * r)),
            (float)(x + (p[6] * r)), (float)(y + (p[7] * r)));
        path.CubicTo(
            (float)(x + (p[8] * r)), (float)(y + (p[9] * r)),
            (float)(x + (p[10] * r)), (float)(y + (p[11] * r)),
            (float)(x + (p[12] * r)), (float)(y + (p[13] * r)));
        path.CubicTo(
            (float)(x + (p[14] * r)), (float)(y + (p[15] * r)),
            (float)(x + (p[16] * r)), (float)(y + (p[17] * r)),
            (float)(x + (p[18] * r)), (float)(y + (p[19] * r)));

        x = w - r;
        y = h;
        path.LineTo((float)(x + (p[18] * r)), (float)(y - (p[19] * r)));
        path.CubicTo(
            (float)(x + (p[16] * r)), (float)(y - (p[17] * r)),
            (float)(x + (p[14] * r)), (float)(y - (p[15] * r)),
            (float)(x + (p[12] * r)), (float)(y - (p[13] * r)));
        path.CubicTo(
            (float)(x + (p[10] * r)), (float)(y - (p[11] * r)),
            (float)(x + (p[8] * r)), (float)(y - (p[9] * r)),
            (float)(x + (p[6] * r)), (float)(y - (p[7] * r)));
        path.CubicTo(
            (float)(x + (p[4] * r)), (float)(y - (p[5] * r)),
            (float)(x + (p[2] * r)), (float)(y - (p[3] * r)),
            (float)(x + (p[0] * r)), (float)(y - (p[1] * r)));

        x = r;
        y = h;
        path.LineTo((float)(x - (p[0] * r)), (float)(y - (p[1] * r)));
        path.CubicTo(
            (float)(x - (p[2] * r)), (float)(y - (p[3] * r)),
            (float)(x - (p[4] * r)), (float)(y - (p[5] * r)),
            (float)(x - (p[6] * r)), (float)(y - (p[7] * r)));
        path.CubicTo(
            (float)(x - (p[8] * r)), (float)(y - (p[9] * r)),
            (float)(x - (p[10] * r)), (float)(y - (p[11] * r)),
            (float)(x - (p[12] * r)), (float)(y - (p[13] * r)));
        path.CubicTo(
            (float)(x - (p[14] * r)), (float)(y - (p[15] * r)),
            (float)(x - (p[16] * r)), (float)(y - (p[17] * r)),
            (float)(x - (p[18] * r)), (float)(y - (p[19] * r)));

        x = r;
        y = 0d;
        path.LineTo((float)(x - (p[18] * r)), (float)(y + (p[19] * r)));
        path.CubicTo(
            (float)(x - (p[16] * r)), (float)(y + (p[17] * r)),
            (float)(x - (p[14] * r)), (float)(y + (p[15] * r)),
            (float)(x - (p[12] * r)), (float)(y + (p[13] * r)));
        path.CubicTo(
            (float)(x - (p[10] * r)), (float)(y + (p[11] * r)),
            (float)(x - (p[8] * r)), (float)(y + (p[9] * r)),
            (float)(x - (p[6] * r)), (float)(y + (p[7] * r)));
        path.CubicTo(
            (float)(x - (p[4] * r)), (float)(y + (p[5] * r)),
            (float)(x - (p[2] * r)), (float)(y + (p[3] * r)),
            (float)(x - (p[0] * r)), (float)(y + (p[1] * r)));

        path.Close();
    }

    private static void BuildContinuousCurvature(
        Gfx.Path path,
        float width,
        float height,
        float topLeft,
        float topRight,
        float bottomRight,
        float bottomLeft)
    {
        var builder = ContinuousCurvatureCornerBuilder.Default;
        var w = (double)width;
        var h = (double)height;

        var r = (double)topRight;
        var tW = Math.Clamp(((width * 0.5) - r) / r, 0d, 1d);
        var tH = Math.Clamp(((height * 0.5) - r) / r, 0d, 1d);
        var p = builder.GetCornerBezierPoints(tW, tH);
        if (p.Length < 20) return;

        var x = w - r;
        var y = 0d;
        path.MoveTo((float)(x + (p[0] * r)), (float)(y + (p[1] * r)));
        path.CubicTo(
            (float)(x + (p[2] * r)), (float)(y + (p[3] * r)),
            (float)(x + (p[4] * r)), (float)(y + (p[5] * r)),
            (float)(x + (p[6] * r)), (float)(y + (p[7] * r)));
        path.CubicTo(
            (float)(x + (p[8] * r)), (float)(y + (p[9] * r)),
            (float)(x + (p[10] * r)), (float)(y + (p[11] * r)),
            (float)(x + (p[12] * r)), (float)(y + (p[13] * r)));
        path.CubicTo(
            (float)(x + (p[14] * r)), (float)(y + (p[15] * r)),
            (float)(x + (p[16] * r)), (float)(y + (p[17] * r)),
            (float)(x + (p[18] * r)), (float)(y + (p[19] * r)));

        r = (double)bottomRight;
        tW = Math.Clamp(((width * 0.5) - r) / r, 0d, 1d);
        tH = Math.Clamp(((height * 0.5) - r) / r, 0d, 1d);
        p = builder.GetCornerBezierPoints(tW, tH);
        if (p.Length < 20) return;

        x = w - r;
        y = h;
        path.LineTo((float)(x + (p[18] * r)), (float)(y - (p[19] * r)));
        path.CubicTo(
            (float)(x + (p[16] * r)), (float)(y - (p[17] * r)),
            (float)(x + (p[14] * r)), (float)(y - (p[15] * r)),
            (float)(x + (p[12] * r)), (float)(y - (p[13] * r)));
        path.CubicTo(
            (float)(x + (p[10] * r)), (float)(y - (p[11] * r)),
            (float)(x + (p[8] * r)), (float)(y - (p[9] * r)),
            (float)(x + (p[6] * r)), (float)(y - (p[7] * r)));
        path.CubicTo(
            (float)(x + (p[4] * r)), (float)(y - (p[5] * r)),
            (float)(x + (p[2] * r)), (float)(y - (p[3] * r)),
            (float)(x + (p[0] * r)), (float)(y - (p[1] * r)));

        r = (double)bottomLeft;
        tW = Math.Clamp(((width * 0.5) - r) / r, 0d, 1d);
        tH = Math.Clamp(((height * 0.5) - r) / r, 0d, 1d);
        p = builder.GetCornerBezierPoints(tW, tH);
        if (p.Length < 20) return;

        x = r;
        y = h;
        path.LineTo((float)(x - (p[0] * r)), (float)(y - (p[1] * r)));
        path.CubicTo(
            (float)(x - (p[2] * r)), (float)(y - (p[3] * r)),
            (float)(x - (p[4] * r)), (float)(y - (p[5] * r)),
            (float)(x - (p[6] * r)), (float)(y - (p[7] * r)));
        path.CubicTo(
            (float)(x - (p[8] * r)), (float)(y - (p[9] * r)),
            (float)(x - (p[10] * r)), (float)(y - (p[11] * r)),
            (float)(x - (p[12] * r)), (float)(y - (p[13] * r)));
        path.CubicTo(
            (float)(x - (p[14] * r)), (float)(y - (p[15] * r)),
            (float)(x - (p[16] * r)), (float)(y - (p[17] * r)),
            (float)(x - (p[18] * r)), (float)(y - (p[19] * r)));

        r = (double)topLeft;
        tW = Math.Clamp(((width * 0.5) - r) / r, 0d, 1d);
        tH = Math.Clamp(((height * 0.5) - r) / r, 0d, 1d);
        p = builder.GetCornerBezierPoints(tW, tH);
        if (p.Length < 20) return;

        x = r;
        y = 0d;
        path.LineTo((float)(x - (p[18] * r)), (float)(y + (p[19] * r)));
        path.CubicTo(
            (float)(x - (p[16] * r)), (float)(y + (p[17] * r)),
            (float)(x - (p[14] * r)), (float)(y + (p[15] * r)),
            (float)(x - (p[12] * r)), (float)(y + (p[13] * r)));
        path.CubicTo(
            (float)(x - (p[10] * r)), (float)(y + (p[11] * r)),
            (float)(x - (p[8] * r)), (float)(y + (p[9] * r)),
            (float)(x - (p[6] * r)), (float)(y + (p[7] * r)));
        path.CubicTo(
            (float)(x - (p[4] * r)), (float)(y + (p[5] * r)),
            (float)(x - (p[2] * r)), (float)(y + (p[3] * r)),
            (float)(x - (p[0] * r)), (float)(y + (p[1] * r)));

        path.Close();
    }

    /// <summary>
    /// G2 连续曲率圆角的三次贝塞尔点求解，逐项对应上游
    /// <c>ContinuousCurvatureRoundedRectangleCornerBuilder</c>。
    /// </summary>
    private sealed class ContinuousCurvatureCornerBuilder
    {
        private const double ExtendedFraction = 2d / 3d;
        private const double ArcFraction = 0.5d;
        private const double Sqrt2 = 1.4142135623730951d;
        private const double FracPi4 = 0.7853981633974483d;
        private const double Frac1Sqrt2 = 0.7071067811865476d;

        public static readonly ContinuousCurvatureCornerBuilder Default = new();

        private readonly double _cos;
        private readonly double _sin;
        private readonly double _cot;
        private readonly double _cos2;
        private readonly double _sin2;
        private readonly double _sin3;
        private readonly double _k0;
        private readonly double _k1;
        private readonly double _k2;
        private readonly double _k3;
        private readonly double[][][] _cache;

        private ContinuousCurvatureCornerBuilder()
        {
            var theta = (1d - ArcFraction) * FracPi4;
            _cos = Math.Cos(theta);
            _sin = Math.Sin(theta);
            _cot = 1d / Math.Tan(theta);
            _cos2 = _cos * _cos;
            _sin2 = _sin * _sin;
            var cos3 = _cos2 * _cos;
            _sin3 = _sin2 * _sin;

            _k0 =
                (27d * ((Sqrt2 - (6d * _cos)) + (6d * Sqrt2 * _cos2) - (4d * cos3)) * _cot) +
                (2d * _sin *
                    (-9d +
                        (2d * (Sqrt2 - (2d * _sin)) * _sin3) +
                        (2d * Sqrt2 * _cos * (9d + _sin2)) -
                        (2d * _cos2 * (9d + (2d * _sin2)))));
            _k1 =
                (-81d * (-2d + Sqrt2 + (4d * (-1d + Sqrt2) * _cos) + (2d * (-2d + Sqrt2) * _cos2)) * _cot) -
                (4d * _sin * (-9d + (9d * Sqrt2) + (Sqrt2 * _sin3) + ((-2d + Sqrt2) * _cos * (9d + _sin2))));
            _k2 =
                (9d * ((9d * (-4d + (3d * Sqrt2) + ((-6d + (4d * Sqrt2)) * _cos))) * _cot +
                    ((-6d + (4d * Sqrt2)) * _sin)));
            _k3 = 27d * (10d - (7d * Sqrt2)) * _cot;

            _cache =
            [
                [BuildEvenCornerBezierPoints(0d), BuildUnevenCornerBezierPoints(0d, 1d)],
                [BuildUnevenCornerBezierPoints(1d, 0d), BuildEvenCornerBezierPoints(1d)],
            ];
        }

        public double[] GetCornerBezierPoints(double tH = 1d, double tV = 1d)
        {
            int i;
            if (tH == 0d) i = 0;
            else if (tH == 1d) i = 1;
            else return BuildCornerBezierPoints(tH, tV);

            int j;
            if (tV == 0d) j = 0;
            else if (tV == 1d) j = 1;
            else return BuildCornerBezierPoints(tH, tV);

            return _cache[i][j];
        }

        private double[] BuildCornerBezierPoints(double tH, double tV) =>
            tH == tV ? BuildEvenCornerBezierPoints(tH) : BuildUnevenCornerBezierPoints(tH, tV);

        private double[] BuildEvenCornerBezierPoints(double t)
        {
            var k = ExtendedFraction * t;

            var kappa = SolveCubicSingle(_k3, _k2, _k1 + (8d * -k * _sin3 * _sin), _k0);

            var x3 = Frac1Sqrt2 + ((-Frac1Sqrt2 + _sin) / kappa);
            var y3 = 1d - Frac1Sqrt2 + ((Frac1Sqrt2 - _cos) / kappa);
            var x2 = x3 - (y3 * _cot);
            var x1 = x2 - ((1.5d * kappa * y3 * y3) / _sin3);
            var x0 = -k;

            var x6 = 1d - y3;
            var y6 = 1d - x3;
            var y7 = 1d - x2;
            var y8 = 1d - x1;
            var y9 = 1d - x0;

            var a = 1.5d * kappa;
            var g = _cos2 - _sin2;
            var x36 = x6 - x3;
            var y36 = y6 - y3;
            var c = -((_cos * y36) - (_sin * x36));
            var lambda = (-g + Math.Sqrt((g * g) - (4d * a * c))) / (2d * a);
            var x4 = x3 + (lambda * _cos);
            var y4 = y3 + (lambda * _sin);
            var x5 = x6 - (lambda * _sin);
            var y5 = y6 - (lambda * _cos);

            return [x0, 0d, x1, 0d, x2, 0d, x3, y3, x4, y4, x5, y5, x6, y6, 1d, y7, 1d, y8, 1d, y9];
        }

        private double[] BuildUnevenCornerBezierPoints(double tH, double tV)
        {
            var kH = ExtendedFraction * tH;
            var kV = ExtendedFraction * tV;

            var kappa3 = SolveCubicSingle(_k3, _k2, _k1 + (8d * -kH * _sin3 * _sin), _k0);
            var kappa6 = SolveCubicSingle(_k3, _k2, _k1 + (8d * -kV * _sin3 * _sin), _k0);

            var x3 = Frac1Sqrt2 + ((-Frac1Sqrt2 + _sin) / kappa3);
            var y3 = 1d - Frac1Sqrt2 + ((Frac1Sqrt2 - _cos) / kappa3);
            var x2 = x3 - (y3 * _cot);
            var x1 = x2 - ((1.5d * kappa3 * y3 * y3) / _sin3);
            var x0 = -kH;

            var x3p = Frac1Sqrt2 + ((-Frac1Sqrt2 + _sin) / kappa6);
            var y3p = 1d - Frac1Sqrt2 + ((Frac1Sqrt2 - _cos) / kappa6);
            var x2p = x3p - (y3p * _cot);
            var x1p = x2p - ((1.5d * kappa6 * y3p * y3p) / _sin3);
            var x0p = -kV;
            var x6 = 1d - y3p;
            var y6 = 1d - x3p;
            var y7 = 1d - x2p;
            var y8 = 1d - x1p;
            var y9 = 1d - x0p;

            var a = 1.5d * kappa3;
            var b = 1.5d * kappa6;
            var g = _cos2 - _sin2;
            var x36 = x6 - x3;
            var y36 = y6 - y3;
            var c = -((_cos * y36) - (_sin * x36));
            var d = (_sin * y36) - (_cos * x36);
            var p = 2d * (d / b);
            var q = (g * g * g) / (a * b * b);
            var r = ((a * d * d) + (c * g * g)) / (a * b * b);
            var lambda6 = SolveDepressedQuarticSingle(p, q, r);
            var lambda3 = (-d - (b * lambda6 * lambda6)) / g;
            var x4 = x3 + (lambda3 * _cos);
            var y4 = y3 + (lambda3 * _sin);
            var x5 = x6 - (lambda6 * _sin);
            var y5 = y6 - (lambda6 * _cos);

            return [x0, 0d, x1, 0d, x2, 0d, x3, y3, x4, y4, x5, y5, x6, y6, 1d, y7, 1d, y8, 1d, y9];
        }

        private static double SolveCubicSingle(double a, double b, double c, double d)
        {
            var f = (((3d * c) / a) - ((b * b) / (a * a))) / 3d;
            var g = (((2d * b * b * b) / (a * a * a)) - ((9d * b * c) / (a * a)) + ((27d * d) / a)) / 27d;
            var h = ((g * g) / 4d) + ((f * f * f) / 27d);
            var sqrtH = Math.Sqrt(h);
            return Math.Cbrt((-g / 2d) + sqrtH) + Math.Cbrt((-g / 2d) - sqrtH) - (b / (3d * a));
        }

        private static double SolveDepressedQuarticSingle(double p, double q, double r)
        {
            var b = -p / 2d;
            var c = -r;
            var d = ((r * p) / 2d) - ((q * q) / 8d);
            var f = ((3d * c) - (b * b)) / 3d;
            var g = (((2d * b * b * b) - (9d * b * c) + (27d * d)) / 27d);
            var rRoot = Math.Sqrt((-f * f * f) / 27d);
            var phi = Math.Acos(-g / (2d * rRoot));
            var y = 2d * Math.Sqrt(-f / 3d) * Math.Cos(phi / 3d);
            var z = y - (b / 3d);
            var u = Math.Sqrt((2d * z) - p);
            return (u - Math.Sqrt((u * u) - (4d * (z + (q / (2d * u)))))) / 2d;
        }
    }
}

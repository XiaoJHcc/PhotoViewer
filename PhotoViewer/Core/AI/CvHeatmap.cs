using System;
using System.Collections.Generic;

namespace PhotoViewer.Core.AI;

/// <summary>
/// 诊断图类型。对应工具页的两张可视化 + 一个文本行。
/// </summary>
public enum CvDiagnostic
{
    /// <summary>锐度：edge_width_p20 线性映射到 [0,1]（NaN 保留为 NaN，由渲染端画灰）。</summary>
    Sharpness = 0,

    /// <summary>抖动矢量场：per-cell (direction, spread, mask)。</summary>
    Shake = 1,

    /// <summary>全图刚体拟合：|T| / |ω| / residual_rms。</summary>
    RigidMotion = 2,
}

/// <summary>
/// v2 抖动矢量场：per-cell 方向 / 位移 / 有效掩膜，与 Avalonia Canvas 线段层直接对接。
/// </summary>
public sealed class ShakeField
{
    /// <summary>拖影方向（rad，[0,π)）；无效格填 NaN。</summary>
    public float[] Direction { get; init; } = Array.Empty<float>();

    /// <summary>拖影位移（px），即方向 8-bin 边宽谱的 max−min；无效格填 NaN。</summary>
    public float[] Spread { get; init; } = Array.Empty<float>();

    /// <summary>有效掩膜：true = 该格参与矢量场绘制。</summary>
    public bool[] Mask { get; init; } = Array.Empty<bool>();
}

/// <summary>
/// 全图刚体拟合结果（像素 / 弧度单位）。
/// </summary>
public readonly struct RigidMotionResult
{
    /// <summary>参与拟合的格数。</summary>
    public int SampleCount { get; init; }

    /// <summary>平移分量的幅值（px）。</summary>
    public float TranslationMagnitude { get; init; }

    /// <summary>旋转分量幅值（rad）。</summary>
    public float RotationMagnitude { get; init; }

    /// <summary>拟合残差 RMS（px）。</summary>
    public float ResidualRms { get; init; }
}

/// <summary>
/// CV 网格 v2 热力图的纯函数提供者：锐度图 / 抖动矢量场 / 刚体拟合。
/// 所有方法无状态、线程安全；调用方自行决定何时重算。
/// </summary>
public static class CvHeatmap
{
    private const int Grid = CvGridResult.GridSize;

    /// <summary>锐度全亮阈值：edge_width_p20 ≤ 此值视为完全锐。</summary>
    public const float WidthSharpPx = 1.5f;

    /// <summary>锐度全暗阈值：edge_width_p20 ≥ 此值视为严重虚。</summary>
    public const float WidthVisPx = 10f;

    /// <summary>刚体拟合最低 spread 阈值（px），低于此的格视为静止纹理不参与。</summary>
    public const float RigidMotionSpreadThresholdPx = 2f;

    /// <summary>
    /// 读取单张原始标量平面（16×16），用于调试快照。NaN 原样返回。
    /// </summary>
    public static float[] GetScalarPlane(CvGridResult result, int scalar)
    {
        ArgumentNullException.ThrowIfNull(result);
        var plane = new float[CvGridResult.PlaneLength];
        for (int y = 0; y < Grid; y++)
        {
            for (int x = 0; x < Grid; x++)
            {
                plane[y * Grid + x] = result.GetCell(scalar, y, x);
            }
        }
        return plane;
    }

    /// <summary>
    /// 构造锐度图：edge_width_p20 → 对数量纲映射。
    /// 亮 = 锐、暗 = 虚；NaN（采样块无有效边）映射为 0（与"严重虚"同深色），语义即"无锐内容"。
    /// 对数映射强调 1 px 数量级差异：1.5/2/3/5/10 px 的 t 约 1.00/0.74/0.49/0.27/0.00。
    /// </summary>
    public static float[] BuildSharpness(CvGridResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        const int scalar = 1; // edge_width_p20
        var plane = new float[CvGridResult.PlaneLength];
        float logSharp = MathF.Log(WidthSharpPx);
        float logVis = MathF.Log(WidthVisPx);
        float range = logVis - logSharp;
        for (int i = 0; i < plane.Length; i++)
        {
            float w = result.Data[scalar * CvGridResult.PlaneLength + i];
            if (float.IsNaN(w) || w <= 0f)
            {
                plane[i] = 0f;
                continue;
            }
            float t = (logVis - MathF.Log(w)) / range;
            if (t < 0f) t = 0f;
            if (t > 1f) t = 1f;
            plane[i] = t;
        }
        return plane;
    }

    /// <summary>
    /// 构造抖动矢量场：取 shake_direction / shake_spread，加上有效性掩膜。
    /// 无效格：edge_count 不足、或 spread &lt; RigidMotionSpreadThresholdPx / 2（排除纯离焦）。
    /// </summary>
    public static ShakeField BuildShakeField(CvGridResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        int n = CvGridResult.PlaneLength;
        var direction = new float[n];
        var spread = new float[n];
        var mask = new bool[n];
        const float minSpread = 0.5f;
        for (int i = 0; i < n; i++)
        {
            float s = result.Data[3 * n + i];
            float d = result.Data[4 * n + i];
            direction[i] = d;
            spread[i] = s;
            mask[i] = !float.IsNaN(s) && !float.IsNaN(d) && s >= minSpread;
        }
        return new ShakeField { Direction = direction, Spread = spread, Mask = mask };
    }

    /// <summary>
    /// 在矢量场上做全图刚体拟合：v_i = T + ω × (p_i − c)，c = 图像中心。
    /// 只纳入 spread ≥ RigidMotionSpreadThresholdPx 的格。
    /// </summary>
    public static RigidMotionResult FitRigidMotion(ShakeField field)
    {
        ArgumentNullException.ThrowIfNull(field);
        int n = CvGridResult.PlaneLength;
        var samples = new List<(float px, float py, float vx, float vy)>(128);
        float cx = (Grid - 1) / 2f;
        float cy = (Grid - 1) / 2f;

        for (int y = 0; y < Grid; y++)
        {
            for (int x = 0; x < Grid; x++)
            {
                int i = y * Grid + x;
                if (!field.Mask[i]) continue;
                float s = field.Spread[i];
                if (s < RigidMotionSpreadThresholdPx) continue;
                float dir = field.Direction[i];
                if (float.IsNaN(dir)) continue;
                float vx = s * MathF.Cos(dir);
                float vy = s * MathF.Sin(dir);
                samples.Add((x - cx, y - cy, vx, vy));
            }
        }

        if (samples.Count < 6)
        {
            return new RigidMotionResult
            {
                SampleCount = samples.Count,
                TranslationMagnitude = 0f,
                RotationMagnitude = 0f,
                ResidualRms = 0f,
            };
        }

        // 待拟合 3 参数：tx, ty, ω。方程：
        //   vx = tx − ω · py    (∂/∂ω 上 x 方程贡献 = −py)
        //   vy = ty + ω · px    (∂/∂ω 上 y 方程贡献 = +px)
        // 封闭解：
        //   tx = mean(vx) + ω · mean(py)
        //   ty = mean(vy) − ω · mean(px)
        //   ω  = Σ(px·vy − py·vx − px·<vy> + py·<vx>) / Σ(px² + py² − <px>² − <py>²)
        double mx = 0, my = 0, mvx = 0, mvy = 0;
        foreach (var s in samples)
        {
            mx += s.px; my += s.py; mvx += s.vx; mvy += s.vy;
        }
        int k = samples.Count;
        mx /= k; my /= k; mvx /= k; mvy /= k;

        double num = 0, den = 0;
        foreach (var s in samples)
        {
            double dx = s.px - mx;
            double dy = s.py - my;
            double ux = s.vx - mvx;
            double uy = s.vy - mvy;
            num += dx * uy - dy * ux;
            den += dx * dx + dy * dy;
        }
        double omega = den > 1e-6 ? num / den : 0;
        double tx = mvx + omega * my;
        double ty = mvy - omega * mx;

        double ssq = 0;
        foreach (var s in samples)
        {
            double rx = s.vx - (tx - omega * s.py);
            double ry = s.vy - (ty + omega * s.px);
            ssq += rx * rx + ry * ry;
        }
        double rms = Math.Sqrt(ssq / (2.0 * k));
        double tm = Math.Sqrt(tx * tx + ty * ty);

        return new RigidMotionResult
        {
            SampleCount = k,
            TranslationMagnitude = (float)tm,
            RotationMagnitude = (float)Math.Abs(omega),
            ResidualRms = (float)rms,
        };
    }
}

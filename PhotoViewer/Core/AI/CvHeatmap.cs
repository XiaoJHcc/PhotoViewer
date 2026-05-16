using System;
using System.Collections.Generic;

namespace PhotoViewer.Core.AI;

/// <summary>
/// 诊断图类型。对应工具页的两张可视化 + 一个文本行。
/// </summary>
public enum CvDiagnostic
{
    /// <summary>锐度：edge_width_p20 对数映射到 [0,1]（NaN → 0，与"严重虚"同深色）。</summary>
    Sharpness = 0,

    /// <summary>抖动矢量场：per-cell (drag_direction, drag_width, mask) + 图像对角线 D。</summary>
    Shake = 1,

    /// <summary>全图刚体拟合：|T| / |ω| / residual_rms / Σw。</summary>
    RigidMotion = 2,
}

/// <summary>
/// v4 抖动矢量场：per-cell 拖影线方向 / 绝对边宽 / 有效掩膜 + 图像对角线 D。
/// 颜色编码与刚体拟合权重都按 drag_width/D 相对值取段，与图像分辨率解耦。
/// </summary>
public sealed class ShakeField
{
    /// <summary>拖影线方向（rad，[0,π)，无极性）；无效格填 NaN。</summary>
    public float[] Direction { get; init; } = Array.Empty<float>();

    /// <summary>拖影线绝对边宽 drag_width（px）= max_bucket 中位边宽；无效格填 NaN。</summary>
    public float[] Width { get; init; } = Array.Empty<float>();

    /// <summary>有效掩膜：true = 该格参与矢量场绘制 / 刚体拟合（Width &amp; Direction 都不为 NaN，且 drag_r ≥ 最低显示阈值）。</summary>
    public bool[] Mask { get; init; } = Array.Empty<bool>();

    /// <summary>图像对角线像素长度 D = √(W²+H²)，用于把 Width 归一化为相对值。</summary>
    public float Diagonal { get; init; }
}

/// <summary>
/// 全图加权刚体拟合结果（像素 / 弧度单位）。
/// </summary>
public readonly struct RigidMotionResult
{
    /// <summary>参与拟合的格数（原始未加权计数）。</summary>
    public int SampleCount { get; init; }

    /// <summary>有效权重和 Σw（梯形过渡后），低于 20 视为"信息不足"。</summary>
    public float WeightSum { get; init; }

    /// <summary>平移分量的幅值（px）。0/180 不可分使 T 方向最多有 180° 翻转，幅值可靠。</summary>
    public float TranslationMagnitude { get; init; }

    /// <summary>旋转分量幅值（rad）。</summary>
    public float RotationMagnitude { get; init; }

    /// <summary>拟合残差 RMS（px，仅在 Σw &gt; 0 时有意义）。</summary>
    public float ResidualRms { get; init; }
}

/// <summary>
/// CV 网格 v4 热力图的纯函数提供者：锐度图（v3 沿用）/ 拖影矢量场 / 加权刚体拟合。
/// 所有方法无状态、线程安全；调用方自行决定何时重算。
/// </summary>
public static class CvHeatmap
{
    private const int Grid = CvGridResult.GridSize;

    /// <summary>锐度全亮阈值：edge_width_p20 ≤ 此值视为完全锐。</summary>
    public const float WidthSharpPx = 1.5f;

    /// <summary>锐度全暗阈值：edge_width_p20 ≥ 此值视为严重虚。</summary>
    public const float WidthVisPx = 10f;

    // ── v4 抖动归一化阈值（drag_r = drag_width / D） ────────────────────────────
    // r1 校准（2026-05-16）：sweet spot 重新中心化到 ~15 px @ 6000×4000（drag_r ≈ 0.18% D）。
    // 之前的 0.5%~1.25% 区间是按"max-bucket 绝对宽 + 误选 θ_st 同向 bucket"的高估值定的；
    // 修正为"θ_st+π/2 同向 bucket（拖影方向边的横向 ramp 宽度）"后，整体落点下移一个数量级；
    // 并且夜景里"看似锐的灯"本身就 5-8 px 边宽，本组阈值据 1181/1197 对照样本实测校准。
    /// <summary>drag_r 最低显示阈值：低于此判"无信号"，不画也不参与刚体拟合。≈3 px @ 6000×4000。</summary>
    public const float DragRMinDisplay = 0.00033f;
    /// <summary>drag_r 权重线性 0→1 的右端点：进入核心置信区（≈4 px @ 6000×4000）。</summary>
    public const float DragRWeightRampEnd = 0.0005f;
    /// <summary>drag_r 权重线性 1→0 的左端点：脱离核心置信区（鲜橙峰，≈17 px @ 6000×4000）。</summary>
    public const float DragRWeightFalloffStart = 0.0020f;
    /// <summary>drag_r 权重归零右端点：进入"肌理 / 长结构"段（≈34 px @ 6000×4000）。</summary>
    public const float DragRMaxValid = 0.0040f;

    /// <summary>
    /// 读取单张原始标量平面（32×32），用于调试快照。NaN 原样返回。
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
    /// 构造锐度图：edge_width_p20 → 对数量纲映射（v3 沿用，不变）。
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

    /// <summary>各向异性下限：A &lt; 此值视为"无主方向"，不画也不参与刚体拟合。0.2 是经验值，远低于
    /// 典型边纹（A &gt; 0.5）也低于车流/反光的各向异性（A 通常 0.3-0.5）。</summary>
    public const float AnisotropyMin = 0.20f;

    /// <summary>
    /// 构造抖动矢量场：取 drag_direction / drag_width，掩膜按 drag_r ≥ DragRMinDisplay 且 anisotropy ≥ AnisotropyMin 判定。
    /// Diagonal 写入返回对象，下游可视化层与刚体拟合都按它做归一化。
    /// </summary>
    public static ShakeField BuildShakeField(CvGridResult result, float diagonal)
    {
        ArgumentNullException.ThrowIfNull(result);
        int n = CvGridResult.PlaneLength;
        var direction = new float[n];
        var width = new float[n];
        var mask = new bool[n];
        float minWidthPx = diagonal > 0 ? diagonal * DragRMinDisplay : 0f;
        for (int i = 0; i < n; i++)
        {
            float wpx = result.Data[3 * n + i]; // drag_width
            float d = result.Data[4 * n + i];   // drag_direction
            float a = result.Data[5 * n + i];   // anisotropy
            direction[i] = d;
            width[i] = wpx;
            mask[i] = !float.IsNaN(wpx) && !float.IsNaN(d) && wpx >= minWidthPx
                      && !float.IsNaN(a) && a >= AnisotropyMin;
        }
        return new ShakeField { Direction = direction, Width = width, Mask = mask, Diagonal = diagonal };
    }

    /// <summary>
    /// 加权刚体拟合：v_i = T + ω × (p_i − c)，c = 图像中心（格坐标）。
    /// 1) 权重按 drag_r 取梯形（DragRWeightRampEnd / DragRWeightFalloffStart / DragRMaxValid）。
    /// 2) 拖影线无极性，每格 v_i 初始取正方向；用预测向量与测量向量的点积符号迭代翻转，最多 3 轮。
    /// 3) 输出 |T| / |ω| / residual_rms / Σw；Σw &lt; 20 由 VM 端解读为"信息不足"。
    /// </summary>
    public static RigidMotionResult FitRigidMotion(ShakeField field)
    {
        ArgumentNullException.ThrowIfNull(field);
        if (field.Diagonal <= 0f)
        {
            return default;
        }

        // 把每格的测量转成 (位置 px、向量 vx/vy、权重 w)。位置用图像中心化的格坐标，单位 = 格。
        var samples = new List<(float px, float py, float vx, float vy, float w)>(256);
        float cx = (Grid - 1) / 2f;
        float cy = (Grid - 1) / 2f;
        float diag = field.Diagonal;

        for (int y = 0; y < Grid; y++)
        {
            for (int x = 0; x < Grid; x++)
            {
                int i = y * Grid + x;
                if (!field.Mask[i]) continue;
                float wpx = field.Width[i];
                float dir = field.Direction[i];
                if (float.IsNaN(wpx) || float.IsNaN(dir)) continue;
                float wgt = TrapezoidWeight(wpx / diag);
                if (wgt <= 0f) continue;
                float vx = wpx * MathF.Cos(dir);
                float vy = wpx * MathF.Sin(dir);
                samples.Add((x - cx, y - cy, vx, vy, wgt));
            }
        }

        if (samples.Count < 6)
        {
            return new RigidMotionResult { SampleCount = samples.Count };
        }

        // 迭代符号对齐：先用统一正向估一次 (T, ω)，再按预测方向翻转 v_i，重拟合。
        var arr = samples.ToArray();
        int iterations = 3;
        double tx = 0, ty = 0, omega = 0;
        for (int iter = 0; iter < iterations; iter++)
        {
            (tx, ty, omega) = SolveWeightedLeastSquares(arr);
            bool anyFlip = false;
            for (int i = 0; i < arr.Length; i++)
            {
                var s = arr[i];
                double predX = tx - omega * s.py;
                double predY = ty + omega * s.px;
                double dot = predX * s.vx + predY * s.vy;
                if (dot < 0)
                {
                    arr[i] = (s.px, s.py, -s.vx, -s.vy, s.w);
                    anyFlip = true;
                }
            }
            if (!anyFlip) break;
        }

        // 收敛后再做一次解，确保符号与解一致。
        (tx, ty, omega) = SolveWeightedLeastSquares(arr);

        double ssq = 0;
        double wsum = 0;
        foreach (var s in arr)
        {
            double rx = s.vx - (tx - omega * s.py);
            double ry = s.vy - (ty + omega * s.px);
            ssq += s.w * (rx * rx + ry * ry);
            wsum += s.w;
        }
        double rms = wsum > 0 ? Math.Sqrt(ssq / (2.0 * wsum)) : 0;
        double tm = Math.Sqrt(tx * tx + ty * ty);

        return new RigidMotionResult
        {
            SampleCount = samples.Count,
            WeightSum = (float)wsum,
            TranslationMagnitude = (float)tm,
            RotationMagnitude = (float)Math.Abs(omega),
            ResidualRms = (float)rms,
        };
    }

    /// <summary>
    /// 计算单个格的刚体拟合权重（梯形过渡）：drag_r 落入核心置信区 [0.083%, 0.50%] 时权重 = 1，
    /// 两端线性下降；区间外 = 0。颜色编码 sweet spot 与此区间对齐。
    /// </summary>
    public static float TrapezoidWeight(float dragR)
    {
        if (float.IsNaN(dragR) || dragR <= DragRMinDisplay) return 0f;
        if (dragR < DragRWeightRampEnd)
        {
            return (dragR - DragRMinDisplay) / (DragRWeightRampEnd - DragRMinDisplay);
        }
        if (dragR <= DragRWeightFalloffStart) return 1f;
        if (dragR < DragRMaxValid)
        {
            return (DragRMaxValid - dragR) / (DragRMaxValid - DragRWeightFalloffStart);
        }
        return 0f;
    }

    /// <summary>
    /// 加权最小二乘求 (tx, ty, ω)，模型：vx = tx − ω·py，vy = ty + ω·px。
    /// 闭式解：先求加权均值消去 tx/ty，再单参数解 ω。
    /// </summary>
    private static (double tx, double ty, double omega) SolveWeightedLeastSquares(
        (float px, float py, float vx, float vy, float w)[] samples)
    {
        double wsum = 0;
        double mx = 0, my = 0, mvx = 0, mvy = 0;
        foreach (var s in samples)
        {
            wsum += s.w;
            mx += s.w * s.px;
            my += s.w * s.py;
            mvx += s.w * s.vx;
            mvy += s.w * s.vy;
        }
        if (wsum <= 1e-9) return (0, 0, 0);
        mx /= wsum; my /= wsum; mvx /= wsum; mvy /= wsum;

        double num = 0, den = 0;
        foreach (var s in samples)
        {
            double dx = s.px - mx;
            double dy = s.py - my;
            double ux = s.vx - mvx;
            double uy = s.vy - mvy;
            num += s.w * (dx * uy - dy * ux);
            den += s.w * (dx * dx + dy * dy);
        }
        double omega = den > 1e-6 ? num / den : 0;
        double tx = mvx + omega * my;
        double ty = mvy - omega * mx;
        return (tx, ty, omega);
    }
}

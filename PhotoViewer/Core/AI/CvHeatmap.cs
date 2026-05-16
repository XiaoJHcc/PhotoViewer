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
/// v5 抖动矢量场：per-cell 拖影线方向 / 绝对边宽 / 有效掩膜 + 局部方向一致性 R_local + 图像对角线 D。
/// 颜色编码改为二维：R_local（方向一致性，主导色相）× drag_r（边宽量级，亮度调整）；
/// 刚体拟合权重仍按 drag_r 相对值取段。
/// </summary>
public sealed class ShakeField
{
    /// <summary>拖影线方向（rad，[0,π)，无极性）；无效格填 NaN。</summary>
    public float[] Direction { get; init; } = Array.Empty<float>();

    /// <summary>拖影线绝对边宽 drag_width（px）= drag_bucket 中位边宽；无效格填 NaN。</summary>
    public float[] Width { get; init; } = Array.Empty<float>();

    /// <summary>有效掩膜：true = 该格参与矢量场绘制 / 刚体拟合（Width &amp; Direction 都不为 NaN，且 drag_r ≥ 最低显示阈值，且 anisotropy ≥ AnisotropyMin）。</summary>
    public bool[] Mask { get; init; } = Array.Empty<bool>();

    /// <summary>局部方向一致性 R_local ∈ [0,1]：5×5 邻域内 Mask=true 格的 2θ 圆形均值长度；邻域有效格 &lt; 5 时为 NaN。</summary>
    public float[] LocalConsistency { get; init; } = Array.Empty<float>();

    /// <summary>r3 新增：每格 block_contrast = luma p98-p2（0-255 标度）。下游通过 <see cref="CvHeatmap.ContrastFactor"/> 转软过渡因子。</summary>
    public float[] Contrast { get; init; } = Array.Empty<float>();

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

    /// <summary>v5 新增：全图方向一致性 R_global ∈ [0,1]，按 weight × exp(i·2θ) 归一。</summary>
    public float DirectionalConsistency { get; init; }

    /// <summary>v5 新增：旋转能量占比 omegaPx / (|T| + omegaPx + ε)，ε=1e-3。omegaPx = |ω| · 半对角线。</summary>
    public float OmegaPxRatio { get; init; }

    /// <summary>v5 新增：有效格占比 = 参与拟合的格数 / 1024。低于 0.05 视为"信息不足"。</summary>
    public float MaskRatio { get; init; }

    /// <summary>v5 r2 新增：参与拟合的格的 R_local 第 10 百分位；旋转抖动判据用，区分"切向场全图相关"与"弱信号假旋转"。</summary>
    public float RLocalP10 { get; init; }
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
    /// 构造锐度图：edge_width_p20 → 对数量纲映射 × 对比度软因子（r3）。
    /// 亮 = 锐、暗 = 虚；NaN（采样块无有效边）映射为 0（与"严重虚"同深色），语义即"无锐内容"。
    /// 对数映射强调 1 px 数量级差异：1.5/2/3/5/10 px 的 t 约 1.00/0.74/0.49/0.27/0.00。
    /// r3：低对比度块（夜景天空 ISO 400 噪点）即使测出"锐边"也被 c_factor 压暗。
    /// contrast 为 null 时退化到 r2 行为（不乘 c_factor）。
    /// </summary>
    public static float[] BuildSharpness(CvGridResult result, float[]? contrast = null)
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
            if (contrast != null)
            {
                t *= ContrastFactor(contrast[i]);
            }
            plane[i] = t;
        }
        return plane;
    }

    /// <summary>各向异性下限：A &lt; 此值视为"无主方向"，不画也不参与刚体拟合。0.2 是经验值，远低于
    /// 典型边纹（A &gt; 0.5）也低于车流/反光的各向异性（A 通常 0.3-0.5）。</summary>
    public const float AnisotropyMin = 0.20f;

    // ── v5 方向一致性 R_local 参数 ────────────────────────────────────────
    /// <summary>R_local 邻域半径：5×5 窗（半径 2）。窗口太小受 NaN 与单格噪声影响，太大会平滑掉旋转抖切向场的方向梯度。</summary>
    public const int LocalRadius = 2;
    /// <summary>R_local 邻域内最少有效格数（不足则该格 R_local = NaN）。</summary>
    public const int LocalMinSamples = 5;

    // ── v5 颜色编码与判定阈值（实测 14 张样本校准 r2，见 plan-2-2-shake-v5 §1.3/§1.5） ──
    /// <summary>R_local 阈值：方向"开始集中"的下断点；之下视为建筑/混合纹理（暗灰色）。</summary>
    public const float RLocalLow = 0.30f;
    /// <summary>R_local 阈值：方向"高度集中"的上断点；之上视为真抖动信号（鲜红色）。</summary>
    public const float RLocalHigh = 0.55f;
    /// <summary>R_global 判"静止纹理"的下断点：低于此 → 不论 |T|/|ω| 多大都判建筑/纹理。
    /// r1 校准（实测 8 张样本）：1181=0.14 / 1211=0.22 必须被挡住；从 0.30 提到 0.45。</summary>
    public const float RGlobalQuietBelow = 0.45f;
    /// <summary>R_global 判"平移/旋转抖动"的下断点：满足此 + 量级阈值 → 真抖动。</summary>
    public const float RGlobalMotionAbove = 0.50f;
    /// <summary>R_global 判"强旋转抖动"的下断点（r2 新增）：低于此 → 不论 |ω| 多大都视为弱信号假旋转。
    /// r2 校准：1467 真强旋 R_global=0.60；1179/1183/1396 假阳性 R_global=0.09-0.54；阈值 0.30 切开。
    /// 注意 1396 R_global=0.536 偏高（小光斑加单一方向纹理混合），但 R_local p10=0.28 旋转判据会兜住。</summary>
    public const float RGlobalStrongRotAbove = 0.30f;
    /// <summary>R_local p10 判"旋转抖动"下断点（r2 新增）：旋转抖切向场全图相关，最差 10% 格也 ≥ 此值。
    /// r2 校准：1465 真旋抖 p10=0.56 / 1467 强旋 p10=0.82；1266/1479 假阳性 p10=0.46/0.48；阈值 0.55 切开。</summary>
    public const float RLocalP10RotMin = 0.55f;
    /// <summary>"强旋转抖动"的 |ω| 下断点（rad）。1467 实测 0.84 远超此阈值。</summary>
    public const float OmegaStrongRot = 0.30f;
    /// <summary>"旋转抖动"的 |ω| 下断点（rad）。r1 校准：1465 实测 0.095（旋转抖）/ 8943 实测 0.083（白天）；
    /// 阈值 0.090 留 0.005 余量切开两者。</summary>
    public const float OmegaRot = 0.090f;
    /// <summary>"平移抖动"的 |T| 下断点（按对角线 D 归一化的相对值）。
    /// r1 校准：实测 1197/1259（抖）|T|/D ≈ 0.150-0.151%；1301/8943（白天）|T|/D ≈ 0.090-0.091%；
    /// 阈值 0.13% 把它们切开（≈9.4 px @ 7211D / 11 px @ 8423D）。</summary>
    public const float TranslateMinDragR = 0.0013f;
    /// <summary>"信息不足"的 Σw 下限。</summary>
    public const float WeightSumMin = 20f;
    /// <summary>"信息不足"的 MaskRatio 下限（5%）。</summary>
    public const float MaskRatioMin = 0.05f;
    /// <summary>残差/动量比阈值，超出 → 混乱场景。</summary>
    public const float ResidualMotionRatio = 0.6f;

    // ── v5 r3 对比度软过渡阈值（实测 14 张样本校准） ──────────────────────────────
    /// <summary>对比度软门控下端：block_contrast (luma p98-p2) &lt; LowEnd → c_factor=0，几乎完全排除。
    /// r3 实测：1396 沥青 p50=88，HighEnd 必须高于此让沥青大半被压低。</summary>
    public const float ContrastLowEnd = 60f;
    /// <summary>对比度软门控上端：≥ HighEnd → c_factor=1，全权重。
    /// r3 实测：典型夜景灯光局部 contrast > 200，建筑墙面 p50=120+；阈值 160 让 1396 沥青 (p50=88) 与 1479 玻璃 (p50=52) c_factor 大幅降低，
    /// 而 1197/1259/1465/1467 真信号靠 p90 > 150 仍能贡献。</summary>
    public const float ContrastHighEnd = 160f;

    /// <summary>r3 软过渡因子：把 block_contrast 映射到 [0,1] 权重。低对比度块的所有读数都按此系数衰减。</summary>
    public static float ContrastFactor(float contrast)
    {
        if (float.IsNaN(contrast) || contrast <= ContrastLowEnd) return 0f;
        if (contrast >= ContrastHighEnd) return 1f;
        return (contrast - ContrastLowEnd) / (ContrastHighEnd - ContrastLowEnd);
    }

    /// <summary>
    /// 构造抖动矢量场：取 drag_direction / drag_width，掩膜按 drag_r ≥ DragRMinDisplay
    /// 且 anisotropy ≥ AnisotropyMin 判定；r3 增加对比度软门控（c_factor &gt; 0 才进 mask）。
    /// 然后在 5×5 邻域内算 R_local（2θ 圆形均值长度）填到 LocalConsistency。
    /// Diagonal 写入返回对象，下游可视化层与刚体拟合都按它做归一化。
    /// contrast 为 null 时退化到 r2 行为（不做对比度门控）。
    /// </summary>
    public static ShakeField BuildShakeField(CvGridResult result, float diagonal, float[]? contrast = null)
    {
        ArgumentNullException.ThrowIfNull(result);
        int n = CvGridResult.PlaneLength;
        var direction = new float[n];
        var width = new float[n];
        var mask = new bool[n];
        var local = new float[n];
        var contrastOut = new float[n];
        float minWidthPx = diagonal > 0 ? diagonal * DragRMinDisplay : 0f;
        for (int i = 0; i < n; i++)
        {
            float wpx = result.Data[3 * n + i]; // drag_width
            float d = result.Data[4 * n + i];   // drag_direction
            float a = result.Data[5 * n + i];   // anisotropy
            direction[i] = d;
            width[i] = wpx;
            float cContrast = contrast != null ? contrast[i] : float.PositiveInfinity;
            contrastOut[i] = contrast != null ? cContrast : float.NaN;
            // 对比度门控：r3 新增 —— 低对比度块（夜景天空噪点 / 沥青颗粒 / 玻璃折射）直接不进 mask。
            // contrast==null 时 cContrast=+∞ 等价于无门控，退化到 r2 行为。
            bool contrastOk = float.IsNaN(cContrast) ? false : ContrastFactor(cContrast) > 0f;
            mask[i] = !float.IsNaN(wpx) && !float.IsNaN(d) && wpx >= minWidthPx
                      && !float.IsNaN(a) && a >= AnisotropyMin
                      && contrastOk;
        }

        // R_local：5×5 邻域 2θ 圆形均值长度；邻域有效格 < LocalMinSamples 的格 → NaN。
        for (int gy = 0; gy < Grid; gy++)
        {
            int y0 = Math.Max(0, gy - LocalRadius);
            int y1 = Math.Min(Grid - 1, gy + LocalRadius);
            for (int gx = 0; gx < Grid; gx++)
            {
                int x0 = Math.Max(0, gx - LocalRadius);
                int x1 = Math.Min(Grid - 1, gx + LocalRadius);
                double sumC = 0, sumS = 0;
                int count = 0;
                for (int ny = y0; ny <= y1; ny++)
                {
                    for (int nx = x0; nx <= x1; nx++)
                    {
                        int ni = ny * Grid + nx;
                        if (!mask[ni]) continue;
                        float th = direction[ni];
                        if (float.IsNaN(th)) continue;
                        // 2θ：让 0 与 π 在单位圆同点（无极性方向的正确圆形统计）。
                        double two = 2.0 * th;
                        sumC += Math.Cos(two);
                        sumS += Math.Sin(two);
                        count++;
                    }
                }
                int idx = gy * Grid + gx;
                if (count < LocalMinSamples)
                {
                    local[idx] = float.NaN;
                }
                else
                {
                    double r = Math.Sqrt(sumC * sumC + sumS * sumS) / count;
                    if (r > 1.0) r = 1.0;
                    local[idx] = (float)r;
                }
            }
        }

        return new ShakeField
        {
            Direction = direction,
            Width = width,
            Mask = mask,
            LocalConsistency = local,
            Contrast = contrastOut,
            Diagonal = diagonal,
        };
    }

    /// <summary>
    /// v5 配色（唯一权威实现）：颜色由 R_local（方向一致性，主导色相）× drag_r（边宽量级，亮度调整）共同决定。
    /// r3：再乘 contrastFactor —— 低对比度块即使方向集中也呈暗色，让人眼一看就知道这格信号弱。
    /// View 与 CvDebugTool 都来这里取色，避免两端 inline 不同步。
    ///   R_local NaN 或 &lt; RLocalLow      → 暗灰段（建筑/混合纹理，与"鲜红抖动峰"形成强对比）
    ///   R_local ∈ [RLocalLow, RLocalHigh) → 黄绿段（规则纹理：百叶/栏杆/有方向但不是抖动）
    ///   R_local ≥ RLocalHigh              → 鲜红段（真抖动信号；亮度峰值落在 sweet spot）
    /// drag_r 的角色：sweet spot（0.05%~0.30%）内全亮，两端低亮，让"长建筑边"与"过短噪声"都被压暗。
    /// contrastFactor 缺省 1（r2 行为）；&lt; 1 时整体亮度按比例衰减。
    /// </summary>
    public static (byte R, byte G, byte B) ColorForShake(float dragR, float rLocal, float contrastFactor = 1f)
    {
        // 亮度因子：sweet spot 内 = 1，外侧线性下降到 0.45。低于 DragRMinDisplay 调用方应短路不画。
        float lum;
        if (dragR < DragRWeightRampEnd)
        {
            float t = (dragR - DragRMinDisplay) / (DragRWeightRampEnd - DragRMinDisplay);
            if (t < 0) t = 0; if (t > 1) t = 1;
            lum = 0.45f + 0.55f * t;
        }
        else if (dragR <= DragRWeightFalloffStart)
        {
            lum = 1.0f;
        }
        else if (dragR < DragRMaxValid)
        {
            float t = (DragRMaxValid - dragR) / (DragRMaxValid - DragRWeightFalloffStart);
            if (t < 0) t = 0; if (t > 1) t = 1;
            lum = 0.45f + 0.55f * t;
        }
        else
        {
            lum = 0.45f;
        }

        // R_local 决定色相段：暗灰 / 黄绿 / 鲜红。NaN 视同低一致性。
        float r = float.IsNaN(rLocal) ? 0f : rLocal;
        (float R, float G, float B) baseColor;
        if (r < RLocalLow)
        {
            // 暗灰段：60-90 灰度，让长建筑边一目了然不抢视线
            baseColor = (90f, 90f, 90f);
        }
        else if (r < RLocalHigh)
        {
            // 黄绿段：插值黄绿，让规则纹理可读但与抖动峰区分
            float t = (r - RLocalLow) / (RLocalHigh - RLocalLow);
            // 暗黄 (110, 130, 40) → 鲜黄绿 (200, 220, 60)
            baseColor = (
                110f + (200f - 110f) * t,
                130f + (220f - 130f) * t,
                40f + (60f - 40f) * t);
        }
        else
        {
            // 鲜红段：高 R_local 表达真抖动峰；250 红 + 适度橙偏
            float t = (r - RLocalHigh) / (1.0f - RLocalHigh);
            if (t > 1) t = 1;
            // 鲜红 (250, 70, 30) → 鲜橙 (255, 150, 50)
            baseColor = (
                250f + (255f - 250f) * t,
                70f + (150f - 70f) * t,
                30f + (50f - 30f) * t);
        }

        byte R8 = (byte)Math.Clamp((int)MathF.Round(baseColor.R * lum * contrastFactor), 0, 255);
        byte G8 = (byte)Math.Clamp((int)MathF.Round(baseColor.G * lum * contrastFactor), 0, 255);
        byte B8 = (byte)Math.Clamp((int)MathF.Round(baseColor.B * lum * contrastFactor), 0, 255);
        return (R8, G8, B8);
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
        var localRs = new List<float>(256); // R_local p10 用：参与拟合的格的 R_local 集合
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
                // r3：权重乘对比度软因子。低对比度格（沥青颗粒 / 玻璃折射）即使方向集中也少投票。
                // Contrast 长度可能为 0（旧路径退化），此时 cf=1 等价 r2 行为。
                float cf = field.Contrast.Length > i ? ContrastFactor(field.Contrast[i]) : 1f;
                wgt *= cf;
                if (wgt <= 0f) continue;
                float vx = wpx * MathF.Cos(dir);
                float vy = wpx * MathF.Sin(dir);
                samples.Add((x - cx, y - cy, vx, vy, wgt));
                float rl = field.LocalConsistency.Length > i ? field.LocalConsistency[i] : float.NaN;
                if (!float.IsNaN(rl)) localRs.Add(rl);
            }
        }

        if (samples.Count < 6)
        {
            float maskRatioEarly = samples.Count / (float)(Grid * Grid);
            return new RigidMotionResult { SampleCount = samples.Count, MaskRatio = maskRatioEarly };
        }

        // R_local p10：参与拟合的格的 R_local 排序后取第 10 百分位；区分"切向场全图相关"与"弱信号假旋转"。
        float rLocalP10 = 0f;
        if (localRs.Count > 0)
        {
            localRs.Sort();
            int p10Idx = (int)(localRs.Count * 0.10);
            if (p10Idx >= localRs.Count) p10Idx = localRs.Count - 1;
            rLocalP10 = localRs[p10Idx];
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
        // R_global：方向 θ 已存在 field.Direction 里；这里用 weight 加权对 2θ 做圆形均值。
        // 注意 v_i 经过迭代翻转，但方向 θ 是无极性 [0,π)，2θ 处理后翻转不影响。
        double cAcc = 0, sAcc = 0;
        foreach (var s in arr)
        {
            double rx = s.vx - (tx - omega * s.py);
            double ry = s.vy - (ty + omega * s.px);
            ssq += s.w * (rx * rx + ry * ry);
            wsum += s.w;
            // v_i = drag_width · (cos θ, sin θ)，θ 可由 atan2(vy, vx) 反推（符号翻转不影响 2θ）
            double th = Math.Atan2(s.vy, s.vx);
            double two = 2.0 * th;
            cAcc += s.w * Math.Cos(two);
            sAcc += s.w * Math.Sin(two);
        }
        double rms = wsum > 0 ? Math.Sqrt(ssq / (2.0 * wsum)) : 0;
        double tm = Math.Sqrt(tx * tx + ty * ty);
        double rGlobal = wsum > 1e-9 ? Math.Sqrt(cAcc * cAcc + sAcc * sAcc) / wsum : 0;
        if (rGlobal > 1.0) rGlobal = 1.0;

        // OmegaPxRatio：把 ω 换算到半对角线处的边缘像素位移，再算占比。
        double halfDiag = field.Diagonal * 0.5;
        double omegaPx = Math.Abs(omega) * halfDiag;
        double ratio = (tm + omegaPx) > 1e-3 ? omegaPx / (tm + omegaPx + 1e-3) : 0;
        float maskRatio = samples.Count / (float)(Grid * Grid);

        return new RigidMotionResult
        {
            SampleCount = samples.Count,
            WeightSum = (float)wsum,
            TranslationMagnitude = (float)tm,
            RotationMagnitude = (float)Math.Abs(omega),
            ResidualRms = (float)rms,
            DirectionalConsistency = (float)rGlobal,
            OmegaPxRatio = (float)ratio,
            MaskRatio = maskRatio,
            RLocalP10 = rLocalP10,
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

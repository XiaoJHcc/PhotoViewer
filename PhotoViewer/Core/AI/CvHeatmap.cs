using System;

namespace PhotoViewer.Core.AI;

/// <summary>
/// CV 网格可视化的归一化策略。决定 <see cref="CvHeatmap.Normalize"/> 取最大值的作用域。
/// </summary>
public enum CvHeatmapNormalize
{
    /// <summary>按单张 16×16 平面自身的最大值归一化;各层/各标量独立缩放。</summary>
    PerPlane = 0,

    /// <summary>同一标量沿 3 层金字塔共享最大值;可直观对比不同尺度下同一标量的强弱分布。</summary>
    PerScalarPyramid = 1,
}

/// <summary>
/// 诊断图类型。与工具页的 3 张 16×16 热力图对应。
/// </summary>
public enum CvDiagnostic
{
    /// <summary>失焦:laplacian_var × sobel_mean(对数复合)。</summary>
    DefocusSharp = 0,

    /// <summary>抖动:grad_dir_entropy(通过阈值 τ 做闸门)。</summary>
    MotionBlur = 1,

    /// <summary>金字塔一致性:同一网格在 level0/level1/level2 的 lap 值变异系数。</summary>
    PyramidConsistency = 2,
}

/// <summary>
/// CV 网格热力图的纯函数提供者(§A3.2 / §3.3)。
/// 输入 <see cref="CvGridResult"/>,输出三张 16×16 诊断图 + 5 个原始标量的每层可视化平面。
/// 所有方法无状态、线程安全;调用方自行决定何时重算。
/// </summary>
public static class CvHeatmap
{
    private const int Grid = CvGridResult.GridSize;

    /// <summary>
    /// 读取单张原始标量平面(16×16),用于"5 标量 × 3 层"网格快照。
    /// </summary>
    /// <param name="result">CV 提取结果。</param>
    /// <param name="pyramid">金字塔层 [0,3)。</param>
    /// <param name="scalar">标量索引 [0,5)。</param>
    /// <returns>长度 256 的一维平面(row-major)。</returns>
    public static float[] GetScalarPlane(CvGridResult result, int pyramid, int scalar)
    {
        ArgumentNullException.ThrowIfNull(result);
        var plane = new float[CvGridResult.PlaneLength];
        for (int y = 0; y < Grid; y++)
        {
            for (int x = 0; x < Grid; x++)
            {
                plane[y * Grid + x] = result.GetCell(pyramid, scalar, y, x);
            }
        }
        return plane;
    }

    /// <summary>
    /// 生成失焦诊断图:使用 level0 的 <c>laplacian_var × sobel_mean</c>(对数复合)。
    /// 两个标量相乘再 log1p,能压掉强纹理高方差区域的偏置,暴露"纹理弱+梯度弱"的整体失焦。
    /// </summary>
    /// <param name="result">CV 提取结果。</param>
    /// <returns>长度 256 的诊断图。数值未归一化,由 <see cref="Normalize"/> 统一处理。</returns>
    public static float[] BuildDefocus(CvGridResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var plane = new float[CvGridResult.PlaneLength];
        const int lapScalar = 0;       // laplacian_var
        const int sobelScalar = 1;     // sobel_mean
        for (int y = 0; y < Grid; y++)
        {
            for (int x = 0; x < Grid; x++)
            {
                float lap = result.GetCell(0, lapScalar, y, x);
                float sob = result.GetCell(0, sobelScalar, y, x);
                plane[y * Grid + x] = MathF.Log(1 + MathF.Max(0, lap) * MathF.Max(0, sob));
            }
        }
        return plane;
    }

    /// <summary>
    /// 生成抖动诊断图:<c>grad_dir_entropy &lt; τ</c> 的格子记 1,其余记 0。
    /// 单向梯度(低熵)是运动模糊的显著特征;τ 由调用方决定,默认 1.5(bit)。
    /// </summary>
    /// <param name="result">CV 提取结果。</param>
    /// <param name="tauBits">低熵阈值,单位 bit(8-bin 直方图熵上限 3.0)。</param>
    /// <returns>长度 256 的 0/1 掩膜。</returns>
    public static float[] BuildMotionBlur(CvGridResult result, float tauBits = 1.5f)
    {
        ArgumentNullException.ThrowIfNull(result);
        var plane = new float[CvGridResult.PlaneLength];
        const int entropyScalar = 2;   // grad_dir_entropy
        const int sobelScalar = 1;     // sobel_mean
        for (int y = 0; y < Grid; y++)
        {
            for (int x = 0; x < Grid; x++)
            {
                float ent = result.GetCell(0, entropyScalar, y, x);
                float sob = result.GetCell(0, sobelScalar, y, x);
                // 仅在"有梯度 + 低熵"时点亮,避免把平面低熵噪声误判为运动模糊。
                plane[y * Grid + x] = (ent < tauBits && sob > 1f) ? 1f : 0f;
            }
        }
        return plane;
    }

    /// <summary>
    /// 生成金字塔一致性诊断图:同格在 3 层 <c>laplacian_var</c> 上的变异系数 (std/mean)。
    /// 值越小代表尺度间稳定,越大代表"单层独有响应",常伴随噪声或边缘误判。
    /// </summary>
    public static float[] BuildPyramidConsistency(CvGridResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var plane = new float[CvGridResult.PlaneLength];
        const int lapScalar = 0;
        for (int y = 0; y < Grid; y++)
        {
            for (int x = 0; x < Grid; x++)
            {
                float v0 = result.GetCell(0, lapScalar, y, x);
                float v1 = result.GetCell(1, lapScalar, y, x);
                float v2 = result.GetCell(2, lapScalar, y, x);
                float mean = (v0 + v1 + v2) / 3f;
                if (mean < 1e-6f) { plane[y * Grid + x] = 0f; continue; }

                float d0 = v0 - mean, d1 = v1 - mean, d2 = v2 - mean;
                float var = (d0 * d0 + d1 * d1 + d2 * d2) / 3f;
                plane[y * Grid + x] = MathF.Sqrt(var) / mean;
            }
        }
        return plane;
    }

    /// <summary>
    /// 归一化到 [0,1]。<see cref="CvHeatmapNormalize.PerPlane"/>:以 <paramref name="plane"/> 自身 max 为参考;
    /// <see cref="CvHeatmapNormalize.PerScalarPyramid"/>:以 <paramref name="referenceMax"/> 为参考(调用方计算)。
    /// </summary>
    public static float[] Normalize(float[] plane, CvHeatmapNormalize mode, float referenceMax = 0f)
    {
        ArgumentNullException.ThrowIfNull(plane);
        float max;
        if (mode == CvHeatmapNormalize.PerPlane)
        {
            max = 0f;
            for (int i = 0; i < plane.Length; i++) if (plane[i] > max) max = plane[i];
        }
        else
        {
            max = referenceMax;
        }

        var result = new float[plane.Length];
        if (max < 1e-12f)
        {
            return result; // 全 0:原样返回全 0 图
        }

        for (int i = 0; i < plane.Length; i++)
        {
            float v = plane[i] / max;
            if (v < 0) v = 0;
            if (v > 1) v = 1;
            result[i] = v;
        }
        return result;
    }

    /// <summary>
    /// 取同一标量沿 3 层金字塔的全局最大值,供 <see cref="CvHeatmapNormalize.PerScalarPyramid"/> 使用。
    /// </summary>
    public static float ScalarPyramidMax(CvGridResult result, int scalar)
    {
        ArgumentNullException.ThrowIfNull(result);
        float max = 0f;
        for (int p = 0; p < CvGridResult.PyramidLevels; p++)
        {
            for (int y = 0; y < Grid; y++)
            {
                for (int x = 0; x < Grid; x++)
                {
                    float v = result.GetCell(p, scalar, y, x);
                    if (v > max) max = v;
                }
            }
        }
        return max;
    }
}

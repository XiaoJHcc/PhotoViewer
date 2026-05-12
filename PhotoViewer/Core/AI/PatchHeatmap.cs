using System;
using MathNet.Numerics.LinearAlgebra;

namespace PhotoViewer.Core.AI;

/// <summary>
/// DINO patch token 的可视化纯函数(§3.3 T4)。
/// 输入 1024×384 的 patch token 张量(token-major, <see cref="DinoFeatureExtractor.ExtractDualAsync"/> 导出),
/// 输出 32×32 的 PCA-RGB 预览图或点击 cosine 热力图。
/// </summary>
public static class PatchHeatmap
{
    /// <summary>patch 网格边长(32)。</summary>
    public const int Grid = DinoModelResources.PatchGrid;

    /// <summary>patch 个数(1024)。</summary>
    public const int Tokens = DinoModelResources.PatchTokenCount;

    /// <summary>每 token 特征维度(384)。</summary>
    public const int Dim = DinoModelResources.FeatureDim;

    /// <summary>
    /// 计算 PCA-RGB:取 patch tokens 的前 3 主成分,逐通道归一到 [0,1] → 32×32×3。
    /// 输出按 row-major 排布:index = (y * Grid + x) * 3 + channel。
    /// </summary>
    /// <param name="patchTokens">1024×384 的 patch 张量(token-major)。</param>
    /// <returns>长度 Grid × Grid × 3 的 RGB 图。</returns>
    public static float[] ComputePcaRgb(float[] patchTokens)
    {
        ArgumentNullException.ThrowIfNull(patchTokens);
        if (patchTokens.Length != Tokens * Dim)
            throw new ArgumentException($"patch tokens 长度应为 {Tokens * Dim},实际 {patchTokens.Length}", nameof(patchTokens));

        // 1) 构造 Tokens × Dim 矩阵并中心化
        var matrix = Matrix<double>.Build.Dense(Tokens, Dim, (i, j) => patchTokens[i * Dim + j]);

        for (int j = 0; j < Dim; j++)
        {
            double mean = 0;
            for (int i = 0; i < Tokens; i++) mean += matrix[i, j];
            mean /= Tokens;
            for (int i = 0; i < Tokens; i++) matrix[i, j] -= mean;
        }

        // 2) 经济型 SVD: X = U Σ Vᵀ;取 U[:, :3] × Σ[:3] 作为前 3 主成分投影
        var svd = matrix.Svd(computeVectors: true);
        var u = svd.U;      // Tokens × min(Tokens,Dim)
        var s = svd.S;      // min(Tokens,Dim)

        var projection = new double[Tokens * 3];
        int take = Math.Min(3, s.Count);
        for (int k = 0; k < take; k++)
        {
            double sk = s[k];
            for (int i = 0; i < Tokens; i++)
            {
                projection[i * 3 + k] = u[i, k] * sk;
            }
        }

        // 3) 每个通道独立 min-max 归一化到 [0,1]
        var rgb = new float[Tokens * 3];
        for (int k = 0; k < 3; k++)
        {
            double min = double.PositiveInfinity, max = double.NegativeInfinity;
            for (int i = 0; i < Tokens; i++)
            {
                double v = projection[i * 3 + k];
                if (v < min) min = v;
                if (v > max) max = v;
            }
            double range = max - min;
            if (range < 1e-12) range = 1;
            for (int i = 0; i < Tokens; i++)
            {
                rgb[i * 3 + k] = (float)((projection[i * 3 + k] - min) / range);
            }
        }
        return rgb;
    }

    /// <summary>
    /// 点击 cosine 热力图:取参考 patch 的特征向量,与所有 patch 做 cosine 相似度 → 32×32。
    /// 输出自动映射到 [0,1]:相似为 1,相反为 0。
    /// </summary>
    /// <param name="patchTokens">1024×384 的 patch 张量(token-major)。</param>
    /// <param name="refGridX">参考 patch 的网格 x(范围 [0,32))。</param>
    /// <param name="refGridY">参考 patch 的网格 y(范围 [0,32))。</param>
    /// <returns>长度 Grid × Grid 的相似度图。</returns>
    public static float[] ComputeRefCosine(float[] patchTokens, int refGridX, int refGridY)
    {
        ArgumentNullException.ThrowIfNull(patchTokens);
        if (patchTokens.Length != Tokens * Dim)
            throw new ArgumentException($"patch tokens 长度应为 {Tokens * Dim},实际 {patchTokens.Length}", nameof(patchTokens));
        if (refGridX < 0 || refGridX >= Grid || refGridY < 0 || refGridY >= Grid)
            throw new ArgumentOutOfRangeException(nameof(refGridX), "参考 patch 坐标越界");

        int refIndex = refGridY * Grid + refGridX;
        int refOffset = refIndex * Dim;

        // 参考向量 L2 范数
        double refNorm = 0;
        for (int j = 0; j < Dim; j++)
        {
            double v = patchTokens[refOffset + j];
            refNorm += v * v;
        }
        refNorm = Math.Sqrt(refNorm);
        if (refNorm < 1e-12) return new float[Tokens];

        var heatmap = new float[Tokens];
        for (int i = 0; i < Tokens; i++)
        {
            int offset = i * Dim;
            double dot = 0, norm = 0;
            for (int j = 0; j < Dim; j++)
            {
                double a = patchTokens[offset + j];
                double b = patchTokens[refOffset + j];
                dot += a * b;
                norm += a * a;
            }
            norm = Math.Sqrt(norm);
            if (norm < 1e-12)
            {
                heatmap[i] = 0.5f;
                continue;
            }
            double cos = dot / (norm * refNorm);
            // cos ∈ [-1,1] → [0,1]
            heatmap[i] = (float)((cos + 1.0) * 0.5);
        }
        return heatmap;
    }
}

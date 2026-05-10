using System;
using Avalonia.Platform;

namespace PhotoViewer.Core.AI;

/// <summary>
/// DINOv3 ViT-S/16 端侧模型的静态资源与预处理规格。
/// 改动此处需同步更新 Tools/export_dinov3_onnx.py 与 Tools/verify_onnx_parity.py。
/// </summary>
internal static class DinoModelResources
{
    /// <summary>模型资源 URI（Avalonia AssetLoader 路径）。</summary>
    public static readonly Uri ModelAssetUri = new("avares://PhotoViewer/Assets/Models/dinov3_vits16.onnx");

    /// <summary>正方形输入边长（像素）。</summary>
    public const int InputSize = 518;

    /// <summary>输入通道数（RGB）。</summary>
    public const int InputChannels = 3;

    /// <summary>[CLS] 特征向量维度（ViT-S/16）。</summary>
    public const int FeatureDim = 384;

    /// <summary>ImageNet 均值（RGB 顺序）。</summary>
    public static readonly float[] NormalizeMean = [0.485f, 0.456f, 0.406f];

    /// <summary>ImageNet 标准差（RGB 顺序）。</summary>
    public static readonly float[] NormalizeStd = [0.229f, 0.224f, 0.225f];

    /// <summary>ONNX 输入节点名。</summary>
    public const string InputName = "pixel_values";

    /// <summary>ONNX [CLS] 输出节点名（全局特征，用于相似聚类）。</summary>
    public const string ClsOutputName = "cls_embedding";

    /// <summary>ONNX patch tokens 输出节点名（M1 暂不消费，B 阶段美学评分使用）。</summary>
    public const string PatchOutputName = "patch_tokens";

    /// <summary>ViT 的 patch size（像素）。ViT-S/16 为 16。</summary>
    public const int PatchSize = 16;

    /// <summary>patch 网格边长 = InputSize / PatchSize。518/16 向下取整为 32。</summary>
    public const int PatchGrid = InputSize / PatchSize;

    /// <summary>patch token 数量 = PatchGrid²，与 ONNX `patch_tokens` 维度 N 一致。</summary>
    public const int PatchTokenCount = PatchGrid * PatchGrid;

    /// <summary>
    /// 模型标识符，写入 <c>photos.feature_model</c>。变更模型或预处理规格时必须更新，
    /// 以便读取侧识别历史缓存是否仍兼容当前推理配置。
    /// </summary>
    public const string ModelId = "dinov3_vits16_f32_518_v1";

    /// <summary>
    /// 从 Avalonia 资源读取模型字节；若资源不存在返回 null。
    /// </summary>
    public static byte[]? TryLoadModelBytes()
    {
        try
        {
            using var stream = AssetLoader.Open(ModelAssetUri);
            using var ms = new System.IO.MemoryStream();
            stream.CopyTo(ms);
            return ms.ToArray();
        }
        catch (System.IO.FileNotFoundException)
        {
            return null;
        }
    }
}

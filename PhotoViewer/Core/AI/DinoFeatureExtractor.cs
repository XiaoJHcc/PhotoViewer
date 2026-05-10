using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media.Imaging;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace PhotoViewer.Core.AI;

/// <summary>
/// DINOv3 [CLS] 特征提取器（ONNX Runtime CPU EP，M1 统一 CPU，不做平台分派）。
/// 线程安全：InferenceSession 内部线程安全，推理调用串行化到后台线程以避免竞争 Avalonia Bitmap。
/// </summary>
public static class DinoFeatureExtractor
{
    private static InferenceSession? _session;
    private static readonly object _sessionLock = new();
    private static readonly SemaphoreSlim _runGate = new(1, 1);

    /// <summary>
    /// 延迟加载 ONNX 会话；模型资源缺失会抛 <see cref="FileNotFoundException"/>。
    /// </summary>
    private static InferenceSession GetSession()
    {
        if (_session != null) return _session;
        lock (_sessionLock)
        {
            if (_session != null) return _session;

            var bytes = DinoModelResources.TryLoadModelBytes()
                ?? throw new FileNotFoundException(
                    $"DINOv3 ONNX 模型未找到：{DinoModelResources.ModelAssetUri}。运行 Tools/export_dinov3_onnx.py 生成后重新构建。");

            var options = new SessionOptions
            {
                GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
                InterOpNumThreads = 1,
                IntraOpNumThreads = Math.Max(1, Environment.ProcessorCount / 2),
            };
            _session = new InferenceSession(bytes, options);
            EnsureDualOutputSchema(_session);
            return _session;
        }
    }

    /// <summary>
    /// 校验 ONNX 模型同时导出 CLS 与 patch 两路输出。M1 虽然只消费 CLS，但缺 patch 端口
    /// 意味着导出脚本未更新（B 阶段上线时才发现就晚了），这里早失败早提示。
    /// </summary>
    private static void EnsureDualOutputSchema(InferenceSession session)
    {
        bool hasCls = false, hasPatch = false;
        foreach (var meta in session.OutputMetadata)
        {
            if (meta.Key == DinoModelResources.ClsOutputName) hasCls = true;
            else if (meta.Key == DinoModelResources.PatchOutputName) hasPatch = true;
        }
        if (!hasCls || !hasPatch)
        {
            throw new InvalidOperationException(
                $"DINOv3 ONNX 模型缺少必要输出端口：cls={hasCls}, patch={hasPatch}。" +
                $"请用最新 Tools/export_dinov3_onnx.py 重新导出（CLS + patch 双输出）。");
        }
    }

    /// <summary>
    /// 从已解码位图提取 384 维 [CLS] 特征向量。L2 归一化后返回。
    /// </summary>
    /// <param name="bitmap">已应用 EXIF 旋转的位图。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>长度 384 的浮点向量。</returns>
    public static async Task<float[]> ExtractAsync(Bitmap bitmap, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(bitmap);

        var tensor = BuildInputTensor(bitmap);
        await _runGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await Task.Run(() => RunInference(tensor), ct).ConfigureAwait(false);
        }
        finally
        {
            _runGate.Release();
        }
    }

    /// <summary>
    /// 执行 ONNX 推理并从输出张量拷出向量，随后 L2 归一化。
    /// </summary>
    private static float[] RunInference(DenseTensor<float> input)
    {
        var session = GetSession();
        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor(DinoModelResources.InputName, input),
        };

        using var outputs = session.Run(inputs);
        // M1 只消费 CLS；patch_tokens 端口已就绪但闲置，B 阶段由 PatchFeatureExtractor 接管。
        var cls = outputs.First(v => v.Name == DinoModelResources.ClsOutputName).AsTensor<float>();

        var result = new float[DinoModelResources.FeatureDim];
        int i = 0;
        foreach (var v in cls)
        {
            if (i >= result.Length) break;
            result[i++] = v;
        }
        L2Normalize(result);
        return result;
    }

    /// <summary>
    /// 将向量 L2 归一化到单位长度；长度为 0 时保持原样。
    /// </summary>
    private static void L2Normalize(Span<float> v)
    {
        double sum = 0;
        for (int i = 0; i < v.Length; i++) sum += v[i] * v[i];
        var norm = (float)Math.Sqrt(sum);
        if (norm < 1e-12f) return;
        for (int i = 0; i < v.Length; i++) v[i] /= norm;
    }

    /// <summary>
    /// 将 Avalonia 位图缩放到方形 518×518 并构造 ONNX 输入张量（NCHW, RGB, 归一化）。
    /// </summary>
    private static DenseTensor<float> BuildInputTensor(Bitmap source)
    {
        int size = DinoModelResources.InputSize;
        var target = new RenderTargetBitmap(new PixelSize(size, size));
        using (var ctx = target.CreateDrawingContext())
        {
            ctx.DrawImage(source, new Rect(0, 0, size, size));
        }

        int channels = DinoModelResources.InputChannels;
        int rowBytes = size * 4;
        int totalBytes = rowBytes * size;
        var rented = ArrayPool<byte>.Shared.Rent(totalBytes);
        try
        {
            unsafe
            {
                fixed (byte* ptr = rented)
                {
                    target.CopyPixels(
                        new PixelRect(0, 0, size, size),
                        (IntPtr)ptr,
                        totalBytes,
                        rowBytes);
                }
            }

            var tensor = new DenseTensor<float>([1, channels, size, size]);
            var buf = tensor.Buffer.Span;
            var mean = DinoModelResources.NormalizeMean;
            var std = DinoModelResources.NormalizeStd;
            int plane = size * size;

            for (int y = 0; y < size; y++)
            {
                int rowOffset = y * rowBytes;
                for (int x = 0; x < size; x++)
                {
                    int i = rowOffset + x * 4;
                    // Avalonia RenderTargetBitmap 默认输出 BGRA8888（Bgra8888Premul），这里按 BGRA 读取。
                    byte b = rented[i + 0];
                    byte g = rented[i + 1];
                    byte r = rented[i + 2];

                    int pixelIdx = y * size + x;
                    buf[0 * plane + pixelIdx] = (r / 255f - mean[0]) / std[0];
                    buf[1 * plane + pixelIdx] = (g / 255f - mean[1]) / std[1];
                    buf[2 * plane + pixelIdx] = (b / 255f - mean[2]) / std[2];
                }
            }

            return tensor;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
            target.Dispose();
        }
    }
}

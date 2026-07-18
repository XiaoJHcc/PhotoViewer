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
/// DINOv3 [CLS] 特征提取器（ONNX Runtime，平台 EP 通过 <see cref="ConfigureSession"/> 注入）。
/// 线程安全：InferenceSession 内部线程安全，推理调用串行化到后台线程以避免竞争 Avalonia Bitmap。
/// </summary>
public static class DinoFeatureExtractor
{
    private static InferenceSession? _session;
    private static readonly object _sessionLock = new();
    private static readonly SemaphoreSlim _runGate = new(1, 1);
    private static Action<SessionOptions>? _configureSession;
    private static bool _gpuFailed;

    /// <summary>
    /// 注入平台专属 Execution Provider 配置（DirectML / CoreML / NNAPI）。
    /// 必须在首次推理前调用，否则 session 已创建后不再生效。
    /// </summary>
    public static void ConfigureSession(Action<SessionOptions> configure)
    {
        _configureSession = configure;
    }

    /// <summary>
    /// 延迟加载 ONNX 会话；GPU EP 初始化失败时自动回退 CPU。
    /// 模型资源缺失会抛 <see cref="FileNotFoundException"/>。
    /// </summary>
    private static InferenceSession GetSession()
    {
        if (_session != null) return _session;
        lock (_sessionLock)
        {
            if (_session != null) return _session;
            _session = CreateSession(useGpu: !_gpuFailed);
            EnsureDualOutputSchema(_session);
            return _session;
        }
    }

    /// <summary>
    /// GPU 推理失败后丢弃当前 session，标记 GPU 不可用，下次 GetSession 重建 CPU session。
    /// </summary>
    private static void InvalidateSessionForGpuFallback()
    {
        lock (_sessionLock)
        {
            _gpuFailed = true;
            _session?.Dispose();
            _session = null;
        }
    }

    private static InferenceSession CreateSession(bool useGpu)
    {
        var bytes = DinoModelResources.TryLoadModelBytes()
            ?? throw new FileNotFoundException(
                $"DINOv3 ONNX 模型未找到：{DinoModelResources.ModelAssetUri}。运行 Training/onnx/export_dinov3_onnx.py 生成后重新构建。");

        var options = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            InterOpNumThreads = 1,
            IntraOpNumThreads = Math.Max(1, Environment.ProcessorCount / 2),
        };

        if (useGpu && _configureSession != null)
        {
            try
            {
                _configureSession(options);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[DinoFeatureExtractor] GPU EP 注册失败，回退 CPU: {ex.Message}");
                _gpuFailed = true;
                options = new SessionOptions
                {
                    GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
                    InterOpNumThreads = 1,
                    IntraOpNumThreads = Math.Max(1, Environment.ProcessorCount / 2),
                };
            }
        }

        return new InferenceSession(bytes, options);
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
                $"请用最新 Training/onnx/export_dinov3_onnx.py 重新导出（CLS + patch 双输出）。");
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
        var (cls, _) = await ExtractDualAsync(bitmap, includePatches: false, ct).ConfigureAwait(false);
        return cls;
    }

    /// <summary>
    /// 双输出推理:同时返回 CLS 向量与 patch token 张量(工具页可视化用)。
    /// CLS 强制 L2 归一化,patch 保留原始值。includePatches=false 时跳过 patch 拷贝以节省内存。
    /// </summary>
    /// <param name="bitmap">已应用 EXIF 旋转的位图。</param>
    /// <param name="includePatches">是否拷贝 patch token 数据。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>CLS 向量(长度 384,L2 归一化)与 patch token 张量(长度 1024×384,按 token-major 排布;includePatches=false 时为 null)。</returns>
    public static async Task<(float[] Cls, float[]? Patches)> ExtractDualAsync(
        Bitmap bitmap, bool includePatches, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(bitmap);

        var tensor = BuildInputTensor(bitmap);
        await _runGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await Task.Run(() => RunInferenceDual(tensor, includePatches), ct).ConfigureAwait(false);
        }
        finally
        {
            _runGate.Release();
        }
    }

    /// <summary>
    /// 执行 ONNX 推理,同时拷出 CLS 与 patch 两路输出。
    /// GPU EP 推理失败时自动回退 CPU 重试一次。
    /// </summary>
    private static (float[] Cls, float[]? Patches) RunInferenceDual(DenseTensor<float> input, bool includePatches)
    {
        var session = GetSession();
        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor(DinoModelResources.InputName, input),
        };

        IDisposableReadOnlyCollection<DisposableNamedOnnxValue> outputs;
        try
        {
            outputs = session.Run(inputs);
        }
        catch (Exception ex) when (!_gpuFailed)
        {
            Console.WriteLine($"[DinoFeatureExtractor] GPU 推理失败，回退 CPU 重试: {ex.Message}");
            InvalidateSessionForGpuFallback();
            session = GetSession();
            outputs = session.Run(inputs);
        }

        using (outputs)
        {
            var cls = outputs.First(v => v.Name == DinoModelResources.ClsOutputName).AsTensor<float>();
            var clsVec = new float[DinoModelResources.FeatureDim];
            int i = 0;
            foreach (var v in cls)
            {
                if (i >= clsVec.Length) break;
                clsVec[i++] = v;
            }
            L2Normalize(clsVec);

            float[]? patchVec = null;
            if (includePatches)
            {
                var patches = outputs.First(v => v.Name == DinoModelResources.PatchOutputName).AsTensor<float>();
                int total = DinoModelResources.PatchTokenCount * DinoModelResources.FeatureDim;
                patchVec = new float[total];
                int j = 0;
                foreach (var v in patches)
                {
                    if (j >= total) break;
                    patchVec[j++] = v;
                }
            }

            return (clsVec, patchVec);
        }
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

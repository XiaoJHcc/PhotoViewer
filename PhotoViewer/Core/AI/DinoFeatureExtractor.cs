using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using PhotoViewer.Core.Image;
using PhotoViewer.Core.Memory;

namespace PhotoViewer.Core.AI;

/// <summary>
/// DINOv3 [CLS] 特征提取器（ONNX Runtime，平台 EP 通过 <see cref="ConfigureSession"/> 注入）。
/// 线程安全：InferenceSession 内部线程安全，推理调用串行化到后台线程执行。
/// </summary>
public static class DinoFeatureExtractor
{
    private static InferenceSession? _session;
    private static readonly object _sessionLock = new();
    private static readonly SemaphoreSlim _runGate = new(1, 1);
    private static Action<SessionOptions>? _configureSession;
    private static bool _gpuFailed;
    private static byte[]? _modelBytesOverride;
    private static long _sessionFootprintBytes; // 内存账本:模型字节 + 会话工作区估算
    private static int _inputSize = DinoModelResources.InputSize;   // 模型实际输入边长（518；升级梯换大分辨率模型时按 ONNX 输入元数据自适应）

    static DinoFeatureExtractor()
    {
        MemoryGovernor.Register(new CacheMemoryClient());
    }

    /// <summary>
    /// 注入平台专属 Execution Provider 配置（DirectML / CoreML / NNAPI）。
    /// 必须在首次推理前调用，否则 session 已创建后不再生效。
    /// </summary>
    public static void ConfigureSession(Action<SessionOptions> configure)
    {
        _configureSession = configure;
    }

    /// <summary>
    /// （仅训练工具）覆盖 ONNX 模型字节来源，必须在首次推理前调用；session 已建后不再生效。
    /// DatasetBuilder 升级梯实验（如 ViT-L 重提）用文件模型替代端侧资源模型；产品代码不调用。
    /// </summary>
    public static void ConfigureModelOverride(byte[] modelBytes)
    {
        _modelBytesOverride = modelBytes;
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
            DetectInputSize(_session);
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
        var bytes = _modelBytesOverride
            ?? DinoModelResources.TryLoadModelBytes()
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

        // 内存账本:模型字节 + 128MB 会话工作区估算(低估可接受,供压力快照判读数量级)
        _sessionFootprintBytes = bytes.Length + 128L * 1024 * 1024;
        return new InferenceSession(bytes, options);
    }

    /// <summary>
    /// 卸载 ONNX 会话(内存压力响应):先取推理闸确保无在途推理,再释放;下次 <see cref="GetSession"/> 惰性重建。
    /// GPU 回退标记与输入尺寸自适应结果保持。
    /// </summary>
    public static void UnloadSession()
    {
        _runGate.Wait();
        try
        {
            lock (_sessionLock)
            {
                _session?.Dispose();
                _session = null;
                _sessionFootprintBytes = 0;
            }
        }
        finally
        {
            _runGate.Release();
        }
    }

    // ---- 统一内存管控(P1-M2)----

    /// <summary>内存客户端:ONNX 会话占用;Warning 起卸载(下次推理惰性重建)。</summary>
    private sealed class CacheMemoryClient : IMemoryClient
    {
        public string Name => "DinoOnnxSession";
        public long FootprintBytes => _session != null ? _sessionFootprintBytes : 0;
        public void Trim(PressureLevel level)
        {
            if (level >= PressureLevel.Warning) UnloadSession();
        }
    }

    /// <summary>
    /// 从 ONNX 输入元数据读取模型实际输入边长（NCHW 的 H），供预处理缩放对齐；
    /// 读不到（动态维度）时保持内置默认 518。升级梯大分辨率模型（如 1024）靠此免改代码。
    /// </summary>
    private static void DetectInputSize(InferenceSession session)
    {
        if (session.InputMetadata.TryGetValue(DinoModelResources.InputName, out var meta)
            && meta.Dimensions.Length == 4 && meta.Dimensions[2] > 0)
        {
            _inputSize = meta.Dimensions[2];
            Console.WriteLine($"[DinoFeatureExtractor] 模型输入边长 = {_inputSize}（按 ONNX 元数据）");
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
                $"请用最新 Training/onnx/export_dinov3_onnx.py 重新导出（CLS + patch 双输出）。");
        }
    }

    /// <summary>
    /// 从已解码图像提取 [CLS] 特征向量（维度随模型，ViT-S=384）。L2 归一化后返回。
    /// </summary>
    /// <param name="image">已应用 EXIF 旋转的图像。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>浮点向量，维度随模型（ViT-S=384）。</returns>
    public static async Task<float[]> ExtractAsync(DecodedImage image, CancellationToken ct = default)
    {
        var (cls, _) = await ExtractDualAsync(image, includePatches: false, ct).ConfigureAwait(false);
        return cls;
    }

    /// <summary>
    /// 双输出推理:同时返回 CLS 向量与 patch token 张量(工具页可视化用)。
    /// CLS 强制 L2 归一化,patch 保留原始值。includePatches=false 时跳过 patch 拷贝以节省内存。
    /// </summary>
    /// <param name="image">已应用 EXIF 旋转的图像。</param>
    /// <param name="includePatches">是否拷贝 patch token 数据。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>CLS 向量(维度随模型,ViT-S=384;L2 归一化)与 patch token 张量(token 数×维度随模型,ViT-S=1024×384,按 token-major 排布;includePatches=false 时为 null)。</returns>
    public static async Task<(float[] Cls, float[]? Patches)> ExtractDualAsync(
        DecodedImage image, bool includePatches, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(image);

        GetSession();   // 先建会话以完成输入尺寸自适应（升级梯大分辨率模型）
        var tensor = BuildInputTensor(image);
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
            // 维度按张量实测（ViT-S=384 / ViT-L=1024），训练工具换模型时无需改这里。
            var clsVec = new float[cls.Length];
            int i = 0;
            foreach (var v in cls) clsVec[i++] = v;
            L2Normalize(clsVec);

            float[]? patchVec = null;
            if (includePatches)
            {
                var patches = outputs.First(v => v.Name == DinoModelResources.PatchOutputName).AsTensor<float>();
                patchVec = new float[patches.Length];
                int j = 0;
                foreach (var v in patches) patchVec[j++] = v;
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
    /// 将图像拉伸缩放到方形模型输入边长（默认 518×518，升级梯按 ONNX 元数据自适应）。
    /// 提取为公共方法：DatasetBuilder --dump-render 用同一路径产出训练用渲染缓存，
    /// 保证训练/推理像素严格同分布（Python 重采样已证与 CLS 不可比，见 EXECUTION-LOG）。
    /// </summary>
    public static DecodedImage RenderInputImage(DecodedImage source)
    {
        int size = _inputSize;
        var scaled = SkiaImageCodec.ScaleToExact(source, size, size);
        if (scaled == null) throw new InvalidOperationException("模型输入缩放失败");
        return scaled;
    }

    /// <summary>
    /// 将图像拉伸缩放到方形模型输入边长并构造 ONNX 输入张量（NCHW, RGB, 归一化）。
    /// </summary>
    private static DenseTensor<float> BuildInputTensor(DecodedImage source)
    {
        int size = _inputSize;
        using var input = RenderInputImage(source);

        int channels = DinoModelResources.InputChannels;
        var tensor = new DenseTensor<float>([1, channels, size, size]);
        var buf = tensor.Buffer.Span;
        var mean = DinoModelResources.NormalizeMean;
        var std = DinoModelResources.NormalizeStd;
        int plane = size * size;
        var pixels = input.Pixels;
        int stride = input.Stride;

        for (int y = 0; y < size; y++)
        {
            long rowOffset = (long)y * stride;
            for (int x = 0; x < size; x++)
            {
                long i = rowOffset + x * 4;
                // Bgra32 字节序: B,G,R,A
                byte b = pixels[i + 0];
                byte g = pixels[i + 1];
                byte r = pixels[i + 2];

                int pixelIdx = y * size + x;
                buf[0 * plane + pixelIdx] = (r / 255f - mean[0]) / std[0];
                buf[1 * plane + pixelIdx] = (g / 255f - mean[1]) / std[1];
                buf[2 * plane + pixelIdx] = (b / 255f - mean[2]) / std[2];
            }
        }

        return tensor;
    }
}

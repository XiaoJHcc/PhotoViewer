using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using PhotoViewer.Core.Color;
using PhotoViewer.Core.Image;
using PhotoViewer.Core.Memory;

namespace PhotoViewer.Core.AI;

/// <summary>
/// DINOv3 [CLS] 特征提取器（ONNX Runtime，平台 EP 通过 <see cref="ConfigureSession"/> 注入）。
/// 推理经内部优先级闸串行化：交互请求优先于尚未开跑的批量后台请求，会话创建与推理同在闸内。
/// </summary>
public static class DinoFeatureExtractor
{
    private static InferenceSession? _session;
    private const long SessionWorkspaceBytes = 128L * 1024 * 1024;
    private static readonly object _sessionLock = new();
    private static readonly PriorityRunGate _runGate = new();
    private static int _backgroundRuns;   // 在途或排队的批量后台推理数（全局，跨执行流）
    private static int _unloadPending;    // 1 = 已请求卸载，由持闸方在退出临界区时处理
    private static Action<SessionOptions>? _configureSession;
    private static bool _gpuFailed;
    private static byte[]? _modelBytesOverride;
    private static long _sessionFootprintBytes; // 内存账本:模型字节 + 会话工作区估算
    private static int _inputSize = DinoModelResources.InputSize;   // 模型实际输入边长（518；升级梯换大分辨率模型时按 ONNX 输入元数据自适应）

    /// <summary>
    /// 当前 ONNX 会话是否成功注册了平台 GPU Execution Provider（诊断/验收用）。
    /// GPU EP 注册抛错或推理失败回退 CPU 后为 false。
    /// </summary>
    public static bool UsesGpuExecutionProvider { get; private set; }

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
            // 先在本地校验再发布：元数据不符时不留一个坏会话。
            var session = CreateSession(useGpu: !_gpuFailed);
            try
            {
                EnsureDualOutputSchema(session);
                DetectInputSize(session);
            }
            catch
            {
                session.Dispose();
                _sessionFootprintBytes = 0;
                throw;
            }
            _session = session;
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
            UsesGpuExecutionProvider = false;
            _session?.Dispose();
            _session = null;
        }
    }

    private static InferenceSession CreateSession(bool useGpu)
    {
        var bytes = _modelBytesOverride
            ?? DinoModelResources.TryLoadModelBytes()
            ?? throw new FileNotFoundException(
                $"DINOv3 ONNX 模型未找到：{DinoModelResources.ModelAssetUri}。按 DEV.md「DINOv3 ONNX 模型」从独立仓 PhotoViewer-Training 导出到 PhotoViewer/Assets/Models/ 后重新构建。");

        // 批量作用域内为本次构建申请准入:几 MB 的缩略图 root 盖不住模型 + 工作区。
        // 模型 blob 在 TryLoadModelBytes 返回时已分配(该 API 不暴露 size),故按"模型 + 工作区"整体入账;
        // 许可只覆盖构建与建会话,不常驻在途账本,常驻占用由 MemoryClient 的 footprint 跟踪。
        using var permit = AdmitSession(bytes.Length);

        var options = CreateSessionOptions();
        try
        {
            if (useGpu && _configureSession != null)
            {
                try
                {
                    _configureSession(options);
                    UsesGpuExecutionProvider = true;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[DinoFeatureExtractor] GPU EP 注册失败，回退 CPU: {ex.Message}");
                    _gpuFailed = true;
                    UsesGpuExecutionProvider = false;
                    options.Dispose();
                    options = CreateSessionOptions();
                }
            }

            // 内存账本:模型字节 + 会话工作区估算(低估可接受,供压力快照判读数量级)
            _sessionFootprintBytes = bytes.Length + SessionWorkspaceBytes;
            return new InferenceSession(bytes, options);
        }
        finally
        {
            options.Dispose();
        }
    }

    /// <summary>批量作用域内为本次建会话申请模型 + 工作区准入;真实内存不足抛 <see cref="AnalysisMemoryException"/>。</summary>
    private static MemoryGovernor.AdmissionPermit? AdmitSession(long modelBytes)
    {
        if (!BatchAnalysisContext.IsActive) return null;
        long request = modelBytes + SessionWorkspaceBytes;
        if (MemoryGovernor.TryAcquire(request, DecodePriority.P3_Background, out var permit) && permit != null)
            return permit;
        throw new AnalysisMemoryException("DINO 会话内存不足", request);
    }

    /// <summary>会话选项:图优化 + 单线程 inter-op、全核 intra-op;关闭线程自旋,把 CPU 让给其它并行段。</summary>
    private static SessionOptions CreateSessionOptions()
    {
        var options = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            InterOpNumThreads = 1,
            IntraOpNumThreads = Math.Max(1, Environment.ProcessorCount),
        };
        try
        {
            options.AddSessionConfigEntry("session.intra_op.allow_spinning", "0");
            options.AddSessionConfigEntry("session.inter_op.allow_spinning", "0");
            return options;
        }
        catch
        {
            options.Dispose();
            throw;
        }
    }

    /// <summary>卸载 ONNX 会话(非 UI 线程调用)：先取推理闸确保无在途推理再释放，下次推理惰性重建。</summary>
    public static void UnloadSession()
    {
        using var lease = _runGate.Acquire(CancellationToken.None);
        Interlocked.Exchange(ref _unloadPending, 0);
        UnloadSessionCore();
    }

    /// <summary>释放会话本体;调用方必须已持有推理闸。</summary>
    private static void UnloadSessionCore()
    {
        lock (_sessionLock)
        {
            _session?.Dispose();
            _session = null;
            _sessionFootprintBytes = 0;
        }
    }

    /// <summary>
    /// 批量首次需要 DINO 时预热模型会话：不持有图片与工作集 root，闸内建会话后立即释放。
    /// 准入被拒先放开闸再等一次 root 释放；没有 root 可等就回收缓存后重试一次；Critical 直接失败。
    /// </summary>
    public static async Task PrepareBatchSessionAsync(CancellationToken ct = default)
    {
        Interlocked.Increment(ref _backgroundRuns);
        try
        {
            bool waitedForRoot = false;
            bool reclaimed = false;
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    using var lease = await _runGate.AcquireAsync(background: true, ct).ConfigureAwait(false);
                    if (_session != null) return;
                    await Task.Run(() => GetSession(), ct).ConfigureAwait(false);
                    return;
                }
                catch (AnalysisMemoryException ex)
                {
                    // 闸已随 using 释放：等内存与回收缓存都不在推理临界区内进行。
                    if (MemoryGovernor.CurrentLevel == PressureLevel.Critical) throw;

                    if (!waitedForRoot && BatchAnalysisContext.WaitForAnalysisRootRelease() is { } waiter)
                    {
                        waitedForRoot = true;
                        using (waiter) await waiter.Task.WaitAsync(ct).ConfigureAwait(false);
                        continue;
                    }

                    if (reclaimed) throw;
                    reclaimed = true;
                    BitmapLoader.ReclaimForAnalysis(ex.Bytes);
                }
            }
        }
        finally
        {
            Interlocked.Decrement(ref _backgroundRuns);
        }
    }

    /// <summary>非阻塞请求卸载：闸空闲就立刻卸载，否则留给下一次推理退出临界区时处理（Trim 不等在途推理）。</summary>
    private static void RequestUnload(PressureLevel level)
    {
        // 有批量后台推理在途或排队时不卸载（Critical 例外）：建销会话的抖动会把整批拖慢。
        if (level < PressureLevel.Critical && Volatile.Read(ref _backgroundRuns) > 0) return;

        // 先置位再尝试空闲卸载：持闸者可能刚处理完 pending 并释放，晚置位就没人再处理。
        Interlocked.Exchange(ref _unloadPending, 1);
        if (_runGate.TryAcquireIdle() is not { } lease) return;
        using (lease)
        {
            if (Interlocked.Exchange(ref _unloadPending, 0) != 0) UnloadSessionCore();
        }
    }

    // ---- 统一内存管控 ----

    /// <summary>内存客户端:ONNX 会话占用;Warning 起请求卸载(下次推理惰性重建,不阻塞调用方)。</summary>
    private sealed class CacheMemoryClient : IMemoryClient
    {
        public string Name => "DinoOnnxSession";
        public long FootprintBytes => _session != null ? _sessionFootprintBytes : 0;
        public void Trim(PressureLevel level)
        {
            if (level >= PressureLevel.Warning) RequestUnload(level);
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
    /// <param name="background">批量后台请求:排在交互请求之后,在途期间不受非 Critical 的会话卸载影响。</param>
    /// <returns>CLS 向量(维度随模型,ViT-S=384;L2 归一化)与 patch token 张量(token 数×维度随模型,ViT-S=1024×384,按 token-major 排布;includePatches=false 时为 null)。</returns>
    public static async Task<(float[] Cls, float[]? Patches)> ExtractDualAsync(
        DecodedImage image, bool includePatches, CancellationToken ct = default, bool background = false)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (background) Interlocked.Increment(ref _backgroundRuns);
        try
        {
            using var lease = await _runGate.AcquireAsync(background, ct).ConfigureAwait(false);
            try
            {
                // 会话创建、元数据校验、输入张量与推理(含 GPU 回退)整段在闸内且在线程池执行。
                return await Task.Run(() => RunGated(image, includePatches), ct).ConfigureAwait(false);
            }
            finally
            {
                // 持闸处理待卸载，然后才释放：排队项接力时也不会让 Critical 一直等不到空闲。
                if (Interlocked.Exchange(ref _unloadPending, 0) != 0) UnloadSessionCore();
            }
        }
        finally
        {
            if (background) Interlocked.Decrement(ref _backgroundRuns);
        }
    }

    /// <summary>闸内的完整推理段：建会话、构造输入张量、执行推理。</summary>
    private static (float[] Cls, float[]? Patches) RunGated(DecodedImage image, bool includePatches)
    {
        var session = GetSession();   // 建会话同时完成输入尺寸自适应（升级梯大分辨率模型）
        var tensor = BuildInputTensor(image);
        return RunInferenceDual(session, tensor, includePatches);
    }

    /// <summary>
    /// 执行一次 native 推理：批量作用域内的 CPU 后端排到独占调度器，与 CV/色彩/YUV 的并行段互斥；
    /// GPU 后端与交互请求同步直跑，不排队。只调 Run 本身，不把含并行转换的取图/建张量卷进来。
    /// 取消令牌只作用于"尚未开跑"的调度；已开始的 native 推理等到结束才返回。
    /// </summary>
    private static IDisposableReadOnlyCollection<DisposableNamedOnnxValue> RunSession(
        InferenceSession session, List<NamedOnnxValue> inputs)
    {
        if (!BatchAnalysisContext.IsActive || UsesGpuExecutionProvider) return session.Run(inputs);

        return Task.Factory
            .StartNew(
                () => session.Run(inputs),
                BatchAnalysisContext.CancellationToken(),
                TaskCreationOptions.DenyChildAttach,
                BatchAnalysisContext.CpuExclusiveScheduler)
            .GetAwaiter().GetResult();
    }

    /// <summary>
    /// 执行 ONNX 推理,同时拷出 CLS 与 patch 两路输出。
    /// GPU EP 推理失败时自动回退 CPU 重试一次;取消/内存准入/调度器故障不走这条回退。
    /// </summary>
    private static (float[] Cls, float[]? Patches) RunInferenceDual(InferenceSession session, DenseTensor<float> input, bool includePatches)
    {
        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor(DinoModelResources.InputName, input),
        };

        IDisposableReadOnlyCollection<DisposableNamedOnnxValue> outputs;
        try
        {
            outputs = RunSession(session, inputs);
        }
        catch (Exception ex) when (!_gpuFailed
            && ex is not OperationCanceledException
            && ex is not AnalysisMemoryException
            && ex is not TaskSchedulerException)
        {
            Console.WriteLine($"[DinoFeatureExtractor] GPU 推理失败，回退 CPU 重试: {ex.Message}");
            InvalidateSessionForGpuFallback();
            session = GetSession();
            outputs = RunSession(session, inputs);
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
    /// 输入口径固定为 **sRGB8**：广色域 / 线性 / 未标记来源先显式规范成 sRGB8，再按线性采样缩放，
    /// 保证模型看到的像素与训练管线（同一入口）一致、不随文件源色域漂移。
    /// 提取为公共方法：DatasetBuilder --dump-render 用同一路径产出训练用渲染缓存，
    /// 保证训练/推理像素严格同分布（Python 重采样已证与 CLS 不可比，见 EXECUTION-LOG）。
    /// </summary>
    public static DecodedImage RenderInputImage(DecodedImage source)
    {
        int size = _inputSize;
        var srgb = source.Encoding == PixelEncoding.Bgra32 && source.ColorSpace == ImageColorSpace.Srgb
            ? null
            : ColorEngine.ToSrgb8(source);
        try
        {
            var scaled = SkiaImageCodec.ScaleToExact(srgb ?? source, size, size);
            if (scaled == null) throw new InvalidOperationException("模型输入缩放失败");
            return scaled;
        }
        finally
        {
            srgb?.Release();
        }
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

    /// <summary>推理闸：单推理临界区，交互请求优先于尚未开跑的批量后台请求；等待项取消即出队，授予/取消竞态下只释放一次。</summary>
    private sealed class PriorityRunGate
    {
        private readonly object _lock = new();
        private readonly LinkedList<Waiter> _foreground = new();
        private readonly LinkedList<Waiter> _background = new();
        private bool _busy;

        /// <summary>排队取闸；<paramref name="background"/> 为 true 的等待项排在任何交互等待项之后。</summary>
        public async Task<Lease> AcquireAsync(bool background, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            Waiter? waiter = null;
            lock (_lock)
            {
                if (_busy)
                {
                    waiter = new Waiter(background);
                    waiter.Node = background ? _background.AddLast(waiter) : _foreground.AddLast(waiter);
                }
                else
                {
                    _busy = true;
                }
            }
            if (waiter is null) return new Lease(this);

            // 取消注册在锁外建立与释放：回调只做"从队列摘掉自己"，与授予互斥。
            var pending = waiter;
            CancellationTokenRegistration registration = default;
            try
            {
                if (cancellationToken.CanBeCanceled)
                    registration = cancellationToken.Register(() => CancelWaiter(pending, cancellationToken));
                await pending.Completion.Task.ConfigureAwait(false);
            }
            finally
            {
                registration.Dispose();
            }
            return new Lease(this);
        }

        /// <summary>同步阻塞取闸：<see cref="UnloadSession"/> 等非 UI 调用沿用原契约。</summary>
        public Lease Acquire(CancellationToken cancellationToken)
            => AcquireAsync(background: false, cancellationToken).GetAwaiter().GetResult();

        /// <summary>闸空闲（无人持闸、无人排队）时立即进入；否则返回 null，不排队也不等待。</summary>
        public Lease? TryAcquireIdle()
        {
            lock (_lock)
            {
                if (_busy || _foreground.Count != 0 || _background.Count != 0) return null;
                _busy = true;
                return new Lease(this);
            }
        }

        private void Release()
        {
            Waiter? next;
            lock (_lock)
            {
                next = TakeLocked(_foreground) ?? TakeLocked(_background);
                // 有等待项时把持闸权直接交给它（_busy 保持 true），否则才回到空闲。
                if (next is null) _busy = false;
            }
            // 锁外唤醒：continuation 异步执行，推理不在锁内开跑。
            next?.Completion.TrySetResult(true);
        }

        private static Waiter? TakeLocked(LinkedList<Waiter> waiters)
        {
            while (waiters.First is { } node)
            {
                waiters.RemoveFirst();
                var waiter = node.Value;
                waiter.Node = null;
                if (waiter.Granted) continue;
                waiter.Granted = true;
                return waiter;
            }
            return null;
        }

        private void CancelWaiter(Waiter waiter, CancellationToken cancellationToken)
        {
            lock (_lock)
            {
                var node = waiter.Node;
                // 已授予的等待项不在这里取消：释放归持闸方，保证有且仅有一次。
                if (waiter.Granted || node is null) return;
                (waiter.Background ? _background : _foreground).Remove(node);
                waiter.Node = null;
            }
            waiter.Completion.TrySetCanceled(cancellationToken);
        }

        /// <summary>持闸凭据；重复释放被忽略。</summary>
        public sealed class Lease : IDisposable
        {
            private PriorityRunGate? _gate;

            public Lease(PriorityRunGate gate) => _gate = gate;

            public void Release() => Interlocked.Exchange(ref _gate, null)?.Release();

            public void Dispose() => Release();
        }

        private sealed class Waiter(bool background)
        {
            public bool Background { get; } = background;

            public TaskCompletionSource<bool> Completion { get; } =
                new(TaskCreationOptions.RunContinuationsAsynchronously);

            public LinkedListNode<Waiter>? Node { get; set; }

            public bool Granted { get; set; }
        }
    }
}

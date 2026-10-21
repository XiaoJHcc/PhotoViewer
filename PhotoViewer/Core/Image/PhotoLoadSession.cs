using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using PhotoViewer.Core.Color;
using PhotoViewer.Core.Design;
using PhotoViewer.Core.Memory;
using PhotoViewer.Core.Platform;
using PhotoViewer.Core.Storage;

namespace PhotoViewer.Core.Image;

/// <summary>原生展示加载会话；只管理代际、代理/Full、增强态与租约，不保存任何 UI 缩放/平移状态。</summary>
public sealed class PhotoLoadSession : IDisposable, IMemoryClient, IDisplayLoadSignal
{
    private IFileRef? _file;
    private DecodedImage? _image;
    private DecodedImage? _enhanced;
    private CancellationTokenSource _cancellation = new();
    private int _generation;
    private int _proxyLongEdge;
    private bool _needsFull;
    private bool _fullLoading;
    private bool _fullFailed;
    private bool _enhanceRequested;
    private int _enhanceToken;
    private int _enhanceVisibleToken;
    private bool _disposed;

    // 最近两份"源展示产物实例 → 增强图":A/B 来回切或代理↔Full 同实例回来时直接复用,不重算也不闪"计算中"。
    // 源只持 WeakReference(不延长源像素租约),增强图归会话持有;正在上屏的那份永远留在缓存里。
    private const int EnhancedCacheCapacity = 2;
    private readonly List<EnhancedEntry> _enhancedCache = new();
    // 缓存读写都在 UI 线程,但内存管家探测线程会读 FootprintBytes,故用一把短锁保护列表
    private readonly object _enhancedCacheGate = new();

    private sealed class EnhancedEntry
    {
        public required WeakReference<DecodedImage> Source { get; init; }
        public required DecodedImage Enhanced { get; init; }
    }

    /// <summary>增强预览开关是否处于开启状态（与增强图是否已就绪无关）。</summary>
    public bool IsEnhanced => _enhanceRequested;
    /// <summary>增强图是否正在计算（已开启但尚未上屏）；宿主可据此显示"计算中"反馈，失败/取消后归 false。</summary>
    public bool IsEnhancing { get; private set; }
    /// <summary>
    /// 增强计算是否已持续超过 <see cref="DesignTokens.ImageBusyDelay"/>；宿主据此显示 IMAGE-07 的"计算中"药丸，
    /// 不足门槛或计算结束立即归 false（<see cref="IsEnhancing"/> 语义不变）。
    /// </summary>
    public bool IsEnhancingVisible { get; private set; }
    /// <summary>当前展示图是否已是增强图。</summary>
    public bool EnhancedDisplay => _enhanced != null;
    /// <summary>当前会话持有的展示图像（增强开启且就绪时为增强图），仅 UI 线程借用，外部不能归还其租约。</summary>
    public DecodedImage? Image => _enhanced ?? _image;
    /// <summary>当前产物是否代理。</summary>
    public bool IsProxy { get; private set; }
    /// <summary>压测口径的代理标记（与本会话的 <see cref="IsProxy"/> 同源）。</summary>
    bool IDisplayLoadSignal.CurrentIsProxy => IsProxy;
    /// <summary>压测口径的编码（当前展示产物，无图时为 null）。</summary>
    PixelEncoding? IDisplayLoadSignal.CurrentEncoding => Image?.Encoding;
    /// <summary>图像、代理状态、加载/增强可见态或错误变化后在 UI 线程触发。</summary>
    public event Action? Changed;
    /// <summary>需要以 STATE-03 提示条呈现的一次性非阻塞提示（如内存压力自动关闭增强），在 UI 线程触发。</summary>
    public event Action<string>? Notice;
    /// <summary>屏上无可用图时的解码错误，无错误时为空；屏上已有代理时的 Full 升级失败记录在 <see cref="FullError"/>。</summary>
    public string Error { get; private set; } = string.Empty;
    /// <summary>Full 升级失败的非阻塞提示；屏上代理保持显示，等待下一次触发重试，成功上屏或换文件后清空。</summary>
    public string FullError { get; private set; } = string.Empty;
    /// <summary>
    /// 切图后超过 <see cref="DesignTokens.StatusLoadingDelay"/> 仍无展示图且无错误时为 true；
    /// 图一到、出错或换文件立即归 false。宿主据此显示 STATE-01 的加载提示。
    /// </summary>
    public bool IsLoadingVisible { get; private set; }

    /// <summary>订阅"本会话已把某文件上屏"（压测驱动器计时用），退订句柄可重复释放。</summary>
    /// <param name="handler">上屏完成回调，参数为该次上屏的文件引用。</param>
    /// <returns>退订句柄。</returns>
    public IDisposable Subscribe(Action<IFileRef> handler)
    {
        void OnChanged()
        {
            if (_file is { } file && _image != null) handler(file);
        }

        Changed += OnChanged;
        return new DelegateSubscription(() => Changed -= OnChanged);
    }

    /// <summary>构造并登记为内存客户端：压力到 Warning 时自动关闭增强并释放增强图。</summary>
    public PhotoLoadSession() => MemoryGovernor.Register(this);

    /// <summary>设置新文件，清空旧图并作废上一轮加载；所有权留在会话。</summary>
    public void SetFile(IFileRef? file)
    {
        _generation++;
        _cancellation.Cancel();
        _cancellation.Dispose();
        _cancellation = new CancellationTokenSource();
        _file = file;
        _fullLoading = false;
        Error = string.Empty;
        FullError = string.Empty;
        _fullFailed = false;
        Replace(null, false);
        if (file != null)
        {
            var cancellationToken = _cancellation.Token;
            int? target = !_needsFull && _proxyLongEdge > 0 ? _proxyLongEdge : null;
            _fullLoading = !target.HasValue;
            _ = LoadAsync(file, _generation, target, cancellationToken);
            // STATE-01：延迟暴露加载态，快速翻页在门槛内完成时不显示提示。
            _ = ShowLoadingAfterDelayAsync(_generation, cancellationToken);
        }
    }

    /// <summary>
    /// 切换增强预览：开启后对当前展示图后台算增强版本并原地替换，关闭则还原原片；模式跨切图持续生效。
    /// 与旧壳增强开关语义一致，只作用于当前解码产物（代理或 Full）。
    /// </summary>
    public void ToggleEnhanced()
    {
        if (_disposed) return;
        _enhanceRequested = !_enhanceRequested;
        int token = ++_enhanceToken;
        if (!_enhanceRequested)
        {
            SetEnhancing(false);
            ReleaseEnhanced();
            Changed?.Invoke();
            return;
        }
        if (_image != null)
        {
            var reused = TryReuseEnhancement(_image);
            if (reused != null)
            {
                var previous = _enhanced;
                _enhanced = reused;
                SetEnhancing(false);
                try { Changed?.Invoke(); }
                finally { if (previous != null && !ReferenceEquals(previous, reused) && !IsEnhancedCached(previous)) previous.Dispose(); }
                return;
            }
            SetEnhancing(true);
            Changed?.Invoke();
            _ = ApplyEnhancementAsync(_image, token);
        }
        else Changed?.Invoke();
    }

    /// <summary>后台算增强图；竞态闸或开关已关闭时丢弃结果。</summary>
    private async Task ApplyEnhancementAsync(DecodedImage source, int token)
    {
        if (!source.TryAcquire())
        {
            await FinishEnhancementAsync(token).ConfigureAwait(false);
            return;
        }
        DecodedImage? enhanced = null;
        try
        {
            enhanced = await Task.Run(() => EnhanceForDisplay(source)).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"[PhotoLoadSession] 增强失败: {exception.Message}");
        }
        finally
        {
            source.Release();
        }
        if (enhanced == null)
        {
            await FinishEnhancementAsync(token).ConfigureAwait(false);
            return;
        }
        var result = enhanced;
        await UiDispatcher.InvokeAsync(() =>
        {
            if (_disposed || token != _enhanceToken || !_enhanceRequested)
            {
                result.Dispose();
                return;
            }
            var previous = _enhanced;
            _enhanced = RememberEnhancement(source, result);
            SetEnhancing(false);
            try { Changed?.Invoke(); }
            finally
            {
                if (previous != null && !ReferenceEquals(previous, _enhanced) && !IsEnhancedCached(previous))
                    previous.Dispose();
            }
        }).ConfigureAwait(false);
    }

    /// <summary>取回同一源产物实例的增强图;命中即提到最近位(源用引用相等判定,源被回收后视为未命中)。</summary>
    private DecodedImage? TryReuseEnhancement(DecodedImage source)
    {
        lock (_enhancedCacheGate)
        {
            for (int i = 0; i < _enhancedCache.Count; i++)
            {
                var entry = _enhancedCache[i];
                if (!entry.Source.TryGetTarget(out var target) || !ReferenceEquals(target, source)) continue;
                if (i > 0)
                {
                    _enhancedCache.RemoveAt(i);
                    _enhancedCache.Insert(0, entry);
                }
                return entry.Enhanced;
            }
            return null;
        }
    }

    /// <summary>
    /// 记录"源 → 增强图"并保持最近 <see cref="EnhancedCacheCapacity"/> 份(同一源替换旧图)。
    /// 正在上屏的那份不参与淘汰,调用方释放缓存时须先摘掉 <see cref="_enhanced"/>。
    /// </summary>
    private DecodedImage RememberEnhancement(DecodedImage source, DecodedImage enhanced)
    {
        lock (_enhancedCacheGate)
        {
            for (int i = _enhancedCache.Count - 1; i >= 0; i--)
            {
                var entry = _enhancedCache[i];
                if (!entry.Source.TryGetTarget(out var target) || !ReferenceEquals(target, source)) continue;
                _enhancedCache.RemoveAt(i);
                if (!ReferenceEquals(entry.Enhanced, enhanced) && !ReferenceEquals(entry.Enhanced, _enhanced))
                    entry.Enhanced.Dispose();
            }
            _enhancedCache.Insert(0, new EnhancedEntry
            {
                Source = new WeakReference<DecodedImage>(source),
                Enhanced = enhanced,
            });
            for (int i = _enhancedCache.Count - 1; i >= 0 && _enhancedCache.Count > EnhancedCacheCapacity; i--)
            {
                if (ReferenceEquals(_enhancedCache[i].Enhanced, _enhanced)) continue;
                var evicted = _enhancedCache[i];
                _enhancedCache.RemoveAt(i);
                evicted.Enhanced.Dispose();
            }
            return enhanced;
        }
    }

    /// <summary>该增强图是否仍由会话缓存持有(持有者不重复释放)。</summary>
    private bool IsEnhancedCached(DecodedImage image)
    {
        lock (_enhancedCacheGate)
        {
            foreach (var entry in _enhancedCache)
                if (ReferenceEquals(entry.Enhanced, image)) return true;
            return false;
        }
    }

    /// <summary>增强任务未产出结果（异常/源已失效）时清除"计算中"状态并通知宿主，不静默停留。</summary>
    /// <param name="token">发起时的竞态令牌；已被后续请求取代时由新任务负责收尾。</param>
    private Task FinishEnhancementAsync(int token)
        => UiDispatcher.InvokeAsync(() =>
        {
            if (_disposed || token != _enhanceToken || !_enhanceRequested || !IsEnhancing) return;
            SetEnhancing(false);
            Changed?.Invoke();
        });

    /// <summary>更新增强计算态；变 false 立即收起可见态，变 true 后延迟 <see cref="DesignTokens.ImageBusyDelay"/> 才暴露。</summary>
    private void SetEnhancing(bool value)
    {
        if (IsEnhancing == value) return;
        IsEnhancing = value;
        int token = ++_enhanceVisibleToken;
        if (!value)
        {
            IsEnhancingVisible = false;
            return;
        }
        _ = ShowEnhancingAfterDelayAsync(token);
    }

    /// <summary>IMAGE-07：计算持续超过门槛才置可见，代际或状态已变时丢弃。</summary>
    private async Task ShowEnhancingAfterDelayAsync(int token)
    {
        await Task.Delay(DesignTokens.ImageBusyDelay).ConfigureAwait(false);
        await UiDispatcher.InvokeAsync(() =>
        {
            if (_disposed || token != _enhanceVisibleToken || !IsEnhancing || IsEnhancingVisible) return;
            IsEnhancingVisible = true;
            Changed?.Invoke();
        }).ConfigureAwait(false);
    }

    /// <summary>STATE-01：切图后延迟暴露加载态，旧代际或图已上屏/出错时丢弃。</summary>
    private async Task ShowLoadingAfterDelayAsync(int generation, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(DesignTokens.StatusLoadingDelay, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        await UiDispatcher.InvokeAsync(() =>
        {
            if (_disposed || generation != _generation || _file == null || _image != null
                || !string.IsNullOrEmpty(Error) || IsLoadingVisible) return;
            IsLoadingVisible = true;
            Changed?.Invoke();
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// 增强算法只在 8 位 sRGB 口径上有定义（256 级直方图 + LUT），因此先转 sRGB8 副本、就地均衡，再回到源展示编码：
    /// 8 位展示档（iPad Full / 压力降级）回到 8 位 Display P3，不扩大成 F16（24MP 可省约 96MB 与一次整图转换）；
    /// F16 线性表面（Mac 全档、iPad 代理）仍回 F16 线性 scRGB。算法输出语义与编码口径都不变，只是不再多拷一份整图。
    /// </summary>
    /// <param name="source">当前展示档的解码产物（会话持有，本方法只读）。</param>
    /// <returns>增强图（调用方负责释放）。</returns>
    private static DecodedImage EnhanceForDisplay(DecodedImage source)
    {
        using var srgb = ColorEngine.ToSrgb8(source);
        ImageEnhancer.EnhanceInPlace(srgb);
        return source.Encoding == PixelEncoding.Rgba8
            ? ColorEngine.ToDisplayP3(srgb)
            : ColorEngine.ToScRgb16(srgb);
    }

    /// <summary>释放增强图与"源 → 增强图"缓存，并作废在途增强任务（关闭增强时缓存一并清空）。</summary>
    private void ReleaseEnhanced()
    {
        ++_enhanceToken;
        var previous = _enhanced;
        _enhanced = null;
        lock (_enhancedCacheGate)
        {
            foreach (var entry in _enhancedCache)
            {
                if (ReferenceEquals(entry.Enhanced, previous)) continue;
                entry.Enhanced.Dispose();
            }
            _enhancedCache.Clear();
        }
        previous?.Dispose();
    }

    /// <summary>原生视口以物理像素上报尺寸与细节需求，2× 超采样及 Full 升级沿用画质红线。</summary>
    public void UpdateViewport(double widthPixels, double heightPixels, bool needsFull)
    {
        // 视口尚未成形（宿主未布局/尺寸为 0）时不得改写代理目标：否则目标会被钳成 1 像素，
        // 解码出一个 1×1 的"合格代理"并因已非代理前置条件而永不重载。
        if (widthPixels < 1 || heightPixels < 1) return;
        int target = Math.Max(1, checked((int)Math.Ceiling(Math.Max(widthPixels, heightPixels) * 2)));
        bool grow = target > _proxyLongEdge;
        bool wasNeedsFull = _needsFull;
        _proxyLongEdge = target;
        BitmapLoader.ProxyTargetLongEdge = target;
        _needsFull = needsFull;
        // 预取档位跟随实际需求:适应态只预取代理,放大/分析栏可见时才让 ±1 邻居解 Full
        BitmapLoader.PrefetchFullWanted = needsFull;
        if (_file == null || !string.IsNullOrEmpty(Error)) return;
        // IMAGE-03：Full 失败后等待下一次触发（需求上升沿或代理目标增长）再重试，避免每次布局刷新都重试。
        if (_fullFailed && needsFull && (!wasNeedsFull || grow)) _fullFailed = false;
        if (needsFull && IsProxy && !_fullLoading && !_fullFailed)
        {
            _fullLoading = true;
            _ = LoadAsync(_file, _generation, null, _cancellation.Token);
        }
        else if (grow && IsProxy && !_fullLoading)
            _ = LoadAsync(_file, _generation, target, _cancellation.Token);
    }

    /// <summary>按统一缓存/准入取得图像，检查代际后在 UI 线程交换；失效结果只归还租约。</summary>
    private async Task LoadAsync(IFileRef file, int generation, int? target, CancellationToken cancellationToken)
    {
        DecodedImage? result = null;
        try
        {
            using (DecodeScheduler.TrackP0())
                result = target.HasValue
                    ? await BitmapLoader.GetBitmapAsync(file, DecodeSizeHint.ProxyTo(target.Value), DecodePriority.P0_Current, cancellationToken).ConfigureAwait(false)
                    : await BitmapLoader.GetBitmapAsync(file, DecodePriority.P0_Current, cancellationToken).ConfigureAwait(false);
            var image = result;
            result = null;
            await UiDispatcher.InvokeAsync(() =>
            {
                if (generation != _generation || cancellationToken.IsCancellationRequested)
                {
                    image?.Release();
                    return;
                }
                if (!target.HasValue) _fullLoading = false;
                if (target.HasValue && _image != null && (!IsProxy || image != null && Math.Max(image.Width, image.Height) <= Math.Max(_image.Width, _image.Height)))
                {
                    image?.Release();
                    return;
                }
                if (image == null)
                {
                    // 屏上已有可用图时不报"无法打开该图片"；仅 Full 升级失败给出非阻塞提示，代理刷新失败保持静默。
                    if (_image == null) Error = "无法打开该图片";
                    else if (!target.HasValue) { FullError = StatusMessages.FullLoadFailed(file.Name); _fullFailed = true; }
                    IsLoadingVisible = false;
                    Changed?.Invoke();
                    return;
                }
                if (!target.HasValue) FullError = string.Empty;
                bool proxy = target.HasValue && image != null && (image.Width < image.SourceWidth || image.Height < image.SourceHeight);
                Replace(image, proxy);
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 本代际仍是当前请求却被取消(并入的在途任务被前一次请求的令牌取消):必须复位加载状态并给出可见原因，
            // 否则会停在"没有图且不会再加载"的黑屏(旧行为:静默吞掉、_fullLoading 永远为 true)。
            if (generation == _generation && !cancellationToken.IsCancellationRequested)
            {
                await UiDispatcher.InvokeAsync(() =>
                {
                    _fullLoading = false;
                    // IMAGE-03/IMAGE-10：屏上已有代理时升级失败只报非阻塞的 Full 提示，保留代理。
                    if (_image == null) Error = "解码被中断，请重新选择照片";
                    else if (!target.HasValue) { FullError = StatusMessages.FullLoadFailed(file.Name); _fullFailed = true; }
                    IsLoadingVisible = false;
                    Changed?.Invoke();
                }).ConfigureAwait(false);
            }
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"[PhotoLoadSession] 加载失败: {exception}");
            await UiDispatcher.InvokeAsync(() =>
            {
                if (generation != _generation) return;
                _fullLoading = false;
                if (_image == null) Error = exception.Message;
                else if (!target.HasValue) { FullError = StatusMessages.FullLoadFailed(file.Name); _fullFailed = true; }
                IsLoadingVisible = false;
                Changed?.Invoke();
            }).ConfigureAwait(false);
        }
        finally { result?.Release(); }
    }

    /// <summary>
    /// 交换会话持有者租约并作废旧增强图；通知后归还旧图，保证订阅者同步上传期间源缓冲有效。
    /// 增强开着时先查"源 → 增强图"缓存：同实例回切直接复用，不重算也不闪"计算中"。
    /// </summary>
    private void Replace(DecodedImage? image, bool proxy)
    {
        int token = ++_enhanceToken;
        var previousEnhanced = _enhanced;
        _enhanced = null;
        DecodedImage? reused = _enhanceRequested && image != null ? TryReuseEnhancement(image) : null;
        _enhanced = reused;
        // 增强开着时新展示档还要再算一次（缓存未命中时）：保持"计算中"直到新增强图就绪，避免反馈闪烁。
        bool willEnhance = _enhanceRequested && image != null && reused == null;
        SetEnhancing(willEnhance);
        var previous = _image;
        _image = image;
        IsProxy = proxy;
        // STATE-01：图一到（或换图清空）即撤下加载提示。
        IsLoadingVisible = false;
        try { Changed?.Invoke(); }
        finally
        {
            previous?.Release();
            // 上一份增强图留在会话缓存里供 A/B 回切复用（缓存自有生命周期），只有未入缓存才在这里释放。
            if (previousEnhanced != null && !ReferenceEquals(previousEnhanced, reused) && !IsEnhancedCached(previousEnhanced))
                previousEnhanced.Dispose();
        }
        if (willEnhance) _ = ApplyEnhancementAsync(image!, token);
    }

    /// <summary>关闭会话并取消在途任务，归还当前图像与增强图。</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        MemoryGovernor.Unregister(this);
        _generation++;
        _cancellation.Cancel();
        _cancellation.Dispose();
        var displayedEnhanced = _enhanced;
        _enhanced = null;
        lock (_enhancedCacheGate)
        {
            foreach (var entry in _enhancedCache)
                if (!ReferenceEquals(entry.Enhanced, displayedEnhanced)) entry.Enhanced.Dispose();
            _enhancedCache.Clear();
        }
        displayedEnhanced?.Dispose();
        ++_enhanceVisibleToken;
        IsEnhancing = false;
        IsEnhancingVisible = false;
        IsLoadingVisible = false;
        _image?.Release();
        _image = null;
    }

    // ---- 统一内存管控：增强图登记为内存客户端 ----

    /// <summary>内存客户端名。</summary>
    public string Name => "EnhancedPreview.Native";

    /// <summary>增强图占用(含"源 → 增强图"缓存里未上屏的那份)的池化字节。</summary>
    public long FootprintBytes
    {
        get
        {
            var displayed = _enhanced;
            long total = displayed?.ByteCount ?? 0;
            lock (_enhancedCacheGate)
            {
                foreach (var entry in _enhancedCache)
                    if (!ReferenceEquals(entry.Enhanced, displayed)) total += entry.Enhanced.ByteCount;
            }
            return total;
        }
    }

    /// <summary>
    /// Warning 起在 UI 线程关闭增强并释放增强图与对比缓存（等同用户手动关 toggle 的还原路径）。
    /// 关闭本身也会清空"源 → 增强图"缓存；只有此前确实开着增强才发一次性提示。
    /// </summary>
    public void Trim(PressureLevel level)
    {
        if (level < PressureLevel.Warning) return;
        if (_enhanced == null && !HasCachedEnhancement()) return;
        UiDispatcher.Post(() =>
        {
            bool wasEnhanced = _enhanceRequested;
            _enhanceRequested = false;
            SetEnhancing(false);
            ReleaseEnhanced();
            if (!wasEnhanced) return;
            Changed?.Invoke();
            // SCOPE-02/STATE-03：自动关闭是一次性事件，由各壳显示提示条；每轮关闭只发一次。
            Notice?.Invoke(StatusMessages.EnhanceClosedForMemory);
        });
    }

    /// <summary>对比缓存里是否还有未上屏的增强图。</summary>
    private bool HasCachedEnhancement()
    {
        lock (_enhancedCacheGate)
            return _enhancedCache.Count > 0;
    }
}


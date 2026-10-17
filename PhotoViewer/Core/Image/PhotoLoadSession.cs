using System;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using PhotoViewer.Core.Color;
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
    private bool _enhanceRequested;
    private int _enhanceToken;
    private bool _disposed;

    /// <summary>增强预览开关是否处于开启状态（与增强图是否已就绪无关）。</summary>
    public bool IsEnhanced => _enhanceRequested;
    /// <summary>增强图是否正在计算（已开启但尚未上屏）；宿主可据此显示"计算中"反馈，失败/取消后归 false。</summary>
    public bool IsEnhancing { get; private set; }
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
    /// <summary>图像、代理状态、增强态或错误变化后在 UI 线程触发。</summary>
    public event Action? Changed;
    /// <summary>当前解码错误，无错误时为空。</summary>
    public string Error { get; private set; } = string.Empty;

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
        Replace(null, false);
        if (file != null)
        {
            int? target = !_needsFull && _proxyLongEdge > 0 ? _proxyLongEdge : null;
            _fullLoading = !target.HasValue;
            _ = LoadAsync(file, _generation, target, _cancellation.Token);
        }
    }

    /// <summary>
    /// 切换增强预览：开启后对当前展示图后台算增强版本并原地替换，关闭则还原原片；模式跨切图持续生效。
    /// 与旧壳 <c>ImageViewModel.ToggleEnhanceAsync</c> 语义一致，只作用于当前解码产物（代理或 Full）。
    /// </summary>
    public void ToggleEnhanced()
    {
        if (_disposed) return;
        _enhanceRequested = !_enhanceRequested;
        int token = ++_enhanceToken;
        if (!_enhanceRequested)
        {
            IsEnhancing = false;
            ReleaseEnhanced();
            Changed?.Invoke();
            return;
        }
        if (_image != null)
        {
            IsEnhancing = true;
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
            _enhanced = result;
            IsEnhancing = false;
            try { Changed?.Invoke(); }
            finally { previous?.Dispose(); }
        }).ConfigureAwait(false);
    }

    /// <summary>增强任务未产出结果（异常/源已失效）时清除"计算中"状态并通知宿主，不静默停留。</summary>
    /// <param name="token">发起时的竞态令牌；已被后续请求取代时由新任务负责收尾。</param>
    private Task FinishEnhancementAsync(int token)
        => UiDispatcher.InvokeAsync(() =>
        {
            if (_disposed || token != _enhanceToken || !_enhanceRequested || !IsEnhancing) return;
            IsEnhancing = false;
            Changed?.Invoke();
        });

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

    /// <summary>释放增强图并作废在途增强任务。</summary>
    private void ReleaseEnhanced()
    {
        ++_enhanceToken;
        var previous = _enhanced;
        _enhanced = null;
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
        _proxyLongEdge = target;
        BitmapLoader.ProxyTargetLongEdge = target;
        _needsFull = needsFull;
        if (_file == null || !string.IsNullOrEmpty(Error)) return;
        if (needsFull && IsProxy && !_fullLoading)
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
                    Error = "无法打开该图片";
                    Changed?.Invoke();
                    return;
                }
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
                    Error = "解码被中断，请重新选择照片";
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
                Error = exception.Message;
                Changed?.Invoke();
            }).ConfigureAwait(false);
        }
        finally { result?.Release(); }
    }

    /// <summary>交换会话持有者租约并作废旧增强图；通知后归还旧图，保证订阅者同步上传期间源缓冲有效。</summary>
    private void Replace(DecodedImage? image, bool proxy)
    {
        int token = ++_enhanceToken;
        var previousEnhanced = _enhanced;
        _enhanced = null;
        // 增强开着时新展示档还要再算一次：保持"计算中"直到新增强图就绪，避免反馈闪烁。
        bool willEnhance = _enhanceRequested && image != null;
        IsEnhancing = willEnhance;
        var previous = _image;
        _image = image;
        IsProxy = proxy;
        try { Changed?.Invoke(); }
        finally
        {
            previous?.Release();
            previousEnhanced?.Dispose();
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
        _enhanced?.Dispose();
        _enhanced = null;
        IsEnhancing = false;
        _image?.Release();
        _image = null;
    }

    // ---- 统一内存管控：增强图登记为内存客户端 ----

    /// <summary>内存客户端名。</summary>
    public string Name => "EnhancedPreview.Native";

    /// <summary>增强图占用的池化字节。</summary>
    public long FootprintBytes => _enhanced?.ByteCount ?? 0;

    /// <summary>Warning 起在 UI 线程关闭增强并释放增强图（等同用户手动关 toggle 的还原路径）。</summary>
    public void Trim(PressureLevel level)
    {
        if (level < PressureLevel.Warning || _enhanced == null) return;
        UiDispatcher.Post(() =>
        {
            if (!_enhanceRequested) return;
            _enhanceRequested = false;
            ReleaseEnhanced();
            Changed?.Invoke();
        });
    }
}


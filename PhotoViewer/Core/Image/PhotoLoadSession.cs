using System;
using System.Threading;
using System.Threading.Tasks;
using PhotoViewer.Core.Platform;
using PhotoViewer.Core.Storage;

namespace PhotoViewer.Core.Image;

/// <summary>原生展示加载会话；只管理代际、代理/Full 与租约，不保存任何 UI 缩放/平移状态。</summary>
public sealed class PhotoLoadSession : IDisposable
{
    private IFileRef? _file;
    private DecodedImage? _image;
    private CancellationTokenSource _cancellation = new();
    private int _generation;
    private int _proxyLongEdge;
    private bool _needsFull;
    private bool _fullLoading;

    /// <summary>当前会话持有的图像，仅 UI 线程借用，外部不能归还其租约。</summary>
    public DecodedImage? Image => _image;
    /// <summary>当前产物是否代理。</summary>
    public bool IsProxy { get; private set; }
    /// <summary>图像、代理状态或错误变化后在 UI 线程触发。</summary>
    public event Action? Changed;
    /// <summary>当前解码错误，无错误时为空。</summary>
    public string Error { get; private set; } = string.Empty;

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

    /// <summary>原生视口以物理像素上报尺寸与细节需求，2× 超采样及 Full 升级沿用画质红线。</summary>
    public void UpdateViewport(double widthPixels, double heightPixels, bool needsFull)
    {
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
                if (_needsFull && IsProxy) UpdateViewport(_proxyLongEdge / 2.0, 0, true);
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

    /// <summary>交换会话持有者租约，通知后归还旧图，保证订阅者同步上传期间源缓冲有效。</summary>
    private void Replace(DecodedImage? image, bool proxy)
    {
        var previous = _image;
        _image = image;
        IsProxy = proxy;
        try { Changed?.Invoke(); }
        finally { previous?.Release(); }
    }

    /// <summary>关闭会话并取消在途任务，归还当前图像。</summary>
    public void Dispose()
    {
        _generation++;
        _cancellation.Cancel();
        _cancellation.Dispose();
        _image?.Release();
        _image = null;
    }
}

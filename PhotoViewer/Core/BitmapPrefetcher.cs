using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Platform.Storage;
using PhotoViewer.ViewModels;
using PhotoViewer.ViewModels.File;

namespace PhotoViewer.Core;

/// <summary>
/// 位图后台预取协调器:
/// 1. 当前图前后预取(前 Forward / 后 Backward)
/// 2. 滚动停止后中心附近预取
/// 3. 统一串行,旧任务自动取消
/// </summary>
public class BitmapPrefetcher
{
    private readonly MainViewModel _main;
    private readonly ThumbnailListViewModel _list;
    private readonly SettingsViewModel _settings;

    private CancellationTokenSource? _currentAroundCts;
    private CancellationTokenSource? _currentVisibleCenterCts;

    private volatile bool _busy;

    /// <summary>
    /// 构造位图预取器,绑定到主视图模型与缩略图列表视图模型。
    /// </summary>
    /// <param name="main">主视图模型(读取当前图、设置)</param>
    /// <param name="list">主缩略图列表视图模型(提供 FilteredFiles 与忙碌状态)</param>
    public BitmapPrefetcher(MainViewModel main, ThumbnailListViewModel list)
    {
        _main = main;
        _list = list;
        _settings = main.Settings;
    }

    /// <summary>
    /// 当前图片变化后调用:取消旧任务,预取前后若干张。
    /// </summary>
    public void PrefetchAroundCurrent()
    {
        _currentAroundCts?.Cancel();
        _currentAroundCts = new CancellationTokenSource();
        var ct = _currentAroundCts.Token;

        Task.Run(async () =>
        {
            try
            {
                if (_main.CurrentFile == null) return;
                var files = _list.FilteredFiles;
                if (files.Count == 0) return;

                var idx = files.IndexOf(_main.CurrentFile);
                if (idx < 0) return;

                int backward = Math.Max(0, _settings.PreloadBackwardCount);
                int forward = Math.Max(0, _settings.PreloadForwardCount);

                var indices = new List<int>();

                for (int i = 1; i <= forward; i++)
                {
                    int p = idx + i;
                    if (p < files.Count) indices.Add(p);
                    else break;
                }
                for (int i = 1; i <= backward; i++)
                {
                    int p = idx - i;
                    if (p >= 0) indices.Add(p);
                    else break;
                }

                var ordered = indices
                    .Select(i => (i, dist: Math.Abs(i - idx)))
                    .OrderBy(t => t.dist)
                    .ThenBy(t => t.i)
                    .Select(t => files[t.i].File)
                    .ToList();

                await RunQueuedAsync(ordered, ct);
            }
            catch { /* 忽略 */ }
        }, ct);
    }

    /// <summary>
    /// 滚动停止后调用:预取可见区域中心附近图片。
    /// </summary>
    public void PrefetchVisibleCenter(int firstIndex, int lastIndex)
    {
        _currentVisibleCenterCts?.Cancel();
        _currentVisibleCenterCts = new CancellationTokenSource();
        var ct = _currentVisibleCenterCts.Token;

        Task.Run(async () =>
        {
            try
            {
                var files = _list.FilteredFiles;
                if (files.Count == 0) return;
                firstIndex = Math.Max(0, Math.Min(firstIndex, files.Count - 1));
                lastIndex = Math.Max(0, Math.Min(lastIndex, files.Count - 1));
                if (lastIndex < firstIndex) return;

                int center = (firstIndex + lastIndex) / 2;
                int need = Math.Max(1, _settings.VisibleCenterPreloadCount);

                var indices = new List<(int idx, int dist)>();
                for (int i = firstIndex; i <= lastIndex; i++)
                {
                    indices.Add((i, Math.Abs(i - center)));
                }

                var selected = indices
                    .OrderBy(t => t.dist)
                    .ThenBy(t => t.idx)
                    .Take(need)
                    .Select(t => files[t.idx].File)
                    .ToList();

                await RunQueuedAsync(selected, ct);
            }
            catch { /* 忽略 */ }
        }, ct);
    }

    /// <summary>
    /// 主图是否仍在加载中(用于让位高优先级解码)。
    /// </summary>
    private bool IsCurrentImageLoading()
    {
        var current = _main.CurrentFile;
        if (current == null) return false;
        var imgVM = _main.ImageVM;
        var path = current.File.Path.LocalPath;
        return imgVM.SourceBitmap == null && !BitmapLoader.IsInCache(path);
    }

    /// <summary>
    /// 让位等待:当前主图或缩略图通道仍在繁忙时退避。
    /// </summary>
    private async Task WaitForHighPriorityIdleAsync(CancellationToken ct)
    {
        int waited = 0;
        while (!ct.IsCancellationRequested)
        {
            if (!IsCurrentImageLoading() && !_list.IsThumbnailLoadingBusy())
                break;
            await Task.Delay(120, ct);
            waited += 120;
            if (waited > 5000) break;
        }
    }

    /// <summary>
    /// 实际预取执行:按并行度限制启动多个任务,逐个预留内存并解码。
    /// </summary>
    private async Task RunQueuedAsync(IEnumerable<IStorageFile> files, CancellationToken ct)
    {
        if (_busy) return;
        _busy = true;
        try
        {
            var fileList = files
                .Where(f => !BitmapLoader.IsInCache(f.Path.LocalPath))
                .ToList();
            if (fileList.Count == 0) return;

            var nativeParallel = Math.Max(1, _settings.NativePreloadParallelism);
            using var nativeSemaphore = new SemaphoreSlim(nativeParallel);
            var tasks = new List<Task>(fileList.Count);

            foreach (var f in fileList)
            {
                if (ct.IsCancellationRequested) break;

                await nativeSemaphore.WaitAsync(ct);
                await Task.Delay(15, ct);

                tasks.Add(Task.Run(async () =>
                {
                    IDisposable? reservation = null;
                    try
                    {
                        await WaitForHighPriorityIdleAsync(ct);
                        if (ct.IsCancellationRequested) return;

                        reservation = await BitmapLoader.ReserveForPreloadAsync(f, ct);
                        if (reservation == null) return;

                        if (!BitmapLoader.IsInCache(f.Path.LocalPath))
                        {
                            await BitmapLoader.PreloadBitmapAsync(f);
                        }

                        await Task.Delay(30, ct);
                    }
                    catch
                    {
                        // 单个失败忽略
                    }
                    finally
                    {
                        reservation?.Dispose();
                        nativeSemaphore.Release();
                    }
                }, ct));
            }

            try { await Task.WhenAll(tasks); }
            catch (OperationCanceledException) { /* 忽略取消 */ }
        }
        finally
        {
            _busy = false;
        }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PhotoViewer.Core.AI;
using PhotoViewer.ViewModels.Main;
using PhotoViewer.ViewModels.Main.File;
using PhotoViewer.ViewModels.Settings;

namespace PhotoViewer.Core.Image;

/// <summary>
/// 位图后台预取协调器:
/// 1. 当前图前后预取(前 Forward / 后 Backward)
/// 2. 滚动停止后中心附近预取
/// 3. 统一串行,旧任务自动取消
///
/// 预取邻居位图后,若分析栏可见,顺手为该邻居预热 <see cref="AnalysisResultCache"/>(读 DB + 派生层现算)。
/// PCA SVD 是切图卡顿的主因 — 邻居预热后切图变成纯 UI 线程 swap。
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
                    .Select(t => files[t.i])
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
                    .Select(t => files[t.idx])
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
    /// 实际预取执行:按并行度限制启动多个任务,逐个预留内存并解码。每个任务完成后顺手预热分析栏缓存。
    /// </summary>
    private async Task RunQueuedAsync(IEnumerable<ImageFile> files, CancellationToken ct)
    {
        if (_busy) return;
        _busy = true;
        try
        {
            var fileList = files.ToList();
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

                        var path = f.File.Path.LocalPath;
                        if (!BitmapLoader.IsInCache(path))
                        {
                            reservation = await BitmapLoader.ReserveForPreloadAsync(f.File, ct);
                            if (reservation == null)
                            {
                                // 预热分析栏缓存仍然有意义(纯 DB IO + CPU,不吃位图内存预算)。
                                await PrewarmAnalysisAsync(f, ct).ConfigureAwait(false);
                                return;
                            }

                            if (!BitmapLoader.IsInCache(path))
                            {
                                await BitmapLoader.PreloadBitmapAsync(f.File);
                            }
                        }

                        await PrewarmAnalysisAsync(f, ct).ConfigureAwait(false);
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

    /// <summary>
    /// 为单张邻居预热分析栏缓存:仅在分析栏可见时执行(避免无谓的 PCA SVD)。
    /// 计算指纹 → 命中即返回 → miss 则读库 + 派生层现算 + 落 cache。任一阶段失败静默跳过。
    /// </summary>
    private async Task PrewarmAnalysisAsync(ImageFile file, CancellationToken ct)
    {
        if (!_main.IsAnalysisViewVisible) return;
        if (ct.IsCancellationRequested) return;

        try
        {
            var fingerprint = await AnalysisDataReader.ComputeFingerprintAsync(file, ct).ConfigureAwait(false);
            if (string.IsNullOrEmpty(fingerprint)) return;
            if (AnalysisResultCache.TryGet(fingerprint) != null) return;

            var data = await AnalysisDataReader.ReadByFingerprintAsync(fingerprint, ct).ConfigureAwait(false);
            if (ct.IsCancellationRequested) return;

            var entry = AnalysisComputer.Compute(data);
            AnalysisResultCache.Put(fingerprint, entry);
        }
        catch (OperationCanceledException) { /* 取消正常 */ }
        catch (Exception ex)
        {
            Console.WriteLine($"[BitmapPrefetcher] analysis prewarm failed for {file.Name}: {ex.Message}");
        }
    }
}

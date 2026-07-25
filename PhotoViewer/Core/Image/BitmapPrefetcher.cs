using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PhotoViewer.Core.AI;
using PhotoViewer.ViewModels.Main;
using PhotoViewer.ViewModels.Main.File;
using PhotoViewer.ViewModels.Settings;
using ReactiveUI;

namespace PhotoViewer.Core.Image;

/// <summary>
/// 位图后台预取协调器:
/// 1. 当前图前后预取(前 Forward / 后 Backward)
/// 2. 滚动停止后中心附近预取
/// 3. latest-wins:新请求取消旧任务后重新排队,不再整体丢弃
///
/// 每张邻居位图的"预留内存 + 解码 + 分析栏预热"作为 P2 工作项投递给 <see cref="DecodeScheduler"/>,
/// 并发度与 P0 让位由调度器统一负责(本类构造时把 Settings.NativePreloadParallelism 同步给调度器)。
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

        // 调度器静态化后拿不到 settings 实例,这里一次性挂钩同步并发度上限
        DecodeScheduler.MaxBackgroundParallelism = Math.Max(1, _settings.NativePreloadParallelism);
        _settings.WhenAnyValue(s => s.NativePreloadParallelism)
            .Subscribe(v => DecodeScheduler.MaxBackgroundParallelism = Math.Max(1, v));
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
    /// 让位等待:P0(主图解码)在途或 P1(可见缩略图)队列非空时退避。
    /// </summary>
    private async Task WaitForHighPriorityIdleAsync(CancellationToken ct)
    {
        int waited = 0;
        while (!ct.IsCancellationRequested)
        {
            if (DecodeScheduler.P0InFlightCount == 0 &&
                DecodeScheduler.GetQueuedCount(DecodePriority.P1_HighValue) == 0)
                break;
            await Task.Delay(120, ct);
            waited += 120;
            if (waited > 1500) break;
        }
    }

    /// <summary>
    /// 实际预取执行:每张邻居位图的"预留内存 + 解码 + 分析栏预热"合并为一个 P2 工作项投递给调度器,
    /// 并发度由调度器统一限制。latest-wins:新请求取消旧 CTS,旧轮次工作项执行时因 ct 已取消而空跑退出。
    /// </summary>
    private Task RunQueuedAsync(IEnumerable<ImageFile> files, CancellationToken ct)
    {
        foreach (var f in files)
        {
            if (ct.IsCancellationRequested) break;
            var file = f;
            DecodeScheduler.Submit(file.File.Path.LocalPath, DecodePriority.P2_Prefetch, async token =>
            {
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(token, ct);
                var lct = linkedCts.Token;

                await WaitForHighPriorityIdleAsync(lct);
                if (lct.IsCancellationRequested) return;

                IDisposable? reservation = null;
                try
                {
                    var path = file.File.Path.LocalPath;
                    if (!BitmapLoader.IsInCache(path))
                    {
                        reservation = await BitmapLoader.ReserveForPreloadAsync(file.File, lct);
                        if (reservation == null)
                        {
                            // 预热分析栏缓存仍然有意义(纯 DB IO + CPU,不吃位图内存预算)。
                            await PrewarmAnalysisAsync(file, lct).ConfigureAwait(false);
                            return;
                        }

                        if (!BitmapLoader.IsInCache(path))
                        {
                            await BitmapLoader.PreloadBitmapAsync(file.File, lct);
                        }
                    }

                    await PrewarmAnalysisAsync(file, lct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { /* 取消正常 */ }
                catch (Exception ex)
                {
                    Console.WriteLine($"[BitmapPrefetcher] preload failed for {file.Name}: {ex.Message}");
                }
                finally
                {
                    reservation?.Dispose();
                }
            });
        }

        return Task.CompletedTask;
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

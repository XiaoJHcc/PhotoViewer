using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Platform.Storage;
using PhotoViewer.ViewModels;

namespace PhotoViewer.Core;

/// <summary>
/// 位图后台预取协调器：
/// 1. 当前图前后预取（前 Forward / 后 Backward）
/// 2. 滚动停止后中心附近预取
/// 3. 统一串行，旧任务自动取消
/// </summary>
public class BackgroundBitmapPrefetcher
{
    private readonly FolderViewModel _folder;
    private readonly SettingsViewModel _settings;
    
    private CancellationTokenSource? _currentAroundCts;
    private CancellationTokenSource? _currentVisibleCenterCts;
    
    private volatile bool _busy;

    public BackgroundBitmapPrefetcher(FolderViewModel folder)
    {
        _folder = folder;
        _settings = folder.Main.Settings;
    }

    /// <summary>
    /// 当前图片变化后调用
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
                if (_folder.Main.CurrentFile == null) return;
                var files = _folder.FilteredFiles;
                if (files.Count == 0) return;

                var idx = files.IndexOf(_folder.Main.CurrentFile);
                if (idx < 0) return;

                int backward = Math.Max(0, _settings.PreloadBackwardCount);
                int forward = Math.Max(0, _settings.PreloadForwardCount);

                var indices = new List<int>();

                // 后 forward
                for (int i = 1; i <= forward; i++)
                {
                    int p = idx + i;
                    if (p < files.Count) indices.Add(p);
                    else break;
                }
                // 前 backward
                for (int i = 1; i <= backward; i++)
                {
                    int p = idx - i;
                    if (p >= 0) indices.Add(p);
                    else break;
                }

                // 去重 & 顺序：优先前后距离近
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
    /// 滚动停止后调用
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
                var files = _folder.FilteredFiles;
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

    private bool IsCurrentImageLoading()
    {
        var current = _folder.Main.CurrentFile;
        if (current == null) return false;
        // 主图尚未准备好（UI显示优先）
        var imgVM = _folder.Main.ImageVM;
        var path = current.File.Path.LocalPath;
        return imgVM.SourceBitmap == null && !BitmapLoader.IsInCache(path);
    }

    private async Task WaitForHighPriorityIdleAsync(CancellationToken ct)
    {
        int waited = 0;
        while (!ct.IsCancellationRequested)
        {
            if (!IsCurrentImageLoading() && !_folder.IsThumbnailLoadingBusy())
                break;
            await Task.Delay(120, ct);
            waited += 120;
            if (waited > 5000) break; // 最长等待 5 秒避免极端阻塞
        }
    }

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

            int parallel = Math.Max(1, _settings.PreloadParallelism);
            using var semaphore = new SemaphoreSlim(parallel);
            var tasks = new List<Task>(fileList.Count);

            foreach (var f in fileList)
            {
                if (ct.IsCancellationRequested) break;

                await semaphore.WaitAsync(ct);

                // 轻微错峰，避免瞬时大量 I/O
                await Task.Delay(15, ct);

                tasks.Add(Task.Run(async () =>
                {
                    try
                    {
                        // 每个任务都需让位于高优先级加载
                        await WaitForHighPriorityIdleAsync(ct);
                        if (ct.IsCancellationRequested) return;

                        // 二次检查，避免重复
                        if (!BitmapLoader.IsInCache(f.Path.LocalPath))
                        {
                            await BitmapLoader.PreloadBitmapAsync(f);
                        }

                        // 节流：给 UI / 解码线程留喘息
                        await Task.Delay(30, ct);
                    }
                    catch
                    {
                        // 单个失败忽略
                    }
                    finally
                    {
                        semaphore.Release();
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

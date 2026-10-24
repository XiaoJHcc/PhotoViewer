using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PhotoViewer.Core.AI;
using PhotoViewer.Core.Memory;
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
    private CancellationTokenSource? _fullWarmCts;
    private int _normalParallelism = 2; // 设置值镜像;Warning 起被 Governor 指令覆盖为 1

    /// <summary>当前图停留多久后预热 Full（放大/开分析栏时直接命中）。</summary>
    private const int FullWarmupDelayMs = 600;

    /// <summary>
    /// 翻页方向上以「可沿 P0 并行」通道预取的邻图张数：比 P0 更早/同时开跑，
    /// 连翻时下一张才能在翻到之前解完（一张只有零提前量）。
    /// </summary>
    private const int AlongsideDepth = 2;

    /// <summary>
    /// RAW 的并行预取深度：RAW 渲染是 GPU 串行瓶颈——两路并发时其中一路要 2 倍时间、总吞吐不涨
    /// （探针 decodeConcurrency：1 路 712ms；2 路 716ms + 1400ms），因此只保留 +1 一张，
    /// 既不占满「沿 P0 并行」通道，也避免三路 RAW 同时渲染把当前图拖慢。
    /// </summary>
    private const int RawAlongsideDepth = 1;

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
        _normalParallelism = Math.Max(1, _settings.NativePreloadParallelism);
        DecodeScheduler.MaxBackgroundParallelism = _normalParallelism;
        _settings.WhenAnyValue(s => s.NativePreloadParallelism)
            .Subscribe(v =>
            {
                _normalParallelism = Math.Max(1, v);
                // 压力下(Warning+)由 Governor 指令(1)接管,不覆盖
                if (MemoryGovernor.CurrentLevel < PressureLevel.Warning)
                    DecodeScheduler.MaxBackgroundParallelism = _normalParallelism;
            });

        // 压力响应:Caution 起停预取(取消在途轮次);Normal 且过恢复观察期后渐进恢复
        MemoryGovernor.LevelChanged += OnMemoryPressureChanged;

        // 当前图停留够久且适应态/分析栏不可见时,后台预热当前图 Full:放大或开分析栏时直接命中
        _main.PropertyChanged += (_, changed) =>
        {
            if (changed.PropertyName == nameof(MainViewModel.CurrentFile)) ScheduleCurrentFullWarmup();
        };
    }

    /// <summary>压力级别变化响应:升级取消在途预取;Warning 解码并发降 1;回 Normal 过观察期后恢复预取。</summary>
    private void OnMemoryPressureChanged(PressureLevel level)
    {
        if (level >= PressureLevel.Caution)
        {
            _currentAroundCts?.Cancel();
            _currentVisibleCenterCts?.Cancel();
        }

        DecodeScheduler.MaxBackgroundParallelism = level >= PressureLevel.Warning ? 1 : _normalParallelism;

        if (level == PressureLevel.Normal)
        {
            _ = Task.Run(async () =>
            {
                await Task.Delay(MemoryGovernor.RecoveryHold);
                if (MemoryGovernor.CurrentLevel == PressureLevel.Normal && !MemoryGovernor.InRecoveryHold)
                    PrefetchAroundCurrent();
            });
        }
    }

    /// <summary>
    /// 当前图片变化后调用:取消旧任务,按保留排名顺序预取前后若干张(翻页方向优先)。
    /// 预取档位跟随实际需求:适应态只取代理,放大/分析栏可见时 ±1 邻居取 Full。
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
                // 翻页方向:方向上的邻居先取(d 计 d),反方向按 2d 垫后
                int direction = _list.FlipDirection >= 0 ? 1 : -1;

                var ordered = new List<(int index, int distance, int cost)>();
                for (int i = 1; i <= forward; i++)
                {
                    int p = idx + i;
                    if (p >= files.Count) break;
                    ordered.Add((p, i, direction > 0 ? i : 2 * i));
                }
                for (int i = 1; i <= backward; i++)
                {
                    int p = idx - i;
                    if (p < 0) break;
                    ordered.Add((p, i, direction > 0 ? 2 * i : i));
                }

                var sorted = ordered
                    .OrderBy(t => t.cost)
                    .ThenBy(t => t.index)
                    .ToList();

                // 方向上的 +1/+2 邻居以"可沿 P0 并行"提交:连翻时 P0 几乎一直在途,普通预取永远排不上。
                // 两张起:预取的解码与当前图 P0 同时开始、耗时相当,一张只有"零提前量"(翻到它时才刚解完);
                // 两张才能让流水线真正跑在前面(翻第 N 张时第 N+1、N+2 张已在解)。
                // RAW 例外:Core Image 的 RAW 渲染是 GPU 串行瓶颈,两三路并行时每路反而慢到 2.5 倍,
                // 只保留一张(与 P0 并发两路)才划算。
                int aheadIndex = idx + direction;
                bool aheadExists = aheadIndex >= 0 && aheadIndex < files.Count;
                int aheadDepth = aheadExists && RawLoader.IsRawFile(files[aheadIndex].File) ? RawAlongsideDepth : AlongsideDepth;
                bool IsAhead(int index)
                {
                    int offset = (index - idx) * direction;
                    return offset >= 1 && offset <= aheadDepth;
                }

                // 分级预取:适应态只解代理;需要 Full 时 ±1 邻居取 Full(变焦连续性);代理目标未知时全部 Full(旧行为)
                int proxyTarget = BitmapLoader.ProxyTargetLongEdge;
                bool fullNeighbors = BitmapLoader.PrefetchFullWanted && BitmapLoader.PrefetchFullNeighbors;
                var items = sorted.Select(t => (
                    file: files[t.index],
                    full: proxyTarget <= 0 || (t.distance == 1 && fullNeighbors),
                    alongP0: aheadExists && IsAhead(t.index))).ToList();
                await RunQueuedAsync(items, ct).ConfigureAwait(false);
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
                    .Select(t => t.idx)
                    .ToList();

                // 可视区中心预取:代理档优先(目标未知时 Full 旧行为)
                int proxyTarget = BitmapLoader.ProxyTargetLongEdge;
                var items = selected.Select(i => (file: files[i], full: proxyTarget <= 0, alongP0: false)).ToList();
                await RunQueuedAsync(items, ct).ConfigureAwait(false);
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
    /// 实际预取执行:每张邻居一个 P2 工作项投递给调度器,并发度由调度器统一限制。
    /// latest-wins:新请求取消旧 CTS,旧轮次工作项执行时因 ct 已取消而空跑退出。
    /// full=false 走代理档(省内存),full=true 走全分辨率(±1 邻居变焦连续性)。
    /// alongP0=true 的项(翻页方向上的下一张)允许在 P0 在途时执行,且不等待高优先级空闲。
    /// </summary>
    private Task RunQueuedAsync(IEnumerable<(ImageFile file, bool full, bool alongP0)> items, CancellationToken ct)
    {
        foreach (var (f, wantFull, alongP0) in items)
        {
            if (ct.IsCancellationRequested) break;
            var file = f;
            var full = wantFull;
            var alongside = alongP0;
            DecodeScheduler.Submit(file.File.StorageId.Value, DecodePriority.P2_Prefetch, async token =>
            {
                // 压力闸:Caution/恢复观察期起预取不执行(投递即空跑退出)
                if (MemoryGovernor.CurrentLevel >= PressureLevel.Caution || MemoryGovernor.InRecoveryHold) return;

                using var linkedCts = alongside ? null : CancellationTokenSource.CreateLinkedTokenSource(token, ct);
                // 沿 P0 并行的项一旦开跑就让它跑完：它正是即将上屏的那一张，轮次切换只阻止尚未开始的工作。
                // 否则下一张的预取每次都在"快好了"时被丢弃（取消点只在解码/绘制之后），P0 再从零解一遍。
                var lct = alongside ? token : linkedCts!.Token;

                if (!alongside)
                {
                    await WaitForHighPriorityIdleAsync(lct);
                    if (lct.IsCancellationRequested) return;
                }
                else if (ct.IsCancellationRequested) return;

                try
                {
                    var path = file.File.StorageId.Value;
                    if (full)
                    {
                        if (!BitmapLoader.IsInCache(path))
                            await BitmapLoader.PreloadBitmapAsync(file.File, lct);
                    }
                    else if (BitmapLoader.TryGetCached(path, ImageSizeClass.Proxy) == null)
                    {
                        // 代理档预取(内部走 P2 准入,被拒即跳过)
                        var img = await BitmapLoader.GetBitmapAsync(file.File, DecodeSizeHint.ProxyTo(BitmapLoader.ProxyTargetLongEdge), DecodePriority.P2_Prefetch, lct);
                        img?.Release();
                    }

                    await PrewarmAnalysisAsync(file, lct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { /* 取消正常 */ }
                catch (Exception ex)
                {
                    Console.WriteLine($"[BitmapPrefetcher] preload failed for {file.Name}: {ex.Message}");
                }
            }, runAlongsideP0: alongside);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// 当前图 Full 预热:切图后停留 ≥600ms(期间再切图即作废)、适应态且分析栏不可见、压力 Normal 且不在恢复观察期时,
    /// 以 P2 解当前图 Full 入缓存(不上屏、不经展示会话),放大或打开分析栏时直接命中。
    /// 预取不挤更重要条目:当前图是排名 ①,它的 Full 可以淘汰任意更差的排名;腾不出位置则跳过。
    /// </summary>
    private void ScheduleCurrentFullWarmup()
    {
        _fullWarmCts?.Cancel();
        _fullWarmCts = new CancellationTokenSource();
        var ct = _fullWarmCts.Token;
        var file = _main.CurrentFile;
        if (file == null) return;

        var path = file.File.StorageId.Value;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(FullWarmupDelayMs, ct).ConfigureAwait(false);
                if (ct.IsCancellationRequested) return;
                // 需要 Full 时主链路自己会取,预热无意义(且会与 P0 抢同一份解码)
                if (BitmapLoader.PrefetchFullWanted || _main.IsAnalysisViewVisible) return;
                if (MemoryGovernor.CurrentLevel != PressureLevel.Normal || MemoryGovernor.InRecoveryHold) return;
                if (BitmapLoader.IsInCache(path, ImageSizeClass.Full)) return;

                DecodeScheduler.Submit(path, DecodePriority.P2_Prefetch, async token =>
                {
                    if (MemoryGovernor.CurrentLevel >= PressureLevel.Caution || MemoryGovernor.InRecoveryHold) return;
                    if (BitmapLoader.IsInCache(path, ImageSizeClass.Full)) return;
                    if (BitmapLoader.PrefetchFullWanted) return;
                    using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(token, ct);
                    var image = await BitmapLoader.GetBitmapAsync(file.File, DecodePriority.P2_Prefetch, linkedCts.Token).ConfigureAwait(false);
                    image?.Release();
                });
            }
            catch (OperationCanceledException) { /* 切图后本轮作废 */ }
            catch (Exception ex)
            {
                Console.WriteLine($"[BitmapPrefetcher] full warmup failed for {file.Name}: {ex.Message}");
            }
        }, ct);
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

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
/// 4. 应用活跃闸:宿主上报"应用进入后台"后不再投递也不执行任何后台预取,回前台重新计划邻居与当前图 Full 预热
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

    // ── 应用活跃状态 ────────────────────────────────────────────────────────────
    // 静态:活跃状态属于进程/应用,不属于某一轮预取。默认活跃,未上报的宿主行为完全不变。
    // 实例只以弱引用登记(不订阅静态事件),因此不会长出退出后仍在的强引用或订阅。
    private static readonly object ApplicationGate = new();
    private static readonly List<WeakReference<BitmapPrefetcher>> LiveInstances = new();
    private static CancellationTokenSource _applicationLifetime = new();
    private static bool _applicationActive = true;
    private static int _backgroundPauseCount;
    private static int _foregroundResumeCount;
    private static int _foregroundReplanCount;
    private static int _suspendedScheduleCount;

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
    /// RAW 提前窗口。默认 2 保留既有邻居规则；扩大时仅窗口内最近的一张 RAW 获得沿 P0 并行资格，
    /// 仍受对应方向的预载份数限制。
    /// </summary>
    public static int RawAlongsideLookahead { get; set; } = 2;

    /// <summary>
    /// 应用是否处于活跃状态。默认 true（桌面端与不上报生命周期的宿主行为不变）；
    /// false 时后台预取不投递新工作，已投递/在途的后台工作随 <see cref="ApplicationLifetimeToken"/> 一并取消。
    /// </summary>
    public static bool IsApplicationActive
    {
        get { lock (ApplicationGate) return _applicationActive; }
    }

    /// <summary>
    /// 当前活跃代际的取消令牌。一切后台预取工作（轮次、工作项、当前图 Full 预热）都必须链接它：
    /// 应用停止时一次性取消尚未开跑与在途的后台工作；恢复后换新代际，旧令牌保持已取消，迟到的旧轮次不会复活。
    /// 沿 P0 并行的项平常切图不取消，但同样链接它——应用停止仍然取消。
    /// </summary>
    public static CancellationToken ApplicationLifetimeToken
    {
        get { lock (ApplicationGate) return _applicationLifetime.Token; }
    }

    /// <summary>累计进入后台次数（宿主上报 false 的次数），验收观察用。</summary>
    public static int BackgroundPauseCount
    {
        get { lock (ApplicationGate) return _backgroundPauseCount; }
    }

    /// <summary>累计回到前台次数（宿主上报 true 的次数），验收观察用。</summary>
    public static int ForegroundResumeCount
    {
        get { lock (ApplicationGate) return _foregroundResumeCount; }
    }

    /// <summary>累计回前台时重新计划的实例次数（每个存活预取器重排邻居与当前图 Full 预热算一次），验收观察用。</summary>
    public static int ForegroundReplanCount
    {
        get { lock (ApplicationGate) return _foregroundReplanCount; }
    }

    /// <summary>累计被后台状态拦下的预取投递次数（轮次与工作项各计一次），验收观察"后台确实没排新工作"用。</summary>
    public static int SuspendedScheduleCount
    {
        get { lock (ApplicationGate) return _suspendedScheduleCount; }
    }

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

        // 应用活跃闸只按弱引用登记:回前台时按存活实例重新计划,不给实例增加静态订阅
        lock (ApplicationGate) LiveInstances.Add(new WeakReference<BitmapPrefetcher>(this));

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

    /// <summary>
    /// 上报应用活跃状态（宿主 Activity 的 OnStart/OnStop 或等价生命周期）。幂等：状态未变化时返回 false 且无副作用。
    /// false 取消一切后台预取工作并拒绝新的投递——走的是预取自己的活跃闸，<b>不借用内存压力通道</b>：
    /// 不改 <see cref="MemoryGovernor"/> 级别、不进入恢复观察期，回前台可立即重新预取。
    /// true 换一代令牌，并让每个存活实例重新计划邻居预取与当前图 Full 预热。
    /// </summary>
    /// <param name="active">true 回前台，false 进后台。</param>
    /// <returns>状态真的发生变化返回 true；重复上报同一状态返回 false。</returns>
    public static bool SetApplicationActive(bool active)
    {
        BitmapPrefetcher[] replan;
        CancellationTokenSource? suspended = null;
        lock (ApplicationGate)
        {
            if (_applicationActive == active) return false;
            _applicationActive = active;
            if (active)
            {
                _foregroundResumeCount++;
                // 换新代际:旧令牌保持已取消,仍在跑的旧轮次不会因为恢复而复活
                _applicationLifetime = new CancellationTokenSource();
                replan = CollectLiveInstancesLocked();
                _foregroundReplanCount += replan.Length;
            }
            else
            {
                _backgroundPauseCount++;
                replan = Array.Empty<BitmapPrefetcher>();
                suspended = _applicationLifetime;
            }
        }

        // 锁外执行:取消回调只落在后台工作自己的链接令牌与等待任务上,不在锁内回调外部代码
        if (suspended != null)
        {
            try
            {
                suspended.Cancel();
            }
            catch (AggregateException ex)
            {
                // 单个取消回调抛异常不能把宿主的上报入口(Android OnStop)带崩
                Console.WriteLine($"[BitmapPrefetcher] suspend callback failed: {ex.GetBaseException().Message}");
            }
        }

        foreach (var instance in replan)
        {
            try
            {
                instance.ReplanForForeground();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[BitmapPrefetcher] resume replan failed: {ex.Message}");
            }
        }

        return true;
    }

    /// <summary>取存活实例并顺手剔除已被回收的弱引用（不持强引用，退出/配置重建不留残留订阅）。</summary>
    /// <returns>当前存活的预取器快照。</returns>
    private static BitmapPrefetcher[] CollectLiveInstancesLocked()
    {
        var live = new List<BitmapPrefetcher>(LiveInstances.Count);
        for (int i = LiveInstances.Count - 1; i >= 0; i--)
        {
            if (LiveInstances[i].TryGetTarget(out var instance)) live.Add(instance);
            else LiveInstances.RemoveAt(i);
        }

        return live.ToArray();
    }

    /// <summary>
    /// 后台预取准入：应用不活跃时拦下投递并记账。后台状态只走这一处判断，不复用压力状态机
    /// （否则每次进后台都会裁缓存并让回前台多等 30s 恢复观察期）。
    /// </summary>
    /// <returns>允许投递返回 true。</returns>
    private static bool CanScheduleBackgroundWork()
    {
        lock (ApplicationGate)
        {
            if (_applicationActive) return true;
            _suspendedScheduleCount++;
            return false;
        }
    }

    /// <summary>
    /// 回前台重排：被取消的轮次不会自愈，这里重新计划当前图 Full 预热与邻居预取
    /// （两者内部各自投递线程池，不在调用线程上解码或等待）。
    /// </summary>
    private void ReplanForForeground()
    {
        ScheduleCurrentFullWarmup();
        PrefetchAroundCurrent();
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
    /// 应用不活跃时不排新一轮,由回前台的重排负责。
    /// </summary>
    public void PrefetchAroundCurrent()
    {
        _currentAroundCts?.Cancel();
        _currentAroundCts = new CancellationTokenSource();
        var ct = _currentAroundCts.Token;
        if (!CanScheduleBackgroundWork()) return;
        var appToken = ApplicationLifetimeToken;

        Task.Run(async () =>
        {
            try
            {
                if (!IsApplicationActive) return;
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
                bool longRawLookahead = RawAlongsideLookahead > AlongsideDepth;
                int rawAhead = longRawLookahead
                    ? SelectRawAlongsideIndex(files, idx, direction, forward, backward, RawAlongsideLookahead)
                    : -1;
                int aheadDepth = aheadExists && RawLoader.IsRawFile(files[aheadIndex].File) ? RawAlongsideDepth : AlongsideDepth;
                bool IsAhead(int index)
                {
                    if (index == rawAhead) return true;
                    int offset = (index - idx) * direction;
                    if (offset < 1 || offset > (longRawLookahead ? AlongsideDepth : aheadDepth)) return false;
                    // 扩展口径下 +1/+2 只给非 RAW：RAW 的名额由上面唯一一张占用。
                    return !longRawLookahead || !RawLoader.IsRawFile(files[index].File);
                }

                int proxyTarget = BitmapLoader.ProxyTargetLongEdge;
                bool fullNeighbors = BitmapLoader.PrefetchFullWanted && BitmapLoader.PrefetchFullNeighbors;
                var items = sorted.Select(t =>
                {
                    bool alongside = aheadExists && IsAhead(t.index);
                    return (file: files[t.index],
                        full: ShouldPrefetchFull(proxyTarget, fullNeighbors, t.distance, alongside),
                        alongP0: alongside);
                }).ToList();
                await RunQueuedAsync(items, ct, appToken).ConfigureAwait(false);
            }
            catch { /* 忽略 */ }
        }, ct);
    }

    public static bool ShouldPrefetchFull(int proxyTarget, bool fullNeighbors, int distance, bool alongside)
        => proxyTarget <= 0 || (fullNeighbors && (distance == 1 || alongside));

    /// <summary>
    /// 沿实际翻页方向、在已配置的前/后预载份数内找距离最近的 RAW（距离上限 <paramref name="window"/>）。
    /// 只返回一张：它获得「沿 P0 并行」资格，其余 RAW 仍按普通后台预取排。
    /// </summary>
    /// <param name="files">当前筛选后的文件列表。</param>
    /// <param name="currentIndex">当前图下标。</param>
    /// <param name="direction">翻页方向（正=向后翻，负=向前翻）。</param>
    /// <param name="forwardCount">配置的向后预载份数。</param>
    /// <param name="backwardCount">配置的向前预载份数。</param>
    /// <param name="window">RAW 提前窗口（距离上限，含）。</param>
    /// <returns>选中的 RAW 下标；窗口内没有 RAW 返回 -1。</returns>
    public static int SelectRawAlongsideIndex(IReadOnlyList<ImageFile> files, int currentIndex, int direction,
        int forwardCount, int backwardCount, int window)
    {
        if (files is null || currentIndex < 0 || currentIndex >= files.Count || direction == 0) return -1;
        int side = direction > 0 ? forwardCount : backwardCount;
        int bound = direction > 0 ? files.Count - 1 - currentIndex : currentIndex;
        int limit = Math.Min(Math.Min(window, Math.Max(0, side)), bound);
        for (int distance = 1; distance <= limit; distance++)
        {
            int index = currentIndex + Math.Sign(direction) * distance;
            if (RawLoader.IsRawFile(files[index].File)) return index;
        }

        return -1;
    }

    /// <summary>
    /// 滚动停止后调用:预取可见区域中心附近图片。应用不活跃时不排新一轮。
    /// </summary>
    public void PrefetchVisibleCenter(int firstIndex, int lastIndex)
    {
        _currentVisibleCenterCts?.Cancel();
        _currentVisibleCenterCts = new CancellationTokenSource();
        var ct = _currentVisibleCenterCts.Token;
        if (!CanScheduleBackgroundWork()) return;
        var appToken = ApplicationLifetimeToken;

        Task.Run(async () =>
        {
            try
            {
                if (!IsApplicationActive) return;
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
                await RunQueuedAsync(items, ct, appToken).ConfigureAwait(false);
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
    /// alongP0=true 的项(翻页方向上的下一张)允许在 P0 在途时执行,且不等待高优先级空闲;
    /// 它平常切图不取消(跑完为止),但同样链接 <paramref name="appToken"/>——应用停止时取消。
    /// </summary>
    private Task RunQueuedAsync(IEnumerable<(ImageFile file, bool full, bool alongP0)> items,
        CancellationToken ct, CancellationToken appToken)
    {
        foreach (var (f, wantFull, alongP0) in items)
        {
            if (ct.IsCancellationRequested) break;
            if (!CanScheduleBackgroundWork()) break;
            var file = f;
            var full = wantFull;
            var alongside = alongP0;
            DecodeScheduler.Submit(file.File.StorageId.Value, DecodePriority.P2_Prefetch, async token =>
            {
                // 压力闸:Caution/恢复观察期起预取不执行(投递即空跑退出)
                if (MemoryGovernor.CurrentLevel >= PressureLevel.Caution || MemoryGovernor.InRecoveryHold) return;
                // 活跃闸:暂停时已入队但还没开跑的项到这里空跑退出,不再起任何后台解码
                if (!IsApplicationActive) return;

                // 沿 P0 并行的项一旦开跑就让它跑完：它正是即将上屏的那一张，轮次切换只阻止尚未开始的工作。
                // 否则下一张的预取每次都在"快好了"时被丢弃（取消点只在解码/绘制之后），P0 再从零解一遍。
                // 应用停止令牌对两类项都链接：后台状态不保留任何在途后台工作。
                using var linkedCts = alongside
                    ? CancellationTokenSource.CreateLinkedTokenSource(token, appToken)
                    : CancellationTokenSource.CreateLinkedTokenSource(token, ct, appToken);
                var lct = linkedCts.Token;

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
    /// 慢图(RAW 等)的 P0 常长于停留窗口:到期时 P0 仍在途就先让位,待主图解完再重新计满 600ms 稳定停留,
    /// 避免 Full 与"代理刚上屏、用户可能已切走"重叠抢解码器;当前图没有已上屏的代理产物(失败/无图)时不额外重解 Full。
    /// 预取不挤更重要条目:当前图是排名 ①,它的 Full 可以淘汰任意更差的排名;腾不出位置则跳过。
    /// 应用不活跃时不排本轮,由回前台的重排重新计划。
    /// </summary>
    private void ScheduleCurrentFullWarmup()
    {
        _fullWarmCts?.Cancel();
        _fullWarmCts = new CancellationTokenSource();
        var ct = _fullWarmCts.Token;
        if (!CanScheduleBackgroundWork()) return;
        var appToken = ApplicationLifetimeToken;
        var file = _main.CurrentFile;
        if (file == null) return;

        var path = file.File.StorageId.Value;

        // 预热前提:仍停在当前图、不需要 Full、分析栏不可见、压力 Normal 且不在恢复观察期
        bool WarmupAllowed(CancellationToken token)
            => !token.IsCancellationRequested
                && !BitmapLoader.PrefetchFullWanted
                && !_main.IsAnalysisViewVisible
                && MemoryGovernor.CurrentLevel == PressureLevel.Normal
                && !MemoryGovernor.InRecoveryHold;

        _ = Task.Run(async () =>
        {
            try
            {
                // 应用停止令牌与切图令牌一起等待:进后台立刻放弃本轮,回前台由重排重新计划
                using var roundCts = CancellationTokenSource.CreateLinkedTokenSource(ct, appToken);
                var wct = roundCts.Token;

                await Task.Delay(FullWarmupDelayMs, wct).ConfigureAwait(false);
                if (!WarmupAllowed(wct)) return;
                // 慢图的代理此刻可能还在 P0:等主图解完,再给它一段稳定停留窗口才预热
                if (DecodeScheduler.P0InFlightCount > 0)
                {
                    if (!await WaitForP0IdleAsync(wct).ConfigureAwait(false)) return;
                    await Task.Delay(FullWarmupDelayMs, wct).ConfigureAwait(false);
                    if (!WarmupAllowed(wct)) return;
                }

                if (BitmapLoader.IsInCache(path, ImageSizeClass.Full)) return;
                // 只有当前图确实解出过代理才预热:解码失败/无图时不额外做一次 Full 重解
                if (BitmapLoader.TryGetCached(path, ImageSizeClass.Proxy) == null) return;

                DecodeScheduler.Submit(path, DecodePriority.P2_Prefetch, async token =>
                {
                    if (MemoryGovernor.CurrentLevel >= PressureLevel.Caution || MemoryGovernor.InRecoveryHold) return;
                    if (!IsApplicationActive) return;
                    if (BitmapLoader.IsInCache(path, ImageSizeClass.Full)) return;
                    if (BitmapLoader.PrefetchFullWanted) return;
                    using var itemCts = CancellationTokenSource.CreateLinkedTokenSource(token, ct, appToken);
                    var image = await BitmapLoader.GetBitmapAsync(file.File, DecodePriority.P2_Prefetch, itemCts.Token).ConfigureAwait(false);
                    image?.Release();
                });
            }
            catch (OperationCanceledException) { /* 切图或进后台后本轮作废 */ }
            catch (Exception ex)
            {
                Console.WriteLine($"[BitmapPrefetcher] full warmup failed for {file.Name}: {ex.Message}");
            }
        }, ct);
    }

    /// <summary>
    /// P0(主图解码)在途时让位:每 120ms 复查取消、压力级别与恢复观察期;
    /// 期间升到 Caution 以上或进入观察期即放弃本次预热(返回 false)。
    /// </summary>
    /// <param name="ct">本轮预热令牌(切图或应用停止即取消)。</param>
    /// <returns>P0 已空闲返回 true;被取消或压力不允许返回 false。</returns>
    private static async Task<bool> WaitForP0IdleAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            if (DecodeScheduler.P0InFlightCount == 0) return true;
            if (MemoryGovernor.CurrentLevel >= PressureLevel.Caution || MemoryGovernor.InRecoveryHold) return false;
            await Task.Delay(120, ct).ConfigureAwait(false);
        }

        return false;
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

using System;
using System.Linq;
using System.Reactive.Concurrency;
using System.Threading.Tasks;
using PhotoViewer.Core.Image;
using PhotoViewer.Core.Platform;
using PhotoViewer.Core.Settings;
using PhotoViewer.ViewModels.Settings;

namespace SettingsDefaultsCheck;

/// <summary>
/// 用假预算源核对设置默认值、schemaVersion 2 往返、draft 不写盘与保存失败不发布。
/// </summary>
internal static class Program
{
    private static async Task<int> Main()
    {
        var dispatcher = new InlineDispatcher();
        UiDispatcher.Initialize(dispatcher);
        var reactiveBuilder = ReactiveUI.Builder.RxAppBuilder.CreateReactiveUIBuilder();
        reactiveBuilder.WithCoreServices();
        reactiveBuilder.WithMainThreadScheduler(dispatcher.MainThreadScheduler);
        reactiveBuilder.BuildApp();
        BitmapLoader.ProxyTargetLongEdge = 3840;

        var failed = 0;
        foreach (var physicalMB in new[] { 4096, 8192, 16384, 32768, 65536 })
        {
            failed += await CheckFreshAsync(physicalMB, null);
        }

        failed += await CheckFreshAsync(8192, 30);
        failed += await CheckFreshAsync(8192, 45);
        failed += await CheckSavedAsync();
        failed += await CheckZeroRoundtripAsync();
        failed += await CheckDraftDoesNotWriteAsync();
        failed += await CheckSimilarityPanelViewStateAsync();
        failed += await CheckLiveCommitAsync();
        failed += await CheckSaveFailureAsync();
        failed += await CheckNativeSessionAsync();
        failed += await CheckRecoveryStatusAsync();

        Console.WriteLine(failed == 0 ? "ALL PASS" : $"{failed} FAIL");
        return failed == 0 ? 0 : 1;
    }

    private static async Task<int> CheckFreshAsync(int physicalMB, int? countOverride)
    {
        var budget = new WindowsLikeBudget(physicalMB, countOverride);
        PerformanceBudget.Initialize(budget);
        var defaults = SettingsDefaults.CreateDefault(budget, appleKeyboard: false);
        using var context = await DraftContext.CreateAsync(defaults);
        var settings = context.Settings;

        var needed = settings.PreloadForwardCount + settings.PreloadBackwardCount + 2;
        var counts = $"preload 前{settings.PreloadForwardCount}/后{settings.PreloadBackwardCount}"
            + $"/中{settings.VisibleCenterPreloadCount} 上限{settings.PreloadMaximum}";
        var line = $"物理 {physicalMB} MB{(countOverride is { } forced ? $"(张数 {forced})" : string.Empty)} -> 缓存 {settings.BitmapCacheMaxMemory} MB / {settings.BitmapCacheMaxCount} 张; "
            + counts + $"; 需 {needed} 张; {settings.BitmapCacheCountInfo}";
        Console.WriteLine(line);

        var problems = 0;
        problems += Require(settings.PreloadForwardCount <= settings.PreloadMaximum, "前预载超过上限");
        problems += Require(settings.PreloadBackwardCount <= settings.PreloadMaximum, "后预载超过上限");
        problems += Require(settings.VisibleCenterPreloadCount <= settings.PreloadMaximum, "列表滚动预载超过上限");
        problems += Require(needed <= settings.BitmapCacheMaxCount, "前后预载 + 当前 + 对比超过缓存张数");
        problems += Require(settings.BitmapCacheCountInfoLevel == PerfInfoLevel.Normal,
            $"提示级别为 {settings.BitmapCacheCountInfoLevel}");

        if (settings.BitmapCacheMaxCount >= 30)
        {
            problems += Require(settings.PreloadForwardCount == 10, $"张数 ≥ 30 时前预载不是 10: {settings.PreloadForwardCount}");
            problems += Require(settings.PreloadBackwardCount == 5, $"张数 ≥ 30 时后预载不是 5: {settings.PreloadBackwardCount}");
            problems += Require(settings.VisibleCenterPreloadCount == 5,
                $"张数 ≥ 30 时列表滚动预载不是 5: {settings.VisibleCenterPreloadCount}");
        }

        if (settings.BitmapCacheMaxCount < 30)
        {
            problems += Require(settings.PreloadBackwardCount <= settings.PreloadForwardCount,
                "后预载多于前预载");
            problems += Require(settings.PreloadForwardCount > 0, "前预载默认值为 0");
        }

        return problems;
    }

    private static async Task<int> CheckSavedAsync()
    {
        var budget = new WindowsLikeBudget(8192, null);
        PerformanceBudget.Initialize(budget);
        var defaults = SettingsDefaults.CreateDefault(budget, appleKeyboard: false);
        var saved = defaults with
        {
            BitmapCacheMaxCount = 40,
            PreloadForwardCount = 7,
            PreloadBackwardCount = 3,
            VisibleCenterPreloadCount = 4,
            BitmapCacheMaxMemory = 3000,
            Hotkeys = [new HotkeySetting("ToggleEnhance", true)],
        };

        using var context = await DraftContext.CreateAsync(defaults, SettingsDocumentCodec.Serialize(saved));
        var settings = context.Settings;

        Console.WriteLine(
            $"已保存设置 -> 缓存 {settings.BitmapCacheMaxMemory} MB / {settings.BitmapCacheMaxCount} 张; "
            + $"preload 前{settings.PreloadForwardCount}/后{settings.PreloadBackwardCount}"
            + $"/中{settings.VisibleCenterPreloadCount} 上限{settings.PreloadMaximum}");

        var problems = 0;
        problems += Require(settings.BitmapCacheMaxMemory == 3000, "已保存缓存内存被改写");
        problems += Require(settings.BitmapCacheMaxCount == 40, "已保存缓存张数被改写");
        problems += Require(settings.PreloadForwardCount == 7, "已保存前预载被改写");
        problems += Require(settings.PreloadBackwardCount == 3, "已保存后预载被改写");
        problems += Require(settings.VisibleCenterPreloadCount == 4, "已保存列表滚动预载被改写");
        problems += Require(settings.Hotkeys[0].Name == "雾天增强", "快捷键名称未按 Command 取当前命令目录");
        problems += Require(settings.Hotkeys[0].Tooltip == "雾天增强", "快捷键提示未按 Command 取当前命令目录");

        var json = SettingsDocumentCodec.Serialize(settings.CreateSnapshot());
        problems += Require(!json.Contains("displayName", StringComparison.OrdinalIgnoreCase), "文件格式显示名仍被持久化");
        problems += Require(!json.Contains("extensions", StringComparison.OrdinalIgnoreCase), "文件格式扩展名仍被持久化");
        problems += Require(!json.Contains("displaySymbol", StringComparison.OrdinalIgnoreCase), "快捷键图标仍被持久化");
        problems += Require(!json.Contains("tooltip", StringComparison.OrdinalIgnoreCase), "快捷键提示仍被持久化");
        return problems;
    }

    private static async Task<int> CheckZeroRoundtripAsync()
    {
        var budget = new WindowsLikeBudget(8192, null);
        PerformanceBudget.Initialize(budget);
        var defaults = SettingsDefaults.CreateDefault(budget, appleKeyboard: false);
        var explicitZero = defaults with
        {
            ShowRating = false,
            SafeSetRating = false,
            AnalysisImmediateCompute = false,
            PreloadForwardCount = 0,
            PreloadBackwardCount = 0,
            VisibleCenterPreloadCount = 0,
        };

        var problems = 0;
        var json = SettingsDocumentCodec.Serialize(explicitZero);
        var read = SettingsDocumentCodec.Read(json, defaults);
        problems += Require(read.Status == SettingsDocumentReadStatus.Loaded, "0 值文档读取失败");
        if (read.Settings is { } roundtrip)
        {
            problems += Require(!roundtrip.ShowRating, "显式 false 被当作缺失");
            problems += Require(!roundtrip.SafeSetRating, "显式 false 被当作缺失（安全写回）");
            problems += Require(roundtrip.PreloadForwardCount == 0, "显式 0 前预载未保留");
            problems += Require(roundtrip.PreloadBackwardCount == 0, "显式 0 后预载未保留");
            problems += Require(roundtrip.VisibleCenterPreloadCount == 0, "显式 0 列表滚动预载未保留");
        }

        using var context = await DraftContext.CreateAsync(defaults);
        context.Settings.ApplySnapshot(explicitZero);
        var snapshot = context.Settings.CreateSnapshot();
        problems += Require(!snapshot.ShowRating, "VM 回填把显式 false 当作缺失");
        problems += Require(snapshot.PreloadForwardCount == 0, "VM 回填把显式 0 当作缺失（前预载）");
        problems += Require(snapshot.PreloadBackwardCount == 0, "VM 回填把显式 0 当作缺失（后预载）");
        problems += Require(snapshot.VisibleCenterPreloadCount == 0, "VM 回填把显式 0 当作缺失（列表滚动）");
        return problems;
    }

    private static async Task<int> CheckDraftDoesNotWriteAsync()
    {
        var budget = new WindowsLikeBudget(8192, null);
        PerformanceBudget.Initialize(budget);
        var defaults = SettingsDefaults.CreateDefault(budget, appleKeyboard: false);
        var store = new MemorySettingsStore();
        var repository = new SettingsRepository(store, defaults);
        await repository.InitializeAsync();

        using var settings = new SettingsViewModel(repository.BeginSession());
        var current = repository.Current;
        settings.ShowRating = !current.ShowRating;
        settings.SimilarityPanelExpanded = true;
        await Task.Delay(30);

        var problems = 0;
        problems += Require(store.SaveCalls == 0, "draft 编辑触发了写盘");
        problems += Require(repository.Current.ContentEquals(current), "draft 编辑改写了仓库当前值");
        problems += Require(settings.Session is not null
            && settings.Session.Draft.ShowRating == settings.ShowRating, "draft 未同步到 SettingsSession.Draft");
        problems += Require(settings.Session is not null
            && settings.Session.Draft.ContentEquals(settings.CreateSnapshot()), "draft 与界面快照不一致");
        return problems;
    }

    /// <summary>相似面板展开态走视图状态存储：展开与收起都必须写回，重启后沿用最后一次开关。</summary>
    private static async Task<int> CheckSimilarityPanelViewStateAsync()
    {
        var budget = new WindowsLikeBudget(8192, null);
        PerformanceBudget.Initialize(budget);
        var defaults = SettingsDefaults.CreateDefault(budget, appleKeyboard: false);
        var store = new MemorySettingsStore();
        var repository = new SettingsRepository(store, defaults);
        await repository.InitializeAsync();

        var viewState = new InMemorySettingsStore();
        using var settings = new SettingsViewModel(repository, viewState);
        var problems = 0;
        problems += Require(!settings.SimilarityPanelExpanded, "相似面板初始态不是收起");

        settings.SimilarityPanelExpanded = true;
        problems += Require(viewState.GetBool(SettingsViewStateKeys.SimilarityPanelExpanded) == true,
            "展开态未写入视图状态");

        settings.SimilarityPanelExpanded = false;
        problems += Require(viewState.GetBool(SettingsViewStateKeys.SimilarityPanelExpanded) == false,
            "收起态未写入视图状态（下次启动会按旧 true 重新展开）");

        using var reopened = new SettingsViewModel(repository, viewState);
        problems += Require(!reopened.SimilarityPanelExpanded, "重新创建后未沿用收起态");
        return problems;
    }

    private static async Task<int> CheckLiveCommitAsync()
    {
        var budget = new WindowsLikeBudget(8192, null);
        PerformanceBudget.Initialize(budget);
        var defaults = SettingsDefaults.CreateDefault(budget, appleKeyboard: false);
        var store = new MemorySettingsStore();
        var repository = new SettingsRepository(store, defaults);

        using var settings = new SettingsViewModel(repository);
        var target = !repository.Current.ShowRating;
        settings.ShowRating = target;

        var committed = await WaitUntilAsync(() => repository.Current.ShowRating == target);
        var problems = 0;
        problems += Require(committed, "live 编辑未提交到仓库");
        problems += Require(store.SaveCalls >= 1, "live 编辑未写盘");
        problems += Require(settings.PersistenceStatus is null, $"live 保存成功仍显示状态：{settings.PersistenceStatus}");
        return problems;
    }

    private static async Task<int> CheckSaveFailureAsync()
    {
        var budget = new WindowsLikeBudget(8192, null);
        PerformanceBudget.Initialize(budget);
        var defaults = SettingsDefaults.CreateDefault(budget, appleKeyboard: false);
        var store = new MemorySettingsStore(SettingsDocumentCodec.Serialize(defaults)) { FailSave = true };
        var repository = new SettingsRepository(store, defaults);
        await repository.InitializeAsync();
        var good = repository.Current;

        var problems = 0;
        var success = await repository.CommitAsync(good with { ShowRating = false });
        problems += Require(!success, "保存失败仍返回成功");
        problems += Require(repository.Current.ContentEquals(good), "保存失败覆盖了 last good");
        problems += Require(repository.LastSaveError is not null, "保存失败未记录错误");

        using var settings = new SettingsViewModel(repository);
        settings.SafeSetRating = !good.SafeSetRating;
        await WaitUntilAsync(() => store.SaveCalls >= 2);
        await Task.Delay(30);
        problems += Require(repository.Current.ContentEquals(good), "live 保存失败仍发布了运行时设置");
        problems += Require(settings.PersistenceStatus?.Contains("保存设置失败", StringComparison.Ordinal) == true,
            $"保存失败未刷新 PersistenceStatus：{settings.PersistenceStatus}");
        return problems;
    }

    private static async Task<int> CheckNativeSessionAsync()
    {
        var budget = new WindowsLikeBudget(8192, null);
        PerformanceBudget.Initialize(budget);
        var defaults = SettingsDefaults.CreateDefault(budget, appleKeyboard: false);
        var store = new MemorySettingsStore();
        var repository = new SettingsRepository(store, defaults);

        using var source = new SettingsViewModel(repository);
        using var session = new NativeSettingsSession(source);
        var target = !source.ShowRating;
        session.WorkingSettings.ShowRating = target;
        await Task.Delay(30);

        var problems = 0;
        problems += Require(store.SaveCalls == 0, "原生 draft 编辑提前写盘");
        var applied = await session.ApplyAsync();
        problems += Require(applied, "原生会话 ApplyAsync 失败");
        problems += Require(repository.Current.ShowRating == target, "原生会话未提交到仓库");
        problems += Require(store.SaveCalls >= 1, "原生会话未写盘");
        var saves = store.SaveCalls;
        problems += Require(await session.ApplyAsync(), "原生会话重复 ApplyAsync 失败");
        problems += Require(store.SaveCalls == saves, "原生会话重复 ApplyAsync 重复写盘");
        return problems;
    }

    private static async Task<int> CheckRecoveryStatusAsync()
    {
        var budget = new WindowsLikeBudget(8192, null);
        PerformanceBudget.Initialize(budget);
        var defaults = SettingsDefaults.CreateDefault(budget, appleKeyboard: false);

        var corruptStore = new MemorySettingsStore("{ broken");
        var corruptRepository = new SettingsRepository(corruptStore, defaults);
        using var corruptSettings = new SettingsViewModel(corruptRepository);
        await WaitUntilAsync(() => corruptSettings.PersistenceStatus is not null);

        var problems = 0;
        problems += Require(corruptRepository.LoadOutcome == SettingsLoadOutcome.Recovered, "损坏文档未按恢复处理");
        problems += Require(corruptSettings.PersistenceStatus?.Contains("已备份为 memory-backup", StringComparison.Ordinal) == true,
            $"损坏恢复状态未显示备份位置：{corruptSettings.PersistenceStatus}");
        problems += Require(corruptStore.SaveCalls == 0, "恢复默认值后未修改却写盘");

        var futureStore = new MemorySettingsStore("{\"schemaVersion\":3}");
        var futureRepository = new SettingsRepository(futureStore, defaults);
        await futureRepository.InitializeAsync();
        using var futureSettings = new SettingsViewModel(futureRepository);
        futureSettings.ShowRating = !futureSettings.ShowRating;
        await Task.Delay(80);

        problems += Require(futureRepository.LoadOutcome == SettingsLoadOutcome.UnsupportedVersion && !futureRepository.CanCommit,
            "未来版本文档未进入不可写状态");
        problems += Require(futureStore.SaveCalls == 0, "未来版本文档被覆盖");
        problems += Require(futureSettings.PersistenceStatus?.Contains("高于当前支持版本", StringComparison.Ordinal) == true,
            $"未来版本状态未显示：{futureSettings.PersistenceStatus}");
        return problems;
    }

    private static async Task<bool> WaitUntilAsync(Func<bool> condition, int timeoutMs = 3000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (condition()) return true;
            await Task.Delay(10).ConfigureAwait(false);
        }

        return condition();
    }

    private static int Require(bool condition, string message)
    {
        if (condition)
        {
            return 0;
        }

        Console.WriteLine("  FAIL " + message);
        return 1;
    }

    private sealed class DraftContext : IDisposable
    {
        private DraftContext(SettingsViewModel settings)
        {
            Settings = settings;
        }

        public SettingsViewModel Settings { get; }

        public static async Task<DraftContext> CreateAsync(AppSettings defaults, string? document = null)
        {
            var store = new MemorySettingsStore(document);
            var repository = new SettingsRepository(store, defaults);
            await repository.InitializeAsync();
            return new DraftContext(new SettingsViewModel(repository.BeginSession()));
        }

        public void Dispose() => Settings.Dispose();
    }

    private sealed class MemorySettingsStore(string? content = null) : ISettingsStore
    {
        private string? _content = content;

        public int LoadCalls { get; private set; }

        public int SaveCalls { get; private set; }

        public bool FailSave { get; init; }

        public Task<SettingsStoreLoadResult> LoadAsync(System.Threading.CancellationToken cancellationToken = default)
        {
            LoadCalls++;
            return Task.FromResult(_content is null
                ? SettingsStoreLoadResult.Absent
                : SettingsStoreLoadResult.FromContent(_content));
        }

        public Task SaveAsync(string json, System.Threading.CancellationToken cancellationToken = default)
        {
            SaveCalls++;
            if (FailSave)
                return Task.FromException(new System.IO.IOException("simulated save failure"));

            _content = json;
            return Task.CompletedTask;
        }

        public Task<string?> BackupCorruptAsync(System.Threading.CancellationToken cancellationToken = default)
            => Task.FromResult<string?>("memory-backup");
    }

    /// <summary>按 Windows 壳的口径给出推荐值（见 PhotoViewer.Windows/Core/WindowsPerformanceBudget.cs）。</summary>
    private sealed class WindowsLikeBudget : IPerformanceBudget
    {
        private const int CacheMemoryDivisor = 4;
        private const int MaxCacheCount = 30;
        private const int MinCacheCount = 3;
        private const int PhotoPixelCap = 24_000_000;
        private const int BytesPerPixel = 8;

        private readonly DefaultPerformanceBudget _platform = new();
        private readonly int _physicalMB;
        private readonly int? _countOverride;

        public WindowsLikeBudget(int physicalMB, int? countOverride)
        {
            _physicalMB = physicalMB;
            _countOverride = countOverride;
        }

        public int GetAppMemoryLimitMB() => _physicalMB;

        public int GetCpuCoreCount() => 8;

        public int GetNativePreloadThreadLimit() => 8;

        public int GetCpuPreloadThreadLimit() => 4;

        public int GetRecommendedCacheMemoryMB() => Math.Clamp(_physicalMB / CacheMemoryDivisor, 1024, 8192);

        public int GetRecommendedCacheCount(int cacheMemoryMB)
        {
            if (_countOverride is { } forced)
            {
                return forced;
            }

            var perPhotoMB = (int)Math.Max(1, ((long)PhotoPixelCap * BytesPerPixel + (1024 * 1024) - 1) / (1024 * 1024));
            return Math.Clamp(cacheMemoryMB / perPhotoMB, MinCacheCount, MaxCacheCount);
        }

        public int GetRecommendedNativePreloadParallelism(int cacheMemoryMB)
            => Math.Min(Math.Clamp(GetCpuCoreCount() / 3, 2, 6), Math.Max(1, cacheMemoryMB / 1024));

        public int GetRecommendedCpuPreloadParallelism(int cacheMemoryMB)
            => ((IPerformanceBudget)_platform).GetRecommendedCpuPreloadParallelism(cacheMemoryMB);
    }

    /// <summary>控制台用的就地调度器。</summary>
    private sealed class InlineDispatcher : IUiDispatcher
    {
        public IScheduler MainThreadScheduler { get; } = ImmediateScheduler.Instance;

        public bool CheckAccess() => true;

        public void Post(Action action, UiPriority priority = UiPriority.Normal) => action();

        public Task InvokeAsync(Action action, UiPriority priority = UiPriority.Normal)
        {
            action();
            return Task.CompletedTask;
        }

        public Task<T> InvokeAsync<T>(Func<T> func, UiPriority priority = UiPriority.Normal) => Task.FromResult(func());
    }
}

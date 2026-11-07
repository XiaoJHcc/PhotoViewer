using System;
using System.Linq;
using System.Reactive.Concurrency;
using System.Reflection;
using System.Threading.Tasks;
using PhotoViewer.Core.Image;
using PhotoViewer.Core.Memory;
using PhotoViewer.Core.Platform;
using PhotoViewer.Core.Settings;
using PhotoViewer.ViewModels.Settings;

namespace SettingsDefaultsCheck;

/// <summary>
/// 用假预算源核对设置默认值、schemaVersion 2 往返、滚动预载延时刻度归一、draft 不写盘、保存失败不发布，
/// 以及设备缓存能力提示的纯计算口径与设置解耦的超限警告（SETTINGS-06）。
/// </summary>
internal static class Program
{
    private static readonly FieldInfo MemoryProviderField =
        typeof(MemoryGovernor).GetField("_provider", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("MemoryGovernor._provider 字段不存在，预算源替身无法注入。");

    private static readonly FieldInfo PressureLevelField =
        typeof(MemoryGovernor).GetField("_level", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("MemoryGovernor._level 字段不存在，压力级别无法构造。");

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
        failed += await CheckVisibleCenterDelayAsync();
        failed += await CheckDraftDoesNotWriteAsync();
        failed += await CheckSimilarityPanelViewStateAsync();
        failed += await CheckLiveCommitAsync();
        failed += await CheckSaveFailureAsync();
        failed += await CheckNativeSessionAsync();
        failed += await CheckRecoveryStatusAsync();
        failed += CheckDeviceCacheEstimate();
        failed += CheckCacheLimitWarning();
        failed += await CheckDeviceCacheInfoInSettingsAsync();
        failed += await CheckNormalPressureBytesPerPixelAsync();

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
            AnalysisImmediateCvCompute = false,
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

    /// <summary>
    /// 滚动预载延时：100–2000 ms、步进 100 ms，输入与文档读写都按最近刻度归一，正中间向上取整（SETTINGS-06）。
    /// </summary>
    private static async Task<int> CheckVisibleCenterDelayAsync()
    {
        var budget = new WindowsLikeBudget(8192, null);
        PerformanceBudget.Initialize(budget);
        var defaults = SettingsDefaults.CreateDefault(budget, appleKeyboard: false);

        var problems = 0;
        problems += Require(
            SettingsDocumentCodec.MinimumVisibleCenterDelayMs == 100
                && SettingsDocumentCodec.MaximumVisibleCenterDelayMs == 2000
                && SettingsDocumentCodec.VisibleCenterDelayStepMs == 100,
            $"范围常量不是 100–2000 / 100：{SettingsDocumentCodec.MinimumVisibleCenterDelayMs}–"
                + $"{SettingsDocumentCodec.MaximumVisibleCenterDelayMs} / {SettingsDocumentCodec.VisibleCenterDelayStepMs}");
        problems += Require(defaults.VisibleCenterDelayMs == 1000, $"默认延时不是 1000 ms：{defaults.VisibleCenterDelayMs}");

        for (var ms = SettingsDocumentCodec.MinimumVisibleCenterDelayMs;
             ms <= SettingsDocumentCodec.MaximumVisibleCenterDelayMs;
             ms += SettingsDocumentCodec.VisibleCenterDelayStepMs)
        {
            var normalized = SettingsDocumentCodec.NormalizeVisibleCenterDelayMs(ms);
            problems += Require(normalized == ms, $"刻度 {ms} ms 未原样保留：{normalized}");
        }

        (int Input, int Expected)[] samples =
        [
            (int.MinValue, 100), (-500, 100), (0, 100), (50, 100), (99, 100), (100, 100),
            (149, 100), (150, 200), (151, 200), (249, 200), (250, 300),
            (349, 300), (350, 400), (1049, 1000), (1050, 1100), (1150, 1200),
            (1949, 1900), (1950, 2000), (1999, 2000), (2000, 2000),
            (2001, 2000), (5000, 2000), (int.MaxValue, 2000),
        ];
        foreach (var (input, expected) in samples)
        {
            var normalized = SettingsDocumentCodec.NormalizeVisibleCenterDelayMs(input);
            problems += Require(normalized == expected, $"归一 {input} ms 应为 {expected}，实际 {normalized}");
        }

        // 文档：写入按刻度归一，读取也把越界/非刻度值收回刻度。
        var serializedInRange = SettingsDocumentCodec.Serialize(defaults with { VisibleCenterDelayMs = 340 });
        var readInRange = SettingsDocumentCodec.Read(serializedInRange, defaults);
        problems += Require(
            readInRange.Settings?.VisibleCenterDelayMs == 300,
            $"写入 340 ms 后回读不是 300：{readInRange.Settings?.VisibleCenterDelayMs}");

        var serializedHalf = SettingsDocumentCodec.Serialize(defaults with { VisibleCenterDelayMs = 150 });
        var readHalf = SettingsDocumentCodec.Read(serializedHalf, defaults);
        problems += Require(
            readHalf.Settings?.VisibleCenterDelayMs == 200,
            $"写入 150 ms 后回读不是 200（正中间向上取整）：{readHalf.Settings?.VisibleCenterDelayMs}");

        var serializedOver = SettingsDocumentCodec.Serialize(defaults with { VisibleCenterDelayMs = 5000 });
        var readOver = SettingsDocumentCodec.Read(serializedOver, defaults);
        problems += Require(
            readOver.Settings?.VisibleCenterDelayMs == 2000,
            $"写入 5000 ms 后回读不是 2000：{readOver.Settings?.VisibleCenterDelayMs}");

        // 旧文档里可能存着旧范围（上限 5000）或任意非刻度值，读取时同样收回新刻度。
        (int Stored, int Expected)[] documents = [(5000, 2000), (4321, 2000), (340, 300), (150, 200), (60, 100)];
        foreach (var (stored, expected) in documents)
        {
            var document = $"{{\"schemaVersion\":{SettingsDocumentCodec.SchemaVersion},\"visibleCenterDelayMs\":{stored}}}";
            var read = SettingsDocumentCodec.Read(document, defaults);
            problems += Require(read.Status == SettingsDocumentReadStatus.Loaded, $"文档 {stored} ms 读取失败：{read.Status}");
            problems += Require(
                read.Settings?.VisibleCenterDelayMs == expected,
                $"文档 {stored} ms 读取后应为 {expected}，实际 {read.Settings?.VisibleCenterDelayMs}");
        }

        // 工作副本：输入按同一口径归一，草稿也拿到归一后的值。
        using var context = await DraftContext.CreateAsync(defaults);
        var settings = context.Settings;
        problems += Require(settings.VisibleCenterDelayMs == 1000, $"界面初值不是 1000 ms：{settings.VisibleCenterDelayMs}");

        settings.VisibleCenterDelayMs = 1234;
        problems += Require(settings.VisibleCenterDelayMs == 1200, $"输入 1234 未归一到 1200：{settings.VisibleCenterDelayMs}");
        settings.VisibleCenterDelayMs = 1250;
        problems += Require(settings.VisibleCenterDelayMs == 1300, $"输入 1250 未向上归一到 1300：{settings.VisibleCenterDelayMs}");
        settings.VisibleCenterDelayMs = 50;
        problems += Require(settings.VisibleCenterDelayMs == 100, $"输入 50 未夹到 100：{settings.VisibleCenterDelayMs}");
        settings.VisibleCenterDelayMs = 2001;
        problems += Require(settings.VisibleCenterDelayMs == 2000, $"输入 2001 未夹到 2000：{settings.VisibleCenterDelayMs}");
        settings.VisibleCenterDelayMs = -300;
        problems += Require(settings.VisibleCenterDelayMs == 100, $"输入 -300 未夹到 100：{settings.VisibleCenterDelayMs}");

        settings.VisibleCenterDelayMs = 1500;
        problems += Require(
            settings.Session is not null && settings.Session.Draft.VisibleCenterDelayMs == 1500,
            $"草稿未同步归一后的延时：{settings.Session?.Draft.VisibleCenterDelayMs}");
        problems += Require(
            settings.CreateSnapshot().VisibleCenterDelayMs == 1500,
            $"快照未带上归一后的延时：{settings.CreateSnapshot().VisibleCenterDelayMs}");

        Console.WriteLine(
            $"滚动预载延时 -> {SettingsDocumentCodec.MinimumVisibleCenterDelayMs}–"
            + $"{SettingsDocumentCodec.MaximumVisibleCenterDelayMs} ms / 步进 {SettingsDocumentCodec.VisibleCenterDelayStepMs} ms; "
            + $"20 档刻度原样保留、{samples.Length} 个越界与半值取样、5 份文档、工作副本输入与草稿/快照同口径");
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

    /// <summary>
    /// 设备能力估算的纯计算口径：容量由调用方给出（可编辑平台 = 用户上限，自动平台 = 设备最大安全容量 =
    /// 预算 × 55%），适屏按代理长边 3:2 且不超过 3300 万像素、完整 3300 万像素，
    /// 两档各按 Normal 压力的每像素字节数换算（F16 = 8、8 位 = 4），未知读数给未知。
    /// </summary>
    private static int CheckDeviceCacheEstimate()
    {
        var problems = 0;
        const long budget = 8L * 1024 * 1024 * 1024;
        var system = new SystemMemoryInfo(16L * 1024 * 1024 * 1024, 6L * 1024 * 1024 * 1024);

        problems += Require(Math.Abs(DeviceCacheCapability.SafeRatio(40) - 0.55) < 1e-9,
            $"Caution 40 时安全比例不是 55%：{DeviceCacheCapability.SafeRatio(40)}");
        problems += Require(Math.Abs(DeviceCacheCapability.SafeRatio(30) - 0.65) < 1e-9,
            $"Caution 30 时安全比例不是 65%：{DeviceCacheCapability.SafeRatio(30)}");
        problems += Require(
            Math.Abs(DeviceCacheCapability.SafeRatio(MemoryGovernor.CautionEnterPercent) - 0.55) < 1e-9,
            $"默认安全比例未沿用 Caution 进入阈值 + 5：{DeviceCacheCapability.SafeRatio(MemoryGovernor.CautionEnterPercent)}");
        problems += Require(DeviceCacheCapability.FullPhotoPixels == 33_000_000, "完整照片像素口径不是 3300 万");

        long deviceSafe = DeviceCacheCapability.DeviceSafeCapacity(budget, 40) ?? -1;
        problems += Require(deviceSafe == (long)(budget * 0.55),
            $"设备最大安全容量不是预算 × 55%：{deviceSafe}");
        problems += Require(DeviceCacheCapability.DeviceSafeCapacity(0, 40) is null, "无预算时设备安全容量应为未知");
        problems += Require(DeviceCacheCapability.DeviceSafeCapacity(budget, 100) == 0, "安全比例为 0 时安全容量应为 0");

        problems += Require(DeviceCacheCapability.ScreenPhotoPixels(3840) == 9_830_400,
            $"适屏像素不是长边² × 2/3：{DeviceCacheCapability.ScreenPhotoPixels(3840)}");
        problems += Require(DeviceCacheCapability.ScreenPhotoPixels(8000) == 33_000_000,
            $"适屏像素未按 3300 万封顶：{DeviceCacheCapability.ScreenPhotoPixels(8000)}");
        problems += Require(DeviceCacheCapability.ScreenPhotoPixels(7036) == 33_000_000
                && DeviceCacheCapability.ScreenPhotoPixels(7035) == 32_994_150,
            $"封顶长边两侧不对：{DeviceCacheCapability.ScreenPhotoPixels(7035)}/"
                + $"{DeviceCacheCapability.ScreenPhotoPixels(7036)}");
        problems += Require(DeviceCacheCapability.ScreenPhotoPixels(int.MaxValue) == 33_000_000
                && DeviceCacheCapability.ScreenPhotoPixels(int.MaxValue - 1) == 33_000_000,
            $"极大长边溢出：{DeviceCacheCapability.ScreenPhotoPixels(int.MaxValue)}");
        problems += Require(DeviceCacheCapability.ScreenPhotoPixels(0) == 0, "视口未知时适屏像素不是 0");

        var f16 = DeviceCacheCapability.Compute(system, deviceSafe, 3840, 8, 8);
        var eightBit = DeviceCacheCapability.Compute(system, deviceSafe, 3840, 4, 4);
        problems += Require(f16.CapacityBytes == deviceSafe,
            $"容量未原样用于换算：{f16.CapacityBytes}/{deviceSafe}");
        problems += Require(f16.ScreenPhotoCount == 60, $"F16 适屏张数不是 60：{f16.ScreenPhotoCount}");
        problems += Require(f16.FullPhotoCount == 17, $"F16 完整张数不是 17：{f16.FullPhotoCount}");
        problems += Require(eightBit.ScreenPhotoCount == 120, $"8 位适屏张数不是 120：{eightBit.ScreenPhotoCount}");
        problems += Require(eightBit.FullPhotoCount == 35, $"8 位完整张数不是 35：{eightBit.FullPhotoCount}");

        // 用户上限口径：容量原样传入，不乘安全比例。
        var userPicked = DeviceCacheCapability.Compute(system, 2048L * 1024 * 1024, 3840, 4, 4);
        problems += Require(userPicked.CapacityBytes == 2048L * 1024 * 1024
                && userPicked.ScreenPhotoCount == 54 && userPicked.FullPhotoCount == 16,
            $"用户上限口径不对：{userPicked.CapacityBytes}/{userPicked.ScreenPhotoCount}/{userPicked.FullPhotoCount}");

        var noViewport = DeviceCacheCapability.Compute(system, deviceSafe, 0, 8, 8);
        problems += Require(noViewport.ScreenPhotoCount is null && noViewport.FullPhotoCount == 17,
            $"视口未知时只有适屏张数应为未知：{noViewport.ScreenPhotoCount}/{noViewport.FullPhotoCount}");

        var noCapacity = DeviceCacheCapability.Compute(system, null, 3840, 4, 4);
        problems += Require(noCapacity.CapacityBytes == 0
                && noCapacity.ScreenPhotoCount is null && noCapacity.FullPhotoCount is null,
            $"容量未知时张数应为未知：{noCapacity.ScreenPhotoCount}/{noCapacity.FullPhotoCount}");

        // 容量已知但不足一张时如实给 0，不当未知。
        foreach (var (zeroCapacity, label) in new (long? Capacity, string Label)[]
                 {
                     (DeviceCacheCapability.DeviceSafeCapacity(1, 40), "预算 1 字节"),
                     (DeviceCacheCapability.DeviceSafeCapacity(budget, 100), "安全比例为 0"),
                     (1L, "容量 1 字节"),
                 })
        {
            var zero = DeviceCacheCapability.Compute(system, zeroCapacity, 3840, 4, 4);
            problems += Require(zero.ScreenPhotoCount == 0 && zero.FullPhotoCount == 0,
                $"{label}时张数未如实给 0：{zero.ScreenPhotoCount}/{zero.FullPhotoCount}");
        }

        problems += Require(
            DeviceCacheCapability.DescribeCapacity(DeviceCacheCapability.Compute(system, 0, 3840, 4, 4))
                == "可缓存 0 张适屏照片 / 0 张 3300 万像素照片",
            "零容量文案不对");

        var unknown = DeviceCacheCapability.Compute(SystemMemoryInfo.Unknown, null, 0, 4, 4);
        problems += Require(DeviceCacheCapability.DescribeSystemMemory(unknown) == "系统内存: 总 未知 / 当前可用 未知",
            $"未知读数未显式未知：{DeviceCacheCapability.DescribeSystemMemory(unknown)}");
        problems += Require(DeviceCacheCapability.DescribeCapacity(unknown) == "适屏照片：未知 / 3300 万像素照片：未知",
            $"未知张数未显式未知：{DeviceCacheCapability.DescribeCapacity(unknown)}");
        problems += Require(DeviceCacheCapability.DescribeCapacity(noViewport) == "适屏照片：未知 / 17 张 3300 万像素照片",
            $"单项未知文案不对：{DeviceCacheCapability.DescribeCapacity(noViewport)}");
        problems += Require(
            DeviceCacheCapability.DescribeCapacity(DeviceCacheCapability.Compute(system, null, 3840, 8, 8))
                == "适屏照片：未知 / 3300 万像素照片：未知",
            "容量未知时两项都应未知");

        problems += Require(DeviceCacheCapability.DescribeCapacity(f16) == "可缓存 60 张适屏照片 / 17 张 3300 万像素照片",
            $"正常文案不对：{DeviceCacheCapability.DescribeCapacity(f16)}");
        foreach (var banned in new[] { "缓存能力", "最大", "约", "3300 万像素照片：", "（3300" })
        {
            problems += Require(!DeviceCacheCapability.DescribeCapacity(f16).Contains(banned, StringComparison.Ordinal),
                $"正常文案残留「{banned}」");
        }

        // 容量不足一张时如实为 0，不抬到 3 张。
        var tiny = DeviceCacheCapability.Compute(system, 100L * 1024 * 1024, 3840, 4, 4);
        problems += Require(tiny.FullPhotoCount == 0, $"零容量未如实给 0：{tiny.FullPhotoCount}");

        // 张数不受用户缓存数量上限（1–400）截断：容量足够时按字节换算，可超过 400。
        var huge = DeviceCacheCapability.Compute(system, 200L * 1024 * 1024 * 1024, 3840, 4, 4);
        problems += Require(huge.FullPhotoCount > 400, $"张数被 400 条上限截断：{huge.FullPhotoCount}");

        Console.WriteLine(
            $"设备能力估算 -> 设备安全容量 8 GiB × 55% = {deviceSafe} B："
            + $"F16 适屏 {f16.ScreenPhotoCount} / 完整 {f16.FullPhotoCount}，"
            + $"8 位适屏 {eightBit.ScreenPhotoCount} / 完整 {eightBit.FullPhotoCount}；"
            + $"用户 2048 MB 适屏 {userPicked.ScreenPhotoCount} / 完整 {userPicked.FullPhotoCount}；"
            + $"未知「{DeviceCacheCapability.DescribeCapacity(unknown)}」，极大长边 {DeviceCacheCapability.ScreenPhotoPixels(int.MaxValue)}");
        return problems;
    }

    /// <summary>
    /// 用户缓存上限超限判定的四个阈值：总内存/平台预算为错误级、可用内存/设备最大安全容量为警告级，
    /// 等于阈值不算超过，未知读数不误判；原因文本固定为「（当前设置超过 xx 上限）」，不带数字与单位。
    /// </summary>
    private static int CheckCacheLimitWarning()
    {
        var problems = 0;
        const long mb = 1024L * 1024;
        const long big = 1L << 40;
        long user = 4096 * mb;

        problems += Require(
            DeviceCacheCapability.EvaluateLimit(user, 8192 * mb, 8192 * mb, 8192 * mb, 8192 * mb) == CacheLimitWarning.Normal,
            "全部阈值之上仍告警");

        problems += Require(
            DeviceCacheCapability.EvaluateLimit(user, user, big, big, big).Level == CacheCapabilityLevel.Normal,
            "等于系统总内存被判超限");
        problems += Require(
            DeviceCacheCapability.EvaluateLimit(user, big, big, user, big).Level == CacheCapabilityLevel.Normal,
            "等于平台应用内存预算被判超限");
        problems += Require(
            DeviceCacheCapability.EvaluateLimit(user, big, user, big, big).Level == CacheCapabilityLevel.Normal,
            "等于系统当前可用内存被判超限");
        problems += Require(
            DeviceCacheCapability.EvaluateLimit(user, big, big, big, user).Level == CacheCapabilityLevel.Normal,
            "等于设备最大安全容量被判超限");

        var overTotal = DeviceCacheCapability.EvaluateLimit(user + mb, user, big, big, big);
        problems += Require(overTotal.Level == CacheCapabilityLevel.Error, $"超过系统总内存未判错误级：{overTotal.Level}");
        problems += Require(overTotal.Reason == "（当前设置超过系统总内存上限）",
            $"超过系统总内存的原因文本不对：{overTotal.Reason}");

        var overBudget = DeviceCacheCapability.EvaluateLimit(user + mb, big, big, user, big);
        problems += Require(overBudget.Level == CacheCapabilityLevel.Error, $"超过平台预算未判错误级：{overBudget.Level}");
        problems += Require(overBudget.Reason == "（当前设置超过应用内存上限）",
            $"超过平台预算的原因文本不对：{overBudget.Reason}");

        var overAvailable = DeviceCacheCapability.EvaluateLimit(user + mb, big, user, big, big);
        problems += Require(overAvailable.Level == CacheCapabilityLevel.Warning, $"超过系统可用未判警告级：{overAvailable.Level}");
        problems += Require(overAvailable.Reason == "（当前设置超过系统可用内存上限）",
            $"超过系统可用的原因文本不对：{overAvailable.Reason}");

        var overDevice = DeviceCacheCapability.EvaluateLimit(user + mb, big, big, big, user);
        problems += Require(overDevice.Level == CacheCapabilityLevel.Warning, $"超过设备安全容量未判警告级：{overDevice.Level}");
        problems += Require(overDevice.Reason == "（当前设置超过设备缓存上限）",
            $"超过设备安全容量的原因文本不对：{overDevice.Reason}");

        foreach (var warning in new[] { overTotal, overBudget, overAvailable, overDevice })
        {
            problems += Require(warning.Reason is { } reason
                    && !reason.Contains("MB", StringComparison.Ordinal)
                    && !reason.Any(char.IsDigit),
                $"警告文案带了数字或单位：{warning.Reason}");
        }

        var unknowns = DeviceCacheCapability.EvaluateLimit(32768 * mb, 0, -1, 0, 0);
        problems += Require(unknowns.Level == CacheCapabilityLevel.Normal && unknowns.Reason is null,
            $"未知读数被误判：{unknowns.Level}/{unknowns.Reason}");

        problems += Require(DeviceCacheCapability.EvaluateLimit(0, user, user, user, user).Level == CacheCapabilityLevel.Normal,
            "上限为 0 时仍告警");

        Console.WriteLine(
            "超限判定 -> 四阈值等于不告警；超过总内存/预算 = 错误级，超过可用/设备安全容量 = 警告级；"
            + $"未知读数 {unknowns.Level}，原因示例：{overDevice.Reason}");
        return problems;
    }

    /// <summary>
    /// 设置页设备能力提示：两行文本由系统读数、平台预算与 Normal 压力字节数决定，
    /// 与用户缓存内存/数量、线程、预载设置及自动口径无关；只有用户上限超限才抬级并给出原因。
    /// </summary>
    private static async Task<int> CheckDeviceCacheInfoInSettingsAsync()
    {
        var budget = new WindowsLikeBudget(8192, null);
        PerformanceBudget.Initialize(budget);
        var defaults = SettingsDefaults.CreateDefault(budget, appleKeyboard: false);

        const string systemLine = "系统内存: 总 16 GB / 当前可用 6 GB";
        // 可编辑平台：张数按工作副本的 2048 MB 上限折算（4 B/像素，适屏 3840² × 2/3）
        const string defaultCounts = "可缓存 54 张适屏照片 / 16 张 3300 万像素照片";

        using var provider = new MemoryProviderScope(new FixedMemoryProvider(
            totalBytes: 16L * 1024 * 1024 * 1024,
            availableBytes: 6L * 1024 * 1024 * 1024,
            budgetBytes: 8L * 1024 * 1024 * 1024));

        using var context = await DraftContext.CreateAsync(defaults);
        var settings = context.Settings;
        var problems = 0;

        problems += Require(settings.ShowsCacheLimits, "桌面口径不应隐藏缓存上限两项");
        problems += Require(settings.BitmapCacheMaxMemory == 2048, $"默认内存上限不是 2048 MB：{settings.BitmapCacheMaxMemory}");
        problems += Require(settings.PerformanceBudgetInfo == systemLine,
            $"第一行不是系统总量/当前可用：{settings.PerformanceBudgetInfo}");
        problems += Require(settings.BitmapCacheCountInfo == defaultCounts,
            $"第二行张数不符：{settings.BitmapCacheCountInfo}");
        problems += Require(settings.PerformanceBudgetInfoLevel == PerfInfoLevel.Normal, "无超限却抬级");
        problems += Require(settings.BitmapCacheCountInfoLevel == PerfInfoLevel.Normal, "张数行不应定级");
        foreach (var stale in new[] { "CPU", "核", "✓", "⚠", "预载", "告警", "缓存能力", "最大", "约 " })
        {
            problems += Require(!settings.PerformanceBudgetInfo.Contains(stale, StringComparison.Ordinal)
                && !settings.BitmapCacheCountInfo.Contains(stale, StringComparison.Ordinal),
                $"旧提示内容仍残留：{stale}");
        }

        // 内存上限翻倍：两档张数按同一比例变化。
        settings.BitmapCacheMaxMemory = 4096;
        problems += Require(settings.BitmapCacheCountInfo == "可缓存 109 张适屏照片 / 32 张 3300 万像素照片",
            $"内存翻倍后张数不对：{settings.BitmapCacheCountInfo}");
        problems += Require(settings.PerformanceBudgetInfo == systemLine, "未超限却追加了警告");

        // 数量、线程与预载都不影响张数。
        settings.BitmapCacheMaxCount = 100;
        settings.PreloadForwardCount = 20;
        settings.PreloadBackwardCount = 20;
        settings.VisibleCenterPreloadCount = 20;
        settings.NativePreloadParallelism = 2;
        settings.CpuPreloadParallelism = 3;
        problems += Require(settings.BitmapCacheCountInfo == "可缓存 109 张适屏照片 / 32 张 3300 万像素照片",
            $"数量/线程/预载改变了张数：{settings.BitmapCacheCountInfo}");
        problems += Require(settings.PerformanceBudgetInfo == systemLine, "数量/线程/预载改变了第一行");

        // 超过设备最大安全容量（8192 MB 预算 × 55% = 4505 MB）：仍按所选内存算张数，同时告警。
        settings.BitmapCacheMaxMemory = 5000;
        problems += Require(settings.PerformanceBudgetInfoLevel == PerfInfoLevel.Warning,
            $"超过设备安全容量未告警：{settings.PerformanceBudgetInfoLevel}");
        problems += Require(settings.PerformanceBudgetInfo == systemLine + "（当前设置超过设备缓存上限）",
            $"设备缓存警告文案不对：{settings.PerformanceBudgetInfo}");
        problems += Require(settings.BitmapCacheCountInfo == "可缓存 133 张适屏照片 / 39 张 3300 万像素照片",
            $"超限后张数未按所选内存：{settings.BitmapCacheCountInfo}");

        settings.BitmapCacheMaxMemory = 7000;
        problems += Require(settings.PerformanceBudgetInfo == systemLine + "（当前设置超过系统可用内存上限）",
            $"系统可用警告文案不对：{settings.PerformanceBudgetInfo}");
        problems += Require(settings.BitmapCacheCountInfo == "可缓存 186 张适屏照片 / 55 张 3300 万像素照片",
            $"超可用内存后张数未按所选内存：{settings.BitmapCacheCountInfo}");

        settings.BitmapCacheMaxMemory = 9000;
        problems += Require(settings.PerformanceBudgetInfoLevel == PerfInfoLevel.Error,
            $"超过应用预算未判错误级：{settings.PerformanceBudgetInfoLevel}");
        problems += Require(settings.PerformanceBudgetInfo == systemLine + "（当前设置超过应用内存上限）",
            $"应用内存警告文案不对：{settings.PerformanceBudgetInfo}");

        settings.BitmapCacheMaxMemory = 20000;
        problems += Require(settings.PerformanceBudgetInfo == systemLine + "（当前设置超过系统总内存上限）",
            $"系统总内存警告文案不对：{settings.PerformanceBudgetInfo}");
        problems += Require(settings.BitmapCacheCountInfo == "可缓存 533 张适屏照片 / 158 张 3300 万像素照片",
            $"超总内存后张数未按所选内存：{settings.BitmapCacheCountInfo}");

        settings.BitmapCacheMaxMemory = 2048;
        problems += Require(settings.PerformanceBudgetInfoLevel == PerfInfoLevel.Normal,
            $"回到范围内未消退警告：{settings.PerformanceBudgetInfoLevel}");
        problems += Require(settings.PerformanceBudgetInfo == systemLine, "警告消退后文案未恢复");
        problems += Require(settings.BitmapCacheCountInfo == defaultCounts, "警告消退改变了张数");

        // 自动管理缓存的平台（iPad / Android）：按设备最大安全容量（4505 MB）算张数，隐藏项改动无影响、不告警。
        BitmapLoader.AutomaticCacheLimit = true;
        try
        {
            using var hidden = await DraftContext.CreateAsync(defaults);
            const string deviceCounts = "可缓存 120 张适屏照片 / 35 张 3300 万像素照片";
            problems += Require(!hidden.Settings.ShowsCacheLimits, "自动口径未隐藏两项上限");
            problems += Require(hidden.Settings.BitmapCacheCountInfo == deviceCounts,
                $"自动口径张数不是设备最大安全容量：{hidden.Settings.BitmapCacheCountInfo}");

            hidden.Settings.BitmapCacheMaxMemory = 20000;
            problems += Require(hidden.Settings.BitmapCacheCountInfo == deviceCounts,
                $"隐藏的上限改变了张数：{hidden.Settings.BitmapCacheCountInfo}");
            problems += Require(hidden.Settings.PerformanceBudgetInfoLevel == PerfInfoLevel.Normal,
                $"隐藏项仍告警：{hidden.Settings.PerformanceBudgetInfoLevel}");
            problems += Require(hidden.Settings.PerformanceBudgetInfo == systemLine, "隐藏项改动了第一行");
        }
        finally
        {
            BitmapLoader.AutomaticCacheLimit = false;
        }

        // 没有平台 budget 但用户内存已知：仍按用户上限给张数，未知读数不告警。
        using var noBudget = new MemoryProviderScope(new FixedMemoryProvider(0, -1, 0));
        using var unknownContext = await DraftContext.CreateAsync(defaults);
        problems += Require(unknownContext.Settings.PerformanceBudgetInfo == "系统内存: 总 未知 / 当前可用 未知",
            $"未知读数未显式未知：{unknownContext.Settings.PerformanceBudgetInfo}");
        problems += Require(unknownContext.Settings.BitmapCacheCountInfo == defaultCounts,
            $"无预算时未按用户上限给张数：{unknownContext.Settings.BitmapCacheCountInfo}");
        problems += Require(unknownContext.Settings.PerformanceBudgetInfoLevel == PerfInfoLevel.Normal,
            $"未知读数误判告警：{unknownContext.Settings.PerformanceBudgetInfoLevel}");

        Console.WriteLine($"设备能力提示 -> {systemLine} ;; {defaultCounts}；内存比例/数量无关/超限/自动/无预算已验");
        return problems;
    }

    /// <summary>
    /// 完整照片档必须按 Normal 压力的展示编码字节数换算：注入会随压力降档的编码策略后，
    /// 当前压力只影响准入口径，能力估算张数不变。
    /// </summary>
    private static async Task<int> CheckNormalPressureBytesPerPixelAsync()
    {
        var problems = 0;
        try
        {
            BitmapLoader.InitializePresentationDecoder(new NullPresentationDecoder(), new PressureSensitiveEncodingPolicy());
        }
        catch (Exception ex)
        {
            Console.WriteLine("  FAIL 无法注入展示解码器：" + ex.Message);
            return 1;
        }

        problems += Require(BitmapLoader.ProxyBytesPerPixel == 8, $"代理档 Normal 每像素字节不是 F16（8）：{BitmapLoader.ProxyBytesPerPixel}");
        problems += Require(BitmapLoader.NormalFullBytesPerPixel == 8, $"完整档 Normal 每像素字节不是 F16（8）：{BitmapLoader.NormalFullBytesPerPixel}");

        var budget = new WindowsLikeBudget(8192, null);
        PerformanceBudget.Initialize(budget);
        var defaults = SettingsDefaults.CreateDefault(budget, appleKeyboard: false);

        using var provider = new MemoryProviderScope(new FixedMemoryProvider(
            totalBytes: 16L * 1024 * 1024 * 1024,
            availableBytes: 6L * 1024 * 1024 * 1024,
            budgetBytes: 8L * 1024 * 1024 * 1024));

        var previous = PressureLevelField.GetValue(null);
        PressureLevelField.SetValue(null, PressureLevel.Warning);
        int admissionBytes;
        try
        {
            problems += Require(BitmapLoader.PresentationBytesPerPixel == 4,
                $"压力降档后准入字节未变：{BitmapLoader.PresentationBytesPerPixel}");
            problems += Require(BitmapLoader.NormalFullBytesPerPixel == 8,
                $"能力估算跟随了当前压力：{BitmapLoader.NormalFullBytesPerPixel}");
            admissionBytes = BitmapLoader.PresentationBytesPerPixel;

            using var context = await DraftContext.CreateAsync(defaults);
            const string expected = "可缓存 27 张适屏照片 / 8 张 3300 万像素照片";
            problems += Require(context.Settings.BitmapCacheCountInfo == expected,
                $"压力级别改变了能力估算：{context.Settings.BitmapCacheCountInfo}");
        }
        finally
        {
            PressureLevelField.SetValue(null, previous);
        }

        Console.WriteLine(
            $"Normal 口径 -> 用户 2048 MB、F16 适屏 27 / 完整 8；压力 Warning 时准入 {admissionBytes} B，"
            + "能力估算仍按 8 B");
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

    /// <summary>固定读数的预算源替身：总内存/可用量/预算由用例给定，不走平台接口。</summary>
    private sealed class FixedMemoryProvider(long totalBytes, long availableBytes, long budgetBytes) : IMemoryBudgetProvider
    {
        public SystemMemoryInfo GetSystemMemoryInfo() => new(totalBytes, availableBytes);

        public long GetBudgetBytes() => budgetBytes;

        public long GetAvailableBytes() => availableBytes;

        public long GetFootprintBytes() => 0;
    }

    /// <summary>把预算源临时注入 MemoryGovernor（不启动探测计时器、不写内存事件日志），退出时还原。</summary>
    private sealed class MemoryProviderScope : IDisposable
    {
        private readonly object? _previous;

        public MemoryProviderScope(IMemoryBudgetProvider provider)
        {
            _previous = MemoryProviderField.GetValue(null);
            MemoryProviderField.SetValue(null, provider);
        }

        public void Dispose() => MemoryProviderField.SetValue(null, _previous);
    }

    /// <summary>不会真正解码的展示解码器替身：只用于让编码协商生效。</summary>
    private sealed class NullPresentationDecoder : IPresentationImageDecoder
    {
        public Task<DecodedImage?> DecodeAsync(
            PhotoViewer.Core.Storage.IFileRef file, int? maxLongEdge, PixelEncoding encoding,
            System.Threading.CancellationToken cancellationToken)
            => throw new NotSupportedException("SettingsDefaultsCheck 不解码");

        public Task<DecodedImage?> DecodeThumbnailAsync(
            PhotoViewer.Core.Storage.IFileRef file, int minShortEdge, System.Threading.CancellationToken cancellationToken)
            => throw new NotSupportedException("SettingsDefaultsCheck 不解码");
    }

    /// <summary>
    /// 会随压力降档的编码策略：完整档 Normal 为 F16、Warning 起降 8 位，代理档恒 F16。
    /// 用来验证设备能力估算固定取 Normal 压力字节数，不跟随当前压力漂移。
    /// </summary>
    private sealed class PressureSensitiveEncodingPolicy : IPresentationEncodingPolicy
    {
        public PixelEncoding Select(ImageSizeClass sizeClass, PressureLevel pressure)
            => sizeClass == ImageSizeClass.Full && pressure >= PressureLevel.Warning
                ? PixelEncoding.Rgba8
                : PixelEncoding.RgbaF16;
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

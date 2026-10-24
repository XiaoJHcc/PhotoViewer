using System;
using System.Reactive.Concurrency;
using System.Text.Json;
using System.Threading.Tasks;
using PhotoViewer.Core.Image;
using PhotoViewer.Core.Platform;
using PhotoViewer.Core.Settings;
using PhotoViewer.ViewModels.Settings;

namespace SettingsDefaultsCheck;

/// <summary>
/// 用假预算源建全新 <see cref="SettingsViewModel"/>，核对缓存与预载默认值：
/// 预载数不得超过预载上限，前后预载 + 当前 + 对比不得超过缓存张数（SETTINGS-06）。
/// 同时核对已保存的设置不被默认值覆盖。
/// </summary>
internal static class Program
{
    private static int Main()
    {
        // 与壳启动一致：先准备 ReactiveUI（主线程调度器 + 核心服务），MessageBus 与 WhenAnyValue 依赖它。
        var dispatcher = new InlineDispatcher();
        UiDispatcher.Initialize(dispatcher);
        var reactiveBuilder = ReactiveUI.Builder.RxAppBuilder.CreateReactiveUIBuilder();
        reactiveBuilder.WithCoreServices();
        reactiveBuilder.WithMainThreadScheduler(dispatcher.MainThreadScheduler);
        reactiveBuilder.BuildApp();
        BitmapLoader.ProxyTargetLongEdge = 3840; // 与 Windows 壳的代理长边同口径

        var failed = 0;
        foreach (var physicalMB in new[] { 4096, 8192, 16384, 32768, 65536 })
        {
            failed += CheckFresh(physicalMB, null);
        }

        // 张数达到基准（30）时默认份数必须与旧口径逐字节一致；更多张数同样保持 10/5/5。
        failed += CheckFresh(8192, 30);
        failed += CheckFresh(8192, 45);

        failed += CheckSaved();

        Console.WriteLine(failed == 0 ? "ALL PASS" : $"{failed} FAIL");
        return failed == 0 ? 0 : 1;
    }

    private static int CheckFresh(int physicalMB, int? countOverride)
    {
        PerformanceBudget.Initialize(new WindowsLikeBudget(physicalMB, countOverride));
        using var settings = new SettingsViewModel(new TransientSettings(), null, applyRuntimeSettings: false);
        DrainLoad();

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

        // 缓存张数达到基准时，默认份数必须与旧口径（10 / 5 / 5）逐字节一致。
        if (settings.BitmapCacheMaxCount >= 30)
        {
            problems += Require(settings.PreloadForwardCount == 10, $"张数 ≥ 30 时前预载不是 10: {settings.PreloadForwardCount}");
            problems += Require(settings.PreloadBackwardCount == 5, $"张数 ≥ 30 时后预载不是 5: {settings.PreloadBackwardCount}");
            problems += Require(settings.VisibleCenterPreloadCount == 5,
                $"张数 ≥ 30 时列表滚动预载不是 5: {settings.VisibleCenterPreloadCount}");
        }

        // 未达到基准时按同一比例收敛，且后预载不多于前预载。
        if (settings.BitmapCacheMaxCount < 30)
        {
            problems += Require(settings.PreloadBackwardCount <= settings.PreloadForwardCount,
                "后预载多于前预载");
            problems += Require(settings.PreloadForwardCount > 0, "前预载默认值为 0");
        }

        return problems;
    }

    private static int CheckSaved()
    {
        PerformanceBudget.Initialize(new WindowsLikeBudget(8192, null));
        var model = new SettingsModel
        {
            BitmapCacheMaxCount = 40,
            PreloadForwardCount = 7,
            PreloadBackwardCount = 3,
            VisibleCenterPreloadCount = 4,
            BitmapCacheMaxMemory = 3000,
            Hotkeys =
            [
                JsonSerializer.Deserialize<HotkeyModel>(
                    """{"Name":"增强预览","Command":"ToggleEnhance","DisplaySymbol":"old","Tooltip":"增强预览","IsDisplay":true}""")!,
            ],
        };
        using var settings = new SettingsViewModel(new TransientSettings(), model, applyRuntimeSettings: false);
        DrainLoad();

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
        string savedHotkey = JsonSerializer.Serialize(model.Hotkeys[0]);
        problems += Require(!savedHotkey.Contains("Name", StringComparison.Ordinal)
            && !savedHotkey.Contains("DisplaySymbol", StringComparison.Ordinal)
            && !savedHotkey.Contains("Tooltip", StringComparison.Ordinal), "快捷键展示元数据仍被持久化");
        return problems;
    }

    private static void DrainLoad()
    {
        // 构造里的 LoadSettingsAsync 在后台跑完才算"启动完毕"；默认模型全为 0，不会覆盖预算默认值。
        for (var attempt = 0; attempt < 50; attempt++)
        {
            Task.Delay(10).GetAwaiter().GetResult();
        }
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

    /// <summary>不落盘的设置服务。</summary>
    private sealed class TransientSettings : ISettingsService
    {
        public string? LastLoadError => null;

        public string? LastSaveError => null;

        public Task<SettingsModel> LoadAsync() => Task.FromResult(new SettingsModel());

        public Task SaveAsync(SettingsModel model) => Task.CompletedTask;
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

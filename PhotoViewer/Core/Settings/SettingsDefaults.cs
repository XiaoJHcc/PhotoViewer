using PhotoViewer.Core.Input;
using PhotoViewer.Core.Platform;

namespace PhotoViewer.Core.Settings;

/// <summary>按平台性能预算生成首次运行的默认设置。</summary>
public static class SettingsDefaults
{
    private const int AutomaticPreloadMaximum = 10;
    private const int DefaultPreloadBaselineCount = 30;
    private const int DefaultPreloadForwardCount = 10;
    private const int DefaultPreloadBackwardCount = 5;
    private const int DefaultPreloadCenterCount = 5;
    private const int MinimumCacheMemoryMB = 512;
    private const int MinimumParallelism = 1;
    private const int MaximumParallelism = 32;

    /// <summary>
    /// 生成默认快照。缓存内存/张数与解码并发取 <paramref name="budget"/> 的推荐值；
    /// 预载份数按推荐张数换算（基准 30 张时为上一张 10 / 下一张 5 / 列表滚动 5）。
    /// </summary>
    /// <param name="budget">平台性能预算源。</param>
    /// <param name="appleKeyboard">是否默认启用苹果键盘映射。</param>
    /// <param name="automaticCacheLimit">缓存容量是否由平台实测预算自动决定（iPad / Android 自动口径）。</param>
    public static AppSettings CreateDefault(
        IPerformanceBudget budget,
        bool appleKeyboard,
        bool automaticCacheLimit = false)
    {
        ArgumentNullException.ThrowIfNull(budget);

        int cacheMemoryMB;
        int cacheCount;
        int nativeParallelism;
        int cpuParallelism;
        try
        {
            cacheMemoryMB = Math.Max(MinimumCacheMemoryMB, budget.GetRecommendedCacheMemoryMB());
            cacheCount = Math.Max(1, budget.GetRecommendedCacheCount(cacheMemoryMB));
            nativeParallelism = budget.GetRecommendedNativePreloadParallelism(cacheMemoryMB);
            cpuParallelism = budget.GetRecommendedCpuPreloadParallelism(cacheMemoryMB);
        }
        catch (Exception)
        {
            cacheMemoryMB = 2048;
            cacheCount = 30;
            nativeParallelism = Math.Min(Math.Max(1, Environment.ProcessorCount), 8);
            cpuParallelism = 1;
        }

        nativeParallelism = Math.Clamp(nativeParallelism, MinimumParallelism, MaximumParallelism);
        cpuParallelism = Math.Clamp(cpuParallelism, MinimumParallelism, MaximumParallelism);

        var preloadMaximum = automaticCacheLimit
            ? AutomaticPreloadMaximum
            : Math.Max(0, cacheCount / 3);

        int ScalePreload(int baseline)
            => automaticCacheLimit || cacheCount >= DefaultPreloadBaselineCount
                ? Math.Min(baseline, preloadMaximum)
                : Math.Clamp(
                    (int)Math.Round(baseline * (double)cacheCount / DefaultPreloadBaselineCount),
                    0,
                    preloadMaximum);

        return new AppSettings
        {
            LayoutMode = LayoutMode.Auto,
            ShowZoomIndicator = true,
            ScalePresets = SettingsCatalog.DefaultScalePresets,
            ShowRating = true,
            SafeSetRating = true,
            SameNameAsOnePhoto = true,
            FileFormats = SettingsCatalog.DefaultFileFormats,
            ExifFields = SettingsCatalog.DefaultExifFields,
            Hotkeys = SettingsCatalog.DefaultHotkeys,
            UseAppleKeyboard = appleKeyboard,
            MapCommandTarget = AppleMappingTarget.Ctrl,
            MapOptionTarget = AppleMappingTarget.Alt,
            MapControlTarget = AppleMappingTarget.Ctrl,
            SimilarityThreshold = 0.85,
            SimilarityMaxResults = 8,
            AnalysisImmediateCompute = false,
            BitmapCacheMaxCount = cacheCount,
            BitmapCacheMaxMemory = cacheMemoryMB,
            PreloadForwardCount = ScalePreload(DefaultPreloadForwardCount),
            PreloadBackwardCount = ScalePreload(DefaultPreloadBackwardCount),
            VisibleCenterPreloadCount = ScalePreload(DefaultPreloadCenterCount),
            VisibleCenterDelayMs = 1000,
            NativePreloadParallelism = nativeParallelism,
            CpuPreloadParallelism = cpuParallelism,
        };
    }
}

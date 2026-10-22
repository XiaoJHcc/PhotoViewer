using System;
using Android.App;
using Android.Content;
using PhotoViewer.Core.Memory;
using PhotoViewer.Core.Platform;

namespace PhotoViewer.Android.Core;

/// <summary>
/// Android 平台性能预算实现。
/// </summary>
public sealed class AndroidPerformanceBudget : IPerformanceBudget
{
    /// <summary>
    /// 获取应用可用的内存上限（MB）。
    /// </summary>
    /// <remarks>
    /// 取 <see cref="AndroidMemoryBudgetProvider"/> 的前台可见上限，与缓存推导、设置提示同一口径；
    /// 设备总内存（旧口径）会高估到被 lmkd 回收的程度，只在预算源读不到 <c>MemTotal</c> 时兜底。
    /// </remarks>
    public int GetAppMemoryLimitMB()
    {
        long budget = MemoryGovernor.BudgetBytes;
        if (budget <= 0) budget = new AndroidMemoryBudgetProvider().GetBudgetBytes();
        if (budget > 0) return (int)Math.Max(0, budget / (1024 * 1024));
        return GetTotalMemoryMB();
    }

    /// <summary>
    /// 获取当前平台可用于解码任务的 CPU 并行能力。
    /// </summary>
    public int GetCpuCoreCount()
        => Math.Max(1, Environment.ProcessorCount);

    /// <summary>
    /// 获取原生解码路径建议的最大并行预载线程数。
    /// </summary>
    public int GetNativePreloadThreadLimit()
        => Math.Min(GetCpuCoreCount(), 4);

    /// <summary>
    /// 获取 CPU 解码路径建议的最大并行预载线程数。
    /// </summary>
    public int GetCpuPreloadThreadLimit()
        => 1;

    /// <summary>
    /// 缓存内存上限的推荐值（MB）：前台可见预算的 50%，夹在 512–4096。
    /// 缓存容量实际由实测预算自动推导（<c>BitmapLoader.AutomaticCacheLimit</c>），这里只给内部字段一个自洽的值。
    /// </summary>
    public int GetRecommendedCacheMemoryMB()
        => Math.Max(512, Math.Min(GetAppMemoryLimitMB() / 2, 4096));

    /// <summary>
    /// 缓存数量上限的推荐值（张）：按单张 2400 万像素 8 位代理（约 96 MB）折算，夹在 3–30。
    /// </summary>
    /// <param name="cacheMemoryMB">缓存内存上限（MB）。</param>
    public int GetRecommendedCacheCount(int cacheMemoryMB)
        => Math.Clamp(cacheMemoryMB / 96, 3, 30);

    /// <summary>
    /// 原生解码路径的推荐预载并行度：6 核及以上 2 路，否则 1 路。
    /// 手机核数再多也以内存与热约束为先，多路 Full 解码只涨驻留不涨吞吐。
    /// </summary>
    /// <param name="cacheMemoryMB">缓存内存上限（MB）。</param>
    public int GetRecommendedNativePreloadParallelism(int cacheMemoryMB)
        => GetCpuCoreCount() >= 6 ? 2 : 1;

    /// <summary>
    /// CPU 解码路径的推荐预载并行度：恒为 1。
    /// </summary>
    /// <param name="cacheMemoryMB">缓存内存上限（MB）。</param>
    public int GetRecommendedCpuPreloadParallelism(int cacheMemoryMB) => 1;

    /// <summary>设备物理内存（MB），仅作预算源不可用时的兜底。</summary>
    private static int GetTotalMemoryMB()
    {
        try
        {
            if (Application.Context.GetSystemService(Context.ActivityService) is ActivityManager activityManager)
            {
                var memoryInfo = new ActivityManager.MemoryInfo();
                activityManager.GetMemoryInfo(memoryInfo);
                return (int)Math.Max(0, memoryInfo.TotalMem / (1024 * 1024));
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to get Android performance budget memory limit: {ex.Message}");
        }

        return 0;
    }
}

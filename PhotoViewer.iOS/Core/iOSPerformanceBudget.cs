using System;
using PhotoViewer.Core.Platform;

namespace PhotoViewer.iOS.Core;

/// <summary>
/// iOS 平台性能预算实现。内存上限按 jetsam 实测给出:phys_footprint + os_proc_available_memory,
/// 不再维护机型表(未在表内的新机型曾一律落到保守回退值)。
/// </summary>
public sealed class iOSPerformanceBudget : IPerformanceBudget
{
    /// <summary>
    /// 记录上次系统内存告警前缓存大小（MB）。
    /// </summary>
    public static int LastMemoryWarningCacheMB { get; private set; }

    /// <summary>
    /// 在系统内存告警到来前记录当前缓存大小。
    /// </summary>
    public static void RecordMemoryWarningCacheMB(long bytes)
        => LastMemoryWarningCacheMB = (int)Math.Max(0, bytes / (1024 * 1024));

    /// <summary>
    /// 获取应用可用的内存上限（MB）：与内存管家同一口径（<see cref="iOSMemoryBudgetProvider.GetBudgetBytes"/>），
    /// 即当前驻留 + 距 jetsam 余量；API 失效时同样回退物理内存 × 50%，设置页显示值与缓存推导预算一致。
    /// </summary>
    public int GetAppMemoryLimitMB()
    {
        long budget = new iOSMemoryBudgetProvider().GetBudgetBytes();
        return budget > 0 ? (int)Math.Min(int.MaxValue, budget / (1024 * 1024)) : 1024;
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
    public int GetCpuPreloadThreadLimit() => 1;
}

using System;
using Foundation;
using PhotoViewer.Apple;
using PhotoViewer.Core.Memory;

namespace PhotoViewer.iOS.Core;

/// <summary>
/// iOS 内存预算提供者:以系统 jetsam 口径实测。
/// 预算 = phys_footprint(当前驻留) + os_proc_available_memory(距杀进程余量),footprint 含 Metal 纹理与 IOSurface;
/// 可用 = os_proc_available_memory。API 失效时回退"物理内存 × 50% − 当前驻留"。
/// 实测读法在 <see cref="AppleMemoryFootprint"/>,与 macOS 预算源共用。
/// </summary>
public sealed class iOSMemoryBudgetProvider : IMemoryBudgetProvider
{
    /// <summary>API 失效时的回退预算占物理内存的比例。</summary>
    private const double FallbackBudgetRatio = 0.5;

    /// <summary>距 jetsam 上限的余量字节数;失效时回退"物理内存 × 50% − 当前驻留"。</summary>
    public long GetAvailableBytes()
    {
        long avail = AppleMemoryFootprint.GetAvailableBytes();
        if (avail > 0) return avail;
        return Math.Max(0, FallbackBudgetBytes() - AppleMemoryFootprint.GetPhysFootprintBytes());
    }

    /// <summary>预算 = 当前驻留 + 距 jetsam 余量(实测动态值);双路失效时回退物理内存 × 50%。</summary>
    public long GetBudgetBytes()
    {
        long footprint = AppleMemoryFootprint.GetPhysFootprintBytes();
        long avail = GetAvailableBytes();
        long budget = footprint + avail;
        return budget > 0 ? budget : FallbackBudgetBytes();
    }

    /// <summary>当前进程驻留内存(phys_footprint,字节);失败返回 0。验收内存探针复用同一口径。</summary>
    public long GetFootprintBytes() => AppleMemoryFootprint.GetPhysFootprintBytes();

    /// <summary>
    /// 系统物理内存:总量取 <see cref="NSProcessInfo.PhysicalMemory"/>,可用量取内核 free + inactive 页估算并夹到总量。
    /// 这是整机口径,不用 os_proc_available_memory 或进程预算冒充;页统计读取失败时可用量如实返回 -1(未知)。
    /// </summary>
    public SystemMemoryInfo GetSystemMemoryInfo()
    {
        try
        {
            long total = (long)NSProcessInfo.ProcessInfo.PhysicalMemory;
            if (total <= 0) return SystemMemoryInfo.Unknown;
            long available = AppleMemoryFootprint.GetSystemAvailableBytes();
            return new SystemMemoryInfo(total, available < 0 ? -1 : Math.Min(available, total));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[iOSMemoryBudget] PhysicalMemory failed: {ex.Message}");
            return SystemMemoryInfo.Unknown;
        }
    }

    /// <summary>回退预算:物理内存 × 50%(不再依赖机型表)。</summary>
    private static long FallbackBudgetBytes()
    {
        try
        {
            long physical = (long)NSProcessInfo.ProcessInfo.PhysicalMemory;
            return physical > 0 ? (long)(physical * FallbackBudgetRatio) : 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[iOSMemoryBudget] PhysicalMemory failed: {ex.Message}");
            return 0;
        }
    }
}

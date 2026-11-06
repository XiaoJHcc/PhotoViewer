using System;
using Foundation;
using PhotoViewer.Apple;
using PhotoViewer.Core.Memory;

namespace PhotoViewer.Mac.Core;

/// <summary>
/// macOS 内存预算提供者:预算 = 物理内存 × 50%,可用 = 预算 − 进程驻留(phys_footprint)。
/// 与 iOS 共用 <see cref="AppleMemoryFootprint"/> 的读法:footprint 含 Metal/IOSurface,
/// 缓存因此能把非缓存占用从预算里扣掉;桌面无硬杀,该口径主要服务缓存容量推导与可观测性。
/// </summary>
public sealed class MacMemoryBudgetProvider : IMemoryBudgetProvider
{
    /// <summary>预算占物理内存的比例。</summary>
    private const double BudgetRatio = 0.5;

    /// <summary>预算 = 物理内存 × 50%;读不到物理内存时为 0(Governor 退化为恒 Normal)。</summary>
    public long GetBudgetBytes()
    {
        try
        {
            long physical = (long)NSProcessInfo.ProcessInfo.PhysicalMemory;
            return physical > 0 ? (long)(physical * BudgetRatio) : 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[MacMemoryBudget] PhysicalMemory failed: {ex.Message}");
            return 0;
        }
    }

    /// <summary>可用 = 预算 − 当前进程驻留(phys_footprint);驻留读不到时视为无压力。</summary>
    public long GetAvailableBytes()
    {
        long budget = GetBudgetBytes();
        if (budget <= 0) return long.MaxValue;
        long footprint = GetFootprintBytes();
        if (footprint <= 0) return long.MaxValue;
        return Math.Max(0, budget - footprint);
    }

    /// <summary>当前进程驻留(phys_footprint,含 GPU 纹理等非托管占用)。</summary>
    public long GetFootprintBytes() => AppleMemoryFootprint.GetPhysFootprintBytes();

    /// <summary>
    /// 系统物理内存:总量取 <see cref="NSProcessInfo.PhysicalMemory"/>,可用量取内核 free + inactive 页估算并夹到总量。
    /// 这是整机口径,不取本进程预算或 os_proc_available_memory;页统计读取失败时可用量如实返回 -1(未知)。
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
            Console.WriteLine($"[MacMemoryBudget] PhysicalMemory failed: {ex.Message}");
            return SystemMemoryInfo.Unknown;
        }
    }
}

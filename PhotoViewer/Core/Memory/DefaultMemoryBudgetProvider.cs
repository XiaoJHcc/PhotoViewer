using System;

namespace PhotoViewer.Core.Memory;

/// <summary>
/// 桌面平台默认预算提供者(Windows / macOS):以 CLR 可见内存总量为基准。
/// 桌面无 jetsam 式硬杀,压力判定主要服务可观测性与预取节制,比例口径即可;
/// 真机平台(iOS/Android)使用各自的实测提供者。
/// </summary>
public sealed class DefaultMemoryBudgetProvider : IMemoryBudgetProvider
{
    /// <summary>桌面预算占物理内存的比例。</summary>
    private const double BudgetRatio = 0.5;

    /// <summary>
    /// 预算 = CLR 可见总内存(TotalAvailableMemoryBytes,物理内存或容器限额)× 50%。
    /// </summary>
    public long GetBudgetBytes()
    {
        try
        {
            long total = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
            return (long)(total * BudgetRatio);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[MemoryBudget] GetBudgetBytes failed: {ex.Message}");
            return 0;
        }
    }

    /// <summary>
    /// 可用 = 预算 − 当前托管占用。桌面端只看托管账本:大图缓冲全在托管堆(byte[]),
    /// 原生侧(Avalonia/Skia)压力不显著;低估是可接受的——压力大时 Governor 早抬级即可。
    /// </summary>
    public long GetAvailableBytes()
    {
        long budget = GetBudgetBytes();
        if (budget <= 0) return long.MaxValue;
        long used = GC.GetTotalMemory(false);
        return Math.Max(0, budget - used);
    }
}

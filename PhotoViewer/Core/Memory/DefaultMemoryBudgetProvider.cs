using System;

namespace PhotoViewer.Core.Memory;

/// <summary>
/// 兜底预算提供者:以 CLR 可见内存总量为基准,只减托管占用。
/// 产品壳各自注入实测预算源(Windows = <c>WindowsMemoryBudgetProvider</c>,macOS = <c>MacMemoryBudgetProvider</c>,
/// iOS/Android 用真机水位);本类留给工具与测试进程 —— 原生解码缓冲不进 GC 账本,拿它给产品壳记账会低估占用。
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
    /// 原生侧(Skia 位图)压力不显著;低估是可接受的——压力大时 Governor 早抬级即可。
    /// </summary>
    public long GetAvailableBytes()
    {
        long budget = GetBudgetBytes();
        if (budget <= 0) return long.MaxValue;
        long used = GetFootprintBytes();
        return Math.Max(0, budget - used);
    }

    /// <summary>与可用口径同源:托管堆实际占用(未强制回收,读数为保守估计)。</summary>
    public long GetFootprintBytes() => GC.GetTotalMemory(false);
}

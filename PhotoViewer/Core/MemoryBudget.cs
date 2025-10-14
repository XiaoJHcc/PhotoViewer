using System;

namespace PhotoViewer.Core;

public interface IMemoryBudget
{
    /// <summary>
    /// 获取应用可用的内存上限（MB）
    /// </summary>
    int GetAppMemoryLimitMB();
}

public static class MemoryBudget
{
    private static IMemoryBudget _budget = new DefaultMemoryBudget();
    
    // 由各平台启动时注入具体实现
    public static void Initialize(IMemoryBudget budget)
    {
        _budget = budget;
    }

    /// <summary>
    /// 获取应用可用的内存上限（MB）
    /// </summary>
    public static int AppMemoryLimitMB => _budget.GetAppMemoryLimitMB();
}


/// <summary>
/// 默认内存预算实现（适用于 Windows, macOS 平台）
/// </summary>
public sealed class DefaultMemoryBudget : IMemoryBudget
{
    public int GetAppMemoryLimitMB()
    {
        try
        {
            var memoryInfo = GC.GetGCMemoryInfo();
            var totalAvailableMemoryBytes = memoryInfo.TotalAvailableMemoryBytes;
            return (int)(totalAvailableMemoryBytes / (1024 * 1024));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to get memory budget: {ex.Message}");
            return 0;
        }
    }
}

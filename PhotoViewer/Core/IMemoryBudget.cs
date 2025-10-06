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
    public static void Initialize(IMemoryBudget decoder)
    {
        _budget = decoder ?? new DefaultMemoryBudget();
    }

    /// <summary>
    /// 获取应用可用的内存上限（MB）
    /// </summary>
    public static int AppMemoryLimitMB => _budget.GetAppMemoryLimitMB();
}


/// <summary>
/// 默认内存预算实现（适用于 Windows, macOS, Linux, Android 等平台）
/// </summary>
public sealed class DefaultMemoryBudget : IMemoryBudget
{
    public int GetAppMemoryLimitMB()
    {
        try
        {
            // 获取系统总内存
            var totalMemoryBytes = GC.GetTotalMemory(false);
            
            // 尝试使用 GC 获取更准确的可用内存信息
            var memoryInfo = GC.GetGCMemoryInfo();
            var totalAvailableMemoryBytes = memoryInfo.TotalAvailableMemoryBytes;
            if (totalAvailableMemoryBytes > 0)
                return (int)(totalAvailableMemoryBytes / (1024 * 1024));
            else
                return (int)(totalMemoryBytes / (1024 * 1024));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to get memory budget: {ex.Message}");
            return 0;
        }
    }
}

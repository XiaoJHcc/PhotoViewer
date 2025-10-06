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
/// 默认内存预算实现（适用于 Windows, macOS, Linux 等平台）
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
            {
                // 使用可用内存的 50% 作为应用内存上限
                var limitBytes = (long)(totalAvailableMemoryBytes * 0.5);
                var limitMB = (int)(limitBytes / (1024 * 1024));
                
                // 限制在合理范围内：最小 512MB，最大 8192MB
                return Math.Clamp(limitMB, 512, 8192);
            }
            
            // 回退方案：基于进程可用内存估算
            var workingSet = Environment.WorkingSet;
            if (workingSet > 0)
            {
                // 保守估算：工作集的 4 倍作为潜在可用内存
                var estimatedAvailable = workingSet * 4;
                var limitBytes = (long)(estimatedAvailable * 0.5);
                var limitMB = (int)(limitBytes / (1024 * 1024));
                
                return Math.Clamp(limitMB, 512, 8192);
            }
            
            // 最终回退：根据平台返回默认值
            if (OperatingSystem.IsWindows())
                return 4096; // Windows 默认 4GB
            else if (OperatingSystem.IsMacOS())
                return 3072; // macOS 默认 3GB
            else
                return 2048; // Linux 等其他平台默认 2GB
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to get memory budget: {ex.Message}");
            return 2048; // 异常时返回安全默认值 2GB
        }
    }
}

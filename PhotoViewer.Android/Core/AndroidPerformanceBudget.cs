using System;
using Android.App;
using Android.Content;
using PhotoViewer.Core;

namespace PhotoViewer.Android.Core;

/// <summary>
/// Android 平台性能预算实现。
/// </summary>
public sealed class AndroidPerformanceBudget : IPerformanceBudget
{
    /// <summary>
    /// 获取设备物理内存大小（MB）。
    /// </summary>
    public int GetAppMemoryLimitMB()
    {
        try
        {
            var activityManager = Application.Context.GetSystemService(Context.ActivityService) as ActivityManager;
            if (activityManager != null)
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
}
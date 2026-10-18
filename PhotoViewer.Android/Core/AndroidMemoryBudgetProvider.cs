using System;
using Android.App;
using Android.Content;
using PhotoViewer.Core.Memory;

namespace PhotoViewer.Android.Core;

/// <summary>
/// Android 内存预算提供者:按系统水位(ActivityManager.GetMemoryInfo)。
/// Android 没有 per-app jetsam 预算,LMK 按系统可用内存杀进程,故以系统 totalMem/availMem 为口径;
/// 另由 AndroidNativeActivity.OnTrimMemory 事件直报抬级补偿。
/// </summary>
public sealed class AndroidMemoryBudgetProvider : IMemoryBudgetProvider
{
    /// <summary>预算 = 系统总内存。</summary>
    public long GetBudgetBytes()
    {
        var info = ReadMemoryInfo();
        return info?.TotalMem ?? 0;
    }

    /// <summary>可用 = 系统可用内存(availMem)。</summary>
    public long GetAvailableBytes()
    {
        var info = ReadMemoryInfo();
        return info?.AvailMem ?? long.MaxValue;
    }

    private static ActivityManager.MemoryInfo? ReadMemoryInfo()
    {
        try
        {
            var am = (ActivityManager?)Application.Context.GetSystemService(Context.ActivityService);
            if (am == null) return null;
            var info = new ActivityManager.MemoryInfo();
            am.GetMemoryInfo(info);
            return info;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[AndroidMemoryBudget] GetMemoryInfo failed: {ex.Message}");
            return null;
        }
    }
}

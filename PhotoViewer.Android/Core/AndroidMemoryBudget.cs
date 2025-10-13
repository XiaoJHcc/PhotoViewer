using System;
using System.IO;
using Android.App;
using Android.Content;
using PhotoViewer.Core;

namespace PhotoViewer.Android.Core;

public class AndroidMemoryBudget : IMemoryBudget
{
    public int GetAppMemoryLimitMB()
    {
        try
        {
            var context = Application.Context;
            var am = (ActivityManager?)context.GetSystemService(Context.ActivityService);
            if (am != null)
            {
                var info = new ActivityManager.MemoryInfo();
                am.GetMemoryInfo(info);
                if (info.TotalMem > 0)
                    return (int)(info.TotalMem / (1024 * 1024));
            }

            // 降级读取 /proc/meminfo
            var totalKB = ReadMemInfoTotalKB();
            if (totalKB > 0)
                return (int)(totalKB / 1024);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"AndroidMemoryBudget error: {ex}");
        }
        return 0;
    }

    private static long ReadMemInfoTotalKB()
    {
        try
        {
            using var sr = new StreamReader("/proc/meminfo");
            string? line;
            while ((line = sr.ReadLine()) != null)
            {
                // 形如: "MemTotal:       12345678 kB"
                if (line.StartsWith("MemTotal", StringComparison.OrdinalIgnoreCase))
                {
                    var parts = line.Split(':');
                    if (parts.Length > 1)
                    {
                        var tokens = parts[1].Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                        if (tokens.Length > 0 && long.TryParse(tokens[0], out var kb))
                            return kb;
                    }
                    break;
                }
            }
        }
        catch
        {
            // 忽略并返回 0
        }
        return 0;
    }
}
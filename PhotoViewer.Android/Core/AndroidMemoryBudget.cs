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
            var activityManager = Application.Context.GetSystemService(Context.ActivityService) as ActivityManager;
            if (activityManager != null)
            {
                var memoryInfo = new ActivityManager.MemoryInfo();
                activityManager.GetMemoryInfo(memoryInfo);
                return (int)(memoryInfo.TotalMem / (1024 * 1024));
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to get Android memory budget: {ex.Message}");
        }

        return -1;
    }
}
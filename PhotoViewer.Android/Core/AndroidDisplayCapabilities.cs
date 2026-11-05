using System;
using Android.App;
using PhotoViewer.Core.Color;

namespace PhotoViewer.Android.Core;

public static class AndroidDisplayCapabilities
{
    public static bool DisplaySupportsP3 { get; private set; }
    public static bool ForceSrgb { get; set; }
    public static bool SurfaceIsP3 { get; set; }

    // 解码目标在表面销毁/回退期间保持稳定，上传时再按实际表面转换，缓存不会因后台预取丢失广色域。
    public static ImageColorSpace PresentationColorSpace
        => DisplaySupportsP3 ? ImageColorSpace.DisplayP3 : ImageColorSpace.Srgb;

    /// <summary>
    /// 从当前窗口读取显示色域能力；无窗口/旧系统一律按 sRGB。
    /// </summary>
    /// <param name="activity">宿主 Activity。</param>
    public static void Detect(Activity activity)
    {
        try
        {
            if (!OperatingSystem.IsAndroidVersionAtLeast(27))
            {
                DisplaySupportsP3 = false;
                return;
            }
            var display = activity.WindowManager?.DefaultDisplay;
            if (display == null)
            {
                DisplaySupportsP3 = false;
                return;
            }
            bool windowWide = activity.Window?.IsWideColorGamut == true;
            bool displayWide = display.IsWideColorGamut;
            DisplaySupportsP3 = displayWide && windowWide && !ForceSrgb;
            Console.WriteLine($"[Native] 色域探测：displayWide={displayWide} windowWide={windowWide} windowColorMode={activity.Window?.ColorMode}");
        }
        catch (Exception exception)
        {
            Console.WriteLine($"[Native] 显示色域探测失败，按 sRGB：{exception.Message}");
            DisplaySupportsP3 = false;
        }
    }
}

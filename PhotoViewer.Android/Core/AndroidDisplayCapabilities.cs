using System;
using Android.App;
using Android.OS;
using Android.Views;
using PhotoViewer.Core.Color;
using AndroidColorSpace = Android.Graphics.ColorSpace;

namespace PhotoViewer.Android.Core;

/// <summary>
/// Android 展示色彩能力探测（A2）：Display 是否支持 Display P3，以及当前 EGL 窗口表面是否真的以 P3 创建。
/// 解码器只在 <see cref="SurfaceIsP3"/> 为真时产出 P3 编码像素；EGL 协商失败时回退 sRGB8，
/// 避免把 P3 编码值当成 sRGB 显示造成过饱和。
/// </summary>
public static class AndroidDisplayCapabilities
{
    /// <summary>设备显示是否声明支持 Display P3（API 26+ 且 wide color gamut 包含 P3）。</summary>
    public static bool DisplaySupportsP3 { get; private set; }

    /// <summary>诊断用：强制按 sRGB 表面/解码（pv-srgb 启动参数）。</summary>
    public static bool ForceSrgb { get; set; }

    /// <summary>当前照片表面是否以 Display P3 显示；EGL 创建成功后由渲染器写入。</summary>
    public static bool SurfaceIsP3 { get; set; }

    /// <summary>展示解码应使用的色彩空间：表面是 P3 时解 P3，否则 sRGB。</summary>
    public static ImageColorSpace PresentationColorSpace
        => SurfaceIsP3 ? ImageColorSpace.DisplayP3 : ImageColorSpace.Srgb;

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
            DisplaySupportsP3 = (windowWide || displayWide) && !ForceSrgb;
            Console.WriteLine($"[Native] 色域探测：displayWide={displayWide} windowWide={windowWide} windowColorMode={activity.Window?.ColorMode}");
        }
        catch (Exception exception)
        {
            Console.WriteLine($"[Native] 显示色域探测失败，按 sRGB：{exception.Message}");
            DisplaySupportsP3 = false;
        }
    }
}

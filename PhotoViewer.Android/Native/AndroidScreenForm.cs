using System;
using Android.App;
using Android.Content;
using AndroidX.Window.Core.Layout;
using AndroidX.Window.Layout;
using PhotoViewer.Core.Settings;
using PhotoViewer.Shared;

namespace PhotoViewer.Android.Native;

/// <summary>
/// Android 主屏形态判定的原生输入（SHELL-07）：当前窗口尺寸取 <c>WindowMetricsCalculator</c> 的真实读数，
/// 两轴尺寸类别取 <c>WindowSizeClass</c>（AndroidX Window 的公开尺寸类别），设备类型取
/// <c>Configuration.SmallestScreenWidthDp &lt; 600</c> 的常规手机判定，再交给共享
/// <see cref="ScreenFormResolver"/> 出形态。判定结果只随窗口尺寸变化，不重建 Activity 或照片会话。
/// </summary>
internal static class AndroidScreenForm
{
    /// <summary>手机设备的常规判定阈值（dp）：最小宽度小于 600 即按手机处理（Android 官方尺寸类别口径）。</summary>
    internal const int PhoneSmallestWidthDp = 600;

    /// <summary>AndroidX 水平紧凑尺寸类别（官方断点 &lt;600dp）。</summary>
    private static WindowWidthSizeClass WidthClassCompact => WindowWidthSizeClass.Compact;

    /// <summary>AndroidX 垂直紧凑尺寸类别（官方断点 &lt;480dp）。</summary>
    private static WindowHeightSizeClass HeightClassCompact => WindowHeightSizeClass.Compact;

    /// <summary>当前窗口的两轴尺寸类别与手机设备标记。</summary>
    /// <param name="WidthDp">窗口宽（dp）。</param>
    /// <param name="HeightDp">窗口高（dp）。</param>
    /// <param name="HorizontalCompact">水平方向是否为紧凑尺寸类别。</param>
    /// <param name="VerticalCompact">垂直方向是否为紧凑尺寸类别。</param>
    /// <param name="PhoneDevice">设备本身是否手机。</param>
    internal readonly record struct Metrics(double WidthDp, double HeightDp, bool HorizontalCompact,
        bool VerticalCompact, bool PhoneDevice, string SizeClassText);

    /// <summary>
    /// 读取当前窗口的尺寸类别：窗口尺寸取 <c>WindowMetricsCalculator.ComputeCurrentWindowMetrics</c> 的
    /// <c>Bounds</c>（含系统栏的整窗，除以密度即 dp），尺寸类别取 <c>WindowSizeClass</c> 的两轴结果。
    /// </summary>
    /// <param name="activity">宿主 Activity。</param>
    /// <returns>形态判定输入。</returns>
    internal static Metrics Read(Activity activity)
    {
        double density = AndroidUiTheme.Density(activity);
        double width = 0, height = 0;
        try
        {
            var metrics = WindowMetricsCalculator.Companion!.OrCreate.ComputeCurrentWindowMetrics(activity);
            if (metrics?.Bounds is { } bounds)
            {
                width = bounds.Width() / density;
                height = bounds.Height() / density;
            }
        }
        catch (Exception exception)
        {
            Console.WriteLine($"[Native] WindowMetricsCalculator 不可用，回退窗口尺寸：{exception.Message}");
        }
        if (width < 1 || height < 1)
        {
            var metrics = activity.Resources?.DisplayMetrics;
            width = (metrics?.WidthPixels ?? 1) / density;
            height = (metrics?.HeightPixels ?? 1) / density;
        }

        bool horizontalCompact;
        bool verticalCompact;
        string text;
        if (TrySizeClasses(width, height, out horizontalCompact, out verticalCompact, out text))
        {
            // 已取得原生尺寸类别。
        }
        else
        {
            // 公开尺寸类别接口不可用时按同一官方面界回退（窗口 < 600dp 为紧凑）。
            horizontalCompact = width < 600;
            verticalCompact = height < 480;
            text = $"fallback w={(horizontalCompact ? "compact" : "expanded")} h={(verticalCompact ? "compact" : "expanded")}";
        }

        bool phoneDevice = IsPhoneDevice(activity);
        return new Metrics(width, height, horizontalCompact, verticalCompact, phoneDevice, text);
    }

    /// <summary>设备是否手机：配置的最小宽度小于 600dp（不读设备型号，不做品牌判断）。</summary>
    /// <param name="context">上下文。</param>
    /// <returns>手机设备返回 true。</returns>
    internal static bool IsPhoneDevice(Context context)
        => (context.Resources?.Configuration?.SmallestScreenWidthDp ?? int.MaxValue) < PhoneSmallestWidthDp;

    /// <summary>
    /// 共享形态判定：把原生尺寸类别、窗口尺寸、设备类型与用户布局模式交给 <see cref="ScreenFormResolver"/>。
    /// </summary>
    /// <param name="activity">宿主 Activity。</param>
    /// <param name="mode">用户布局模式（设置工作副本/当前生效值）。</param>
    /// <returns>主屏形态。</returns>
    internal static ScreenForm Resolve(Activity activity, LayoutMode mode)
    {
        var metrics = Read(activity);
        return ScreenFormResolver.Resolve(metrics.HorizontalCompact, metrics.VerticalCompact,
            metrics.WidthDp, metrics.HeightDp, metrics.PhoneDevice, mode, LayoutCapabilities.PhoneLayoutSupported);
    }

    /// <summary>
    /// Android 竖屏保留系统状态栏：共享耳区把顶行放进状态栏图标区域时，只把顶行、分析栏与顶栏
    /// 下移到状态栏下缘；水平值不变，其他槽位不动。横屏或耳区已在安全区下方时原样返回。
    /// </summary>
    /// <param name="slots">共享 <see cref="PhoneScreenLayout"/> 结果。</param>
    /// <param name="input">同一份槽位输入。</param>
    /// <returns>平台修正后的槽位。</returns>
    internal static PhoneLayout ApplyStatusBarTopRowShift(in PhoneLayout slots, in PhoneLayoutInput input)
    {
        if (input.Landscape || PhoneScreenLayout.PortraitExifShown(in input) || slots.Filter.Y >= input.Safe.Top)
            return slots;
        double delta = input.Safe.Top - slots.Filter.Y;
        return new PhoneLayout(
            Shift(slots.Filter, delta), Shift(slots.More, delta), slots.FilterMoreGroup, Shift(slots.Exif, delta),
            slots.ExifItems, slots.Navigation, slots.Rating, slots.Commands, slots.Filmstrip, Shift(slots.Analysis, delta),
            new LayoutRect(slots.TopBar.X, slots.TopBar.Y, slots.TopBar.Width, slots.TopBar.Height + delta),
            slots.BottomBar, slots.LeadingBar, slots.TrailingBar,
            new EdgeInsets(slots.PhotoAvoidance.Left, slots.PhotoAvoidance.Top + delta,
                slots.PhotoAvoidance.Right, slots.PhotoAvoidance.Bottom),
            slots.FilmstripPadding);
    }

    private static LayoutRect Shift(LayoutRect rect, double delta)
        => rect.Width > 0 && rect.Height > 0 ? new LayoutRect(rect.X, rect.Y + delta, rect.Width, rect.Height) : rect;

    /// <summary>当前窗口是否处于手机形态（设置页据此收窄控制页内容）。</summary>
    /// <param name="activity">宿主 Activity。</param>
    /// <param name="mode">用户布局模式。</param>
    /// <returns>手机形态返回 true。</returns>
    internal static bool IsPhoneForm(Activity activity, LayoutMode mode)
        => Resolve(activity, mode) is ScreenForm.PhonePortrait or ScreenForm.PhoneLandscape;

    /// <summary>
    /// 两轴尺寸类别：AndroidX <c>WindowSizeClass</c> 的 <c>Compute(widthDp, heightDp)</c>（以官方断点集）给出宽/高等级。
    /// 接口不可用或返回空时返回 false，由调用方按官方面界回退。
    /// </summary>
    /// <param name="widthDp">窗口宽（dp）。</param>
    /// <param name="heightDp">窗口高（dp）。</param>
    /// <param name="horizontalCompact">水平是否紧凑。</param>
    /// <param name="verticalCompact">垂直是否紧凑。</param>
    /// <param name="text">可读的等级文本（报告用）。</param>
    /// <returns>取到原生尺寸类别返回 true。</returns>
    private static bool TrySizeClasses(double widthDp, double heightDp, out bool horizontalCompact,
        out bool verticalCompact, out string text)
    {
        horizontalCompact = false;
        verticalCompact = false;
        text = string.Empty;
        try
        {
            var sizeClass = WindowSizeClass.Compute((float)widthDp, (float)heightDp);
            if (sizeClass == null) return false;
            var widthClass = sizeClass.WindowWidthSizeClass;
            var heightClass = sizeClass.WindowHeightSizeClass;
            // 尺寸类别是 Java 单例对象；绑定层的属性 getter 每次返回新的托管包装，必须按对象相等比较。
            horizontalCompact = WidthClassCompact.Equals(widthClass);
            verticalCompact = HeightClassCompact.Equals(heightClass);
            text = $"w={widthClass} h={heightClass}";
            return true;
        }
        catch (Exception exception)
        {
            Console.WriteLine($"[Native] WindowSizeClass 不可用：{exception.Message}");
            return false;
        }
    }
}

using Android.App;
using Android.Content.Res;
using Android.Views;
using AndroidX.Core.View;

namespace PhotoViewer.Android;

internal static class AndroidSystemBars
{
    internal static void Apply(Activity activity, bool? gestureNavigation = null)
    {
        if (activity.Window is not { } window) return;
        var decor = window.DecorView;
        var controller = WindowCompat.GetInsetsController(window, decor)!;
        controller.SystemBarsBehavior = WindowInsetsControllerCompat.BehaviorShowTransientBarsBySwipe;
        int status = WindowInsetsCompat.Type.StatusBars();
        int navigation = WindowInsetsCompat.Type.NavigationBars();
        if (activity.Resources?.Configuration?.Orientation != Orientation.Landscape)
        {
            controller.Show(status | navigation);
            return;
        }

        // SHELL-02：横屏只隐藏手势导航，不能把三键导航一起收走。
        var raw = decor.RootWindowInsets;
        var insets = raw == null ? null : WindowInsetsCompat.ToWindowInsetsCompat(raw, decor);
        if (gestureNavigation ?? IsGestureNavigation(activity, insets))
            controller.Hide(status | navigation);
        else
        {
            controller.Hide(status);
            controller.Show(navigation);
        }
    }

    internal static bool IsGestureNavigation(global::Android.Content.Context context, WindowInsetsCompat? insets)
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(29)) return false;
        try
        {
            // 部分系统把手势条也报告为 TappableElement。优先读系统实际导航模式，不依赖设备型号或栏高。
            int mode = global::Android.Provider.Settings.Secure.GetInt(context.ContentResolver, "navigation_mode", -1);
            if (mode is 0 or 1 or 2) return mode == 2;
        }
        catch (Java.Lang.SecurityException)
        {
            // 厂商不公开该设置时，仅按可读的 Insets 分类。
        }

        return insets != null
            && insets.GetInsetsIgnoringVisibility(WindowInsetsCompat.Type.NavigationBars())!.Bottom
                > insets.GetInsetsIgnoringVisibility(WindowInsetsCompat.Type.TappableElement())!.Bottom;
    }

    internal static bool IsStatusBarVisible(Activity activity)
    {
        var insets = activity.Window?.DecorView?.RootWindowInsets;
        return insets != null && WindowInsetsCompat.ToWindowInsetsCompat(insets)?.IsVisible(WindowInsetsCompat.Type.StatusBars()) == true;
    }
}

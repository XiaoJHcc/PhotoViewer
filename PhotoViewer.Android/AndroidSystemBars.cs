using Android.App;
using Android.Content.Res;
using Android.Views;
using AndroidX.Core.View;

namespace PhotoViewer.Android;

internal static class AndroidSystemBars
{
    internal static void Apply(Activity activity)
    {
        if (activity.Window == null) return;
        var controller = WindowCompat.GetInsetsController(activity.Window, activity.Window.DecorView)!;
        controller.SystemBarsBehavior = WindowInsetsControllerCompat.BehaviorShowTransientBarsBySwipe;
        if (activity.Resources?.Configuration?.Orientation == Orientation.Landscape)
            controller.Hide(WindowInsetsCompat.Type.StatusBars());
        else
            controller.Show(WindowInsetsCompat.Type.StatusBars());
    }

    internal static bool IsStatusBarVisible(Activity activity)
    {
        var insets = activity.Window?.DecorView?.RootWindowInsets;
        return insets != null && WindowInsetsCompat.ToWindowInsetsCompat(insets)?.IsVisible(WindowInsetsCompat.Type.StatusBars()) == true;
    }
}

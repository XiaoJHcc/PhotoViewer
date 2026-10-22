using System;
using System.Linq;
using UIKit;

namespace PhotoViewer.iOS.Core;

/// <summary>
/// iOS 前台控制器定位器。
/// 弹窗类平台能力（设置页、文档选择器）统一从这里拿宿主控制器，
/// 原生壳的根控制器挂在窗口根控制器下。
/// </summary>
internal static class iOSViewControllerLocator
{
    /// <summary>
    /// 获取当前可用于弹出界面或选择器的宿主控制器。
    /// </summary>
    /// <returns>前台可见的控制器；没有时返回 null。</returns>
    public static UIViewController? GetPresentingController()
    {
        var window = UIApplication.SharedApplication
            .ConnectedScenes
            .OfType<UIWindowScene>()
            .Where(scene =>
                scene.ActivationState == UISceneActivationState.ForegroundActive ||
                scene.ActivationState == UISceneActivationState.ForegroundInactive)
            .SelectMany(scene => scene.Windows)
            .FirstOrDefault(window => window.IsKeyWindow)
            ?? UIApplication.SharedApplication
                .ConnectedScenes
                .OfType<UIWindowScene>()
                .SelectMany(scene => scene.Windows)
                .FirstOrDefault();

        return window?.RootViewController;
    }

    /// <summary>
    /// 获取展示链顶部的控制器。
    /// </summary>
    /// <returns>当前最顶层的控制器；宿主不可用时返回 null。</returns>
    public static UIViewController? GetTopMostViewController()
    {
        var root = GetPresentingController();
        if (root == null)
        {
            return null;
        }

        var current = root;
        while (current.PresentedViewController != null)
        {
            current = current.PresentedViewController;
        }

        return current switch
        {
            UINavigationController navigationController => navigationController.VisibleViewController ?? navigationController,
            UITabBarController tabBarController => tabBarController.SelectedViewController ?? tabBarController,
            _ => current,
        };
    }
}

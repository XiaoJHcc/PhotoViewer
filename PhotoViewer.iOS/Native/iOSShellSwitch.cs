using Foundation;

namespace PhotoViewer.iOS.Native;

/// <summary>
/// 启动壳选择：Debug 默认原生壳，Release 默认旧壳（Avalonia）。
/// 显式参数优先于 NSUserDefaults 开关，NSUserDefaults 开关优先于编译期默认值。
/// </summary>
internal static class iOSShellSwitch
{
    /// <summary>旧壳开关的 NSUserDefaults 键名（<c>defaults write cc.xiaojh.PhotoViewer PVLegacyUI -bool YES</c>）。</summary>
    private const string LegacyUiDefaultsKey = "PVLegacyUI";

    /// <summary>
    /// 解析本次启动使用的 UIApplicationDelegate 类型。
    /// </summary>
    /// <param name="args">进程启动参数。</param>
    /// <returns>原生壳或旧壳的委托类型。</returns>
    public static Type SelectDelegateType(string[] args)
    {
        if (args.Contains("--legacy-ui", StringComparer.Ordinal))
        {
            return SelectLegacy();
        }

        if (args.Contains("--native", StringComparer.Ordinal))
        {
            return SelectNative();
        }

        if (NSUserDefaults.StandardUserDefaults.BoolForKey(LegacyUiDefaultsKey))
        {
            return SelectLegacy();
        }

#if DEBUG
        return SelectNative();
#else
        return SelectLegacy();
#endif
    }

    /// <summary>标记并返回原生壳委托类型。</summary>
    /// <returns>原生壳委托类型。</returns>
    private static Type SelectNative()
    {
        BootTrace.Mark("shell: native (UIKit)");
        return typeof(iOSNativeAppDelegate);
    }

    /// <summary>标记并返回旧壳委托类型（仅在旧壳分支执行，原生路径不会加载 Avalonia）。</summary>
    /// <returns>旧壳委托类型。</returns>
    private static Type SelectLegacy()
    {
        BootTrace.Mark("shell: legacy (Avalonia)");
        return typeof(AppDelegate);
    }
}

using Foundation;

namespace PhotoViewer.Mac.Native;

/// <summary>
/// 产品显示名取 .app 的 CFBundleDisplayName（构建期由根 Directory.Build.props 的
/// PhotoViewerDisplayName 写入中间 Info.plist），源码里不再另存一份产品名；
/// 诊断页文案按编译能力区分标准版与 AI 版。
/// </summary>
internal static class MacProductInfo
{
    public static string DisplayName { get; } = ResolveDisplayName();

    private static string ResolveDisplayName()
    {
        if (NSBundle.MainBundle.InfoDictionary?["CFBundleDisplayName"] is NSString displayName && displayName.Length > 0)
        {
            return displayName;
        }

#if PHOTOVIEWER_DINO
        return "PhotoViewer AI";
#else
        return "PhotoViewer";
#endif
    }

#if PHOTOVIEWER_DINO
    public const string DiagnosticName = "DINO 诊断";
    public const string DiagnosticSymbol = "sparkles";
#else
    public const string DiagnosticName = "CV 诊断";
    public const string DiagnosticSymbol = "waveform.path.ecg";
#endif
}

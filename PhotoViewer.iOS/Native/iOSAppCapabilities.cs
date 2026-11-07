namespace PhotoViewer.iOS.Native;

/// <summary>
/// 构建期发行能力：标准版不含 DINO/相似聚类，AI 版保留；不做运行时切换。
/// </summary>
internal static class iOSAppCapabilities
{
#if PHOTOVIEWER_DINO
    internal const bool HasDino = true;
#else
    internal const bool HasDino = false;
#endif

#if PHOTOVIEWER_DINO
    internal const string DiagnosticName = "DINO 诊断";
#else
    internal const string DiagnosticName = "CV 诊断";
#endif

    internal const string EditionName = HasDino ? "AI" : "standard";
}

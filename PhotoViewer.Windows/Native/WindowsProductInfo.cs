namespace PhotoViewer.Windows.Native;

/// <summary>
/// 发行版本事实(编译期常量,不是运行时设置,EDITION-01)：
/// 标准版 <c>PhotoViewer</c> 不含 DINO,AI 版 <c>PhotoViewer-AI</c> 内置完整 DINO。
/// 两个版本共用同一份用户设置、照片数据库与单实例身份(<see cref="WindowsSingleInstance"/> 的键不变),
/// 只有显示名、诊断工具页名与相关入口按版本收敛。
/// </summary>
internal static class WindowsProductInfo
{
#if PHOTOVIEWER_DINO
    /// <summary>窗口标题与产品名(与 macOS / Android / iOS 同口径)。</summary>
    internal const string DisplayName = "PhotoViewer-AI";

    /// <summary>诊断工具页名与直达菜单名。</summary>
    internal const string DiagnosticName = "DINO 诊断";

    /// <summary>工具窗口分类键(导航图标查表用)。</summary>
    internal const string DiagnosticCategoryKey = "Dino";

    /// <summary>发行版标识,写进验收报告。</summary>
    internal const string EditionName = "ai";

    /// <summary>当前产物是否内置 DINO。</summary>
    internal const bool HasDino = true;
#else
    /// <summary>窗口标题与产品名(标准版沿用原产品名)。</summary>
    internal const string DisplayName = "PhotoViewer";

    /// <summary>诊断工具页名:标准版保留三瓦片 CV 诊断,不得继续叫 DINO 诊断(EDITION-04)。</summary>
    internal const string DiagnosticName = "CV 诊断";

    /// <summary>工具窗口分类键(导航图标查表用)。</summary>
    internal const string DiagnosticCategoryKey = "Cv";

    /// <summary>发行版标识,写进验收报告。</summary>
    internal const string EditionName = "standard";

    /// <summary>当前产物是否内置 DINO。</summary>
    internal const bool HasDino = false;
#endif
}

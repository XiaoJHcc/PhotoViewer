namespace PhotoViewer.Core.Settings;

/// <summary>文件栏布局模式。</summary>
public enum LayoutMode
{
    /// <summary>文件栏在顶部，横向排列。</summary>
    Row,

    /// <summary>文件栏在侧边，纵向排列。</summary>
    Col,

    /// <summary>按屏幕方向自动选择。</summary>
    Auto,

    /// <summary>手机形态（只在实现了手机形态的平台可选；其他平台按 <see cref="Auto"/> 处理）。</summary>
    Phone,
}

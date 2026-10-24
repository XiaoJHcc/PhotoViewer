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
}

using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using ReactiveUI;

namespace PhotoViewer.ViewModels.Main;

/// <summary>
/// 分析栏列表项基类。同一列表里同时容纳两种瓦片:
/// - <see cref="AnalysisDetailItem"/> 渲染为 <see cref="PhotoViewer.Controls.DetailPreview"/> (中心/对焦点裁剪);
/// - <see cref="AnalysisDiagnosticItem"/> 渲染为 <see cref="PhotoViewer.Controls.DiagnosticTile"/> (PCA/Cosine/锐度/抖动)。
/// 风格上两者一致:正方形外框 + 灰色细边 + 圆角 6 + 左上角药丸标签,统一靠齐 DetailPreview。
/// </summary>
public abstract class AnalysisItem : ReactiveObject
{
    private string _shortLabel;
    /// <summary>左上角药丸标签;诊断瓦片会把参考点 / 判定文字折进此字段(已无底部标签)。</summary>
    public string ShortLabel
    {
        get => _shortLabel;
        set => this.RaiseAndSetIfChanged(ref _shortLabel, value);
    }

    /// <summary>构造,设定初始标签。</summary>
    protected AnalysisItem(string shortLabel)
    {
        _shortLabel = shortLabel;
    }
}

/// <summary>
/// 细节预览项(中心 / 对焦点)。直接对应原 <see cref="DetailPreviewItem"/> 的字段集合。
/// </summary>
public sealed class AnalysisDetailItem : AnalysisItem
{
    /// <summary>裁剪中心(归一化 [0,1])。对焦点项会随 EXIF 动态更新。</summary>
    private Point _center;
    public Point Center
    {
        get => _center;
        set => this.RaiseAndSetIfChanged(ref _center, value);
    }

    /// <summary>对焦框尺寸(归一化),非 null 时叠加绿色对焦框。仅对焦点项使用。</summary>
    private Size? _focusFrame;
    public Size? FocusFrame
    {
        get => _focusFrame;
        set => this.RaiseAndSetIfChanged(ref _focusFrame, value);
    }

    /// <summary>构造细节预览项。</summary>
    public AnalysisDetailItem(string shortLabel, Point center, Size? focusFrame = null) : base(shortLabel)
    {
        _center = center;
        _focusFrame = focusFrame;
    }
}

/// <summary>
/// 诊断瓦片项(PCA / Cosine / 锐度 / 抖动)。所有字段都从只读库出发的派生层现算,缓存缺失时 Source = null。
/// </summary>
public sealed class AnalysisDiagnosticItem : AnalysisItem
{
    /// <summary>主图(诊断热力图或 PCA-RGB)。null 表示未提取。</summary>
    private Bitmap? _source;
    public Bitmap? Source
    {
        get => _source;
        set => this.RaiseAndSetIfChanged(ref _source, value);
    }

    /// <summary>背景图(压暗原图缩略图;抖动瓦片使用)。</summary>
    private Bitmap? _backgroundSource;
    public Bitmap? BackgroundSource
    {
        get => _backgroundSource;
        set => this.RaiseAndSetIfChanged(ref _backgroundSource, value);
    }

    /// <summary>覆盖层控件(抖动瓦片承载 ShakeFieldView)。整个生命周期一个固定实例,通过其 DP 更新数据。</summary>
    private Control? _overlay;
    public Control? Overlay
    {
        get => _overlay;
        set => this.RaiseAndSetIfChanged(ref _overlay, value);
    }

    /// <summary>占位文本;Source 为 null 时由瓦片居中显示,默认"未提取"。</summary>
    private string? _placeholderText = "未提取";
    public string? PlaceholderText
    {
        get => _placeholderText;
        set => this.RaiseAndSetIfChanged(ref _placeholderText, value);
    }

    /// <summary>构造诊断瓦片项。</summary>
    public AnalysisDiagnosticItem(string shortLabel) : base(shortLabel) { }
}
